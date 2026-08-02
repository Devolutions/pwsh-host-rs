using System;
using System.Threading;
using System.Threading.Tasks;
using Devolutions.PowerShell.Ffi;

namespace NativeAotFfiSample;

/// <summary>
/// End-to-end duplex broker smoke. A real PowerShell pipeline calls
/// <c>$DpsBroker.Request</c>; a dedicated non-UI pump thread receives the frame,
/// hands it to an ordinary dispatcher, and the dispatcher replies later by
/// correlation ID. The pump never performs application work inline and never
/// calls back into PowerShell.
/// </summary>
internal static class BrokerChannelSmoke
{
    private const uint EchoKind = 1;
    private const uint EventKind = 2;

    internal static bool Run(PowerShellRuntime runtime)
    {
        using PowerShellBrokerChannel channel = runtime.CreateBrokerChannel(
            new PowerShellBrokerChannelOptions(
                maximumInflightFrames: 8,
                maximumBodyBytes: 4096,
                defaultDeadline: TimeSpan.FromSeconds(20)));

        int events = 0;
        int requests = 0;
        Exception? pumpFailure = null;
        using var stopping = new CancellationTokenSource();

        var pump = new Thread(() =>
        {
            try
            {
                while (!stopping.IsCancellationRequested &&
                    channel.TryReceive(TimeSpan.FromMilliseconds(100), out PowerShellBrokerRequest? request))
                {
                    if (request is null)
                    {
                        continue;
                    }

                    if (request.IsOneWay)
                    {
                        Interlocked.Increment(ref events);
                        continue;
                    }

                    Interlocked.Increment(ref requests);

                    // Dispatch-only: hand the copied request to an ordinary
                    // worker and immediately return to waiting. The reply
                    // happens later, from a different thread.
                    PowerShellBrokerRequest captured = request;
                    _ = Task.Run(() =>
                    {
                        byte[] reply = new byte[captured.Body.Length];
                        for (int index = 0; index < captured.Body.Length; index++)
                        {
                            reply[index] = (byte)(captured.Body[index] + 1);
                        }

                        channel.TryReply(captured.CorrelationId, reply);
                    });
                }
            }
            catch (Exception exception)
            {
                pumpFailure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "broker-pump",
        };
        pump.Start();

        try
        {
            using PowerShell synchronous = PowerShell.Create();
            synchronous.AddScript("'unused'").WithBroker(channel);
            try
            {
                _ = synchronous.Invoke();
                Console.Error.WriteLine("NativeAOT facade allowed a synchronous invocation with a broker attached.");
                return false;
            }
            catch (PowerShellFfiException exception)
                when (exception.Status == PowerShellFfiStatus.UnsupportedCapability)
            {
                // Expected: a broker requires asynchronous invocation.
            }

            using PowerShell command = PowerShell.Create();
            PowerShellInvocationResult result = command
                .AddScript(
                    "$DpsBroker.Post(2, [byte[]]@(9));" +
                    "$reply = $DpsBroker.Request(1, [byte[]]@(1,2,3));" +
                    "[string]::Join('-', $reply)")
                .WithBroker(channel)
                .InvokeAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (pumpFailure is not null)
            {
                Console.Error.WriteLine($"NativeAOT broker pump failed: {pumpFailure.Message}");
                return false;
            }

            if (result.Output.Records.Count != 1 || result.Output.Records[0].DisplayText != "2-3-4")
            {
                Console.Error.WriteLine("NativeAOT broker request did not receive the dispatched reply.");
                return false;
            }

            if (Volatile.Read(ref requests) != 1)
            {
                Console.Error.WriteLine("NativeAOT broker pump did not observe exactly one request.");
                return false;
            }

            if (Volatile.Read(ref events) != 1)
            {
                Console.Error.WriteLine("NativeAOT broker pump did not observe the one-way event.");
                return false;
            }
        }
        finally
        {
            stopping.Cancel();
            channel.Dispose();
            pump.Join(TimeSpan.FromSeconds(5));
        }

        Console.WriteLine("NativeAOT duplex broker channel: Success");
        return true;
    }
}
