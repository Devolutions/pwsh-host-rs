using System.Collections.Generic;
using System.Security.Cryptography;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// The explicit state of a finite host-owned operation.
/// </summary>
public enum PowerShellFiniteOperationStatus
{
    Active,
    Succeeded,
    Cancelled,
    TimedOut,
    SnapshotInvalidated,
    PermissionDenied,
    Released,
    Expired,
    AccessDenied,
    Rejected,
    RegistryDisposed,
}

/// <summary>
/// The application-owned access decision for a retained fixed-schema page.
/// </summary>
public enum PowerShellFinitePageValidation
{
    Allowed,
    SnapshotInvalidated,
    PermissionDenied,
}

/// <summary>
/// Copies one fixed-schema page into registry-owned storage and reports its bounds.
/// </summary>
/// <typeparam name="TPage">The application's closed, fixed-schema page type.</typeparam>
/// <remarks>
/// The codec must return an independent copied value. It must not return a live
/// application object, a handle, a credential, or a page containing dynamically
/// discovered members.
/// </remarks>
public interface IPowerShellFinitePageCodec<TPage>
    where TPage : class
{
    /// <summary>
    /// Attempts to copy a page and report its item and UTF-8-equivalent byte counts.
    /// </summary>
    bool TryCopy(TPage source, out TPage copy, out int itemCount, out int byteCount);
}

/// <summary>
/// Revalidates the application-owned snapshot and permission revisions before a
/// retained page is returned.
/// </summary>
public interface IPowerShellFinitePageAccessValidator
{
    /// <summary>
    /// Validates an operation's immutable page binding.
    /// </summary>
    PowerShellFinitePageValidation Validate(in PowerShellFiniteOperationBinding binding);
}

/// <summary>
/// Binds one finite operation to one closed schema and application-owned revisions.
/// </summary>
public readonly struct PowerShellFiniteOperationBinding
{
    /// <summary>
    /// Creates a page binding.
    /// </summary>
    public PowerShellFiniteOperationBinding(
        Guid schemaId,
        long snapshotRevision,
        long permissionRevision)
    {
        if (schemaId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaId));
        }

        if (snapshotRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotRevision));
        }

        if (permissionRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permissionRevision));
        }

        SchemaId = schemaId;
        SnapshotRevision = snapshotRevision;
        PermissionRevision = permissionRevision;
    }

    /// <summary>
    /// Gets the application's fixed page-schema identifier.
    /// </summary>
    public Guid SchemaId { get; }

    /// <summary>
    /// Gets the application-owned source snapshot revision.
    /// </summary>
    public long SnapshotRevision { get; }

    /// <summary>
    /// Gets the application-owned permission revision.
    /// </summary>
    public long PermissionRevision { get; }
}

/// <summary>
/// Defines fixed bounds and static copy/validation hooks for one page type.
/// </summary>
/// <typeparam name="TPage">The application's closed, fixed-schema page type.</typeparam>
public sealed class PowerShellFinitePageContract<TPage>
    where TPage : class
{
    /// <summary>
    /// Creates a bounded fixed-schema page contract.
    /// </summary>
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

        if (maximumPages < 1 || maximumPages > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPages));
        }

        if (maximumItemsPerPage < 1 || maximumItemsPerPage > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItemsPerPage));
        }

        if (maximumPageBytes < 1 || maximumPageBytes > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPageBytes));
        }

        if (checked(maximumPages * maximumItemsPerPage) > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumItemsPerPage),
                "The retained item cardinality exceeds 65,536.");
        }

        if (checked(maximumPages * maximumPageBytes) > 16 * 1_024 * 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPageBytes),
                "The retained byte cardinality exceeds 16 MiB.");
        }

        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(accessValidator);

        SchemaId = schemaId;
        MaximumPages = maximumPages;
        MaximumItemsPerPage = maximumItemsPerPage;
        MaximumPageBytes = maximumPageBytes;
        Codec = codec;
        AccessValidator = accessValidator;
    }

    /// <summary>
    /// Gets the closed page-schema identifier accepted by this contract.
    /// </summary>
    public Guid SchemaId { get; }

    /// <summary>
    /// Gets the maximum number of retained pages for one operation.
    /// </summary>
    public int MaximumPages { get; }

    /// <summary>
    /// Gets the maximum item count reported by one copied page.
    /// </summary>
    public int MaximumItemsPerPage { get; }

    /// <summary>
    /// Gets the maximum byte count reported by one copied page.
    /// </summary>
    public int MaximumPageBytes { get; }

    internal IPowerShellFinitePageCodec<TPage> Codec { get; }

    internal IPowerShellFinitePageAccessValidator AccessValidator { get; }
}

