#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// One entry of a generated static member table. The table is emitted as a
/// literal array and looked up through a generated switch, so dispatch never
/// touches reflection, a dynamic binder, or a member name.
/// </summary>
public readonly struct PowerShellBridgeMemberEntry
{
    public PowerShellBridgeMemberEntry(
        ulong objectTypeId,
        uint ordinal,
        BridgeMemberKind kind,
        BridgeMutation mutation,
        BridgePermission permission,
        int argumentCount,
        byte resultTag,
        ulong orderingKey,
        int maximumRequestBytes,
        int maximumReplyBytes)
    {
        ObjectTypeId = objectTypeId;
        Ordinal = ordinal;
        Kind = kind;
        Mutation = mutation;
        Permission = permission;
        ArgumentCount = argumentCount;
        ResultTag = resultTag;
        OrderingKey = orderingKey;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumReplyBytes = maximumReplyBytes;
    }

    public ulong ObjectTypeId { get; }

    public uint Ordinal { get; }

    public BridgeMemberKind Kind { get; }

    public BridgeMutation Mutation { get; }

    /// <summary>Declared permission metadata. It is an input to the authorizer, never a decision.</summary>
    public BridgePermission Permission { get; }

    public int ArgumentCount { get; }

    public byte ResultTag { get; }

    public ulong OrderingKey { get; }

    public int MaximumRequestBytes { get; }

    public int MaximumReplyBytes { get; }
}

/// <summary>
/// Matches the required shape of a contract's hand-declared COM transport
/// method, so a payload pack binds a transport with a method group.
/// </summary>
public delegate int PowerShellBridgeInvoke(
    ulong leaseId,
    uint generation,
    ulong objectId,
    uint memberId,
    nint input,
    int inputLength,
    nint output,
    int outputCapacity,
    out int outputLength);

/// <summary>
/// The carrier seam a generated payload wrapper talks to. Generated code never
/// references a carrier directly, so changing the carrier cannot change
/// generated code.
/// </summary>
public interface IPowerShellBridgeTransport
{
    /// <summary>
    /// Sends one request frame and copies the reply. Returns the transport
    /// status; see the failure table in <c>docs/in-process-ffi.md</c>.
    /// </summary>
    int Invoke(
        ulong leaseId,
        uint generation,
        ulong objectId,
        uint memberId,
        ReadOnlySpan<byte> request,
        Span<byte> reply,
        out int replyLength);

    /// <summary>
    /// Delivers one one-way event frame. It must not block the calling
    /// pipeline thread.
    /// </summary>
    void PostEvent(uint kind, ulong orderingKey, ReadOnlySpan<byte> body);
}

