# In-process PowerShell FFI experiment

`pwsh-host` can be exposed through the `pwsh-sdk-ffi` Rust `cdylib`.
The library receives an explicit PowerShell payload directory, loads that
payload's `hostfxr`, initializes `pwsh.dll`, injects
`Devolutions.PowerShell.SDK.Bindings`, and invokes its unmanaged function table.

The `dotnet/sdk-ffi` facade uses only `LibraryImport`. It has no
`System.Management.Automation` or `Microsoft.PowerShell.*` dependency.

## Experimental runtime boundary

The `dotnet/nativeaot-sample` NativeAOT executable has been exercised on Windows x64
with an explicit PowerShell 7.4 payload in the **same process**. It creates a
pipeline and receives script output through the Rust `cdylib`.

This is an acceptance experiment, not a claim that NativeAOT plus a dynamically
hosted CoreCLR is a supported general .NET topology. The runtime host rejects
incompatible initialization. Do not rely on dynamic runtime switching, multiple
PowerShell runtime versions, or runtime unloading in the same process.

## Lifecycle and ABI

- The direct and manifest activation exports are process-global and accept one
  canonical payload directory plus one activation identity. Repeating the same
  activation succeeds; selecting a different payload, or changing between
  direct and manifest activation, returns `MULTI_PWSH_INCOMPATIBLE_PAYLOAD`.
- The native product ABI reports its compatible version and feature flags through
  `multi_pwsh_get_abi_info`; the managed package and native asset ship together
  and use the unversioned `multi_pwsh_*` exports. The injected managed function
  table independently reports its compatibility version.
- The supported ABI uses sized, caller-owned `multi_pwsh_call_result` structures. Each operation
  returns its own bounded UTF-8 diagnostic and truncation metadata, so concurrent
  calls never read a process-global error slot. `multi_pwsh_utf8_span` permits
  `(NULL, 0)` for an empty value; the library never retains caller memory.
- The caller owns all input and output buffers. UTF-8 fields use a two-pass
  required-length query and never retain caller memory.
- `multi_pwsh_create` returns an opaque builder handle. `multi_pwsh_release`
  disposes its managed `GCHandle`; handles are never reused by the current
  process. `multi_pwsh_invoke` instead returns a distinct immutable result
  handle. Release it explicitly with `multi_pwsh_result_release`; it remains
  readable after the builder is cleared, mutated, or released.
  - `multi_pwsh_result_get_metadata` reports the immutable invocation ID,
    terminal synchronous state (`Completed` or `Terminated`), and `had_errors`
    bit alongside the retained stream snapshot.
- Exports contain Rust panics and return fixed status codes without unwinding
  into the caller.
- Normal pipeline execution and builder-mutation calls are serialized by the
  cdylib's **process-global** operation lock, including calls made through
  otherwise independent builders and sessions. This ABI version does not
  support parallel independent-session execution. `Stop` is the exception:
  builder and operation stop requests may run concurrently with the active
  pipeline so cancellation is not blocked behind `Invoke`.
- A v2 session is a separate opaque handle, not a builder alias. A session owns
  (or narrowly borrows) one local runspace; builders created by
  `multi_pwsh_session_create_builder` keep an internal managed lease, so they
  remain valid if the public session handle is released first. Session release
  prevents new builders and closes an owned runspace after the final builder
  lease is released. Immutable results and operations remain independently
  owned as before.
- Explicit native release consumes its raw handle: a second release or any
  later use deterministically returns `MULTI_PWSH_INVALID_HANDLE`. In contrast,
  the facade's `IDisposable` implementations are idempotent and their
  `SafeHandle` leases keep an in-flight facade call alive until that call
  returns.

## Payload trust and activation

`PowerShellRuntime.Activate()` resolves `pwsh` from `PATH` and activates its
containing payload directory. `PowerShellRuntime.Activate(payloadDirectory)`
activates exactly the caller-selected directory. Neither direct activation mode
reads `devolutions-pwsh-payload.json` or requires any other manifest: the
application controls which PowerShell it trusts and bears responsibility for
its provenance and integrity.

For deployments that require a verified, race-resistant payload, hash-pinned
activation is deliberately **not** an existence check. Before loading
`hostfxr`, `multi_pwsh_initialize_payload` canonicalizes the payload
and manifest paths, parses the manifest, verifies the caller-supplied SHA-256
pin of the complete manifest bytes, checks the target RID/architecture, and
SHA-256 verifies every declared file. A hash-pinned manifest must declare
**every regular file recursively beneath the selected payload root**; an
undeclared DLL, module file, or other regular file rejects activation. It also requires hashes for
`pwsh.dll`, `pwsh.runtimeconfig.json`, `pwsh.deps.json`,
`System.Management.Automation.dll`, `hostfxr.dll`, and `coreclr.dll` on the
currently supported Windows payload.

The schema is `devolutions-pwsh-payload` version `1`:

```json
{
  "schema": "devolutions-pwsh-payload",
  "schemaVersion": 1,
  "payload": { "id": "PowerShell", "version": "7.x.y" },
  "target": { "rid": "win-x64", "architecture": "x64" },
  "runtime": {
    "powerShellVersion": "7.x.y",
    "dotnetVersion": "x.y.z",
    "hostfxrVersion": "x.y.z",
    "bindingsAbiVersion": 2,
    "requiredBindingsFeatures": 123136
  },
  "files": [{ "path": "every/regular/file", "sha256": "<64 hexadecimal digits>" }],
  "trust": { "allowSymlinks": false },
  "sessionPolicy": {
    "modulePaths": [],
    "workingDirectories": [],
    "moduleImports": [],
    "environmentKeys": []
  }
}
```

