using System.Collections.Generic;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// One copied presentation record emitted by an observed invocation.
/// </summary>
/// <remarks>
/// <para>
/// Text remains the bounded display representation produced by PowerShell. When
/// <see cref="Stream"/> is <see cref="PowerShellStreamKind.Progress"/>, <see cref="Progress"/>
/// contains the separate, fixed-shape copied progress fields. No PowerShell record or
/// application object crosses this boundary.
/// </para>
/// <para>
/// This record is read from the same acknowledgement cursor as
/// <see cref="PowerShellObservedDiagnosticRecord"/>. A caller must use either
/// <see cref="PowerShellObservedInvocation.ReadDiagnostics(ulong, int?)"/> or
/// <see cref="PowerShellObservedInvocation.ReadPresentation(ulong, int?)"/> for a cursor
/// sequence, not both.
/// </para>
/// </remarks>
public sealed class PowerShellObservedPresentationRecord
{
    internal PowerShellObservedPresentationRecord(
        PowerShellStreamKind stream,
        ulong sequence,
        string text,
        PowerShellProgressUpdate? progress)
    {
        Stream = stream;
        Sequence = sequence;
        Text = text;
        Progress = progress;
    }

    public PowerShellStreamKind Stream { get; }

    public ulong Sequence { get; }

    public string Text { get; }

    /// <summary>
    /// The copied fixed-shape progress update for a progress-stream record; otherwise null.
    /// </summary>
    public PowerShellProgressUpdate? Progress { get; }
}

/// <summary>
/// An acknowledgement page of copied presentation records.
/// </summary>
public sealed class PowerShellObservedPresentationPage
{
    internal PowerShellObservedPresentationPage(
        PowerShellObservedPresentationRecord[] records,
        ulong acknowledgedSequence,
        ulong nextSequence,
        ulong totalRecordCount,
        PowerShellFfiStatus terminalStatus,
        bool isTerminal,
        bool isComplete)
    {
        Records = Array.AsReadOnly(records);
        AcknowledgedSequence = acknowledgedSequence;
        NextSequence = nextSequence;
        TotalRecordCount = totalRecordCount;
        TerminalStatus = terminalStatus;
        IsTerminal = isTerminal;
        IsComplete = isComplete;
    }

    public IReadOnlyList<PowerShellObservedPresentationRecord> Records { get; }

    public ulong AcknowledgedSequence { get; }

    public ulong NextSequence { get; }

    public ulong TotalRecordCount { get; }

    public PowerShellFfiStatus TerminalStatus { get; }

    public bool IsTerminal { get; }

    /// <summary>
    /// True only after both observed channels reached successful terminal states
    /// and every copied record was acknowledged.
    /// </summary>
    public bool IsComplete { get; }
}
