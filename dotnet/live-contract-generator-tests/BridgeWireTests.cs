using System.Buffers.Binary;
using Devolutions.PowerShell.Ffi.LiveObjects;

/// <summary>
/// Round-trip and rejection fixtures for the Bridge Contract v2 wire codec.
/// The generator fixtures prove the emitted codec compiles; these prove the
/// primitives it is built on encode, decode, and refuse the right things.
/// </summary>
internal static class BridgeWireTests
{
    internal static void Run()
    {
        ScalarsRoundTrip();
        StringsAreBoundedAndStrict();
        ListsRoundTripAndEnforceTheirBound();
        DataRoundTripsAndBindsItsIdentity();
        NestedListOfDataRoundTrips();
        HandlesCarryTheirDeclaredType();
        FramesRoundTripAndRejectMalformedHeaders();
        TruncatedAndOverlongValuesAreRejected();
        ContainerLengthsMustAgreeWithTheirChildren();
        DepthIsCapped();
    }

    private static void ScalarsRoundTrip()
    {
        byte[] buffer = new byte[512];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryWriteNull(), "write null");
        Require(writer.TryWriteBool(true), "write bool");
        Require(writer.TryWriteInt32(-42), "write int32");
        Require(writer.TryWriteInt64(long.MinValue), "write int64");
        Require(writer.TryWriteDouble(-0.5), "write double");
        Require(writer.TryWriteGuid(new Guid("2C7E8A11-6B44-4E27-9F0A-0C6C0F53D8E1")), "write guid");
        Require(writer.TryWriteEnum32(7), "write enum");
        Require(writer.TryWriteBytes(new byte[] { 1, 2, 3 }, 16), "write bytes");
        Require(writer.IsComplete, "writer complete");

