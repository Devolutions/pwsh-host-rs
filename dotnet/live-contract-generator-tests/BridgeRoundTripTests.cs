using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Devolutions.PowerShell.Ffi.LiveObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

/// <summary>
/// Drives a real payload wrapper through a real generated dispatcher.
/// </summary>
/// <remarks>
/// The Host and Payload halves are compiled and emitted as separate assemblies,
/// exactly as they ship, and are wired only by the shared transport interface.
/// Nothing here reaches inside the generated code: the payload side is exercised
/// through its ordinary public properties and methods, and the consumer side
/// through the same COM-shaped entry point a hand-written wrapper forwards to.
/// </remarks>
internal static class BridgeRoundTripTests
{
    private const string HostExtras = """

        public sealed class SampleChildHandler : ISampleChildBridgeHandler
        {
            public string Name = "child";
            public bool Released;

            public string GetName(in SampleRootCallContext context) => Name;
            public void SetName(in SampleRootCallContext context, string value) => Name = value;
            public long? GetSize(in SampleRootCallContext context) => 4096L;
            public void Release(in SampleRootCallContext context) => Released = true;
        }

        public sealed class SampleRootHandler : ISampleRootBridgeHandler
        {
            public readonly SampleChildHandler Child = new();
            public int Progress = -1;
            public bool BreakIndexer;

            public string GetProductVersion(in SampleRootCallContext context) => "1.2.3";
            public System.Collections.Generic.IReadOnlyList<string> GetTags(in SampleRootCallContext context) => new[] { "alpha", "beta" };
            public ISampleChildBridgeHandler? FindChild(in SampleRootCallContext context, string name) => name == "missing" ? null : Child;
            public ISampleChildBridgeHandler GetAt(in SampleRootCallContext context, int index) => BreakIndexer ? null! : Child;
            public SampleState GetState(in SampleRootCallContext context) => SampleState.Open;
            public SampleFailureValue? GetLastFailure(in SampleRootCallContext context) => new SampleFailureValue("denied", 7);
            public void OnReportProgress(in SampleRootCallContext context, int percent) => Progress = percent;
            public void Release(in SampleRootCallContext context) { }
        }

        public sealed class SampleAuthorizer : ISampleRootAuthorizer
        {
            public bool DenySetters;
            public bool DenyEverything;
            public int Calls;

            public bool IsAuthorized(in SampleRootCallContext context)
            {
                Calls++;
                if (DenyEverything) { return false; }
                return !(DenySetters && context.Kind == global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.Setter);
            }
        }

        public static class HostEntry
        {            public static SampleRootHandler Handler = new();
            public static SampleAuthorizer Authorizer = new();

            public static object Create()
            {
                Handler = new SampleRootHandler();
                Authorizer = new SampleAuthorizer();
                return new SampleRootDispatcher(Handler, Authorizer);
            }

            public static object CurrentAuthorizer() => Authorizer;
            public static object CurrentHandler() => Handler;
        }

        public static class HostProbe
        {
            // Reaches the post-allocation encode-failure path through the public
            // Dispatch surface: the capacity preflight passes, then the reply
            // buffer is too small to hold the lease value.
            public static int OpenWithUndersizedReply(object dispatcher)
            {
                byte[] request = new byte[
                    global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeWire.RequestHeaderSize +
                    global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeWire.ValueHeaderSize + 32];
                var writer = new global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeValueWriter(
                    request.AsSpan(global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeWire.RequestHeaderSize));
                if (!writer.TryWriteBytes(SampleRootContract.DescriptorHash, 32))
                {
                    return -1;
                }

                var header = new global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeRequestHeader(
                    global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeFrameKind.Open,
                    1, 0U, 0UL, 0UL, 0U, writer.Length);
                if (!header.TryWrite(request))
                {
                    return -1;
                }

                return ((SampleRootDispatcher)dispatcher).Dispatch(
                    0UL, 0U, 0UL, 0U, 4096, request, new byte[16], out _);
            }
        }
        """;

