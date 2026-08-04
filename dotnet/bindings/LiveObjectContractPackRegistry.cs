#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace NativeHost;

internal abstract class FfiLiveObjectLease : IDisposable
{
    internal abstract object Value { get; }

    internal virtual bool RequiresInvocationBinding => false;

    internal virtual object BeginInvocationBinding() => Value;

    internal virtual void EndInvocationBinding()
    {
    }

    public abstract void Dispose();
}

internal sealed class FfiManagedLiveObjectLease : FfiLiveObjectLease
{
    private IDisposable? value;

    internal FfiManagedLiveObjectLease(object value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        this.value = value as IDisposable
            ?? throw new ArgumentException("The live object payload proxy must be disposable.", nameof(value));
    }

    internal override object Value { get; }

    public override void Dispose()
    {
        IDisposable? value = Interlocked.Exchange(ref this.value, null);
        value?.Dispose();
    }
}

internal sealed unsafe class FfiExternalLiveObjectLease : FfiLiveObjectLease
{
    private readonly delegate* unmanaged<IntPtr, void> release;
    private IntPtr proxyHandle;

    internal FfiExternalLiveObjectLease(
        IntPtr proxyHandle,
        delegate* unmanaged<IntPtr, void> release)
    {
        if (proxyHandle == IntPtr.Zero)
        {
            throw new ArgumentException("The payload contract pack returned a null proxy handle.", nameof(proxyHandle));
        }

        this.proxyHandle = proxyHandle;
        this.release = release;
    }

    internal override object Value
    {
        get
        {
            IntPtr value = Volatile.Read(ref proxyHandle);
            if (value == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(FfiExternalLiveObjectLease));
            }

            try
            {
                return GCHandle.FromIntPtr(value).Target
                    ?? throw new InvalidOperationException("The payload contract pack proxy handle has no target.");
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException("The payload contract pack returned an invalid proxy handle.", exception);
            }
        }
    }

    public override void Dispose()
    {
        IntPtr value = Interlocked.Exchange(ref proxyHandle, IntPtr.Zero);
        if (value != IntPtr.Zero)
        {
            release(value);
        }
    }
}

