namespace Devolutions.PowerShell.Ffi;

public sealed unsafe class PowerShellSessionPool : IDisposable
{
    internal PowerShellSessionPool()
    {
    }

    internal static PowerShellSessionPool Create(PowerShellSessionPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PowerShell.EnsureSupportedAbi();
        NativeSessionPoolOptions nativeOptions = new()
        {
            Size = checked((uint)sizeof(NativeSessionPoolOptions)),
            MinimumSessions = options.MinimumSessions,
            MaximumSessions = options.MaximumSessions,
        };
        ulong poolHandle = 0;
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.CreateSessionPool(&nativeOptions, &poolHandle, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        throw new PowerShellFfiException(
            PowerShellFfiStatus.UnsupportedCapability,
            "Native PowerShell FFI unexpectedly accepted an unsupported session pool.");
    }

    public void Dispose()
    {
    }
}
