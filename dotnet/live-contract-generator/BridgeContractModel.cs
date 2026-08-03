#nullable enable

using Microsoft.CodeAnalysis;
using System.Collections.Generic;

namespace Devolutions.MultiPwsh.LiveContract.Generator;

/// <summary>Wire tags, mirrored from <c>PowerShellBridgeTag</c>.</summary>
internal static class BridgeTag
{
    internal const byte Null = 0;
    internal const byte Bool = 1;
    internal const byte Int32 = 2;
    internal const byte Int64 = 3;
    internal const byte Double = 4;
    internal const byte Utf8String = 5;
    internal const byte Bytes = 6;
    internal const byte Guid = 7;
    internal const byte Enum32 = 8;
    internal const byte Handle = 9;
    internal const byte List = 10;
    internal const byte Data = 11;
    internal const byte Error = 12;
}

/// <summary>Structural limits, mirrored from the normative specification.</summary>
internal static class BridgeLimits
{
    internal const int MaximumObjects = 64;
    internal const int MaximumGraphDepth = 8;
    internal const int MaximumMembersPerObject = 64;
    internal const int MaximumParameters = 8;
    internal const int MaximumDataFields = 32;
    internal const int MaximumEnumMembers = 64;
    internal const int MaximumUtf8Bytes = 8192;
    internal const int MaximumCollectionCount = 4096;
    internal const int MaximumFrameBytes = 65536;
    internal const int MaximumReliableEvents = 64;

    internal const int ValueHeaderSize = 8;
    internal const int RequestHeaderSize = 32;
    internal const int ReplyHeaderSize = 8;
    internal const int ListPrologueSize = 8;
    internal const int DataPrologueSize = 16;
    internal const int ErrorPrologueSize = 8;
    internal const int DataFieldPrologueSize = 8;

    /// <summary>Saturating budget arithmetic. Overflow is impossible above this ceiling.</summary>
    internal const int Saturated = MaximumFrameBytes + 1;

    internal static int Add(int left, int right)
    {
        long total = (long)left + right;
        return total >= Saturated ? Saturated : (int)total;
    }

    internal static int Multiply(int left, int right)
    {
        long total = (long)left * right;
        return total >= Saturated ? Saturated : (int)total;
    }
}

/// <summary>Member record kinds, mirrored from <c>BridgeMemberKind</c>.</summary>
internal enum BridgeRecordKind : byte
{
    Getter = 1,
    Setter = 2,
    Method = 3,
    Event = 4,
    ReliableEvent = 5,
}

/// <summary>
/// One fully resolved value position. Every bound is declared at the position
/// that uses it, so no bound is ever inherited or inferred.
/// </summary>
internal sealed class BridgeTypeRef
{
    internal BridgeTypeRef(byte tag, ITypeSymbol? symbol)
    {
        Tag = tag;
        Symbol = symbol;
    }

    internal byte Tag { get; }

    internal ITypeSymbol? Symbol { get; }

    internal bool IsNullable { get; set; }

    /// <summary>The declared UTF-8 or opaque byte bound for a string or bytes position.</summary>
    internal int MaximumBytes { get; set; }

    /// <summary>The declared element bound for a collection position.</summary>
    internal int MaximumCount { get; set; }

    /// <summary>The enumeration, object, or data identifier this position refers to.</summary>
    internal ulong TypeId { get; set; }

    /// <summary>The element position of a collection.</summary>
    internal BridgeTypeRef? Element { get; set; }

