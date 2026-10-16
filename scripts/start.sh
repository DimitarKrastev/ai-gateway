#!/usr/bin/env bash
# Publishes the gateway (Release, incl. the web UI) to ./publish and starts it detached from this terminal, so it
# keeps running after the shell/session ends. Port: GATEWAY_PORT (default 6200). Data: GATEWAY_DATA_DIR (default ./data).
# Other GATEWAY_* variables (see README) are passed through. Usage: scripts/start.sh [--no-publish]
set -euo pipefail
source "$(dirname "$0")/_common.sh"
cd "$ROOT"

PUBLISH=1
[[ ${1:-} == --no-publish ]] && PUBLISH=0

if pid=$(gateway_pid); then
  echo "Gateway already running (pid $pid) on port $GATEWAY_PORT. Use scripts/restart.sh to restart."
  exit 0
fi
if listener=$(port_listener) && [[ -n $listener ]]; then
  echo "Port $GATEWAY_PORT is in use by another process (pid $listener: $(ps -o command= -p "$listener" | cut -c1-80)). Not starting." >&2
  exit 1
fi

if [[ -n $(git status --porcelain 2>/dev/null || true) ]]; then
  echo "warning: working tree has uncommitted changes; the published build includes them." >&2
fi
if [[ $PUBLISH == 1 || ! -f $PUBLISH_DIR/Gateway.dll ]]; then
  echo "Publishing to $PUBLISH_DIR ..."
  dotnet publish src/Gateway/Gateway.csproj -c Release -o "$PUBLISH_DIR" -nologo -v q
fi

mkdir -p "$GATEWAY_DATA_DIR/logs"
export GATEWAY_PORT GATEWAY_DATA_DIR
# New session (setsid) + no controlling terminal + ignored SIGHUP: survives the terminal or agent session closing.
nohup /usr/bin/perl -MPOSIX -e 'POSIX::setsid(); exec @ARGV or die "exec: $!"' \
  dotnet "$PUBLISH_DIR/Gateway.dll" </dev/null >>"$LOG_FILE" 2>&1 &
echo $! > "$PID_FILE"

for _ in $(seq 1 100); do
  if curl -fsS -o /dev/null "http://127.0.0.1:$GATEWAY_PORT/api/health" 2>/dev/null; then
    echo "Gateway running: http://127.0.0.1:$GATEWAY_PORT/ (pid $(cat "$PID_FILE"), log $LOG_FILE)"
    if [[ -z ${GATEWAY_ADMIN_PASSWORD:-} && -f $GATEWAY_DATA_DIR/admin-password.txt ]]; then
      echo "Admin password: see $GATEWAY_DATA_DIR/admin-password.txt"
    fi
    exit 0
  fi
  if ! kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then break; fi
  sleep 0.2
done
echo "Gateway failed to start; last log lines:" >&2
tail -20 "$LOG_FILE" >&2
exit 1
