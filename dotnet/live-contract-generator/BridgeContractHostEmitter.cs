#nullable enable

using System.Text;

namespace Devolutions.MultiPwsh.LiveContract.Generator;

/// <summary>
/// Emits the consumer-side typed handler interfaces and the call context they
/// receive. The generated dispatcher that decodes frames, revalidates the lease,
/// and authorizes each accessor is delivered with the lease runtime; these
/// interfaces are its stable typed surface.
/// </summary>
internal static class BridgeContractHostEmitter
{
    internal static void Emit(StringBuilder source, BridgeContractModel contract)
    {
        EmitContext(source, contract);
        foreach (BridgeObjectModel model in contract.Objects)
        {
            EmitHandler(source, contract, model);
        }
    }

    private static void EmitContext(StringBuilder source, BridgeContractModel contract)
    {
        source.AppendLine("/// <summary>");
        source.AppendLine("/// The revalidated identity of one bridge call. It is passed to every handler");
        source.AppendLine("/// so an application authorizes each getter, setter, and method independently.");
        source.AppendLine("/// </summary>");
        source.Append("public readonly struct ").Append(BridgeNames.Prefix(contract.Root)).AppendLine("CallContext");
        source.AppendLine("{");
        source.Append("    public ").Append(BridgeNames.Prefix(contract.Root)).AppendLine("CallContext(");
        source.AppendLine("        ulong leaseId,");
        source.AppendLine("        uint generation,");
        source.AppendLine("        ulong objectId,");
        source.AppendLine("        uint memberId,");
        source.AppendLine("        global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind kind,");
        source.AppendLine("        global::Devolutions.PowerShell.Ffi.LiveObjects.BridgePermission declaredPermission,");
        source.AppendLine("        global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMutation declaredMutation)");
        source.AppendLine("    {");
        source.AppendLine("        LeaseId = leaseId;");
        source.AppendLine("        Generation = generation;");
        source.AppendLine("        ObjectId = objectId;");
        source.AppendLine("        MemberId = memberId;");
        source.AppendLine("        Kind = kind;");
        source.AppendLine("        DeclaredPermission = declaredPermission;");
        source.AppendLine("        DeclaredMutation = declaredMutation;");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    public ulong LeaseId { get; }");
        source.AppendLine();
        source.AppendLine("    public uint Generation { get; }");
        source.AppendLine();
        source.AppendLine("    public ulong ObjectId { get; }");
        source.AppendLine();
        source.AppendLine("    public uint MemberId { get; }");
        source.AppendLine();
        source.AppendLine("    /// <summary>Whether this call is a getter, a setter, a method, or an event.</summary>");
        source.AppendLine("    public global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMemberKind Kind { get; }");
        source.AppendLine();
        source.AppendLine("    /// <summary>Declared permission metadata. It is an input to the authorizer, never a decision.</summary>");
        source.AppendLine("    public global::Devolutions.PowerShell.Ffi.LiveObjects.BridgePermission DeclaredPermission { get; }");
        source.AppendLine();
        source.AppendLine("    public global::Devolutions.PowerShell.Ffi.LiveObjects.BridgeMutation DeclaredMutation { get; }");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("/// <summary>");
        source.AppendLine("/// Authorizes one bridge call. It is consulted for every getter, every setter,");
        source.AppendLine("/// and every method, immediately before the application handler runs.");
        source.AppendLine("/// </summary>");
        source.Append("public interface I").Append(BridgeNames.Prefix(contract.Root)).AppendLine("Authorizer");
        source.AppendLine("{");
        source.Append("    bool IsAuthorized(in ").Append(BridgeNames.Prefix(contract.Root)).AppendLine("CallContext context);");
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitHandler(StringBuilder source, BridgeContractModel contract, BridgeObjectModel model)
    {
        string context = BridgeNames.Prefix(contract.Root) + "CallContext";
        source.Append("/// <summary>The application handler for bridge object ")
            .Append(BridgeTypeNames.Number(model.Id)).AppendLine(".</summary>");
        source.Append("public interface ").Append(BridgeNames.Handler(model)).AppendLine();
        source.AppendLine("{");
        foreach (BridgeMemberModel member in model.Members)
        {
            switch (member.Kind)
            {
                case BridgeRecordKind.Getter:
                    source.Append("    ").Append(BridgeTypeNames.Full(member.Result, contract, payload: false)).Append(" Get")
                        .Append(BridgeNames.Identifier(member.Name)).Append("(in ").Append(context).AppendLine(" context);");
                    break;
                case BridgeRecordKind.Setter:
                    source.Append("    void Set").Append(BridgeNames.Identifier(member.Name)).Append("(in ").Append(context)
                        .Append(" context, ").Append(BridgeTypeNames.Full(member.Parameters[0].Type, contract, payload: false))
                        .AppendLine(" value);");
                    break;
                case BridgeRecordKind.Method:
                {
                    string result = member.Result.Tag == BridgeTag.Null && !member.Result.IsNullable
                        ? "void"
                        : BridgeTypeNames.Full(member.Result, contract, payload: false);
                    source.Append("    ").Append(result).Append(' ').Append(BridgeNames.Identifier(MethodName(member)))
                        .Append("(in ").Append(context).Append(" context");
                    EmitParameters(source, contract, member);
                    source.AppendLine(");");
                    break;
                }

                case BridgeRecordKind.Event:
                    source.Append("    void On").Append(BridgeNames.Identifier(member.Name)).Append("(in ").Append(context)
                        .Append(" context");
                    EmitParameters(source, contract, member);
                    source.AppendLine(");");
                    break;
                default:
                    break;
            }
        }

        source.Append("    /// <summary>Releases the handle identified by ").Append("<c>context.ObjectId</c>").AppendLine(".</summary>");
        source.Append("    void Release(in ").Append(context).AppendLine(" context);");
        source.AppendLine("}");
        source.AppendLine();
    }

    private static void EmitParameters(StringBuilder source, BridgeContractModel contract, BridgeMemberModel member)
    {
        foreach (BridgeParameterModel parameter in member.Parameters)
        {
            source.Append(", ").Append(BridgeTypeNames.Full(parameter.Type, contract, payload: false)).Append(' ')
                .Append(BridgeNames.Identifier(parameter.Name));
        }
    }

    private static string MethodName(BridgeMemberModel member) =>
        member.Name == "get_Item" ? "GetAt" : member.Name;
}
