using System.Runtime.InteropServices;
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

    internal void CopyTo(Span<char> destination)
    {
        char[] source = value ?? throw new ObjectDisposedException(nameof(PowerShellSecret));
        if (destination.Length != source.Length)
        {
            throw new ArgumentException("Secret buffer length is invalid.", nameof(destination));
        }

        source.AsSpan().CopyTo(destination);
    }

    internal int Length => (value ?? throw new ObjectDisposedException(nameof(PowerShellSecret))).Length;

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
