namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellCapabilityBinding
{
    public PowerShellCapabilityBinding(
        PowerShellCapabilityDefinition definition,
        IPowerShellCapabilityHandler handler)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public PowerShellCapabilityDefinition Definition { get; }

    public IPowerShellCapabilityHandler Handler { get; }
}
