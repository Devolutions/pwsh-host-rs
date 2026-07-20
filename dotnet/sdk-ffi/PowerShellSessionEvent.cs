namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellSessionEvent
{
    internal PowerShellSessionEvent(ulong sequence, PowerShellSessionState state, bool isTruncated)
    {
        Sequence = sequence;
        State = state;
        IsTruncated = isTruncated;
    }

    public ulong Sequence { get; }

    public PowerShellSessionState State { get; }

    public bool IsTruncated { get; }
}
