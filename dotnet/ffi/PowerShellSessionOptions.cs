namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellSessionOptions
{
    public PowerShellSessionOptions(
        PowerShellRunspaceMode runspaceMode = PowerShellRunspaceMode.NewRunspace,
        PowerShellSessionInitialConfiguration initialConfiguration = PowerShellSessionInitialConfiguration.Default,
        PowerShellSessionHistoryMode historyMode = PowerShellSessionHistoryMode.Disabled,
        PowerShellSessionPreference errorPreference = PowerShellSessionPreference.Inherit,
        PowerShellSessionPreference warningPreference = PowerShellSessionPreference.Inherit,
        PowerShellSessionPreference verbosePreference = PowerShellSessionPreference.Inherit,
        PowerShellSessionPreference debugPreference = PowerShellSessionPreference.Inherit,
        PowerShellSessionPreference informationPreference = PowerShellSessionPreference.Inherit,
        string? allowedModulePath = null,
        PowerShellSessionConfiguration? configuration = null)
    {
        if (configuration is not null && !string.IsNullOrEmpty(allowedModulePath))
        {
            throw new ArgumentException(
                "Use PowerShellSessionConfiguration.AllowedModulePaths instead of combining it with the legacy allowedModulePath argument.",
                nameof(allowedModulePath));
        }

        RunspaceMode = runspaceMode;
        InitialConfiguration = initialConfiguration;
        HistoryMode = historyMode;
        ErrorPreference = errorPreference;
        WarningPreference = warningPreference;
        VerbosePreference = verbosePreference;
        DebugPreference = debugPreference;
        InformationPreference = informationPreference;
        AllowedModulePath = allowedModulePath ?? string.Empty;
        Configuration = configuration ?? PowerShellSessionConfiguration.FromLegacyModulePath(AllowedModulePath);
    }

    public PowerShellRunspaceMode RunspaceMode { get; }

    public PowerShellSessionInitialConfiguration InitialConfiguration { get; }

    public PowerShellSessionHistoryMode HistoryMode { get; }

    public PowerShellSessionPreference ErrorPreference { get; }

    public PowerShellSessionPreference WarningPreference { get; }

    public PowerShellSessionPreference VerbosePreference { get; }

    public PowerShellSessionPreference DebugPreference { get; }

    public PowerShellSessionPreference InformationPreference { get; }

    public string AllowedModulePath { get; }

    public PowerShellSessionConfiguration Configuration { get; }
}
