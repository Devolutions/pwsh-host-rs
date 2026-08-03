#nullable enable

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// The closed, finite value tag set for the Bridge Contract v2 wire format.
/// Only <see cref="List"/>, <see cref="Data"/>, and <see cref="Error"/> nest,
/// and nesting is capped by <see cref="PowerShellBridgeWire.MaximumValueDepth"/>.
/// </summary>
public static class PowerShellBridgeTag
{
    public const byte Null = 0;
    public const byte Bool = 1;
    public const byte Int32 = 2;
    public const byte Int64 = 3;
    public const byte Double = 4;
    public const byte Utf8String = 5;
    public const byte Bytes = 6;
    public const byte Guid = 7;
    public const byte Enum32 = 8;
    public const byte Handle = 9;
    public const byte List = 10;
    public const byte Data = 11;
    public const byte Error = 12;

    public const byte Maximum = Error;

    public static bool IsDefined(byte tag) => tag <= Maximum;
}

/// <summary>Distinguishes the request frame shapes, including the lease-open control frame.</summary>
public static class PowerShellBridgeFrameKind
{
    public const byte Invoke = 0;
    public const byte Release = 1;
    public const byte Event = 2;
    public const byte Open = 3;
    public const byte Close = 4;
    public const byte ReliableEvent = 5;

    public static bool IsDefined(byte kind) => kind <= ReliableEvent;
}

/// <summary>
/// The fixed transport statuses a generated consumer dispatcher returns. Revoked
/// handles and denied authorization share one status on purpose, so a caller
/// cannot probe which object handles exist.
/// </summary>
public static class PowerShellBridgeStatus
{
    public const int Success = 0;
    public const int InvalidArgument = unchecked((int)0x80070057);
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int Bounds = unchecked((int)0x8000000B);
    public const int OutOfMemory = unchecked((int)0x8007000E);
    public const int BufferTooSmall = unchecked((int)0x8007007A);
    public const int ContractMismatch = unchecked((int)0x8007075B);
}

/// <summary>Distinguishes a value reply from a typed error reply.</summary>
public static class PowerShellBridgeReplyKind
{
    public const byte Value = 0;
    public const byte Error = 1;

    public static bool IsDefined(byte kind) => kind <= Error;
}

/// <summary>
/// Fixed-width, little-endian constants shared by generated payload wrappers and
/// generated consumer dispatchers. Nothing here is self-describing: a frame is
/// only meaningful next to the compiled contract that produced it.
/// </summary>
public static class PowerShellBridgeWire
{
    public const byte ProtocolVersion = 2;

    public const int ValueHeaderSize = 8;
    public const int RequestHeaderSize = 32;
    public const int ReplyHeaderSize = 8;

    public const int ListPrologueSize = 8;
    public const int DataPrologueSize = 16;
    public const int ErrorPrologueSize = 8;
    public const int DataFieldPrologueSize = 8;

    public const int MaximumFrameBytes = 65536;
    public const int MaximumValueDepth = 4;
    public const int MaximumUtf8Bytes = 8192;
    public const int MaximumByteCount = 8192;
    public const int MaximumCollectionCount = 4096;

    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Returns whether a declared UTF-8 or byte bound is in range.</summary>
    public static bool IsValidByteBound(int value) => value > 0 && value <= MaximumUtf8Bytes;

    /// <summary>Returns whether a declared collection bound is in range.</summary>
    public static bool IsValidCollectionBound(int value) => value > 0 && value <= MaximumCollectionCount;
}

/// <summary>
/// Fixed DBC routing and response-envelope constants for generated bridge
/// frames. The binding ID is channel-scoped; it is never a CLR identity.
/// </summary>
public static class PowerShellBridgeBrokerWire
{
    public const uint RequestKind = 0x4252_0001;
    public const uint EventKind = 0x4252_0002;
    public const int RouteHeaderSize = sizeof(ulong);
    public const int ReplyEnvelopeSize = sizeof(int) + sizeof(uint);

