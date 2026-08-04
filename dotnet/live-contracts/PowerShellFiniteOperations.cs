#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace Devolutions.PowerShell.Ffi.LiveObjects.FiniteOperations;

/// <summary>
/// The finite lifecycle states and request failures returned by
/// <see cref="PowerShellFiniteOperationRegistry{TPage}"/>.
/// </summary>
public enum PowerShellFiniteOperationStatus
{
    InvalidArgument = 1,
    AccessDenied = 2,
    CapacityExceeded = 3,
    Active = 4,
    Succeeded = 5,
    Failed = 6,
    Cancelled = 7,
    TimedOut = 8,
    Expired = 9,
    SnapshotInvalidated = 10,
    PermissionChanged = 11,
    InvalidCursor = 12,
    BoundsExceeded = 13,
    Released = 14,
}

/// <summary>
/// A generated operation identifier. It is random, never re-issued by a
/// registry, and only usable with the owner capability that created it.
/// </summary>
public readonly struct PowerShellFiniteOperationId : IEquatable<PowerShellFiniteOperationId>
{
    private readonly Guid value;

    private PowerShellFiniteOperationId(Guid value) => this.value = value;

    /// <summary>Gets the opaque value carried over a fixed generated contract.</summary>
    public Guid Value => value;

    /// <summary>Returns whether this identifier carries a non-empty random value.</summary>
    public bool IsValid => value != Guid.Empty;

    /// <summary>
    /// Converts a value received through a fixed contract. Possession of this
    /// value alone never grants access: every registry operation also requires
    /// its owner capability.
    /// </summary>
    public static PowerShellFiniteOperationId FromValue(Guid value) => new(value);

    public bool Equals(PowerShellFiniteOperationId other) => value.Equals(other.value);

    public override bool Equals(object? obj) => obj is PowerShellFiniteOperationId other && Equals(other);

    public override int GetHashCode() => value.GetHashCode();

    public static bool operator ==(PowerShellFiniteOperationId left, PowerShellFiniteOperationId right) => left.Equals(right);

    public static bool operator !=(PowerShellFiniteOperationId left, PowerShellFiniteOperationId right) => !left.Equals(right);
}

/// <summary>
/// A non-forgeable host-side capability that binds operations to one owner.
/// It is intentionally not serializable or transferable through a bridge
/// contract. Disposing it performs explicit cleanup of all of its operations.
/// </summary>
public sealed class PowerShellFiniteOperationOwner : IDisposable
{
    private IFiniteOperationOwnerRegistry? registry;

    internal PowerShellFiniteOperationOwner(IFiniteOperationOwnerRegistry registry) =>
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

    internal bool IsOwnedBy(IFiniteOperationOwnerRegistry expected) =>
        ReferenceEquals(Volatile.Read(ref registry), expected);

    /// <summary>Releases every operation owned by this capability.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref registry, null)?.ReleaseOwner(this);
        GC.SuppressFinalize(this);
    }
}

internal interface IFiniteOperationOwnerRegistry
{
    void ReleaseOwner(PowerShellFiniteOperationOwner owner);
}

/// <summary>
/// Immutable snapshot, permission, and fixed-schema binding for one operation.
/// The registry rejects an empty schema identity or zero snapshot/revision
/// values so a caller cannot accidentally opt out of revalidation.
/// </summary>
public readonly struct PowerShellFiniteOperationBinding
{
    public PowerShellFiniteOperationBinding(Guid schemaId, ulong snapshotRevision, ulong permissionRevision)
    {
        SchemaId = schemaId;
        SnapshotRevision = snapshotRevision;
        PermissionRevision = permissionRevision;
    }

    public Guid SchemaId { get; }

    public ulong SnapshotRevision { get; }

    public ulong PermissionRevision { get; }

    internal bool IsValid =>
        SchemaId != Guid.Empty &&
        SnapshotRevision != 0 &&
        PermissionRevision != 0;
}

