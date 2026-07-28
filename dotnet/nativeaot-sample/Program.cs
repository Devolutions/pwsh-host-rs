using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;
using NativeAotFfiSample;

string contractPackPath = System.IO.Path.Combine(
    AppContext.BaseDirectory,
    "Devolutions.MultiPwsh.LiveObject.TestPack.dll");
if (!System.IO.File.Exists(contractPackPath))
{
    Console.Error.WriteLine("NativeAOT facade did not publish the external live-object contract pack.");
    return 1;
}

PowerShellLiveObjectContractPack[] contractPacks =
[
    new PowerShellLiveObjectContractPack(
        contractPackPath,
        "Devolutions.MultiPwsh.LiveObject.TestPack.LiveObjectTestPack, Devolutions.MultiPwsh.LiveObject.TestPack"),
];

PowerShellRuntime runtime;
switch (args.Length)
{
    case 0:
        runtime = PowerShellRuntime.Activate(contractPacks);
        break;
    case 1:
        runtime = PowerShellRuntime.Activate(args[0], contractPacks);
        break;
    default:
        Console.Error.WriteLine("Usage: NativeAotFfiSample [payload-directory]");
        return 2;
}

if (!System.IO.File.Exists(System.IO.Path.Combine(runtime.PayloadDirectory, "pwsh.dll")))
{
    Console.Error.WriteLine("NativeAOT facade did not report the selected PowerShell payload.");
    return 1;
}

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

