using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Owns one broker channel and the generated bridge dispatchers registered to
/// receive frames on it. A bridge channel is constructed by
/// <see cref="PowerShellRuntime.CreateBridgeChannel"/>.
/// </summary>
public sealed class PowerShellBridgeChannel : IDisposable
{
    private readonly object gate = new();
    private readonly PowerShellBrokerChannel broker;
    private readonly Dictionary<ulong, PowerShellBridgeBinding> bindings = [];
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
        lock (gate)
        {
            if (bindings.TryGetValue(binding.BindingId, out PowerShellBridgeBinding? registered) &&
                ReferenceEquals(registered, binding))
            {
                bindings.Remove(binding.BindingId);
            }
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
        if (!broker.TryReceive(timeout, out PowerShellBrokerRequest? request))
        {
            return false;
        }
        if (request is null)
        {
            return true;
        }

        if (!PowerShellBridgeBrokerWire.TryReadRoute(request.Body, out ulong bindingId))
        {
            Reject(request, "The generated bridge route is malformed.");
            return true;
        }

        PowerShellBridgeBinding? binding;
        lock (gate)
        {
            bindings.TryGetValue(bindingId, out binding);
        }
        if (binding is null)
        {
            Reject(request, "The generated bridge binding is not registered on this channel.");
            return true;
        }

        IPowerShellBridgeDispatcher dispatcher;
        try
        {
            dispatcher = binding.GetDispatcher();
        }
        catch (ObjectDisposedException)
        {
            Reject(request, "The generated bridge binding has been released.");
            return true;
        }

        ReadOnlySpan<byte> frame = request.Body.AsSpan(PowerShellBridgeBrokerWire.RouteHeaderSize);
        bool isEvent = request.Kind == PowerShellBridgeBrokerWire.EventKind;
        if ((isEvent && !request.IsOneWay) ||
            (!isEvent && (request.Kind != PowerShellBridgeBrokerWire.RequestKind || request.IsOneWay)) ||
            frame.Length < PowerShellBridgeWire.RequestHeaderSize ||
            frame.Length > dispatcher.MaximumRequestBytes ||
            !PowerShellBridgeRequestHeader.TryRead(frame, out PowerShellBridgeRequestHeader header) ||
            (isEvent != (header.FrameKind == PowerShellBridgeFrameKind.Event)))
        {
            Reject(request, "The generated bridge frame is invalid for its route.");
            return true;
        }

        dispatch = new PowerShellBridgeDispatch(
            broker,
            request,
            dispatcher,
            header,
            frame.ToArray(),
            isEvent);
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

    private void Reject(PowerShellBrokerRequest request, string message)
    {
        if (!request.IsOneWay)
        {
            broker.TryReplyError(request.CorrelationId, PowerShellBridgeStatus.InvalidArgument, message);
        }
    }
}

/// <summary>
/// Associates one generated bridge dispatcher with one
/// <see cref="PowerShellBridgeChannel"/>. The binding owns its dispatcher.
/// </summary>
public sealed class PowerShellBridgeBinding : IDisposable
{
    private readonly PowerShellBridgeChannel channel;
    private IPowerShellBridgeDispatcher? dispatcher;
    private int disposed;

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
        return Volatile.Read(ref dispatcher) ??
            throw new ObjectDisposedException(nameof(PowerShellBridgeBinding));
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

        IPowerShellBridgeDispatcher? released = Interlocked.Exchange(ref dispatcher, null);
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
}

/// <summary>
/// A copied, prevalidated bridge frame. Call <see cref="Dispatch"/> from the
/// host's worker queue, never inline from <see cref="PowerShellBridgeChannel.TryReceive"/>.
/// </summary>
public sealed class PowerShellBridgeDispatch
{
    private readonly PowerShellBrokerChannel broker;
    private readonly PowerShellBrokerRequest request;
    private readonly IPowerShellBridgeDispatcher dispatcher;
    private readonly PowerShellBridgeRequestHeader header;
    private readonly byte[] frame;
    private readonly bool isEvent;
    private int dispatched;

    internal PowerShellBridgeDispatch(
        PowerShellBrokerChannel broker,
        PowerShellBrokerRequest request,
        IPowerShellBridgeDispatcher dispatcher,
        PowerShellBridgeRequestHeader header,
        byte[] frame,
        bool isEvent)
    {
        this.broker = broker;
        this.request = request;
        this.dispatcher = dispatcher;
        this.header = header;
        this.frame = frame;
        this.isEvent = isEvent;
    }

    /// <summary>Runs generated dispatch once and attempts its deferred DBC reply.</summary>
    /// <returns>
    /// <see langword="false"/> when a request reply became terminal before this
    /// worker finished, such as after cancellation, timeout, or channel close.
    /// Events always return <see langword="true"/> after dispatch.
    /// </returns>
    public bool Dispatch()
    {
        if (Interlocked.Exchange(ref dispatched, 1) != 0)
        {
            throw new InvalidOperationException("A generated bridge dispatch may only run once.");
        }

        if (isEvent)
        {
            dispatcher.DispatchEvent(frame);
            return true;
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
        return broker.TryReply(request.CorrelationId, reply.AsSpan(0, PowerShellBridgeBrokerWire.ReplyEnvelopeSize + replyLength));
    }
}
