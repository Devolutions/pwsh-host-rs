# In-process PowerShell FFI experiment

`pwsh-host` can be exposed through the `pwsh-sdk-ffi` Rust `cdylib`.
The library receives a caller-selected PowerShell payload directory, or resolves
`pwsh` from `PATH`, then loads that payload's `hostfxr`, initializes `pwsh.dll`, injects
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

### Experimental live-object probe

`PowerShellRuntime.CreateLiveObjectProbe` is a deliberately narrow,
non-production `IUnknown` experiment. It creates a payload-local
`PSCustomObject` with one numeric `Count` property and projects only
`GetCount` and `Increment` through a source-generated, fixed-GUID COM
interface. It is portable userland `ComWrappers` use: it does not use Windows
COM activation, registry metadata, BSTR/automation marshalling, or classic
runtime COM interop.

The `net10.0` facade receives an opaque interface pointer, creates a unique
generated RCW, and immediately releases the producer's transit reference. The
RCW owns its reference until `PowerShellLiveObjectProbe.Dispose`, which calls
`ComObject.FinalRelease` after unregistering the payload identity entry; using
the wrapper afterward throws
`ObjectDisposedException`. `PowerShell.AddArgument(PowerShellLiveObjectProbe)`
returns the original pointer to the payload binding, which accepts only
pointers it created and restores the original payload object rather than
accepting arbitrary foreign `IUnknown` values.

This is currently validated only by the Windows x64 NativeAOT sample with an
explicit PowerShell 7.4 payload. It must be used only after the creating
invocation has completed and never from a capability callback or while a
pipeline is active. The existing process-global operation lock does not make
arbitrary direct proxy calls, reentrancy, runspace affinity, arbitrary member
invocation, or cross-platform payload activation supported. The probe is not a
general mechanism for exposing `PSObject`, SMA objects, delegates, hosts,
credentials, reflection, or ordinary managed object references to the
consumer runtime.

### Experimental reverse session-object probe

`PowerShellSessionObjectProbe` proves the inverse direction. A .NET 10-owned
broker exports an `IUnknown` transfer reference to
`PowerShellSession.SetLiveObjectVariable`; the payload creates a unique
generated RCW, wraps it in a small PowerShell-visible payload proxy, and
places that proxy in the session's `SessionStateProxy`. PowerShell can invoke
only the proxy's fixed `Count` and `Increment` members, whose bodies call the
generated interface and run in the .NET 10 broker.

The payload retains each projected RCW by variable name and calls
`ComObject.FinalRelease` when its value is replaced through either session
setter, removed with `RemoveVariable`, or the session owner is released. The
.NET 10 facade releases its transfer reference immediately after a successful
or failed assignment, but the managed probe itself must outlive its
session-variable binding. Remove the variable before disposing the probe.

This is a narrow Windows x64 / PowerShell 7.4 acceptance experiment, not
support for arbitrary .NET 10 objects in PowerShell session state. PowerShell
scripts can remove, overwrite, or alias the variable independently. The
payload reconciles its tracked bindings when each pipeline ends: it releases a
proxy once no session variable references it and retains it while an alias
still exists. The session mutation lock still rejects changes during an active
pipeline, and the proxy must not call back into the FFI API.

### Preview contract packs

`PowerShellLiveObjectContract` and `PowerShellLiveObject<TContract>` provide
the preview path for application-owned, consumer-to-session live objects.
Applications compile one source-generated `IUnknown` interface into their
`net10.0` consumer and their trusted `net8.0` payload adapter. The contract
contains a non-empty interface GUID, major/minor version, and direction flags;
methods must use preserved HRESULT returns and explicitly ABI-safe values.

The consumer activates with
`PowerShellRuntime.Activate(contractPacks)` or
`PowerShellRuntime.Activate(payloadDirectory, contractPacks)`. Each
`PowerShellLiveObjectContractPack` names an absolute payload-adapter assembly
and its assembly-qualified static export type. During activation, the host
loads the adapter into the selected payload runtime and invokes its
`GetLiveObjectContractPackV1` unmanaged export. That export supplies a bounded
contract descriptor list plus create/release callbacks for PowerShell-visible
payload proxies. The payload registry rejects unknown, duplicate,
direction-incompatible, or metadata-incompatible contracts before it projects
an `IUnknown`.

