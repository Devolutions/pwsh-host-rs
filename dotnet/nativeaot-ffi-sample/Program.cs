using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Devolutions.PowerShell.Ffi;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: NativeAotFfiSample <payload-directory> <manifest-path> <manifest-sha256>");
    return 2;
}

PowerShellRuntime runtime = PowerShellRuntime.Activate(
    new PowerShellPayloadActivationOptions(args[0], args[1], args[2]));
using PowerShell powerShell = runtime.Create();
PowerShellInvocationResult output = powerShell.AddScript("'nativeaot-in-process'").Invoke();
PowerShellValue outputScalar = output.Output.Records.Count == 1
    ? output.Output.Records[0].ScalarValue
    : null;

if (output.Output.Records.Count != 1 ||
    output.Output.Records[0].DisplayText != "nativeaot-in-process" ||
    output.Output.Records[0].TypeNames.Count == 0 ||
    outputScalar?.Kind != PowerShellValueKind.String ||
    outputScalar is null ||
    !outputScalar.TryGetString(out string outputText) ||
    outputText != "nativeaot-in-process" ||
    output.Output.TotalRecordCount != 1 ||
    output.Output.DroppedRecordCount != 0 ||
    output.State != PowerShellInvocationState.Completed ||
    output.InvocationId == 0 ||
    output.HadErrors)
{
    Console.Error.WriteLine("Unexpected PowerShell output snapshot.");
    return 1;
}

using (PowerShellSession session = runtime.CreateSession(
    new PowerShellSessionOptions(
        historyMode: PowerShellSessionHistoryMode.Enabled,
        errorPreference: PowerShellSessionPreference.Stop,
        warningPreference: PowerShellSessionPreference.Continue)))
using (PowerShell sessionPowerShell = session.CreatePowerShell())
{
    PowerShellInvocationResult sessionOutput = sessionPowerShell
        .AddScript("Write-Output \"$ErrorActionPreference|$WarningPreference\"")
        .Invoke();
    PowerShellSessionSnapshot snapshot = session.GetSnapshot();
    if (sessionOutput.Output.Records.Count != 1 ||
        sessionOutput.Output.Records[0].DisplayText != "Stop|Continue" ||
        snapshot.State != PowerShellSessionState.Opened ||
        snapshot.InvocationCount != 1 ||
        snapshot.HistoryCount != 1 ||
        session.GetEvents().Count < 3)
    {
        Console.Error.WriteLine("NativeAOT facade did not preserve bounded session settings and state.");
        return 1;
    }
}

using PowerShell asynchronous = PowerShell.Create();
PowerShellInvocationResult asynchronousOutput = await asynchronous
    .AddScript("'nativeaot-in-process-async'")
    .InvokeAsync();
if (asynchronousOutput.Output.Records.Count != 1 ||
    asynchronousOutput.Output.Records[0].DisplayText != "nativeaot-in-process-async" ||
    asynchronousOutput.State != PowerShellInvocationState.Completed)
{
    Console.Error.WriteLine("NativeAOT facade did not preserve the async operation result.");
    return 1;
}

