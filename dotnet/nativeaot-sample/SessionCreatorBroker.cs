using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace NativeAotFfiSample;

[GeneratedComClass]
internal sealed partial class SessionCreatorBroker : IPowerShellLiveObjectTestBroker, IPowerShellLiveObjectBroker
{
    internal const ulong RootObjectId = 1;
    internal const ulong ChildrenObjectId = 2;
    internal const uint RootAdd = 1;
    internal const uint ChildrenCount = 2;
    internal const uint ChildrenGetAt = 3;
    internal const uint ChildGetName = 10;
    internal const uint ChildSetName = 11;
    internal const uint ChildGetHost = 12;
    internal const uint ChildSetHost = 13;
    internal const uint ChildGetDescription = 14;
    internal const uint ChildSetDescription = 15;
    internal const uint ChildGetGroup = 16;
    internal const uint ChildSetGroup = 17;

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
                result = PowerShellLiveObjectBrokerWire.EncodeString($"{leaseId}:{generation}");
            }
            else if (closed || requestedLeaseId != leaseId || requestedGeneration != generation)
            {
                return EAccessDenied;
            }
            else if (objectId == RootObjectId && memberId == RootAdd && inputTag == PowerShellLiveObjectBrokerWire.Utf8String &&
                TryReadText(inputValue, out string name))
            {
                children.Add(new Child((ulong)children.Count + 3, name));
                result = PowerShellLiveObjectBrokerWire.EncodeObjectHandle(children[^1].Id);
            }
            else if (objectId == ChildrenObjectId && memberId == ChildrenCount && inputTag == PowerShellLiveObjectBrokerWire.Null)
            {
                result = PowerShellLiveObjectBrokerWire.EncodeInt32(children.Count);
            }
            else if (objectId == ChildrenObjectId && memberId == ChildrenGetAt && inputTag == PowerShellLiveObjectBrokerWire.Int32 &&
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
                return CallRaw(leaseId, generation, RootObjectId, 999, nullValue, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation, ulong.MaxValue, ChildGetName, nullValue, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation, RootObjectId, RootAdd, malformedUtf8, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation, RootObjectId, RootAdd, invalidProtocol, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation, RootObjectId, RootAdd, invalidLength, 264) == EInvalidArg &&
                    CallRaw(leaseId, generation + 1, RootObjectId, RootAdd, nullValue, 264) == EAccessDenied &&
                    CallRaw(leaseId + 1, generation, RootObjectId, RootAdd, nullValue, 264) == EAccessDenied &&
                    CallRaw(leaseId, generation, ChildrenObjectId, ChildrenGetAt, PowerShellLiveObjectBrokerWire.EncodeInt32(0), 264) == EBounds &&
                    CallRaw(leaseId, generation, ChildrenObjectId, ChildrenCount, nullValue, 0) == EBufferTooSmall &&
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
        if (memberId is ChildGetName or ChildGetHost or ChildGetDescription or ChildGetGroup && tag == PowerShellLiveObjectBrokerWire.Null)
        {
            result = PowerShellLiveObjectBrokerWire.EncodeString(memberId switch
            {
                ChildGetName => child.Name,
                ChildGetHost => child.Host,
                ChildGetDescription => child.Description,
                _ => child.Group,
            });
            return true;
        }

        if (memberId is ChildSetName or ChildSetHost or ChildSetDescription or ChildSetGroup &&
            tag == PowerShellLiveObjectBrokerWire.Utf8String && TryReadText(value, out string text))
        {
            switch (memberId)
            {
                case ChildSetName: child.Name = text; break;
                case ChildSetHost: child.Host = text; break;
                case ChildSetDescription: child.Description = text; break;
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
