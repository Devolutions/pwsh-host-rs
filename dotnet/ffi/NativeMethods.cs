using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Devolutions.PowerShell.Ffi;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAbiInfo
{
    internal uint Size;
    internal uint AbiVersion;
    internal ulong FeatureFlags;
    internal uint MinimumCompatibleAbiVersion;
    internal uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeUtf8Span
{
    internal byte* Data;
    internal nuint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDataValue
{
    internal uint Size;
    internal uint Kind;
    internal uint Flags;
    internal uint Reserved;
    internal byte* Data;
    internal nuint DataLength;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCapabilityRegistration
{
    internal uint Size;
    internal uint Flags;
    internal NativeDataValue* Definitions;
    internal nint DispatchCallback;
    internal nint CancelCallback;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePayloadActivation
{
    internal uint Size;
    internal uint TrustPolicy;
    internal uint Flags;
    internal uint Reserved;
    internal NativeUtf8Span PayloadPath;
    internal NativeUtf8Span ManifestPath;
    internal NativeUtf8Span ManifestSha256;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCallResult
{
    internal uint Size;
    internal int Status;
    internal uint Flags;
    internal uint Reserved;
    internal byte* Diagnostic;
    internal nuint DiagnosticCapacity;
    internal nuint DiagnosticRequired;
    internal nuint DiagnosticWritten;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSessionOptions
{
    internal uint Size;
    internal uint RunspaceMode;
    internal uint InitialConfiguration;
    internal uint HistoryMode;
    internal uint ErrorPreference;
    internal uint WarningPreference;
    internal uint VerbosePreference;
    internal uint DebugPreference;
    internal uint InformationPreference;
    internal uint Flags;
    internal uint Reserved;
    internal NativeUtf8Span AllowedModulePath;
    internal uint ExecutionPolicy;
    internal uint ConfigurationFlags;
    internal NativeDataValue InitialVariables;
    internal NativeDataValue ModuleImports;
    internal NativeDataValue AllowedModulePaths;
    internal NativeUtf8Span WorkingDirectory;
    internal NativeDataValue Environment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSessionSnapshot
{
    internal uint Size;
    internal uint State;
    internal uint RunspaceState;
    internal uint Flags;
    internal uint ActivePipelineCount;
    internal uint EventCount;
    internal ulong InvocationCount;
    internal ulong HistoryCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSessionPoolOptions
{
    internal uint Size;
    internal uint MinimumSessions;
    internal uint MaximumSessions;
    internal uint Flags;
    internal uint Reserved;
}

internal static unsafe partial class NativeMethods
{
    internal const string LibraryName = "multi-pwsh-sdk";

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_get_abi_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetAbiInfo(NativeAbiInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_initialize_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Initialize(NativeUtf8Span payloadPath, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_initialize_payload")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int InitializePayload(NativePayloadActivation* activation, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Create(ulong* handle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Release(ulong handle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_command_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddCommand(ulong handle, NativeUtf8Span command, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_script_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddScript(ulong handle, NativeUtf8Span script, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_argument_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddArgument(ulong handle, NativeUtf8Span argument, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_parameter_string_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddParameterString(
        ulong handle,
        NativeUtf8Span name,
        NativeUtf8Span value,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_parameter_i64")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddParameterInt64(
        ulong handle,
        NativeUtf8Span name,
        long value,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_command_utf8_local")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddCommandWithLocalScope(
        ulong handle,
        NativeUtf8Span command,
        uint useLocalScope,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_script_utf8_local")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddScriptWithLocalScope(
        ulong handle,
        NativeUtf8Span script,
        uint useLocalScope,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_argument_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddArgumentValue(
        ulong handle,
        NativeDataValue* value,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_parameter_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddParameterValue(
        ulong handle,
        NativeUtf8Span name,
        NativeDataValue* value,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_parameter_switch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddParameterSwitch(
        ulong handle,
        NativeUtf8Span name,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_input_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddInputValue(
        ulong handle,
        NativeDataValue* value,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_complete_input")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CompleteInput(ulong handle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_reset_input")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ResetInput(ulong handle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_add_statement")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AddStatement(ulong handle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_clear")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Clear(ulong handle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_invoke_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Invoke(
        ulong handle,
        byte* buffer,
        nuint bufferLength,
        nuint* requiredLength,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_invoke")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int InvokeToResult(
        ulong handle,
        ulong* resultHandle,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReleaseInvocationResult(ulong resultHandle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_get_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInvocationResultInfo(
        ulong resultHandle,
        uint* flags,
        uint* sequenceCount,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_get_metadata")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInvocationResultMetadata(
        ulong resultHandle,
        uint* state,
        ulong* invocationId,
        uint* hadErrors,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_get_stream_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInvocationResultStreamInfo(
        ulong resultHandle,
        uint stream,
        uint* recordCount,
        uint* flags,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_get_stream_record_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInvocationResultStreamRecordInfo(
        ulong resultHandle,
        uint stream,
        uint recordIndex,
        ulong* sequence,
        uint* flags,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_copy_stream_record_field_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CopyInvocationResultStreamRecordField(
        ulong resultHandle,
        uint stream,
        uint recordIndex,
        uint field,
        byte* buffer,
        nuint bufferLength,
        nuint* requiredLength,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_get_stream_totals")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInvocationResultStreamTotals(
        ulong resultHandle,
        uint stream,
        ulong* totalRecordCount,
        ulong* droppedRecordCount,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_get_stream_record_projection_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInvocationResultStreamRecordProjectionInfo(
        ulong resultHandle,
        uint stream,
        uint recordIndex,
        uint* propertyEntryCount,
        uint* droppedPropertyEntryCount,
        uint* typeNameCount,
        uint* droppedTypeNameCount,
        uint* projectionFlags,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_copy_stream_record_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CopyInvocationResultStreamRecordValue(
        ulong resultHandle,
        uint stream,
        uint recordIndex,
        uint valueSlot,
        uint* kind,
        byte* buffer,
        nuint bufferLength,
        nuint* requiredLength,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_result_get_sequence_record")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInvocationResultSequenceRecord(
        ulong resultHandle,
        uint sequenceIndex,
        uint* stream,
        uint* recordIndex,
        ulong* sequence,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_get_invocation_error_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetInvocationErrorCount(
        ulong handle,
        uint* errorCount,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_copy_invocation_error_field_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CopyInvocationErrorField(
        ulong handle,
        uint errorIndex,
        uint field,
        byte* buffer,
        nuint bufferLength,
        nuint* requiredLength,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Stop(ulong handle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_invoke_async")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int InvokeAsync(
        ulong handle,
        ulong* operationHandle,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_operation_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReleaseOperation(ulong operationHandle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_operation_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int StopOperation(ulong operationHandle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_operation_poll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int PollOperation(
        ulong operationHandle,
        uint* operationState,
        int* terminalStatus,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_operation_wait")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int WaitOperation(
        ulong operationHandle,
        uint timeoutMilliseconds,
        uint* operationState,
        int* terminalStatus,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_operation_get_result")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetOperationResult(
        ulong operationHandle,
        ulong* resultHandle,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreateSession(
        NativeSessionOptions* options,
        ulong* sessionHandle,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReleaseSession(ulong sessionHandle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_create_builder")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreateSessionBuilder(
        ulong sessionHandle,
        ulong* builderHandle,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_get_snapshot")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetSessionSnapshot(
        ulong sessionHandle,
        NativeSessionSnapshot* snapshot,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_get_event_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetSessionEventInfo(
        ulong sessionHandle,
        uint eventIndex,
        ulong* sequence,
        uint* state,
        uint* flags,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_set_variable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SetSessionVariable(
        ulong sessionHandle,
        NativeUtf8Span name,
        NativeDataValue* value,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_remove_variable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RemoveSessionVariable(
        ulong sessionHandle,
        NativeUtf8Span name,
        uint* removed,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_get_variable_snapshot")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetSessionVariableSnapshot(
        ulong sessionHandle,
        NativeUtf8Span name,
        uint* found,
        uint* kind,
        byte* buffer,
        nuint bufferLength,
        nuint* requiredLength,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_capability_register")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RegisterCapabilities(
        NativeCapabilityRegistration* registration,
        ulong* capabilityHandle,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_capability_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReleaseCapabilities(ulong capabilityHandle, NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_set_capabilities")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int SetCapabilities(
        ulong builderHandle,
        ulong capabilityHandle,
        NativeCallResult* result);

    [LibraryImport(LibraryName, EntryPoint = "dps_pwsh_v2_session_pool_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreateSessionPool(
        NativeSessionPoolOptions* options,
        ulong* poolHandle,
        NativeCallResult* result);
}
