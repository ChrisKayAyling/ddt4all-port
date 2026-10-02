#!/usr/bin/env bash
# make-app.sh <osx-rid> <version> -> artifacts/DDT4All-<ver>-<rid>.app.zip (+ stage/<rid>/DDT4All.app)
# Optional signing (macOS only), driven by env vars:
#   CODESIGN_IDENTITY="Developer ID Application: Name (TEAMID)"   -> codesign --options runtime
#   (the .dmg script additionally notarizes if NOTARY_PROFILE is set)
set -euo pipefail
RID="$1"; VER="$2"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; INST="$(cd "$HERE/.." && pwd)"; ROOT="$(cd "$INST/.." && pwd)"
ART="$ROOT/artifacts"; APP="$ART/stage/app-$RID/DDT4All.app"
rm -rf "$ART/stage/app-$RID"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -a "$ART/publish/$RID/." "$APP/Contents/MacOS/"
chmod 755 "$APP/Contents/MacOS/Ddt4All.App"
cp "$INST/assets/ddt4all.icns" "$APP/Contents/Resources/ddt4all.icns"
sed "s/@VERSION@/$VER/g" "$HERE/Info.plist.in" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

if [ -n "${CODESIGN_IDENTITY:-}" ]; then
  if [ "$(uname -s)" != Darwin ]; then echo "CODESIGN_IDENTITY set but not on macOS; skipping signing" >&2
  else
    echo "-- codesign ($CODESIGN_IDENTITY)"
    # sign nested native code first, then the bundle
    find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -name '*.so' \) -print0 |
      xargs -0 -n1 codesign --force --timestamp --options runtime -s "$CODESIGN_IDENTITY" 
    codesign --force --timestamp --options runtime --entitlements "$HERE/entitlements.plist" \
      -s "$CODESIGN_IDENTITY" "$APP/Contents/MacOS/Ddt4All.App"
    codesign --force --timestamp --options runtime --entitlements "$HERE/entitlements.plist" \
      -s "$CODESIGN_IDENTITY" "$APP"
    codesign --verify --deep --strict "$APP"
  fi
elif [ "$(uname -s)" = Darwin ]; then
  # No Developer ID: ad-hoc sign the whole bundle. Without a sealed signature a downloaded (quarantined)
  # app is reported as "damaged" by Gatekeeper; ad-hoc signed it only needs right-click > Open once.
  echo "-- codesign (ad-hoc)"
  find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -name '*.so' \) -print0 | xargs -0 -n1 codesign --force -s -
  codesign --force -s - "$APP/Contents/MacOS/Ddt4All.App"
  codesign --force --deep -s - "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"
fi

OUT="$ART/DDT4All-$VER-$RID.app.zip"; rm -f "$OUT"
if command -v ditto >/dev/null; then ditto -c -k --keepParent "$APP" "$OUT"
else (cd "$(dirname "$APP")" && python3 - "$OUT" <<'PY'
import sys, os, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, _, fs in os.walk("DDT4All.app"):
        for f in fs:
            p = os.path.join(d, f); zi = zipfile.ZipInfo.from_file(p, p)
            zi.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(zi, open(p, "rb").read())   # preserves unix mode bits (exec)
PY
); fi
echo "-- wrote $(basename "$OUT")"
