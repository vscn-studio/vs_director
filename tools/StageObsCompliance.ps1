param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][string]$ObsBuildDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('windows-x64', 'linux-x86_64', 'macos-arm64', 'macos-x86_64')][string]$BuildTarget,
    [string]$ExpectedObsRevision = $env:OBS_REF
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$obsRepository = Join-Path $repository 'obs-studio'
$obsHeaders = Join-Path $obsRepository 'libobs'
$generatedHeaders = Join-Path $ObsBuildDirectory 'config'
if (-not (Test-Path -LiteralPath (Join-Path $obsHeaders 'obs-module.h')) -or
    -not (Test-Path -LiteralPath (Join-Path $generatedHeaders 'obsconfig.h'))) {
    throw 'OBS source and generated obsconfig.h are required; build OBS before packaging.'
}
function Invoke-GitChecked {
    param([string]$Directory, [string[]]$Arguments)
    $result = & git -C $Directory @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed in $Directory" }
    return $result
}
$obsRevision = Invoke-GitChecked $obsRepository @('rev-parse', 'HEAD')
if (-not $ExpectedObsRevision -or $obsRevision -ne $ExpectedObsRevision) {
    throw "OBS checkout $obsRevision does not match the expected revision $ExpectedObsRevision"
}
$submodules = @(Invoke-GitChecked $obsRepository @('submodule', 'status', '--recursive'))
foreach ($module in $submodules) {
    if ($module -notmatch '^ [0-9a-f]{40} ') { throw "OBS submodule is missing or mismatched: $module" }
}
$legalDirectory = [System.IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath (Join-Path $legalDirectory 'corresponding-source')) {
    throw 'Use a fresh staging directory to avoid mixing source files from different builds.'
}
$sourceDirectory = Join-Path $legalDirectory 'corresponding-source'
New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
foreach ($name in @('LICENSE', 'COPYRIGHT.txt')) {
    Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $legalDirectory
}
$licenseDirectory = Join-Path $legalDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
foreach ($name in @('OBS-THIRD-PARTY.txt', 'GPL-2.0.txt')) {
    Copy-Item -LiteralPath (Join-Path $repository "licenses/$name") -Destination $licenseDirectory
}
$pluginSource = Join-Path $sourceDirectory 'obs-plugin'
New-Item -ItemType Directory -Force -Path $pluginSource | Out-Null
foreach ($name in @('CMakeLists.txt', 'README.md', 'src', 'data')) {
    Copy-Item -LiteralPath (Join-Path $repository "obs-plugin/$name") -Destination $pluginSource -Recurse -Force
}
$workflowDirectory = Join-Path $sourceDirectory '.github/workflows'
New-Item -ItemType Directory -Force -Path $workflowDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $repository '.github/workflows/build-obs-plugin.yml') -Destination $workflowDirectory
$toolDirectory = Join-Path $sourceDirectory 'tools'
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
Copy-Item -LiteralPath $PSCommandPath -Destination $toolDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CheckObsPackage.py') -Destination $toolDirectory
Copy-Item -LiteralPath (Join-Path $repository 'modinfo.json') -Destination $sourceDirectory
# Include interface declarations and the configuration generated for this slice.
foreach ($header in Get-ChildItem -LiteralPath $obsHeaders -Recurse -File -Filter '*.h') {
    $relative = [System.IO.Path]::GetRelativePath($obsHeaders, $header.FullName)
    $target = Join-Path $sourceDirectory "obs-headers/libobs/$relative"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $header.FullName -Destination $target
}
$buildInputs = Join-Path $sourceDirectory "build-inputs/$BuildTarget"
New-Item -ItemType Directory -Force -Path $buildInputs | Out-Null
Copy-Item -LiteralPath $generatedHeaders -Destination (Join-Path $buildInputs 'config') -Recurse
Copy-Item -LiteralPath (Join-Path $obsRepository 'plugins/CMakeLists.txt') -Destination (Join-Path $buildInputs 'plugins-CMakeLists.txt')
Copy-Item -LiteralPath (Join-Path $obsRepository 'CMakePresets.json') -Destination $buildInputs
# Archive tracked upstream sources, including nested submodules, without .git,
# checkout credentials, downloaded binaries or build output. The workflow above
# records how the separately included plugin is added to this upstream tree.
$upstream = Join-Path $sourceDirectory 'upstream'
New-Item -ItemType Directory -Force -Path $upstream | Out-Null
$archives = @()
$repositories = @(@{ Path = '.'; Revision = $obsRevision; Directory = $obsRepository })
foreach ($module in $submodules) {
    if ($module -match '^ ([0-9a-f]{40}) (\S+)') {
        $repositories += @{ Path = $Matches[2]; Revision = $Matches[1]; Directory = (Join-Path $obsRepository $Matches[2]) }
    }
}
for ($index = 0; $index -lt $repositories.Count; $index++) {
    $entry = $repositories[$index]
    $name = if ($index -eq 0) { 'obs-studio.tar.gz' } else { "submodule-$index.tar.gz" }
    $archive = Join-Path $upstream $name
    Invoke-GitChecked $entry.Directory @('archive', '--format=tar.gz', "--output=$archive", $entry.Revision)
    $archives += [ordered]@{ path = $entry.Path; revision = $entry.Revision; archive = $name; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{
    directorRevision = (Invoke-GitChecked $repository @('rev-parse', 'HEAD'))
    obsRevision = $obsRevision
    target = $BuildTarget
    sources = $archives
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $buildInputs 'manifest.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $sourceDirectory
Copy-Item -LiteralPath (Join-Path $repository 'COPYRIGHT.txt') -Destination $sourceDirectory
New-Item -ItemType Directory -Force -Path (Join-Path $sourceDirectory 'licenses') | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'licenses/OBS-THIRD-PARTY.txt') -Destination (Join-Path $sourceDirectory 'licenses')
Copy-Item -LiteralPath (Join-Path $repository 'licenses/GPL-2.0.txt') -Destination (Join-Path $sourceDirectory 'licenses')
