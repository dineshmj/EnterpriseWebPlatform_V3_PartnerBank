#!/usr/bin/env bash
# ┌─ :: RESILIENCE + SECURITY :: Start check: the built image really serves, locked down ─────────
# │ Why : A Partner Bank image fails only at its first start if something it needs at run time is
# │       wrong - a file it may not read, a folder it may not write, the SQLite native library,
# │       the TLS set-up. That must show in CI, before the image is published.
# │ How : A throw-away PKI (root, server certificate, client certificate, a request-signing
# │       certificate; valid one day, in the runner's temp folder, never .ewp-pki) mounted
# │       read-only where the image expects it. The container runs as the image's own non-root
# │       user, with a read-only root file system, no capabilities, no privilege escalation, a
# │       fresh /data volume and the settings of the manifest's "check" entry. When it has
# │       started, ONE request over TLS (the server certificate checked against the throw-away
# │       root; with a client certificate where the port requires one) must answer the expected
# │       status. The container's log goes to the job log either way.
# │ Ref : doc/Containers.md "What CI does per image" | CIS Docker Benchmark 5.4, 5.12, 5.25
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -euo pipefail

: "${IMAGE:?}" "${CHECK:?}"
pki="$RUNNER_TEMP/start-check-pki"
name="start-check"
rm -rf "$pki" && mkdir -p "$pki"

port=$(jq -r '.port' <<< "$CHECK")
path=$(jq -r '.path' <<< "$CHECK")
expect=$(jq -r '.expect' <<< "$CHECK")
client=$(jq -r '.clientCertificate' <<< "$CHECK")

# ── The throw-away PKI (the same file names as %USERPROFILE%\.ewp-pki) ──────────────────────────────
cd "$pki"
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 1 \
  -subj "/CN=Start check root" -keyout root.key -out root.pem \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
openssl x509 -in root.pem -outform DER -out ewp-trust-root.cer

issue() { # $1 name, $2 subject, $3 extensions
  openssl req -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -subj "$2" -keyout "$1.key" -out "$1.csr"
  printf '%s\n' "$3" > "$1.ext"
  openssl x509 -req -in "$1.csr" -CA root.pem -CAkey root.key -CAcreateserial -days 1 -extfile "$1.ext" -out "$1.pem"
}
issue server "/CN=localhost" "subjectAltName=DNS:localhost
extendedKeyUsage=serverAuth
keyUsage=critical,digitalSignature"
issue client "/CN=Start check client" "extendedKeyUsage=clientAuth
keyUsage=critical,digitalSignature"
issue signing "/CN=Start check request signing" "keyUsage=critical,digitalSignature"

password=$(openssl rand -hex 16)
printf '%s' "$password" > partner-bank-server.pfx.password
openssl pkcs12 -export -inkey server.key -in server.pem -certfile root.pem \
  -passout "pass:$password" -out partner-bank-server.pfx
openssl x509 -in signing.pem -outform DER -out ewp-request-signing.cer

# SEC: readable by the container's non-root user; the folder is mounted read-only.
chmod 755 "$pki"
chmod 644 "$pki"/*
cd - > /dev/null

# ── Run it, locked down ─────────────────────────────────────────────────────────────────────────────
env_args=()
while IFS= read -r pair; do env_args+=(-e "$pair"); done < <(jq -r '.env | to_entries[] | "\(.key)=\(.value)"' <<< "$CHECK")

docker rm -f "$name" > /dev/null 2>&1 || true
docker run -d --name "$name" \
  --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp \
  --mount type=tmpfs,destination=/data,tmpfs-mode=1777 \
  --mount "type=bind,source=$pki,destination=/etc/partner-bank/pki,readonly" \
  -p "127.0.0.1:$port:$port" \
  "${env_args[@]}" \
  "$IMAGE" > /dev/null

cleanup() {
  echo "----- container log -----"
  docker logs "$name" 2>&1 || true
  docker rm -f "$name" > /dev/null 2>&1 || true
}
trap cleanup EXIT

# Started = the host's "Application started." line; a container that stops first has failed.
# NOTE: grep reads the WHOLE log (no -q): with -q it stops at the match, docker logs gets SIGPIPE
#       while still writing, and pipefail turns a found line into "not found".
started() { docker logs "$name" 2>&1 | grep 'Application started' > /dev/null; }
for _ in $(seq 1 60); do
  if [ "$(docker inspect -f '{{.State.Running}}' "$name")" != "true" ]; then
    echo "::error::$IMAGE stopped during start-up (exit code $(docker inspect -f '{{.State.ExitCode}}' "$name"))."
    exit 1
  fi
  if started; then break; fi
  sleep 1
done
if ! started; then
  echo "::error::$IMAGE did not start within 60 seconds."
  exit 1
fi

cert_args=()
if [ "$client" = "true" ]; then cert_args=(--cert "$pki/client.pem" --key "$pki/client.key"); fi
status=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 15 --cacert "$pki/root.pem" "${cert_args[@]}" \
  "https://localhost:$port$path")

echo "GET https://localhost:$port$path -> $status (expected $expect)"
if [ "$status" != "$expect" ]; then
  echo "::error::$IMAGE answered $status on $path, expected $expect."
  exit 1
fi