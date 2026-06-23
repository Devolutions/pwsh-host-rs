[CmdletBinding()]
param(
    [string]$PackageSource,

    [string]$PackageVersion,

    [string]$RuntimeIdentifier,

    [string]$Configuration = 'Debug',

    [string]$PowerShellPayloadPath = $env:PwshExePath,

    [switch]$SkipRuntimeSmoke,

    [switch]$KeepWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageId = 'Devolutions.MultiPwsh.Cli'
if ([string]::IsNullOrWhiteSpace($PackageSource)) {
    $PackageSource = Join-Path $repoRoot 'artifacts\native-nuget'
}
elseif (-not [System.IO.Path]::IsPathRooted($PackageSource)) {
    $PackageSource = Join-Path $repoRoot $PackageSource
}

function Invoke-CheckedCommand {
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

function Get-CurrentRuntimeIdentifier {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    $archName = switch ($arch) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        'X86' { 'x86' }
        default { throw "Unsupported process architecture for AppHost smoke test: $arch" }
    }

    if ($IsWindows) {
        "win-$archName"
    }
    elseif ($IsMacOS) {
        "osx-$archName"
    }
    else {
        "linux-$archName"
    }
}

function Assert-FileExists {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        throw "Expected file was not found: $Path"
    }
}

function Assert-UnixExecutable {
    param([Parameter(Mandatory)][string]$Path)

    if ($IsWindows) {
        return
    }

    & /usr/bin/env sh -c 'test -x "$1"' sh $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Expected file to be executable: $Path"
    }
}

function Resolve-PowerShellPayloadDirectory {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $command = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $command) {
            throw 'PowerShell payload path was not provided and pwsh could not be found.'
        }

        $Path = $command.Source
    }

    $resolved = (Resolve-Path $Path).Path
    if (Test-Path -Path $resolved -PathType Leaf) {
        Split-Path -Path $resolved -Parent
    }
    else {
        $resolved
    }
}

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $RuntimeIdentifier = Get-CurrentRuntimeIdentifier
}

$packageSource = (Resolve-Path $PackageSource).Path
$package = if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    Get-ChildItem -Path $packageSource -Filter "$packageId.*.nupkg" |
        Sort-Object Name -Descending |
        Select-Object -First 1
}
else {
    Get-ChildItem -Path $packageSource -Filter "$packageId.$PackageVersion.nupkg" |
        Select-Object -First 1
}

if ($null -eq $package) {
    throw "$packageId package was not found in $packageSource"
}

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = $package.BaseName.Substring("$packageId.".Length)
}

$workspace = Join-Path ([System.IO.Path]::GetTempPath()) "multi-pwsh-apphost-package-smoke-$([guid]::NewGuid().ToString('N'))"
$nugetCache = Join-Path $workspace 'nuget-cache'
$projectDir = Join-Path $workspace 'sample'
$env:NUGET_PACKAGES = $nugetCache

try {
    New-Item -Path $projectDir -ItemType Directory -Force | Out-Null
    New-Item -Path $nugetCache -ItemType Directory -Force | Out-Null

    $outputName = if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        'pwsh.exe'
    }
    else {
        'pwsh'
    }

    $projectPath = Join-Path $projectDir 'AppHostPackageSmoke.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>$RuntimeIdentifier</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <MultiPwshAppHostEnabled>true</MultiPwshAppHostEnabled>
    <MultiPwshAppHostOutputName>$outputName</MultiPwshAppHostOutputName>
    <MultiPwshRuntimeNativeContentCopyEnabled>false</MultiPwshRuntimeNativeContentCopyEnabled>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="$packageId" Version="$PackageVersion" PrivateAssets="all" />
  </ItemGroup>

  <Target Name="ValidateResolvedAppHostMetadata" DependsOnTargets="ResolveMultiPwshAppHostAssets" AfterTargets="ResolveMultiPwshAppHost">
    <ItemGroup>
      <_CurrentRidAppHostAsset Include="@(MultiPwshAppHostAsset)" Condition="'%(MultiPwshAppHostAsset.RuntimeIdentifier)' == '$RuntimeIdentifier'" />
    </ItemGroup>

    <Error Condition="'`$(MultiPwshAppHostSupportedRuntimeIdentifiers)' == ''" Text="MultiPwshAppHostSupportedRuntimeIdentifiers was not set." />
    <Error Condition="'`$(MultiPwshAppHostManifestPath)' == '' Or !Exists('`$(MultiPwshAppHostManifestPath)')" Text="MultiPwshAppHostManifestPath was not set to an existing manifest." />
    <Error Condition="'@(_CurrentRidAppHostAsset)' == ''" Text="MultiPwshAppHostAsset was not emitted for $RuntimeIdentifier." />
    <Error Condition="'@(_CurrentRidAppHostAsset)' != '' And '%(_CurrentRidAppHostAsset.AppHostFileName)' != '$outputName'" Text="MultiPwshAppHostAsset AppHostFileName metadata was not '$outputName'." />
    <Error Condition="'@(_CurrentRidAppHostAsset)' != '' And '%(_CurrentRidAppHostAsset.PackageRelativePath)' == ''" Text="MultiPwshAppHostAsset PackageRelativePath metadata was not set." />
    <Error Condition="'`$(MultiPwshAppHostResolvedNativeBinary)' == ''" Text="MultiPwshAppHostResolvedNativeBinary was not set." />
    <Error Condition="'@(MultiPwshAppHostNativeBinary)' == ''" Text="MultiPwshAppHostNativeBinary item was not set." />
  </Target>
