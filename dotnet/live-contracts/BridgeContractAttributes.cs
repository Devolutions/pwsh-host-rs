#nullable enable

using System;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// How a bridge member changes application state. It selects the generated
/// consumer path; it never grants permission by itself.
/// </summary>
public enum BridgeMutation
{
    /// <summary>The member reads state and stages nothing.</summary>
    None = 0,

    /// <summary>The member changes state inside the authorized call.</summary>
    Direct = 1,

    /// <summary>
    /// The member stages an intent, validates it, and commits only after the
    /// member call succeeds. Any failure inside that call aborts every intent it
    /// staged.
    /// </summary>
    Staged = 2,
}

/// <summary>
/// Declared permission metadata for one accessor. It is an input to the
/// application's authorizer and never a substitute for it: the getter, the
/// setter, and every method are authorized independently on every call.
/// </summary>
public enum BridgePermission
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 3,
}

/// <summary>Which kind of record a descriptor member entry describes.</summary>
public enum BridgeMemberKind
{
    Getter = 1,
    Setter = 2,
    Method = 3,
    Event = 4,
    ReliableEvent = 5,
}

/// <summary>
/// Declares the root of one closed Bridge Contract v2 graph.
/// </summary>
/// <remarks>
/// <paramref name="transportInterfaceId"/> must be the IID of a
/// <c>[GeneratedComInterface]</c> interface declared in the same compilation
/// with the exact <c>Invoke</c>/<c>CloseLease</c> shape. A source generator
/// cannot see another generator's output, so the COM declaration is hand
/// written; and the payload pack registry keys on the interface identifier, so
/// every contract needs its own.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class BridgeContractAttribute : Attribute
{
    public BridgeContractAttribute(string id, int majorVersion, int minorVersion, string transportInterfaceId)
    {
        Id = id;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        TransportInterfaceId = transportInterfaceId;
    }

    /// <summary>Gets the stable contract identity.</summary>
    public string Id { get; }

    /// <summary>Gets the major version. The pack descriptor stores it as a <see cref="ushort"/>.</summary>
    public int MajorVersion { get; }

    /// <summary>Gets the minor version. The pack descriptor stores it as a <see cref="ushort"/>.</summary>
    public int MinorVersion { get; }

    /// <summary>Gets the IID of the hand-declared COM transport interface.</summary>
    public string TransportInterfaceId { get; }
}

/// <summary>Declares one object type in the contract graph, including the root.</summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class BridgeObjectAttribute : Attribute
{
    public BridgeObjectAttribute(ulong id) => Id = id;

    /// <summary>Gets the object type identifier, unique within the contract.</summary>
    public ulong Id { get; }

    /// <summary>
    /// Gets or sets the explicit release ordinal. Release is an ordinal, never
    /// an implicit finalizer, and shares the contract's single ordinal space.
    /// </summary>
    public uint ReleaseId { get; set; }
}

/// <summary>
/// Marks a closed child object as a finite operation with one static status,
/// cancellation, and snapshot-page shape.
/// </summary>
/// <remarks>
/// The generator validates the referenced member ordinals, allocates the child
/// handle as an owner-bound opaque operation identity, and bounds later
/// admission by <see cref="MaximumLifetimeMilliseconds"/>. It does not infer
/// product snapshot, authorization-revision, or external-work semantics.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class BridgeFiniteOperationAttribute : Attribute
{
    /// <summary>Gets or sets the read-only status member ordinal.</summary>
    public uint StatusMemberId { get; set; }

    /// <summary>Gets or sets the terminal-status Boolean field ordinal.</summary>
    public uint StatusTerminalFieldId { get; set; }

    /// <summary>Gets or sets the direct execute-only cancellation member ordinal.</summary>
    public uint CancelMemberId { get; set; }

    /// <summary>Gets or sets the read-only snapshot-page member ordinal.</summary>
    public uint PageMemberId { get; set; }

    /// <summary>
    /// Gets or sets the maximum time in milliseconds for which new operation
    /// requests may be admitted. It must be between 1 and 3,600,000.
    /// </summary>
    public int MaximumLifetimeMilliseconds { get; set; }
}

