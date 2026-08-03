using System;
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
        bool lateReplyWasRejected = false;
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
                        lateReplyWasRejected = !dispatch.Dispatch();
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
                !lateReplyWasRejected)
            {
                Console.Error.WriteLine("NativeAOT generated bridge accepted a late reply after cancellation.");
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
