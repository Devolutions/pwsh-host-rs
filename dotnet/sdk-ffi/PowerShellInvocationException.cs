using System.Collections.Generic;

namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellInvocationException : PowerShellFfiException
{
    internal PowerShellInvocationException(
        PowerShellFfiStatus status,
        string diagnostic,
        PowerShellInvocationResult invocationResult)
        : base(status, diagnostic)
    {
        InvocationResult = invocationResult;
    }

    public PowerShellInvocationResult InvocationResult { get; }

    public IReadOnlyList<PowerShellInvocationError> Errors => InvocationResult.Errors.Records;
}
