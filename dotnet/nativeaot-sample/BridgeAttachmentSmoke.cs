using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Devolutions.MultiPwsh.BridgeTest;
using Devolutions.PowerShell.Ffi;

namespace NativeAotFfiSample;

/// <summary>
/// Proves the generated bridge attachment is pull-pumped over DBC and dispatched
/// from a worker rather than from the PowerShell pipeline or broker pump thread.
/// </summary>
internal static class BridgeAttachmentSmoke
{
    internal static bool Run(PowerShellRuntime runtime)
    {
        using var channel = runtime.CreateBridgeChannel(
            new PowerShellBrokerChannelOptions(
                maximumInflightFrames: 8,
                maximumBodyBytes: 4096,
                defaultDeadline: TimeSpan.FromSeconds(20)));
        using var host = new BridgeTestCountHost(41);
        using PowerShellBridgeBinding binding = channel.CreateBinding(host.Dispatcher);
        using var stopping = new CancellationTokenSource();
        using var ready = new ManualResetEventSlim();
        Exception pumpFailure = null;
        int dispatched = 0;
        int events = 0;

        var pump = new Thread(() =>
        {
            try
            {
                if (!channel.TryReceive(TimeSpan.Zero, out _))
                {
                    throw new InvalidOperationException("The generated bridge channel closed before the pump attached.");
                }

                ready.Set();
                while (!stopping.IsCancellationRequested &&
                       channel.TryReceive(TimeSpan.FromMilliseconds(100), out PowerShellBridgeDispatch dispatch))
                {
                    if (dispatch is null)
                    {
                        continue;
                    }

                    _ = Task.Run(() =>
                    {
                        bool replied = dispatch.Dispatch();
                        if (replied)
                        {
                            Interlocked.Increment(ref dispatched);
                        }
                    });
                    Interlocked.Increment(ref events);
                }
            }
            catch (Exception exception)
            {
                pumpFailure = exception;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "generated-bridge-pump",
        };
        pump.Start();

        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(5)) || pumpFailure is not null)
            {
                Console.Error.WriteLine($"NativeAOT generated bridge pump failed: {pumpFailure?.Message ?? "did not attach"}");
                return false;
            }

            using (PowerShell synchronous = runtime.Create())
            {
                synchronous.AddScript("'unused'").WithBridge(binding, "RDM");
                try
                {
                    _ = synchronous.Invoke();
                    Console.Error.WriteLine("NativeAOT facade allowed a synchronous invocation with a generated bridge attached.");
                    return false;
                }
                catch (PowerShellFfiException exception)
                    when (exception.Status == PowerShellFfiStatus.UnsupportedCapability)
                {
                }
            }

            using PowerShell command = runtime.Create();
            PowerShellInvocationResult output = command
                .AddScript("$RDM.Report(77); \"$($RDM.Count)|$($RDM.Add(5))\"")
                .WithBridge(binding, "RDM")
                .InvokeAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (pumpFailure is not null ||
                output.Output.Records.Count != 1 ||
                output.Output.Records[0].DisplayText != "41|46" ||
                !SpinWait.SpinUntil(() => host.LastReportedCount == 77, TimeSpan.FromSeconds(5)) ||
                Volatile.Read(ref dispatched) < 2 ||
                Volatile.Read(ref events) < 3)
            {
                Console.Error.WriteLine("NativeAOT generated bridge attachment did not complete its bounded request and event dispatch.");
                return false;
            }

            using PowerShell finiteJob = runtime.Create();
            PowerShellInvocationResult jobOutput = finiteJob
                .AddScript("$job = $RDM.StartJob(); $before = $job.Status; $page = $job.ReadResults(0); $job.Cancel(); \"$($before.State)|$($page.Columns[2].Name)|$($page.Rows[0].Label)|$(@($page.Rows).Count)|$($job.Status.IsTerminal)\"")
                .WithBridge(binding, "RDM")
                .InvokeAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (pumpFailure is not null ||
                jobOutput.Output.Records.Count != 1 ||
                jobOutput.Output.Records[0].DisplayText != "Running|Label|result-10|2|True")
            {
                Console.Error.WriteLine("NativeAOT generated bridge job did not preserve its fixed-schema typed page, status, and cancellation.");
                return false;
            }

            using PowerShell reliableEvent = runtime.Create();
            Task<PowerShellInvocationResult> reliableEventInvocation = reliableEvent
                .AddScript("$RDM.ReportReliable(88); Start-Sleep -Milliseconds 500")
                .WithBridge(binding, "RDM")
                .InvokeAsync(CancellationToken.None);
            PowerShellBridgeReliableEventStream? stream = null;
            if (!SpinWait.SpinUntil(
                    () => (stream = channel.GetReliableEventStreams(binding)
                        .SingleOrDefault(candidate => candidate.Identity.MemberId == 6U)) is not null,
                    TimeSpan.FromSeconds(5)) ||
                stream is null)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not retain a reliable event stream.");
                return false;
            }