        var reader = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(reader.TryReadNull(), "read null");
        Require(reader.TryReadBool(out bool flag) && flag, "read bool");
        Require(reader.TryReadInt32(out int int32) && int32 == -42, "read int32");
        Require(reader.TryReadInt64(out long int64) && int64 == long.MinValue, "read int64");
        Require(reader.TryReadDouble(out double real) && real == -0.5, "read double");
        Require(reader.TryReadGuid(out Guid guid) && guid == new Guid("2C7E8A11-6B44-4E27-9F0A-0C6C0F53D8E1"), "read guid");
        Require(reader.TryReadEnum32(out int enumValue) && enumValue == 7, "read enum");
        Require(reader.TryReadBytes(16, out ReadOnlySpan<byte> bytes) && bytes.Length == 3 && bytes[2] == 3, "read bytes");
        Require(reader.IsComplete, "reader complete");
    }

    private static void StringsAreBoundedAndStrict()
    {
        byte[] buffer = new byte[256];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryWriteString("héllo", 32), "write string");
        Require(writer.TryWriteString(null, 32), "a null string writes Null");
        Require(writer.IsComplete, "writer complete");

        var reader = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(reader.TryReadString(32, out string? text) && text == "héllo", "read string");
        Require(reader.TryReadString(32, out string? absent) && absent is null, "read null string");
        Require(reader.IsComplete, "reader complete");

        var overCap = new PowerShellBridgeValueWriter(new byte[256]);
        Require(!overCap.TryWriteString(new string('a', 40), 32), "an over-cap string is rejected");
        Require(overCap.Failed, "an over-cap write latches failure");

        var embeddedNul = new PowerShellBridgeValueWriter(new byte[256]);
        Require(!embeddedNul.TryWriteString("a\0b", 32), "an embedded NUL is rejected");

        // A well-formed frame whose declared cap is smaller than the encoded
        // value must be refused on read, not silently truncated.
        var wide = new PowerShellBridgeValueWriter(buffer);
        Require(wide.TryWriteString(new string('a', 24), 64), "write a 24-byte string");
        var narrow = new PowerShellBridgeValueReader(buffer.AsSpan(0, wide.Length));
        Require(!narrow.TryReadString(8, out _), "a value above the reader's declared cap is rejected");
    }

    private static void ListsRoundTripAndEnforceTheirBound()
    {
        byte[] buffer = new byte[1024];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryBeginList(3, PowerShellBridgeTag.Int32, 8, out int scope), "begin list");
        for (int index = 0; index < 3; index++)
        {
            Require(writer.TryWriteInt32(index * 11), "write element");
        }

        Require(writer.TryEndList(scope), "end list");
        Require(writer.IsComplete, "writer complete");

        var reader = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(reader.TryReadListHeader(8, out int count, out byte element), "read list header");
        Require(count == 3 && element == PowerShellBridgeTag.Int32, "list metadata");
        for (int index = 0; index < count; index++)
        {
            Require(reader.TryReadInt32(out int value) && value == index * 11, "read element");
        }

        Require(reader.TryEndContainer() && reader.IsComplete, "close list");

        var overCap = new PowerShellBridgeValueWriter(new byte[1024]);
        Require(!overCap.TryBeginList(9, PowerShellBridgeTag.Int32, 8, out _), "an over-cap count is rejected");

        var narrow = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(!narrow.TryReadListHeader(2, out _, out _), "a count above the reader's declared cap is rejected");
    }

    private static void DataRoundTripsAndBindsItsIdentity()
    {
        byte[] buffer = new byte[512];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryBeginData(70UL, 2, out int scope), "begin data");
        Require(writer.TryWriteFieldOrdinal(1U) && writer.TryWriteString("why", 64), "field 1");
        Require(writer.TryWriteFieldOrdinal(2U) && writer.TryWriteInt32(9), "field 2");
        Require(writer.TryEndData(scope) && writer.IsComplete, "end data");

        var reader = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(reader.TryReadDataHeader(70UL, out int fields) && fields == 2, "read data header");
        Require(reader.TryReadFieldOrdinal(1U) && reader.TryReadString(64, out string? why) && why == "why", "read field 1");
        Require(reader.TryReadFieldOrdinal(2U) && reader.TryReadInt32(out int code) && code == 9, "read field 2");
        Require(reader.TryEndContainer() && reader.IsComplete, "close data");

        var wrongId = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(!wrongId.TryReadDataHeader(71UL, out _), "a foreign data identifier is rejected");

        var wrongOrdinal = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(wrongOrdinal.TryReadDataHeader(70UL, out _), "read data header");
        Require(!wrongOrdinal.TryReadFieldOrdinal(2U), "an out-of-order field ordinal is rejected");
    }

    private static void NestedListOfDataRoundTrips()
    {
        byte[] buffer = new byte[2048];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryBeginList(2, PowerShellBridgeTag.Data, 4, out int listScope), "begin list");
        for (int index = 0; index < 2; index++)
        {
            Require(writer.TryBeginData(70UL, 1, out int dataScope), "begin data");
            Require(writer.TryWriteFieldOrdinal(1U) && writer.TryWriteInt32(index), "field");
            Require(writer.TryEndData(dataScope), "end data");
        }

        Require(writer.TryEndList(listScope) && writer.IsComplete, "end list");

        var reader = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(reader.TryReadListHeader(4, out int count, out byte element), "read list header");
        Require(count == 2 && element == PowerShellBridgeTag.Data, "list metadata");
        for (int index = 0; index < count; index++)
        {
            Require(reader.TryReadDataHeader(70UL, out int fields) && fields == 1, "read data header");
            Require(reader.TryReadFieldOrdinal(1U) && reader.TryReadInt32(out int value) && value == index, "read field");
            Require(reader.TryEndContainer(), "close data");
        }

        Require(reader.TryEndContainer() && reader.IsComplete, "close list");
    }

    private static void HandlesCarryTheirDeclaredType()
    {
        byte[] buffer = new byte[64];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryWriteHandle(2UL, 4096UL) && writer.IsComplete, "write handle");

        var reader = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(reader.TryReadHandle(2UL, out ulong objectId) && objectId == 4096UL, "read handle");

        var wrongType = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(!wrongType.TryReadHandle(3UL, out _), "a handle of the wrong declared type is rejected");

        var zeroType = new PowerShellBridgeValueWriter(new byte[64]);
        Require(!zeroType.TryWriteHandle(0UL, 1UL), "an untyped handle is rejected");
    }

    private static void FramesRoundTripAndRejectMalformedHeaders()
    {
        byte[] frame = new byte[PowerShellBridgeWire.RequestHeaderSize + 16];
        var writer = new PowerShellBridgeValueWriter(frame.AsSpan(PowerShellBridgeWire.RequestHeaderSize));
        Require(writer.TryWriteInt32(5) && writer.IsComplete, "write argument");
        var header = new PowerShellBridgeRequestHeader(
            PowerShellBridgeFrameKind.Invoke, 1, 12U, 34UL, 56UL, 78U, writer.Length);
        Require(header.TryWrite(frame), "write request header");

        Require(
            PowerShellBridgeRequestHeader.TryRead(frame.AsSpan(0, PowerShellBridgeWire.RequestHeaderSize + writer.Length), out var read),
            "read request header");
        Require(
            read.FrameKind == PowerShellBridgeFrameKind.Invoke && read.ArgumentCount == 1 && read.MemberId == 12U &&
            read.ObjectId == 34UL && read.LeaseId == 56UL && read.Generation == 78U && read.BodyLength == writer.Length,
            "request header fields");

        byte[] badVersion = frame.ToArray();
        badVersion[0] = 1;
        Require(!PowerShellBridgeRequestHeader.TryRead(badVersion.AsSpan(0, PowerShellBridgeWire.RequestHeaderSize + writer.Length), out _), "a version-1 frame is rejected");

        byte[] badKind = frame.ToArray();
        badKind[1] = 9;
        Require(!PowerShellBridgeRequestHeader.TryRead(badKind.AsSpan(0, PowerShellBridgeWire.RequestHeaderSize + writer.Length), out _), "an undeclared frame kind is rejected");

        byte[] badLength = frame.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(badLength.AsSpan(28), writer.Length + 1);
        Require(!PowerShellBridgeRequestHeader.TryRead(badLength.AsSpan(0, PowerShellBridgeWire.RequestHeaderSize + writer.Length), out _), "a body length that disagrees with the buffer is rejected");

        byte[] reply = new byte[PowerShellBridgeWire.ReplyHeaderSize + 8];
        var replyWriter = new PowerShellBridgeValueWriter(reply.AsSpan(PowerShellBridgeWire.ReplyHeaderSize));
        Require(replyWriter.TryWriteNull() && replyWriter.IsComplete, "write reply value");
        Require(new PowerShellBridgeReplyHeader(PowerShellBridgeReplyKind.Value, replyWriter.Length).TryWrite(reply), "write reply header");
        Require(PowerShellBridgeReplyHeader.TryRead(reply, out var replyHeader), "read reply header");
        Require(replyHeader.ReplyKind == PowerShellBridgeReplyKind.Value && replyHeader.BodyLength == replyWriter.Length, "reply header fields");
    }

    private static void TruncatedAndOverlongValuesAreRejected()
    {
        byte[] buffer = new byte[64];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryWriteInt64(1234) && writer.IsComplete, "write int64");

        var truncated = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length - 1));
        Require(!truncated.TryReadInt64(out _), "a truncated value is rejected");

        var wrongTag = new PowerShellBridgeValueReader(buffer.AsSpan(0, writer.Length));
        Require(!wrongTag.TryReadInt32(out _), "a value of the wrong tag is rejected");

        var tooSmall = new PowerShellBridgeValueWriter(new byte[4]);
        Require(!tooSmall.TryWriteInt32(1), "a write past the buffer is rejected");
    }

    private static void ContainerLengthsMustAgreeWithTheirChildren()
    {
        byte[] buffer = new byte[256];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryBeginList(2, PowerShellBridgeTag.Int32, 4, out int scope), "begin list");
        Require(writer.TryWriteInt32(1) && writer.TryWriteInt32(2), "write elements");
        Require(writer.TryEndList(scope) && writer.IsComplete, "end list");

        // Shrink the container's declared length so it no longer covers its
        // second element. The reader must refuse rather than reinterpret.
        byte[] shrunk = buffer.AsSpan(0, writer.Length).ToArray();
        int declared = BinaryPrimitives.ReadInt32LittleEndian(shrunk.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(shrunk.AsSpan(4), declared - 12);
        var reader = new PowerShellBridgeValueReader(shrunk);
        Require(reader.TryReadListHeader(4, out int count, out _) && count == 2, "read shrunken list header");
        Require(reader.TryReadInt32(out _), "first element still fits");
        Require(!reader.TryReadInt32(out _), "an element past the container's declared end is rejected");
    }

    private static void DepthIsCapped()
    {
        byte[] buffer = new byte[1024];
        var writer = new PowerShellBridgeValueWriter(buffer);
        Require(writer.TryBeginList(1, PowerShellBridgeTag.Data, 2, out int a), "depth 1");
        Require(writer.TryBeginData(70UL, 1, out int b), "depth 2");
        Require(writer.TryWriteFieldOrdinal(1U), "field");
        Require(writer.TryBeginList(1, PowerShellBridgeTag.Int32, 2, out int c), "depth 3");
        Require(writer.TryWriteInt32(1), "leaf");
        Require(writer.TryEndList(c) && writer.TryEndData(b) && writer.TryEndList(a), "unwind");
        Require(writer.IsComplete, "writer complete");

        var deep = new PowerShellBridgeValueWriter(new byte[1024]);
        Require(deep.TryBeginList(1, PowerShellBridgeTag.List, 2, out _), "open 1");
        Require(deep.TryBeginList(1, PowerShellBridgeTag.List, 2, out _), "open 2");
        Require(deep.TryBeginList(1, PowerShellBridgeTag.List, 2, out _), "open 3");
        Require(deep.TryBeginList(1, PowerShellBridgeTag.List, 2, out _), "open 4");
        Require(!deep.TryBeginList(1, PowerShellBridgeTag.Int32, 2, out _), "a fifth container exceeds the depth cap");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Bridge wire fixture failed: {what}.");
        }
    }
}

