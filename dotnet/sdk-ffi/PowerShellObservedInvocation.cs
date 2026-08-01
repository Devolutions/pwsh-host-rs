using System.Text;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A single native-backed invocation with independently acknowledged copied result and diagnostic channels.
/// </summary>
public sealed unsafe class PowerShellObservedInvocation : IDisposable
{
    private const uint Terminal = 1;
    private const uint Truncated = 1 << 1;
    private const uint Complete = 1 << 2;
    private readonly PowerShellObservedInvocationHandle handle;
    private readonly int maximumResultPageRecords;
    private readonly int maximumDiagnosticPageRecords;

    internal PowerShellObservedInvocation(ulong nativeHandle, PowerShellObservedInvocationOptions options)
    {
        handle = new PowerShellObservedInvocationHandle(nativeHandle);
        maximumResultPageRecords = options.MaximumResultPageRecords;
        maximumDiagnosticPageRecords = options.MaximumDiagnosticPageRecords;
    }

    /// <summary>
    /// Acknowledges the prior result page and returns the next ordered copied result values.
    /// </summary>
    public PowerShellValuePage ReadResults(ulong acknowledgedThrough, int? maximumRecords = null)
    {
        PowerShell.EnsureObservedInvocationSupported();
        int limit = maximumRecords ?? maximumResultPageRecords;
        if (limit < 1 || limit > maximumResultPageRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        using PowerShellObservedInvocationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong nativePageHandle = 0;
        int status = NativeMethods.ReadObservedResultPage(
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
                "Native PowerShell FFI returned an invalid observed result page handle.");
        }

        using var pageHandle = new PowerShellTypedResultPageHandle(nativePageHandle);
        NativeTypedResultPageInfo info = ReadPageInfo(pageHandle.Value, diagnostic, isDiagnostic: false);
        ValidatePageInfo(info, acknowledgedThrough, checked((uint)limit), isDiagnostic: false);

        bool isTerminal = (info.Flags & Terminal) != 0;
        bool isTruncated = (info.Flags & Truncated) != 0;
        bool isComplete = (info.Flags & Complete) != 0;
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
                    "Native PowerShell FFI returned unordered observed result records.");
            }

            records[checked((int)index)] = new PowerShellValuePageRecord(
                sequence,
                PowerShellTypedResultInvocation.ReadValue(pageHandle.Value, index, kind, diagnostic));
            previousSequence = sequence;
        }

        ValidatePageCursor(records.Length, previousSequence, info, "observed result");
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

    /// <summary>
    /// Acknowledges the prior diagnostic page and returns copied output and diagnostic records.
    /// </summary>
    public PowerShellObservedDiagnosticPage ReadDiagnostics(ulong acknowledgedThrough, int? maximumRecords = null)
    {
        PowerShell.EnsureObservedInvocationSupported();
        int limit = maximumRecords ?? maximumDiagnosticPageRecords;
        if (limit < 1 || limit > maximumDiagnosticPageRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        using PowerShellObservedInvocationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong nativePageHandle = 0;
        int status = NativeMethods.ReadObservedDiagnosticPage(
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
                "Native PowerShell FFI returned an invalid observed diagnostic page handle.");
        }

        using var pageHandle = new PowerShellObservedDiagnosticPageHandle(nativePageHandle);
        NativeTypedResultPageInfo info = ReadPageInfo(pageHandle.Value, diagnostic, isDiagnostic: true);
        ValidatePageInfo(info, acknowledgedThrough, checked((uint)limit), isDiagnostic: true);

        bool isTerminal = (info.Flags & Terminal) != 0;
        bool isComplete = (info.Flags & Complete) != 0;
        var records = new PowerShellObservedDiagnosticRecord[checked((int)info.RecordCount)];
        ulong previousSequence = info.AcknowledgedSequence;
        for (uint index = 0; index < info.RecordCount; index++)
        {
            uint stream = 0;
            ulong sequence = 0;
            result = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.GetObservedDiagnosticPageRecordInfo(
                pageHandle.Value,
                index,
                &stream,
                &sequence,
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
            if (!Enum.IsDefined((PowerShellStreamKind)stream) ||
                sequence <= previousSequence ||
                sequence > info.NextSequence)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned unordered observed diagnostic records.");
            }

            records[checked((int)index)] = new PowerShellObservedDiagnosticRecord(
                (PowerShellStreamKind)stream,
                sequence,
                ReadDiagnosticText(pageHandle.Value, index, diagnostic));
            previousSequence = sequence;
        }

        ValidatePageCursor(records.Length, previousSequence, info, "observed diagnostic");
        return new PowerShellObservedDiagnosticPage(
            records,
            info.AcknowledgedSequence,
            info.NextSequence,
            info.TotalRecordCount,
            (PowerShellFfiStatus)info.TerminalStatus,
            isTerminal,
            isComplete);
    }

    public void Stop()
    {
        PowerShell.EnsureObservedInvocationSupported();
        using PowerShellObservedInvocationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.StopObservedInvocation(lease.Value, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
    }

    public void Dispose()
    {
        handle.Dispose();
    }

    private static NativeTypedResultPageInfo ReadPageInfo(ulong pageHandle, byte* diagnostic, bool isDiagnostic)
    {
        NativeTypedResultPageInfo info = new()
        {
            Size = checked((uint)sizeof(NativeTypedResultPageInfo)),
        };
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = isDiagnostic
            ? NativeMethods.GetObservedDiagnosticPageInfo(pageHandle, &info, &result)
            : NativeMethods.GetTypedResultPageInfo(pageHandle, &info, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        return info;
    }

    private static void ValidatePageInfo(
        NativeTypedResultPageInfo info,
        ulong acknowledgedThrough,
        uint maximumRecords,
        bool isDiagnostic)
    {
        if (!Enum.IsDefined((PowerShellFfiStatus)info.TerminalStatus) ||
            info.AcknowledgedSequence != acknowledgedThrough ||
            info.NextSequence < info.AcknowledgedSequence ||
            info.NextSequence > info.TotalRecordCount ||
            info.DroppedRecordCount > info.TotalRecordCount ||
            info.RecordCount > maximumRecords ||
            (info.Flags & ~(Terminal | Truncated | Complete)) != 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned invalid observed invocation page metadata.");
        }

        bool isTerminal = (info.Flags & Terminal) != 0;
        bool isTruncated = (info.Flags & Truncated) != 0;
        bool isComplete = (info.Flags & Complete) != 0;
        if ((!isTerminal && info.TerminalStatus != (int)PowerShellFfiStatus.Success) ||
            (isDiagnostic && (isTruncated || info.DroppedRecordCount != 0)) ||
            (isComplete && (!isTerminal ||
                            info.TerminalStatus != (int)PowerShellFfiStatus.Success ||
                            isTruncated ||
                            info.DroppedRecordCount != 0 ||
                            info.TotalRecordCount != info.AcknowledgedSequence)))
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned inconsistent observed invocation terminal metadata.");
        }
    }

    private static void ValidatePageCursor(
        int recordCount,
        ulong previousSequence,
        NativeTypedResultPageInfo info,
        string channel)
    {
        if (recordCount == 0
            ? info.NextSequence != info.AcknowledgedSequence
            : info.NextSequence != previousSequence)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                $"Native PowerShell FFI returned an inconsistent {channel} cursor.");
        }
    }

    private static string ReadDiagnosticText(ulong pageHandle, uint recordIndex, byte* diagnostic)
    {
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        nuint requiredLength = 0;
        int status = NativeMethods.CopyObservedDiagnosticPageRecordText(
            pageHandle,
            recordIndex,
            null,
            0,
            &requiredLength,
            &result);
        if (status != (int)PowerShellFfiStatus.Success &&
            status != (int)PowerShellFfiStatus.BufferTooSmall)
        {
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }

        if (requiredLength > PowerShellValue.MaximumPayloadLength)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an unbounded observed diagnostic text.");
        }

        byte[] text = new byte[checked((int)requiredLength)];
        fixed (byte* textPointer = text)
        {
            result = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.CopyObservedDiagnosticPageRecordText(
                pageHandle,
                recordIndex,
                textPointer,
                (nuint)text.Length,
                &requiredLength,
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }

        if (requiredLength != (nuint)text.Length)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI changed observed diagnostic text during copy.");
        }

        return Encoding.UTF8.GetString(text);
    }
}