/// <summary>
/// The payload-owned sink a v2 contract pack receives instead of its consumer
/// transport. It owns one reference to that transport and exposes it only while
/// the associated proxy is bound to an invocation.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class FfiBridgeContractSink : IPowerShellBridgeContractSink, IFfiBridgeContractLeaseSink
{
    private const int Created = 0;
    private const int Declared = 1;
    private const int Bound = 2;
    private const int Unbound = 3;
    private const int Disposed = 4;

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly object gate = new();
    private readonly ulong interfaceIdLow;
    private readonly ulong interfaceIdHigh;
    private readonly ushort majorVersion;
    private readonly ushort minorVersion;
    private readonly PowerShellLiveObjectContract contract;
    private IntPtr consumerContract;
    private IPowerShellBridgePayloadCallback? callback;
    private ComObject? callbackComObject;
    private IntPtr activeRootHandle;
    private int state;

    internal FfiBridgeContractSink(PowerShellLiveObjectContract contract, IntPtr consumerContract)
    {
        if (consumerContract == IntPtr.Zero)
        {
            throw new ArgumentException("The bridge consumer contract pointer is null.", nameof(consumerContract));
        }

        this.contract = contract;
        Span<byte> interfaceId = stackalloc byte[16];
        if (!contract.InterfaceId.TryWriteBytes(interfaceId))
        {
            throw new InvalidOperationException("The bridge consumer contract identifier is invalid.");
        }

        interfaceIdLow = BinaryPrimitives.ReadUInt64LittleEndian(interfaceId);
        interfaceIdHigh = BinaryPrimitives.ReadUInt64LittleEndian(interfaceId[8..]);
        majorVersion = contract.MajorVersion;
        minorVersion = contract.MinorVersion;

        AddReference(consumerContract);
        this.consumerContract = consumerContract;
    }

    internal bool IsDeclared
    {
        get
        {
            lock (gate)
            {
                return state is Declared or Bound or Unbound;
            }
        }
    }

    internal bool Matches(PowerShellLiveObjectContract requested, IntPtr candidate)
    {
        if (requested != contract || candidate == IntPtr.Zero)
        {
            return false;
        }

        lock (gate)
        {
            // PowerShellLiveObject<TContract> obtains this exact generated-COM
            // interface pointer from its cached wrapper for every assignment.
            // The pointer is therefore the broker identity at this boundary; the
            // registry does not manufacture an unbounded second proxy for it.
            return state != Disposed && candidate == consumerContract;
        }
    }

    internal int BeginBinding(out object root)
    {
        root = null!;
        IPowerShellBridgePayloadCallback? currentCallback;
        lock (gate)
        {
            if (state is not (Declared or Unbound))
            {
                return PowerShellBridgeStatus.AccessDenied;
            }

            state = Bound;
            currentCallback = callback;
        }

        if (currentCallback is null)
        {
            EndBinding();
            return PowerShellBridgeStatus.InvalidArgument;
        }

        int status = currentCallback.Bind(out nint rootHandle);
        if (status != PowerShellBridgeStatus.Success || rootHandle == IntPtr.Zero)
        {
            EndBinding();
            return status == PowerShellBridgeStatus.Success
                ? PowerShellBridgeStatus.InvalidArgument
                : status;
        }

        try
        {
            root = GCHandle.FromIntPtr(rootHandle).Target
                ?? throw new InvalidOperationException("The bridge payload callback returned a root handle with no target.");
        }
        catch (InvalidOperationException)
        {
            _ = currentCallback.ReleaseRoot(rootHandle);
            EndBinding();
            return PowerShellBridgeStatus.InvalidArgument;
        }

        lock (gate)
        {
            if (state != Bound)
            {
                _ = currentCallback.ReleaseRoot(rootHandle);
                return PowerShellBridgeStatus.AccessDenied;
            }

            activeRootHandle = rootHandle;
            return PowerShellBridgeStatus.Success;
        }
    }

    internal void EndBinding()
    {
        IPowerShellBridgePayloadCallback? currentCallback;
        IntPtr rootHandle;
        lock (gate)
        {
            if (state != Bound)
            {
                return;
            }

            state = Unbound;
            rootHandle = Interlocked.Exchange(ref activeRootHandle, IntPtr.Zero);
            currentCallback = callback;
        }

        Exception? failure = null;
        try
        {
            if (currentCallback is not null)
            {
                int status = currentCallback.Unbind();
                if (status != PowerShellBridgeStatus.Success)
                {
                    failure = PowerShellBridgeException.FromStatus(status, contract.InterfaceId.ToString("D"));
                }
            }
        }
        finally
        {
            if (currentCallback is not null && rootHandle != IntPtr.Zero)
            {
                int status = currentCallback.ReleaseRoot(rootHandle);
                if (failure is null && status != PowerShellBridgeStatus.Success)
                {
                    failure = PowerShellBridgeException.FromStatus(status, contract.InterfaceId.ToString("D"));
                }
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    int IFfiBridgeContractLeaseSink.BeginBinding(out object root) => BeginBinding(out root);

    void IFfiBridgeContractLeaseSink.EndBinding() => EndBinding();

    internal IntPtr Export()
    {
        IntPtr value = ComWrappers.GetOrCreateComInterfaceForObject(this, CreateComInterfaceFlags.None);
        if (value == IntPtr.Zero)
        {
            throw new InvalidOperationException("The bridge contract sink did not create an IUnknown pointer.");
        }

        return value;
    }

    public int GetRequestedContract(
        out ulong requestedInterfaceIdLow,
        out ulong requestedInterfaceIdHigh,
        out ushort requestedMajorVersion,
        out ushort requestedMinorVersion)
    {
        requestedInterfaceIdLow = interfaceIdLow;
        requestedInterfaceIdHigh = interfaceIdHigh;
        requestedMajorVersion = majorVersion;
        requestedMinorVersion = minorVersion;
        return Volatile.Read(ref state) == Disposed
            ? PowerShellBridgeStatus.AccessDenied
            : PowerShellBridgeStatus.Success;
    }

    public int Declare(
        ulong requestedInterfaceIdLow,
        ulong requestedInterfaceIdHigh,
        ushort requestedMajorVersion,
        ushort requestedMinorVersion,
        nint callbackPointer)
    {
        if (callbackPointer == IntPtr.Zero)
        {
            return PowerShellBridgeStatus.InvalidArgument;
        }

        lock (gate)
        {
            if (state != Created)
            {
                return PowerShellBridgeStatus.InvalidArgument;
            }

            if (requestedInterfaceIdLow != interfaceIdLow ||
                requestedInterfaceIdHigh != interfaceIdHigh ||
                requestedMajorVersion != majorVersion ||
                requestedMinorVersion != minorVersion)
            {
                return PowerShellBridgeStatus.ContractMismatch;
            }

            ComObject? imported = null;
            try
            {
                object projected = ComWrappers.GetOrCreateObjectForComInstance(
                    callbackPointer,
                    CreateObjectFlags.UniqueInstance);
                imported = projected as ComObject
                    ?? throw new InvalidOperationException(
                        "The bridge payload callback did not create a source-generated COM wrapper.");
                IPowerShellBridgePayloadCallback typed = projected as IPowerShellBridgePayloadCallback
                    ?? throw new InvalidOperationException(
                        "The bridge payload callback has an unexpected COM interface.");
                callback = typed;
                callbackComObject = imported;
                imported = null;
                state = Declared;
                return PowerShellBridgeStatus.Success;
            }
            catch (COMException exception)
            {
                return exception.HResult;
            }
            catch
            {
                return unchecked((int)0x80004005);
            }
            finally
            {
                imported?.FinalRelease();
            }
        }
    }

    public int GetConsumerContract(out nint contractPointer)
    {
        contractPointer = IntPtr.Zero;
        lock (gate)
        {
            if (state != Bound || consumerContract == IntPtr.Zero)
            {
                return PowerShellBridgeStatus.AccessDenied;
            }

            try
            {
                AddReference(consumerContract);
                contractPointer = consumerContract;
                return PowerShellBridgeStatus.Success;
            }
            catch
            {
                return unchecked((int)0x80004005);
            }
        }
    }

    public void Dispose()
    {
        IntPtr contractPointer;
        ComObject? callbackObject;
        lock (gate)
        {
            if (state == Disposed)
            {
                return;
            }

            state = Disposed;
            contractPointer = Interlocked.Exchange(ref consumerContract, IntPtr.Zero);
            callback = null;
            callbackObject = callbackComObject;
            callbackComObject = null;
        }

        try
        {
            if (contractPointer != IntPtr.Zero)
            {
                PowerShellBridgeComReference.Release(contractPointer);
            }
        }
        finally
        {
            callbackObject?.FinalRelease();
        }
    }

    private static void AddReference(IntPtr value)
    {
        IntPtr* vtable = *(IntPtr**)value;
        if (vtable == null || vtable[1] == IntPtr.Zero)
        {
            throw new InvalidOperationException("A bridge COM reference has an invalid IUnknown vtable.");
        }

        var addReference = (delegate* unmanaged[MemberFunction]<IntPtr, uint>)vtable[1];
        _ = addReference(value);
    }

}

internal interface IFfiBridgeContractLeaseSink : IDisposable
{
    int BeginBinding(out object root);

    void EndBinding();
}

internal sealed unsafe class FfiBridgeContractLease : FfiLiveObjectLease
{
    private readonly object gate = new();
    private readonly delegate* unmanaged<IntPtr, void> release;
    private readonly IFfiBridgeContractLeaseSink sink;
    private readonly object unboundValue = new();
    private IntPtr proxyHandle;
    private object currentValue;
    private bool bound;
    private bool disposed;

    internal FfiBridgeContractLease(
        IntPtr proxyHandle,
        delegate* unmanaged<IntPtr, void> release,
        IFfiBridgeContractLeaseSink sink)
    {
        if (proxyHandle == IntPtr.Zero)
        {
            throw new ArgumentException("The bridge payload contract pack returned a null proxy handle.", nameof(proxyHandle));
        }

        this.release = release;
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.proxyHandle = proxyHandle;
        currentValue = unboundValue;
    }

    internal override object Value
    {
        get
        {
            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(FfiBridgeContractLease));
                }

                return currentValue;
            }
        }
    }

    internal override bool RequiresInvocationBinding => true;

    internal override object BeginInvocationBinding()
    {
        lock (gate)
        {
            if (disposed)
            {
                throw new PowerShellBridgeException(
                    PowerShellBridgeStatus.AccessDenied,
                    "The bridge payload proxy was released; rediscover the contract before binding it again.");
            }

            if (bound)
            {
                return currentValue;
            }

            int status = sink.BeginBinding(out object value);
            if (status != PowerShellBridgeStatus.Success)
            {
                throw PowerShellBridgeException.FromStatus(status, sink.GetType().Name);
            }

            currentValue = value;
            bound = true;
            return value;
        }
    }

    internal override void EndInvocationBinding()
    {
        lock (gate)
        {
            if (!bound)
            {
                return;
            }

            // Publish the tombstone before invoking pack code. A failed close must
            // not leave a root reachable as though its invocation were still live.
            bound = false;
            currentValue = unboundValue;
            sink.EndBinding();
        }
    }

    internal bool Matches(PowerShellLiveObjectContract contract, IntPtr candidate) =>
        sink is FfiBridgeContractSink contractSink && contractSink.Matches(contract, candidate);

    public override void Dispose()
    {
        IntPtr handle;
        bool wasBound;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            wasBound = bound;
            bound = false;
            currentValue = unboundValue;
            handle = Interlocked.Exchange(ref proxyHandle, IntPtr.Zero);
        }

        Exception? failure = null;
        try
        {
            if (wasBound)
            {
                sink.EndBinding();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                if (handle != IntPtr.Zero)
                {
                    release(handle);
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                try
                {
                    sink.Dispose();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }
}

internal unsafe sealed class FfiLiveObjectContractPackRegistry
{
    private const uint PackAbiVersion = 1;
    private const uint MaximumPacks = 16;
    private const uint MaximumContracts = 32;

    private readonly object gate = new();
    private readonly Dictionary<PowerShellLiveObjectContract, Registration> registrations = new();

    internal FfiLiveObjectContractPackRegistry(
        PowerShellLiveObjectContract probeContract,
        Func<IntPtr, FfiLiveObjectLease> createProbeLease)
    {
        registrations.Add(probeContract, Registration.CreateManaged(createProbeLease));
    }

    internal void Register(IntPtr apiPointer)
    {
        RegisterMany(&apiPointer, 1);
    }

    internal void RegisterMany(IntPtr* apiPointers, uint apiCount)
    {
        if (apiPointers == null || apiCount == 0 || apiCount > MaximumPacks)
        {
            throw new InvalidOperationException("Live object contract pack input is invalid.");
        }

        var additions = new Dictionary<PowerShellLiveObjectContract, Registration>();
        var interfaceIds = new HashSet<Guid>();
        for (uint packIndex = 0; packIndex < apiCount; packIndex++)
        {
            IntPtr apiPointer = apiPointers[packIndex];
            if (apiPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("Live object contract pack API pointer is null.");
            }

            NativeLiveObjectContractPackApi api = *(NativeLiveObjectContractPackApi*)apiPointer;
            if (api.Size < (nuint)sizeof(NativeLiveObjectContractPackApi) ||
                api.AbiVersion != PackAbiVersion ||
                api.ContractCount == 0 ||
                api.ContractCount > MaximumContracts ||
                api.Contracts == null ||
                api.CreatePayloadProxy == IntPtr.Zero ||
                api.ReleasePayloadProxy == IntPtr.Zero)
            {
                throw new InvalidOperationException("Live object contract pack API is invalid.");
            }

            var create = (delegate* unmanaged<IntPtr, IntPtr*, int>)api.CreatePayloadProxy;
            var release = (delegate* unmanaged<IntPtr, void>)api.ReleasePayloadProxy;
            for (uint contractIndex = 0; contractIndex < api.ContractCount; contractIndex++)
            {
                PowerShellLiveObjectContract contract =
                    PowerShellLiveObjectContract.FromNative(api.Contracts[contractIndex]);
                                if ((contract.Directions & PowerShellLiveObjectDirection.ConsumerToSession) == 0)
                                {
                                    throw new InvalidOperationException(
                                        "Live object contract packs contain a contract with an unsupported direction.");
                                }

                                if (!interfaceIds.Add(contract.InterfaceId))
                                {
                                    throw new InvalidOperationException(
                                        "Live object contract packs contain duplicate interface identifiers.");
                                }

                                if (!additions.TryAdd(contract, Registration.CreateExternal(create, release)))
                                {
                                    throw new InvalidOperationException(
                                        "Live object contract packs contain incompatible interface identifiers.");
                                }
            }
        }

        lock (gate)
        {
            foreach (PowerShellLiveObjectContract contract in additions.Keys)
            {
                foreach (PowerShellLiveObjectContract existing in registrations.Keys)
                {
                    if (existing.InterfaceId == contract.InterfaceId)
                    {
                        throw new InvalidOperationException(
                            "A live object contract interface identifier has already been registered.");
                    }
                }
            }

            foreach (KeyValuePair<PowerShellLiveObjectContract, Registration> entry in additions)
            {
                registrations.Add(entry.Key, entry.Value);
            }
        }
    }

    internal FfiLiveObjectLease CreateLease(
        PowerShellLiveObjectContract contract,
        IntPtr comObject)
    {
        if ((contract.Directions & PowerShellLiveObjectDirection.ConsumerToSession) == 0 ||
            comObject == IntPtr.Zero)
        {
            throw new InvalidOperationException("Live object transfer metadata is invalid.");
        }

        Registration? registration;
        lock (gate)
        {
            if (!registrations.TryGetValue(contract, out registration))
            {
                throw new InvalidOperationException("The live object contract is not registered by the payload.");
            }
        }

        return registration!.CreateLease(contract, comObject);
    }

    internal FfiLiveObjectLease CreateBridgeBrokerLease(
        PowerShellLiveObjectContract contract,
        FfiBridgeBrokerSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if ((contract.Directions & (PowerShellLiveObjectDirection.ConsumerToSession |
                                    PowerShellLiveObjectDirection.BridgeContract)) !=
            (PowerShellLiveObjectDirection.ConsumerToSession |
             PowerShellLiveObjectDirection.BridgeContract))
        {
            throw new InvalidOperationException("Bridge attachment metadata is invalid.");
        }

        Registration? registration;
        lock (gate)
        {
            if (!registrations.TryGetValue(contract, out registration))
            {
                throw new InvalidOperationException("The bridge contract is not registered by the payload.");
            }
        }

        return registration!.CreateBridgeBrokerLease(contract, sink);
    }

    private sealed class Registration
    {
        private readonly Func<IntPtr, FfiLiveObjectLease>? managedFactory;
        private readonly delegate* unmanaged<IntPtr, IntPtr*, int> create;
        private readonly delegate* unmanaged<IntPtr, void> release;

        private Registration(Func<IntPtr, FfiLiveObjectLease> managedFactory)
        {
            this.managedFactory = managedFactory;
        }

        private Registration(
            delegate* unmanaged<IntPtr, IntPtr*, int> create,
            delegate* unmanaged<IntPtr, void> release)
        {
            this.create = create;
            this.release = release;
        }

        internal static Registration CreateManaged(Func<IntPtr, FfiLiveObjectLease> factory)
        {
            return new Registration(factory);
        }

        internal static Registration CreateExternal(
            delegate* unmanaged<IntPtr, IntPtr*, int> create,
            delegate* unmanaged<IntPtr, void> release)
        {
            return new Registration(create, release);
        }

        internal FfiLiveObjectLease CreateLease(PowerShellLiveObjectContract contract, IntPtr comObject)
        {
            if (managedFactory is not null)
            {
                return managedFactory(comObject);
            }

            if ((contract.Directions & PowerShellLiveObjectDirection.BridgeContract) != 0)
            {
                return CreateBridgeLease(contract, comObject);
            }

            IntPtr proxyHandle = IntPtr.Zero;
            int hresult = create(comObject, &proxyHandle);
            if (hresult != 0 || proxyHandle == IntPtr.Zero)
            {
                throw new COMException("The payload contract pack could not project the live object.", hresult);
            }

            return new FfiExternalLiveObjectLease(proxyHandle, release);
        }

        private FfiLiveObjectLease CreateBridgeLease(PowerShellLiveObjectContract contract, IntPtr comObject)
        {
            FfiBridgeContractSink? sink = null;
            IntPtr sinkPointer = IntPtr.Zero;
            IntPtr proxyHandle = IntPtr.Zero;
            try
            {
                sink = new FfiBridgeContractSink(contract, comObject);
                sinkPointer = sink.Export();
                int hresult = create(sinkPointer, &proxyHandle);
                if (hresult != 0 || proxyHandle == IntPtr.Zero)
                {
                    throw new COMException("The bridge payload contract pack could not project the live object.", hresult);
                }

                if (!sink.IsDeclared)
                {
                    throw new InvalidOperationException(
                        "The bridge payload contract pack returned a proxy without declaring the requested contract.");
                }

                FfiLiveObjectLease lease = new FfiBridgeContractLease(proxyHandle, release, sink);
                proxyHandle = IntPtr.Zero;
                sink = null;
                return lease;
            }
            finally
            {
                if (sinkPointer != IntPtr.Zero)
                {
                    PowerShellBridgeComReference.Release(sinkPointer);
                }

                if (proxyHandle != IntPtr.Zero)
                {
                    release(proxyHandle);
                }

                sink?.Dispose();
            }
        }

        internal FfiLiveObjectLease CreateBridgeBrokerLease(
            PowerShellLiveObjectContract contract,
            FfiBridgeBrokerSink sink)
        {
            IntPtr sinkPointer = IntPtr.Zero;
            IntPtr proxyHandle = IntPtr.Zero;
            try
            {
                sinkPointer = sink.Export();
                int hresult = create(sinkPointer, &proxyHandle);
                if (hresult != 0 || proxyHandle == IntPtr.Zero)
                {
                    throw new COMException("The bridge payload contract pack could not project the broker bridge.", hresult);
                }

                if (!sink.IsDeclared)
                {
                    throw new InvalidOperationException(
                        "The bridge payload contract pack returned a proxy without declaring the attached bridge.");
                }

                FfiLiveObjectLease lease = new FfiBridgeContractLease(proxyHandle, release, sink);
                proxyHandle = IntPtr.Zero;
                sink = null!;
                return lease;
            }
            finally
            {
                if (sinkPointer != IntPtr.Zero)
                {
                    PowerShellBridgeComReference.Release(sinkPointer);
                }

                if (proxyHandle != IntPtr.Zero)
                {
                    release(proxyHandle);
                }

                sink?.Dispose();
            }
        }
    }
}