using (var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
using (PowerShell cancellation = PowerShell.Create())
{
    try
    {
        _ = await cancellation
            .AddScript("Start-Sleep -Seconds 30; 'unexpected async completion'")
            .InvokeAsync(cancellationSource.Token);
        Console.Error.WriteLine("NativeAOT facade did not cancel the async operation.");
        return 1;
    }
    catch (OperationCanceledException)
    {
    }
}

using (PowerShell doubleStop = PowerShell.Create())
using (PowerShellInvocationOperation operation = doubleStop
    .AddScript("Start-Sleep -Seconds 30; 'unexpected double-stop completion'")
    .BeginInvoke())
{
    operation.Stop();
    operation.Stop();
    PowerShellInvocationOperationStatus status = operation.Wait(TimeSpan.FromSeconds(5));
    if (status.State != PowerShellOperationState.Cancelled ||
        status.TerminalStatus != PowerShellFfiStatus.OperationCancelled)
    {
        Console.Error.WriteLine("NativeAOT facade did not preserve the cancellation terminal state.");
        return 1;
    }

    try
    {
        _ = operation.GetResult();
        Console.Error.WriteLine("NativeAOT facade exposed a cancelled operation result.");
        return 1;
    }
    catch (PowerShellFfiException exception) when (exception.Status == PowerShellFfiStatus.OperationCancelled)
    {
    }
}

using PowerShell nonTerminating = PowerShell.Create();
PowerShellInvocationResult diagnostics = nonTerminating
    .AddScript(@"
        Write-Error -Message 'nativeaot-non-terminating-error' -Category InvalidOperation -TargetObject 42
        Write-Warning 'nativeaot-warning'
        Write-Verbose 'nativeaot-verbose' -Verbose
        Write-Debug 'nativeaot-debug' -Debug
        Write-Information 'nativeaot-information' -InformationAction Continue
        Write-Progress -Activity 'nativeaot-progress' -Status 'running' -PercentComplete 50
    ")
    .InvokeWithDiagnostics();
if (diagnostics.Errors.Records.Count != 1 ||
    !diagnostics.Errors.Records[0].Message.Contains("nativeaot-non-terminating-error", StringComparison.Ordinal) ||
    diagnostics.Warnings.Records.Count != 1 ||
    diagnostics.Verbose.Records.Count != 1 ||
    diagnostics.Debug.Records.Count != 1 ||
    diagnostics.Information.Records.Count != 1 ||
    diagnostics.Progress.Records.Count != 1 ||
    diagnostics.Sequence.Count < 6 ||
    !diagnostics.HadErrors ||
    diagnostics.State != PowerShellInvocationState.Completed)
{
    Console.Error.WriteLine("NativeAOT facade did not preserve the bounded stream snapshot.");
    return 1;
}

PowerShellInvocationError diagnosticError = diagnostics.Errors.Records[0];
if (diagnostics.Errors.TotalRecordCount != 1 ||
    diagnostics.Errors.DroppedRecordCount != 0 ||
    diagnosticError.CategoryReason.Length == 0 ||
    diagnosticError.CommandName.Length == 0 ||
    diagnosticError.TargetValue?.Kind != PowerShellValueKind.SignedInteger)
{
    Console.Error.WriteLine("NativeAOT facade did not preserve copied error context and totals.");
    return 1;
}

using PowerShell projected = PowerShell.Create();
PowerShellInvocationResult projectionResult = projected
    .AddScript("[pscustomobject]@{ Name = 'projection'; Count = 2; Nested = @{ Value = 1 }; Items = 1, 2 }")
    .Invoke();
PowerShellObjectSnapshot projection = projectionResult.Output.Records.Single();
if (projection.ScalarValue is not null ||
    projection.PropertyBag?.Kind != PowerShellValueKind.PropertyBag ||
    projection.PropertyEntryCount != 2 ||
    projection.DroppedPropertyEntryCount != 2 ||
    projection.IsPropertyBagTruncated ||
    projection.TypeNameCount == 0)
{
    Console.Error.WriteLine("NativeAOT facade did not preserve the bounded scalar/property projection contract.");
    return 1;
}

byte[] serializedSnapshot = PowerShellSnapshotSerializer.Serialize(projectionResult);
PowerShellInvocationResult deserializedSnapshot = PowerShellSnapshotSerializer.Deserialize(serializedSnapshot);
if (deserializedSnapshot.Output.Records.Single().PropertyBag?.Kind != PowerShellValueKind.PropertyBag ||
    deserializedSnapshot.Output.Records.Single().DroppedPropertyEntryCount != 2)
{
    Console.Error.WriteLine("NativeAOT facade did not round-trip the storage/display snapshot format.");
    return 1;
}

try
{
    _ = PowerShellSnapshotSerializer.Deserialize(new byte[PowerShellSnapshotSerializer.MaxDocumentBytes + 1]);
    Console.Error.WriteLine("NativeAOT facade accepted an oversized snapshot document.");
    return 1;
}
catch (ArgumentOutOfRangeException)
{
}

try
{
    _ = PowerShellSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"version\":99,\"result\":null}"));
    Console.Error.WriteLine("NativeAOT facade accepted an invalid snapshot document.");
    return 1;
}
catch (ArgumentException)
{
}

