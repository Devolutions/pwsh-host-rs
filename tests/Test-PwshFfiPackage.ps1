[CmdletBinding()]
param(
    [string]$PackageSource,

    [string]$PackageVersion,

    [string]$PowerShellPayloadDirectory = $env:PWSH_FFI_PAYLOAD,

    [string]$Configuration = 'Release',

    [switch]$KeepWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'The FFI package smoke test currently requires a Windows x64 host.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageId = 'Devolutions.MultiPwsh.Sdk'

function Get-MultiPwshVersion {
    $manifestPath = Join-Path $repoRoot 'crates\multi-pwsh\Cargo.toml'
    $match = Select-String -Path $manifestPath -Pattern '^version = "([^"]+)"$' | Select-Object -First 1
    if ($null -eq $match) {
        throw "Unable to read the multi-pwsh version from $manifestPath"
    }

    return $match.Matches[0].Groups[1].Value
}

$multiPwshVersion = Get-MultiPwshVersion
if ([string]::IsNullOrWhiteSpace($PackageSource)) {
    $PackageSource = Join-Path $repoRoot 'artifacts\sdk-nuget'
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

function Resolve-PowerShellPayloadDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = (Resolve-Path $Path).Path
    if (-not (Test-Path (Join-Path $resolved 'pwsh.dll') -PathType Leaf) -or
        -not (Test-Path (Join-Path $resolved 'pwsh.runtimeconfig.json') -PathType Leaf)) {
        throw "The PowerShell payload is missing pwsh.dll or pwsh.runtimeconfig.json: $resolved"
    }

    return $resolved
}

function New-PowerShellPayloadManifest {
    param(
        [Parameter(Mandatory)][string]$PayloadDirectory,
        [Parameter(Mandatory)][string]$ManifestPath
    )

    $runtimeConfig = Get-Content -Path (Join-Path $PayloadDirectory 'pwsh.runtimeconfig.json') -Raw | ConvertFrom-Json
    $dotnetFramework = @($runtimeConfig.runtimeOptions.includedFrameworks |
        Where-Object { $_.name -eq 'Microsoft.NETCore.App' } |
        Select-Object -First 1)
    if ($dotnetFramework.Count -ne 1 -or [string]::IsNullOrWhiteSpace($dotnetFramework[0].version)) {
        throw 'The PowerShell runtimeconfig does not identify Microsoft.NETCore.App.'
    }

    $hostfxrProductVersion = (Get-Item (Join-Path $PayloadDirectory 'hostfxr.dll')).VersionInfo.ProductVersion
    $hostfxrVersion = ($hostfxrProductVersion -split '\s+')[0]
    if ([string]::IsNullOrWhiteSpace($hostfxrVersion)) {
        throw 'hostfxr.dll does not have a readable product version.'
    }

    $powerShellVersion = & (Join-Path $PayloadDirectory 'pwsh.exe') -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($powerShellVersion)) {
        throw 'Unable to read the PowerShell payload version.'
    }

    $requiredFiles = @(
        'pwsh.dll',
        'pwsh.runtimeconfig.json',
        'pwsh.deps.json',
        'System.Management.Automation.dll',
        'hostfxr.dll',
        'coreclr.dll')
    foreach ($relativePath in $requiredFiles) {
        $fullPath = Join-Path $PayloadDirectory $relativePath
        if (-not (Test-Path $fullPath -PathType Leaf)) {
            throw "The PowerShell payload is missing required manifest file: $relativePath"
        }
    }

    $files = @(
        Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -Force | ForEach-Object {
            if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "The PowerShell payload contains a symlink/reparse point that this generator does not permit: $($_.FullName)"
            }
            if (-not $_.PSIsContainer) {
                $relativePath = [IO.Path]::GetRelativePath($PayloadDirectory, $_.FullName) -replace '\\', '/'
                [ordered]@{
                    path = $relativePath
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
        } | Sort-Object { $_.path }
    )
    $fileHashes = @{}
    foreach ($file in $files) {
        $fileHashes[$file.path] = $file.sha256
    }
    $moduleIdentities = @(
        foreach ($name in @('Microsoft.PowerShell.Security', 'Microsoft.PowerShell.Utility')) {
            $relativePath = "Modules/$name/$name.psd1"
            $fullPath = Join-Path $PayloadDirectory ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path $fullPath -PathType Leaf)) {
                throw "The PowerShell payload is missing required module manifest: $relativePath"
            }
            $moduleManifest = Import-PowerShellDataFile -Path $fullPath
            if ($null -eq $moduleManifest.ModuleVersion) {
                throw "The PowerShell module manifest does not define ModuleVersion: $relativePath"
            }
            $sha256 = $fileHashes[$relativePath]
            if ([string]::IsNullOrWhiteSpace($sha256)) {
                throw "The PowerShell module manifest is not included in the complete payload file list: $relativePath"
            }
            [ordered]@{
                name = $name
                manifestPath = $relativePath
                version = $moduleManifest.ModuleVersion.ToString()
                sha256 = $sha256
            }
        }
    )

    $manifest = [ordered]@{
        schema = 'devolutions-pwsh-payload'
        schemaVersion = 1
        payload = [ordered]@{
            id = 'PowerShell'
            version = $powerShellVersion.Trim()
        }
        target = [ordered]@{
            rid = 'win-x64'
            architecture = 'x64'
        }
        runtime = [ordered]@{
            powerShellVersion = $powerShellVersion.Trim()
            dotnetVersion = $dotnetFramework[0].version
            hostfxrVersion = $hostfxrVersion
            bindingsAbiVersion = 2
            requiredBindingsFeatures = 123136
        }
        files = $files
        trust = [ordered]@{
            allowSymlinks = $false
        }
        sessionPolicy = [ordered]@{
            modulePaths = @('Modules')
            workingDirectories = @('.')
            moduleImports = @('Microsoft.PowerShell.Security', 'Microsoft.PowerShell.Utility')
            moduleIdentities = $moduleIdentities
            environmentKeys = @('DPS_FFI_TEST')
        }
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $ManifestPath -Encoding utf8
    return (Get-FileHash -Path $ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
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
if ($PackageVersion -ne $multiPwshVersion) {
    throw "$packageId version $PackageVersion must match multi-pwsh version $multiPwshVersion"
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $archivePaths = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]($archive.Entries | ForEach-Object FullName),
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($requiredPath in @(
        'README.md',
        'buildTransitive/Devolutions.MultiPwsh.Sdk.targets',
        'contentFiles/any/any/devolutions-pwsh-payload.manifest.template.json',
        'lib/net8.0/Devolutions.MultiPwsh.Sdk.dll',
        'runtimes/win-x64/native/multi-pwsh-sdk.dll')) {
        if (-not $archivePaths.Contains($requiredPath)) {
            throw "Package is missing required entry: $requiredPath"
        }
    }

    $nativeEntry = $archive.GetEntry('runtimes/win-x64/native/multi-pwsh-sdk.dll')
    if ($null -eq $nativeEntry) {
        throw 'Package native FFI asset could not be opened.'
    }

    $nativeStream = $nativeEntry.Open()
    $nativeBytes = [System.IO.MemoryStream]::new()
    try {
        $nativeStream.CopyTo($nativeBytes)
    }
    finally {
        $nativeStream.Dispose()
    }

    $nativeImports = [System.Text.Encoding]::ASCII.GetString($nativeBytes.ToArray())
    if ($nativeImports -match 'VCRUNTIME[0-9_]*\.DLL' -or $nativeImports -match 'MSVCP[0-9_]*\.DLL') {
        throw 'The packaged native FFI asset must use the static MSVC runtime.'
    }
}
finally {
    $archive.Dispose()
}

$payloadDirectory = Resolve-PowerShellPayloadDirectory -Path $PowerShellPayloadDirectory
$workspace = Join-Path (Join-Path $repoRoot 'artifacts') "ffi-package-smoke-$([guid]::NewGuid().ToString('N'))"
$nugetCache = Join-Path $workspace 'nuget-cache'
$oldNugetPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $nugetCache

try {
    New-Item -Path $nugetCache -ItemType Directory -Force | Out-Null
    $manifestPath = Join-Path $workspace 'payload-manifest.json'
    $manifestSha256 = New-PowerShellPayloadManifest -PayloadDirectory $payloadDirectory -ManifestPath $manifestPath
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

    $inertProjectDirectory = Join-Path $workspace 'inert'
    New-Item -Path $inertProjectDirectory -ItemType Directory -Force | Out-Null
    $inertProject = Join-Path $inertProjectDirectory 'Inert.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$packageId" Version="$PackageVersion" PrivateAssets="all" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path $inertProject -Encoding utf8
    'using System; Console.WriteLine("inert");' | Set-Content -Path (Join-Path $inertProjectDirectory 'Program.cs') -Encoding utf8

    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('restore', $inertProject, '--configfile', $nugetConfig)
    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('build', $inertProject, '--no-restore', '-c', $Configuration)
    $inertNativeAsset = Join-Path $inertProjectDirectory "bin\$Configuration\net8.0\win-x64\multi-pwsh-sdk.dll"
    if (Test-Path $inertNativeAsset -PathType Leaf) {
        throw "FFI native assets must be inert by default, but found $inertNativeAsset"
    }

    $consumerDirectory = Join-Path $workspace 'consumer'
    New-Item -Path $consumerDirectory -ItemType Directory -Force | Out-Null
    $consumerProject = Join-Path $consumerDirectory 'FfiPackageConsumer.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <DevolutionsMultiPwshSdkEnabled>true</DevolutionsMultiPwshSdkEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$packageId" Version="$PackageVersion" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path $consumerProject -Encoding utf8
    @"
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Devolutions.PowerShell.Ffi;

void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

void VerifyCopiedValueReaders()
{
    Guid expectedGuid = Guid.Parse("3e0a9e49-2cc4-44d0-b4ce-65fc29f5d8b1");
    Uri expectedUri = new("https://example.test/rdm");
    DateTime expectedDateTime = new(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc);
    DateTimeOffset expectedDateTimeOffset = new(2026, 7, 18, 10, 30, 0, TimeSpan.FromHours(-4));
    PowerShellValue bytes = PowerShellValue.Bytes(new byte[] { 1, 2, 3 });
    PowerShellValue value = PowerShellValue.PropertyBag(new[]
    {
        new KeyValuePair<string, PowerShellValue>("Text", PowerShellValue.String("copied-reader")),
        new KeyValuePair<string, PowerShellValue>("Switch", PowerShellValue.Switch()),
        new KeyValuePair<string, PowerShellValue>("Boolean", PowerShellValue.Boolean(true)),
        new KeyValuePair<string, PowerShellValue>("Signed", PowerShellValue.SignedInteger(-42)),
        new KeyValuePair<string, PowerShellValue>("Unsigned", PowerShellValue.UnsignedInteger(42)),
        new KeyValuePair<string, PowerShellValue>("Double", PowerShellValue.Double(1.5)),
        new KeyValuePair<string, PowerShellValue>("Decimal", PowerShellValue.Decimal(3.25m)),
        new KeyValuePair<string, PowerShellValue>("Bytes", bytes),
        new KeyValuePair<string, PowerShellValue>("DateTime", PowerShellValue.DateTime(expectedDateTime)),
        new KeyValuePair<string, PowerShellValue>("DateTimeOffset", PowerShellValue.DateTimeOffset(expectedDateTimeOffset)),
        new KeyValuePair<string, PowerShellValue>("Guid", PowerShellValue.Guid(expectedGuid)),
        new KeyValuePair<string, PowerShellValue>("Uri", PowerShellValue.Uri(expectedUri)),
        new KeyValuePair<string, PowerShellValue>(
            "Array",
            PowerShellValue.Array(new[]
            {
                PowerShellValue.SignedInteger(1),
                PowerShellValue.String("two"),
            })),
    });

    Require(
        PowerShellValue.Null.IsNull &&
        value.GetPropertyBag().Count == 13 &&
        value.TryGetProperty("text", out PowerShellValue? text) &&
        text!.TryGetString(out string? textValue) &&
        textValue == "copied-reader" &&
        value.TryGetProperty("Switch", out PowerShellValue? switchValue) &&
        switchValue!.TryGetSwitch(out bool switchPresent) &&
        switchPresent &&
        value.TryGetProperty("Boolean", out PowerShellValue? booleanValue) &&
        booleanValue!.TryGetBoolean(out bool boolean) &&
        boolean &&
        value.TryGetProperty("Signed", out PowerShellValue? signedValue) &&
        signedValue!.TryGetSignedInteger(out long signed) &&
        signed == -42 &&
        value.TryGetProperty("Unsigned", out PowerShellValue? unsignedValue) &&
        unsignedValue!.TryGetUnsignedInteger(out ulong unsigned) &&
        unsigned == 42 &&
        value.TryGetProperty("Double", out PowerShellValue? doubleValue) &&
        doubleValue!.TryGetDouble(out double doubleResult) &&
        doubleResult == 1.5 &&
        value.TryGetProperty("Decimal", out PowerShellValue? decimalValue) &&
        decimalValue!.TryGetDecimal(out decimal decimalResult) &&
        decimalResult == 3.25m &&
        value.TryGetProperty("Bytes", out PowerShellValue? bytesValue) &&
        bytesValue!.TryGetBytes(out byte[]? byteResult) &&
        byteResult!.SequenceEqual(new byte[] { 1, 2, 3 }) &&
        value.TryGetProperty("DateTime", out PowerShellValue? dateTimeValue) &&
        dateTimeValue!.TryGetDateTime(out DateTime dateTimeResult) &&
        dateTimeResult == expectedDateTime &&
        value.TryGetProperty("DateTimeOffset", out PowerShellValue? dateTimeOffsetValue) &&
        dateTimeOffsetValue!.TryGetDateTimeOffset(out DateTimeOffset dateTimeOffsetResult) &&
        dateTimeOffsetResult == expectedDateTimeOffset &&
        value.TryGetProperty("Guid", out PowerShellValue? guidValue) &&
        guidValue!.TryGetGuid(out Guid guidResult) &&
        guidResult == expectedGuid &&
        value.TryGetProperty("Uri", out PowerShellValue? uriValue) &&
        uriValue!.TryGetUri(out Uri? uriResult) &&
        uriResult == expectedUri &&
        value.TryGetProperty("Array", out PowerShellValue? arrayValue) &&
        arrayValue!.GetArray().Count == 2 &&
        arrayValue.GetArray()[0].TryGetSignedInteger(out long firstArrayValue) &&
        firstArrayValue == 1 &&
        !value.TryGetProperty("missing", out _) &&
        !value.TryGetString(out _),
        "Copied PowerShellValue readers did not preserve the documented DTO graph.");

    bool rejectedArrayRead = false;
    try
    {
        _ = value.GetArray();
    }
    catch (InvalidOperationException)
    {
        rejectedArrayRead = true;
    }
    Require(rejectedArrayRead, "Property bags must not be read as arrays.");
}

async Task VerifyCancellationAndDisposeAsync()
{
    using (var cancellation = new CancellationTokenSource())
    using (PowerShell cancellationBuilder = PowerShell.Create())
    {
        Task<PowerShellInvocationResult> invocation = cancellationBuilder
            .AddScript("1..50 | ForEach-Object { Start-Sleep -Milliseconds 100; Write-Output `$_ }")
            .InvokeAsync(cancellation.Token);
        await Task.Delay(75);
        cancellation.Cancel();
        try
        {
            await invocation;
            throw new InvalidOperationException("Cancelled InvokeAsync returned a successful partial result.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    using (PowerShell stopBuilder = PowerShell.Create())
    using (PowerShellInvocationOperation operation = stopBuilder
        .AddScript("Start-Sleep -Seconds 5; 'must-not-be-returned'")
        .BeginInvoke())
    {
        operation.Stop();
        operation.Stop();
        PowerShellInvocationOperationStatus status = operation.Wait(TimeSpan.FromSeconds(5));
        Require(
            status.State == PowerShellOperationState.Cancelled &&
            status.TerminalStatus == PowerShellFfiStatus.OperationCancelled,
            "Repeated Stop did not reach the deterministic cancelled terminal state.");
        try
        {
            operation.GetResult();
            throw new InvalidOperationException("Cancelled operation exposed a successful result.");
        }
        catch (PowerShellFfiException exception)
            when (exception.Status == PowerShellFfiStatus.OperationCancelled)
        {
        }

        operation.Dispose();
        operation.Dispose();
    }
}

async Task VerifySafeHandleDisposeRacesAsync()
{
    for (int iteration = 0; iteration < 4; iteration++)
    {
        PowerShell builder = PowerShell.Create()
            .AddScript("Start-Sleep -Milliseconds 150; 'safe-handle-lease'");
        using var enteredInvocation = new ManualResetEventSlim(false);
        Task<PowerShellInvocationResult?> invoke = Task.Run(() =>
        {
            enteredInvocation.Set();
            try
            {
                return builder.Invoke();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        });
        Task dispose = Task.Run(() =>
        {
            enteredInvocation.Wait();
            Thread.Sleep(10);
            builder.Dispose();
            builder.Dispose();
        });

        PowerShellInvocationResult? result = await invoke;
        await dispose;
        if (result is not null)
        {
            Require(
                result.Output.Records.Count == 1 &&
                result.Output.Records[0].DisplayText == "safe-handle-lease",
                "An in-flight SafeHandle lease returned an unexpected result.");
        }
    }
}

if (args.Length != 3)
{
   return 2;
}

PowerShellRuntime runtime = PowerShellRuntime.Activate(
   new PowerShellPayloadActivationOptions(args[0], args[1], args[2]));
VerifyCopiedValueReaders();
const string SecretMarker = "ffi-secret-marker-not-accepted";
Require(
   PowerShellSecretTransfer.Policy == PowerShellSecretTransferPolicy.Rejected &&
   typeof(PowerShellSecretTransfer).GetMethod(nameof(PowerShellSecretTransfer.ThrowNotSupported), BindingFlags.Public | BindingFlags.Static)!.GetParameters().Length == 0,
   "The secret-transfer rejection boundary unexpectedly accepts input.");
try
{
   PowerShellSecretTransfer.ThrowNotSupported();
   return 1;
}
catch (PowerShellSecretTransferNotSupportedException exception)
{
   Require(
       !exception.Message.Contains(SecretMarker, StringComparison.Ordinal),
       "Secret-transfer rejection leaked a caller marker into diagnostics.");
}
using (var secureString = new SecureString())
{
    foreach (char character in SecretMarker)
    {
       secureString.AppendChar(character);
    }
    secureString.MakeReadOnly();
    try
    {
       _ = PowerShellValue.From(secureString);
       return 1;
    }
    catch (PowerShellValueConversionException)
    {
    }
}
try
{
    _ = PowerShellValue.From((Action)(() => { }));
    return 1;
}
catch (PowerShellValueConversionException)
{
}
Require(
    !typeof(PowerShell).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
       .SelectMany(method => method.GetParameters())
       .Any(parameter => parameter.ParameterType == typeof(SecureString)),
    "The facade unexpectedly exposes SecureString parameter transfer.");

using PowerShell powerShell = PowerShell.Create();
PowerShellInvocationResult result = powerShell
    .AddScript("Write-Error -Message 'package-non-terminating-error'")
    .InvokeWithDiagnostics();
if (result.Errors.Records.Count != 1 || !result.Errors.Records[0].Message.Contains("package-non-terminating-error"))
{
    return 1;
}

using (PowerShell projectionBuilder = PowerShell.Create())
{
    PowerShellInvocationResult projectionResult = projectionBuilder
        .AddScript("[pscustomobject]@{ Name = 'package-projection'; Count = 2; Nested = @{ Value = 1 }; Items = 1, 2 }; Write-Error -Message 'package-projection-error' -Category InvalidOperation -TargetObject 42")
        .InvokeWithDiagnostics();
    PowerShellObjectSnapshot projection = projectionResult.Output.Records[0];
    PowerShellInvocationError projectionError = projectionResult.Errors.Records[0];
    PowerShellValue? projectionBag = projection.PropertyBag;
    PowerShellValue? projectionTarget = projectionError.TargetValue;
    Require(
        projectionResult.Output.TotalRecordCount == 1 &&
        projectionResult.Output.DroppedRecordCount == 0 &&
        projectionBag?.Kind == PowerShellValueKind.PropertyBag &&
        projection.PropertyEntryCount == 2 &&
        projection.DroppedPropertyEntryCount == 2 &&
        projection.ScalarValue is null &&
        projectionBag is not null &&
        projectionBag.TryGetProperty("Name", out PowerShellValue? projectionName) &&
        projectionName!.TryGetString(out string? projectionNameText) &&
        projectionNameText == "package-projection" &&
        projectionBag.TryGetProperty("Count", out PowerShellValue? projectionCount) &&
        projectionCount!.TryGetSignedInteger(out long projectionCountValue) &&
        projectionCountValue == 2 &&
        projectionTarget?.Kind == PowerShellValueKind.SignedInteger &&
        projectionTarget is not null &&
        projectionTarget.TryGetSignedInteger(out long projectionTargetValue) &&
        projectionTargetValue == 42 &&
        projectionResult.Errors.TotalRecordCount == 1,
        "Package consumer did not preserve bounded snapshot projections.");

    byte[] stored = PowerShellSnapshotSerializer.Serialize(projectionResult);
    Require(
        !Encoding.UTF8.GetString(stored).Contains(SecretMarker, StringComparison.Ordinal),
        "Snapshot serialization leaked a rejected secret marker.");
    PowerShellInvocationResult restored = PowerShellSnapshotSerializer.Deserialize(stored);
    PowerShellValue? restoredBag = restored.Output.Records[0].PropertyBag;
    Require(
        restoredBag?.Kind == PowerShellValueKind.PropertyBag &&
        restored.Output.Records[0].DroppedPropertyEntryCount == 2 &&
        restoredBag is not null &&
        restoredBag.TryGetProperty("Count", out PowerShellValue? restoredCount) &&
        restoredCount!.TryGetSignedInteger(out long restoredCountValue) &&
        restoredCountValue == 2,
        "Package consumer did not round-trip the bounded storage/display snapshot.");

    try
    {
        _ = PowerShellSnapshotSerializer.Deserialize(new byte[PowerShellSnapshotSerializer.MaxDocumentBytes + 1]);
        return 1;
    }
    catch (ArgumentOutOfRangeException)
    {
    }

    try
    {
        _ = PowerShellSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"version\":99,\"result\":null}"));
        return 1;
    }
    catch (ArgumentException)
    {
    }

    try
    {
        string withUnknownMember = Encoding.UTF8.GetString(stored)[..^1] + ",\"unexpected\":true}";
        _ = PowerShellSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes(withUnknownMember));
        return 1;
    }
    catch (ArgumentException)
    {
    }

    try
    {
        _ = PowerShellSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes(new string('[', 17) + new string(']', 17)));
        return 1;
    }
    catch (ArgumentException)
    {
    }
}

using (PowerShell terminating = PowerShell.Create())
{
    try
    {
        terminating.AddScript("throw 'package-terminating-error'").Invoke();
        return 1;
    }
    catch (PowerShellInvocationException exception)
    {
        Require(
            exception.Errors.Count == 1 &&
            exception.Errors[0].Message.Contains("package-terminating-error"),
            "Terminating invocation did not preserve its semantic error snapshot.");
    }
}

using (PowerShell nulBuilder = PowerShell.Create())
{
    try
    {
        nulBuilder.AddScript("Write-Output 'nul'\0");
        return 1;
    }
    catch (ArgumentException)
    {
    }
}

try
{
    _ = new PowerShellSessionConfiguration(workingDirectory: "relative-directory");
    return 1;
}
catch (ArgumentException)
{
}

try
{
    _ = runtime.CreateSession(new PowerShellSessionOptions(
        configuration: new PowerShellSessionConfiguration(allowedModulePaths: new[] { args[0] })));
    return 1;
}
catch (PowerShellFfiException exception)
    when (exception.Status == PowerShellFfiStatus.SessionPolicyViolation)
{
    Require(
        !exception.Message.Contains(args[0], StringComparison.OrdinalIgnoreCase),
        "Session-policy diagnostics leaked a supplied path.");
}

var sessionConfiguration = new PowerShellSessionConfiguration(
    initialVariables: new Dictionary<string, PowerShellValue>
    {
        ["FfiMarker"] = PowerShellValue.String("session-marker"),
        ["FfiNumber"] = PowerShellValue.SignedInteger(7),
    },
    moduleImports: new[] { "Microsoft.PowerShell.Utility" },
    allowedModulePaths: new[] { Path.Combine(args[0], "Modules") },
    workingDirectory: args[0],
    environment: new Dictionary<string, string>
    {
        ["DPS_FFI_TEST"] = "session-environment",
    });
using PowerShellSession session = runtime.CreateSession(
    new PowerShellSessionOptions(
        historyMode: PowerShellSessionHistoryMode.Enabled,
        errorPreference: PowerShellSessionPreference.Stop,
        configuration: sessionConfiguration));
using PowerShell sessionPowerShell = session.CreatePowerShell();
PowerShellInvocationResult sessionResult = sessionPowerShell
    .AddScript("@(`$FfiMarker, `$FfiNumber, `$env:DPS_FFI_TEST, (Get-Location).Path, `$ErrorActionPreference)")
    .Invoke();
using PowerShell reusedSessionPowerShell = session.CreatePowerShell();
PowerShellInvocationResult moduleResult = reusedSessionPowerShell
    .AddScript("(Get-Module Microsoft.PowerShell.Utility).Name")
    .Invoke();
PowerShellSessionSnapshot snapshot = session.GetSnapshot();
if (sessionResult.Output.Records.Count != 5 ||
    sessionResult.Output.Records[0].DisplayText != "session-marker" ||
    sessionResult.Output.Records[1].DisplayText != "7" ||
    sessionResult.Output.Records[2].DisplayText != "session-environment" ||
string.Equals(sessionResult.Output.Records[3].DisplayText, args[0], StringComparison.OrdinalIgnoreCase) ||
!File.Exists(Path.Combine(sessionResult.Output.Records[3].DisplayText, "pwsh.dll")) ||
sessionResult.Output.Records[4].DisplayText != "Stop" ||
    moduleResult.Output.Records.Count != 1 ||
    moduleResult.Output.Records[0].DisplayText != "Microsoft.PowerShell.Utility" ||
    snapshot.InvocationCount != 2 ||
    snapshot.HistoryCount != 2 ||
    session.GetEvents().Count < 3)
{
    return 1;
}

PowerShellCapabilityDefinition connectionNameDefinition = new(
    "rdm.get-connection-name",
    Array.Empty<PowerShellCapabilityArgumentSchema>(),
    new[] { PowerShellValueKind.String },
    PowerShellCapabilityPermission.Read,
    maximumInputBytes: 64,
    maximumOutputBytes: 256,
    deadline: TimeSpan.FromSeconds(5));
using (PowerShellCapabilitySet capabilities = runtime.RegisterCapabilities(new[]
{
    new PowerShellCapabilityBinding(connectionNameDefinition, new ConnectionNameCapability()),
}))
using (PowerShell capabilityPowerShell = session.CreatePowerShell())
{
    PowerShellInvocationResult capabilityResult = capabilityPowerShell
        .AddScript("`$DpsCapabilities.Invoke('rdm.get-connection-name')")
        .WithCapabilities(capabilities)
        .Invoke();
    Require(
        capabilityResult.Output.Records.Count == 1 &&
        capabilityResult.Output.Records[0].DisplayText == "nativeaot-connection",
        "The bounded capability callback did not round-trip through the payload bridge.");
}
try
{
    using PowerShellCapabilitySet duplicateCapabilities = runtime.RegisterCapabilities(new[]
    {
        new PowerShellCapabilityBinding(connectionNameDefinition, new ConnectionNameCapability()),
        new PowerShellCapabilityBinding(connectionNameDefinition, new ConnectionNameCapability()),
    });
    return 1;
}
catch (ArgumentException)
{
}
using (PowerShell unknownCapabilityPowerShell = session.CreatePowerShell())
{
    using PowerShellCapabilitySet unknownCapabilities = runtime.RegisterCapabilities(new[]
    {
        new PowerShellCapabilityBinding(connectionNameDefinition, new ConnectionNameCapability()),
    });
    try
    {
        unknownCapabilityPowerShell
            .AddScript("`$DpsCapabilities.Invoke('rdm.unknown')")
            .WithCapabilities(unknownCapabilities)
            .Invoke();
        return 1;
    }
    catch (PowerShellInvocationException)
    {
    }
}
PowerShellCapabilityDefinition failingDefinition = new(
    "rdm.fail",
    Array.Empty<PowerShellCapabilityArgumentSchema>(),
    new[] { PowerShellValueKind.String },
    PowerShellCapabilityPermission.Read,
    maximumInputBytes: 64,
    maximumOutputBytes: 256,
    deadline: TimeSpan.FromSeconds(5));
using (PowerShellCapabilitySet failingCapabilities = runtime.RegisterCapabilities(new[]
{
    new PowerShellCapabilityBinding(failingDefinition, new ThrowingCapability()),
}))
using (PowerShell failingCapabilityPowerShell = session.CreatePowerShell())
{
    try
    {
        failingCapabilityPowerShell
            .AddScript("`$DpsCapabilities.Invoke('rdm.fail')")
            .WithCapabilities(failingCapabilities)
            .Invoke();
        return 1;
    }
    catch (PowerShellInvocationException)
    {
    }
}
PowerShellCapabilityDefinition timeoutDefinition = new(
    "rdm.timeout",
    Array.Empty<PowerShellCapabilityArgumentSchema>(),
    new[] { PowerShellValueKind.String },
    PowerShellCapabilityPermission.Read,
    maximumInputBytes: 64,
    maximumOutputBytes: 256,
    deadline: TimeSpan.FromMilliseconds(10));
using (PowerShellCapabilitySet timeoutCapabilities = runtime.RegisterCapabilities(new[]
{
    new PowerShellCapabilityBinding(timeoutDefinition, new TimeoutCapability()),
}))
using (PowerShell timeoutCapabilityPowerShell = session.CreatePowerShell())
{
    try
    {
        timeoutCapabilityPowerShell
            .AddScript("`$DpsCapabilities.Invoke('rdm.timeout')")
            .WithCapabilities(timeoutCapabilities)
            .Invoke();
        return 1;
    }
    catch (PowerShellInvocationException)
    {
    }
}
PowerShellCapabilityDefinition reentryDefinition = new(
    "rdm.reentry",
    Array.Empty<PowerShellCapabilityArgumentSchema>(),
    new[] { PowerShellValueKind.String },
    PowerShellCapabilityPermission.Read,
    maximumInputBytes: 64,
    maximumOutputBytes: 256,
    deadline: TimeSpan.FromSeconds(5));
using (PowerShellCapabilitySet reentryCapabilities = runtime.RegisterCapabilities(new[]
{
    new PowerShellCapabilityBinding(reentryDefinition, new ReentryCapability()),
}))
using (PowerShell reentryCapabilityPowerShell = session.CreatePowerShell())
{
    PowerShellInvocationResult reentryResult = reentryCapabilityPowerShell
        .AddScript("`$DpsCapabilities.Invoke('rdm.reentry')")
        .WithCapabilities(reentryCapabilities)
        .Invoke();
    Require(
        reentryResult.Output.Records.Count == 1 &&
        reentryResult.Output.Records[0].DisplayText == "reentry-blocked",
        "A capability handler re-entered the FFI instead of receiving backpressure.");
}
PowerShellCapabilityDefinition cancelledDefinition = new(
    "rdm.wait-for-cancellation",
    Array.Empty<PowerShellCapabilityArgumentSchema>(),
    new[] { PowerShellValueKind.Null },
    PowerShellCapabilityPermission.Read,
    maximumInputBytes: 64,
    maximumOutputBytes: 64,
    deadline: TimeSpan.FromSeconds(5));
using var cancellableHandler = new CancellableCapability();
using (PowerShellCapabilitySet cancelledCapabilities = runtime.RegisterCapabilities(new[]
{
    new PowerShellCapabilityBinding(cancelledDefinition, cancellableHandler),
}))
using (PowerShell cancelledCapabilityPowerShell = session.CreatePowerShell())
using (PowerShellInvocationOperation cancelledCapabilityOperation = cancelledCapabilityPowerShell
    .AddScript("`$DpsCapabilities.Invoke('rdm.wait-for-cancellation')")
    .WithCapabilities(cancelledCapabilities)
    .BeginInvoke())
{
    Require(
        cancellableHandler.Started.Wait(TimeSpan.FromSeconds(5)),
        "The cancellable capability handler did not begin before the operation was stopped.");
    cancelledCapabilityOperation.Stop();
    Require(
        cancelledCapabilityOperation.Wait(TimeSpan.FromSeconds(5)).State == PowerShellOperationState.Cancelled,
        "Stopping an active capability invocation did not cancel the handler and operation.");
}

PowerShellValue copiedVariable = PowerShellValue.PropertyBag(new[]
{
    new KeyValuePair<string, PowerShellValue>("Marker", PowerShellValue.String("copied-variable")),
    new KeyValuePair<string, PowerShellValue>(
        "Items",
        PowerShellValue.Array(new[]
        {
            PowerShellValue.SignedInteger(3),
            PowerShellValue.String("four"),
        })),
});
session.SetVariable("FfiCopied", copiedVariable);
Require(
    session.TryGetVariable("FfiCopied", out PowerShellValue? copiedSnapshot) &&
    copiedSnapshot?.Kind == PowerShellValueKind.PropertyBag &&
    copiedSnapshot is not null &&
    copiedSnapshot.TryGetProperty("Marker", out PowerShellValue? copiedMarker) &&
    copiedMarker!.TryGetString(out string? copiedMarkerText) &&
    copiedMarkerText == "copied-variable" &&
    copiedSnapshot.TryGetProperty("Items", out PowerShellValue? copiedItems) &&
    copiedItems!.GetArray().Count == 2 &&
    copiedItems.GetArray()[0].TryGetSignedInteger(out long copiedFirstItem) &&
    copiedFirstItem == 3,
    "Session variable storage did not return a copied property-bag snapshot.");
using (PowerShell copiedVariableBuilder = session.CreatePowerShell())
{
    PowerShellInvocationResult copiedVariableResult = copiedVariableBuilder
        .AddScript("@(`$FfiCopied.Marker, `$FfiCopied.Items[0], `$FfiCopied.Items[1])")
        .Invoke();
    Require(
        copiedVariableResult.Output.Records.Count == 3 &&
        copiedVariableResult.Output.Records[0].DisplayText == "copied-variable" &&
        copiedVariableResult.Output.Records[1].DisplayText == "3" &&
        copiedVariableResult.Output.Records[2].DisplayText == "four",
        "Copied session variables were not retained as tagged PowerShell values.");
}
session.SetVariable(
    "FfiResult",
    PowerShellValue.PropertyBag(
    [
        new KeyValuePair<string, PowerShellValue>("Status", PowerShellValue.String("pending")),
    ]));
using (PowerShell resultVariableBuilder = session.CreatePowerShell())
{
    _ = resultVariableBuilder
        .AddScript("`$FfiResult = [pscustomobject]@{ Status = 'completed'; Count = 2 }")
        .Invoke();
}
Require(
    session.TryGetVariable("FfiResult", out PowerShellValue? resultSnapshot) &&
    resultSnapshot?.Kind == PowerShellValueKind.PropertyBag &&
    resultSnapshot is not null &&
    resultSnapshot.TryGetProperty("Status", out PowerShellValue? resultStatus) &&
    resultStatus!.TryGetString(out string? resultStatusText) &&
    resultStatusText == "completed" &&
    resultSnapshot.TryGetProperty("Count", out PowerShellValue? resultCount) &&
    resultCount!.TryGetSignedInteger(out long resultCountValue) &&
    resultCountValue == 2,
    "A value-only script result did not return as a copied session-variable DTO.");
Require(
    session.RemoveVariable("FfiCopied") &&
    !session.RemoveVariable("FfiCopied") &&
    !session.TryGetVariable("FfiCopied", out _),
    "Session variable removal or absence reporting is incorrect.");

using (PowerShell unsupportedVariableBuilder = session.CreatePowerShell())
{
    unsupportedVariableBuilder
        .AddScript("`$FfiUnsupported = [System.Collections.ArrayList]::new(); `$null")
        .Invoke();
}
try
{
    _ = session.TryGetVariable("FfiUnsupported", out _);
    return 1;
}
catch (PowerShellFfiException exception) when (exception.Status == PowerShellFfiStatus.UnsupportedValue)
{
}
Require(session.RemoveVariable("FfiUnsupported"), "An unsupported session variable could not be removed.");

using (PowerShell pendingVariableBuilder = session.CreatePowerShell())
using (PowerShellInvocationOperation pendingVariableOperation = pendingVariableBuilder
    .AddScript("Start-Sleep -Seconds 5")
    .BeginInvoke())
{
    try
    {
        session.SetVariable("FfiBlocked", PowerShellValue.String("must-not-mutate"));
        return 1;
    }
    catch (PowerShellFfiException exception) when (exception.Status == PowerShellFfiStatus.Backpressure)
    {
    }

    pendingVariableOperation.Stop();
    Require(
        pendingVariableOperation.Wait(TimeSpan.FromSeconds(5)).State == PowerShellOperationState.Cancelled,
        "Stopping a session operation did not release the variable-mutation gate.");
}

var cyclicValue = new List<object?>();
cyclicValue.Add(cyclicValue);
try
{
    _ = PowerShellValue.From(cyclicValue);
    return 1;
}
catch (ArgumentException)
{
}

object? nestedValue = "leaf";
for (int depth = 0; depth <= 8; depth++)
{
    nestedValue = new object?[] { nestedValue };
}
try
{
    _ = PowerShellValue.From(nestedValue);
    return 1;
}
catch (ArgumentException)
{
}

using (PowerShellSession restrictedSession = runtime.CreateSession(
    new PowerShellSessionOptions(
        configuration: new PowerShellSessionConfiguration(
            moduleImports: new[] { "Microsoft.PowerShell.Security" },
            allowedModulePaths: new[] { Path.Combine(args[0], "Modules") },
            executionPolicy: PowerShellSessionExecutionPolicy.Restricted))))
using (PowerShell restrictedPowerShell = restrictedSession.CreatePowerShell())
{
    PowerShellInvocationResult restrictedResult = restrictedPowerShell.AddCommand("Get-ExecutionPolicy").Invoke();
    Require(
        restrictedResult.Output.Records.Count == 1 &&
        restrictedResult.Output.Records[0].DisplayText == "Restricted",
        "The restricted execution-policy subset was not applied.");
    string unapprovedScript = Path.Combine(Path.GetTempPath(), $"devolutions-pwsh-ffi-{Guid.NewGuid():N}.ps1");
    File.WriteAllText(unapprovedScript, "'unapproved external script'");
    try
    {
        using PowerShell unapprovedScriptPowerShell = restrictedSession.CreatePowerShell();
        bool unapprovedScriptRejected;
        try
        {
            PowerShellInvocationResult unapprovedResult = unapprovedScriptPowerShell
                .AddScript($"& '{unapprovedScript.Replace("'", "''", StringComparison.Ordinal)}'")
                .Invoke();
            unapprovedScriptRejected = unapprovedResult.HadErrors;
        }
        catch (PowerShellInvocationException)
        {
            unapprovedScriptRejected = true;
        }
        Require(
            unapprovedScriptRejected,
            "The restricted session ran an external script outside its approved staged module roots.");
    }
    finally
    {
        File.Delete(unapprovedScript);
    }
}

PowerShellSession leasedSession = runtime.CreateSession(
    new PowerShellSessionOptions());
PowerShell leasedBuilder = leasedSession.CreatePowerShell();
leasedSession.Dispose();
leasedSession.Dispose();
try
{
    leasedSession.GetSnapshot();
    return 1;
}
catch (ObjectDisposedException)
{
}

PowerShellInvocationResult leasedResult = leasedBuilder
    .AddScript("'builder-outlives-session'")
    .Invoke();
Require(
    leasedResult.Output.Records.Count == 1 &&
    leasedResult.Output.Records[0].DisplayText == "builder-outlives-session",
    "A builder did not retain its session lease after public session disposal.");
leasedBuilder.Dispose();
leasedBuilder.Dispose();

try
{
    runtime.CreateSessionPool(new PowerShellSessionPoolOptions(1, 1));
    return 1;
}
catch (PowerShellFfiException exception)
    when (exception.Status == PowerShellFfiStatus.UnsupportedCapability)
{
}

await VerifyCancellationAndDisposeAsync();
await VerifySafeHandleDisposeRacesAsync();

Console.WriteLine("FFI package consumer: Success");
return 0;

sealed class ConnectionNameCapability : IPowerShellCapabilityHandler
{
    public PowerShellValue Invoke(
        PowerShellCapabilityInvocation invocation,
        IReadOnlyList<PowerShellValue> arguments)
    {
        if (invocation.Definition.Name != "rdm.get-connection-name" ||
            arguments.Count != 0 ||
            invocation.CancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Capability contract was not preserved.");
        }

        return PowerShellValue.String("nativeaot-connection");
    }
}

sealed class ThrowingCapability : IPowerShellCapabilityHandler
{
    public PowerShellValue Invoke(
        PowerShellCapabilityInvocation invocation,
        IReadOnlyList<PowerShellValue> arguments)
    {
        throw new InvalidOperationException("The callback exception must not escape to native code.");
    }
}

sealed class TimeoutCapability : IPowerShellCapabilityHandler
{
    public PowerShellValue Invoke(
        PowerShellCapabilityInvocation invocation,
        IReadOnlyList<PowerShellValue> arguments)
    {
        Thread.Sleep(100);
        return PowerShellValue.String("late-response");
    }
}

sealed class ReentryCapability : IPowerShellCapabilityHandler
{
    public PowerShellValue Invoke(
        PowerShellCapabilityInvocation invocation,
        IReadOnlyList<PowerShellValue> arguments)
    {
        try
        {
            using PowerShell reentrant = PowerShell.Create();
            throw new InvalidOperationException("The capability handler unexpectedly re-entered the FFI.");
        }
        catch (PowerShellFfiException exception)
            when (exception.Status == PowerShellFfiStatus.Backpressure)
        {
            return PowerShellValue.String("reentry-blocked");
        }
    }
}

sealed class CancellableCapability : IPowerShellCapabilityHandler, IDisposable
{
    public ManualResetEventSlim Started { get; } = new(false);

    public PowerShellValue Invoke(
        PowerShellCapabilityInvocation invocation,
        IReadOnlyList<PowerShellValue> arguments)
    {
        Started.Set();
        while (!invocation.CancellationToken.IsCancellationRequested)
        {
            Thread.Sleep(10);
        }

        invocation.CancellationToken.ThrowIfCancellationRequested();
        return PowerShellValue.Null;
    }

    public void Dispose()
    {
        Started.Dispose();
    }
}
"@ | Set-Content -Path (Join-Path $consumerDirectory 'Program.cs') -Encoding utf8

    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('restore', $consumerProject, '--configfile', $nugetConfig)
    Invoke-CheckedCommand -FilePath dotnet -ArgumentList @('publish', $consumerProject, '--no-restore', '-c', $Configuration)

    $publishDirectory = Join-Path $consumerDirectory "bin\$Configuration\net8.0\win-x64\publish"
    $consumerExe = Join-Path $publishDirectory 'FfiPackageConsumer.exe'
    $nativeAsset = Join-Path $publishDirectory 'multi-pwsh-sdk.dll'
    if (-not (Test-Path $consumerExe -PathType Leaf) -or -not (Test-Path $nativeAsset -PathType Leaf)) {
        throw 'The published package consumer is missing its executable or native FFI asset.'
    }
    $nativeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($nativeAsset)
    if ($nativeVersion.FileVersion -ne $multiPwshVersion -or
        $nativeVersion.ProductVersion -ne $multiPwshVersion -or
        $nativeVersion.OriginalFilename -ne 'multi-pwsh-sdk.dll') {
        throw "The packaged SDK native asset version metadata does not match multi-pwsh $multiPwshVersion."
    }

    Invoke-CheckedCommand -FilePath $consumerExe -ArgumentList @($payloadDirectory, $manifestPath, $manifestSha256)
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