`payload.version` and `runtime.powerShellVersion` must match the
`System.Management.Automation` version in `pwsh.deps.json`.
`runtime.dotnetVersion` is checked against `pwsh.runtimeconfig.json`, and
`runtime.hostfxrVersion` is checked against the Windows `hostfxr.dll` product
version. The bindings ABI must be `2` and declare the async-operation,
snapshot-projection, bounded session-configuration, and copied-session-variable
bits (`123136`). Validation failures are returned by the activation call as bounded
diagnostics with one of `PayloadManifestInvalid`, `PayloadUntrusted`,
`PayloadHashMismatch`, or `PayloadIncompatible`; CoreCLR has not started yet.

Manifest file paths are slash-separated relative paths only. `..`, rooted,
dot, and duplicate paths are rejected. The payload root and every declared
file are canonicalized; a file resolving outside the canonical root is
rejected. Payload roots reached through normal Windows junction/path
substitution are accepted after canonicalization. Per-file symlinks are
rejected unless `trust.allowSymlinks` is explicitly true, and even then must
resolve inside the payload root. With the default `allowSymlinks: false`, any
symlink in a hash-pinned payload rejects its complete-closure check.

The hash-pinned manifest itself must be outside the payload root: otherwise a
manifest that hashes every regular file would need to contain a stable hash of
its own bytes. The package template is deliberately not a usable manifest.
Build the `files` array from the installed payload, including nested module
files, then set the remaining placeholders and pin the final manifest bytes.
For example, this produces the complete array for a non-symlinked payload:

```powershell
$payload = (Resolve-Path 'C:\path\to\payload').Path
$files = @(
  Get-ChildItem -LiteralPath $payload -Recurse -Force |
    ForEach-Object {
      if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Payload symlinks require an explicitly reviewed allowSymlinks policy: $($_.FullName)"
      }
      if (-not $_.PSIsContainer) {
        [ordered]@{
          path = [IO.Path]::GetRelativePath($payload, $_.FullName) -replace '\\', '/'
          sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
      }
    } |
    Sort-Object path
)
```

`tests/Test-PwshFfiPackage.ps1` uses this same complete-closure approach when
it generates its smoke-test manifest.

The caller's manifest SHA-256 pin is the trust anchor. This implementation
does **not** validate a signature and must not be described as signed.
For hash-pinned activation, the validated files are copied into a fresh
per-activation staging root, every staged file is hashed again against the
manifest, and `hostfxr` and PowerShell load only from that staging root. The
native runtime retains ownership of the staging root for its process lifetime.
This removes the validation-to-load race against the original payload path.
The staging directory is an ordinary per-user temporary filesystem directory:
a peer with the same account and sufficient filesystem access can still modify
it after verification, so this is not a defense against a hostile same-account
process. Protect the account and its temporary directory accordingly.

There is no conventional manifest lookup beside the payload. Direct activation
uses the requested payload directory or the `pwsh` runtime selected from
`PATH`; it has no manifest-derived session policy, so extended session
configuration remains denied. The obsolete
`PowerShellPayloadActivationOptions.UnsafeUntrustedLocalDevelopment` factory
retains the older unpinned-manifest behavior for compatibility only. An attacker
who can replace that manifest or payload can subvert it; do not use it for
deployment.

### Async operations and cancellation

`multi_pwsh_invoke_async` returns an opaque operation handle. It is a genuine
native-owned invocation: the Rust bridge retains the builder and its managed
pipeline until the operation reaches a terminal state. It is not a managed
`Task.Run` wrapper around synchronous `Invoke`.

- `multi_pwsh_operation_poll` and `multi_pwsh_operation_wait` report
  `Pending`, `Running`, `Completed`, `Cancelled`, or `Failed`, together with
  the terminal status. A wait timeout reports the current non-terminal state;
  `UINT32_MAX` waits indefinitely.
- `multi_pwsh_operation_stop` is idempotent. The first stop request wins over
  any concurrent successful completion: the operation becomes `Cancelled`,
  returns `MULTI_PWSH_OPERATION_CANCELLED_STATUS`, and never exposes an
  immutable result. This prevents a caller from accepting partial script
  output as a successful result. Repeated stop calls and stop after a terminal
  state succeed.
- A completed operation exposes immutable result snapshots through
  `multi_pwsh_operation_get_result`. Retrieval before terminal completion
  returns `MULTI_PWSH_OPERATION_NOT_TERMINAL`; cancelled and failed operations
  return their terminal status plus the operation's bounded (4 KiB) diagnostic.
  Result handles are independently released with
  `multi_pwsh_result_release` and can outlive the operation handle.
- `multi_pwsh_operation_release` is explicit single-owner release. Releasing an
  active operation first requests cancellation, then detaches the caller's
  handle. Existing native calls and the worker retain internal references, so
  no release race can free a pipeline while it is executing. The raw operation
  handle is then stale and a repeated release deterministically returns
  `MULTI_PWSH_INVALID_HANDLE`; `PowerShellInvocationOperation.Dispose()` remains
  idempotent. Releasing a builder with an active operation likewise requests
  cancellation.
- A builder is immutable while its async operation is active. Builder mutation,
  synchronous invocation, and input calls return `MULTI_PWSH_BACKPRESSURE`
  until terminal completion. Builder `Stop` targets its active operation and
  has the same cancellation-wins semantics.

