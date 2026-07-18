using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Devolutions.PowerShell.Ffi;

public sealed unsafe class PowerShell : IDisposable
{
    private const uint RequiredAbiVersion = 2;
    private const int Success = (int)PowerShellFfiStatus.Success;
    private const int BufferTooSmall = (int)PowerShellFfiStatus.BufferTooSmall;
    private const ulong StructuredInvocationErrorsFeature = 1UL << 0;
    private const ulong PerCallDiagnosticsFeature = 1UL << 1;
    private const ulong Utf8SpansFeature = 1UL << 2;
    private const ulong ImmutableResultsFeature = 1UL << 3;
    private const ulong TaggedValuesFeature = 1UL << 4;
    private const ulong CommandOptionsFeature = 1UL << 5;
    private const ulong BoundedInputFeature = 1UL << 6;
    private const ulong InvocationMetadataFeature = 1UL << 7;
    private const ulong AsyncOperationsFeature = 1UL << 8;
    private const ulong PayloadManifestFeature = 1UL << 9;
    private const ulong SessionsFeature = 1UL << 10;
    private const ulong SessionPollingFeature = 1UL << 11;
    private const ulong SessionPoolRejectionFeature = 1UL << 12;
    private const ulong SnapshotProjectionsFeature = 1UL << 13;
    private const ulong SessionConfigurationFeature = 1UL << 14;
    private const ulong SessionVariablesFeature = 1UL << 15;
    private const ulong CapabilityRpcFeature = 1UL << 16;
    private const ulong RequiredFeatures =
        StructuredInvocationErrorsFeature | PerCallDiagnosticsFeature | Utf8SpansFeature |
        ImmutableResultsFeature | TaggedValuesFeature | CommandOptionsFeature | BoundedInputFeature |
        InvocationMetadataFeature | AsyncOperationsFeature | PayloadManifestFeature | SessionsFeature |
        SessionPollingFeature | SessionPoolRejectionFeature | SnapshotProjectionsFeature |
        SessionConfigurationFeature | SessionVariablesFeature | CapabilityRpcFeature;
    private const uint ResultTerminatingFailure = 1;
    private const uint ResultSequenceTruncated = 1 << 1;
    private const uint StreamTruncated = 1;
    private const uint RecordFieldsTruncated = 1;
    private const uint RecordScalarValuePresent = 1 << 1;
    private const uint RecordPropertyBagPresent = 1 << 2;
    private const uint RecordPropertyBagTruncated = 1 << 3;
    private const uint RecordTypeNamesTruncated = 1 << 4;
    private const uint RecordErrorTargetValuePresent = 1 << 5;
    private const uint StreamCount = 7;
    private const uint RecordFieldCount = 20;
    private const uint MaxRecordsPerStream = 32;
    private const uint MaxSequenceRecords = MaxRecordsPerStream * StreamCount;
    private const nuint MaxResultFieldUtf8Bytes = 16 * 1024;
    private const nuint MaxResultValueBytes = 16 * 1024;

    private readonly PowerShellHandle handle;

    internal PowerShell(PowerShellHandle handle)
    {
        this.handle = handle;
    }

    public static uint AbiVersion => GetAbiInfo().AbiVersion;

    public static ulong FeatureFlags => GetAbiInfo().FeatureFlags;

    [Obsolete("Use Initialize(PowerShellPayloadActivationOptions) with a hash-pinned manifest. This overload is unsafe local development compatibility only.")]
    public static void Initialize(string payloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        Initialize(PowerShellPayloadActivationOptions.UnsafeUntrustedLocalDevelopment(
            payloadDirectory,
            Path.Combine(payloadDirectory, "devolutions-pwsh-payload.json")));
    }

