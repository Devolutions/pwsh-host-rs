using System.Collections.Generic;

namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellInvocationStreamBatch
{
    internal PowerShellInvocationStreamBatch(
        PowerShellInvocationStreamRecord[] records,
        ulong nextSequence,
        PowerShellOperationState state,
        PowerShellFfiStatus terminalStatus,
        bool isCursorLost,
        ulong lostRecordCount,
        bool isTruncated,
        ulong totalRecordCount,
        ulong droppedRecordCount,
        ulong sourceDroppedRecordCount)
    {
        Records = Array.AsReadOnly(records);
        NextSequence = nextSequence;
        State = state;
        TerminalStatus = terminalStatus;
        IsCursorLost = isCursorLost;
        LostRecordCount = lostRecordCount;
        IsTruncated = isTruncated;
        TotalRecordCount = totalRecordCount;
        DroppedRecordCount = droppedRecordCount;
        SourceDroppedRecordCount = sourceDroppedRecordCount;
    }

    public IReadOnlyList<PowerShellInvocationStreamRecord> Records { get; }

    public ulong NextSequence { get; }

    public PowerShellOperationState State { get; }

    public PowerShellFfiStatus TerminalStatus { get; }

    public bool IsTerminal => State is PowerShellOperationState.Completed or
        PowerShellOperationState.Cancelled or PowerShellOperationState.Failed;

    public bool IsCursorLost { get; }

    public ulong LostRecordCount { get; }

    public bool IsTruncated { get; }

    public ulong TotalRecordCount { get; }

    public ulong DroppedRecordCount { get; }

    public ulong SourceDroppedRecordCount { get; }
}
