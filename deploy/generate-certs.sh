#!/usr/bin/env bash
# Creates an internal CA and a TLS certificate that lets Nugget answer for
# api.nuget.org (and any extra names you pass).
#
#   ./generate-certs.sh                         # api.nuget.org only
#   ./generate-certs.sh nuget.corp.example 10.1.2.3
#
# Output (in ./certs, or $CERT_DIR):
#   ca.crt        distribute to every developer and build machine's trust store
#   ca.key        keep offline / secret; only needed to issue new certificates
#   nugget.crt    server certificate (with the CA appended, full chain)
#   nugget.key    server private key
#
# If your organisation already runs a CA, issue a certificate for the same
# names from it instead and skip this script.
set -euo pipefail

CERT_DIR="${CERT_DIR:-$(dirname "$0")/certs}"
DAYS="${DAYS:-825}"
mkdir -p "$CERT_DIR"
cd "$CERT_DIR"

names=(api.nuget.org "$@")
san=""
for name in "${names[@]}"; do
  if [[ "$name" =~ ^[0-9.]+$ || "$name" == *:* ]]; then
    san+="IP:${name},"
  else
    san+="DNS:${name},"
  fi
done
san="${san%,}"

if [[ ! -f ca.key ]]; then
  echo "Creating CA (ca.crt / ca.key)"
  openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
    -keyout ca.key -out ca.crt \
    -subj "/CN=Nugget Internal CA" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1
  chmod 600 ca.key
else
  echo "Reusing existing CA (ca.crt / ca.key)"
fi

echo "Issuing server certificate for: ${names[*]}"
openssl req -newkey rsa:2048 -sha256 -nodes \
  -keyout nugget.key -out nugget.csr \
  -subj "/CN=${names[0]}" >/dev/null 2>&1
cat > nugget.ext <<EXT
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=${san}
EXT
# 825 days is the maximum validity Apple platforms accept for TLS certificates.
openssl x509 -req -in nugget.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -days "$DAYS" -sha256 -extfile nugget.ext -out nugget.leaf.crt >/dev/null 2>&1
cat nugget.leaf.crt ca.crt > nugget.crt
rm -f nugget.csr nugget.ext nugget.leaf.crt ca.srl
# The container runs as an unprivileged user.
chmod 644 nugget.crt nugget.key

echo "Done. Next: trust ca.crt on clients and point api.nuget.org at this host."
