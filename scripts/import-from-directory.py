#!/usr/bin/env python3
"""Uploads every NuGet package found under a directory tree into Nugget.

Searches the given directories recursively for .nupkg files, in any layout.
Packages are uploaded byte for byte, so their signatures stay valid. Needs
Python 3.8+ and nothing outside the standard library.

    scripts/import-from-directory.py \\
        --root /srv/nuget-packages \\
        --nugget-url https://nuget.corp.example --nugget-api-key "$NUGGET_API_KEY"

ID and version come from each package's .nuspec, not from its file name. When
the same version is found more than once (e.g. in several dated directories),
the first copy in path order is uploaded; a copy with different content is
reported as a conflict. Symbol packages (.snupkg, .symbols.nupkg) are ignored.

With --error-dir, every file that could not be uploaded (unreadable, failed,
conflict, differing copy) is moved there, to the same path relative to the
error directory as it had relative to --root, so failures are easy to find by
directory. Move a file back under --root to try it again.

Safe to run again: versions Nugget already has are skipped without being
uploaded (compared by size and SHA-512), so a second run only uploads what is
new or failed before (and was not moved away with --error-dir).

Settings can also come from the environment: NUGGET_URL, NUGGET_API_KEY.

Exit codes: 0 everything was uploaded, 1 some packages failed, conflict or
could not be read (see --failures), 2 nothing was uploaded (bad settings or
connection).
"""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import errno
import hashlib
import json
import os
import shutil
import ssl
import sys
import threading
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ElementTree
import zipfile
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Set, Tuple, Union

USER_AGENT = "nugget-directory-import/1.0"
CHUNK = 1024 * 1024
MAX_NUSPEC_BYTES = 8 * 1024 * 1024  # the same limit as Nugget's


@dataclass
class LocalPackage:
    path: str
    root: str
    size: int
    id: str
    version: str
    _sha512: Optional[str] = field(default=None, repr=False)

    @property
    def key(self) -> Tuple[str, str]:
        return self.id.lower(), normalize_version(self.version)

    @property
    def display_path(self) -> str:
        return os.path.relpath(self.path, self.root)

    def sha512(self) -> str:
        """Base64 SHA-512 of the file, as Nugget reports it (computed once)."""
        if self._sha512 is None:
            digest = hashlib.sha512()
            with open(self.path, "rb") as source:
                for chunk in iter(lambda: source.read(CHUNK), b""):
                    digest.update(chunk)
            self._sha512 = base64.b64encode(digest.digest()).decode()
        return self._sha512


@dataclass(frozen=True)
class Failure:
    path: str
    id: str
    version: str
    reason: str
    root: str = ""  # set when path is a package file under this root


class HttpError(Exception):
    def __init__(self, status: int, message: str, url: str = ""):
        if status:
            text = f"HTTP {status} from {url}: {message}" if url else f"HTTP {status}: {message}"
        else:
            text = f"cannot reach {url}: {message}" if url else message
        super().__init__(text)
        self.status = status


class InvalidPackage(Exception):
    pass


class Http:
    """Small urllib wrapper: TLS settings, readable errors."""

    def __init__(self, ca_file: Optional[str], insecure: bool, timeout: float):
        self.timeout = timeout
        if insecure:
            self.context = ssl._create_unverified_context()  # noqa: S323 - asked for
        else:
            # The system trust store, plus an internal CA if given.
            self.context = ssl.create_default_context(cafile=ca_file)

    def open(self, method: str, url: str, headers: Optional[Dict[str, str]] = None,
             data=None):
        request = urllib.request.Request(url, data=data, method=method)
        request.add_header("User-Agent", USER_AGENT)
        for name, value in (headers or {}).items():
            request.add_header(name, value)
        try:
            return urllib.request.urlopen(request, context=self.context, timeout=self.timeout)
        except urllib.error.HTTPError as error:
            body = error.read(2000).decode("utf-8", "replace").strip()
            raise HttpError(error.code, body or str(error.reason), url) from None
        except (urllib.error.URLError, OSError) as error:
            raise HttpError(0, str(getattr(error, "reason", error)), url) from None

    def json(self, url: str, headers: Optional[Dict[str, str]] = None):
        with self.open("GET", url, dict(headers or {}, Accept="application/json")) as response:
            return json.load(response)


