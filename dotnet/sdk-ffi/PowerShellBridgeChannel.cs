using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Owns one broker channel and the generated bridge dispatchers registered to
/// receive frames on it. A bridge channel is constructed by
/// <see cref="PowerShellRuntime.CreateBridgeChannel"/>.
/// </summary>
public sealed class PowerShellBridgeChannel : IDisposable
{
    private const int MaximumReliableStreams = 32;
    private const int MaximumRetainedReliableEvents = 256;

    private readonly object gate = new();
    private readonly PowerShellBrokerChannel broker;
    private readonly Dictionary<ulong, PowerShellBridgeBinding> bindings = [];
    private readonly Dictionary<PowerShellBridgeReliableEventStreamKey, PowerShellBridgeReliableEventStream> reliableStreams = [];
    private int retainedReliableEventCount;
    private long droppedReliableEventCount;
    private long droppedReliableStreamCount;
    private ulong nextBindingId;
    private int disposed;

    internal PowerShellBridgeChannel(PowerShellBrokerChannel broker)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    /// <summary>
    /// Registers a generated dispatcher on this channel and returns the binding
    /// used to assign its closed contract to a session.
    /// </summary>
    public PowerShellBridgeBinding CreateBinding(IPowerShellBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        lock (gate)
        {
            ThrowIfDisposed();
            ulong bindingId = NextBindingIdLocked();
            var binding = new PowerShellBridgeBinding(this, bindingId, dispatcher);
            bindings.Add(bindingId, binding);
            return binding;
        }
    }

    /// <summary>
        /// Gets the total number of reliable event frames rejected after a stream
        /// reached its generated retention bound or the channel's aggregate bound.
        /// This is channel-wide accounting; a stream exposes its own dropped count.
        /// </summary>
    public long DroppedReliableEventCount
        {
            get
            {
                lock (gate)
                {
                    return droppedReliableEventCount;
                }
            }
        }

    /// <summary>
        /// Gets the number of distinct reliable streams rejected because the
        /// channel's fixed stream table was full.
        /// </summary>
    public long DroppedReliableStreamCount
        {
            get
            {
                lock (gate)
                {
                    return droppedReliableStreamCount;
                }
            }
        }

