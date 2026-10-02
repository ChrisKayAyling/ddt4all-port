#!/usr/bin/env bash
# make-dmg.sh <osx-rid> <version> -> artifacts/DDT4All-<ver>-<rid>.dmg   (macOS only: hdiutil)
# Optional env: CODESIGN_IDENTITY (sign dmg), NOTARY_PROFILE (xcrun notarytool keychain profile -> notarize+staple)
set -euo pipefail
trap 'echo "::error::make-dmg.sh failed at line $LINENO"' ERR
[ "$(uname -s)" = Darwin ] || { echo "make-dmg.sh requires macOS (hdiutil)" >&2; exit 1; }
RID="$1"; VER="$2"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
ART="$ROOT/artifacts"; APP="$ART/stage/app-$RID/DDT4All.app"; OUT="$ART/DDT4All-$VER-$RID.dmg"
D="$ART/stage/dmg-$RID"; rm -rf "$D" "$OUT"; mkdir -p "$D"
cp -a "$APP" "$D/"; ln -s /Applications "$D/Applications"
hdiutil create -volname "DDT4All $VER" -srcfolder "$D" -ov -format UDZO -fs HFS+ "$OUT"
if [ -n "${CODESIGN_IDENTITY:-}" ]; then codesign --force --timestamp -s "$CODESIGN_IDENTITY" "$OUT"; fi
if [ -n "${NOTARY_PROFILE:-}" ]; then
  xcrun notarytool submit "$OUT" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$OUT"
fi
echo "-- wrote $(basename "$OUT")"