The facade projects this model as `PowerShell.BeginInvoke()` and
`PowerShell.InvokeAsync(CancellationToken)`. `PowerShellInvocationOperation`
offers `Poll`, bounded `Wait`, `Stop`, and `GetResult`. Token cancellation
calls the native stop primitive, then waits for the native terminal state
before completing the task as cancelled; it never abandons or prematurely
releases the operation handle. The facade uses `LibraryImport` and
`SafeHandle` leases only—no SMA objects, native callbacks, or collectible
managed delegates cross the boundary.

### Sessions, bounded settings, and polling

`PowerShellRuntime.CreateSession(PowerShellSessionOptions)` creates a reusable
`PowerShellSession`. It deliberately offers only copied, declarative settings:

- `NewRunspace` creates and owns a local runspace. Its initial configuration is
  either PowerShell's default local state or `ConstrainedLanguage`. Its
  `PowerShellSessionConfiguration` can set copied tagged initial variables,
  approved module paths/imports, one approved working directory, allowlisted
  environment values, and the `Default`/`Restricted` execution-policy subset.
  It is not an arbitrary `InitialSessionState` or module-loading API.
- `CurrentRunspace` is a narrow ambient-runspace opt-in. It requires an already
  opened default runspace and rejects all configuration, history, preferences,
  and module-path changes, so this boundary cannot mutate the embedding
  application's current runspace.
- History is a Boolean invocation setting. Error, warning, verbose, debug, and
  information preferences are limited to inherit, continue, silently continue,
  or stop. Interactive preference modes are rejected because the ABI has no
  prompt channel. Error preference is also passed through a bounded
  `PSInvocationSettings`; no settings object leaves managed code.

The activated manifest governs session configuration. `sessionPolicy` is
optional, but omitted or empty entries deny the corresponding module paths,
working directories, module imports, and environment keys. Policy paths are
canonical, slash-separated relative directories inside the payload (use `.`
for the payload root); requested facade paths must be absolute existing
directories and match an approved canonical source directory exactly. For
hash-pinned activation, approved module roots, working directories, and exact
module-manifest identities are translated to their staged equivalents before
the managed session is created; initial imports cannot use the original source
directory. A session working directory selected from the source payload is
therefore observed as the corresponding staged directory. Module imports are
bounded names and are resolved only beneath approved module paths. For a
staged approved module root, the payload-local authorization manager permits
only external scripts under that root and rejects every other external script.
This preserves the exact manifest identity and staged closure boundary; it is
not a general authorization bypass or an additional module search path.
Diagnostics report policy categories without echoing supplied paths or
environment values. `CurrentRunspace` rejects all configuration so the ABI
does not mutate ambient application state.

`GetSnapshot()` and `GetEvents()` are polling APIs. A snapshot reports copied
session/runspace state, active pipeline count, invocation and history counts.
Events are numeric state records only—no callback function pointer, delegate,
or managed object crosses the ABI. At most 32 events are retained; overflow is
reported by the snapshot's `AreEventsTruncated` flag. Pipelines on the same
session are serialized by the in-process host's operation boundary and the
process-global normal-operation lock. A snapshot does not authorize concurrent
live runspace use, including across separate sessions.

The lifecycle is explicit even though configuration/import are synchronous:
construction validates configuration and approved imports before returning an
`Opened` session; invocation reports `Running`; stop/cancellation returns to
`Opened` after terminal cleanup; owner release is `Closed`; an unrecoverable
runspace failure reports `Faulted`. A closed public session cannot create
builders, while an already-created builder retains its managed lease until it
is released. There is no concurrent session mutator model: normal operations
are process-globally serialized and copied-variable access rejects an active
async session operation.

`PowerShellSessionPoolOptions` and `CreateSessionPool` form an explicit
rejection boundary. `multi_pwsh_session_pool_create` validates a bounded
maximum of 1–64 sessions (with a minimum no greater than that) then always returns
`MULTI_PWSH_UNSUPPORTED_CAPABILITY`: the current single CoreCLR, local-runspace
model has no safe pool lifecycle/concurrency implementation. It does not
pretend that serializing one runspace is a pool.

The proxy surface is `Create`, `AddCommand`/`AddScript` (including
`useLocalScope`), `AddArgument`, typed and switch parameters, `AddStatement`,
`Clear`, bounded input feeding, result-oriented `Invoke`, `Stop`, and
`Dispose`. `AddArguments` and `AddParameters` are convenience bulk builders;
each item is validated and appended in order, so callers should `Clear` to
discard a partially built pipeline after an item fails.

## Tagged values and input

`multi_pwsh_data_value` is the only native value-transfer envelope. Its `kind`,
bounded caller-owned payload, and zero flags are copied synchronously by the
call. It never carries a CLR object, `PSObject`, delegate, handle, reflection
request, or JSON document. The facade exposes the same contract as immutable
`PowerShellValue` instances:

- null, string, switch, Boolean, signed/unsigned 64-bit integer, double,
  invariant decimal text, bytes, `DateTime`, `DateTimeOffset`, `Guid`, and
  absolute `Uri`;
- arrays of at most 64 tagged values;
- property bags of at most 64 unique string keys. The payload is copied to a
  fresh managed `PSObject` with note properties, so it is a
  PSCustomObject-style snapshot rather than a live caller object.

Payloads are capped at 64 KiB and containers nest at most eight levels. Fixed
numeric payloads are little-endian; `Guid` uses canonical UTF-8 `D` text,
decimal uses invariant UTF-8 text, `DateTime` is `DateTime.ToBinary()`, and
`DateTimeOffset` is ticks followed by a signed little-endian offset-minute
count. Array payloads are `u32 item-count` followed by `u32 kind`, `u32
length`, and payload for each item. Property bags use `u32 item-count`,
`u32 key-length`, UTF-8 key, then that same nested value record. These binary
envelopes are deliberately not JSON.

