# Devolutions.MultiPwsh.Sdk

Experimental `net10.0` NativeAOT facade for an in-process PowerShell payload
hosted by the `multi-pwsh-sdk.dll` Rust library.
Its public C# types remain in the `Devolutions.PowerShell.Ffi` namespace.

This package ships native FFI assets for `win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, `linux-arm`, `osx-x64`, and `osx-arm64`. It does not include
PowerShell. Only `win-x64` currently has end-to-end NativeAOT payload smoke
coverage. Release CI also runs a package-consumer ABI smoke on `win-x64`,
`linux-x64`, `osx-x64`, and `osx-arm64`; that verifies native-library loading,
the ABI export, and RID-specific staging, not payload activation.

By default, activation locates `pwsh` on `PATH` and loads its containing
payload directory. Supply a directory to select a payload explicitly:

```csharp
using Devolutions.PowerShell.Ffi;

PowerShellRuntime defaultRuntime = PowerShellRuntime.Activate();
PowerShellRuntime selectedRuntime = PowerShellRuntime.Activate(payloadDirectory);
```

The application controls payload provenance and file integrity. The SDK does
not read, package, or validate a PowerShell payload manifest.

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
RID-specific native library beside the application. The package never
embeds a PowerShell payload. Windows native assets use a static MSVC runtime
and the dedicated unwind-enabled `ffi-release` Rust profile, so native panic
containment remains effective at the ABI boundary.
The SDK NuGet version and the native DLL `FileVersion`/`ProductVersion` match
the `multi-pwsh` CLI release version.

## Live-contract generator preview

The package carries a `net8.0` compile surface for
`LiveContractAttribute`, `LiveObjectAttribute`, and `LiveMemberAttribute`, plus
the incremental generator under `analyzers/`. This lets a trusted `net8.0`
payload-pack project compile the same explicitly annotated contract declaration
as the `net10.0` NativeAOT host without referencing the NativeAOT facade
assembly. In `Payload` mode the package injects its small contract source files
into the payload compilation and removes the contracts DLL from the compiler
reference set. This is required because trusted packs are loaded from bytes and
cannot resolve a normal external contracts assembly. Do not manually link a
second copy of those contract source files in a Payload project. NuGet
auto-loads the analyzer; the package's transitive target makes the project
`LiveContractMode` visible to it:

```xml
<PropertyGroup>
  <LiveContractMode>Payload</LiveContractMode>
</PropertyGroup>
```

Compile the host declaration with `LiveContractMode=Host` and the trusted
payload declaration with `LiveContractMode=Payload`. Both sides must use the
same source-declared IDs and contract version. This remains a restricted preview: it does not expose a generic object bridge,
reflection dispatch, callbacks, credentials, `PSHost`, remoting, or arbitrary
CLR values. For the supported root/add/collection/string-property graph, the
generator emits a bounded staged host adapter and static public payload
wrappers over the single broker interface. The host must continue to end the
lease authoritatively; generated root disposal never releases a lease held by
potentially retained child wrappers.

The facade requires native ABI v2. `ReadStreamBatch` is independently gated by
the `LIVE_STREAM_POLLING` feature bit, so a native asset that lacks polling
returns `UnsupportedCapability` before its additive export is called. Use
matching 0.17.0 package and native assets to enable polling. Its `SafeHandle`
wrapper keeps a native handle alive for each P/Invoke call, including concurrent
disposal races. Empty strings are valid UTF-8 inputs; embedded NUL characters
are rejected.

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

`ReadStreamBatch(afterSequence, maximumRecords)` polls an active operation's
immutable copied display records. Batches contain ordered stream kind/sequence/
text records, terminal state, and explicit cursor-loss and truncation counters.
The operation retains at most 32 records; pass the previous `NextSequence` to
advance the cursor and treat any loss or truncation as incomplete display data.
This is neither a live console, `PSHost`, callback/event API, nor a stream of
SMA objects, credentials, or other CLR references. Cancellation can expose
already-captured records, but still never produces a successful final result.

## DTO projections and bounded paging

`PowerShellDtoContractAttribute` and `PowerShellDtoMemberAttribute` opt an
application DTO into the package's separate incremental source generator. The
generator emits direct `Read`, `TryRead`, and `Write` methods for a versioned
`PowerShellValue` property bag; no reflection or runtime type discovery is
used. Contracts require public settable properties and a public parameterless
constructor, and support only the documented copied scalar kinds plus bounded
one-dimensional arrays of those scalars. Every property bag carries an exact
`$version` value. By default unknown properties, missing required properties,
incorrect scalar kinds, and string/array limit violations return a structured
`PowerShellDtoProjectionError`; `Read` raises the corresponding typed
exception. This is an application DTO mapper, not a serializer for arbitrary
CLR graphs, PowerShell objects, credentials, callbacks, or live-object
contracts.

`PowerShellValuePager` is the reusable bounded acknowledgement state machine
for copied result values. Its caller-configured record and page bounds apply
backpressure to writers, and its pages expose ordered sequences plus an
acknowledgement cursor. Records remain retained only until
`Acknowledge(sequence)` removes them. `GetCompletion().IsComplete` is true
only after a successful terminal state and acknowledgement of every produced
record; cancellation, disposal, a terminal error, or unacknowledged records
are never silently complete. This primitive is deliberately distinct from
`ReadStreamBatch`: it does not turn the existing lossy display stream into a
typed data feed, and it never exposes SMA values or unbounded retention.

`BeginTypedResultInvocation(options)` connects that acknowledgement model to
an invocation's output through the additive `TYPED_RESULT_PAGING` native
feature. `Read(acknowledgedThrough, maximumRecords)` acknowledges the prior
page and returns only copied `PowerShellValue` records (including bounded
arrays and property bags), never SMA objects, `PSObject`, or display text.
The configured buffer is a hard producer backpressure limit; values are not
dropped to make room. Each page reports total, dropped, truncation, terminal,
and completeness metadata. Outputs that cannot be represented losslessly as a
documented tagged value terminate the typed operation with `UnsupportedValue`
rather than being converted to text or silently omitted. Older native assets
are rejected with `UnsupportedCapability` before the additive exports are
called.

`PowerShellRuntime.CreateSession(PowerShellSessionOptions)` creates a separate,
reusable local-runspace session. `PowerShellSessionConfiguration` supplies
copied tagged initial variables, module imports/paths, a working directory,
environment values, and the `Default`/`Restricted` execution-policy subset.
Paths are absolute existing directories, imports are names resolved beneath the
application-supplied module paths, and
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
credentials, live application proxy objects, or callback objects.

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

Applications define their own capability schemas using lowercase,
namespace-qualified names such as `app.get-label`; applications must still
register their own handlers.
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
