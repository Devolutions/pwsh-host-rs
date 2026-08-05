namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A bounded, redacted credential resolver result.
/// </summary>
public sealed class PowerShellCredentialResult : IDisposable
{
    internal PowerShellCredentialResult(
        string? username,
        string? domain,
        PowerShellSecret? password,
        IReadOnlyList<string> outputMessages,
        IReadOnlyList<string> errorMessages,
        string? logMessage,
        bool isCancelled)
    {
        Username = username;
        Domain = domain;
        Password = password;
        OutputMessages = outputMessages;
        ErrorMessages = errorMessages;
        LogMessage = logMessage;
        IsCancelled = isCancelled;
    }

    public string? Username { get; }

    public string? Domain { get; }

    public PowerShellSecret? Password { get; }

    public IReadOnlyList<string> OutputMessages { get; }

    public IReadOnlyList<string> ErrorMessages { get; }

    public string? LogMessage { get; }

    public bool IsCancelled { get; }

    public override string ToString() => "<redacted PowerShell credential result>";

    public void Dispose()
    {
        Password?.Dispose();
    }
}
