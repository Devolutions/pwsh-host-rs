#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.LiveObject.TestPack;

public static unsafe class LiveObjectTestPack
{
    private const int EFail = unchecked((int)0x80004005);
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static readonly IntPtr Api = CreateApi();

    [UnmanagedCallersOnly]
    public static IntPtr GetLiveObjectContractPackV1()
    {
        return Api;
    }

    [UnmanagedCallersOnly]
    private static int CreatePayloadProxy(IntPtr comObject, IntPtr* proxyHandle)
    {
        if (comObject == IntPtr.Zero || proxyHandle == null)
        {
            return EFail;
        }

        *proxyHandle = IntPtr.Zero;
        try
        {
            var proxy = TestCountProxy.Create(comObject);
            *proxyHandle = GCHandle.ToIntPtr(GCHandle.Alloc(proxy));
            return 0;
        }
        catch (COMException exception)
        {
            return exception.HResult;
        }
        catch
        {
            return EFail;
        }
    }

    [UnmanagedCallersOnly]
    private static void ReleasePayloadProxy(IntPtr proxyHandle)
    {
        if (proxyHandle != IntPtr.Zero)
        {
            GCHandle handle = GCHandle.FromIntPtr(proxyHandle);
            if (handle.Target is IDisposable proxy)
            {
                proxy.Dispose();
            }

            handle.Free();
        }
    }

    private static IntPtr CreateApi()
    {
        NativeLiveObjectContractDescriptor* contract =
            (NativeLiveObjectContractDescriptor*)NativeMemory.Alloc((nuint)sizeof(NativeLiveObjectContractDescriptor));
        *contract = PowerShellLiveObjectTestContracts.Count.ToNative();

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

    public sealed class TestCountProxy : IDisposable
    {
        private readonly object gate = new();
        private IPowerShellLiveObjectTestCount? value;
        private ComObject? comObject;

        private TestCountProxy(IPowerShellLiveObjectTestCount value, ComObject comObject)
        {
            this.value = value;
            this.comObject = comObject;
        }

        public long Count => Invoke(static (IPowerShellLiveObjectTestCount value, out long count) => value.GetCount(out count));

        public long Increment()
        {
            return Invoke(static (IPowerShellLiveObjectTestCount value, out long count) => value.Increment(out count));
        }

        public static TestCountProxy Create(IntPtr pointer)
        {
            object projected = ComWrappers.GetOrCreateObjectForComInstance(
                pointer,
                CreateObjectFlags.UniqueInstance);
            ComObject comObject = projected as ComObject
                ?? throw new InvalidOperationException("Live object did not create a source-generated COM wrapper.");
            if (projected is not IPowerShellLiveObjectTestCount value)
            {
                comObject.FinalRelease();
                throw new InvalidOperationException("Live object has an unexpected COM contract.");
            }

            return new TestCountProxy(value, comObject);
        }

        public void Dispose()
        {
            lock (gate)
            {
                ComObject? release = comObject;
                value = null;
                comObject = null;
                release?.FinalRelease();
            }
        }

        private long Invoke(TestCountOperation operation)
        {
            lock (gate)
            {
                IPowerShellLiveObjectTestCount contract = value
                    ?? throw new ObjectDisposedException(nameof(TestCountProxy));
                int hresult = operation(contract, out long count);
                if (hresult != 0)
                {
                    throw new COMException("The external live object contract call failed.", hresult);
                }

                return count;
            }
        }

        private delegate int TestCountOperation(IPowerShellLiveObjectTestCount value, out long count);
    }
}