    /// <summary>
        /// Looks up a generated reliable event stream that has been observed on this
        /// channel. A stream is scoped to its binding, lease generation, object
        /// handle, and member ordinal; handles from another channel never match.
        /// </summary>
    public bool TryGetReliableEventStream(
            PowerShellBridgeBinding binding,
            ulong leaseId,
            uint generation,
            ulong objectId,
            uint memberId,
            out PowerShellBridgeReliableEventStream? stream)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(binding.Channel, this) ||
            leaseId == 0 ||
            generation == 0 ||
            objectId == 0 ||
            memberId == 0)
        {
            stream = null;
            return false;
        }

        lock (gate)
        {
            return reliableStreams.TryGetValue(
                new PowerShellBridgeReliableEventStreamKey(binding.BindingId, leaseId, generation, objectId, memberId),
                out stream);
        }
    }

    /// <summary>
    /// Gets the currently observed reliable event streams for one binding. The
    /// returned snapshot is bounded by the channel's fixed stream table and does
    /// not discover members beyond frames the payload has already emitted.
    /// </summary>
    public IReadOnlyList<PowerShellBridgeReliableEventStream> GetReliableEventStreams(PowerShellBridgeBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(binding.Channel, this))
        {
            throw new ArgumentException("The bridge binding belongs to another channel.", nameof(binding));
        }

        lock (gate)
        {
            return reliableStreams
                .Where(pair => pair.Key.BindingId == binding.BindingId)
                .Select(static pair => pair.Value)
                .ToArray();
        }
    }

    internal PowerShellBrokerChannel Broker
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return broker;
            }
        }
    }

    internal void Remove(PowerShellBridgeBinding binding)
    {
        PowerShellBridgeReliableEventStream[] streams;
        lock (gate)
        {
            if (bindings.TryGetValue(binding.BindingId, out PowerShellBridgeBinding? registered) &&
                ReferenceEquals(registered, binding))
            {
                bindings.Remove(binding.BindingId);
            }

            streams = DetachReliableStreamsLocked(pair => pair.Key.BindingId == binding.BindingId);
        }

        foreach (PowerShellBridgeReliableEventStream stream in streams)
        {
            stream.TerminateFromChannel(PowerShellBridgeReliableEventTerminalState.LeaseClosed);
        }
    }

    /// <summary>
    /// Pulls and structurally validates one generated bridge frame. The returned
    /// work item must be dispatched asynchronously; this method never invokes an
    /// application dispatcher on the broker pump thread.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> after the channel closes. A <see langword="true"/>
    /// result with no work means either timeout or a rejected frame.
    /// </returns>
    public bool TryReceive(TimeSpan timeout, out PowerShellBridgeDispatch? dispatch)
    {
        dispatch = null;
        if (!broker.TryReceiveWithTerminalObservation(timeout, out PowerShellBrokerRequest? request))
        {
            return false;
        }
        if (request is null)
        {
            return true;
        }

        if (!PowerShellBridgeBrokerWire.TryReadRoute(request.Body, out ulong bindingId))
        {
            RejectAndRelease(request, "The generated bridge route is malformed.");
            return true;
        }

        PowerShellBridgeBinding? binding;
        lock (gate)
        {
            bindings.TryGetValue(bindingId, out binding);
        }
        if (binding is null)
        {
            RejectAndRelease(request, "The generated bridge binding is not registered on this channel.");
            return true;
        }

        PowerShellBridgeDispatcherLease dispatcherLease;
        try
        {
            dispatcherLease = binding.AcquireDispatcher();
        }
        catch (ObjectDisposedException)
        {
            RejectAndRelease(request, "The generated bridge binding has been released.");
            return true;
        }
        IPowerShellBridgeDispatcher dispatcher = dispatcherLease.Dispatcher;

        ReadOnlySpan<byte> frame = request.Body.AsSpan(PowerShellBridgeBrokerWire.RouteHeaderSize);
        bool isEvent = request.Kind == PowerShellBridgeBrokerWire.EventKind;
        bool isReliableEvent = false;
        if ((isEvent && !request.IsOneWay) ||
            (!isEvent && (request.Kind != PowerShellBridgeBrokerWire.RequestKind || request.IsOneWay)) ||
            frame.Length < PowerShellBridgeWire.RequestHeaderSize ||
            frame.Length > dispatcher.MaximumRequestBytes ||
            !PowerShellBridgeRequestHeader.TryRead(frame, out PowerShellBridgeRequestHeader header) ||
            (isEvent != (header.FrameKind is PowerShellBridgeFrameKind.Event or PowerShellBridgeFrameKind.ReliableEvent)))
        {
            dispatcherLease.Dispose();
            RejectAndRelease(request, "The generated bridge frame is invalid for its route.");
            return true;
        }

        isReliableEvent = header.FrameKind == PowerShellBridgeFrameKind.ReliableEvent;
        if (header.FrameKind == PowerShellBridgeFrameKind.Close)
        {
            TerminateReliableStreams(
                binding.BindingId,
                header.LeaseId,
                header.Generation,
                PowerShellBridgeReliableEventTerminalState.LeaseClosed);
        }

        dispatch = new PowerShellBridgeDispatch(
            broker,
            request,
            dispatcherLease,
            header,
            frame.ToArray(),
            isEvent);
        if (isReliableEvent)
        {
            int maximumRetained = dispatcher.GetReliableEventMaximumRetained(header.MemberId);
            if (maximumRetained is < 1 or > 64 || !TryRetainReliableEvent(binding, header, maximumRetained, dispatch))
            {
                dispatch.Dispose();
            }

            dispatch = null;
        }

        return true;
    }

    /// <summary>
    /// Disposes every registered binding before closing the native broker
    /// channel. Each binding is attempted even when an earlier dispatcher fails.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        PowerShellBridgeBinding[] released;
        lock (gate)
        {
            released = bindings.Values.ToArray();
            bindings.Clear();
            foreach (PowerShellBridgeReliableEventStream stream in reliableStreams.Values)
            {
                stream.TerminateFromChannel(PowerShellBridgeReliableEventTerminalState.ChannelClosed);
            }

            reliableStreams.Clear();
        }

        Exception? failure = null;
        try
        {
            foreach (PowerShellBridgeBinding binding in released)
            {
                try
                {
                    binding.DisposeFromChannel();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
        }
        finally
        {
            broker.Dispose();
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private ulong NextBindingIdLocked()
    {
        do
        {
            nextBindingId = checked(nextBindingId + 1);
        }
        while (nextBindingId == 0 || bindings.ContainsKey(nextBindingId));

        return nextBindingId;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(PowerShellBridgeChannel));
        }
    }

    private void RejectAndRelease(PowerShellBrokerRequest request, string message)
    {
        try
        {
            if (!request.IsOneWay)
            {
                broker.TryReplyError(request.CorrelationId, PowerShellBridgeStatus.InvalidArgument, message);
            }
        }
        finally
        {
            request.TerminalObservation?.Dispose();
        }
    }

    private bool TryRetainReliableEvent(
        PowerShellBridgeBinding binding,
        PowerShellBridgeRequestHeader header,
        int maximumRetained,
        PowerShellBridgeDispatch dispatch)
    {
        var key = new PowerShellBridgeReliableEventStreamKey(
            binding.BindingId,
            header.LeaseId,
            header.Generation,
            header.ObjectId,
            header.MemberId);
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            if (!bindings.TryGetValue(binding.BindingId, out PowerShellBridgeBinding? registered) ||
                !ReferenceEquals(registered, binding))
            {
                return false;
            }

            if (!reliableStreams.TryGetValue(key, out PowerShellBridgeReliableEventStream? stream))
            {
                if (reliableStreams.Count >= MaximumReliableStreams)
                {
                    droppedReliableEventCount = checked(droppedReliableEventCount + 1);
                    droppedReliableStreamCount = checked(droppedReliableStreamCount + 1);
                    return false;
                }

                stream = new PowerShellBridgeReliableEventStream(this, key, maximumRetained);
                reliableStreams.Add(key, stream);
            }

            if (Interlocked.Increment(ref retainedReliableEventCount) > MaximumRetainedReliableEvents)
            {
                Interlocked.Decrement(ref retainedReliableEventCount);
                droppedReliableEventCount = checked(droppedReliableEventCount + 1);
                stream.MarkChannelOverflow();
                return false;
            }

            if (!stream.TryRetain(dispatch))
            {
                Interlocked.Decrement(ref retainedReliableEventCount);
                droppedReliableEventCount = checked(droppedReliableEventCount + 1);
                return false;
            }

            return true;
        }
    }

    internal void ReleaseReliableEvent()
    {
        if (Interlocked.Decrement(ref retainedReliableEventCount) < 0)
        {
            Interlocked.Increment(ref retainedReliableEventCount);
            throw new InvalidOperationException("The reliable bridge event retention count underflowed.");
        }
    }

    internal void RemoveReliableStream(
        PowerShellBridgeReliableEventStreamKey key,
        PowerShellBridgeReliableEventStream stream)
    {
        lock (gate)
        {
            if (reliableStreams.TryGetValue(key, out PowerShellBridgeReliableEventStream? registered) &&
                ReferenceEquals(registered, stream))
            {
                reliableStreams.Remove(key);
            }
        }
    }

    private void TerminateReliableStreams(
        ulong bindingId,
        ulong leaseId,
        uint generation,
        PowerShellBridgeReliableEventTerminalState state)
    {
        PowerShellBridgeReliableEventStream[] streams;
        lock (gate)
        {
            streams = DetachReliableStreamsLocked(pair =>
                pair.Key.BindingId == bindingId &&
                pair.Key.LeaseId == leaseId &&
                pair.Key.Generation == generation);
        }

        foreach (PowerShellBridgeReliableEventStream stream in streams)
        {
            stream.TerminateFromChannel(state);
        }
    }

    private PowerShellBridgeReliableEventStream[] DetachReliableStreamsLocked(
        Func<KeyValuePair<PowerShellBridgeReliableEventStreamKey, PowerShellBridgeReliableEventStream>, bool> predicate)
    {
        KeyValuePair<PowerShellBridgeReliableEventStreamKey, PowerShellBridgeReliableEventStream>[] matches = reliableStreams
            .Where(predicate)
            .ToArray();
        foreach (KeyValuePair<PowerShellBridgeReliableEventStreamKey, PowerShellBridgeReliableEventStream> match in matches)
        {
            reliableStreams.Remove(match.Key);
        }

        return matches.Select(static match => match.Value).ToArray();
    }
}

