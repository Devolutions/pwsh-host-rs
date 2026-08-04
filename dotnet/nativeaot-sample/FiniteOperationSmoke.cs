using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Devolutions.PowerShell.Ffi;

namespace NativeAotFfiSample;

internal static class FiniteOperationSmoke
{
    internal static bool Run()
    {
        Guid schemaId = new("D3C1B4E9-2F40-49AD-88EC-26E2A09B36F2");
        var validator = new TestAccessValidator();
        var contract = new PowerShellFinitePageContract<TestPage>(
            schemaId,
            maximumPages: 2,
            maximumItemsPerPage: 2,
            maximumPageBytes: 64,
            new TestPageCodec(),
            validator);
        using var registry = new PowerShellFiniteOperationRegistry<TestPage>(
            contract,
            maximumOperations: 4);
        using PowerShellFiniteOperationOwner owner = registry.CreateOwner();
        using PowerShellFiniteOperationOwner otherOwner = registry.CreateOwner();
        PowerShellFiniteOperationBinding binding = new(
            schemaId,
            snapshotRevision: 1,
            permissionRevision: 1);
        PowerShellFiniteOperationOptions options = new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        PowerShellFiniteOperationResult rejected = registry.TryStart(
            owner,
            binding,
            options,
            out PowerShellFiniteOperationLease rejectedLease);
        using (rejectedLease)
        {
            PowerShellFiniteOperationResult rejectedCompletion = registry.TryComplete(
                    owner,
                    rejected.OperationId,
                    [
                        new TestPage(0, "one"),
                        new TestPage(1, "two"),
                        new TestPage(2, "three"),
                    ]);
            if (rejected.Status != PowerShellFiniteOperationStatus.Active ||
                    rejectedCompletion.Status != PowerShellFiniteOperationStatus.Rejected ||
                    !rejectedCompletion.IsTerminal ||
                    !rejectedLease.CancellationToken.IsCancellationRequested ||
                    registry.TryRelease(owner, rejected.OperationId).Status != PowerShellFiniteOperationStatus.Released)
            {
                    Console.Error.WriteLine("NativeAOT finite operations did not fail closed for an over-bound completion.");
                    return false;
            }
        }

        PowerShellFiniteOperationResult started = registry.TryStart(
            owner,
            binding,
            options,
            out PowerShellFiniteOperationLease lease);
        using (lease)
        {
            if (started.Status != PowerShellFiniteOperationStatus.Active ||
                    !started.OperationId.IsValid ||
                    lease.CancellationToken.IsCancellationRequested ||
                    registry.TryGetStatus(otherOwner, started.OperationId).Status != PowerShellFiniteOperationStatus.AccessDenied)
            {
                    Console.Error.WriteLine("NativeAOT finite operations did not reject cross-owner access.");
                    return false;
            }

            TestPage firstSource = new(0, "first");
            PowerShellFiniteOperationResult completed = registry.TryComplete(
                    owner,
                    started.OperationId,
                    [
                        firstSource,
                        new TestPage(1, "second"),
                    ]);
            PowerShellFinitePageReadResult<TestPage> firstPage = registry.TryReadPage(
                owner,
                started.OperationId,
                PowerShellFinitePageCursor.Start);
            if (completed.Status != PowerShellFiniteOperationStatus.Succeeded ||
                !completed.IsTerminal ||
                !lease.CancellationToken.IsCancellationRequested ||
                !firstPage.HasPage ||
                firstPage.Page is null ||
                firstPage.Page.Name != "first" ||
                firstPage.NextCursor is null)
            {
                Console.Error.WriteLine("NativeAOT finite operations did not retain copied cursor pages.");
                return false;
            }

            validator.Validation = PowerShellFinitePageValidation.SnapshotInvalidated;
            PowerShellFinitePageReadResult<TestPage> invalidatedPage = registry.TryReadPage(
                owner,
                started.OperationId,
                firstPage.NextCursor.Value);
            if (invalidatedPage.HasPage ||
                invalidatedPage.Operation.Status != PowerShellFiniteOperationStatus.SnapshotInvalidated ||
                registry.TryGetStatus(owner, started.OperationId).Status != PowerShellFiniteOperationStatus.SnapshotInvalidated ||
                registry.TryRelease(owner, started.OperationId).Status != PowerShellFiniteOperationStatus.Released)
            {
                Console.Error.WriteLine("NativeAOT finite operations did not terminalize a stale snapshot.");
                return false;
            }
        }

        validator.Validation = PowerShellFinitePageValidation.Allowed;
        PowerShellFiniteOperationResult cancellable = registry.TryStart(
            owner,
            binding,
            options,
            out PowerShellFiniteOperationLease cancellableLease);
        using (cancellableLease)
        {
            if (cancellable.Status != PowerShellFiniteOperationStatus.Active ||
                registry.TryCancel(owner, cancellable.OperationId).Status != PowerShellFiniteOperationStatus.Cancelled ||
                registry.TryCancel(owner, cancellable.OperationId).Status != PowerShellFiniteOperationStatus.Cancelled ||
                !cancellableLease.CancellationToken.IsCancellationRequested ||
                registry.TryRelease(owner, cancellable.OperationId).Status != PowerShellFiniteOperationStatus.Released)
            {
                Console.Error.WriteLine("NativeAOT finite operations did not preserve first-wins cancellation.");
                return false;
            }
        }

        PowerShellFiniteOperationResult expiring = registry.TryStart(
            owner,
            binding,
            new PowerShellFiniteOperationOptions(
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1)),
            out PowerShellFiniteOperationLease expiringLease);
        using (expiringLease)
        {
            Thread.Sleep(20);
            if (expiring.Status != PowerShellFiniteOperationStatus.Active ||
                !expiringLease.CancellationToken.IsCancellationRequested ||
                registry.TryGetStatus(owner, expiring.OperationId).Status != PowerShellFiniteOperationStatus.Expired)
            {
                Console.Error.WriteLine("NativeAOT finite operations did not enforce deadline and retention expiry.");
                return false;
            }
        }

