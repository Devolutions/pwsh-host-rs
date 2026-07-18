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

function Get-NormalizedCDeclaration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $normalized = ([regex]::Replace($Value, '\s+', ' ')).Trim()
    $normalized = $normalized -replace '\(\s+', '('
    $normalized -replace '\s+\)', ')'
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

function Convert-CParameterToManagedTypeName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Parameter
    )

    $type = ([regex]::Replace($Parameter.Trim(), '\s+[A-Za-z_][A-Za-z0-9_]*$', '')).Trim()
    $type = $type -replace '^const\s+', ''
    $type = $type -replace '\s*\*', '*'
    $type = ([regex]::Replace($type, '\s+', ' ')).Trim()

    switch ($type) {
        'uint64_t' { return 'System.UInt64' }
        'uint64_t*' { return 'System.UInt64*' }
        'uint32_t' { return 'System.UInt32' }
        'uint32_t*' { return 'System.UInt32*' }
        'int64_t' { return 'System.Int64' }
        'int32_t' { return 'System.Int32' }
        'int32_t*' { return 'System.Int32*' }
        'size_t' { return 'System.UIntPtr' }
        'size_t*' { return 'System.UIntPtr*' }
        'uint8_t*' { return 'System.Byte*' }
        'struct dps_pwsh_abi_info*' { return 'Devolutions.PowerShell.Ffi.NativeAbiInfo*' }
        'struct dps_pwsh_utf8_span' { return 'Devolutions.PowerShell.Ffi.NativeUtf8Span' }
        'struct dps_pwsh_data_value*' { return 'Devolutions.PowerShell.Ffi.NativeDataValue*' }
        'struct dps_pwsh_capability_registration*' { return 'Devolutions.PowerShell.Ffi.NativeCapabilityRegistration*' }
        'struct dps_pwsh_payload_activation*' { return 'Devolutions.PowerShell.Ffi.NativePayloadActivation*' }
        'struct dps_pwsh_call_result*' { return 'Devolutions.PowerShell.Ffi.NativeCallResult*' }
        'struct dps_pwsh_session_options*' { return 'Devolutions.PowerShell.Ffi.NativeSessionOptions*' }
        'struct dps_pwsh_session_snapshot*' { return 'Devolutions.PowerShell.Ffi.NativeSessionSnapshot*' }
        'struct dps_pwsh_session_pool_options*' { return 'Devolutions.PowerShell.Ffi.NativeSessionPoolOptions*' }
        default { throw "No managed ABI type mapping is defined for C parameter '$Parameter'." }
    }
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
$facadeProject = Join-Path $repoRoot 'dotnet\ffi\Devolutions.PowerShell.Ffi.csproj'
$facadeAssembly = Join-Path $repoRoot 'dotnet\ffi\bin\Release\net8.0\Devolutions.PowerShell.Ffi.dll'
$bindingsProject = Join-Path $repoRoot 'dotnet\bindings\Devolutions.PowerShell.SDK.Bindings.csproj'
$bindingsAssembly = Join-Path $repoRoot 'dotnet\bindings\bin\Release\net8.0\Devolutions.PowerShell.SDK.Bindings.dll'
$headerPath = Join-Path $repoRoot 'crates\pwsh-ffi\include\devolutions_pwsh_ffi.h'
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

$headerForPublicBaseline = Get-Content -Path $headerPath -Raw
if ($headerForPublicBaseline -notmatch '(?s)#ifdef __cplusplus\s+extern "C" \{\s+#endif') {
    throw 'The native FFI header must wrap ABI declarations in extern "C" for C++ consumers.'
}
if ($headerForPublicBaseline -notmatch '(?s)#ifdef __cplusplus\s+\}\s+#endif\s+#endif\s*$') {
    throw 'The native FFI header must close its C++ linkage guard before the include guard.'
}
foreach ($match in [regex]::Matches($headerForPublicBaseline, '(?m)^int32_t\s+(dps_pwsh_(?:v2_[a-z0-9_]+|get_abi_info))\s*\(')) {
    $actual.Add("header:$($match.Groups[1].Value)")
}

