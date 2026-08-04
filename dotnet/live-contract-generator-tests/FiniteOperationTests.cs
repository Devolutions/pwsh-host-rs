#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// Focused lifecycle tests for the finite operation primitive. The bridge
/// generator tests prove the same fixed schemas compile on Host and Payload;
/// these tests prove the host-side retained state never needs reflection,
/// serialization, or a scheduler.
/// </summary>
internal static class FiniteOperationTests
{
    private static readonly Guid SchemaId = new("59E1C18D-EE83-4FC8-90E9-BE3E62D95D80");

    internal static void Run()
    {
        OwnerAndTerminalPrecedenceAreClosed();
        CopiedPagesAreBoundedAndRevalidated();
        RetentionAndExplicitCleanupAreFinite();
    }

    private static void OwnerAndTerminalPrecedenceAreClosed()
    {
        using PowerShellFiniteOperationRegistry<Page> registry = CreateRegistry(out TestClock clock, out _);
        using PowerShellFiniteOperationOwner owner = registry.CreateOwner();
        using PowerShellFiniteOperationOwner otherOwner = registry.CreateOwner();

        PowerShellFiniteOperationResult started = Start(registry, owner, clock, out PowerShellFiniteOperationLease lease);
        Require(started.Status == PowerShellFiniteOperationStatus.Active && started.OperationId.IsValid, "start creates an opaque active operation");
        Require(
            registry.TryGet(otherOwner, started.OperationId).Status == PowerShellFiniteOperationStatus.AccessDenied,
            "a different owner cannot probe an operation identifier");

        Require(
            registry.TryCancel(owner, started.OperationId).Status == PowerShellFiniteOperationStatus.Cancelled &&
            registry.TryCancel(owner, started.OperationId).Status == PowerShellFiniteOperationStatus.Cancelled,
            "cancellation is idempotent");
        Require(
            registry.TryComplete(owner, started.OperationId, [new Page(1, "late")]).Status == PowerShellFiniteOperationStatus.Cancelled,
            "recorded cancellation wins over a late completion");
        Require(lease.CancellationToken.IsCancellationRequested, "cancellation signals the host worker token");

        PowerShellFiniteOperationResult completed = Start(registry, owner, clock, out _);
        Require(
            registry.TryComplete(owner, completed.OperationId, [new Page(1, "done")]).Status == PowerShellFiniteOperationStatus.Succeeded &&
            registry.TryCancel(owner, completed.OperationId).Status == PowerShellFiniteOperationStatus.Succeeded,
            "a committed terminal outcome wins over later cancellation");

        PowerShellFiniteOperationResult timedOut = Start(
            registry,
            owner,
            clock,
            out _,
            deadline: TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(
            registry.TryCancel(owner, timedOut.OperationId).Status == PowerShellFiniteOperationStatus.TimedOut,
            "deadline wins before cancellation at the same transition");

        PowerShellFiniteOperationResult failed = Start(registry, owner, clock, out _);
        Require(
            registry.TryFail(owner, failed.OperationId, 17).Status == PowerShellFiniteOperationStatus.Failed &&
            registry.TryGet(owner, failed.OperationId).ErrorCode == 17,
            "application failure is retained as a deterministic numeric terminal outcome");
    }

    private static void CopiedPagesAreBoundedAndRevalidated()
    {
        using PowerShellFiniteOperationRegistry<Page> registry = CreateRegistry(out TestClock clock, out TestValidator validator);
        using PowerShellFiniteOperationOwner owner = registry.CreateOwner();

        var source = new Page(11, "alpha");
        PowerShellFiniteOperationResult completed = Start(registry, owner, clock, out _);
        Require(
            registry.TryComplete(owner, completed.OperationId, [source, new Page(12, "beta")]).Status == PowerShellFiniteOperationStatus.Succeeded,
            "bounded fixed-schema pages complete successfully");
        source.Label = "mutated-after-complete";

        PowerShellFinitePageReadResult<Page> first = registry.TryReadPage(
            owner,
            completed.OperationId,
            PowerShellFinitePageCursor.Start);
        Require(
            first.Operation.Status == PowerShellFiniteOperationStatus.Succeeded &&
            first.HasPage &&
            first.Page is { Value: 11, Label: "alpha" } &&
            first.NextCursor is { Index: 1 } &&
            validator.Calls == 1,
            "each page is detached and revalidated before it is returned");

        validator.Result = PowerShellFinitePageValidation.SnapshotInvalidated;
        PowerShellFinitePageReadResult<Page> invalidated = registry.TryReadPage(
            owner,
            completed.OperationId,
            first.NextCursor!.Value);
        Require(
            invalidated.Operation.Status == PowerShellFiniteOperationStatus.SnapshotInvalidated &&
            !invalidated.HasPage &&
            validator.Calls == 2,
            "snapshot invalidation becomes a deterministic terminal page outcome");

        validator.Result = PowerShellFinitePageValidation.Allowed;
        PowerShellFiniteOperationResult permissionBound = Start(registry, owner, clock, out _);
        Require(
            registry.TryComplete(owner, permissionBound.OperationId, [new Page(13, "gamma")]).Status ==
                PowerShellFiniteOperationStatus.Succeeded,
            "a separate fixed-schema page sequence completes before permission revalidation");
        validator.Result = PowerShellFinitePageValidation.PermissionChanged;
        Require(
            registry.TryReadPage(owner, permissionBound.OperationId, PowerShellFinitePageCursor.Start).Operation.Status ==
                PowerShellFiniteOperationStatus.PermissionChanged,
            "permission revision changes become a deterministic terminal page outcome");

        validator.Result = PowerShellFinitePageValidation.Allowed;
        PowerShellFiniteOperationResult oversized = Start(registry, owner, clock, out _);
        Require(
            registry.TryComplete(owner: owner, operationId: oversized.OperationId, pages: [new Page(1, new string('x', 129))]).Status ==
                PowerShellFiniteOperationStatus.BoundsExceeded,
            "an oversized copied page fails closed");

        PowerShellFiniteOperationResult pending = Start(registry, owner, clock, out _);
        Require(
            registry.TryReadPage(owner, pending.OperationId, PowerShellFinitePageCursor.Start).Operation.Status ==
                PowerShellFiniteOperationStatus.Active,
            "an active operation has no page before terminal success");
    }

    private static void RetentionAndExplicitCleanupAreFinite()
    {
        using PowerShellFiniteOperationRegistry<Page> registry = CreateRegistry(out TestClock clock, out _);
        using PowerShellFiniteOperationOwner owner = registry.CreateOwner();

        PowerShellFiniteOperationResult completed = Start(
            registry,
            owner,
            clock,
            out PowerShellFiniteOperationLease lease,
            retention: TimeSpan.FromSeconds(1));
        Require(
            registry.TryComplete(owner, completed.OperationId, [new Page(1, "retained")]).Status == PowerShellFiniteOperationStatus.Succeeded,
            "a completed operation enters its finite retention lease");

        clock.Advance(TimeSpan.FromSeconds(1));
        Require(
            registry.TryGet(owner, completed.OperationId).Status == PowerShellFiniteOperationStatus.Expired &&
            registry.TryReadPage(owner, completed.OperationId, PowerShellFinitePageCursor.Start).Operation.Status ==
                PowerShellFiniteOperationStatus.Expired,
            "terminal retention expiry clears pages and leaves an explicit tombstone");
        Require(
            registry.TryRelease(owner, completed.OperationId).Status == PowerShellFiniteOperationStatus.Released &&
            lease.CancellationToken.IsCancellationRequested &&
            registry.TryGet(owner, completed.OperationId).Status == PowerShellFiniteOperationStatus.AccessDenied,
            "explicit cleanup releases the bounded tombstone and signals its worker token");
    }

    private static PowerShellFiniteOperationRegistry<Page> CreateRegistry(
        out TestClock clock,
        out TestValidator validator)
    {
        clock = new TestClock(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));
        validator = new TestValidator();
        return new PowerShellFiniteOperationRegistry<Page>(
            new PowerShellFinitePageContract<Page>(
                SchemaId,
                maximumPages: 4,
                maximumItemsPerPage: 1,
                maximumPageBytes: 128,
                new PageCodec(),
                validator),
            maximumOperations: 16,
            timeProvider: clock);
    }

