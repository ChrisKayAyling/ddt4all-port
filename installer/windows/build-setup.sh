#!/usr/bin/env bash
# build-setup.sh <rid> <version>: compile the Inno script when iscc (native or via wine) is on PATH.
set -euo pipefail
RID="$1"; VER="$2"; ARCH="${RID#win-}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
ISCC="$(command -v iscc || command -v iscc.exe)"
"$ISCC" "/DAppVersion=$VER" "/DArch=$ARCH" "/DSourceDir=$ROOT/artifacts/publish/$RID" "/DOutDir=$ROOT/artifacts" "$HERE/ddt4all.iss"