`PowerShellValue.From(object?)` converts only that documented set and nested
property bags/arrays. It throws `PowerShellValueConversionException` for
delegates and unsupported CLR objects; no facade builder overload accepts a
raw `object`.

`AddInput` copies one tagged value into a synchronous per-builder collection.
The collection accepts at most 64 values and 64 KiB of payload; exceeding
either bound returns `MULTI_PWSH_BACKPRESSURE`. Call `CompleteInput` before
`Invoke` after starting input. Invoking an uncompleted collection returns
`MULTI_PWSH_INPUT_NOT_COMPLETED` and retains the input so it can be completed or
`ResetInput` can discard it. Invoking without ever starting input retains the
ordinary no-input behavior. Completion, reset, clear, release, and invocation
all discard the managed input collection after its defined use, preventing
reuse from retaining unreported input.

Async input intentionally supports only that bounded producer model: add up to
64 tagged values, call `CompleteInput`, then start the operation. Streaming a
producer concurrently with `InvokeAsync`, adding input after start, and
reopening completed input are intentionally unsupported. This preserves an
explicit copied-input lifetime and deterministic cancellation boundary.

`multi_pwsh_result_get_stream_info` and indexed record APIs expose output,
error, warning, verbose, debug, information, and progress. Retained records
carry a global capture sequence available through
`multi_pwsh_result_get_sequence_record`. Each stream retains at most 32
records; each field retains at most 4,096 UTF-16 code units.
`multi_pwsh_result_get_stream_totals` reports all observed and dropped
records, not merely the retained ring. Result, stream, and record flags report
terminating invocation, dropped sequence/stream records, and field truncation.
Managed stream buffers are cleared before and after every result invocation,
including streams the caller does not read.

The facade projects this into immutable `PowerShellInvocationResult` snapshots:
output is a `PowerShellObjectSnapshot` with display text, up to eight copied
type-label strings, and an optional copied tagged scalar. Type labels are
declarative text only; they do not expose, bind, or retain a CLR type or object
identity. The optional property bag is built only from `PSNoteProperty` values
that convert to documented tagged scalars. It retains at most 16 ordinal-sorted
keys of at most 128 UTF-16 code units, a 1 KiB scalar payload per value, and a
16 KiB total envelope. Complex values and enumerables are neither traversed nor
enumerated; they are dropped and the retained/dropped entry counts and
truncation flag make that loss explicit.

Errors add copied category reason/activity/target labels, command and source
location text, pipeline coordinates, details/recommended-action text, target
display text, and an optional scalar target projection. These are bounded
strings or tagged copies, never `ErrorRecord` or `InvocationInfo` references.
There is intentionally no per-record terminal flag: PowerShell does not expose
one reliably for every captured error. `IsTerminatingFailure` is the reliable
result-level terminal indicator. A terminating invocation throws
`PowerShellInvocationException`, whose `InvocationResult` retains that bounded
snapshot.

`PowerShellSnapshotSerializer` serializes and restores those immutable facade
snapshots as deterministic, versioned UTF-8 JSON (format version 1). It is for
storage or display only, capped at 1 MiB and JSON depth-limited to 16 by the
source-generated DTO/parser contract. It rejects unknown members, unsupported versions, malformed
tagged-value envelopes, oversized values, and invalid stream totals. It never
deserializes into PowerShell, SMA, a parent CLR object, or an arbitrary CLR
type.

Call `PowerShellRuntime.Activate()` to select `pwsh` from `PATH`, or
`PowerShellRuntime.Activate(payloadDirectory)` to select an explicit payload.
Use `PowerShellRuntime.Activate(new PowerShellPayloadActivationOptions(
payloadDirectory, manifestPath, manifestSha256))` only when opting into
hash-pinned activation. Then use `runtime.Create()` (or the equivalent
`PowerShell.Create()` process-global entry point) to construct builders. The
runtime object reports the selected paths, trust policy, and negotiated
ABI/features; it does not permit selecting a second payload or unloading the
selected runtime.

## RDM DTO migration boundary

This SDK offers DTO migration compatibility, not SMA compatibility. The table
below is the current implementation status for RDM-facing work.

