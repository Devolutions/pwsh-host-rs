namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Defines one copied property allowed in a staged intent payload.
/// </summary>
public sealed class PowerShellStagedIntentProperty
{
    private const int MaximumNameLength = 64;
    private readonly IReadOnlyList<PowerShellValueKind> allowedKinds;

    /// <summary>
    /// Initializes a property definition.
    /// </summary>
    public PowerShellStagedIntentProperty(
        string name,
        IEnumerable<PowerShellValueKind> allowedKinds,
        bool isRequired = true)
    {
        if (!IsIdentifier(name))
        {
            throw new ArgumentException(
                "Staged intent property names must be bounded ASCII identifiers.",
                nameof(name));
        }

        ArgumentNullException.ThrowIfNull(allowedKinds);
        PowerShellValueKind[] kinds = allowedKinds.Distinct().ToArray();
        if (kinds.Length == 0 || kinds.Any(kind => !Enum.IsDefined(kind)))
        {
            throw new ArgumentException(
                "Staged intent properties require one or more documented value kinds.",
                nameof(allowedKinds));
        }

        Name = name;
        this.allowedKinds = Array.AsReadOnly(kinds);
        IsRequired = isRequired;
    }

    /// <summary>
    /// Gets the case-insensitive property name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the permitted copied value kinds.
    /// </summary>
    public IReadOnlyList<PowerShellValueKind> AllowedKinds => allowedKinds;

    /// <summary>
    /// Gets whether the property must be present.
    /// </summary>
    public bool IsRequired { get; }

    private static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumNameLength ||
            !(value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
        {
            return false;
        }

        return value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }
}

/// <summary>
/// Validates the copied property-bag payload for one staged intent operation.
/// </summary>
public sealed class PowerShellStagedIntentSchema
{
    /// <summary>
    /// The largest intent payload accepted by this SDK coordinator.
    /// </summary>
    public const int MaximumIntentPayloadBytes = 60 * 1024;

    private readonly IReadOnlyList<PowerShellStagedIntentProperty> properties;
    private readonly IReadOnlyDictionary<string, PowerShellStagedIntentProperty> propertiesByName;

    /// <summary>
    /// Initializes a bounded copied property-bag schema.
    /// </summary>
    public PowerShellStagedIntentSchema(
        IEnumerable<PowerShellStagedIntentProperty> properties,
        int maximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(properties);
        PowerShellStagedIntentProperty[] propertyArray = properties.ToArray();
        if (propertyArray.Length > PowerShellValue.MaximumContainerEntries ||
            propertyArray.Any(property => property is null) ||
            propertyArray.Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != propertyArray.Length)
        {
            throw new ArgumentException(
                "Staged intent schemas require at most 64 uniquely named properties.",
                nameof(properties));
        }
        if (maximumPayloadBytes is <= 0 or > MaximumIntentPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        this.properties = Array.AsReadOnly(propertyArray);
        propertiesByName = propertyArray.ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        MaximumPayloadBytes = maximumPayloadBytes;
    }

    /// <summary>
    /// Gets the allowed payload properties.
    /// </summary>
    public IReadOnlyList<PowerShellStagedIntentProperty> Properties => properties;

    /// <summary>
    /// Gets the maximum serialized <see cref="PowerShellValue"/> payload length.
    /// </summary>
    public int MaximumPayloadBytes { get; }

    internal bool TryValidate(PowerShellValue value, out string? message)
    {
        if (value.Kind != PowerShellValueKind.PropertyBag)
        {
            message = "The intent must be a property bag.";
            return false;
        }
        if (value.Payload.Length > MaximumPayloadBytes)
        {
            message = "The intent payload exceeds its declared bound.";
            return false;
        }

        IReadOnlyDictionary<string, PowerShellValue> values = value.GetPropertyBag();
        if (values.Count > properties.Count)
        {
            message = "The intent contains unsupported properties.";
            return false;
        }

        foreach ((string name, PowerShellValue propertyValue) in values)
        {
            if (!propertiesByName.TryGetValue(name, out PowerShellStagedIntentProperty? property) ||
                !property.AllowedKinds.Contains(propertyValue.Kind))
            {
                message = "The intent contains an unsupported property or value kind.";
                return false;
            }
        }

        if (properties.Any(property => property.IsRequired && !values.ContainsKey(property.Name)))
        {
            message = "The intent is missing a required property.";
            return false;
        }

        message = null;
        return true;
    }
}

/// <summary>
/// Defines one canonical staged intent operation and its application handler.
/// </summary>
public sealed class PowerShellStagedIntentDefinition
{
    internal const int MaximumDefinitions = PowerShellCapabilityDefinition.MaximumCapabilities / 4;
    internal static readonly TimeSpan CapabilityCallbackDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumStageDeadline = TimeSpan.FromHours(24);

    /// <summary>
    /// Initializes a definition for a canonical staged intent operation.
    /// </summary>
    public PowerShellStagedIntentDefinition(
        string operationName,
        PowerShellStagedIntentSchema schema,
        IPowerShellStagedIntentHandler handler,
        TimeSpan deadline)
    {
        if (!PowerShellCapabilityDefinition.IsCanonicalName(operationName) ||
            operationName.Length > PowerShellCapabilityDefinition.MaximumNameLength - ".validate".Length)
        {
            throw new ArgumentException(
                "Staged intent operation names must be canonical and leave room for their lifecycle suffixes.",
                nameof(operationName));
        }
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(handler);
        if (deadline <= TimeSpan.Zero || deadline > MaximumStageDeadline ||
            deadline.TotalMilliseconds != Math.Truncate(deadline.TotalMilliseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        OperationName = operationName;
        Schema = schema;
        Handler = handler;
        Deadline = deadline;
    }

    /// <summary>
    /// Gets the canonical base capability name.
    /// </summary>
    public string OperationName { get; }

    /// <summary>
    /// Gets the copied property-bag schema.
    /// </summary>
    public PowerShellStagedIntentSchema Schema { get; }

    /// <summary>
    /// Gets the application-owned lifecycle handler.
    /// </summary>
    public IPowerShellStagedIntentHandler Handler { get; }

    /// <summary>
    /// Gets the stage deadline, measured from the start of the stage operation.
    /// </summary>
    public TimeSpan Deadline { get; }
}
