using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;
using GeneratedContract = Devolutions.MultiPwsh.LiveContracts.ISessionCreatorLiveContractGenerated;
using GeneratedHostAdapter = Devolutions.MultiPwsh.LiveContracts.SessionCreatorLiveContractHostAdapter;
using GeneratedHostModel = Devolutions.MultiPwsh.LiveContracts.SessionCreatorLiveContractHostModel;

namespace NativeAotFfiSample;

[GeneratedComClass]
internal sealed partial class SessionCreatorBroker : IPowerShellLiveObjectBrokerContract, IPowerShellLiveObjectBroker
{
    private const int EInvalidArg = unchecked((int)0x80070057);
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int EBufferTooSmall = unchecked((int)0x8007007A);
    private readonly object gate = new();
    private readonly ulong leaseId;
    private readonly GeneratedHostModel model = new();
    private uint generation = 1;
    private bool closed;

    internal SessionCreatorBroker()
    {
        Span<byte> random = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(random);
        leaseId = BinaryPrimitives.ReadUInt64LittleEndian(random);
        if (leaseId == 0)
        {
            leaseId = 1;
        }
    }

    public int Invoke(ulong requestedLeaseId, uint requestedGeneration, ulong objectId, uint memberId, nint input, int inputLength, nint output, int outputCapacity, out int outputLength)
    {
        outputLength = 0;
        lock (gate)
        {
            if (!TryRead(input, inputLength, out byte inputTag, out ReadOnlySpan<byte> inputValue))
            {
                return EInvalidArg;
            }

            if (closed)
            {
                return EAccessDenied;
            }

            if (requestedLeaseId == 0 && requestedGeneration == 0 && objectId == 0 && memberId == 0 &&
                inputTag == PowerShellLiveObjectBrokerWire.Null)
            {
                byte[] lease = PowerShellLiveObjectBrokerWire.EncodeString($"{leaseId}:{generation}:{GeneratedContract.ContractHash}");
                outputLength = lease.Length;
                if (output == 0 || outputCapacity < lease.Length)
                {
                    return EBufferTooSmall;
                }

                Marshal.Copy(lease, 0, output, lease.Length);
                return 0;
            }

            if (requestedLeaseId != leaseId || requestedGeneration != generation)
            {
                return EAccessDenied;
            }

            int status = GeneratedHostAdapter.TryInvoke(
                model,
                objectId,
                memberId,
                inputTag,
                inputValue,
                output == 0 ? 0 : outputCapacity,
                out byte[] result,
                out outputLength);
            if (status != 0)
            {
                return status;
            }

            if (output == 0 || outputCapacity < outputLength)
            {
                return EBufferTooSmall;
            }

            Marshal.Copy(result, 0, output, result.Length);
            return 0;
        }
    }

    public int CloseLease(ulong requestedLeaseId, uint requestedGeneration)
    {
        lock (gate)
        {
            if (closed || requestedLeaseId != leaseId || requestedGeneration != generation)
            {
                return EAccessDenied;
            }

            closed = true;
            generation++;
            return 0;
        }
    }

    internal void EndLease()
    {
        lock (gate)
        {
            if (!closed)
            {
                closed = true;
                generation++;
            }
        }
    }