        return registry.Count == 0 && VerifyAggregateQuota();
    }

    private static bool VerifyAggregateQuota()
    {
        Guid schemaId = new("C35443D8-6A31-4C5E-AB9D-E559A5704D0D");
        var validator = new TestAccessValidator();
        var contract = new PowerShellFinitePageContract<TestPage>(
            schemaId,
            maximumPages: 1,
            maximumItemsPerPage: 1,
            maximumPageBytes: 1_048_576,
            new QuotaPageCodec(),
            validator);
        using var registry = new PowerShellFiniteOperationRegistry<TestPage>(
            contract,
            maximumOperations: 32);
        using PowerShellFiniteOperationOwner owner = registry.CreateOwner();
        PowerShellFiniteOperationBinding binding = new(schemaId, 1, 1);
        PowerShellFiniteOperationOptions options = new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        var operationIds = new List<PowerShellFiniteOperationId>();
        var leases = new List<PowerShellFiniteOperationLease>();

        for (int index = 0; index < 15; index++)
        {
            PowerShellFiniteOperationResult started = registry.TryStart(
                owner,
                binding,
                options,
                out PowerShellFiniteOperationLease lease);
            leases.Add(lease);
            if (started.Status != PowerShellFiniteOperationStatus.Active ||
                registry.TryComplete(
                    owner,
                    started.OperationId,
                    [new TestPage(index, "quota")]).Status != PowerShellFiniteOperationStatus.Succeeded)
            {
                return false;
            }

            operationIds.Add(started.OperationId);
        }

        PowerShellFiniteOperationResult reentrant = registry.TryStart(
            owner,
            binding,
            options,
            out PowerShellFiniteOperationLease reentrantLease);
        leases.Add(reentrantLease);
        PowerShellFiniteOperationResult outer = registry.TryStart(
            owner,
            binding,
            options,
            out PowerShellFiniteOperationLease outerLease);
        leases.Add(outerLease);
        PowerShellFiniteOperationResult reentrantCompletion = null;
        PowerShellFiniteOperationResult outerCompletion;
        using (outerLease.CancellationToken.Register(() =>
            reentrantCompletion = registry.TryComplete(
                owner,
                reentrant.OperationId,
                [new TestPage(15, "reentrant")])))
        {
            outerCompletion = registry.TryComplete(
                owner,
                outer.OperationId,
                [new TestPage(16, "outer")]);
        }

        if (reentrant.Status != PowerShellFiniteOperationStatus.Active ||
            outer.Status != PowerShellFiniteOperationStatus.Active ||
            outerCompletion.Status != PowerShellFiniteOperationStatus.Succeeded ||
            reentrantCompletion is null ||
            reentrantCompletion.Status != PowerShellFiniteOperationStatus.Rejected ||
            !reentrantLease.CancellationToken.IsCancellationRequested ||
            !outerLease.CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        operationIds.Add(reentrant.OperationId);
        operationIds.Add(outer.OperationId);
        foreach (PowerShellFiniteOperationId operationId in operationIds)
        {
            if (registry.TryRelease(owner, operationId).Status != PowerShellFiniteOperationStatus.Released)
            {
                return false;
            }
        }

        foreach (PowerShellFiniteOperationLease lease in leases)
        {
            lease.Dispose();
        }

        return registry.Count == 0;
    }

    private sealed class TestPage
    {
        internal TestPage(int ordinal, string name)
        {
            Ordinal = ordinal;
            Name = name;
        }

        internal int Ordinal { get; }

        internal string Name { get; }
    }

    private sealed class TestPageCodec : IPowerShellFinitePageCodec<TestPage>
    {
        public bool TryCopy(
            TestPage source,
            out TestPage copy,
            out int itemCount,
            out int byteCount)
        {
            copy = null!;
            itemCount = 0;
            byteCount = 0;
            if (source is null || source.Name is null)
            {
                return false;
            }

            itemCount = 1;
            byteCount = checked(sizeof(int) + Encoding.UTF8.GetByteCount(source.Name));
            copy = new TestPage(source.Ordinal, source.Name);
            return true;
        }
    }

    private sealed class QuotaPageCodec : IPowerShellFinitePageCodec<TestPage>
    {
        public bool TryCopy(
            TestPage source,
            out TestPage copy,
            out int itemCount,
            out int byteCount)
        {
            copy = new TestPage(source.Ordinal, source.Name);
            itemCount = 1;
            byteCount = 1_048_576;
            return true;
        }
    }

    private sealed class TestAccessValidator : IPowerShellFinitePageAccessValidator
    {
        internal PowerShellFinitePageValidation Validation { get; set; } =
            PowerShellFinitePageValidation.Allowed;

        public PowerShellFinitePageValidation Validate(in PowerShellFiniteOperationBinding binding)
        {
            return Validation;
        }
    }
}
