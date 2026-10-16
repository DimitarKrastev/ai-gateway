#!/usr/bin/env bash
# Shows whether the gateway started by scripts/start.sh is running and healthy.
set -euo pipefail
source "$(dirname "$0")/_common.sh"

if pid=$(gateway_pid); then
  health=$(curl -fsS "http://127.0.0.1:$GATEWAY_PORT/api/health" 2>/dev/null || echo "unreachable")
  echo "running  pid $pid  port $GATEWAY_PORT  health: $health"
  echo "log: $LOG_FILE"
else
  echo "not running"
  exit 1
fi
