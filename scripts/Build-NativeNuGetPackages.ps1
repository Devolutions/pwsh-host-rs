[CmdletBinding()]
param(
    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$StagingRoot,

    [string]$OutputRoot,

    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    [ValidateSet('Cli')]
    [string[]]$Packages = @('Cli'),

    [switch]$NoBuild,

    [switch]$NoPack,

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($StagingRoot)) {
    $StagingRoot = Join-Path $repoRoot 'artifacts\cli\multi-pwsh'
}
elseif (-not [System.IO.Path]::IsPathRooted($StagingRoot)) {
    $StagingRoot = Join-Path $repoRoot $StagingRoot
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\native-nuget'
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot $OutputRoot
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList
    )

    Write-Host ">> $FilePath $($ArgumentList -join ' ')"
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($ArgumentList -join ' ')"
    }
}

function Get-SourceVersion {
    $multiManifest = Join-Path $repoRoot 'crates\multi-pwsh\Cargo.toml'
    $hostManifest = Join-Path $repoRoot 'crates\pwsh-host\Cargo.toml'

    $multiMatch = Select-String -Path $multiManifest -Pattern '^version = "([^"]+)"$' | Select-Object -First 1
    $hostMatch = Select-String -Path $hostManifest -Pattern '^version = "([^"]+)"$' | Select-Object -First 1

    if (($null -eq $multiMatch) -or ($null -eq $hostMatch)) {
        throw 'Unable to detect crate versions from Cargo.toml.'
    }

    $multiVersion = $multiMatch.Matches[0].Groups[1].Value
    $hostVersion = $hostMatch.Matches[0].Groups[1].Value

    if ($multiVersion -ne $hostVersion) {
        throw "Crate version mismatch detected: multi-pwsh=$multiVersion pwsh-host=$hostVersion"
    }

    $multiVersion
}

function Resolve-RustTarget {
    param([Parameter(Mandatory)][string]$RuntimeIdentifier)

    switch ($RuntimeIdentifier) {
        'win-x64' { @{ CargoTarget = 'x86_64-pc-windows-msvc'; BinaryName = 'multi-pwsh.exe' } }
        'win-arm64' { @{ CargoTarget = 'aarch64-pc-windows-msvc'; BinaryName = 'multi-pwsh.exe' } }
        'linux-x64' { @{ CargoTarget = 'x86_64-unknown-linux-gnu'; BinaryName = 'multi-pwsh' } }
        'linux-arm64' { @{ CargoTarget = 'aarch64-unknown-linux-gnu'; BinaryName = 'multi-pwsh' } }
        'osx-x64' { @{ CargoTarget = 'x86_64-apple-darwin'; BinaryName = 'multi-pwsh' } }
        'osx-arm64' { @{ CargoTarget = 'aarch64-apple-darwin'; BinaryName = 'multi-pwsh' } }
        default { throw "Unsupported runtime identifier: $RuntimeIdentifier" }
    }
}

function Resolve-AppHostFileName {
    param([Parameter(Mandatory)][string]$RuntimeIdentifier)

    if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        'pwsh.exe'
    }
    else {
        'pwsh'
    }
}

function New-AppHostManifest {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string[]]$RuntimeIdentifiers
    )

    $assets = foreach ($rid in $RuntimeIdentifiers) {
        $target = Resolve-RustTarget -RuntimeIdentifier $rid
        [ordered]@{
            runtimeIdentifier = $rid
            packageRelativePath = "runtimes/$rid/native/$($target['BinaryName'])"
            nativeFileName = $target['BinaryName']
            appHostFileName = Resolve-AppHostFileName -RuntimeIdentifier $rid
        }
    }

    $manifest = [ordered]@{
        packageId = $PackageId
        packageVersion = $PackageVersion
        supportedRuntimeIdentifiers = $RuntimeIdentifiers
        requiredPayloadFiles = @('pwsh.dll', 'pwsh.runtimeconfig.json')
        requiredAdjacentPayloadFiles = @('pwsh.dll', 'pwsh.runtimeconfig.json')
        supportedPayloadLayouts = @('adjacent', 'runtimeNativeSharedPayload')
        notes = 'This package supplies only the native PowerShell apphost executable. Consumers must provide their PowerShell managed payload either beside the executable or at the shared root above runtimes/<rid>/native.'
        assets = @($assets)
    }

    $manifestDirectory = Split-Path -Path $Path -Parent
    New-Item -Path $manifestDirectory -ItemType Directory -Force | Out-Null
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $Path -Encoding utf8
}