    public static void Initialize(PowerShellPayloadActivationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureSupportedAbi();
        byte[] payloadBytes = EncodeUtf8(options.PayloadDirectory);
        byte[] manifestBytes = EncodeUtf8(options.ManifestPath);
        byte[] manifestHashBytes = EncodeUtf8(options.ManifestSha256);
        fixed (byte* payloadPointer = payloadBytes)
        fixed (byte* manifestPointer = manifestBytes)
        fixed (byte* manifestHashPointer = manifestHashBytes)
        {
            NativePayloadActivation activation = new()
            {
                Size = checked((uint)sizeof(NativePayloadActivation)),
                TrustPolicy = checked((uint)options.TrustPolicy),
                PayloadPath = new NativeUtf8Span
                {
                    Data = payloadBytes.Length == 0 ? null : payloadPointer,
                    Length = (nuint)payloadBytes.Length,
                },
                ManifestPath = new NativeUtf8Span
                {
                    Data = manifestBytes.Length == 0 ? null : manifestPointer,
                    Length = (nuint)manifestBytes.Length,
                },
                ManifestSha256 = new NativeUtf8Span
                {
                    Data = manifestHashBytes.Length == 0 ? null : manifestHashPointer,
                    Length = (nuint)manifestHashBytes.Length,
                },
            };
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = NativeMethods.InitializePayload(&activation, &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }
    }

    public static PowerShell Create()
    {
        EnsureSupportedAbi();
        ulong nativeHandle = 0;
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.Create(&nativeHandle, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        return CreateFromNative(nativeHandle);
    }

    public PowerShell AddCommand(string command)
    {
        return AddCommand(command, false);
    }

    public PowerShell AddCommand(string command, bool useLocalScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        InvokeUtf8ForHandle(
            command,
            (nativeHandle, value, result) => NativeMethods.AddCommandWithLocalScope(
                nativeHandle,
                value,
                useLocalScope ? 1u : 0u,
                result));
        return this;
    }

    public PowerShell AddScript(string script)
    {
        return AddScript(script, false);
    }

    public PowerShell AddScript(string script, bool useLocalScope)
    {
        ArgumentNullException.ThrowIfNull(script);
        InvokeUtf8ForHandle(
            script,
            (nativeHandle, value, result) => NativeMethods.AddScriptWithLocalScope(
                nativeHandle,
                value,
                useLocalScope ? 1u : 0u,
                result));
        return this;
    }

    public PowerShell AddArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        InvokeUtf8ForHandle(argument, static (nativeHandle, value, result) => NativeMethods.AddArgument(nativeHandle, value, result));
        return this;
    }

    public PowerShell AddArgument(PowerShellValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        InvokeValueForHandle(value, static (nativeHandle, nativeValue, result) => NativeMethods.AddArgumentValue(nativeHandle, nativeValue, result));
        return this;
    }

    public PowerShell AddArguments(IEnumerable<PowerShellValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (PowerShellValue value in values)
        {
            AddArgument(value);
        }

        return this;
    }

    public PowerShell AddParameter(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        byte[] nameBytes = EncodeUtf8(name);
        byte[] valueBytes = EncodeUtf8(value);
        using PowerShellHandle.HandleLease lease = handle.Borrow();
        fixed (byte* namePointer = nameBytes)
        fixed (byte* valuePointer = valueBytes)
        {
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = NativeMethods.AddParameterString(
                lease.Value,
                new NativeUtf8Span { Data = namePointer, Length = (nuint)nameBytes.Length },
                new NativeUtf8Span { Data = valuePointer, Length = (nuint)valueBytes.Length },
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }

        return this;
    }

    public PowerShell AddParameter(string name, long value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        InvokeUtf8ForHandle(
            name,
            (nativeHandle, valueSpan, result) => NativeMethods.AddParameterInt64(nativeHandle, valueSpan, value, result));
        return this;
    }

    public PowerShell AddParameter(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        InvokeUtf8ForHandle(name, static (nativeHandle, value, result) => NativeMethods.AddParameterSwitch(nativeHandle, value, result));
        return this;
    }

    public PowerShell AddParameter(string name, PowerShellValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        byte[] nameBytes = EncodeUtf8(name);
        byte[] payload = value.Payload;
        using PowerShellHandle.HandleLease lease = handle.Borrow();
        fixed (byte* namePointer = nameBytes)
        fixed (byte* payloadPointer = payload)
        {
            NativeDataValue nativeValue = CreateNativeValue(value, payloadPointer);
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = NativeMethods.AddParameterValue(
                lease.Value,
                new NativeUtf8Span { Data = namePointer, Length = (nuint)nameBytes.Length },
                &nativeValue,
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }

        return this;
    }

    public PowerShell AddParameters(IEnumerable<KeyValuePair<string, PowerShellValue>> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (KeyValuePair<string, PowerShellValue> parameter in parameters)
        {
            AddParameter(parameter.Key, parameter.Value);
        }

        return this;
    }

    public PowerShell AddStatement()
    {
        InvokeForHandle(static (nativeHandle, result) => NativeMethods.AddStatement(nativeHandle, result));
        return this;
    }

    public PowerShell WithCapabilities(PowerShellCapabilitySet capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        EnsureCapabilityRpcSupported();
        using PowerShellHandle.HandleLease lease = handle.Borrow();
        capabilities.AttachTo(lease.Value);
        return this;
    }

    public PowerShell Clear()
    {
        InvokeForHandle(static (nativeHandle, result) => NativeMethods.Clear(nativeHandle, result));
        return this;
    }

    public PowerShell AddInput(PowerShellValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        InvokeValueForHandle(value, static (nativeHandle, nativeValue, result) => NativeMethods.AddInputValue(nativeHandle, nativeValue, result));
        return this;
    }

    public PowerShell AddInputs(IEnumerable<PowerShellValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (PowerShellValue value in values)
        {
            AddInput(value);
        }

        return this;
    }

    public PowerShell CompleteInput()
    {
        InvokeForHandle(static (nativeHandle, result) => NativeMethods.CompleteInput(nativeHandle, result));
        return this;
    }

    public PowerShell ResetInput()
    {
        InvokeForHandle(static (nativeHandle, result) => NativeMethods.ResetInput(nativeHandle, result));
        return this;
    }

    public PowerShellInvocationResult Invoke()
    {
        return InvokeWithDiagnostics();
    }

    public PowerShellInvocationResult InvokeWithDiagnostics()
    {
        using PowerShellHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong nativeResultHandle = 0;
        int status = NativeMethods.InvokeToResult(lease.Value, &nativeResultHandle, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);

        using var invocationResultHandle = new PowerShellInvocationResultHandle(nativeResultHandle);
        PowerShellInvocationResult invocationResult = ReadInvocationResult(invocationResultHandle);
        if (invocationResult.IsTerminatingFailure)
        {
            throw new PowerShellInvocationException(
                PowerShellFfiStatus.ManagedFailure,
                "PowerShell invocation terminated.",
                invocationResult);
        }

        return invocationResult;
    }

    public PowerShellInvocationOperation BeginInvoke()
    {
        using PowerShellHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong nativeOperationHandle = 0;
        int status = NativeMethods.InvokeAsync(lease.Value, &nativeOperationHandle, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        return new PowerShellInvocationOperation(new PowerShellOperationHandle(nativeOperationHandle));
    }

    public Task<PowerShellInvocationResult> InvokeAsync(CancellationToken cancellationToken = default)
    {
        return PowerShellAsyncOperationAwaiter.GetResultAsync(BeginInvoke(), cancellationToken, releaseWhenComplete: true);
    }

    public string InvokeText()
    {
        PowerShellInvocationResult invocationResult = InvokeWithDiagnostics();
        var output = new StringBuilder();
        foreach (PowerShellObjectSnapshot record in invocationResult.Output.Records)
        {
            output.AppendLine(record.DisplayText);
        }

        return output.ToString();
    }

    public void Stop()
    {
        InvokeForHandle(static (nativeHandle, result) => NativeMethods.Stop(nativeHandle, result));
    }

    public void Dispose()
    {
        handle.Dispose();
    }

    private static NativeAbiInfo GetAbiInfo()
    {
        NativeAbiInfo info = new() { Size = checked((uint)sizeof(NativeAbiInfo)) };
        int status = NativeMethods.GetAbiInfo(&info);
        if (status != Success)
        {
            throw new PowerShellFfiException((PowerShellFfiStatus)status, "Native PowerShell FFI ABI metadata is unavailable.");
        }

        return info;
    }

    internal static void EnsureSupportedAbi()
    {
        EnsureSupportedAbi(GetAbiInfo());
    }

    internal static void EnsureCapabilityRpcSupported()
    {
        EnsureSupportedAbi();
        if ((FeatureFlags & CapabilityRpcFeature) == 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.UnsupportedCapability,
                "The selected PowerShell payload does not support bounded capability RPC.");
        }
    }

    private static void EnsureSupportedAbi(NativeAbiInfo info)
    {
        if (info.AbiVersion != RequiredAbiVersion ||
            info.MinimumCompatibleAbiVersion > RequiredAbiVersion ||
            (info.FeatureFlags & RequiredFeatures) != RequiredFeatures)
        {
            throw new NotSupportedException(
                $"Native PowerShell FFI ABI {info.AbiVersion} does not support facade ABI {RequiredAbiVersion} structured errors, diagnostics, UTF-8, value, command, input, result, async operation, payload manifest, and session features.");
        }
    }

    internal static PowerShell CreateFromNative(ulong nativeHandle)
    {
        if (nativeHandle == 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an invalid builder handle.");
        }

        return new PowerShell(new PowerShellHandle(nativeHandle));
    }

    internal static PowerShellInvocationResult ReadInvocationResult(PowerShellInvocationResultHandle resultHandle)
    {
        using PowerShellInvocationResultHandle.HandleLease lease = resultHandle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult callResult = NativeCall.CreateResult(diagnostic);
        uint resultFlags = 0;
        uint sequenceCount = 0;
        int status = NativeMethods.GetInvocationResultInfo(lease.Value, &resultFlags, &sequenceCount, &callResult);
        NativeCall.ThrowIfFailed(status, callResult, diagnostic);
        uint nativeState = 0;
        ulong invocationId = 0;
        uint hadErrors = 0;
        callResult = NativeCall.CreateResult(diagnostic);
        status = NativeMethods.GetInvocationResultMetadata(
            lease.Value,
            &nativeState,
            &invocationId,
            &hadErrors,
            &callResult);
        NativeCall.ThrowIfFailed(status, callResult, diagnostic);
        if (nativeState is not (uint)PowerShellInvocationState.Completed and not (uint)PowerShellInvocationState.Terminated ||
            hadErrors > 1)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned invalid invocation metadata.");
        }
        if (sequenceCount > MaxSequenceRecords)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an unbounded invocation sequence count.");
        }

        var streams = new NativeStreamSnapshot[checked((int)StreamCount)];
        for (uint stream = 0; stream < StreamCount; stream++)
        {
            streams[checked((int)stream)] = ReadStreamSnapshot(lease.Value, stream);
        }

        var sequence = new List<PowerShellStreamSequenceRecord>(checked((int)sequenceCount));
        for (uint index = 0; index < sequenceCount; index++)
        {
            callResult = NativeCall.CreateResult(diagnostic);
            uint stream = 0;
            uint recordIndex = 0;
            ulong recordSequence = 0;
            status = NativeMethods.GetInvocationResultSequenceRecord(
                lease.Value,
                index,
                &stream,
                &recordIndex,
                &recordSequence,
                &callResult);
            NativeCall.ThrowIfFailed(status, callResult, diagnostic);
            if (stream >= StreamCount || recordIndex >= streams[checked((int)stream)].Records.Count)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned an invalid invocation sequence record.");
            }

            sequence.Add(new PowerShellStreamSequenceRecord(
                (PowerShellStreamKind)stream,
                recordIndex,
                recordSequence));
        }

        return new PowerShellInvocationResult(
            CreateOutputSnapshot(streams[(int)PowerShellStreamKind.Output]),
            CreateErrorSnapshot(streams[(int)PowerShellStreamKind.Error]),
            CreateTextSnapshot(streams[(int)PowerShellStreamKind.Warning], PowerShellStreamKind.Warning),
            CreateTextSnapshot(streams[(int)PowerShellStreamKind.Verbose], PowerShellStreamKind.Verbose),
            CreateTextSnapshot(streams[(int)PowerShellStreamKind.Debug], PowerShellStreamKind.Debug),
            CreateTextSnapshot(streams[(int)PowerShellStreamKind.Information], PowerShellStreamKind.Information),
            CreateTextSnapshot(streams[(int)PowerShellStreamKind.Progress], PowerShellStreamKind.Progress),
            sequence.AsReadOnly(),
            (PowerShellInvocationState)nativeState,
            invocationId,
            hadErrors != 0,
            (resultFlags & ResultTerminatingFailure) != 0,
            (resultFlags & ResultSequenceTruncated) != 0);
    }

