namespace Devolutions.PowerShell.Ffi;

public enum PowerShellPayloadTrustPolicy
{
    HashPinnedManifest = 0,
    Direct = 1,

    [Obsolete("Use Direct payload activation without a manifest through PowerShell.Initialize or PowerShellRuntime.Activate.")]
    UnsafeUntrustedLocalDevelopment = Direct,
}
