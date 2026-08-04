using System;
using System.Threading;
using System.Threading.Tasks;
using Devolutions.MultiPwsh.FiniteOperationTest;
using Devolutions.PowerShell.Ffi;

namespace NativeAotFfiSample;

/// <summary>
/// Proves the fixed finite-operation contract across the real DBC payload
/// boundary. The payload only invokes generated, closed contract members.
/// </summary>
internal static class FiniteOperationAttachmentSmoke
{
    internal static bool Run(PowerShellRuntime runtime)
    {
        using var channel = runtime.CreateBridgeChannel(
            new PowerShellBrokerChannelOptions(
                maximumInflightFrames: 8,
                maximumBodyBytes: 4096,
                defaultDeadline: TimeSpan.FromSeconds(20)));
        using var host = new FiniteOperationTestHost();
        using PowerShellBridgeBinding binding = channel.CreateBinding(host.Dispatcher);
        using var stopping = new CancellationTokenSource();
        using var ready = new ManualResetEventSlim();
        Exception pumpFailure = null;
        int dispatched = 0;

        var pump = new Thread(() =>
        {
            try
            {
                if (!channel.TryReceive(TimeSpan.Zero, out _))
                {
                    throw new InvalidOperationException("The finite-operation bridge channel closed before the pump attached.");
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
                        if (dispatch.Dispatch())
                        {
                            Interlocked.Increment(ref dispatched);
                        }
                    });
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
            Name = "finite-operation-bridge-pump",
        };
        pump.Start();

        try
        {
            if (!ready.Wait(TimeSpan.FromSeconds(5)) || pumpFailure is not null)
            {
                Console.Error.WriteLine(
                    $"NativeAOT finite-operation bridge pump failed: {pumpFailure?.Message ?? "did not attach"}");
                return false;
            }

            PowerShellInvocationResult completed = Invoke(
                runtime,
                binding,
                @"
                    $ticket = $RDM.Start(1)
                    $first = $RDM.ReadPage($ticket.OperationId, 0)
                    $copy = $RDM.ReadPage($ticket.OperationId, 0)
                    ""$($ticket.Status)|$($first.Status)|$($first.Rows[0])|$($first.NextCursor)|$($copy.Rows[0])""
                ");
            if (!IsSingleOutput(completed, "5|5|alpha|1|alpha"))
            {
                Console.Error.WriteLine("NativeAOT finite-operation bridge did not return detached fixed-schema pages.");
                return false;
            }

            Guid invalidatedOperation = Start(runtime, binding, 1);
            if (invalidatedOperation == Guid.Empty)
            {
                Console.Error.WriteLine("NativeAOT finite-operation bridge could not start the snapshot test operation.");
                return false;
            }

            PowerShellInvocationResult firstPage = Invoke(
                runtime,
                binding,
                $"$page = $RDM.ReadPage([guid]'{invalidatedOperation:D}', 0); \"$($page.Status)|$($page.Rows[0])\"");
            if (!IsSingleOutput(firstPage, "5|alpha"))
            {
                Console.Error.WriteLine("NativeAOT finite-operation bridge could not read its initial copied page.");
                return false;
            }

            host.InvalidateSnapshot();
            PowerShellInvocationResult invalidated = Invoke(
                runtime,
                binding,
                $"$page = $RDM.ReadPage([guid]'{invalidatedOperation:D}', 1); \"$($page.Status)|$($page.HasPage)\"");
            if (!IsSingleOutput(invalidated, "10|False"))
            {
                Console.Error.WriteLine("NativeAOT finite-operation bridge did not terminally invalidate a later page read.");
                return false;
            }

            PowerShellInvocationResult cancelled = Invoke(
                runtime,
                binding,
                @"
                    $ticket = $RDM.Start(2)
                    $first = $RDM.Cancel($ticket.OperationId)
                    $second = $RDM.Cancel($ticket.OperationId)
                    $released = $RDM.Release($ticket.OperationId)
                    ""$($first.Status)|$($second.Status)|$released""
                ");
            if (!IsSingleOutput(cancelled, "7|7|14"))
            {
                Console.Error.WriteLine("NativeAOT finite-operation bridge did not preserve idempotent cancellation and release.");
                return false;
            }

            Guid expiringOperation = Start(runtime, binding, 3);
            if (expiringOperation == Guid.Empty)
            {
                Console.Error.WriteLine("NativeAOT finite-operation bridge could not start the retention test operation.");
                return false;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(1200));
            PowerShellInvocationResult expired = Invoke(
                runtime,
                binding,
                $"$page = $RDM.ReadPage([guid]'{expiringOperation:D}', 0); \"$($page.Status)|$($page.HasPage)\"");
            if (!IsSingleOutput(expired, "9|False") ||
                pumpFailure is not null ||
                Volatile.Read(ref dispatched) < 12)
            {
                Console.Error.WriteLine("NativeAOT finite-operation bridge did not retain and expire a terminal operation deterministically.");
                return false;
            }
        }
        finally
        {
            stopping.Cancel();
            channel.Dispose();
            pump.Join(TimeSpan.FromSeconds(5));
        }

        Console.WriteLine("NativeAOT finite-operation bridge attachment: Success");
        return true;
    }

    private static Guid Start(PowerShellRuntime runtime, PowerShellBridgeBinding binding, int mode)
    {
        PowerShellInvocationResult started = Invoke(
            runtime,
            binding,
            $"$ticket = $RDM.Start({mode}); \"$($ticket.Status)|$($ticket.OperationId)\"");
        if (started.Output.Records.Count != 1 ||
            started.Output.Records[0].DisplayText is not string text)
        {
            Console.Error.WriteLine(
                $"NativeAOT finite-operation start returned {started.Output.Records.Count} output record(s) and {started.Errors.Records.Count} error record(s).");
            return Guid.Empty;
        }

        string[] parts = text.Split('|');
        if (parts.Length != 2 || parts[0] != "5" || !Guid.TryParse(parts[1], out Guid operationId))
        {
            Console.Error.WriteLine($"NativeAOT finite-operation start returned '{text}'.");
            return Guid.Empty;
        }

        return
            operationId;
    }

    private static PowerShellInvocationResult Invoke(
        PowerShellRuntime runtime,
        PowerShellBridgeBinding binding,
        string script)
    {
        using PowerShell command = runtime.Create();
        return command
            .AddScript(script)
            .WithBridge(binding, "RDM")
            .InvokeAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static bool IsSingleOutput(PowerShellInvocationResult result, string expected) =>
        !result.HadErrors &&
        result.Output.Records.Count == 1 &&
        result.Output.Records[0].DisplayText == expected;
}
