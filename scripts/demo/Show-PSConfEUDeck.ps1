[CmdletBinding()]
param(
    [int] $StartSlide = 1,
    [switch] $Full,
    [switch] $Strict
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion -lt [version] '7.4') {
    throw 'Deck requires PowerShell 7.4 or newer.'
}

if (-not (Get-Module -ListAvailable -Name Deck)) {
    throw 'Deck is not installed. Run: Install-Module Deck -Scope CurrentUser -Force'
}

Import-Module Deck -ErrorAction Stop

$deckName = if ($Full) { 'PSConfEU-MultiPwsh-Full.deck.md' } else { 'PSConfEU-MultiPwsh.deck.md' }
$deckPath = Join-Path $PSScriptRoot $deckName
$showDeckParameters = @{
    Path = $deckPath
}

if ($StartSlide -gt 1) {
    $showDeckParameters.StartSlide = $StartSlide
}

if ($Strict) {
    $showDeckParameters.Strict = $true
}

Show-Deck @showDeckParameters