/// <summary>
/// The bounded active lifetime and terminal retention window of one operation.
/// The registry applies both values only after validating them against its fixed
/// limits; no operation can request an unlimited lifetime or retention lease.
/// </summary>
public readonly struct PowerShellFiniteOperationOptions
{
    public PowerShellFiniteOperationOptions(TimeSpan deadline, TimeSpan terminalRetention)
    {
        Deadline = deadline;
        TerminalRetention = terminalRetention;
    }

    public TimeSpan Deadline { get; }

    public TimeSpan TerminalRetention { get; }
}

/// <summary>
/// The fixed-schema copy function used by a finite page registry. Implementations
/// must create a detached value and report its exact item and encoded byte
/// counts. No serializer, reflection, or run-time schema discovery is involved.
/// </summary>
public interface IPowerShellFinitePageCodec<TPage>
{
    bool TryCopy(TPage source, out TPage copy, out int itemCount, out int byteCount);
}

/// <summary>Result of revalidating the operation's snapshot and permission binding.</summary>
public enum PowerShellFinitePageValidation
{
    Allowed = 0,
    SnapshotInvalidated = 1,
    PermissionChanged = 2,
}

/// <summary>
/// Revalidates the immutable snapshot and permission-revision binding for every
/// page read. It must return a value rather than throw so an invalidation becomes
/// one deterministic terminal outcome.
/// </summary>
public interface IPowerShellFinitePageAccessValidator
{
    PowerShellFinitePageValidation Validate(in PowerShellFiniteOperationBinding binding);
}

/// <summary>
/// Static limits and the exact codec/validator for one finite page shape. The
/// shape identity is supplied by the application from its generated contract and
/// must match each operation binding exactly.
/// </summary>
public sealed class PowerShellFinitePageContract<TPage>
{
    /// <summary>Hard page count ceiling for one completed operation.</summary>
    public const int HardMaximumPages = 256;

    /// <summary>Hard item cardinality ceiling for one copied page.</summary>
    public const int HardMaximumItemsPerPage = PowerShellBridgeWire.MaximumCollectionCount;

    /// <summary>Hard encoded page size ceiling.</summary>
    public const int HardMaximumPageBytes = PowerShellBridgeWire.MaximumFrameBytes;

    public PowerShellFinitePageContract(
        Guid schemaId,
        int maximumPages,
        int maximumItemsPerPage,
        int maximumPageBytes,
        IPowerShellFinitePageCodec<TPage> codec,
        IPowerShellFinitePageAccessValidator accessValidator)
    {
        if (schemaId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaId));
        }

        if (maximumPages is < 1 or > HardMaximumPages)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPages));
        }

        if (maximumItemsPerPage is < 1 or > HardMaximumItemsPerPage)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItemsPerPage));
        }

        if (maximumPageBytes is < 1 or > HardMaximumPageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPageBytes));
        }

        SchemaId = schemaId;
        MaximumPageCount = maximumPages;
        MaximumItemsPerPage = maximumItemsPerPage;
        MaximumPageBytes = maximumPageBytes;
        Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        AccessValidator = accessValidator ?? throw new ArgumentNullException(nameof(accessValidator));
    }

    public Guid SchemaId { get; }

    public int MaximumPageCount { get; }

    public int MaximumItemsPerPage { get; }

    public int MaximumPageBytes { get; }

    public IPowerShellFinitePageCodec<TPage> Codec { get; }

    public IPowerShellFinitePageAccessValidator AccessValidator { get; }
}

/// <summary>A validated zero-based cursor into one finite copied page sequence.</summary>
public readonly struct PowerShellFinitePageCursor : IEquatable<PowerShellFinitePageCursor>
{
    private readonly int index;

    internal PowerShellFinitePageCursor(int index) => this.index = index;

    /// <summary>Gets the first page cursor.</summary>
    public static PowerShellFinitePageCursor Start => new(0);

    /// <summary>Gets the zero-based cursor position.</summary>
    public int Index => index;

    /// <summary>Creates a valid cursor from a fixed-schema integer position.</summary>
    public static bool TryCreate(int index, out PowerShellFinitePageCursor cursor)
    {
        cursor = default;
        if (index < 0)
        {
            return false;
        }

        cursor = new PowerShellFinitePageCursor(index);
        return true;
    }

    public bool Equals(PowerShellFinitePageCursor other) => index == other.index;

    public override bool Equals(object? obj) => obj is PowerShellFinitePageCursor other && Equals(other);

    public override int GetHashCode() => index;
}

