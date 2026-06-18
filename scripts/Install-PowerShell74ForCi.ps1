[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-GitHubHeaders {
    $headers = @{
        "User-Agent" = "multi-pwsh-ci"
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN"
    }

    $headers
}

function Get-PowerShell74Release {
    $headers = Get-GitHubHeaders
    $releases = Invoke-RestMethod `
        -Headers $headers `
        -Uri "https://api.github.com/repos/PowerShell/PowerShell/releases?per_page=100"

    $release = $releases |
        Where-Object { -not $_.prerelease -and $_.tag_name -match "^v7\.4\.\d+$" } |
        Sort-Object { [version]$_.tag_name.TrimStart("v") } -Descending |
        Select-Object -First 1

    if ($null -eq $release) {
        throw "Unable to find a PowerShell 7.4.x release."
    }

    $release
}

function Get-AssetName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    $archName = switch ($arch) {
        "Arm64" { "arm64" }
        "X64" { "x64" }
        default { throw "Unsupported process architecture for PowerShell CI bootstrap: $arch" }
    }

    if ($IsWindows) {
        "PowerShell-$Version-win-$archName.zip"
    }
    elseif ($IsMacOS) {
        "powershell-$Version-osx-$archName.tar.gz"
    }
    else {
        "powershell-$Version-linux-$archName.tar.gz"
    }
}

function New-PwshShim {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PwshExe,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [Parameter(Mandatory = $true)]
        [string]$TempRoot
    )

    $shimDir = Join-Path $TempRoot "pwsh-7.4-shims"
    New-Item -Path $shimDir -ItemType Directory -Force | Out-Null

    $minorShimName = if ($IsWindows) { "pwsh-7.4.cmd" } else { "pwsh-7.4" }
    $patchShimName = if ($IsWindows) { "pwsh-$Version.cmd" } else { "pwsh-$Version" }

    foreach ($shimName in @($minorShimName, $patchShimName)) {
        $shimPath = Join-Path $shimDir $shimName

        if ($IsWindows) {
            $escapedExe = $PwshExe.Replace("%", "%%")
            "@echo off`r`n`"$escapedExe`" %*`r`n" | Set-Content -Path $shimPath -Encoding ascii -NoNewline
        }
        else {
            @"
#!/usr/bin/env sh
exec "$PwshExe" "$@"
"@ | Set-Content -Path $shimPath -Encoding utf8NoBOM -NoNewline
            chmod +x $shimPath
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_PATH)) {
        $shimDir | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
    }

    $env:PATH = "$shimDir$([IO.Path]::PathSeparator)$env:PATH"
}

$tempRoot = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    $env:RUNNER_TEMP
}
else {
    [IO.Path]::GetTempPath()
}

$release = Get-PowerShell74Release
$version = $release.tag_name.TrimStart("v")
$assetName = Get-AssetName -Version $version
$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1

if ($null -eq $asset) {
    throw "Unable to find PowerShell asset '$assetName' in release $($release.tag_name)."
}

$installDir = Join-Path $tempRoot "pwsh-$version"
$archivePath = Join-Path $tempRoot $assetName

if (Test-Path -Path $installDir) {
    Remove-Item -Path $installDir -Recurse -Force
}

New-Item -Path $installDir -ItemType Directory -Force | Out-Null

Write-Host "Downloading $assetName..."
Invoke-WebRequest -Uri $asset.browser_download_url -Headers (Get-GitHubHeaders) -OutFile $archivePath

if ($assetName.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
    Expand-Archive -Path $archivePath -DestinationPath $installDir -Force
}
else {
    tar -xzf $archivePath -C $installDir
}

$pwshExeName = if ($IsWindows) { "pwsh.exe" } else { "pwsh" }
$pwshExe = Join-Path $installDir $pwshExeName

if (-not (Test-Path -Path $pwshExe -PathType Leaf)) {
    throw "PowerShell executable was not found after extraction: $pwshExe"
}

if (-not $IsWindows) {
    chmod +x $pwshExe
}

New-PwshShim -PwshExe $pwshExe -Version $version -TempRoot $tempRoot

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
    "PwshExePath=$pwshExe" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
}

Write-Host "Installed PowerShell $version for binding discovery: $pwshExe"
& $pwshExe -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
