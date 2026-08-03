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