The `dotnet/live-object-test-pack` project is an acceptance fixture, not a
shipping application contract. It proves that a separately compiled net8
adapter can project a .NET 10 broker into a `PowerShellSession`, expose
ordinary scalar members, nested broker members, and a C# indexer as
PowerShell-visible properties and `Children[index]` access. The acceptance
broker mutates a nested child through both `Primary` and `Children[0]` to
verify that separately projected RCWs retain the same consumer-owned child
identity. It retains live state across invocations and preserves a
session-variable alias.

Contract packs are deliberately explicit and trusted application code. They
cannot expose raw pointers through PowerShell or turn arbitrary CLR objects
into contracts. The current external-pack registry supports only the proven
consumer-to-session direction. Payload-to-consumer object contracts,
cross-session identity, compatibility relaxation, and SDK-owned
application-specific schema generation remain future work.

The transfer is `IUnknown` only: the NativeAOT consumer creates a
source-generated CCW, Rust retains no object graph and forwards its transfer
reference, and the trusted payload adapter creates source-generated RCWs before
wrapping them in ordinary fixed managed proxy classes. PowerShell binds those
managed proxy members; it does not bind a raw COM object and requires neither
`IDispatch` nor a dynamic proxy. This proves direct in-process generated-COM
calls on the serialized local-pipeline path, not apartment marshalling. Contract
brokers must therefore be thread-safe and treated as MTA/agile-only until an
application supplies and validates an explicit apartment dispatcher or free
threaded marshaler strategy.

### Live-object execution policy

Direct calls from a PowerShell-visible payload proxy to a consumer broker are
allowed only when the broker records bounded consumer-owned intent. Such a
call must not invoke the native FFI, start another pipeline, access payload
session state, call capability RPC, or mutate UI/runspace-affine state.

Contract packs must treat the source-generated interface as the complete
allowlist: interface IIDs, member ordinals, parameter/return types, numeric
ranges, collection bounds, HRESULTs, and any authorization decision are
application-owned static contract data. The SDK does not inspect a COM vtable
for a per-member schema, redact secrets, or supply a cancellation token to a
direct COM call. Do not include `SecureString`, credential, arbitrary object,
or secret-bearing string members. Reads must complete synchronously within
application-defined bounds; writes should only stage validated intent and
commit it after the PowerShell invocation reaches a successful terminal state.

The native bridge tracks active pipeline execution process-wide. General FFI
calls fail with `Backpressure` while any pipeline is active, including calls
forwarded to another thread by a broker; calls from a capability callback also
fail before they acquire runtime or operation locks. Cancellation, operation
polling/waiting, session snapshots/events, and immutable-result reads remain
available while a pipeline runs. Session-variable reads and mutations fail
with `Backpressure` before lock acquisition. These rules prevent deadlock;
they do not make a live PowerShell object or runspace callable from a broker.
A session-affine dispatcher is required before supporting that category.

## Lifecycle and ABI

- The direct activation exports are process-global and accept one canonical
  payload directory. Repeating the same activation succeeds; selecting a
  different payload returns `MULTI_PWSH_INCOMPATIBLE_PAYLOAD`.
- The native product ABI reports its compatible version and feature flags through
  `multi_pwsh_get_abi_info`; the managed package and native asset ship together
  and use the unversioned `multi_pwsh_*` exports. The injected managed function
  table independently reports its compatibility version.
- The public native ABI remains v2. `ReadStreamBatch` is enabled only when the
  `LIVE_STREAM_POLLING` feature bit is present, so consumers can reject a native
  asset that lacks polling before calling its additive export.
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

## Payload selection and activation

