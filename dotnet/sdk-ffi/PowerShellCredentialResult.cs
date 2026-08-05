namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A redacted, disposable result from a payload-local credential result sink.
/// </summary>
public sealed class PowerShellCredentialResult : IDisposable
{
    internal PowerShellCredentialResult(
        string username,
        string domain,
        PowerShellSecret? password,
        bool isCancelled,
        string outputMessages,
        string errorMessages,
        string logMessage)
    {
        Username = username;
        Domain = domain;
        Password = password;
        IsCancelled = isCancelled;
        OutputMessages = outputMessages;
        ErrorMessages = errorMessages;
        LogMessage = logMessage;
    }

    public string Username { get; }

    public string Domain { get; }

    /// <summary>
    /// The payload-local password, when the script assigned either Password or SecurePassword.
    /// </summary>
    public PowerShellSecret? Password { get; }

    public bool IsCancelled { get; }

    public string OutputMessages { get; }

    public string ErrorMessages { get; }

    public string LogMessage { get; }

    public override string ToString() =>
        $"<redacted PowerShell credential result: cancelled={IsCancelled}, password={(Password is null ? "absent" : "present")}>";

    public void Dispose()
    {
        Password?.Dispose();
    }
}
