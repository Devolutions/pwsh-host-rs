using Devolutions.MultiPwsh.LiveContract.Generator;
using Devolutions.PowerShell.Ffi.LiveObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// Fixtures for the closed Bridge Contract v2 compiler. Every negative case here
/// asserts that an unsupported declaration fails at generator compile time with
/// an actionable diagnostic rather than at runtime.
/// </summary>
internal static class BridgeContractTests
{
    internal const string Valid = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;
        using Devolutions.PowerShell.Ffi.LiveObjects;

        namespace Fixture;

        [GeneratedComInterface]
        [Guid("2C7E8A11-6B44-4E27-9F0A-0C6C0F53D8E1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public partial interface ISampleTransport
        {
            [PreserveSig]
            int Invoke(ulong leaseId, uint generation, ulong objectId, uint memberId,
                       nint input, int inputLength, nint output, int outputCapacity, out int outputLength);

            [PreserveSig]
            int CloseLease(ulong leaseId, uint generation);
        }

        [BridgeContract("7A1D66C8-9E2B-4C55-8D3F-1B7C4F2A9E10", 1, 0, "2C7E8A11-6B44-4E27-9F0A-0C6C0F53D8E1")]
        [BridgeObject(1, ReleaseId = 900)]
        public interface ISampleRoot
        {
            [BridgeMember(1, Permission = BridgePermission.Read, MaximumUtf8Bytes = 256)]
            string ProductVersion { get; }

            [BridgeMember(2, Permission = BridgePermission.Read, MaximumCollectionCount = 64, MaximumUtf8Bytes = 128)]
            IReadOnlyList<string> Tags { get; }

            [BridgeMember(3, Permission = BridgePermission.Execute, ResultObjectId = 2)]
            ISampleChild? FindChild([BridgeBound(MaximumUtf8Bytes = 128)] string name);

            [BridgeMember(4, Permission = BridgePermission.Read, MaximumCollectionCount = 32, ResultObjectId = 2)]
            ISampleChild this[int index] { get; }

            [BridgeMember(5, Permission = BridgePermission.Read)]
            SampleState State { get; }

            [BridgeMember(6, Permission = BridgePermission.Read)]
            ISampleFailure? LastFailure { get; }

            [BridgeEvent(500, OrderingKey = 7)]
            void ReportProgress(int percent);
        }

        [BridgeObject(2, ReleaseId = 901)]
        public interface ISampleChild
        {
            [BridgeMember(10, SetterId = 11,
                Permission = BridgePermission.Read,
                SetterPermission = BridgePermission.Write,
                SetterMutation = BridgeMutation.Direct,
                MaximumUtf8Bytes = 256)]
            string Name { get; set; }

            [BridgeMember(12, Permission = BridgePermission.Read)]
            long? Size { get; }
        }

        [BridgeData(70)]
        public interface ISampleFailure
        {
            [BridgeField(1, MaximumUtf8Bytes = 256)]
            string Reason { get; }

            [BridgeField(2)]
            int Code { get; }
        }

        [BridgeEnum(80)]
        public enum SampleState
        {
            Closed = 0,
            Open = 1,
        }
        """;

    internal static void Run(
        Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run,
        Action<IEnumerable<Diagnostic>> assertNoErrors)
    {
        VerifySurface(run, assertNoErrors, "Payload", "class SampleRootBridge");
        VerifySurface(run, assertNoErrors, "Payload", "public string ProductVersion");
        VerifySurface(run, assertNoErrors, "Payload", "public void ReportProgress(int percent)");
        VerifySurface(run, assertNoErrors, "Host", "interface ISampleRootBridgeHandler");
        VerifySurface(run, assertNoErrors, "Host", "interface ISampleRootAuthorizer");
        VerifySurface(run, assertNoErrors, "Host", "class SampleFailureValue");
        VerifyDescriptorParity(run, assertNoErrors);
        VerifyNoDynamicDispatch(run, assertNoErrors);
        VerifyReleaseOrdinals(run, assertNoErrors);

        // Every rejection is a compile-time diagnostic, never a runtime failure.
        VerifyDiagnostic(run, Valid.Replace(", MaximumUtf8Bytes = 256", string.Empty, StringComparison.Ordinal), "MPWLC016");
        VerifyDiagnostic(run, Valid.Replace(", MaximumCollectionCount = 64", string.Empty, StringComparison.Ordinal), "MPWLC016");
        VerifyDiagnostic(run, Valid.Replace("string ProductVersion { get; }", "object ProductVersion { get; }", StringComparison.Ordinal), "MPWLC017");
        VerifyDiagnostic(run, Valid.Replace("string ProductVersion { get; }", "System.Threading.Tasks.Task ProductVersion { get; }", StringComparison.Ordinal), "MPWLC017");
        VerifyDiagnostic(run, Valid.Replace("string ProductVersion { get; }", "decimal ProductVersion { get; }", StringComparison.Ordinal), "MPWLC017");
        VerifyDiagnostic(run, Valid.Replace("string ProductVersion { get; }", "System.DateTime ProductVersion { get; }", StringComparison.Ordinal), "MPWLC017");
        VerifyDiagnostic(run, Valid.Replace("string ProductVersion { get; }", "string[] ProductVersion { get; }", StringComparison.Ordinal), "MPWLC017");
        VerifyDiagnostic(run, Valid.Replace("string ProductVersion { get; }", "System.Security.SecureString ProductVersion { get; }", StringComparison.Ordinal), "MPWLC017");
        VerifyDiagnostic(run, Valid.Replace("[BridgeObject(1, ReleaseId = 900)]", "[BridgeObject(1)]", StringComparison.Ordinal), "MPWLC024");
        VerifyDiagnostic(run, Valid.Replace("[BridgeObject(2, ReleaseId = 901)]", "[BridgeObject(2, ReleaseId = 900)]", StringComparison.Ordinal), "MPWLC024");
        VerifyDiagnostic(run, Valid.Replace("[BridgeMember(1, Permission = BridgePermission.Read, MaximumUtf8Bytes = 256)]", "[BridgeMember(1, MaximumUtf8Bytes = 256)]", StringComparison.Ordinal), "MPWLC023");
        VerifyDiagnostic(run, Valid.Replace("SetterPermission = BridgePermission.Write", "SetterPermission = BridgePermission.Read", StringComparison.Ordinal), "MPWLC023");
        VerifyDiagnostic(run, Valid.Replace("SetterMutation = BridgeMutation.Direct", "SetterMutation = BridgeMutation.Staged", StringComparison.Ordinal), "MPWLC023");
        VerifyDiagnostic(run, Valid.Replace("public enum SampleState", "[Flags] public enum SampleState", StringComparison.Ordinal), "MPWLC020");
        VerifyDiagnostic(run, Valid.Replace("public enum SampleState", "public enum SampleState : long", StringComparison.Ordinal), "MPWLC020");
        VerifyDiagnostic(run, Valid.Replace("void ReportProgress(int percent);", "int ReportProgress(int percent);", StringComparison.Ordinal), "MPWLC021");
        VerifyDiagnostic(run, Valid.Replace("MaximumCollectionCount = 64, MaximumUtf8Bytes = 128", "MaximumCollectionCount = 4096, MaximumUtf8Bytes = 8192", StringComparison.Ordinal), "MPWLC022");
        VerifyDiagnostic(run, Valid.Replace("[Guid(\"2C7E8A11-6B44-4E27-9F0A-0C6C0F53D8E1\")]", "[Guid(\"11111111-2222-3333-4444-555555555555\")]", StringComparison.Ordinal), "MPWLC012");
        VerifyDiagnostic(run, Valid.Replace("public interface ISampleChild", "public interface ISampleChild : System.IDisposable", StringComparison.Ordinal), "MPWLC013");
        VerifyDiagnostic(run, Valid.Replace("int Code { get; }", "ISampleChild Code { get; }", StringComparison.Ordinal), "MPWLC019");
        VerifyDiagnostic(run, Valid.Replace("[BridgeMember(12, Permission = BridgePermission.Read)]", string.Empty, StringComparison.Ordinal), "MPWLC014");
        VerifyDiagnostic(run, Valid + "\n[BridgeObject(3, ReleaseId = 902)]\npublic interface ISampleOrphan { }\n", "MPWLC013");
        VerifyMissingMode(run);
        VerifyMixedFamilies(run);
    }

    private static void VerifySurface(
        Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run,
        Action<IEnumerable<Diagnostic>> assertNoErrors,
        string mode,
        string required)
    {
        var (result, output) = run(Valid, mode);
        assertNoErrors(Diagnostics(result));
        assertNoErrors(output.GetDiagnostics());
        string generated = Generated(result);
        if (!generated.Contains(required, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The {mode} bridge output does not contain '{required}'.");
        }
    }

    private static void VerifyDescriptorParity(
        Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run,
        Action<IEnumerable<Diagnostic>> assertNoErrors)
    {
        string host = ExtractHash(run, assertNoErrors, "Host");
        string payload = ExtractHash(run, assertNoErrors, "Payload");
        if (host.Length != 64 || !string.Equals(host, payload, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Host and Payload descriptor hashes must match byte for byte: '{host}' vs '{payload}'.");
        }
    }

    private static string ExtractHash(
        Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run,
        Action<IEnumerable<Diagnostic>> assertNoErrors,
        string mode)
    {
        var (result, output) = run(Valid, mode);
        assertNoErrors(Diagnostics(result));
        assertNoErrors(output.GetDiagnostics());
        string generated = Generated(result);
        const string marker = "DescriptorHashHex = \"";
        int start = generated.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"The {mode} bridge output does not emit a descriptor hash.");
        }

        start += marker.Length;
        int end = generated.IndexOf('"', start);
        return generated.Substring(start, end - start);
    }

    private static void VerifyNoDynamicDispatch(
        Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run,
        Action<IEnumerable<Diagnostic>> assertNoErrors)
    {
        foreach (string mode in new[] { "Host", "Payload" })
        {
            var (result, output) = run(Valid, mode);
            assertNoErrors(Diagnostics(result));
            string generated = Generated(result);
            foreach (string banned in new[] { "System.Reflection", "IDispatch", "System.Text.Json", "JsonSerializer", "Activator.CreateInstance", "GetType()" })
            {
                if (generated.Contains(banned, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The {mode} bridge output must not contain '{banned}'.");
                }
            }

            foreach (string token in new[] { " dynamic ", "(dynamic)", "dynamic " })
            {
                if (generated.Contains(token, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The {mode} bridge output must not use dynamic dispatch.");
                }
            }
        }
    }

    private static void VerifyReleaseOrdinals(
        Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run,
        Action<IEnumerable<Diagnostic>> assertNoErrors)
    {
        var (result, output) = run(Valid, "Payload");
        assertNoErrors(Diagnostics(result));
        assertNoErrors(output.GetDiagnostics());
        string generated = Generated(result);
        foreach (string required in new[] { "Release1 = 900U", "Release2 = 901U", "TryGetReleaseOrdinal", "public void Release()" })
        {
            if (!generated.Contains(required, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The bridge output must emit explicit release ordinals: missing '{required}'.");
            }
        }
    }

    private static void VerifyMissingMode(Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run)
    {
        var (result, output) = run(Valid, string.Empty);
        if (!Diagnostics(result).Any(diagnostic => diagnostic.Id == "MPWLC011"))
        {
            throw new InvalidOperationException("A bridge contract without an explicit mode must report MPWLC011.");
        }
    }

    private static void VerifyMixedFamilies(Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run)
    {
        const string legacy = """

            [LiveContract("A1E95B5C-9E8E-4A3D-B6F2-93F81C51B19E", 1, 0, "5BFBF6D7-7BFA-4C15-8D35-02B665F39A18")]
            [LiveObject(1000)]
            public interface ILegacyRoot
            {
            }
            """;
        var (result, output) = run(Valid + legacy, "Host");
        if (!Diagnostics(result).Any(diagnostic => diagnostic.Id == "MPWLC012"))
        {
            throw new InvalidOperationException("Declaring both a v1 and a v2 root must report MPWLC012.");
        }
    }

    private static void VerifyDiagnostic(
        Func<string, string, (GeneratorDriverRunResult Result, Compilation Output)> run,
        string source,
        string expected)
    {
        if (string.Equals(source, Valid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The fixture for {expected} did not change the valid contract.");
        }

        var (result, output) = run(source, "Host");
        Diagnostic[] reported = Diagnostics(result).ToArray();
        if (!reported.Any(diagnostic => diagnostic.Id == expected))
        {
            string actual = reported.Length == 0
                ? "none"
                : string.Join(", ", reported.Select(diagnostic => diagnostic.Id).Distinct());
            throw new InvalidOperationException($"Expected {expected} but the generator reported {actual}.");
        }
    }

    private static IEnumerable<Diagnostic> Diagnostics(GeneratorDriverRunResult result) =>
        result.Results.SelectMany(static generator => generator.Diagnostics);

    private static string Generated(GeneratorDriverRunResult result) =>
        string.Join(Environment.NewLine, result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
}



