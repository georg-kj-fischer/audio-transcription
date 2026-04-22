[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "AudioInOutTranscribing.App\AudioInOutTranscribing.App.csproj"
$installerProject = Join-Path $repoRoot "AudioInOutTranscribing.Installer\AudioInOutTranscribing.Installer.wixproj"
$publishDir = Join-Path $repoRoot ("artifacts\publish\{0}\{1}" -f $Configuration, $Runtime)
$installerArtifactsRoot = Join-Path $repoRoot "AudioInOutTranscribing.Installer\bin"
$defaultSeedVersion = "1.0.0"

function Get-MsiVersionParts {
    param([Parameter(Mandatory = $true)][string]$InputVersion)

    if (-not ($InputVersion -match '^\d+\.\d+\.\d+$')) {
        throw "Version '$InputVersion' must follow MSI format Major.Minor.Build (for example 1.2.3)."
    }

    $parts = $InputVersion.Split('.') | ForEach-Object { [int]$_ }
    if ($parts[0] -lt 0 -or $parts[0] -gt 255) {
        throw "Version '$InputVersion' has Major=$($parts[0]), but MSI requires Major to be between 0 and 255."
    }

    if ($parts[1] -lt 0 -or $parts[1] -gt 255) {
        throw "Version '$InputVersion' has Minor=$($parts[1]), but MSI requires Minor to be between 0 and 255."
    }

    if ($parts[2] -lt 0 -or $parts[2] -gt 65535) {
        throw "Version '$InputVersion' has Build=$($parts[2]), but MSI requires Build to be between 0 and 65535."
    }

    return $parts
}

function Get-MsiProductVersion {
    param([Parameter(Mandatory = $true)][string]$MsiPath)

    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $database = $installer.OpenDatabase($MsiPath, 0)
        $view = $database.OpenView("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'")
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "Could not read ProductVersion from MSI '$MsiPath'."
        }

        return [string]$record.StringData(1)
    }
    finally {
        if ($null -ne $record) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($record) }
        if ($null -ne $view) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($view) }
        if ($null -ne $database) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
        if ($null -ne $installer) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) }
    }
}

function Increment-MsiVersion {
    param([Parameter(Mandatory = $true)][string]$InputVersion)

    $parts = Get-MsiVersionParts -InputVersion $InputVersion
    $major = $parts[0]
    $minor = $parts[1]
    $build = $parts[2]

    $build++
    if ($build -gt 65535) {
        $build = 0
        $minor++
    }

    if ($minor -gt 255) {
        $minor = 0
        $major++
    }

    if ($major -gt 255) {
        throw "Cannot auto-increment beyond MSI version 255.255.65535."
    }

    return "{0}.{1}.{2}" -f $major, $minor, $build
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $latestMsi = Get-ChildItem -Path $installerArtifactsRoot -Filter *.msi -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -ne $latestMsi) {
        $currentVersion = Get-MsiProductVersion -MsiPath $latestMsi.FullName
        $Version = Increment-MsiVersion -InputVersion $currentVersion
        Write-Host "Auto-incremented MSI version from $currentVersion to $Version."
    }
    else {
        $Version = Increment-MsiVersion -InputVersion $defaultSeedVersion
        Write-Host "No existing MSI found. Using initial auto-incremented version $Version."
    }
}
else {
    [void](Get-MsiVersionParts -InputVersion $Version)
    Write-Host "Using explicit MSI version $Version."
}

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
