<p align="center">
  <img src="assets/logo.svg" alt="Nugget logo: a chicken nugget" width="160">
</p>

<h1 align="center">Nugget</h1>

<p align="center"><b>An offline NuGet server for air-gapped networks, backed only by S3.</b></p>

Nugget implements the NuGet V3 protocol the way `api.nuget.org` does: the same
resources, the same URL layout and the same JSON. Point `api.nuget.org` at
Nugget in your internal DNS, and `dotnet restore`, Visual Studio, Rider,
`dotnet package search`, `dotnet tool install` and NuGetAudit work with the
**stock NuGet configuration**, exactly as if the machine were online.

```
 developer / CI ──https://api.nuget.org──▶ (internal DNS) ──▶ Nugget ──▶ S3 (MinIO, ...)
                                                               ▲
 uploads (curl, scripts, ...) ──PUT /api/v2/package + API key──┘
```

* **Restore**: flat container (`/v3-flatcontainer/`), package metadata
  (registration hives `registration5-semver1`, `registration5-gz-semver1`,
  `registration5-gz-semver2`), SemVer 2.0.0 filtering, paged registrations
  for packages with many versions.
* **Search and autocomplete** (`/query`, `/autocomplete`) with nuget.org's
  query syntax (`packageid:`, `id:`, `tags:`, `title:`, `author:`, ...),
  prerelease/SemVer filters, package type filters (`dotnet tool search`).