    public static bool TryWriteRoute(ulong bindingId, Span<byte> destination)
    {
        if (bindingId == 0 || destination.Length < RouteHeaderSize)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, bindingId);
        return true;
    }

    public static bool TryReadRoute(ReadOnlySpan<byte> source, out ulong bindingId)
    {
        bindingId = 0;
        if (source.Length < RouteHeaderSize)
        {
            return false;
        }

        bindingId = BinaryPrimitives.ReadUInt64LittleEndian(source);
        return bindingId != 0;
    }
}

/// <summary>
/// Fixed payload-owned COM handshake for a generated Bridge Contract v2 pack
/// that uses a broker-backed attachment. The pack receives no application
/// object or callback; every generated frame goes through this sink.
/// </summary>
[GeneratedComInterface]
[Guid("10C88A62-041B-49FC-89AF-1B91BF5DA9A5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public partial interface IPowerShellBridgeBrokerSink
{
    [PreserveSig]
    int GetRequestedContract(
        out ulong interfaceIdLow,
        out ulong interfaceIdHigh,
        out ushort majorVersion,
        out ushort minorVersion,
        out uint maximumRequestBytes,
        out uint maximumReplyBytes);

    [PreserveSig]
    int Declare(
        ulong interfaceIdLow,
        ulong interfaceIdHigh,
        ushort majorVersion,
        ushort minorVersion,
        nint callback,
        uint maximumRequestBytes,
        uint maximumReplyBytes);

    [PreserveSig]
    int Request(nint body, int bodyLength, nint reply, int replyCapacity, out int replyLength);

    [PreserveSig]
    int Post(nint body, int bodyLength);
}

/// <summary>
/// The 32-byte request frame header. It carries
/// <c>(leaseId, generation, objectId, memberId)</c> even when the transport also
/// passes them out of band, so a consumer dispatcher can reject a frame that
/// disagrees with its own transport parameters before dispatching anything.
/// </summary>
public readonly struct PowerShellBridgeRequestHeader
{
    public PowerShellBridgeRequestHeader(
        byte frameKind,
        ushort argumentCount,
        uint memberId,
        ulong objectId,
        ulong leaseId,
        uint generation,
        int bodyLength)
    {
        FrameKind = frameKind;
        ArgumentCount = argumentCount;
        MemberId = memberId;
        ObjectId = objectId;
        LeaseId = leaseId;
        Generation = generation;
        BodyLength = bodyLength;
    }

    public byte FrameKind { get; }
    public ushort ArgumentCount { get; }
    public uint MemberId { get; }
    public ulong ObjectId { get; }
    public ulong LeaseId { get; }
    public uint Generation { get; }
    public int BodyLength { get; }

    public bool TryWrite(Span<byte> destination)
    {
        if (destination.Length < PowerShellBridgeWire.RequestHeaderSize ||
            !PowerShellBridgeFrameKind.IsDefined(FrameKind) ||
            BodyLength < 0 ||
            BodyLength > PowerShellBridgeWire.MaximumFrameBytes - PowerShellBridgeWire.RequestHeaderSize)
        {
            return false;
        }

        destination[0] = PowerShellBridgeWire.ProtocolVersion;
        destination[1] = FrameKind;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), ArgumentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4), MemberId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8), ObjectId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(16), LeaseId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24), Generation);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(28), BodyLength);
        return true;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out PowerShellBridgeRequestHeader header)
    {
        header = default;
        if (source.Length < PowerShellBridgeWire.RequestHeaderSize ||
            source[0] != PowerShellBridgeWire.ProtocolVersion ||
            !PowerShellBridgeFrameKind.IsDefined(source[1]))
        {
            return false;
        }

        int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(28));
        if (bodyLength < 0 ||
            bodyLength != source.Length - PowerShellBridgeWire.RequestHeaderSize ||
            source.Length > PowerShellBridgeWire.MaximumFrameBytes)
        {
            return false;
        }

        header = new PowerShellBridgeRequestHeader(
            source[1],
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2)),
            BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4)),
            BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(24)),
            bodyLength);
        return true;
    }
}