| RDM need | Status | FFI replacement and boundary |
| --- | --- | --- |
| Explicit payload activation, hostfxr startup, one runtime per process | Implemented | Direct payload or `PATH` activation, with optional hash-pinned manifest staging, and an opaque `PowerShellRuntime`; only `win-x64` has NativeAOT smoke evidence. |
| Script or named-command execution with scalar parameters | Implemented | `AddScript`, `AddCommand`, `AddParameter`, and bounded `PowerShellValue` inputs. No raw `object`, `PSObject`, or `SecureString` overload exists. |
| Script parameter declarations and syntax errors | Implemented, copied-only | `PowerShellRuntime.ParseScriptParameters` passes the input to payload-local `Parser.ParseInput` as data, never executable pipeline text. It returns bounded parameter/parse-error DTOs, not SMA AST or token objects. |
| Output, errors, diagnostics, warning/progress streams | Implemented | Immutable bounded `PowerShellInvocationResult` snapshots. Safe scalar and property-bag projections are read through typed copied-value readers, never live SMA collections. |
| Timeout, cancellation, async completion, deterministic disposal | Implemented | `InvokeAsync(CancellationToken)`, `BeginInvoke`, `Wait`, `Stop`, and `SafeHandle` ownership. Cancellation wins and never returns a partial success result. |
| Long-lived local state | Implemented | Opaque local `PowerShellSession` plus serialized builders. It is not a pool or a remoting session. |
| `SessionStateProxy.SetVariable` for value data | Implemented, copied-only | `SetVariable`, `TryGetVariable`, and `RemoveVariable` transfer bounded tagged values only. No methods, proxies, handles, or CLR identity survive the boundary. |
| Approved local modules | Implemented, local-only | Each requested import must map to a manifest-pinned `.psd1` identity: canonical name, path beneath an approved module root, SHA-256 also listed in `files`, and exact `ModuleVersion`. Rust resolves the identity and the payload imports that exact manifest path. This does not validate PowerCLI or remoting dependencies. |
| `PSCredential` parameter | Intentionally unsupported | Arbitrary scripts can emit or transform a supplied credential. The DTO result model cannot guarantee redaction or a zeroable managed lifetime. |
| Enumerated RDM capability calls and bounded host interaction | Implemented, opt-in | A registered `PowerShellCapabilitySet` makes only declared typed calls available through the temporary payload-local `$DpsCapabilities` object. `PowerShellHostInteraction` supplies schemas for text, progress, line, and choice interactions; it is not a `PSHost` proxy. |
| PowerCLI typed return objects, PSRP/WinRM/SSH, pools, and transports | Unsupported | Retain the existing SMA/process paths. No CLR type, transport, or live session crosses the facade. |

A local package-swap assessment of RDM's
`Windows/RemoteDesktopManager/Business` project confirmed this is not a
drop-in replacement for `Devolutions.PowerShell.SDK`. That project directly
uses SMA parser AST/token types and `Collection<PSObject>` output. The copied
parser metadata API above supports an RDM migration of the former, but it
cannot preserve public SMA AST/token signatures. The latter must migrate to
RDM-owned DTOs from `PowerShellInvocationResult` snapshots. Existing RDM
custom actions that set live `Connection`, `RDM`, `Core`, or `Result` objects
on a `Runspace`; VMware PowerCLI code that retains live runspaces/typed
`PSObject` base objects; and interactive/remoting code using `PSHost`,
`PSCredential`, `PSSession`, or connection-info types remain incompatible.
They require a separate RDM migration or an existing SMA-backed path, never an
SMA forwarding assembly or generic proxy bridge.

### Copied session variables

`PowerShellSession.SetVariable(string, PowerShellValue)`,
`TryGetVariable(string, out PowerShellValue?)`, and `RemoveVariable(string)`
are the migration path for declarative `Result`, `Core`, `connection`, and
option snapshots. Variable names are ASCII identifiers up to 64 characters.
Values are copied on every set/get, capped at 64 KiB, at most 64 entries per
array/property bag, and at most eight nested levels. Cycles and unsupported CLR
objects are rejected before the native call.

The operation is rejected with `Backpressure` while that session has a pending
or running async operation. Synchronous operations and variable operations use
the process-global operation lock, so they cannot race a runspace mutation.
`TryGetVariable` cannot return a `PSVariable`, adapted object, methods, or any
value that cannot be encoded as the documented copied graph; such values return
`UnsupportedValue` rather than being stringified.

### Value-only projection readers

`PowerShellValue` is an immutable copied DTO, not an opaque serialized
`PSObject`. Its `TryGetString`, numeric, Boolean, date/time, GUID, URI, and
byte-array readers, together with `GetArray`, `GetPropertyBag`, and
`TryGetProperty`, let an RDM adapter map data into its own DTOs without parsing
display text or snapshot JSON. Arrays and property bags always return copied,
immutable collections; bytes are cloned on every read.

### Script parameter metadata

`PowerShellRuntime.ParseScriptParameters(script)` supports RDM script-editor
metadata without a facade reference to `System.Management.Automation.Language`.
The supplied script is an argument to a fixed payload-local
`Parser.ParseInput` helper and is never executed. On success the immutable DTO
contains each parameter's name, declared type spelling, default-expression
spelling, mandatory flag, description/help text, `ValidateSet` entries, aliases,
parameter-set name/position/pipeline flags, and copied
`ValidatePattern`/`ValidateRange`/`ValidateLength`/`ValidateCount` argument
spelling. On syntax failure it returns copied message, error-ID, and
source-offset DTOs instead of parameters. The API rejects scripts above 64 KiB
and fails rather than truncating more than 16 parameters, 16 total
`ValidateSet` values, eight aliases/parameter sets/validations per parameter,
four validation arguments, 32 combined metadata records, or 16 parse errors.
It deliberately does not return tokens, AST nodes, evaluated attribute
expressions, or arbitrary parser objects.

Output projection is deliberately narrower than input values. A
`PowerShellObjectSnapshot` can contain either one scalar or a property bag of
up to 16 scalar properties. Callers must check `IsPropertyBagTruncated` and
`DroppedPropertyEntryCount` before treating a projected bag as complete. For
example, a value-only replacement for reading a custom resolver result is:

```csharp
PowerShellObjectSnapshot record = result.Output.Records.Single();
if (record.IsPropertyBagTruncated ||
    record.PropertyBag is not { } properties ||
    !properties.TryGetProperty("Name", out PowerShellValue? name) ||
    !name!.TryGetString(out string? connectionName))
{
    throw new InvalidOperationException("The resolver result was not a complete DTO projection.");
}
```