    /// <summary>The worst-case encoded size of this position, saturating at the frame ceiling.</summary>
    internal int MaximumEncodedBytes(IReadOnlyDictionary<ulong, BridgeDataModel> data)
    {
        switch (Tag)
        {
            case BridgeTag.Null:
                return BridgeLimits.ValueHeaderSize;
            case BridgeTag.Bool:
                return BridgeLimits.ValueHeaderSize + 1;
            case BridgeTag.Int32:
            case BridgeTag.Enum32:
                return BridgeLimits.ValueHeaderSize + 4;
            case BridgeTag.Int64:
            case BridgeTag.Double:
                return BridgeLimits.ValueHeaderSize + 8;
            case BridgeTag.Guid:
            case BridgeTag.Handle:
                return BridgeLimits.ValueHeaderSize + 16;
            case BridgeTag.Utf8String:
            case BridgeTag.Bytes:
                return BridgeLimits.Add(BridgeLimits.ValueHeaderSize, MaximumBytes);
            case BridgeTag.List:
                return BridgeLimits.Add(
                    BridgeLimits.ValueHeaderSize + BridgeLimits.ListPrologueSize,
                    BridgeLimits.Multiply(MaximumCount, Element!.MaximumEncodedBytes(data)));
            case BridgeTag.Data:
                return data.TryGetValue(TypeId, out BridgeDataModel? model)
                    ? model.MaximumEncodedBytes(data)
                    : BridgeLimits.Saturated;
            default:
                return BridgeLimits.Saturated;
        }
    }
}

/// <summary>One method or accessor parameter.</summary>
internal sealed class BridgeParameterModel
{
    internal BridgeParameterModel(string name, BridgeTypeRef type)
    {
        Name = name;
        Type = type;
    }

    internal string Name { get; }

    internal BridgeTypeRef Type { get; }
}

/// <summary>
/// One descriptor member record. A mutable property expands into an independent
/// getter record and setter record so each carries its own authorization.
/// </summary>
internal sealed class BridgeMemberModel
{
    internal BridgeMemberModel(
        BridgeObjectModel owner,
        ISymbol symbol,
        string name,
        uint ordinal,
        BridgeRecordKind kind,
        byte mutation,
        byte permission,
        ulong errorDataId,
        ulong orderingKey,
        BridgeTypeRef result,
        IReadOnlyList<BridgeParameterModel> parameters)
    {
        Owner = owner;
        Symbol = symbol;
        Name = name;
        Ordinal = ordinal;
        Kind = kind;
        Mutation = mutation;
        Permission = permission;
        ErrorDataId = errorDataId;
        OrderingKey = orderingKey;
        Result = result;
        Parameters = parameters;
    }

    internal BridgeObjectModel Owner { get; }

    internal ISymbol Symbol { get; }

    /// <summary>The declared CLR member name. Names never travel on the wire.</summary>
    internal string Name { get; }

    internal uint Ordinal { get; }

    internal BridgeRecordKind Kind { get; }

    internal byte Mutation { get; }

    internal byte Permission { get; }

    internal ulong ErrorDataId { get; }

    internal ulong OrderingKey { get; }

    internal BridgeTypeRef Result { get; }

    internal IReadOnlyList<BridgeParameterModel> Parameters { get; }

    /// <summary>Maximum unacknowledged records retained by a reliable event stream.</summary>
    internal int MaximumRetainedEvents { get; set; }

    internal int MaximumRequestBytes(IReadOnlyDictionary<ulong, BridgeDataModel> data)
    {
        int total = BridgeLimits.RequestHeaderSize;
        foreach (BridgeParameterModel parameter in Parameters)
        {
            total = BridgeLimits.Add(total, parameter.Type.MaximumEncodedBytes(data));
        }

        return total;
    }

    internal int MaximumReplyBytes(IReadOnlyDictionary<ulong, BridgeDataModel> data)
    {
        int value = BridgeLimits.Add(BridgeLimits.ReplyHeaderSize, Result.MaximumEncodedBytes(data));
        if (ErrorDataId == 0)
        {
            return value;
        }

        int error = BridgeLimits.Add(
            BridgeLimits.ReplyHeaderSize + BridgeLimits.ValueHeaderSize + BridgeLimits.ErrorPrologueSize,
            data.TryGetValue(ErrorDataId, out BridgeDataModel? model)
                ? model.MaximumEncodedBytes(data)
                : BridgeLimits.Saturated);
        return error > value ? error : value;
    }
}

/// <summary>One declared object interface, including the root.</summary>
internal sealed class BridgeObjectModel
{
    internal BridgeObjectModel(INamedTypeSymbol symbol, ulong id, uint releaseId)
    {
        Symbol = symbol;
        Id = id;
        ReleaseId = releaseId;
    }

    internal INamedTypeSymbol Symbol { get; }

    internal ulong Id { get; }

    internal uint ReleaseId { get; }

    internal string Name => Symbol.Name;

    internal List<BridgeMemberModel> Members { get; } = new();
}

