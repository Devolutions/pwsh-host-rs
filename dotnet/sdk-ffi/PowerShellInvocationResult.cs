using System.Collections.Generic;

namespace Devolutions.PowerShell.Ffi;

public sealed class PowerShellInvocationResult
{
    internal PowerShellInvocationResult(
        PowerShellStreamSnapshot<PowerShellObjectSnapshot> output,
        PowerShellStreamSnapshot<PowerShellInvocationError> errors,
        PowerShellStreamSnapshot<PowerShellStreamRecord> warnings,
        PowerShellStreamSnapshot<PowerShellStreamRecord> verbose,
        PowerShellStreamSnapshot<PowerShellStreamRecord> debug,
        PowerShellStreamSnapshot<PowerShellStreamRecord> information,
        PowerShellStreamSnapshot<PowerShellStreamRecord> progress,
        IReadOnlyList<PowerShellStreamSequenceRecord> sequence,
        PowerShellInvocationState state,
        ulong invocationId,
        bool hadErrors,
        bool isTerminatingFailure,
        bool isSequenceTruncated)
    {
        Output = output;
        Errors = errors;
        Warnings = warnings;
        Verbose = verbose;
        Debug = debug;
        Information = information;
        Progress = progress;
        Sequence = sequence;
        State = state;
        InvocationId = invocationId;
        HadErrors = hadErrors;
        IsTerminatingFailure = isTerminatingFailure;
        IsSequenceTruncated = isSequenceTruncated;
    }

    public PowerShellStreamSnapshot<PowerShellObjectSnapshot> Output { get; }

    public PowerShellStreamSnapshot<PowerShellInvocationError> Errors { get; }

    public PowerShellStreamSnapshot<PowerShellStreamRecord> Warnings { get; }

    public PowerShellStreamSnapshot<PowerShellStreamRecord> Verbose { get; }

    public PowerShellStreamSnapshot<PowerShellStreamRecord> Debug { get; }

    public PowerShellStreamSnapshot<PowerShellStreamRecord> Information { get; }

    public PowerShellStreamSnapshot<PowerShellStreamRecord> Progress { get; }

    public IReadOnlyList<PowerShellStreamSequenceRecord> Sequence { get; }

    public PowerShellInvocationState State { get; }

    public ulong InvocationId { get; }

    public bool HadErrors { get; }

    public bool IsTerminatingFailure { get; }

    public bool IsSequenceTruncated { get; }
}
