#!/usr/bin/env bash
# package-appimage.sh <rid> <version>
# Needs appimagetool (set APPIMAGETOOL=/path, else downloaded into artifacts/tools/).
# Runs appimagetool with --appimage-extract-and-run so FUSE is not required to build.
set -euo pipefail
RID="$1"; VER="$2"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INST="$(cd "$HERE/.." && pwd)"; ROOT="$(cd "$INST/.." && pwd)"
ART="$ROOT/artifacts"; PUB="$ART/publish/$RID"
case "$RID" in linux-x64) A=x86_64;; linux-arm64) A=aarch64;; *) exit 2;; esac

TOOL="${APPIMAGETOOL:-$ART/tools/appimagetool-$A.AppImage}"
if [ ! -x "$TOOL" ]; then
  mkdir -p "$ART/tools"
  URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$A.AppImage"
  echo "-- downloading appimagetool ($URL)"
  curl -fsSL --retry 2 -o "$TOOL" "$URL" || { echo "-- cannot download appimagetool; AppImage skipped"; rm -f "$TOOL"; exit 1; }
  chmod +x "$TOOL"
fi

AD="$ART/stage/AppDir-$RID/DDT4All.AppDir"
rm -rf "$ART/stage/AppDir-$RID"; mkdir -p "$AD/usr/lib/ddt4all" "$AD/usr/bin"
cp -a "$PUB/." "$AD/usr/lib/ddt4all/"
chmod 755 "$AD/usr/lib/ddt4all/Ddt4All.App"
cp "$HERE/ddt4all.desktop" "$AD/ddt4all.desktop"
cp "$INST/assets/ddt4all-256.png" "$AD/ddt4all.png"
mkdir -p "$AD/usr/share/icons/hicolor/256x256/apps"; cp "$INST/assets/ddt4all-256.png" "$AD/usr/share/icons/hicolor/256x256/apps/ddt4all.png"
cat > "$AD/AppRun" <<'L'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/ddt4all/Ddt4All.App" "$@"
L
chmod 755 "$AD/AppRun"
ln -s ../lib/ddt4all/Ddt4All.App "$AD/usr/bin/ddt4all"

OUT="$ART/DDT4All-$VER-$A.AppImage"
ARCH=$A "$TOOL" --appimage-extract-and-run --no-appstream "$AD" "$OUT"
echo "-- wrote $(basename "$OUT")"
