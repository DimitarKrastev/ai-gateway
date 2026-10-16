#!/usr/bin/env bash
# Stops the gateway started by scripts/start.sh (only the process recorded in the pid file, after checking it is ours).
set -euo pipefail
source "$(dirname "$0")/_common.sh"

if ! pid=$(gateway_pid); then
  echo "Gateway is not running (no live gateway process in $PID_FILE)."
  rm -f "$PID_FILE"
  exit 0
fi
kill -TERM "$pid"
for _ in $(seq 1 50); do
  kill -0 "$pid" 2>/dev/null || { rm -f "$PID_FILE"; echo "Gateway stopped (pid $pid)."; exit 0; }
  sleep 0.2
done
echo "Gateway did not exit after 10 s; sending SIGKILL." >&2
kill -KILL "$pid" 2>/dev/null || true
rm -f "$PID_FILE"
