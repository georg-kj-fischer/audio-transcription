[CmdletBinding()]
param(
    [string]$MsiPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $defaultMsi = Join-Path $repoRoot "AudioInOutTranscribing.Installer\bin\Release\AudioInOutTranscribing.Installer.msi"
    if (-not (Test-Path $defaultMsi)) {
        throw "MSI not found at '$defaultMsi'. Build it first with .\tools\build-msi.ps1."
    }
    $MsiPath = $defaultMsi
}

$MsiPath = [System.IO.Path]::GetFullPath($MsiPath)
if (-not (Test-Path $MsiPath)) {
    throw "MSI path '$MsiPath' does not exist."
}

$logDir = Join-Path $repoRoot "artifacts\logs"
New-Item -Path $logDir -ItemType Directory -Force | Out-Null
$logPath = Join-Path $logDir ("install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

Write-Host "Installing MSI: $MsiPath"
Write-Host "Installer log: $logPath"

& msiexec.exe /i "$MsiPath" /qb+ /norestart /l*vx "$logPath"
$exitCode = $LASTEXITCODE

switch ($exitCode) {
    0 {
        Write-Host "Installation completed successfully."
        exit 0
    }
    3010 {
        Write-Warning "Installation succeeded. A restart is required (exit code 3010)."
        exit 0
    }
    default {
        throw "Installation failed with exit code $exitCode. See log at '$logPath'."
    }
}
