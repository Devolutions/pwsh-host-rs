#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Devolutions.MultiPwsh.LiveContract.Generator;

/// <summary>
/// Produces the canonical descriptor byte sequence and its SHA-256 hash.
/// </summary>
/// <remarks>
/// Every integer is little-endian and every name is a <c>u32</c> byte length
/// followed by strict UTF-8. Nothing is derived from
/// <c>ISymbol.ToDisplayString</c>, assembly identity, namespace, culture,
/// nullable project settings, or any dictionary or <c>GetMembers()</c>
/// iteration order: the analyser has already sorted every collection by the
/// unique identifier the author declared, so no tie is possible.
/// </remarks>
internal static class BridgeContractDescriptor
{
    private const uint Magic = 0x32574D42;
    private const uint DescriptorVersion = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Compute(BridgeContractModel contract)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, Magic);
        WriteUInt32(stream, DescriptorVersion);
        WriteName(stream, contract.ContractId);
        WriteUInt32(stream, (uint)contract.MajorVersion);
        WriteUInt32(stream, (uint)contract.MinorVersion);
        WriteGuid(stream, contract.TransportInterfaceId);
        WriteUInt64(stream, contract.RootObject.Id);

        WriteUInt32(stream, (uint)contract.Enums.Count);
        foreach (BridgeEnumModel model in contract.Enums)
        {
            WriteUInt64(stream, model.Id);
            WriteName(stream, model.Name);
            WriteUInt32(stream, (uint)model.Members.Count);
            foreach (BridgeEnumMemberModel member in model.Members)
            {
                WriteInt32(stream, member.Value);
                WriteName(stream, member.Name);
            }
        }

        WriteUInt32(stream, (uint)contract.Data.Count);
        foreach (BridgeDataModel model in contract.Data)
        {
            WriteUInt64(stream, model.Id);
            WriteName(stream, model.Name);
            WriteUInt32(stream, (uint)model.Fields.Count);
            foreach (BridgeFieldModel field in model.Fields)
            {
                WriteUInt32(stream, field.Ordinal);
                WriteName(stream, field.Name);
                WriteTypeRef(stream, field.Type);
            }
        }

        WriteUInt32(stream, (uint)contract.Objects.Count);
        foreach (BridgeObjectModel model in contract.Objects)
        {
            WriteUInt64(stream, model.Id);
            WriteName(stream, model.Name);
            WriteUInt32(stream, model.ReleaseId);
            WriteUInt32(stream, (uint)model.Members.Count);
            foreach (BridgeMemberModel member in model.Members)
            {
                WriteUInt32(stream, member.Ordinal);
                WriteName(stream, member.Name);
                stream.WriteByte((byte)member.Kind);
                stream.WriteByte(member.Mutation);
                stream.WriteByte(member.Permission);
                stream.WriteByte(member.Result.IsNullable ? (byte)1 : (byte)0);
                WriteUInt64(stream, member.ErrorDataId);
                WriteUInt64(stream, member.OrderingKey);
                WriteTypeRef(stream, member.Result);
                WriteUInt32(stream, (uint)member.Parameters.Count);
                foreach (BridgeParameterModel parameter in member.Parameters)
                {
                    WriteName(stream, parameter.Name);
                    WriteTypeRef(stream, parameter.Type);
                }
            }
        }

        byte[] descriptor = stream.ToArray();
        contract.Descriptor = descriptor;
        using SHA256 sha256 = SHA256.Create();
        contract.DescriptorHash = sha256.ComputeHash(descriptor);
    }

    internal static string ToHex(IReadOnlyList<byte> value)
    {
        var builder = new StringBuilder(value.Count * 2);
        for (int index = 0; index < value.Count; index++)
        {
            builder.Append(value[index].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static void WriteTypeRef(Stream stream, BridgeTypeRef type)
    {
        byte tag = type.IsNullable ? (byte)(type.Tag | 0x80) : type.Tag;
        stream.WriteByte(tag);
        switch (type.Tag)
        {
            case BridgeTag.Utf8String:
            case BridgeTag.Bytes:
                WriteUInt32(stream, (uint)type.MaximumBytes);
                break;
            case BridgeTag.Enum32:
            case BridgeTag.Handle:
            case BridgeTag.Data:
                WriteUInt64(stream, type.TypeId);
                break;
            case BridgeTag.List:
                WriteUInt32(stream, (uint)type.MaximumCount);
                WriteTypeRef(stream, type.Element!);
                break;
            default:
                break;
        }
    }

    private static void WriteName(Stream stream, string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteGuid(Stream stream, string value)
    {
        byte[] bytes = Guid.Parse(value).ToByteArray();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteInt32(Stream stream, int value) => WriteUInt32(stream, unchecked((uint)value));

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        WriteUInt32(stream, (uint)value);
        WriteUInt32(stream, (uint)(value >> 32));
    }
}
