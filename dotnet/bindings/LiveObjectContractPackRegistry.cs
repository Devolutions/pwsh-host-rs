#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace NativeHost;

internal abstract class FfiLiveObjectLease : IDisposable
{
    internal abstract object Value { get; }

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

        return registration!.CreateLease(comObject);
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

        internal FfiLiveObjectLease CreateLease(IntPtr comObject)
        {
            if (managedFactory is not null)
            {
                return managedFactory(comObject);
            }

            IntPtr proxyHandle = IntPtr.Zero;
            int hresult = create(comObject, &proxyHandle);
            if (hresult != 0 || proxyHandle == IntPtr.Zero)
            {
                throw new COMException("The payload contract pack could not project the live object.", hresult);
            }

            return new FfiExternalLiveObjectLease(proxyHandle, release);
        }
    }
}
