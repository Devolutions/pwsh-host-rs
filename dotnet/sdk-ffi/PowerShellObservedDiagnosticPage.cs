using System.Collections.Generic;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// One lossless copied diagnostic record emitted by an observed invocation.
/// </summary>
public sealed class PowerShellObservedDiagnosticRecord
{
    internal PowerShellObservedDiagnosticRecord(PowerShellStreamKind stream, ulong sequence, string text)
    {
        Stream = stream;
        Sequence = sequence;
        Text = text;
    }

    public PowerShellStreamKind Stream { get; }

    public ulong Sequence { get; }

    public string Text { get; }
}

/// <summary>
/// An acknowledgement page of copied diagnostic records.
/// </summary>
public sealed class PowerShellObservedDiagnosticPage
{
    internal PowerShellObservedDiagnosticPage(
        PowerShellObservedDiagnosticRecord[] records,
        ulong acknowledgedSequence,
        ulong nextSequence,
        ulong totalRecordCount,
        ulong droppedRecordCount,
        PowerShellFfiStatus terminalStatus,
        bool isTerminal,
        bool isTruncated,
        bool isComplete)
    {
        Records = Array.AsReadOnly(records);
        AcknowledgedSequence = acknowledgedSequence;
        NextSequence = nextSequence;
        TotalRecordCount = totalRecordCount;
        DroppedRecordCount = droppedRecordCount;
        TerminalStatus = terminalStatus;
        IsTerminal = isTerminal;
        IsTruncated = isTruncated;
        IsComplete = isComplete;
    }

    public IReadOnlyList<PowerShellObservedDiagnosticRecord> Records { get; }

    public ulong AcknowledgedSequence { get; }

    public ulong NextSequence { get; }

    public ulong TotalRecordCount { get; }

    public ulong DroppedRecordCount { get; }

    public PowerShellFfiStatus TerminalStatus { get; }

    public bool IsTerminal { get; }

    public bool IsTruncated { get; }

    /// <summary>
    /// True only after both observed channels reached successful terminal states
    /// and every copied record was acknowledged.
    /// </summary>
    public bool IsComplete { get; }
}
