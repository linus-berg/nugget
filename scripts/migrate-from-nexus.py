#!/usr/bin/env python3
"""Copies every NuGet package from Sonatype Nexus Repository into Nugget.

Works with Nexus Repository 3.67 and later (it only uses the stable
/service/rest/v1 API). Packages are copied byte for byte, so their signatures
stay valid. Needs Python 3.8+ and nothing outside the standard library.

    scripts/migrate-from-nexus.py \\
        --nexus-url https://nexus.corp.example --nexus-user admin \\
        --nugget-url https://nuget.corp.example --nugget-api-key "$NUGGET_API_KEY"

By default all NuGet hosted and proxy repositories are copied (group
repositories only repeat their members). Pick repositories with --repository.

Safe to run again: versions Nugget already has are skipped without being
downloaded (their SHA-512 is compared with the one Nexus reports), so a second
run only copies what is new or failed before.

Settings can also come from the environment: NEXUS_URL, NEXUS_USER,
NEXUS_PASSWORD, NUGGET_URL, NUGGET_API_KEY.

Exit codes: 0 everything was copied, 1 some packages failed or conflict (see
--failures), 2 nothing was copied (bad settings, login or connection).
"""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import getpass
import hashlib
import json
import os
import ssl
import sys
import tempfile
import threading
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from typing import Dict, Iterator, List, Optional, Set, Tuple

USER_AGENT = "nugget-nexus-migration/1.0"
CHUNK = 1024 * 1024
# Nexus checksum name -> hashlib name, strongest first.
CHECKSUMS = (("sha512", "sha512"), ("sha256", "sha256"), ("sha1", "sha1"), ("md5", "md5"))
PACKAGE_TYPES = ("application/zip", "application/octet-stream", "application/x-zip-compressed")


@dataclass(frozen=True)
class Package:
    repository: str
    id: str
    version: str
    path: str  # path of the .nupkg inside the Nexus repository
    checksums: Dict[str, str] = field(compare=False, hash=False)


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


class Http:
    """Small urllib wrapper: TLS settings, auth header, readable errors."""

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


class Nexus:
    def __init__(self, http: Http, url: str, user: Optional[str], password: Optional[str]):
        self.http = http
        self.url = url.rstrip("/")
        self.headers: Dict[str, str] = {}
        if user:
            token = base64.b64encode(f"{user}:{password or ''}".encode()).decode()
            self.headers["Authorization"] = f"Basic {token}"

    def repositories(self) -> List[dict]:
        return self.http.json(f"{self.url}/service/rest/v1/repositories", self.headers)

    def packages(self, repository: str) -> Iterator[Package]:
        """Every NuGet package of a repository (the components API pages through all)."""
        token = None
        while True:
            query = {"repository": repository}
            if token:
                query["continuationToken"] = token
            page = self.http.json(
                f"{self.url}/service/rest/v1/components?{urllib.parse.urlencode(query)}",
                self.headers,
            )
            for component in page.get("items") or []:
                if component.get("format") != "nuget":
                    continue
                asset = package_asset(component.get("assets") or [])
                if asset is None:
                    warn(f"{repository}: {component.get('name')} {component.get('version')} "
                         "has no .nupkg, skipped")
                    continue
                yield Package(
                    repository=repository,
                    id=component.get("name") or "",
                    version=component.get("version") or "",
                    path=asset["path"],
                    checksums={k: v.lower() for k, v in (asset.get("checksum") or {}).items()
                               if isinstance(v, str)},
                )
            token = page.get("continuationToken")
            if not token:
                return

    def download(self, package: Package, target) -> None:
        """Streams the package into an open binary file and checks its checksum."""
        path = urllib.parse.quote(package.path.lstrip("/"), safe="/")
        url = f"{self.url}/repository/{urllib.parse.quote(package.repository)}/{path}"
        expected = [(name, algorithm) for name, algorithm in CHECKSUMS
                    if name in package.checksums]
        hashes = {name: hashlib.new(algorithm) for name, algorithm in expected}
        with self.http.open("GET", url, self.headers) as response:
            while True:
                chunk = response.read(CHUNK)
                if not chunk:
                    break
                target.write(chunk)
                for digest in hashes.values():
                    digest.update(chunk)
        for name, digest in hashes.items():
            if digest.hexdigest() != package.checksums[name]:
                raise HttpError(0, f"{name} checksum mismatch for {url}")


