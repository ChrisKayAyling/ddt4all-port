#!/usr/bin/env bash
# DDT4All.NET packaging driver (Linux / macOS hosts).
#
#   installer/build.sh                      # host platform, all package formats possible here
#   installer/build.sh -r linux-x64 -r win-x64
#   installer/build.sh --all                # every RID (cross-publish; see docs/BUILDING.md)
#   installer/build.sh --publish-only -r linux-x64
#
# Options:
#   -r, --rid RID        linux-x64|linux-arm64|win-x64|win-arm64|osx-x64|osx-arm64 (repeatable)
#       --all            all six RIDs
#       --version V      override version (default: Version in Directory.Build.props; leading v stripped)
#       --publish-only   only run dotnet publish (artifacts/publish/<rid>/)
#       --no-single-file publish as a folder instead of a single-file bundle
#                        (default: single-file everywhere except osx-*, which uses a folder
#                        layout inside the .app so it can be codesigned cleanly)
#       --trim           enable IL trimming (OFF by default; Avalonia is not trim-safe without testing)
#   -h, --help
#
# Outputs land in artifacts/ . Env: DOTNET (dotnet executable), APPIMAGETOOL (path), SKIP_APPIMAGE=1.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
ART="$ROOT/artifacts"
PROJ="$ROOT/src/Ddt4All.App/Ddt4All.App.csproj"
DOTNET="${DOTNET:-dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -x "$HOME/.dotnet/dotnet" ] && ! command -v "$DOTNET" >/dev/null && export PATH="$HOME/.dotnet:$PATH"

ALL_RIDS=(linux-x64 linux-arm64 win-x64 win-arm64 osx-x64 osx-arm64)
RIDS=(); VERSION="${VERSION:-}"; PUBLISH_ONLY=0; SINGLE=auto; TRIM=0

while [ $# -gt 0 ]; do
  case "$1" in
    -r|--rid) RIDS+=("$2"); shift 2;;
    --all) RIDS=("${ALL_RIDS[@]}"); shift;;
    --version) VERSION="$2"; shift 2;;
    --publish-only) PUBLISH_ONLY=1; shift;;
    --no-single-file) SINGLE=0; shift;;
    --single-file) SINGLE=1; shift;;
    --trim) TRIM=1; shift;;
    -h|--help) sed -n '2,20p' "$0"; exit 0;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done

if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
fi
VERSION="${VERSION#v}"; VERSION="${VERSION:-0.0.0}"

if [ ${#RIDS[@]} -eq 0 ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) RIDS=(linux-x64);;
    Linux-aarch64|Linux-arm64) RIDS=(linux-arm64);;
    Darwin-arm64) RIDS=(osx-arm64);;
    Darwin-x86_64) RIDS=(osx-x64);;
    *) echo "unsupported host; pass --rid" >&2; exit 2;;
  esac
fi

mkdir -p "$ART"
echo "== DDT4All.NET $VERSION  rids: ${RIDS[*]}"

publish() {
  local rid="$1" out="$ART/publish/$1"
  rm -rf "$out"
  local args=(publish "$PROJ" -c Release -r "$rid" --self-contained
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false
    -p:SatelliteResourceLanguages=en -p:Version="$VERSION" -p:UseAppHost=true
    -p:PublishTrimmed=$([ $TRIM = 1 ] && echo true || echo false)
    -o "$out")
  local single="$SINGLE"
  if [ "$single" = auto ]; then case "$rid" in osx-*) single=0;; *) single=1;; esac; fi
  if [ "$single" = 1 ]; then
    args+=(-p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
           -p:IncludeNativeLibrariesForSelfExtract=true)
  fi
  echo "-- publish $rid"
  "$DOTNET" "${args[@]}"
  find "$out" -name '*.pdb' -delete   # native-lib debug symbols (e.g. libSkiaSharp.pdb, 84 MB) are not shipped
}

zip_dir() { # zip_dir <dir> <zipfile>  (python: no zip(1) dependency)
  python3 - "$1" "$2" <<'PY'
import sys, os, zipfile
src, dst = sys.argv[1], sys.argv[2]
base = os.path.dirname(src.rstrip("/"))
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, _, fs in os.walk(src):
        for f in sorted(fs):
            p = os.path.join(d, f)
            zi = zipfile.ZipInfo.from_file(p, os.path.relpath(p, base))
            zi.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(zi, open(p, "rb").read())
PY
}

for rid in "${RIDS[@]}"; do
  publish "$rid"
  [ $PUBLISH_ONLY = 1 ] && continue
  case "$rid" in
    linux-*)
      "$HERE/linux/package-linux.sh" "$rid" "$VERSION" ;;
    win-*)
      stage="$ART/stage/ddt4all-$VERSION-$rid-portable/DDT4All"
      rm -rf "$ART/stage/ddt4all-$VERSION-$rid-portable"; mkdir -p "$stage"
      cp -a "$ART/publish/$rid/." "$stage/"
      cp "$HERE/assets/ddt4all.ico" "$stage/"
      zip_dir "$stage" "$ART/ddt4all-$VERSION-$rid-portable.zip"
      echo "-- wrote ddt4all-$VERSION-$rid-portable.zip"
      if command -v iscc >/dev/null 2>&1 || command -v iscc.exe >/dev/null 2>&1; then
        "$HERE/windows/build-setup.sh" "$rid" "$VERSION"
      else
        echo "-- Inno Setup (iscc) not found: skipping Setup.exe (built on Windows / CI via installer/build.ps1)"
      fi ;;
    osx-*)
      "$HERE/macos/make-app.sh" "$rid" "$VERSION"
      if [ "$(uname -s)" = Darwin ]; then
        "$HERE/macos/make-dmg.sh" "$rid" "$VERSION"
      else
        echo "-- not on macOS: .dmg skipped (hdiutil required); .app zip produced"
      fi ;;
  esac
done

echo "== artifacts:"; ls -la "$ART" | grep -v -E ' (publish|stage)$' || true
