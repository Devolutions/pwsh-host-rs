[CmdletBinding()]
param(
    [string]$PwshExePath = $env:PwshExePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Actual,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Expected,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($Actual -cne $Expected) {
        throw "$Description. Expected '$Expected'; actual '$Actual'."
    }
}

function Assert-Sequence {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Actual,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($Actual.Count -ne $Expected.Count) {
        throw "$Description count changed. Expected $($Expected.Count); actual $($Actual.Count)."
    }

    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Actual[$index] -cne $Expected[$index]) {
            throw "$Description changed at index $index. Expected '$($Expected[$index])'; actual '$($Actual[$index])'."
        }
    }
}

function Get-ManagedTypeName {
    param(
        [Parameter(Mandatory = $true)]
        [Type]$Type
    )

    if ($Type.IsPointer) {
        return "$(Get-ManagedTypeName -Type $Type.GetElementType())*"
    }

    if ($Type -eq [System.UIntPtr]) {
        return 'System.UIntPtr'
    }

    $Type.FullName
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$facadeProject = Join-Path $repoRoot 'dotnet\sdk-ffi\Devolutions.MultiPwsh.Sdk.csproj'
$facadeAssembly = Join-Path $repoRoot 'dotnet\sdk-ffi\bin\Release\net10.0\Devolutions.MultiPwsh.Sdk.dll'
$facadeInspectorProject = Join-Path $repoRoot 'tests\PwshFfiApiBaselineInspector\PwshFfiApiBaselineInspector.csproj'
$facadeInspectorAssembly = Join-Path $repoRoot 'tests\PwshFfiApiBaselineInspector\bin\Release\net10.0\PwshFfiApiBaselineInspector.dll'
$bindingsProject = Join-Path $repoRoot 'dotnet\bindings\Devolutions.PowerShell.SDK.Bindings.csproj'
$bindingsAssembly = Join-Path $repoRoot 'dotnet\bindings\bin\Release\net8.0\Devolutions.PowerShell.SDK.Bindings.dll'
$nativeMethodsPath = Join-Path $repoRoot 'dotnet\sdk-ffi\NativeMethods.cs'
$ffiBindingsPath = Join-Path $repoRoot 'dotnet\bindings\FfiBindings.cs'
$rustBindingsPath = Join-Path $repoRoot 'crates\pwsh-host\src\bindings\ffi.rs'
$rustFfiPath = Join-Path $repoRoot 'crates\pwsh-sdk-ffi\src\lib.rs'
$baselinePath = Join-Path $PSScriptRoot 'PwshFfiApiBaseline.txt'

& dotnet build $facadeProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to build the FFI facade for ABI contract validation.'
}

& dotnet build $facadeInspectorProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to build the .NET 10 FFI facade inspector.'
}

$bindingsBuildArguments = @('build', $bindingsProject, '-c', 'Release', '--nologo')
if (-not [string]::IsNullOrWhiteSpace($PwshExePath)) {
    $bindingsBuildArguments += "-p:PwshExePath=$PwshExePath"
}

& dotnet @bindingsBuildArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to build the managed FFI binding table for ABI contract validation.'
}

$bindingsAssemblyObject = [System.Reflection.Assembly]::LoadFrom($bindingsAssembly)
$facadeInspection = @(& dotnet $facadeInspectorAssembly) | ConvertFrom-Json
$forbiddenFacadeReferences = @(
    $facadeInspection.References |
        Where-Object {
            $_ -eq 'System.Management.Automation' -or
            $_ -like 'Microsoft.PowerShell.*'
        }
)
if ($forbiddenFacadeReferences.Count -ne 0) {
    throw "The NativeAOT facade must not reference SMA or Microsoft.PowerShell assemblies: $($forbiddenFacadeReferences -join ', ')."
}
$instanceFields = [System.Reflection.BindingFlags]'Instance, Public, NonPublic, DeclaredOnly'
$actual = [System.Collections.Generic.List[string]]::new()
$facadeInspection.PublicBaseline | ForEach-Object { $actual.Add($_) }

$bindingsForPublicBaseline = Get-Content -Path $ffiBindingsPath -Raw
foreach ($match in [regex]::Matches($bindingsForPublicBaseline, '(?m)^\s*public static (?:unsafe )?(?:int|IntPtr)\s+([A-Za-z0-9_]+)\s*\(')) {
    $actual.Add("bindings:$($match.Groups[1].Value)")
}

$nativeMethodsSource = Get-Content -Path $nativeMethodsPath -Raw
foreach ($match in [regex]::Matches($nativeMethodsSource, '\[LibraryImport\(LibraryName,\s*EntryPoint\s*=\s*"(?<entryPoint>multi_pwsh_[a-z0-9_]+)"\)\]')) {
    $actual.Add("native:$($match.Groups['entryPoint'].Value)")
}

$expected = Get-Content -Path $baselinePath | Where-Object { $_ -and -not $_.StartsWith('#') }
$difference = Compare-Object -ReferenceObject ($expected | Sort-Object) -DifferenceObject ($actual | Sort-Object)
if ($null -ne $difference) {
    $difference | Format-Table -AutoSize | Out-String | Write-Error
    throw 'The FFI public API changed. Review and update tests/PwshFfiApiBaseline.txt in the same change.'
}

# This gate validates the managed interop declarations, Rust exports, layouts,
# and bridge table directly on every CI OS without requiring a C compiler.
$ffiBindingsSource = Get-Content -Path $ffiBindingsPath -Raw
$rustBindingsSource = Get-Content -Path $rustBindingsPath -Raw
$rustFfiSource = Get-Content -Path $rustFfiPath -Raw

if ([IntPtr]::Size -ne 8) {
    throw 'The FFI ABI contract is currently win-x64 only and requires an eight-byte pointer size.'
}