def package_asset(assets: List[dict]) -> Optional[dict]:
    """The .nupkg among a component's assets (never the .snupkg symbols)."""
    candidates = [a for a in assets if a.get("path")
                  and not a["path"].lower().endswith(".snupkg")]
    for asset in candidates:
        if asset["path"].lower().endswith(".nupkg"):
            return asset
    # Hosted repositories store packages as {id}/{version} without extension.
    for asset in candidates:
        if (asset.get("contentType") or "").lower() in PACKAGE_TYPES:
            return asset
    return candidates[0] if len(candidates) == 1 else None


class Nugget:
    def __init__(self, http: Http, url: str, api_key: str):
        self.http = http
        self.url = url.rstrip("/")
        self.api_key = api_key
        self._versions: Dict[str, Set[str]] = {}
        self._lock = threading.Lock()

    def check(self) -> None:
        self.http.json(f"{self.url}/v3/index.json")

    def state(self, package: Package) -> str:
        """'absent', 'same' or 'different' (same version, other file) in Nugget."""
        if not self._has_version(package):
            return "absent"
        sha512 = package.checksums.get("sha512")
        if not sha512:
            return "same"  # Nexus gave no SHA-512 to compare with
        lower_id = urllib.parse.quote(package.id.lower())
        version = urllib.parse.quote(normalize_version(package.version))
        leaf = self.http.json(f"{self.url}/v3/catalog0/data/{lower_id}/{version}.json")
        expected = base64.b64encode(bytes.fromhex(sha512)).decode()
        return "same" if leaf.get("packageHash") == expected else "different"

    def _has_version(self, package: Package) -> bool:
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


def copy(package: Package, nexus: Nexus, nugget: Nugget, workdir: str,
         skip_existing: bool, dry_run: bool) -> Tuple[str, str]:
    """Copies one package; returns (outcome, detail)."""
    if skip_existing:
        state = nugget.state(package)
        if state == "same":
            return "present", ""
        if state == "different":
            return "conflict", "Nugget has a different package with this ID and version"
    if dry_run:
        return "would copy", ""
    handle, path = tempfile.mkstemp(suffix=".nupkg", dir=workdir)
    try:
        with os.fdopen(handle, "wb") as target:
            nexus.download(package, target)
        status, body = nugget.upload(path)
    finally:
        os.remove(path)
    if status == 201:
        return "added", ""
    if status == 200:
        return "present", ""
    if status == 409:
        return "conflict", "Nugget has a different package with this ID and version"
    return "failed", body


