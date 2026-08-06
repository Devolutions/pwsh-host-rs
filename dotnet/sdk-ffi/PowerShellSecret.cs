using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace Devolutions.PowerShell.Ffi;

/// <summary>
/// A non-displayable, non-serializable, zeroable local secret lease.
/// </summary>
public sealed class PowerShellSecret : IDisposable
{
    internal const int MaximumLength = 4_096;
    private char[]? value;

    private PowerShellSecret(char[] value)
    {
        this.value = value;
    }

    /// <summary>
    /// Copies a secret into a bounded, disposable lease.
    /// </summary>
    public static PowerShellSecret Create(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > MaximumLength || value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                $"Secrets must contain between 1 and {MaximumLength} non-NUL UTF-16 code units.",
                nameof(value));
        }

        return new PowerShellSecret(value.ToArray());
    }

    internal static PowerShellSecret TakeOwnership(char[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > MaximumLength || value.AsSpan().IndexOf('\0') >= 0)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
            throw new ArgumentException("The payload returned an invalid secret value.", nameof(value));
        }

        return new PowerShellSecret(value);
    }

    /// <summary>
    /// Gets the number of UTF-16 code units in this secret.
    /// </summary>
    public int Length => (value ?? throw new ObjectDisposedException(nameof(PowerShellSecret))).Length;

    /// <summary>
    /// Copies the secret to a caller-owned buffer. The caller must clear the
    /// buffer after use.
    /// </summary>
    public void CopyTo(Span<char> destination)
    {
        char[] source = value ?? throw new ObjectDisposedException(nameof(PowerShellSecret));
        if (destination.Length != source.Length)
        {
            throw new ArgumentException("Secret buffer length is invalid.", nameof(destination));
        }

        source.AsSpan().CopyTo(destination);
    }

    /// <summary>
    /// Creates an independently owned, read-only <see cref="SecureString"/>.
    /// The caller must dispose the returned value.
    /// </summary>
    public SecureString ToSecureString()
    {
        char[] source = value ?? throw new ObjectDisposedException(nameof(PowerShellSecret));
        var secureString = new SecureString();
        foreach (char character in source)
        {
            secureString.AppendChar(character);
        }

        secureString.MakeReadOnly();
        return secureString;
    }

    public override string ToString() => "<redacted PowerShell secret>";

    public void Dispose()
    {
        char[]? source = Interlocked.Exchange(ref value, null);
        if (source is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(source.AsSpan()));
        }
    }
}
