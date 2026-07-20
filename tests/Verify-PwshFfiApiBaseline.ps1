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

function New-AbiInfo {
    param(
        [Parameter(Mandatory = $true)]
        [Type]$AbiInfoType,

        [Parameter(Mandatory = $true)]
        [UInt64]$FeatureFlags,

        [Parameter(Mandatory = $true)]
        [UInt32]$AbiVersion,

        [Parameter(Mandatory = $true)]
        [UInt32]$MinimumCompatibleAbiVersion
    )

    $instance = [Activator]::CreateInstance($AbiInfoType)
    $fieldFlags = [System.Reflection.BindingFlags]'Instance, NonPublic'
    $AbiInfoType.GetField('Size', $fieldFlags).SetValue($instance, [UInt32]24)
    $AbiInfoType.GetField('AbiVersion', $fieldFlags).SetValue($instance, $AbiVersion)
    $AbiInfoType.GetField('FeatureFlags', $fieldFlags).SetValue($instance, $FeatureFlags)
    $AbiInfoType.GetField('MinimumCompatibleAbiVersion', $fieldFlags).SetValue($instance, $MinimumCompatibleAbiVersion)
    $AbiInfoType.GetField('Reserved', $fieldFlags).SetValue($instance, [UInt32]0)
    $instance
}

function Assert-AbiRejected {
    param(
        [Parameter(Mandatory = $true)]
        [System.Reflection.MethodInfo]$EnsureSupportedAbi,

        [Parameter(Mandatory = $true)]
        $AbiInfo,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    try {
        $EnsureSupportedAbi.Invoke($null, @($AbiInfo))
    }
    catch {
        $exception = $_.Exception
        while ($null -ne $exception) {
            if ($exception -is [System.NotSupportedException]) {
                return
            }
            $exception = $exception.InnerException
        }

        throw "$Description did not throw NotSupportedException."
    }

    throw "$Description was accepted."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$facadeProject = Join-Path $repoRoot 'dotnet\ffi\Devolutions.MultiPwsh.Sdk.csproj'
$facadeAssembly = Join-Path $repoRoot 'dotnet\ffi\bin\Release\net8.0\Devolutions.MultiPwsh.Sdk.dll'
$bindingsProject = Join-Path $repoRoot 'dotnet\bindings\Devolutions.PowerShell.SDK.Bindings.csproj'
$bindingsAssembly = Join-Path $repoRoot 'dotnet\bindings\bin\Release\net8.0\Devolutions.PowerShell.SDK.Bindings.dll'
$nativeMethodsPath = Join-Path $repoRoot 'dotnet\ffi\NativeMethods.cs'
$ffiBindingsPath = Join-Path $repoRoot 'dotnet\bindings\FfiBindings.cs'
$rustBindingsPath = Join-Path $repoRoot 'crates\pwsh-host\src\bindings\ffi.rs'
$rustFfiPath = Join-Path $repoRoot 'crates\pwsh-ffi\src\lib.rs'
$baselinePath = Join-Path $PSScriptRoot 'PwshFfiApiBaseline.txt'

& dotnet build $facadeProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to build the FFI facade for ABI contract validation.'
}

$bindingsBuildArguments = @('build', $bindingsProject, '-c', 'Release', '--nologo')
if (-not [string]::IsNullOrWhiteSpace($PwshExePath)) {
    $bindingsBuildArguments += "-p:PwshExePath=$PwshExePath"
}

& dotnet @bindingsBuildArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to build the managed FFI binding table for ABI contract validation.'
}

$facadeAssemblyObject = [System.Reflection.Assembly]::LoadFrom($facadeAssembly)
$bindingsAssemblyObject = [System.Reflection.Assembly]::LoadFrom($bindingsAssembly)
$forbiddenFacadeReferences = @(
    $facadeAssemblyObject.GetReferencedAssemblies() |
        Where-Object {
            $_.Name -eq 'System.Management.Automation' -or
            $_.Name -like 'Microsoft.PowerShell.*'
        }
)
if ($forbiddenFacadeReferences.Count -ne 0) {
    throw "The NativeAOT facade must not reference SMA or Microsoft.PowerShell assemblies: $($forbiddenFacadeReferences.Name -join ', ')."
}
$flags = [System.Reflection.BindingFlags]'Public, Instance, Static, DeclaredOnly'
$actual = [System.Collections.Generic.List[string]]::new()

