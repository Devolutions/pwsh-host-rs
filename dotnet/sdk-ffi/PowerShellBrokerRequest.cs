namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// An immutable, fully copied broker request. The native delivery handle is
/// already released when this instance is produced, so nothing here keeps a
/// native pointer, buffer lifetime, or Rust-owned memory alive.
/// </summary>
public sealed class PowerShellBrokerRequest
{
    internal PowerShellBrokerRequest(
        ulong correlationId,
        ulong orderingKey,
        uint kind,
        byte[] body,
        TimeSpan remaining,
        bool isOneWay,
        bool isMutating,
        uint droppedBefore)
    {
        CorrelationId = correlationId;
        OrderingKey = orderingKey;
        Kind = kind;
        Body = body;
        Remaining = remaining;
        IsOneWay = isOneWay;
        IsMutating = isMutating;
        DroppedBefore = droppedBefore;
    }

    /// <summary>Channel-scoped, monotonic, never reused. Reply with this from any thread.</summary>
    public ulong CorrelationId { get; }

    /// <summary>At most one mutating frame per ordering key is dispatched at a time.</summary>
    public ulong OrderingKey { get; }

    /// <summary>Application-defined frame kind. The SDK assigns it no meaning.</summary>
    public uint Kind { get; }

    /// <summary>The copied frame body.</summary>
    public byte[] Body { get; }

    /// <summary>Time remaining before the absolute deadline, measured when the frame was received.</summary>
    public TimeSpan Remaining { get; }

    /// <summary>True for an event frame, which accepts no reply.</summary>
    public bool IsOneWay { get; }

    /// <summary>True when the frame participates in per-ordering-key serialization.</summary>
    public bool IsMutating { get; }

    /// <summary>One-way frames coalesced away before this one was delivered.</summary>
    public uint DroppedBefore { get; }
}