/// <summary>
/// Associates one generated bridge dispatcher with one
/// <see cref="PowerShellBridgeChannel"/>. The binding owns its dispatcher.
/// </summary>
public sealed class PowerShellBridgeBinding : IDisposable
{
    private readonly PowerShellBridgeChannel channel;
    private readonly object gate = new();
    private IPowerShellBridgeDispatcher? dispatcher;
    private int disposed;
    private int activeDispatches;

    internal PowerShellBridgeBinding(
        PowerShellBridgeChannel channel,
        ulong bindingId,
        IPowerShellBridgeDispatcher dispatcher)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        BindingId = bindingId;
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Gets this channel-scoped, non-zero binding identity.</summary>
    public ulong BindingId { get; }

    /// <summary>Gets the descriptor identity the payload must request.</summary>
    public Guid ContractInterfaceId => GetDispatcher().ContractInterfaceId;

    /// <summary>Gets the descriptor major version the payload must request.</summary>
    public ushort ContractMajorVersion => GetDispatcher().ContractMajorVersion;

    /// <summary>Gets the descriptor minor version the payload must request.</summary>
    public ushort ContractMinorVersion => GetDispatcher().ContractMinorVersion;

    internal PowerShellBridgeChannel Channel => channel;

    internal IPowerShellBridgeDispatcher GetDispatcher()
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(PowerShellBridgeBinding));
            }

            return dispatcher ?? throw new ObjectDisposedException(nameof(PowerShellBridgeBinding));
        }
    }

    internal PowerShellBridgeDispatcherLease AcquireDispatcher()
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || dispatcher is null)
            {
                throw new ObjectDisposedException(nameof(PowerShellBridgeBinding));
            }

            activeDispatches = checked(activeDispatches + 1);
            return new PowerShellBridgeDispatcherLease(this, dispatcher);
        }
    }

    /// <summary>
    /// Removes this binding from its channel and disposes its generated
    /// dispatcher. It is idempotent.
    /// </summary>
    public void Dispose()
    {
        DisposeCore(removeFromChannel: true);
    }

    internal void DisposeFromChannel()
    {
        DisposeCore(removeFromChannel: false);
    }

    private void DisposeCore(bool removeFromChannel)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        IPowerShellBridgeDispatcher? released = null;
        lock (gate)
        {
            if (activeDispatches == 0)
            {
                released = dispatcher;
                dispatcher = null;
            }
        }
        try
        {
            released?.Dispose();
        }
        finally
        {
            if (removeFromChannel)
            {
                channel.Remove(this);
            }
        }
    }

    internal void ReleaseDispatcher()
    {
        IPowerShellBridgeDispatcher? released = null;
        lock (gate)
        {
            if (activeDispatches <= 0)
            {
                throw new InvalidOperationException("The generated bridge dispatcher lease was released more than once.");
            }

            activeDispatches--;
            if (activeDispatches == 0 && Volatile.Read(ref disposed) != 0)
            {
                released = dispatcher;
                dispatcher = null;
            }
        }

        released?.Dispose();
    }
}

