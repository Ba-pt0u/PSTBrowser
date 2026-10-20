#!/bin/bash
# UI smoke test of the macOS / Linux application (run by .github/workflows/build.yml):
# PstBrowser --ui-smoke drives the interface on a prepared case workspace and writes « SMOKE <step> »;
# this script takes a screenshot at each step (Linux: under Xvfb with ImageMagick; macOS: screencapture).
# Usage: tests/ui-smoke-desktop.sh <PstBrowser executable> <case folder> <output folder> <query>
app=$1; case_dir=$2; out=$3; query=$4
mkdir -p "$out"
shot() {
  if [ "$(uname)" = Darwin ]; then screencapture -x "$1"; else import -window root "$1"; fi
  [ -f "$1" ] || : > "$1.manquante"
}
"$app" --ui-smoke "$case_dir" "$out" "$query" 2>"$out/stderr.log" | while IFS= read -r line; do
  echo "$line"
  case "$line" in
    "SMOKE "*) shot "$out/${line#SMOKE }.png" ;;
  esac
done
exit "${PIPESTATUS[0]}"
