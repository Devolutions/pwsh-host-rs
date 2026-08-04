using System;
using System.Collections.Generic;
using System.Threading;
using Devolutions.PowerShell.Ffi;

namespace NativeAotFfiSample;

internal static class ObservedPresentationSmoke
{
    internal static bool Run(PowerShellRuntime runtime)
    {
        using PowerShell builder = runtime.Create();
        using PowerShellObservedInvocation invocation = builder
            .AddScript(@"
                Write-Information 'nativeaot-presentation-text' -InformationAction Continue
                Write-Progress -Id 7 -ParentId 3 -Activity 'nativeaot-progress' -Status 'running' `
                    -CurrentOperation 'presenting' -PercentComplete 42 -SecondsRemaining 9
            ")
            .BeginObservedInvocation(new PowerShellObservedInvocationOptions(
                maximumBufferedResultRecords: 4,
                maximumResultPageRecords: 2,
                maximumBufferedDiagnosticRecords: 4,
                maximumDiagnosticPageRecords: 2));

        ulong resultAcknowledgement = 0;
        ulong presentationAcknowledgement = 0;
        var records = new List<PowerShellObservedPresentationRecord>();
        PowerShellValuePage resultPage = null!;
        PowerShellObservedPresentationPage presentationPage = null!;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            resultPage = invocation.ReadResults(resultAcknowledgement, maximumRecords: 2);
            resultAcknowledgement = resultPage.NextSequence;
            presentationPage = invocation.ReadPresentation(presentationAcknowledgement, maximumRecords: 2);
            presentationAcknowledgement = presentationPage.NextSequence;
            records.AddRange(presentationPage.Records);
            if (resultPage.IsComplete && presentationPage.IsComplete)
            {
                break;
            }

            Thread.Sleep(10);
        }

        PowerShellObservedPresentationRecord progress = records.Find(
            static record => record.Stream == PowerShellStreamKind.Progress);
        if (resultPage is null ||
            presentationPage is null ||
            !resultPage.IsComplete ||
            !presentationPage.IsComplete ||
            !records.Exists(static record =>
                record.Stream == PowerShellStreamKind.Information &&
                record.Text.Contains("nativeaot-presentation-text", StringComparison.Ordinal)) ||
            progress?.Progress is not
            {
                ActivityId: 7,
                ParentActivityId: 3,
                Activity: "nativeaot-progress",
                StatusDescription: "running",
                CurrentOperation: "presenting",
                PercentComplete: 42,
                SecondsRemaining: 9,
                IsCompleted: false,
            })
        {
            Console.Error.WriteLine("NativeAOT observed presentation did not preserve bounded text and typed progress.");
            return false;
        }

        return true;
    }
}