/// <summary>
/// Holds a generated dispatcher alive after the channel pump copies a frame.
/// Disposing a binding removes new routing immediately but never disposes a
/// dispatcher while a previously admitted worker is using it.
/// </summary>
internal sealed class PowerShellBridgeDispatcherLease : IDisposable
{
    private PowerShellBridgeBinding? binding;

    internal PowerShellBridgeDispatcherLease(
        PowerShellBridgeBinding binding,
        IPowerShellBridgeDispatcher dispatcher)
    {
        this.binding = binding;
        Dispatcher = dispatcher;
    }

    internal IPowerShellBridgeDispatcher Dispatcher { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref binding, null)?.ReleaseDispatcher();
    }
}

/// <summary>
/// A copied, prevalidated bridge frame. Call <see cref="Dispatch"/> from the
/// host's worker queue, never inline from <see cref="PowerShellBridgeChannel.TryReceive"/>.
/// </summary>
public sealed class PowerShellBridgeDispatchResult
{
    internal PowerShellBridgeDispatchResult(
        bool handlerStarted,
        bool replyAccepted,
        PowerShellBrokerTerminalInfo? terminalState)
    {
        HandlerStarted = handlerStarted;
        ReplyAccepted = replyAccepted;
        TerminalState = terminalState;
    }

