<#
.SYNOPSIS
  Builds the TC001 Max Thermal Webcam (media source COM DLL, TC001MaxWebcam.exe and the tray icon
  TC001MaxWebcamTray.exe) after running its self-test.

.DESCRIPTION
  Needs the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10). No admin rights; changes
  nothing on the system. NuGet restores the DirectNCore package (MIT, by Simon Mourier) on the
  first build. First runs tests\TC001MaxWebcam.Tests against tests\fixtures (no hardware, no
  camera; -SkipTests skips it) and stops if a test fails. Output: bin\publish\
  (framework-dependent, win-x64), and from it the installer package
  TC001MaxWebcam-<version>-x64.msi at the top of the repository (installer\, WiX Toolset from NuGet,
  built in bin\installer; an older .msi at the top is replaced; git ignores it; -NoInstaller skips
  it). Double-click the .msi to install or to upgrade (one UAC prompt); once 1.2.0 or later is
  installed, Start menu > Update TC001 Max Thermal Webcam does the same. install.ps1 is the
  scripted alternative. The test project is not published.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$NoInstaller
)
$ErrorActionPreference = "Stop"

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$sdks = & $dotnet --list-sdks 2>$null
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Write-Error ".NET 10 SDK not found ('dotnet --list-sdks' shows: $sdks). Install it with: winget install Microsoft.DotNet.SDK.10"
    exit 1
}

$root = $PSScriptRoot
$out = Join-Path $root "bin\publish"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# Self-test of the pure code (protocol on real camera bytes, settings, palettes, picture, reader
# state machine with a fake USB link). No hardware; a failure stops the build.
if (-not $SkipTests) {
    $fixtures = Join-Path $root "tests\fixtures"
    Write-Host "dotnet run TC001MaxWebcam.Tests (fixtures: $fixtures)"
    # nothing but program arguments after "--": dotnet run forwards unknown options to the program
    & $dotnet run --project (Join-Path $root "tests\TC001MaxWebcam.Tests\TC001MaxWebcam.Tests.csproj") -c $Configuration -- $fixtures
    if ($LASTEXITCODE -ne 0) { Write-Error "TC001MaxWebcam.Tests failed (exit $LASTEXITCODE)"; exit 1 }
}

foreach ($project in @("src\TC001MaxWebcam.Source\TC001MaxWebcam.Source.csproj", "src\TC001MaxWebcam\TC001MaxWebcam.csproj", "src\TC001MaxWebcam.Tray\TC001MaxWebcam.Tray.csproj")) {
    Write-Host "dotnet publish $project -> $out"
    & $dotnet publish (Join-Path $root $project) -c $Configuration -r win-x64 --self-contained false -o $out -nologo
    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed for $project"; exit 1 }
}
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.txt") $out -Force

$comhost = Join-Path $out "TC001MaxWebcamSource.comhost.dll"
if (-not (Test-Path $comhost)) { Write-Error "missing $comhost after publish"; exit 1 }
Write-Host ""
Write-Host "Built: $out"
Get-ChildItem $out -File | ForEach-Object { Write-Host ("  {0,-48} {1,10:N0} bytes" -f $_.Name, $_.Length) }

if ($NoInstaller) {
    Write-Host ""
    Write-Host "Next (elevated PowerShell): powershell -ExecutionPolicy Bypass -File `"$root\install.ps1`""
    exit 0
}
# The .msi (installer\Package.wxs). --disable-build-servers: no MSBuild node lingers after the build.
# Built in bin\installer, then moved to the top of the repository (the only .msi there).
$msiOut = Join-Path $root "bin\installer"
Get-ChildItem $msiOut -Filter "TC001MaxWebcam-*.msi" -ErrorAction SilentlyContinue | Remove-Item -Force
Write-Host ""
Write-Host "dotnet build installer\TC001MaxWebcam.Installer.wixproj -> $msiOut"
& $dotnet build (Join-Path $root "installer\TC001MaxWebcam.Installer.wixproj") -c $Configuration --output $msiOut -nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { Write-Error "building the installer failed"; exit 1 }
$built = Get-ChildItem $msiOut -Filter "TC001MaxWebcam-*.msi" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { Write-Error "no .msi in $msiOut after the build"; exit 1 }
Get-ChildItem $root -Filter "TC001MaxWebcam-*.msi" | Where-Object Name -ne $built.Name | ForEach-Object {
    Write-Host "Removing the older $($_.Name)"
    Remove-Item $_.FullName -Force
}
$msi = Move-Item $built.FullName (Join-Path $root $built.Name) -Force -PassThru
Write-Host ""
Write-Host "Installer: $($msi.FullName) ($('{0:N0}' -f $msi.Length) bytes)"
Write-Host "Next: double-click it to install or upgrade (Windows asks for administrator rights once),"
Write-Host "or, with 1.2.0 or later installed: Start menu > Update TC001 Max Thermal Webcam."
Write-Host "Remove it in Settings > Apps > Installed apps. Scripted alternative: install.ps1 (elevated)."
