using System.Threading;
using System.Threading.Tasks;

namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellInvocationOperation : IDisposable
{
    private const uint InfiniteTimeoutMilliseconds = uint.MaxValue;
    private const int MaximumStreamBatchRecords = 32;
    private const uint StreamBatchCursorLost = 1;
    private const uint StreamBatchTruncated = 1 << 1;
    private const uint StreamRecordFieldsTruncated = 1;
    private const nuint MaximumStreamRecordUtf8Bytes = 4096;
    private readonly PowerShellOperationHandle handle;

    internal PowerShellInvocationOperation(PowerShellOperationHandle handle)
    {
        this.handle = handle;
    }

    public PowerShellInvocationOperationStatus Poll()
    {
        return GetStatus(0, wait: false);
    }

    public PowerShellInvocationOperationStatus Wait(TimeSpan timeout)
    {
        uint timeoutMilliseconds;
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            timeoutMilliseconds = InfiniteTimeoutMilliseconds;
        }
        else
        {
            if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            timeoutMilliseconds = checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
        }

        return GetStatus(timeoutMilliseconds, wait: true);
    }

    public void Stop()
    {
        StopCore();
    }

    public PowerShellInvocationResult GetResult()
    {
        return GetResultCore();
    }

    public PowerShellInvocationStreamBatch ReadStreamBatch(ulong afterSequence, int maximumRecords = MaximumStreamBatchRecords)
    {
        if (maximumRecords < 1 || maximumRecords > MaximumStreamBatchRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        PowerShell.EnsureLiveStreamPollingSupported();
        return ReadStreamBatchCore(afterSequence, checked((uint)maximumRecords));
    }

    public Task<PowerShellInvocationResult> GetResultAsync(CancellationToken cancellationToken = default)
    {
        return PowerShellAsyncOperationAwaiter.GetResultAsync(this, cancellationToken, releaseWhenComplete: false);
    }

    public void Dispose()
    {
        handle.Dispose();
    }

    private unsafe void StopCore()
    {
        using PowerShellOperationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.StopOperation(lease.Value, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
    }

    private unsafe PowerShellInvocationResult GetResultCore()
    {
        using PowerShellOperationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong nativeResultHandle = 0;
        int status = NativeMethods.GetOperationResult(lease.Value, &nativeResultHandle, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);

        using var invocationResultHandle = new PowerShellInvocationResultHandle(nativeResultHandle);
        PowerShellInvocationResult invocationResult = PowerShell.ReadInvocationResult(invocationResultHandle);
        if (invocationResult.IsTerminatingFailure)
        {
            throw new PowerShellInvocationException(
                PowerShellFfiStatus.ManagedFailure,
                "PowerShell invocation terminated.",
                invocationResult);
        }

        return invocationResult;
    }

    private unsafe PowerShellInvocationStreamBatch ReadStreamBatchCore(ulong afterSequence, uint maximumRecords)
    {
        using PowerShellOperationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong batchHandle = 0;
        int status = NativeMethods.ReadOperationStreamBatch(
            lease.Value,
            afterSequence,
            maximumRecords,
            &batchHandle,
            &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        if (batchHandle == 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an invalid operation stream batch handle.");
        }

        try
        {
            NativeOperationStreamBatchInfo info = new()
            {
                Size = checked((uint)sizeof(NativeOperationStreamBatchInfo)),
            };
            result = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.GetOperationStreamBatchInfo(batchHandle, &info, &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);

            if (!Enum.IsDefined((PowerShellOperationState)info.OperationState) ||
                !Enum.IsDefined((PowerShellFfiStatus)info.TerminalStatus) ||
                info.RecordCount > maximumRecords ||
                info.NextSequence < afterSequence ||
                info.LostRecordCount > info.DroppedRecordCount ||
                info.DroppedRecordCount > info.TotalRecordCount ||
                info.SourceDroppedRecordCount > info.TotalRecordCount)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned invalid live stream batch metadata.");
            }

            var records = new PowerShellInvocationStreamRecord[checked((int)info.RecordCount)];
            ulong previousSequence = afterSequence;
            for (uint index = 0; index < info.RecordCount; index++)
            {
                uint stream = 0;
                ulong sequence = 0;
                uint flags = 0;
                result = NativeCall.CreateResult(diagnostic);
                status = NativeMethods.GetOperationStreamBatchRecordInfo(
                    batchHandle,
                    index,
                    &stream,
                    &sequence,
                    &flags,
                    &result);
                NativeCall.ThrowIfFailed(status, result, diagnostic);
                if (!Enum.IsDefined((PowerShellStreamKind)stream) ||
                    sequence == 0 ||
                    sequence <= previousSequence ||
                    sequence > info.NextSequence)
                {
                    throw new PowerShellFfiException(
                        PowerShellFfiStatus.ManagedFailure,
                        "Native PowerShell FFI returned unordered live stream records.");
                }

                records[checked((int)index)] = new PowerShellInvocationStreamRecord(
                    (PowerShellStreamKind)stream,
                    sequence,
                    ReadStreamBatchRecordText(batchHandle, index, diagnostic),
                    (flags & StreamRecordFieldsTruncated) != 0);
                previousSequence = sequence;
            }

            if (records.Length == 0 ? info.NextSequence != afterSequence : info.NextSequence != previousSequence)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned an inconsistent live stream cursor.");
            }

            return new PowerShellInvocationStreamBatch(
                records,
                info.NextSequence,
                (PowerShellOperationState)info.OperationState,
                (PowerShellFfiStatus)info.TerminalStatus,
                (info.Flags & StreamBatchCursorLost) != 0,
                info.LostRecordCount,
                (info.Flags & StreamBatchTruncated) != 0,
                info.TotalRecordCount,
                info.DroppedRecordCount,
                info.SourceDroppedRecordCount);
        }
        finally
        {
            result = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.ReleaseOperationStreamBatch(batchHandle, &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }
    }

    private static unsafe string ReadStreamBatchRecordText(ulong batchHandle, uint recordIndex, byte* diagnostic)
    {
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        nuint requiredLength = 0;
        int status = NativeMethods.CopyOperationStreamBatchRecordText(
            batchHandle,
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

        if (requiredLength > MaximumStreamRecordUtf8Bytes)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an unbounded live stream record.");
        }

        byte[] value = new byte[checked((int)requiredLength)];
        fixed (byte* valuePointer = value)
        {
            result = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.CopyOperationStreamBatchRecordText(
                batchHandle,
                recordIndex,
                valuePointer,
                (nuint)value.Length,
                &requiredLength,
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }

        return System.Text.Encoding.UTF8.GetString(value);
    }

    private PowerShellInvocationOperationStatus GetStatus(uint timeoutMilliseconds, bool wait)
    {
        return GetStatusCore(timeoutMilliseconds, wait);
    }

    private unsafe PowerShellInvocationOperationStatus GetStatusCore(uint timeoutMilliseconds, bool wait)
    {
        using PowerShellOperationHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        uint nativeState = 0;
        int nativeTerminalStatus = 0;
        int status = wait
            ? NativeMethods.WaitOperation(
                lease.Value,
                timeoutMilliseconds,
                &nativeState,
                &nativeTerminalStatus,
                &result)
            : NativeMethods.PollOperation(lease.Value, &nativeState, &nativeTerminalStatus, &result);

        if (!Enum.IsDefined((PowerShellOperationState)nativeState) ||
            !Enum.IsDefined((PowerShellFfiStatus)nativeTerminalStatus))
        {
            NativeCall.ThrowIfFailed(status, result, diagnostic);
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned invalid async operation metadata.");
        }

        PowerShellOperationState operationState = (PowerShellOperationState)nativeState;
        PowerShellFfiStatus terminalStatus = (PowerShellFfiStatus)nativeTerminalStatus;
        PowerShellFfiStatus effectiveStatus = NativeCall.EffectiveStatus(status, result);
        bool terminal = operationState is PowerShellOperationState.Completed or
            PowerShellOperationState.Cancelled or PowerShellOperationState.Failed;
        if ((!terminal && (effectiveStatus != PowerShellFfiStatus.Success ||
                           terminalStatus != PowerShellFfiStatus.Success)) ||
            (terminal && effectiveStatus != terminalStatus))
        {
            NativeCall.ThrowIfFailed(status, result, diagnostic);
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned inconsistent async operation metadata.");
        }

        string operationDiagnostic = terminal && terminalStatus != PowerShellFfiStatus.Success
            ? NativeCall.ReadDiagnostic(result, diagnostic)
            : string.Empty;
        return new PowerShellInvocationOperationStatus(operationState, terminalStatus, operationDiagnostic);
    }

    internal void StopForCancellation()
    {
        try
        {
            Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (PowerShellFfiException)
        {
        }
    }
}
