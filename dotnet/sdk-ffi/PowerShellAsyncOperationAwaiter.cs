using System.Threading;
using System.Threading.Tasks;

namespace Devolutions.PowerShell.Ffi;

internal static class PowerShellAsyncOperationAwaiter
{
    internal static async Task<PowerShellInvocationResult> GetResultAsync(
        PowerShellInvocationOperation operation,
        CancellationToken cancellationToken,
        bool releaseWhenComplete)
    {
        try
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static current => ((PowerShellInvocationOperation)current!).StopForCancellation(),
                operation);

            PowerShellInvocationOperationStatus status;
            do
            {
                status = operation.Wait(TimeSpan.FromMilliseconds(25));
                if (!status.IsTerminal)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }
            }
            while (!status.IsTerminal);

            if (status.State == PowerShellOperationState.Completed)
            {
                return operation.GetResult();
            }

            if (status.State == PowerShellOperationState.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "The PowerShell async operation reached its native cancellation terminal state.",
                    cancellationToken);
            }

            throw new PowerShellFfiException(status.TerminalStatus, status.Diagnostic);
        }
        finally
        {
            if (releaseWhenComplete)
            {
                operation.Dispose();
            }
        }
    }
}