    internal static void Run(Func<string, string, string, (Compilation Output, IEnumerable<Diagnostic> Diagnostics)> compile)
    {
        Assembly host = Emit(compile, BridgeContractTests.Valid + HostExtras, "Host", "BridgeRoundTripHost");
        Assembly payload = Emit(compile, BridgeContractTests.Valid, "Payload", "BridgeRoundTripPayload");

        Type entry = host.GetType("Fixture.HostEntry")!;
        Type probe = host.GetType("Fixture.HostProbe")!;
        Type bridge = payload.GetType("Fixture.SampleRootBridge")!;

        ReadsWriteAndReleaseRoundTrip(entry, bridge);
        ARetainedWrapperFailsAfterLeaseClose(entry, bridge);
        AStaleGenerationNeverReachesAHandler(entry, bridge);
        AForgedHandleNeverReachesAHandler(entry, bridge);
        AReadPermissionCannotAuthorizeASetter(entry, bridge);
        AnUnknownOrdinalIsRejectedWithoutMutation(entry, bridge);
        AMismatchedDescriptorHashIsRejected(entry, bridge);
        AClosedLeaseCanBeReopenedWithAFreshIdentity(entry, bridge);
        AFailedOpenReplyRollsBackItsLease(entry, probe, bridge);
        ADroppedDispatcherDoesNotLeakItsLeaseSlot(entry, bridge);
        ANullHandlerFromANonNullableMemberFailsClosed(entry, bridge);
        RepeatedCreateInvokeReleaseCyclesAreStable(entry, bridge);
    }

    /// <summary>
    /// A non-nullable handle result is an annotation, not a runtime guarantee: the
    /// application is ordinary code and can return null from a member the contract
    /// declares as never null. The generated writer must reject that as a malformed
    /// frame, because the alternative is an exception thrown from inside the
    /// dispatcher and out through whatever the application wrapped it in.
    /// </summary>
    private static void ANullHandlerFromANonNullableMemberFailsClosed(Type entry, Type bridge)
    {
        using var session = new Session(entry, bridge);
        Require(session.Call("GetAt", 0) is not null, "the indexer round-trips before the handler is broken");

        HostSet(entry, "CurrentHandler", "BreakIndexer", true);
        try
        {
            Unwrap(() => session.Call("GetAt", 0));
            throw new InvalidOperationException("Bridge round trip failed: a null handler was accepted.");
        }
        catch (PowerShellBridgeException exception)
        {
            // Not a revoked status: the lease is healthy and the frame is the
            // thing that is wrong, so it must read as malformed rather than as a
            // lease that has ended.
            Require(
                exception.Status == PowerShellBridgeStatus.InvalidArgument,
                $"a null handler reports InvalidArgument (saw {exception.Status:X8})");
        }
        finally
        {
            HostSet(entry, "CurrentHandler", "BreakIndexer", false);
        }

        // The lease survives a member that failed this way, so the failure is
        // contained rather than poisoning everything reached afterwards.
        Require((string)session.Get("ProductVersion")! == "1.2.3", "the lease still works after a broken member");
        Require(session.Call("GetAt", 0) is not null, "the same member works again once the application is fixed");
    }

