using Microsoft.Win32.SafeHandles;

namespace Devolutions.PowerShell.Ffi;

internal sealed unsafe class PowerShellInvocationResultHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal PowerShellInvocationResultHandle(ulong value)
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
                throw new ObjectDisposedException(nameof(PowerShellInvocationResult));
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
        int status = NativeMethods.ReleaseInvocationResult(unchecked((ulong)handle), &result);
        return status == (int)PowerShellFfiStatus.Success &&
            result.Status == (int)PowerShellFfiStatus.Success;
    }

    internal sealed class HandleLease : IDisposable
    {
        private PowerShellInvocationResultHandle? owner;

        internal HandleLease(PowerShellInvocationResultHandle owner)
        {
            this.owner = owner;
            Value = unchecked((ulong)owner.DangerousGetHandle());
        }

        internal ulong Value { get; }

        public void Dispose()
        {
            PowerShellInvocationResultHandle? current = Interlocked.Exchange(ref owner, null);
            current?.DangerousRelease();
        }
    }
}
