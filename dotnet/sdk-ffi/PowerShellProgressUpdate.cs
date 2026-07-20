namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A copied, validated progress update sent through <c>host.report-progress</c>.
/// </summary>
public sealed class PowerShellProgressUpdate
{
    internal PowerShellProgressUpdate(
        long activityId,
        long parentActivityId,
        string activity,
        string? statusDescription,
        string? currentOperation,
        int percentComplete,
        long secondsRemaining,
        bool isCompleted)
    {
        ActivityId = activityId;
        ParentActivityId = parentActivityId;
        Activity = activity;
        StatusDescription = statusDescription;
        CurrentOperation = currentOperation;
        PercentComplete = percentComplete;
        SecondsRemaining = secondsRemaining;
        IsCompleted = isCompleted;
    }

    public long ActivityId { get; }

    public long ParentActivityId { get; }

    public string Activity { get; }

    public string? StatusDescription { get; }

    public string? CurrentOperation { get; }

    public int PercentComplete { get; }

    public long SecondsRemaining { get; }

    public bool IsCompleted { get; }

    internal static PowerShellProgressUpdate Parse(PowerShellValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        IReadOnlyDictionary<string, PowerShellValue> properties = value.GetPropertyBag();
        foreach (string name in properties.Keys)
        {
            if (name is not ("ActivityId" or "ParentActivityId" or "Activity" or
                "StatusDescription" or "CurrentOperation" or "PercentComplete" or
                "SecondsRemaining" or "IsCompleted"))
            {
                throw new ArgumentException(
                    $"The host progress payload contains an unsupported property '{name}'.",
                    nameof(value));
            }
        }

        long activityId = GetRequiredInteger(properties, "ActivityId", 0, long.MaxValue);
        long parentActivityId = GetOptionalInteger(properties, "ParentActivityId", -1, long.MaxValue, -1);
        string activity = GetRequiredText(properties, "Activity", 512);
        string? statusDescription = GetOptionalText(properties, "StatusDescription", 1024);
        string? currentOperation = GetOptionalText(properties, "CurrentOperation", 1024);
        long percentComplete = GetOptionalInteger(properties, "PercentComplete", -1, 100, -1);
        long secondsRemaining = GetOptionalInteger(properties, "SecondsRemaining", -1, long.MaxValue, -1);
        bool isCompleted = GetOptionalBoolean(properties, "IsCompleted", false);

        return new PowerShellProgressUpdate(
            activityId,
            parentActivityId,
            activity,
            statusDescription,
            currentOperation,
            checked((int)percentComplete),
            secondsRemaining,
            isCompleted);
    }

    private static long GetRequiredInteger(
        IReadOnlyDictionary<string, PowerShellValue> properties,
        string name,
        long minimum,
        long maximum)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value))
        {
            throw new ArgumentException($"The host progress payload is missing '{name}'.");
        }

        return GetInteger(value, name, minimum, maximum);
    }

    private static long GetOptionalInteger(
        IReadOnlyDictionary<string, PowerShellValue> properties,
        string name,
        long minimum,
        long maximum,
        long defaultValue)
    {
        return properties.TryGetValue(name, out PowerShellValue? value)
            ? GetInteger(value, name, minimum, maximum)
            : defaultValue;
    }

    private static long GetInteger(PowerShellValue value, string name, long minimum, long maximum)
    {
        if (!value.TryGetSignedInteger(out long result) || result < minimum || result > maximum)
        {
            throw new ArgumentException($"The host progress property '{name}' is outside its accepted integer range.");
        }

        return result;
    }

    private static string GetRequiredText(
        IReadOnlyDictionary<string, PowerShellValue> properties,
        string name,
        int maximumLength)
    {
        string? value = GetOptionalText(properties, name, maximumLength);
        if (value is null)
        {
            throw new ArgumentException($"The host progress payload is missing '{name}'.");
        }

        return value;
    }

    private static string? GetOptionalText(
        IReadOnlyDictionary<string, PowerShellValue> properties,
        string name,
        int maximumLength)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value))
        {
            return null;
        }
        if (!value.TryGetString(out string? result) || result is null || result.Length > maximumLength)
        {
            throw new ArgumentException($"The host progress property '{name}' is not accepted text.");
        }

        return result;
    }

    private static bool GetOptionalBoolean(
        IReadOnlyDictionary<string, PowerShellValue> properties,
        string name,
        bool defaultValue)
    {
        if (!properties.TryGetValue(name, out PowerShellValue? value))
        {
            return defaultValue;
        }
        if (!value.TryGetBoolean(out bool result))
        {
            throw new ArgumentException($"The host progress property '{name}' is not a Boolean.");
        }

        return result;
    }
}
