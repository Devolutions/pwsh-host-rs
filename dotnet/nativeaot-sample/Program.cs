using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Devolutions.MultiPwsh.BridgeTest;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;
using NativeAotFfiSample;

string contractPackPath = System.IO.Path.Combine(
    AppContext.BaseDirectory,
    "Devolutions.MultiPwsh.LiveObject.TestPack.dll");
string incompatibleContractPackPath = System.IO.Path.Combine(
    AppContext.BaseDirectory,
    "Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack.dll");
string bridgeContractPackPath = System.IO.Path.Combine(
    AppContext.BaseDirectory,
    "Devolutions.MultiPwsh.BridgeContract.TestPack.dll");
if (!System.IO.File.Exists(contractPackPath))
{
    Console.Error.WriteLine("NativeAOT facade did not publish the external live-object contract pack.");
    return 1;
}
if (!System.IO.File.Exists(incompatibleContractPackPath))
{
    Console.Error.WriteLine("NativeAOT facade did not publish the incompatible live-object contract pack fixture.");
    return 1;
}
if (!System.IO.File.Exists(bridgeContractPackPath))
{
    Console.Error.WriteLine("NativeAOT facade did not publish the bridge contract test pack.");
    return 1;
}

PowerShellLiveObjectContractPack[] contractPacks =
[
    new PowerShellLiveObjectContractPack(
        contractPackPath,
        "Devolutions.MultiPwsh.LiveObject.TestPack.LiveObjectTestPack, Devolutions.MultiPwsh.LiveObject.TestPack"),
    new PowerShellLiveObjectContractPack(
        bridgeContractPackPath,
        "Devolutions.MultiPwsh.BridgeTest.BridgeContractTestPack, Devolutions.MultiPwsh.BridgeContract.TestPack"),
];

if (args.Length == 2 && args[1].StartsWith("--expect-rejected-contract-pack:", StringComparison.Ordinal))
{
    string fixtureName = args[1]["--expect-rejected-contract-pack:".Length..];
    (string TypeName, string ExpectedReason)? scenario = fixtureName switch
    {
        "duplicate-across-packs" => (
            "Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack.IncompatibleLiveObjectTestPack",
            "duplicate interface identifiers"),
        "duplicate-within-pack" => (
            "Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack.DuplicateContractLiveObjectTestPack",
            "duplicate interface identifiers"),
        "direction-violation" => (
            "Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack.DirectionViolationLiveObjectTestPack",
            "unsupported direction"),
        "reserved-identifier" => (
            "Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack.ReservedContractLiveObjectTestPack",
            "has already been registered"),
        "unsupported-pack-abi" => (
            "Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack.UnsupportedAbiLiveObjectTestPack",
            "contract pack API is invalid"),
        "bridge-marker-without-direction" => (
            "Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack.BridgeMarkerWithoutDirectionLiveObjectTestPack",
            "must be declared together with ConsumerToSession"),
        _ => null,
    };

    if (scenario is null)
    {
        Console.Error.WriteLine($"Unknown contract-pack rejection fixture '{fixtureName}'.");
        return 2;
    }

    // The duplicate-across-packs fixture only collides once the compatible pack is
    // already present in the same registration batch.
    PowerShellLiveObjectContractPack[] rejectedPacks = fixtureName == "duplicate-across-packs"
        ?
        [
            contractPacks[0],
            new PowerShellLiveObjectContractPack(
                incompatibleContractPackPath,
                $"{scenario.Value.TypeName}, Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack"),
        ]
        :
        [
            new PowerShellLiveObjectContractPack(
                incompatibleContractPackPath,
                $"{scenario.Value.TypeName}, Devolutions.MultiPwsh.LiveObject.Incompatible.TestPack"),
        ];

    try
    {
        _ = PowerShellRuntime.Activate(args[0], rejectedPacks);
    }
    catch (PowerShellFfiException exception) when (exception.Status == PowerShellFfiStatus.HostFailure)
    {
        if (!exception.Message.Contains(scenario.Value.ExpectedReason, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"Contract pack '{fixtureName}' was rejected for the wrong reason: {exception.Message}");
            return 1;
        }

        Console.WriteLine($"Rejected live-object contract pack '{fixtureName}': {scenario.Value.ExpectedReason}");
        return 0;
    }

    Console.Error.WriteLine($"Contract pack '{fixtureName}' was accepted.");
    return 1;
}

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

if (!ObservedPresentationSmoke.Run(runtime))
{
    return 1;
}

