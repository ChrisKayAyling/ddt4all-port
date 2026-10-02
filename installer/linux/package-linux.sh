#!/usr/bin/env bash
# package-linux.sh <rid> <version>  -> tar.gz, .deb, AppImage in artifacts/
set -euo pipefail
RID="$1"; VER="$2"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INST="$(cd "$HERE/.." && pwd)"; ROOT="$(cd "$INST/.." && pwd)"
ART="$ROOT/artifacts"; PUB="$ART/publish/$RID"
EXE=Ddt4All.App

case "$RID" in
  linux-x64) DEBARCH=amd64; APPARCH=x86_64;;
  linux-arm64) DEBARCH=arm64; APPARCH=aarch64;;
  *) echo "bad rid $RID" >&2; exit 2;;
esac

# ---- tar.gz -----------------------------------------------------------
T="$ART/stage/tar-$RID/ddt4all-$VER-$RID"
rm -rf "$ART/stage/tar-$RID"; mkdir -p "$T"
cp -a "$PUB/." "$T/"
cp "$INST/assets/ddt4all-256.png" "$T/ddt4all.png"
cp "$HERE/ddt4all.desktop" "$T/"
cat > "$T/ddt4all" <<'L'
#!/bin/sh
D="$(dirname "$(readlink -f "$0")")"
exec "$D/Ddt4All.App" "$@"
L
chmod 755 "$T/ddt4all" "$T/$EXE"
tar -C "$ART/stage/tar-$RID" -czf "$ART/ddt4all-$VER-$RID.tar.gz" "ddt4all-$VER-$RID"
echo "-- wrote ddt4all-$VER-$RID.tar.gz"

# ---- .deb -------------------------------------------------------------
if command -v dpkg-deb >/dev/null; then
  P="$ART/stage/deb-$RID/ddt4all"
  rm -rf "$ART/stage/deb-$RID"
  mkdir -p "$P/DEBIAN" "$P/usr/lib/ddt4all" "$P/usr/bin" "$P/usr/share/applications" \
           "$P/usr/share/doc/ddt4all" "$P/usr/share/pixmaps"
  cp -a "$PUB/." "$P/usr/lib/ddt4all/"
  chmod 755 "$P/usr/lib/ddt4all/$EXE"
  ln -s ../lib/ddt4all/$EXE "$P/usr/bin/ddt4all"
  cp "$HERE/ddt4all.desktop" "$P/usr/share/applications/ddt4all.desktop"
  for s in 16 24 32 48 64 128 256 512; do
    d="$P/usr/share/icons/hicolor/${s}x${s}/apps"; mkdir -p "$d"
    cp "$INST/assets/ddt4all-$s.png" "$d/ddt4all.png"
  done
  cp "$INST/assets/ddt4all.svg" "$P/usr/share/pixmaps/ddt4all.svg" 2>/dev/null || true
  mkdir -p "$P/usr/share/icons/hicolor/scalable/apps"; cp "$INST/assets/ddt4all.svg" "$P/usr/share/icons/hicolor/scalable/apps/ddt4all.svg"
  rm -f "$P/usr/share/pixmaps/ddt4all.svg"; rmdir "$P/usr/share/pixmaps"
  cat > "$P/usr/share/doc/ddt4all/copyright" <<C
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: DDT4All.NET

Files: *
Copyright: DDT4All.NET contributors
License: GPL-3+
 See /usr/share/common-licenses/GPL-3 (port of the GPL-licensed ddt4all project).
C
  SIZE=$(du -sk --exclude=DEBIAN "$P" | cut -f1)
  cat > "$P/DEBIAN/control" <<C
Package: ddt4all
Version: $VER
Section: utils
Priority: optional
Architecture: $DEBARCH
Installed-Size: $SIZE
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6, libicu72 | libicu74 | libicu76 | libicu70 | libicu67 | libicu66 | libicu-dev
Recommends: xdg-utils
Maintainer: DDT4All.NET contributors <noreply@example.invalid>
Homepage: https://github.com/cedricp/ddt4all
Description: Diagnostic tool for vehicle ECUs (DDT4All.NET)
 Cross-platform .NET / Avalonia port of DDT4All: browse ECU definitions,
 read and write parameters, run diagnostic sessions through ELM327-family
 and serial adapters. Self-contained; no .NET runtime needed.
C
  cp "$HERE/deb/postinst" "$HERE/deb/postrm" "$P/DEBIAN/"
  chmod 755 "$P/DEBIAN/postinst" "$P/DEBIAN/postrm"
  find "$P" -type d -exec chmod 755 {} +
  fakeroot dpkg-deb --build -Zxz "$P" "$ART/ddt4all_${VER}_${DEBARCH}.deb" >/dev/null
  echo "-- wrote ddt4all_${VER}_${DEBARCH}.deb"
fi

# ---- AppImage ---------------------------------------------------------
if [ -z "${SKIP_APPIMAGE:-}" ]; then
  "$HERE/package-appimage.sh" "$RID" "$VER" || echo "-- AppImage step failed/skipped (see message above)"
fi
