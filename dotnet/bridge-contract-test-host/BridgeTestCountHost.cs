using System;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.BridgeTest;

/// <summary>Acceptance-only generated bridge host used by the NativeAOT sample.</summary>
public sealed class BridgeTestCountHost : IDisposable
{
    private readonly Handler handler;
    private readonly PowerShellBridgeTestCountDispatcher dispatcher;

    public BridgeTestCountHost(long initialCount)
    {
        handler = new Handler(initialCount);
        dispatcher = new PowerShellBridgeTestCountDispatcher(handler, new Authorizer());
    }

    public long Count => handler.Count;

    public long LastReportedCount => handler.LastReportedCount;

    public long LastReliableReportedCount => handler.LastReliableReportedCount;

    public int ReliableReportCount => handler.ReliableReportCount;

    public IPowerShellBridgeDispatcher Dispatcher => dispatcher;

    public int Invoke(
        ulong leaseId,
        uint generation,
        ulong objectId,
        uint memberId,
        nint input,
        int inputLength,
        nint output,
        int outputCapacity,
        out int outputLength) =>
        dispatcher.Invoke(
            leaseId,
            generation,
            objectId,
            memberId,
            input,
            inputLength,
            output,
            outputCapacity,
            out outputLength);

    public int CloseLease(ulong leaseId, uint generation) => dispatcher.CloseLease(leaseId, generation);

    public void Dispose() => dispatcher.Dispose();

    private sealed class Handler : IPowerShellBridgeTestCountBridgeHandler
    {
        internal Handler(long count) => Count = count;

        internal long Count { get; private set; }

        public long GetCount(in PowerShellBridgeTestCountCallContext context) => Count;

        public IPowerShellBridgeTestJobBridgeHandler StartJob(in PowerShellBridgeTestCountCallContext context) =>
            new JobHandler();

        public long Increment(in PowerShellBridgeTestCountCallContext context)
        {
            Count = checked(Count + 1);
            return Count;
        }

        public long Add(in PowerShellBridgeTestCountCallContext context, int value)
        {
            if (value is < -128 or > 128)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            return checked(Count + value);
        }

        public long LastReportedCount { get; private set; }

        public void OnReport(in PowerShellBridgeTestCountCallContext context, long value)
        {
            LastReportedCount = value;
        }

        public long LastReliableReportedCount { get; private set; }

        public int ReliableReportCount { get; private set; }

        public void OnReportReliable(in PowerShellBridgeTestCountCallContext context, long value)
        {
            LastReliableReportedCount = value;
            ReliableReportCount = checked(ReliableReportCount + 1);
        }

        public void Release(in PowerShellBridgeTestCountCallContext context)
        {
        }
    }

    private sealed class JobHandler : IPowerShellBridgeTestJobBridgeHandler
    {
        private static readonly long[] Results = [10, 20, 30];
        private static readonly PowerShellBridgeTestGridColumnValue[] Columns =
        [
            new("Sequence", PowerShellBridgeTestGridColumnType.Int64),
            new("Value", PowerShellBridgeTestGridColumnType.Int64),
            new("Label", PowerShellBridgeTestGridColumnType.Utf8String),
        ];
        private PowerShellBridgeTestJobState state = PowerShellBridgeTestJobState.Running;

        public PowerShellBridgeTestJobStatusValue GetStatus(in PowerShellBridgeTestCountCallContext context) =>
            new(state, state != PowerShellBridgeTestJobState.Running, Results.Length);

        public void Cancel(in PowerShellBridgeTestCountCallContext context)
        {
            if (state == PowerShellBridgeTestJobState.Running)
            {
                state = PowerShellBridgeTestJobState.Cancelled;
            }
        }

        public PowerShellBridgeTestJobPageValue ReadResults(
            in PowerShellBridgeTestCountCallContext context,
            int cursor)
        {
            if (cursor < 0 || cursor > Results.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor));
            }

            int take = Math.Min(2, Results.Length - cursor);
            var rows = new PowerShellBridgeTestGridRowValue[take];
            for (int index = 0; index < take; index++)
            {
                long value = Results[cursor + index];
                rows[index] = new PowerShellBridgeTestGridRowValue(cursor + index, value, $"result-{value}");
            }

            int next = cursor + take;
            return new PowerShellBridgeTestJobPageValue(
                Columns,
                rows,
                next,
                next == Results.Length,
                false,
                Results.Length);
        }

        public void Release(in PowerShellBridgeTestCountCallContext context)
        {
        }
    }

    private sealed class Authorizer : IPowerShellBridgeTestCountAuthorizer
    {
        public bool IsAuthorized(in PowerShellBridgeTestCountCallContext context) => true;
    }
}
