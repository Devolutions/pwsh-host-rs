[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = 'latest',

    [Parameter(Mandatory = $false)]
    [string]$Owner = 'Devolutions',

    [Parameter(Mandatory = $false)]
    [string]$Repository = 'multi-pwsh',

    [Parameter(Mandatory = $false)]
    [string]$OfflineCache,

    [Parameter(Mandatory = $false)]
    [string]$ArchivePath,

    [Parameter(Mandatory = $false)]
    [string]$ChecksumPath
)

$ErrorActionPreference = 'Stop'

function Get-ReleaseArch {
    $candidates = @($env:PROCESSOR_ARCHITECTURE, $env:PROCESSOR_ARCHITEW6432) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        switch ($candidate.ToUpperInvariant()) {
            'ARM64' { return 'arm64' }
            'AMD64' { return 'x64' }
        }
    }

    if ([Environment]::Is64BitOperatingSystem) {
        return 'x64'
    }

    throw "Unsupported architecture. Supported architectures: AMD64, ARM64"
}

function Test-PathContainsEntry {
    param(
        [string]$PathValue,
        [string]$Entry
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $false
    }

    $entryNormalized = $Entry.Trim().TrimEnd('\\')
    $segments = $PathValue -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($segment in $segments) {
        if ([string]::Equals($segment.Trim().TrimEnd('\\'), $entryNormalized, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-MultiPwshHome {
    if (-not [string]::IsNullOrWhiteSpace($env:MULTI_PWSH_HOME)) {
        return $env:MULTI_PWSH_HOME
    }

    return (Join-Path $HOME '.pwsh')
}

function Get-MultiPwshBinDir {
    if (-not [string]::IsNullOrWhiteSpace($env:MULTI_PWSH_BIN_DIR)) {
        return $env:MULTI_PWSH_BIN_DIR
    }

    return (Join-Path (Get-MultiPwshHome) 'bin')
}

function Resolve-ReleaseVersionDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CacheRoot,

        [Parameter(Mandatory = $true)]
        [string]$RequestedVersion,

        [Parameter(Mandatory = $true)]
        [string]$AssetName
    )

    $multiPwshRoot = Join-Path $CacheRoot 'multi-pwsh'
    if (-not (Test-Path -LiteralPath $multiPwshRoot -PathType Container)) {
        throw "Offline cache does not contain a multi-pwsh directory: $multiPwshRoot"
    }

    if ($RequestedVersion -eq 'latest') {
        $candidates = Get-ChildItem -LiteralPath $multiPwshRoot -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $AssetName) -PathType Leaf } |
            Sort-Object Name -Descending

        if (-not $candidates) {
            throw "Offline cache does not contain $AssetName under $multiPwshRoot"
        }

        return $candidates[0].FullName
    }

    $versionDirectoryName = if ($RequestedVersion.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        $RequestedVersion
    }
    else {
        "v$RequestedVersion"
    }
    $versionDirectory = Join-Path $multiPwshRoot $versionDirectoryName
    if (-not (Test-Path -LiteralPath (Join-Path $versionDirectory $AssetName) -PathType Leaf)) {
        throw "Offline cache does not contain $AssetName under $versionDirectory"
    }

    return $versionDirectory
}

function Assert-ArchiveChecksum {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,

        [Parameter(Mandatory = $true)]
        [string]$ChecksumPath,

        [Parameter(Mandatory = $true)]
        [string]$AssetName
    )

    if (-not (Test-Path -LiteralPath $ChecksumPath -PathType Leaf)) {
        throw "Checksum file was not found: $ChecksumPath"
    }

    $expected = $null
    foreach ($line in Get-Content -LiteralPath $ChecksumPath) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }

        if ($trimmed -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq $AssetName) {
            $expected = $Matches[1].ToLowerInvariant()
            break
        }
    }

    if (-not $expected) {
        throw "Checksum entry for $AssetName was not found in $ChecksumPath"
    }

    $actual = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "Checksum mismatch for $AssetName`: expected $expected, got $actual"
    }
}

$arch = Get-ReleaseArch
$assetName = "multi-pwsh-windows-$arch.zip"

