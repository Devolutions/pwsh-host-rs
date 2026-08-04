# Generated Bridge Contract v2 over DBC

This document specifies the supported generated Bridge Contract v2 attachment.
It supersedes the preliminary COM-carrier path for an attached bridge
invocation. The public NativeAOT facade remains independent of
`System.Management.Automation`; all PowerShell and contract-pack interaction
below occurs only in the payload.

## Construction and lifetime

1. The NativeAOT host activates a payload with the generated contract pack,
   creates a `PowerShellBridgeChannel`, and creates one
   `PowerShellBridgeBinding` from its generated dispatcher.
2. The host attaches that binding and one ASCII-like variable name to a
   `PowerShell` builder or a session-created builder. Attachment opens no
   lease, starts no pipeline, and does not expose a `PSObject` to the host.
3. Attachment configures the builder's one broker slot with the **bridge**
   role. A bridge role and raw `$DpsBroker` are mutually exclusive for the
   invocation. A second broker attachment fails; the supported way to use raw
   broker traffic and a generated bridge is two invocations.
4. The payload resolves the one declared contract pack, gives it the fixed
   payload-owned broker sink, and requires exactly one matching declaration.
   It verifies the contract interface ID, major/minor version, declared
   descriptor hash, and generated maximum request and reply sizes before the
   builder can start. Missing packs, missing declarations, mismatches, or a
   channel too small for the declared frames fail attachment closed.
5. At asynchronous invocation start, the payload asks the generated pack to
   create the root wrapper, opens its lease through DBC, and installs that root
   in the requested variable. It saves and restores a pre-existing variable of
   the same name. The root exists only while that invocation is active.
6. Invocation cleanup publishes the local tombstone, sends `Close` when a
   channel is still usable, restores the saved variable, releases the pack
   proxy, and disposes the payload binding. Cleanup is idempotent. A failed
   close never makes an escaped wrapper live again.

Only the async invocation paths are legal for an attached bridge. Synchronous
invocation is rejected by the existing broker guard before a pipeline can
block waiting for a host dispatcher.

## Fixed DBC frames

The DBC body for every generated bridge frame starts with this fixed
little-endian route header:

```text
0  u64  bindingId       non-zero, allocated by PowerShellBridgeChannel
8  ...  Bridge Contract v2 request frame
```

`bindingId` is channel-scoped and non-reused. It is not an object handle,
contract discovery protocol, CLR identity, or application-defined selector.
The receiving `PowerShellBridgeChannel` resolves it to exactly one generated
dispatcher. A second registration of an active binding ID is impossible.

The DBC `kind` is fixed by the SDK:

| Kind | DBC mode | Inner frame kinds |
| --- | --- | --- |
| `BridgeRequest` | request/reply | `Open`, `Invoke`, `Release`, `Close` |
| `BridgeEvent` | one-way | `Event`, `ReliableEvent` |

The existing v2 32-byte request frame and 8-byte reply frame remain unchanged,
except `Close` is an explicit request/reply frame kind. `Open` carries the
payload descriptor hash; the generated dispatcher compares it before it
allocates a lease. `Close` carries the active lease tuple and performs the
first-wins lease transition. `Event` and `ReliableEvent` are admitted,
authorized, and dispatched using their generated static member entries, but
never receive a reply.

A request/reply DBC response carries a fixed bridge envelope:

```text
0  i32  bridgeStatus
4  u32  reserved = 0
8  ...  v2 reply frame, present only when bridgeStatus == 0
```

The host returns all generated dispatcher outcomes in this envelope. It uses
DBC `reply_error` only for infrastructure failures that have no bridge
status, such as malformed routing or an unavailable dispatcher. This preserves
the distinction between malformed input, denied access, contract mismatch,
bounds, and table exhaustion.

### Terminal observation before worker dispatch

`PowerShellBridgeChannel.TryReceive` creates one bounded terminal-observation
lease for every copied request before releasing its pump-thread-only delivery
handle. The resulting `PowerShellBridgeDispatch` exposes only copied terminal
metadata: current state, mapped terminal status, and the channel-relative
terminal epoch. It deliberately does **not** expose a `CancellationToken`, a
payload object, or a native delivery handle.

A worker can query or wait on that lease without calling the payload,
PowerShell, or an application dispatcher. `DispatchDetailed()` performs one
last pre-handler check. If cancellation, timeout, close, or another terminal
transition won before application work starts, it returns
`HandlerStarted = false`, `ReplyAccepted = false`, and the terminal state; the
generated authorizer and handler are not called. If termination wins after a
handler starts, it is reported in the dispatch result and the attempted reply
is rejected first-wins. No observation can revive a correlation.