$bindingsForPublicBaseline = Get-Content -Path $ffiBindingsPath -Raw
foreach ($match in [regex]::Matches($bindingsForPublicBaseline, '(?m)^\s*public static (?:unsafe )?(?:int|IntPtr)\s+([A-Za-z0-9_]+)\s*\(')) {
    $actual.Add("bindings:$($match.Groups[1].Value)")
}

$expected = Get-Content -Path $baselinePath | Where-Object { $_ -and -not $_.StartsWith('#') }
$difference = Compare-Object -ReferenceObject ($expected | Sort-Object) -DifferenceObject ($actual | Sort-Object)
if ($null -ne $difference) {
    $difference | Format-Table -AutoSize | Out-String | Write-Error
    throw 'The FFI public API changed. Review and update tests/PwshFfiApiBaseline.txt in the same change.'
}

# This gate deliberately uses a parser/reflection contract instead of requiring a
# C compiler: the CI runners do not guarantee one portable compiler invocation.
# The checks below validate the C declarations, managed layouts, and bridge table
# directly on every CI OS.
$header = Get-Content -Path $headerPath -Raw
$headerWithoutComments = [regex]::Replace($header, '/\*.*?\*/', '', [System.Text.RegularExpressions.RegexOptions]::Singleline)
$nativeMethodsSource = Get-Content -Path $nativeMethodsPath -Raw
$ffiBindingsSource = Get-Content -Path $ffiBindingsPath -Raw
$rustBindingsSource = Get-Content -Path $rustBindingsPath -Raw
$rustFfiSource = Get-Content -Path $rustFfiPath -Raw

if ([IntPtr]::Size -ne 8) {
    throw 'The FFI ABI contract is currently win-x64 only and requires an eight-byte pointer size.'
}

