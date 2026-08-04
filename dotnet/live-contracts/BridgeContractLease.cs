#nullable enable

using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// One admitted bridge call. Resolving the lease and the object handle is a
/// single atomic step, so a call can never pass the lease check, lose a race to
/// closure, and then fail object resolution.
/// </summary>
/// <remarks>
/// An admitted call holds the resolved handler reference by value. A concurrent
/// closure clears the lease's object table but cannot revoke a reference the
/// call already holds, which is exactly why closure never has to block on or
/// interrupt work already in flight.
/// </remarks>
public readonly struct PowerShellBridgeAdmission
{
    internal PowerShellBridgeAdmission(ulong leaseId, uint generation, ulong objectId, ulong objectTypeId, object handler)
    {
        LeaseId = leaseId;
        Generation = generation;
        ObjectId = objectId;
        ObjectTypeId = objectTypeId;
        Handler = handler;
    }

    public ulong LeaseId { get; }

    public uint Generation { get; }

    public ulong ObjectId { get; }

    /// <summary>The declared object type of the resolved handle.</summary>
    public ulong ObjectTypeId { get; }

    /// <summary>The application handler this handle resolves to within its lease.</summary>
    public object? Handler { get; }

    public bool IsValid => Handler is not null;
}

/// <summary>Result of atomically admitting a generated finite-operation cancel request.</summary>
public enum PowerShellBridgeFiniteOperationCancelResult
{
    /// <summary>The handle was not an active finite operation.</summary>
    Invalid = 0,

    /// <summary>The caller won the one handler dispatch for this operation's cancellation.</summary>
    InvokeHandler = 1,

    /// <summary>The operation was already cancelled; return the generated success reply without redispatching.</summary>
    AlreadyCancelled = 2,
}

/// <summary>
/// The bounded lease and object tables a generated bridge dispatcher admits
/// calls against.
/// </summary>
/// <remarks>
/// <para>
/// Lease identifiers are process-monotonic and never reused, and object
/// identifiers are monotonic within their lease and never reused inside it, so
/// a released or escaped wrapper can never target a later object.
/// </para>
/// <para>
/// A lease has exactly one <c>Active -&gt; Closed</c> transition. The first
/// caller wins; a losing <see cref="Close"/> reports
/// <see cref="PowerShellBridgeStatus.AccessDenied"/> and changes nothing, while
/// <see cref="CloseAll"/> is idempotent because consumer disposal returns
/// nothing. Closure increments the generation and tombstones every handle in
/// the same locked transition.
/// </para>
/// </remarks>
public sealed class PowerShellBridgeLeaseTable
{
    /// <summary>The number of bridge leases that may be active in one process.</summary>
    public const int MaximumLeases = 16;

    /// <summary>The number of live object handles one lease may retain.</summary>
    public const int MaximumObjectsPerLease = 1024;

    /// <summary>The number of opaque finite-operation identities issued over one lease lifetime.</summary>
    public const int MaximumFiniteOperationsPerLease = 1024;

    private static readonly object ProcessGate = new();
    private static readonly List<WeakReference<PowerShellBridgeLeaseTable>> ActiveTables = new();
    private static ulong nextLeaseId;
    private static uint nextGeneration;

    private readonly object gate = new();
    private readonly Dictionary<ulong, Lease> leases = new();

