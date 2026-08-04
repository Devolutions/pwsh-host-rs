#nullable enable

using System.Collections.Generic;
using System.Text;

namespace Devolutions.MultiPwsh.LiveContract.Generator;

/// <summary>
/// Emits the generated consumer dispatcher: the transport-neutral core that
/// decodes a frame, admits it against the bounded lease and object tables,
/// authorizes the accessor independently, calls the typed handler, and encodes
/// the reply.
/// </summary>
/// <remarks>
/// The core takes spans, so a later carrier can feed it without changing a line
/// of generated logic. A thin COM-shaped entry point sits on top for the small
/// hand-written <c>[GeneratedComClass]</c> wrapper an application supplies; a
/// source generator cannot see another generator's output, so that wrapper
/// cannot be emitted here.
/// </remarks>
internal static class BridgeContractDispatcherEmitter
{
    internal static void Emit(StringBuilder source, BridgeContractModel contract)
    {
        string name = BridgeNames.Prefix(contract.Root) + "Dispatcher";
        string constants = BridgeNames.Contract(contract);
        string context = BridgeNames.Prefix(contract.Root) + "CallContext";
        string rootHandler = BridgeNames.Handler(contract.RootObject);
        string authorizer = "I" + BridgeNames.Prefix(contract.Root) + "Authorizer";
        int maximumReply = MaximumReply(contract);
        int maximumRequest = MaximumRequest(contract);

        source.AppendLine("/// <summary>");
        source.AppendLine("/// Dispatches bridge frames to typed application handlers. Every getter,");
        source.AppendLine("/// setter, and method is authorized independently, immediately before it runs,");
        source.AppendLine("/// and every frame is admitted against the lease and object tables first.");
        source.AppendLine("/// </summary>");
        source.Append("public sealed class ").Append(name)
            .AppendLine(" : global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeDispatcher");
        source.AppendLine("{");
        source.AppendLine("    private readonly global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeLeaseTable leases = new();");
        source.Append("    private readonly ").Append(rootHandler).AppendLine(" root;");
        source.Append("    private readonly ").Append(authorizer).AppendLine(" authorizer;");
        source.AppendLine("    private int disposed;");
        source.AppendLine();
        source.Append("    public ").Append(name).Append('(').Append(rootHandler).Append(" root, ").Append(authorizer).AppendLine(" authorizer)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(root);");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(authorizer);");
        source.AppendLine("        this.root = root;");
        source.AppendLine("        this.authorizer = authorizer;");
        source.AppendLine("    }");
        source.AppendLine();
        source.Append("    /// <summary>The largest reply any declared member can produce, computed at compile time.</summary>")
            .AppendLine();
        source.Append("    public const int MaximumReplyBytes = ").Append(BridgeTypeNames.Number(maximumReply)).AppendLine(";");
        source.AppendLine();
        source.AppendLine("    /// <summary>The largest request frame any declared member can accept, computed at compile time.</summary>");
        source.Append("    public const int MaximumRequestBytes = ").Append(BridgeTypeNames.Number(maximumRequest)).AppendLine(";");
        source.AppendLine();
        source.AppendLine("    /// <summary>The contract transport identity used for payload discovery.</summary>");
        source.Append("    public global::System.Guid ContractInterfaceId { get; } = global::System.Guid.Parse(")
            .Append(constants).AppendLine(".TransportInterfaceId);");
        source.AppendLine();
        source.Append("    /// <summary>The declared contract major version.</summary>").AppendLine();
        source.Append("    public ushort ContractMajorVersion => checked((ushort)").Append(constants).AppendLine(".MajorVersion);");
        source.AppendLine();
        source.Append("    /// <summary>The declared contract minor version.</summary>").AppendLine();
        source.Append("    public ushort ContractMinorVersion => checked((ushort)").Append(constants).AppendLine(".MinorVersion);");
        source.AppendLine();
        source.AppendLine("    int global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeDispatcher.MaximumReplyBytes => MaximumReplyBytes;");
        source.AppendLine("    int global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeDispatcher.MaximumRequestBytes => MaximumRequestBytes;");
        source.AppendLine();
        source.AppendLine("    int global::Devolutions.PowerShell.Ffi.LiveObjects.IPowerShellBridgeDispatcher.GetReliableEventMaximumRetained(uint memberId)");
        source.AppendLine("    {");
        source.AppendLine("        return memberId switch");
        source.AppendLine("        {");
        foreach (BridgeObjectModel model in contract.Objects)
        {
            foreach (BridgeMemberModel member in model.Members)
            {
                if (member.Kind == BridgeRecordKind.ReliableEvent)
                {
                    source.Append("            ").Append(BridgeTypeNames.Number(member.Ordinal)).Append("U => ")
                        .Append(BridgeTypeNames.Number(member.MaximumRetainedEvents)).AppendLine(",");
                }
            }
        }
        source.AppendLine("            _ => 0,");
        source.AppendLine("        };");
        source.AppendLine("    }");
        source.AppendLine();
        EmitComEntryPoint(source, name, maximumReply);
        EmitCloseAndDispose(source);
        EmitCore(source, contract, constants, context, maximumReply);
        EmitEvent(source, contract, constants, context);
        EmitOpen(source, contract, constants);
        EmitClose(source, context);
        EmitRelease(source, contract, constants, context);
        EmitMemberDispatch(source, contract, constants, context);
        EmitEventMemberDispatch(source, contract, context);
        EmitHandleHelpers(source, contract);
        EmitReplyHelpers(source, contract);
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitComEntryPoint(StringBuilder source, string name, int maximumReply)
    {
        source.AppendLine("    /// <summary>");
        source.AppendLine("    /// The COM-shaped entry point. Forward a contract transport interface's");
        source.AppendLine("    /// <c>Invoke</c> to this from a hand-written <c>[GeneratedComClass]</c> wrapper.");
        source.AppendLine("    /// It copies through managed buffers, so a consumer project never needs");
        source.AppendLine("    /// <c>AllowUnsafeBlocks</c> to host a generated dispatcher.");
        source.AppendLine("    /// </summary>");
        source.AppendLine("    public int Invoke(");
        source.AppendLine("        ulong leaseId,");
        source.AppendLine("        uint generation,");
        source.AppendLine("        ulong objectId,");
        source.AppendLine("        uint memberId,");
        source.AppendLine("        nint input,");
        source.AppendLine("        int inputLength,");
        source.AppendLine("        nint output,");
        source.AppendLine("        int outputCapacity,");
        source.AppendLine("        out int outputLength)");
        source.AppendLine("    {");
        source.AppendLine("        outputLength = 0;");
        source.Append("        if (inputLength < 0 || outputCapacity < 0 || inputLength > ").Append(BridgeTypeNames.Wire).AppendLine(".MaximumFrameBytes ||");
        source.AppendLine("            (input == 0 && inputLength != 0) || (output == 0 && outputCapacity != 0))");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        byte[] request = new byte[inputLength];");
        source.AppendLine("        if (inputLength > 0)");
        source.AppendLine("        {");
        source.AppendLine("            global::System.Runtime.InteropServices.Marshal.Copy(input, request, 0, inputLength);");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        byte[] reply = new byte[").Append(BridgeTypeNames.Number(maximumReply)).AppendLine("];");
        source.AppendLine("        int status = Dispatch(leaseId, generation, objectId, memberId, outputCapacity, request, reply, out int written);");
        source.AppendLine("        outputLength = written;");
        source.AppendLine("        if (status != 0)");
        source.AppendLine("        {");
        source.AppendLine("            return status;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        if (written > outputCapacity || written > reply.Length)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".BufferTooSmall;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        if (written > 0)");
        source.AppendLine("        {");
        source.AppendLine("            global::System.Runtime.InteropServices.Marshal.Copy(reply, 0, output, written);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        return 0;");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitCloseAndDispose(StringBuilder source)
    {
        source.AppendLine("    /// <summary>The payload half of the single Active-to-Closed transition. First caller wins.</summary>");
        source.AppendLine("    public int CloseLease(ulong leaseId, uint generation) => leases.Close(leaseId, generation);");
        source.AppendLine();
        source.AppendLine("    /// <summary>Ends every lease and tombstones every handle. Idempotent.</summary>");
        source.AppendLine("    public void Dispose()");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) { return; }");
        source.AppendLine("        leases.CloseAll();");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitCore(
        StringBuilder source,
        BridgeContractModel contract,
        string constants,
        string context,
        int maximumReply)
    {
        source.AppendLine("    /// <summary>The transport-neutral dispatch core.</summary>");
        source.AppendLine("    public int Dispatch(");
        source.AppendLine("        ulong leaseId,");
        source.AppendLine("        uint generation,");
        source.AppendLine("        ulong objectId,");
        source.AppendLine("        uint memberId,");
        source.AppendLine("        int outputCapacity,");
        source.AppendLine("        global::System.ReadOnlySpan<byte> request,");
        source.AppendLine("        global::System.Span<byte> reply,");
        source.AppendLine("        out int replyLength)");
        source.AppendLine("    {");
        source.AppendLine("        replyLength = 0;");
        source.AppendLine("        if (global::System.Threading.Volatile.Read(ref disposed) != 0)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        // 1. Structural validation. Nothing is dispatched from a frame that");
        source.AppendLine("        //    disagrees with itself or with its transport parameters.");
        source.Append("        if (!").Append(BridgeTypeNames.RequestHeader).AppendLine(".TryRead(request, out var header) ||");
        source.AppendLine("            header.MemberId != memberId ||");
        source.AppendLine("            header.ObjectId != objectId ||");
        source.AppendLine("            header.LeaseId != leaseId ||");
        source.AppendLine("            header.Generation != generation)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        if (header.FrameKind == ").Append(BridgeTypeNames.FrameKind).AppendLine(".Open)");
        source.AppendLine("        {");
        source.AppendLine("            return DispatchOpen(in header, request, reply, outputCapacity, out replyLength);");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        if (header.FrameKind == ").Append(BridgeTypeNames.FrameKind).Append(".Event || header.FrameKind == ")
            .Append(BridgeTypeNames.FrameKind).AppendLine(".ReliableEvent)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        if (header.FrameKind == ").Append(BridgeTypeNames.FrameKind).AppendLine(".Close)");
        source.AppendLine("        {");
        source.AppendLine("            return DispatchClose(in header, reply, outputCapacity, out replyLength);");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        if (header.FrameKind == ").Append(BridgeTypeNames.FrameKind).AppendLine(".Release)");
        source.AppendLine("        {");
        source.AppendLine("            return DispatchRelease(in header, reply, outputCapacity, out replyLength);");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        if (!").Append(constants).AppendLine(".TryGetMemberByOrdinal(memberId, out var entry) ||");
        source.AppendLine("            entry.ArgumentCount != header.ArgumentCount ||");
        source.Append("            (entry.Kind == global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.Event || ")
            .Append("entry.Kind == global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.ReliableEvent))").AppendLine();
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        // 2. Reply capacity is checked before dispatch, so a handler can never");
        source.AppendLine("        //    mutate and then fail on a buffer the caller sized too small.");
        source.AppendLine("        if (outputCapacity < entry.MaximumReplyBytes)");
        source.AppendLine("        {");
        source.AppendLine("            replyLength = entry.MaximumReplyBytes;");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".BufferTooSmall;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        // 3. Admission resolves the lease and the object handle atomically.");
        source.AppendLine("        if (!leases.TryAdmit(leaseId, generation, objectId, out var admission) ||");
        source.AppendLine("            admission.ObjectTypeId != entry.ObjectTypeId)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        // 4. Authorize this accessor independently. The declared permission is an");
        source.AppendLine("        //    input to the authorizer and never a substitute for it.");
        source.Append("        var context = new ").Append(context).AppendLine("(");
        source.AppendLine("            leaseId, generation, objectId, memberId, entry.Kind, entry.Permission, entry.Mutation);");
        source.AppendLine("        if (!authorizer.IsAuthorized(in context))");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        // 5. Decode arguments, dispatch, and encode the reply.");
        source.Append("        var __bridgeReader = new ").Append(BridgeTypeNames.Reader).Append("(request.Slice(")
            .Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize));");
        source.Append("        var __bridgeWriter = new ").Append(BridgeTypeNames.Writer).Append("(reply.Slice(")
            .Append(BridgeTypeNames.Wire).AppendLine(".ReplyHeaderSize));");
        source.AppendLine("        int status = DispatchMember(in admission, in context, memberId, ref __bridgeReader, ref __bridgeWriter);");
        source.AppendLine("        if (status != 0)");
        source.AppendLine("        {");
        source.AppendLine("            return status;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        if (!__bridgeReader.IsComplete || !__bridgeWriter.IsComplete)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        return CompleteReply(").Append(BridgeTypeNames.ReplyKind)
            .AppendLine(".Value, __bridgeWriter.Length, reply, out replyLength);");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitEvent(
        StringBuilder source,
        BridgeContractModel contract,
        string constants,
        string context)
    {
        source.AppendLine("    /// <summary>Dispatches a generated one-way event after DBC has copied it off the pump thread.</summary>");
        source.AppendLine("    public int DispatchEvent(global::System.ReadOnlySpan<byte> request)");
        source.AppendLine("    {");
        source.AppendLine("        if (global::System.Threading.Volatile.Read(ref disposed) != 0 ||");
        source.Append("            !").Append(BridgeTypeNames.RequestHeader).AppendLine(".TryRead(request, out var header) ||");
        source.Append("            (header.FrameKind != ").Append(BridgeTypeNames.FrameKind).Append(".Event && header.FrameKind != ")
            .Append(BridgeTypeNames.FrameKind).AppendLine(".ReliableEvent) ||");
        source.Append("            !").Append(constants).AppendLine(".TryGetMemberByOrdinal(header.MemberId, out var entry) ||");
        source.AppendLine("            (entry.Kind != global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.Event &&");
        source.AppendLine("             entry.Kind != global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.ReliableEvent) ||");
        source.AppendLine("            (header.FrameKind == global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeFrameKind.Event &&");
        source.AppendLine("             entry.Kind != global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.Event) ||");
        source.AppendLine("            (header.FrameKind == global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeFrameKind.ReliableEvent &&");
        source.AppendLine("             entry.Kind != global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.ReliableEvent) ||");
        source.AppendLine("            entry.ArgumentCount != header.ArgumentCount ||");
        source.AppendLine("            !leases.TryAdmit(header.LeaseId, header.Generation, header.ObjectId, out var admission) ||");
        source.AppendLine("            admission.ObjectTypeId != entry.ObjectTypeId)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var context = new ").Append(context).AppendLine("(");
        source.AppendLine("            header.LeaseId, header.Generation, header.ObjectId, header.MemberId, entry.Kind, entry.Permission, entry.Mutation);");
        source.AppendLine("        if (!authorizer.IsAuthorized(in context))");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var __bridgeReader = new ").Append(BridgeTypeNames.Reader).Append("(request.Slice(")
            .Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize));");
        source.AppendLine("        int status = DispatchEventMember(in admission, in context, header.MemberId, ref __bridgeReader);");
        source.AppendLine("        return status == 0 && __bridgeReader.IsComplete ? 0 :");
        source.Append("            status == 0 ? ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument : status;");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitClose(StringBuilder source, string context)
    {
        source.AppendLine("    private int DispatchClose(");
        source.Append("        in ").Append(BridgeTypeNames.RequestHeader).AppendLine(" header,");
        source.AppendLine("        global::System.Span<byte> reply,");
        source.AppendLine("        int outputCapacity,");
        source.AppendLine("        out int replyLength)");
        source.AppendLine("    {");
        source.AppendLine("        replyLength = 0;");
        source.Append("        const int CloseReplyBytes = ").Append(BridgeTypeNames.Wire).Append(".ReplyHeaderSize + ")
            .Append(BridgeTypeNames.Wire).AppendLine(".ValueHeaderSize;");
        source.AppendLine("        if (header.MemberId != 0U || header.ObjectId != 0UL || header.ArgumentCount != 0 || header.BodyLength != 0)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine("        if (outputCapacity < CloseReplyBytes)");
        source.AppendLine("        {");
        source.AppendLine("            replyLength = CloseReplyBytes;");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".BufferTooSmall;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var context = new ").Append(context).AppendLine("(");
        source.AppendLine("            header.LeaseId, header.Generation, 0UL, 0U,");
        source.AppendLine("            global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.Method,");
        source.AppendLine("            global::Devolutions.PowerShell.Ffi.LiveObjects.BridgePermission.Execute,");
        source.AppendLine("            global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMutation.None);");
        source.AppendLine("        if (!authorizer.IsAuthorized(in context))");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.AppendLine("        if (leases.Close(header.LeaseId, header.Generation) != 0)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.Append("        var __bridgeWriter = new ").Append(BridgeTypeNames.Writer).Append("(reply.Slice(")
            .Append(BridgeTypeNames.Wire).AppendLine(".ReplyHeaderSize));");
        source.AppendLine("        if (!__bridgeWriter.TryWriteNull() || !__bridgeWriter.IsComplete)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.Append("        return CompleteReply(").Append(BridgeTypeNames.ReplyKind)
            .AppendLine(".Value, __bridgeWriter.Length, reply, out replyLength);");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitOpen(StringBuilder source, BridgeContractModel contract, string constants)
    {
        source.AppendLine("    private int DispatchOpen(");
        source.Append("        in ").Append(BridgeTypeNames.RequestHeader).AppendLine(" header,");
        source.AppendLine("        global::System.ReadOnlySpan<byte> request,");
        source.AppendLine("        global::System.Span<byte> reply,");
        source.AppendLine("        int outputCapacity,");
        source.AppendLine("        out int replyLength)");
        source.AppendLine("    {");
        source.AppendLine("        replyLength = 0;");
        source.AppendLine("        if (header.MemberId != 0U || header.ObjectId != 0UL || header.LeaseId != 0UL ||");
        source.AppendLine("            header.Generation != 0U || header.ArgumentCount != 1)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        const int OpenReplyBytes = ").Append(BridgeTypeNames.Wire).Append(".ReplyHeaderSize + ")
            .Append(BridgeTypeNames.Wire).AppendLine(".ValueHeaderSize + 52;");
        source.AppendLine("        if (outputCapacity < OpenReplyBytes)");
        source.AppendLine("        {");
        source.AppendLine("            // Report the requirement before allocating, so a buffer probe cannot");
        source.AppendLine("            // consume a lease slot.");
        source.AppendLine("            replyLength = OpenReplyBytes;");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".BufferTooSmall;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var __bridgeReader = new ").Append(BridgeTypeNames.Reader).Append("(request.Slice(")
            .Append(BridgeTypeNames.Wire).AppendLine(".RequestHeaderSize));");
        source.AppendLine("        if (!__bridgeReader.TryReadBytes(32, out global::System.ReadOnlySpan<byte> payloadHash) || !__bridgeReader.IsComplete)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        if (!payloadHash.SequenceEqual(").Append(constants).AppendLine(".DescriptorHash))");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".ContractMismatch;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        int status = leases.TryOpen(").Append(constants).Append(".Object")
            .Append(BridgeTypeNames.Number(contract.RootObject.Id))
            .AppendLine(", root, out ulong leaseId, out uint generation, out ulong rootObjectId);");
        source.AppendLine("        if (status != 0)");
        source.AppendLine("        {");
        source.AppendLine("            return status;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        byte[] lease = new byte[52];");
        source.AppendLine("        global::System.Span<byte> leaseSpan = lease;");
        source.AppendLine("        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(leaseSpan, leaseId);");
        source.AppendLine("        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(leaseSpan.Slice(8), generation);");
        source.AppendLine("        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(leaseSpan.Slice(12), rootObjectId);");
        source.Append("        ").Append(constants).AppendLine(".DescriptorHash.CopyTo(leaseSpan.Slice(20));");
        source.Append("        var __bridgeWriter = new ").Append(BridgeTypeNames.Writer).Append("(reply.Slice(")
            .Append(BridgeTypeNames.Wire).AppendLine(".ReplyHeaderSize));");
        source.AppendLine("        // A lease that is allocated but whose reply never reaches the payload");
        source.AppendLine("        // would be unreachable forever, and the one-lease rule would then reject");
        source.AppendLine("        // every later open. Roll it back on any failure after allocation.");
        source.AppendLine("        if (!__bridgeWriter.TryWriteBytes(lease, 52) || !__bridgeWriter.IsComplete)");
        source.AppendLine("        {");
        source.AppendLine("            leases.Close(leaseId, generation);");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        int completed = CompleteReply(").Append(BridgeTypeNames.ReplyKind)
            .AppendLine(".Value, __bridgeWriter.Length, reply, out replyLength);");
        source.AppendLine("        if (completed != 0)");
        source.AppendLine("        {");
        source.AppendLine("            leases.Close(leaseId, generation);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        return completed;");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitRelease(StringBuilder source, BridgeContractModel contract, string constants, string context)
    {
        source.AppendLine("    private int DispatchRelease(");
        source.Append("        in ").Append(BridgeTypeNames.RequestHeader).AppendLine(" header,");
        source.AppendLine("        global::System.Span<byte> reply,");
        source.AppendLine("        int outputCapacity,");
        source.AppendLine("        out int replyLength)");
        source.AppendLine("    {");
        source.AppendLine("        replyLength = 0;");
        source.Append("        const int ReleaseReplyBytes = ").Append(BridgeTypeNames.Wire).Append(".ReplyHeaderSize + ")
            .Append(BridgeTypeNames.Wire).AppendLine(".ValueHeaderSize;");
        source.AppendLine("        if (header.ArgumentCount != 0 || header.BodyLength != 0)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        if (!").Append(constants).AppendLine(".TryGetReleaseObjectType(header.MemberId, out ulong objectTypeId))");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        if (outputCapacity < ReleaseReplyBytes)");
        source.AppendLine("        {");
        source.AppendLine("            replyLength = ReleaseReplyBytes;");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".BufferTooSmall;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        if (!leases.TryAdmit(header.LeaseId, header.Generation, header.ObjectId, out var admission) ||");
        source.AppendLine("            admission.ObjectTypeId != objectTypeId)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        var context = new ").Append(context).AppendLine("(");
        source.AppendLine("            header.LeaseId, header.Generation, header.ObjectId, header.MemberId,");
        source.AppendLine("            global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind.Method,");
        source.AppendLine("            global::Devolutions.PowerShell.Ffi.LiveObjects.BridgePermission.Execute,");
        source.AppendLine("            global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMutation.None);");
        source.AppendLine("        if (!authorizer.IsAuthorized(in context))");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        switch (objectTypeId)");
        source.AppendLine("        {");
        foreach (BridgeObjectModel model in contract.Objects)
        {
            source.Append("            case ").Append(BridgeTypeNames.Number(model.Id)).AppendLine("UL:");
            source.Append("                ((").Append(BridgeNames.Handler(model)).AppendLine(")admission.Handler!).Release(in context);");
            source.AppendLine("                break;");
        }

        source.Append("            default: return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        leases.TryRelease(header.LeaseId, header.Generation, header.ObjectId);");
        source.Append("        var __bridgeWriter = new ").Append(BridgeTypeNames.Writer).Append("(reply.Slice(")
            .Append(BridgeTypeNames.Wire).AppendLine(".ReplyHeaderSize));");
        source.AppendLine("        if (!__bridgeWriter.TryWriteNull() || !__bridgeWriter.IsComplete)");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        return CompleteReply(").Append(BridgeTypeNames.ReplyKind)
            .AppendLine(".Value, __bridgeWriter.Length, reply, out replyLength);");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitMemberDispatch(
        StringBuilder source,
        BridgeContractModel contract,
        string constants,
        string context)
    {
        source.AppendLine("    private int DispatchMember(");
        source.AppendLine("        in global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeAdmission admission,");
        source.Append("        in ").Append(context).AppendLine(" context,");
        source.AppendLine("        uint memberId,");
        source.Append("        ref ").Append(BridgeTypeNames.Reader).AppendLine(" __bridgeReader,");
        source.Append("        ref ").Append(BridgeTypeNames.Writer).AppendLine(" __bridgeWriter)");
        source.AppendLine("    {");
        source.AppendLine("        switch (memberId)");
        source.AppendLine("        {");
        foreach (BridgeObjectModel model in contract.Objects)
        {
            foreach (BridgeMemberModel member in model.Members)
            {
                if (member.Kind is BridgeRecordKind.Event or BridgeRecordKind.ReliableEvent)
                {
                    continue;
                }

                EmitMemberCase(source, contract, model, member, constants);
            }
        }

        source.Append("            default: return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitEventMemberDispatch(
        StringBuilder source,
        BridgeContractModel contract,
        string context)
    {
        source.AppendLine("    private int DispatchEventMember(");
        source.AppendLine("        in global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeAdmission admission,");
        source.Append("        in ").Append(context).AppendLine(" context,");
        source.AppendLine("        uint memberId,");
        source.Append("        ref ").Append(BridgeTypeNames.Reader).AppendLine(" __bridgeReader)");
        source.AppendLine("    {");
        source.AppendLine("        switch (memberId)");
        source.AppendLine("        {");
        foreach (BridgeObjectModel model in contract.Objects)
        {
            foreach (BridgeMemberModel member in model.Members)
            {
                if (member.Kind is not (BridgeRecordKind.Event or BridgeRecordKind.ReliableEvent))
                {
                    continue;
                }

                const string Indent = "                ";
                source.Append("            case ").Append(BridgeNames.Contract(contract)).Append(".Member")
                    .Append(BridgeTypeNames.Number(member.Ordinal)).AppendLine(":");
                source.AppendLine("            {");
                source.Append(Indent).Append("var __bridgeTarget = (").Append(BridgeNames.Handler(model))
                    .AppendLine(")admission.Handler!;");
                int temp = 0;
                var arguments = new List<string>();
                for (int index = 0; index < member.Parameters.Count; index++)
                {
                    BridgeParameterModel parameter = member.Parameters[index];
                    string local = "__bridgeArgument" + BridgeTypeNames.Number(index);
                    arguments.Add(local);
                    source.Append(Indent).Append(BridgeTypeNames.Full(parameter.Type, contract, payload: false)).Append(' ')
                        .Append(local).AppendLine(" = default!;");
                    BridgeCodecEmitter.EmitRead(
                        source,
                        contract,
                        parameter.Type,
                        local,
                        Indent,
                        "return " + BridgeTypeNames.Status + ".InvalidArgument;",
                        payload: false,
                        ref temp);
                }

                source.Append(Indent).Append("__bridgeTarget.On").Append(BridgeNames.Identifier(member.Name))
                    .Append("(in context")
                    .Append(arguments.Count == 0 ? string.Empty : ", " + string.Join(", ", arguments))
                    .AppendLine(");");
                source.AppendLine(Indent + "return 0;");
                source.AppendLine("            }");
                source.AppendLine();
            }
        }

        source.Append("            default: return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void EmitMemberCase(
        StringBuilder source,
        BridgeContractModel contract,
        BridgeObjectModel model,
        BridgeMemberModel member,
        string constants)
    {
        const string Indent = "                ";
        string fail = "return " + BridgeTypeNames.Status + ".InvalidArgument;";
        string handler = BridgeNames.Handler(model);
        source.Append("            case ").Append(constants).Append(".Member").Append(BridgeTypeNames.Number(member.Ordinal)).AppendLine(":");
        source.AppendLine("            {");
        source.Append(Indent).Append("var __bridgeTarget = (").Append(handler).AppendLine(")admission.Handler!;");
        if (model.FiniteOperation is { } operation && member.Ordinal == operation.CancelMemberId)
        {
            source.Append(Indent).Append("if (!__bridgeReader.IsComplete) ").AppendLine(fail);
            source.Append(Indent).AppendLine("var __bridgeCancel = leases.TryBeginFiniteOperationCancel(");
            source.Append(Indent).AppendLine("    admission.LeaseId, admission.Generation, admission.ObjectId);");
            source.Append(Indent).Append("if (__bridgeCancel == global::Devolutions.PowerShell.Ffi.LiveObjects.")
                .AppendLine("PowerShellBridgeFiniteOperationCancelResult.AlreadyCancelled)");
            source.Append(Indent).AppendLine("{");
            BridgeCodecEmitter.Guard(source, Indent + "    ", "__bridgeWriter.TryWriteNull()", fail);
            source.Append(Indent).AppendLine("    return 0;");
            source.Append(Indent).AppendLine("}");
            source.Append(Indent).Append("if (__bridgeCancel != global::Devolutions.PowerShell.Ffi.LiveObjects.")
                .AppendLine("PowerShellBridgeFiniteOperationCancelResult.InvokeHandler)");
            source.Append(Indent).AppendLine("{");
            source.Append(Indent).Append("    return ").Append(BridgeTypeNames.Status).AppendLine(".AccessDenied;");
            source.Append(Indent).AppendLine("}");
        }

        int temp = 0;
        var arguments = new List<string>();
        for (int index = 0; index < member.Parameters.Count; index++)
        {
            BridgeParameterModel parameter = member.Parameters[index];
            string local = "__bridgeArgument" + BridgeTypeNames.Number(index);
            arguments.Add(local);
            source.Append(Indent).Append(BridgeTypeNames.Full(parameter.Type, contract, payload: false)).Append(' ')
                .Append(local).AppendLine(" = default!;");
            BridgeCodecEmitter.EmitRead(source, contract, parameter.Type, local, Indent, fail, payload: false, ref temp);
        }

        string call = member.Kind switch
        {
            BridgeRecordKind.Getter => "__bridgeTarget.Get" + BridgeNames.Identifier(member.Name) + "(in context)",
            BridgeRecordKind.Setter => "__bridgeTarget.Set" + BridgeNames.Identifier(member.Name) + "(in context, " + arguments[0] + ")",
            _ => "__bridgeTarget." + BridgeNames.Identifier(MethodName(member)) + "(in context" +
                 (arguments.Count == 0 ? string.Empty : ", " + string.Join(", ", arguments)) + ")",
        };

        bool isVoid = member.Result.Tag == BridgeTag.Null && !member.Result.IsNullable;
        if (isVoid)
        {
            source.Append(Indent).Append(call).AppendLine(";");
            BridgeCodecEmitter.Guard(source, Indent, "__bridgeWriter.TryWriteNull()", fail);
        }
        else
        {
            source.Append(Indent).Append(BridgeTypeNames.Full(member.Result, contract, payload: false))
                .Append(" __bridgeResult = ").Append(call).AppendLine(";");
            BridgeCodecEmitter.EmitWrite(source, contract, member.Result, "__bridgeResult", Indent, fail, payload: false, ref temp);
        }

        source.Append(Indent).AppendLine("return 0;");
        source.AppendLine("            }");
        source.AppendLine();
    }

    private static void EmitHandleHelpers(StringBuilder source, BridgeContractModel contract)
    {
        foreach (BridgeObjectModel model in contract.Objects)
        {
            string handler = BridgeNames.Handler(model);
            string id = BridgeTypeNames.Number(model.Id);
            source.AppendLine("    /// <summary>Registers an application handler and returns its lease-scoped identifier.</summary>");
            source.Append("    private ulong Register").Append(id)
                .AppendLine("(in global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeAdmission admission, " + handler + " handler)");
            source.AppendLine("    {");
            if (model.FiniteOperation is { } operation)
            {
                source.Append("        return leases.RegisterFiniteOperation(admission.LeaseId, admission.Generation, admission.ObjectId, ")
                    .Append(id).Append("UL, handler, ").Append(BridgeTypeNames.Number(operation.MaximumLifetimeMilliseconds))
                    .AppendLine(");");
            }
            else
            {
                source.Append("        return leases.Register(admission.LeaseId, admission.Generation, ").Append(id).AppendLine("UL, handler);");
            }
            source.AppendLine("    }");
            source.AppendLine();
            source.AppendLine("    /// <summary>Resolves an inbound handle within its own lease. A forged, stale, or cross-lease handle fails here.</summary>");
            source.Append("    private bool TryResolve").Append(id)
                .Append("(in global::Devolutions.PowerShell.Ffi.LiveObjects.PowerShellBridgeAdmission admission, ulong objectId, out ")
                .Append(handler).AppendLine(" handler)");
            source.AppendLine("    {");
            source.AppendLine("        if (leases.TryAdmit(admission.LeaseId, admission.Generation, objectId, out var resolved) &&");
            source.Append("            resolved.ObjectTypeId == ").Append(id).Append("UL && resolved.Handler is ").Append(handler).AppendLine(" typed)");
            source.AppendLine("        {");
            source.AppendLine("            handler = typed;");
            source.AppendLine("            return true;");
            source.AppendLine("        }");
            source.AppendLine();
            source.AppendLine("        handler = default!;");
            source.AppendLine("        return false;");
            source.AppendLine("    }");
            source.AppendLine();
        }
    }

    private static void EmitReplyHelpers(StringBuilder source, BridgeContractModel contract)
    {
        source.AppendLine("    private static int CompleteReply(byte replyKind, int bodyLength, global::System.Span<byte> reply, out int replyLength)");
        source.AppendLine("    {");
        source.AppendLine("        replyLength = 0;");
        source.Append("        var header = new ").Append(BridgeTypeNames.ReplyHeader).AppendLine("(replyKind, bodyLength);");
        source.AppendLine("        if (!header.TryWrite(reply))");
        source.AppendLine("        {");
        source.Append("            return ").Append(BridgeTypeNames.Status).AppendLine(".InvalidArgument;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        replyLength = ").Append(BridgeTypeNames.Wire).AppendLine(".ReplyHeaderSize + bodyLength;");
        source.AppendLine("        return 0;");
        source.AppendLine("    }");
    }

    private static int MaximumReply(BridgeContractModel contract)
    {
        int maximum = BridgeLimits.ReplyHeaderSize + BridgeLimits.ValueHeaderSize + 52;
        foreach (BridgeObjectModel model in contract.Objects)
        {
            foreach (BridgeMemberModel member in model.Members)
            {
                int value = member.MaximumReplyBytes(contract.DataById);
                if (value > maximum)
                {
                    maximum = value;
                }
            }
        }

        return maximum;
    }

    private static int MaximumRequest(BridgeContractModel contract)
    {
        int maximum = BridgeLimits.RequestHeaderSize + BridgeLimits.ValueHeaderSize + 32;
        foreach (BridgeObjectModel model in contract.Objects)
        {
            foreach (BridgeMemberModel member in model.Members)
            {
                int value = member.MaximumRequestBytes(contract.DataById);
                if (value > maximum)
                {
                    maximum = value;
                }
            }
        }

        return maximum;
    }

    private static string MethodName(BridgeMemberModel member) =>
        member.Name == "get_Item" ? "GetAt" : member.Name;
}