/// <summary>One field of a copied data contract.</summary>
internal sealed class BridgeFieldModel
{
    internal BridgeFieldModel(ISymbol symbol, string name, uint ordinal, BridgeTypeRef type)
    {
        Symbol = symbol;
        Name = name;
        Ordinal = ordinal;
        Type = type;
    }

    internal ISymbol Symbol { get; }

    internal string Name { get; }

    internal uint Ordinal { get; }

    internal BridgeTypeRef Type { get; }
}

/// <summary>One copied data-transfer contract carried by value.</summary>
internal sealed class BridgeDataModel
{
    internal BridgeDataModel(INamedTypeSymbol symbol, ulong id)
    {
        Symbol = symbol;
        Id = id;
    }

    internal INamedTypeSymbol Symbol { get; }

    internal ulong Id { get; }

    internal string Name => Symbol.Name;

    internal List<BridgeFieldModel> Fields { get; } = new();

    internal int MaximumEncodedBytes(IReadOnlyDictionary<ulong, BridgeDataModel> data)
    {
        int total = BridgeLimits.ValueHeaderSize + BridgeLimits.DataPrologueSize;
        foreach (BridgeFieldModel field in Fields)
        {
            total = BridgeLimits.Add(total, BridgeLimits.DataFieldPrologueSize);
            total = BridgeLimits.Add(total, field.Type.MaximumEncodedBytes(data));
        }

        return total;
    }
}

/// <summary>One member of a closed enumeration.</summary>
internal sealed class BridgeEnumMemberModel
{
    internal BridgeEnumMemberModel(string name, int value)
    {
        Name = name;
        Value = value;
    }

    internal string Name { get; }

    internal int Value { get; }
}

/// <summary>One closed enumeration. <c>[Flags]</c> declarations are rejected.</summary>
internal sealed class BridgeEnumModel
{
    internal BridgeEnumModel(INamedTypeSymbol symbol, ulong id)
    {
        Symbol = symbol;
        Id = id;
    }

    internal INamedTypeSymbol Symbol { get; }

    internal ulong Id { get; }

    internal string Name => Symbol.Name;

    internal List<BridgeEnumMemberModel> Members { get; } = new();
}

/// <summary>The fully analysed contract, independent of generator mode.</summary>
internal sealed class BridgeContractModel
{
    internal BridgeContractModel(
        INamedTypeSymbol root,
        string @namespace,
        string contractId,
        int majorVersion,
        int minorVersion,
        string transportInterfaceId,
        string transportInterfaceType,
        List<BridgeObjectModel> objects,
        List<BridgeDataModel> data,
        List<BridgeEnumModel> enums)
    {
        Root = root;
        Namespace = @namespace;
        ContractId = contractId;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        TransportInterfaceId = transportInterfaceId;
        TransportInterfaceType = transportInterfaceType;
        Objects = objects;
        Data = data;
        Enums = enums;
        DataById = new Dictionary<ulong, BridgeDataModel>();
        foreach (BridgeDataModel model in data)
        {
            DataById[model.Id] = model;
        }
    }

    internal INamedTypeSymbol Root { get; }

    internal string Namespace { get; }

    internal string ContractId { get; }

    internal int MajorVersion { get; }

    internal int MinorVersion { get; }

    internal string TransportInterfaceId { get; }

    /// <summary>The fully qualified name of the hand-declared COM transport interface.</summary>
    internal string TransportInterfaceType { get; }

    /// <summary>Objects in ascending identifier order.</summary>
    internal List<BridgeObjectModel> Objects { get; }

    /// <summary>Data contracts in ascending identifier order.</summary>
    internal List<BridgeDataModel> Data { get; }

    /// <summary>Enumerations in ascending identifier order.</summary>
    internal List<BridgeEnumModel> Enums { get; }

    internal Dictionary<ulong, BridgeDataModel> DataById { get; }

    internal BridgeObjectModel RootObject => Objects.Find(model =>
        SymbolEqualityComparer.Default.Equals(model.Symbol, Root))!;

    internal byte[] Descriptor { get; set; } = System.Array.Empty<byte>();

    internal byte[] DescriptorHash { get; set; } = System.Array.Empty<byte>();
}
