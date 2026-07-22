using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Marks a consumer-owned generated-COM broker whose disposal makes future
/// contract calls fail according to that contract's HRESULT policy.
/// </summary>
public interface IPowerShellLiveObjectBroker : IDisposable
{
}

/// <summary>
/// Owns the consumer-side COM lease for one registered live-object contract.
/// </summary>
public sealed unsafe class PowerShellLiveObject<TContract> : IDisposable
    where TContract : class
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly object gate = new();
    private IPowerShellLiveObjectBroker? broker;

    public PowerShellLiveObject(
        PowerShellLiveObjectContract contract,
        TContract broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (!typeof(TContract).IsInterface || contract.InterfaceId != typeof(TContract).GUID)
        {
            throw new ArgumentException(
                "The live object contract must identify the generated COM interface.",
                nameof(contract));
        }

        this.broker = broker as IPowerShellLiveObjectBroker
            ?? throw new ArgumentException(
                "The live object broker must implement IPowerShellLiveObjectBroker.",
                nameof(broker));
        Contract = contract;
    }

    public PowerShellLiveObjectContract Contract { get; }

    public void Dispose()
    {
        lock (gate)
        {
            IPowerShellLiveObjectBroker? value = broker;
            if (value is null)
            {
                return;
            }

            broker = null;
            value.Dispose();
        }
    }

    internal void AssignToSession(Action<nint> assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        lock (gate)
        {
            IPowerShellLiveObjectBroker value = broker
                ?? throw new ObjectDisposedException(nameof(PowerShellLiveObject<TContract>));
            nint pointer = ComWrappers.GetOrCreateComInterfaceForObject(
                value,
                CreateComInterfaceFlags.None);
            if (pointer == 0)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "The live object broker did not create an IUnknown pointer.");
            }

            try
            {
                assignment(pointer);
            }
            finally
            {
                ReleaseTransitReference(pointer);
            }
        }
    }

    private static void ReleaseTransitReference(nint pointer)
    {
        IntPtr* vtable = *(IntPtr**)pointer;
        if (vtable == null || vtable[2] == IntPtr.Zero)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "The live object broker has an invalid IUnknown vtable.");
        }

        var release = (delegate* unmanaged[MemberFunction]<nint, uint>)vtable[2];
        _ = release(pointer);
    }
}