/// <summary>The 8-byte reply frame header. It precedes exactly one tagged value.</summary>
public readonly struct PowerShellBridgeReplyHeader
{
    public PowerShellBridgeReplyHeader(byte replyKind, int bodyLength)
    {
        ReplyKind = replyKind;
        BodyLength = bodyLength;
    }

    public byte ReplyKind { get; }
    public int BodyLength { get; }

    public bool TryWrite(Span<byte> destination)
    {
        if (destination.Length < PowerShellBridgeWire.ReplyHeaderSize ||
            !PowerShellBridgeReplyKind.IsDefined(ReplyKind) ||
            BodyLength < 0 ||
            BodyLength > PowerShellBridgeWire.MaximumFrameBytes - PowerShellBridgeWire.ReplyHeaderSize)
        {
            return false;
        }

        destination[0] = PowerShellBridgeWire.ProtocolVersion;
        destination[1] = ReplyKind;
        destination[2] = 0;
        destination[3] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4), BodyLength);
        return true;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out PowerShellBridgeReplyHeader header)
    {
        header = default;
        if (source.Length < PowerShellBridgeWire.ReplyHeaderSize ||
            source[0] != PowerShellBridgeWire.ProtocolVersion ||
            !PowerShellBridgeReplyKind.IsDefined(source[1]) ||
            source[2] != 0 ||
            source[3] != 0)
        {
            return false;
        }

        int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4));
        if (bodyLength < 0 ||
            bodyLength != source.Length - PowerShellBridgeWire.ReplyHeaderSize ||
            source.Length > PowerShellBridgeWire.MaximumFrameBytes)
        {
            return false;
        }

        header = new PowerShellBridgeReplyHeader(source[1], bodyLength);
        return true;
    }
}