/// <summary>
/// The optional one-way event sink a consumer may expose alongside its contract
/// transport interface. It is obtained by <c>QueryInterface</c> on the same
/// <c>IUnknown</c> the payload pack already receives.
/// </summary>
/// <remarks>
/// An implementation <b>must</b> return without blocking. Structural
/// non-blocking delivery through the duplex broker channel requires appending
/// transport bind callbacks to the contract-pack API and is tracked separately;
/// until then this rule is enforced by review, not by construction.
/// </remarks>
[GeneratedComInterface]
[Guid("9D4B2F87-1A63-4F0E-A5C4-6E0B1D5C7A32")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellBridgeEventSink
{
    [PreserveSig]
    int PostEvent(uint kind, ulong orderingKey, nint body, int bodyLength);
}

/// <summary>
/// Carries generated bridge frames over one contract's COM transport interface,
/// with an optional event sink for declared one-way members.
/// </summary>
public sealed unsafe class PowerShellBridgeComTransport : IPowerShellBridgeTransport
{
    private readonly PowerShellBridgeInvoke invoke;
    private readonly IPowerShellBridgeEventSink? events;

    /// <summary>
    /// Binds a transport to a contract's COM broker. <paramref name="events"/>
    /// is null when the consumer exposes no sink; a declared event then fails
    /// deterministically rather than degrading to a blocking delivery.
    /// </summary>
    public PowerShellBridgeComTransport(PowerShellBridgeInvoke invoke, IPowerShellBridgeEventSink? events)
    {
        this.invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        this.events = events;
    }

    /// <summary>Gets whether the consumer exposed a one-way event sink.</summary>
    public bool SupportsEvents => events is not null;

    public int Invoke(
        ulong leaseId,
        uint generation,
        ulong objectId,
        uint memberId,
        ReadOnlySpan<byte> request,
        Span<byte> reply,
        out int replyLength)
    {
        replyLength = 0;
        if (request.Length is < PowerShellBridgeWire.RequestHeaderSize or > PowerShellBridgeWire.MaximumFrameBytes ||
            reply.Length > PowerShellBridgeWire.MaximumFrameBytes)
        {
            return PowerShellBridgeStatus.InvalidArgument;
        }

        IntPtr requestBuffer = IntPtr.Zero;
        IntPtr replyBuffer = IntPtr.Zero;
        try
        {
            requestBuffer = Marshal.AllocHGlobal(request.Length);
            replyBuffer = reply.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(reply.Length);
            fixed (byte* source = request)
            {
                Buffer.MemoryCopy(source, (void*)requestBuffer, request.Length, request.Length);
            }

            int status = invoke(
                leaseId,
                generation,
                objectId,
                memberId,
                requestBuffer,
                request.Length,
                replyBuffer,
                reply.Length,
                out int written);
            if (written < 0)
            {
                return PowerShellBridgeStatus.InvalidArgument;
            }

            replyLength = written;
            if (status != PowerShellBridgeStatus.Success)
            {
                return status;
            }

            if (written > reply.Length)
            {
                return PowerShellBridgeStatus.BufferTooSmall;
            }

            if (written > 0)
            {
                fixed (byte* destination = reply)
                {
                    Buffer.MemoryCopy((void*)replyBuffer, destination, reply.Length, written);
                }
            }

            return PowerShellBridgeStatus.Success;
        }
        finally
        {
            if (requestBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(requestBuffer);
            }

            if (replyBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(replyBuffer);
            }
        }
    }

    public void PostEvent(uint kind, ulong orderingKey, ReadOnlySpan<byte> body)
    {
        IPowerShellBridgeEventSink sink = events
            ?? throw new InvalidOperationException(
                "This bridge lease has no one-way event sink. A declared event requires a consumer that exposes IPowerShellBridgeEventSink.");
        if (body.Length is < PowerShellBridgeWire.RequestHeaderSize or > PowerShellBridgeWire.MaximumFrameBytes)
        {
            throw new InvalidOperationException("The bridge event frame is out of range.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(body.Length);
        try
        {
            fixed (byte* source = body)
            {
                Buffer.MemoryCopy(source, (void*)buffer, body.Length, body.Length);
            }

            int status = sink.PostEvent(kind, orderingKey, buffer, body.Length);
            if (status != PowerShellBridgeStatus.Success)
            {
                throw new InvalidOperationException("The bridge event was rejected by the consumer sink.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>
/// The deterministic failure a generated payload wrapper raises when a bridge
/// call is rejected. It carries the transport status and never carries
/// application state.
/// </summary>
public sealed class PowerShellBridgeException : Exception
{
    public PowerShellBridgeException(int status, string message)
        : base(message)
    {
        Status = status;
    }

    /// <summary>Gets the transport status from the bridge failure table.</summary>
    public int Status { get; }

    /// <summary>Gets whether the lease or handle was revoked, or authorization was denied.</summary>
    public bool IsRevoked => Status == PowerShellBridgeStatus.AccessDenied;

    /// <summary>Builds the deterministic failure for a transport status.</summary>
    public static PowerShellBridgeException FromStatus(int status, string contract)
    {
        string detail = status switch
        {
            PowerShellBridgeStatus.AccessDenied =>
                "the bridge lease, the object handle, or the requested authorization was rejected",
            PowerShellBridgeStatus.InvalidArgument => "the bridge frame was rejected as malformed",
            PowerShellBridgeStatus.Bounds => "a declared bridge bound was exceeded",
            PowerShellBridgeStatus.OutOfMemory => "a bounded bridge runtime table is full",
            PowerShellBridgeStatus.BufferTooSmall => "the bridge reply exceeded the declared reply bound",
            PowerShellBridgeStatus.ContractMismatch => "the host and payload bridge contract descriptors differ",
            _ => "the bridge call failed",
        };
        return new PowerShellBridgeException(status, $"Bridge contract '{contract}' call failed: {detail}.");
    }
}
