namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellCapabilityDefinition
{
    internal const int MaximumCapabilities = 16;
    internal const int MaximumNameLength = 64;
    internal const int MaximumDeadlineMilliseconds = 30_000;

    private readonly IReadOnlyList<PowerShellCapabilityArgumentSchema> arguments;
    private readonly IReadOnlyList<PowerShellValueKind> responseKinds;

    public PowerShellCapabilityDefinition(
        string name,
        IEnumerable<PowerShellCapabilityArgumentSchema> arguments,
        IEnumerable<PowerShellValueKind> responseKinds,
        PowerShellCapabilityPermission permissions,
        int maximumInputBytes,
        int maximumOutputBytes,
        TimeSpan deadline)
    {
        if (!IsCanonicalName(name))
        {
            throw new ArgumentException(
                "Capability names must be canonical lowercase rdm.* or host.* identifiers with bounded dot or hyphen segments.",
                nameof(name));
        }
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(responseKinds);
        if (permissions == PowerShellCapabilityPermission.None ||
            (permissions & ~(
                PowerShellCapabilityPermission.Read |
                PowerShellCapabilityPermission.Write |
                PowerShellCapabilityPermission.Report |
                PowerShellCapabilityPermission.HostInteraction)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions), "Capability permissions are invalid.");
        }
        if (maximumInputBytes is <= 0 or > PowerShellValue.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
        }
        if (maximumOutputBytes is <= 0 or > PowerShellValue.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        }
        if (deadline <= TimeSpan.Zero || deadline.TotalMilliseconds > MaximumDeadlineMilliseconds ||
            deadline.TotalMilliseconds != Math.Truncate(deadline.TotalMilliseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        PowerShellCapabilityArgumentSchema[] argumentValues = arguments.ToArray();
        if (argumentValues.Length > PowerShellValue.MaximumContainerEntries || argumentValues.Any(schema => schema is null))
        {
            throw new ArgumentException("Capability argument schemas exceed their bound.", nameof(arguments));
        }
        PowerShellValueKind[] responseValues = responseKinds.Distinct().ToArray();
        if (responseValues.Length == 0 || responseValues.Any(kind => !Enum.IsDefined(kind)))
        {
            throw new ArgumentException("Capability response schemas require one or more documented value kinds.", nameof(responseKinds));
        }

        Name = name;
        this.arguments = Array.AsReadOnly(argumentValues);
        this.responseKinds = Array.AsReadOnly(responseValues);
        Permissions = permissions;
        MaximumInputBytes = maximumInputBytes;
        MaximumOutputBytes = maximumOutputBytes;
        Deadline = deadline;
    }

    public string Name { get; }

    public IReadOnlyList<PowerShellCapabilityArgumentSchema> Arguments => arguments;

    public IReadOnlyList<PowerShellValueKind> ResponseKinds => responseKinds;

    public PowerShellCapabilityPermission Permissions { get; }

    public int MaximumInputBytes { get; }

    public int MaximumOutputBytes { get; }

    public TimeSpan Deadline { get; }

    internal uint DeadlineMilliseconds => checked((uint)Deadline.TotalMilliseconds);

    internal static bool IsCanonicalName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumNameLength ||
            !(value.StartsWith("rdm.", StringComparison.Ordinal) || value.StartsWith("host.", StringComparison.Ordinal)))
        {
            return false;
        }

        bool previousSeparator = true;
        foreach (char character in value)
        {
            bool separator = character is '.' or '-';
            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
            {
                return false;
            }
            if (separator && previousSeparator)
            {
                return false;
            }

            previousSeparator = separator;
        }

        return !previousSeparator;
    }
}