`PowerShellRuntime.Activate()` resolves `pwsh` from `PATH` and activates its
containing payload directory. `PowerShellRuntime.Activate(payloadDirectory)`
activates exactly the caller-selected existing directory. The native host
canonicalizes that directory before loading its local `hostfxr`.

The SDK deliberately does not read, package, generate, or validate a payload
metadata file. It does not pin a PowerShell, hostfxr, CoreCLR, module, or file
version. The application selects the PowerShell installation and is responsible
for any provenance or integrity controls its deployment requires.

One payload is process-global. Repeating activation for the same canonical
directory succeeds; selecting a different directory after initialization fails
with `IncompatiblePayload`. Runtime switching and unloading are unsupported.

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

### Live stream polling

`PowerShellInvocationOperation.ReadStreamBatch(afterSequence, maximumRecords)`
is a polling-only, copied-record view of a running operation. `afterSequence`
is a record-sequence cursor where `0` starts at the beginning; a cursor beyond
the newest sequence or a limit outside `1..32` is rejected. Each returned
record contains only its stream kind, a native-owned monotonic nonzero
sequence, a fixed-bounded copied display-text field, and a truncation bit. It
never carries `PSObject`, `ErrorRecord`, SMA state, CLR objects, credentials,
callbacks, or pipeline input.

The batch is immutable and includes `NextSequence`, operation state and
terminal status, cursor-loss metadata, total/dropped/source-dropped counters,
and truncation metadata. The native operation owns a 32-record ring; reading a
cursor older than its retained range succeeds with `IsCursorLost` and
`LostRecordCount` rather than silently skipping data. The payload-to-native
capture path is separately bounded, and source eviction is reported through
`SourceDroppedRecordCount`. Stream records can be read while an operation is
running and remain readable after completion or cancellation, but cancellation
still wins: `GetResult` never converts partial live output into a successful
result.

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

Session configuration is supplied directly by the application. Requested module
paths and working directories must be absolute existing directories; module
imports are bounded names resolved only beneath the supplied module paths. The
payload-local authorization manager permits external scripts only beneath those
module roots. This is not a general authorization bypass or an additional
module search path. `CurrentRunspace` rejects all configuration so the ABI does
not mutate ambient application state.

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
Then use `runtime.Create()` (or the equivalent `PowerShell.Create()`
process-global entry point) to construct builders. The runtime object reports
the selected path and negotiated ABI/features; it does not permit selecting a
second payload or unloading the selected runtime.

## Facade scope

This SDK offers copied-DTO compatibility, not SMA compatibility. The table
below defines the generic managed facade boundary.

| Application need | Status | FFI replacement and boundary |
| --- | --- | --- |
| Explicit payload activation, hostfxr startup, one runtime per process | Implemented | Direct payload or `PATH` activation and an opaque `PowerShellRuntime`; only `win-x64` has NativeAOT smoke evidence. |
| Script or named-command execution with scalar parameters | Implemented | `AddScript`, `AddCommand`, `AddParameter`, and bounded `PowerShellValue` inputs. No raw `object`, `PSObject`, or `SecureString` overload exists. |
| Script parameter declarations and syntax errors | Implemented, copied-only | `PowerShellRuntime.ParseScriptParameters` passes the input to payload-local `Parser.ParseInput` as data, never executable pipeline text. It returns bounded parameter/parse-error DTOs, not SMA AST or token objects. |
| Output, errors, diagnostics, warning/progress streams | Implemented | Immutable bounded `PowerShellInvocationResult` snapshots. Safe scalar and property-bag projections are read through typed copied-value readers, never live SMA collections. |
| Timeout, cancellation, async completion, deterministic disposal | Implemented | `InvokeAsync(CancellationToken)`, `BeginInvoke`, `Wait`, `Stop`, and `SafeHandle` ownership. Cancellation wins and never returns a partial success result. |
| Long-lived local state | Implemented | Opaque local `PowerShellSession` plus serialized builders. It is not a pool or a remoting session. |
| `SessionStateProxy.SetVariable` for value data | Implemented, copied-only | `SetVariable`, `TryGetVariable`, and `RemoveVariable` transfer bounded tagged values only. No methods, proxies, handles, or CLR identity survive the boundary. |
| Application-selected local modules | Implemented, local-only | Each requested import is resolved by name beneath a caller-supplied module root. This does not validate PowerCLI or remoting dependencies. |
| `PSCredential` parameter | Intentionally unsupported | Arbitrary scripts can emit or transform a supplied credential. The DTO result model cannot guarantee redaction or a zeroable managed lifetime. |
| Enumerated application capability calls and bounded host interaction | Implemented, opt-in | A registered `PowerShellCapabilitySet` makes only declared typed calls available through the temporary payload-local `$DpsCapabilities` object. `PowerShellHostInteraction` supplies schemas for text, progress, line, and choice interactions; it is not a `PSHost` proxy. |
| PowerCLI typed return objects, PSRP/WinRM/SSH, pools, and transports | Unsupported | Retain the existing SMA/process paths. No CLR type, transport, or live session crosses the facade. |