This is an application-owned projection contract. It is not a substitute for
`PSObject.BaseObject`, adapted members, methods, PowerCLI CLR types, or generic
typed invocation.

`PowerShellSnapshotReader.GetCompleteProperties` makes the completeness check
mandatory before returning a property bag, while
`PowerShellSnapshotReader.CreateDisplaySnapshot` builds an immutable copied
text view of every output/stream channel and reports whether it is complete.
Neither helper recreates an SMA object, parses CLIXML as a CLR graph, or treats
display text as a typed DTO.

### Replacing injected RDM data

Value-only scripts can migrate their former injected objects into explicit
copied variables: for example, `FfiConnection`, `FfiCoreOptions`, and
`FfiResult`. The caller sets input bags before invoking a session builder. A
script may replace `FfiResult` with a scalar-only `PSCustomObject` or
hashtable, and the caller retrieves its bounded property-bag snapshot with
`TryGetVariable` or `TryGetPropertyBag` after the invocation completes.
`SetPropertyBag` and `InvokeAndReadVariable` are convenience APIs for this
same copied-only flow. The package NativeAOT contract covers this update/readback
flow.

The old object's methods do not migrate this way. A reviewed operation that
needs parent-side behavior must instead be a named `rdm.*` capability with
explicit arguments and a copied response. A script that needs a local resource
may create it inside an opaque `PowerShellSession` and use it only through
approved script or module commands in that same serialized session; no
`PSSession`, `Runspace`, PowerCLI object, or resource handle is returned to the
parent.

### Exact local module identities

`sessionPolicy.moduleImports` alone is not sufficient. Every declared import
must have exactly one `sessionPolicy.moduleIdentities` entry:

```json
{
  "name": "Example.Module",
  "manifestPath": "Modules/Example.Module/Example.Module.psd1",
  "version": "1.2.3",
  "sha256": "<the same SHA-256 recorded for manifestPath in files>"
}
```

Activation verifies the manifest file hash before CoreCLR starts, requires the
identity manifest to reside beneath a declared `modulePaths` root, and reads its
literal `ModuleVersion` without executing it. Session creation resolves a
requested module name only through this identity and imports the exact pinned
manifest, not a name found by ambient `PSModulePath`. Missing, duplicate,
outside-root, mismatched-hash, or mismatched-version identities are rejected
with `SessionPolicyViolation` or a manifest activation error.

This is a local module identity contract, not a module sandbox or a PowerCLI
claim. The complete-closure requirement hashes every regular module file, and
`win-x64` local built-in module smoke coverage is the only
validated configuration. Do not enable PowerCLI, arbitrary module
initialization, native loading, or remoting modules until their full payload
dependency closure has separately passed this contract.

### Declarative recipes and result schemas

`PowerShellCommandRecipe` describes one bounded command name plus copied
parameters; `PowerShellScriptRecipe` describes bounded source text. Both accept
an optional timeout and `PowerShellResultSchema`. `PowerShellRuntime.Invoke`
and `InvokeAsync` apply the timeout using the normal cancellation contract; a
timed-out recipe has no successful partial result.

The schema verifies output record bounds, an optional allowed copied scalar
kind set, required copied property names, error policy, and snapshot
completeness. It rejects a missing, wrong-kind, or truncated result rather than
silently treating display strings as data. This is intended for migration
adapters that already own the expected DTO contract, not as a general result
deserializer.

`PowerShellCommandPolicy` is an opt-in application guardrail for recipe command
allowlists, parameter counts, script opt-in, and source size. It is explicitly
not a PowerShell sandbox: once arbitrary script source is allowed, that script
retains the full authority of its payload session. Applications must keep their
real authorization, payload manifest, session policy, and capability decisions
outside this advisory policy.

### Payload-owned module adapter contract

A future adapter for a specific reviewed module must be payload-owned and
manifest-pinned: its exact `.psd1`, version, hash, dependencies, and native
closure belong in the selected payload manifest. Its public contract may accept
and return only documented copied `PowerShellValue` DTOs and may use only
explicitly registered capability names. It must prove its target payload and
module closure through a NativeAOT package smoke before being advertised.

This is not a generic module bridge. It cannot expose PowerCLI CLR objects,
remoting/PSRP, credentials, live RDM objects, arbitrary callbacks, or an
ambient `PSModulePath`. Any such behavior requires a separately designed
boundary rather than an exception to this contract.

### Credentials remain a hard rejection boundary

There is no `PowerShellSecret`, `PowerShellCredential`, `SecureString`,
password-specific parameter, or serialized credential path in this API.
General string values remain ordinary DTO data and must never be used as a
secret transport. Passing a credential to an arbitrary script would let that
script write, encode, or throw it into ordinary result/error streams. A
one-time ABI buffer does not solve that exfiltration problem, and accepting one
would make the facade's redaction and zeroization guarantees false. RDM flows
that require `PSCredential` remain on the existing SMA/process implementation.

The threat model assumes the invoked payload script, a module it loads, and
PowerShell formatting/error behavior are all capable of observing a bound
credential. They can return it through output, an error, a progress message, a
serialization transform, or process memory that cannot be synchronously
zeroized from the parent. The native boundary therefore makes no promise that
could be defeated by script behavior: it transfers no credential material,
does not retain secret handles, and rejects the dedicated API before payload
binding. This protects the DTO/snapshot contract, not a caller that manually
places a password in a general string or script.

### Bounded capability RPC and host interaction