function Resolve-PackageProject {
    param([Parameter(Mandatory)][string]$Package)

    switch ($Package) {
        'Cli' {
            @{
                Id = 'Devolutions.MultiPwsh.Cli'
                Project = Join-Path $repoRoot 'nuget\Devolutions.MultiPwsh.Cli\Devolutions.MultiPwsh.Cli.csproj'
                FixedEntries = @(
                    'build/Devolutions.MultiPwsh.Cli.targets',
                    'build/Devolutions.MultiPwsh.Cli.AppHostManifest.json',
                    'buildTransitive/Devolutions.MultiPwsh.Cli.props',
                    'buildTransitive/Devolutions.MultiPwsh.Cli.targets',
                    'README.md'
                )
            }
        }
        default { throw "Unsupported package: $Package" }
    }
}

function Assert-FileExists {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        throw "Expected file was not found: $Path"
    }
}

function Set-NupkgUnixExecutablePermissions {
    param([Parameter(Mandatory)][string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::Open($PackagePath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '^runtimes/(linux|osx)-[^/]+/native/multi-pwsh$') {
                $entry.ExternalAttributes = -2115174400 # 0o100755 << 16 as a signed Int32.
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-NupkgContents {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][hashtable]$PackageInfo,
        [Parameter(Mandatory)][string]$PackageVersion,
        [Parameter(Mandatory)][string[]]$ExpectedRuntimeIdentifiers
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $expectedEntries = New-Object System.Collections.Generic.List[string]
    foreach ($rid in $ExpectedRuntimeIdentifiers) {
        $target = Resolve-RustTarget -RuntimeIdentifier $rid
        $expectedEntries.Add("runtimes/$rid/native/$($target['BinaryName'])")
    }

    foreach ($entry in $PackageInfo['FixedEntries']) {
        $expectedEntries.Add($entry)
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $actualEntries = @{}
        foreach ($entry in $archive.Entries) {
            $actualEntries[$entry.FullName] = $true
        }

        foreach ($entry in $expectedEntries) {
            if (-not $actualEntries.ContainsKey($entry)) {
                throw "Expected package entry '$entry' was not found in $PackagePath"
            }
        }

        $manifestEntryName = 'build/Devolutions.MultiPwsh.Cli.AppHostManifest.json'
        $manifestEntry = $archive.GetEntry($manifestEntryName)
        if ($null -eq $manifestEntry) {
            throw "Expected package manifest '$manifestEntryName' was not found in $PackagePath"
        }

        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try {
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }

        if ($manifest.packageId -ne $PackageInfo['Id']) {
            throw "Package manifest packageId mismatch: expected '$($PackageInfo['Id'])', got '$($manifest.packageId)'"
        }

        if ($manifest.packageVersion -ne $PackageVersion) {
            throw "Package manifest packageVersion mismatch: expected '$PackageVersion', got '$($manifest.packageVersion)'"
        }

        $manifestRids = @($manifest.supportedRuntimeIdentifiers)
        $manifestAssets = @($manifest.assets)
        foreach ($rid in $ExpectedRuntimeIdentifiers) {
            if ($manifestRids -notcontains $rid) {
                throw "Package manifest is missing supported RID '$rid'"
            }

            $target = Resolve-RustTarget -RuntimeIdentifier $rid
            $asset = $manifestAssets | Where-Object { $_.runtimeIdentifier -eq $rid } | Select-Object -First 1
            if ($null -eq $asset) {
                throw "Package manifest is missing an asset for RID '$rid'"
            }

            $expectedPackageRelativePath = "runtimes/$rid/native/$($target['BinaryName'])"
            if ($asset.packageRelativePath -ne $expectedPackageRelativePath) {
                throw "Package manifest asset path mismatch for '$rid': expected '$expectedPackageRelativePath', got '$($asset.packageRelativePath)'"
            }

            if ($asset.nativeFileName -ne $target['BinaryName']) {
                throw "Package manifest native file mismatch for '$rid': expected '$($target['BinaryName'])', got '$($asset.nativeFileName)'"
            }

            $expectedAppHostFileName = Resolve-AppHostFileName -RuntimeIdentifier $rid
            if ($asset.appHostFileName -ne $expectedAppHostFileName) {
                throw "Package manifest apphost file mismatch for '$rid': expected '$expectedAppHostFileName', got '$($asset.appHostFileName)'"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-SourceVersion
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Package version is empty.'
}

if ($Clean) {
    Remove-Item -Path $StagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -Path $StagingRoot -ItemType Directory -Force | Out-Null
New-Item -Path $OutputRoot -ItemType Directory -Force | Out-Null

$cargoManifest = Join-Path $repoRoot 'crates\multi-pwsh\Cargo.toml'

foreach ($rid in $RuntimeIdentifiers) {
    $target = Resolve-RustTarget -RuntimeIdentifier $rid
    $stageDir = Join-Path $StagingRoot $rid
    New-Item -Path $stageDir -ItemType Directory -Force | Out-Null

    if (-not $NoBuild) {
        $env:CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER = 'aarch64-linux-gnu-gcc'
        $previousRustFlags = $env:RUSTFLAGS
        try {
            if ($target['CargoTarget'] -like '*-windows-msvc') {
                $env:RUSTFLAGS = '-C target-feature=+crt-static'
            }

            Invoke-NativeCommand -FilePath cargo -ArgumentList @(
                'build',
                '--locked',
                '--release',
                '--package',
                'multi-pwsh',
                '--bin',
                'multi-pwsh',
                '--manifest-path',
                $cargoManifest,
                '--target',
                $target['CargoTarget']
            )
        }
        finally {
            $env:RUSTFLAGS = $previousRustFlags
        }

        $builtBinary = Join-Path $repoRoot "target\$($target['CargoTarget'])\release\$($target['BinaryName'])"
        Assert-FileExists -Path $builtBinary
        Copy-Item -Path $builtBinary -Destination (Join-Path $stageDir $target['BinaryName']) -Force
    }

    Assert-FileExists -Path (Join-Path $stageDir $target['BinaryName'])
}

if (-not $NoPack) {
    foreach ($package in $Packages) {
        $packageInfo = Resolve-PackageProject -Package $package
        New-AppHostManifest -Path (Join-Path $StagingRoot 'apphost-manifest.json') -PackageId $packageInfo['Id'] -PackageVersion $Version -RuntimeIdentifiers $RuntimeIdentifiers

        Invoke-NativeCommand -FilePath dotnet -ArgumentList @(
            'pack',
            $packageInfo['Project'],
            '-c',
            $Configuration,
            '-o',
            $OutputRoot,
            "/p:MultiPwshCliStagingRoot=$StagingRoot",
            "/p:Version=$Version",
            '/p:ContinuousIntegrationBuild=true'
        )

        $packagePath = Join-Path $OutputRoot "$($packageInfo['Id']).$Version.nupkg"
        Assert-FileExists -Path $packagePath
        Set-NupkgUnixExecutablePermissions -PackagePath $packagePath
        Assert-NupkgContents -PackagePath $packagePath -PackageInfo $packageInfo -PackageVersion $Version -ExpectedRuntimeIdentifiers $RuntimeIdentifiers
    }
}