$expectedHeaderFunctions = @'
uint32_t dps_pwsh_abi_version(void);
uint64_t dps_pwsh_feature_flags(void);
int32_t dps_pwsh_get_abi_info(struct dps_pwsh_abi_info* info);
int32_t dps_pwsh_v2_initialize_utf8(struct dps_pwsh_utf8_span payload_path, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_initialize_payload(const struct dps_pwsh_payload_activation* activation, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_create(uint64_t* handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_release(uint64_t handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_command_utf8(uint64_t handle, struct dps_pwsh_utf8_span command, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_script_utf8(uint64_t handle, struct dps_pwsh_utf8_span script, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_argument_utf8(uint64_t handle, struct dps_pwsh_utf8_span argument, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_parameter_string_utf8(uint64_t handle, struct dps_pwsh_utf8_span name, struct dps_pwsh_utf8_span value, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_parameter_i64(uint64_t handle, struct dps_pwsh_utf8_span name, int64_t value, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_command_utf8_local(uint64_t handle, struct dps_pwsh_utf8_span command, uint32_t use_local_scope, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_script_utf8_local(uint64_t handle, struct dps_pwsh_utf8_span script, uint32_t use_local_scope, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_argument_value(uint64_t handle, const struct dps_pwsh_data_value* value, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_parameter_value(uint64_t handle, struct dps_pwsh_utf8_span name, const struct dps_pwsh_data_value* value, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_parameter_switch(uint64_t handle, struct dps_pwsh_utf8_span name, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_input_value(uint64_t handle, const struct dps_pwsh_data_value* value, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_complete_input(uint64_t handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_reset_input(uint64_t handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_statement(uint64_t handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_clear(uint64_t handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_invoke_utf8(uint64_t handle, uint8_t* buffer, size_t buffer_len, size_t* required_len, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_get_invocation_error_count(uint64_t handle, uint32_t* error_count, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_copy_invocation_error_field_utf8(uint64_t handle, uint32_t error_index, uint32_t field, uint8_t* buffer, size_t buffer_len, size_t* required_len, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_stop(uint64_t handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_capability_register(const struct dps_pwsh_capability_registration* registration, uint64_t* capability_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_capability_release(uint64_t capability_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_set_capabilities(uint64_t handle, uint64_t capability_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_invoke(uint64_t handle, uint64_t* result_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_release(uint64_t result_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_info(uint64_t result_handle, uint32_t* flags, uint32_t* sequence_count, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_metadata(uint64_t result_handle, uint32_t* state, uint64_t* invocation_id, uint32_t* had_errors, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_stream_info(uint64_t result_handle, uint32_t stream, uint32_t* record_count, uint32_t* flags, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_stream_record_info(uint64_t result_handle, uint32_t stream, uint32_t record_index, uint64_t* sequence, uint32_t* flags, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_copy_stream_record_field_utf8(uint64_t result_handle, uint32_t stream, uint32_t record_index, uint32_t field, uint8_t* buffer, size_t buffer_len, size_t* required_len, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_stream_totals(uint64_t result_handle, uint32_t stream, uint64_t* total_record_count, uint64_t* dropped_record_count, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_stream_record_projection_info(uint64_t result_handle, uint32_t stream, uint32_t record_index, uint32_t* property_entry_count, uint32_t* dropped_property_entry_count, uint32_t* type_name_count, uint32_t* dropped_type_name_count, uint32_t* projection_flags, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_copy_stream_record_value(uint64_t result_handle, uint32_t stream, uint32_t record_index, uint32_t value_slot, uint32_t* kind, uint8_t* buffer, size_t buffer_len, size_t* required_len, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_sequence_record(uint64_t result_handle, uint32_t sequence_index, uint32_t* stream, uint32_t* record_index, uint64_t* sequence, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_invoke_async(uint64_t handle, uint64_t* operation_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_release(uint64_t operation_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_stop(uint64_t operation_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_poll(uint64_t operation_handle, uint32_t* state, int32_t* terminal_status, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_wait(uint64_t operation_handle, uint32_t timeout_milliseconds, uint32_t* state, int32_t* terminal_status, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_get_result(uint64_t operation_handle, uint64_t* result_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_create(const struct dps_pwsh_session_options* options, uint64_t* session_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_release(uint64_t session_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_create_builder(uint64_t session_handle, uint64_t* builder_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_get_snapshot(uint64_t session_handle, struct dps_pwsh_session_snapshot* snapshot, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_get_event_info(uint64_t session_handle, uint32_t event_index, uint64_t* sequence, uint32_t* state, uint32_t* flags, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_set_variable(uint64_t session_handle, struct dps_pwsh_utf8_span name, const struct dps_pwsh_data_value* value, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_remove_variable(uint64_t session_handle, struct dps_pwsh_utf8_span name, uint32_t* removed, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_get_variable_snapshot(uint64_t session_handle, struct dps_pwsh_utf8_span name, uint32_t* found, uint32_t* kind, uint8_t* buffer, size_t buffer_len, size_t* required_len, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_pool_create(const struct dps_pwsh_session_pool_options* options, uint64_t* pool_handle, struct dps_pwsh_call_result* result);
int32_t dps_pwsh_initialize_utf8(const uint8_t* payload_path, size_t payload_path_len);
int32_t dps_pwsh_last_error_utf8(uint8_t* buffer, size_t buffer_len, size_t* required_len);
int32_t dps_pwsh_create(uint64_t* handle);
int32_t dps_pwsh_release(uint64_t handle);
int32_t dps_pwsh_add_command_utf8(uint64_t handle, const uint8_t* command, size_t command_len);
int32_t dps_pwsh_add_script_utf8(uint64_t handle, const uint8_t* script, size_t script_len);
int32_t dps_pwsh_add_argument_utf8(uint64_t handle, const uint8_t* argument, size_t argument_len);
int32_t dps_pwsh_add_parameter_string_utf8(uint64_t handle, const uint8_t* name, size_t name_len, const uint8_t* value, size_t value_len);
int32_t dps_pwsh_add_parameter_i64(uint64_t handle, const uint8_t* name, size_t name_len, int64_t value);
int32_t dps_pwsh_add_statement(uint64_t handle);
int32_t dps_pwsh_clear(uint64_t handle);
int32_t dps_pwsh_invoke_utf8(uint64_t handle, uint8_t* buffer, size_t buffer_len, size_t* required_len);
int32_t dps_pwsh_get_invocation_error_count(uint64_t handle, uint32_t* error_count);
int32_t dps_pwsh_copy_invocation_error_field_utf8(uint64_t handle, uint32_t error_index, uint32_t field, uint8_t* buffer, size_t buffer_len, size_t* required_len);
int32_t dps_pwsh_stop(uint64_t handle);
'@ -split [Environment]::NewLine | Where-Object { $_ } | ForEach-Object { Get-NormalizedCDeclaration $_ }

$actualHeaderFunctions = @(
    [regex]::Matches($headerWithoutComments, '(?ms)(?:uint32_t|uint64_t|int32_t)\s+dps_pwsh_[a-z0-9_]+\s*\([^;]*?\);') |
        ForEach-Object { Get-NormalizedCDeclaration $_.Value }
)
Assert-Sequence -Actual $actualHeaderFunctions -Expected $expectedHeaderFunctions -Description 'C header export signatures'

$headerFunctionsByName = @{}
foreach ($signature in $expectedHeaderFunctions) {
    $name = [regex]::Match($signature, '\bdps_pwsh_[a-z0-9_]+').Value
    $headerFunctionsByName.Add($name, $signature)
}

$expectedHeaderStructs = [ordered]@{
    'dps_pwsh_abi_info' = @('uint32_t size', 'uint32_t abi_version', 'uint64_t feature_flags', 'uint32_t minimum_compatible_abi_version', 'uint32_t reserved')
    'dps_pwsh_utf8_span' = @('const uint8_t* data', 'size_t len')
    'dps_pwsh_data_value' = @('uint32_t size', 'uint32_t kind', 'uint32_t flags', 'uint32_t reserved', 'const uint8_t* data', 'size_t data_len')
    'dps_pwsh_payload_activation' = @('uint32_t size', 'uint32_t trust_policy', 'uint32_t flags', 'uint32_t reserved', 'struct dps_pwsh_utf8_span payload_path', 'struct dps_pwsh_utf8_span manifest_path', 'struct dps_pwsh_utf8_span manifest_sha256')
    'dps_pwsh_call_result' = @('uint32_t size', 'int32_t status', 'uint32_t flags', 'uint32_t reserved', 'uint8_t* diagnostic', 'size_t diagnostic_capacity', 'size_t diagnostic_required', 'size_t diagnostic_written')
    'dps_pwsh_capability_registration' = @('uint32_t size', 'uint32_t flags', 'const struct dps_pwsh_data_value* definitions', 'dps_pwsh_capability_dispatch_callback dispatch', 'dps_pwsh_capability_cancel_callback cancel')
    'dps_pwsh_session_options' = @('uint32_t size', 'uint32_t runspace_mode', 'uint32_t initial_configuration', 'uint32_t history_mode', 'uint32_t error_preference', 'uint32_t warning_preference', 'uint32_t verbose_preference', 'uint32_t debug_preference', 'uint32_t information_preference', 'uint32_t flags', 'uint32_t reserved', 'struct dps_pwsh_utf8_span allowed_module_path', 'uint32_t execution_policy', 'uint32_t configuration_flags', 'struct dps_pwsh_data_value initial_variables', 'struct dps_pwsh_data_value module_imports', 'struct dps_pwsh_data_value allowed_module_paths', 'struct dps_pwsh_utf8_span working_directory', 'struct dps_pwsh_data_value environment')
    'dps_pwsh_session_snapshot' = @('uint32_t size', 'uint32_t state', 'uint32_t runspace_state', 'uint32_t flags', 'uint32_t active_pipeline_count', 'uint32_t event_count', 'uint64_t invocation_count', 'uint64_t history_count')
    'dps_pwsh_session_pool_options' = @('uint32_t size', 'uint32_t minimum_sessions', 'uint32_t maximum_sessions', 'uint32_t flags', 'uint32_t reserved')
}

Assert-Sequence -Actual @(
    [regex]::Matches($headerWithoutComments, '(?m)^struct\s+(dps_pwsh_[a-z0-9_]+)\s*\{') |
        ForEach-Object { $_.Groups[1].Value }
) -Expected @($expectedHeaderStructs.Keys) -Description 'C header ABI structure declarations'

foreach ($structName in $expectedHeaderStructs.Keys) {
    $structMatch = [regex]::Match($headerWithoutComments, "(?s)struct\s+$structName\s*\{(?<body>.*?)\};")
    if (-not $structMatch.Success) {
        throw "C header structure '$structName' is missing."
    }

    $actualFields = @(
        [regex]::Matches($structMatch.Groups['body'].Value, '(?m)^\s*(?<type>(?:const\s+)?(?:struct\s+\w+|dps_pwsh_capability_\w+|u?int\d+_t|size_t)\s*\*?)\s+(?<field>\w+)\s*;') |
            ForEach-Object { Get-NormalizedCDeclaration "$($_.Groups['type'].Value) $($_.Groups['field'].Value)" }
    )
    Assert-Sequence -Actual $actualFields -Expected $expectedHeaderStructs[$structName] -Description "C header structure '$structName'"
}

$expectedFeatures = [ordered]@{
    'DPS_PWSH_FEATURE_STRUCTURED_INVOCATION_ERRORS' = '(UINT64_C(1) << 0)'
    'DPS_PWSH_FEATURE_PER_CALL_DIAGNOSTICS' = '(UINT64_C(1) << 1)'
    'DPS_PWSH_FEATURE_UTF8_SPANS' = '(UINT64_C(1) << 2)'
    'DPS_PWSH_FEATURE_IMMUTABLE_RESULTS' = '(UINT64_C(1) << 3)'
    'DPS_PWSH_FEATURE_TAGGED_VALUES' = '(UINT64_C(1) << 4)'
    'DPS_PWSH_FEATURE_COMMAND_OPTIONS' = '(UINT64_C(1) << 5)'
    'DPS_PWSH_FEATURE_BOUNDED_INPUT' = '(UINT64_C(1) << 6)'
    'DPS_PWSH_FEATURE_INVOCATION_METADATA' = '(UINT64_C(1) << 7)'
    'DPS_PWSH_FEATURE_ASYNC_OPERATIONS' = '(UINT64_C(1) << 8)'
    'DPS_PWSH_FEATURE_PAYLOAD_MANIFEST' = '(UINT64_C(1) << 9)'
    'DPS_PWSH_FEATURE_SESSIONS' = '(UINT64_C(1) << 10)'
    'DPS_PWSH_FEATURE_SESSION_POLLING' = '(UINT64_C(1) << 11)'
    'DPS_PWSH_FEATURE_SESSION_POOL_REJECTION' = '(UINT64_C(1) << 12)'
    'DPS_PWSH_FEATURE_SNAPSHOT_PROJECTIONS' = '(UINT64_C(1) << 13)'
    'DPS_PWSH_FEATURE_SESSION_CONFIGURATION' = '(UINT64_C(1) << 14)'
    'DPS_PWSH_FEATURE_SESSION_VARIABLES' = '(UINT64_C(1) << 15)'
    'DPS_PWSH_FEATURE_CAPABILITY_RPC' = '(UINT64_C(1) << 16)'
}

$expectedConstants = [ordered]@{
    'DPS_PWSH_ABI_VERSION' = '2u'
    'DPS_PWSH_ABI_MINIMUM_COMPATIBLE_VERSION' = '2u'
    'DPS_PWSH_CALL_RESULT_DIAGNOSTIC_TRUNCATED' = 'UINT32_C(1)'
    'DPS_PWSH_RESULT_TERMINATING_FAILURE' = 'UINT32_C(1)'
    'DPS_PWSH_RESULT_SEQUENCE_TRUNCATED' = '(UINT32_C(1) << 1)'
    'DPS_PWSH_RESULT_STREAM_TRUNCATED' = 'UINT32_C(1)'
    'DPS_PWSH_RESULT_RECORD_FIELDS_TRUNCATED' = 'UINT32_C(1)'
    'DPS_PWSH_RESULT_RECORD_SCALAR_VALUE_PRESENT' = '(UINT32_C(1) << 1)'
    'DPS_PWSH_RESULT_RECORD_PROPERTY_BAG_PRESENT' = '(UINT32_C(1) << 2)'
    'DPS_PWSH_RESULT_RECORD_PROPERTY_BAG_TRUNCATED' = '(UINT32_C(1) << 3)'
    'DPS_PWSH_RESULT_RECORD_TYPE_NAMES_TRUNCATED' = '(UINT32_C(1) << 4)'
    'DPS_PWSH_RESULT_RECORD_ERROR_TARGET_VALUE_PRESENT' = '(UINT32_C(1) << 5)'
}

Assert-Sequence -Actual @(
    [regex]::Matches($headerWithoutComments, '(?m)^#define\s+(DPS_PWSH_FEATURE_[A-Z0-9_]+)\s+') |
        ForEach-Object { $_.Groups[1].Value }
) -Expected @($expectedFeatures.Keys) -Description 'C header feature flag declarations'

foreach ($entry in $expectedFeatures.GetEnumerator() + $expectedConstants.GetEnumerator()) {
    $constantMatch = [regex]::Match($headerWithoutComments, "(?m)^\s*#define\s+$([regex]::Escape($entry.Key))\s+(?<value>.+?)\s*$")
    if (-not $constantMatch.Success) {
        throw "C header constant '$($entry.Key)' is missing."
    }
    Assert-Equal -Actual (Get-NormalizedCDeclaration $constantMatch.Groups['value'].Value) -Expected $entry.Value -Description "C header constant '$($entry.Key)'"
}

$expectedStatuses = [ordered]@{
    'DPS_PWSH_SUCCESS' = 0
    'DPS_PWSH_BUFFER_TOO_SMALL' = 1
    'DPS_PWSH_INVALID_ARGUMENT' = -1
    'DPS_PWSH_NOT_INITIALIZED' = -2
    'DPS_PWSH_INCOMPATIBLE_PAYLOAD' = -3
    'DPS_PWSH_INVALID_HANDLE' = -4
    'DPS_PWSH_HOST_FAILURE' = -5
    'DPS_PWSH_MANAGED_FAILURE' = -6
    'DPS_PWSH_PANIC' = -7
    'DPS_PWSH_INPUT_NOT_COMPLETED' = -8
    'DPS_PWSH_BACKPRESSURE' = -9
    'DPS_PWSH_UNSUPPORTED_VALUE' = -10
    'DPS_PWSH_OPERATION_CANCELLED_STATUS' = -11
    'DPS_PWSH_OPERATION_NOT_TERMINAL' = -12
    'DPS_PWSH_PAYLOAD_MANIFEST_INVALID' = -13
    'DPS_PWSH_PAYLOAD_UNTRUSTED' = -14
    'DPS_PWSH_PAYLOAD_HASH_MISMATCH' = -15
    'DPS_PWSH_PAYLOAD_INCOMPATIBLE' = -16
    'DPS_PWSH_UNSUPPORTED_CAPABILITY' = -17
    'DPS_PWSH_SESSION_POLICY_VIOLATION' = -18
}

$statusMatch = [regex]::Match($headerWithoutComments, '(?s)enum\s+dps_pwsh_status\s*\{(?<body>.*?)\};')
if (-not $statusMatch.Success) {
    throw 'C header status enumeration is missing.'
}
$actualStatuses = [ordered]@{}
foreach ($status in [regex]::Matches($statusMatch.Groups['body'].Value, '(?<name>DPS_PWSH_[A-Z0-9_]+)\s*=\s*(?<value>-?\d+)')) {
    $actualStatuses.Add($status.Groups['name'].Value, [int]$status.Groups['value'].Value)
}
Assert-Sequence -Actual @($actualStatuses.Keys) -Expected @($expectedStatuses.Keys) -Description 'C header status enumeration order'
foreach ($statusName in $expectedStatuses.Keys) {
    Assert-Equal -Actual $actualStatuses[$statusName] -Expected $expectedStatuses[$statusName] -Description "C header status '$statusName'"
}

$expectedResultRecordFields = [ordered]@{
    'DPS_PWSH_RESULT_RECORD_DISPLAY_TEXT' = 0
    'DPS_PWSH_RESULT_RECORD_TYPE_NAMES' = 1
    'DPS_PWSH_RESULT_RECORD_FULLY_QUALIFIED_ERROR_ID' = 2
    'DPS_PWSH_RESULT_RECORD_CATEGORY' = 3
    'DPS_PWSH_RESULT_RECORD_EXCEPTION_TYPE' = 4
    'DPS_PWSH_RESULT_RECORD_INVOCATION_NAME' = 5
    'DPS_PWSH_RESULT_RECORD_POSITION_MESSAGE' = 6
    'DPS_PWSH_RESULT_RECORD_SCRIPT_STACK_TRACE' = 7
    'DPS_PWSH_RESULT_RECORD_CATEGORY_REASON' = 8
    'DPS_PWSH_RESULT_RECORD_CATEGORY_ACTIVITY' = 9
    'DPS_PWSH_RESULT_RECORD_CATEGORY_TARGET_NAME' = 10
    'DPS_PWSH_RESULT_RECORD_CATEGORY_TARGET_TYPE' = 11
    'DPS_PWSH_RESULT_RECORD_COMMAND_NAME' = 12
    'DPS_PWSH_RESULT_RECORD_INVOCATION_LINE' = 13
    'DPS_PWSH_RESULT_RECORD_OFFSET_IN_LINE' = 14
    'DPS_PWSH_RESULT_RECORD_PIPELINE_LENGTH' = 15
    'DPS_PWSH_RESULT_RECORD_PIPELINE_POSITION' = 16
    'DPS_PWSH_RESULT_RECORD_ERROR_DETAILS_MESSAGE' = 17
    'DPS_PWSH_RESULT_RECORD_RECOMMENDED_ACTION' = 18
    'DPS_PWSH_RESULT_RECORD_TARGET_DISPLAY_TEXT' = 19
}
$expectedResultValueSlots = [ordered]@{
    'DPS_PWSH_RESULT_RECORD_VALUE_SCALAR' = 0
    'DPS_PWSH_RESULT_RECORD_VALUE_PROPERTY_BAG' = 1
    'DPS_PWSH_RESULT_RECORD_VALUE_ERROR_TARGET' = 2
}
foreach ($enumContract in @(
    @{ Name = 'dps_pwsh_result_record_field'; Values = $expectedResultRecordFields },
    @{ Name = 'dps_pwsh_result_record_value_slot'; Values = $expectedResultValueSlots }
)) {
    $enumMatch = [regex]::Match($headerWithoutComments, "(?s)enum\s+$($enumContract.Name)\s*\{(?<body>.*?)\};")
    if (-not $enumMatch.Success) {
        throw "C header enum '$($enumContract.Name)' is missing."
    }
    $actualValues = [ordered]@{}
    foreach ($entry in [regex]::Matches($enumMatch.Groups['body'].Value, '(?<name>DPS_PWSH_[A-Z0-9_]+)\s*=\s*(?<value>\d+)')) {
        $actualValues.Add($entry.Groups['name'].Value, [int]$entry.Groups['value'].Value)
    }
    Assert-Sequence -Actual @($actualValues.Keys) -Expected @($enumContract.Values.Keys) -Description "C header enum '$($enumContract.Name)' order"
    foreach ($name in $enumContract.Values.Keys) {
        Assert-Equal -Actual $actualValues[$name] -Expected $enumContract.Values[$name] -Description "C header enum '$($enumContract.Name).$name'"
    }
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

$expectedImportedExports = @(
    $expectedHeaderFunctions |
        Where-Object { $_ -match '\bdps_pwsh_get_abi_info\b' -or $_ -match '\bdps_pwsh_v2_' } |
        ForEach-Object { [regex]::Match($_, '\bdps_pwsh_[a-z0-9_]+').Value }
)
Assert-Sequence -Actual @($libraryImports.EntryPoint | Sort-Object) -Expected @($expectedImportedExports | Sort-Object) -Description 'C#/header v2 import export set'
Assert-Equal -Actual $libraryImports.Count -Expected $expectedImportedExports.Count -Description 'C#/header v2 import count'

foreach ($import in $libraryImports) {
    $headerSignature = $headerFunctionsByName[$import.EntryPoint]
    if ($null -eq $headerSignature) {
        throw "Managed import '$($import.EntryPoint)' has no checked C header declaration."
    }

    $signatureMatch = [regex]::Match($headerSignature, '^(?<return>\w+)\s+\w+\((?<parameters>.*)\);$')
    if (-not $signatureMatch.Success) {
        throw "Unable to parse checked C header declaration '$headerSignature'."
    }
    Assert-Equal -Actual (Get-ManagedTypeName $import.Method.ReturnType) -Expected 'System.Int32' -Description "Managed import '$($import.EntryPoint)' return type"

    $expectedParameterTypes = @()
    if ($signatureMatch.Groups['parameters'].Value -ne 'void') {
        $expectedParameterTypes = @(
            $signatureMatch.Groups['parameters'].Value -split ',' |
                ForEach-Object { Convert-CParameterToManagedTypeName $_ }
        )
    }
    $actualParameterTypes = @($import.Method.GetParameters() | ForEach-Object { Get-ManagedTypeName $_.ParameterType })
    Assert-Sequence -Actual $actualParameterTypes -Expected $expectedParameterTypes -Description "Managed import '$($import.EntryPoint)' signature"
}

$cdeclAttributes = [regex]::Matches($nativeMethodsSource, '\[\s*UnmanagedCallConv\s*\(\s*CallConvs\s*=\s*\[\s*typeof\s*\(\s*CallConvCdecl\s*\)\s*\]\s*\)\s*\]')
Assert-Equal -Actual $cdeclAttributes.Count -Expected $expectedImportedExports.Count -Description 'Managed Cdecl import attribute count'

$allRustExports = @(
    [regex]::Matches($rustFfiSource, 'pub\s+(?:unsafe\s+)?extern\s+"C"\s+fn\s+(dps_pwsh_[a-z0-9_]+)\s*\(') |
        ForEach-Object { $_.Groups[1].Value }
)
Assert-Sequence -Actual @($allRustExports | Sort-Object) -Expected @($headerFunctionsByName.Keys | Sort-Object) -Description 'C header/Rust export set'
foreach ($export in $headerFunctionsByName.Keys) {
    $exportPattern = "(?s)#\[no_mangle\](?:\s*#\[[^\]]+\])?\s*pub\s+(?:unsafe\s+)?extern\s+`"C`"\s+fn\s+$([regex]::Escape($export))\s*\("
    if (-not [regex]::IsMatch($rustFfiSource, $exportPattern)) {
        throw "Rust export '$export' is missing #[no_mangle] extern `"C`" linkage."
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