/// <summary>One host-side operation lease returned when an operation is created.</summary>
public readonly struct PowerShellFiniteOperationLease
{
    internal PowerShellFiniteOperationLease(
        PowerShellFiniteOperationId operationId,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        OperationId = operationId;
        CancellationToken = cancellationToken;
        Deadline = deadline;
    }

    public PowerShellFiniteOperationId OperationId { get; }

    /// <summary>
    /// Gets the cooperative cancellation token. The registry cancels it when a
    /// cancellation, timeout, terminal transition, explicit release, owner
    /// disposal, or registry disposal wins the lifecycle transition.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    public DateTimeOffset Deadline { get; }
}

/// <summary>Terminal or active state returned by finite operation operations.</summary>
public readonly struct PowerShellFiniteOperationResult
{
    internal PowerShellFiniteOperationResult(
        PowerShellFiniteOperationStatus status,
        PowerShellFiniteOperationId operationId,
        DateTimeOffset? expiresAt,
        int errorCode)
    {
        Status = status;
        OperationId = operationId;
        ExpiresAt = expiresAt;
        ErrorCode = errorCode;
    }

    public PowerShellFiniteOperationStatus Status { get; }

    public PowerShellFiniteOperationId OperationId { get; }

    /// <summary>Gets the expiration time for a retained terminal state, when retained.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Gets the host-supplied non-zero error code for a failed operation.</summary>
    public int ErrorCode { get; }

    public bool IsTerminal => PowerShellFiniteOperationRegistry<object>.IsTerminal(Status);
}

/// <summary>The result of reading one copied finite page.</summary>
public readonly struct PowerShellFinitePageReadResult<TPage>
{
    internal PowerShellFinitePageReadResult(
        PowerShellFiniteOperationResult operation,
        TPage? page,
        bool hasPage,
        PowerShellFinitePageCursor? nextCursor)
    {
        Operation = operation;
        Page = page;
        HasPage = hasPage;
        NextCursor = nextCursor;
    }

    public PowerShellFiniteOperationResult Operation { get; }

    public TPage? Page { get; }

    public bool HasPage { get; }

    public PowerShellFinitePageCursor? NextCursor { get; }
}

/// <summary>
/// A bounded, owner-capability-backed store for finite typed operations and
/// copied fixed-schema pages. It owns no worker pool and does not dispatch work:
/// the host starts, completes, fails, or cancels its own fixed operation.
/// </summary>
public sealed class PowerShellFiniteOperationRegistry<TPage> : IDisposable, IFiniteOperationOwnerRegistry
{
    /// <summary>Maximum operation entries, including retained terminal entries.</summary>
    public const int MaximumOperations = 64;

    /// <summary>Largest accepted active deadline.</summary>
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromHours(1);

    /// <summary>Largest accepted terminal retention lease.</summary>
    public static readonly TimeSpan MaximumTerminalRetention = TimeSpan.FromMinutes(15);

    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = new();
    private readonly PowerShellFinitePageContract<TPage> contract;
    private readonly TimeProvider timeProvider;
    private readonly int maximumOperations;
    private int disposed;