    private static NativeStreamSnapshot ReadStreamSnapshot(ulong resultHandle, uint stream)
    {
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult callResult = NativeCall.CreateResult(diagnostic);
        uint recordCount = 0;
        uint streamFlags = 0;
        int status = NativeMethods.GetInvocationResultStreamInfo(
            resultHandle,
            stream,
            &recordCount,
            &streamFlags,
            &callResult);
        NativeCall.ThrowIfFailed(status, callResult, diagnostic);
        if (recordCount > MaxRecordsPerStream)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an unbounded invocation stream count.");
        }

        callResult = NativeCall.CreateResult(diagnostic);
        ulong totalRecordCount = 0;
        ulong droppedRecordCount = 0;
        status = NativeMethods.GetInvocationResultStreamTotals(
            resultHandle,
            stream,
            &totalRecordCount,
            &droppedRecordCount,
            &callResult);
        NativeCall.ThrowIfFailed(status, callResult, diagnostic);
        if (totalRecordCount < recordCount || droppedRecordCount > totalRecordCount)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned invalid invocation stream totals.");
        }

        var records = new List<NativeStreamRecord>(checked((int)recordCount));
        for (uint index = 0; index < recordCount; index++)
        {
            callResult = NativeCall.CreateResult(diagnostic);
            ulong sequence = 0;
            uint recordFlags = 0;
            status = NativeMethods.GetInvocationResultStreamRecordInfo(
                resultHandle,
                stream,
                index,
                &sequence,
                &recordFlags,
                &callResult);
            NativeCall.ThrowIfFailed(status, callResult, diagnostic);

            var fields = new string[checked((int)RecordFieldCount)];
            for (uint field = 0; field < RecordFieldCount; field++)
            {
                fields[field] = ReadStreamRecordField(resultHandle, stream, index, field);
            }

            callResult = NativeCall.CreateResult(diagnostic);
            uint propertyEntryCount = 0;
            uint droppedPropertyEntryCount = 0;
            uint typeNameCount = 0;
            uint droppedTypeNameCount = 0;
            uint projectionFlags = 0;
            status = NativeMethods.GetInvocationResultStreamRecordProjectionInfo(
                resultHandle,
                stream,
                index,
                &propertyEntryCount,
                &droppedPropertyEntryCount,
                &typeNameCount,
                &droppedTypeNameCount,
                &projectionFlags,
                &callResult);
            NativeCall.ThrowIfFailed(status, callResult, diagnostic);

            PowerShellValue? scalarValue = (projectionFlags & RecordScalarValuePresent) != 0
                ? ReadStreamRecordValue(resultHandle, stream, index, 0)
                : null;
            PowerShellValue? propertyBag = (projectionFlags & RecordPropertyBagPresent) != 0
                ? ReadStreamRecordValue(resultHandle, stream, index, 1)
                : null;
            PowerShellValue? errorTargetValue = (projectionFlags & RecordErrorTargetValuePresent) != 0
                ? ReadStreamRecordValue(resultHandle, stream, index, 2)
                : null;

            records.Add(new NativeStreamRecord(
                sequence,
                recordFlags,
                projectionFlags,
                propertyEntryCount,
                droppedPropertyEntryCount,
                typeNameCount,
                droppedTypeNameCount,
                scalarValue,
                propertyBag,
                errorTargetValue,
                fields));
        }

        return new NativeStreamSnapshot(streamFlags, totalRecordCount, droppedRecordCount, records);
    }

    private static string ReadStreamRecordField(ulong resultHandle, uint stream, uint recordIndex, uint field)
    {
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult callResult = NativeCall.CreateResult(diagnostic);
        nuint requiredLength = 0;
        int status = NativeMethods.CopyInvocationResultStreamRecordField(
            resultHandle,
            stream,
            recordIndex,
            field,
            null,
            0,
            &requiredLength,
            &callResult);
        if (status != Success && status != BufferTooSmall)
        {
            NativeCall.ThrowIfFailed(status, callResult, diagnostic);
        }

        if (requiredLength > MaxResultFieldUtf8Bytes)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an unbounded invocation stream field.");
        }

        byte[] value = new byte[checked((int)requiredLength)];
        fixed (byte* valuePointer = value)
        {
            callResult = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.CopyInvocationResultStreamRecordField(
                resultHandle,
                stream,
                recordIndex,
                field,
                valuePointer,
                (nuint)value.Length,
                &requiredLength,
                &callResult);
            NativeCall.ThrowIfFailed(status, callResult, diagnostic);
        }

        return Encoding.UTF8.GetString(value, 0, checked((int)requiredLength));
    }

    private static PowerShellValue ReadStreamRecordValue(ulong resultHandle, uint stream, uint recordIndex, uint valueSlot)
    {
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult callResult = NativeCall.CreateResult(diagnostic);
        uint kind = 0;
        nuint requiredLength = 0;
        int status = NativeMethods.CopyInvocationResultStreamRecordValue(
            resultHandle,
            stream,
            recordIndex,
            valueSlot,
            &kind,
            null,
            0,
            &requiredLength,
            &callResult);
        if (status != Success && status != BufferTooSmall)
        {
            NativeCall.ThrowIfFailed(status, callResult, diagnostic);
        }

        if (requiredLength > MaxResultValueBytes)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an unbounded invocation snapshot value.");
        }

        byte[] payload = new byte[checked((int)requiredLength)];
        fixed (byte* payloadPointer = payload)
        {
            callResult = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.CopyInvocationResultStreamRecordValue(
                resultHandle,
                stream,
                recordIndex,
                valueSlot,
                &kind,
                payloadPointer,
                (nuint)payload.Length,
                &requiredLength,
                &callResult);
            NativeCall.ThrowIfFailed(status, callResult, diagnostic);
        }

        return PowerShellValue.FromNative(kind, payload);
    }

    private static PowerShellStreamSnapshot<PowerShellObjectSnapshot> CreateOutputSnapshot(NativeStreamSnapshot snapshot)
    {
        var output = new PowerShellObjectSnapshot[snapshot.Records.Count];
        for (int index = 0; index < output.Length; index++)
        {
            NativeStreamRecord record = snapshot.Records[index];
            string[] typeNames = record.Fields[1].Length == 0
                ? Array.Empty<string>()
                : record.Fields[1].Split('\n');
            output[index] = new PowerShellObjectSnapshot(
                record.Fields[0],
                typeNames,
                record.Sequence,
                (record.Flags & RecordFieldsTruncated) != 0,
                record.ScalarValue,
                record.PropertyBag,
                record.PropertyEntryCount,
                record.DroppedPropertyEntryCount,
                record.TypeNameCount,
                record.DroppedTypeNameCount,
                (record.ProjectionFlags & RecordPropertyBagTruncated) != 0);
        }

        return new PowerShellStreamSnapshot<PowerShellObjectSnapshot>(
            PowerShellStreamKind.Output,
            output,
            (snapshot.Flags & StreamTruncated) != 0,
            snapshot.TotalRecordCount,
            snapshot.DroppedRecordCount);
    }

    private static PowerShellStreamSnapshot<PowerShellInvocationError> CreateErrorSnapshot(NativeStreamSnapshot snapshot)
    {
        var errors = new PowerShellInvocationError[snapshot.Records.Count];
        for (int index = 0; index < errors.Length; index++)
        {
            NativeStreamRecord record = snapshot.Records[index];
            errors[index] = new PowerShellInvocationError(
                record.Fields[0],
                record.Fields[2],
                record.Fields[3],
                record.Fields[4],
                record.Fields[5],
                record.Fields[6],
                record.Fields[7],
                record.Fields[8],
                record.Fields[9],
                record.Fields[10],
                record.Fields[11],
                record.Fields[12],
                record.Fields[13],
                record.Fields[14],
                record.Fields[15],
                record.Fields[16],
                record.Fields[17],
                record.Fields[18],
                record.Fields[19],
                record.ErrorTargetValue,
                record.Sequence,
                (record.Flags & RecordFieldsTruncated) != 0);
        }

        return new PowerShellStreamSnapshot<PowerShellInvocationError>(
            PowerShellStreamKind.Error,
            errors,
            (snapshot.Flags & StreamTruncated) != 0,
            snapshot.TotalRecordCount,
            snapshot.DroppedRecordCount);
    }

    private static PowerShellStreamSnapshot<PowerShellStreamRecord> CreateTextSnapshot(
        NativeStreamSnapshot snapshot,
        PowerShellStreamKind stream)
    {
        var records = new PowerShellStreamRecord[snapshot.Records.Count];
        for (int index = 0; index < records.Length; index++)
        {
            NativeStreamRecord record = snapshot.Records[index];
            records[index] = new PowerShellStreamRecord(
                record.Fields[0],
                record.Sequence,
                (record.Flags & RecordFieldsTruncated) != 0);
        }

        return new PowerShellStreamSnapshot<PowerShellStreamRecord>(
            stream,
            records,
            (snapshot.Flags & StreamTruncated) != 0,
            snapshot.TotalRecordCount,
            snapshot.DroppedRecordCount);
    }

    private sealed class NativeStreamSnapshot
    {
        internal NativeStreamSnapshot(uint flags, ulong totalRecordCount, ulong droppedRecordCount, List<NativeStreamRecord> records)
        {
            Flags = flags;
            TotalRecordCount = totalRecordCount;
            DroppedRecordCount = droppedRecordCount;
            Records = records;
        }

        internal uint Flags { get; }

        internal ulong TotalRecordCount { get; }

        internal ulong DroppedRecordCount { get; }

        internal List<NativeStreamRecord> Records { get; }
    }

    private sealed class NativeStreamRecord
    {
        internal NativeStreamRecord(
            ulong sequence,
            uint flags,
            uint projectionFlags,
            uint propertyEntryCount,
            uint droppedPropertyEntryCount,
            uint typeNameCount,
            uint droppedTypeNameCount,
            PowerShellValue? scalarValue,
            PowerShellValue? propertyBag,
            PowerShellValue? errorTargetValue,
            string[] fields)
        {
            Sequence = sequence;
            Flags = flags;
            ProjectionFlags = projectionFlags;
            PropertyEntryCount = propertyEntryCount;
            DroppedPropertyEntryCount = droppedPropertyEntryCount;
            TypeNameCount = typeNameCount;
            DroppedTypeNameCount = droppedTypeNameCount;
            ScalarValue = scalarValue;
            PropertyBag = propertyBag;
            ErrorTargetValue = errorTargetValue;
            Fields = fields;
        }

        internal ulong Sequence { get; }

        internal uint Flags { get; }

        internal uint ProjectionFlags { get; }

        internal uint PropertyEntryCount { get; }

        internal uint DroppedPropertyEntryCount { get; }

        internal uint TypeNameCount { get; }

        internal uint DroppedTypeNameCount { get; }

        internal PowerShellValue? ScalarValue { get; }

        internal PowerShellValue? PropertyBag { get; }

        internal PowerShellValue? ErrorTargetValue { get; }

        internal string[] Fields { get; }
    }

    private void InvokeValueForHandle(PowerShellValue value, NativeValueHandleOperation operation)
    {
        byte[] payload = value.Payload;
        using PowerShellHandle.HandleLease lease = handle.Borrow();
        fixed (byte* payloadPointer = payload)
        {
            NativeDataValue nativeValue = CreateNativeValue(value, payloadPointer);
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = operation(lease.Value, &nativeValue, &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }
    }

    internal static NativeDataValue CreateNativeValue(PowerShellValue value, byte* payload)
    {
        return new NativeDataValue
        {
            Size = checked((uint)sizeof(NativeDataValue)),
            Kind = (uint)value.Kind,
            Flags = 0,
            Reserved = 0,
            Data = value.Payload.Length == 0 ? null : payload,
            DataLength = (nuint)value.Payload.Length,
        };
    }

    private void InvokeForHandle(NativeHandleOperation operation)
    {
        using PowerShellHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = operation(lease.Value, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
    }

    private void InvokeUtf8ForHandle(string value, NativeUtf8HandleOperation operation)
    {
        byte[] bytes = EncodeUtf8(value);
        using PowerShellHandle.HandleLease lease = handle.Borrow();
        fixed (byte* pointer = bytes)
        {
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = operation(
                lease.Value,
                new NativeUtf8Span { Data = pointer, Length = (nuint)bytes.Length },
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }
    }

    private static void InvokeUtf8(string value, NativeUtf8Operation operation)
    {
        byte[] bytes = EncodeUtf8(value);
        fixed (byte* pointer = bytes)
        {
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = operation(
                new NativeUtf8Span { Data = pointer, Length = (nuint)bytes.Length },
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }
    }

    internal static byte[] EncodeUtf8(string value)
    {
        if (value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Values passed to the native PowerShell API cannot contain NUL characters.", nameof(value));
        }

        return Encoding.UTF8.GetBytes(value);
    }

    private unsafe delegate int NativeHandleOperation(ulong nativeHandle, NativeCallResult* result);

    private unsafe delegate int NativeUtf8HandleOperation(
        ulong nativeHandle,
        NativeUtf8Span value,
        NativeCallResult* result);

    private unsafe delegate int NativeUtf8Operation(NativeUtf8Span value, NativeCallResult* result);

    private unsafe delegate int NativeValueHandleOperation(
        ulong nativeHandle,
        NativeDataValue* value,
        NativeCallResult* result);
}
