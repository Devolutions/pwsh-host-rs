using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Experimental proof that a payload-owned PowerShell object can be projected
/// through a portable in-process <c>IUnknown</c> contract.
/// </summary>
public sealed unsafe class PowerShellLiveObjectProbe : IDisposable
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly object gate = new();
    private IPowerShellLiveObjectProbe? proxy;
    private ComObject? comObject;
    private nint pointer;

    private PowerShellLiveObjectProbe(
        IPowerShellLiveObjectProbe proxy,
        ComObject comObject,
        nint pointer)
    {
        this.proxy = proxy;
        this.comObject = comObject;
        this.pointer = pointer;
    }

    public long Count => Invoke(static (IPowerShellLiveObjectProbe value, out long count) => value.GetCount(out count));

    public long Increment()
    {
        return Invoke(static (IPowerShellLiveObjectProbe value, out long count) => value.Increment(out count));
    }

    internal static PowerShellLiveObjectProbe Create(long initialCount)
    {
        nint pointer = 0;
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.CreateLiveObjectProbe(initialCount, &pointer, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        if (pointer == 0)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned a null live object probe pointer.");
        }

        ComObject? comObject = null;
        bool transitReleaseAttempted = false;
        try
        {
            object projected = ComWrappers.GetOrCreateObjectForComInstance(
                pointer,
                CreateObjectFlags.UniqueInstance);
            comObject = projected as ComObject
                ?? throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI did not create a source-generated COM wrapper.");
            var proxy = projected as IPowerShellLiveObjectProbe
                ?? throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned an object with an unexpected COM contract.");

            transitReleaseAttempted = true;
            ReleaseTransitReference(pointer);
            return new PowerShellLiveObjectProbe(proxy, comObject, pointer);
        }
        catch
        {
            if (comObject is not null)
            {
                comObject.FinalRelease();
            }

            if (!transitReleaseAttempted)
            {
                ReleaseTransitReference(pointer);
            }

            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            ComObject? value = comObject;
            nint valuePointer = pointer;
            if (value is null || valuePointer == 0)
            {
                return;
            }

            Unregister(valuePointer);
            proxy = null;
            comObject = null;
            pointer = 0;
            value.FinalRelease();
        }
    }

    internal void AddTo(PowerShell powerShell)
    {
        lock (gate)
        {
            ArgumentNullException.ThrowIfNull(powerShell);
            if (proxy is null || pointer == 0)
            {
                throw new ObjectDisposedException(nameof(PowerShellLiveObjectProbe));
            }

            powerShell.AddLiveObjectProbe(pointer);
        }
    }

    private long Invoke(ProbeOperation operation)
    {
        lock (gate)
        {
            if (proxy is null)
            {
                throw new ObjectDisposedException(nameof(PowerShellLiveObjectProbe));
            }

            int hresult = operation(proxy, out long count);
            if (hresult != 0)
            {
                throw new COMException("The payload live object probe call failed.", hresult);
            }

            return count;
        }
    }

    private static void ReleaseTransitReference(nint pointer)
    {
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.ReleaseLiveObjectProbe(pointer, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
    }

    private static void Unregister(nint pointer)
    {
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.UnregisterLiveObjectProbe(pointer, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
    }

    private delegate int ProbeOperation(IPowerShellLiveObjectProbe value, out long count);
}