Capability RPC is feature-gated, disabled by default, and deliberately narrow.
`PowerShellRuntime.RegisterCapabilities` copies an immutable set of at most 16
definitions into Rust. Each definition has a canonical lowercase `rdm.*` or
`host.*` name, exact argument arity and value-kind schemas, allowed response
kinds, permissions, input/output byte caps, and a deadline. `WithCapabilities`
attaches one registration to one builder invocation. The payload creates the
temporary `$DpsCapabilities` only for that invocation and removes it before the
result is returned.

```csharp
var definition = new PowerShellCapabilityDefinition(
    "rdm.get-connection-name",
    Array.Empty<PowerShellCapabilityArgumentSchema>(),
    new[] { PowerShellValueKind.String },
    PowerShellCapabilityPermission.Read,
    maximumInputBytes: 64,
    maximumOutputBytes: 256,
    deadline: TimeSpan.FromSeconds(5));
using var capabilities = runtime.RegisterCapabilities(new[]
{
    new PowerShellCapabilityBinding(definition, connectionNameHandler),
});
using var command = session.CreatePowerShell()
    .AddScript("$DpsCapabilities.Invoke('rdm.get-connection-name')")
    .WithCapabilities(capabilities);
PowerShellInvocationResult result = command.Invoke();
```

The cross-boundary path is tagged `PowerShellValue` data only:

```text
script -> $DpsCapabilities.Invoke -> payload-local bridge -> Rust
       -> static AOT-safe managed callback -> typed handler -> Rust -> script
```

The opaque registration ID maps only to the private immutable definition set.
The payload cannot receive a parent CLR object, `GCHandle`, raw function
pointer, delegate, member path, or arbitrary method name. Unknown names,
duplicates, schema/type mismatches, malformed/deep/oversized values, inactive
registrations, handler exceptions, and invalid response kinds fail rather than
falling back to PowerShell reflection or string serialization. Diagnostics
from a failed callback are intentionally generic.

Dispatch is synchronous from the payload's point of view. Its deadline is
capped by the registered definition; cancellation, stopping the operation, or
unregistering a registration cancels the managed handler cooperatively and
rejects a late response. Rust rejects any FFI call attempted from the same
capability callback, preserving global operation serialization and avoiding a
callback-to-session deadlock. Registrations use `SafeHandle` ownership, and
disposal revokes new dispatches while cancellation is requested for active
ones.

`PowerShellHostInteraction` is a library of typed capability definitions:
`host.write-text`, `host.report-progress`, `host.read-line`,
`host.prompt-choice`, and `host.prompt-multiple-choice`. Handlers choose the
actual UI policy. They accept only bounded text/property-bag/choice DTOs and
never carry a credential or secure-string response. This reuses the capability
mechanism; there is no second callback vtable, direct console I/O, or `PSHost`
surface.

`PowerShellRdmCapabilities` provides harmless standard schemas for
`rdm.get-connection-name`, `rdm.get-connection-display`, and
`rdm.report-status`; callers opt in by registering handlers and no capability
is available merely because its definition exists. For
`host.report-progress`, handlers can use
`PowerShellHostInteraction.ParseProgressUpdate` to validate explicit copied
`ActivityId`, `ParentActivityId`, activity/status text, percentage, remaining
seconds, and completion fields. It intentionally never derives typed progress
from a generic progress-stream display string.

This remains intentionally unlike a generic `RDM`/`Core`/`connection` object
bridge. Put copied `Result`, `Core`, and `connection` data in session
variables. Promote only a reviewed operation, such as the example
`rdm.get-connection-name` or `rdm.report-status`, to an enumerated capability.
Scripts cannot call arbitrary proxy methods or obtain the original managed
objects.

### One-shot administrative command pilot

The first additive RDM pilot is a local, no-credential `Stop-Computer` or
`Restart-Computer` command. Use `-WhatIf` in development and CI; real reboot or
shutdown execution must require an application-owned feature flag and explicit
operator approval.

```csharp
using var cancellation = new CancellationTokenSource();
cancellation.CancelAfter(TimeSpan.FromSeconds(15));
using PowerShell command = runtime.Create()
    .AddCommand("Restart-Computer")
    .AddParameter("ComputerName", computerName)
    .AddParameter("WhatIf");

try
{
    PowerShellInvocationResult result = await command.InvokeAsync(cancellation.Token);
    foreach (PowerShellInvocationError error in result.Errors.Records)
    {
        Console.Error.WriteLine(error.Message);
    }
}
catch (PowerShellInvocationException exception)
{
    foreach (PowerShellInvocationError error in exception.InvocationResult.Errors.Records)
    {
        Console.Error.WriteLine(error.Message);
    }
    throw;
}
```

The caller owns activation, cancellation, and disposal; it contains no
`System.Management.Automation` reference. A destructive path is not part of
this repository's normal tests.

## Explicit limitations

This is not binary or source compatible with the full PowerShell SDK. The
facade cannot transfer live `PSObject`, runspace, delegate, custom `PSHost`, or
arbitrary CLR object values. It exposes no SMA types. Secret and credential
transfer is an explicit rejection boundary:
`PowerShellSecretTransfer.Policy` is `Rejected` and
`PowerShellSecretTransfer.ThrowNotSupported()` throws a typed exception.
`SecureString`, `PSCredential`, raw serialized credentials, and secret DTOs
are deliberately not accepted. Because arbitrary PowerShell can expose any
input it receives, this facade cannot truthfully guarantee secret redaction,
serializer safety, or zeroable managed credential lifetime. Do not put secrets
in tagged values or session variables; snapshots are copied general output, not
a secret store. Snapshot values are
intentionally copied data rather than live objects: they do not provide
arbitrary property access, opaque object handles, callbacks, secret transfer,
pools, remoting, or generic CLR object serialization.

