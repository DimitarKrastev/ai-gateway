#!/usr/bin/env bash
# End-to-end run against the fake providers: builds, starts the standalone fakes and a throw-away gateway on free
# ports from AUX_PORTS, replays Claude Code + Codex traffic (e2e/run.mjs) and checks the results.
# Only processes started by this script are stopped. Usage: scripts/e2e.sh [--keep] [--no-build]
#   --keep      leave fakes + gateway running afterwards (prints URLs, password and a stop command)
#   --no-build  reuse the existing Release build
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$(pwd)

KEEP=0; BUILD=1
for a in "$@"; do
  case "$a" in
    --keep) KEEP=1 ;;
    --no-build) BUILD=0 ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done

AUX=${AUX_PORTS:-6202-6219}
LO=${AUX%-*}; HI=${AUX#*-}
port_free() { ! lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }
pick_port() {
  local p
  for ((p = LO; p <= HI; p++)); do
    if [[ " ${TAKEN:-} " != *" $p "* ]] && port_free "$p"; then TAKEN="${TAKEN:-} $p"; echo "$p"; return 0; fi
  done
  echo "no free port in $AUX" >&2; return 1
}

if [[ $BUILD == 1 ]]; then
  dotnet build AiGateway.sln -c Release -nologo -v q
fi
GW_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
FAKES_DLL="$ROOT/src/Gateway.Fakes/bin/Release/net10.0/Gateway.Fakes.dll"

TAKEN=""
FAKES_PORT=$(pick_port); TAKEN=" $FAKES_PORT"
GW_PORT=$(pick_port)
WORK=$(mktemp -d "${TMPDIR:-/tmp}/aigw-e2e.XXXXXX")
PASSWORD="e2e-$(openssl rand -hex 8)"
FAKES_URL="http://127.0.0.1:$FAKES_PORT"
GW_URL="http://127.0.0.1:$GW_PORT"

PIDS=()
cleanup() {
  local code=$?
  if [[ $KEEP == 1 && $code == 0 ]]; then
    printf '%s\n' "${PIDS[@]}" > "$WORK/pids"
    echo
    echo "Left running:  gateway $GW_URL  (admin password: $PASSWORD)  fakes $FAKES_URL"
    echo "Data dir:      $WORK/data   logs: $WORK/*.log"
    echo "Stop with:     kill \$(cat $WORK/pids)"
    return
  fi
  for pid in "${PIDS[@]}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
  if [[ $code != 0 ]]; then echo "logs kept in $WORK" >&2; else rm -rf "$WORK"; fi
}
trap cleanup EXIT

wait_up() {
  local url=$1 i
  for ((i = 0; i < 100; i++)); do
    curl -fsS -o /dev/null "$url" 2>/dev/null && return 0
    sleep 0.2
  done
  echo "timed out waiting for $url" >&2; return 1
}

echo "fakes on $FAKES_PORT, gateway on $GW_PORT, work dir $WORK"
# Fakes generate at E2E_FAKE_TPS output tokens/s (fast mode 2.5x) so recorded output speeds are realistic.
FAKES_PORT=$FAKES_PORT AUX_PORTS=$AUX Fakes__TokensPerSecond=${E2E_FAKE_TPS:-1500} dotnet "$FAKES_DLL" >"$WORK/fakes.log" 2>&1 &
PIDS+=($!)
GATEWAY_PORT=$GW_PORT GATEWAY_DATA_DIR="$WORK/data" GATEWAY_ADMIN_PASSWORD=$PASSWORD \
GATEWAY_UPSTREAM_ANTHROPIC_API=$FAKES_URL GATEWAY_UPSTREAM_ANTHROPIC_AUTH=$FAKES_URL \
GATEWAY_UPSTREAM_OPENAI_API=$FAKES_URL GATEWAY_UPSTREAM_CHATGPT=$FAKES_URL GATEWAY_UPSTREAM_OPENAI_AUTH=$FAKES_URL \
  dotnet "$GW_DLL" >"$WORK/gateway.log" 2>&1 &
PIDS+=($!)

wait_up "$FAKES_URL/fake-admin/anthropic/accounts"
wait_up "$GW_URL/api/health"

GATEWAY_URL=$GW_URL FAKES_URL=$FAKES_URL GATEWAY_ADMIN_PASSWORD=$PASSWORD node "$ROOT/e2e/run.mjs"
