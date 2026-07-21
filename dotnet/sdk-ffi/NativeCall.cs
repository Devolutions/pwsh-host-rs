using System.Text;

namespace Devolutions.PowerShell.Ffi;

internal static unsafe class NativeCall
{
    internal const int DiagnosticCapacity = 4096;

    internal static NativeCallResult CreateResult(byte* diagnostic)
    {
        return new NativeCallResult
        {
            Size = checked((uint)sizeof(NativeCallResult)),
            Diagnostic = diagnostic,
            DiagnosticCapacity = DiagnosticCapacity,
        };
    }

    internal static void ThrowIfFailed(int status, in NativeCallResult result, byte* diagnostic)
    {
        if (status == (int)PowerShellFfiStatus.Success &&
            result.Status == (int)PowerShellFfiStatus.Success)
        {
            return;
        }

        PowerShellFfiStatus effectiveStatus = EffectiveStatus(status, result);
        throw new PowerShellFfiException(effectiveStatus, ReadDiagnostic(result, diagnostic));
    }

    internal static PowerShellFfiStatus EffectiveStatus(int status, in NativeCallResult result)
    {
        int effectiveStatus = result.Status != (int)PowerShellFfiStatus.Success
            ? result.Status
            : status;
        return Enum.IsDefined((PowerShellFfiStatus)effectiveStatus)
            ? (PowerShellFfiStatus)effectiveStatus
            : PowerShellFfiStatus.Panic;
    }

    internal static string ReadDiagnostic(in NativeCallResult result, byte* diagnostic)
    {
        if (result.DiagnosticWritten == 0)
        {
            return $"Native PowerShell FFI returned status {result.Status}.";
        }

        int written = checked((int)result.DiagnosticWritten);
        string message = Encoding.UTF8.GetString(diagnostic, written);
        return (result.Flags & 1) == 0
            ? message
            : $"{message} (diagnostic truncated; {result.DiagnosticRequired} UTF-8 bytes required)";
    }
}
