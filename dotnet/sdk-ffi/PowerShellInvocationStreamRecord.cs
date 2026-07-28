namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellInvocationStreamRecord
{
    internal PowerShellInvocationStreamRecord(
        PowerShellStreamKind stream,
        ulong sequence,
        string displayText,
        bool isTruncated)
    {
        Stream = stream;
        Sequence = sequence;
        DisplayText = displayText;
        IsTruncated = isTruncated;
    }

    public PowerShellStreamKind Stream { get; }

    public ulong Sequence { get; }

    public string DisplayText { get; }

    public bool IsTruncated { get; }
}
