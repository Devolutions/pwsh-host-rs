# Devolutions.MultiPwsh.Sdk

Experimental `net8.0` NativeAOT facade for an in-process PowerShell payload
hosted by the `multi-pwsh-sdk.dll` Rust library.
Its public C# types remain in the `Devolutions.PowerShell.Ffi` namespace.

This package ships only the `win-x64` native FFI asset. It does not include
PowerShell. Activate an explicit PowerShell payload with a hash-pinned manifest:

```csharp
using Devolutions.PowerShell.Ffi;

PowerShellRuntime runtime = PowerShellRuntime.Activate(
    new PowerShellPayloadActivationOptions(
        payloadDirectory,
        manifestPath,
        manifestSha256));
```

The manifest must use the `devolutions-pwsh-payload` schema and SHA-256-pin
every regular file recursively beneath the selected payload root, including
nested module dependencies. The package provides
`contentFiles/any/any/devolutions-pwsh-payload.manifest.template.json`; it is a
template, not a trusted manifest. Store the final manifest SHA-256 in
application-controlled deployment configuration. `PowerShell.Initialize(string)`
and `PowerShellRuntime.Activate(string)` are obsolete unsafe local-development
compatibility overloads; they require the conventional manifest beside the
payload but do not pin it.

Enable native-asset staging in the consuming project:

```xml
<PropertyGroup>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <DevolutionsMultiPwshSdkEnabled>true</DevolutionsMultiPwshSdkEnabled>
</PropertyGroup>
```

The facade deliberately exposes no SMA types and supports one selected
PowerShell/CoreCLR runtime per process. NativeAOT plus dynamically hosted
CoreCLR is an experimental deployment condition, not a generally supported
.NET hosting topology.

Native deployment is inert by default even for a matching RID. Set
`DevolutionsMultiPwshSdkEnabled=true` to stage the package's
`runtimes/win-x64/native/multi-pwsh-sdk.dll` beside the application. The
package never embeds a PowerShell payload. Its native asset uses a static MSVC
runtime and the dedicated unwind-enabled `ffi-release` Rust profile, so native
panic containment remains effective at the ABI boundary.
The SDK NuGet version and the native DLL `FileVersion`/`ProductVersion` match
the `multi-pwsh` CLI release version.

The facade requires native ABI v2 and obtains a bounded diagnostic from the
specific native call that failed. Its `SafeHandle` wrapper keeps a native handle
alive for each P/Invoke call, including concurrent disposal races. Empty
strings are valid UTF-8 inputs; embedded NUL characters are rejected.

Hash-pinned activation verifies canonical paths, no file traversal, target RID
and architecture, manifest schema/pin, complete file closure, every declared
file hash, PowerShell/.NET/hostfxr versions, and bindings ABI/features before
`hostfxr` is initialized. It then copies and re-verifies the declared files in
a fresh process-owned staging directory and loads only from that directory.
The staging directory remains owned for process runtime lifetime; a hostile
same-account process with filesystem access remains outside this boundary. It
does not validate signatures. `UnsafeUntrustedLocalDevelopment` is an explicitly
unsafe, direct local-load opt-in that accepts an unpinned manifest and does not
enforce complete closure; it is not appropriate for deployment.

`PowerShellValue` is the only way to pass non-string values. It supports
bounded primitive values, bytes, arrays, and property bags that become copied
PSCustomObject-style snapshots. `PowerShellValue.From` rejects delegates and
unsupported CLR objects with `PowerShellValueConversionException`; raw objects
are never passed to PowerShell. `AddInput` is synchronous and bounded to 64
values/64 KiB. Call `CompleteInput` before invoking a started input collection,
or use `ResetInput`/`Clear` to discard it.

`Invoke` returns an immutable `PowerShellInvocationResult`. Its output and
standard PowerShell streams are bounded snapshots rather than SMA objects:
each stream retains at most 32 records and each text field is capped at 4,096
UTF-16 code units. Snapshot metadata reports truncated records/fields and a
global stream sequence, plus total/dropped record counts. Output snapshots can
carry a copied safe scalar and a bounded tagged property bag containing only
scalar `PSNoteProperty` values; complex values and enumerables are not
traversed. Error snapshots add copied category/target, command, source and
pipeline context. Per-error terminal status is intentionally unavailable:
`IsTerminatingFailure` is result-level only. A terminating invocation throws
`PowerShellInvocationException` with the same result snapshot.
Results also expose a monotonic invocation ID, terminal state, and `HadErrors`
metadata without exposing any SMA type.

`runtime.ParseScriptParameters(script)` supports script-editor parameter
metadata without executing `script`. It returns copied parameter DTOs (name,
declared type/default-expression spelling, mandatory flag, description/help
text, and `ValidateSet` entries) or copied syntax-error DTOs; it never exposes
SMA AST/token objects. The API accepts at most 64 KiB of source and fails rather
than truncating more than 16 parameters, 16 `ValidateSet` values, or 16 parse
errors.

`PowerShellSnapshotSerializer` provides deterministic version-1 UTF-8 JSON for
storage or display of immutable invocation results. Documents are capped at
1 MiB and reject unknown members, invalid versions, malformed tagged values,
and invalid bounds. Deserialization only rebuilds copied facade DTOs; it never
creates PowerShell/SMA, live CLR objects, or object handles.

