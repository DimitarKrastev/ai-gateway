#!/usr/bin/env bash
# Stops the running gateway (if any), republishes from the current checkout and starts it again.
set -euo pipefail
dir=$(dirname "$0")
"$dir/stop.sh"
"$dir/start.sh" "$@"
