[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('AliasPinning', 'HostSelectors', 'PrereleaseInstall', 'VenvSupport')]
    [string]$Demo,

    [switch]$KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = switch ($Demo) {
    'AliasPinning' { Join-Path $PSScriptRoot 'Demo-AliasPinning.ps1' }
    'HostSelectors' { Join-Path $PSScriptRoot 'Demo-HostSelectors.ps1' }
    'PrereleaseInstall' { Join-Path $PSScriptRoot 'Demo-PrereleaseInstall.ps1' }
    'VenvSupport' { Join-Path $PSScriptRoot 'Demo-VenvSupport.ps1' }
}

& $scriptPath -Deck -PauseSeconds 0 -KeepArtifacts:$KeepArtifacts
