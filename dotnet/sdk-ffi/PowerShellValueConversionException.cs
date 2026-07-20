namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellValueConversionException : ArgumentException
{
    internal PowerShellValueConversionException(Type sourceType, string message)
        : base(message)
    {
        SourceType = sourceType;
    }

    public Type SourceType { get; }
}
