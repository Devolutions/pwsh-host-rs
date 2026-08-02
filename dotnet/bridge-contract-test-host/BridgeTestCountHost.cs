using System;

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

        public long Increment(in PowerShellBridgeTestCountCallContext context)
        {
            Count = checked(Count + 1);
            return Count;
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
