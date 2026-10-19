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
  # dotnet-stack itself waits forever when the runtime cannot suspend its threads: bounded to 60 s
  if [ -x "$stack" ]; then
    "$stack" report -p "$p" > "${TMPDIR:-/tmp}/stack-$p.txt" 2>&1 & s=$!
    for ((w = 0; w < 60; w++)); do kill -0 "$s" 2>/dev/null || break; sleep 1; done
    kill -0 "$s" 2>/dev/null && { kill "$s"; echo "(pas de réponse du runtime : threads impossibles à suspendre)"; }
    head -400 "${TMPDIR:-/tmp}/stack-$p.txt"
  fi
done
pkill -x pstbrowser-cli
exit 124
