[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "AudioInOutTranscribing.App\AudioInOutTranscribing.App.csproj"
$installerProject = Join-Path $repoRoot "AudioInOutTranscribing.Installer\AudioInOutTranscribing.Installer.wixproj"
$publishDir = Join-Path $repoRoot ("artifacts\publish\{0}\{1}" -f $Configuration, $Runtime)

Write-Host "Publishing app to $publishDir ..."
dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishDirWithSlash = $publishDir
if (-not $publishDirWithSlash.EndsWith("\")) {
    $publishDirWithSlash = "$publishDirWithSlash\"
}

Write-Host "Building MSI ..."
dotnet build $installerProject -c $Configuration -p:PublishDir="$publishDirWithSlash" -p:ProductVersion="$Version"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$installerOutputDir = Join-Path $repoRoot ("AudioInOutTranscribing.Installer\bin\{0}" -f $Configuration)
$msi = Get-ChildItem -Path $installerOutputDir -Filter *.msi -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $msi) {
    throw "MSI was not produced. Look under $installerOutputDir for build artifacts."
}

Write-Host "MSI created: $($msi.FullName)"
