namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A native-backed, bounded acknowledgement reader for copied PowerShell values.
/// </summary>
public sealed unsafe class PowerShellTypedResultInvocation : IDisposable
{
    private const uint Terminal = 1;
    private const uint Truncated = 1 << 1;
    private const uint Complete = 1 << 2;
    private readonly PowerShellTypedResultInvocationHandle handle;
    private readonly int maximumPageRecords;

    internal PowerShellTypedResultInvocation(ulong nativeHandle, PowerShellValuePagerOptions options)
    {
        handle = new PowerShellTypedResultInvocationHandle(nativeHandle);
        maximumPageRecords = options.MaximumPageRecords;
    }

    /// <summary>
    /// Acknowledges <paramref name="acknowledgedThrough"/> from the prior page and
    /// returns the next ordered page of copied tagged values.
    /// </summary>
    public PowerShellValuePage Read(ulong acknowledgedThrough, int? maximumRecords = null)
    {
        PowerShell.EnsureTypedResultPagingSupported();
        int limit = maximumRecords ?? maximumPageRecords;
        if (limit < 1 || limit > maximumPageRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        using PowerShellTypedResultInvocationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong nativePageHandle = 0;
        int status = NativeMethods.ReadTypedResultPage(
            lease.Value,
            acknowledgedThrough,
            checked((uint)limit),
            &nativePageHandle,
            &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        if (nativePageHandle == 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an invalid typed result page handle.");
        }

        using var pageHandle = new PowerShellTypedResultPageHandle(nativePageHandle);
        NativeTypedResultPageInfo info = new()
        {
            Size = checked((uint)sizeof(NativeTypedResultPageInfo)),
        };
        result = NativeCall.CreateResult(diagnostic);
        status = NativeMethods.GetTypedResultPageInfo(pageHandle.Value, &info, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);

        if (!Enum.IsDefined((PowerShellFfiStatus)info.TerminalStatus) ||
            info.AcknowledgedSequence != acknowledgedThrough ||
            info.NextSequence < info.AcknowledgedSequence ||
            info.NextSequence > info.TotalRecordCount ||
            info.DroppedRecordCount > info.TotalRecordCount ||
            info.RecordCount > (uint)limit ||
            (info.Flags & ~(Terminal | Truncated | Complete)) != 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned invalid typed result page metadata.");
        }

        bool isTerminal = (info.Flags & Terminal) != 0;
        bool isTruncated = (info.Flags & Truncated) != 0;
        bool isComplete = (info.Flags & Complete) != 0;
        if ((!isTerminal && info.TerminalStatus != (int)PowerShellFfiStatus.Success) ||
            (isComplete && (!isTerminal ||
                            info.TerminalStatus != (int)PowerShellFfiStatus.Success ||
                            isTruncated ||
                            info.DroppedRecordCount != 0 ||
                            info.TotalRecordCount != info.AcknowledgedSequence)))
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned inconsistent typed result terminal metadata.");
        }

        var records = new PowerShellValuePageRecord[checked((int)info.RecordCount)];
        ulong previousSequence = info.AcknowledgedSequence;
        for (uint index = 0; index < info.RecordCount; index++)
        {
            ulong sequence = 0;
            uint kind = 0;
            result = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.GetTypedResultPageRecordInfo(
                pageHandle.Value,
                index,
                &sequence,
                &kind,
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
            if (!Enum.IsDefined((PowerShellValueKind)kind) ||
                sequence <= previousSequence ||
                sequence > info.NextSequence)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned unordered typed result records.");
            }

            records[checked((int)index)] = new PowerShellValuePageRecord(
                sequence,
                ReadValue(pageHandle.Value, index, kind, diagnostic));
            previousSequence = sequence;
        }

        if (records.Length == 0 ? info.NextSequence != info.AcknowledgedSequence : info.NextSequence != previousSequence)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an inconsistent typed result cursor.");
        }

        return new PowerShellValuePage(
            Array.AsReadOnly(records),
            info.AcknowledgedSequence,
            info.NextSequence,
            info.TotalRecordCount,
            (PowerShellFfiStatus)info.TerminalStatus,
            isTerminal,
            info.DroppedRecordCount,
            isTruncated,
            isComplete);
    }

    public void Stop()
    {
        PowerShell.EnsureTypedResultPagingSupported();
        using PowerShellTypedResultInvocationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.StopTypedResultInvocation(lease.Value, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
    }

    public void Dispose()
    {
        handle.Dispose();
    }

    internal static PowerShellValue ReadValue(
        ulong pageHandle,
        uint recordIndex,
        uint expectedKind,
        byte* diagnostic)
    {
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        uint kind = 0;
        nuint requiredLength = 0;
        int status = NativeMethods.CopyTypedResultPageRecordValue(
            pageHandle,
            recordIndex,
            &kind,
            null,
            0,
            &requiredLength,
            &result);
        if (status != (int)PowerShellFfiStatus.Success &&
            status != (int)PowerShellFfiStatus.BufferTooSmall)
        {
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }

        if (kind != expectedKind || requiredLength > PowerShellValue.MaximumPayloadLength)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an invalid typed result value.");
        }

        byte[] payload = new byte[checked((int)requiredLength)];
        fixed (byte* payloadPointer = payload)
        {
            result = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.CopyTypedResultPageRecordValue(
                pageHandle,
                recordIndex,
                &kind,
                payloadPointer,
                (nuint)payload.Length,
                &requiredLength,
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }

        if (kind != expectedKind || requiredLength != (nuint)payload.Length)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI changed a typed result value during copy.");
        }

        return PowerShellValue.FromNative(kind, payload);
    }
}
