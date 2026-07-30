using Microsoft.Win32.SafeHandles;

namespace Devolutions.PowerShell.Ffi;

internal sealed unsafe class PowerShellTypedResultPageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly ulong nativeValue;

    internal PowerShellTypedResultPageHandle(ulong value)
        : base(ownsHandle: true)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        nativeValue = value;
        SetHandle((nint)1);
    }

    internal ulong Value
    {
        get
        {
            if (IsInvalid)
            {
                throw new ObjectDisposedException(nameof(PowerShellTypedResultPageHandle));
            }

            return nativeValue;
        }
    }

    protected override bool ReleaseHandle()
    {
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.ReleaseTypedResultPage(nativeValue, &result);
        return status == (int)PowerShellFfiStatus.Success &&
            result.Status == (int)PowerShellFfiStatus.Success;
    }
}