/// <summary>
/// Writes Bridge Contract v2 tagged values into a caller-owned span. Every write
/// is bounds-checked and the first failure latches, so generated code can write a
/// whole frame and check once.
/// </summary>
public ref struct PowerShellBridgeValueWriter
{
    private readonly Span<byte> buffer;
    private int position;
    private int depth;
    private bool failed;

    public PowerShellBridgeValueWriter(Span<byte> buffer)
    {
        this.buffer = buffer;
        position = 0;
        depth = 0;
        failed = false;
    }

    /// <summary>Gets the number of bytes written so far.</summary>
    public readonly int Length => position;

    /// <summary>Gets whether any write failed. A failed writer never produces a frame.</summary>
    public readonly bool Failed => failed;

    /// <summary>Gets whether the writer is complete: nothing failed and no container is open.</summary>
    public readonly bool IsComplete => !failed && depth == 0;

    public bool TryWriteNull() => TryWriteScalar(PowerShellBridgeTag.Null, 0, out _);

    public bool TryWriteBool(bool value)
    {
        if (!TryWriteScalar(PowerShellBridgeTag.Bool, 1, out int payload))
        {
            return false;
        }

        buffer[payload] = value ? (byte)1 : (byte)0;
        return true;
    }

    public bool TryWriteInt32(int value)
    {
        if (!TryWriteScalar(PowerShellBridgeTag.Int32, sizeof(int), out int payload))
        {
            return false;
        }

        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(payload), value);
        return true;
    }

    public bool TryWriteInt64(long value)
    {
        if (!TryWriteScalar(PowerShellBridgeTag.Int64, sizeof(long), out int payload))
        {
            return false;
        }

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(payload), value);
        return true;
    }

    public bool TryWriteDouble(double value)
    {
        if (!TryWriteScalar(PowerShellBridgeTag.Double, sizeof(double), out int payload))
        {
            return false;
        }

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(payload), BitConverter.DoubleToInt64Bits(value));
        return true;
    }

    /// <summary>Writes a strict UTF-8 string. A null value writes <see cref="PowerShellBridgeTag.Null"/>.</summary>
    public bool TryWriteString(string? value, int maximumUtf8Bytes)
    {
        if (value is null)
        {
            return TryWriteNull();
        }

        if (!PowerShellBridgeWire.IsValidByteBound(maximumUtf8Bytes) || value.IndexOf('\0') >= 0)
        {
            return Fail();
        }

        int count;
        try
        {
            count = PowerShellBridgeWire.StrictUtf8.GetByteCount(value);
        }
        catch (ArgumentException)
        {
            return Fail();
        }

        if (count > maximumUtf8Bytes || !TryWriteScalar(PowerShellBridgeTag.Utf8String, count, out int payload))
        {
            return count > maximumUtf8Bytes ? Fail() : false;
        }

        if (count == 0)
        {
            return true;
        }

        try
        {
            PowerShellBridgeWire.StrictUtf8.GetBytes(value.AsSpan(), buffer.Slice(payload, count));
        }
        catch (ArgumentException)
        {
            return Fail();
        }

        return true;
    }

    public bool TryWriteBytes(ReadOnlySpan<byte> value, int maximumByteCount)
    {
        if (!PowerShellBridgeWire.IsValidByteBound(maximumByteCount) || value.Length > maximumByteCount)
        {
            return Fail();
        }

        if (!TryWriteScalar(PowerShellBridgeTag.Bytes, value.Length, out int payload))
        {
            return false;
        }

        value.CopyTo(buffer.Slice(payload, value.Length));
        return true;
    }

    public bool TryWriteGuid(Guid value)
    {
        if (!TryWriteScalar(PowerShellBridgeTag.Guid, 16, out int payload))
        {
            return false;
        }

        return value.TryWriteBytes(buffer.Slice(payload, 16)) || Fail();
    }

    public bool TryWriteEnum32(int value)
    {
        if (!TryWriteScalar(PowerShellBridgeTag.Enum32, sizeof(int), out int payload))
        {
            return false;
        }

        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(payload), value);
        return true;
    }

    /// <summary>Writes a lease-scoped handle. The declared object type travels with it.</summary>
    public bool TryWriteHandle(ulong objectTypeId, ulong objectId)
    {
        if (objectTypeId == 0)
        {
            return Fail();
        }

        if (!TryWriteScalar(PowerShellBridgeTag.Handle, 16, out int payload))
        {
            return false;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(payload), objectTypeId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(payload + 8), objectId);
        return true;
    }

    public bool TryBeginList(int count, byte elementTag, int maximumCount, out int scope)
    {
        scope = position;
        if (!PowerShellBridgeWire.IsValidCollectionBound(maximumCount) ||
            count < 0 ||
            count > maximumCount ||
            !PowerShellBridgeTag.IsDefined(elementTag))
        {
            return Fail();
        }

        if (!TryOpen(PowerShellBridgeTag.List, PowerShellBridgeWire.ListPrologueSize, out int payload))
        {
            return false;
        }

        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(payload), count);
        buffer[payload + 4] = elementTag;
        buffer[payload + 5] = 0;
        buffer[payload + 6] = 0;
        buffer[payload + 7] = 0;
        return true;
    }

    public bool TryEndList(int scope) => TryClose(scope);

    public bool TryBeginData(ulong dataId, int fieldCount, out int scope)
    {
        scope = position;
        if (dataId == 0 || fieldCount < 0)
        {
            return Fail();
        }

        if (!TryOpen(PowerShellBridgeTag.Data, PowerShellBridgeWire.DataPrologueSize, out int payload))
        {
            return false;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(payload), dataId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(payload + 8), fieldCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(payload + 12), 0);
        return true;
    }

    /// <summary>Writes the field prologue that precedes one data field value.</summary>
    public bool TryWriteFieldOrdinal(uint ordinal)
    {
        if (failed || ordinal == 0)
        {
            return Fail();
        }

        if (!TryAdvance(PowerShellBridgeWire.DataFieldPrologueSize, out int start))
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(start), ordinal);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(start + 4), 0);
        return true;
    }

    public bool TryEndData(int scope) => TryClose(scope);

    public bool TryBeginError(int code, out int scope)
    {
        scope = position;
        if (!TryOpen(PowerShellBridgeTag.Error, PowerShellBridgeWire.ErrorPrologueSize, out int payload))
        {
            return false;
        }

        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(payload), code);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(payload + 4), 0);
        return true;
    }

    public bool TryEndError(int scope) => TryClose(scope);

    private bool TryWriteScalar(byte tag, int payloadLength, out int payload)
    {
        payload = 0;
        if (failed || depth >= PowerShellBridgeWire.MaximumValueDepth + 1)
        {
            return Fail();
        }

        if (!TryAdvance(PowerShellBridgeWire.ValueHeaderSize + payloadLength, out int start))
        {
            return false;
        }

        WriteValueHeader(start, tag, payloadLength);
        payload = start + PowerShellBridgeWire.ValueHeaderSize;
        return true;
    }

    private bool TryOpen(byte tag, int prologueLength, out int payload)
    {
        payload = 0;
        if (failed || depth >= PowerShellBridgeWire.MaximumValueDepth)
        {
            return Fail();
        }

        if (!TryAdvance(PowerShellBridgeWire.ValueHeaderSize + prologueLength, out int start))
        {
            return false;
        }

        WriteValueHeader(start, tag, 0);
        payload = start + PowerShellBridgeWire.ValueHeaderSize;
        depth++;
        return true;
    }

    private bool TryClose(int scope)
    {
        if (failed || depth == 0 || scope < 0 || scope > position - PowerShellBridgeWire.ValueHeaderSize)
        {
            return Fail();
        }

        int length = position - scope - PowerShellBridgeWire.ValueHeaderSize;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(scope + 4), length);
        depth--;
        return true;
    }

    private void WriteValueHeader(int start, byte tag, int payloadLength)
    {
        buffer[start] = PowerShellBridgeWire.ProtocolVersion;
        buffer[start + 1] = tag;
        buffer[start + 2] = 0;
        buffer[start + 3] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(start + 4), payloadLength);
    }

    private bool TryAdvance(int length, out int start)
    {
        start = position;
        if (length < 0 ||
            length > PowerShellBridgeWire.MaximumFrameBytes ||
            position > buffer.Length - length ||
            position > PowerShellBridgeWire.MaximumFrameBytes - length)
        {
            return Fail();
        }

        position += length;
        return true;
    }

    private bool Fail()
    {
        failed = true;
        return false;
    }
}

