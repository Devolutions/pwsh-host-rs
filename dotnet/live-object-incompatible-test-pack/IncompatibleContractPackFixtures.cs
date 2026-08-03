#nullable enable

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack;

/// <summary>
/// Shared allocation helpers for the contract-pack rejection fixtures. Each fixture
/// publishes a process-lifetime table from native memory, mirroring how a real payload
/// adapter exposes <c>GetLiveObjectContractPackV1</c>.
/// </summary>
internal static unsafe class ContractPackFixture
{
    internal const int EFail = unchecked((int)0x80004005);

    internal static IntPtr CreateApi(
        uint abiVersion,
        ReadOnlySpan<PowerShellLiveObjectContract> contracts,
        delegate* unmanaged<IntPtr, IntPtr*, int> create,
        delegate* unmanaged<IntPtr, void> release)
    {
        NativeLiveObjectContractDescriptor* descriptors =
            (NativeLiveObjectContractDescriptor*)NativeMemory.Alloc(
                (nuint)contracts.Length,
                (nuint)sizeof(NativeLiveObjectContractDescriptor));
        for (int index = 0; index < contracts.Length; index++)
        {
            descriptors[index] = contracts[index].ToNative();
        }

        return CreateApi(abiVersion, descriptors, (uint)contracts.Length, create, release);
    }

    /// <summary>
    /// Publishes descriptors that were written directly rather than projected from
    /// <see cref="PowerShellLiveObjectContract"/>. A pack is native memory the consumer
    /// does not control, so a fixture has to be able to declare a descriptor the managed
    /// constructor would refuse to build.
    /// </summary>
    internal static IntPtr CreateRawApi(
        uint abiVersion,
        ReadOnlySpan<NativeLiveObjectContractDescriptor> contracts,
        delegate* unmanaged<IntPtr, IntPtr*, int> create,
        delegate* unmanaged<IntPtr, void> release)
    {
        NativeLiveObjectContractDescriptor* descriptors =
            (NativeLiveObjectContractDescriptor*)NativeMemory.Alloc(
                (nuint)contracts.Length,
                (nuint)sizeof(NativeLiveObjectContractDescriptor));
        for (int index = 0; index < contracts.Length; index++)
        {
            descriptors[index] = contracts[index];
        }

        return CreateApi(abiVersion, descriptors, (uint)contracts.Length, create, release);
    }

    private static IntPtr CreateApi(
        uint abiVersion,
        NativeLiveObjectContractDescriptor* descriptors,
        uint contractCount,
        delegate* unmanaged<IntPtr, IntPtr*, int> create,
        delegate* unmanaged<IntPtr, void> release)
    {
        NativeLiveObjectContractPackApi* api =
            (NativeLiveObjectContractPackApi*)NativeMemory.Alloc((nuint)sizeof(NativeLiveObjectContractPackApi));
        *api = new NativeLiveObjectContractPackApi
        {
            Size = (nuint)sizeof(NativeLiveObjectContractPackApi),
            AbiVersion = abiVersion,
            ContractCount = contractCount,
            Contracts = descriptors,
            CreatePayloadProxy = (IntPtr)create,
            ReleasePayloadProxy = (IntPtr)release,
        };
        return (IntPtr)api;
    }
}

/// <summary>
/// Declares a well-formed contract that only flows payload-to-consumer. The registry
/// requires <see cref="PowerShellLiveObjectDirection.ConsumerToSession"/>, so the pack
/// must be rejected for an unsupported direction rather than partially registered.
/// </summary>
public static unsafe class DirectionViolationLiveObjectTestPack
{
    private static readonly IntPtr Api = CreateApi();

    [UnmanagedCallersOnly]
    public static IntPtr GetLiveObjectContractPackV1()
    {
        return Api;
    }

    [UnmanagedCallersOnly]
    private static int CreatePayloadProxy(IntPtr _, IntPtr* __)
    {
        return ContractPackFixture.EFail;
    }

    [UnmanagedCallersOnly]
    private static void ReleasePayloadProxy(IntPtr _)
    {
    }

    private static IntPtr CreateApi()
    {
        PowerShellLiveObjectContract contract = new(
            Guid.Parse("2C4B7B27-1F5D-4C3E-9E1B-4F0C2A6D8E51"),
            majorVersion: 1,
            minorVersion: 0,
            PowerShellLiveObjectDirection.PayloadToConsumer);

        return ContractPackFixture.CreateApi(
            abiVersion: 1,
            new ReadOnlySpan<PowerShellLiveObjectContract>(in contract),
            &CreatePayloadProxy,
            &ReleasePayloadProxy);
    }
}

/// <summary>
/// Declares the same interface identifier twice inside a single pack. Duplicate
/// identifiers must be rejected even when the surrounding pack is otherwise valid.
/// </summary>
public static unsafe class DuplicateContractLiveObjectTestPack
{
    private static readonly IntPtr Api = CreateApi();

    [UnmanagedCallersOnly]
    public static IntPtr GetLiveObjectContractPackV1()
    {
        return Api;
    }

    [UnmanagedCallersOnly]
    private static int CreatePayloadProxy(IntPtr _, IntPtr* __)
    {
        return ContractPackFixture.EFail;
    }

    [UnmanagedCallersOnly]
    private static void ReleasePayloadProxy(IntPtr _)
    {
    }