    /// <summary>Gets whether generated authorization and handler dispatch started.</summary>
    public bool HandlerStarted { get; }

    /// <summary>Gets whether the deferred reply completed a live correlation.</summary>
    public bool ReplyAccepted { get; }

    /// <summary>Gets the observed terminal state when one prevented or rejected dispatch.</summary>
    public PowerShellBrokerTerminalInfo? TerminalState { get; }
}

public sealed class PowerShellBridgeDispatch : IDisposable
{
    private readonly PowerShellBrokerChannel broker;
    private readonly PowerShellBrokerRequest request;
    private readonly PowerShellBridgeDispatcherLease dispatcherLease;
    private readonly IPowerShellBridgeDispatcher dispatcher;
    private readonly PowerShellBridgeRequestHeader header;
    private readonly byte[] frame;
    private readonly bool isEvent;
    private PowerShellBrokerTerminalObservation? terminalObservation;
    private int dispatched;
    // 0 = queued, 1 = worker entered dispatch, 2 = released. A reliable-event
    // stream can release a record concurrently with a queued worker, so release
    // must win before handler entry but must not free a dispatcher used by a
    // handler that already started.
    private int dispatchState;
    private int resourcesReleased;

    internal PowerShellBridgeDispatch(
        PowerShellBrokerChannel broker,
        PowerShellBrokerRequest request,
        PowerShellBridgeDispatcherLease dispatcherLease,
        PowerShellBridgeRequestHeader header,
        byte[] frame,
        bool isEvent)
    {
        this.broker = broker;
        this.request = request;
        this.dispatcherLease = dispatcherLease;
        dispatcher = dispatcherLease.Dispatcher;
        this.header = header;
        this.frame = frame;
        this.isEvent = isEvent;
        terminalObservation = request.TerminalObservation;
    }

    /// <summary>Gets the immutable copied bridge frame.</summary>
    public ReadOnlyMemory<byte> Frame => frame;

    /// <summary>
    /// Reads the request's current terminal state before application work begins.
    /// A terminal result means application dispatch must not start.
    /// </summary>
    public bool TryGetTerminalState(out PowerShellBrokerTerminalInfo terminal)
    {
        PowerShellBrokerTerminalObservation? observation = Volatile.Read(ref terminalObservation);
        if (observation is null)
        {
            terminal = default;
            return false;
        }

        return observation.TryGetTerminalState(out terminal);
    }