/// <summary>
/// Reads Bridge Contract v2 tagged values. Every container records its declared
/// end offset, so a frame whose parent length disagrees with the children it
/// actually contains is rejected instead of silently reinterpreted.
/// </summary>
public ref struct PowerShellBridgeValueReader
{
    private readonly ReadOnlySpan<byte> buffer;
    private int position;
    private int depth;
    private int end0;
    private int end1;
    private int end2;
    private int end3;

    public PowerShellBridgeValueReader(ReadOnlySpan<byte> buffer)
    {
        this.buffer = buffer;
        position = 0;
        depth = 0;
        end0 = 0;
        end1 = 0;
        end2 = 0;
        end3 = 0;
    }

    /// <summary>Gets whether every byte was consumed and no container is still open.</summary>
    public readonly bool IsComplete => depth == 0 && position == buffer.Length;

    public readonly bool TryPeekTag(out byte tag)
    {
        tag = 0;
        if (position > buffer.Length - PowerShellBridgeWire.ValueHeaderSize ||
            buffer[position] != PowerShellBridgeWire.ProtocolVersion ||
            !PowerShellBridgeTag.IsDefined(buffer[position + 1]))
        {
            return false;
        }

        tag = buffer[position + 1];
        return true;
    }

    public bool TryReadNull() => TryReadScalar(PowerShellBridgeTag.Null, 0, out _);

    public bool TryReadBool(out bool value)
    {
        value = false;
        if (!TryReadScalar(PowerShellBridgeTag.Bool, 1, out int payload))
        {
            return false;
        }

        byte raw = buffer[payload];
        value = raw == 1;
        return raw <= 1;
    }

    public bool TryReadInt32(out int value)
    {
        value = 0;
        if (!TryReadScalar(PowerShellBridgeTag.Int32, sizeof(int), out int payload))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(payload));
        return true;
    }

    public bool TryReadInt64(out long value)
    {
        value = 0;
        if (!TryReadScalar(PowerShellBridgeTag.Int64, sizeof(long), out int payload))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(payload));
        return true;
    }

    public bool TryReadDouble(out double value)
    {
        value = 0;
        if (!TryReadScalar(PowerShellBridgeTag.Double, sizeof(double), out int payload))
        {
            return false;
        }

        value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(payload)));
        return true;
    }

    public bool TryReadString(int maximumUtf8Bytes, out string? value)
    {
        value = null;
        if (!TryPeekTag(out byte tag))
        {
            return false;
        }

        if (tag == PowerShellBridgeTag.Null)
        {
            return TryReadNull();
        }

        if (!PowerShellBridgeWire.IsValidByteBound(maximumUtf8Bytes) ||
            !TryReadVariable(PowerShellBridgeTag.Utf8String, maximumUtf8Bytes, out int payload, out int length))
        {
            return false;
        }

        try
        {
            value = length == 0
                ? string.Empty
                : PowerShellBridgeWire.StrictUtf8.GetString(buffer.Slice(payload, length));
        }
        catch (ArgumentException)
        {
            value = null;
            return false;
        }

        if (value.IndexOf('\0') >= 0)
        {
            value = null;
            return false;
        }

        return true;
    }

    public bool TryReadBytes(int maximumByteCount, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!PowerShellBridgeWire.IsValidByteBound(maximumByteCount) ||
            !TryReadVariable(PowerShellBridgeTag.Bytes, maximumByteCount, out int payload, out int length))
        {
            return false;
        }

        value = buffer.Slice(payload, length);
        return true;
    }

    public bool TryReadGuid(out Guid value)
    {
        value = default;
        if (!TryReadScalar(PowerShellBridgeTag.Guid, 16, out int payload))
        {
            return false;
        }

        value = new Guid(buffer.Slice(payload, 16));
        return true;
    }

    public bool TryReadEnum32(out int value)
    {
        value = 0;
        if (!TryReadScalar(PowerShellBridgeTag.Enum32, sizeof(int), out int payload))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(payload));
        return true;
    }

    public bool TryReadHandle(ulong expectedObjectTypeId, out ulong objectId)
    {
        objectId = 0;
        if (!TryReadScalar(PowerShellBridgeTag.Handle, 16, out int payload))
        {
            return false;
        }

        ulong objectTypeId = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(payload));
        if (objectTypeId == 0 || (expectedObjectTypeId != 0 && objectTypeId != expectedObjectTypeId))
        {
            return false;
        }

        objectId = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(payload + 8));
        return true;
    }

    public bool TryReadListHeader(int maximumCount, out int count, out byte elementTag)
    {
        count = 0;
        elementTag = 0;
        if (!PowerShellBridgeWire.IsValidCollectionBound(maximumCount) ||
            !TryOpen(PowerShellBridgeTag.List, PowerShellBridgeWire.ListPrologueSize, out int payload))
        {
            return false;
        }

        count = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(payload));
        elementTag = buffer[payload + 4];
        return count >= 0 &&
            count <= maximumCount &&
            PowerShellBridgeTag.IsDefined(elementTag) &&
            buffer[payload + 5] == 0 &&
            buffer[payload + 6] == 0 &&
            buffer[payload + 7] == 0;
    }

    public bool TryReadDataHeader(ulong expectedDataId, out int fieldCount)
    {
        fieldCount = 0;
        if (!TryOpen(PowerShellBridgeTag.Data, PowerShellBridgeWire.DataPrologueSize, out int payload))
        {
            return false;
        }

        ulong dataId = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(payload));
        fieldCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(payload + 8));
        return dataId != 0 &&
            (expectedDataId == 0 || dataId == expectedDataId) &&
            fieldCount >= 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(payload + 12)) == 0;
    }

    public bool TryReadFieldOrdinal(uint expectedOrdinal)
    {
        if (position > CurrentEnd - PowerShellBridgeWire.DataFieldPrologueSize)
        {
            return false;
        }

        uint ordinal = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(position));
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(position + 4));
        position += PowerShellBridgeWire.DataFieldPrologueSize;
        return ordinal == expectedOrdinal && ordinal != 0 && reserved == 0;
    }

    public bool TryReadErrorHeader(out int code)
    {
        code = 0;
        if (!TryOpen(PowerShellBridgeTag.Error, PowerShellBridgeWire.ErrorPrologueSize, out int payload))
        {
            return false;
        }

        code = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(payload));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(payload + 4)) == 0;
    }

    /// <summary>Closes the innermost open container and asserts it consumed exactly its declared bytes.</summary>
    public bool TryEndContainer()
    {
        if (depth == 0 || position != CurrentEnd)
        {
            return false;
        }

        depth--;
        return true;
    }

    private readonly int CurrentEnd => depth switch
    {
        1 => end0,
        2 => end1,
        3 => end2,
        4 => end3,
        _ => buffer.Length,
    };

    private bool TryReadScalar(byte tag, int payloadLength, out int payload)
    {
        payload = 0;
        if (!TryReadHeader(out byte actualTag, out int length, out int start) ||
            actualTag != tag ||
            length != payloadLength)
        {
            return false;
        }

        payload = start;
        position = start + length;
        return true;
    }

    private bool TryReadVariable(byte tag, int maximumLength, out int payload, out int length)
    {
        payload = 0;
        length = 0;
        if (!TryReadHeader(out byte actualTag, out int actualLength, out int start) ||
            actualTag != tag ||
            actualLength > maximumLength)
        {
            return false;
        }

        payload = start;
        length = actualLength;
        position = start + actualLength;
        return true;
    }

    private bool TryOpen(byte tag, int prologueLength, out int payload)
    {
        payload = 0;
        if (depth >= PowerShellBridgeWire.MaximumValueDepth ||
            !TryReadHeader(out byte actualTag, out int length, out int start) ||
            actualTag != tag ||
            length < prologueLength)
        {
            return false;
        }

        payload = start;
        position = start + prologueLength;
        depth++;
        SetCurrentEnd(start + length);
        return true;
    }

    private bool TryReadHeader(out byte tag, out int length, out int payloadStart)
    {
        tag = 0;
        length = 0;
        payloadStart = 0;
        int end = CurrentEnd;
        if (position > end - PowerShellBridgeWire.ValueHeaderSize ||
            buffer[position] != PowerShellBridgeWire.ProtocolVersion ||
            !PowerShellBridgeTag.IsDefined(buffer[position + 1]) ||
            buffer[position + 2] != 0 ||
            buffer[position + 3] != 0)
        {
            return false;
        }

        int declared = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(position + 4));
        payloadStart = position + PowerShellBridgeWire.ValueHeaderSize;
        if (declared < 0 || declared > end - payloadStart)
        {
            return false;
        }

        tag = buffer[position + 1];
        length = declared;
        return true;
    }

    private void SetCurrentEnd(int value)
    {
        switch (depth)
        {
            case 1:
                end0 = value;
                break;
            case 2:
                end1 = value;
                break;
            case 3:
                end2 = value;
                break;
            default:
                end3 = value;
                break;
        }
    }
}
