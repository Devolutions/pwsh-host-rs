#nullable enable

using System.Collections.Generic;
using System.Text;

namespace Devolutions.MultiPwsh.LiveContract.Generator;

/// <summary>
/// Emits the payload-side CLR wrappers script uses with ordinary property and
/// method syntax, plus their inline typed codecs. Nothing emitted here uses
/// reflection, <c>IDispatch</c>, a dynamic binder, or a serializer.
/// </summary>
internal static class BridgeContractPayloadEmitter
{
    internal static void Emit(StringBuilder source, BridgeContractModel contract)
    {
        EmitClient(source, contract);
        EmitBinding(source, contract);
        foreach (BridgeObjectModel model in contract.Objects)
        {
            EmitWrapper(source, contract, model);
        }
    }

    private static void EmitClient(StringBuilder source, BridgeContractModel contract)
    {
        string client = BridgeNames.Client(contract);
        string constants = BridgeNames.Contract(contract);
        string root = BridgeNames.Wrapper(contract.RootObject);
        source.Append("internal sealed class ").Append(client).AppendLine();
        source.AppendLine("{");
        source.Append("    private readonly ").Append(BridgeTypeNames.Transport).AppendLine(" transport;");
        source.AppendLine("    private readonly object gate = new();");
        source.AppendLine("    private readonly global::System.Collections.Generic.Dictionary<ulong, object> handles = new();");
        source.AppendLine("    private readonly ulong leaseId;");
        source.AppendLine("    private readonly uint generation;");
        source.AppendLine("    private int closed;");
        source.AppendLine();
        source.Append("    private ").Append(client).Append('(').Append(BridgeTypeNames.Transport).AppendLine(" transport, ulong leaseId, uint generation)");
        source.AppendLine("    {");
        source.AppendLine("        this.transport = transport;");
        source.AppendLine("        this.leaseId = leaseId;");
        source.AppendLine("        this.generation = generation;");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    internal ulong RootObjectId { get; private set; }");
        source.AppendLine();
        source.AppendLine("    internal ulong LeaseId => leaseId;");
        source.AppendLine();
        source.AppendLine("    internal uint Generation => generation;");
        source.AppendLine();
        source.AppendLine("    /// <summary>Performs the lease-open handshake and verifies the echoed descriptor hash.</summary>");
        source.Append("    internal static ").Append(client).Append(" Open(").Append(BridgeTypeNames.Transport).AppendLine(" transport)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(transport);");
        source.Append("        int requestLength = ").Append(BridgeTypeNames.Wire)
            .Append(".RequestHeaderSize + ").Append(BridgeTypeNames.Wire).AppendLine(".ValueHeaderSize + 32;");
        source.AppendLine("        byte[] request = new byte[requestLength];");
        source.Append("        var writer = new ").Append(BridgeTypeNames.Writer).Append("(request.AsSpan(").Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize));");
        source.Append("        if (!writer.TryWriteBytes(").Append(constants).AppendLine(".DescriptorHash, 32) || !writer.IsComplete)");
        source.AppendLine("        {");
        source.Append("            throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
            .Append(".InvalidArgument, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' could not encode its open frame.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var header = new ").Append(BridgeTypeNames.RequestHeader).Append('(').Append(BridgeTypeNames.FrameKind)
            .AppendLine(".Open, 1, 0U, 0UL, 0UL, 0U, writer.Length);");
        source.AppendLine("        if (!header.TryWrite(request))");
        source.AppendLine("        {");
        source.Append("            throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
            .Append(".InvalidArgument, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' could not encode its open frame.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        byte[] reply = new byte[").Append(BridgeTypeNames.Wire).AppendLine(".ReplyHeaderSize + 8 + 52];");
        source.AppendLine("        int status = transport.Invoke(0UL, 0U, 0UL, 0U, request, reply, out int replyLength);");
        source.AppendLine("        if (status != 0)");
        source.AppendLine("        {");
        source.Append("            throw ").Append(BridgeTypeNames.BridgeException).Append(".FromStatus(status, \"")
            .Append(BridgeNames.Escape(contract.ContractId)).AppendLine("\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        if (!").Append(BridgeTypeNames.ReplyHeader).AppendLine(".TryRead(reply.AsSpan(0, replyLength), out var replyHeader) ||");
        source.Append("            replyHeader.ReplyKind != ").Append(BridgeTypeNames.ReplyKind).AppendLine(".Value)");
        source.AppendLine("        {");
        source.Append("            throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
            .Append(".InvalidArgument, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' received a malformed open reply.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var reader = new ").Append(BridgeTypeNames.Reader).Append("(reply.AsSpan(").Append(BridgeTypeNames.Wire).AppendLine(".ReplyHeaderSize, replyHeader.BodyLength));");
        source.AppendLine("        if (!reader.TryReadBytes(52, out global::System.ReadOnlySpan<byte> lease) || lease.Length != 52 || !reader.IsComplete)");
        source.AppendLine("        {");
        source.Append("            throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
            .Append(".InvalidArgument, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' received a malformed open reply.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        ulong openedLease = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(lease);");
        source.AppendLine("        uint openedGeneration = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(lease.Slice(8));");
        source.AppendLine("        ulong rootObjectId = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(lease.Slice(12));");
        source.Append("        if (openedLease == 0UL || openedGeneration == 0U || rootObjectId == 0UL || !lease.Slice(20).SequenceEqual(")
            .Append(constants).AppendLine(".DescriptorHash))");
        source.AppendLine("        {");
        source.Append("            throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
            .Append(".ContractMismatch, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' rejected the host descriptor hash.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        return new ").Append(client).AppendLine("(transport, openedLease, openedGeneration) { RootObjectId = rootObjectId };");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    internal byte[] Invoke(ulong objectId, uint memberId, int argumentCount, byte[] request, int bodyLength, int replyCapacity)");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Threading.Volatile.Read(ref closed) != 0)");
        source.AppendLine("        {");
        source.Append("            throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
            .Append(".AccessDenied, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' lease has been released.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var header = new ").Append(BridgeTypeNames.RequestHeader).Append('(').Append(BridgeTypeNames.FrameKind)
            .AppendLine(".Invoke, checked((ushort)argumentCount), memberId, objectId, leaseId, generation, bodyLength);");
        source.Append("        if (!header.TryWrite(request)) { throw new ").Append(BridgeTypeNames.BridgeException).Append('(')
            .Append(BridgeTypeNames.Status).Append(".InvalidArgument, \"Bridge contract '")
            .Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' could not encode a request frame.\"); }");
        source.Append("        byte[] reply = new byte[replyCapacity];").AppendLine();
        source.Append("        int status = transport.Invoke(leaseId, generation, objectId, memberId, request.AsSpan(0, ")
            .Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize + bodyLength), reply, out int replyLength);");
        source.Append("        if (status != 0) { throw ").Append(BridgeTypeNames.BridgeException).Append(".FromStatus(status, \"")
            .Append(BridgeNames.Escape(contract.ContractId)).AppendLine("\"); }");
        source.AppendLine("        if (replyLength < 0 || replyLength > reply.Length) { throw new " + BridgeTypeNames.BridgeException + "(" + BridgeTypeNames.Status + ".InvalidArgument, \"Bridge reply length is out of range.\"); }");
        source.AppendLine("        return replyLength == reply.Length ? reply : reply.AsSpan(0, replyLength).ToArray();");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    internal void PostEvent(ulong objectId, uint memberId, ulong orderingKey, byte[] request, int argumentCount, int bodyLength)");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Threading.Volatile.Read(ref closed) != 0)");
        source.AppendLine("        {");
        source.Append("            throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
            .Append(".AccessDenied, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' lease has been released.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var header = new ").Append(BridgeTypeNames.RequestHeader).Append('(').Append(BridgeTypeNames.FrameKind)
            .AppendLine(".Event, checked((ushort)argumentCount), memberId, objectId, leaseId, generation, bodyLength);");
        source.Append("        if (!header.TryWrite(request)) { throw new ").Append(BridgeTypeNames.BridgeException).Append('(')
            .Append(BridgeTypeNames.Status).Append(".InvalidArgument, \"Bridge contract '")
            .Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' could not encode an event frame.\"); }");
        source.Append("        transport.PostEvent(0x42520000U | memberId, orderingKey, request.AsSpan(0, ")
            .Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize + bodyLength));");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    /// <summary>Sends an explicit release frame for one object handle.</summary>");
        source.AppendLine("    internal void Release(ulong objectId, uint releaseId)");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Threading.Volatile.Read(ref closed) != 0) { return; }");
        source.Append("        byte[] request = new byte[").Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize];");
        source.Append("        var header = new ").Append(BridgeTypeNames.RequestHeader).Append('(').Append(BridgeTypeNames.FrameKind)
            .AppendLine(".Release, 0, releaseId, objectId, leaseId, generation, 0);");
        source.Append("        if (!header.TryWrite(request)) { throw new ").Append(BridgeTypeNames.BridgeException).Append('(')
            .Append(BridgeTypeNames.Status).Append(".InvalidArgument, \"Bridge contract '")
            .Append(BridgeNames.Escape(contract.ContractId)).AppendLine("' could not encode a release frame.\"); }");
        source.Append("        byte[] reply = new byte[").Append(BridgeTypeNames.Wire).Append(".ReplyHeaderSize + ")
            .Append(BridgeTypeNames.Wire).AppendLine(".ValueHeaderSize];");
        source.AppendLine("        int status = transport.Invoke(leaseId, generation, objectId, releaseId, request, reply, out _);");
        source.Append("        if (status != 0) { throw ").Append(BridgeTypeNames.BridgeException).Append(".FromStatus(status, \"")
            .Append(BridgeNames.Escape(contract.ContractId)).AppendLine("\"); }");
        source.AppendLine("        lock (gate) { handles.Remove(objectId); }");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    /// <summary>Marks the lease released so every escaped wrapper fails deterministically.</summary>");
        source.AppendLine("    internal void Close()");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Threading.Interlocked.Exchange(ref closed, 1) != 0) { return; }");
        source.AppendLine("        lock (gate) { handles.Clear(); }");
        source.AppendLine("    }");
        source.AppendLine();
        foreach (BridgeObjectModel model in contract.Objects)
        {
            string wrapper = BridgeNames.Wrapper(model);
            source.Append("    internal ").Append(wrapper).Append(" Resolve").Append(wrapper).AppendLine("(ulong objectId)");
            source.AppendLine("    {");
            source.AppendLine("        lock (gate)");
            source.AppendLine("        {");
            source.Append("            if (handles.TryGetValue(objectId, out object? existing) && existing is ").Append(wrapper).AppendLine(" typed)");
            source.AppendLine("            {");
            source.AppendLine("                return typed;");
            source.AppendLine("            }");
            source.AppendLine();
            source.AppendLine("            // The consumer's own object table is bounded, but a bound enforced");
            source.AppendLine("            // only by the peer is not a bound on this side. Every other number");
            source.AppendLine("            // in this protocol is re-checked locally; this one is too.");
            source.Append("            if (handles.Count >= global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeLeaseTable.MaximumObjectsPerLease)").AppendLine();
            source.AppendLine("            {");
            source.Append("                throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
                .Append(".OutOfMemory, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId))
                .AppendLine("' exceeded its bounded object table.\");");
            source.AppendLine("            }");
            source.AppendLine();
            source.Append("            var created = new ").Append(wrapper).AppendLine("(this, objectId);");
            source.AppendLine("            handles[objectId] = created;");
            source.AppendLine("            return created;");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine();
        }

        source.Append("    internal ").Append(root).Append(" Root => Resolve").Append(root).AppendLine("(RootObjectId);");
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitBinding(StringBuilder source, BridgeContractModel contract)
    {
        string binding = BridgeNames.Binding(contract);
        string client = BridgeNames.Client(contract);
        string root = BridgeNames.Wrapper(contract.RootObject);
        string constants = BridgeNames.Contract(contract);
        string transport = contract.TransportInterfaceType;
        source.Append("internal sealed class ").Append(binding).AppendLine();
        source.AppendLine("{");
        source.AppendLine("    private static readonly global::System.Runtime.InteropServices.Marshalling.StrategyBasedComWrappers ComWrappers = new();");
        source.AppendLine("    private readonly object gate = new();");
        source.AppendLine("    private readonly global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeContractSink sink;");
        source.AppendLine("    private global::System.Runtime.InteropServices.Marshalling.ComObject? sinkComObject;");
        source.Append("    private ").Append(client).AppendLine("? client;");
        source.Append("    private ").Append(transport).AppendLine("? contract;");
        source.AppendLine("    private global::System.Runtime.InteropServices.Marshalling.ComObject? contractComObject;");
        source.AppendLine("    private int bound;");
        source.AppendLine("    private int disposed;");
        source.AppendLine();
        source.Append("    private ").Append(binding).Append("(")
            .Append("global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeContractSink sink, ")
            .AppendLine("global::System.Runtime.InteropServices.Marshalling.ComObject sinkComObject)");
        source.AppendLine("    {");
        source.AppendLine("        this.sink = sink;");
        source.AppendLine("        this.sinkComObject = sinkComObject;");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    internal static ").Append(binding).Append(" Create(")
            .Append("global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeContractSink sink, ")
            .AppendLine("global::System.Runtime.InteropServices.Marshalling.ComObject sinkComObject)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(sink);");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(sinkComObject);");
        source.AppendLine("        try");
        source.AppendLine("        {");
        source.AppendLine("            global::System.Guid expected = global::System.Guid.Parse(" + constants + ".TransportInterfaceId);");
        source.AppendLine("            byte[] identity = expected.ToByteArray();");
        source.AppendLine("            ulong expectedLow = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(identity);");
        source.AppendLine("            ulong expectedHigh = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(8));");
        source.AppendLine("            int status = sink.GetRequestedContract(out ulong requestedLow, out ulong requestedHigh, out ushort requestedMajor, out ushort requestedMinor);");
        source.AppendLine("            if (status != global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeStatus.Success)");
        source.AppendLine("            {");
        source.AppendLine("                throw global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeException.FromStatus(status, " + constants + ".ContractId);");
        source.AppendLine("            }");
        source.AppendLine();
        source.Append("            if (requestedLow != expectedLow || requestedHigh != expectedHigh || requestedMajor != ")
            .Append(constants).Append(".MajorVersion || requestedMinor != ").Append(constants).AppendLine(".MinorVersion)");
        source.AppendLine("            {");
        source.AppendLine("                throw new global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeException(");
        source.AppendLine("                    global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeStatus.ContractMismatch,");
        source.AppendLine("                    \"The bridge sink requested a different contract identity.\");");
        source.AppendLine("            }");
        source.AppendLine();
        source.Append("            return new ").Append(binding).AppendLine("(sink, sinkComObject);");
        source.AppendLine("        }");
        source.AppendLine("        catch");
        source.AppendLine("        {");
        source.AppendLine("            sinkComObject.FinalRelease();");
        source.AppendLine("            throw;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    internal static void Declare(")
            .Append("global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeContractSink sink, nint callback)").AppendLine();
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(sink);");
        source.AppendLine("        if (callback == 0)");
        source.AppendLine("        {");
        source.AppendLine("            throw new global::System.ArgumentException(\"The bridge callback pointer is null.\", nameof(callback));");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        global::System.Guid expected = global::System.Guid.Parse(" + constants + ".TransportInterfaceId);");
        source.AppendLine("        byte[] identity = expected.ToByteArray();");
        source.AppendLine("        ulong expectedLow = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(identity);");
        source.AppendLine("        ulong expectedHigh = global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(identity.AsSpan(8));");
        source.AppendLine("        int status = sink.Declare(expectedLow, expectedHigh, checked((ushort)" + constants + ".MajorVersion), checked((ushort)" + constants + ".MinorVersion), callback);");
        source.AppendLine("        if (status != global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeStatus.Success)");
        source.AppendLine("        {");
        source.AppendLine("            throw global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeException.FromStatus(status, " + constants + ".ContractId);");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    public object Bind()");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Threading.Volatile.Read(ref disposed) != 0)");
        source.AppendLine("        {");
        source.AppendLine("            throw new global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeException(");
        source.AppendLine("                global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeStatus.AccessDenied,");
        source.AppendLine("                \"The bridge payload proxy was released; rediscover the contract before binding it again.\");");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        lock (gate)");
        source.AppendLine("        {");
        source.AppendLine("            if (bound != 0)");
        source.AppendLine("            {");
        source.AppendLine("                return client!.Root;");
        source.AppendLine("            }");
        source.AppendLine();
        source.AppendLine("            nint pointer = 0;");
        source.AppendLine("            global::System.Runtime.InteropServices.Marshalling.ComObject? imported = null;");
        source.AppendLine("            try");
        source.AppendLine("            {");
        source.AppendLine("                int status = sink.GetConsumerContract(out pointer);");
        source.AppendLine("                if (status != global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeStatus.Success || pointer == 0)");
        source.AppendLine("                {");
        source.AppendLine("                    throw global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeException.FromStatus(status, " + constants + ".ContractId);");
        source.AppendLine("                }");
        source.AppendLine();
        source.AppendLine("                object projected = ComWrappers.GetOrCreateObjectForComInstance(pointer, global::System.Runtime.InteropServices.CreateObjectFlags.UniqueInstance);");
        source.AppendLine("                imported = projected as global::System.Runtime.InteropServices.Marshalling.ComObject");
        source.AppendLine("                    ?? throw new global::System.InvalidOperationException(\"The bridge sink did not return a source-generated COM transport.\");");
        source.Append("                ").Append(transport).Append(" typed = projected as ").Append(transport)
            .AppendLine(" ?? throw new global::System.InvalidOperationException(\"The bridge sink returned the wrong transport interface.\");");
        source.AppendLine("                var bridgeTransport = new global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeComTransport(");
        source.AppendLine("                    typed.Invoke,");
        source.AppendLine("                    projected as global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeEventSink);");
        source.Append("                ").Append(client).Append(" opened = ").Append(client).AppendLine(".Open(bridgeTransport);");
        source.AppendLine("                client = opened;");
        source.AppendLine("                contract = typed;");
        source.AppendLine("                contractComObject = imported;");
        source.AppendLine("                bound = 1;");
        source.AppendLine("                imported = null;");
        source.AppendLine("                return opened.Root;");
        source.AppendLine("            }");
        source.AppendLine("            finally");
        source.AppendLine("            {");
        source.AppendLine("                if (pointer != 0)");
        source.AppendLine("                {");
        source.AppendLine("                    global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeComReference.Release(pointer);");
        source.AppendLine("                }");
        source.AppendLine();
        source.AppendLine("                imported?.FinalRelease();");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    public void Unbind()");
        source.AppendLine("    {");
        source.Append(client).AppendLine("? previousClient;");
        source.Append(transport).AppendLine("? previousContract;");
        source.AppendLine("        global::System.Runtime.InteropServices.Marshalling.ComObject? previousComObject;");
        source.AppendLine("        lock (gate)");
        source.AppendLine("        {");
        source.AppendLine("            if (bound == 0)");
        source.AppendLine("            {");
        source.AppendLine("                return;");
        source.AppendLine("            }");
        source.AppendLine();
        source.AppendLine("            bound = 0;");
        source.AppendLine("            previousClient = client;");
        source.AppendLine("            previousContract = contract;");
        source.AppendLine("            previousComObject = contractComObject;");
        source.AppendLine("            client = null;");
        source.AppendLine("            contract = null;");
        source.AppendLine("            contractComObject = null;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        global::System.Exception? failure = null;");
        source.AppendLine("        try");
        source.AppendLine("        {");
        source.AppendLine("            previousClient?.Close();");
        source.AppendLine("            if (previousContract is not null && previousClient is not null)");
        source.AppendLine("            {");
        source.AppendLine("                int status = previousContract.CloseLease(previousClient.LeaseId, previousClient.Generation);");
        source.AppendLine("                if (status != global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeStatus.Success)");
        source.AppendLine("                {");
        source.AppendLine("                    failure = global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeException.FromStatus(status, " + constants + ".ContractId);");
        source.AppendLine("                }");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine("        catch (global::System.Exception exception)");
        source.AppendLine("        {");
        source.AppendLine("            failure = exception;");
        source.AppendLine("        }");
        source.AppendLine("        finally");
        source.AppendLine("        {");
        source.AppendLine("            previousComObject?.FinalRelease();");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        if (failure is not null)");
        source.AppendLine("        {");
        source.AppendLine("            throw failure;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    public void Dispose()");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Threading.Interlocked.Exchange(ref disposed, 1) != 0)");
        source.AppendLine("        {");
        source.AppendLine("            return;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        global::System.Exception? failure = null;");
        source.AppendLine("        try");
        source.AppendLine("        {");
        source.AppendLine("            Unbind();");
        source.AppendLine("        }");
        source.AppendLine("        catch (global::System.Exception exception)");
        source.AppendLine("        {");
        source.AppendLine("            failure = exception;");
        source.AppendLine("        }");
        source.AppendLine("        finally");
        source.AppendLine("        {");
        source.AppendLine("            global::System.Runtime.InteropServices.Marshalling.ComObject? previous;");
        source.AppendLine("            lock (gate)");
        source.AppendLine("            {");
        source.AppendLine("                previous = sinkComObject;");
        source.AppendLine("                sinkComObject = null;");
        source.AppendLine("            }");
        source.AppendLine("            previous?.FinalRelease();");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        if (failure is not null)");
        source.AppendLine("        {");
        source.AppendLine("            throw failure;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitWrapper(StringBuilder source, BridgeContractModel contract, BridgeObjectModel model)
    {
        string wrapper = BridgeNames.Wrapper(model);
        string client = BridgeNames.Client(contract);
        string constants = BridgeNames.Contract(contract);
        bool isRoot = model.Id == contract.RootObject.Id;
        source.Append("/// <summary>The payload wrapper for bridge object ").Append(BridgeTypeNames.Number(model.Id)).AppendLine(".</summary>");
        source.Append("public sealed class ").Append(wrapper);
        source.AppendLine(isRoot ? " : global::System.IDisposable" : string.Empty);
        source.AppendLine("{");
        source.Append("    private readonly ").Append(client).AppendLine(" client;");
        source.AppendLine();
        source.Append("    internal ").Append(wrapper).Append('(').Append(client).AppendLine(" client, ulong objectId)");
        source.AppendLine("    {");
        source.AppendLine("        this.client = client;");
        source.AppendLine("        ObjectId = objectId;");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    /// <summary>The lease-scoped object identifier. It is an integer, never a pointer or CLR identity.</summary>");
        source.AppendLine("    internal ulong ObjectId { get; }");
        source.AppendLine();

        if (isRoot)
        {
            source.Append("    /// <summary>Opens a lease over one bound transport and returns the contract root.</summary>");
            source.AppendLine();
            source.Append("    public static ").Append(wrapper).Append(" Open(").Append(BridgeTypeNames.Transport).AppendLine(" transport)");
            source.AppendLine("    {");
            source.Append("        return ").Append(client).AppendLine(".Open(transport).Root;");
            source.AppendLine("    }");
            source.AppendLine();
        }

        foreach (BridgeMemberModel member in model.Members)
        {
            switch (member.Kind)
            {
                case BridgeRecordKind.Getter:
                    EmitProperty(source, contract, model, member);
                    break;
                case BridgeRecordKind.Method:
                    EmitMethod(source, contract, member);
                    break;
                case BridgeRecordKind.Event:
                    EmitEvent(source, contract, member);
                    break;
                default:
                    break;
            }
        }

        source.AppendLine("    /// <summary>Releases this handle through its declared release ordinal.</summary>");
        source.AppendLine("    public void Release()");
        source.AppendLine("    {");
        source.Append("        client.Release(ObjectId, ").Append(constants).Append(".Release")
            .Append(BridgeTypeNames.Number(model.Id)).AppendLine(");");
        source.AppendLine("    }");

        if (isRoot)
        {
            source.AppendLine();
            source.AppendLine("    /// <summary>Marks the lease released locally. The host still ends the lease authoritatively.</summary>");
            source.AppendLine("    public void Dispose() => client.Close();");
        }

        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitProperty(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeObjectModel model,
        BridgeMemberModel getter)
    {
        BridgeMemberModel? setter = model.Members.Find(candidate =>
            candidate.Kind == BridgeRecordKind.Setter && candidate.Name == getter.Name);
        string type = BridgeTypeNames.Full(getter.Result, contract, payload: true);
        source.Append("    public ").Append(type).Append(' ').Append(BridgeNames.Identifier(getter.Name)).AppendLine();
        source.AppendLine("    {");
        source.AppendLine("        get");
        source.AppendLine("        {");
        EmitCallBody(source, contract, getter, [], "            ", type);
        source.AppendLine("        }");
        if (setter is not null)
        {
            source.AppendLine("        set");
            source.AppendLine("        {");
            EmitCallBody(source, contract, setter, ["value"], "            ", "void");
            source.AppendLine("        }");
        }

        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitMethod(StringBuilder source, BridgeContractModel contract, BridgeMemberModel member)
    {
        string type = BridgeTypeNames.Full(member.Result, contract, payload: true);
        var names = new List<string>();
        source.Append("    public ").Append(type).Append(' ').Append(BridgeNames.Identifier(MethodName(member))).Append('(');
        for (int index = 0; index < member.Parameters.Count; index++)
        {
            BridgeParameterModel parameter = member.Parameters[index];
            if (index != 0)
            {
                source.Append(", ");
            }

            string name = BridgeNames.Identifier(parameter.Name);
            names.Add(name);
            source.Append(BridgeTypeNames.Full(parameter.Type, contract, payload: true)).Append(' ').Append(name);
        }

        source.AppendLine(")");
        source.AppendLine("    {");
        EmitCallBody(source, contract, member, names, "        ", type);
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitEvent(StringBuilder source, BridgeContractModel contract, BridgeMemberModel member)
    {
        string constants = BridgeNames.Contract(contract);
        var names = new List<string>();
        source.Append("    public void ").Append(BridgeNames.Identifier(member.Name)).Append('(');
        for (int index = 0; index < member.Parameters.Count; index++)
        {
            BridgeParameterModel parameter = member.Parameters[index];
            if (index != 0)
            {
                source.Append(", ");
            }

            string name = BridgeNames.Identifier(parameter.Name);
            names.Add(name);
            source.Append(BridgeTypeNames.Full(parameter.Type, contract, payload: true)).Append(' ').Append(name);
        }

        source.AppendLine(")");
        source.AppendLine("    {");
        int request = member.MaximumRequestBytes(contract.DataById);
        source.Append("        byte[] __bridgeRequest = new byte[").Append(BridgeTypeNames.Number(request)).AppendLine("];");
        source.Append("        var __bridgeWriter = new ").Append(BridgeTypeNames.Writer).Append("(__bridgeRequest.AsSpan(")
            .Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize));");
        int temp = 0;
        string onFail = "throw new " + BridgeTypeNames.BridgeException + "(" + BridgeTypeNames.Status +
            ".InvalidArgument, \"Bridge contract '" + BridgeNames.Escape(contract.ContractId) + "' rejected an argument that exceeds its declared bound.\");";
        for (int index = 0; index < member.Parameters.Count; index++)
        {
            BridgeCodecEmitter.EmitWrite(source, contract, member.Parameters[index].Type, names[index], "        ", onFail, payload: true, ref temp);
        }

        BridgeCodecEmitter.Guard(source, "        ", "__bridgeWriter.IsComplete", onFail);
        source.Append("        client.PostEvent(ObjectId, ").Append(constants).Append(".Member").Append(BridgeTypeNames.Number(member.Ordinal))
            .Append(", ").Append(BridgeTypeNames.Number(member.OrderingKey)).Append("UL, __bridgeRequest, ")
            .Append(BridgeTypeNames.Number(member.Parameters.Count)).AppendLine(", __bridgeWriter.Length);");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitCallBody(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeMemberModel member,
        IReadOnlyList<string> arguments,
        string indent,
        string resultType)
    {
        string constants = BridgeNames.Contract(contract);
        int request = member.MaximumRequestBytes(contract.DataById);
        int reply = member.MaximumReplyBytes(contract.DataById);
        string onFail = "throw new " + BridgeTypeNames.BridgeException + "(" + BridgeTypeNames.Status +
            ".InvalidArgument, \"Bridge contract '" + BridgeNames.Escape(contract.ContractId) + "' rejected a frame that violates its declared bounds.\");";
        source.Append(indent).Append("byte[] __bridgeRequest = new byte[").Append(BridgeTypeNames.Number(request)).AppendLine("];");
        source.Append(indent).Append("var __bridgeWriter = new ").Append(BridgeTypeNames.Writer).Append("(__bridgeRequest.AsSpan(")
            .Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize));");
        int temp = 0;
        for (int index = 0; index < member.Parameters.Count && index < arguments.Count; index++)
        {
            BridgeCodecEmitter.EmitWrite(source, contract, member.Parameters[index].Type, arguments[index], indent, onFail, payload: true, ref temp);
        }

        BridgeCodecEmitter.Guard(source, indent, "__bridgeWriter.IsComplete", onFail);
        source.Append(indent).Append("byte[] __bridgeReply = client.Invoke(ObjectId, ").Append(constants).Append(".Member")
            .Append(BridgeTypeNames.Number(member.Ordinal)).Append(", ").Append(BridgeTypeNames.Number(member.Parameters.Count))
            .Append(", __bridgeRequest, __bridgeWriter.Length, ").Append(BridgeTypeNames.Number(reply)).AppendLine(");");
        BridgeCodecEmitter.Guard(source, indent, BridgeTypeNames.ReplyHeader + ".TryRead(__bridgeReply, out var __bridgeReplyHeader)", onFail);
        source.Append(indent).Append("if (__bridgeReplyHeader.ReplyKind == ").Append(BridgeTypeNames.ReplyKind).AppendLine(".Error)");
        source.Append(indent).AppendLine("{");
        source.Append(indent).Append("    throw new ").Append(BridgeTypeNames.BridgeException).Append('(').Append(BridgeTypeNames.Status)
            .Append(".AccessDenied, \"Bridge contract '").Append(BridgeNames.Escape(contract.ContractId))
            .AppendLine("' returned a declared application failure.\");");
        source.Append(indent).AppendLine("}");
        source.AppendLine();
        source.Append(indent).Append("var __bridgeReader = new ").Append(BridgeTypeNames.Reader).Append("(__bridgeReply.AsSpan(")
            .Append(BridgeTypeNames.Wire).AppendLine(".ReplyHeaderSize, __bridgeReplyHeader.BodyLength));");
        if (member.Result.Tag == BridgeTag.Null && !member.Result.IsNullable)
        {
        BridgeCodecEmitter.Guard(source, indent, "__bridgeReader.TryReadNull() && __bridgeReader.IsComplete", onFail);
            if (resultType != "void")
            {
                source.Append(indent).AppendLine("return default!;");
            }

            return;
        }

        source.Append(indent).Append(resultType).AppendLine(" __bridgeResult = default!;");
        BridgeCodecEmitter.EmitRead(source, contract, member.Result, "__bridgeResult", indent, onFail, payload: true, ref temp);
        BridgeCodecEmitter.Guard(source, indent, "__bridgeReader.IsComplete", onFail);
        source.Append(indent).AppendLine("return __bridgeResult;");
    }

    private static string MethodName(BridgeMemberModel member) =>
        member.Name == "get_Item" ? "GetAt" : member.Name;
}
