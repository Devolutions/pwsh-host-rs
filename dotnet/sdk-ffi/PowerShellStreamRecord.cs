namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellStreamRecord
{
    internal PowerShellStreamRecord(string displayText, ulong sequence, bool isTruncated)
    {
        DisplayText = displayText;
        Sequence = sequence;
        IsTruncated = isTruncated;
    }

    public string DisplayText { get; }

    public ulong Sequence { get; }

    public bool IsTruncated { get; }
}