    private static IntPtr CreateApi()
    {
        Guid interfaceId = Guid.Parse("6E1D0A93-8C77-4A0B-9D44-1B0A5C2E7F36");
        Span<PowerShellLiveObjectContract> contracts =
        [
            new PowerShellLiveObjectContract(
                interfaceId,
                majorVersion: 1,
                minorVersion: 0,
                PowerShellLiveObjectDirection.ConsumerToSession),
            new PowerShellLiveObjectContract(
                interfaceId,
                majorVersion: 2,
                minorVersion: 0,
                PowerShellLiveObjectDirection.ConsumerToSession),
        ];

        return ContractPackFixture.CreateApi(
            abiVersion: 1,
            contracts,
            &CreatePayloadProxy,
            &ReleasePayloadProxy);
    }
}

/// <summary>
/// Declares the built-in live-object probe interface identifier. Re-registering an
/// identifier the payload already owns must be rejected instead of shadowing it.
/// </summary>
public static unsafe class ReservedContractLiveObjectTestPack
{
    private static readonly IntPtr Api = CreateApi();

    [UnmanagedCallersOnly]
    public static IntPtr GetLiveObjectContractPackV1()
    {
        return Api;
    }

    [UnmanagedCallersOnly]
    private static int CreatePayloadProxy(IntPtr _, IntPtr* __)
    {
        return ContractPackFixture.EFail;
    }

    [UnmanagedCallersOnly]
    private static void ReleasePayloadProxy(IntPtr _)
    {
    }

    private static IntPtr CreateApi()
    {
        PowerShellLiveObjectContract contract = new(
            Guid.Parse("9A2A6F07-319B-422A-A7A4-6C3A32C7B379"),
            majorVersion: 3,
            minorVersion: 0,
            PowerShellLiveObjectDirection.ConsumerToSession);

        return ContractPackFixture.CreateApi(
            abiVersion: 1,
            new ReadOnlySpan<PowerShellLiveObjectContract>(in contract),
            &CreatePayloadProxy,
            &ReleasePayloadProxy);
    }
}

/// <summary>
/// Declares a pack ABI version the payload does not implement. Pack ABI changes are a
/// coordinated breaking release, so an unknown version must fail activation outright
/// rather than being negotiated down.
/// </summary>
public static unsafe class UnsupportedAbiLiveObjectTestPack
{
    private static readonly IntPtr Api = CreateApi();

    [UnmanagedCallersOnly]
    public static IntPtr GetLiveObjectContractPackV1()
    {
        return Api;
    }

    [UnmanagedCallersOnly]
    private static int CreatePayloadProxy(IntPtr _, IntPtr* __)
    {
        return ContractPackFixture.EFail;
    }

    [UnmanagedCallersOnly]
    private static void ReleasePayloadProxy(IntPtr _)
    {
    }

    private static IntPtr CreateApi()
    {
        PowerShellLiveObjectContract contract = new(
            Guid.Parse("B0F5C1D2-3A46-4E58-8C7A-9D2B1E4F6083"),
            majorVersion: 1,
            minorVersion: 0,
            PowerShellLiveObjectDirection.ConsumerToSession);

        return ContractPackFixture.CreateApi(
            abiVersion: 2,
            new ReadOnlySpan<PowerShellLiveObjectContract>(in contract),
            &CreatePayloadProxy,
            &ReleasePayloadProxy);
    }
}

/// <summary>
/// Declares the Bridge Contract v2 marker on its own, without the
/// <see cref="PowerShellLiveObjectDirection.ConsumerToSession"/> direction the marker
/// only ever augments. The managed contract type refuses to build such a descriptor,
/// so this fixture writes the raw bits directly — which is exactly what an untrusted
/// native pack can do. A contract that claims to speak v2 but reads as "not a
/// consumer-to-session surface" would slip past every existing direction check, so it
/// must be rejected at registration rather than registered and later misrouted.
/// </summary>
public static unsafe class BridgeMarkerWithoutDirectionLiveObjectTestPack
{
    private static readonly IntPtr Api = CreateApi();

    [UnmanagedCallersOnly]
    public static IntPtr GetLiveObjectContractPackV1()
    {
        return Api;
    }

    [UnmanagedCallersOnly]
    private static int CreatePayloadProxy(IntPtr _, IntPtr* __)
    {
        return ContractPackFixture.EFail;
    }

    [UnmanagedCallersOnly]
    private static void ReleasePayloadProxy(IntPtr _)
    {
    }

    private static IntPtr CreateApi()
    {
        Guid interfaceId = Guid.Parse("6E1D4C08-52A7-4B36-9F0D-8B7A1E5C2D64");
        Span<byte> interfaceIdBytes = stackalloc byte[16];
        if (!interfaceId.TryWriteBytes(interfaceIdBytes))
        {
            throw new InvalidOperationException("The fixture interface identifier is invalid.");
        }

        NativeLiveObjectContractDescriptor descriptor = new()
        {
            Size = checked((uint)sizeof(NativeLiveObjectContractDescriptor)),
            Directions = (uint)PowerShellLiveObjectDirection.BridgeContract,
            InterfaceIdLow = BinaryPrimitives.ReadUInt64LittleEndian(interfaceIdBytes),
            InterfaceIdHigh = BinaryPrimitives.ReadUInt64LittleEndian(interfaceIdBytes[8..]),
            MajorVersion = 1,
            MinorVersion = 0,
            Reserved = 0,
        };

        return ContractPackFixture.CreateRawApi(
            abiVersion: 1,
            new ReadOnlySpan<NativeLiveObjectContractDescriptor>(in descriptor),
            &CreatePayloadProxy,
            &ReleasePayloadProxy);
    }
}
