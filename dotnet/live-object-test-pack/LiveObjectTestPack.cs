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
        private TestChildProxy? primary;
        private TestChildCollectionProxy? children;

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

        public long Revision
        {
            get => Invoke(static (IPowerShellLiveObjectTestCount value, out long revision) => value.GetRevision(out revision));
            set
            {
                lock (gate)
                {
                    int hresult = GetContract().SetRevision(value);
                    if (hresult != 0)
                    {
                        throw new COMException("The external live object revision update failed.", hresult);
                    }
                }
            }
        }

        public TestChildProxy Primary
        {
            get
            {
                lock (gate)
                {
                    if (primary is not null)
                    {
                        return primary;
                    }

                    int hresult = GetContract().GetPrimary(out IPowerShellLiveObjectTestChild child);
                    if (hresult != 0)
                    {
                        throw new COMException("The external live object primary lookup failed.", hresult);
                    }

                    primary = TestChildProxy.Create(child);
                    return primary;
                }
            }
        }

        public TestChildCollectionProxy Children
        {
            get
            {
                lock (gate)
                {
                    if (children is not null)
                    {
                        return children;
                    }

                    int hresult = GetContract().GetChildren(out IPowerShellLiveObjectTestChildCollection collection);
                    if (hresult != 0)
                    {
                        throw new COMException("The external live object child collection lookup failed.", hresult);
                    }

                    children = TestChildCollectionProxy.Create(collection);
                    return children;
                }
            }
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
                primary?.Dispose();
                primary = null;
                children?.Dispose();
                children = null;
                ComObject? release = comObject;
                value = null;
                comObject = null;
                release?.FinalRelease();
            }
        }

        private IPowerShellLiveObjectTestCount GetContract()
        {
            return value ?? throw new ObjectDisposedException(nameof(TestCountProxy));
        }

        private long Invoke(TestCountOperation operation)
        {
            lock (gate)
            {
                IPowerShellLiveObjectTestCount contract = GetContract();
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

    public sealed class TestChildProxy : IDisposable
    {
        private readonly object gate = new();
        private IPowerShellLiveObjectTestChild? value;
        private ComObject? comObject;

        private TestChildProxy(IPowerShellLiveObjectTestChild value, ComObject comObject)
        {
            this.value = value;
            this.comObject = comObject;
        }

        public long Value
        {
            get => Invoke(static (IPowerShellLiveObjectTestChild value, out long result) => value.GetValue(out result));
            set
            {
                lock (gate)
                {
                    IPowerShellLiveObjectTestChild contract = this.value
                        ?? throw new ObjectDisposedException(nameof(TestChildProxy));
                    int hresult = contract.SetValue(value);
                    if (hresult != 0)
                    {
                        throw new COMException("The nested live object update failed.", hresult);
                    }
                }
            }
        }

        internal static TestChildProxy Create(IPowerShellLiveObjectTestChild value)
        {
            ComObject comObject = (object)value as ComObject
                ?? throw new InvalidOperationException("Nested live object did not create a source-generated COM wrapper.");
            return new TestChildProxy(value, comObject);
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

        private long Invoke(TestChildOperation operation)
        {
            lock (gate)
            {
                IPowerShellLiveObjectTestChild contract = value
                    ?? throw new ObjectDisposedException(nameof(TestChildProxy));
                int hresult = operation(contract, out long result);
                if (hresult != 0)
                {
                    throw new COMException("The nested live object contract call failed.", hresult);
                }

                return result;
            }
        }

        private delegate int TestChildOperation(IPowerShellLiveObjectTestChild value, out long result);
    }

    public sealed class TestChildCollectionProxy : IDisposable
    {
        private readonly object gate = new();
        private readonly Dictionary<int, TestChildProxy> items = [];
        private IPowerShellLiveObjectTestChildCollection? value;
        private ComObject? comObject;

        private TestChildCollectionProxy(IPowerShellLiveObjectTestChildCollection value, ComObject comObject)
        {
            this.value = value;
            this.comObject = comObject;
        }

        public int Count => Invoke(static (IPowerShellLiveObjectTestChildCollection value, out int count) => value.GetCount(out count));

        public TestChildProxy this[int index]
        {
            get
            {
                lock (gate)
                {
                    if (items.TryGetValue(index, out TestChildProxy? item))
                    {
                        return item;
                    }

                    IPowerShellLiveObjectTestChildCollection contract = value
                        ?? throw new ObjectDisposedException(nameof(TestChildCollectionProxy));
                    int hresult = contract.GetAt(index, out IPowerShellLiveObjectTestChild child);
                    if (hresult != 0)
                    {
                        throw new COMException("The nested live object collection lookup failed.", hresult);
                    }

                    item = TestChildProxy.Create(child);
                    items.Add(index, item);
                    return item;
                }
            }
        }

        internal static TestChildCollectionProxy Create(IPowerShellLiveObjectTestChildCollection value)
        {
            ComObject comObject = (object)value as ComObject
                ?? throw new InvalidOperationException("Nested live object collection did not create a source-generated COM wrapper.");
            return new TestChildCollectionProxy(value, comObject);
        }

        public void Dispose()
        {
            lock (gate)
            {
                foreach (TestChildProxy item in items.Values)
                {
                    item.Dispose();
                }

                items.Clear();
                ComObject? release = comObject;
                value = null;
                comObject = null;
                release?.FinalRelease();
            }
        }

        private int Invoke(TestChildCollectionOperation operation)
        {
            lock (gate)
            {
                IPowerShellLiveObjectTestChildCollection contract = value
                    ?? throw new ObjectDisposedException(nameof(TestChildCollectionProxy));
                int hresult = operation(contract, out int result);
                if (hresult != 0)
                {
                    throw new COMException("The nested live object collection contract call failed.", hresult);
                }

                return result;
            }
        }

        private delegate int TestChildCollectionOperation(
            IPowerShellLiveObjectTestChildCollection value,
            out int result);
    }
}
