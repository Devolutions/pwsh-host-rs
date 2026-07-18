using System.Threading;
using System.Threading.Tasks;

namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellInvocationOperation : IDisposable
{
    private const uint InfiniteTimeoutMilliseconds = uint.MaxValue;
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
