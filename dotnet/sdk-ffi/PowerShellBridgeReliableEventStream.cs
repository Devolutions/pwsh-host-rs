namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// The terminal condition of a bounded, retained generated bridge event stream.
/// Overflow is explicit: subsequently posted records are counted as dropped
/// rather than silently replacing unacknowledged records.
/// </summary>
public enum PowerShellBridgeReliableEventTerminalState
{
    Active = 0,
    RetentionOverflow = 1,
    LeaseClosed = 2,
    ChannelClosed = 3,
    Disposed = 4,
}

/// <summary>Copied state returned with each reliable event read.</summary>
public readonly record struct PowerShellBridgeReliableEventStreamInfo(
    ulong NextSequence,
    ulong AcknowledgedSequence,
    long DroppedEventCount,
    PowerShellBridgeReliableEventTerminalState TerminalState)
{
    /// <summary>Gets whether no further records will be admitted to this stream.</summary>
    public bool IsTerminal => TerminalState != PowerShellBridgeReliableEventTerminalState.Active;
}

/// <summary>
/// The closed generated-contract identity of one observed reliable event stream.
/// It contains only copied numeric identifiers, never a payload object or CLR
/// object reference.
/// </summary>
public readonly record struct PowerShellBridgeReliableEventStreamIdentity(
    ulong LeaseId,
    uint Generation,
    ulong ObjectId,
    uint MemberId);

/// <summary>
/// A copied, sequence-numbered generated event. Call <see cref="Dispatch"/> from
/// a worker after the pull pump returns it; it never invokes application code on
/// the pipeline thread.
/// </summary>
public sealed class PowerShellBridgeReliableEvent
{
    private readonly PowerShellBridgeDispatch dispatch;

    internal PowerShellBridgeReliableEvent(ulong sequence, PowerShellBridgeDispatch dispatch)
    {
        Sequence = sequence;
        this.dispatch = dispatch;
    }

    /// <summary>Gets this stream-local, non-zero sequence number.</summary>
    public ulong Sequence { get; }

    /// <summary>Gets the immutable copied bridge frame.</summary>
    public ReadOnlyMemory<byte> Frame => dispatch.Frame;

    /// <summary>
    /// Runs generated authorization and typed event handling once on the caller's
    /// worker. It may not be called from the bridge pump.
    /// </summary>
    public void Dispatch()
    {
        dispatch.DispatchDetailed();
    }

    internal void Dispose()
    {
        dispatch.Dispose();
    }
}

/// <summary>
/// A bounded cursor read. Acknowledgement is intentionally separate so a host
/// can retry reading an unacknowledged copied record after worker scheduling.
/// </summary>
public sealed class PowerShellBridgeReliableEventBatch
{
    internal PowerShellBridgeReliableEventBatch(
        IReadOnlyList<PowerShellBridgeReliableEvent> events,
        PowerShellBridgeReliableEventStreamInfo info)
    {
        Events = events;
        Info = info;
    }

    /// <summary>Gets the copied records after the requested cursor.</summary>
    public IReadOnlyList<PowerShellBridgeReliableEvent> Events { get; }

    /// <summary>Gets the stream cursor, loss, and terminal state snapshot.</summary>
    public PowerShellBridgeReliableEventStreamInfo Info { get; }
}

/// <summary>
/// A fixed-capacity retained stream for one generated reliable event member.
/// It is created by the channel when the first frame arrives and is scoped to
/// one channel binding, lease generation, object handle, and member ordinal.
/// </summary>
public sealed class PowerShellBridgeReliableEventStream : IDisposable
{
    private readonly object gate = new();
    private readonly PowerShellBridgeChannel channel;
    private readonly PowerShellBridgeReliableEventStreamKey key;
    private readonly int maximumRetainedEvents;
    private readonly Queue<PowerShellBridgeReliableEvent> events = [];
    private ulong nextSequence = 1;
    private ulong acknowledgedSequence;
    private ulong maximumAcknowledgableSequence;
    private long droppedEventCount;
    private PowerShellBridgeReliableEventTerminalState terminalState;
    private int disposed;

    internal PowerShellBridgeReliableEventStream(
        PowerShellBridgeChannel channel,
        PowerShellBridgeReliableEventStreamKey key,
        int maximumRetainedEvents)
    {
        this.channel = channel;
        this.key = key;
        this.maximumRetainedEvents = maximumRetainedEvents;
    }

    /// <summary>Gets the generated maximum count of unacknowledged retained records.</summary>
    public int MaximumRetainedEvents => maximumRetainedEvents;

