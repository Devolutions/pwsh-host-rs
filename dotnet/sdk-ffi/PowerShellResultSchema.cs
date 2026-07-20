namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Validates a bounded copied invocation result without materializing SMA values.
/// </summary>
public sealed class PowerShellResultSchema
{
    private readonly IReadOnlyList<PowerShellValueKind> allowedScalarKinds;
    private readonly IReadOnlyList<string> requiredPropertyNames;

    public PowerShellResultSchema(
        int minimumOutputRecords = 0,
        int maximumOutputRecords = 32,
        IEnumerable<PowerShellValueKind>? allowedScalarKinds = null,
        IEnumerable<string>? requiredPropertyNames = null,
        bool requireNoErrors = true,
        bool requireCompleteSnapshots = true)
    {
        if (minimumOutputRecords < 0 || maximumOutputRecords < minimumOutputRecords || maximumOutputRecords > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputRecords));
        }

        PowerShellValueKind[] scalarKinds = allowedScalarKinds?.Distinct().ToArray() ?? [];
        if (scalarKinds.Any(kind => !IsScalarKind(kind)))
        {
            throw new ArgumentException("Result schemas may contain only documented scalar kinds.", nameof(allowedScalarKinds));
        }

        string[] propertyNames = requiredPropertyNames?.ToArray() ?? [];
        if (propertyNames.Length > PowerShellValue.MaximumContainerEntries ||
            propertyNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128) ||
            propertyNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != propertyNames.Length)
        {
            throw new ArgumentException("Required property names must be unique, non-empty, and bounded.", nameof(requiredPropertyNames));
        }

        MinimumOutputRecords = minimumOutputRecords;
        MaximumOutputRecords = maximumOutputRecords;
        this.allowedScalarKinds = Array.AsReadOnly(scalarKinds);
        this.requiredPropertyNames = Array.AsReadOnly(propertyNames);
        RequireNoErrors = requireNoErrors;
        RequireCompleteSnapshots = requireCompleteSnapshots;
    }

    public int MinimumOutputRecords { get; }

    public int MaximumOutputRecords { get; }

    public IReadOnlyList<PowerShellValueKind> AllowedScalarKinds => allowedScalarKinds;

    public IReadOnlyList<string> RequiredPropertyNames => requiredPropertyNames;

    public bool RequireNoErrors { get; }

    public bool RequireCompleteSnapshots { get; }

    public void Validate(PowerShellInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Output.TotalRecordCount < (ulong)MinimumOutputRecords ||
            result.Output.TotalRecordCount > (ulong)MaximumOutputRecords ||
            result.Output.Records.Count < MinimumOutputRecords ||
            result.Output.Records.Count > MaximumOutputRecords)
        {
            throw new InvalidOperationException("PowerShell output record count violates the declared result schema.");
        }

        if (RequireNoErrors && (result.HadErrors || result.Errors.TotalRecordCount != 0))
        {
            throw new InvalidOperationException("PowerShell produced errors where the declared result schema requires none.");
        }

        if (RequireCompleteSnapshots &&
            (result.Output.IsTruncated ||
             result.Errors.IsTruncated ||
             result.Warnings.IsTruncated ||
             result.Verbose.IsTruncated ||
             result.Debug.IsTruncated ||
             result.Information.IsTruncated ||
             result.Progress.IsTruncated ||
             result.Output.Records.Any(record => record.IsTruncated || record.IsPropertyBagTruncated)))
        {
            throw new InvalidOperationException("PowerShell produced truncated data where the declared result schema requires complete snapshots.");
        }

        foreach (PowerShellObjectSnapshot record in result.Output.Records)
        {
            if (allowedScalarKinds.Count != 0 &&
                (record.ScalarValue is null || !allowedScalarKinds.Contains(record.ScalarValue.Kind)))
            {
                throw new InvalidOperationException("PowerShell output scalar kind violates the declared result schema.");
            }

            if (requiredPropertyNames.Count != 0)
            {
                IReadOnlyDictionary<string, PowerShellValue> properties = PowerShellSnapshotReader.GetCompleteProperties(record);
                if (requiredPropertyNames.Any(name => !properties.ContainsKey(name)))
                {
                    throw new InvalidOperationException("PowerShell output is missing a required copied property.");
                }
            }

        }
    }

    private static bool IsScalarKind(PowerShellValueKind kind)
    {
        return kind is
            PowerShellValueKind.Null or
            PowerShellValueKind.Switch or
            PowerShellValueKind.Boolean or
            PowerShellValueKind.SignedInteger or
            PowerShellValueKind.UnsignedInteger or
            PowerShellValueKind.Double or
            PowerShellValueKind.Decimal or
            PowerShellValueKind.String or
            PowerShellValueKind.Bytes or
            PowerShellValueKind.DateTime or
            PowerShellValueKind.DateTimeOffset or
            PowerShellValueKind.Guid or
            PowerShellValueKind.Uri;
    }
}