try
{
    string withUnknownMember = Encoding.UTF8.GetString(serializedSnapshot)[..^1] + ",\"unexpected\":true}";
    _ = PowerShellSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes(withUnknownMember));
    Console.Error.WriteLine("NativeAOT facade accepted an unknown snapshot member.");
    return 1;
}
catch (ArgumentException)
{
}

try
{
    _ = PowerShellSnapshotSerializer.Deserialize(Encoding.UTF8.GetBytes(new string('[', 17) + new string(']', 17)));
    Console.Error.WriteLine("NativeAOT facade accepted an over-depth snapshot document.");
    return 1;
}
catch (ArgumentException)
{
}

using PowerShell terminating = PowerShell.Create();
try
{
    terminating.AddScript("throw 'nativeaot-terminating-error'").Invoke();
    Console.Error.WriteLine("NativeAOT facade did not report the terminating error.");
    return 1;
}
catch (PowerShellInvocationException exception) when (
    exception.InvocationResult.Errors.Records.Count == 1 &&
    exception.InvocationResult.Errors.Records[0].Message.Contains("nativeaot-terminating-error", StringComparison.Ordinal) &&
    exception.InvocationResult.HadErrors &&
    exception.InvocationResult.State == PowerShellInvocationState.Terminated)
{
}

using PowerShell values = PowerShell.Create();
PowerShellInvocationResult valueResult = values
    .AddScript(
        "param($Text, $Number, [switch] $Flag, $Bag) \"$Text|$Number|$Flag|$($Bag.Name):$($Bag.Count)\"",
        useLocalScope: true)
    .AddParameters(
    [
        new KeyValuePair<string, PowerShellValue>("Text", PowerShellValue.String("tagged")),
        new KeyValuePair<string, PowerShellValue>("Number", PowerShellValue.Decimal(42.5m)),
        new KeyValuePair<string, PowerShellValue>(
            "Bag",
            PowerShellValue.PropertyBag(
            [
                new KeyValuePair<string, PowerShellValue>("Name", PowerShellValue.String("snapshot")),
                new KeyValuePair<string, PowerShellValue>("Count", PowerShellValue.SignedInteger(2)),
            ])),
    ])
    .AddParameter("Flag")
    .Invoke();
if (valueResult.Output.Records.Count != 1 ||
    valueResult.Output.Records[0].DisplayText != "tagged|42.5|True|snapshot:2")
{
    Console.Error.WriteLine("NativeAOT facade did not preserve tagged values, property bags, or switch parameters.");
    return 1;
}

using PowerShell input = PowerShell.Create();
PowerShellInvocationResult inputResult = input
    .AddScript("$input | ForEach-Object { $_ * 2 }")
    .AddInputs([PowerShellValue.SignedInteger(3), PowerShellValue.SignedInteger(4)])
    .CompleteInput()
    .Invoke();
if (inputResult.Output.Records.Count != 2 ||
    inputResult.Output.Records[0].DisplayText != "6" ||
    inputResult.Output.Records[1].DisplayText != "8")
{
    Console.Error.WriteLine("NativeAOT facade did not preserve bounded completed input.");
    return 1;
}

try
{
    _ = PowerShellValue.From((Func<int>)(() => 1));
    Console.Error.WriteLine("NativeAOT facade accepted an unsupported delegate value.");
    return 1;
}
catch (PowerShellValueConversionException)
{
}

Console.WriteLine("NativeAOT in-process PowerShell FFI: Success");
return 0;
