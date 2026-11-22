<#
.SYNOPSIS
  Installs the "TC001 Max Thermal Webcam" virtual camera. Run ELEVATED (Run as administrator).
  The usual way is the installer package instead (TC001MaxWebcam-<version>-x64.msi at the top of the repository,
  built by build.ps1: double-click it, no PowerShell). This script is the scripted equivalent; do not
  mix the two (uninstall one before using the other).

.DESCRIPTION
  Every system change is printed. Safe to run again (it skips what is already in place).
    1. Copies bin\publish\* to C:\Program Files\TC001MaxWebcam\ (a folder the Windows Camera
       Frame Server services can read; a user folder fails with "Access denied").
    2. Creates C:\ProgramData\TC001MaxWebcam\ (settings.json and the camera's webcam.log) with its
       own ACL: SYSTEM and Administrators full control; the owner (-Owner, default: you) Modify,
       to edit settings.json; LOCAL SERVICE (the Frame Server, which runs the camera's TC001 Max
       reader) Read, plus "create files" on the folder itself, and CREATOR OWNER Modify on files -
       so the camera can create and rotate only its own webcam.log and never change settings.json.
       Writes a default settings.json if there is none.
   2b. Creates C:\ProgramData\TC001MaxWebcam\claims\ (camera claims shared with labbench): Users
       and LOCAL SERVICE Read + create files, CREATOR OWNER Modify on files.
    3. Registers the media source COM server in HKLM: regsvr32 /s TC001MaxWebcamSource.comhost.dll
    4. Creates the virtual camera with system lifetime for all users:
       TC001MaxWebcam.exe install  (MFCreateVirtualCamera, MFVirtualCameraLifetime_System)
  Needs Windows 11 and the .NET 10 runtime (x64). Build first with build.ps1 (no admin).
  Undo with uninstall.ps1. The camera itself needs the WinUSB driver (Zadig), see README.md.

.PARAMETER Owner
  The account that will edit settings.json (gets Modify on the data folder).
  Default: the account running this script.

.PARAMETER RestartFrameServer
  Stop the FrameServer and FrameServerMonitor services before copying, so DLLs they still have
  loaded from an earlier install can be replaced. Windows starts them again on demand.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$Owner = "$env:USERDOMAIN\$env:USERNAME",
    [switch]$RestartFrameServer
)
$ErrorActionPreference = "Stop"

function Write-Change([string]$text) { Write-Host "  [change] $text" -ForegroundColor Yellow }
function Write-Same([string]$text) { Write-Host "  [ok]     $text" }

