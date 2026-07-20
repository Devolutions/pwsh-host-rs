namespace Devolutions.PowerShell.Ffi;

public class PowerShellFfiException : InvalidOperationException
{
    protected internal PowerShellFfiException(PowerShellFfiStatus status, string diagnostic)
        : base($"Native PowerShell FFI failed with status {status}: {diagnostic}")
    {
        Status = status;
    }

    public PowerShellFfiStatus Status { get; }
}