$expectedManagedStructs = [ordered]@{
    'NativeAbiInfo' = @{ Size = 24; Fields = @('Size|0|System.UInt32', 'AbiVersion|4|System.UInt32', 'FeatureFlags|8|System.UInt64', 'MinimumCompatibleAbiVersion|16|System.UInt32', 'Reserved|20|System.UInt32') }
    'NativeUtf8Span' = @{ Size = 16; Fields = @('Data|0|System.Byte*', 'Length|8|System.UIntPtr') }
    'NativeDataValue' = @{ Size = 32; Fields = @('Size|0|System.UInt32', 'Kind|4|System.UInt32', 'Flags|8|System.UInt32', 'Reserved|12|System.UInt32', 'Data|16|System.Byte*', 'DataLength|24|System.UIntPtr') }
    'NativeCallResult' = @{ Size = 48; Fields = @('Size|0|System.UInt32', 'Status|4|System.Int32', 'Flags|8|System.UInt32', 'Reserved|12|System.UInt32', 'Diagnostic|16|System.Byte*', 'DiagnosticCapacity|24|System.UIntPtr', 'DiagnosticRequired|32|System.UIntPtr', 'DiagnosticWritten|40|System.UIntPtr') }
    'NativeCapabilityRegistration' = @{ Size = 32; Fields = @('Size|0|System.UInt32', 'Flags|4|System.UInt32', 'Definitions|8|Devolutions.PowerShell.Ffi.NativeDataValue*', 'DispatchCallback|16|System.IntPtr', 'CancelCallback|24|System.IntPtr') }
    'NativeLiveObjectContractPack' = @{ Size = 40; Fields = @('Size|0|System.UInt32', 'Flags|4|System.UInt32', 'PayloadAdapterAssemblyPath|8|Devolutions.PowerShell.Ffi.NativeUtf8Span', 'PayloadAdapterTypeName|24|Devolutions.PowerShell.Ffi.NativeUtf8Span') }
    'NativeSessionOptions' = @{ Size = 216; Fields = @('Size|0|System.UInt32', 'RunspaceMode|4|System.UInt32', 'InitialConfiguration|8|System.UInt32', 'HistoryMode|12|System.UInt32', 'ErrorPreference|16|System.UInt32', 'WarningPreference|20|System.UInt32', 'VerbosePreference|24|System.UInt32', 'DebugPreference|28|System.UInt32', 'InformationPreference|32|System.UInt32', 'Flags|36|System.UInt32', 'Reserved|40|System.UInt32', 'AllowedModulePath|48|Devolutions.PowerShell.Ffi.NativeUtf8Span', 'ExecutionPolicy|64|System.UInt32', 'ConfigurationFlags|68|System.UInt32', 'InitialVariables|72|Devolutions.PowerShell.Ffi.NativeDataValue', 'ModuleImports|104|Devolutions.PowerShell.Ffi.NativeDataValue', 'AllowedModulePaths|136|Devolutions.PowerShell.Ffi.NativeDataValue', 'WorkingDirectory|168|Devolutions.PowerShell.Ffi.NativeUtf8Span', 'Environment|184|Devolutions.PowerShell.Ffi.NativeDataValue') }
    'NativeSessionSnapshot' = @{ Size = 40; Fields = @('Size|0|System.UInt32', 'State|4|System.UInt32', 'RunspaceState|8|System.UInt32', 'Flags|12|System.UInt32', 'ActivePipelineCount|16|System.UInt32', 'EventCount|20|System.UInt32', 'InvocationCount|24|System.UInt64', 'HistoryCount|32|System.UInt64') }
    'NativeSessionPoolOptions' = @{ Size = 20; Fields = @('Size|0|System.UInt32', 'MinimumSessions|4|System.UInt32', 'MaximumSessions|8|System.UInt32', 'Flags|12|System.UInt32', 'Reserved|16|System.UInt32') }
    'NativeOperationStreamBatchInfo' = @{ Size = 64; Fields = @('Size|0|System.UInt32', 'OperationState|4|System.UInt32', 'TerminalStatus|8|System.Int32', 'Flags|12|System.UInt32', 'NextSequence|16|System.UInt64', 'TotalRecordCount|24|System.UInt64', 'DroppedRecordCount|32|System.UInt64', 'SourceDroppedRecordCount|40|System.UInt64', 'LostRecordCount|48|System.UInt64', 'RecordCount|56|System.UInt32', 'Reserved|60|System.UInt32') }
    'NativeLiveObjectContractDescriptor' = @{ Size = 32; Fields = @('Size|0|System.UInt32', 'Directions|4|System.UInt32', 'InterfaceIdLow|8|System.UInt64', 'InterfaceIdHigh|16|System.UInt64', 'MajorVersion|24|System.UInt16', 'MinorVersion|26|System.UInt16', 'Reserved|28|System.UInt32') }
    'NativeLiveObjectContractPackApi' = @{ Size = 40; Fields = @('Size|0|System.UIntPtr', 'AbiVersion|8|System.UInt32', 'ContractCount|12|System.UInt32', 'Contracts|16|Devolutions.PowerShell.Ffi.LiveObjects.NativeLiveObjectContractDescriptor*', 'CreatePayloadProxy|24|System.IntPtr', 'ReleasePayloadProxy|32|System.IntPtr') }
}

Assert-Sequence -Actual @(
    $facadeInspection.NativeStructs |
        ForEach-Object Name |
        Sort-Object
) -Expected @($expectedManagedStructs.Keys | Sort-Object) -Description 'Managed native interop structures'

