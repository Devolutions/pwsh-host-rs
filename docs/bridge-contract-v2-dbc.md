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
| `BridgeEvent` | one-way | `Event` |

The existing v2 32-byte request frame and 8-byte reply frame remain unchanged,
except `Close` is an explicit request/reply frame kind. `Open` carries the
payload descriptor hash; the generated dispatcher compares it before it
allocates a lease. `Close` carries the active lease tuple and performs the
first-wins lease transition. `Event` is admitted, authorized, and dispatched
using its generated static member entry, but never receives a reply.

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
slot is appended, its feature bit is required on both payload and native sides,
and the Rust loader performs the existing header-first size, feature, and
non-null slot checks before reading it. An older, undersized, or mismatched
payload is rejected before any bridge channel opens. There is no optional slot,
fallback carrier, ABI version negotiation, descriptor subsetting, or
best-effort compatibility path.

The attachment does not add credential or secret transfer, `PSHost`/RawUI,
remoting/PSRP, named-pipe console attachment, pools or concurrent sessions,
PowerCLI compatibility, generic RDM feature flags, or generic RDM
authorization/persistence. It is a finite, generated, bounded local bridge
only.
