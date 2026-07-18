namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Declares the only supported secret-transfer policy for this facade.
/// </summary>
public enum PowerShellSecretTransferPolicy
{
    Rejected = 0,
}

/// <summary>
/// Thrown when an application requests secret or credential transfer across the FFI boundary.
/// </summary>
public sealed class PowerShellSecretTransferNotSupportedException : NotSupportedException
{
    internal PowerShellSecretTransferNotSupportedException()
        : base(
            "Secret and credential transfer is intentionally unsupported. " +
            "The payload can expose or transform a credential's secret through arbitrary PowerShell output, " +
            "so this boundary cannot guarantee redaction, serialization safety, or a zeroable managed lifetime.")
    {
    }
}

/// <summary>
/// Provides the explicit rejection boundary for secrets and credentials.
/// </summary>
public static class PowerShellSecretTransfer
{
    public static PowerShellSecretTransferPolicy Policy => PowerShellSecretTransferPolicy.Rejected;

    /// <summary>
    /// Throws a typed exception instead of accepting a password, credential, SecureString, or serialized secret.
    /// </summary>
    public static void ThrowNotSupported()
    {
        throw new PowerShellSecretTransferNotSupportedException();
    }
}
