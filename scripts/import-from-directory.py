#!/usr/bin/env python3
"""Uploads every NuGet package found under a directory tree into Nugget.

Walks the given directories recursively, in any layout, and uploads each
.nupkg file as it is found: the tree is never loaded into memory as a whole,
so even very large archives work. Packages are uploaded byte for byte, so
their signatures stay valid. Needs Python 3.8+ and nothing outside the
standard library.

    scripts/import-from-directory.py \\
        --root /srv/nuget-packages \\
        --nugget-url https://nuget.corp.example --nugget-api-key "$NUGGET_API_KEY"

Every step is logged with a timestamp: each directory directly below the root
as it is scanned, and for every package its path, ID, version and size, what
Nugget already has, the upload and its result.

ID and version come from each package's .nuspec, not from its file name.
Symbol packages (.snupkg, .symbols.nupkg) are ignored. Versions Nugget already
has are compared by size and SHA-512: identical ones are skipped, different
ones are reported as conflicts and never overwritten. The same goes for a
version stored more than once (e.g. in several dated directories): the first
copy found is uploaded, later copies are compared with it. (--dry-run uploads
nothing, so it cannot spot such duplicates.)

With --error-dir, every file that could not be uploaded (unreadable, failed,
conflict) is moved there, to the same path relative to the error directory as
it had relative to --root, so failures are easy to find by directory. Move a
file back under --root to try it again.

Safe to run again: a second run only uploads what is new or failed before
(and was not moved away with --error-dir).

Settings can also come from the environment: NUGGET_URL, NUGGET_API_KEY.

Exit codes: 0 everything was uploaded, 1 some packages failed, conflict or
could not be read (see --failures), 2 nothing was uploaded (bad settings or
connection), 130 interrupted.
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
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ElementTree
import zipfile
from dataclasses import dataclass, field
from typing import Callable, Dict, Iterator, List, Optional, Set, Tuple

USER_AGENT = "nugget-directory-import/1.0"
CHUNK = 1024 * 1024
MAX_NUSPEC_BYTES = 8 * 1024 * 1024  # the same limit as Nugget's
CONFLICT = "Nugget has a different package with this ID and version"
# Outcomes, in the order of the summary, with how the summary names them.
OUTCOMES = (
    ("added", "added"),
    ("present", "already in Nugget"),
    ("would add", "would be uploaded"),
    ("conflict", "conflict"),
    ("failed", "failed"),
    ("unreadable", "unreadable"),
    ("unreadable directory", "unreadable director(ies)"),
    ("not moved", "could not be moved to the error dir"),
)


@dataclass
class LocalPackage:
    path: str
    size: int
    id: str
    version: str
    _sha512: Optional[str] = field(default=None, repr=False)

    def sha512(self) -> str:
        """Base64 SHA-512 of the file, as Nugget reports it (computed once)."""
        if self._sha512 is None:
            digest = hashlib.sha512()
            with open(self.path, "rb") as source:
                for chunk in iter(lambda: source.read(CHUNK), b""):
                    digest.update(chunk)
            self._sha512 = base64.b64encode(digest.digest()).decode()
        return self._sha512


@dataclass
class Result:
    """What happened to one package file."""
    number: int
    root: str
    path: str
    outcome: str = "failed"  # one of OUTCOMES
    detail: str = ""
    package: Optional[LocalPackage] = None

    @property
    def relative_path(self) -> str:
        return os.path.relpath(self.path, self.root)

    @property
    def tag(self) -> str:
        """[number] plus ID and version once known, to tell interleaved lines apart."""
        if self.package is None:
            return f"[{self.number}]"
        return f"[{self.number}] {self.package.id} {self.package.version}:"

    def describe(self) -> str:
        if self.outcome == "added":
            return f"{self.tag} added ({self.detail})"
        if self.outcome == "present":
            return f"{self.tag} skipped, already in Nugget ({self.detail})"
        if self.outcome == "would add":
            return (f"{self.tag} would be uploaded" + (f", {self.detail}" if self.detail else "")
                    + " (dry run)")
        return f"{self.tag} {self.outcome}: {self.detail} ({self.relative_path})"


class HttpError(Exception):
    def __init__(self, status: int, message: str, url: str = ""):
        if status:
            text = f"HTTP {status} from {url}: {message}" if url else f"HTTP {status}: {message}"
        else:
            text = f"cannot reach {url}: {message}" if url else message
        super().__init__(text)
        self.status = status


class ConfigError(Exception):
    pass


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

    def check(self) -> None:
        self.http.json(f"{self.url}/v3/index.json")

    def state(self, package: LocalPackage) -> Tuple[str, str]:
        """('absent', 'same' or 'different', why) for the package's version in
        Nugget. One request per package, so nothing is cached in memory."""
        lower_id = urllib.parse.quote(package.id.lower())
        version = urllib.parse.quote(normalize_version(package.version))
        try:
            leaf = self.http.json(f"{self.url}/v3/catalog0/data/{lower_id}/{version}.json")
        except HttpError as error:
            if error.status == 404:
                return "absent", "not in Nugget yet"
            raise
        size = leaf.get("packageSize")
        if isinstance(size, int) and size != package.size:
            return "different", (f"Nugget's copy is {format_size(size)}, "
                                 f"this file {format_size(package.size)}")
        expected = leaf.get("packageHash")
        if not expected:
            return "same", "same size; Nugget reports no SHA-512 to compare"
        if package.sha512() == expected:
            return "same", "same SHA-512"
        return "different", "same size, different SHA-512"

    def upload(self, path: str, size: int) -> Tuple[int, str]:
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


def log(text: str) -> None:
    with _print_lock:
        print(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {text}", flush=True)


def warn(text: str) -> None:
    with _print_lock:
        print(f"{time.strftime('%Y-%m-%d %H:%M:%S')} warning: {text}",
              file=sys.stderr, flush=True)


def format_size(size: float) -> str:
    if size < 1024:
        return f"{int(size)} bytes"
    for unit in ("KB", "MB", "GB"):
        size /= 1024
        if size < 1024:
            return f"{size:.1f} {unit}"
    return f"{size / 1024:.1f} TB"


def format_duration(seconds: float) -> str:
    if seconds < 60:
        return f"{seconds:.1f} s"
    minutes, seconds = divmod(int(seconds), 60)
    hours, minutes = divmod(minutes, 60)
    return f"{hours}h {minutes:02}m {seconds:02}s" if hours else f"{minutes}m {seconds:02}s"


def is_package_file(name: str) -> bool:
    lower = name.lower()
    # .snupkg does not end in ".nupkg"; legacy symbol packages do, and carry
    # the same ID and version as the real package.
    return lower.endswith(".nupkg") and not lower.endswith(".symbols.nupkg")


def find_packages(root: str, unreadable: Callable[[OSError], None]) -> Iterator[str]:
    """Package files under root, one directory at a time and in sorted order
    (dated directories: oldest first). Only one directory listing is held in
    memory at a time, per level of depth."""
    for directory, subdirectories, files in os.walk(root, onerror=unreadable):
        subdirectories.sort()
        if os.path.dirname(directory) == root:
            log(f"Scanning {os.path.relpath(directory, root)}{os.sep}")
        for name in sorted(files):
            if is_package_file(name):
                yield os.path.join(directory, name)


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def read_package(path: str) -> LocalPackage:
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
    return LocalPackage(path=path, size=size, id=values["id"], version=values["version"])


def move_to_error_dir(path: str, root: str, error_dir: str) -> str:
    """Moves a failed file to the same path relative to error_dir as to root."""
    target = os.path.join(error_dir, os.path.relpath(path, root))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    try:
        os.replace(path, target)  # a rename on the same file system
    except OSError as error:
        if error.errno != errno.EXDEV:
            raise
        # Another file system: copy, then delete. The .partial name means an
        # interruption never leaves a half-copied .nupkg behind.
        partial = target + ".partial"
        if os.path.lexists(partial):
            os.remove(partial)
        shutil.copy2(path, partial)
        os.replace(partial, target)
        try:
            os.remove(path)
        except OSError:
            os.remove(target)  # not moved after all: keep a single copy
            raise
    return target


def process(result: Result, nugget: Nugget, skip_existing: bool, dry_run: bool) -> Result:
    """Reads, checks and uploads one package file (in a worker thread), and
    logs the outcome right after its steps."""
    try:
        check_and_upload(result, nugget, skip_existing, dry_run)
    except Exception as error:  # noqa: BLE001 - one bad file must not stop the run
        result.outcome, result.detail = "failed", f"unexpected error: {error!r}"
    (log if result.outcome in ("added", "present", "would add") else warn)(result.describe())
    return result


def check_and_upload(result: Result, nugget: Nugget, skip_existing: bool,
                     dry_run: bool) -> None:
    log(f"{result.tag} reading {result.relative_path}")
    try:
        result.package = package = read_package(result.path)
    except InvalidPackage as error:
        result.outcome, result.detail = "unreadable", str(error)
        return
    try:
        why = ""
        if skip_existing:
            log(f"{result.tag} {format_size(package.size)}, checking Nugget")
            state, why = nugget.state(package)
            if state == "same":
                result.outcome, result.detail = "present", why
                return
            if state == "different":
                result.outcome, result.detail = "conflict", f"{CONFLICT} ({why})"
                return
        else:
            log(f"{result.tag} {format_size(package.size)}")
        if dry_run:
            result.outcome, result.detail = "would add", why
            return
        log(f"{result.tag} " + (f"{why}, " if why else "") + "uploading")
        started = time.monotonic()
        status, body = nugget.upload(result.path, package.size)
        took = format_duration(time.monotonic() - started)
        if status == 201:
            result.outcome, result.detail = "added", f"HTTP 201 in {took}"
        elif status == 200:
            result.outcome, result.detail = "present", f"identical, HTTP 200 in {took}"
        elif status == 409:
            result.outcome, result.detail = "conflict", f"{CONFLICT} (HTTP 409)"
        else:
            result.outcome, result.detail = "failed", body
    except Exception as error:  # noqa: BLE001 - reported and counted
        result.outcome, result.detail = "failed", str(error)


class Report:
    """Counts outcomes, logs them, writes --failures as it goes (nothing is
    kept per package) and moves failed files to --error-dir."""

    def __init__(self, failures_path: Optional[str], error_dir: Optional[str],
                 dry_run: bool):
        self.totals: Dict[str, int] = {}
        self.files = 0
        self.failures = 0
        self.moved = 0
        self.uploaded_bytes = 0
        self.error_dir = None if dry_run else error_dir
        self.failures_file = None
        if failures_path:
            try:
                self.failures_file = open(failures_path, "w", encoding="utf-8")
            except OSError as error:
                raise ConfigError(f"cannot write --failures file: {error}") from None
            self._write("path", "id", "version", "reason")

    def close(self) -> None:
        if self.failures_file:
            self.failures_file.close()

    def _write(self, *columns: str) -> None:
        if self.failures_file:
            self.failures_file.write("\t".join(" ".join(c.split()) for c in columns) + "\n")
            self.failures_file.flush()  # complete even if the run is interrupted

    def _count(self, outcome: str) -> None:
        self.totals[outcome] = self.totals.get(outcome, 0) + 1

    def directory_error(self, error: OSError) -> None:
        warn(f"cannot read directory {error.filename}: {error.strerror}")
        self._count("unreadable directory")
        self.failures += 1
        self._write(error.filename or "", "", "", f"directory not readable: {error.strerror}")

    def add(self, result: Result) -> None:
        """Counts a result (already logged by its worker); records and moves failures."""
        self.files += 1
        self._count(result.outcome)
        package = result.package
        if result.outcome == "added":
            self.uploaded_bytes += package.size if package else 0
        if result.outcome in ("added", "present", "would add"):
            return
        self.failures += 1
        self._write(result.path, package.id if package else "",
                    package.version if package else "", result.detail)
        if self.error_dir:
            try:
                target = move_to_error_dir(result.path, result.root, self.error_dir)
            except OSError as error:
                warn(f"{result.tag} cannot move {result.path} to {self.error_dir}: {error}")
                self._count("not moved")
                return
            self.moved += 1
            log(f"{result.tag} moved to {target}")

    def summary(self) -> str:
        parts = [f"{self.totals[key]} {label}" for key, label in OUTCOMES if key in self.totals]
        if self.uploaded_bytes:
            parts.append(f"{format_size(self.uploaded_bytes)} uploaded")
        return f"{self.files} package file(s)" + (": " + ", ".join(parts) if parts else "")


def run(pool: concurrent.futures.ThreadPoolExecutor, args: argparse.Namespace,
        nugget: Nugget, report: Report) -> bool:
    """Feeds package files to the workers as the tree is walked; False when
    interrupted."""
    # Only a few packages wait in the queue, however large the tree is.
    limit = max(1, args.parallel) * 2
    pending: Set[concurrent.futures.Future] = set()

    def collect() -> None:
        done, _ = concurrent.futures.wait(pending,
                                          return_when=concurrent.futures.FIRST_COMPLETED)
        for future in done:
            pending.discard(future)
            report.add(future.result())

    number = 0
    try:
        for root in args.root:
            log(f"Scanning {root}")
            for path in find_packages(root, report.directory_error):
                while len(pending) >= limit:
                    collect()
                number += 1
                pending.add(pool.submit(process, Result(number, root, path), nugget,
                                        args.skip_existing, args.dry_run))
        log(f"Scan complete: {number} package file(s) found, waiting for the last uploads")
        while pending:
            collect()
        return True
    except KeyboardInterrupt:
        for future in pending:
            future.cancel()  # those not started yet
        running = {future for future in pending if not future.cancelled()}
        pending.clear()
        pending.update(running)
        warn(f"interrupted: finishing the {len(pending)} package(s) in progress "
             "(Ctrl-C again to stop at once)")
        while pending:
            collect()
        return False


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
                        help="only log what would be uploaded")
    parser.add_argument("--failures", metavar="FILE",
                        help="write packages that could not be uploaded to FILE (TSV), "
                             "as they happen")
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


def main(argv: List[str]) -> int:
    args = parse_args(argv)
    started = time.monotonic()
    http = Http(args.ca_file, args.insecure, args.timeout)
    nugget = Nugget(http, args.nugget_url, args.nugget_api_key or "")
    log(f"Checking Nugget at {nugget.url}")
    nugget.check()
    log("Nugget is reachable")
    log(f"Importing from {', '.join(args.root)}")
    if args.dry_run:
        log("Dry run: nothing is uploaded or moved")
    elif args.error_dir:
        log(f"Files that cannot be uploaded are moved to {args.error_dir}")
    else:
        log("Files that cannot be uploaded stay where they are (no --error-dir)")
    if args.failures:
        log(f"Failures are listed in {args.failures}")
    log(f"{max(1, args.parallel)} package(s) at a time; "
        + ("versions Nugget already has are skipped" if args.skip_existing
           else "uploading even versions Nugget already has"))

    report = Report(args.failures, args.error_dir, args.dry_run)
    try:
        with concurrent.futures.ThreadPoolExecutor(max(1, args.parallel)) as pool:
            completed = run(pool, args, nugget, report)
    finally:
        report.close()

    took = format_duration(time.monotonic() - started)
    log(("Done" if completed else "Interrupted") + f" after {took}: {report.summary()}")
    if report.moved:
        log(f"Moved {report.moved} file(s) to {args.error_dir}")
    if args.failures and report.failures:
        log(f"Listed {report.failures} failure(s) in {args.failures}")
    if not completed:
        return 130
    return 1 if report.failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except (HttpError, ConfigError) as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(2)
    except KeyboardInterrupt:
        sys.exit(130)
