# Building and packaging

Prerequisites: .NET 9 SDK (and python3 for the packaging helpers). Everything else is optional per target.

```bash
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1   # if using a user-local SDK
dotnet build Ddt4All.sln -c Release
dotnet test  Ddt4All.sln -c Release
```

## One command per platform

| Host | Command | Produces (in `artifacts/`) |
|---|---|---|
| Linux | `installer/build.sh -r linux-x64` | `ddt4all-<v>-linux-x64.tar.gz`, `ddt4all_<v>_amd64.deb`, `DDT4All-<v>-x86_64.AppImage` |
| Linux | `installer/build.sh -r linux-arm64` | same for arm64 (`.deb` arm64, AppImage aarch64) |
| Windows | `.\installer\build.ps1` | `ddt4all-<v>-win-x64-portable.zip`, `DDT4All-<v>-win-x64-Setup.exe` (needs Inno Setup 6) |
| macOS | `installer/build.sh -r osx-arm64` | `DDT4All-<v>-osx-arm64.app.zip`, `.dmg` |
| anywhere | `installer/build.sh --all` | cross-publishes all six RIDs; packages whatever the host can build |

Options: `--version X` (default `<Version>` from `Directory.Build.props`; a leading `v` is stripped),
`--publish-only` (just `artifacts/publish/<rid>/`), `--no-single-file`, `--trim`.

Publish settings: self-contained, ReadyToRun, single-file with compression (native libs self-extract on first
launch to `~/.net`) on Linux and Windows. macOS uses a folder layout inside the `.app` so it signs cleanly.
**Trimming is off by default**: Avalonia, compiled bindings and `System.Text.Json` source-gen need to be
verified under the trimmer before enabling `--trim`; ReadyToRun + compression gives most of the startup win.

## Linux details

* `.deb`: built with `fakeroot dpkg-deb -Zxz`; app in `/usr/lib/ddt4all`, symlink `/usr/bin/ddt4all`, `.desktop`
  file, hicolor icons (16-512 + svg); `postinst` refreshes caches and prints the `dialout` group note.
  Check with `dpkg-deb --info/--contents artifacts/*.deb`.
* AppImage: `installer/linux/package-appimage.sh` downloads `appimagetool` into `artifacts/tools/` (or set
  `APPIMAGETOOL=/path`) and runs it with `--appimage-extract-and-run` (no FUSE needed). `SKIP_APPIMAGE=1` skips it.
  Note: AppImages built on one architecture package that architecture only (use an arm64 runner or QEMU for arm64
  *runtime* testing; cross-publishing is fine).

## Windows details

`installer/windows/ddt4all.iss` (Inno Setup 6): start-menu entry, optional desktop icon (unchecked by default),
uninstaller, no file associations. Compile manually:
`iscc /DAppVersion=1.0.0 /DArch=x64 /DSourceDir=..\..\artifacts\publish\win-x64 installer\windows\ddt4all.iss`.
`winget install JRSoftware.InnoSetup` provides `ISCC.exe`; `build.ps1` finds it in the default locations.
Code signing is not wired in; add a `SignTool=` entry to `[Setup]` once a certificate exists.

## macOS details

`installer/macos/make-app.sh` assembles `DDT4All.app` (Info.plist from `Info.plist.in`, `.icns`).
`make-dmg.sh` uses `hdiutil` (macOS only). Optional, env-driven:

| Variable | Effect |
|---|---|
| `CODESIGN_IDENTITY` | `codesign --options runtime` with `macos/entitlements.plist` (JIT, unsigned memory, USB, Bluetooth) |
| `NOTARY_PROFILE` | after the dmg is built: `xcrun notarytool submit --wait` + `stapler staple` (profile created with `xcrun notarytool store-credentials`) |

## Icons

`python3 installer/make_icons.py` regenerates `installer/assets/` (PNG 16-1024, `.ico`, `.icns`, `.svg`) using only
numpy + the standard library. The generated files are committed; rerun only when changing the design.

## CI

* `.github/workflows/ci.yml`: build + test on ubuntu/windows/macos, plus a Linux package smoke job.
* `.github/workflows/release.yml`: on tag `v*`, builds Linux (tar.gz/deb/AppImage, x64+arm64), Windows (zip + Inno
  Setup on `windows-latest`, x64+arm64), macOS (`.app.zip` + `.dmg`, x64+arm64), then creates a GitHub release with all
  files and `SHA256SUMS.txt`. Optional secrets: `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`, `MACOS_CODESIGN_IDENTITY`,
  `NOTARY_APPLE_ID`, `NOTARY_TEAM_ID`, `NOTARY_PASSWORD`. Manual runs (`workflow_dispatch`) upload artifacts only.