/// <summary>Declares one property or method on a bridge object.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class BridgeMemberAttribute : Attribute
{
    public BridgeMemberAttribute(uint id) => Id = id;

    /// <summary>Gets the getter or method ordinal.</summary>
    public uint Id { get; }

    /// <summary>Gets or sets the independent setter ordinal for a mutable property.</summary>
    public uint SetterId { get; set; }

    /// <summary>Gets or sets how the getter or method changes state.</summary>
    public BridgeMutation Mutation { get; set; }

    /// <summary>Gets or sets how the setter changes state.</summary>
    public BridgeMutation SetterMutation { get; set; }

    /// <summary>Gets or sets the declared permission for the getter or method.</summary>
    public BridgePermission Permission { get; set; }

    /// <summary>Gets or sets the declared permission for the setter.</summary>
    public BridgePermission SetterPermission { get; set; }

    /// <summary>Gets or sets the declared object type identifier of a handle result.</summary>
    public ulong ResultObjectId { get; set; }

    /// <summary>Gets or sets the declared data type identifier carried by a typed error reply.</summary>
    public ulong ErrorDataId { get; set; }

    /// <summary>Gets or sets the bound for a UTF-8 string position, in bytes.</summary>
    public int MaximumUtf8Bytes { get; set; }

    /// <summary>Gets or sets the bound for an opaque byte position, in bytes.</summary>
    public int MaximumByteCount { get; set; }

    /// <summary>Gets or sets the element bound for a collection position.</summary>
    public int MaximumCollectionCount { get; set; }
}

/// <summary>
/// Declares a one-way event ordinal. Events are generated ordinals, never CLR
/// <c>event</c> accessors, so no delegate crosses the boundary.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BridgeEventAttribute : Attribute
{
    public BridgeEventAttribute(uint id) => Id = id;

    /// <summary>Gets the event ordinal. It must fit in 16 bits for the broker frame kind.</summary>
    public uint Id { get; }

    /// <summary>Gets or sets the static ordering key passed out of band to the broker.</summary>
    public ulong OrderingKey { get; set; }

    /// <summary>Gets or sets the bound for a UTF-8 string parameter position, in bytes.</summary>
    public int MaximumUtf8Bytes { get; set; }

    /// <summary>Gets or sets the bound for an opaque byte parameter position, in bytes.</summary>
    public int MaximumByteCount { get; set; }

    /// <summary>Gets or sets the element bound for a collection parameter position.</summary>
    public int MaximumCollectionCount { get; set; }
}

/// <summary>
/// Declares a retained, pull-pumped event ordinal. The generated payload emits
/// a bounded copied frame; the host assigns its sequence and retains it until
/// acknowledgment or an explicit overflow terminal state.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BridgeReliableEventAttribute : Attribute
{
    public BridgeReliableEventAttribute(uint id) => Id = id;

    /// <summary>Gets the event ordinal, unique across the closed contract.</summary>
    public uint Id { get; }

    /// <summary>Gets or sets the static ordering key passed out of band to the broker.</summary>
    public ulong OrderingKey { get; set; }

    /// <summary>
    /// Gets or sets the declared permission supplied to the generated per-member
    /// authorizer when a host worker dispatches a retained event.
    /// </summary>
    public BridgePermission Permission { get; set; }

    /// <summary>Gets or sets the maximum retained unacknowledged events for one invocation.</summary>
    public int MaximumRetainedEvents { get; set; }

    /// <summary>Gets or sets the bound for a UTF-8 string parameter position, in bytes.</summary>
    public int MaximumUtf8Bytes { get; set; }

    /// <summary>Gets or sets the bound for an opaque byte parameter position, in bytes.</summary>
    public int MaximumByteCount { get; set; }

    /// <summary>Gets or sets the element bound for a collection parameter position.</summary>
    public int MaximumCollectionCount { get; set; }
}

/// <summary>
/// Declares the bound for one parameter or return position. Member-level bounds
/// apply to a property or method result only; a bounded parameter position
/// declares its own, so no bound is ever inherited across positions.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
public sealed class BridgeBoundAttribute : Attribute
{
    /// <summary>Gets or sets the declared object type identifier of a handle position.</summary>
    public ulong ResultObjectId { get; set; }