class Nugget:
    def __init__(self, http: Http, url: str, api_key: str):
        self.http = http
        self.url = url.rstrip("/")
        self.api_key = api_key
        self._versions: Dict[str, Set[str]] = {}
        self._lock = threading.Lock()

    def check(self) -> None:
        self.http.json(f"{self.url}/v3/index.json")

    def state(self, package: LocalPackage) -> str:
        """'absent', 'same' or 'different' (same version, other file) in Nugget."""
        if not self._has_version(package):
            return "absent"
        lower_id = urllib.parse.quote(package.id.lower())
        version = urllib.parse.quote(normalize_version(package.version))
        leaf = self.http.json(f"{self.url}/v3/catalog0/data/{lower_id}/{version}.json")
        size = leaf.get("packageSize")
        if isinstance(size, int) and size != package.size:
            return "different"  # no need to hash the file
        expected = leaf.get("packageHash")
        if not expected:
            return "same"
        return "same" if package.sha512() == expected else "different"

    def _has_version(self, package: LocalPackage) -> bool:
        lower_id = package.id.lower()
        with self._lock:
            versions = self._versions.get(lower_id)
        if versions is None:
            try:
                index = self.http.json(
                    f"{self.url}/v3-flatcontainer/{urllib.parse.quote(lower_id)}/index.json"
                )
                versions = set(index.get("versions") or [])
            except HttpError as error:
                if error.status != 404:
                    raise
                versions = set()
            with self._lock:
                self._versions[lower_id] = versions
        return normalize_version(package.version) in versions

    def upload(self, path: str) -> Tuple[int, str]:
        size = os.path.getsize(path)
        with open(path, "rb") as body:
            try:
                with self.http.open(
                    "PUT",
                    f"{self.url}/api/v2/package",
                    {
                        "X-NuGet-ApiKey": self.api_key,
                        "Content-Type": "application/octet-stream",
                        "Content-Length": str(size),
                    },
                    data=body,
                ) as response:
                    return response.status, response.read().decode("utf-8", "replace")
            except HttpError as error:
                return error.status, str(error)


def normalize_version(version: str) -> str:
    """NuGet's normalized form, as used in Nugget's URLs (1.0 -> 1.0.0)."""
    text = version.strip().split("+", 1)[0]
    release, _, prerelease = text.partition("-")
    parts = release.split(".")
    if not 1 <= len(parts) <= 4 or not all(p.isdigit() for p in parts):
        return text.lower()
    numbers = [int(p) for p in parts] + [0] * max(0, 3 - len(parts))
    if len(numbers) == 4 and numbers[3] == 0:
        numbers = numbers[:3]
    normalized = ".".join(str(n) for n in numbers)
    return (normalized + ("-" + prerelease if prerelease else "")).lower()


_print_lock = threading.Lock()


def say(text: str) -> None:
    with _print_lock:
        print(text, flush=True)


def warn(text: str) -> None:
    with _print_lock:
        print(f"warning: {text}", file=sys.stderr, flush=True)


def is_package_file(name: str) -> bool:
    lower = name.lower()
    # .snupkg does not end in ".nupkg"; legacy symbol packages do, and carry
    # the same ID and version as the real package.
    return lower.endswith(".nupkg") and not lower.endswith(".symbols.nupkg")


def find_packages(roots: List[str], failures: List[Failure],
                  totals: Dict[str, int]) -> List[Tuple[str, str]]:
    """(root, path) of every package file, in a stable order."""
    found: List[Tuple[str, str]] = []
    seen: Set[str] = set()

    def unreadable(error: OSError) -> None:
        warn(f"cannot read directory {error.filename}: {error.strerror}")
        failures.append(Failure(error.filename or "", "", "",
                                f"directory not readable: {error.strerror}"))
        totals["unreadable directory"] = totals.get("unreadable directory", 0) + 1

    for root in roots:
        for directory, subdirectories, files in os.walk(root, onerror=unreadable):
            subdirectories.sort()  # dated directories: oldest first
            for name in sorted(files):
                path = os.path.join(directory, name)
                if is_package_file(name) and path not in seen:
                    seen.add(path)
                    found.append((root, path))
    return found


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def read_package(root: str, path: str) -> LocalPackage:
    """ID and version from the package's .nuspec."""
    try:
        size = os.path.getsize(path)
        with zipfile.ZipFile(path) as archive:
            nuspecs = [entry for entry in archive.infolist()
                       if "/" not in entry.filename
                       and entry.filename.lower().endswith(".nuspec")]
            if len(nuspecs) != 1:
                raise InvalidPackage(f"{len(nuspecs)} .nuspec files at the root, expected 1")
            with archive.open(nuspecs[0]) as stream:
                data = stream.read(MAX_NUSPEC_BYTES + 1)
    except (OSError, zipfile.BadZipFile, zipfile.LargeZipFile, RuntimeError) as error:
        raise InvalidPackage(f"not a readable package: {error}") from None
    if len(data) > MAX_NUSPEC_BYTES:
        raise InvalidPackage("the .nuspec is too large")
    try:
        document = ElementTree.fromstring(data)
    except ElementTree.ParseError as error:
        raise InvalidPackage(f"invalid .nuspec: {error}") from None
    values: Dict[str, str] = {}
    for metadata in document:
        if local_name(metadata.tag) == "metadata":
            for element in metadata:
                name = local_name(element.tag)
                if name in ("id", "version") and element.text:
                    values[name] = element.text.strip()
    if not values.get("id") or not values.get("version"):
        raise InvalidPackage("the .nuspec has no ID or version")
    return LocalPackage(path=path, root=root, size=size,
                        id=values["id"], version=values["version"])