Sessions do not accept live `PSHost` values, arbitrary callbacks/delegates, credentials,
runspace connection information, remoting transports/providers, nested live
PowerShell, steppable pipelines, generic CLR values, or arbitrary initial
session-state objects. Capability callbacks are registered immutable DTO
handlers only; there is no host callback vtable, callback rooting,
prompt/credential callback, or arbitrary delegate/object bridge. Custom
remoting and actual session pools remain permanent non-goals until a separate
bounded architecture proves their lifecycle and concurrency semantics.

Only an explicit payload root is supported. The FFI initialization path does not
resolve `pwsh` from `PATH`. The activation manifest and its SHA-256 pin must
come from application-controlled deployment configuration, not from the
payload being selected.

## Package preview

`Devolutions.MultiPwsh.Sdk` is a preview package with native assets for the
same release RIDs as `Devolutions.MultiPwsh.Cli`: `win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, `linux-arm`, `osx-x64`, and `osx-arm64`. The Rust
cdylib is inert by default; consumers set a matching `RuntimeIdentifier` and
`DevolutionsMultiPwshSdkEnabled=true` to stage that RID's native library beside
the executable. The package deliberately does not carry a PowerShell payload.
Release CI consumes the package and ABI-smokes native staging/loading on
`win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`; this does not validate
PowerShell payload activation on those platforms.
Its NuGet version and Windows native DLL `FileVersion`/`ProductVersion` match
the `multi-pwsh` CLI release version. Only the `win-x64` RID currently has
end-to-end NativeAOT payload smoke coverage; publishing another asset is not a
claim that its payload activation topology has been validated.

The package includes the optional hash-pinned activation template
`contentFiles/any/any/devolutions-pwsh-payload.manifest.template.json` as a
schema template only. When opting into verified staging, replace every placeholder after installing
the external payload, write the completed manifest outside an immutable
PowerShell installation when needed, hash the final manifest bytes, and store
that hash in the application's protected deployment configuration. Do not ship
the template as a trusted manifest and do not rely on a file merely being
present.

### RDM packaging and rollout contract

The RDM caller must opt in to `win-x64` native staging, deploy the selected
PowerShell payload and every approved module separately, produce a completed
hash-pinned manifest, and pass the manifest path and SHA-256 through
application-controlled configuration. The RDM application must not add a
transitive `System.Management.Automation`, `Microsoft.PowerShell.SDK`, or
payload runtime asset to the NativeAOT facade path. Activation reports a
deterministic incompatibility if another selected payload/runtime already owns
the process.

`win-arm64`, Linux, and macOS have packaged native assets but remain unvalidated
for payload activation and must not be advertised as supported until each has a
real NativeAOT activation smoke test. The current module identity smoke test covers only exact
manifest-pinned local built-in modules; it does not prove PowerCLI or binary
module support.

Adopt this as an additive RDM feature-flagged pilot:

1. Start with `RemoteToolsManager` one-shot local administrative commands in
   safe `-WhatIf`/mocked contract tests.
2. Move value-only custom resolver and script flows only after their state fits
   copied variables and their credential requirement is absent.
3. Add persistent approved-module local sessions only after their actual
   payload/module dependencies are exercised.
4. Keep existing SMA-backed paths for live RDM object injection,
   `PSObject.BaseObject`, typed generic invoke/PowerCLI, remoting, SSH/WinRM,
   and process-host scenarios.

The non-negotiable non-goals are live SMA identity, `PSObject.BaseObject`,
generic typed invocation, `PSDataCollection` live events, custom
`PSHost`/`PSCmdlet`/provider/remoting inheritance, private
`RemoteSessionNamedPipeServer` reflection and `Enter-PSHostProcess`, PSRP/WSMan
or SSH custom transport, and transparent binary PowerShell-module proxies.

## Validation

Build the managed bindings before Rust because `pwsh-host` embeds the Release
bindings assembly:

```powershell
dotnet build dotnet/bindings/Devolutions.PowerShell.SDK.Bindings.csproj -c Release
cargo test -p pwsh-sdk-ffi --all-targets

$env:PWSH_FFI_PAYLOAD = 'C:\Program Files\PowerShell\7'
cargo test -p pwsh-sdk-ffi explicit_payload_round_trip_uses_the_exported_abi -- --ignored
cargo test -p pwsh-sdk-ffi explicit_payload_async_operations_are_terminal_and_lifetime_safe -- --ignored
cargo test -p pwsh-sdk-ffi explicit_payload_lifecycle_stress_enforces_serialization_and_lifetime_contracts -- --ignored
cargo test -p pwsh-sdk-ffi explicit_payload_increment_6_sessions_are_bounded_and_lifetime_safe -- --ignored

dotnet publish dotnet/nativeaot-sample/NativeAotFfiSample.csproj -c Release
./dotnet/nativeaot-sample/bin/Release/net8.0/win-x64/publish/NativeAotFfiSample.exe
# Or select a payload explicitly, with an optional hash-pinned manifest:
./dotnet/nativeaot-sample/bin/Release/net8.0/win-x64/publish/NativeAotFfiSample.exe <payload>
./dotnet/nativeaot-sample/bin/Release/net8.0/win-x64/publish/NativeAotFfiSample.exe <payload> <manifest> <manifest-sha256>
```

The ignored Rust test and NativeAOT sample require a real payload root containing
`pwsh.dll`, `pwsh.runtimeconfig.json`, and
`System.Management.Automation.dll`.