    /// <summary>Gets or sets the bound for a UTF-8 string position, in bytes.</summary>
    public int MaximumUtf8Bytes { get; set; }

    /// <summary>Gets or sets the bound for an opaque byte position, in bytes.</summary>
    public int MaximumByteCount { get; set; }

    /// <summary>Gets or sets the element bound for a collection position.</summary>
    public int MaximumCollectionCount { get; set; }
}

/// <summary>Declares a copied data-transfer interface carried by value.</summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class BridgeDataAttribute : Attribute
{
    public BridgeDataAttribute(ulong id) => Id = id;

    /// <summary>Gets the data type identifier, unique within the contract.</summary>
    public ulong Id { get; }
}

/// <summary>
/// Marks one copied data contract as the statically validated page of a
/// <see cref="BridgeFiniteOperationAttribute"/>.
/// </summary>
/// <remarks>
/// Every named ordinal is validated against its exact fixed type. The handler
/// remains responsible for issuing and revalidating opaque cursors and product
/// revisions; this marker does not create a durable cursor store.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class BridgeSnapshotPageAttribute : Attribute
{
    /// <summary>Gets or sets the bounded static column-list field ordinal.</summary>
    public uint ColumnsFieldId { get; set; }

    /// <summary>Gets or sets the bounded static row-list field ordinal.</summary>
    public uint RowsFieldId { get; set; }

    /// <summary>Gets or sets the opaque next-cursor <see cref="Guid"/> field ordinal.</summary>
    public uint NextCursorFieldId { get; set; }

    /// <summary>Gets or sets the snapshot revision <see cref="long"/> field ordinal.</summary>
    public uint SnapshotRevisionFieldId { get; set; }

    /// <summary>Gets or sets the permission revision <see cref="long"/> field ordinal.</summary>
    public uint PermissionRevisionFieldId { get; set; }

    /// <summary>Gets or sets the cursor lease expiry <see cref="long"/> field ordinal.</summary>
    public uint CursorLeaseExpiresAtFieldId { get; set; }

    /// <summary>Gets or sets the terminal-state <see cref="bool"/> field ordinal.</summary>
    public uint IsTerminalFieldId { get; set; }

    /// <summary>Gets or sets the deterministic gap <see cref="bool"/> field ordinal.</summary>
    public uint IsGapFieldId { get; set; }

    /// <summary>Gets or sets the deterministic overflow <see cref="bool"/> field ordinal.</summary>
    public uint IsOverflowFieldId { get; set; }

    /// <summary>Gets or sets the truncation <see cref="bool"/> field ordinal.</summary>
    public uint IsTruncatedFieldId { get; set; }

    /// <summary>Gets or sets the total-count <see cref="long"/> field ordinal.</summary>
    public uint TotalCountFieldId { get; set; }
}

/// <summary>Declares one field of a bridge data contract.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BridgeFieldAttribute : Attribute
{
    public BridgeFieldAttribute(uint ordinal) => Ordinal = ordinal;

    /// <summary>Gets the field ordinal, unique within its data contract.</summary>
    public uint Ordinal { get; }

    /// <summary>Gets or sets the declared object type identifier of a handle field.</summary>
    public ulong ResultObjectId { get; set; }

    /// <summary>Gets or sets the bound for a UTF-8 string field, in bytes.</summary>
    public int MaximumUtf8Bytes { get; set; }

    /// <summary>Gets or sets the bound for an opaque byte field, in bytes.</summary>
    public int MaximumByteCount { get; set; }

    /// <summary>Gets or sets the element bound for a collection field.</summary>
    public int MaximumCollectionCount { get; set; }
}

/// <summary>
/// Declares a closed enumeration. <c>[Flags]</c> is rejected because a combined
/// value equals no declared member, so the closed allow-list cannot validate it.
/// </summary>
[AttributeUsage(AttributeTargets.Enum)]
public sealed class BridgeEnumAttribute : Attribute
{
    public BridgeEnumAttribute(ulong id) => Id = id;

    /// <summary>Gets the enumeration type identifier, unique within the contract.</summary>
    public ulong Id { get; }
}