</Project>
"@ | Set-Content -Path $projectPath -Encoding utf8

    $nugetConfig = Join-Path $projectDir 'NuGet.Config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packageSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $nugetConfig -Encoding utf8

    $inertProjectDir = Join-Path $workspace 'inert-sample'
    New-Item -Path $inertProjectDir -ItemType Directory -Force | Out-Null
    $inertProjectPath = Join-Path $inertProjectDir 'AppHostPackageInertSmoke.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>$RuntimeIdentifier</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <MultiPwshRuntimeNativeContentCopyEnabled>false</MultiPwshRuntimeNativeContentCopyEnabled>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="$packageId" Version="$PackageVersion" PrivateAssets="all" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path $inertProjectPath -Encoding utf8

    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('restore', $inertProjectPath, '--configfile', $nugetConfig)
    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('build', $inertProjectPath, '--no-restore', '-c', $Configuration)

    $inertOutputDir = Join-Path $inertProjectDir "bin\$Configuration\net8.0\$RuntimeIdentifier"
    foreach ($unexpectedName in @('multi-pwsh.exe', 'multi-pwsh', 'pwsh.exe', 'pwsh')) {
        $unexpectedPath = Join-Path $inertOutputDir $unexpectedName
        if (Test-Path -Path $unexpectedPath -PathType Leaf) {
            throw "AppHost targets should be inert by default, but found unexpected output: $unexpectedPath"
        }
    }

    $runtimeNativeName = if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
        'multi-pwsh.exe'
    }
    else {
        'multi-pwsh'
    }
    $unexpectedRuntimeNativePath = Join-Path $inertOutputDir "runtimes\$RuntimeIdentifier\native\$runtimeNativeName"
    if (Test-Path -Path $unexpectedRuntimeNativePath -PathType Leaf) {
        throw "Runtime native content copy should be disabled, but found unexpected output: $unexpectedRuntimeNativePath"
    }

    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('restore', $projectPath, '--configfile', $nugetConfig)
    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('build', $projectPath, '--no-restore', '-c', $Configuration)
    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('publish', $projectPath, '--no-restore', '-c', $Configuration)

    $buildOutput = Join-Path $projectDir "bin\$Configuration\net8.0\$RuntimeIdentifier\$outputName"
    $publishOutput = Join-Path $projectDir "bin\$Configuration\net8.0\$RuntimeIdentifier\publish\$outputName"
    Assert-FileExists -Path $buildOutput
    Assert-FileExists -Path $publishOutput
    Assert-UnixExecutable -Path $buildOutput
    Assert-UnixExecutable -Path $publishOutput

    if (-not $SkipRuntimeSmoke) {
        $payloadDir = Resolve-PowerShellPayloadDirectory -Path $PowerShellPayloadPath
        Assert-FileExists -Path (Join-Path $payloadDir 'pwsh.dll')
        Assert-FileExists -Path (Join-Path $payloadDir 'pwsh.runtimeconfig.json')

        $payloadCopy = Join-Path $workspace 'payload'
        Copy-Item -Path $payloadDir -Destination $payloadCopy -Recurse -Force
        Copy-Item -Path $buildOutput -Destination (Join-Path $payloadCopy $outputName) -Force
        Assert-UnixExecutable -Path (Join-Path $payloadCopy $outputName)

        Push-Location ([System.IO.Path]::GetTempPath())
        try {
            Invoke-CheckedCommand -FilePath (Join-Path $payloadCopy $outputName) -ArgumentList @(
                '-NoLogo',
                '-NoProfile',
                '-Command',
                '$PSVersionTable.PSVersion.ToString()'
            )
        }
        finally {
            Pop-Location
        }
    }
}
finally {
    if ($KeepWorkspace) {
        Write-Host "Kept smoke workspace: $workspace"
    }
    else {
        Remove-Item -Path $workspace -Recurse -Force -ErrorAction SilentlyContinue
    }
}
