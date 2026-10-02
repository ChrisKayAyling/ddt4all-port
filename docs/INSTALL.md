# Installing DDT4All.NET

DDT4All.NET ships **self-contained** (the .NET runtime is bundled), so nothing else needs installing.
Pick the package for your platform from the GitHub Releases page.

| Platform | Package | Notes |
|---|---|---|
| Debian / Ubuntu / Mint / Raspberry Pi OS | `ddt4all_<ver>_amd64.deb`, `ddt4all_<ver>_arm64.deb` | menu entry, icon, `ddt4all` command |
| Any Linux | `DDT4All-<ver>-x86_64.AppImage` (`aarch64` also built) | no install, no root |
| Any Linux | `ddt4all-<ver>-linux-<arch>.tar.gz` | unpack anywhere, run `./ddt4all` |
| Windows 10/11 | `DDT4All-<ver>-win-x64-Setup.exe` (or `win-arm64`) | start-menu entry, optional desktop icon, uninstaller |
| Windows | `ddt4all-<ver>-win-x64-portable.zip` | unzip and run `Ddt4All.App.exe` |
| macOS 11+ | `DDT4All-<ver>-osx-arm64.dmg` (Apple silicon) / `osx-x64.dmg` (Intel) | drag to Applications |

## Linux

```bash
sudo apt install ./ddt4all_1.0.0_amd64.deb     # installs to /usr/lib/ddt4all, command: ddt4all
# or
chmod +x DDT4All-1.0.0-x86_64.AppImage && ./DDT4All-1.0.0-x86_64.AppImage
```

Requirements: a desktop session (X11 or XWayland), `libfontconfig1`, ICU (`libicu*`, present on all mainstream
distros). The `.deb` declares these as dependencies.

**Serial / USB adapters.** Opening `/dev/ttyUSB*` / `/dev/ttyACM*` requires membership of the `dialout` group
(`uucp` on Arch/Fedora-like systems):

```bash
sudo usermod -aG dialout "$USER"     # then log out and in again
```

The `.deb` post-install script prints this reminder. If ModemManager grabs your adapter, add a udev rule or
`sudo systemctl disable --now ModemManager`. Uninstall: `sudo apt remove ddt4all`.

## Windows

Run the Setup.exe (admin rights are requested for Program Files; choose "install for me only" in the dialog
if you prefer a per-user install). The installer registers no file associations. Uninstall from
*Settings > Apps*. The portable zip needs no install and stores nothing outside its folder besides the
normal per-user app data.

Windows SmartScreen may warn about unsigned builds ("More info > Run anyway") until the release pipeline
is given a code-signing certificate.

USB-serial adapters need their vendor driver (FTDI/CH340/CP210x); Windows Update normally installs it.

## macOS

Open the `.dmg`, drag **DDT4All** to *Applications*. Unsigned/non-notarized builds are blocked by Gatekeeper:
right-click the app > *Open* once, or `xattr -dr com.apple.quarantine /Applications/DDT4All.app`.
Release builds are signed and notarized when the maintainers configured the signing secrets
(see BUILDING.md).

## First run

Point the app at your ECU database (`ecu.zip`) as described in the in-app first-run dialog / README.
