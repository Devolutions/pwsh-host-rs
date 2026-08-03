using System.Text;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// An opt-in, strictly dispatch-only duplex broker channel. It lets a running
/// PowerShell pipeline request application work without executing application
/// code on the pipeline thread.
/// </summary>
/// <remarks>
/// <para>
/// A builder with a channel attached must be invoked asynchronously; the
/// synchronous paths reject it. That is a liveness precondition, not a
/// preference: a synchronous invocation from a thread whose dispatcher also
/// services the pump would deadlock without any FFI call occurring, so no guard
/// could catch it.
/// </para>
/// <para>
/// <see cref="TryReceive"/> performs wait, inspect, copy, and release inside one
/// call on one thread, so the native delivery handle never escapes to consumer
/// code and there is no finalizer that could release it on the wrong thread.
/// Releasing a delivery handle is not abandonment: the request stays outstanding
/// until <see cref="TryReply"/>, <see cref="TryReplyError"/>,
/// <see cref="TryCancel"/>, its deadline, or channel close.
/// </para>
/// <para>
/// The channel carries bounded opaque byte frames and fixed-width metadata only.
/// It is not an object bridge: no dynamic member access, no self-describing wire
/// format, no <c>PSObject</c> or SMA type, no CLR object identity, no delegates,
/// and no credential material.
/// </para>
/// </remarks>
public sealed unsafe class PowerShellBrokerChannel : IDisposable
{
    private const uint BrokerAbiVersion = 1;
    private const uint FrameFlagOneWay = 1;
    private const uint FrameFlagMutating = 1 << 1;
    private const int MaximumErrorMessageBytes = 512;

    private readonly int maximumBodyBytes;
    private ulong channelHandle;
    private int disposed;

    private PowerShellBrokerChannel(ulong channelHandle, int maximumBodyBytes)
    {
        this.channelHandle = channelHandle;
        this.maximumBodyBytes = maximumBodyBytes;
    }

    internal ulong Handle => Volatile.Read(ref channelHandle);

    internal static PowerShellBrokerChannel Create(PowerShellBrokerChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        NativeBrokerChannelOptions native = new()
        {
            Size = checked((uint)sizeof(NativeBrokerChannelOptions)),
            AbiVersion = BrokerAbiVersion,
            MaximumInflightFrames = checked((uint)options.MaximumInflightFrames),
            MaximumBodyBytes = checked((uint)options.MaximumBodyBytes),
            DefaultDeadlineMilliseconds = checked((uint)options.DefaultDeadline.TotalMilliseconds),
            Flags = 0,
        };

        ulong handle = 0;
        int status = NativeMethods.BrokerOpen(&native, &handle, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        return new PowerShellBrokerChannel(handle, options.MaximumBodyBytes);
    }

    /// <summary>
    /// Waits for one request, copies it, releases the native delivery handle,
    /// and returns. The caller must hand the returned request to its own
    /// dispatcher and return to this method; it must not perform application
    /// work inline.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> once the channel is closed. <see langword="true"/>
    /// with a <see langword="null"/> <paramref name="request"/> means the wait
    /// timed out and the pump should continue.
    /// </returns>
    public bool TryReceive(TimeSpan timeout, out PowerShellBrokerRequest? request)
    {
        return TryReceiveCore(timeout, createTerminalObservation: false, out request);
    }

    internal bool TryReceiveWithTerminalObservation(TimeSpan timeout, out PowerShellBrokerRequest? request)
    {
        return TryReceiveCore(timeout, createTerminalObservation: true, out request);
    }

    private bool TryReceiveCore(
        TimeSpan timeout,
        bool createTerminalObservation,
        out PowerShellBrokerRequest? request)
    {
        request = null;
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The broker wait timeout is out of range.");
        }

        ulong channel = Handle;
        if (channel == 0)
        {
            return false;
        }

        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong frame = 0;
        int status = NativeMethods.BrokerWait(channel, checked((uint)timeout.TotalMilliseconds), &frame, &result);
        PowerShellFfiStatus effective = NativeCall.EffectiveStatus(status, result);
        if (effective == PowerShellFfiStatus.BrokerClosed || effective == PowerShellFfiStatus.InvalidHandle)
        {
            return false;
        }

        NativeCall.ThrowIfFailed(status, result, diagnostic);
        if (frame == 0)
        {
            return true;
        }

        try
        {
            request = ReadFrame(frame, diagnostic, createTerminalObservation);
        }
        catch (PowerShellFfiException exception)
            when (exception.Status is PowerShellFfiStatus.BrokerClosed or PowerShellFfiStatus.InvalidHandle)
        {
            return false;
        }
        finally
        {
            // Release on the same thread that received the frame. This is not
            // abandonment; the correlation stays outstanding.
            NativeCallResult releaseResult = NativeCall.CreateResult(diagnostic);
            NativeMethods.BrokerFrameRelease(frame, &releaseResult);
        }

        return true;
    }