Console.WriteLine("NativeAOT observed presentation: Success");

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

    var patchHandler = new ConnectionPatchIntentHandler();
    var patchSchema = new PowerShellStagedIntentSchema(
        [
            new PowerShellStagedIntentProperty("ConnectionId", [PowerShellValueKind.String]),
            new PowerShellStagedIntentProperty("DisplayName", [PowerShellValueKind.String]),
        ],
        maximumPayloadBytes: 256);
    var patchDefinition = new PowerShellStagedIntentDefinition(
        "rdm.connection-patch",
        patchSchema,
        patchHandler,
        deadline: TimeSpan.FromSeconds(30));
    using (PowerShellStagedIntentCoordinator intents = runtime.RegisterStagedIntents([patchDefinition]))
    {
        session.SetPropertyBag(
            "connection",
            [
                new("Id", PowerShellValue.String("connection-42")),
                new("Name", PowerShellValue.String("Production")),
                new("Host", PowerShellValue.String("rdp.example.test")),
            ]);
        using PowerShell stageConnectionPatch = session.CreatePowerShell();
        PowerShellInvocationResult stagedPatchOutput = stageConnectionPatch
            .AddScript(@"
                $stage = $DpsCapabilities.Invoke('rdm.connection-patch.stage', [pscustomobject]@{
                    stageId = 'connection-42-review'
                    intent = [pscustomobject]@{
                        ConnectionId = $connection.Id
                        DisplayName = ""$($connection.Name)-reviewed""
                    }
                })
                $validation = $DpsCapabilities.Invoke('rdm.connection-patch.validate', 'connection-42-review')
                $commit = $DpsCapabilities.Invoke('rdm.connection-patch.commit', 'connection-42-review')
                ""$($stage.status)|$($validation.status)|$($commit.status)""
            ")
            .WithCapabilities(intents.Capabilities)
            .Invoke();
        using PowerShell abortConnectionPatch = session.CreatePowerShell();
        PowerShellInvocationResult abortedPatchOutput = abortConnectionPatch
            .AddScript(@"
                $stage = $DpsCapabilities.Invoke('rdm.connection-patch.stage', [pscustomobject]@{
                    stageId = 'connection-42-discard'
                    intent = [pscustomobject]@{
                        ConnectionId = $connection.Id
                        DisplayName = 'Discarded'
                    }
                })
                $abort = $DpsCapabilities.Invoke('rdm.connection-patch.abort', 'connection-42-discard')
                ""$($stage.status)|$($abort.status)""
            ")
            .WithCapabilities(intents.Capabilities)
            .Invoke();
        if (stagedPatchOutput.Output.Records.Count != 1 ||
            stagedPatchOutput.Output.Records[0].DisplayText != "staged|validated|committed" ||
            abortedPatchOutput.Output.Records.Count != 1 ||
            abortedPatchOutput.Output.Records[0].DisplayText != "staged|aborted" ||
            patchHandler.CommittedIntent is not
            {
                StageIdentifier: "connection-42-review",
                ConnectionId: "connection-42",
                DisplayName: "Production-reviewed",
            } ||
            patchHandler.AbortedStageIdentifier != "connection-42-discard" ||
            !session.RemoveVariable("connection"))
        {
            Console.Error.WriteLine("NativeAOT facade did not coordinate the bounded connection patch intent lifecycle.");
            return 1;
        }
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
            genericReadOutput.Output.Records[0].DisplayText != "98")
        {
            Console.Error.WriteLine("NativeAOT facade did not retain the external contract-pack live object.");
            return 1;
        }

        using PowerShell graphGenericProbe = session.CreatePowerShell();
        const string childName = "na\u00EFve-\u6771\u4EAC";
        const string childHost = "host-\u6771\u4EAC";
        const string childDescription = "description-\u6771\u4EAC";
        const string childGroup = "group-\u6771\u4EAC";
        PowerShellInvocationResult graphOutput;
        try
        {
            graphOutput = graphGenericProbe
                .AddScript(@"
                    $genericAlias.Primary.Value = 17
                    $genericAlias.Children[1].Value = 29
                    $child = $genericAlias.Add(""na$([char]0x00EF)ve-$([char]0x6771)$([char]0x4EAC)"")
                    $child.Host = ""host-$([char]0x6771)$([char]0x4EAC)""
                    $genericAlias.Children[2].Description = ""description-$([char]0x6771)$([char]0x4EAC)""
                    $genericAlias.Children[2].Group = ""group-$([char]0x6771)$([char]0x4EAC)""
                    $collectionChild = $genericAlias.Children[2]
                    $referenceEquals = [object]::ReferenceEquals($child, $collectionChild)
                    $equals = $child -eq $collectionChild
                    $names = @($genericAlias.Children | ForEach-Object Name) -join ','
                    ""$($genericAlias.Primary.Value)|$($genericAlias.Children.Count)|$($genericAlias.Children[0].Value)|$($genericAlias.Children[1].Value)|$($child.Name)|$($child.Host)|$($collectionChild.Description)|$($collectionChild.Group)|$referenceEquals|$equals|$names""
                ")
                .Invoke();
        }
        catch (PowerShellInvocationException exception)
        {
            Console.Error.WriteLine(
                exception.InvocationResult.Errors.Records.Count == 0
                    ? "NativeAOT facade terminated the generated-COM graph invocation without an error record."
                    : exception.InvocationResult.Errors.Records[0].Message);
            return 1;
        }

        if (graphOutput.Output.Records.Count != 1 ||
            graphOutput.Output.Records[0].DisplayText !=
                $"17|3|17|29|{childName}|{childHost}|{childDescription}|{childGroup}|True|True|primary,secondary,{childName}" ||
            genericBroker.GetPrimary(out IPowerShellLiveObjectTestChild primary) != 0 ||
            primary.GetValue(out long primaryValue) != 0 ||
            primaryValue != 17 ||
            genericBroker.GetChildren(out IPowerShellLiveObjectTestChildCollection children) != 0 ||
            children.GetAt(1, out IPowerShellLiveObjectTestChild secondary) != 0 ||
            secondary.GetValue(out long secondaryValue) != 0 ||
            secondaryValue != 29 ||
            children.GetAt(2, out IPowerShellLiveObjectTestChild addedChild) != 0 ||
            addedChild.GetName(out string addedName) != 0 ||
            addedName != childName ||
            addedChild.GetHost(out string addedHost) != 0 ||
            addedHost != childHost ||
            addedChild.GetDescription(out string addedDescription) != 0 ||
            addedDescription != childDescription ||
            addedChild.GetGroup(out string addedGroup) != 0 ||
            addedGroup != childGroup)
        {
            Console.Error.WriteLine("NativeAOT facade did not preserve SessionCreator-style generated-COM members.");
            return 1;
        }

        using PowerShell leakChild = session.CreatePowerShell();
        PowerShellInvocationResult leakOutput = leakChild
            .AddScript("$global:MultiPwshLiveObjectLeakedChild = $genericAlias.Children[2]")
            .Invoke();
        if (leakOutput.HadErrors || !session.RemoveVariable("genericAlias"))
        {
            Console.Error.WriteLine("NativeAOT facade could not remove the generated-COM root lease.");
            return 1;
        }

        bool leakedChildWasSilentlyInert = false;
        string lateAccessDescription = string.Empty;
        using (PowerShell readLeakedChild = session.CreatePowerShell())
        {
            try
            {
                PowerShellInvocationResult lateOutput = readLeakedChild
                    .AddScript(@"
                        try
                        {
                            ""value:$($global:MultiPwshLiveObjectLeakedChild.Host)""
                        }
                        catch
                        {
                            ""error:$($_.Exception.GetType().FullName):$($_.Exception.Message)""
                        }
                    ")
                    .Invoke();
                lateAccessDescription = lateOutput.Output.Records.Count == 1
                    ? lateOutput.Output.Records[0].DisplayText
                    : $"records={lateOutput.Output.Records.Count}; errors={lateOutput.Errors.Records.Count}";
                // The current root-only reconciliation disposes the child wrapper, but
                // PowerShell projects its failed getter as null instead of a tombstone error.
                leakedChildWasSilentlyInert = lateAccessDescription == "value:";
            }
            catch (PowerShellInvocationException exception)
            {
                lateAccessDescription = $"errors={exception.InvocationResult.Errors.Records.Count}";
            }
        }

        using PowerShell clearLeakedChild = session.CreatePowerShell();
        _ = clearLeakedChild
            .AddScript("Remove-Variable -Scope Global -Name MultiPwshLiveObjectLeakedChild -ErrorAction SilentlyContinue")
            .Invoke();
        if (!leakedChildWasSilentlyInert)
        {
            Console.Error.WriteLine(
                $"NativeAOT facade did not preserve the expected leaked-child teardown probe behavior: {lateAccessDescription}");
            return 1;
        }
    }

    using (var bridgeBroker = new BridgeTestCountBroker(41))
    using (var bridgeLiveObject = new PowerShellLiveObject<IPowerShellBridgeTestCountTransport>(
        new PowerShellLiveObjectContract(
            typeof(IPowerShellBridgeTestCountTransport).GUID,
            majorVersion: 1,
            minorVersion: 0,
            PowerShellLiveObjectDirection.ConsumerToSession | PowerShellLiveObjectDirection.BridgeContract),
        bridgeBroker))
    {
        // Both variables refer to one consumer COM identity. The payload must
        // create one discovery proxy and share its one invocation lease rather
        // than making the dispatcher reject a second Open.
        session.SetLiveObjectVariable("bridgeOne", bridgeLiveObject);
        session.SetLiveObjectVariable("bridgeTwo", bridgeLiveObject);

        using PowerShell firstBridgeInvocation = session.CreatePowerShell();
        PowerShellInvocationResult firstBridgeOutput = firstBridgeInvocation
            .AddScript("\"$($bridgeOne.Increment())|$($bridgeTwo.Count)\"")
            .Invoke();
        if (firstBridgeOutput.Output.Records.Count != 1 ||
            firstBridgeOutput.Output.Records[0].DisplayText != "42|42" ||
            bridgeBroker.Count != 42 ||
            bridgeBroker.OpenedLeaseCount != 1)
        {
            Console.Error.WriteLine("NativeAOT facade did not bind one shared bridge lease for two payload variables.");
            return 1;
        }

        // A fresh invocation must reopen a fresh lease after the prior unbind.
        using PowerShell secondBridgeInvocation = session.CreatePowerShell();
        PowerShellInvocationResult secondBridgeOutput = secondBridgeInvocation
            .AddScript("\"$($bridgeOne.Increment())|$($bridgeTwo.Count)\"")
            .Invoke();
        if (secondBridgeOutput.Output.Records.Count != 1 ||
            secondBridgeOutput.Output.Records[0].DisplayText != "43|43" ||
            bridgeBroker.Count != 43 ||
            bridgeBroker.OpenedLeaseCount != 2)
        {
            Console.Error.WriteLine("NativeAOT facade did not rebind the bridge contract for a later invocation.");
            return 1;
        }

        // The list prevents direct-reference reconciliation from rewriting the
        // captured root. Its generated client must therefore tombstone itself at
        // unbind instead of becoming the v1-style silently inert wrapper.
        using PowerShell captureBridgeRoot = session.CreatePowerShell();
        PowerShellInvocationResult captureBridgeOutput = captureBridgeRoot
            .AddScript("""
                $global:bridgeEscaped = [System.Collections.Generic.List[object]]::new()
                [void]$global:bridgeEscaped.Add($bridgeOne)
                $bridgeOne.Increment()
                """)
            .Invoke();
        if (captureBridgeOutput.Output.Records.Count != 1 ||
            captureBridgeOutput.Output.Records[0].DisplayText != "44" ||
            bridgeBroker.Count != 44)
        {
            Console.Error.WriteLine("NativeAOT facade could not capture the bridge root for its tombstone probe.");
            return 1;
        }

        using PowerShell readEscapedBridgeRoot = session.CreatePowerShell();
        PowerShellInvocationResult escapedBridgeOutput = readEscapedBridgeRoot
            .AddScript("""
                try
                {
                    $null = $global:bridgeEscaped[0].Increment()
                    'unexpected'
                }
                catch
                {
                    $_.Exception.Message
                }
                """)
            .Invoke();
        using PowerShell clearEscapedBridgeRoot = session.CreatePowerShell();
        _ = clearEscapedBridgeRoot
            .AddScript("Remove-Variable -Scope Global -Name bridgeEscaped -ErrorAction SilentlyContinue")
            .Invoke();
        if (escapedBridgeOutput.Output.Records.Count != 1 ||
            !escapedBridgeOutput.Output.Records[0].DisplayText.Contains("lease has been released", StringComparison.Ordinal))
        {
            string observed = escapedBridgeOutput.Output.Records.Count == 1
                ? escapedBridgeOutput.Output.Records[0].DisplayText
                : $"records={escapedBridgeOutput.Output.Records.Count}; errors={escapedBridgeOutput.Errors.Records.Count}";
            Console.Error.WriteLine($"NativeAOT facade did not tombstone an escaped bridge root after invocation unbind: {observed}");
            return 1;
        }

        if (!session.RemoveVariable("bridgeOne") ||
            !session.RemoveVariable("bridgeTwo"))
        {
            Console.Error.WriteLine("NativeAOT facade did not release the bridge contract variables.");
            return 1;
        }
    }

    using (var rawBroker = new SessionCreatorBroker())
    {
        if (!rawBroker.VerifyRawRejections())
        {
            Console.Error.WriteLine("NativeAOT facade did not reject raw broker inputs without mutation.");
            return 1;
        }
    }

    using (var broker = new SessionCreatorBroker())
    using (var brokerLiveObject = new PowerShellLiveObject<IPowerShellLiveObjectBrokerContract>(
        PowerShellLiveObjectTestContracts.SessionCreatorBroker,
        broker))
    {
        try
        {
            session.SetLiveObjectVariable("brokerRdm", brokerLiveObject);
            using PowerShell brokerScript = session.CreatePowerShell();
            PowerShellInvocationResult brokerOutput = brokerScript
                .AddScript(@"
                $child = $brokerRdm.Add(""na$([char]0x00EF)ve-$([char]0x6771)$([char]0x4EAC)"")
                $child.Host = ""host-$([char]0x6771)$([char]0x4EAC)""
                $brokerRdm.Children[0].Description = ""description""
                $brokerRdm.Children[0].Group = ""group""
                $same = [object]::ReferenceEquals($child, $brokerRdm.Children[0]) -and $child -eq $brokerRdm.Children[0]
                $names = @($brokerRdm.Children | ForEach-Object Name) -join ','
                ""$($child.Name)|$($child.Host)|$($brokerRdm.Children[0].Description)|$($brokerRdm.Children[0].Group)|$same|$names""
            ")
                .Invoke();
            if (brokerOutput.Output.Records.Count != 1 ||
                brokerOutput.Output.Records[0].DisplayText != "na\u00EFve-\u6771\u4EAC|host-\u6771\u4EAC|description|group|True|na\u00EFve-\u6771\u4EAC" ||
                brokerOutput.HadErrors)
            {
                Console.Error.WriteLine("NativeAOT facade did not preserve the single-interface SessionCreator broker contract.");
                return 1;
            }

            using PowerShell invalidBrokerInput = session.CreatePowerShell();
            PowerShellInvocationResult invalidBrokerOutput = invalidBrokerInput
                .AddScript(@"
                    $oversize = try { $brokerRdm.Add(('x' * 129)); $false } catch { $true }
                    $invalidIndex = try { $brokerRdm.Children.get_Item(1); $false } catch { $true }
                    ""$oversize|$invalidIndex""
                ")
                .Invoke();
            if (invalidBrokerOutput.Output.Records.Count != 1 ||
                invalidBrokerOutput.Output.Records[0].DisplayText != "True|True")
            {
                Console.Error.WriteLine("NativeAOT facade did not reject bounded broker inputs.");
                return 1;
            }

            using PowerShell leakBrokerChild = session.CreatePowerShell();
            _ = leakBrokerChild
                .AddScript("$global:MultiPwshBrokerLeakedChild = $brokerRdm.Children[0]")
                .Invoke();

            broker.EndLease();
            using PowerShell readBrokerLeakedChild = session.CreatePowerShell();
            PowerShellInvocationResult tombstoneOutput = readBrokerLeakedChild
                .AddScript(@"
                try { $global:MultiPwshBrokerLeakedChild.ReadHost() }
                catch {
                    if ($_.Exception.InnerException) { $_.Exception.InnerException.GetType().FullName }
                    else { $_.Exception.GetType().FullName }
                }
            ")
                .Invoke();
            using PowerShell clearBrokerLeakedChild = session.CreatePowerShell();
            _ = clearBrokerLeakedChild
                .AddScript("Remove-Variable -Scope Global -Name MultiPwshBrokerLeakedChild -ErrorAction SilentlyContinue")
                .Invoke();
            if (tombstoneOutput.Output.Records.Count != 1 ||
                tombstoneOutput.Output.Records[0].DisplayText != "System.ObjectDisposedException")
            {
                Console.Error.WriteLine("NativeAOT facade did not tombstone a leaked broker child.");
                return 1;
            }

            if (!session.RemoveVariable("brokerRdm"))
            {
                Console.Error.WriteLine("NativeAOT facade could not remove the broker root wrapper.");
                return 1;
            }
        }
        finally
        {
            broker.EndLease();
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
        Write-Error -Message 'nativeaot-live-error' -TargetObject ('x' * 5000)
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
        records[1].IsTruncated ||
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

if (!BrokerChannelSmoke.Run(runtime))
{
    return 1;
}

if (!BridgeAttachmentSmoke.Run(runtime))
{
    return 1;
}

Console.WriteLine("NativeAOT in-process PowerShell FFI: Success");
return 0;
