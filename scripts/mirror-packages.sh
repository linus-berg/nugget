#!/usr/bin/env bash
# Downloads packages from nuget.org and uploads them to a Nugget server.
#
#   scripts/mirror-packages.sh                            # the example packages below
#   scripts/mirror-packages.sh Serilog Polly.Core@8.9.1   # latest Serilog, a given Polly.Core
#   NUGGET_URL=https://nuget.corp.example NUGGET_API_KEY=... \
#     CURL_OPTS="--cacert deploy/certs/ca.crt" scripts/mirror-packages.sh Newtonsoft.Json
#
# Settings (environment variables):
#   NUGGET_URL            server to upload to (default: the development server,
#                         http://localhost:5080)
#   NUGGET_API_KEY        API key (default: dev-api-key, from
#                         appsettings.Development.json)
#   SOURCE_URL            where packages come from (default: https://api.nuget.org)
#   DOWNLOAD_DIR          download cache (default: $TMPDIR/nugget-packages)
#   WITH_VULNERABILITIES  1 = also mirror nuget.org's vulnerability data, so
#                         restore's NuGetAudit warnings work against Nugget
#   CURL_OPTS             extra curl options for the upload, e.g. --cacert
#
# Dependencies are not resolved: list every package a project needs (restoring
# against the server reports the missing ones). Versions must be normalized
# (1.0.0, not 1.0), like nuget.org's download URLs.
set -euo pipefail

NUGGET_URL="${NUGGET_URL:-http://localhost:5080}"
NUGGET_API_KEY="${NUGGET_API_KEY:-dev-api-key}"
SOURCE_URL="${SOURCE_URL:-https://api.nuget.org}"
DOWNLOAD_DIR="${DOWNLOAD_DIR:-${TMPDIR:-/tmp}/nugget-packages}"
WITH_VULNERABILITIES="${WITH_VULNERABILITIES:-0}"
read -r -a curl_opts <<< "${CURL_OPTS:-}"

# Everything examples/SampleApp needs (including dependencies), plus a .NET
# tool to try `dotnet tool install`.
EXAMPLES=(
  Newtonsoft.Json@13.0.3
  Serilog@4.0.0
  Serilog@4.4.0
  Serilog.Sinks.Console@6.0.0
  Humanizer.Core@2.14.1
  Polly.Core@8.9.1
  dotnetsay@3.0.3
)

added=0
present=0
failed=0

lower() {
  tr '[:upper:]' '[:lower:]' <<< "$1"
}

# Newest stable version on the source (newest prerelease if there is none).
latest_version() {
  local versions
  versions=$(curl -fs --compressed "$SOURCE_URL/v3-flatcontainer/$1/index.json" |
    tr -d ' \r\n\t' | sed 's/.*"versions":\[//; s/\].*//' | tr ',' '\n' | tr -d '"') ||
    return 1
  grep -v -- '-' <<< "$versions" | tail -n 1 | grep . || tail -n 1 <<< "$versions"
}

# upload <file> <url> <content type> <label> [replace]
# With "replace" (vulnerability files), 200 means the file was stored.
upload() {
  local response status
  response=$(mktemp)
  status=$(curl -sS ${curl_opts[@]+"${curl_opts[@]}"} -o "$response" -w '%{http_code}' \
    -X PUT -H "X-NuGet-ApiKey: $NUGGET_API_KEY" -H "Content-Type: $3" \
    --data-binary "@$1" "$2") || status=000
  case "$status" in
    201) echo "   added    $4"; added=$((added + 1)) ;;
    200)
      if [[ "${5:-}" == "replace" ]]; then
        echo "   stored   $4"
        added=$((added + 1))
      else
        echo "   present  $4"
        present=$((present + 1))
      fi
      ;;
    *)
      echo "!! $status      $4: $(cat "$response")" >&2
      failed=$((failed + 1))
      ;;
  esac
  rm -f "$response"
}

mirror_package() {
  local id="${1%%@*}" version="" lower_id lower_version file url
  [[ "$1" == *@* ]] && version="${1#*@}"
  lower_id=$(lower "$id")
  if [[ -z "$version" ]]; then
    if ! version=$(latest_version "$lower_id"); then
      echo "!! $id is not on $SOURCE_URL" >&2
      failed=$((failed + 1))
      return
    fi
  fi

  lower_version=$(lower "$version")
  file="$DOWNLOAD_DIR/$lower_id.$lower_version.nupkg"
  url="$SOURCE_URL/v3-flatcontainer/$lower_id/$lower_version/$lower_id.$lower_version.nupkg"
  if [[ ! -s "$file" ]]; then
    if ! curl -fsS -o "$file.part" "$url"; then
      echo "!! could not download $id $version ($url)" >&2
      rm -f "$file.part"
      failed=$((failed + 1))
      return
    fi
    mv "$file.part" "$file"
  fi

  # The server reads the exact ID and version from the package itself.
  upload "$file" "$NUGGET_URL/api/v2/package" application/octet-stream "$id $version"
}

mirror_vulnerabilities() {
  local index name url file
  index=$(curl -fsS --compressed "$SOURCE_URL/v3/vulnerabilities/index.json")
  # Entries look like {"@name":"base","@id":"https://.../vulnerability.base.json",...}
  while IFS= read -r entry; do
    name=$(sed 's/^"@name":"\([^"]*\)".*/\1/' <<< "$entry")
    url=$(sed 's/.*"@id":"\([^"]*\)"$/\1/' <<< "$entry")
    file="$DOWNLOAD_DIR/vulnerability.$name.json"
    curl -fsS --compressed -o "$file" "$url"
    upload "$file" "$NUGGET_URL/api/v2/vulnerabilities/$name" application/json \
      "vulnerability data '$name'" replace
  done < <(grep -o '"@name":"[^"]*","@id":"[^"]*"' <<< "$index")
}

packages=("$@")
if [[ ${#packages[@]} -eq 0 ]]; then
  packages=("${EXAMPLES[@]}")
fi

mkdir -p "$DOWNLOAD_DIR"
echo "Mirroring ${#packages[@]} package(s) from $SOURCE_URL to $NUGGET_URL"
for spec in "${packages[@]}"; do
  mirror_package "$spec"
done

if [[ "$WITH_VULNERABILITIES" == "1" ]]; then
  echo "Mirroring vulnerability data"
  mirror_vulnerabilities
fi

echo "Done: $added added, $present already present, $failed failed"
[[ $failed -eq 0 ]]
