namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A payload-local credential adapter. Its password never becomes a
/// <see cref="PowerShellValue"/> or a session variable.
/// </summary>
public sealed class PowerShellCredential : IDisposable
{
    public PowerShellCredential(string userName, PowerShellSecret password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        if (userName.IndexOf('\0') >= 0 || userName.Length > 256)
        {
            throw new ArgumentException("Credential user names must be non-NUL text of at most 256 characters.", nameof(userName));
        }

        UserName = userName;
        Password = password;
    }

    public string UserName { get; }

    internal PowerShellSecret Password { get; }

    public override string ToString() => $"<redacted PowerShell credential for '{UserName}'>";

    public void Dispose()
    {
        Password.Dispose();
    }
}
