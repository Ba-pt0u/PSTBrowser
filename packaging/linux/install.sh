#!/bin/sh
# Installs PST Browser for the current user only (no administrator rights): ~/.local/opt/pstbrowser,
# commands pstbrowser and pstbrowser-cli in ~/.local/bin, entry in the applications menu.
# Uninstall: ./install.sh --remove
set -e
here="$(cd "$(dirname "$0")" && pwd)"
dest="$HOME/.local/opt/pstbrowser"
bin="$HOME/.local/bin"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
if [ "$1" = "--remove" ]; then
  rm -rf "$dest" "$bin/pstbrowser" "$bin/pstbrowser-cli" "$apps/pstbrowser.desktop" "$icons/pstbrowser.png"
  echo "PST Browser a été désinstallé (les dossiers d'affaire ne sont pas touchés)."
  exit 0
fi
mkdir -p "$dest" "$bin" "$apps" "$icons"
rm -rf "$dest"/*
cp -R "$here/app" "$here/cli" "$dest/"
ln -sf "$dest/app/PstBrowser" "$bin/pstbrowser"
ln -sf "$dest/cli/pstbrowser-cli" "$bin/pstbrowser-cli"
cp "$here/pstbrowser.png" "$icons/pstbrowser.png"
sed "s#^Exec=pstbrowser#Exec=$dest/app/PstBrowser#" "$here/pstbrowser.desktop" > "$apps/pstbrowser.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" >/dev/null 2>&1 || true
echo "PST Browser est installé dans $dest (menu des applications, commande pstbrowser)."
if ! ldconfig -p 2>/dev/null | grep -q libwebkit2gtk-4.1; then
  echo "Attention : le composant WebKitGTK (libwebkit2gtk-4.1) est nécessaire pour afficher les messages."
  echo "  Debian / Ubuntu : sudo apt install libwebkit2gtk-4.1-0     Fedora / RHEL : sudo dnf install webkit2gtk4.1"
fi