foreach ($structName in $expectedManagedStructs.Keys) {
    $managedType = @($facadeInspection.NativeStructs | Where-Object Name -eq $structName)
    if ($managedType.Count -ne 1) {
        throw "Managed ABI structure '$structName' is missing."
    }
    $managedType = $managedType[0]
    $contract = $expectedManagedStructs[$structName]
    Assert-Equal -Actual $managedType.Size -Expected $contract.Size -Description "Managed ABI structure '$structName' size"

    $actualFields = @($managedType.Fields)
    $expectedFields = @($contract.Fields)
    Assert-Equal -Actual $actualFields.Count -Expected $expectedFields.Count -Description "Managed ABI structure '$structName' field count"
    for ($index = 0; $index -lt $expectedFields.Count; $index++) {
        $name, $offset, $typeName = $expectedFields[$index] -split '\|'
        Assert-Equal -Actual $actualFields[$index].Name -Expected $name -Description "Managed ABI structure '$structName' field order"
        Assert-Equal -Actual $actualFields[$index].Offset -Expected ([Int64]$offset) -Description "Managed ABI structure '$structName.$name' offset"
        Assert-Equal -Actual $actualFields[$index].TypeName -Expected $typeName -Description "Managed ABI structure '$structName.$name' type"
    }
}

$expectedFacadeStatuses = [ordered]@{
    'Success' = 0
    'BufferTooSmall' = 1
    'InvalidArgument' = -1
    'NotInitialized' = -2
    'IncompatiblePayload' = -3
    'InvalidHandle' = -4
    'HostFailure' = -5
    'ManagedFailure' = -6
    'Panic' = -7
    'InputNotCompleted' = -8
    'Backpressure' = -9
    'UnsupportedValue' = -10
    'OperationCancelled' = -11
    'OperationNotTerminal' = -12
    'UnsupportedCapability' = -17
}
$actualFacadeStatusNames = @($facadeInspection.Statuses.PSObject.Properties.Name)
Assert-Equal -Actual $actualFacadeStatusNames.Count -Expected $expectedFacadeStatuses.Count -Description 'Managed FFI status enumeration count'
foreach ($statusName in $expectedFacadeStatuses.Keys) {
    if ($actualFacadeStatusNames -notcontains $statusName) {
        throw "Managed FFI status '$statusName' is missing."
    }
    Assert-Equal -Actual ([Convert]::ToInt32($facadeInspection.Statuses.$statusName)) -Expected $expectedFacadeStatuses[$statusName] -Description "Managed FFI status '$statusName'"
}

$libraryImports = @($facadeInspection.NativeImports)

$sourceImportedExports = @(
    [regex]::Matches($nativeMethodsSource, '\[LibraryImport\(LibraryName,\s*EntryPoint\s*=\s*"(?<entryPoint>multi_pwsh_[a-z0-9_]+)"\)\]') |
        ForEach-Object { $_.Groups['entryPoint'].Value }
)
Assert-Sequence -Actual @($libraryImports.EntryPoint | Sort-Object) -Expected @($sourceImportedExports | Sort-Object) -Description 'Managed LibraryImport entry points'
Assert-Equal -Actual $libraryImports.Count -Expected ($sourceImportedExports | Select-Object -Unique).Count -Description 'Managed LibraryImport entry point count'

foreach ($import in $libraryImports) {
    Assert-Equal -Actual $import.ReturnType -Expected 'System.Int32' -Description "Managed import '$($import.EntryPoint)' return type"
}

$cdeclAttributes = [regex]::Matches($nativeMethodsSource, '\[\s*UnmanagedCallConv\s*\(\s*CallConvs\s*=\s*\[\s*typeof\s*\(\s*CallConvCdecl\s*\)\s*\]\s*\)\s*\]')
Assert-Equal -Actual $cdeclAttributes.Count -Expected $libraryImports.Count -Description 'Managed Cdecl import attribute count'

$allRustExports = @(
    [regex]::Matches($rustFfiSource, 'pub\s+(?:unsafe\s+)?extern\s+"C"\s+fn\s+(multi_pwsh_[a-z0-9_]+)\s*\(') |
        ForEach-Object { $_.Groups[1].Value }
)
Assert-Sequence -Actual @($allRustExports | Sort-Object) -Expected @($libraryImports.EntryPoint | Sort-Object) -Description 'Managed/Rust export set'
foreach ($import in $libraryImports) {
    if ($allRustExports -notcontains $import.EntryPoint) {
        throw "Managed import '$($import.EntryPoint)' has no Rust export."
    }

    $exportPattern = "(?s)#\[no_mangle\](?:\s*#\[[^\]]+\])?\s*pub\s+(?:unsafe\s+)?extern\s+`"C`"\s+fn\s+$([regex]::Escape($import.EntryPoint))\s*\("
    if (-not [regex]::IsMatch($rustFfiSource, $exportPattern)) {
        throw "Rust export '$($import.EntryPoint)' is missing #[no_mangle] extern `"C`" linkage."
    }
}

