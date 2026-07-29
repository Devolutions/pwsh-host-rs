#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;
using BrokerMembers = Devolutions.PowerShell.Ffi.LiveObjects.PowerShellLiveObjectBrokerWire.PowerShellLiveObjectBrokerMembers;

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
                IPowerShellLiveObjectTestBroker broker => TestBrokerProxy.Create(broker, (ComObject)projected),
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

    public sealed class TestBrokerProxy : IDisposable
    {
        private readonly BrokerClient client;
        private readonly Dictionary<ulong, TestBrokerChildProxy> children = [];
        private TestBrokerChildrenProxy? collection;

        private TestBrokerProxy(BrokerClient client)
        {
            this.client = client;
        }

        public TestBrokerChildProxy Add(string name)
        {
            return GetOrAddChild(client.InvokeHandle(BrokerMembers.RootObjectId, BrokerMembers.RootAdd, PowerShellLiveObjectBrokerWire.EncodeString(name)));
        }

        public TestBrokerChildrenProxy Children => collection ??= new TestBrokerChildrenProxy(this);

        internal TestBrokerChildProxy GetOrAddChild(ulong handle)
        {
            if (!children.TryGetValue(handle, out TestBrokerChildProxy? child))
            {
                child = new TestBrokerChildProxy(client, handle);
                children.Add(handle, child);
            }

            return child;
        }

        internal int GetChildCount()
        {
            return client.InvokeInt32(BrokerMembers.ChildrenObjectId, BrokerMembers.ChildrenCount, PowerShellLiveObjectBrokerWire.Encode(PowerShellLiveObjectBrokerWire.Null, []));
        }

        internal TestBrokerChildProxy GetChildAt(int index)
        {
            return GetOrAddChild(client.InvokeHandle(BrokerMembers.ChildrenObjectId, BrokerMembers.ChildrenGetAt, PowerShellLiveObjectBrokerWire.EncodeInt32(index)));
        }

        public static TestBrokerProxy Create(IPowerShellLiveObjectTestBroker value, ComObject comObject)
        {
            return new TestBrokerProxy(new BrokerClient(value, comObject));
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }

    public sealed class TestBrokerChildProxy
    {
        private readonly BrokerClient client;
        private readonly ulong handle;

        internal TestBrokerChildProxy(BrokerClient client, ulong handle)
        {
            this.client = client;
            this.handle = handle;
        }

        public string Name { get => Get(BrokerMembers.ChildGetName); set => Set(BrokerMembers.ChildSetName, value); }
        public string Host { get => Get(BrokerMembers.ChildGetHost); set => Set(BrokerMembers.ChildSetHost, value); }
        public string Description { get => Get(BrokerMembers.ChildGetDescription); set => Set(BrokerMembers.ChildSetDescription, value); }
        public string Group { get => Get(BrokerMembers.ChildGetGroup); set => Set(BrokerMembers.ChildSetGroup, value); }
        public string ReadHost() => Host;

        private string Get(uint member) => client.InvokeString(handle, member, PowerShellLiveObjectBrokerWire.Encode(PowerShellLiveObjectBrokerWire.Null, []));
        private void Set(uint member, string value) => client.InvokeVoid(handle, member, PowerShellLiveObjectBrokerWire.EncodeString(value));
    }

    public sealed class TestBrokerChildrenProxy : IReadOnlyList<TestBrokerChildProxy>
    {
        private readonly TestBrokerProxy root;
        internal TestBrokerChildrenProxy(TestBrokerProxy root) { this.root = root; }
        public int Count => root.GetChildCount();
        public TestBrokerChildProxy this[int index] => root.GetChildAt(index);
        public IEnumerator<TestBrokerChildProxy> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class BrokerClient : IDisposable
    {
        private const int EBufferTooSmall = unchecked((int)0x8007007A);
        private readonly object gate = new();
        private IPowerShellLiveObjectTestBroker? value;
        private ComObject? comObject;
        private readonly ulong leaseId;
        private readonly uint generation;

        internal BrokerClient(IPowerShellLiveObjectTestBroker value, ComObject comObject)
        {
            this.value = value;
            this.comObject = comObject;
            (leaseId, generation) = OpenLease(value);
        }

        internal ulong InvokeHandle(ulong objectId, uint memberId, byte[] input)
        {
            byte[] output = Invoke(objectId, memberId, input);
            if (!PowerShellLiveObjectBrokerWire.TryDecode(output, out byte tag, out ReadOnlySpan<byte> value) ||
                tag != PowerShellLiveObjectBrokerWire.ObjectHandle || value.Length != sizeof(ulong))
                throw new InvalidOperationException("Broker returned an invalid object handle.");
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(value);
        }

        internal int InvokeInt32(ulong objectId, uint memberId, byte[] input)
        {
            byte[] output = Invoke(objectId, memberId, input);
            if (!PowerShellLiveObjectBrokerWire.TryDecode(output, out byte tag, out ReadOnlySpan<byte> value) ||
                tag != PowerShellLiveObjectBrokerWire.Int32 || value.Length != sizeof(int))
                throw new InvalidOperationException("Broker returned an invalid integer.");
            return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value);
        }

        internal string InvokeString(ulong objectId, uint memberId, byte[] input)
        {
            byte[] output = Invoke(objectId, memberId, input);
            if (!PowerShellLiveObjectBrokerWire.TryDecode(output, out byte tag, out ReadOnlySpan<byte> value) ||
                tag != PowerShellLiveObjectBrokerWire.Utf8String)
                throw new InvalidOperationException("Broker returned an invalid string.");
            return System.Text.Encoding.UTF8.GetString(value);
        }

        internal void InvokeVoid(ulong objectId, uint memberId, byte[] input)
        {
            _ = Invoke(objectId, memberId, input);
        }

        private byte[] Invoke(ulong objectId, uint memberId, byte[] input)
        {
            lock (gate)
            {
                IPowerShellLiveObjectTestBroker broker = value ?? throw new ObjectDisposedException(nameof(TestBrokerProxy));
                IntPtr inputBuffer = Marshal.AllocHGlobal(input.Length);
                IntPtr outputBuffer = Marshal.AllocHGlobal(PowerShellLiveObjectBrokerWire.HeaderSize + PowerShellLiveObjectBrokerWire.MaximumValueBytes);
                try
                {
                    Marshal.Copy(input, 0, inputBuffer, input.Length);
                    int status = broker.Invoke(leaseId, generation, objectId, memberId, inputBuffer, input.Length, outputBuffer, 264, out int outputLength);
                    if (status == EBufferTooSmall || outputLength < 0 || outputLength > 264)
                        throw new InvalidOperationException("Broker output exceeds the fixed payload buffer.");
                    if (status == unchecked((int)0x80070005))
                        throw new ObjectDisposedException(nameof(TestBrokerProxy), "The host invocation lease has ended.");
                    if (status != 0) throw new COMException("Broker invocation failed.", status);
                    byte[] output = new byte[outputLength];
                    Marshal.Copy(outputBuffer, output, 0, outputLength);
                    return output;
                }
                finally { Marshal.FreeHGlobal(inputBuffer); Marshal.FreeHGlobal(outputBuffer); }
            }
        }

        public void Dispose()
        {
            // Host-owned EndLease is the security boundary. A child retained by
            // PowerShell must keep this client usable long enough to observe it.
        }

        private static (ulong LeaseId, uint Generation) OpenLease(IPowerShellLiveObjectTestBroker broker)
        {
            byte[] input = PowerShellLiveObjectBrokerWire.Encode(PowerShellLiveObjectBrokerWire.Null, []);
            IntPtr inputBuffer = Marshal.AllocHGlobal(input.Length);
            IntPtr outputBuffer = Marshal.AllocHGlobal(264);
            try
            {
                Marshal.Copy(input, 0, inputBuffer, input.Length);
                int status = broker.Invoke(0, 0, 0, 0, inputBuffer, input.Length, outputBuffer, 264, out int outputLength);
                if (status != 0 || outputLength < PowerShellLiveObjectBrokerWire.HeaderSize || outputLength > 264)
                    throw new COMException("Broker lease initialization failed.", status);
                byte[] output = new byte[outputLength];
                Marshal.Copy(outputBuffer, output, 0, outputLength);
                if (!PowerShellLiveObjectBrokerWire.TryDecode(output, out byte tag, out ReadOnlySpan<byte> payload) ||
                    tag != PowerShellLiveObjectBrokerWire.Utf8String)
                    throw new InvalidOperationException("Broker returned an invalid lease.");
                string[] parts = System.Text.Encoding.UTF8.GetString(payload).Split(':');
                if (parts.Length != 2 || !ulong.TryParse(parts[0], out ulong leaseId) || !uint.TryParse(parts[1], out uint generation))
                    throw new InvalidOperationException("Broker returned an invalid lease.");
                return (leaseId, generation);
            }
            finally { Marshal.FreeHGlobal(inputBuffer); Marshal.FreeHGlobal(outputBuffer); }
        }
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
