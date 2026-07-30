using Microsoft.Win32.SafeHandles;

namespace Devolutions.PowerShell.Ffi;

internal sealed unsafe class PowerShellTypedResultInvocationHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly ulong nativeValue;

    internal PowerShellTypedResultInvocationHandle(ulong value)
        : base(ownsHandle: true)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        nativeValue = value;
        SetHandle((nint)1);
    }

    internal HandleLease Borrow()
    {
        bool addedRef = false;
        try
        {
            DangerousAddRef(ref addedRef);
            if (IsInvalid)
            {
                throw new ObjectDisposedException(nameof(PowerShellTypedResultInvocation));
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
        int status = NativeMethods.ReleaseTypedResultInvocation(nativeValue, &result);
        return status == (int)PowerShellFfiStatus.Success &&
            result.Status == (int)PowerShellFfiStatus.Success;
    }

    internal sealed class HandleLease : IDisposable
    {
        private PowerShellTypedResultInvocationHandle? owner;

        internal HandleLease(PowerShellTypedResultInvocationHandle owner)
        {
            this.owner = owner;
            Value = owner.nativeValue;
        }

        internal ulong Value { get; }

        public void Dispose()
        {
            PowerShellTypedResultInvocationHandle? current = Interlocked.Exchange(ref owner, null);
            current?.DangerousRelease();
        }
    }
}