foreach ($type in $facadeAssemblyObject.GetExportedTypes() | Sort-Object FullName) {
    $actual.Add("facade:type:$($type.FullName)")
    foreach ($constructor in $type.GetConstructors($flags) | Sort-Object ToString) {
        $actual.Add("facade:ctor:$($type.FullName)::$constructor")
    }
    foreach ($property in $type.GetProperties($flags) | Sort-Object Name) {
        $actual.Add("facade:property:$($type.FullName)::$property")
    }
    foreach ($method in $type.GetMethods($flags) | Where-Object { -not $_.IsSpecialName } | Sort-Object ToString) {
        $actual.Add("facade:method:$($type.FullName)::$method")
    }
    foreach ($field in $type.GetFields($flags) | Where-Object { -not $_.IsSpecialName } | Sort-Object Name) {
        $actual.Add("facade:field:$($type.FullName)::$field")
    }
}

$bindingsForPublicBaseline = Get-Content -Path $ffiBindingsPath -Raw
foreach ($match in [regex]::Matches($bindingsForPublicBaseline, '(?m)^\s*public static (?:unsafe )?(?:int|IntPtr)\s+([A-Za-z0-9_]+)\s*\(')) {
    $actual.Add("bindings:$($match.Groups[1].Value)")
}

$nativeMethodsSource = Get-Content -Path $nativeMethodsPath -Raw
foreach ($match in [regex]::Matches($nativeMethodsSource, '\[LibraryImport\(LibraryName,\s*EntryPoint\s*=\s*"(?<entryPoint>dps_pwsh_[a-z0-9_]+)"\)\]')) {
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
    'NativePayloadActivation' = @{ Size = 64; Fields = @('Size|0|System.UInt32', 'TrustPolicy|4|System.UInt32', 'Flags|8|System.UInt32', 'Reserved|12|System.UInt32', 'PayloadPath|16|Devolutions.PowerShell.Ffi.NativeUtf8Span', 'ManifestPath|32|Devolutions.PowerShell.Ffi.NativeUtf8Span', 'ManifestSha256|48|Devolutions.PowerShell.Ffi.NativeUtf8Span') }
    'NativeCallResult' = @{ Size = 48; Fields = @('Size|0|System.UInt32', 'Status|4|System.Int32', 'Flags|8|System.UInt32', 'Reserved|12|System.UInt32', 'Diagnostic|16|System.Byte*', 'DiagnosticCapacity|24|System.UIntPtr', 'DiagnosticRequired|32|System.UIntPtr', 'DiagnosticWritten|40|System.UIntPtr') }
    'NativeCapabilityRegistration' = @{ Size = 32; Fields = @('Size|0|System.UInt32', 'Flags|4|System.UInt32', 'Definitions|8|Devolutions.PowerShell.Ffi.NativeDataValue*', 'DispatchCallback|16|System.IntPtr', 'CancelCallback|24|System.IntPtr') }
    'NativeSessionOptions' = @{ Size = 216; Fields = @('Size|0|System.UInt32', 'RunspaceMode|4|System.UInt32', 'InitialConfiguration|8|System.UInt32', 'HistoryMode|12|System.UInt32', 'ErrorPreference|16|System.UInt32', 'WarningPreference|20|System.UInt32', 'VerbosePreference|24|System.UInt32', 'DebugPreference|28|System.UInt32', 'InformationPreference|32|System.UInt32', 'Flags|36|System.UInt32', 'Reserved|40|System.UInt32', 'AllowedModulePath|48|Devolutions.PowerShell.Ffi.NativeUtf8Span', 'ExecutionPolicy|64|System.UInt32', 'ConfigurationFlags|68|System.UInt32', 'InitialVariables|72|Devolutions.PowerShell.Ffi.NativeDataValue', 'ModuleImports|104|Devolutions.PowerShell.Ffi.NativeDataValue', 'AllowedModulePaths|136|Devolutions.PowerShell.Ffi.NativeDataValue', 'WorkingDirectory|168|Devolutions.PowerShell.Ffi.NativeUtf8Span', 'Environment|184|Devolutions.PowerShell.Ffi.NativeDataValue') }
    'NativeSessionSnapshot' = @{ Size = 40; Fields = @('Size|0|System.UInt32', 'State|4|System.UInt32', 'RunspaceState|8|System.UInt32', 'Flags|12|System.UInt32', 'ActivePipelineCount|16|System.UInt32', 'EventCount|20|System.UInt32', 'InvocationCount|24|System.UInt64', 'HistoryCount|32|System.UInt64') }
    'NativeSessionPoolOptions' = @{ Size = 20; Fields = @('Size|0|System.UInt32', 'MinimumSessions|4|System.UInt32', 'MaximumSessions|8|System.UInt32', 'Flags|12|System.UInt32', 'Reserved|16|System.UInt32') }
}

Assert-Sequence -Actual @(
    $facadeAssemblyObject.GetTypes() |
        Where-Object { $_.Namespace -eq 'Devolutions.PowerShell.Ffi' -and $_.IsValueType -and $_.Name.StartsWith('Native', [StringComparison]::Ordinal) } |
        ForEach-Object Name |
        Sort-Object
) -Expected @($expectedManagedStructs.Keys | Sort-Object) -Description 'Managed native interop structures'

