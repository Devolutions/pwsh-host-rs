namespace Devolutions.PowerShell.Ffi;

public static class PowerShellHostInteraction
{
    /// <summary>
    /// Validates a copied <c>host.report-progress</c> property bag without parsing
    /// generic stream display text.
    /// </summary>
    public static PowerShellProgressUpdate ParseProgressUpdate(PowerShellValue value)
    {
        return PowerShellProgressUpdate.Parse(value);
    }

    public static PowerShellCapabilityDefinition WriteText { get; } = new(
        "host.write-text",
        [new PowerShellCapabilityArgumentSchema([PowerShellValueKind.String])],
        [PowerShellValueKind.Null],
        PowerShellCapabilityPermission.HostInteraction | PowerShellCapabilityPermission.Report,
        maximumInputBytes: 4096,
        maximumOutputBytes: 64,
        deadline: TimeSpan.FromSeconds(5));

    public static PowerShellCapabilityDefinition ReportProgress { get; } = new(
        "host.report-progress",
        [new PowerShellCapabilityArgumentSchema([PowerShellValueKind.PropertyBag])],
        [PowerShellValueKind.Null],
        PowerShellCapabilityPermission.HostInteraction | PowerShellCapabilityPermission.Report,
        maximumInputBytes: 8192,
        maximumOutputBytes: 64,
        deadline: TimeSpan.FromSeconds(5));

    public static PowerShellCapabilityDefinition ReadLine { get; } = new(
        "host.read-line",
        [new PowerShellCapabilityArgumentSchema([PowerShellValueKind.String])],
        [PowerShellValueKind.Null, PowerShellValueKind.String],
        PowerShellCapabilityPermission.HostInteraction | PowerShellCapabilityPermission.Read,
        maximumInputBytes: 4096,
        maximumOutputBytes: 4096,
        deadline: TimeSpan.FromSeconds(30));

    public static PowerShellCapabilityDefinition PromptChoice { get; } = new(
        "host.prompt-choice",
        [new PowerShellCapabilityArgumentSchema([PowerShellValueKind.PropertyBag])],
        [PowerShellValueKind.SignedInteger, PowerShellValueKind.Null],
        PowerShellCapabilityPermission.HostInteraction | PowerShellCapabilityPermission.Read,
        maximumInputBytes: 8192,
        maximumOutputBytes: 64,
        deadline: TimeSpan.FromSeconds(30));

    public static PowerShellCapabilityDefinition PromptMultipleChoice { get; } = new(
        "host.prompt-multiple-choice",
        [new PowerShellCapabilityArgumentSchema([PowerShellValueKind.PropertyBag])],
        [PowerShellValueKind.Array, PowerShellValueKind.Null],
        PowerShellCapabilityPermission.HostInteraction | PowerShellCapabilityPermission.Read,
        maximumInputBytes: 8192,
        maximumOutputBytes: 8192,
        deadline: TimeSpan.FromSeconds(30));
}
