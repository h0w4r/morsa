[CmdletBinding()]
param(
    # Installs locally by default so the host operating system remains unchanged.
    [string] $InstallDirectory
)

$ErrorActionPreference = 'Stop'
$sdkVersion = '10.0.302'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $InstallDirectory) {
    $InstallDirectory = Join-Path $repositoryRoot '.dotnet'
    # Keep Windows and Linux native hosts separate in repositories shared by WSL.
    if ((Test-Path -LiteralPath (Join-Path $InstallDirectory 'dotnet')) -and
        -not (Test-Path -LiteralPath (Join-Path $InstallDirectory 'dotnet.exe'))) {
        $InstallDirectory = Join-Path $repositoryRoot '.dotnet-windows'
    }
}
$installScript = Join-Path ([System.IO.Path]::GetTempPath()) "morsa-dotnet-install-$sdkVersion.ps1"
$dotnetExecutable = Join-Path $InstallDirectory 'dotnet.exe'

function Get-InstalledSdkVersion {
    param([Parameter(Mandatory)] [string] $Executable)

    # Avoid a parent global.json selecting an unrelated repository-local SDK.
    Push-Location ([System.IO.Path]::GetPathRoot($Executable))
    try {
        return (& $Executable --version)
    }
    finally {
        Pop-Location
    }
}

if ((Test-Path -LiteralPath $dotnetExecutable) -and
    (Get-InstalledSdkVersion -Executable $dotnetExecutable) -eq $sdkVersion) {
    Write-Host "Morsa .NET SDK $sdkVersion is already installed in $InstallDirectory"
    exit 0
}

New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null

try {
    # TLS certificate validation is left enabled; no insecure bypass is permitted.
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
    & $installScript -Version $sdkVersion -InstallDir $InstallDirectory -NoPath

    $actualVersion = Get-InstalledSdkVersion -Executable $dotnetExecutable
    if ($actualVersion -ne $sdkVersion) {
        throw "Expected SDK $sdkVersion but installed $actualVersion."
    }

    Write-Host "Installed .NET SDK $actualVersion in $InstallDirectory"
    Write-Host "Use: `$env:PATH = '$InstallDirectory;' + `$env:PATH"
}
finally {
    Remove-Item -LiteralPath $installScript -Force -ErrorAction SilentlyContinue
}
