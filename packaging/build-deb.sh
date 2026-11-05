#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: packaging/build-deb.sh VERSION}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]]; then
  printf 'Invalid semantic version: %s\n' "$version" >&2
  exit 2
fi
repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output="$repository/artifacts/packages"
staging="$(mktemp -d)"
trap 'rm -rf -- "$staging"' EXIT
mkdir -p "$output" "$staging/opt/fritz-tv" "$staging/DEBIAN" "$staging/usr/share/applications" "$staging/usr/share/icons/hicolor/32x32/apps" "$staging/usr/share/doc/fritz-tv"
dotnet publish "$repository/src/FritzTV.Desktop" -c Release -r linux-x64 --self-contained true --no-restore -p:Version="$version" -o "$staging/opt/fritz-tv"
cat > "$staging/DEBIAN/control" <<CONTROL
Package: fritz-tv
Version: $version
Section: video
Priority: optional
Architecture: amd64
Maintainer: FRITZ TV contributors <noreply@github.com>
Depends: libvlc5 (>= 3.0.20), vlc-plugin-base, vlc-plugin-video-output, libx11-6, libice6, libsm6, libfontconfig1, mkvtoolnix (>= 96.0)
Description: TV and radio player for FRITZ!Box DVB-C SAT>IP
 Native desktop player, broadcast programme guide and recording tools.
CONTROL
install -m 644 "$repository/packaging/fritz-tv.desktop" "$staging/usr/share/applications/fritz-tv.desktop"
install -m 644 "$repository/src/FritzTV.Desktop/Assets/fritz-tv.png" "$staging/usr/share/icons/hicolor/32x32/apps/fritz-tv.png"
install -m 644 "$repository/LICENSE" "$staging/usr/share/doc/fritz-tv/copyright"
chmod 755 "$staging/opt/fritz-tv/FritzTV.Desktop"
dpkg-deb --root-owner-group --build "$staging" "$output/fritz-tv_${version}_amd64.deb"
printf 'Application: FRITZ TV\nVersion: %s\nLicence: GPL-3.0-or-later\nNative dependencies: system LibVLC and MKVToolNix; see Debian package dependencies.\n' "$version" > "$output/linux-dependencies.txt"