    private static PowerShellFiniteOperationResult Start(
        PowerShellFiniteOperationRegistry<Page> registry,
        PowerShellFiniteOperationOwner owner,
        TestClock clock,
        out PowerShellFiniteOperationLease lease,
        TimeSpan? deadline = null,
        TimeSpan? retention = null)
    {
        PowerShellFiniteOperationResult result = registry.TryStart(
            owner,
            new PowerShellFiniteOperationBinding(SchemaId, snapshotRevision: 1, permissionRevision: 1),
            new PowerShellFiniteOperationOptions(
                deadline ?? TimeSpan.FromMinutes(1),
                retention ?? TimeSpan.FromMinutes(1)),
            out lease);
        Require(result.Status == PowerShellFiniteOperationStatus.Active, "finite operation start succeeds");
        return result;
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Finite operation test failed: {description}.");
        }
    }

    private sealed class Page
    {
        internal Page(int value, string label)
        {
            Value = value;
            Label = label;
        }

        internal int Value { get; }

        internal string Label { get; set; }
    }

    private sealed class PageCodec : IPowerShellFinitePageCodec<Page>
    {
        public bool TryCopy(Page source, out Page copy, out int itemCount, out int byteCount)
        {
            copy = null!;
            itemCount = 0;
            byteCount = 0;
            if (source is null || source.Label is null)
            {
                return false;
            }

            byteCount = checked(sizeof(int) + Encoding.UTF8.GetByteCount(source.Label));
            itemCount = 1;
            copy = new Page(source.Value, source.Label);
            return true;
        }
    }

    private sealed class TestValidator : IPowerShellFinitePageAccessValidator
    {
        internal int Calls { get; private set; }

        internal PowerShellFinitePageValidation Result { get; set; }

        public PowerShellFinitePageValidation Validate(in PowerShellFiniteOperationBinding binding)
        {
            Calls++;
            return Result;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now;

        internal TestClock(DateTimeOffset now) => this.now = now;

        public override DateTimeOffset GetUtcNow() => now;

        internal void Advance(TimeSpan value) => now += value;
    }
}
