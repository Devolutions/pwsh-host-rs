#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.FiniteOperationTest;

/// <summary>NativeAOT payload pack for the static finite-operation acceptance contract.</summary>
public static unsafe partial class FiniteOperationTestPack
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
                throw new InvalidOperationException("The finite-operation pack did not receive a bridge sink.");
            }

            PowerShellBridgeTestFiniteOperationBridgeBinding binding;
            IPowerShellBridgeContractSink? contractSink = projected as IPowerShellBridgeContractSink;
            IPowerShellBridgeBrokerSink? brokerSink = projected as IPowerShellBridgeBrokerSink;
            if (contractSink is not null)
            {
                binding = PowerShellBridgeTestFiniteOperationBridgeBinding.Create(contractSink, sinkComObject);
            }
            else if (brokerSink is not null)
            {
                binding = PowerShellBridgeTestFiniteOperationBridgeBinding.Create(brokerSink, sinkComObject);
            }
            else
            {
                throw new InvalidOperationException("The finite-operation pack received an unsupported bridge sink.");
            }

            var callback = new FiniteOperationCallback(binding);
            IntPtr callbackPointer = IntPtr.Zero;
            try
            {
                callbackPointer = ComWrappers.GetOrCreateComInterfaceForObject(
                    callback,
                    CreateComInterfaceFlags.None);
                if (callbackPointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The finite-operation pack could not create its invocation callback.");
                }

                if (contractSink is not null)
                {
                    PowerShellBridgeTestFiniteOperationBridgeBinding.Declare(contractSink, callbackPointer);
                }
                else
                {
                    PowerShellBridgeTestFiniteOperationBridgeBinding.Declare(brokerSink!, callbackPointer);
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
            typeof(IPowerShellBridgeTestFiniteOperationTransport).GUID,
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
    private sealed partial class FiniteOperationCallback : IPowerShellBridgePayloadCallback, IDisposable
    {
        private readonly object gate = new();
        private PowerShellBridgeTestFiniteOperationBridgeBinding? binding;
        private IntPtr rootHandle;

        internal FiniteOperationCallback(PowerShellBridgeTestFiniteOperationBridgeBinding binding)
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
                        ?? throw new ObjectDisposedException(nameof(FiniteOperationCallback));
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
            PowerShellBridgeTestFiniteOperationBridgeBinding? release;
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
