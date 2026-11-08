#!/bin/bash
# Builds « PST Browser.app » for one Mac architecture and zips it.
# Usage: packaging/macos/build-app.sh <osx-arm64|osx-x64> <version> <output folder>
# Requires: native/build-sqlite-macos.sh run before, Xcode command-line tools (sips, iconutil, codesign, ditto).
set -e
rid=$1; version=$2; out=$3
root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
app="$work/PST Browser.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out"

# self-contained application (not a single file: the bundle holds the native libraries next to the executable)
dotnet publish "$root/src/PstBrowser.Desktop/PstBrowser.Desktop.csproj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:DebugType=none -o "$app/Contents/MacOS"
sed "s/@VERSION@/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"

# icon: every size of the macOS icon set from the 256 px image
iconset="$work/PstBrowser.iconset"
mkdir -p "$iconset"
for s in 16 32 64 128 256; do
  sips -z $s $s "$root/packaging/pstbrowser.png" --out "$iconset/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2)); [ $d -le 256 ] && sips -z $d $d "$root/packaging/pstbrowser.png" --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/PstBrowser.icns"

# ad hoc signature (no Apple developer account): every library, then the bundle
codesign --force --deep --sign - --timestamp=none "$app"
codesign --verify --deep --strict --verbose=2 "$app"

name="PstBrowser-macos-${rid#osx-}.zip"
ditto -c -k --keepParent "$app" "$out/$name"
echo "$out/$name"
