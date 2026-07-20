namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellStreamSequenceRecord
{
    internal PowerShellStreamSequenceRecord(PowerShellStreamKind stream, uint index, ulong sequence)
    {
        Stream = stream;
        Index = index;
        Sequence = sequence;
    }

    public PowerShellStreamKind Stream { get; }

    public uint Index { get; }

    public ulong Sequence { get; }
}