def parse_args(argv: List[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Copy all NuGet packages from Nexus Repository (3.67+) into Nugget.",
        epilog="Environment variables: NEXUS_URL, NEXUS_USER, NEXUS_PASSWORD, "
               "NUGGET_URL, NUGGET_API_KEY.",
    )
    env = os.environ.get
    parser.add_argument("--nexus-url", default=env("NEXUS_URL"),
                        help="Nexus base URL, e.g. https://nexus.corp.example")
    parser.add_argument("--nexus-user", default=env("NEXUS_USER"),
                        help="Nexus user (or user token name); anonymous when omitted")
    parser.add_argument("--nexus-password", default=env("NEXUS_PASSWORD"),
                        help="password or user token passcode (asked for when omitted)")
    parser.add_argument("--nugget-url", default=env("NUGGET_URL"),
                        help="Nugget base URL, e.g. https://nuget.corp.example")
    parser.add_argument("--nugget-api-key", default=env("NUGGET_API_KEY"),
                        help="Nugget API key")
    parser.add_argument("--repository", action="append", default=[],
                        help="Nexus repository to copy (repeatable); "
                             "default: every NuGet hosted and proxy repository")
    parser.add_argument("--hosted-only", action="store_true",
                        help="skip proxy repositories (packages cached from nuget.org)")
    parser.add_argument("--parallel", type=int, default=4,
                        help="packages copied at the same time (default 4)")
    parser.add_argument("--no-skip-existing", dest="skip_existing", action="store_false",
                        help="download and upload even what Nugget already has")
    parser.add_argument("--dry-run", action="store_true",
                        help="only list what would be copied")
    parser.add_argument("--failures", metavar="FILE",
                        help="write packages that could not be copied to FILE (TSV)")
    parser.add_argument("--ca-file", help="extra trusted CA certificate(s), PEM")
    parser.add_argument("--insecure", action="store_true",
                        help="do not verify TLS certificates (testing only)")
    parser.add_argument("--timeout", type=float, default=300,
                        help="network timeout in seconds (default 300)")
    args = parser.parse_args(argv)
    missing = [name for name in ("nexus_url", "nugget_url") if not getattr(args, name)]
    if not args.dry_run and not args.nugget_api_key:
        missing.append("nugget_api_key")
    if missing:
        parser.error("missing " + ", ".join("--" + m.replace("_", "-") for m in missing))
    if args.nexus_user and args.nexus_password is None:
        if not sys.stdin.isatty():
            parser.error("--nexus-password (or NEXUS_PASSWORD) is needed when not "
                         "running in a terminal")
        args.nexus_password = getpass.getpass(f"Nexus password for {args.nexus_user}: ")
    return args


def select_repositories(nexus: Nexus, wanted: List[str], hosted_only: bool) -> List[str]:
    nuget = {r["name"]: r for r in nexus.repositories() if r.get("format") == "nuget"}
    if wanted:
        unknown = [name for name in wanted if name not in nuget]
        if unknown:
            raise ConfigError(f"not NuGet repositories in Nexus: {', '.join(unknown)} "
                              f"(found: {', '.join(sorted(nuget)) or 'none'})")
        names = wanted
    else:
        names = sorted(nuget)
    selected = []
    for name in names:
        kind = nuget[name].get("type")
        if kind == "group":
            say(f"skipping group repository {name} (its members are copied directly)")
        elif kind == "proxy" and hosted_only:
            say(f"skipping proxy repository {name} (--hosted-only)")
        else:
            selected.append(name)
    return selected


def main(argv: List[str]) -> int:
    args = parse_args(argv)
    http = Http(args.ca_file, args.insecure, args.timeout)
    nexus = Nexus(http, args.nexus_url, args.nexus_user, args.nexus_password)
    nugget = Nugget(http, args.nugget_url, args.nugget_api_key or "")
    nugget.check()

    repositories = select_repositories(nexus, args.repository, args.hosted_only)
    packages: List[Package] = []
    seen: Set[Tuple[str, str]] = set()
    for repository in repositories:
        count = 0
        for package in nexus.packages(repository):
            key = (package.id.lower(), normalize_version(package.version))
            if key in seen:
                continue  # the same version in another repository
            seen.add(key)
            packages.append(package)
            count += 1
        say(f"{repository}: {count} package version(s)")
    say(f"Copying {len(packages)} package version(s) from {nexus.url} to {nugget.url}"
        + (" (dry run)" if args.dry_run else ""))

    totals: Dict[str, int] = {}
    failures: List[Tuple[Package, str]] = []
    width = len(str(len(packages)))
    with tempfile.TemporaryDirectory(prefix="nexus-to-nugget-") as workdir, \
            concurrent.futures.ThreadPoolExecutor(max(1, args.parallel)) as pool:
        futures = {pool.submit(copy, p, nexus, nugget, workdir, args.skip_existing,
                               args.dry_run): p for p in packages}
        for done, future in enumerate(concurrent.futures.as_completed(futures), 1):
            package = futures[future]
            try:
                outcome, detail = future.result()
            except Exception as error:  # noqa: BLE001 - reported and counted
                outcome, detail = "failed", str(error)
            totals[outcome] = totals.get(outcome, 0) + 1
            if outcome in ("failed", "conflict"):
                failures.append((package, detail))
            say(f"[{done:>{width}}/{len(packages)}] {outcome:<10} {package.id} "
                f"{package.version} ({package.repository})" + (f": {detail}" if detail else ""))

    if args.failures and failures:
        with open(args.failures, "w", encoding="utf-8") as report:
            report.write("repository\tid\tversion\treason\n")
            for package, detail in failures:
                reason = " ".join(detail.split())
                report.write(f"{package.repository}\t{package.id}\t{package.version}\t{reason}\n")
        say(f"Wrote {len(failures)} failure(s) to {args.failures}")
    say("Done: " + (", ".join(f"{n} {k}" for k, n in sorted(totals.items())) or "nothing to do"))
    return 1 if failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except (HttpError, ConfigError) as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(2)
    except KeyboardInterrupt:
        sys.exit(130)
