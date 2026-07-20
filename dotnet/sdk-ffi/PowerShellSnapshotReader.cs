namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Converts immutable invocation snapshots into copied display and value DTOs.
/// </summary>
public static class PowerShellSnapshotReader
{
    public static IReadOnlyDictionary<string, PowerShellValue> GetCompleteProperties(PowerShellObjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.IsTruncated ||
            snapshot.IsPropertyBagTruncated ||
            snapshot.DroppedPropertyEntryCount != 0 ||
            snapshot.PropertyBag is not { Kind: PowerShellValueKind.PropertyBag } propertyBag)
        {
            throw new InvalidOperationException("The output snapshot does not contain a complete copied property bag.");
        }

        return propertyBag.GetPropertyBag();
    }

    public static bool TryGetProperty(
        PowerShellObjectSnapshot snapshot,
        string name,
        out PowerShellValue? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return GetCompleteProperties(snapshot).TryGetValue(name, out value);
    }

    public static PowerShellDisplaySnapshot CreateDisplaySnapshot(PowerShellInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new PowerShellDisplaySnapshot(
            Copy(result.Output.Records.Select(record => record.DisplayText)),
            Copy(result.Errors.Records.Select(record => record.Message)),
            Copy(result.Warnings.Records.Select(record => record.DisplayText)),
            Copy(result.Verbose.Records.Select(record => record.DisplayText)),
            Copy(result.Debug.Records.Select(record => record.DisplayText)),
            Copy(result.Information.Records.Select(record => record.DisplayText)),
            Copy(result.Progress.Records.Select(record => record.DisplayText)),
            !result.Output.IsTruncated &&
            !result.Errors.IsTruncated &&
            !result.Warnings.IsTruncated &&
            !result.Verbose.IsTruncated &&
            !result.Debug.IsTruncated &&
            !result.Information.IsTruncated &&
            !result.Progress.IsTruncated &&
            result.Output.Records.All(record => !record.IsTruncated) &&
            result.Errors.Records.All(record => !record.IsTruncated) &&
            result.Warnings.Records.All(record => !record.IsTruncated) &&
            result.Verbose.Records.All(record => !record.IsTruncated) &&
            result.Debug.Records.All(record => !record.IsTruncated) &&
            result.Information.Records.All(record => !record.IsTruncated) &&
            result.Progress.Records.All(record => !record.IsTruncated));
    }

    private static IReadOnlyList<string> Copy(IEnumerable<string> values)
    {
        return Array.AsReadOnly(values.ToArray());
    }
}

public sealed class PowerShellDisplaySnapshot
{
    internal PowerShellDisplaySnapshot(
        IReadOnlyList<string> output,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> verbose,
        IReadOnlyList<string> debug,
        IReadOnlyList<string> information,
        IReadOnlyList<string> progress,
        bool isComplete)
    {
        Output = output;
        Errors = errors;
        Warnings = warnings;
        Verbose = verbose;
        Debug = debug;
        Information = information;
        Progress = progress;
        IsComplete = isComplete;
    }

    public IReadOnlyList<string> Output { get; }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<string> Verbose { get; }

    public IReadOnlyList<string> Debug { get; }

    public IReadOnlyList<string> Information { get; }

    public IReadOnlyList<string> Progress { get; }

    public bool IsComplete { get; }

    public string OutputText => string.Join(Environment.NewLine, Output);
}
