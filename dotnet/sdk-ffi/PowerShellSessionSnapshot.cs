namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellSessionSnapshot
{
    internal PowerShellSessionSnapshot(
        PowerShellSessionState state,
        PowerShellSessionState runspaceState,
        bool areEventsTruncated,
        uint activePipelineCount,
        ulong invocationCount,
        ulong historyCount)
    {
        State = state;
        RunspaceState = runspaceState;
        AreEventsTruncated = areEventsTruncated;
        ActivePipelineCount = activePipelineCount;
        InvocationCount = invocationCount;
        HistoryCount = historyCount;
    }

    public PowerShellSessionState State { get; }

    public PowerShellSessionState RunspaceState { get; }

    public bool AreEventsTruncated { get; }

    public uint ActivePipelineCount { get; }

    public ulong InvocationCount { get; }

    public ulong HistoryCount { get; }
}
