using System.Collections.Generic;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Caller-configured bounds for a lossless copied-value result pager.
/// </summary>
public sealed class PowerShellValuePagerOptions
{
    public PowerShellValuePagerOptions(int maximumBufferedRecords = 32, int maximumPageRecords = 32)
    {
        if (maximumBufferedRecords < 1 || maximumBufferedRecords > PowerShellValue.MaximumContainerEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedRecords));
        }

        if (maximumPageRecords < 1 || maximumPageRecords > maximumBufferedRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPageRecords));
        }

        MaximumBufferedRecords = maximumBufferedRecords;
        MaximumPageRecords = maximumPageRecords;
    }

    public int MaximumBufferedRecords { get; }

    public int MaximumPageRecords { get; }
}

public sealed class PowerShellValuePageRecord
{
    internal PowerShellValuePageRecord(ulong sequence, PowerShellValue value)
    {
        Sequence = sequence;
        Value = value;
    }

    public ulong Sequence { get; }

    public PowerShellValue Value { get; }
}

public sealed class PowerShellValuePage
{
    internal PowerShellValuePage(
        IReadOnlyList<PowerShellValuePageRecord> records,
        ulong acknowledgedSequence,
        ulong nextSequence,
        ulong totalRecordCount,
        PowerShellFfiStatus terminalStatus,
        bool isTerminal)
    {
        Records = records;
        AcknowledgedSequence = acknowledgedSequence;
        NextSequence = nextSequence;
        TotalRecordCount = totalRecordCount;
        TerminalStatus = terminalStatus;
        IsTerminal = isTerminal;
    }

    public IReadOnlyList<PowerShellValuePageRecord> Records { get; }

    /// <summary>
    /// The sequence the reader supplied to this page request.
    /// </summary>
    public ulong AcknowledgedSequence { get; }

    /// <summary>
    /// Acknowledgement cursor for the records returned by this page.
    /// </summary>
    public ulong NextSequence { get; }

    public ulong TotalRecordCount { get; }

    public PowerShellFfiStatus TerminalStatus { get; }

    public bool IsTerminal { get; }
}

public sealed class PowerShellValuePagerCompletion
{
    internal PowerShellValuePagerCompletion(
        PowerShellFfiStatus terminalStatus,
        string diagnostic,
        ulong totalRecordCount,
        ulong acknowledgedRecordCount)
    {
        TerminalStatus = terminalStatus;
        Diagnostic = diagnostic;
        TotalRecordCount = totalRecordCount;
        AcknowledgedRecordCount = acknowledgedRecordCount;
    }

    public PowerShellFfiStatus TerminalStatus { get; }

    public string Diagnostic { get; }

    public ulong TotalRecordCount { get; }

    public ulong AcknowledgedRecordCount { get; }

    public bool IsComplete =>
        TerminalStatus == PowerShellFfiStatus.Success &&
        TotalRecordCount == AcknowledgedRecordCount;
}

/// <summary>
/// A bounded, lossless acknowledgement pager for copied <see cref="PowerShellValue"/> records.
/// </summary>
/// <remarks>
/// Producers block when the configured buffer is full. Records are retained only
/// until the consumer explicitly acknowledges their sequence. A terminal success
/// cannot be reported complete until every record has been acknowledged.
/// </remarks>
public sealed class PowerShellValuePager : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<PowerShellValuePageRecord> records;
    private readonly int maximumPageRecords;
    private readonly int maximumBufferedRecords;
    private ulong nextSequence = 1;
    private ulong acknowledgedSequence;
    private ulong maximumAcknowledgableSequence;
    private ulong totalRecordCount;
    private PowerShellFfiStatus terminalStatus = PowerShellFfiStatus.Success;
    private string diagnostic = string.Empty;
    private bool terminal;
    private bool disposed;

    public PowerShellValuePager(PowerShellValuePagerOptions? options = null)
    {
        options ??= new PowerShellValuePagerOptions();
        maximumBufferedRecords = options.MaximumBufferedRecords;
        maximumPageRecords = options.MaximumPageRecords;
        records = new Queue<PowerShellValuePageRecord>(maximumBufferedRecords);
    }

    public void Write(PowerShellValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            while (!terminal && !disposed && records.Count == maximumBufferedRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(gate, TimeSpan.FromMilliseconds(50));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfClosed();
            records.Enqueue(new PowerShellValuePageRecord(nextSequence, value));
            nextSequence = checked(nextSequence + 1);
            totalRecordCount = checked(totalRecordCount + 1);
            Monitor.PulseAll(gate);
        }
    }

    public PowerShellValuePage Read(ulong acknowledgedThrough, int? maximumRecords = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            int limit = maximumRecords ?? maximumPageRecords;
            if (limit < 1 || limit > maximumPageRecords)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRecords));
            }

            if (acknowledgedThrough != acknowledgedSequence)
            {
                throw new InvalidOperationException("Result pages must be read from the last acknowledged cursor.");
            }

            PowerShellValuePageRecord[] page = records.Take(limit).ToArray();
            ulong next = page.Length == 0 ? acknowledgedSequence : page[^1].Sequence;
            maximumAcknowledgableSequence = next;
            return new PowerShellValuePage(
                Array.AsReadOnly(page),
                acknowledgedSequence,
                next,
                totalRecordCount,
                terminalStatus,
                terminal);
        }
    }

    public void Acknowledge(ulong sequence)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (sequence < acknowledgedSequence || sequence > maximumAcknowledgableSequence)
            {
                throw new InvalidOperationException("Result acknowledgement is outside the most recently returned page.");
            }

            while (records.Count != 0 && records.Peek().Sequence <= sequence)
            {
                records.Dequeue();
            }

            acknowledgedSequence = sequence;
            maximumAcknowledgableSequence = acknowledgedSequence;
            Monitor.PulseAll(gate);
        }
    }

    public void Complete(PowerShellFfiStatus status = PowerShellFfiStatus.Success, string? terminalDiagnostic = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (terminal)
            {
                throw new InvalidOperationException("The result pager is already terminal.");
            }

            terminal = true;
            terminalStatus = status;
            diagnostic = terminalDiagnostic ?? string.Empty;
            Monitor.PulseAll(gate);
        }
    }

    public PowerShellValuePagerCompletion GetCompletion()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!terminal)
            {
                throw new InvalidOperationException("The result pager has not reached a terminal state.");
            }

            return new PowerShellValuePagerCompletion(
                terminalStatus,
                diagnostic,
                totalRecordCount,
                acknowledgedSequence);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            terminal = true;
            terminalStatus = PowerShellFfiStatus.OperationCancelled;
            diagnostic = "The result pager was disposed before final acknowledgement.";
            records.Clear();
            Monitor.PulseAll(gate);
        }
    }

    private void ThrowIfClosed()
    {
        ThrowIfDisposed();
        if (terminal)
        {
            throw new InvalidOperationException("The result pager is terminal and cannot accept additional records.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
