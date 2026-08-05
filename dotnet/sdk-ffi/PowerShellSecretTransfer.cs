namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Declares whether the opt-in, payload-local secret adapter is available.
/// </summary>
public enum PowerShellSecretTransferPolicy
{
    Rejected = 0,

    /// <summary>
    /// Secrets are accepted only through <see cref="PowerShellSecret"/> and
    /// <see cref="PowerShellCredential"/> parameter adapters.
    /// </summary>
    ExplicitLeaseOnly = 1,
}

/// <summary>
/// Provides the explicit boundary for payload-local secret transfer.
/// </summary>
/// <remarks>
/// This API does not make arbitrary PowerShell scripts trustworthy. It prevents
/// secrets from entering copied values, session variables, snapshots, and normal
/// diagnostics. Secret-bound pipelines must use an explicitly selected result
/// shape, which either discards normal output or returns a leased secret value.
/// </remarks>
public static class PowerShellSecretTransfer
{
    /// <summary>
    /// Reports the legacy implicit-transfer policy. Explicit leases are a
    /// separate opt-in API and do not change this compatibility contract.
    /// </summary>
    public static PowerShellSecretTransferPolicy Policy => PowerShellSecretTransferPolicy.Rejected;

    /// <summary>
    /// Rejects implicit transfer for callers that retain the legacy behavior.
    /// </summary>
    public static void ThrowNotSupported()
    {
        throw new PowerShellSecretTransferNotSupportedException();
    }
}

public sealed class PowerShellSecretTransferNotSupportedException : NotSupportedException
{
    internal PowerShellSecretTransferNotSupportedException()
        : base(
            "Secret and credential transfer is intentionally unsupported. " +
            "Use the separate explicit lease API for bounded payload-local secret binding.")
    {
    }
}