    /// <summary>
    /// Waits for a terminal state without calling into the payload or a
    /// dispatcher. A <see langword="false"/> return means the request is still
    /// live after timeout.
    /// </summary>
    public bool WaitForTerminal(TimeSpan timeout, out PowerShellBrokerTerminalInfo terminal)
    {
        PowerShellBrokerTerminalObservation? observation = Volatile.Read(ref terminalObservation);
        if (observation is null)
        {
            terminal = default;
            return false;
        }

        return observation.WaitForTerminal(timeout, out terminal);
    }

    /// <summary>Runs generated dispatch once and attempts its deferred DBC reply.</summary>
    /// <returns>
    /// <see langword="false"/> when a request reply became terminal before this
    /// worker finished, such as after cancellation, timeout, or channel close.
    /// Events always return <see langword="true"/> after dispatch.
    /// </returns>
    public bool Dispatch()
    {
        return DispatchDetailed().ReplyAccepted;
    }

    /// <summary>
    /// Runs generated dispatch once. A terminal state observed before dispatch
    /// suppresses application handler execution; a race that terminates after a
    /// handler starts is reported in the returned result and cannot revive the
    /// correlation.
    /// </summary>
    public PowerShellBridgeDispatchResult DispatchDetailed()
    {
        if (Interlocked.Exchange(ref dispatched, 1) != 0)
        {
            throw new InvalidOperationException("A generated bridge dispatch may only run once.");
        }

        if (Interlocked.CompareExchange(ref dispatchState, 1, 0) != 0)
        {
            return new PowerShellBridgeDispatchResult(
                handlerStarted: false,
                replyAccepted: false,
                terminalState: null);
        }

        if (!isEvent && TryGetTerminalState(out PowerShellBrokerTerminalInfo terminalBefore) && terminalBefore.IsTerminal)
        {
            return new PowerShellBridgeDispatchResult(
                handlerStarted: false,
                replyAccepted: false,
                terminalState: terminalBefore);
        }

        try
        {
            if (isEvent)
            {
                dispatcher.DispatchEvent(frame);
                return new PowerShellBridgeDispatchResult(
                    handlerStarted: true,
                    replyAccepted: true,
                    terminalState: null);
            }

            byte[] reply = new byte[checked(dispatcher.MaximumReplyBytes + PowerShellBridgeBrokerWire.ReplyEnvelopeSize)];
            Span<byte> bridgeReply = reply.AsSpan(PowerShellBridgeBrokerWire.ReplyEnvelopeSize);
            int status = dispatcher.Dispatch(
                header.LeaseId,
                header.Generation,
                header.ObjectId,
                header.MemberId,
                bridgeReply.Length,
                frame,
                bridgeReply,
                out int replyLength);
            if (replyLength < 0 || replyLength > bridgeReply.Length)
            {
                status = PowerShellBridgeStatus.InvalidArgument;
                replyLength = 0;
            }

            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(reply, status);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(sizeof(int)), 0);
            bool replyAccepted = broker.TryReply(
                request.CorrelationId,
                reply.AsSpan(0, PowerShellBridgeBrokerWire.ReplyEnvelopeSize + replyLength));
            PowerShellBrokerTerminalInfo? terminal = null;
            if (!replyAccepted && TryGetTerminalState(out PowerShellBrokerTerminalInfo terminalAfter))
            {
                terminal = terminalAfter;
            }

            return new PowerShellBridgeDispatchResult(
                handlerStarted: true,
                replyAccepted: replyAccepted,
                terminalState: terminal);
        }
        finally
        {
            Volatile.Write(ref dispatchState, 2);
            ReleaseResources();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        while (true)
        {
            int state = Volatile.Read(ref dispatchState);
            if (state == 1)
            {
                // A handler already owns the dispatcher lease. Its finally block
                // releases it after returning, but no new handler can start.
                return;
            }

            if (state == 2 ||
                Interlocked.CompareExchange(ref dispatchState, 2, 0) == 0)
            {
                ReleaseResources();
                return;
            }
        }
    }

    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref resourcesReleased, 1) == 0)
        {
            Interlocked.Exchange(ref terminalObservation, null)?.Dispose();
            dispatcherLease.Dispose();
        }
    }
}