$expectedTableSlots = @(
    @{ Field = 'PowerShell_Create'; Rust = 'create_fn'; Alias = 'FnFfiPowerShellCreate'; Method = 'FfiPowerShell_Create'; Signature = 'IntPtr*,FfiCallResult*,int' }
    @{ Field = 'PowerShell_Release'; Rust = 'release_fn'; Alias = 'FnFfiPowerShellRelease'; Method = 'FfiPowerShell_Release'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddArgumentUtf8'; Rust = 'add_argument_utf8_fn'; Alias = 'FnFfiPowerShellAddUtf8'; Method = 'FfiPowerShell_AddArgumentUtf8'; Signature = 'IntPtr,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddParameterStringUtf8'; Rust = 'add_parameter_string_utf8_fn'; Alias = 'FnFfiPowerShellAddParameterStringUtf8'; Method = 'FfiPowerShell_AddParameterStringUtf8'; Signature = 'IntPtr,byte*,int,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddParameterInt64'; Rust = 'add_parameter_int64_fn'; Alias = 'FnFfiPowerShellAddParameterInt64'; Method = 'FfiPowerShell_AddParameterInt64'; Signature = 'IntPtr,byte*,int,long,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddCommandUtf8'; Rust = 'add_command_utf8_fn'; Alias = 'FnFfiPowerShellAddUtf8'; Method = 'FfiPowerShell_AddCommandUtf8'; Signature = 'IntPtr,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddScriptUtf8'; Rust = 'add_script_utf8_fn'; Alias = 'FnFfiPowerShellAddUtf8'; Method = 'FfiPowerShell_AddScriptUtf8'; Signature = 'IntPtr,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddStatement'; Rust = 'add_statement_fn'; Alias = 'FnFfiPowerShellAddStatement'; Method = 'FfiPowerShell_AddStatement'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShell_InvokeToUtf8'; Rust = 'invoke_to_utf8_fn'; Alias = 'FnFfiPowerShellInvokeToUtf8'; Method = 'FfiPowerShell_InvokeToUtf8'; Signature = 'IntPtr,byte*,int,int*,FfiCallResult*,int' }
    @{ Field = 'PowerShell_GetInvocationErrorCount'; Rust = 'get_invocation_error_count_fn'; Alias = 'FnFfiPowerShellGetInvocationErrorCount'; Method = 'FfiPowerShell_GetInvocationErrorCount'; Signature = 'IntPtr,int*,FfiCallResult*,int' }
    @{ Field = 'PowerShell_CopyInvocationErrorFieldToUtf8'; Rust = 'copy_invocation_error_field_to_utf8_fn'; Alias = 'FnFfiPowerShellCopyInvocationErrorFieldToUtf8'; Method = 'FfiPowerShell_CopyInvocationErrorFieldToUtf8'; Signature = 'IntPtr,int,int,byte*,int,int*,FfiCallResult*,int' }
    @{ Field = 'PowerShell_Clear'; Rust = 'clear_fn'; Alias = 'FnFfiPowerShellClear'; Method = 'FfiPowerShell_Clear'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShell_Stop'; Rust = 'stop_fn'; Alias = 'FnFfiPowerShellStop'; Method = 'FfiPowerShell_Stop'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShell_InvokeToResult'; Rust = 'invoke_to_result_fn'; Alias = 'FnFfiPowerShellInvokeToResult'; Method = 'FfiPowerShell_InvokeToResult'; Signature = 'IntPtr,IntPtr*,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_Release'; Rust = 'invocation_result_release_fn'; Alias = 'FnFfiInvocationResultRelease'; Method = 'FfiInvocationResult_Release'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_GetInfo'; Rust = 'invocation_result_get_info_fn'; Alias = 'FnFfiInvocationResultGetInfo'; Method = 'FfiInvocationResult_GetInfo'; Signature = 'IntPtr,uint*,int*,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_GetStreamInfo'; Rust = 'invocation_result_get_stream_info_fn'; Alias = 'FnFfiInvocationResultGetStreamInfo'; Method = 'FfiInvocationResult_GetStreamInfo'; Signature = 'IntPtr,int,int*,uint*,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_GetStreamRecordInfo'; Rust = 'invocation_result_get_stream_record_info_fn'; Alias = 'FnFfiInvocationResultGetStreamRecordInfo'; Method = 'FfiInvocationResult_GetStreamRecordInfo'; Signature = 'IntPtr,int,int,long*,uint*,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_CopyStreamRecordFieldToUtf8'; Rust = 'invocation_result_copy_stream_record_field_to_utf8_fn'; Alias = 'FnFfiInvocationResultCopyStreamRecordFieldToUtf8'; Method = 'FfiInvocationResult_CopyStreamRecordFieldToUtf8'; Signature = 'IntPtr,int,int,int,byte*,int,int*,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_GetSequenceRecord'; Rust = 'invocation_result_get_sequence_record_fn'; Alias = 'FnFfiInvocationResultGetSequenceRecord'; Method = 'FfiInvocationResult_GetSequenceRecord'; Signature = 'IntPtr,int,int*,int*,long*,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddCommandUtf8Local'; Rust = 'add_command_utf8_local_fn'; Alias = 'FnFfiPowerShellAddScopedUtf8'; Method = 'FfiPowerShell_AddCommandUtf8Local'; Signature = 'IntPtr,byte*,int,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddScriptUtf8Local'; Rust = 'add_script_utf8_local_fn'; Alias = 'FnFfiPowerShellAddScopedUtf8'; Method = 'FfiPowerShell_AddScriptUtf8Local'; Signature = 'IntPtr,byte*,int,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddArgumentValue'; Rust = 'add_argument_value_fn'; Alias = 'FnFfiPowerShellAddValue'; Method = 'FfiPowerShell_AddArgumentValue'; Signature = 'IntPtr,uint,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddParameterValue'; Rust = 'add_parameter_value_fn'; Alias = 'FnFfiPowerShellAddParameterValue'; Method = 'FfiPowerShell_AddParameterValue'; Signature = 'IntPtr,byte*,int,uint,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddParameterSwitch'; Rust = 'add_parameter_switch_fn'; Alias = 'FnFfiPowerShellAddUtf8'; Method = 'FfiPowerShell_AddParameterSwitch'; Signature = 'IntPtr,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddInputValue'; Rust = 'add_input_value_fn'; Alias = 'FnFfiPowerShellAddValue'; Method = 'FfiPowerShell_AddInputValue'; Signature = 'IntPtr,uint,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShell_CompleteInput'; Rust = 'complete_input_fn'; Alias = 'FnFfiPowerShellAddStatement'; Method = 'FfiPowerShell_CompleteInput'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShell_ResetInput'; Rust = 'reset_input_fn'; Alias = 'FnFfiPowerShellAddStatement'; Method = 'FfiPowerShell_ResetInput'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_GetMetadata'; Rust = 'invocation_result_get_metadata_fn'; Alias = 'FnFfiInvocationResultGetMetadata'; Method = 'FfiInvocationResult_GetMetadata'; Signature = 'IntPtr,uint*,long*,int*,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_Create'; Rust = 'session_create_fn'; Alias = 'FnFfiPowerShellSessionCreate'; Method = 'FfiPowerShellSession_Create'; Signature = 'uint,uint,uint,uint,uint,uint,uint,uint,byte*,int,IntPtr*,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_Release'; Rust = 'session_release_fn'; Alias = 'FnFfiPowerShellSessionRelease'; Method = 'FfiPowerShellSession_Release'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_CreateBuilder'; Rust = 'session_create_builder_fn'; Alias = 'FnFfiPowerShellSessionCreateBuilder'; Method = 'FfiPowerShellSession_CreateBuilder'; Signature = 'IntPtr,IntPtr*,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_GetSnapshot'; Rust = 'session_get_snapshot_fn'; Alias = 'FnFfiPowerShellSessionGetSnapshot'; Method = 'FfiPowerShellSession_GetSnapshot'; Signature = 'IntPtr,uint*,uint*,uint*,uint*,uint*,long*,long*,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_GetEventInfo'; Rust = 'session_get_event_info_fn'; Alias = 'FnFfiPowerShellSessionGetEventInfo'; Method = 'FfiPowerShellSession_GetEventInfo'; Signature = 'IntPtr,int,long*,uint*,uint*,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_GetStreamTotals'; Rust = 'invocation_result_get_stream_totals_fn'; Alias = 'FnFfiInvocationResultGetStreamTotals'; Method = 'FfiInvocationResult_GetStreamTotals'; Signature = 'IntPtr,int,long*,long*,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_GetStreamRecordProjectionInfo'; Rust = 'invocation_result_get_stream_record_projection_info_fn'; Alias = 'FnFfiInvocationResultGetStreamRecordProjectionInfo'; Method = 'FfiInvocationResult_GetStreamRecordProjectionInfo'; Signature = 'IntPtr,int,int,int*,int*,int*,int*,int*,FfiCallResult*,int' }
    @{ Field = 'InvocationResult_CopyStreamRecordValue'; Rust = 'invocation_result_copy_stream_record_value_fn'; Alias = 'FnFfiInvocationResultCopyStreamRecordValue'; Method = 'FfiInvocationResult_CopyStreamRecordValue'; Signature = 'IntPtr,int,int,int,uint*,byte*,int,int*,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_CreateConfigured'; Rust = 'session_create_configured_fn'; Alias = 'FnFfiPowerShellSessionCreateConfigured'; Method = 'FfiPowerShellSession_CreateConfigured'; Signature = 'uint,uint,uint,uint,uint,uint,uint,uint,uint,byte*,int,byte*,int,byte*,int,byte*,int,byte*,int,IntPtr*,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_SetVariable'; Rust = 'session_set_variable_fn'; Alias = 'FnFfiPowerShellSessionSetVariable'; Method = 'FfiPowerShellSession_SetVariable'; Signature = 'IntPtr,byte*,int,uint,byte*,int,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_RemoveVariable'; Rust = 'session_remove_variable_fn'; Alias = 'FnFfiPowerShellSessionRemoveVariable'; Method = 'FfiPowerShellSession_RemoveVariable'; Signature = 'IntPtr,byte*,int,uint*,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_GetVariableSnapshot'; Rust = 'session_get_variable_snapshot_fn'; Alias = 'FnFfiPowerShellSessionGetVariableSnapshot'; Method = 'FfiPowerShellSession_GetVariableSnapshot'; Signature = 'IntPtr,byte*,int,uint*,uint*,byte*,int,int*,FfiCallResult*,int' }
    @{ Field = 'PowerShell_SetCapabilityContext'; Rust = 'power_shell_set_capability_context_fn'; Alias = 'FnFfiPowerShellSetCapabilityContext'; Method = 'FfiPowerShell_SetCapabilityContext'; Signature = 'IntPtr,ulong,ulong,IntPtr,FfiCallResult*,int' }
    @{ Field = 'LiveObjectProbe_Create'; Rust = 'live_object_probe_create_fn'; Alias = 'FnFfiLiveObjectProbeCreate'; Method = 'FfiLiveObjectProbe_Create'; Signature = 'long,IntPtr*,FfiCallResult*,int' }
    @{ Field = 'LiveObjectProbe_Release'; Rust = 'live_object_probe_release_fn'; Alias = 'FnFfiLiveObjectProbeRelease'; Method = 'FfiLiveObjectProbe_Release'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'LiveObjectProbe_Unregister'; Rust = 'live_object_probe_unregister_fn'; Alias = 'FnFfiLiveObjectProbeUnregister'; Method = 'FfiLiveObjectProbe_Unregister'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShell_AddArgumentLiveObject'; Rust = 'power_shell_add_argument_live_object_fn'; Alias = 'FnFfiPowerShellAddLiveObject'; Method = 'FfiPowerShell_AddArgumentLiveObject'; Signature = 'IntPtr,IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_SetLiveObjectVariable'; Rust = 'power_shell_session_set_live_object_variable_fn'; Alias = 'FnFfiPowerShellSessionSetLiveObjectVariable'; Method = 'FfiPowerShellSession_SetLiveObjectVariable'; Signature = 'IntPtr,byte*,int,IntPtr,FfiCallResult*,int' }
    @{ Field = 'LiveObjectContractPack_Register'; Rust = 'live_object_contract_pack_register_fn'; Alias = 'FnFfiLiveObjectContractPackRegister'; Method = 'FfiLiveObjectContractPack_Register'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'PowerShellSession_SetLiveObjectContractVariable'; Rust = 'power_shell_session_set_live_object_contract_variable_fn'; Alias = 'FnFfiPowerShellSessionSetLiveObjectContractVariable'; Method = 'FfiPowerShellSession_SetLiveObjectContractVariable'; Signature = 'IntPtr,byte*,int,NativeLiveObjectContractDescriptor*,IntPtr,FfiCallResult*,int' }
    @{ Field = 'LiveObjectContractPack_RegisterMany'; Rust = 'live_object_contract_pack_register_many_fn'; Alias = 'FnFfiLiveObjectContractPackRegisterMany'; Method = 'FfiLiveObjectContractPack_RegisterMany'; Signature = 'IntPtr*,uint,FfiCallResult*,int' }
)

$ffiApiType = $bindingsAssemblyObject.GetType('NativeHost.Bindings+FfiApiV2', $true)
Assert-Equal -Actual ([System.Runtime.InteropServices.Marshal]::SizeOf([Type]$ffiApiType)) -Expected 424 -Description 'Managed FfiApiV2 size'
$ffiApiFields = @($ffiApiType.GetFields($instanceFields) | Sort-Object MetadataToken)
$expectedFfiApiFieldNames = @('Size', 'AbiVersion', 'FeatureFlags') + @($expectedTableSlots | ForEach-Object { $_.Field })
Assert-Sequence -Actual @($ffiApiFields | ForEach-Object Name) -Expected $expectedFfiApiFieldNames -Description 'Managed FfiApiV2 slot order'
for ($index = 0; $index -lt $ffiApiFields.Count; $index++) {
    $field = $ffiApiFields[$index]
    $expectedOffset = if ($index -eq 0) { 0 } elseif ($index -eq 1) { 8 } elseif ($index -eq 2) { 16 } else { 24 + (($index - 3) * 8) }
    $expectedType = if ($index -eq 0) { 'System.UIntPtr' } elseif ($index -eq 1) { 'System.UInt32' } elseif ($index -eq 2) { 'System.UInt64' } else { 'System.IntPtr' }
    Assert-Equal -Actual ([System.Runtime.InteropServices.Marshal]::OffsetOf($ffiApiType, $field.Name).ToInt64()) -Expected ([Int64]$expectedOffset) -Description "Managed FfiApiV2 '$($field.Name)' offset"
    Assert-Equal -Actual (Get-ManagedTypeName $field.FieldType) -Expected $expectedType -Description "Managed FfiApiV2 '$($field.Name)' type"
}

$expectedLiveTableSlots = @(
    @{ Field = 'PowerShell_BeginLiveInvocation'; Method = 'FfiPowerShell_BeginLiveInvocation'; Signature = 'IntPtr,IntPtr*,FfiCallResult*,int' }
    @{ Field = 'LiveInvocation_Poll'; Method = 'FfiLiveInvocation_Poll'; Signature = 'IntPtr,int*,FfiCallResult*,int' }
    @{ Field = 'LiveInvocation_ReadBatch'; Method = 'FfiLiveInvocation_ReadBatch'; Signature = 'IntPtr,long,int,IntPtr*,FfiCallResult*,int' }
    @{ Field = 'LiveInvocationBatch_GetInfo'; Method = 'FfiLiveInvocationBatch_GetInfo'; Signature = 'IntPtr,long*,long*,long*,int*,FfiCallResult*,int' }
    @{ Field = 'LiveInvocationBatch_GetRecordInfo'; Method = 'FfiLiveInvocationBatch_GetRecordInfo'; Signature = 'IntPtr,int,int*,long*,uint*,FfiCallResult*,int' }
    @{ Field = 'LiveInvocationBatch_CopyRecordTextToUtf8'; Method = 'FfiLiveInvocationBatch_CopyRecordTextToUtf8'; Signature = 'IntPtr,int,byte*,int,int*,FfiCallResult*,int' }
    @{ Field = 'LiveInvocationBatch_Release'; Method = 'FfiLiveInvocationBatch_Release'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'LiveInvocation_Complete'; Method = 'FfiLiveInvocation_Complete'; Signature = 'IntPtr,IntPtr*,FfiCallResult*,int' }
    @{ Field = 'LiveInvocation_Stop'; Method = 'FfiLiveInvocation_Stop'; Signature = 'IntPtr,FfiCallResult*,int' }
    @{ Field = 'LiveInvocation_Release'; Method = 'FfiLiveInvocation_Release'; Signature = 'IntPtr,FfiCallResult*,int' }
)
$ffiApiV3Type = $bindingsAssemblyObject.GetType('NativeHost.Bindings+FfiApiV3', $true)
Assert-Equal -Actual ([System.Runtime.InteropServices.Marshal]::SizeOf([Type]$ffiApiV3Type)) -Expected 104 -Description 'Managed FfiApiV3 size'
$ffiApiV3Fields = @($ffiApiV3Type.GetFields($instanceFields) | Sort-Object MetadataToken)
$expectedFfiApiV3FieldNames = @('Size', 'AbiVersion', 'FeatureFlags') + @($expectedLiveTableSlots | ForEach-Object { $_.Field })
Assert-Sequence -Actual @($ffiApiV3Fields | ForEach-Object Name) -Expected $expectedFfiApiV3FieldNames -Description 'Managed FfiApiV3 slot order'
for ($index = 0; $index -lt $ffiApiV3Fields.Count; $index++) {
    $field = $ffiApiV3Fields[$index]
    $expectedOffset = if ($index -eq 0) { 0 } elseif ($index -eq 1) { 8 } elseif ($index -eq 2) { 16 } else { 24 + (($index - 3) * 8) }
    $expectedType = if ($index -eq 0) { 'System.UIntPtr' } elseif ($index -eq 1) { 'System.UInt32' } elseif ($index -eq 2) { 'System.UInt64' } else { 'System.IntPtr' }
    Assert-Equal -Actual ([System.Runtime.InteropServices.Marshal]::OffsetOf($ffiApiV3Type, $field.Name).ToInt64()) -Expected ([Int64]$expectedOffset) -Description "Managed FfiApiV3 '$($field.Name)' offset"
    Assert-Equal -Actual (Get-ManagedTypeName $field.FieldType) -Expected $expectedType -Description "Managed FfiApiV3 '$($field.Name)' type"
}

$compactFfiBindingsSource = $ffiBindingsSource -replace '\s+', ''
$expectedBridgeFeatures = 'FeatureFlags=(1UL<<4)|(1UL<<5)|(1UL<<6)|FfiFeatureAsyncOperationPrimitives|FfiFeatureSessionPrimitives|FfiFeatureSessionPolling|FfiFeatureSnapshotProjections|FfiFeatureSessionConfiguration|FfiFeatureSessionVariables|FfiFeatureCapabilityRpc|FfiFeatureLiveObjectProbe|FfiFeatureLiveSessionObjectProbe|FfiFeatureLiveObjectContracts'
if (-not $compactFfiBindingsSource.Contains($expectedBridgeFeatures)) {
    throw 'Managed FfiApiV2 feature flags no longer advertise the checked bridge capabilities.'
}
foreach ($slot in $expectedTableSlots) {
    $assignment = "$($slot.Field)=(IntPtr)(delegate*unmanaged<$($slot.Signature)>)&$($slot.Method)"
    if ($compactFfiBindingsSource.IndexOf($assignment, [StringComparison]::Ordinal) -lt 0) {
        throw "Managed FfiApiV2 slot '$($slot.Field)' does not have its checked target and signature '$($slot.Signature)'."
    }
}
if (-not $compactFfiBindingsSource.Contains('FeatureFlags=FfiFeatureLiveStreamPolling')) {
    throw 'Managed FfiApiV3 does not advertise live stream polling.'
}
foreach ($slot in $expectedLiveTableSlots) {
    $assignment = "$($slot.Field)=(IntPtr)(delegate*unmanaged<$($slot.Signature)>)&$($slot.Method)"
    if ($compactFfiBindingsSource.IndexOf($assignment, [StringComparison]::Ordinal) -lt 0) {
        throw "Managed FfiApiV3 slot '$($slot.Field)' does not have its checked target and signature '$($slot.Signature)'."
    }
}

$rustApiTableMatch = [regex]::Match($rustBindingsSource, '(?s)struct\s+FfiApiV2\s*\{(?<body>.*?)\n\s*\}')
if (-not $rustApiTableMatch.Success) {
    throw 'Rust FfiApiV2 table declaration is missing.'
}
$rustApiTableFields = @(
    [regex]::Matches($rustApiTableMatch.Groups['body'].Value, '(?m)^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*[^,]+,') |
        ForEach-Object { $_.Groups['name'].Value }
)
$expectedRustApiTableFields = @('size', 'abi_version', 'feature_flags') + @($expectedTableSlots | ForEach-Object Rust)
Assert-Sequence -Actual $rustApiTableFields -Expected $expectedRustApiTableFields -Description 'Rust FfiApiV2 slot order'

$rustLiveApiTableMatch = [regex]::Match($rustBindingsSource, '(?s)struct\s+FfiApiV3\s*\{(?<body>.*?)\n\s*\}')
if (-not $rustLiveApiTableMatch.Success) {
    throw 'Rust FfiApiV3 table declaration is missing.'
}
$rustLiveApiTableFields = @(
    [regex]::Matches($rustLiveApiTableMatch.Groups['body'].Value, '(?m)^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*[^,]+,') |
        ForEach-Object { $_.Groups['name'].Value }
)
Assert-Sequence -Actual $rustLiveApiTableFields -Expected @(
    'size',
    'abi_version',
    'feature_flags',
    'power_shell_begin_live_invocation_fn',
    'live_invocation_poll_fn',
    'live_invocation_read_batch_fn',
    'live_invocation_batch_get_info_fn',
    'live_invocation_batch_get_record_info_fn',
    'live_invocation_batch_copy_record_text_to_utf8_fn',
    'live_invocation_batch_release_fn',
    'live_invocation_complete_fn',
    'live_invocation_stop_fn',
    'live_invocation_release_fn'
) -Description 'Rust FfiApiV3 slot order'
if (-not $rustBindingsSource.Contains('Err(_) => None')) {
    throw 'Rust bindings must treat the ABI v3 managed table as optional for ABI v2 compatibility.'
}

$rustBindingsTableMatch = [regex]::Match($rustBindingsSource, '(?s)pub\(crate\)\s+struct\s+FfiBindings\s*\{(?<body>.*?)\n\s*\}')
if (-not $rustBindingsTableMatch.Success) {
    throw 'Rust FfiBindings declaration is missing.'
}
$rustBindingsFields = @(
    [regex]::Matches($rustBindingsTableMatch.Groups['body'].Value, '(?m)^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<type>[A-Za-z_][A-Za-z0-9_]*),') |
        ForEach-Object { "$($_.Groups['name'].Value)|$($_.Groups['type'].Value)" }
)
Assert-Sequence -Actual $rustBindingsFields -Expected @($expectedTableSlots | ForEach-Object { "$($_.Rust)|$($_.Alias)" }) -Description 'Rust FfiBindings slot order and aliases'

$expectedRustFunctionAliases = @'
FnBindingsGetFfiApiV2|unsafeextern"system"fn()->*constFfiApiV2
FnFfiPowerShellCreate|unsafeextern"system"fn(*mutPowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellRelease|unsafeextern"system"fn(PowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellAddUtf8|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,*mutFfiCallResult)->i32
FnFfiPowerShellAddParameterStringUtf8|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,*constu8,i32,*mutFfiCallResult)->i32
FnFfiPowerShellAddParameterInt64|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,i64,*mutFfiCallResult)->i32
FnFfiPowerShellAddStatement|unsafeextern"system"fn(PowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellInvokeToUtf8|unsafeextern"system"fn(PowerShellHandle,*mutu8,i32,*muti32,*mutFfiCallResult)->i32
FnFfiPowerShellGetInvocationErrorCount|unsafeextern"system"fn(PowerShellHandle,*muti32,*mutFfiCallResult)->i32
FnFfiPowerShellCopyInvocationErrorFieldToUtf8|unsafeextern"system"fn(PowerShellHandle,i32,i32,*mutu8,i32,*muti32,*mutFfiCallResult)->i32
FnFfiPowerShellClear|unsafeextern"system"fn(PowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellStop|unsafeextern"system"fn(PowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellInvokeToResult|unsafeextern"system"fn(PowerShellHandle,*mutPowerShellHandle,*mutFfiCallResult)->i32
FnFfiInvocationResultRelease|unsafeextern"system"fn(PowerShellHandle,*mutFfiCallResult)->i32
FnFfiInvocationResultGetInfo|unsafeextern"system"fn(PowerShellHandle,*mutu32,*muti32,*mutFfiCallResult)->i32
FnFfiInvocationResultGetStreamInfo|unsafeextern"system"fn(PowerShellHandle,i32,*muti32,*mutu32,*mutFfiCallResult)->i32
FnFfiInvocationResultGetStreamRecordInfo|unsafeextern"system"fn(PowerShellHandle,i32,i32,*muti64,*mutu32,*mutFfiCallResult)->i32
FnFfiInvocationResultCopyStreamRecordFieldToUtf8|unsafeextern"system"fn(PowerShellHandle,i32,i32,i32,*mutu8,i32,*muti32,*mutFfiCallResult)->i32
FnFfiInvocationResultGetSequenceRecord|unsafeextern"system"fn(PowerShellHandle,i32,*muti32,*muti32,*muti64,*mutFfiCallResult)->i32
FnFfiPowerShellAddScopedUtf8|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,i32,*mutFfiCallResult)->i32
FnFfiPowerShellAddValue|unsafeextern"system"fn(PowerShellHandle,u32,*constu8,i32,*mutFfiCallResult)->i32
FnFfiPowerShellAddParameterValue|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,u32,*constu8,i32,*mutFfiCallResult)->i32
FnFfiInvocationResultGetMetadata|unsafeextern"system"fn(PowerShellHandle,*mutu32,*muti64,*muti32,*mutFfiCallResult)->i32
FnFfiPowerShellSessionCreate|unsafeextern"system"fn(u32,u32,u32,u32,u32,u32,u32,u32,*constu8,i32,*mutPowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellSessionRelease|unsafeextern"system"fn(PowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellSessionCreateBuilder|unsafeextern"system"fn(PowerShellHandle,*mutPowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellSessionGetSnapshot|unsafeextern"system"fn(PowerShellHandle,*mutu32,*mutu32,*mutu32,*mutu32,*mutu32,*muti64,*muti64,*mutFfiCallResult)->i32
FnFfiPowerShellSessionGetEventInfo|unsafeextern"system"fn(PowerShellHandle,i32,*muti64,*mutu32,*mutu32,*mutFfiCallResult)->i32
FnFfiInvocationResultGetStreamTotals|unsafeextern"system"fn(PowerShellHandle,i32,*muti64,*muti64,*mutFfiCallResult)->i32
FnFfiInvocationResultGetStreamRecordProjectionInfo|unsafeextern"system"fn(PowerShellHandle,i32,i32,*muti32,*muti32,*muti32,*muti32,*muti32,*mutFfiCallResult)->i32
FnFfiInvocationResultCopyStreamRecordValue|unsafeextern"system"fn(PowerShellHandle,i32,i32,i32,*mutu32,*mutu8,i32,*muti32,*mutFfiCallResult)->i32
FnFfiPowerShellSessionCreateConfigured|unsafeextern"system"fn(u32,u32,u32,u32,u32,u32,u32,u32,u32,*constu8,i32,*constu8,i32,*constu8,i32,*constu8,i32,*constu8,i32,*mutPowerShellHandle,*mutFfiCallResult)->i32
FnFfiPowerShellSessionSetVariable|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,u32,*constu8,i32,*mutFfiCallResult)->i32
FnFfiPowerShellSessionRemoveVariable|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,*mutu32,*mutFfiCallResult)->i32
FnFfiPowerShellSessionGetVariableSnapshot|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,*mutu32,*mutu32,*mutu8,i32,*muti32,*mutFfiCallResult)->i32
FnFfiPowerShellSetCapabilityContext|unsafeextern"system"fn(PowerShellHandle,u64,u64,*constlibc::c_void,*mutFfiCallResult)->i32
FnFfiLiveObjectProbeCreate|unsafeextern"system"fn(i64,*mut*mutlibc::c_void,*mutFfiCallResult)->i32
FnFfiLiveObjectProbeRelease|unsafeextern"system"fn(*mutlibc::c_void,*mutFfiCallResult)->i32
FnFfiLiveObjectProbeUnregister|unsafeextern"system"fn(*mutlibc::c_void,*mutFfiCallResult)->i32
FnFfiPowerShellAddLiveObject|unsafeextern"system"fn(PowerShellHandle,*mutlibc::c_void,*mutFfiCallResult)->i32
FnFfiPowerShellSessionSetLiveObjectVariable|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,*mutlibc::c_void,*mutFfiCallResult)->i32
FnFfiLiveObjectContractPackRegister|unsafeextern"system"fn(*mutlibc::c_void,*mutFfiCallResult)->i32
FnFfiPowerShellSessionSetLiveObjectContractVariable|unsafeextern"system"fn(PowerShellHandle,*constu8,i32,*constFfiLiveObjectContractDescriptor,*mutlibc::c_void,*mutFfiCallResult)->i32
FnFfiLiveObjectContractPackRegisterMany|unsafeextern"system"fn(*const*mutlibc::c_void,u32,*mutFfiCallResult)->i32
'@ -split [Environment]::NewLine | Where-Object { $_ }

foreach ($expectedAlias in $expectedRustFunctionAliases) {
    $name, $expectedSignature = $expectedAlias -split '\|'
    $aliasMatch = [regex]::Match($rustBindingsSource, "(?s)type\s+$name\s*=\s*(?<signature>.*?);")
    if (-not $aliasMatch.Success) {
        throw "Rust FFI function alias '$name' is missing."
    }
    $actualSignature = $aliasMatch.Groups['signature'].Value -replace '\s+', ''
    $actualSignature = $actualSignature -replace ',\)', ')'
    Assert-Equal -Actual $actualSignature -Expected $expectedSignature -Description "Rust FFI function alias '$name'"
}
