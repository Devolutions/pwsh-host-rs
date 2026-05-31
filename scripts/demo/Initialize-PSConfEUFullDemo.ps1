[CmdletBinding()]
param(
    [string]$DemoName = 'psconfeu-full',
    [switch]$KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '_DemoCommon.ps1')

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$releasePath = Join-Path $repoRoot 'target\release'
if (Test-Path -LiteralPath $releasePath) {
    $env:PATH = "$releasePath;$env:PATH"
}

$global:PSConfEUFullDemoContext = New-DemoContext -DemoName $DemoName -KeepArtifacts:$KeepArtifacts
$env:MULTI_PWSH_CACHE_KEEP = '1'

Show-DemoContext -Context $global:PSConfEUFullDemoContext
Write-Host ''
Write-Host 'Full demo context stored in $global:PSConfEUFullDemoContext.' -ForegroundColor Green
Write-Host 'Run .\scripts\demo\Reset-PSConfEUFullDemo.ps1 when you are done.' -ForegroundColor Green
