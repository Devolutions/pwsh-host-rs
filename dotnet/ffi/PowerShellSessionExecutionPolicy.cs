namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// The noninteractive execution-policy subset accepted for a newly owned local runspace.
/// Execution policy is not a security boundary.
/// </summary>
public enum PowerShellSessionExecutionPolicy
{
    Default = 0,
    Restricted = 1,
}
