#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;
using Devolutions.MultiPwsh.LiveContracts;

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
            object projected = ComWrappers.GetOrCreateObjectForComInstance(comObject, CreateObjectFlags.UniqueInstance);
            IDisposable proxy = projected switch
            {
                IPowerShellLiveObjectBrokerContract broker => SessionCreatorLiveContractProxy.Create(broker, (ComObject)projected),
                IPowerShellLiveObjectTestCount count => new TestCountProxy(count, (ComObject)projected),
                _ => throw new InvalidOperationException("Live object has an unexpected COM contract."),
            };
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
        (NativeLiveObjectContractDescriptor*)NativeMemory.Alloc((nuint)(2 * sizeof(NativeLiveObjectContractDescriptor)));
        contract[0] = PowerShellLiveObjectTestContracts.Count.ToNative();
        contract[1] = PowerShellLiveObjectTestContracts.SessionCreatorBroker.ToNative();

        NativeLiveObjectContractPackApi* api =
            (NativeLiveObjectContractPackApi*)NativeMemory.Alloc((nuint)sizeof(NativeLiveObjectContractPackApi));
        *api = new NativeLiveObjectContractPackApi
        {
            Size = (nuint)sizeof(NativeLiveObjectContractPackApi),
            AbiVersion = 1,
            ContractCount = 2,
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
        private readonly Dictionary<long, TestChildProxy> childProxies = [];
        private TestChildCollectionProxy? children;

        internal TestCountProxy(IPowerShellLiveObjectTestCount value, ComObject comObject)
        {
            this.value = value;
            this.comObject = comObject;
        }

        public long Count => Invoke(static (IPowerShellLiveObjectTestCount value, out long count) => value.GetCount(out count));

        public long Increment()
        {
            return Invoke(static (IPowerShellLiveObjectTestCount value, out long count) => value.Increment(out count));
        }

        public TestChildProxy Primary
        {
            get
            {
                lock (gate)
                {
                    int hresult = GetContract().GetPrimary(out IPowerShellLiveObjectTestChild child);
                    if (hresult != 0)
                    {
                        throw new COMException("The external live object primary lookup failed.", hresult);
                    }

                    return GetOrAddChild(child);
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

                    children = TestChildCollectionProxy.Create(collection, GetOrAddChild);
                    return children;
                }
            }
        }

        public TestChildProxy Add(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            lock (gate)
            {
                int hresult = GetContract().Add(name, out IPowerShellLiveObjectTestChild child);
                if (hresult != 0)
                {
                    throw new COMException("The external live object child creation failed.", hresult);
                }

                return GetOrAddChild(child);
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
                foreach (TestChildProxy child in childProxies.Values)
                {
                    child.Dispose();
                }

                childProxies.Clear();
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

        private TestChildProxy GetOrAddChild(IPowerShellLiveObjectTestChild value)
        {
            int hresult = value.GetIdentity(out long identity);
            if (hresult != 0)
            {
                TestChildProxy.ReleaseUnowned(value);
                throw new COMException("The external live object child identity lookup failed.", hresult);
            }

            if (childProxies.TryGetValue(identity, out TestChildProxy? existing))
            {
                if (!existing.IsContract(value))
                {
                    TestChildProxy.ReleaseUnowned(value);
                }

                return existing;
            }

            TestChildProxy child = TestChildProxy.Create(value, identity);
            childProxies.Add(identity, child);
            return child;
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

        private TestChildProxy(IPowerShellLiveObjectTestChild value, ComObject comObject, long identity)
        {
            this.value = value;
            this.comObject = comObject;
            Identity = identity;
        }

        public long Identity { get; }

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

        public string Name
        {
            get => GetText(static (IPowerShellLiveObjectTestChild value, out string text) => value.GetName(out text));
            set => SetText(value, static (IPowerShellLiveObjectTestChild contract, string text) => contract.SetName(text));
        }

        public string Host
        {
            get => GetText(static (IPowerShellLiveObjectTestChild value, out string text) => value.GetHost(out text));
            set => SetText(value, static (IPowerShellLiveObjectTestChild contract, string text) => contract.SetHost(text));
        }

        public string Description
        {
            get => GetText(static (IPowerShellLiveObjectTestChild value, out string text) => value.GetDescription(out text));
            set => SetText(value, static (IPowerShellLiveObjectTestChild contract, string text) => contract.SetDescription(text));
        }

        public string Group
        {
            get => GetText(static (IPowerShellLiveObjectTestChild value, out string text) => value.GetGroup(out text));
            set => SetText(value, static (IPowerShellLiveObjectTestChild contract, string text) => contract.SetGroup(text));
        }

        internal static TestChildProxy Create(IPowerShellLiveObjectTestChild value, long identity)
        {
            ComObject comObject = (object)value as ComObject
                ?? throw new InvalidOperationException("Nested live object did not create a source-generated COM wrapper.");
            return new TestChildProxy(value, comObject, identity);
        }

        internal static void ReleaseUnowned(IPowerShellLiveObjectTestChild value)
        {
            ((object)value as ComObject)?.FinalRelease();
        }

        internal bool IsContract(IPowerShellLiveObjectTestChild candidate)
        {
            return ReferenceEquals(value, candidate);
        }

        public override bool Equals(object? obj)
        {
            return obj is TestChildProxy other && Identity == other.Identity;
        }

        public override int GetHashCode()
        {
            return Identity.GetHashCode();
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

        private string GetText(TestChildTextGetter getter)
        {
            lock (gate)
            {
                IPowerShellLiveObjectTestChild contract = value
                    ?? throw new ObjectDisposedException(nameof(TestChildProxy));
                int hresult = getter(contract, out string text);
                if (hresult != 0)
                {
                    throw new COMException("The nested live object text lookup failed.", hresult);
                }

                return text;
            }
        }

        private void SetText(string text, TestChildTextSetter setter)
        {
            ArgumentNullException.ThrowIfNull(text);
            lock (gate)
            {
                IPowerShellLiveObjectTestChild contract = value
                    ?? throw new ObjectDisposedException(nameof(TestChildProxy));
                int hresult = setter(contract, text);
                if (hresult != 0)
                {
                    throw new COMException("The nested live object text update failed.", hresult);
                }
            }
        }

        private delegate int TestChildOperation(IPowerShellLiveObjectTestChild value, out long result);

        private delegate int TestChildTextGetter(IPowerShellLiveObjectTestChild value, out string result);

        private delegate int TestChildTextSetter(IPowerShellLiveObjectTestChild value, string text);
    }

    public sealed class TestChildCollectionProxy : IReadOnlyList<TestChildProxy>, IDisposable
    {
        private readonly object gate = new();
        private readonly Func<IPowerShellLiveObjectTestChild, TestChildProxy> childFactory;
        private IPowerShellLiveObjectTestChildCollection? value;
        private ComObject? comObject;

        private TestChildCollectionProxy(
            IPowerShellLiveObjectTestChildCollection value,
            ComObject comObject,
            Func<IPowerShellLiveObjectTestChild, TestChildProxy> childFactory)
        {
            this.value = value;
            this.comObject = comObject;
            this.childFactory = childFactory;
        }

        public int Count => Invoke(static (IPowerShellLiveObjectTestChildCollection value, out int count) => value.GetCount(out count));

        public TestChildProxy this[int index]
        {
            get
            {
                lock (gate)
                {
                    IPowerShellLiveObjectTestChildCollection contract = value
                        ?? throw new ObjectDisposedException(nameof(TestChildCollectionProxy));
                    int hresult = contract.GetAt(index, out IPowerShellLiveObjectTestChild child);
                    if (hresult != 0)
                    {
                        throw new COMException("The nested live object collection lookup failed.", hresult);
                    }

                    return childFactory(child);
                }
            }
        }

        public IEnumerator<TestChildProxy> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        internal static TestChildCollectionProxy Create(
            IPowerShellLiveObjectTestChildCollection value,
            Func<IPowerShellLiveObjectTestChild, TestChildProxy> childFactory)
        {
            ComObject comObject = (object)value as ComObject
                ?? throw new InvalidOperationException("Nested live object collection did not create a source-generated COM wrapper.");
            return new TestChildCollectionProxy(value, comObject, childFactory);
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
