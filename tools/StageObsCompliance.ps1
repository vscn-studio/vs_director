param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$legalDirectory = [System.IO.Path]::GetFullPath($Destination)
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
Copy-Item -LiteralPath (Join-Path $repository 'modinfo.json') -Destination $sourceDirectory
# Include the dependency's interface declarations used by the CI build.
$obsHeaders = Join-Path $repository 'obs-studio/libobs'
if (Test-Path -LiteralPath $obsHeaders) {
    foreach ($header in Get-ChildItem -LiteralPath $obsHeaders -Recurse -File -Filter '*.h') {
        $relative = [System.IO.Path]::GetRelativePath($obsHeaders, $header.FullName)
        $target = Join-Path $sourceDirectory "obs-headers/libobs/$relative"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $header.FullName -Destination $target
    }
}
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $sourceDirectory
Copy-Item -LiteralPath (Join-Path $repository 'COPYRIGHT.txt') -Destination $sourceDirectory
New-Item -ItemType Directory -Force -Path (Join-Path $sourceDirectory 'licenses') | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'licenses/OBS-THIRD-PARTY.txt') -Destination (Join-Path $sourceDirectory 'licenses')
Copy-Item -LiteralPath (Join-Path $repository 'licenses/GPL-2.0.txt') -Destination (Join-Path $sourceDirectory 'licenses')