`BeginInvoke` returns a native-owned `PowerShellInvocationOperation`; use
`Poll`, bounded `Wait`, `Stop`, and `GetResult` for explicit lifecycle control.
`InvokeAsync(CancellationToken)` waits for native terminal cleanup before
completing a cancelled task. Cancellation is idempotent and wins over a
concurrent completion, so cancelled operations expose no partial result.
Async input is limited to the existing copied, bounded producer: add input,
call `CompleteInput`, then start the operation. Input cannot be fed after
start.

`PowerShellRuntime.CreateSession(PowerShellSessionOptions)` creates a separate,
reusable local-runspace session. `PowerShellSessionConfiguration` supplies
copied tagged initial variables, module imports/paths, a working directory,
environment values, and the `Default`/`Restricted` execution-policy subset.
All module paths, working directories, import names, and environment keys must
be explicitly allowlisted by the activated manifest's `sessionPolicy`; omitted
or empty policy lists deny that configuration. Paths are absolute existing
directories, imports are names resolved beneath approved module paths, and
current-runspace sessions reject every configuration field. This is not a
general `InitialSessionState` or arbitrary module-loading API. `GetSnapshot`
and `GetEvents` are bounded polling APIs; events retain at most 32 numeric
state records and report truncation. A session is not a pool. Normal pipeline
execution and builder mutation, including calls through independent sessions,
are process-globally serialized; only `Stop`/cancellation can run concurrently
with an active pipeline.
`CreateSessionPool` is an intentional rejection boundary: it validates a
maximum bound of 1–64 sessions then returns
`UnsupportedCapability`, rather than faking concurrent pool behavior.

Sessions can also retain copied declarative values without exposing
`SessionStateProxy` or SMA:

```csharp
session.SetVariable(
    "connection",
    PowerShellValue.PropertyBag(
    [
        new("ComputerName", PowerShellValue.String(computerName)),
        new("Port", PowerShellValue.SignedInteger(port)),
    ]));

if (session.TryGetVariable("connection", out PowerShellValue? connectionSnapshot))
{
    // This is a fresh bounded copied value, never a PSObject or a proxy.
}
```

`SetVariable`, `TryGetVariable`, and `RemoveVariable` accept only bounded
`PowerShellValue` graphs and ASCII identifier names. They reject a session with
a pending/running async invocation, and they return `UnsupportedValue` rather
than stringifying a value that cannot be copied. Do not use these APIs for
credentials, live RDM proxy objects, or callback objects.

`PowerShellCommandRecipe` and `PowerShellScriptRecipe` provide bounded,
declarative one-shot calls. An optional `PowerShellResultSchema` rejects output
that has the wrong count, copied scalar kind, required property, error state, or
truncation. `PowerShellRuntime.Invoke` and `InvokeAsync` apply recipe timeouts
through the existing cancellation model. `PowerShellCommandPolicy` is an
application allowlist/size guardrail only; it is not a PowerShell sandbox and
cannot make approved arbitrary script source safe.

`PowerShellSnapshotReader.GetCompleteProperties` rejects truncated snapshots
before returning their copied property bag, and
`CreateDisplaySnapshot` returns copied text for each stream plus an explicit
completeness flag. `PowerShellSession.SetPropertyBag`,
`TryGetPropertyBag`, and `InvokeAndReadVariable` support value-only session
result DTO flows; they never retrieve a live `PSObject`.

`ParseScriptParameters` also projects bounded aliases, parameter-set name and
position/pipeline flags, and `ValidatePattern`, `ValidateRange`,
`ValidateLength`, and `ValidateCount` argument spelling. The combined output
is capped at 32 metadata records, so it fails rather than silently truncating.

`PowerShellRdmCapabilities` supplies opt-in schemas for
`rdm.get-connection-name`, `rdm.get-connection-display`, and
`rdm.report-status`; applications must still register their own handlers.
`PowerShellHostInteraction.ParseProgressUpdate` validates an explicit copied
`host.report-progress` property bag (including ID and range fields), rather
than inferring progress from stream display text.

Raw ABI releases consume their handles, so a repeated raw release or later use
returns `InvalidHandle`. Public `Dispose` methods are idempotent; their
`SafeHandle` leases defer native release until any in-flight facade call exits.

The facade never accepts `PSHost`, delegates/callbacks, connection info,
remoting providers, credentials, `SecureString`, serialized credentials,
nested or steppable pipelines, generic CLR values, or live
runspace/PowerShell objects. `PowerShellSecretTransfer.Policy` is explicitly
`Rejected`; `ThrowNotSupported()` produces a typed exception rather than
accepting secret material. An arbitrary script can deliberately emit any value
it receives, so this boundary cannot promise secret redaction or a zeroable
managed credential lifetime. Do not place secrets in `PowerShellValue` or
session variables: snapshots and their serializer are general copied output,
not a secret store. There is no prompt or callback channel. Opaque object
handles, secret transfer, pools, remoting, and arbitrary CLR-object transfer
remain non-goals.

For an additive one-shot administrative pilot, build a command with
`AddCommand("Restart-Computer")`, use `AddParameter("WhatIf")` for tests, and
call `InvokeAsync` with a `CancellationTokenSource.CancelAfter` timeout.
Inspect `PowerShellInvocationResult`/`PowerShellInvocationException` stream
snapshots; do not use `InvokeText()` as a success-only path. Actual shutdown or
restart execution must be application-gated and is never exercised by package
tests.
