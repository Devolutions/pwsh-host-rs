[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    Write-Host 'Skipping Windows bootstrap installer test on non-Windows runner.'
    return
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerPath = Join-Path $repoRoot 'tools\install-multi-pwsh.ps1'
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer script was not found at $installerPath"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("multi-pwsh-bootstrap-test-" + [System.Guid]::NewGuid().ToString('N'))
$oldMultiPwshHome = $env:MULTI_PWSH_HOME
$oldMultiPwshBinDir = $env:MULTI_PWSH_BIN_DIR
$oldUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')

try {
    New-Item -Path $testRoot -ItemType Directory -Force | Out-Null

    $env:MULTI_PWSH_HOME = Join-Path $testRoot 'home'
    $env:MULTI_PWSH_BIN_DIR = Join-Path $testRoot 'bin'

    $script:MockArchivePath = $null
    $script:MockPayloadRoot = Join-Path $testRoot 'payload'

    function Invoke-WebRequest {
        param(
            [Parameter(Mandatory = $true)]
            [string]$Uri,

            [Parameter(Mandatory = $true)]
            [string]$OutFile,

            [switch]$UseBasicParsing
        )

        $outDir = Split-Path -Parent $OutFile
        New-Item -Path $outDir -ItemType Directory -Force | Out-Null

        if ($OutFile.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $script:MockPayloadRoot -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -Path $script:MockPayloadRoot -ItemType Directory -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:MockPayloadRoot 'multi-pwsh.exe') -Value 'mock multi-pwsh' -Encoding ascii
            Compress-Archive -Path (Join-Path $script:MockPayloadRoot 'multi-pwsh.exe') -DestinationPath $OutFile -Force
            $script:MockArchivePath = $OutFile
            return
        }

        Assert-True -Condition ($null -ne $script:MockArchivePath) -Message 'Checksum was requested before the archive was created.'
        $assetName = Split-Path -Leaf $script:MockArchivePath
        $hash = (Get-FileHash -LiteralPath $script:MockArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath $OutFile -Value "$hash  $assetName" -Encoding ascii
    }

    Get-Content -LiteralPath $installerPath -Raw | Invoke-Expression

    $targetExe = Join-Path $env:MULTI_PWSH_BIN_DIR 'multi-pwsh.exe'
    Assert-True -Condition (Test-Path -LiteralPath $targetExe -PathType Leaf) -Message "Expected installer to create $targetExe"
}
finally {
    [Environment]::SetEnvironmentVariable('Path', $oldUserPath, 'User')

    if ($null -eq $oldMultiPwshHome) {
        Remove-Item Env:\MULTI_PWSH_HOME -ErrorAction SilentlyContinue
    }
    else {
        $env:MULTI_PWSH_HOME = $oldMultiPwshHome
    }

    if ($null -eq $oldMultiPwshBinDir) {
        Remove-Item Env:\MULTI_PWSH_BIN_DIR -ErrorAction SilentlyContinue
    }
    else {
        $env:MULTI_PWSH_BIN_DIR = $oldMultiPwshBinDir
    }

    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'Bootstrap installer test completed successfully.'
