namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellSessionPoolOptions
{
    public PowerShellSessionPoolOptions(uint minimumSessions, uint maximumSessions)
    {
        MinimumSessions = minimumSessions;
        MaximumSessions = maximumSessions;
    }

    public uint MinimumSessions { get; }

    public uint MaximumSessions { get; }
}