This is not a drop-in replacement for `Devolutions.PowerShell.SDK`. Existing
applications that expose SMA parser AST/token types, `Collection<PSObject>`,
live runspaces, typed `PSObject` base objects, or interactive/remoting APIs
must retain an SMA-backed path or migrate to application-owned copied DTOs from
`PowerShellInvocationResult` snapshots. This facade never becomes an SMA
forwarding assembly or a generic proxy bridge.

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
`TryGetProperty`, let an application adapter map data into its own DTOs without parsing
display text or snapshot JSON. Arrays and property bags always return copied,
immutable collections; bytes are cloned on every read.

### Script parameter metadata

`PowerShellRuntime.ParseScriptParameters(script)` supports application script-editor
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

### Replacing injected application data

Value-only scripts can map application-owned input DTOs into explicit
copied variables: for example, `FfiConnection`, `FfiCoreOptions`, and
`FfiResult`. The caller sets input bags before invoking a session builder. A
script may replace `FfiResult` with a scalar-only `PSCustomObject` or
hashtable, and the caller retrieves its bounded property-bag snapshot with
`TryGetVariable` or `TryGetPropertyBag` after the invocation completes.
`SetPropertyBag` and `InvokeAndReadVariable` are convenience APIs for this
same copied-only flow. The package NativeAOT contract covers this update/readback
flow.

The old object's methods do not migrate this way. A reviewed operation that
needs parent-side behavior must instead be a named `app.*` capability with
explicit arguments and a copied response. A script that needs a local resource
may create it inside an opaque `PowerShellSession` and use it only through
approved script or module commands in that same serialized session; no
`PSSession`, `Runspace`, PowerCLI object, or resource handle is returned to the
parent.

### Local module roots

Session configuration accepts module names and absolute existing module roots.
Imports are resolved only beneath those roots, and the payload-local
authorization manager permits external scripts only from those locations. This
is not a module sandbox or a PowerCLI claim. Do not enable PowerCLI, arbitrary
module initialization, native loading, or remoting modules until their runtime
behavior has passed a separate application-owned validation.

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
retains the full authority of its payload session. Applications must keep their real authorization and capability decisions outside
this advisory policy.

### Payload-owned module adapter contract

A future adapter for a specific reviewed module must be application-owned. Its
public contract may accept and return only documented copied `PowerShellValue`
DTOs and may use only explicitly registered capability names. It must prove its
target payload and module behavior through a NativeAOT package smoke before
being advertised.

This is not a generic module bridge. It cannot expose PowerCLI CLR objects,
remoting/PSRP, credentials, live application objects, arbitrary callbacks, or an
ambient `PSModulePath`. Any such behavior requires a separately designed
boundary rather than an exception to this contract.

### Credentials remain a hard rejection boundary

