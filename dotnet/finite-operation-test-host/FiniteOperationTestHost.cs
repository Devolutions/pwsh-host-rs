#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Devolutions.PowerShell.Ffi.LiveObjects;
using Devolutions.PowerShell.Ffi.LiveObjects.FiniteOperations;

namespace Devolutions.MultiPwsh.FiniteOperationTest;

/// <summary>
/// Acceptance-only host for the fixed finite-operation contract. Its three modes
/// prove a completed copied report, an idempotently cancelled pending job, and
/// a deliberately short retained terminal result;
/// it never accepts an application target, script, schema, or callback.
/// </summary>
public sealed class FiniteOperationTestHost : IDisposable
{
    private readonly Handler handler;
    private readonly PowerShellBridgeTestFiniteOperationDispatcher dispatcher;

    public FiniteOperationTestHost()
    {
        handler = new Handler();
        dispatcher = new PowerShellBridgeTestFiniteOperationDispatcher(handler, new Authorizer());
    }

    public IPowerShellBridgeDispatcher Dispatcher => dispatcher;

    public void InvalidateSnapshot() => handler.InvalidateSnapshot();

    public void Dispose()
    {
        dispatcher.Dispose();
        handler.Dispose();
    }

    private sealed class Handler :
        IPowerShellBridgeTestFiniteOperationBridgeHandler,
        IPowerShellFinitePageAccessValidator,
        IDisposable
    {
        private static readonly Guid SchemaId = new("4D67D86E-5193-473F-98C3-90E786A5FC3D");

        private readonly PowerShellFiniteOperationRegistry<Page> operations;
        private readonly PowerShellFiniteOperationOwner owner;
        private long snapshotRevision = 1;
        private long permissionRevision = 1;

        internal Handler()
        {
            operations = new PowerShellFiniteOperationRegistry<Page>(
                new PowerShellFinitePageContract<Page>(
                    SchemaId,
                    maximumPages: 2,
                    maximumItemsPerPage: 1,
                    maximumPageBytes: 64,
                    new PageCodec(),
                    this),
                maximumOperations: 8);
            owner = operations.CreateOwner();
        }

        public FiniteOperationTicketValue Start(in PowerShellBridgeTestFiniteOperationCallContext context, int mode)
        {
            if (mode is < 1 or > 3)
            {
                return Ticket(default, PowerShellFiniteOperationStatus.InvalidArgument);
            }

            PowerShellFiniteOperationResult started = operations.TryStart(
                owner,
                new PowerShellFiniteOperationBinding(
                    SchemaId,
                    checked((ulong)Volatile.Read(ref snapshotRevision)),
                    checked((ulong)Volatile.Read(ref permissionRevision))),
                new PowerShellFiniteOperationOptions(
                    TimeSpan.FromSeconds(30),
                    mode == 3 ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1)),
                out _);
            if (started.Status != PowerShellFiniteOperationStatus.Active)
            {
                return Ticket(started);
            }

            if (mode is 1 or 3)
            {
                _ = operations.TryComplete(
                    owner,
                    started.OperationId,
                    [new Page(0, "alpha"), new Page(1, "beta")]);
            }

            return Ticket(operations.TryGet(owner, started.OperationId));
        }

        public FiniteOperationPageReadValue ReadPage(
            in PowerShellBridgeTestFiniteOperationCallContext context,
            Guid operationId,
            int cursor)
        {
            if (!PowerShellFinitePageCursor.TryCreate(cursor, out PowerShellFinitePageCursor pageCursor))
            {
                return new FiniteOperationPageReadValue(
                    (int)PowerShellFiniteOperationStatus.InvalidCursor,
                    false,
                    -1,
                    -1,
                    Array.Empty<string>());
            }

            PowerShellFinitePageReadResult<Page> page = operations.TryReadPage(
                owner,
                PowerShellFiniteOperationId.FromValue(operationId),
                pageCursor);
            return new FiniteOperationPageReadValue(
                (int)page.Operation.Status,
                page.HasPage,
                page.NextCursor?.Index ?? -1,
                page.HasPage ? page.Page!.Ordinal : -1,
                page.HasPage ? [page.Page!.Label] : Array.Empty<string>());
        }

        public FiniteOperationTicketValue Cancel(
            in PowerShellBridgeTestFiniteOperationCallContext context,
            Guid operationId) =>
            Ticket(operations.TryCancel(owner, PowerShellFiniteOperationId.FromValue(operationId)));

        public int Release(
            in PowerShellBridgeTestFiniteOperationCallContext context,
            Guid operationId) =>
            (int)operations.TryRelease(owner, PowerShellFiniteOperationId.FromValue(operationId)).Status;

        public void Release(in PowerShellBridgeTestFiniteOperationCallContext context)
        {
        }

        public PowerShellFinitePageValidation Validate(in PowerShellFiniteOperationBinding binding)
        {
            if (binding.SnapshotRevision != checked((ulong)Volatile.Read(ref snapshotRevision)))
            {
                return PowerShellFinitePageValidation.SnapshotInvalidated;
            }

            return binding.PermissionRevision == checked((ulong)Volatile.Read(ref permissionRevision))
                ? PowerShellFinitePageValidation.Allowed
                : PowerShellFinitePageValidation.PermissionChanged;
        }

        internal void InvalidateSnapshot() => Interlocked.Increment(ref snapshotRevision);

        public void Dispose()
        {
            owner.Dispose();
            operations.Dispose();
        }

        private FiniteOperationTicketValue Ticket(PowerShellFiniteOperationResult result) =>
            Ticket(result.OperationId, result.Status);

        private FiniteOperationTicketValue Ticket(
            PowerShellFiniteOperationId operationId,
            PowerShellFiniteOperationStatus status) =>
            new(
                operationId.Value,
                (int)status,
                Volatile.Read(ref snapshotRevision),
                Volatile.Read(ref permissionRevision));
    }

    private sealed class Authorizer : IPowerShellBridgeTestFiniteOperationAuthorizer
    {
        public bool IsAuthorized(in PowerShellBridgeTestFiniteOperationCallContext context) => true;
    }

    private sealed class Page
    {
        internal Page(int ordinal, string label)
        {
            Ordinal = ordinal;
            Label = label;
        }

        internal int Ordinal { get; }

        internal string Label { get; }
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

            itemCount = 1;
            byteCount = checked(sizeof(int) + Encoding.UTF8.GetByteCount(source.Label));
            copy = new Page(source.Ordinal, source.Label);
            return true;
        }
    }
}