            PowerShellBridgeReliableEventBatch firstReliableBatch = stream.Read(0, 2);
            if (firstReliableBatch.Events.Count != 1 ||
                firstReliableBatch.Events[0].Sequence != 1 ||
                firstReliableBatch.Info.IsTerminal)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not assign a readable reliable-event cursor.");
                return false;
            }

            Task.Run(firstReliableBatch.Events[0].Dispatch).GetAwaiter().GetResult();
            stream.Acknowledge(firstReliableBatch.Events[0].Sequence);
            if (host.LastReliableReportedCount != 88 || host.ReliableReportCount != 1)
            {
                Console.Error.WriteLine("NativeAOT generated bridge dispatched a reliable event on an unexpected path.");
                return false;
            }

            _ = reliableEventInvocation.GetAwaiter().GetResult();
            if (stream.GetInfo().TerminalState != PowerShellBridgeReliableEventTerminalState.LeaseClosed)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not close the completed reliable-event lease.");
                return false;
            }

            using PowerShell reliableOverflow = runtime.Create();
            Task<PowerShellInvocationResult> reliableOverflowInvocation = reliableOverflow
                .AddScript("$RDM.ReportReliable(1); $RDM.ReportReliable(2); $RDM.ReportReliable(3); Start-Sleep -Milliseconds 500")
                .WithBridge(binding, "RDM")
                .InvokeAsync(CancellationToken.None);
            PowerShellBridgeReliableEventStream? overflowStream = null;
            if (!SpinWait.SpinUntil(
                    () =>
                    {
                        overflowStream = channel.GetReliableEventStreams(binding)
                            .SingleOrDefault(candidate =>
                                candidate.Identity.MemberId == 6U &&
                                candidate.Identity.LeaseId != stream.Identity.LeaseId);
                        return overflowStream?.GetInfo().TerminalState == PowerShellBridgeReliableEventTerminalState.RetentionOverflow;
                    },
                    TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not terminalize an over-retained reliable stream.");
                return false;
            }

            PowerShellBridgeReliableEventBatch overflowBatch = overflowStream!.Read(0, 2);
            if (overflowBatch.Events.Count != 2 ||
                overflowBatch.Events[0].Sequence != 1 ||
                overflowBatch.Events[1].Sequence != 2 ||
                overflowBatch.Info.DroppedEventCount < 1 ||
                overflowBatch.Info.TerminalState != PowerShellBridgeReliableEventTerminalState.RetentionOverflow)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not account for reliable-event retention overflow.");
                return false;
            }

            foreach (PowerShellBridgeReliableEvent @event in overflowBatch.Events)
            {
                Task.Run(@event.Dispatch).GetAwaiter().GetResult();
            }

            overflowStream.Acknowledge(overflowBatch.Events[^1].Sequence);
            if (host.ReliableReportCount != 3)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not dispatch every retained reliable event.");
                return false;
            }

            _ = reliableOverflowInvocation.GetAwaiter().GetResult();
            using var reliableCancellation = new CancellationTokenSource();
            using PowerShell reliableCancelled = runtime.Create();
            Task<PowerShellInvocationResult> reliableCancelledInvocation = reliableCancelled
                .AddScript("$RDM.ReportReliable(55); Start-Sleep -Seconds 20")
                .WithBridge(binding, "RDM")
                .InvokeAsync(reliableCancellation.Token);
            PowerShellBridgeReliableEventStream? cancelledStream = null;
            if (!SpinWait.SpinUntil(
                    () =>
                    {
                        cancelledStream = channel.GetReliableEventStreams(binding)
                            .SingleOrDefault(candidate =>
                                candidate.Identity.MemberId == 6U &&
                                candidate.Identity.LeaseId != stream.Identity.LeaseId &&
                                candidate.Identity.LeaseId != overflowStream.Identity.LeaseId);
                        return cancelledStream is not null;
                    },
                    TimeSpan.FromSeconds(5)) ||
                cancelledStream is null)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not retain the cancellable reliable event.");
                return false;
            }

            PowerShellBridgeReliableEventBatch cancelledBatch = cancelledStream.Read(0, 1);
            if (cancelledBatch.Events.Count != 1 || cancelledBatch.Events[0].Sequence != 1)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not expose the cancellable reliable event before cleanup.");
                return false;
            }

            reliableCancellation.Cancel();
            try
            {
                _ = reliableCancelledInvocation.GetAwaiter().GetResult();
                Console.Error.WriteLine("NativeAOT generated bridge completed a reliable-event invocation after cancellation.");
                return false;
            }
            catch (OperationCanceledException)
            {
            }

            if (!SpinWait.SpinUntil(
                    () => cancelledStream.GetInfo().TerminalState == PowerShellBridgeReliableEventTerminalState.LeaseClosed,
                    TimeSpan.FromSeconds(5)) ||
                cancelledStream.GetInfo().DroppedEventCount != 1)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not terminalize retained reliable events on cancellation.");
                return false;
            }

            Task.Run(cancelledBatch.Events[0].Dispatch).GetAwaiter().GetResult();
            if (host.ReliableReportCount != 3)
            {
                Console.Error.WriteLine("NativeAOT generated bridge dispatched a reliable event after cancellation released it.");
                return false;
            }

            if (!VerifyReliableStreamSlotReuse(runtime, channel, binding))
            {
                return false;
            }
        }
        finally
        {
            stopping.Cancel();
            channel.Dispose();
            pump.Join(TimeSpan.FromSeconds(5));
        }

        if (!VerifyCancellationAndLateReply(runtime))
        {
            return false;
        }

        Console.WriteLine("NativeAOT generated bridge attachment: Success");
        return true;
    }

    private static bool VerifyReliableStreamSlotReuse(
        PowerShellRuntime runtime,
        PowerShellBridgeChannel channel,
        PowerShellBridgeBinding binding)
    {
        ulong previousLeaseId = 0;
        for (int value = 100; value < 133; value++)
        {
            using PowerShell command = runtime.Create();
            Task<PowerShellInvocationResult> invocation = command
                .AddScript($"$RDM.ReportReliable({value}); Start-Sleep -Milliseconds 100")
                .WithBridge(binding, "RDM")
                .InvokeAsync(CancellationToken.None);

            PowerShellBridgeReliableEventStream? stream = null;
            if (!SpinWait.SpinUntil(
                    () =>
                    {
                        stream = channel.GetReliableEventStreams(binding)
                            .SingleOrDefault(candidate =>
                                candidate.Identity.MemberId == 6U &&
                                candidate.Identity.LeaseId != previousLeaseId);
                        return stream is not null;
                    },
                    TimeSpan.FromSeconds(5)) ||
                stream is null)
            {
                Console.Error.WriteLine("NativeAOT generated bridge did not reuse a reliable-event stream slot after lease close.");
                return false;
            }

            previousLeaseId = stream.Identity.LeaseId;
            _ = invocation.GetAwaiter().GetResult();
            if (stream.GetInfo().TerminalState != PowerShellBridgeReliableEventTerminalState.LeaseClosed ||
                channel.GetReliableEventStreams(binding).Any(candidate =>
                    candidate.Identity.LeaseId == previousLeaseId))
            {
                Console.Error.WriteLine("NativeAOT generated bridge retained a terminal reliable-event stream slot.");
                return false;
            }
        }

        return true;
    }

    private static bool VerifyCancellationAndLateReply(PowerShellRuntime runtime)
    {
        using var channel = runtime.CreateBridgeChannel(
            new PowerShellBrokerChannelOptions(
                maximumInflightFrames: 8,
                maximumBodyBytes: 4096,
                defaultDeadline: TimeSpan.FromMilliseconds(200)));
        using var host = new BridgeTestCountHost(41);
        using PowerShellBridgeBinding binding = channel.CreateBinding(host.Dispatcher);
        using var stopping = new CancellationTokenSource();
        using var ready = new ManualResetEventSlim();
        using var delayedRequest = new ManualResetEventSlim();
        using var releaseDelayedRequest = new ManualResetEventSlim();
        using var delayedDispatchComplete = new ManualResetEventSlim();
        Exception pumpFailure = null;
        PowerShellBridgeDispatch delayedDispatch = null;
        PowerShellBridgeDispatchResult lateDispatchResult = default;
        int requestCount = 0;

        var pump = new Thread(() =>
        {
            try
            {
                if (!channel.TryReceive(TimeSpan.Zero, out _))
                {
                    throw new InvalidOperationException("The generated bridge channel closed before the cancellation pump attached.");
                }

                ready.Set();
                while (!stopping.IsCancellationRequested &&
                    channel.TryReceive(TimeSpan.FromMilliseconds(100), out PowerShellBridgeDispatch dispatch))
                {
                    if (dispatch is null)
                    {
                        continue;
                    }

                    if (Interlocked.Increment(ref requestCount) == 2)
                    {
                        delayedDispatch = dispatch;
                        delayedRequest.Set();
                        releaseDelayedRequest.Wait();
                        lateDispatchResult = dispatch.DispatchDetailed();
                        delayedDispatchComplete.Set();
                        continue;
                    }

                    _ = Task.Run(dispatch.Dispatch);
                }
            }
            catch (Exception exception)
            {
                pumpFailure = exception;
                ready.Set();
                delayedDispatchComplete.Set();
            }
        })
        {
            IsBackground = true,
            Name = "generated-bridge-cancellation-pump",
        };
        pump.Start();

        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(5)) || pumpFailure is not null)
            {
                Console.Error.WriteLine($"NativeAOT generated bridge cancellation pump failed: {pumpFailure?.Message ?? "did not attach"}");
                return false;
            }

            using var cancellation = new CancellationTokenSource();
            using PowerShell command = runtime.Create();
            Task<PowerShellInvocationResult> invocation = command
                .AddScript("$RDM.Count")
                .WithBridge(binding, "RDM")
                .InvokeAsync(cancellation.Token);

            if (!delayedRequest.Wait(TimeSpan.FromSeconds(5)) || delayedDispatch is null)
            {
                Console.Error.WriteLine("NativeAOT generated bridge cancellation did not capture a request for deferred reply.");
                return false;
            }

            cancellation.Cancel();
            if (Task.WhenAny(invocation, Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult() != invocation)
            {
                Console.Error.WriteLine("NativeAOT generated bridge cancellation did not terminate its invocation.");
                return false;
            }

            try
            {
                _ = invocation.GetAwaiter().GetResult();
                Console.Error.WriteLine("NativeAOT generated bridge invocation completed after cancellation.");
                return false;
            }
            catch (OperationCanceledException)
            {
            }

            releaseDelayedRequest.Set();
            if (!delayedDispatchComplete.Wait(TimeSpan.FromSeconds(5)) ||
                pumpFailure is not null ||
                lateDispatchResult.HandlerStarted ||
                lateDispatchResult.ReplyAccepted ||
                lateDispatchResult.TerminalState?.State != PowerShellBrokerTerminalState.Cancelled)
            {
                Console.Error.WriteLine("NativeAOT generated bridge dispatched application work after cancellation.");
                return false;
            }
        }
        finally
        {
            releaseDelayedRequest.Set();
            stopping.Cancel();
            channel.Dispose();
            pump.Join(TimeSpan.FromSeconds(5));
        }

        return true;
    }
}