using (PowerShellLiveObjectProbe liveProbe = runtime.CreateLiveObjectProbe(41))
{
    if (liveProbe.Count != 41 || liveProbe.Increment() != 42)
    {
        Console.Error.WriteLine("NativeAOT facade did not project the live payload object.");
        return 1;
    }

    using PowerShell liveObjectRoundTrip = runtime.Create();
    PowerShellInvocationResult liveObjectOutput = liveObjectRoundTrip
        .AddScript("param($value) $value.Count += 1; $value.Count", useLocalScope: true)
        .AddArgument(liveProbe)
        .Invoke();
    if (liveObjectOutput.Output.Records.Count != 1 ||
        liveObjectOutput.Output.Records[0].DisplayText != "43" ||
        liveProbe.Count != 43)
    {
        Console.Error.WriteLine("NativeAOT facade did not preserve live payload object identity.");
        return 1;
    }

    liveProbe.Dispose();
    try
    {
        _ = liveProbe.Count;
        Console.Error.WriteLine("NativeAOT facade allowed use of a disposed live object probe.");
        return 1;
    }
    catch (ObjectDisposedException)
    {
    }
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

    using (var reverseProbe = new PowerShellSessionObjectProbe(64))
    {
        session.SetLiveObjectVariable("reverseProbe", reverseProbe);
        using PowerShell incrementProbe = session.CreatePowerShell();
        PowerShellInvocationResult incrementOutput = incrementProbe
            .AddScript("$reverseProbe.Increment()")
            .Invoke();
        if (incrementOutput.Output.Records.Count != 1 ||
            incrementOutput.Output.Records[0].DisplayText != "65" ||
            reverseProbe.Count != 65)
        {
            Console.Error.WriteLine("NativeAOT facade did not invoke the .NET session object through PowerShell.");
            return 1;
        }

        using PowerShell readProbe = session.CreatePowerShell();
        PowerShellInvocationResult readOutput = readProbe
            .AddScript("$reverseProbe.Count")
            .Invoke();
        if (readOutput.Output.Records.Count != 1 ||
            readOutput.Output.Records[0].DisplayText != "65" ||
            !session.RemoveVariable("reverseProbe"))
        {
            Console.Error.WriteLine("NativeAOT facade did not retain the .NET session object across PowerShell invocations.");
            return 1;
        }

        reverseProbe.Dispose();
        try
        {
            _ = reverseProbe.Count;
            Console.Error.WriteLine("NativeAOT facade allowed use of a disposed .NET session object probe.");
            return 1;
        }
        catch (ObjectDisposedException)
        {
        }
    }

    var genericBroker = new GenericCountBroker(96);
    using (var genericLiveObject = new PowerShellLiveObject<IPowerShellLiveObjectTestCount>(
        PowerShellLiveObjectTestContracts.Count,
        genericBroker))
    {
        session.SetLiveObjectVariable("genericProbe", genericLiveObject);
        using PowerShell incrementGenericProbe = session.CreatePowerShell();
        PowerShellInvocationResult genericIncrementOutput = incrementGenericProbe
            .AddScript("$genericProbe.Increment()")
            .Invoke();
        if (genericIncrementOutput.Output.Records.Count != 1 ||
            genericIncrementOutput.Output.Records[0].DisplayText != "97" ||
            genericBroker.GetCount(out long genericCount) != 0 ||
            genericCount != 97)
        {
            Console.Error.WriteLine("NativeAOT facade did not invoke the external contract-pack live object.");
            return 1;
        }

        using PowerShell aliasGenericProbe = session.CreatePowerShell();
        PowerShellInvocationResult aliasOutput = aliasGenericProbe
            .AddScript("$genericAlias = $genericProbe; Remove-Variable genericProbe")
            .Invoke();
        if (aliasOutput.HadErrors)
        {
            Console.Error.WriteLine("NativeAOT facade could not rebind the external contract-pack live object.");
            return 1;
        }

        using PowerShell readGenericProbe = session.CreatePowerShell();
        PowerShellInvocationResult genericReadOutput = readGenericProbe
            .AddScript("$genericAlias.Increment()")
            .Invoke();
        if (genericReadOutput.Output.Records.Count != 1 ||
            genericReadOutput.Output.Records[0].DisplayText != "98" ||
            !session.RemoveVariable("genericAlias"))
        {
            Console.Error.WriteLine("NativeAOT facade did not retain the external contract-pack live object.");
            return 1;
        }
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

using (PowerShell liveStream = PowerShell.Create())
using (PowerShellInvocationOperation operation = liveStream
    .AddScript(@"
        Write-Output 'nativeaot-live-before'
        Write-Error -Message 'nativeaot-live-error'
        Write-Progress -Activity 'nativeaot-live-progress' -Status 'running' -PercentComplete 50
        Start-Sleep -Milliseconds 300
        Write-Output 'nativeaot-live-after'
    ")
    .BeginInvoke())
{
    ulong cursor = 0;
    var records = new List<PowerShellInvocationStreamRecord>();
    bool observedOutputBeforeCompletion = false;
    bool observedErrorBeforeCompletion = false;
    bool observedProgressBeforeCompletion = false;
    for (int attempt = 0; attempt < 100; attempt++)
    {
        PowerShellInvocationStreamBatch batch = operation.ReadStreamBatch(cursor);
        records.AddRange(batch.Records);
        cursor = batch.NextSequence;
        if (!batch.IsTerminal)
        {
            observedOutputBeforeCompletion |= batch.Records.Any(record => record.Stream == PowerShellStreamKind.Output);
            observedErrorBeforeCompletion |= batch.Records.Any(record => record.Stream == PowerShellStreamKind.Error);
            observedProgressBeforeCompletion |= batch.Records.Any(record => record.Stream == PowerShellStreamKind.Progress);
        }
        if (batch.IsTerminal)
        {
            break;
        }

        await Task.Delay(25);
    }

    PowerShellInvocationOperationStatus status = operation.Wait(TimeSpan.FromSeconds(5));
    PowerShellInvocationStreamBatch terminalBatch = operation.ReadStreamBatch(cursor);
    records.AddRange(terminalBatch.Records);
    bool ordered = records.Count == 4;
    for (int index = 0; index < records.Count; index++)
    {
        ordered &= records[index].Sequence != 0 &&
            (index == 0 || records[index].Sequence > records[index - 1].Sequence);
    }
    if (status.State != PowerShellOperationState.Completed ||
        status.TerminalStatus != PowerShellFfiStatus.Success ||
        !observedOutputBeforeCompletion ||
        !observedErrorBeforeCompletion ||
        !observedProgressBeforeCompletion ||
        !ordered ||
        records[0].Stream != PowerShellStreamKind.Output ||
        records[0].DisplayText != "nativeaot-live-before" ||
        records[1].Stream != PowerShellStreamKind.Error ||
        !records[1].DisplayText.Contains("nativeaot-live-error", StringComparison.Ordinal) ||
        records[2].Stream != PowerShellStreamKind.Progress ||
        records[3].Stream != PowerShellStreamKind.Output ||
        records[3].DisplayText != "nativeaot-live-after")
    {
        Console.Error.WriteLine("NativeAOT facade did not preserve ordered copied output, error, and progress records before completion.");
        return 1;
    }
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
    .AddScript("Write-Output 'nativeaot-cancel-stream'; Start-Sleep -Seconds 30; 'unexpected double-stop completion'")
    .BeginInvoke())
{
    ulong cursor = 0;
    PowerShellInvocationStreamBatch liveBatch = null;
    for (int attempt = 0; attempt < 100; attempt++)
    {
        liveBatch = operation.ReadStreamBatch(cursor);
        cursor = liveBatch.NextSequence;
        if (liveBatch.Records.Count != 0)
        {
            break;
        }

        await Task.Delay(25);
    }
    if (liveBatch?.Records.Count != 1 ||
        liveBatch.Records[0].DisplayText != "nativeaot-cancel-stream" ||
        liveBatch.IsTerminal)
    {
        Console.Error.WriteLine("NativeAOT facade did not expose a running operation stream before cancellation.");
        return 1;
    }

    operation.Stop();
    operation.Stop();
    PowerShellInvocationOperationStatus status = operation.Wait(TimeSpan.FromSeconds(5));
    PowerShellInvocationStreamBatch cancelledBatch = operation.ReadStreamBatch(cursor);
    if (status.State != PowerShellOperationState.Cancelled ||
        status.TerminalStatus != PowerShellFfiStatus.OperationCancelled ||
        cancelledBatch.State != PowerShellOperationState.Cancelled ||
        cancelledBatch.TerminalStatus != PowerShellFfiStatus.OperationCancelled)
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
    !projection.IsPropertyBagTruncated ||
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