    /// <summary>Completes an outstanding request. Safe from any thread.</summary>
    /// <returns>
    /// <see langword="false"/> when the frame already reached a terminal state,
    /// which means it was cancelled, timed out, or the channel closed and the
    /// handler should stop doing the work.
    /// </returns>
    public bool TryReply(ulong correlationId, ReadOnlySpan<byte> body)
    {
        if (body.Length > maximumBodyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(body),
                body.Length,
                "The broker reply exceeds the channel maximum body size.");
        }

        ulong channel = Handle;
        if (channel == 0)
        {
            return false;
        }

        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        fixed (byte* bodyPointer = body)
        {
            int status = NativeMethods.BrokerReply(
                channel,
                correlationId,
                body.Length == 0 ? null : bodyPointer,
                checked((uint)body.Length),
                &result);
            return CompletedOrTerminal(status, result, diagnostic);
        }
    }

    /// <summary>Fails an outstanding request with a bounded message. Safe from any thread.</summary>
    public bool TryReplyError(ulong correlationId, int code, string? message)
    {
        ulong channel = Handle;
        if (channel == 0)
        {
            return false;
        }

        byte[] encoded = message is null || message.Length == 0
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(message);
        if (encoded.Length > MaximumErrorMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                encoded.Length,
                $"The broker error message exceeds {MaximumErrorMessageBytes} UTF-8 bytes.");
        }

        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        fixed (byte* messagePointer = encoded)
        {
            NativeUtf8Span span = new()
            {
                Data = encoded.Length == 0 ? null : messagePointer,
                Length = (nuint)encoded.Length,
            };
            int status = NativeMethods.BrokerReplyError(channel, correlationId, code, span, &result);
            return CompletedOrTerminal(status, result, diagnostic);
        }
    }

    /// <summary>Cancels an outstanding request. Safe from any thread.</summary>
    public bool TryCancel(ulong correlationId)
    {
        ulong channel = Handle;
        if (channel == 0)
        {
            return false;
        }

        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.BrokerCancel(channel, correlationId, &result);
        return CompletedOrTerminal(status, result, diagnostic);
    }

    /// <summary>
    /// Closes the channel. Every waiter is woken and every outstanding request
    /// fails deterministically.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        ulong channel = Interlocked.Exchange(ref channelHandle, 0);
        if (channel == 0)
        {
            return;
        }

        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        NativeMethods.BrokerClose(channel, &result);
    }

    private PowerShellBrokerRequest ReadFrame(
        ulong frame,
        byte* diagnostic,
        bool createTerminalObservation)
    {
        NativeCallResult infoResult = NativeCall.CreateResult(diagnostic);
        NativeBrokerFrameInfo info = new()
        {
            Size = checked((uint)sizeof(NativeBrokerFrameInfo)),
            AbiVersion = BrokerAbiVersion,
        };
        int status = NativeMethods.BrokerFrameGetInfo(frame, &info, &infoResult);
        NativeCall.ThrowIfFailed(status, infoResult, diagnostic);

        byte[] body = info.BodyLength == 0 ? Array.Empty<byte>() : new byte[info.BodyLength];
        if (info.BodyLength != 0)
        {
            NativeCallResult readResult = NativeCall.CreateResult(diagnostic);
            uint required = 0;
            fixed (byte* bodyPointer = body)
            {
                status = NativeMethods.BrokerFrameRead(
                    frame,
                    bodyPointer,
                    checked((uint)body.Length),
                    &required,
                    &readResult);
            }

            NativeCall.ThrowIfFailed(status, readResult, diagnostic);
        }

        PowerShellBrokerTerminalObservation? terminalObservation = null;
        try
        {
            if (createTerminalObservation && (info.Flags & FrameFlagOneWay) == 0)
            {
                terminalObservation = CreateTerminalObservation(info.CorrelationId, diagnostic);
            }

            return new PowerShellBrokerRequest(
                info.CorrelationId,
                info.OrderingKey,
                info.Kind,
                body,
                TimeSpan.FromMilliseconds(info.RemainingMilliseconds),
                (info.Flags & FrameFlagOneWay) != 0,
                (info.Flags & FrameFlagMutating) != 0,
                info.DroppedBefore,
                terminalObservation);
        }
        catch
        {
            terminalObservation?.Dispose();
            throw;
        }
    }

    private PowerShellBrokerTerminalObservation CreateTerminalObservation(ulong correlationId, byte* diagnostic)
    {
        ulong channel = Handle;
        if (channel == 0)
        {
            throw new ObjectDisposedException(nameof(PowerShellBrokerChannel));
        }

        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        ulong observation = 0;
        int status = NativeMethods.BrokerObserve(channel, correlationId, &observation, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        if (observation == 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "The native broker created a null terminal observation.");
        }

        return new PowerShellBrokerTerminalObservation(observation);
    }

    private static bool CompletedOrTerminal(int status, in NativeCallResult result, byte* diagnostic)
    {
        PowerShellFfiStatus effective = NativeCall.EffectiveStatus(status, result);
        switch (effective)
        {
            case PowerShellFfiStatus.Success:
                return true;
            case PowerShellFfiStatus.BrokerInvalidTerminalState:
            case PowerShellFfiStatus.BrokerClosed:
            case PowerShellFfiStatus.InvalidHandle:
                return false;
            default:
                NativeCall.ThrowIfFailed(status, result, diagnostic);
                return true;
        }
    }
}
