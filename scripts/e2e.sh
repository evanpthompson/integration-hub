#!/usr/bin/env bash
# End-to-end check for MVP-0: manifest -> orchestrator -> gRPC -> worker -> upstream
# -> JMESPath -> canonical record. Starts both services, asserts, tears them down.
#
# Usage: scripts/e2e.sh
#
# ponytail: hits the real Open-Meteo API rather than a stub. It is keyless, stable,
# and the whole point of MVP-0 is proving a real upstream works. CI gets a stubbed
# variant when CI exists (Phase 3, task 3.3) — a network dependency there would be
# flaky for no benefit.
set -euo pipefail

cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

ORCH_URL="http://localhost:5066"
SYNTH_URL="http://localhost:8080"
WORKER_PORT="${WORKER_PORT:-50051}"
FAILURES=0

pass() { printf '  \033[32mok\033[0m   %s\n' "$1"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; FAILURES=$((FAILURES + 1)); }

cleanup() {
  [[ -n "${WORKER_PID:-}" ]] && kill "$WORKER_PID" 2>/dev/null || true
  [[ -n "${ORCH_PID:-}" ]] && kill "$ORCH_PID" 2>/dev/null || true
  [[ -n "${SYNTH_PID:-}" ]] && kill "$SYNTH_PID" 2>/dev/null || true
  wait 2>/dev/null || true
}
trap cleanup EXIT

# assert_json <label> <method> <path> <body> <expected-status> <python-expr-on-`d`>
assert_json() {
  local label="$1" method="$2" path="$3" body="$4" want_status="$5" expr="${6:-True}"
  local out status payload
  out=$(curl -s -X "$method" "$ORCH_URL$path" \
        -H 'content-type: application/json' \
        ${body:+-d "$body"} -w '\n%{http_code}')
  status="${out##*$'\n'}"
  payload="${out%$'\n'*}"

  if [[ "$status" != "$want_status" ]]; then
    fail "$label — wanted HTTP $want_status, got $status: $payload"
    return
  fi
  if ! printf '%s' "$payload" | python3 -c "
import json,sys
d = json.load(sys.stdin)
sys.exit(0 if ($expr) else 1)
" 2>/dev/null; then
    fail "$label — HTTP $status but assertion failed: $payload"
    return
  fi
  pass "$label"
}

# A process left over from manual testing will silently serve these requests and
# make the teardown assertions lie — "worker down" passes as 200 because a
# different worker answered. Fail fast instead.
for port in 5066 8080 "$WORKER_PORT"; do
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    echo "port $port is already in use — stop the stray process first:" >&2
    lsof -nP -iTCP:"$port" -sTCP:LISTEN >&2
    exit 1
  fi
done

echo "==> regenerating protobuf stubs"
uv run python -m grpc_tools.protoc \
  --proto_path=proto --python_out=worker --grpc_python_out=worker --pyi_out=worker \
  proto/worker.proto

echo "==> building orchestrator"
dotnet build src/Orchestrator -v q --nologo >/dev/null

echo "==> starting synthetic upstream"
(cd synthetic && go run . >/tmp/ih-synthetic.log 2>&1) &
SYNTH_PID=$!

echo "==> starting worker"
uv run python worker/server.py >/tmp/ih-worker.log 2>&1 &
WORKER_PID=$!

echo "==> starting orchestrator"
dotnet run --project src/Orchestrator --no-build >/tmp/ih-orch.log 2>&1 &
ORCH_PID=$!

curl -s --retry 40 --retry-all-errors --retry-delay 1 --max-time 90 \
  "$ORCH_URL/healthz" >/dev/null || { echo "orchestrator never came up"; tail -20 /tmp/ih-orch.log; exit 1; }
curl -s --retry 40 --retry-all-errors --retry-delay 1 --max-time 90 \
  "$SYNTH_URL/healthz" >/dev/null || { echo "synthetic never came up"; tail -20 /tmp/ih-synthetic.log; exit 1; }
