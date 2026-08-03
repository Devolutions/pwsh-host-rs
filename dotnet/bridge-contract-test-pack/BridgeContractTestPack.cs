#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.BridgeTest;

public static unsafe partial class BridgeContractTestPack
{
    private const int EFail = unchecked((int)0x80004005);
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static readonly IntPtr Api = CreateApi();

    [UnmanagedCallersOnly]
    public static IntPtr GetLiveObjectContractPackV1() => Api;

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
            object projected = ComWrappers.GetOrCreateObjectForComInstance(comObject, CreateObjectFlags.UniqueInstance);
            if (projected is not ComObject sinkComObject)
            {
                throw new InvalidOperationException("The bridge contract pack did not receive a bridge sink.");
            }

            PowerShellBridgeTestCountBridgeBinding binding;
            IPowerShellBridgeContractSink? contractSink = projected as IPowerShellBridgeContractSink;
            IPowerShellBridgeBrokerSink? brokerSink = projected as IPowerShellBridgeBrokerSink;
            if (contractSink is not null)
            {
                binding = PowerShellBridgeTestCountBridgeBinding.Create(contractSink, sinkComObject);
            }
            else if (brokerSink is not null)
            {
                binding = PowerShellBridgeTestCountBridgeBinding.Create(brokerSink, sinkComObject);
            }
            else
            {
                throw new InvalidOperationException("The bridge contract pack received an unsupported bridge sink.");
            }

            var callback = new BridgeTestCountCallback(binding);
            IntPtr callbackPointer = IntPtr.Zero;
            try
            {
                callbackPointer = ComWrappers.GetOrCreateComInterfaceForObject(
                    callback,
                    CreateComInterfaceFlags.None);
                if (callbackPointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The bridge contract pack could not create its invocation callback.");
                }

                if (contractSink is not null)
                {
                    PowerShellBridgeTestCountBridgeBinding.Declare(contractSink, callbackPointer);
                }
                else
                {
                    PowerShellBridgeTestCountBridgeBinding.Declare(brokerSink!, callbackPointer);
                }

                *proxyHandle = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
            }
            catch
            {
                callback.Dispose();
                throw;
            }
            finally
            {
                if (callbackPointer != IntPtr.Zero)
                {
                    PowerShellBridgeComReference.Release(callbackPointer);
                }
            }

            return PowerShellBridgeStatus.Success;
        }
        catch (PowerShellBridgeException exception)
        {
            return exception.Status;
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
        if (proxyHandle == IntPtr.Zero)
        {
            return;
        }

        GCHandle handle = GCHandle.FromIntPtr(proxyHandle);
        try
        {
            (handle.Target as IDisposable)?.Dispose();
        }
        finally
        {
            handle.Free();
        }
    }

    private static IntPtr CreateApi()
    {
        NativeLiveObjectContractDescriptor* contract =
            (NativeLiveObjectContractDescriptor*)NativeMemory.Alloc((nuint)sizeof(NativeLiveObjectContractDescriptor));
        contract[0] = new PowerShellLiveObjectContract(
            typeof(IPowerShellBridgeTestCountTransport).GUID,
            majorVersion: 1,
            minorVersion: 0,
            PowerShellLiveObjectDirection.ConsumerToSession | PowerShellLiveObjectDirection.BridgeContract).ToNative();

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

    [GeneratedComClass]
    private sealed partial class BridgeTestCountCallback : IPowerShellBridgePayloadCallback, IDisposable
    {
        private readonly object gate = new();
        private PowerShellBridgeTestCountBridgeBinding? binding;
        private IntPtr rootHandle;

        internal BridgeTestCountCallback(PowerShellBridgeTestCountBridgeBinding binding)
        {
            this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        }

        public int Bind(out nint root)
        {
            root = IntPtr.Zero;
            lock (gate)
            {
                if (rootHandle != IntPtr.Zero)
                {
                    return PowerShellBridgeStatus.AccessDenied;
                }

                try
                {
                    object value = binding?.Bind()
                        ?? throw new ObjectDisposedException(nameof(BridgeTestCountCallback));
                    rootHandle = GCHandle.ToIntPtr(GCHandle.Alloc(value));
                    root = rootHandle;
                    return PowerShellBridgeStatus.Success;
                }
                catch (PowerShellBridgeException exception)
                {
                    return exception.Status;
                }
                catch
                {
                    return EFail;
                }
            }
        }

        public int Unbind()
        {
            lock (gate)
            {
                try
                {
                    binding?.Unbind();
                    return PowerShellBridgeStatus.Success;
                }
                catch (PowerShellBridgeException exception)
                {
                    return exception.Status;
                }
                catch
                {
                    return EFail;
                }
            }
        }

        public int ReleaseRoot(nint root)
        {
            lock (gate)
            {
                if (root == IntPtr.Zero || root != rootHandle)
                {
                    return PowerShellBridgeStatus.AccessDenied;
                }

                try
                {
                    GCHandle.FromIntPtr(rootHandle).Free();
                    rootHandle = IntPtr.Zero;
                    return PowerShellBridgeStatus.Success;
                }
                catch
                {
                    return EFail;
                }
            }
        }

        public void Dispose()
        {
            PowerShellBridgeTestCountBridgeBinding? release;
            IntPtr root;
            lock (gate)
            {
                release = binding;
                binding = null;
                root = rootHandle;
                rootHandle = IntPtr.Zero;
            }

            try
            {
                release?.Unbind();
            }
            finally
            {
                if (root != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(root).Free();
                }

                release?.Dispose();
            }
        }
    }
}
