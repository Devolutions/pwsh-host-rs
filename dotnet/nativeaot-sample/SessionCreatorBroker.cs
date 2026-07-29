using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;
using GeneratedContract = Devolutions.MultiPwsh.LiveContracts.ISessionCreatorLiveContractGenerated;

namespace NativeAotFfiSample;

[GeneratedComClass]
internal sealed partial class SessionCreatorBroker : IPowerShellLiveObjectTestBroker, IPowerShellLiveObjectBroker
{
    private const int EInvalidArg = unchecked((int)0x80070057);
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int EBounds = unchecked((int)0x8000000B);
    private const int EBufferTooSmall = unchecked((int)0x8007007A);
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object gate = new();
    private readonly ulong leaseId;
    private readonly List<Child> children = [];
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

            byte[] result;
            if (requestedLeaseId == 0 && requestedGeneration == 0 && objectId == 0 && memberId == 0 &&
                inputTag == PowerShellLiveObjectBrokerWire.Null)
            {
                result = PowerShellLiveObjectBrokerWire.EncodeString($"{leaseId}:{generation}:{GeneratedContract.ContractHash}");
            }
            else if (closed || requestedLeaseId != leaseId || requestedGeneration != generation)
            {
                return EAccessDenied;
            }
            else if (objectId == GeneratedContract.RootObjectId && memberId == GeneratedContract.Add && inputTag == PowerShellLiveObjectBrokerWire.Utf8String &&
                TryReadText(inputValue, out string name))
            {
                children.Add(new Child((ulong)children.Count + 3, name));
                result = PowerShellLiveObjectBrokerWire.EncodeObjectHandle(children[^1].Id);
            }
            else if (objectId == GeneratedContract.ChildrenObjectId && memberId == GeneratedContract.CountGet && inputTag == PowerShellLiveObjectBrokerWire.Null)
            {
                result = PowerShellLiveObjectBrokerWire.EncodeInt32(children.Count);
            }
            else if (objectId == GeneratedContract.ChildrenObjectId && memberId == GeneratedContract.GetAt && inputTag == PowerShellLiveObjectBrokerWire.Int32 &&
                inputValue.Length == sizeof(int))
            {
                int index = BinaryPrimitives.ReadInt32LittleEndian(inputValue);
                if ((uint)index >= (uint)children.Count)
                {
                    return EBounds;
                }

                result = PowerShellLiveObjectBrokerWire.EncodeObjectHandle(children[index].Id);
            }
            else if (TryGetChild(objectId, out Child child) && TryInvokeChild(child, memberId, inputTag, inputValue, out result))
            {
            }
            else
            {
                return EInvalidArg;
            }

            outputLength = result.Length;
            if (output == 0 || outputCapacity < result.Length)
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
            if (requestedLeaseId != leaseId || requestedGeneration != generation)
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
            byte[] invalidProtocol = (byte[])nullValue.Clone();
            invalidProtocol[0] = 2;
            byte[] invalidLength = (byte[])nullValue.Clone();
            invalidLength[4] = 1;

            lock (gate)
            {
                int count = children.Count;
                return CallRaw(leaseId, generation, GeneratedContract.RootObjectId, 999, nullValue, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation, ulong.MaxValue, GeneratedContract.NameGet, nullValue, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation, GeneratedContract.RootObjectId, GeneratedContract.Add, malformedUtf8, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation, GeneratedContract.RootObjectId, GeneratedContract.Add, invalidProtocol, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation, GeneratedContract.RootObjectId, GeneratedContract.Add, invalidLength, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation + 1, GeneratedContract.RootObjectId, GeneratedContract.Add, nullValue, 264) == EAccessDenied &&
                    CallRaw(leaseId + 1, generation, GeneratedContract.RootObjectId, GeneratedContract.Add, nullValue, 264) == EAccessDenied &&
                    CallRaw(leaseId, generation, GeneratedContract.ChildrenObjectId, GeneratedContract.GetAt, PowerShellLiveObjectBrokerWire.EncodeInt32(0), 264) == EBounds &&
                    CallRaw(leaseId, generation, GeneratedContract.ChildrenObjectId, GeneratedContract.CountGet, nullValue, 0) == EBufferTooSmall &&
                    children.Count == count;
            }
        }

    private int CallRaw(ulong requestedLeaseId, uint requestedGeneration, ulong objectId, uint memberId, byte[] input, int outputCapacity)
        {
            IntPtr inputBuffer = Marshal.AllocHGlobal(input.Length);
            IntPtr outputBuffer = Marshal.AllocHGlobal(Math.Max(outputCapacity, 1));
            try
            {
                Marshal.Copy(input, 0, inputBuffer, input.Length);
                return Invoke(requestedLeaseId, requestedGeneration, objectId, memberId, inputBuffer, input.Length, outputBuffer, outputCapacity, out _);
            }
            finally
            {
                Marshal.FreeHGlobal(inputBuffer);
                Marshal.FreeHGlobal(outputBuffer);
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

    private static bool TryReadText(ReadOnlySpan<byte> value, out string text)
    {
        text = string.Empty;
        if (value.IsEmpty)
        {
            return false;
        }

        try
        {
            text = StrictUtf8.GetString(value);
            return text.Length <= 128 && text.AsSpan().IndexOf('\0') < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryGetChild(ulong objectId, out Child child)
    {
        if (objectId >= 3 &&
            objectId - 3 < (ulong)children.Count &&
            children[(int)(objectId - 3)].Id == objectId)
        {
            child = children[(int)(objectId - 3)];
            return true;
        }

        child = null!;
        return false;
    }

    private static bool TryInvokeChild(Child child, uint memberId, byte tag, ReadOnlySpan<byte> value, out byte[] result)
    {
        result = null!;
        if (memberId is GeneratedContract.NameGet or GeneratedContract.HostGet or GeneratedContract.DescriptionGet or GeneratedContract.GroupGet && tag == PowerShellLiveObjectBrokerWire.Null)
        {
            result = PowerShellLiveObjectBrokerWire.EncodeString(memberId switch
            {
                GeneratedContract.NameGet => child.Name,
                GeneratedContract.HostGet => child.Host,
                GeneratedContract.DescriptionGet => child.Description,
                _ => child.Group,
            });
            return true;
        }

        if (memberId is GeneratedContract.NameSet or GeneratedContract.HostSet or GeneratedContract.DescriptionSet or GeneratedContract.GroupSet &&
            tag == PowerShellLiveObjectBrokerWire.Utf8String && TryReadText(value, out string text))
        {
            switch (memberId)
            {
                case GeneratedContract.NameSet: child.Name = text; break;
                case GeneratedContract.HostSet: child.Host = text; break;
                case GeneratedContract.DescriptionSet: child.Description = text; break;
                default: child.Group = text; break;
            }

            result = PowerShellLiveObjectBrokerWire.Encode(PowerShellLiveObjectBrokerWire.Null, []);
            return true;
        }

        return false;
    }

    private sealed class Child
    {
        internal Child(ulong id, string name) { Id = id; Name = name; }
        internal ulong Id { get; }
        internal string Name { get; set; }
        internal string Host { get; set; } = string.Empty;
        internal string Description { get; set; } = string.Empty;
        internal string Group { get; set; } = string.Empty;
    }
}
