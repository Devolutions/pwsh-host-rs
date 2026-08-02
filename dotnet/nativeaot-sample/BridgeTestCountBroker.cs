using System;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using Devolutions.MultiPwsh.BridgeTest;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace NativeAotFfiSample;

[GeneratedComClass]
internal sealed partial class BridgeTestCountBroker :
    IPowerShellBridgeTestCountTransport,
    IPowerShellLiveObjectBroker
{
    private readonly BridgeTestCountHost host;
    private int openedLeaseCount;

    internal BridgeTestCountBroker(long initialCount)
    {
        host = new BridgeTestCountHost(initialCount);
    }

    internal long Count => host.Count;

    internal int OpenedLeaseCount => Volatile.Read(ref openedLeaseCount);

    public int Invoke(
        ulong leaseId,
        uint generation,
        ulong objectId,
        uint memberId,
        nint input,
        int inputLength,
        nint output,
        int outputCapacity,
        out int outputLength)
    {
        CountOpenFrame(input, inputLength);
        return host.Invoke(
            leaseId,
            generation,
            objectId,
            memberId,
            input,
            inputLength,
            output,
            outputCapacity,
            out outputLength);
    }

    public int CloseLease(ulong leaseId, uint generation) => host.CloseLease(leaseId, generation);

    public void Dispose() => host.Dispose();

    private void CountOpenFrame(nint input, int inputLength)
    {
        if (input == 0 || inputLength < PowerShellBridgeWire.RequestHeaderSize)
        {
            return;
        }

        unsafe
        {
            ReadOnlySpan<byte> frame = new((void*)input, inputLength);
            if (PowerShellBridgeRequestHeader.TryRead(frame, out PowerShellBridgeRequestHeader header) &&
                header.FrameKind == PowerShellBridgeFrameKind.Open)
            {
                Interlocked.Increment(ref openedLeaseCount);
            }
        }
    }
}
