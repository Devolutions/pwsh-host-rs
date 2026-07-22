#nullable enable

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Devolutions.PowerShell.Ffi.LiveObjects;

[Flags]
public enum PowerShellLiveObjectDirection : uint
{
    None = 0,
    PayloadToConsumer = 1,
    ConsumerToSession = 1 << 1,
}

/// <summary>
/// Identifies one explicitly registered cross-runtime live-object contract.
/// </summary>
public readonly struct PowerShellLiveObjectContract : IEquatable<PowerShellLiveObjectContract>
{
    private const PowerShellLiveObjectDirection KnownDirections =
        PowerShellLiveObjectDirection.PayloadToConsumer |
        PowerShellLiveObjectDirection.ConsumerToSession;

    public PowerShellLiveObjectContract(
        Guid interfaceId,
        ushort majorVersion,
        ushort minorVersion,
        PowerShellLiveObjectDirection directions)
    {
        InterfaceId = interfaceId;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        Directions = directions;
        Validate();
    }

    public Guid InterfaceId { get; }

    public ushort MajorVersion { get; }

    public ushort MinorVersion { get; }

    public PowerShellLiveObjectDirection Directions { get; }

    public bool Equals(PowerShellLiveObjectContract other)
    {
        return InterfaceId == other.InterfaceId &&
            MajorVersion == other.MajorVersion &&
            MinorVersion == other.MinorVersion &&
            Directions == other.Directions;
    }

    public override bool Equals(object? obj)
    {
        return obj is PowerShellLiveObjectContract other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(InterfaceId, MajorVersion, MinorVersion, Directions);
    }

    public static bool operator ==(PowerShellLiveObjectContract left, PowerShellLiveObjectContract right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(PowerShellLiveObjectContract left, PowerShellLiveObjectContract right)
    {
        return !left.Equals(right);
    }

    internal NativeLiveObjectContractDescriptor ToNative()
    {
        Validate();
        Span<byte> interfaceId = stackalloc byte[16];
        if (!InterfaceId.TryWriteBytes(interfaceId))
        {
            throw new InvalidOperationException("The live object contract interface identifier is invalid.");
        }

        return new NativeLiveObjectContractDescriptor
        {
            Size = checked((uint)Marshal.SizeOf<NativeLiveObjectContractDescriptor>()),
            Directions = checked((uint)Directions),
            InterfaceIdLow = BinaryPrimitives.ReadUInt64LittleEndian(interfaceId),
            InterfaceIdHigh = BinaryPrimitives.ReadUInt64LittleEndian(interfaceId[8..]),
            MajorVersion = MajorVersion,
            MinorVersion = MinorVersion,
        };
    }

    internal static PowerShellLiveObjectContract FromNative(NativeLiveObjectContractDescriptor descriptor)
    {
        if (descriptor.Size < (uint)Marshal.SizeOf<NativeLiveObjectContractDescriptor>() ||
            descriptor.Reserved != 0)
        {
            throw new InvalidOperationException("Live object contract descriptor is invalid.");
        }

        Span<byte> interfaceId = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(interfaceId, descriptor.InterfaceIdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(interfaceId[8..], descriptor.InterfaceIdHigh);
        return new PowerShellLiveObjectContract(
            new Guid(interfaceId),
            descriptor.MajorVersion,
            descriptor.MinorVersion,
            checked((PowerShellLiveObjectDirection)descriptor.Directions));
    }

    private void Validate()
    {
        if (InterfaceId == Guid.Empty ||
            MajorVersion == 0 ||
            Directions == PowerShellLiveObjectDirection.None ||
            (Directions & ~KnownDirections) != 0)
        {
            throw new ArgumentException("Live object contract metadata is invalid.");
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeLiveObjectContractDescriptor
{
    public uint Size;
    public uint Directions;
    public ulong InterfaceIdLow;
    public ulong InterfaceIdHigh;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeLiveObjectContractPackApi
{
    public nuint Size;
    public uint AbiVersion;
    public uint ContractCount;
    public NativeLiveObjectContractDescriptor* Contracts;
    public IntPtr CreatePayloadProxy;
    public IntPtr ReleasePayloadProxy;
}