    /// <summary>
    /// The process-wide lease budget is a static counter incremented on open and
    /// decremented only by an explicit close. A dispatcher dropped without
    /// disposal would therefore consume a slot forever, and once enough leaked,
    /// no bridge in the process could ever open a lease again.
    /// </summary>
    private static void ADroppedDispatcherDoesNotLeakItsLeaseSlot(Type entry, Type bridge)
    {
        MethodInfo open = bridge.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!;

        // Two batches, each below the live bound, with a collection between. The
        // second batch only fits if the first batch's slots were reclaimed, which
        // is the whole point: 16 *live* leases is the intended bound, 16
        // *abandoned* ones must not be permanent.
        for (int batch = 0; batch < 2; batch++)
        {
            for (int index = 0; index < 12; index++)
            {
                AbandonOneLease(entry, open);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        using var session = new Session(entry, bridge);
        Require(
            (string)session.Get("ProductVersion")! == "1.2.3",
            "a dispatcher dropped without disposal must not permanently consume its process-wide lease slot");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonOneLease(Type entry, MethodInfo open)
    {
        var transport = new Transport(entry);
        _ = open.Invoke(null, [transport]);
    }

    /// <summary>
    /// A lease allocated by an open whose reply never reaches the payload would
    /// be unreachable forever, and the one-lease rule would then reject every
    /// later open — permanently bricking the dispatcher. The allocation must be
    /// rolled back on any failure after it.
    /// </summary>
    private static void AFailedOpenReplyRollsBackItsLease(Type entry, Type probe, Type bridge)
    {
        var transport = new Transport(entry);
        try
        {
            int status = (int)probe
                .GetMethod("OpenWithUndersizedReply", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [transport.Dispatcher])!;
            Require(status != 0, "an open whose reply cannot be encoded fails");

            object root = bridge.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [transport])!;
            Require((string)Get(root, "ProductVersion")! == "1.2.3", "a later open still succeeds, so the failed one rolled back");
        }
        finally
        {
            transport.Dispose();
        }
    }

    /// <summary>
    /// The specification says an open after closure allocates a new lease whose
    /// identifier the process never reuses. The first implementation tombstoned
    /// the lease but left it in the table, so the one-lease-per-broker rule
    /// rejected every later open and a wrapper rebuilt against the same
    /// dispatcher could never work again.
    /// </summary>
    private static void AClosedLeaseCanBeReopenedWithAFreshIdentity(Type entry, Type bridge)
    {
        var transport = new Transport(entry);
        try
        {
            object first = bridge.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [transport])!;
            (ulong firstLease, uint firstGeneration) = transport.Lease;
            Require(firstLease != 0UL, "the first open allocates a lease");
            Require((string)Get(first, "ProductVersion")! == "1.2.3", "the first lease works");

            transport.CloseLease();
            RequireBridgeFailure(() => Get(first, "ProductVersion"), "the first wrapper is revoked after close");

            object second = bridge.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [transport])!;
            (ulong secondLease, uint secondGeneration) = transport.Lease;
            Require(secondLease != firstLease, "a reopened lease has a fresh identifier");
            Require(secondGeneration != firstGeneration, "a reopened lease has a fresh generation");
            Require((string)Get(second, "ProductVersion")! == "1.2.3", "the reopened lease works");
            RequireBridgeFailure(() => Get(first, "ProductVersion"), "the old wrapper stays revoked after reopen");
        }
        finally
        {
            transport.Dispose();
        }
    }

    private static void ReadsWriteAndReleaseRoundTrip(Type entry, Type bridge)
    {
        using var session = new Session(entry, bridge);
        Require((string)session.Get("ProductVersion")! == "1.2.3", "a string getter round-trips");
        Require(((System.Collections.IEnumerable)session.Get("Tags")!).Cast<string>().SequenceEqual(["alpha", "beta"]), "a bounded collection round-trips");
        Require(session.Get("State")!.ToString() == "Open", "an enumeration round-trips");
        Require(session.Get("LastFailure") is not null, "a data contract round-trips");

        object child = session.Call("FindChild", "one")!;
        Require((string)Get(child, "Name")! == "child", "a child handle resolves and reads");
        Set(child, "Name", "renamed");
        Require((string)Get(child, "Name")! == "renamed", "a setter mutates through the bridge");
        Require((long?)Get(child, "Size") == 4096L, "a nullable value round-trips");

        object second = session.Call("FindChild", "two")!;
        Require((string)Get(second, "Name")! == "renamed", "the same handler resolves to the same handle");

        Require(session.Call("FindChild", "missing") is null, "a null handle round-trips as Null");

        object indexed = session.Call("GetAt", 0)!;
        Require((string)Get(indexed, "Name")! == "renamed", "a bounded indexer round-trips");

        Invoke(child, "Release");
        Require((bool)HostField(entry, "CurrentHandler", "Child", "Released")!, "release reaches the application handler");

        // The identifier is never re-allocated, so the released wrapper is dead.
        RequireBridgeFailure(() => Get(child, "Name"), "a released handle is revoked");
    }