    /// <summary>Gets the copied lease-scoped identity for this stream.</summary>
    public PowerShellBridgeReliableEventStreamIdentity Identity =>
        new(key.LeaseId, key.Generation, key.ObjectId, key.MemberId);

    /// <summary>Gets the current stream terminal state and cursor accounting.</summary>
    public PowerShellBridgeReliableEventStreamInfo GetInfo()
    {
        lock (gate)
        {
            return GetInfoLocked();
        }
    }

    /// <summary>
    /// Copies at most <paramref name="maximumEvents"/> retained records after
    /// <paramref name="afterSequence"/>. Reads do not acknowledge records.
    /// </summary>
    public PowerShellBridgeReliableEventBatch Read(ulong afterSequence, int maximumEvents)
    {
        if (maximumEvents is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (afterSequence < acknowledgedSequence || afterSequence >= nextSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(afterSequence));
            }

            var copied = new List<PowerShellBridgeReliableEvent>(Math.Min(maximumEvents, events.Count));
            foreach (PowerShellBridgeReliableEvent @event in events)
            {
                if (@event.Sequence > afterSequence)
                {
                    copied.Add(@event);
                    if (copied.Count == maximumEvents)
                    {
                        break;
                    }
                }
            }

            if (copied.Count != 0)
            {
                maximumAcknowledgableSequence = copied[^1].Sequence;
            }

            return new PowerShellBridgeReliableEventBatch(copied, GetInfoLocked());
        }
    }

    /// <summary>
    /// Releases all records through <paramref name="sequence"/> after they have
    /// been handed to application workers. The cursor must not exceed the last
    /// sequence returned by <see cref="Read"/>.
    /// </summary>
    public void Acknowledge(ulong sequence)
    {
        List<PowerShellBridgeReliableEvent>? released = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (sequence < acknowledgedSequence || sequence > maximumAcknowledgableSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }

            while (events.Count != 0 && events.Peek().Sequence <= sequence)
            {
                (released ??= []).Add(events.Dequeue());
            }

            acknowledgedSequence = sequence;
            maximumAcknowledgableSequence = acknowledgedSequence;
        }

        Release(released);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        List<PowerShellBridgeReliableEvent> released;
        lock (gate)
        {
            terminalState = PowerShellBridgeReliableEventTerminalState.Disposed;
            released = events.ToList();
            events.Clear();
        }

        Release(released);
        channel.RemoveReliableStream(key, this);
    }

    internal bool TryRetain(PowerShellBridgeDispatch dispatch)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            if (terminalState != PowerShellBridgeReliableEventTerminalState.Active)
            {
                if (terminalState == PowerShellBridgeReliableEventTerminalState.RetentionOverflow)
                {
                    droppedEventCount = checked(droppedEventCount + 1);
                }

                return false;
            }

            if (events.Count >= maximumRetainedEvents)
            {
                droppedEventCount = checked(droppedEventCount + 1);
                terminalState = PowerShellBridgeReliableEventTerminalState.RetentionOverflow;
                return false;
            }

            events.Enqueue(new PowerShellBridgeReliableEvent(nextSequence++, dispatch));
            return true;
        }
    }

    internal void MarkChannelOverflow()
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) == 0 &&
                terminalState == PowerShellBridgeReliableEventTerminalState.Active)
            {
                droppedEventCount = checked(droppedEventCount + 1);
                terminalState = PowerShellBridgeReliableEventTerminalState.RetentionOverflow;
            }
        }
    }

    internal void TerminateFromChannel(PowerShellBridgeReliableEventTerminalState state)
    {
        List<PowerShellBridgeReliableEvent>? released = null;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            terminalState = state;
            if (events.Count != 0)
            {
                released = events.ToList();
                events.Clear();
                droppedEventCount = checked(droppedEventCount + released.Count);
            }
        }

        Release(released);
    }

    private PowerShellBridgeReliableEventStreamInfo GetInfoLocked()
    {
        return new PowerShellBridgeReliableEventStreamInfo(
            nextSequence,
            acknowledgedSequence,
            droppedEventCount,
            terminalState);
    }

    private void Release(List<PowerShellBridgeReliableEvent>? released)
    {
        if (released is null)
        {
            return;
        }

        foreach (PowerShellBridgeReliableEvent @event in released)
        {
            @event.Dispose();
            channel.ReleaseReliableEvent();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(PowerShellBridgeReliableEventStream));
        }
    }
}

internal readonly record struct PowerShellBridgeReliableEventStreamKey(
    ulong BindingId,
    ulong LeaseId,
    uint Generation,
    ulong ObjectId,
    uint MemberId);