if ($Version -eq 'latest') {
    $releasePath = 'latest/download'
    $displayVersion = 'latest'
}
else {
    if (-not $Version.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        $Version = "v$Version"
    }

    $releasePath = "download/$Version"
    $displayVersion = $Version
}

$downloadUrl = "https://github.com/$Owner/$Repository/releases/$releasePath/$assetName"
$checksumUrl = "https://github.com/$Owner/$Repository/releases/$releasePath/checksums.txt"
$installHome = Get-MultiPwshHome
$binDir = Get-MultiPwshBinDir
$targetExe = Join-Path $binDir 'multi-pwsh.exe'

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("multi-pwsh-install-" + [System.Guid]::NewGuid().ToString('N'))
$resolvedArchivePath = if ([string]::IsNullOrWhiteSpace($ArchivePath)) { Join-Path $tempRoot $assetName } else { $ArchivePath }
$localChecksumPath = if ([string]::IsNullOrWhiteSpace($ChecksumPath)) { Join-Path $tempRoot 'checksums.txt' } else { $ChecksumPath }
$extractDir = Join-Path $tempRoot 'extract'

New-Item -Path $extractDir -ItemType Directory -Force | Out-Null

try {
    if (-not [string]::IsNullOrWhiteSpace($OfflineCache)) {
        $versionDirectory = Resolve-ReleaseVersionDirectory -CacheRoot $OfflineCache -RequestedVersion $Version -AssetName $assetName
        $sourceArchive = Join-Path $versionDirectory $assetName
        $sourceChecksum = if ([string]::IsNullOrWhiteSpace($ChecksumPath)) { Join-Path $versionDirectory 'checksums.txt' } else { $ChecksumPath }
        Write-Host "Using offline $assetName from $sourceArchive"
        if (-not [string]::Equals([System.IO.Path]::GetFullPath($sourceArchive), [System.IO.Path]::GetFullPath($resolvedArchivePath), [System.StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $sourceArchive -Destination $resolvedArchivePath -Force
        }
        if (-not [string]::Equals([System.IO.Path]::GetFullPath($sourceChecksum), [System.IO.Path]::GetFullPath($localChecksumPath), [System.StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $sourceChecksum -Destination $localChecksumPath -Force
        }
    }
    elseif ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        Write-Host "Downloading $assetName ($displayVersion)..."

        $invokeParams = @{
            Uri = $downloadUrl
            OutFile = $resolvedArchivePath
        }

        $checksumInvokeParams = @{
            Uri = $checksumUrl
            OutFile = $localChecksumPath
        }

        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            $invokeParams['UseBasicParsing'] = $true
            $checksumInvokeParams['UseBasicParsing'] = $true
        }

        Invoke-WebRequest @invokeParams
        Invoke-WebRequest @checksumInvokeParams
    }
    elseif ([string]::IsNullOrWhiteSpace($ChecksumPath)) {
        throw '-ChecksumPath is required when -ArchivePath is used without -OfflineCache'
    }

    Assert-ArchiveChecksum -ArchivePath $resolvedArchivePath -ChecksumPath $localChecksumPath -AssetName $assetName

    Expand-Archive -Path $resolvedArchivePath -DestinationPath $extractDir -Force

    $sourceExe = Join-Path $extractDir 'multi-pwsh.exe'
    if (-not (Test-Path -Path $sourceExe -PathType Leaf)) {
        throw 'Archive did not contain expected binary: multi-pwsh.exe'
    }

    New-Item -Path $binDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path $sourceExe -Destination $targetExe -Force

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Test-PathContainsEntry -PathValue $userPath -Entry $binDir)) {
        $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $binDir } else { "$userPath;$binDir" }
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
        $pathStatus = "Added $binDir to user PATH."
    }
    else {
        $pathStatus = "$binDir is already present in user PATH."
    }

    if (-not (Test-PathContainsEntry -PathValue $env:Path -Entry $binDir)) {
        $env:Path = "$binDir;$env:Path"
    }

    Write-Host "Installed multi-pwsh to $targetExe"
    Write-Host $pathStatus
    Write-Host 'Run: multi-pwsh --help'
}
finally {
    if (Test-Path -Path $tempRoot -PathType Container) {
        Remove-Item -Path $tempRoot -Recurse -Force
    }
}