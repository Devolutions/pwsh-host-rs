using Microsoft.Win32.SafeHandles;

namespace Devolutions.PowerShell.Ffi;

internal sealed unsafe class PowerShellHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal PowerShellHandle(ulong value)
        : base(ownsHandle: true)
    {
        SetHandle(unchecked((nint)value));
    }

    internal HandleLease Borrow()
    {
        bool addedRef = false;
        try
        {
            DangerousAddRef(ref addedRef);
            if (IsInvalid)
            {
                throw new ObjectDisposedException(nameof(PowerShell));
            }

            return new HandleLease(this);
        }
        catch
        {
            if (addedRef)
            {
                DangerousRelease();
            }

            throw;
        }
    }

    protected override bool ReleaseHandle()
    {
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.Release(unchecked((ulong)handle), &result);
        return status == (int)PowerShellFfiStatus.Success &&
            result.Status == (int)PowerShellFfiStatus.Success;
    }

    internal sealed class HandleLease : IDisposable
    {
        private PowerShellHandle? owner;

        internal HandleLease(PowerShellHandle owner)
        {
            this.owner = owner;
            Value = unchecked((ulong)owner.DangerousGetHandle());
        }

        internal ulong Value { get; }

        public void Dispose()
        {
            PowerShellHandle? current = Interlocked.Exchange(ref owner, null);
            current?.DangerousRelease();
        }
    }
}
