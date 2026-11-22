<#
.SYNOPSIS
  Removes the "TC001 Max Thermal Webcam" virtual camera. Run ELEVATED (Run as administrator).
  For an install made with the .msi, use Settings > Apps > Installed apps instead.

.DESCRIPTION
  Every system change is printed. Safe to run again.
    1. TC001MaxWebcam.exe uninstall   (IMFVirtualCamera::Remove, all-users and current-user)
    2. regsvr32 /u /s TC001MaxWebcamSource.comhost.dll
    3. Deletes C:\Program Files\TC001MaxWebcam\
    4. Deletes C:\ProgramData\TC001MaxWebcam\ (settings.json, webcam.log, claims\), unless -KeepSettings.
  The WinUSB driver that Zadig installed for the camera is not touched (README: "Uninstall").

.PARAMETER KeepSettings
  Keep C:\ProgramData\TC001MaxWebcam\ (settings.json and the log).

.PARAMETER RestartFrameServer
  Stop the FrameServer and FrameServerMonitor services first, so they release the DLL.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RestartFrameServer
#>
[CmdletBinding()]
param(
    [switch]$KeepSettings,
    [switch]$RestartFrameServer
)
$ErrorActionPreference = "Stop"

function Write-Change([string]$text) { Write-Host "  [change] $text" -ForegroundColor Yellow }
function Write-Same([string]$text) { Write-Host "  [ok]     $text" }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "uninstall.ps1 must run elevated: right-click PowerShell > Run as administrator, then run it again." -ForegroundColor Red
    exit 1
}

$dest = Join-Path $env:ProgramFiles "TC001MaxWebcam"
$exe = Join-Path $dest "TC001MaxWebcam.exe"
$dll = Join-Path $dest "TC001MaxWebcamSource.comhost.dll"
$dataDir = Join-Path $env:ProgramData "TC001MaxWebcam"
$problems = 0

Write-Host "1. Virtual camera"
if (Test-Path $exe) {
    Write-Change "`"$exe`" uninstall"
    & $exe uninstall
    if ($LASTEXITCODE -ne 0) { Write-Host "  uninstall returned $LASTEXITCODE" -ForegroundColor Red; $problems++ }
} else {
    Write-Same "$exe not present (nothing to remove)"
}

Write-Host "2. COM registration"
if (Test-Path $dll) {
    Write-Change "regsvr32 /u /s `"$dll`""
    $p = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList "/u", "/s", "`"$dll`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { Write-Host "  regsvr32 /u returned $($p.ExitCode)" -ForegroundColor Red; $problems++ }
} else {
    Write-Same "$dll not present"
}

Write-Host "3. Files"
if ($RestartFrameServer) {
    foreach ($svc in @("FrameServerMonitor", "FrameServer")) {
        $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
        if ($s -and $s.Status -eq "Running") {
            Write-Change "Stop-Service $svc (Windows restarts it on demand)"
            Stop-Service -Name $svc -Force
        }
    }
}
if (Test-Path $dest) {
    Write-Change "delete $dest"
    try {
        Remove-Item $dest -Recurse -Force
    } catch {
        Write-Host "  cannot delete everything: $($_.Exception.Message). Close camera apps and run again with -RestartFrameServer." -ForegroundColor Red
        $problems++
    }
} else {
    Write-Same "$dest not present"
}

Write-Host "4. Data folder"
if ($KeepSettings) {
    Write-Same "kept $dataDir (-KeepSettings)"
} elseif (Test-Path $dataDir) {
    Write-Change "delete $dataDir"
    try {
        Remove-Item $dataDir -Recurse -Force
    } catch {
        Write-Host "  cannot delete $dataDir ($($_.Exception.Message)); close camera apps and run again with -RestartFrameServer." -ForegroundColor Red
        $problems++
    }
} else {
    Write-Same "$dataDir not present"
}

if ($problems -eq 0) {
    Write-Host "Uninstalled." -ForegroundColor Green
} else {
    Write-Host "Finished with $problems problem(s), see above." -ForegroundColor Red
    exit 1
}
