# Shared settings for start/stop/status scripts (sourced, not executed).
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
GATEWAY_PORT=${GATEWAY_PORT:-6200}
GATEWAY_DATA_DIR=${GATEWAY_DATA_DIR:-$ROOT/data}
PUBLISH_DIR="$ROOT/publish"
PID_FILE="$GATEWAY_DATA_DIR/gateway.pid"
LOG_FILE="$GATEWAY_DATA_DIR/logs/gateway.log"

# Prints the pid from the pid file if that process is alive and is this gateway; returns 1 otherwise.
gateway_pid() {
  [[ -f $PID_FILE ]] || return 1
  local pid; pid=$(cat "$PID_FILE")
  [[ $pid =~ ^[0-9]+$ ]] || return 1
  local cmd; cmd=$(ps -o command= -p "$pid" 2>/dev/null) || return 1
  [[ $cmd == *"$PUBLISH_DIR/Gateway.dll"* ]] || return 1
  echo "$pid"
}

port_listener() { lsof -nP -iTCP:"$GATEWAY_PORT" -sTCP:LISTEN -t 2>/dev/null | head -1; }
