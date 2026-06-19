[CmdletBinding()]
param(
    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$StagingRoot,

    [string]$OutputRoot,

    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    [ValidateSet('Cli', 'AppHost')]
    [string[]]$Packages = @('Cli', 'AppHost'),

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

function Resolve-PackageProject {
    param([Parameter(Mandatory)][string]$Package)

    switch ($Package) {
        'Cli' {
            @{
                Id = 'Devolutions.MultiPwsh.Cli'
                Project = Join-Path $repoRoot 'nuget\Devolutions.MultiPwsh.Cli\Devolutions.MultiPwsh.Cli.csproj'
                FixedEntries = @('build/Devolutions.MultiPwsh.Cli.targets')
            }
        }
        'AppHost' {
            @{
                Id = 'Devolutions.MultiPwsh.AppHost'
                Project = Join-Path $repoRoot 'nuget\Devolutions.MultiPwsh.AppHost\Devolutions.MultiPwsh.AppHost.csproj'
                FixedEntries = @(
                    'buildTransitive/Devolutions.MultiPwsh.AppHost.props',
                    'buildTransitive/Devolutions.MultiPwsh.AppHost.targets',
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
        Invoke-NativeCommand -FilePath dotnet -ArgumentList @(
            'pack',
            $packageInfo['Project'],
            '-c',
            $Configuration,
            '-o',
            $OutputRoot,
            "/p:Version=$Version",
            '/p:ContinuousIntegrationBuild=true'
        )

        $packagePath = Join-Path $OutputRoot "$($packageInfo['Id']).$Version.nupkg"
        Assert-FileExists -Path $packagePath
        Set-NupkgUnixExecutablePermissions -PackagePath $packagePath
        Assert-NupkgContents -PackagePath $packagePath -PackageInfo $packageInfo -ExpectedRuntimeIdentifiers $RuntimeIdentifiers
    }
}