    public PowerShellFiniteOperationRegistry(
        PowerShellFinitePageContract<TPage> contract,
        int maximumOperations = MaximumOperations,
        TimeProvider? timeProvider = null)
    {
        if (maximumOperations is < 1 or > MaximumOperations)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOperations));
        }

        this.contract = contract ?? throw new ArgumentNullException(nameof(contract));
        this.maximumOperations = maximumOperations;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the number of active or retained entries consuming the bounded table.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                return entries.Count;
            }
        }
    }

    /// <summary>Creates a host-only capability used to own a finite operation set.</summary>
    public PowerShellFiniteOperationOwner CreateOwner()
    {
        ThrowIfDisposed();
        return new PowerShellFiniteOperationOwner(this);
    }

    /// <summary>
    /// Starts an empty finite operation. The caller performs its own fixed work
    /// and later completes it with copied pages or fails it with a numeric code.
    /// </summary>
    public PowerShellFiniteOperationResult TryStart(
        PowerShellFiniteOperationOwner owner,
        in PowerShellFiniteOperationBinding binding,
        in PowerShellFiniteOperationOptions options,
        out PowerShellFiniteOperationLease lease)
    {
        lease = default;
        if (!Owns(owner))
        {
            return Result(PowerShellFiniteOperationStatus.AccessDenied);
        }

        if (!binding.IsValid ||
            binding.SchemaId != contract.SchemaId ||
            options.Deadline <= TimeSpan.Zero ||
            options.Deadline > MaximumDeadline ||
            options.TerminalRetention <= TimeSpan.Zero ||
            options.TerminalRetention > MaximumTerminalRetention)
        {
            return Result(PowerShellFiniteOperationStatus.InvalidArgument);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (!Owns(owner))
            {
                return Result(PowerShellFiniteOperationStatus.AccessDenied);
            }

            if (entries.Count >= maximumOperations)
            {
                return Result(PowerShellFiniteOperationStatus.CapacityExceeded);
            }

            Guid value = NewIdentifierLocked();
            DateTimeOffset now = timeProvider.GetUtcNow();
            var operationId = PowerShellFiniteOperationId.FromValue(value);
            var entry = new Entry(owner, binding, now + options.Deadline, options.TerminalRetention);
            entries.Add(value, entry);
            entry.ScheduleDeadline(timeProvider, () => OnDeadline(value, entry));
            lease = new PowerShellFiniteOperationLease(operationId, entry.Cancellation.Token, entry.Deadline);
            return Result(entry, operationId);
        }
    }

    /// <summary>
    /// Completes an active operation with copied pages. Page count, item count,
    /// and byte count are validated before the terminal success transition.
    /// </summary>
    public PowerShellFiniteOperationResult TryComplete(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        IReadOnlyList<TPage>? pages)
    {
        if (!TryResolveActive(owner, operationId, out Entry entry, out PowerShellFiniteOperationResult result))
        {
            return result;
        }

        if (pages is null || pages.Count > contract.MaximumPageCount)
        {
            return Transition(owner, operationId, entry, PowerShellFiniteOperationStatus.BoundsExceeded, 0);
        }

        var copies = new List<TPage>(pages.Count);
        for (int index = 0; index < pages.Count; index++)
        {
            TPage source = pages[index];
            if (source is null ||
                !contract.Codec.TryCopy(source, out TPage copy, out int itemCount, out int byteCount) ||
                copy is null ||
                itemCount is < 0 or > int.MaxValue ||
                byteCount is < 0 or > int.MaxValue ||
                itemCount > contract.MaximumItemsPerPage ||
                byteCount > contract.MaximumPageBytes)
            {
                return Transition(owner, operationId, entry, PowerShellFiniteOperationStatus.BoundsExceeded, 0);
            }

            copies.Add(copy);
        }

        lock (gate)
        {
            if (!TryLookupLocked(owner, operationId, out Entry current))
            {
                return Result(PowerShellFiniteOperationStatus.AccessDenied);
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            PowerShellFiniteOperationStatus status = ResolveLocked(current, now);
            if (status != PowerShellFiniteOperationStatus.Active)
            {
                return Result(current, operationId);
            }

            current.Pages = copies;
            TransitionLocked(current, PowerShellFiniteOperationStatus.Succeeded, now, 0);
            return Result(current, operationId);
        }
    }

    /// <summary>
    /// Records one deterministic application failure. A non-zero code is copied
    /// into the retained terminal result; text, exceptions, and arbitrary objects
    /// are intentionally outside this primitive.
    /// </summary>
    public PowerShellFiniteOperationResult TryFail(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        int errorCode)
    {
        if (errorCode == 0)
        {
            return Result(PowerShellFiniteOperationStatus.InvalidArgument);
        }

        if (!TryResolveActive(owner, operationId, out Entry entry, out PowerShellFiniteOperationResult result))
        {
            return result;
        }

        return Transition(owner, operationId, entry, PowerShellFiniteOperationStatus.Failed, errorCode);
    }

    /// <summary>
    /// Cancels an operation idempotently. At one locked transition the precedence
    /// is deadline first, then recorded cancellation, then an already committed
    /// terminal outcome. Thus a later completion can never replace a cancellation.
    /// </summary>
    public PowerShellFiniteOperationResult TryCancel(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        if (!Owns(owner))
        {
            return Result(PowerShellFiniteOperationStatus.AccessDenied);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (!TryLookupLocked(owner, operationId, out Entry entry))
            {
                return Result(PowerShellFiniteOperationStatus.AccessDenied);
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            if (ResolveLocked(entry, now) == PowerShellFiniteOperationStatus.Active)
            {
                TransitionLocked(entry, PowerShellFiniteOperationStatus.Cancelled, now, 0);
            }

            return Result(entry, operationId);
        }
    }

    /// <summary>Reads the retained operation state without reading a page.</summary>
    public PowerShellFiniteOperationResult TryGet(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        if (!Owns(owner))
        {
            return Result(PowerShellFiniteOperationStatus.AccessDenied);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (!TryLookupLocked(owner, operationId, out Entry entry))
            {
                return Result(PowerShellFiniteOperationStatus.AccessDenied);
            }

            _ = ResolveLocked(entry, timeProvider.GetUtcNow());
            return Result(entry, operationId);
        }
    }

    /// <summary>
    /// Reads one fixed-schema copied page. Every successful page read invokes the
    /// access validator with the original snapshot and permission revisions before
    /// a detached page leaves the registry.
    /// </summary>
    public PowerShellFinitePageReadResult<TPage> TryReadPage(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        PowerShellFinitePageCursor cursor)
    {
        if (!Owns(owner))
        {
            return ReadResult(PowerShellFiniteOperationStatus.AccessDenied);
        }

        Entry entry;
        TPage source;
        PowerShellFiniteOperationBinding binding;
        int nextIndex;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!TryLookupLocked(owner, operationId, out entry))
            {
                return ReadResult(PowerShellFiniteOperationStatus.AccessDenied);
            }

            PowerShellFiniteOperationStatus status = ResolveLocked(entry, timeProvider.GetUtcNow());
            if (status != PowerShellFiniteOperationStatus.Succeeded)
            {
                return ReadResult(Result(entry, operationId));
            }

            if (entry.Pages is null || cursor.Index >= entry.Pages.Count)
            {
                return ReadResult(PowerShellFiniteOperationStatus.InvalidCursor, operationId);
            }

            source = entry.Pages[cursor.Index];
            binding = entry.Binding;
            nextIndex = cursor.Index + 1;
        }

        PowerShellFinitePageValidation validation = contract.AccessValidator.Validate(binding);
        if (validation != PowerShellFinitePageValidation.Allowed)
        {
            return InvalidatePageAccess(owner, operationId, validation);
        }

        if (source is null ||
            !contract.Codec.TryCopy(source, out TPage copy, out int itemCount, out int byteCount) ||
            copy is null ||
            itemCount is < 0 or > int.MaxValue ||
            byteCount is < 0 or > int.MaxValue ||
            itemCount > contract.MaximumItemsPerPage ||
            byteCount > contract.MaximumPageBytes)
        {
            return FailPageBounds(owner, operationId);
        }

        lock (gate)
        {
            if (!TryLookupLocked(owner, operationId, out Entry current))
            {
                return ReadResult(PowerShellFiniteOperationStatus.AccessDenied);
            }

            PowerShellFiniteOperationStatus status = ResolveLocked(current, timeProvider.GetUtcNow());
            if (status != PowerShellFiniteOperationStatus.Succeeded)
            {
                return ReadResult(Result(current, operationId));
            }

            PowerShellFinitePageCursor? next = current.Pages is not null && nextIndex < current.Pages.Count
                ? new PowerShellFinitePageCursor(nextIndex)
                : null;
            return new PowerShellFinitePageReadResult<TPage>(Result(current, operationId), copy, hasPage: true, next);
        }
    }

    /// <summary>
    /// Explicitly ends an operation lease and releases its copied pages. A worker
    /// holding its cancellation token is notified before the entry is removed.
    /// </summary>
    public PowerShellFiniteOperationResult TryRelease(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        if (!Owns(owner))
        {
            return Result(PowerShellFiniteOperationStatus.AccessDenied);
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (!TryLookupLocked(owner, operationId, out Entry entry))
            {
                return Result(PowerShellFiniteOperationStatus.AccessDenied);
            }

            entries.Remove(operationId.Value);
            DisposeEntry(entry);
            return new PowerShellFiniteOperationResult(
                PowerShellFiniteOperationStatus.Released,
                operationId,
                expiresAt: null,
                errorCode: 0);
        }
    }

    /// <summary>
    /// Resolves elapsed terminal retentions into explicit <see cref="PowerShellFiniteOperationStatus.Expired"/>
    /// tombstones. Tombstones retain a bounded table slot until
    /// <see cref="TryRelease"/> or owner disposal performs explicit cleanup.
    /// </summary>
    public int ExpireRetainedOperations()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            DateTimeOffset now = timeProvider.GetUtcNow();
            int expired = 0;
            foreach (Entry entry in entries.Values)
            {
                if (entry.Status != PowerShellFiniteOperationStatus.Expired &&
                    ResolveLocked(entry, now) == PowerShellFiniteOperationStatus.Expired)
                {
                    expired++;
                }
            }

            return expired;
        }
    }

    /// <summary>Returns whether a status represents a lifecycle terminal outcome.</summary>
    public static bool IsTerminal(PowerShellFiniteOperationStatus status) =>
        status is
            PowerShellFiniteOperationStatus.Succeeded or
            PowerShellFiniteOperationStatus.Failed or
            PowerShellFiniteOperationStatus.Cancelled or
            PowerShellFiniteOperationStatus.TimedOut or
            PowerShellFiniteOperationStatus.Expired or
            PowerShellFiniteOperationStatus.SnapshotInvalidated or
            PowerShellFiniteOperationStatus.PermissionChanged or
            PowerShellFiniteOperationStatus.BoundsExceeded or
            PowerShellFiniteOperationStatus.Released;

    void IFiniteOperationOwnerRegistry.ReleaseOwner(PowerShellFiniteOperationOwner owner) => ReleaseOwner(owner);

    /// <summary>Closes every owner-bound operation and releases copied pages.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (gate)
        {
            foreach (Entry entry in entries.Values)
            {
                DisposeEntry(entry);
            }

            entries.Clear();
        }
    }

    private PowerShellFiniteOperationResult Transition(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        Entry expected,
        PowerShellFiniteOperationStatus requested,
        int errorCode)
    {
        lock (gate)
        {
            if (!TryLookupLocked(owner, operationId, out Entry entry) || !ReferenceEquals(entry, expected))
            {
                return Result(PowerShellFiniteOperationStatus.AccessDenied);
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            if (ResolveLocked(entry, now) == PowerShellFiniteOperationStatus.Active)
            {
                TransitionLocked(entry, requested, now, errorCode);
            }

            return Result(entry, operationId);
        }
    }

    private bool TryResolveActive(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        out Entry entry,
        out PowerShellFiniteOperationResult result)
    {
        entry = null!;
        result = default;
        if (!Owns(owner))
        {
            result = Result(PowerShellFiniteOperationStatus.AccessDenied);
            return false;
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (!TryLookupLocked(owner, operationId, out entry))
            {
                result = Result(PowerShellFiniteOperationStatus.AccessDenied);
                return false;
            }

            PowerShellFiniteOperationStatus status = ResolveLocked(entry, timeProvider.GetUtcNow());
            if (status != PowerShellFiniteOperationStatus.Active)
            {
                result = Result(entry, operationId);
                return false;
            }

            return true;
        }
    }

    private PowerShellFinitePageReadResult<TPage> InvalidatePageAccess(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        PowerShellFinitePageValidation validation)
    {
        PowerShellFiniteOperationStatus requested = validation == PowerShellFinitePageValidation.SnapshotInvalidated
            ? PowerShellFiniteOperationStatus.SnapshotInvalidated
            : PowerShellFiniteOperationStatus.PermissionChanged;

        lock (gate)
        {
            if (!TryLookupLocked(owner, operationId, out Entry entry))
            {
                return ReadResult(PowerShellFiniteOperationStatus.AccessDenied);
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            if (ResolveLocked(entry, now) == PowerShellFiniteOperationStatus.Succeeded)
            {
                TransitionLocked(entry, requested, now, 0);
            }

            return ReadResult(Result(entry, operationId));
        }
    }

    private PowerShellFinitePageReadResult<TPage> FailPageBounds(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        lock (gate)
        {
            if (!TryLookupLocked(owner, operationId, out Entry entry))
            {
                return ReadResult(PowerShellFiniteOperationStatus.AccessDenied);
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            if (ResolveLocked(entry, now) == PowerShellFiniteOperationStatus.Succeeded)
            {
                TransitionLocked(entry, PowerShellFiniteOperationStatus.BoundsExceeded, now, 0);
            }

            return ReadResult(Result(entry, operationId));
        }
    }

    private bool Owns(PowerShellFiniteOperationOwner? owner)
    {
        ThrowIfDisposed();
        return owner is not null && owner.IsOwnedBy(this);
    }

    private bool TryLookupLocked(PowerShellFiniteOperationOwner owner, PowerShellFiniteOperationId operationId, out Entry entry)
    {
        entry = null!;
        if (!operationId.IsValid ||
            !entries.TryGetValue(operationId.Value, out Entry? candidate) ||
            candidate is null ||
            !ReferenceEquals(candidate.Owner, owner) ||
            !owner.IsOwnedBy(this))
        {
            return false;
        }

        entry = candidate;
        return true;
    }

    private Guid NewIdentifierLocked()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = new Guid(bytes);
        }
        while (value == Guid.Empty || entries.ContainsKey(value));

        return value;
    }

    private PowerShellFiniteOperationStatus ResolveLocked(Entry entry, DateTimeOffset now)
    {
        if (entry.Status == PowerShellFiniteOperationStatus.Active && now >= entry.Deadline)
        {
            TransitionLocked(entry, PowerShellFiniteOperationStatus.TimedOut, now, 0);
        }
        else if (IsTerminal(entry.Status) &&
            entry.Status != PowerShellFiniteOperationStatus.Expired &&
            entry.Status != PowerShellFiniteOperationStatus.Released &&
            entry.ExpiresAt is DateTimeOffset expiresAt &&
            now >= expiresAt)
        {
            entry.Pages = null;
            entry.Status = PowerShellFiniteOperationStatus.Expired;
            entry.ErrorCode = 0;
        }

        return entry.Status;
    }

    private static void DisposeEntry(Entry entry)
    {
        entry.Pages = null;
        entry.DisposeDeadlineTimer();
        try
        {
            entry.Cancellation.Cancel();
        }
        finally
        {
            entry.Cancellation.Dispose();
        }
    }

    private static void TransitionLocked(
        Entry entry,
        PowerShellFiniteOperationStatus status,
        DateTimeOffset now,
        int errorCode)
    {
        entry.Status = status;
        entry.DisposeDeadlineTimer();
        entry.ErrorCode = errorCode;
        if (status != PowerShellFiniteOperationStatus.Succeeded)
        {
            entry.Pages = null;
        }

        entry.ExpiresAt = now + entry.TerminalRetention;
        entry.Cancellation.Cancel();
    }

    private static PowerShellFiniteOperationResult Result(PowerShellFiniteOperationStatus status) =>
        new(status, default, expiresAt: null, errorCode: 0);

    private static PowerShellFiniteOperationResult Result(Entry entry, PowerShellFiniteOperationId operationId) =>
        new(entry.Status, operationId, entry.ExpiresAt, entry.ErrorCode);

    private static PowerShellFinitePageReadResult<TPage> ReadResult(PowerShellFiniteOperationStatus status) =>
        new(Result(status), default, hasPage: false, nextCursor: null);

    private static PowerShellFinitePageReadResult<TPage> ReadResult(
        PowerShellFiniteOperationStatus status,
        PowerShellFiniteOperationId operationId) =>
        new(new PowerShellFiniteOperationResult(status, operationId, expiresAt: null, errorCode: 0), default, hasPage: false, nextCursor: null);

    private static PowerShellFinitePageReadResult<TPage> ReadResult(PowerShellFiniteOperationResult operation) =>
        new(operation, default, hasPage: false, nextCursor: null);

    private void ReleaseOwner(PowerShellFiniteOperationOwner owner)
    {
        lock (gate)
        {
            List<Guid>? removed = null;
            foreach ((Guid operationId, Entry entry) in entries)
            {
                if (ReferenceEquals(entry.Owner, owner))
                {
                    (removed ??= []).Add(operationId);
                    DisposeEntry(entry);
                }
            }

            if (removed is not null)
            {
                foreach (Guid operationId in removed)
                {
                    entries.Remove(operationId);
                }
            }
        }
    }

    private void OnDeadline(Guid operationValue, Entry expected)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 ||
                !entries.TryGetValue(operationValue, out Entry? entry) ||
                !ReferenceEquals(entry, expected) ||
                entry.Status != PowerShellFiniteOperationStatus.Active)
            {
                return;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            if (now >= entry.Deadline)
            {
                TransitionLocked(entry, PowerShellFiniteOperationStatus.TimedOut, now, 0);
                return;
            }

            entry.RescheduleDeadline(entry.Deadline - now);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(PowerShellFiniteOperationRegistry<TPage>));
        }
    }

    private sealed class Entry
    {
        internal Entry(
            PowerShellFiniteOperationOwner owner,
            PowerShellFiniteOperationBinding binding,
            DateTimeOffset deadline,
            TimeSpan terminalRetention)
        {
            Owner = owner;
            Binding = binding;
            Deadline = deadline;
            TerminalRetention = terminalRetention;
            Cancellation = new CancellationTokenSource();
            Status = PowerShellFiniteOperationStatus.Active;
        }

        internal PowerShellFiniteOperationOwner Owner { get; }

        internal PowerShellFiniteOperationBinding Binding { get; }

        internal DateTimeOffset Deadline { get; }

        internal TimeSpan TerminalRetention { get; }

        internal CancellationTokenSource Cancellation { get; }

        internal PowerShellFiniteOperationStatus Status { get; set; }

        internal int ErrorCode { get; set; }

        internal DateTimeOffset? ExpiresAt { get; set; }

        internal List<TPage>? Pages { get; set; }

        private ITimer? deadlineTimer;

        internal void ScheduleDeadline(TimeProvider timeProvider, Action callback)
        {
            TimeSpan dueTime = Deadline - timeProvider.GetUtcNow();
            deadlineTimer = timeProvider.CreateTimer(
                static state => ((Action)state!).Invoke(),
                callback,
                dueTime > TimeSpan.Zero ? dueTime : TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
        }

        internal void RescheduleDeadline(TimeSpan dueTime)
        {
            _ = deadlineTimer?.Change(
                dueTime > TimeSpan.Zero ? dueTime : TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
        }

        internal void DisposeDeadlineTimer()
        {
            deadlineTimer?.Dispose();
            deadlineTimer = null;
        }
    }
}
