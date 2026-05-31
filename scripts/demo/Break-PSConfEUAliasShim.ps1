[CmdletBinding()]
param(
    [string]$AliasName = 'pwsh-7.4'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:MULTI_PWSH_BIN_DIR)) {
    throw 'MULTI_PWSH_BIN_DIR is not set. Create a demo context before running this script.'
}

$shimName = if ($IsWindows -and -not $AliasName.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    "$AliasName.exe"
}
else {
    $AliasName
}

$shimPath = Join-Path $env:MULTI_PWSH_BIN_DIR $shimName
if (-not (Test-Path -LiteralPath $shimPath)) {
    throw "Alias shim '$shimPath' does not exist. Create the alias before breaking it."
}

$brokenPath = "$shimPath.broken"
Move-Item -LiteralPath $shimPath -Destination $brokenPath -Force

[pscustomobject]@{
    BrokenAlias = $AliasName
    MovedFrom = $shimPath
    MovedTo = $brokenPath
}