$instanceFields = [System.Reflection.BindingFlags]'Instance, Public, NonPublic, DeclaredOnly'
foreach ($structName in $expectedManagedStructs.Keys) {
    $managedType = $facadeAssemblyObject.GetType("Devolutions.PowerShell.Ffi.$structName", $true)
    $contract = $expectedManagedStructs[$structName]
    Assert-Equal -Actual ([System.Runtime.InteropServices.Marshal]::SizeOf([Type]$managedType)) -Expected $contract.Size -Description "Managed ABI structure '$structName' size"

    $actualFields = @($managedType.GetFields($instanceFields) | Sort-Object MetadataToken)
    $expectedFields = @($contract.Fields)
    Assert-Equal -Actual $actualFields.Count -Expected $expectedFields.Count -Description "Managed ABI structure '$structName' field count"
    for ($index = 0; $index -lt $expectedFields.Count; $index++) {
        $name, $offset, $typeName = $expectedFields[$index] -split '\|'
        Assert-Equal -Actual $actualFields[$index].Name -Expected $name -Description "Managed ABI structure '$structName' field order"
        Assert-Equal -Actual ([System.Runtime.InteropServices.Marshal]::OffsetOf($managedType, $name).ToInt64()) -Expected ([Int64]$offset) -Description "Managed ABI structure '$structName.$name' offset"
        Assert-Equal -Actual (Get-ManagedTypeName $actualFields[$index].FieldType) -Expected $typeName -Description "Managed ABI structure '$structName.$name' type"
    }
}

$facadeStatusType = $facadeAssemblyObject.GetType('Devolutions.PowerShell.Ffi.PowerShellFfiStatus', $true)
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
    'PayloadManifestInvalid' = -13
    'PayloadUntrusted' = -14
    'PayloadHashMismatch' = -15
    'PayloadIncompatible' = -16
    'UnsupportedCapability' = -17
    'SessionPolicyViolation' = -18
}
$actualFacadeStatusNames = @([Enum]::GetNames($facadeStatusType))
Assert-Equal -Actual $actualFacadeStatusNames.Count -Expected $expectedFacadeStatuses.Count -Description 'Managed FFI status enumeration count'
foreach ($statusName in $expectedFacadeStatuses.Keys) {
    if ($actualFacadeStatusNames -notcontains $statusName) {
        throw "Managed FFI status '$statusName' is missing."
    }
    Assert-Equal -Actual ([Convert]::ToInt32([Enum]::Parse($facadeStatusType, $statusName))) -Expected $expectedFacadeStatuses[$statusName] -Description "Managed FFI status '$statusName'"
}

$nativeMethodsType = $facadeAssemblyObject.GetType('Devolutions.PowerShell.Ffi.NativeMethods', $true)
$staticNonPublic = [System.Reflection.BindingFlags]'Static, NonPublic, DeclaredOnly'
$libraryImports = @(
    $nativeMethodsType.GetMethods($staticNonPublic) |
        ForEach-Object {
            $method = $_
            $libraryImport = @($method.GetCustomAttributesData() | Where-Object { $_.AttributeType.FullName -eq 'System.Runtime.InteropServices.LibraryImportAttribute' })
            if ($libraryImport.Count -eq 1) {
                $entryPoint = @($libraryImport[0].NamedArguments | Where-Object { $_.MemberName -eq 'EntryPoint' })
                if ($entryPoint.Count -ne 1) {
                    throw "Native import '$($method.Name)' has no explicit EntryPoint."
                }
                [pscustomobject]@{
                    Method = $method
                    EntryPoint = [string]$entryPoint[0].TypedValue.Value
                }
            }
        } |
        Where-Object { $null -ne $_ }
)

$sourceImportedExports = @(
    [regex]::Matches($nativeMethodsSource, '\[LibraryImport\(LibraryName,\s*EntryPoint\s*=\s*"(?<entryPoint>dps_pwsh_[a-z0-9_]+)"\)\]') |
        ForEach-Object { $_.Groups['entryPoint'].Value }
)
Assert-Sequence -Actual @($libraryImports.EntryPoint | Sort-Object) -Expected @($sourceImportedExports | Sort-Object) -Description 'Managed LibraryImport entry points'
Assert-Equal -Actual $libraryImports.Count -Expected ($sourceImportedExports | Select-Object -Unique).Count -Description 'Managed LibraryImport entry point count'

foreach ($import in $libraryImports) {
    Assert-Equal -Actual (Get-ManagedTypeName $import.Method.ReturnType) -Expected 'System.Int32' -Description "Managed import '$($import.EntryPoint)' return type"
}

