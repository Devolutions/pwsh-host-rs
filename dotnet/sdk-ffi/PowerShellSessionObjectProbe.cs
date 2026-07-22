using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Experimental proof that a .NET 10-owned object can be invoked from a
/// PowerShell session variable through a portable in-process <c>IUnknown</c>.
/// </summary>
public sealed unsafe partial class PowerShellSessionObjectProbe : IDisposable
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly object gate = new();
    private readonly FfiSessionObjectProbeBroker broker;
    private bool disposed;

    public PowerShellSessionObjectProbe(long initialCount)
    {
        broker = new FfiSessionObjectProbeBroker(initialCount);
    }

    public long Count => Invoke(static (FfiSessionObjectProbeBroker value, out long count) => value.GetCount(out count));

    public long Increment()
    {
        return Invoke(static (FfiSessionObjectProbeBroker value, out long count) => value.Increment(out count));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            broker.Dispose();
            disposed = true;
        }
    }

    internal void AssignToSession(Action<nint> assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        lock (gate)
        {
            ThrowIfDisposed();
            nint pointer = ComWrappers.GetOrCreateComInterfaceForObject(
                broker,
                CreateComInterfaceFlags.None);
            if (pointer == 0)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "The .NET session object probe did not create an IUnknown pointer.");
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

    internal static void ReleaseTransitReference(nint pointer)
    {
        if (pointer == 0)
        {
            throw new ArgumentException("Live object probe pointer is null.", nameof(pointer));
        }

        IntPtr* vtable = *(IntPtr**)pointer;
        if (vtable == null || vtable[2] == IntPtr.Zero)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "The .NET session object probe has an invalid IUnknown vtable.");
        }

        var release = (delegate* unmanaged[MemberFunction]<nint, uint>)vtable[2];
        _ = release(pointer);
    }

    private long Invoke(ProbeOperation operation)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            int hresult = operation(broker, out long count);
            if (hresult != 0)
            {
                throw new COMException("The .NET session object probe call failed.", hresult);
            }

            return count;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private delegate int ProbeOperation(FfiSessionObjectProbeBroker value, out long count);

    [GeneratedComClass]
    private sealed partial class FfiSessionObjectProbeBroker : IPowerShellLiveObjectProbe
    {
        private const int EFail = unchecked((int)0x80004005);
        private readonly object gate = new();
        private long count;
        private bool disposed;

        public FfiSessionObjectProbeBroker(long initialCount)
        {
            count = initialCount;
        }

        public int GetCount(out long value)
        {
            lock (gate)
            {
                if (disposed)
                {
                    value = default;
                    return EFail;
                }

                value = count;
                return 0;
            }
        }

        public int Increment(out long value)
        {
            lock (gate)
            {
                if (disposed || count == long.MaxValue)
                {
                    value = default;
                    return EFail;
                }

                value = ++count;
                return 0;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
            }
        }
    }
}