There is no `PowerShellSecret`, `PowerShellCredential`, `SecureString`,
password-specific parameter, or serialized credential path in this API.
General string values remain ordinary DTO data and must never be used as a
secret transport. Passing a credential to an arbitrary script would let that
script write, encode, or throw it into ordinary result/error streams. A
one-time ABI buffer does not solve that exfiltration problem, and accepting one
would make the facade's redaction and zeroization guarantees false. Applications
that require `PSCredential` must use a separately designed boundary.

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
definitions into Rust. Each definition has a canonical lowercase
namespace-qualified name, exact argument arity and value-kind schemas, allowed response
kinds, permissions, input/output byte caps, and a deadline. `WithCapabilities`
attaches one registration to one builder invocation. The payload creates the
temporary `$DpsCapabilities` only for that invocation and removes it before the
result is returned.

```csharp
var definition = new PowerShellCapabilityDefinition(
    "app.get-label",
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
    .AddScript("$DpsCapabilities.Invoke('app.get-label')")
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

Applications define their own capability schemas; no application-specific
catalog is part of this SDK. For
`host.report-progress`, handlers can use
`PowerShellHostInteraction.ParseProgressUpdate` to validate explicit copied
`ActivityId`, `ParentActivityId`, activity/status text, percentage, remaining
seconds, and completion fields. It intentionally never derives typed progress
from a generic progress-stream display string.

This remains intentionally unlike a generic application-object bridge. Put
copied input and result data in session variables. Promote only a reviewed
operation, such as the example `app.get-label`, to an enumerated capability.
Scripts cannot call arbitrary proxy methods or obtain original managed objects.

For a connection-edit flow, the NativeAOT sample sets a copied `connection`
property bag (`Id`, `Name`, and `Host`) and attaches the declared
`rdm.stage-connection-patch` capability only to the invocation that uses it.
The capability accepts exactly one bounded property bag with `ConnectionId`
and `DisplayName`, validates both fields, records a consumer-owned patch
intent, and returns only `{ Accepted = true }`. This replaces a narrowly
reviewed operation, not an injected `$RDM` object: scripts cannot discover
other members, obtain the original connection, or invoke another application
operation. The sample's two-second deadline, 256-byte input limit, and
64-byte response limit are application-contract choices that production
callers must set for their own reviewed intent DTO.

### One-shot administrative command example

A local, no-credential `Stop-Computer` or `Restart-Computer` command is a
bounded one-shot example. Use `-WhatIf` in development and CI; real reboot or
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

Direct initialization resolves `pwsh` from `PATH` by default or accepts an
explicit payload root. The SDK does not use a payload metadata file or pin a
PowerShell version.

## Package preview

`Devolutions.MultiPwsh.Sdk` is a `net10.0` preview package with native assets for the
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

### Deployment and rollout contract

The application must opt in to `win-x64` native staging, deploy the selected
PowerShell payload and any selected modules separately. The application must not add a
transitive `System.Management.Automation`, `Microsoft.PowerShell.SDK`, or
payload runtime asset to the NativeAOT facade path. Activation reports a
deterministic incompatibility if another selected payload/runtime already owns
the process.

`win-arm64`, Linux, and macOS have packaged native assets but remain unvalidated
for payload activation and must not be advertised as supported until each has a
real NativeAOT activation smoke test. Current coverage does not prove PowerCLI
or binary module support.

   Adopt this as an additive application feature-flagged deployment:

   1. Start with one-shot local administrative commands in safe
   `-WhatIf`/mocked contract tests.
   2. Move value-only resolver and script flows only after their state fits copied
   variables and their credential requirement is absent.
3. Add persistent approved-module local sessions only after their actual
   payload/module dependencies are exercised.
   4. Keep existing SMA-backed paths for live application object injection,
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
./dotnet/nativeaot-sample/bin/Release/net10.0/win-x64/publish/NativeAotFfiSample.exe
# Or select a payload explicitly:
./dotnet/nativeaot-sample/bin/Release/net10.0/win-x64/publish/NativeAotFfiSample.exe <payload>
```

The ignored Rust test and NativeAOT sample require a real payload root containing
`pwsh.dll`, `pwsh.runtimeconfig.json`, and
`System.Management.Automation.dll`.
