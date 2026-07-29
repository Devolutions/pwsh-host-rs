#nullable enable

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// Internal acceptance contract used to verify application-provided live-object
/// contract packs without adding contract-specific native plumbing.
/// </summary>
public static class PowerShellLiveObjectTestContracts
{
    public static readonly PowerShellLiveObjectContract Count = new(
        typeof(IPowerShellLiveObjectTestCount).GUID,
        majorVersion: 1,
        minorVersion: 0,
        PowerShellLiveObjectDirection.ConsumerToSession);

    public static readonly PowerShellLiveObjectContract SessionCreatorBroker = new(
        typeof(IPowerShellLiveObjectTestBroker).GUID,
        majorVersion: 1,
        minorVersion: 0,
        PowerShellLiveObjectDirection.ConsumerToSession);
}

[GeneratedComInterface]
[Guid("5BFBF6D7-7BFA-4C15-8D35-02B665F39A18")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectTestBroker
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
        byte[] text = System.Text.Encoding.UTF8.GetBytes(value);
        return Encode(Utf8String, text);
    }

    public static class PowerShellLiveObjectBrokerMembers
    {
        public const ulong RootObjectId = 1;
        public const ulong ChildrenObjectId = 2;
        public const uint RootAdd = 1;
        public const uint ChildrenCount = 2;
        public const uint ChildrenGetAt = 3;
        public const uint ChildGetName = 10;
        public const uint ChildSetName = 11;
        public const uint ChildGetHost = 12;
        public const uint ChildSetHost = 13;
        public const uint ChildGetDescription = 14;
        public const uint ChildSetDescription = 15;
        public const uint ChildGetGroup = 16;
        public const uint ChildSetGroup = 17;
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

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("C9A4FEA0-4EA6-48BE-8B4F-B30BB328CCBD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectTestCount
{
    [PreserveSig]
    int GetCount(out long count);

    [PreserveSig]
    int Increment(out long count);

    [PreserveSig]
    int GetRevision(out long revision);

    [PreserveSig]
    int SetRevision(long revision);

    [PreserveSig]
    int GetPrimary(out IPowerShellLiveObjectTestChild child);

    [PreserveSig]
    int GetChildren(out IPowerShellLiveObjectTestChildCollection children);

    [PreserveSig]
    int Add(string name, out IPowerShellLiveObjectTestChild child);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("EDB0F021-1CA0-4A03-829D-D6325A34E642")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectTestChild
{
    [PreserveSig]
    int GetValue(out long value);

    [PreserveSig]
    int SetValue(long value);

    [PreserveSig]
    int GetIdentity(out long identity);

    [PreserveSig]
    int GetName(out string name);

    [PreserveSig]
    int SetName(string name);

    [PreserveSig]
    int GetHost(out string host);

    [PreserveSig]
    int SetHost(string host);

    [PreserveSig]
    int GetDescription(out string description);

    [PreserveSig]
    int SetDescription(string description);

    [PreserveSig]
    int GetGroup(out string group);

    [PreserveSig]
    int SetGroup(string group);
}

[GeneratedComInterface]
[Guid("B120DAA8-8A42-4417-8315-BB3AFA0FD5DE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellLiveObjectTestChildCollection
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetAt(int index, out IPowerShellLiveObjectTestChild child);
}
