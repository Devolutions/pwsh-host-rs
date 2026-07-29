#nullable enable

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

[GeneratedComInterface]
[Guid("5BFBF6D7-7BFA-4C15-8D35-02B665F39A18")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectBrokerContract
{
    [PreserveSig]
    int Invoke(
        ulong leaseId,
        uint generation,
        ulong objectId,
        uint memberId,
        nint input,
        int inputLength,
        nint output,
        int outputCapacity,
        out int outputLength);

    [PreserveSig]
    int CloseLease(ulong leaseId, uint generation);
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct PowerShellLiveObjectBrokerValueHeader
{
    [FieldOffset(0)]
    public byte Tag;

    [FieldOffset(4)]
    public int Length;
}

public static class PowerShellLiveObjectBrokerWire
{
    public const byte ProtocolVersion = 1;
    public const int HeaderSize = 8;
    public const int MaximumValueBytes = 256;
    public const byte Null = 0;
    public const byte Utf8String = 1;
    public const byte ObjectHandle = 2;
    public const byte Int32 = 3;

    public static byte[] EncodeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encode(Utf8String, System.Text.Encoding.UTF8.GetBytes(value));
    }

    public static byte[] EncodeInt32(int value)
    {
        byte[] payload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, value);
        return Encode(Int32, payload);
    }

    public static byte[] EncodeObjectHandle(ulong value)
    {
        byte[] payload = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
        return Encode(ObjectHandle, payload);
    }

    public static byte[] Encode(byte tag, ReadOnlySpan<byte> value)
    {
        if (value.Length > MaximumValueBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        byte[] result = new byte[HeaderSize + value.Length];
        result[0] = ProtocolVersion;
        result[1] = tag;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), value.Length);
        value.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }

    public static bool TryDecode(ReadOnlySpan<byte> value, out byte tag, out ReadOnlySpan<byte> payload)
    {
        tag = default;
        payload = default;
        if (value.Length < HeaderSize || value[0] != ProtocolVersion)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
        if (length < 0 || length > MaximumValueBytes || length != value.Length - HeaderSize)
        {
            return false;
        }

        tag = value[1];
        payload = value[HeaderSize..];
        return true;
    }
}