# --- preconditions ---------------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "install.ps1 must run elevated: right-click PowerShell > Run as administrator, then run it again." -ForegroundColor Red
    exit 1
}
$build = [Environment]::OSVersion.Version.Build
if ($build -lt 22000) {
    Write-Host "Virtual cameras need Windows 11 (build 22000+); this is build $build." -ForegroundColor Red
    exit 1
}
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$runtimes = @()
if (Test-Path $dotnet) { $runtimes = & $dotnet --list-runtimes 2>$null }
if (-not ($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' })) {
    Write-Host "The .NET 10 runtime (Microsoft.NETCore.App 10.x, x64) is missing in $env:ProgramFiles\dotnet." -ForegroundColor Red
    Write-Host "Install it (winget install Microsoft.DotNet.Runtime.10, or the .NET 10 SDK) and run this again." -ForegroundColor Red
    exit 1
}
try {
    $ownerSid = (New-Object Security.Principal.NTAccount($Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
} catch {
    Write-Host "Unknown account '$Owner'. Pass -Owner DOMAIN\user (the account that will edit settings.json)." -ForegroundColor Red
    exit 1
}

$src = Join-Path $PSScriptRoot "bin\publish"
$dest = Join-Path $env:ProgramFiles "TC001MaxWebcam"
$comhost = "TC001MaxWebcamSource.comhost.dll"
$exe = Join-Path $dest "TC001MaxWebcam.exe"
$dataDir = Join-Path $env:ProgramData "TC001MaxWebcam"
if (-not (Test-Path (Join-Path $src $comhost))) {
    Write-Host "No build found in $src. Run build.ps1 first (no admin needed)." -ForegroundColor Red
    exit 1
}
Write-Host "Installing the TC001 Max Thermal Webcam (settings owner $Owner, $ownerSid)"

# --- 1. files --------------------------------------------------------------------------------
Write-Host "1. Files -> $dest"
if ($RestartFrameServer) {
    foreach ($svc in @("FrameServerMonitor", "FrameServer")) {
        $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
        if ($s -and $s.Status -eq "Running") {
            Write-Change "Stop-Service $svc (Windows restarts it on demand)"
            Stop-Service -Name $svc -Force
        }
    }
}
if (-not (Test-Path $dest)) {
    Write-Change "create folder $dest"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
}
$failed = @()
foreach ($file in Get-ChildItem $src -File) {
    $target = Join-Path $dest $file.Name
    if ((Test-Path $target) -and ((Get-FileHash $target).Hash -eq (Get-FileHash $file.FullName).Hash)) {
        Write-Same "$($file.Name) unchanged"
        continue
    }
    try {
        Copy-Item $file.FullName $target -Force
        Write-Change "copy $($file.Name)"
    } catch {
        $failed += $file.Name
        Write-Host "  [error]  cannot replace $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
    }
}
if ($failed.Count -gt 0) {
    Write-Host "Files are in use (a camera app or the Frame Server has the old DLL loaded). Close camera apps and run again with -RestartFrameServer." -ForegroundColor Red
    exit 1
}

# --- 2. data folder + ACL --------------------------------------------------------------------
Write-Host "2. Data folder $dataDir"
if (-not (Test-Path $dataDir)) {
    Write-Change "create folder $dataDir"
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
}
# SIDs: SYSTEM S-1-5-18, Administrators S-1-5-32-544, LOCAL SERVICE S-1-5-19 (the Frame Server
# service account), CREATOR OWNER S-1-3-0. No inheritance from C:\ProgramData.
$grants = @(
    "*S-1-5-18:(OI)(CI)F",
    "*S-1-5-32-544:(OI)(CI)F",
    "*${ownerSid}:(OI)(CI)M",
    "*S-1-5-19:(OI)(CI)RX",
    "*S-1-3-0:(OI)(IO)M"
)
Write-Change "icacls `"$dataDir`" /inheritance:r /grant:r $($grants -join ' ')"
& icacls.exe $dataDir /inheritance:r /grant:r @grants | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "icacls failed ($LASTEXITCODE)" -ForegroundColor Red; exit 1 }
# The camera's own log: LOCAL SERVICE may add files to this folder only (WD on a folder = create
# files; no inheritance flags = this folder, not the files in it); CREATOR OWNER above then gives
# it Modify on the files it created (webcam.log, webcam.log.1) and nothing else. A separate
# /grant (not /grant:r) so the RX entry above stays.
Write-Change "icacls `"$dataDir`" /grant *S-1-5-19:(WD)"
& icacls.exe $dataDir /grant "*S-1-5-19:(WD)" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "icacls failed ($LASTEXITCODE)" -ForegroundColor Red; exit 1 }
foreach ($f in Get-ChildItem $dataDir -File) {
    Write-Change "icacls `"$($f.FullName)`" /reset (inherit the folder ACL)"
    & icacls.exe $f.FullName /reset | Out-Null
}
& icacls.exe $dataDir

# The camera claims shared with labbench (src\TC001MaxWebcam.Source\Core\CameraClaims.cs): Users and
# LOCAL SERVICE may read and add files; CREATOR OWNER gives each process Modify on its own claim only.
$claimDir = Join-Path $dataDir "claims"
Write-Host "2b. Claim folder $claimDir (taking turns with labbench)"
if (-not (Test-Path $claimDir)) {
    Write-Change "create folder $claimDir"
    New-Item -ItemType Directory -Force -Path $claimDir | Out-Null
}
$claimGrants = @(
    "*S-1-5-18:(OI)(CI)F",
    "*S-1-5-32-544:(OI)(CI)F",
    "*S-1-5-32-545:(OI)(CI)RX",
    "*S-1-5-19:(OI)(CI)RX",
    "*S-1-3-0:(OI)(IO)M"
)
Write-Change "icacls `"$claimDir`" /inheritance:r /grant:r $($claimGrants -join ' ')"
& icacls.exe $claimDir /inheritance:r /grant:r @claimGrants | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "icacls failed ($LASTEXITCODE)" -ForegroundColor Red; exit 1 }
foreach ($sid in @("*S-1-5-32-545", "*S-1-5-19")) {
    Write-Change "icacls `"$claimDir`" /grant ${sid}:(WD)"
    & icacls.exe $claimDir /grant "${sid}:(WD)" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "icacls failed ($LASTEXITCODE)" -ForegroundColor Red; exit 1 }
}

# --- 3. COM registration ---------------------------------------------------------------------
Write-Host "3. COM registration (HKLM)"
$dll = Join-Path $dest $comhost
Write-Change "regsvr32 /s `"$dll`""
$p = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList "/s", "`"$dll`"" -Wait -PassThru
if ($p.ExitCode -ne 0) {
    Write-Host "regsvr32 failed with exit code $($p.ExitCode). Is the .NET 10 x64 runtime installed?" -ForegroundColor Red
    exit 1
}

# --- 4. virtual camera -----------------------------------------------------------------------
Write-Host "4. Virtual camera (system lifetime, all users)"
Write-Change "`"$exe`" install"
& $exe install
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creating the virtual camera failed (exit $LASTEXITCODE). Fallback without system lifetime: run `"$exe`" run (no admin) while you need the camera." -ForegroundColor Red
    exit 1
}

# --- 5. default settings ---------------------------------------------------------------------
$settings = Join-Path $dataDir "settings.json"
if (Test-Path $settings) {
    Write-Same "kept $settings"
} else {
    Write-Change "write the default $settings"
    & $exe settings reset | Out-Null
}

Write-Host ""
Write-Host "Installed. Status (read-only; the camera is not opened):" -ForegroundColor Green
& $exe status
Write-Host ""
Write-Host "To check it: open the Windows Camera app and switch cameras until it shows"
Write-Host "  'TC001 Max Thermal Webcam Windows Virtual Camera' ('Starting the camera', then the picture in ~4 s)."
Write-Host "Settings: `"$exe`" settings set rotate=180   Log: $dataDir\webcam.log   Remove: uninstall.ps1 (elevated)"
