<#
.SYNOPSIS
Runs the tests and builds VS Director (MIT, HansJack / VSCN-Studio).
.DESCRIPTION
Requires .NET 10, Vintage Story 1.22.3, and extracted VSImGui 1.2.7.
Set VINTAGE_STORY and VSIMGUI, or pass the dependency paths below.
Release packages are written to artifacts. Deployment is disabled unless
ModDeploymentDirectory is explicitly supplied; existing old packages are kept.
.EXAMPLE
./build.ps1 -VintageStoryPath /path/to/Vintagestory -VSImGuiPath /path/to/vsimgui_1.2.7
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$VintageStoryPath,
    [string]$VSImGuiPath,
    [string]$ModDeploymentDirectory
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "VSDirector.csproj"
$tests = Join-Path $PSScriptRoot "tests\VSDirector.CoreTests\VSDirector.CoreTests.csproj"
$patchProbe = Join-Path $PSScriptRoot "tests\VSDirector.PatchProbe\VSDirector.PatchProbe.csproj"
$dependencyProperties = @()
if ($VintageStoryPath) { $dependencyProperties += "-p:VintageStoryPath=$VintageStoryPath" }
if ($VSImGuiPath) { $dependencyProperties += "-p:VSImGuiPath=$VSImGuiPath" }
$deploymentProperties = @()
if ($ModDeploymentDirectory) { $deploymentProperties += "-p:ModDeploymentDirectory=$ModDeploymentDirectory" }

dotnet run --project $tests --configuration $Configuration @dependencyProperties
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet run --project $patchProbe --configuration $Configuration @dependencyProperties
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet build $project --configuration $Configuration @dependencyProperties @deploymentProperties
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
Write-Host "VSImGui UI checks are covered by PatchProbe."
exit 0
