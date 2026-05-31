[CmdletBinding()]
param(
    [string]$DemoName = 'psconfeu-full'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_DemoCommon.ps1')

if (Get-Variable -Name PSConfEUFullDemoContext -Scope Global -ErrorAction SilentlyContinue) {
    Remove-DemoContext -Context $global:PSConfEUFullDemoContext
    Remove-Variable -Name PSConfEUFullDemoContext -Scope Global -Force
    Remove-Item Env:\MULTI_PWSH_CACHE_KEEP -ErrorAction SilentlyContinue
    return
}

$demoHome = Join-Path $HOME ".pwsh-demo\$DemoName"
if (Test-Path -LiteralPath $demoHome) {
    Remove-Item -LiteralPath $demoHome -Recurse -Force
}

Remove-Item Env:\MULTI_PWSH_CACHE_KEEP -ErrorAction SilentlyContinue