    private static void ARetainedWrapperFailsAfterLeaseClose(Type entry, Type bridge)
    {
        var session = new Session(entry, bridge);
        object child = session.Call("FindChild", "one")!;
        session.CloseLeaseFromConsumer();
        RequireBridgeFailure(() => Get(child, "Name"), "a retained child wrapper fails after lease close");
        RequireBridgeFailure(() => session.Get("ProductVersion"), "the root wrapper fails after lease close");
        session.Dispose();
    }

    private static void AStaleGenerationNeverReachesAHandler(Type entry, Type bridge)
    {
        using var session = new Session(entry, bridge);
        (ulong leaseId, uint generation) = session.Lease;
        session.CloseLeaseFromPayload();

        // The generation is superseded by closure, so the frozen tuple is stale.
        int before = (int)HostField(entry, "CurrentAuthorizer", "Calls")!;
        int status = session.Transport.RawInvoke(leaseId, generation, session.RootObjectId, 1U);
        Require(status == PowerShellBridgeStatus.AccessDenied, "a stale generation is refused");
        Require((int)HostField(entry, "CurrentAuthorizer", "Calls")! == before, "a stale frame never reaches the authorizer");
    }

    private static void AForgedHandleNeverReachesAHandler(Type entry, Type bridge)
    {
        using var session = new Session(entry, bridge);
        (ulong leaseId, uint generation) = session.Lease;
        int before = (int)HostField(entry, "CurrentAuthorizer", "Calls")!;
        foreach (ulong forged in new ulong[] { 0UL, 999UL, ulong.MaxValue })
        {
            int status = session.Transport.RawInvoke(leaseId, generation, forged, 10U);
            Require(status == PowerShellBridgeStatus.AccessDenied, $"a forged handle {forged} is refused");
        }

        int cross = session.Transport.RawInvoke(leaseId + 1, generation, session.RootObjectId, 1U);
        Require(cross == PowerShellBridgeStatus.AccessDenied, "a cross-lease frame is refused");
        Require((int)HostField(entry, "CurrentAuthorizer", "Calls")! == before, "no forged frame reached the authorizer");
    }

    private static void AReadPermissionCannotAuthorizeASetter(Type entry, Type bridge)
    {
        using var session = new Session(entry, bridge);
        object child = session.Call("FindChild", "one")!;
        HostSet(entry, "CurrentAuthorizer", "DenySetters", true);
        RequireBridgeFailure(() => Set(child, "Name", "blocked"), "a denied setter fails");
        Require((string)HostField(entry, "CurrentHandler", "Child", "Name")! != "blocked", "a denied setter leaves no mutation");
        Require((string)Get(child, "Name")! != "blocked", "the getter is still authorized independently");
        HostSet(entry, "CurrentAuthorizer", "DenySetters", false);
    }

    private static void AnUnknownOrdinalIsRejectedWithoutMutation(Type entry, Type bridge)
    {
        using var session = new Session(entry, bridge);
        (ulong leaseId, uint generation) = session.Lease;
        int before = (int)HostField(entry, "CurrentAuthorizer", "Calls")!;
        foreach (uint ordinal in new uint[] { 0U, 4242U, uint.MaxValue })
        {
            int status = session.Transport.RawInvoke(leaseId, generation, session.RootObjectId, ordinal);
            Require(status == PowerShellBridgeStatus.InvalidArgument, $"ordinal {ordinal} is refused");
        }

        // A declared ordinal aimed at the wrong object type is refused too.
        int wrongOwner = session.Transport.RawInvoke(leaseId, generation, session.RootObjectId, 10U);
        Require(wrongOwner == PowerShellBridgeStatus.AccessDenied, "a member aimed at the wrong object type is refused");
        Require((int)HostField(entry, "CurrentAuthorizer", "Calls")! == before, "no rejected ordinal reached the authorizer");
    }

