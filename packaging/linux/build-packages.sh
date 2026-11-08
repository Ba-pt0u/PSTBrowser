#!/bin/bash
# Builds the Linux packages of PST Browser (application + command-line tool), x86-64:
#   pstbrowser-linux-amd64.deb, pstbrowser-linux-x86_64.rpm, pstbrowser-linux-x64.tar.gz (per-user install, no root)
# The web engine (WebKitGTK) comes from the distribution: the packages depend on it.
# Usage: packaging/linux/build-packages.sh <version> <output folder>
# Requires: native/build-sqlite-linux.sh run before, dpkg-deb, rpmbuild.
set -e
version=$1; out=$2
root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
mkdir -p "$out"
pub() { dotnet publish "$root/src/$1/$1.csproj" -c Release -r linux-x64 --self-contained true -p:Version="$version" -p:DebugType=none -o "$2"; }
pub PstBrowser.Desktop "$work/opt/pstbrowser/app"
pub PstBrowser.Cli "$work/opt/pstbrowser/cli"

# ---- .deb and .rpm: /opt/pstbrowser, commands in /usr/bin, applications menu entry
stage="$work/stage"
mkdir -p "$stage/opt" "$stage/usr/bin" "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/256x256/apps"
cp -R "$work/opt/pstbrowser" "$stage/opt/"
ln -s /opt/pstbrowser/app/PstBrowser "$stage/usr/bin/pstbrowser"
ln -s /opt/pstbrowser/cli/pstbrowser-cli "$stage/usr/bin/pstbrowser-cli"
cp "$root/packaging/linux/pstbrowser.desktop" "$stage/usr/share/applications/"
cp "$root/packaging/pstbrowser.png" "$stage/usr/share/icons/hicolor/256x256/apps/pstbrowser.png"

deb="$work/deb"
cp -a "$stage" "$deb"
mkdir -p "$deb/DEBIAN"
cat > "$deb/DEBIAN/control" <<CTL
Package: pstbrowser
Version: $version
Architecture: amd64
Maintainer: PST Browser <https://github.com/Ba-pt0u/PSTBrowser>
Depends: libwebkit2gtk-4.1-0, libgtk-3-0 | libgtk-3-0t64, libfontconfig1, libicu74 | libicu72 | libicu70 | libicu67 | libicu76 | libicu77
Section: mail
Priority: optional
Homepage: https://github.com/Ba-pt0u/PSTBrowser
Description: Visualiseur hors ligne de fichiers PST, OST et MSG
 Consultation d'exports eDiscovery sans Outlook et sans connexion réseau :
 recherche plein texte, lecture sécurisée, aide à l'enquête, exports.
CTL
dpkg-deb --root-owner-group --build "$deb" "$out/pstbrowser-linux-amd64.deb"

rpmtop="$work/rpm"
mkdir -p "$rpmtop"/{BUILD,RPMS,SOURCES,SPECS,SRPMS,BUILDROOT}
cat > "$rpmtop/SPECS/pstbrowser.spec" <<SPEC
Name: pstbrowser
Version: $version
Release: 1
Summary: Visualiseur hors ligne de fichiers PST, OST et MSG
License: MIT
URL: https://github.com/Ba-pt0u/PSTBrowser
Requires: webkit2gtk4.1, gtk3, fontconfig, libicu
AutoReqProv: no
%define __strip /bin/true
%define _build_id_links none

%description
Consultation d'exports eDiscovery sans Outlook et sans connexion réseau :
recherche plein texte, lecture sécurisée, aide à l'enquête, exports.

%install
cp -a $stage/. %{buildroot}/

%files
/opt/pstbrowser
/usr/bin/pstbrowser
/usr/bin/pstbrowser-cli
/usr/share/applications/pstbrowser.desktop
/usr/share/icons/hicolor/256x256/apps/pstbrowser.png
SPEC
rpmbuild --define "_topdir $rpmtop" --target x86_64 -bb "$rpmtop/SPECS/pstbrowser.spec" >/dev/null
cp "$rpmtop"/RPMS/x86_64/pstbrowser-*.rpm "$out/pstbrowser-linux-x86_64.rpm"

# ---- portable archive: install.sh installs for the current user
tgz="$work/pstbrowser"
mkdir -p "$tgz"
cp -R "$work/opt/pstbrowser/app" "$work/opt/pstbrowser/cli" "$tgz/"
cp "$root/packaging/linux/install.sh" "$root/packaging/linux/pstbrowser.desktop" "$root/packaging/pstbrowser.png" "$tgz/"
tar -czf "$out/pstbrowser-linux-x64.tar.gz" -C "$work" pstbrowser
ls -la "$out"