/// <summary>
/// Sets the active-work deadline and terminal-page retention lifetime for an operation.
/// </summary>
public readonly struct PowerShellFiniteOperationOptions
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Creates bounded operation lifetimes.
    /// </summary>
    public PowerShellFiniteOperationOptions(
        TimeSpan executionDeadline,
        TimeSpan terminalLeaseLifetime)
    {
        if (executionDeadline < TimeSpan.FromMilliseconds(1) ||
            executionDeadline > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(executionDeadline));
        }

        if (terminalLeaseLifetime < TimeSpan.FromMilliseconds(1) ||
            terminalLeaseLifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalLeaseLifetime));
        }

        ExecutionDeadline = executionDeadline;
        TerminalLeaseLifetime = terminalLeaseLifetime;
    }

    /// <summary>
    /// Gets the maximum active lifetime. A deadline wins before a later completion
    /// or cancellation is admitted.
    /// </summary>
    public TimeSpan ExecutionDeadline { get; }

    /// <summary>
    /// Gets the bounded interval during which terminal pages remain readable.
    /// </summary>
    public TimeSpan TerminalLeaseLifetime { get; }
}

/// <summary>
/// An opaque, owner-scoped, cryptographically random finite-operation identity.
/// </summary>
public readonly struct PowerShellFiniteOperationId : IEquatable<PowerShellFiniteOperationId>
{
    internal PowerShellFiniteOperationId(Guid ownerToken, Guid operationToken)
    {
        OwnerToken = ownerToken;
        OperationToken = operationToken;
    }

    internal Guid OwnerToken { get; }

    internal Guid OperationToken { get; }

    /// <summary>
    /// Gets whether this is an identity issued by a registry.
    /// </summary>
    public bool IsValid => OwnerToken != Guid.Empty && OperationToken != Guid.Empty;

    /// <inheritdoc />
    public bool Equals(PowerShellFiniteOperationId other)
    {
        return OwnerToken == other.OwnerToken && OperationToken == other.OperationToken;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is PowerShellFiniteOperationId other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(OwnerToken, OperationToken);
    }

    /// <summary>
    /// Compares two opaque operation identities.
    /// </summary>
    public static bool operator ==(PowerShellFiniteOperationId left, PowerShellFiniteOperationId right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two opaque operation identities.
    /// </summary>
    public static bool operator !=(PowerShellFiniteOperationId left, PowerShellFiniteOperationId right)
    {
        return !left.Equals(right);
    }
}

/// <summary>
/// An opaque, operation-scoped cursor for a retained fixed-schema page.
/// </summary>
public readonly struct PowerShellFinitePageCursor : IEquatable<PowerShellFinitePageCursor>
{
    internal PowerShellFinitePageCursor(Guid operationToken, Guid cursorToken)
    {
        OperationToken = operationToken;
        CursorToken = cursorToken;
    }

    internal Guid OperationToken { get; }

    internal Guid CursorToken { get; }

    /// <summary>
    /// Gets the only cursor that starts a page sequence.
    /// </summary>
    public static PowerShellFinitePageCursor Start => default;

    /// <summary>
    /// Gets whether this cursor starts a page sequence.
    /// </summary>
    public bool IsStart => OperationToken == Guid.Empty && CursorToken == Guid.Empty;

    /// <inheritdoc />
    public bool Equals(PowerShellFinitePageCursor other)
    {
        return OperationToken == other.OperationToken && CursorToken == other.CursorToken;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is PowerShellFinitePageCursor other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(OperationToken, CursorToken);
    }

    /// <summary>
    /// Compares two opaque page cursors.
    /// </summary>
    public static bool operator ==(PowerShellFinitePageCursor left, PowerShellFinitePageCursor right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two opaque page cursors.
    /// </summary>
    public static bool operator !=(PowerShellFinitePageCursor left, PowerShellFinitePageCursor right)
    {
        return !left.Equals(right);
    }
}

/// <summary>
/// The copied state of one finite operation.
/// </summary>
public sealed class PowerShellFiniteOperationResult
{
    internal PowerShellFiniteOperationResult(
        PowerShellFiniteOperationId operationId,
        PowerShellFiniteOperationStatus status,
        bool isTerminal)
    {
        OperationId = operationId;
        Status = status;
        IsTerminal = isTerminal;
    }

    /// <summary>
    /// Gets the operation identity when admission succeeded.
    /// </summary>
    public PowerShellFiniteOperationId OperationId { get; }

    /// <summary>
    /// Gets the explicit operation state.
    /// </summary>
    public PowerShellFiniteOperationStatus Status { get; }

    /// <summary>
    /// Gets whether this result represents a terminal state.
    /// </summary>
    public bool IsTerminal { get; }
}

/// <summary>
/// A copied result of reading one finite fixed-schema page.
/// </summary>
/// <typeparam name="TPage">The application's closed, fixed-schema page type.</typeparam>
public sealed class PowerShellFinitePageReadResult<TPage>
    where TPage : class
{
    internal PowerShellFinitePageReadResult(
        PowerShellFiniteOperationResult operation,
        TPage? page,
        PowerShellFinitePageCursor? nextCursor)
    {
        Operation = operation;
        Page = page;
        NextCursor = nextCursor;
    }

    /// <summary>
    /// Gets the copied state observed while reading.
    /// </summary>
    public PowerShellFiniteOperationResult Operation { get; }

    /// <summary>
    /// Gets the copied page when the read succeeded.
    /// </summary>
    public TPage? Page { get; }

    /// <summary>
    /// Gets the next opaque cursor, or <see langword="null"/> for the terminal page.
    /// </summary>
    public PowerShellFinitePageCursor? NextCursor { get; }

    /// <summary>
    /// Gets whether this result contains a page.
    /// </summary>
    public bool HasPage => Page is not null;
}

/// <summary>
/// Owns a bounded set of finite operations.
/// </summary>
public sealed class PowerShellFiniteOperationOwner : IDisposable
{
    private readonly PowerShellFiniteOperationRegistryOwner registry;
    private int disposed;

    internal PowerShellFiniteOperationOwner(
        PowerShellFiniteOperationRegistryOwner registry,
        Guid token)
    {
        this.registry = registry;
        Token = token;
    }

    internal Guid Token { get; }

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>
    /// Cancels and releases every operation owned by this owner.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        registry.ReleaseOwner(this);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Provides cooperative cancellation for host-side work admitted by a finite operation.
/// </summary>
/// <remarks>
/// This token is not a PowerShell, payload, or broker callback capability. A worker
/// may use it to stop its own work, but must not synchronously invoke PowerShell or
/// unrelated FFI operations from a cancellation registration.
/// </remarks>
public sealed class PowerShellFiniteOperationLease : IDisposable
{
    private readonly PowerShellFiniteOperationRegistryOwner registry;
    private readonly PowerShellFiniteOperationOwner owner;
    private readonly PowerShellFiniteOperationId operationId;
    private readonly CancellationToken cancellationToken;
    private int disposed;

    internal PowerShellFiniteOperationLease(
        PowerShellFiniteOperationRegistryOwner registry,
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        CancellationToken cancellationToken)
    {
        this.registry = registry;
        this.owner = owner;
        this.operationId = operationId;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets cooperative cancellation for only the associated host-side operation.
    /// </summary>
    public CancellationToken CancellationToken => cancellationToken;

    /// <summary>
    /// Cancels and releases the operation when it remains owned.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        registry.ReleaseFromLease(owner, operationId);
        GC.SuppressFinalize(this);
    }
}

internal interface PowerShellFiniteOperationRegistryOwner
{
    void ReleaseOwner(PowerShellFiniteOperationOwner owner);

    void ReleaseFromLease(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId);
}

/// <summary>
/// Retains a bounded, owner-scoped set of finite operations and copied fixed-schema pages.
/// </summary>
/// <typeparam name="TPage">The application's closed, fixed-schema page type.</typeparam>
/// <remarks>
/// This is a host-side lifetime primitive. It does not dispatch jobs, transfer page
/// objects across a PowerShell boundary, infer authorization, or persist operations.
/// Applications provide the static page codec and the revision validator.
/// </remarks>
public sealed class PowerShellFiniteOperationRegistry<TPage> :
    IDisposable,
    PowerShellFiniteOperationRegistryOwner
    where TPage : class
{
    private const int MaximumRetainedItems = 65_536;
    private const int MaximumRetainedBytes = 16 * 1_024 * 1_024;

    private readonly object gate = new();
    private readonly PowerShellFinitePageContract<TPage> contract;
    private readonly int maximumOperations;
    private readonly Dictionary<Guid, PowerShellFiniteOperationOwner> owners = new();
    private readonly Dictionary<Guid, OperationEntry> operations = new();
    private int retainedItemCount;
    private int retainedByteCount;
    private int disposed;

    /// <summary>
    /// Creates a bounded finite-operation registry.
    /// </summary>
    public PowerShellFiniteOperationRegistry(
        PowerShellFinitePageContract<TPage> contract,
        int maximumOperations = 32)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (maximumOperations < 1 || maximumOperations > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOperations));
        }

        this.contract = contract;
        this.maximumOperations = maximumOperations;
    }

    /// <summary>
    /// Gets the number of active or retained terminal operations.
    /// </summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                SweepExpiredLocked(GetTimestampMilliseconds());
                return operations.Count;
            }
        }
    }

    /// <summary>
    /// Creates an owner whose opaque token scopes all subsequently admitted operations.
    /// </summary>
    public PowerShellFiniteOperationOwner CreateOwner()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            Guid token;
            do
            {
                token = CreateRandomToken();
            }
            while (owners.ContainsKey(token));

            var owner = new PowerShellFiniteOperationOwner(this, token);
            owners.Add(token, owner);
            return owner;
        }
    }

    /// <summary>
    /// Admits one owner-scoped finite operation.
    /// </summary>
    public PowerShellFiniteOperationResult TryStart(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationBinding binding,
        PowerShellFiniteOperationOptions options,
        out PowerShellFiniteOperationLease lease)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lease = null!;

        if (binding.SchemaId != contract.SchemaId)
        {
            return CreateResult(default, PowerShellFiniteOperationStatus.Rejected);
        }

        if (!AreOptionsValid(options))
        {
            return CreateResult(default, PowerShellFiniteOperationStatus.Rejected);
        }

        lock (gate)
        {
            SweepExpiredLocked(GetTimestampMilliseconds());
            if (!TryValidateOwnerLocked(owner))
            {
                return CreateResult(default, GetClosedStatusLocked());
            }

            if (operations.Count >= maximumOperations)
            {
                return CreateResult(default, PowerShellFiniteOperationStatus.Rejected);
            }
        }

        lock (gate)
        {
            SweepExpiredLocked(GetTimestampMilliseconds());
            if (!TryValidateOwnerLocked(owner))
            {
                return CreateResult(default, GetClosedStatusLocked());
            }

            if (operations.Count >= maximumOperations)
            {
                return CreateResult(default, PowerShellFiniteOperationStatus.Rejected);
            }

            Guid operationToken;
            do
            {
                operationToken = CreateRandomToken();
            }
            while (operations.ContainsKey(operationToken));

            var operationId = new PowerShellFiniteOperationId(owner.Token, operationToken);
            var cancellationSource = new CancellationTokenSource();
            lease = new PowerShellFiniteOperationLease(
                this,
                owner,
                operationId,
                cancellationSource.Token);
            operations.Add(
                operationToken,
                new OperationEntry(
                    owner.Token,
                    operationId,
                    binding,
                    options,
                    GetTimestampMilliseconds(),
                    cancellationSource));
            return CreateResult(operationId, PowerShellFiniteOperationStatus.Active);
        }
    }

    /// <summary>
    /// Completes an active operation with independently copied, bounded pages.
    /// </summary>
    public PowerShellFiniteOperationResult TryComplete(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        IReadOnlyList<TPage> pages)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(pages);

        lock (gate)
        {
            PowerShellFiniteOperationResult current = TryGetOperationLocked(owner, operationId, out _);
            if (current.Status != PowerShellFiniteOperationStatus.Active)
            {
                return current;
            }
        }

        if (!TryCopyPages(pages, out CopiedPages copiedPages))
        {
            lock (gate)
            {
                PowerShellFiniteOperationResult current = TryGetOperationLocked(owner, operationId, out OperationEntry? entry);
                if (current.Status == PowerShellFiniteOperationStatus.Active && entry is not null)
                {
                    entry.Terminalize(PowerShellFiniteOperationStatus.Rejected, GetTimestampMilliseconds());
                    return CreateResult(operationId, PowerShellFiniteOperationStatus.Rejected);
                }

                return current;
            }
        }

        lock (gate)
        {
            PowerShellFiniteOperationResult current = TryGetOperationLocked(owner, operationId, out OperationEntry? entry);
            if (current.Status != PowerShellFiniteOperationStatus.Active || entry is null)
            {
                return current;
            }

            if (retainedItemCount > MaximumRetainedItems - copiedPages.ItemCount ||
                retainedByteCount > MaximumRetainedBytes - copiedPages.ByteCount)
            {
                entry.Terminalize(PowerShellFiniteOperationStatus.Rejected, GetTimestampMilliseconds());
                return CreateResult(operationId, PowerShellFiniteOperationStatus.Rejected);
            }

            entry.Complete(copiedPages, GetTimestampMilliseconds());
            retainedItemCount += copiedPages.ItemCount;
            retainedByteCount += copiedPages.ByteCount;
            // Token registrations can re-enter this registry synchronously.
            entry.SignalCancellation();
            return CreateResult(operationId, PowerShellFiniteOperationStatus.Succeeded);
        }
    }

    /// <summary>
    /// Cancels an active operation. Repeated cancellation returns the same terminal result.
    /// </summary>
    public PowerShellFiniteOperationResult TryCancel(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (gate)
        {
            PowerShellFiniteOperationResult current = TryGetOperationLocked(owner, operationId, out OperationEntry? entry);
            if (current.Status != PowerShellFiniteOperationStatus.Active || entry is null)
            {
                return current;
            }

            entry.Terminalize(PowerShellFiniteOperationStatus.Cancelled, GetTimestampMilliseconds());
            return CreateResult(operationId, PowerShellFiniteOperationStatus.Cancelled);
        }
    }

    /// <summary>
    /// Gets the current operation state without reading a page.
    /// </summary>
    public PowerShellFiniteOperationResult TryGetStatus(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (gate)
        {
            return TryGetOperationLocked(owner, operationId, out _);
        }
    }

    /// <summary>
    /// Pulls one copied page through an opaque operation-scoped cursor.
    /// </summary>
    public PowerShellFinitePageReadResult<TPage> TryReadPage(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        PowerShellFinitePageCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(owner);

        PowerShellFiniteOperationBinding binding;
        lock (gate)
        {
            PowerShellFiniteOperationResult current = TryGetOperationLocked(owner, operationId, out OperationEntry? entry);
            if (current.Status != PowerShellFiniteOperationStatus.Succeeded || entry is null)
            {
                return new PowerShellFinitePageReadResult<TPage>(current, null, null);
            }

            if (!entry.TryGetPageIndex(cursor, out _))
            {
                if (cursor.IsStart && entry.Pages.Count == 0)
                {
                    return new PowerShellFinitePageReadResult<TPage>(current, null, null);
                }

                return new PowerShellFinitePageReadResult<TPage>(
                    CreateResult(
                        operationId,
                        PowerShellFiniteOperationStatus.Rejected,
                        isTerminal: false),
                    null,
                    null);
            }

            binding = entry.Binding;
        }

        PowerShellFinitePageValidation validation = contract.AccessValidator.Validate(in binding);
        if (validation != PowerShellFinitePageValidation.Allowed)
        {
            PowerShellFiniteOperationStatus status = MapValidation(validation);
            lock (gate)
            {
                PowerShellFiniteOperationResult current = TryGetOperationLocked(owner, operationId, out OperationEntry? entry);
                if (current.Status == PowerShellFiniteOperationStatus.Succeeded && entry is not null)
                {
                    entry.InvalidatePageAccess(status);
                    current = CreateResult(operationId, status);
                }

                return new PowerShellFinitePageReadResult<TPage>(current, null, null);
            }

        }

        lock (gate)
        {
            PowerShellFiniteOperationResult current = TryGetOperationLocked(owner, operationId, out OperationEntry? entry);
            if (current.Status != PowerShellFiniteOperationStatus.Succeeded ||
                entry is null ||
                !entry.TryGetPageIndex(cursor, out int pageIndex))
            {
                return new PowerShellFinitePageReadResult<TPage>(current, null, null);
            }

            StoredPage page = entry.Pages[pageIndex];
            PowerShellFinitePageCursor? nextCursor = entry.GetNextCursor(pageIndex);
            return new PowerShellFinitePageReadResult<TPage>(current, page.Value, nextCursor);
        }
    }

    /// <summary>
    /// Cancels and releases an operation, invalidating all of its cursors.
    /// </summary>
    public PowerShellFiniteOperationResult TryRelease(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (gate)
        {
            if (!TryValidateOwnerLocked(owner))
            {
                return CreateResult(default, GetClosedStatusLocked());
            }

            if (!operationId.IsValid ||
                operationId.OwnerToken != owner.Token ||
                !operations.TryGetValue(operationId.OperationToken, out OperationEntry? entry) ||
                entry.OwnerToken != owner.Token)
            {
                return CreateResult(default, PowerShellFiniteOperationStatus.AccessDenied);
            }

            long now = GetTimestampMilliseconds();
            if (ExpireOrReleaseTerminalLocked(entry, now))
            {
                return CreateResult(operationId, PowerShellFiniteOperationStatus.Expired);
            }

            RemoveOperationLocked(entry);
            return CreateResult(operationId, PowerShellFiniteOperationStatus.Released);
        }
    }

    /// <summary>
    /// Cancels and releases every retained operation.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (gate)
        {
            foreach (OperationEntry entry in operations.Values.ToArray())
            {
                RemoveOperationLocked(entry);
            }

            owners.Clear();
        }

        GC.SuppressFinalize(this);
    }

    void PowerShellFiniteOperationRegistryOwner.ReleaseOwner(PowerShellFiniteOperationOwner owner)
    {
        ReleaseOwner(owner);
    }

    void PowerShellFiniteOperationRegistryOwner.ReleaseFromLease(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        ReleaseFromLease(owner, operationId);
    }

    private void ReleaseOwner(PowerShellFiniteOperationOwner owner)
    {
        lock (gate)
        {
            if (!owners.Remove(owner.Token))
            {
                return;
            }

            foreach (OperationEntry entry in operations.Values
                .Where(entry => entry.OwnerToken == owner.Token)
                .ToArray())
            {
                RemoveOperationLocked(entry);
            }
        }
    }

    private void ReleaseFromLease(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId)
    {
        _ = TryRelease(owner, operationId);
    }

    private PowerShellFiniteOperationResult TryGetOperationLocked(
        PowerShellFiniteOperationOwner owner,
        PowerShellFiniteOperationId operationId,
        out OperationEntry? entry)
    {
        entry = null;
        if (!TryValidateOwnerLocked(owner))
        {
            return CreateResult(default, GetClosedStatusLocked());
        }

        if (!operationId.IsValid ||
            operationId.OwnerToken != owner.Token ||
            !operations.TryGetValue(operationId.OperationToken, out OperationEntry? candidate) ||
            candidate.OwnerToken != owner.Token)
        {
            return CreateResult(default, PowerShellFiniteOperationStatus.AccessDenied);
        }

        long now = GetTimestampMilliseconds();
        if (ExpireOrReleaseTerminalLocked(candidate, now))
        {
            return CreateResult(operationId, PowerShellFiniteOperationStatus.Expired);
        }

        entry = candidate;
        return CreateResult(operationId, candidate.Status);
    }

    private bool TryCopyPages(IReadOnlyList<TPage> pages, out CopiedPages copiedPages)
    {
        if (pages.Count > contract.MaximumPages)
        {
            copiedPages = default;
            return false;
        }

        var copied = new List<StoredPage>(pages.Count);
        int itemCountTotal = 0;
        int byteCountTotal = 0;
        for (int index = 0; index < pages.Count; index++)
        {
            TPage? source = pages[index];
            if (source is null)
            {
                copiedPages = default;
                return false;
            }

            if (!contract.Codec.TryCopy(source, out TPage copy, out int itemCount, out int byteCount) ||
                copy is null ||
                itemCount < 0 ||
                itemCount > contract.MaximumItemsPerPage ||
                byteCount < 0 ||
                byteCount > contract.MaximumPageBytes)
            {
                copiedPages = default;
                return false;
            }

            itemCountTotal = checked(itemCountTotal + itemCount);
            byteCountTotal = checked(byteCountTotal + byteCount);
            copied.Add(new StoredPage(copy));
        }

        copiedPages = new CopiedPages(copied, itemCountTotal, byteCountTotal);
        return true;
    }

    private bool TryValidateOwnerLocked(PowerShellFiniteOperationOwner owner)
    {
        return Volatile.Read(ref disposed) == 0 &&
            !owner.IsDisposed &&
            owners.TryGetValue(owner.Token, out PowerShellFiniteOperationOwner? registeredOwner) &&
            ReferenceEquals(owner, registeredOwner);
    }

    private PowerShellFiniteOperationStatus GetClosedStatusLocked()
    {
        return Volatile.Read(ref disposed) == 0
            ? PowerShellFiniteOperationStatus.AccessDenied
            : PowerShellFiniteOperationStatus.RegistryDisposed;
    }

    private void SweepExpiredLocked(long now)
    {
        foreach (OperationEntry entry in operations.Values.ToArray())
        {
            ExpireOrReleaseTerminalLocked(entry, now);
        }
    }

    private bool ExpireOrReleaseTerminalLocked(OperationEntry entry, long now)
    {
        if (entry.Status == PowerShellFiniteOperationStatus.Active &&
            now >= entry.ActiveDeadlineMilliseconds)
        {
            entry.Terminalize(
                PowerShellFiniteOperationStatus.TimedOut,
                entry.ActiveDeadlineMilliseconds);
        }

        if (entry.IsTerminal &&
            now >= entry.TerminalDeadlineMilliseconds)
        {
            RemoveOperationLocked(entry);
            return true;
        }

        return false;
    }

    private void RemoveOperationLocked(OperationEntry entry)
    {
        if (!operations.Remove(entry.OperationId.OperationToken))
        {
            return;
        }

        retainedItemCount -= entry.RetainedItemCount;
        retainedByteCount -= entry.RetainedByteCount;
        entry.Release();
    }

    private static PowerShellFiniteOperationResult CreateResult(
        PowerShellFiniteOperationId operationId,
        PowerShellFiniteOperationStatus status,
        bool? isTerminal = null)
    {
        return new PowerShellFiniteOperationResult(
            operationId,
            status,
            isTerminal ?? (operationId.IsValid && status != PowerShellFiniteOperationStatus.Active));
    }

    private static PowerShellFiniteOperationStatus MapValidation(PowerShellFinitePageValidation validation)
    {
        return validation switch
        {
            PowerShellFinitePageValidation.SnapshotInvalidated => PowerShellFiniteOperationStatus.SnapshotInvalidated,
            PowerShellFinitePageValidation.PermissionDenied => PowerShellFiniteOperationStatus.PermissionDenied,
            _ => PowerShellFiniteOperationStatus.Rejected,
        };
    }

    private static Guid CreateRandomToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid token;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            token = new Guid(bytes);
        }
        while (token == Guid.Empty);

        return token;
    }

    private static long GetTimestampMilliseconds()
    {
        return Environment.TickCount64;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private static bool AreOptionsValid(PowerShellFiniteOperationOptions options)
    {
        return options.ExecutionDeadline >= TimeSpan.FromMilliseconds(1) &&
            options.ExecutionDeadline <= TimeSpan.FromHours(1) &&
            options.TerminalLeaseLifetime >= TimeSpan.FromMilliseconds(1) &&
            options.TerminalLeaseLifetime <= TimeSpan.FromHours(1);
    }

    private sealed class OperationEntry
    {
        private readonly CancellationTokenSource cancellationSource;
        private readonly long terminalLeaseLifetimeMilliseconds;
        private readonly Dictionary<Guid, int> cursors = new();
        private List<StoredPage> pages = [];

        internal OperationEntry(
            Guid ownerToken,
            PowerShellFiniteOperationId operationId,
            PowerShellFiniteOperationBinding binding,
            PowerShellFiniteOperationOptions options,
            long now,
            CancellationTokenSource cancellationSource)
        {
            OwnerToken = ownerToken;
            OperationId = operationId;
            Binding = binding;
            ActiveDeadlineMilliseconds = checked(now + ToMilliseconds(options.ExecutionDeadline));
            terminalLeaseLifetimeMilliseconds = ToMilliseconds(options.TerminalLeaseLifetime);
            this.cancellationSource = cancellationSource;
            cancellationSource.CancelAfter(options.ExecutionDeadline);
            Status = PowerShellFiniteOperationStatus.Active;
        }

        internal Guid OwnerToken { get; }

        internal PowerShellFiniteOperationId OperationId { get; }

        internal PowerShellFiniteOperationBinding Binding { get; }

        internal long ActiveDeadlineMilliseconds { get; }

        internal long TerminalDeadlineMilliseconds { get; private set; } = long.MaxValue;

        internal PowerShellFiniteOperationStatus Status { get; private set; }

        internal bool IsTerminal => Status != PowerShellFiniteOperationStatus.Active;

        internal IReadOnlyList<StoredPage> Pages => pages;

        internal int RetainedItemCount { get; private set; }

        internal int RetainedByteCount { get; private set; }

        internal void Complete(CopiedPages copiedPages, long now)
        {
            pages = copiedPages.Pages;
            RetainedItemCount = copiedPages.ItemCount;
            RetainedByteCount = copiedPages.ByteCount;
            for (int index = 1; index < pages.Count; index++)
            {
                Guid cursorToken;
                do
                {
                    cursorToken = CreateRandomToken();
                }
                while (!cursors.TryAdd(cursorToken, index));
            }

            SetTerminalState(PowerShellFiniteOperationStatus.Succeeded, now);
        }

        internal void Terminalize(PowerShellFiniteOperationStatus status, long now)
        {
            if (Status != PowerShellFiniteOperationStatus.Active)
            {
                return;
            }

            SetTerminalState(status, now);
            SignalCancellation();
        }

        internal void SignalCancellation()
        {
            cancellationSource.Cancel();
        }

        private void SetTerminalState(PowerShellFiniteOperationStatus status, long now)
        {
            Status = status;
            TerminalDeadlineMilliseconds = checked(now + terminalLeaseLifetimeMilliseconds);
        }

        internal void InvalidatePageAccess(PowerShellFiniteOperationStatus status)
        {
            if (Status == PowerShellFiniteOperationStatus.Succeeded)
            {
                Status = status;
            }
        }

        internal bool TryGetPageIndex(PowerShellFinitePageCursor cursor, out int pageIndex)
        {
            if (cursor.IsStart)
            {
                pageIndex = 0;
                return pages.Count != 0;
            }

            if (cursor.OperationToken != OperationId.OperationToken ||
                cursor.CursorToken == Guid.Empty ||
                !cursors.TryGetValue(cursor.CursorToken, out pageIndex))
            {
                pageIndex = 0;
                return false;
            }

            return true;
        }

        internal PowerShellFinitePageCursor? GetNextCursor(int pageIndex)
        {
            int nextPageIndex = checked(pageIndex + 1);
            if (nextPageIndex >= pages.Count)
            {
                return null;
            }

            foreach ((Guid token, int index) in cursors)
            {
                if (index == nextPageIndex)
                {
                    return new PowerShellFinitePageCursor(OperationId.OperationToken, token);
                }
            }

            throw new InvalidOperationException("The finite operation cursor table is inconsistent.");
        }

        internal void Release()
        {
            if (Status == PowerShellFiniteOperationStatus.Active)
            {
                Terminalize(PowerShellFiniteOperationStatus.Released, GetTimestampMilliseconds());
            }

            cancellationSource.Dispose();
            cursors.Clear();
            pages.Clear();
        }

        private static long ToMilliseconds(TimeSpan duration)
        {
            return checked((long)Math.Ceiling(duration.TotalMilliseconds));
        }
    }

    private sealed class StoredPage
    {
        internal StoredPage(TPage value)
        {
            Value = value;
        }

        internal TPage Value { get; }
    }

    private readonly struct CopiedPages
    {
        internal CopiedPages(List<StoredPage> pages, int itemCount, int byteCount)
        {
            Pages = pages;
            ItemCount = itemCount;
            ByteCount = byteCount;
        }

        internal List<StoredPage> Pages { get; }

        internal int ItemCount { get; }

        internal int ByteCount { get; }
    }
}
