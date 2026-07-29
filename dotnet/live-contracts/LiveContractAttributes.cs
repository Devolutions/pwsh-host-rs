#nullable enable

using System;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

[AttributeUsage(AttributeTargets.Interface)]
public sealed class LiveContractAttribute : Attribute
{
    public LiveContractAttribute(string id, int majorVersion, int minorVersion)
        : this(id, majorVersion, minorVersion, string.Empty)
    {
    }

    public LiveContractAttribute(string id, int majorVersion, int minorVersion, string interfaceId)
    {
        Id = id;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        InterfaceId = interfaceId;
    }

    public string Id { get; }
    public int MajorVersion { get; }
    public int MinorVersion { get; }
    public string InterfaceId { get; }
}

[AttributeUsage(AttributeTargets.Interface)]
public sealed class LiveObjectAttribute : Attribute
{
    public LiveObjectAttribute(ulong id) => Id = id;
    public ulong Id { get; }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class LiveMemberAttribute : Attribute
{
    public LiveMemberAttribute(uint getterOrMethodId) => GetterOrMethodId = getterOrMethodId;
    public uint GetterOrMethodId { get; }
    public uint SetterId { get; set; }
    public ulong ResultObjectId { get; set; }
    public int MaximumUtf8Bytes { get; set; }
    public int MaximumCollectionCount { get; set; }
}