    /// <summary>Gets the number of leases this table currently owns.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return leases.Count;
            }
        }
    }

    /// <summary>
    /// Allocates one lease and registers the root handler as its first object.
    /// A caller that already holds an active lease is rejected, so a replayed or
    /// concurrent open cannot consume lease slots.
    /// </summary>
    public int TryOpen(
        ulong rootObjectTypeId,
        object rootHandler,
        out ulong leaseId,
        out uint generation,
        out ulong rootObjectId)
    {
        leaseId = 0;
        generation = 0;
        rootObjectId = 0;
        if (rootObjectTypeId == 0 || rootHandler is null)
        {
            return PowerShellBridgeStatus.InvalidArgument;
        }

        lock (gate)
        {
            if (leases.Count != 0)
            {
                // One consumer broker owns one lease. Several payload proxies over
                // the same broker share it, which is why closure is first-wins
                // rather than reference counted.
                return PowerShellBridgeStatus.AccessDenied;
            }

            ulong allocated;
            uint allocatedGeneration;
            lock (ProcessGate)
            {
                // The budget is process-wide, so it must be reclaimed when a
                // dispatcher is dropped without disposal. Sweeping collected
                // tables here makes the counter self-healing; a plain counter
                // would leak a slot per abandoned dispatcher, permanently.
                SweepCollectedTablesLocked();
                if (ActiveTables.Count >= MaximumLeases)
                {
                    return PowerShellBridgeStatus.OutOfMemory;
                }

                allocated = checked(nextLeaseId + 1);
                allocatedGeneration = checked(nextGeneration + 1);
                nextLeaseId = allocated;
                nextGeneration = allocatedGeneration;
                ActiveTables.Add(new WeakReference<PowerShellBridgeLeaseTable>(this));
            }

            var lease = new Lease(allocated, allocatedGeneration);
            ulong rootId = lease.RegisterLocked(rootObjectTypeId, rootHandler);
            if (rootId == 0)
            {
                lock (ProcessGate)
                {
                    ForgetTableLocked();
                }

                return PowerShellBridgeStatus.OutOfMemory;
            }

            leases.Add(allocated, lease);
            leaseId = allocated;
            generation = allocatedGeneration;
            rootObjectId = rootId;
            return PowerShellBridgeStatus.Success;
        }
    }

    /// <summary>
    /// Resolves the lease and the object handle in one atomic step. An unknown,
    /// closed, or superseded lease and an unknown, released, or cross-lease
    /// handle are indistinguishable to the caller by design, so a caller cannot
    /// probe which handles exist.
    /// </summary>
    public bool TryAdmit(ulong leaseId, uint generation, ulong objectId, out PowerShellBridgeAdmission admission)
    {
        admission = default;
        lock (gate)
        {
            if (!leases.TryGetValue(leaseId, out Lease? lease) ||
                lease.Closed ||
                lease.Generation != generation ||
                !lease.TryResolveLocked(objectId, out ulong objectTypeId, out object? handler))
            {
                return false;
            }

            admission = new PowerShellBridgeAdmission(leaseId, generation, objectId, objectTypeId, handler!);
            return true;
        }
    }

    /// <summary>
    /// Registers a handler returned by the application and returns its
    /// lease-scoped identifier. Returning the same handler again yields the same
    /// identifier until it is released.
    /// </summary>
    public ulong Register(ulong leaseId, uint generation, ulong objectTypeId, object handler)
    {
        if (objectTypeId == 0 || handler is null)
        {
            return 0;
        }

        lock (gate)
        {
            return !leases.TryGetValue(leaseId, out Lease? lease) || lease.Closed || lease.Generation != generation
                ? 0
                : lease.RegisterLocked(objectTypeId, handler);
        }
    }

    /// <summary>
    /// Registers an owner-bound finite-operation handler with a nonzero
    /// cryptographically random object identifier. The identifier cannot be
    /// reissued during this lease lifetime.
    /// </summary>
    public ulong RegisterFiniteOperation(
        ulong leaseId,
        uint generation,
        ulong ownerObjectId,
        ulong objectTypeId,
        object handler,
        int maximumLifetimeMilliseconds)
    {
        if (objectTypeId == 0 ||
            ownerObjectId == 0 ||
            handler is null ||
            maximumLifetimeMilliseconds is < 1 or > 3_600_000)
        {
            return 0;
        }

        lock (gate)
        {
            return !leases.TryGetValue(leaseId, out Lease? lease) || lease.Closed || lease.Generation != generation
                ? 0
                : lease.RegisterFiniteOperationLocked(
                    ownerObjectId,
                    objectTypeId,
                    handler,
                    maximumLifetimeMilliseconds);
        }
    }

    /// <summary>
    /// Atomically admits only the first cancel for an active finite operation.
    /// Subsequent calls are successful no-ops and must not redispatch the handler.
    /// </summary>
    public PowerShellBridgeFiniteOperationCancelResult TryBeginFiniteOperationCancel(
        ulong leaseId,
        uint generation,
        ulong objectId)
    {
        lock (gate)
        {
            return !leases.TryGetValue(leaseId, out Lease? lease) || lease.Closed || lease.Generation != generation
                ? PowerShellBridgeFiniteOperationCancelResult.Invalid
                : lease.TryBeginFiniteOperationCancelLocked(objectId);
        }
    }

    /// <summary>Releases one object handle. The identifier is never re-allocated.</summary>
    public bool TryRelease(ulong leaseId, uint generation, ulong objectId)
    {
        lock (gate)
        {
            return leases.TryGetValue(leaseId, out Lease? lease) &&
                !lease.Closed &&
                lease.Generation == generation &&
                lease.TryReleaseLocked(objectId);
        }
    }

    /// <summary>
    /// Performs the single <c>Active -&gt; Closed</c> transition on behalf of the
    /// payload. The first caller wins; a later or stale call reports
    /// <see cref="PowerShellBridgeStatus.AccessDenied"/>.
    /// </summary>
    public int Close(ulong leaseId, uint generation)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(leaseId, out Lease? lease) || lease.Closed || lease.Generation != generation)
            {
                return PowerShellBridgeStatus.AccessDenied;
            }

            CloseLocked(lease);
            ReleaseTableSlot();

            // The closed lease is removed so a later open can allocate a fresh
            // one. Nothing escapes by doing so: lease identifiers are
            // process-monotonic and generations are never reused, so a wrapper
            // holding the old pair can never match the new lease.
            leases.Remove(leaseId);
            return PowerShellBridgeStatus.Success;
        }
    }

    /// <summary>
    /// Ends every lease this table owns. Consumer disposal returns nothing, so
    /// repeating it is a no-op rather than a failure.
    /// </summary>
    public void CloseAll()
    {
        lock (gate)
        {
            foreach (Lease lease in leases.Values)
            {
                if (!lease.Closed)
                {
                    CloseLocked(lease);
                }
            }

            leases.Clear();
            ReleaseTableSlot();
        }
    }

    private static void CloseLocked(Lease lease)
    {
        // One locked transition: tombstone every handle, then supersede the
        // generation so no frame carrying the old one is ever admitted again.
        lease.TombstoneLocked();
    }

    private void ReleaseTableSlot()
    {
        lock (ProcessGate)
        {
            ForgetTableLocked();
        }
    }

    private void ForgetTableLocked()
    {
        for (int index = ActiveTables.Count - 1; index >= 0; index--)
        {
            if (!ActiveTables[index].TryGetTarget(out PowerShellBridgeLeaseTable? table) ||
                ReferenceEquals(table, this))
            {
                ActiveTables.RemoveAt(index);
            }
        }
    }

    private static void SweepCollectedTablesLocked()
    {
        for (int index = ActiveTables.Count - 1; index >= 0; index--)
        {
            if (!ActiveTables[index].TryGetTarget(out _))
            {
                ActiveTables.RemoveAt(index);
            }
        }
    }

    private sealed class Lease
    {
        private readonly Dictionary<ulong, Entry> objectsById = new();
        private readonly Dictionary<object, ulong> idsByHandler = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ulong> issuedFiniteOperationIds = new();
        private ulong nextObjectId = 1;

        internal Lease(ulong id, uint generation)
        {
            Id = id;
            Generation = generation;
        }

        internal ulong Id { get; }

        internal uint Generation { get; private set; }

        internal bool Closed { get; private set; }

        internal ulong RegisterLocked(ulong objectTypeId, object handler)
        {
            if (Closed)
            {
                return 0;
            }

            if (idsByHandler.TryGetValue(handler, out ulong existing))
            {
                return objectsById.TryGetValue(existing, out Entry? entry) &&
                    entry is not null &&
                    entry.ObjectTypeId == objectTypeId
                    ? existing
                    : 0;
            }

            if (objectsById.Count >= MaximumObjectsPerLease || nextObjectId == 0)
            {
                return 0;
            }

            ulong id = nextObjectId++;
            objectsById.Add(id, new Entry(objectTypeId, handler, 0, false, 0));
            idsByHandler.Add(handler, id);
            return id;
        }

        internal ulong RegisterFiniteOperationLocked(
            ulong ownerObjectId,
            ulong objectTypeId,
            object handler,
            int maximumLifetimeMilliseconds)
        {
            if (Closed)
            {
                return 0;
            }

            SweepExpiredFiniteOperationsLocked();
            if (!TryResolveLocked(ownerObjectId, out _, out _) ||
                objectsById.Count >= MaximumObjectsPerLease ||
                issuedFiniteOperationIds.Count >= MaximumFiniteOperationsPerLease)
            {
                return 0;
            }

            if (idsByHandler.TryGetValue(handler, out ulong existing))
            {
                if (objectsById.TryGetValue(existing, out Entry? entry) &&
                    entry is not null &&
                    entry.ObjectTypeId == objectTypeId &&
                    entry.IsFiniteOperation &&
                    entry.OwnerObjectId == ownerObjectId
                    )
                {
                    if (IsAdmissibleLocked(entry))
                    {
                        return existing;
                    }

                    RemoveEntryAndDescendantsLocked(existing);
                }
                else
                {
                    return 0;
                }
            }

            Span<byte> random = stackalloc byte[sizeof(ulong)];
            for (int attempt = 0; attempt < 32; attempt++)
            {
                RandomNumberGenerator.Fill(random);
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(random);
                if (id == 0 || objectsById.ContainsKey(id) || !issuedFiniteOperationIds.Add(id))
                {
                    continue;
                }

                long deadline = checked(Environment.TickCount64 + maximumLifetimeMilliseconds);
                objectsById.Add(id, new Entry(objectTypeId, handler, ownerObjectId, true, deadline));
                idsByHandler.Add(handler, id);
                return id;
            }

            return 0;
        }

        internal bool TryResolveLocked(ulong objectId, out ulong objectTypeId, out object? handler)
        {
            if (objectsById.TryGetValue(objectId, out Entry? entry) && IsAdmissibleLocked(entry))
            {
                objectTypeId = entry.ObjectTypeId;
                handler = entry.Handler;
                return true;
            }

            RemoveEntryAndDescendantsLocked(objectId);
            objectTypeId = 0;
            handler = null;
            return false;
        }

        internal PowerShellBridgeFiniteOperationCancelResult TryBeginFiniteOperationCancelLocked(ulong objectId)
        {
            if (!objectsById.TryGetValue(objectId, out Entry? entry) ||
                !entry.IsFiniteOperation ||
                !IsAdmissibleLocked(entry))
            {
                RemoveEntryAndDescendantsLocked(objectId);
                return PowerShellBridgeFiniteOperationCancelResult.Invalid;
            }

            if (entry.CancelDispatched)
            {
                return PowerShellBridgeFiniteOperationCancelResult.AlreadyCancelled;
            }

            entry.CancelDispatched = true;
            return PowerShellBridgeFiniteOperationCancelResult.InvokeHandler;
        }

        internal bool TryReleaseLocked(ulong objectId)
        {
            if (!objectsById.TryGetValue(objectId, out Entry? entry) || entry is null)
            {
                return false;
            }

            RemoveEntryAndDescendantsLocked(objectId);
            return true;
        }

        internal void TombstoneLocked()
        {
            objectsById.Clear();
            idsByHandler.Clear();
            Closed = true;
            Generation = unchecked(Generation + 1);
        }

        private bool IsAdmissibleLocked(Entry entry)
        {
            if (entry.ExpiresAtMilliseconds != 0 && Environment.TickCount64 >= entry.ExpiresAtMilliseconds)
            {
                return false;
            }

            if (entry.OwnerObjectId == 0)
            {
                return true;
            }

            return objectsById.TryGetValue(entry.OwnerObjectId, out Entry? owner) &&
                owner != entry &&
                IsAdmissibleLocked(owner);
        }

        private void SweepExpiredFiniteOperationsLocked()
        {
            var expired = new List<ulong>();
            foreach (KeyValuePair<ulong, Entry> candidate in objectsById)
            {
                if (candidate.Value.IsFiniteOperation && !IsAdmissibleLocked(candidate.Value))
                {
                    expired.Add(candidate.Key);
                }
            }

            foreach (ulong objectId in expired)
            {
                RemoveEntryAndDescendantsLocked(objectId);
            }
        }

        private void RemoveEntryAndDescendantsLocked(ulong objectId)
        {
            if (!objectsById.TryGetValue(objectId, out Entry? entry))
            {
                return;
            }

            var children = new List<ulong>();
            foreach (KeyValuePair<ulong, Entry> candidate in objectsById)
            {
                if (candidate.Value.OwnerObjectId == objectId)
                {
                    children.Add(candidate.Key);
                }
            }

            foreach (ulong child in children)
            {
                RemoveEntryAndDescendantsLocked(child);
            }

            objectsById.Remove(objectId);
            idsByHandler.Remove(entry.Handler);
        }

        private sealed class Entry
        {
            internal Entry(
                ulong objectTypeId,
                object handler,
                ulong ownerObjectId,
                bool isFiniteOperation,
                long expiresAtMilliseconds)
            {
                ObjectTypeId = objectTypeId;
                Handler = handler;
                OwnerObjectId = ownerObjectId;
                IsFiniteOperation = isFiniteOperation;
                ExpiresAtMilliseconds = expiresAtMilliseconds;
            }

            internal ulong ObjectTypeId { get; }

            internal object Handler { get; }

            internal ulong OwnerObjectId { get; }

            internal bool IsFiniteOperation { get; }

            internal long ExpiresAtMilliseconds { get; }

            internal bool CancelDispatched { get; set; }
        }
    }
}
