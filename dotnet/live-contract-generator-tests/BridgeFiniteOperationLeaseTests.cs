#nullable enable

using Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// Exercises the runtime guarantees behind a generated finite-operation
/// declaration without requiring a payload or PowerShell process.
/// </summary>
internal static class BridgeFiniteOperationLeaseTests
{
    internal static void Run()
    {
        VerifyOwnerBindingAndCrossTableRejection();
        VerifyCancelAdmissionIsFirstWins();
        VerifyExpiryRemovesAndReissuesHandles();
        VerifyExpiredOperationsReclaimCapacity();
        VerifyIssuedOperationBudget();
    }

    private static void VerifyOwnerBindingAndCrossTableRejection()
    {
        var table = new PowerShellBridgeLeaseTable();
        Open(table, out ulong leaseId, out uint generation, out ulong rootId);
        var handler = new object();
        ulong operationId = table.RegisterFiniteOperation(leaseId, generation, rootId, 2, handler, 60_000);
        if (operationId == 0 ||
            operationId == rootId ||
            operationId == 1 ||
            operationId != table.RegisterFiniteOperation(leaseId, generation, rootId, 2, handler, 60_000) ||
            !table.TryAdmit(leaseId, generation, operationId, out PowerShellBridgeAdmission admitted) ||
            admitted.ObjectTypeId != 2 ||
            !ReferenceEquals(admitted.Handler, handler))
        {
            throw new InvalidOperationException("A finite operation must receive one active nonsequential owner-bound handle.");
        }

        var other = new PowerShellBridgeLeaseTable();
        if (other.TryAdmit(leaseId, generation, operationId, out _))
        {
            throw new InvalidOperationException("A finite operation must not resolve through another lease table.");
        }

        if (!table.TryRelease(leaseId, generation, rootId) ||
            table.TryAdmit(leaseId, generation, operationId, out _) ||
            table.TryBeginFiniteOperationCancel(leaseId, generation, operationId) != PowerShellBridgeFiniteOperationCancelResult.Invalid)
        {
            throw new InvalidOperationException("Releasing an owner must invalidate each finite-operation descendant.");
        }

        if (table.Close(leaseId, generation) != PowerShellBridgeStatus.Success)
        {
            throw new InvalidOperationException("A finite-operation lease did not close.");
        }
    }

    private static void VerifyCancelAdmissionIsFirstWins()
    {
        var table = new PowerShellBridgeLeaseTable();
        Open(table, out ulong leaseId, out uint generation, out ulong rootId);
        ulong operationId = table.RegisterFiniteOperation(leaseId, generation, rootId, 2, new object(), 60_000);
        int invokeCount = 0;
        int alreadyCancelledCount = 0;
        int invalidCount = 0;

        Parallel.For(0, 32, _ =>
        {
            switch (table.TryBeginFiniteOperationCancel(leaseId, generation, operationId))
            {
                case PowerShellBridgeFiniteOperationCancelResult.InvokeHandler:
                    Interlocked.Increment(ref invokeCount);
                    break;
                case PowerShellBridgeFiniteOperationCancelResult.AlreadyCancelled:
                    Interlocked.Increment(ref alreadyCancelledCount);
                    break;
                default:
                    Interlocked.Increment(ref invalidCount);
                    break;
            }
        });

        if (invokeCount != 1 || alreadyCancelledCount != 31 || invalidCount != 0)
        {
            throw new InvalidOperationException("Finite-operation cancellation must dispatch at most one handler.");
        }

        if (table.Close(leaseId, generation) != PowerShellBridgeStatus.Success ||
            table.TryBeginFiniteOperationCancel(leaseId, generation, operationId) != PowerShellBridgeFiniteOperationCancelResult.Invalid)
        {
            throw new InvalidOperationException("A closed finite operation must reject late cancellation.");
        }
    }

    private static void VerifyExpiryRemovesAndReissuesHandles()
    {
        var table = new PowerShellBridgeLeaseTable();
        Open(table, out ulong leaseId, out uint generation, out ulong rootId);
        var handler = new object();
        ulong expired = table.RegisterFiniteOperation(leaseId, generation, rootId, 2, handler, 1);
        Thread.Sleep(25);
        if (table.TryAdmit(leaseId, generation, expired, out _))
        {
            throw new InvalidOperationException("A finite operation must reject admission after its deadline.");
        }

        ulong replacement = table.RegisterFiniteOperation(leaseId, generation, rootId, 2, handler, 60_000);
        if (replacement == 0 || replacement == expired || !table.TryAdmit(leaseId, generation, replacement, out _))
        {
            throw new InvalidOperationException("An expired finite operation must be tombstoned before its handler can be reissued.");
        }

        if (table.Close(leaseId, generation) != PowerShellBridgeStatus.Success)
        {
            throw new InvalidOperationException("The finite-operation expiry lease did not close.");
        }
    }

    private static void VerifyIssuedOperationBudget()
    {
        var table = new PowerShellBridgeLeaseTable();
        Open(table, out ulong leaseId, out uint generation, out ulong rootId);
        var issued = new HashSet<ulong>();
        for (int index = 0; index < PowerShellBridgeLeaseTable.MaximumFiniteOperationsPerLease; index++)
        {
            ulong operationId = table.RegisterFiniteOperation(leaseId, generation, rootId, 2, new object(), 60_000);
            if (operationId == 0 || !issued.Add(operationId) || !table.TryRelease(leaseId, generation, operationId))
            {
                throw new InvalidOperationException("Finite-operation handles must remain unique until the lease closes.");
            }

        }

        if (table.RegisterFiniteOperation(leaseId, generation, rootId, 2, new object(), 60_000) != 0)
        {
            throw new InvalidOperationException("A lease must reject finite-operation identities past its issued-handle budget.");
        }

        if (table.Close(leaseId, generation) != PowerShellBridgeStatus.Success)
        {
            throw new InvalidOperationException("The finite-operation budget lease did not close.");
        }
    }

    private static void VerifyExpiredOperationsReclaimCapacity()
    {
        var table = new PowerShellBridgeLeaseTable();
        Open(table, out ulong leaseId, out uint generation, out ulong rootId);
        for (int index = 0; index < PowerShellBridgeLeaseTable.MaximumObjectsPerLease - 1; index++)
        {
            if (table.RegisterFiniteOperation(leaseId, generation, rootId, 2, new object(), 200) == 0)
            {
                throw new InvalidOperationException("The finite-operation capacity fixture could not fill the live-object table.");
            }
        }

        Thread.Sleep(225);
        if (table.RegisterFiniteOperation(leaseId, generation, rootId, 2, new object(), 60_000) == 0)
        {
            throw new InvalidOperationException("Expired finite operations must be reclaimed before live-object capacity is checked.");
        }

        if (table.Close(leaseId, generation) != PowerShellBridgeStatus.Success)
        {
            throw new InvalidOperationException("The finite-operation capacity lease did not close.");
        }
    }

    private static void Open(PowerShellBridgeLeaseTable table, out ulong leaseId, out uint generation, out ulong rootId)
    {
        if (table.TryOpen(1, new object(), out leaseId, out generation, out rootId) != PowerShellBridgeStatus.Success)
        {
            throw new InvalidOperationException("The finite-operation fixture could not open its root lease.");
        }
    }
}