    internal bool VerifyRawRejections()
    {
        byte[] nullValue = PowerShellLiveObjectBrokerWire.Encode(PowerShellLiveObjectBrokerWire.Null, []);
        byte[] malformedUtf8 = PowerShellLiveObjectBrokerWire.Encode(PowerShellLiveObjectBrokerWire.Utf8String, [0xC3, 0x28]);
        byte[] validAdd = PowerShellLiveObjectBrokerWire.EncodeString("preflight");
        byte[] invalidProtocol = (byte[])nullValue.Clone();
        invalidProtocol[0] = 2;
        byte[] invalidLength = (byte[])nullValue.Clone();
        invalidLength[4] = 1;
        byte[] negativeLength = (byte[])nullValue.Clone();
        negativeLength[4] = byte.MaxValue;
        negativeLength[5] = byte.MaxValue;
        negativeLength[6] = byte.MaxValue;
        negativeLength[7] = byte.MaxValue;
        byte[] oversizeLength = (byte[])nullValue.Clone();
        oversizeLength[4] = 1;
        oversizeLength[5] = 1;
        byte[] unknownTag = PowerShellLiveObjectBrokerWire.Encode(99, []);

        lock (gate)
        {
            uint activeGeneration = generation;
            int count = model.Count;
            bool acceptedBootstrap = CallRaw(0, 0, 0, 0, nullValue, 264, false, out int bootstrapLength) == 0 &&
                bootstrapLength > PowerShellLiveObjectBrokerWire.HeaderSize;
            bool rejectedNullOutput = CallRaw(
                leaseId,
                generation,
                GeneratedContract.Object1,
                GeneratedContract.Member1,
                validAdd,
                0,
                true,
                out int requiredAddLength) == EBufferTooSmall &&
                requiredAddLength == PowerShellLiveObjectBrokerWire.HeaderSize + sizeof(ulong);
            bool rejected = CallRaw(leaseId, generation, GeneratedContract.Object1, 999, nullValue, 264) == EInvalidArg &&
                CallRaw(leaseId, generation, ulong.MaxValue, GeneratedContract.Member10, nullValue, 264) == EInvalidArg &&
                CallRaw(leaseId, generation, GeneratedContract.Object1, GeneratedContract.Member1, malformedUtf8, 264) == EInvalidArg &&
                CallRaw(leaseId, generation, GeneratedContract.Object1, GeneratedContract.Member1, invalidProtocol, 264) == EInvalidArg &&
                CallRaw(leaseId, generation, GeneratedContract.Object1, GeneratedContract.Member1, invalidLength, 264) == EInvalidArg &&
                CallRaw(leaseId, generation, GeneratedContract.Object1, GeneratedContract.Member1, negativeLength, 264) == EInvalidArg &&
                CallRaw(leaseId, generation, GeneratedContract.Object1, GeneratedContract.Member1, oversizeLength, 264) == EInvalidArg &&
                CallRaw(leaseId, generation, GeneratedContract.Object1, GeneratedContract.Member1, unknownTag, 264) == EInvalidArg &&
                CallRaw(leaseId, generation, GeneratedContract.Object1, GeneratedContract.Member1, validAdd, PowerShellLiveObjectBrokerWire.HeaderSize + sizeof(ulong) - 1) == EBufferTooSmall &&
                CallRaw(leaseId, generation + 1, GeneratedContract.Object1, GeneratedContract.Member1, nullValue, 264) == EAccessDenied &&
                CallRaw(leaseId + 1, generation, GeneratedContract.Object1, GeneratedContract.Member1, nullValue, 264) == EAccessDenied &&
                CallRaw(leaseId, generation, GeneratedContract.Object2, GeneratedContract.Member4, PowerShellLiveObjectBrokerWire.EncodeInt32(0), 264) == GeneratedHostAdapter.EBounds &&
                CallRaw(leaseId, generation, GeneratedContract.Object2, GeneratedContract.Member3, nullValue, 0) == EBufferTooSmall &&
                model.Count == count;

            bool closedOnce = CloseLease(leaseId, activeGeneration) == 0;
            int repeatedCloseStatus = CloseLease(leaseId, activeGeneration);
            int staleInvokeStatus = CallRaw(leaseId, activeGeneration, GeneratedContract.Object1, GeneratedContract.Member1, nullValue, 264);
            bool closedRepeatedly = repeatedCloseStatus == EAccessDenied && staleInvokeStatus == EAccessDenied;
            EndLease();
            EndLease();
            return acceptedBootstrap && rejectedNullOutput && rejected && closedOnce && closedRepeatedly;
        }
    }

    private int CallRaw(ulong requestedLeaseId, uint requestedGeneration, ulong objectId, uint memberId, byte[] input, int outputCapacity)
    {
        return CallRaw(requestedLeaseId, requestedGeneration, objectId, memberId, input, outputCapacity, false, out _);
    }

    private int CallRaw(
        ulong requestedLeaseId,
        uint requestedGeneration,
        ulong objectId,
        uint memberId,
        byte[] input,
        int outputCapacity,
        bool nullOutput,
        out int outputLength)
    {
        IntPtr inputBuffer = Marshal.AllocHGlobal(input.Length);
        IntPtr outputBuffer = nullOutput ? IntPtr.Zero : Marshal.AllocHGlobal(Math.Max(outputCapacity, 1));
        try
        {
            Marshal.Copy(input, 0, inputBuffer, input.Length);
            return Invoke(requestedLeaseId, requestedGeneration, objectId, memberId, inputBuffer, input.Length, outputBuffer, outputCapacity, out outputLength);
        }
        finally
        {
            Marshal.FreeHGlobal(inputBuffer);
            if (outputBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(outputBuffer);
            }
        }
    }

    public void Dispose()
    {
        EndLease();
    }

    private static bool TryRead(nint input, int inputLength, out byte tag, out ReadOnlySpan<byte> value)
    {
        tag = default;
        value = default;
        if (inputLength < PowerShellLiveObjectBrokerWire.HeaderSize ||
            inputLength > PowerShellLiveObjectBrokerWire.HeaderSize + PowerShellLiveObjectBrokerWire.MaximumValueBytes ||
            input == 0)
        {
            return false;
        }

        byte[] buffer = new byte[inputLength];
        Marshal.Copy(input, buffer, 0, buffer.Length);
        return PowerShellLiveObjectBrokerWire.TryDecode(buffer, out tag, out value);
    }
}
