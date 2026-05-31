[CmdletBinding()]
param(
    [switch]$Full
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:MULTI_PWSH_VENV_DIR)) {
    throw 'MULTI_PWSH_VENV_DIR is not set. Create a demo context before running this script.'
}

$worlds = @(
    @{
        Name = 'cloud'
        ModuleName = 'Conference.CloudIdentity'
        Message = 'Graph / Entra / AI-generated test in the cloud world'
    },
    @{
        Name = 'onprem'
        ModuleName = 'Conference.OnPremOps'
        Message = 'AD / scheduled job / firewall-safe path in the on-prem world'
    }
)

if ($Full) {
    $worlds += @(
        @{
            Name = 'preview'
            ModuleName = 'Conference.PreviewLab'
            Message = 'Preview / compatibility test before the next PSConfEU session'
        },
        @{
            Name = 'ai'
            ModuleName = 'Conference.AISafety'
            Message = 'AI-generated script review in a disposable module world'
        }
    )
}

foreach ($world in $worlds) {
    $venvPath = Join-Path $env:MULTI_PWSH_VENV_DIR $world.Name
    if (-not (Test-Path -LiteralPath $venvPath)) {
        & multi-pwsh venv create $world.Name
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create multi-pwsh venv '$($world.Name)'."
        }
    }

    $modulePath = Join-Path $venvPath "Modules\$($world.ModuleName)"
    New-Item -ItemType Directory -Force -Path $modulePath | Out-Null

    $moduleFile = Join-Path $modulePath "$($world.ModuleName).psm1"
    $message = $world.Message
    $content = @"
function Get-ConferenceReality {
    '$message'
}

Export-ModuleMember -Function Get-ConferenceReality
"@

    Set-Content -Path $moduleFile -Value $content -Encoding utf8

    [pscustomobject]@{
        Venv = $world.Name
        Module = $world.ModuleName
        Path = $moduleFile
    }
}
