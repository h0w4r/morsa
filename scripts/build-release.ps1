[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateSet('linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64')]
    [string] $Rid,

    [switch] $PublishOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:DOTNET) { $env:DOTNET } else { Join-Path $root '.dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$stage = Join-Path $root "artifacts\stage\$Rid"
$work = Join-Path $root "artifacts\publish\$Rid"
$dist = Join-Path $root 'artifacts\dist'
Remove-Item -Recurse -Force $stage, $work -ErrorAction SilentlyContinue

$directories = @(
    'bin', 'libexec\morsa', 'share\doc\morsa', 'share\man\man1',
    'share\bash-completion\completions', 'share\zsh\site-functions',
    'share\fish\vendor_completions.d'
)
foreach ($directory in $directories) {
    New-Item -ItemType Directory -Force -Path (Join-Path $stage $directory) | Out-Null
}
New-Item -ItemType Directory -Force -Path $work, $dist | Out-Null

function Publish-MorsaComponent {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $PublishedBinary,
        [Parameter(Mandatory)] [string] $InstalledBinary,
        [Parameter(Mandatory)] [string] $Destination
    )

    $projectPath = Join-Path $root $Project
    $output = Join-Path $work $InstalledBinary
    & $dotnet restore $projectPath --runtime $Rid --disable-parallel
    if ($LASTEXITCODE -ne 0) { throw "Restore failed for $Project." }

    & $dotnet publish $projectPath --configuration Release --runtime $Rid `
        --self-contained true --no-restore `
        "-p:Version=$Version" `
        '-p:PublishSingleFile=true' '-p:PublishTrimmed=false' `
        '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' `
        '-p:DebugSymbols=false' --output $output
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $Project." }

    $source = Join-Path $output $PublishedBinary
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing published binary $source." }
    Copy-Item -LiteralPath $source -Destination $Destination
}

Publish-MorsaComponent 'src\Morsa.Cli\Morsa.Cli.csproj' 'morsa' 'morsa' (Join-Path $stage 'bin\morsa')
Publish-MorsaComponent 'src\Morsa.ParserHost\Morsa.ParserHost.csproj' 'morsa-parser-host' 'morsa-parser-host' (Join-Path $stage 'libexec\morsa\morsa-parser-host')
Publish-MorsaComponent 'src\Morsa.PluginHost\Morsa.PluginHost.csproj' 'morsa-plugin-host' 'morsa-plugin-host' (Join-Path $stage 'libexec\morsa\morsa-plugin-host')
Publish-MorsaComponent 'src\Morsa.Mcp\Morsa.Mcp.csproj' 'morsa-mcp' 'morsa-mcp' (Join-Path $stage 'libexec\morsa\morsa-mcp')

# Delivery metadata and shell integrations accompany every self-contained payload.
Copy-Item "$root\LICENSE" "$stage\share\doc\morsa\LICENSE"
Copy-Item "$root\NOTICE.md" "$stage\share\doc\morsa\NOTICE.md"
Copy-Item "$root\README.md" "$stage\share\doc\morsa\README.md"
Copy-Item "$root\README.es.md" "$stage\share\doc\morsa\README.es.md"
# Ship the complete bilingual documentation for offline installations.
Copy-Item "$root\docs" "$stage\share\doc\morsa\docs" -Recurse
Copy-Item "$root\man\morsa.1" "$stage\share\man\man1\morsa.1"
Copy-Item "$root\completions\morsa.bash" "$stage\share\bash-completion\completions\morsa"
Copy-Item "$root\completions\_morsa" "$stage\share\zsh\site-functions\_morsa"
Copy-Item "$root\completions\morsa.fish" "$stage\share\fish\vendor_completions.d\morsa.fish"
Copy-Item "$root\scripts\install.sh" "$stage\install.sh"
Copy-Item "$root\scripts\uninstall.sh" "$stage\uninstall.sh"

$commit = (& git -C $root rev-parse HEAD 2>$null)
if (-not $commit) { $commit = 'unknown' }
@"
name=Morsa
version=$Version
rid=$Rid
commit=$commit
framework=net10.0
self_contained=true
trimmed=false
"@ | Set-Content -Encoding utf8NoBOM "$stage\share\doc\morsa\BUILD-INFO"

if ($PublishOnly) {
    Write-Host "Published $Rid payload at $stage"
    exit 0
}

# bsdtar is shipped with current Windows. Stage under the canonical archive root.
$archive = Join-Path $dist "morsa-$Version-$Rid.tar.gz"
$archiveParent = Join-Path $root 'artifacts\archive'
$archiveRoot = Join-Path $archiveParent "morsa-$Version-$Rid"
Remove-Item -Recurse -Force $archiveRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null
Copy-Item -Path "$stage\*" -Destination $archiveRoot -Recurse -Force
& tar -czf $archive -C $archiveParent (Split-Path -Leaf $archiveRoot)
if ($LASTEXITCODE -ne 0) { throw 'tar archive creation failed.' }
Write-Host "Created $archive"