Observation leases are explicitly released when a dispatch completes, is
discarded, or is rejected. Native allocation is bounded to four leases per
correlation and `max_inflight * 4` per channel. A channel close marks existing
leases `Aborted` and leaves them readable until release, while unknown,
one-way, cross-channel, duplicate, and released leases fail closed.

## Bounds and dispatch

The source generator computes fixed maximum request and reply frame sizes for
each contract. Attachment requires:

```text
channel MaximumBodyBytes >= 8 route bytes + maximum request frame bytes
channel MaximumBodyBytes >= 8 envelope bytes + maximum reply frame bytes
```

It also checks the fixed `Open`, `Release`, `Close`, and event shapes. The
payload allocates only the declared maximum for the selected member; it never
allocates the channel maximum for a short property read. Frames exceeding a
declared or channel bound are rejected, never truncated.

`PowerShellBrokerChannel.TryReceive` copies and releases a delivery handle.
The host must then hand the returned `PowerShellBridgeDispatch` to an
application worker and return promptly to pumping. The worker may call
`Dispatch` later; it resolves the binding, validates the route and generated
frame, calls the generated dispatcher, and replies by correlation ID. It must
not synchronously invoke PowerShell, call unrelated FFI operations, or wait on
pipeline work. A late, duplicate, cancelled, timed-out, closed, or
cross-channel reply simply reports that it did not complete a live
correlation; it cannot affect a later request.

Events are one-way, at-most-once, and subject to the DBC's documented
coalescing/drop accounting. Events do not bypass the generated contract:
their route, descriptor member, lease, object handle, and authorization are
validated before their generated handler runs.

### Retained reliable event streams

`[BridgeReliableEvent]` is distinct from the existing lossy
`[BridgeEvent]`. It declares a `void` shape, explicit `Permission`, static
`OrderingKey`, and `MaximumRetainedEvents` in `1..64`. The bound is part of
the canonical Host/Payload descriptor bytes, so a retention mismatch fails the
normal descriptor-hash handshake before the lease opens.

The payload posts a `ReliableEvent` one-way frame exactly as it posts an
ordinary generated event. `PowerShellBridgeChannel.TryReceive` copies it off
the pump, assigns a stream-local monotonic sequence, and retains the copied
frame under the closed key:

```text
bindingId + leaseId + generation + objectId + memberId
```

No generated authorizer or handler runs on the pump. The host discovers only
already-observed streams for its known binding, calls `Read(afterSequence,
maximumEvents)`, queues each copied `PowerShellBridgeReliableEvent.Dispatch()`
to an application worker, then calls `Acknowledge(sequence)` after handoff.
Acknowledgement releases every retained frame through that cursor; a read can
be retried before acknowledgement. The stream identity exposes only copied
numeric identifiers and its records expose only copied bounded frames.

The channel retains at most 32 streams and 256 frames total, in addition to
each generated member's `MaximumRetainedEvents` bound. A stream that reaches
either retention bound changes once to `RetentionOverflow`; it preserves
already retained frames, counts all later drops, and admits no later frame.
The channel also exposes aggregate rejected-frame and rejected-stream counts.
There is no unbounded queue, replacement of unacknowledged records, callback,
delegate, or producer-side retry protocol. This is reliable **until its
explicit terminal state**, not a promise of lossless delivery under a host that
does not acknowledge bounded records.

An observed `Close` for the matching binding/lease/generation terminates its
streams as `LeaseClosed` and releases unacknowledged copied frames. Channel
disposal similarly terminates every remaining stream as `ChannelClosed`.
Lease-close and binding disposal atomically detach those terminal streams from
the channel's active 32-slot table, so a completed invocation cannot exhaust
future stream capacity. A host that needs terminal cursor/drop state retains
the stream reference it observed while that lease was active; that detached
reference remains readable as a terminal snapshot until it is disposed.
Binding, generation, object, and member mismatches cannot access an active
stream.
Cancellation and attachment teardown stop queued frames in the native broker;
the attachment closes admission before it terminalizes queued frames, so a
payload callback that resolved its bridge context before cancellation cannot
enqueue behind cleanup. The sole post-cancellation exception is the
payload-sink-validated, fixed generated `Close` request needed to expose the
lease tombstone; no application request or event can use that path. Retained
streams terminalize under the same lease-close rule. A host must treat a
terminal stream as final and must never dispatch a released or dropped frame.
A record released before its worker begins is gated from the generated
authorizer and handler; an already-started handler may finish, but it cannot
admit another record or revive the closed lease.

