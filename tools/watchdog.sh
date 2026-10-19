#!/bin/bash
# Runs a command with a time limit (CI, macOS / Linux). On a hang, prints the managed stacks of every PST Browser
# process (dotnet-stack, when installed) before failing, so that the cause can be found from the log.
# Usage: tools/watchdog.sh <seconds> <command> [arguments...]
limit=$1; shift
"$@" &
pid=$!
for ((t = 0; t < limit; t++)); do
  kill -0 "$pid" 2>/dev/null || { wait "$pid"; exit $?; }
  sleep 1
done
echo "::error::Délai dépassé ($limit s) : $*"
stack=$(command -v dotnet-stack || echo "$HOME/.dotnet/tools/dotnet-stack")
for p in $(pgrep -x pstbrowser-cli); do
  echo "===== processus $p : $(ps -o args= -p "$p")"
  if [ -x "$stack" ]; then "$stack" report -p "$p" 2>&1 | head -400; fi
done
pkill -x pstbrowser-cli
exit 124
