namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellCapabilityArgumentSchema
{
    private readonly IReadOnlyList<PowerShellValueKind> allowedKinds;

    public PowerShellCapabilityArgumentSchema(IEnumerable<PowerShellValueKind> allowedKinds)
    {
        ArgumentNullException.ThrowIfNull(allowedKinds);
        PowerShellValueKind[] values = allowedKinds.Distinct().ToArray();
        if (values.Length == 0 || values.Any(kind => !Enum.IsDefined(kind)))
        {
            throw new ArgumentException("Capability argument schemas require one or more documented value kinds.", nameof(allowedKinds));
        }

        this.allowedKinds = Array.AsReadOnly(values);
    }

    public IReadOnlyList<PowerShellValueKind> AllowedKinds => allowedKinds;
}