### Finite jobs and report pages

The generator represents a finite job as an ordinary closed `[BridgeObject]`,
not as `Task`, `ValueTask`, a delegate, or a CLR object transfer. A contract
declares static `Start`, `Status`, `Cancel`, and paged-result member ordinals;
`Start` returns a lease-scoped job handle with a generated object type ID and
explicit release ordinal. Each later operation carries the same lease and
generation checks as every other bridge object. A stale, cross-lease, released,
or closed job handle is rejected without reaching its handler.

Status and page values are copied `[BridgeData]`. A report page declares its
fixed schema as a bounded list of flat column declarations (bounded names and a
closed generated enum for each column type) and its values as a bounded list of
flat typed row declarations. A row field corresponds to the contract-declared
column ordinal and type; there is no dynamically typed cell union or
runtime-discovered property. The same page carries fixed cursor, completion,
truncation, and total-count fields. The generator rejects direct nested data
fields, nested collections, recursive row/page graphs, data graphs deeper than
the bridge graph bound, dynamic columns, `DataTable`, `PSObject`, and arbitrary
object bags. Collection and field bounds are encoded in the descriptor and
rechecked by both generated codecs; over-bound pages fail rather than truncate.

`Cancel` is only a generated request to the application handler. It is not a
promise that external work has stopped, and a terminal job result may race a
cancel request. Applications must expose the resulting terminal status through
their static status/page values; the SDK does not imply task, process, or
external-side-effect atomicity.

### Generated finite operations and snapshot pages

An opt-in finite operation is a closed child `[BridgeObject]` marked with
`[BridgeFiniteOperation]`. Its declaration names exactly one read-only status
member, one direct execute-only `void Cancel()` member, and one read-only page
member. It also declares a bounded admission lifetime. The status data carries
an explicitly identified `bool IsTerminal` field.

The page result is a copied `[BridgeData]` marked with
`[BridgeSnapshotPage]`. It has bounded static column and row lists plus
statically typed `Guid NextCursor`, `long SnapshotRevision`, `long
PermissionRevision`, `long CursorLeaseExpiresAtMilliseconds`, `bool
IsTerminal`, `bool IsGap`, `bool IsOverflow`, `bool IsTruncated`, and `long
TotalCount` fields. The generated page member has exactly the copied input
shape `ReadPage(Guid cursor, long snapshotRevision, long permissionRevision)`.
The generator rejects a missing field, a mismatched tag, a dynamic/handle row,
an unbounded collection, or any extra operation member.

The dispatcher allocates finite-operation child handles with a nonzero
cryptographically random object identifier, bounded to 1,024 issued operation
identities for one lease. An operation is bound to the admitted parent object;
releasing that parent invalidates every owned operation child. Its deadline is
checked before each later admission, so a request admitted before expiry can
finish but no request at or after expiry reaches application code. Repeating
`Cancel` returns the same generated success reply without calling the handler
again. These are local bridge-lifetime guarantees, not a promise that external
work was stopped.

The operation handler owns the application snapshot and permission revisions:
it creates opaque cursor values, binds them to the caller's snapshot and
authorization revision, checks their expiry, and returns a terminal gap or
overflow page rather than silently replaying or skipping records. The generated
per-member authorizer still runs before every first cancel, status, and page
call. The SDK deliberately does not persist cursors, infer a permission
revision, retain report objects, or claim durable checkpoint semantics; those
need product authentication and retention policy.

Finite-operation and snapshot-page metadata is encoded in descriptor format
version 4. It changes neither the public native ABI nor the required V1 payload
binding table. A host and payload pack for an annotated contract must be
regenerated together: a version-3 descriptor or a different finite-operation
layout produces a different exact descriptor hash and fails `Open` before a
lease is allocated. An additive contract member must also advance the declared
contract minor version on both the host dispatcher and payload-pack
registration; bridge declaration matching remains exact rather than a
compatible-version range.

### Host finite-operation page registry

`PowerShellFiniteOperationRegistry<TPage>` is a separate, host-only primitive
for retaining the copied result of a finite application operation before a
generated bridge handler exposes it. It has no SMA dependency and does not
attach a page or an operation to a payload by itself. The application supplies
one closed `TPage`, an `IPowerShellFinitePageCodec<TPage>` that copies that
fixed shape, and an `IPowerShellFinitePageAccessValidator` that revalidates the
opaque `PowerShellFiniteOperationBinding` before both admission and every page
read.