* **README, icon and license** of each package (Visual Studio's details pane).
* **Vulnerability data** (`VulnerabilityInfo/6.7.0`): mirror nuget.org's
  advisory files and NuGetAudit warns offline; registration and search
  results carry the advisories for Visual Studio.
* **Exact names**: the package ID and version are read from the package's
  nuspec, so `Newtonsoft.Json` stays `Newtonsoft.Json` (whatever the
  uploaded file was called) and `1.0.0-Beta.2` keeps its casing; storage
  keys and download URLs are lower case, like nuget.org's.
* **Byte for byte**: packages are stored unmodified, so nuget.org's author and
  repository signatures still verify.
* **S3 only**: no database, no local disk state. Several instances can run
  against the same bucket.
* **Download counts** (of this mirror), used to rank search results.

## Contents

1. [Quick start](#quick-start)
2. [Pointing api.nuget.org at Nugget](#pointing-apinugetorg-at-nugget)
3. [Uploading packages](#uploading-packages)
4. [HTTP API](#http-api)
5. [Configuration](#configuration)
6. [How it works](#how-it-works)
7. [Operations](#operations)
8. [Limitations](#limitations)
9. [Development](#development)

## Quick start

You need an S3 compatible store (MinIO, Ceph, RustFS, AWS S3, ...) and a TLS
certificate for `api.nuget.org` from a CA your clients trust.

```bash
cd deploy
./generate-certs.sh nuget.corp.example        # or issue a cert from your own CA
cat > .env <<EOF
NUGGET_API_KEY=$(openssl rand -hex 24)
S3_URL=https://minio.corp.example:9000
S3_ACCESS_KEY=...
S3_SECRET_KEY=...
EOF
docker compose up -d
curl --cacert certs/ca.crt https://nuget.corp.example/health/ready
```

The bucket (`nuget` by default) is created on first start. Then upload a
package:

```bash
curl -X PUT -H "X-NuGet-ApiKey: $NUGGET_API_KEY" \
     -H "Content-Type: application/octet-stream" \
     --data-binary @newtonsoft.json.13.0.3.nupkg \
     https://nuget.corp.example/api/v2/package
```

## Pointing api.nuget.org at Nugget

1. **DNS**: in your internal DNS, resolve `api.nuget.org` to the Nugget host
   (or load balancer). Only `api.nuget.org` is needed; search, autocomplete
   and everything else is served from the same host (the service index points
   there, unlike nuget.org's which uses `azuresearch-*.nuget.org`).
2. **TLS**: clients connect to `https://api.nuget.org`, so Nugget needs a
   certificate for that name. `deploy/generate-certs.sh` creates a CA and a
   certificate for `api.nuget.org` plus any names you pass; or issue one from
   your organisation's CA. Configure it with
   `ASPNETCORE_Kestrel__Certificates__Default__Path` / `__KeyPath` (PEM), or
   terminate TLS at a reverse proxy (then set
   `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, already set in the image, and
   make the proxy pass the `Host` header).
3. **Trust the CA on clients** (developer machines, build agents, CI images):
   * Windows: import `ca.crt` into *Trusted Root Certification Authorities*
     (GPO for the fleet).
   * Linux (Debian/Ubuntu): copy to `/usr/local/share/ca-certificates/` and
     run `update-ca-certificates`; Fedora/RHEL: `/etc/pki/ca-trust/source/anchors/`
     and `update-ca-trust`.
   * macOS: add to the System keychain and mark it trusted.
   * Docker images that run `dotnet restore` (e.g. `mcr.microsoft.com/dotnet/sdk`)
     need the CA too: `COPY ca.crt /usr/local/share/ca-certificates/` and
     `RUN update-ca-certificates`.

Nothing else changes on clients: the default `nuget.org` source
(`https://api.nuget.org/v3/index.json`) now talks to Nugget.

URLs in responses are built from the host each request used, so the same
server can also be reached under an internal name (e.g.
`https://nuget.corp.example/v3/index.json`). Set `Nugget__PublicBaseUrl` to
force one base URL.

> Package signatures are verified as usual. If restores are slow or warn about
> certificate revocation checks in the air gap, set
> `NUGET_CERT_REVOCATION_MODE=offline` on clients.

## Uploading packages

Uploads are a plain HTTP `PUT` (or `POST`) of the `.nupkg` file to
`/api/v2/package`, with the API key in the `X-NuGet-ApiKey` header. Any HTTP
client works. The examples use the development server; replace the URL and key
with yours (and add `--cacert ca.crt` if your CA is not trusted system wide).

```bash
NUGGET=http://localhost:5080
KEY=dev-api-key

# Download a package from nuget.org (on a machine with internet access) ...
curl -fsSLO https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg

# ... and upload it, unchanged.
curl -X PUT -H "X-NuGet-ApiKey: $KEY" -H "Content-Type: application/octet-stream" \
     --data-binary @newtonsoft.json.13.0.3.nupkg "$NUGGET/api/v2/package"
```

```json
{"id":"Newtonsoft.Json","version":"13.0.3","status":"created","listed":true,
 "packageContent":"http://localhost:5080/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", ...}
```

The file name does not matter: ID and version are read from the package.
Uploads are idempotent and can run in parallel (also through several
instances), so a whole folder can be pushed in one go:

```bash
find packages/ -name '*.nupkg' -print0 |
  xargs -0 -P 8 -I{} curl -s -o /dev/null -w "%{http_code} {}\n" -X PUT \
    -H "X-NuGet-ApiKey: $KEY" --data-binary @{} "$NUGGET/api/v2/package"
```

| Status | Meaning                                                          |
|--------|------------------------------------------------------------------|
| 201    | Package added                                                    |
| 200    | The identical package was already there (re-uploading is safe)  |
| 409    | A *different* package with this ID and version already exists   |
| 400    | Not a valid package (or a `.snupkg` symbol package)             |
| 401    | Missing or wrong API key (403: no API key configured at all)    |
| 413    | Larger than `Nugget__MaxPackageBytes`                            |
| 5xx    | Storage trouble; safe to retry                                   |

`dotnet nuget push` works too (it sends a multipart upload); once
`api.nuget.org` points at Nugget the default source is enough:

```bash
dotnet nuget push my.package.1.0.0.nupkg --api-key "$KEY" --source https://api.nuget.org/v3/index.json
```

`scripts/mirror-packages.sh` combines downloading and uploading:

```bash
scripts/mirror-packages.sh                              # a few example packages
scripts/mirror-packages.sh Serilog Polly.Core@8.9.1     # latest Serilog, a given Polly.Core
NUGGET_URL=https://nuget.corp.example NUGGET_API_KEY=... scripts/mirror-packages.sh Newtonsoft.Json
```

### Unlisting and deleting

```bash
# Unlist: hidden from search and "latest", still restorable (like nuget.org).
curl -X DELETE -H "X-NuGet-ApiKey: $KEY" "$NUGGET/api/v2/package/Newtonsoft.Json/13.0.3"
# List it again.
curl -X POST -H "X-NuGet-ApiKey: $KEY" "$NUGGET/api/v2/package/Newtonsoft.Json/13.0.3"
# Delete the version and its files for good.
curl -X DELETE -H "X-NuGet-ApiKey: $KEY" "$NUGGET/api/v2/package/Newtonsoft.Json/13.0.3?purge=true"
```

### Vulnerability data (NuGetAudit)

nuget.org publishes the security advisories that restore's NuGetAudit and
Visual Studio use. Copy its files to Nugget (e.g. daily) and they work offline:

```bash
# Lists the files, e.g. "base" and "update", with their download URLs (@id).
curl -s --compressed https://api.nuget.org/v3/vulnerabilities/index.json

curl -s --compressed -o base.json "<@id of base>"
curl -X PUT -H "X-NuGet-ApiKey: $KEY" -H "Content-Type: application/json" \
     --data-binary @base.json "$NUGGET/api/v2/vulnerabilities/base"
# ... the same for "update".
```

`WITH_VULNERABILITIES=1 scripts/mirror-packages.sh` does all of that. Once
uploaded, the service index advertises `VulnerabilityInfo/6.7.0`, restore
reports `NU1901`-`NU1904` warnings, `dotnet list package --vulnerable` works,
and Visual Studio shows the advisories.

## HTTP API

Read endpoints are anonymous; write endpoints need the API key in
`X-NuGet-ApiKey` (or `X-Api-Key`, or `Authorization: Bearer ...`).

| Method       | Path                                                     | Purpose                                         |
|--------------|----------------------------------------------------------|-------------------------------------------------|
| GET          | `/v3/index.json`                                         | Service index                                   |
| GET          | `/v3-flatcontainer/{id}/index.json`                      | All versions (including unlisted)               |
| GET, HEAD    | `/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg`  | Package download                                |
| GET          | `/v3-flatcontainer/{id}/{version}/{id}.nuspec`           | Manifest                                        |
| GET          | `/v3-flatcontainer/{id}/{version}/icon\|readme\|license`  | Embedded files                                  |
| GET          | `/v3/registration5-gz-semver2/{id}/index.json` (+ pages, `{version}.json`) | Package metadata (any `registration*` hive) |
| GET          | `/v3/catalog0/data/{id}/{version}.json`                  | All metadata of one version (hash, size, ...)   |
| GET          | `/query?q=&skip=&take=&prerelease=&semVerLevel=&packageType=` | Search                                     |
| GET          | `/autocomplete?q=` / `?id=`                              | ID / version completion                         |
| GET          | `/v3/vulnerabilities/index.json`                         | Vulnerability files (when uploaded)             |
| GET          | `/api/v2/package/{id}/{version?}`                        | Redirect to the download (nuget.org V2 URL)     |
| GET          | `/health`, `/health/ready`                               | Liveness; readiness (catalog loaded)            |
| PUT, POST 🔑 | `/api/v2/package`                                        | Upload (raw body or multipart)                  |
| DELETE 🔑    | `/api/v2/package/{id}/{version}`                         | Unlist (hidden from search, still restorable)   |
| DELETE 🔑    | `/api/v2/package/{id}/{version}?purge=true`              | Delete the version and its files                |
| POST 🔑      | `/api/v2/package/{id}/{version}`                         | Relist                                          |
| PUT 🔑       | `/api/v2/vulnerabilities/{name}`                         | Upload a vulnerability file                     |
| DELETE 🔑    | `/api/v2/vulnerabilities/{name}`                         | Remove a vulnerability file                     |
| POST 🔑      | `/api/v2/admin/rebuild`                                  | Rebuild the catalog from S3 (recovery)          |

See `Nugget.Api/Nugget.Api.http` for ready-made requests.

## Configuration

Settings come from `appsettings.json` or environment variables (`__` separates
levels).

| Environment variable                     | Default             | Meaning                                                                    |
|------------------------------------------|---------------------|----------------------------------------------------------------------------|
| `Nugget__ApiKey`                         | *(none)*            | API key for uploads. Without any key, uploads are disabled.               |
| `Nugget__ApiKeys__0`, `__1`, ...         |                     | Additional keys (e.g. one per uploader).                                  |
| `Nugget__PublicBaseUrl`                  | *(request host)*    | Force the base URL used in responses, e.g. `https://api.nuget.org`.        |
| `Nugget__MaxPackageBytes`                | `2147483648`        | Largest accepted package.                                                  |
| `Nugget__PackageDetailsUrlTemplate`      | *(none)*            | Optional "package details" link for Visual Studio (`{id}`, `{version}`).   |
| `Nugget__LogRequests`                    | `true`              | Log every request.                                                         |
| `Nugget__MetadataCacheVersions`          | `250000`            | Package versions whose metadata is cached in memory.                       |
| `Nugget__Sync__PollIntervalSeconds`      | `5`                 | How often other instances' changes are picked up.                         |
| `Nugget__Sync__SnapshotIntervalMinutes`  | `15`                | How often the catalog snapshot is rewritten.                               |
| `Nugget__Sync__DownloadFlushSeconds`     | `60`                | How often download counts are saved.                                       |
| `Nugget__Sync__ChangeRetentionHours`     | `24`                | How long change log entries are kept after a snapshot includes them.      |
| `Storage__ServiceUrl`                    | *(AWS)*             | S3 endpoint, e.g. `https://minio.corp.example:9000`.                       |
| `Storage__Bucket`                        | `nuget`             | Bucket.                                                                    |
| `Storage__Prefix`                        | *(none)*            | Key prefix, to share a bucket.                                             |
| `Storage__Region`                        | `us-east-1`         | Region (signing region for non-AWS stores).                                |
| `Storage__AccessKey` / `__SecretKey`     | *(credential chain)* | Static credentials; otherwise the AWS default chain (env, profile, IRSA). |
| `Storage__ForcePathStyle`                | `true`              | `host/bucket/key` URLs (needed by MinIO and most non-AWS stores).          |
| `Storage__ConditionalWrites`             | `true`              | Use `If-Match` / `If-None-Match` (see [Operations](#operations)).          |
| `Storage__CreateBucket`                  | `true`              | Create the bucket when it is missing.                                      |
| `Storage__UseChunkEncoding`              | `true`              | `aws-chunked` uploads; disable for stores that reject it.                  |
| `ASPNETCORE_HTTPS_PORTS`                 |                     | HTTPS port(s), e.g. `8443`.                                                |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` / `__KeyPath` |  | TLS certificate and key (PEM).                                |
| `SSL_CERT_DIR`                           | `/etc/ssl/certs`    | Extra trusted CA directories, e.g. `/etc/ssl/certs:/certs/ca`.             |

**Internal CA for S3**: the S3 client checks MinIO's certificate against the
OS trust store. Either bake the CA into the image (see the comment in
`Nugget.Api/Dockerfile`) or mount it and add its directory to `SSL_CERT_DIR`
(as `deploy/docker-compose.yml` does).

## How it works

Everything lives in the bucket:

```
packages/{id}/{version}/{id}.{version}.nupkg   the package, byte for byte (lower case, like nuget.org)
packages/{id}/{version}/{id}.nuspec            extracted manifest
packages/{id}/{version}/icon|readme|license    extracted embedded files
metadata/{id}.json.gz                          all versions of one package ID with exact ID/version
catalog/snapshot.json.gz                       compact catalog of all packages, for fast startup
changes/{ticks}-{instance}-{kind}-{subject}    small change log shared by instances
vulnerabilities/{name}.json.gz                 mirrored advisory files
```

* An upload writes the package files first and then the package's metadata
  document; the metadata document is the commit point. It is updated with
  conditional writes (`If-Match` on the previous ETag), so concurrent uploads
  of versions of the same package, through any instance, never lose a version.
* Each instance keeps a catalog of all package IDs and versions in memory.
  Search, autocomplete, version lists (the call restore makes for every
  package) and 404s for packages that are not mirrored are answered from it
  without touching S3. It loads from the snapshot at startup; roughly
  200,000 package IDs with 2 million versions load in a few seconds, take a
  few hundred MB of RAM, and search in 20-90 ms.
* Instances learn about each other's changes by polling the change log (one
  `ListObjectsV2` request every few seconds). Snapshots are rewritten
  periodically by whichever instance gets there first, and old change log
  entries are removed.
* Registration documents are built from the metadata documents, which are
  cached in memory.

## Operations

* **Multiple instances**: run as many as you like behind a load balancer
  against the same bucket; no sticky sessions needed. Uploads through one
  instance appear on the others within `PollIntervalSeconds`. This relies on
  the store honouring conditional writes (`If-Match` / `If-None-Match` on
  `PutObject`); MinIO, RustFS and AWS S3 do. Check yours: the second of these
  must fail with `PreconditionFailed`:

  ```bash
  aws --endpoint-url "$S3_URL" s3api put-object --bucket nuget --key probe --body README.md --if-none-match '*'
  aws --endpoint-url "$S3_URL" s3api put-object --bucket nuget --key probe --body README.md --if-none-match '*'
  aws --endpoint-url "$S3_URL" s3api delete-object --bucket nuget --key probe
  ```

  If it does not, set `Storage__ConditionalWrites=false` and run a **single**
  instance.
* **Startup**: `/health/ready` returns 503 until the catalog is loaded; the
  flat container and registration endpoints already work meanwhile (from S3).
  Search answers 503 until then.
* **Recovery**: the package files and metadata documents are the source of
  truth; the snapshot and change log are derived. If the snapshot is missing
  or damaged it is rebuilt automatically from the metadata documents; you can
  also force that with `POST /api/v2/admin/rebuild`.
* **Backups**: back up (or replicate) the bucket. Nothing else holds state.
* **Unlisting** hides a version from search and "latest" while keeping it
  restorable (like nuget.org). `?purge=true` removes it completely.
* **Tested with** MinIO (also over TLS with an internal CA), RustFS, and real
  .NET 10 clients restoring, searching and installing tools with the default
  nuget.org source on a network without internet access. Note that MinIO's
  open-source server has been archived upstream and receives no more
  security updates; S3 compatible alternatives work too.

## Limitations

* Only `api.nuget.org` is emulated. `www.nuget.org` (the gallery website,
  account pages, the V2 OData feed) is not; point `www.nuget.org` at Nugget as
  well (and add it to the certificate) if scripts download through
  `https://www.nuget.org/api/v2/package/{id}/{version}`, which is redirected.
* No catalog (`Catalog/3.0.0`), no symbol server (`.snupkg` uploads are
  rejected), no deprecation information, no owners and no "verified" prefix
  checkmarks (nuget.org does not publish that data with the packages).
* Download counts are this mirror's own, not nuget.org's.
* The service index does not advertise `RepositorySignatures`; packages keep
  nuget.org's repository signatures, so `trustedSigners` configured for the
  nuget.org repository certificate keep working.

## Development

```bash
dotnet test                       # 49 tests, in-memory S3; no services needed
```

Run the server against a local S3 server (any S3 compatible one), e.g.:

```bash
docker run -d -p 9000:9000 -e RUSTFS_ACCESS_KEY=minioadmin \
  -e RUSTFS_SECRET_KEY=minioadmin rustfs/rustfs
dotnet run --project Nugget.Api   # Development: http://localhost:5080, API key "dev-api-key"
```

`appsettings.Development.json` points at `http://localhost:9000` with
`minioadmin` credentials. Then fill it and restore a project from it:

```bash
scripts/mirror-packages.sh        # example packages from nuget.org
cd examples/SampleApp
dotnet run                        # restores through examples/nuget.config
dotnet tool install --tool-path ../.tools dotnetsay   # tools come from Nugget too
```

`examples/nuget.config` replaces nuget.org with the development server and
uses its own package cache (`examples/.nuget-packages`), so every package
really comes from Nugget. Copy it next to any project to test that project
against the server.

Build the image with `docker build -f Nugget.Api/Dockerfile .` (the tests run
during the build; `--build-arg SKIP_TESTS=true` skips them).

Code style follows `Nugget.sln.DotSettings` (Rider / ReSharper).
