[CmdletBinding()]
param(
    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$StagingRoot,

    [string]$OutputRoot,

    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    [switch]$NoBuild,

    [switch]$NoPack,

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$forwardedParameters = @{}
foreach ($key in $PSBoundParameters.Keys) {
    $forwardedParameters[$key] = $PSBoundParameters[$key]
}

if (-not $forwardedParameters.ContainsKey('OutputRoot')) {
    $forwardedParameters['OutputRoot'] = Join-Path $repoRoot 'artifacts\cli-nuget'
}

& (Join-Path $PSScriptRoot 'Build-NativeNuGetPackages.ps1') @forwardedParameters