$cdeclAttributes = [regex]::Matches($nativeMethodsSource, '\[\s*UnmanagedCallConv\s*\(\s*CallConvs\s*=\s*\[\s*typeof\s*\(\s*CallConvCdecl\s*\)\s*\]\s*\)\s*\]')
Assert-Equal -Actual $cdeclAttributes.Count -Expected $libraryImports.Count -Description 'Managed Cdecl import attribute count'

$allRustExports = @(
    [regex]::Matches($rustFfiSource, 'pub\s+(?:unsafe\s+)?extern\s+"C"\s+fn\s+(dps_pwsh_[a-z0-9_]+)\s*\(') |
        ForEach-Object { $_.Groups[1].Value }
)
foreach ($import in $libraryImports) {
    if ($allRustExports -notcontains $import.EntryPoint) {
        throw "Managed import '$($import.EntryPoint)' has no Rust export."
    }

    $exportPattern = "(?s)#\[no_mangle\](?:\s*#\[[^\]]+\])?\s*pub\s+(?:unsafe\s+)?extern\s+`"C`"\s+fn\s+$([regex]::Escape($import.EntryPoint))\s*\("
    if (-not [regex]::IsMatch($rustFfiSource, $exportPattern)) {
        throw "Rust export '$($import.EntryPoint)' is missing #[no_mangle] extern `"C`" linkage."
    }
}

$ensureSupportedAbi = $facadeAssemblyObject.GetType('Devolutions.PowerShell.Ffi.PowerShell', $true).GetMethod(
    'EnsureSupportedAbi',
    $staticNonPublic,
    $null,
    [Type[]]@($facadeAssemblyObject.GetType('Devolutions.PowerShell.Ffi.NativeAbiInfo', $true)),
    $null)
if ($null -eq $ensureSupportedAbi) {
    throw 'The facade must retain an ABI validation overload that accepts NativeAbiInfo.'
}

$abiInfoType = $facadeAssemblyObject.GetType('Devolutions.PowerShell.Ffi.NativeAbiInfo', $true)
$allRequiredFeatures = [UInt64]0x1FFFF
$ensureSupportedAbi.Invoke($null, @(New-AbiInfo -AbiInfoType $abiInfoType -FeatureFlags $allRequiredFeatures -AbiVersion 2 -MinimumCompatibleAbiVersion 2))
for ($bit = 0; $bit -le 16; $bit++) {
    $withoutFeature = $allRequiredFeatures -bxor ([UInt64]1 -shl $bit)
    Assert-AbiRejected -EnsureSupportedAbi $ensureSupportedAbi -AbiInfo (New-AbiInfo -AbiInfoType $abiInfoType -FeatureFlags $withoutFeature -AbiVersion 2 -MinimumCompatibleAbiVersion 2) -Description "Facade ABI validation without required feature bit $bit"
}
Assert-AbiRejected -EnsureSupportedAbi $ensureSupportedAbi -AbiInfo (New-AbiInfo -AbiInfoType $abiInfoType -FeatureFlags $allRequiredFeatures -AbiVersion 1 -MinimumCompatibleAbiVersion 1) -Description 'Facade ABI validation for an incompatible ABI version'
Assert-AbiRejected -EnsureSupportedAbi $ensureSupportedAbi -AbiInfo (New-AbiInfo -AbiInfoType $abiInfoType -FeatureFlags $allRequiredFeatures -AbiVersion 2 -MinimumCompatibleAbiVersion 3) -Description 'Facade ABI validation for an incompatible minimum ABI version'

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
)

$ffiApiType = $bindingsAssemblyObject.GetType('NativeHost.Bindings+FfiApiV2', $true)
Assert-Equal -Actual ([System.Runtime.InteropServices.Marshal]::SizeOf([Type]$ffiApiType)) -Expected 360 -Description 'Managed FfiApiV2 size'
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

$compactFfiBindingsSource = $ffiBindingsSource -replace '\s+', ''
$expectedBridgeFeatures = 'FeatureFlags=(1UL<<4)|(1UL<<5)|(1UL<<6)|FfiFeatureAsyncOperationPrimitives|FfiFeatureSessionPrimitives|FfiFeatureSessionPolling|FfiFeatureSnapshotProjections|FfiFeatureSessionConfiguration|FfiFeatureSessionVariables|FfiFeatureCapabilityRpc'
if (-not $compactFfiBindingsSource.Contains($expectedBridgeFeatures)) {
    throw 'Managed FfiApiV2 feature flags no longer advertise the checked bridge capabilities.'
}
foreach ($slot in $expectedTableSlots) {
    $assignment = "$($slot.Field)=(IntPtr)(delegate*unmanaged<$($slot.Signature)>)&$($slot.Method)"
    if ($compactFfiBindingsSource.IndexOf($assignment, [StringComparison]::Ordinal) -lt 0) {
        throw "Managed FfiApiV2 slot '$($slot.Field)' does not have its checked target and signature '$($slot.Signature)'."
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