curl -s -X POST "$SYNTH_URL/_synth/reset" >/dev/null

echo
echo "==> health and registry"
assert_json "liveness"                 GET  /healthz       ''  200 "d['status'] == 'ok'"
assert_json "registry loaded manifests" GET /integrations  ''  200 \
  "{'open-meteo','github'} <= {i['id'] for i in d}"

echo
echo "==> the thesis: manifests drive real calls"
assert_json "currentWeather returns a canonical record" \
  POST /integrations/open-meteo/resources/currentWeather/invoke \
  '{"latitude":"38.88","longitude":"-94.82"}' 200 \
  "d['count'] == 1 and isinstance(d['records'][0]['tempC'], (int, float)) and d['records'][0]['id']"

assert_json "elevation — added as YAML only, never compiled against" \
  POST /integrations/open-meteo/resources/elevation/invoke \
  '{"latitude":"38.88","longitude":"-94.82"}' 200 \
  "d['count'] == 1 and isinstance(d['records'][0]['meters'], (int, float))"

echo
echo "==> deterministic upstream: same seed, same bytes"
assert_json "synthetic.orders renames and flattens a nested payload" \
  POST /integrations/synthetic/resources/orders/invoke '{"limit":"2"}' 200 \
  "d['count'] == 2 and d['records'][0]['id'] == 'ord_00001' and d['records'][0]['lineCount'] >= 1"

assert_json "synthetic.snapshot reaches through nesting" \
  POST /integrations/synthetic/resources/snapshot/invoke '{"station":"KMCI"}' 200 \
  "d['records'][0]['id'] == 'KMCI' and 'tempC' in d['records'][0]"

echo
echo "==> retries: the fault is armed in YAML, not in this script"
assert_json "two upstream failures then success is RETRIED_SUCCESS" \
  POST /integrations/synthetic-flaky/resources/orders/invoke '{}' 200 \
  "d['attempts'] == 3 and d['outcome'] == 'RetriedSuccess' and d['count'] == 1"

assert_json "a first-time success is not mislabelled as retried" \
  POST /integrations/synthetic/resources/orders/invoke '{"limit":"1"}' 200 \
  "d['attempts'] == 1 and d['outcome'] == 'Success'"

echo
echo "==> failures are reported, not swallowed"
assert_json "missing required param is caught before any network call" \
  POST /integrations/open-meteo/resources/currentWeather/invoke \
  '{"latitude":"38.88"}' 400 "d['error'] == 'INVALID_PARAMS' and 'longitude' in d['message']"

assert_json "undeclared param is rejected, not dropped" \
  POST /integrations/open-meteo/resources/currentWeather/invoke \
  '{"latitude":"38.88","longitude":"-94.82","sneaky":"x"}' 400 \
  "d['error'] == 'INVALID_PARAMS' and 'sneaky' in d['message']"

assert_json "unknown resource" \
  POST /integrations/open-meteo/resources/nope/invoke '{}' 404 \
  "d['error'] == 'UNKNOWN_RESOURCE'"

assert_json "unknown integration" \
  POST /integrations/nope/resources/nope/invoke '{}' 404 \
  "d['error'] == 'UNKNOWN_INTEGRATION'"

echo
echo "==> worker down is a clean 503, not a hang or a stack trace"
kill "$WORKER_PID" 2>/dev/null || true
wait "$WORKER_PID" 2>/dev/null || true
WORKER_PID=""
assert_json "worker unavailable" \
  POST /integrations/open-meteo/resources/currentWeather/invoke \
  '{"latitude":"38.88","longitude":"-94.82"}' 503 "d['error'] == 'WORKER_UNAVAILABLE'"

echo
if [[ "$FAILURES" -eq 0 ]]; then
  printf '\033[32mMVP-0 end-to-end: all checks passed\033[0m\n'
else
  printf '\033[31mMVP-0 end-to-end: %d check(s) failed\033[0m\n' "$FAILURES"
  exit 1
fi
