#nullable enable

using System.Runtime.InteropServices;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack;

public static unsafe class IncompatibleLiveObjectTestPack
{
    private const int EFail = unchecked((int)0x80004005);
    private static readonly IntPtr Api = CreateApi();

    [UnmanagedCallersOnly]
    public static IntPtr GetLiveObjectContractPackV1()
    {
        return Api;
    }

    [UnmanagedCallersOnly]
    private static int CreatePayloadProxy(IntPtr _, IntPtr* __)
    {
        return EFail;
    }

    [UnmanagedCallersOnly]
    private static void ReleasePayloadProxy(IntPtr _)
    {
    }

    private static IntPtr CreateApi()
    {
        NativeLiveObjectContractDescriptor* contract =
            (NativeLiveObjectContractDescriptor*)NativeMemory.Alloc((nuint)sizeof(NativeLiveObjectContractDescriptor));
        *contract = new PowerShellLiveObjectContract(
            Guid.Parse("C9A4FEA0-4EA6-48BE-8B4F-B30BB328CCBD"),
            majorVersion: 1,
            minorVersion: 1,
            PowerShellLiveObjectDirection.ConsumerToSession).ToNative();

        NativeLiveObjectContractPackApi* api =
            (NativeLiveObjectContractPackApi*)NativeMemory.Alloc((nuint)sizeof(NativeLiveObjectContractPackApi));
        *api = new NativeLiveObjectContractPackApi
        {
            Size = (nuint)sizeof(NativeLiveObjectContractPackApi),
            AbiVersion = 1,
            ContractCount = 1,
            Contracts = contract,
            CreatePayloadProxy = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&CreatePayloadProxy,
            ReleasePayloadProxy = (IntPtr)(delegate* unmanaged<IntPtr, void>)&ReleasePayloadProxy,
        };
        return (IntPtr)api;
    }
}