def try_read_package(root: str, path: str) -> Union[LocalPackage, Failure]:
    try:
        return read_package(root, path)
    except InvalidPackage as error:
        return Failure(path, "", "", str(error), root)


def move_to_error_dir(failure: Failure, error_dir: str) -> str:
    """Moves a failed file to the same path relative to error_dir as to its root."""
    target = os.path.join(error_dir, os.path.relpath(failure.path, failure.root))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    try:
        os.replace(failure.path, target)  # a rename on the same file system
    except OSError as error:
        if error.errno != errno.EXDEV:
            raise
        # Another file system: copy, then delete. The .partial name means an
        # interruption never leaves a half-copied .nupkg behind.
        partial = target + ".partial"
        if os.path.lexists(partial):
            os.remove(partial)
        shutil.copy2(failure.path, partial)
        os.replace(partial, target)
        try:
            os.remove(failure.path)
        except OSError:
            os.remove(target)  # not moved after all: keep a single copy
            raise
    return target


def upload(copies: List[LocalPackage], nugget: Nugget, skip_existing: bool,
           dry_run: bool) -> Tuple[str, str, List[LocalPackage]]:
    """Uploads the first copy of a version; returns (outcome, detail, copies
    with other content than the uploaded one)."""
    package = copies[0]
    differing = [other for other in copies[1:]
                 if other.size != package.size or other.sha512() != package.sha512()]
    if skip_existing:
        state = nugget.state(package)
        if state == "same":
            return "present", "", differing
        if state == "different":
            return "conflict", "Nugget has a different package with this ID and version", \
                differing
    if dry_run:
        return "would add", "", differing
    status, body = nugget.upload(package.path)
    if status == 201:
        return "added", "", differing
    if status == 200:
        return "present", "", differing
    if status == 409:
        return "conflict", "Nugget has a different package with this ID and version", \
            differing
    return "failed", body, differing