The registry bounds operation count, pages, items per page, each page's byte
count, registry-wide retained items (65,536), and registry-wide retained bytes
(16 MiB). `TryStart` issues a cryptographically random owner-scoped
`PowerShellFiniteOperationId`; `TryComplete` accepts only an
`IReadOnlyList<TPage>` that the codec copies within those bounds. A page cursor
is an opaque random operation-scoped capability. Cursors cannot cross an
operation, and all cursors are invalidated when their owner, lease, registry,
or retained terminal lifetime ends.

An active deadline wins before a later completion or cancellation is admitted.
The first terminal transition wins: success, cancellation, or timeout. A
successful operation's pages remain readable only through its configured
terminal lease lifetime. The registry rechecks the supplied access validator
for every page read. `SnapshotInvalidated` and `PermissionDenied` terminalize a
previously successful retained result and return no page; a caller must start a
new operation under a newly admitted binding. `TryCancel` is idempotent only
for cancellation: later cancels return `Cancelled`, while a cancel after a
success or timeout returns that existing terminal state. `TryRelease`, owner
disposal, and registry disposal cancel and remove retained state.

The `PowerShellFiniteOperationLease.CancellationToken` is a cooperative signal
for the host's own operation work only. It is not a DBC callback capability:
its registrations must not synchronously invoke PowerShell, invoke foreign FFI
work, or wait on a pipeline. The registry does not dispatch jobs, authorize
application actions, serialize arbitrary objects, persist a checkpoint, or
make external effects atomic. Generated bridge contracts still need a static
handler, per-member authorization, and fixed wire DTOs to expose an accepted
registry result to a payload.

## Authorization, mutation, and errors

Generated dispatch preserves this order: structural frame validation, reply
bound validation, lease/object admission, per-member authorization, handler
dispatch, and reply encoding. Every getter, setter, method, release, close,
and event has an explicit generated member shape; there is no reflection,
dynamic member lookup, arbitrary `PSObject`, generic CLR object, delegate, or
JSON transfer.

The generated `I...Authorizer.IsAuthorized(in ...CallContext)` hook is called
for every generated request and event immediately before the application
handler. Declared permissions are inputs to that hook, not authorization
decisions. This SDK hook does not provide RDM authorization, persistence, or
an ambient permission system.

`Mutation.Staged` remains rejected by the generator until a generated handler
can perform an explicit stage/validate/commit/abort lifecycle for one member
call. Direct mutations are only local handler operations; no bridge operation
promises atomicity with external side effects. A handler that needs external
transactions must reject the operation or use an application-defined staged
workflow outside this bridge.

Lease closure increments the generation and tombstones all object IDs in one
transition. Unknown, stale, closed, cross-lease, or released handles and
authorization denial all use the same generated access-denied status, so a
script cannot probe object existence. An admitted handler reference may finish
after close, but no later frame is admitted.

## Compatibility and exclusions

The payload binding table remains one required V1 table. The bridge attachment
slot remains appended and feature bit 26 remains required. Reliable generated
events add payload feature bit 28 without a new callback slot: the existing
payload-owned bridge sink carries the distinct bounded inner frame kind. Rust
still performs header-first table-size, feature, and non-null slot validation;
it now requires bit 28 as well. An older, undersized, or mismatched payload is
rejected before any bridge channel opens. There is no optional slot, fallback
carrier, ABI version negotiation, descriptor subsetting, or best-effort
compatibility path.

Terminal observation is a native public-ABI capability (feature bit 27), not a
payload-table capability: it observes an existing DBC correlation and never
calls into payload code. Its four appended native exports use the fixed
24-byte `BrokerTerminalInfo` structure. Hosts must require the feature bit
before creating a bridge channel, so a native asset missing the exports fails
closed rather than producing an unobservable bridge dispatch.

Reliable generated events are also a native public-ABI capability (feature bit
28). The public ABI remains v2: no field is shifted and no public native
function is added. A host requires bit 28 before it can create a bridge
channel, preventing a new facade from accepting a payload whose V1 table
cannot recognize the reliable-event descriptor and wire kind.

Structured observed presentation is independent of bridge routing. Feature bit
29 appends one V1 payload-table value-copy slot for the fixed progress
projection used by `PowerShellObservedInvocation.ReadPresentation`; it does not
add a bridge frame, callback, or object transfer. The Rust host validates that
slot and bit before dereferencing it, so an old or undersized payload fails
activation rather than degrading progress to parsed display text.

The attachment does not add credential or secret transfer, `PSHost`/RawUI,
remoting/PSRP, named-pipe console attachment, pools or concurrent sessions,
PowerCLI compatibility, generic RDM feature flags, or generic RDM
authorization/persistence. It is a finite, generated, bounded local bridge
only.
