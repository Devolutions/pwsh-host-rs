#nullable enable

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace NativeHost;

/// <summary>
/// Payload-owned endpoint for one generated DBC bridge attachment. It gives the
/// pack a fixed COM handshake, but never exposes a consumer contract pointer.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class FfiBridgeBrokerSink : IPowerShellBridgeBrokerSink, IFfiBridgeContractLeaseSink
{
    private const int Created = 0;
    private const int Declared = 1;
    private const int Bound = 2;
    private const int Unbound = 3;
    private const int Closing = 4;
    private const int Disposed = 5;

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly object gate = new();
    private readonly PowerShellLiveObjectContract contract;
    private readonly ulong interfaceIdLow;
    private readonly ulong interfaceIdHigh;
    private readonly ulong bindingId;
    private readonly uint maximumRequestBytes;
    private readonly uint maximumReplyBytes;
    private readonly Func<byte[], int, byte[]> request;
    private readonly Action<byte[]> post;
    private IPowerShellBridgePayloadCallback? callback;
    private ComObject? callbackComObject;
    private IntPtr activeRootHandle;
    private int state;

    internal FfiBridgeBrokerSink(
        PowerShellLiveObjectContract contract,
        ulong bindingId,
        uint maximumRequestBytes,
        uint maximumReplyBytes,
        Func<byte[], int, byte[]> request,
        Action<byte[]> post)
    {
        if (bindingId == 0 ||
            maximumRequestBytes is 0 or > PowerShellBridgeWire.MaximumFrameBytes ||
            maximumReplyBytes is 0 or > PowerShellBridgeWire.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bindingId), "The bridge binding metadata is invalid.");
        }

        this.contract = contract;
        this.bindingId = bindingId;
        this.maximumRequestBytes = maximumRequestBytes;
        this.maximumReplyBytes = maximumReplyBytes;
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        this.post = post ?? throw new ArgumentNullException(nameof(post));

        Span<byte> identity = stackalloc byte[16];
        if (!contract.InterfaceId.TryWriteBytes(identity))
        {
            throw new InvalidOperationException("The bridge contract identifier is invalid.");
        }

        interfaceIdLow = BinaryPrimitives.ReadUInt64LittleEndian(identity);
        interfaceIdHigh = BinaryPrimitives.ReadUInt64LittleEndian(identity[8..]);
    }

    internal bool IsDeclared
    {
        get
        {
            lock (gate)
            {
                return state is Declared or Bound or Unbound;
            }
        }
    }

    internal IntPtr Export()
    {
        IntPtr value = ComWrappers.GetOrCreateComInterfaceForObject(this, CreateComInterfaceFlags.None);
        if (value == IntPtr.Zero)
        {
            throw new InvalidOperationException("The bridge broker sink did not create an IUnknown pointer.");
        }

        return value;
    }

    public int GetRequestedContract(
        out ulong requestedInterfaceIdLow,
        out ulong requestedInterfaceIdHigh,
        out ushort requestedMajorVersion,
        out ushort requestedMinorVersion,
        out uint requestedMaximumRequestBytes,
        out uint requestedMaximumReplyBytes)
    {
        requestedInterfaceIdLow = interfaceIdLow;
        requestedInterfaceIdHigh = interfaceIdHigh;
        requestedMajorVersion = contract.MajorVersion;
        requestedMinorVersion = contract.MinorVersion;
        requestedMaximumRequestBytes = maximumRequestBytes;
        requestedMaximumReplyBytes = maximumReplyBytes;
        return Volatile.Read(ref state) == Disposed
            ? PowerShellBridgeStatus.AccessDenied
            : PowerShellBridgeStatus.Success;
    }

    public int Declare(
        ulong requestedInterfaceIdLow,
        ulong requestedInterfaceIdHigh,
        ushort requestedMajorVersion,
        ushort requestedMinorVersion,
        nint callbackPointer,
        uint requestedMaximumRequestBytes,
        uint requestedMaximumReplyBytes)
    {
        if (callbackPointer == IntPtr.Zero)
        {
            return PowerShellBridgeStatus.InvalidArgument;
        }

        lock (gate)
        {
            if (state != Created)
            {
                return PowerShellBridgeStatus.InvalidArgument;
            }

            if (requestedInterfaceIdLow != interfaceIdLow ||
                requestedInterfaceIdHigh != interfaceIdHigh ||
                requestedMajorVersion != contract.MajorVersion ||
                requestedMinorVersion != contract.MinorVersion ||
                requestedMaximumRequestBytes != maximumRequestBytes ||
                requestedMaximumReplyBytes != maximumReplyBytes)
            {
                return PowerShellBridgeStatus.ContractMismatch;
            }

            ComObject? imported = null;
            try
            {
                object projected = ComWrappers.GetOrCreateObjectForComInstance(
                    callbackPointer,
                    CreateObjectFlags.UniqueInstance);
                imported = projected as ComObject
                    ?? throw new InvalidOperationException(
                        "The bridge payload callback did not create a source-generated COM wrapper.");
                callback = projected as IPowerShellBridgePayloadCallback
                    ?? throw new InvalidOperationException(
                        "The bridge payload callback has an unexpected COM interface.");
                callbackComObject = imported;
                imported = null;
                state = Declared;
                return PowerShellBridgeStatus.Success;
            }
            catch (COMException exception)
            {
                return exception.HResult;
            }
            catch
            {
                return unchecked((int)0x80004005);
            }
            finally
            {
                imported?.FinalRelease();
            }
        }
    }

    public int Request(nint body, int bodyLength, nint reply, int replyCapacity, out int replyLength)
    {
        replyLength = 0;
        if (bodyLength is < PowerShellBridgeWire.RequestHeaderSize or > PowerShellBridgeWire.MaximumFrameBytes ||
            replyCapacity < PowerShellBridgeBrokerWire.ReplyEnvelopeSize ||
            body == IntPtr.Zero ||
            reply == IntPtr.Zero ||
            bodyLength > maximumRequestBytes ||
            replyCapacity > maximumReplyBytes + PowerShellBridgeBrokerWire.ReplyEnvelopeSize)
        {
            return PowerShellBridgeStatus.InvalidArgument;
        }

        bool closeDuringTeardown = IsCloseFrame(body, bodyLength);
        lock (gate)
        {
            if (state != Bound && (state != Closing || !closeDuringTeardown))
            {
                return PowerShellBridgeStatus.AccessDenied;
            }
        }

        byte[] routed = new byte[checked(PowerShellBridgeBrokerWire.RouteHeaderSize + bodyLength)];
        BinaryPrimitives.WriteUInt64LittleEndian(routed, bindingId);
        Marshal.Copy(body, routed, PowerShellBridgeBrokerWire.RouteHeaderSize, bodyLength);
        try
        {
            byte[] response = request(routed, replyCapacity);
            if (response.Length > replyCapacity)
            {
                return PowerShellBridgeStatus.BufferTooSmall;
            }

            Marshal.Copy(response, 0, reply, response.Length);
            replyLength = response.Length;
            return PowerShellBridgeStatus.Success;
        }
        catch (PowerShellBridgeException exception)
        {
            return exception.Status;
        }
        catch
        {
            return unchecked((int)0x80004005);
        }
    }

    public int Post(nint body, int bodyLength)
    {
        if (bodyLength is < PowerShellBridgeWire.RequestHeaderSize or > PowerShellBridgeWire.MaximumFrameBytes ||
            body == IntPtr.Zero ||
            bodyLength > maximumRequestBytes)
        {
            return PowerShellBridgeStatus.InvalidArgument;
        }

        lock (gate)
        {
            if (state != Bound)
            {
                return PowerShellBridgeStatus.AccessDenied;
            }
        }

        byte[] routed = new byte[checked(PowerShellBridgeBrokerWire.RouteHeaderSize + bodyLength)];
        BinaryPrimitives.WriteUInt64LittleEndian(routed, bindingId);
        Marshal.Copy(body, routed, PowerShellBridgeBrokerWire.RouteHeaderSize, bodyLength);
        try
        {
            post(routed);
            return PowerShellBridgeStatus.Success;
        }
        catch (PowerShellBridgeException exception)
        {
            return exception.Status;
        }
        catch
        {
            return unchecked((int)0x80004005);
        }
    }

    public int BeginBinding(out object root)
    {
        root = null!;
        IPowerShellBridgePayloadCallback? current;
        lock (gate)
        {
            if (state is not (Declared or Unbound))
            {
                return PowerShellBridgeStatus.AccessDenied;
            }

            state = Bound;
            current = callback;
        }

        if (current is null)
        {
            EndBinding();
            return PowerShellBridgeStatus.InvalidArgument;
        }

        int status = current.Bind(out nint rootHandle);
        if (status != PowerShellBridgeStatus.Success || rootHandle == IntPtr.Zero)
        {
            EndBinding();
            return status == PowerShellBridgeStatus.Success
                ? PowerShellBridgeStatus.InvalidArgument
                : status;
        }

        try
        {
            root = GCHandle.FromIntPtr(rootHandle).Target
                ?? throw new InvalidOperationException("The bridge payload callback returned a root handle with no target.");
        }
        catch
        {
            _ = current.ReleaseRoot(rootHandle);
            EndBinding();
            return PowerShellBridgeStatus.InvalidArgument;
        }

        lock (gate)
        {
            if (state != Bound)
            {
                _ = current.ReleaseRoot(rootHandle);
                return PowerShellBridgeStatus.AccessDenied;
            }

            activeRootHandle = rootHandle;
            return PowerShellBridgeStatus.Success;
        }
    }

    public void EndBinding()
    {
        IPowerShellBridgePayloadCallback? current;
        IntPtr rootHandle;
        lock (gate)
        {
            if (state != Bound)
            {
                return;
            }

            // The generated payload unbinds by synchronously sending its declared
            // close frame. Keep that one frame routable, but reject all other
            // traffic while the root is being torn down.
            state = Closing;
            rootHandle = Interlocked.Exchange(ref activeRootHandle, IntPtr.Zero);
            current = callback;
        }

        Exception? failure = null;
        try
        {
            if (current is not null)
            {
                int status = current.Unbind();
                if (status != PowerShellBridgeStatus.Success)
                {
                    failure = PowerShellBridgeException.FromStatus(status, contract.InterfaceId.ToString("D"));
                }
            }
        }
        finally
        {
            try
            {
                if (current is not null && rootHandle != IntPtr.Zero)
                {
                    int status = current.ReleaseRoot(rootHandle);
                    if (failure is null && status != PowerShellBridgeStatus.Success)
                    {
                        failure = PowerShellBridgeException.FromStatus(status, contract.InterfaceId.ToString("D"));
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    if (state == Closing)
                    {
                        state = Unbound;
                    }
                }
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static bool IsCloseFrame(nint body, int bodyLength)
    {
        if (body == IntPtr.Zero || bodyLength != PowerShellBridgeWire.RequestHeaderSize)
        {
            return false;
        }

        ReadOnlySpan<byte> frame = new((void*)body, bodyLength);
        return PowerShellBridgeRequestHeader.TryRead(frame, out PowerShellBridgeRequestHeader header) &&
            header.FrameKind == PowerShellBridgeFrameKind.Close &&
            header.MemberId == 0 &&
            header.ObjectId == 0 &&
            header.ArgumentCount == 0 &&
            header.BodyLength == 0;
    }

    public void Dispose()
    {
        ComObject? current;
        lock (gate)
        {
            if (state == Disposed)
            {
                return;
            }

            state = Disposed;
            callback = null;
            current = callbackComObject;
            callbackComObject = null;
        }

        current?.FinalRelease();
    }
}
