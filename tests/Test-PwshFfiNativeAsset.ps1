[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageSource,

    [string]$PackageVersion,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string]$RuntimeIdentifier,

    [string]$Configuration = 'Release',

    [switch]$KeepWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageId = 'Devolutions.MultiPwsh.Sdk'
$nativeAssets = @{
    'win-x64' = 'multi-pwsh-sdk.dll'
    'linux-x64' = 'libmulti-pwsh-sdk.so'
    'osx-x64' = 'libmulti-pwsh-sdk.dylib'
    'osx-arm64' = 'libmulti-pwsh-sdk.dylib'
}

function Get-HostRuntimeIdentifier {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    if ($IsWindows -and $architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
        return 'win-x64'
    }
    if ($IsLinux -and $architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
        return 'linux-x64'
    }
    if ($IsMacOS -and $architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
        return 'osx-x64'
    }
    if ($IsMacOS -and $architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
        return 'osx-arm64'
    }

    throw "No native SDK ABI smoke is defined for $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription) / $architecture."
}

function Get-MultiPwshVersion {
    $manifestPath = Join-Path $repoRoot 'crates\multi-pwsh\Cargo.toml'
    $match = Select-String -Path $manifestPath -Pattern '^version = "([^"]+)"$' | Select-Object -First 1
    if ($null -eq $match) {
        throw "Unable to read the multi-pwsh version from $manifestPath"
    }

    return $match.Matches[0].Groups[1].Value
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList
    )

    Write-Host ">> $FilePath $($ArgumentList -join ' ')"
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($ArgumentList -join ' ')"
    }
}

if ($RuntimeIdentifier -ne (Get-HostRuntimeIdentifier)) {
    throw "Requested RID $RuntimeIdentifier does not match the executing host $(Get-HostRuntimeIdentifier)."
}

if (-not [System.IO.Path]::IsPathRooted($PackageSource)) {
    $PackageSource = Join-Path $repoRoot $PackageSource
}
$packageSource = (Resolve-Path $PackageSource).Path

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = Get-MultiPwshVersion
}

$workspace = Join-Path ([System.IO.Path]::GetTempPath()) "multi-pwsh-sdk-native-asset-smoke-$([guid]::NewGuid().ToString('N'))"
$nugetCache = Join-Path $workspace 'nuget-cache'
$oldNugetPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $nugetCache

try {
    New-Item -Path $workspace, $nugetCache -ItemType Directory -Force | Out-Null

    $nugetConfig = Join-Path $workspace 'NuGet.Config'
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

    $consumerDirectory = Join-Path $workspace 'consumer'
    New-Item -Path $consumerDirectory -ItemType Directory -Force | Out-Null
    $consumerProject = Join-Path $consumerDirectory 'FfiNativeAssetSmoke.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>{0}</RuntimeIdentifier>
    <DevolutionsMultiPwshSdkEnabled>true</DevolutionsMultiPwshSdkEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Devolutions.MultiPwsh.Sdk" Version="{1}" />
  </ItemGroup>
</Project>
'@ -f $RuntimeIdentifier, $PackageVersion | Set-Content -Path $consumerProject -Encoding utf8

    @'
using System;
using System.IO;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal struct AbiInfo
{
    public uint Size;
    public uint AbiVersion;
    public ulong FeatureFlags;
    public uint MinimumCompatibleAbiVersion;
    public uint Reserved;
}

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetAbiInfoDelegate(ref AbiInfo abiInfo);

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Expected the staged native library filename.");
            return 2;
        }

        string libraryPath = Path.Combine(AppContext.BaseDirectory, args[0]);
        if (!File.Exists(libraryPath))
        {
            Console.Error.WriteLine($"The staged native library was not found: {libraryPath}");
            return 3;
        }

        nint library = NativeLibrary.Load(libraryPath);
        try
        {
            nint export = NativeLibrary.GetExport(library, "dps_pwsh_get_abi_info");
            GetAbiInfoDelegate getAbiInfo = Marshal.GetDelegateForFunctionPointer<GetAbiInfoDelegate>(export);
            var abiInfo = new AbiInfo { Size = (uint)Marshal.SizeOf<AbiInfo>() };
            int status = getAbiInfo(ref abiInfo);
            if (status != 0)
            {
                Console.Error.WriteLine($"dps_pwsh_get_abi_info returned {status}.");
                return 4;
            }

            if (abiInfo.AbiVersion != 2 || abiInfo.MinimumCompatibleAbiVersion != 2 || abiInfo.FeatureFlags == 0)
            {
                Console.Error.WriteLine(
                    $"Unexpected FFI ABI metadata: version={abiInfo.AbiVersion}, minimum={abiInfo.MinimumCompatibleAbiVersion}, features={abiInfo.FeatureFlags}.");
                return 5;
            }

            Console.WriteLine(
                $"Loaded {args[0]}: ABI {abiInfo.AbiVersion}, features 0x{abiInfo.FeatureFlags:X}.");
            return 0;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
'@ | Set-Content -Path (Join-Path $consumerDirectory 'Program.cs') -Encoding utf8

    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('restore', $consumerProject, '--configfile', $nugetConfig)
    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('build', $consumerProject, '--no-restore', '-c', $Configuration)

    $outputDirectory = Join-Path $consumerDirectory "bin\$Configuration\net8.0\$RuntimeIdentifier"
    $nativeAsset = Join-Path $outputDirectory $nativeAssets[$RuntimeIdentifier]
    if (-not (Test-Path -Path $nativeAsset -PathType Leaf)) {
        throw "The package did not stage the expected native asset: $nativeAsset"
    }

    $consumerAssembly = Join-Path $outputDirectory 'FfiNativeAssetSmoke.dll'
    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @($consumerAssembly, $nativeAssets[$RuntimeIdentifier])
}
finally {
    if ($null -eq $oldNugetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $oldNugetPackages
    }

    if ($KeepWorkspace) {
        Write-Host "Kept smoke workspace: $workspace"
    }
    else {
        Remove-Item -Path $workspace -Recurse -Force -ErrorAction SilentlyContinue
    }
}
