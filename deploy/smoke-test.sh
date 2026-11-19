#!/usr/bin/env bash
# End-to-end smoke test of the server image over real TLS (P17-T14, P16-T10):
#   build (optional) → run with a fresh self-signed certificate → health over HTTPS → ingest with the Python SDK over
#   HTTPS → read the records back → check the server refuses an untrusted client and runs as non-root.
# Usage (from the repository root): deploy/smoke-test.sh [image]   (default: builds latticelog-server:smoke)
# Needs: docker, openssl, curl, python3.
set -euo pipefail

IMAGE="${1:-}"
NAME="latticelog-smoke-$$"
VOLUME="$NAME-data"
WORK="$(mktemp -d)"
SECRET="smoke-$(openssl rand -hex 16)"
NS="ci/smoke"
COUNT=250

cleanup() {
  status=$?
  if [ $status -ne 0 ] && docker ps -a --format '{{.Names}}' | grep -qx "$NAME"; then
    echo "--- container logs"; docker logs "$NAME" 2>&1 | tail -50 || true
  fi
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  docker volume rm "$VOLUME" >/dev/null 2>&1 || true
  rm -rf "$WORK"
  exit $status
}
trap cleanup EXIT

if [ -z "$IMAGE" ]; then
  IMAGE="latticelog-server:smoke"
  docker build -f deploy/Dockerfile -t "$IMAGE" .
fi

echo "--- certificate and tokens"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" -addext "extendedKeyUsage=serverAuth" \
  -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null
openssl pkcs12 -export -inkey "$WORK/key.pem" -in "$WORK/cert.pem" -out "$WORK/server.pfx" -passout pass:smoke-pass
HASH="$(printf %s "$SECRET" | sha256sum | cut -d' ' -f1)"
cat > "$WORK/tokens.json" <<EOF
{"tokens":[{"id":"smoke","sha256":"$HASH","scopes":["ingest","read"],"namespaces":["$NS"]}]}
EOF
chmod 644 "$WORK/server.pfx" "$WORK/tokens.json"   # readable by the container's non-root user

echo "--- start"
docker run -d --name "$NAME" -p 127.0.0.1:7400:7400 -v "$VOLUME:/data" \
  -v "$WORK/tokens.json:/config/tokens.json:ro" -v "$WORK/server.pfx:/run/secrets/server.pfx:ro" \
  -e LATTICELOG_CERT_PASSWORD=smoke-pass "$IMAGE" >/dev/null
for _ in $(seq 1 60); do
  if curl -sf --cacert "$WORK/cert.pem" https://localhost:7400/v1/health >/dev/null; then break; fi
  sleep 1
done
curl -sf --cacert "$WORK/cert.pem" https://localhost:7400/v1/health | grep -q '"status":"ok"'
echo "health OK over HTTPS"

echo "--- an untrusted client is refused"
if curl -sf https://localhost:7400/v1/health >/dev/null 2>&1; then echo "untrusted client was accepted"; exit 1; fi

echo "--- the process does not run as root"
USER_ID="$(docker inspect -f '{{.Config.User}}' "$IMAGE")"
if [ -z "$USER_ID" ] || [ "$USER_ID" = "0" ] || [ "$USER_ID" = "root" ]; then echo "image runs as root ($USER_ID)"; exit 1; fi
echo "runs as user $USER_ID"

echo "--- ingest $COUNT records with the Python SDK over HTTPS"
SSL_CERT_FILE="$WORK/cert.pem" python3 -I - "$WORK/spool" "$SECRET" "$NS" "$COUNT" <<'PY'
import sys
sys.path.insert(0, "sdks/python")
import latticelog
spool, token, ns, count = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
log = latticelog.Logger(spool, endpoint="https://localhost:7400", token=token, service="smoke", namespace=ns,
                        durability="durable")
for i in range(count):
    (log.error if i % 25 == 0 else log.info)("smoke {i}", event="smoke.tick", i=i)
if not log.flush(timeout=60):
    sys.exit("the server did not acknowledge every record")
log.close()
PY

echo "--- read them back"
BODY="$(curl -sf --cacert "$WORK/cert.pem" -H "Authorization: Bearer $SECRET" \
  "https://localhost:7400/v1/search?ns=ci%2Fsmoke&limit=1000&order=asc")"
python3 -I -c '
import json, sys
doc = json.loads(sys.stdin.read())
events = doc["events"]
values = [e["attributes"]["i"] for e in events]
expected = list(range(int(sys.argv[1])))
assert values == expected, f"got {len(values)} events, first {values[:5]}"
assert doc["coverage"]["partial_results"] is False
print(f"{len(values)} events read back in order")
' "$COUNT" <<< "$BODY"

ERRORS="$(curl -sf --cacert "$WORK/cert.pem" -H "Authorization: Bearer $SECRET" \
  "https://localhost:7400/v1/search?ns=ci%2Fsmoke&where=level%20%3E%3D%20ERROR&limit=1000")"
python3 -I -c 'import json,sys; n=len(json.load(sys.stdin)["events"]); assert n == 10, n; print(f"{n} errors found by query")' <<< "$ERRORS"

echo "--- data survives a restart"
docker restart "$NAME" >/dev/null
for _ in $(seq 1 60); do
  if curl -sf --cacert "$WORK/cert.pem" https://localhost:7400/v1/health >/dev/null; then break; fi
  sleep 1
done
AFTER="$(curl -sf --cacert "$WORK/cert.pem" -H "Authorization: Bearer $SECRET" \
  "https://localhost:7400/v1/search?ns=ci%2Fsmoke&limit=1000")"
python3 -I -c 'import json,sys; n=len(json.load(sys.stdin)["events"]); assert n == int(sys.argv[1]), n; print(f"{n} events after restart")' "$COUNT" <<< "$AFTER"

echo "SMOKE TEST PASSED"
