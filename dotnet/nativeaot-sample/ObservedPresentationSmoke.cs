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
                Write-Output 'nativeaot-presentation-result'
                Write-Information 'nativeaot-presentation-text' -InformationAction Continue
                Write-Progress -Id 7 -ParentId 3 -Activity 'nativeaot-progress' -Status 'running' `
                    -CurrentOperation 'presenting' -PercentComplete 42 -SecondsRemaining 9
            ")
            .BeginObservedInvocation(new PowerShellObservedInvocationOptions(
                maximumBufferedResultRecords: 4,
                maximumResultPageRecords: 2,
                maximumBufferedDiagnosticRecords: 4,
                maximumDiagnosticPageRecords: 2));

        PowerShellObservedTranscript transcript = invocation.CreateTranscript();
        var records = new List<PowerShellObservedPresentationRecord>();
        PowerShellValuePage resultPage = null!;
        PowerShellObservedPresentationPage presentationPage = null!;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            resultPage = transcript.ReadResults();
            if (!ReferenceEquals(resultPage, transcript.ReadResults()))
            {
                Console.Error.WriteLine("NativeAOT observed transcript did not retain an uncommitted result page.");
                return false;
            }

            presentationPage = transcript.ReadPresentation();
            if (!ReferenceEquals(presentationPage, transcript.ReadPresentation()))
            {
                Console.Error.WriteLine("NativeAOT observed transcript did not retain an uncommitted presentation page.");
                return false;
            }

            records.AddRange(presentationPage.Records);
            ulong presentationAcknowledgement = transcript.PresentationAcknowledgedThrough;
            transcript.CommitResults(resultPage);
            if (transcript.ResultAcknowledgedThrough != resultPage.NextSequence ||
                transcript.PresentationAcknowledgedThrough != presentationAcknowledgement)
            {
                Console.Error.WriteLine("NativeAOT observed transcript did not commit result and presentation cursors independently.");
                return false;
            }

            try
            {
                transcript.CommitResults(resultPage);
                Console.Error.WriteLine("NativeAOT observed transcript accepted a stale result page.");
                return false;
            }
            catch (InvalidOperationException)
            {
            }

            transcript.CommitPresentation(presentationPage);
            try
            {
                transcript.CommitPresentation(presentationPage);
                Console.Error.WriteLine("NativeAOT observed transcript accepted a stale presentation page.");
                return false;
            }
            catch (InvalidOperationException)
            {
            }

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
            resultPage.TotalRecordCount != 1 ||
            resultPage.DroppedRecordCount != 0 ||
            resultPage.IsTruncated ||
            presentationPage.DroppedRecordCount != 0 ||
            presentationPage.IsTruncated ||
            transcript.ResultAcknowledgedThrough != resultPage.NextSequence ||
            transcript.PresentationAcknowledgedThrough != presentationPage.NextSequence ||
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
