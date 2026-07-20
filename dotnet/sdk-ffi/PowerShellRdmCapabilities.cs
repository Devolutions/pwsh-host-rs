namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Standard harmless RDM capability schemas. Applications register handlers for
/// only the definitions they explicitly need.
/// </summary>
public static class PowerShellRdmCapabilities
{
    public static PowerShellCapabilityDefinition GetConnectionName { get; } = new(
        "rdm.get-connection-name",
        [],
        [PowerShellValueKind.String],
        PowerShellCapabilityPermission.Read,
        maximumInputBytes: 64,
        maximumOutputBytes: 1024,
        deadline: TimeSpan.FromSeconds(5));

    public static PowerShellCapabilityDefinition GetConnectionDisplay { get; } = new(
        "rdm.get-connection-display",
        [],
        [PowerShellValueKind.PropertyBag],
        PowerShellCapabilityPermission.Read,
        maximumInputBytes: 64,
        maximumOutputBytes: 8192,
        deadline: TimeSpan.FromSeconds(5));

    public static PowerShellCapabilityDefinition ReportStatus { get; } = new(
        "rdm.report-status",
        [new PowerShellCapabilityArgumentSchema([PowerShellValueKind.PropertyBag])],
        [PowerShellValueKind.Null],
        PowerShellCapabilityPermission.Report,
        maximumInputBytes: 8192,
        maximumOutputBytes: 64,
        deadline: TimeSpan.FromSeconds(5));
}
