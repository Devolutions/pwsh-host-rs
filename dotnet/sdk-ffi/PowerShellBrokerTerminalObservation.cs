namespace Devolutions.PowerShell.Ffi;

/// <summary>Current terminal state of one copied broker correlation.</summary>
public enum PowerShellBrokerTerminalState
{
    Pending = 0,
    Dispatched = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5,
    Aborted = 6,
}

/// <summary>Immutable terminal observation copied from the native broker.</summary>
public readonly struct PowerShellBrokerTerminalInfo
{
    internal PowerShellBrokerTerminalInfo(
        PowerShellBrokerTerminalState state,
        PowerShellFfiStatus terminalStatus,
        ulong terminalEpochMilliseconds)
    {
        State = state;
        TerminalStatus = terminalStatus;
        TerminalEpochMilliseconds = terminalEpochMilliseconds;
    }

    /// <summary>Gets the correlation's current lifecycle state.</summary>
    public PowerShellBrokerTerminalState State { get; }

    /// <summary>Gets the mapped terminal status, or <see cref="PowerShellFfiStatus.OperationNotTerminal"/>.</summary>
    public PowerShellFfiStatus TerminalStatus { get; }

    /// <summary>Gets the terminal time relative to the channel epoch, or zero while live.</summary>
    public ulong TerminalEpochMilliseconds { get; }

    /// <summary>Gets whether the correlation has reached a first-wins terminal state.</summary>
    public bool IsTerminal => State >= PowerShellBrokerTerminalState.Completed;
}

/// <summary>
/// A bounded, worker-thread-safe observation lease for one non-one-way broker
/// correlation. It never owns a delivery handle and cannot invoke PowerShell.
/// </summary>
internal sealed unsafe class PowerShellBrokerTerminalObservation : IDisposable
{
    private const uint BrokerAbiVersion = 1;

    private ulong handle;
    private int disposed;

    internal PowerShellBrokerTerminalObservation(ulong handle)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }

        this.handle = handle;
    }

    /// <summary>
    /// Copies the current state. A <see langword="false"/> return means the
    /// correlation remains live; terminal details are still returned.
    /// </summary>
    public bool TryGetTerminalState(out PowerShellBrokerTerminalInfo terminal)
    {
        terminal = Read(waitMilliseconds: null);
        return terminal.IsTerminal;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a terminal state. A
    /// <see langword="false"/> return means the correlation was still live at
    /// timeout; no pipeline work is performed while waiting.
    /// </summary>
    public bool WaitForTerminal(TimeSpan timeout, out PowerShellBrokerTerminalInfo terminal)
    {
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The broker observation timeout is out of range.");
        }

        terminal = Read(checked((uint)timeout.TotalMilliseconds));
        return terminal.IsTerminal;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        ulong observation = Interlocked.Exchange(ref handle, 0);
        if (observation == 0)
        {
            return;
        }

        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.BrokerObservationRelease(observation, &result);
        PowerShellFfiStatus effective = NativeCall.EffectiveStatus(status, result);
        if (effective is not (PowerShellFfiStatus.Success or PowerShellFfiStatus.InvalidHandle))
        {
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }
    }

    private PowerShellBrokerTerminalInfo Read(uint? waitMilliseconds)
    {
        ulong observation = Volatile.Read(ref handle);
        if (Volatile.Read(ref disposed) != 0 || observation == 0)
        {
            throw new ObjectDisposedException(nameof(PowerShellBrokerTerminalObservation));
        }

        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        NativeBrokerTerminalInfo info = new()
        {
            Size = checked((uint)sizeof(NativeBrokerTerminalInfo)),
            AbiVersion = BrokerAbiVersion,
        };
        int status = waitMilliseconds is uint timeout
            ? NativeMethods.BrokerObservationWait(observation, timeout, &info, &result)
            : NativeMethods.BrokerObservationGetInfo(observation, &info, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);

        if (info.State > (uint)PowerShellBrokerTerminalState.Aborted ||
            (info.State < (uint)PowerShellBrokerTerminalState.Completed &&
             (info.TerminalStatus != (int)PowerShellFfiStatus.OperationNotTerminal ||
              info.TerminalEpochMilliseconds != 0)))
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "The native broker terminal observation returned invalid state metadata.");
        }

        return new PowerShellBrokerTerminalInfo(
            (PowerShellBrokerTerminalState)info.State,
            (PowerShellFfiStatus)info.TerminalStatus,
            info.TerminalEpochMilliseconds);
    }
}