    private static void AMismatchedDescriptorHashIsRejected(Type entry, Type bridge)
    {
        var transport = new Transport(entry);
        transport.CorruptOpenHash = true;
        try
        {
            bridge.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [transport]);
            throw new InvalidOperationException("Bridge round trip failed: a mismatched descriptor hash was accepted.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is PowerShellBridgeException bridgeException)
        {
            Require(bridgeException.Status == PowerShellBridgeStatus.ContractMismatch, "a mismatched hash reports ContractMismatch");
        }
        finally
        {
            transport.Dispose();
        }
    }

    private static void RepeatedCreateInvokeReleaseCyclesAreStable(Type entry, Type bridge)
    {
        for (int cycle = 0; cycle < 64; cycle++)
        {
            using var session = new Session(entry, bridge);
            object child = session.Call("FindChild", "one")!;
            Require((string)Get(child, "Name")! == "child", "cycle reads");
            Invoke(child, "Release");
            if ((cycle & 7) == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly object root;

        internal Session(Type entry, Type bridge)
        {
            Transport = new Transport(entry);
            root = bridge.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [Transport])!;
        }

        internal Transport Transport { get; }

        internal (ulong LeaseId, uint Generation) Lease => Transport.Lease;

        internal ulong RootObjectId => Transport.RootObjectId;

        internal object? Get(string name) => BridgeRoundTripTests.Get(root, name);

        internal object? Call(string name, params object[] arguments) =>
            root.GetType().GetMethod(name)!.Invoke(root, arguments);

        internal void CloseLeaseFromPayload() => Transport.CloseLease();

        internal void CloseLeaseFromConsumer() => Transport.DisposeDispatcher();

        public void Dispose() => Transport.Dispose();
    }

    /// <summary>Mirrors the shipped COM transport, forwarding to the generated dispatcher.</summary>
    private sealed class Transport : IPowerShellBridgeTransport, IDisposable
    {
        private readonly object dispatcher;
        private readonly MethodInfo invoke;
        private readonly MethodInfo closeLease;
        private readonly MethodInfo dispose;
        private ulong leaseId;
        private uint generation;

        internal Transport(Type entry)
        {
            dispatcher = entry.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
            invoke = dispatcher.GetType().GetMethod("Invoke")!;
            closeLease = dispatcher.GetType().GetMethod("CloseLease")!;
            dispose = dispatcher.GetType().GetMethod("Dispose")!;
        }

        internal bool CorruptOpenHash { get; set; }

        internal object Dispatcher => dispatcher;

        internal (ulong LeaseId, uint Generation) Lease => (leaseId, generation);

        internal ulong RootObjectId { get; private set; }

        public int Invoke(
            ulong requestedLease,
            uint requestedGeneration,
            ulong objectId,
            uint memberId,
            ReadOnlySpan<byte> request,
            Span<byte> reply,
            out int replyLength)
        {
            byte[] requestBytes = request.ToArray();
            if (CorruptOpenHash && requestBytes.Length > PowerShellBridgeWire.RequestHeaderSize + PowerShellBridgeWire.ValueHeaderSize)
            {
                requestBytes[^1] ^= 0xFF;
            }

            int status = Forward(requestedLease, requestedGeneration, objectId, memberId, requestBytes, reply.Length, out byte[] replyBytes, out replyLength);
            if (status == PowerShellBridgeStatus.Success && replyLength > 0)
            {
                replyBytes.AsSpan(0, replyLength).CopyTo(reply);
                CaptureLease(requestedLease, reply, replyLength);
            }

            return status;
        }

        public void PostEvent(uint kind, ulong orderingKey, ReadOnlySpan<byte> body) =>
            throw new InvalidOperationException("This bridge lease has no one-way event sink.");

        internal int RawInvoke(ulong requestedLease, uint requestedGeneration, ulong objectId, uint memberId)
        {
            byte[] request = new byte[PowerShellBridgeWire.RequestHeaderSize];
            var header = new PowerShellBridgeRequestHeader(
                PowerShellBridgeFrameKind.Invoke, 0, memberId, objectId, requestedLease, requestedGeneration, 0);
            if (!header.TryWrite(request))
            {
                throw new InvalidOperationException("Bridge round trip failed: could not encode a raw frame.");
            }

            return Forward(requestedLease, requestedGeneration, objectId, memberId, request, 4096, out _, out _);
        }

        internal void CloseLease() => closeLease.Invoke(dispatcher, [leaseId, generation]);

        internal void DisposeDispatcher() => dispose.Invoke(dispatcher, null);

        public void Dispose() => dispose.Invoke(dispatcher, null);

        private int Forward(
            ulong requestedLease,
            uint requestedGeneration,
            ulong objectId,
            uint memberId,
            byte[] request,
            int replyCapacity,
            out byte[] replyBytes,
            out int replyLength)
        {
            replyBytes = new byte[replyCapacity];
            nint input = Marshal.AllocHGlobal(Math.Max(request.Length, 1));
            nint output = Marshal.AllocHGlobal(Math.Max(replyCapacity, 1));
            try
            {
                if (request.Length > 0)
                {
                    Marshal.Copy(request, 0, input, request.Length);
                }

                object[] arguments =
                [
                    requestedLease, requestedGeneration, objectId, memberId,
                    input, request.Length, output, replyCapacity, 0,
                ];
                int status = (int)invoke.Invoke(dispatcher, arguments)!;
                replyLength = (int)arguments[8];
                if (status == PowerShellBridgeStatus.Success && replyLength > 0)
                {
                    Marshal.Copy(output, replyBytes, 0, replyLength);
                }

                return status;
            }
            finally
            {
                Marshal.FreeHGlobal(input);
                Marshal.FreeHGlobal(output);
            }
        }

        private void CaptureLease(ulong requestedLease, Span<byte> reply, int replyLength)
        {
            if (requestedLease != 0 ||
                !PowerShellBridgeReplyHeader.TryRead(reply[..replyLength], out PowerShellBridgeReplyHeader header))
            {
                return;
            }

            var reader = new PowerShellBridgeValueReader(reply.Slice(PowerShellBridgeWire.ReplyHeaderSize, header.BodyLength));
            if (reader.TryReadBytes(52, out ReadOnlySpan<byte> lease) && lease.Length == 52)
            {
                leaseId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(lease);
                generation = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(lease[8..]);
                RootObjectId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(lease[12..]);
            }
        }
    }

    private static Assembly Emit(
        Func<string, string, string, (Compilation Output, IEnumerable<Diagnostic> Diagnostics)> compile,
        string source,
        string mode,
        string assemblyName)
    {
        (Compilation output, IEnumerable<Diagnostic> diagnostics) = compile(source, mode, assemblyName);
        Diagnostic[] errors = diagnostics
            .Concat(output.GetDiagnostics())
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new InvalidOperationException(
                $"The {mode} round-trip assembly did not compile:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        }

        using var stream = new MemoryStream();
        EmitResult result = output.Emit(stream);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"The {mode} round-trip assembly did not emit:{Environment.NewLine}" +
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        }

        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private static object? Get(object target, string name) =>
        target.GetType().GetProperty(name)!.GetValue(target);

    private static void Set(object target, string name, object value) =>
        Unwrap(() => target.GetType().GetProperty(name)!.SetValue(target, value));

    private static void Invoke(object target, string name) =>
        Unwrap(() => target.GetType().GetMethod(name)!.Invoke(target, null));

    private static object? HostField(Type entry, string accessor, params string[] path)
    {
        object? current = entry.GetMethod(accessor, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        foreach (string step in path)
        {
            current = current!.GetType().GetField(step)!.GetValue(current);
        }

        return current;
    }

    private static void HostSet(Type entry, string accessor, string field, object value)
    {
        object target = entry.GetMethod(accessor, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        target.GetType().GetField(field)!.SetValue(target, value);
    }

    private static void Unwrap(Action action)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static void RequireBridgeFailure(Action action, string what)
    {
        try
        {
            Unwrap(action);
        }
        catch (PowerShellBridgeException exception)
        {
            Require(exception.IsRevoked, $"{what} (expected a revoked status, saw {exception.Status:X8})");
            return;
        }

        throw new InvalidOperationException($"Bridge round trip failed: {what} did not fail.");
    }

    private static void RequireBridgeFailure(Func<object?> action, string what) =>
        RequireBridgeFailure(() => { _ = action(); }, what);

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Bridge round trip failed: {what}.");
        }
    }
}


