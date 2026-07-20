namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellCapabilityInvocation
{
    internal PowerShellCapabilityInvocation(
        PowerShellCapabilityDefinition definition,
        ulong invocationId,
        CancellationToken cancellationToken)
    {
        Definition = definition;
        InvocationId = invocationId;
        CancellationToken = cancellationToken;
    }

    public PowerShellCapabilityDefinition Definition { get; }

    public ulong InvocationId { get; }

    public CancellationToken CancellationToken { get; }
}
