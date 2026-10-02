<#
.SYNOPSIS  DDT4All.NET packaging driver for Windows (PowerShell 5.1+ / pwsh 7).
.EXAMPLE   .\installer\build.ps1                       # win-x64: publish + portable zip + Setup.exe (if iscc found)
.EXAMPLE   .\installer\build.ps1 -Rid win-x64,win-arm64 -Version 1.2.0
.EXAMPLE   .\installer\build.ps1 -PublishOnly -Rid linux-x64   # cross-publish any RID
#>
param(
  [string[]]$Rid = @('win-x64'),
  [string]$Version = '',
  [switch]$PublishOnly,
  [switch]$NoSingleFile,
  [switch]$Trim
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Here
$Art  = Join-Path $Root 'artifacts'
$Proj = Join-Path $Root 'src\Ddt4All.App\Ddt4All.App.csproj'

if (-not $Version) {
  $m = Select-String -Path (Join-Path $Root 'Directory.Build.props') -Pattern '<Version>(.*)</Version>' | Select-Object -First 1
  $Version = if ($m) { $m.Matches[0].Groups[1].Value } else { '0.0.0' }
}
$Version = $Version.TrimStart('v')
New-Item -ItemType Directory -Force -Path $Art | Out-Null
Write-Host "== DDT4All.NET $Version  rids: $($Rid -join ', ')"

foreach ($r in $Rid) {
  $out = Join-Path $Art "publish\$r"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  $a = @('publish', $Proj, '-c', 'Release', '-r', $r, '--self-contained',
         '-p:PublishReadyToRun=true', '-p:DebugType=None', '-p:DebugSymbols=false',
         '-p:SatelliteResourceLanguages=en', "-p:Version=$Version", '-p:UseAppHost=true',
         "-p:PublishTrimmed=$($Trim.IsPresent.ToString().ToLower())", '-o', $out)
  if (-not $NoSingleFile) {
    $a += @('-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
  }
  Write-Host "-- publish $r"
  & dotnet @a
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $r" }
  Get-ChildItem $out -Filter *.pdb -Recurse | Remove-Item -Force   # drop native-lib symbols (libSkiaSharp.pdb is ~84 MB)
  if ($PublishOnly) { continue }

  if ($r -like 'win-*') {
    $stageRoot = Join-Path $Art "stage\ddt4all-$Version-$r-portable"
    $stage = Join-Path $stageRoot 'DDT4All'
    if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -Recurse -Force "$out\*" $stage
    Copy-Item (Join-Path $Here 'assets\ddt4all.ico') $stage
    $zip = Join-Path $Art "ddt4all-$Version-$r-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "-- wrote $zip"

    $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
      foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
      }
    }
    if ($iscc) {
      & $iscc "/DAppVersion=$Version" "/DArch=$($r.Substring(4))" "/DSourceDir=$out" "/DOutDir=$Art" (Join-Path $Here 'windows\ddt4all.iss')
      if ($LASTEXITCODE -ne 0) { throw 'iscc failed' }
    } else {
      Write-Warning 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup); Setup.exe skipped.'
    }
  } else {
    Write-Warning "$r was published to $out; packaging for non-Windows targets needs installer/build.sh on Linux/macOS."
  }
}
