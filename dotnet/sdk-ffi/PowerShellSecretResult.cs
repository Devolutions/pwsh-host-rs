namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// Explicitly approved result shapes for a secret-bound pipeline.
/// </summary>
public enum PowerShellSecretResultKind
{
    None = 0,
    SecureString = 1,
    Credential = 2,
}

/// <summary>
/// Result from a secret-bound invocation. Normal PowerShell output and
/// diagnostics are intentionally unavailable from this result.
/// </summary>
public sealed class PowerShellSecretResult : IDisposable
{
    internal PowerShellSecretResult(PowerShellSecretResultKind kind, PowerShellSecret? secret, PowerShellCredential? credential)
    {
        Kind = kind;
        Secret = secret;
        Credential = credential;
    }

    public PowerShellSecretResultKind Kind { get; }

    public PowerShellSecret? Secret { get; }

    public PowerShellCredential? Credential { get; }

    public override string ToString() => $"<redacted PowerShell secret result: {Kind}>";

    public void Dispose()
    {
        Credential?.Dispose();
        Secret?.Dispose();
    }
}
