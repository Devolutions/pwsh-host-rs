#nullable enable

using Microsoft.CodeAnalysis;

namespace Devolutions.MultiPwsh.LiveContract.Generator;

/// <summary>
/// Diagnostics for the closed Bridge Contract v2 compiler. Every rule listed
/// here must also appear in <c>AnalyzerReleases.Unshipped.md</c>, because
/// <c>EnforceExtendedAnalyzerRules</c> turns an unlisted rule into RS2008.
/// </summary>
internal static class BridgeContractDiagnostics
{
    private const string Category = "LiveContract";

    internal static readonly DiagnosticDescriptor MissingMode = new(
        "MPWLC011",
        "Bridge contract mode is required",
        "Set LiveContractMode to Host or Payload when compiling a [BridgeContract] declaration",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidContract = new(
        "MPWLC012",
        "Invalid bridge contract",
        "Bridge contract '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidObject = new(
        "MPWLC013",
        "Invalid bridge object",
        "Bridge object '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidMember = new(
        "MPWLC014",
        "Invalid bridge member",
        "Bridge member '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidSetter = new(
        "MPWLC015",
        "Invalid bridge setter",
        "Bridge setter for '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor MissingBound = new(
        "MPWLC016",
        "Bridge member requires an explicit bound",
        "Bridge position '{0}' requires an explicit bound: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedType = new(
        "MPWLC017",
        "Unsupported bridge type",
        "Bridge position '{0}' uses an unsupported type: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnresolvedObject = new(
        "MPWLC018",
        "Unresolved bridge object reference",
        "Bridge position '{0}' does not resolve to a declared bridge object: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidData = new(
        "MPWLC019",
        "Invalid bridge data contract",
        "Bridge data contract '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidEnum = new(
        "MPWLC020",
        "Invalid bridge enumeration",
        "Bridge enumeration '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidEvent = new(
        "MPWLC021",
        "Invalid bridge event",
        "Bridge event '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ExceededLimit = new(
        "MPWLC022",
        "Bridge contract exceeds a structural or frame-size limit",
        "Bridge contract element '{0}' exceeds a limit: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidAuthorization = new(
        "MPWLC023",
        "Invalid bridge mutation or authorization metadata",
        "Bridge member '{0}' has invalid mutation or authorization metadata: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidRelease = new(
        "MPWLC024",
        "Invalid bridge release ordinal",
        "Bridge object '{0}' has an invalid release ordinal: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidFiniteOperation = new(
        "MPWLC025",
        "Invalid finite operation or snapshot page",
        "Bridge declaration '{0}' has an invalid finite operation or snapshot page shape: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