def parse_args(argv: List[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Upload all NuGet packages found under a directory into Nugget.",
        epilog="Environment variables: NUGGET_URL, NUGGET_API_KEY.",
    )
    env = os.environ.get
    parser.add_argument("--root", action="append", default=[], metavar="DIR",
                        help="directory to search for .nupkg files, recursively "
                             "(repeatable; required)")
    parser.add_argument("--nugget-url", default=env("NUGGET_URL"),
                        help="Nugget base URL, e.g. https://nuget.corp.example")
    parser.add_argument("--nugget-api-key", default=env("NUGGET_API_KEY"),
                        help="Nugget API key")
    parser.add_argument("--parallel", type=int, default=4,
                        help="packages uploaded at the same time (default 4)")
    parser.add_argument("--no-skip-existing", dest="skip_existing", action="store_false",
                        help="upload even what Nugget already has")
    parser.add_argument("--dry-run", action="store_true",
                        help="only list what would be uploaded")
    parser.add_argument("--failures", metavar="FILE",
                        help="write packages that could not be uploaded to FILE (TSV)")
    parser.add_argument("--error-dir", metavar="DIR",
                        help="move packages that could not be uploaded to DIR, under "
                             "the same relative path as under --root "
                             "(ROOT/a/b.nupkg -> DIR/a/b.nupkg)")
    parser.add_argument("--ca-file", help="extra trusted CA certificate(s), PEM")
    parser.add_argument("--insecure", action="store_true",
                        help="do not verify TLS certificates (testing only)")
    parser.add_argument("--timeout", type=float, default=300,
                        help="network timeout in seconds (default 300)")
    args = parser.parse_args(argv)
    missing = []
    if not args.root:
        missing.append("root")
    if not args.nugget_url:
        missing.append("nugget_url")
    if not args.dry_run and not args.nugget_api_key:
        missing.append("nugget_api_key")
    if missing:
        parser.error("missing " + ", ".join("--" + m.replace("_", "-") for m in missing))
    not_directories = [root for root in args.root if not os.path.isdir(root)]
    if not_directories:
        parser.error("not a directory: " + ", ".join(not_directories))
    args.root = [os.path.abspath(root) for root in args.root]
    if args.error_dir:
        if len(args.root) > 1:
            parser.error("--error-dir works with a single --root")
        args.error_dir = os.path.abspath(args.error_dir)
        root = os.path.realpath(args.root[0])
        if os.path.commonpath([root, os.path.realpath(args.error_dir)]) == root:
            # The next run would find the moved files as packages again.
            parser.error("--error-dir must not be inside --root")
        if not args.dry_run:
            try:
                os.makedirs(args.error_dir, exist_ok=True)
            except OSError as error:
                parser.error(f"cannot create --error-dir: {error}")
            if not os.access(args.error_dir, os.W_OK | os.X_OK):
                parser.error(f"--error-dir is not writable: {args.error_dir}")
    return args


def run(pool: concurrent.futures.ThreadPoolExecutor, args: argparse.Namespace,
        nugget: Nugget, failures: List[Failure], totals: Dict[str, int]) -> None:
    def fail(failure: Failure) -> None:
        failures.append(failure)
        if not args.error_dir or not failure.root or args.dry_run:
            return
        try:
            move_to_error_dir(failure, args.error_dir)
            totals["moved to error dir"] = totals.get("moved to error dir", 0) + 1
        except OSError as error:
            warn(f"cannot move {failure.path} to {args.error_dir}: {error}")
            totals["not moved to error dir"] = totals.get("not moved to error dir", 0) + 1

    files = find_packages(args.root, failures, totals)
    say(f"Found {len(files)} package file(s) under {', '.join(args.root)}, "
        "reading their manifests")

    versions: Dict[Tuple[str, str], List[LocalPackage]] = {}
    for result in pool.map(lambda found: try_read_package(*found), files):
        if isinstance(result, Failure):
            warn(f"{result.path}: {result.reason}")
            fail(result)
            totals["unreadable"] = totals.get("unreadable", 0) + 1
        else:
            versions.setdefault(result.key, []).append(result)
    duplicates = sum(len(copies) - 1 for copies in versions.values())
    say(f"Uploading {len(versions)} package version(s) to {nugget.url}"
        + (f" ({duplicates} duplicate file(s), uploaded once)" if duplicates else "")
        + (" (dry run)" if args.dry_run else ""))

    width = len(str(len(versions)))
    futures = {pool.submit(upload, copies, nugget, args.skip_existing, args.dry_run): copies
               for copies in versions.values()}
    try:
        for done, future in enumerate(concurrent.futures.as_completed(futures), 1):
            package = futures[future][0]
            try:
                outcome, detail, differing = future.result()
            except Exception as error:  # noqa: BLE001 - reported and counted
                outcome, detail, differing = "failed", str(error), []
            totals[outcome] = totals.get(outcome, 0) + 1
            if outcome in ("failed", "conflict"):
                fail(Failure(package.path, package.id, package.version, detail, package.root))
                detail = f"{detail} ({package.display_path})"
            say(f"[{done:>{width}}/{len(versions)}] {outcome:<10} {package.id} "
                f"{package.version}" + (f": {detail}" if detail else ""))
            for other in differing:
                reason = f"differs from {package.path}, the copy used for this version"
                warn(f"{other.display_path}: {reason}")
                fail(Failure(other.path, other.id, other.version, reason, other.root))
                totals["differing copy"] = totals.get("differing copy", 0) + 1
    except KeyboardInterrupt:
        for future in futures:
            future.cancel()  # stop at the uploads already running
        raise


def main(argv: List[str]) -> int:
    args = parse_args(argv)
    http = Http(args.ca_file, args.insecure, args.timeout)
    nugget = Nugget(http, args.nugget_url, args.nugget_api_key or "")
    nugget.check()

    totals: Dict[str, int] = {}
    failures: List[Failure] = []
    with concurrent.futures.ThreadPoolExecutor(max(1, args.parallel)) as pool:
        run(pool, args, nugget, failures, totals)

    if args.failures and failures:
        with open(args.failures, "w", encoding="utf-8") as report:
            report.write("path\tid\tversion\treason\n")
            for failure in failures:
                reason = " ".join(failure.reason.split())
                report.write(f"{failure.path}\t{failure.id}\t{failure.version}\t{reason}\n")
        say(f"Wrote {len(failures)} failure(s) to {args.failures}")
    if totals.get("moved to error dir"):
        say(f"Moved {totals['moved to error dir']} failed file(s) to {args.error_dir}")
    say("Done: " + (", ".join(f"{n} {k}" for k, n in sorted(totals.items())) or "nothing to do"))
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except HttpError as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(2)
    except KeyboardInterrupt:
        sys.exit(130)
