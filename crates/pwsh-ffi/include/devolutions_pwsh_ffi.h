#ifndef DEVOLUTIONS_PWSH_FFI_H
#define DEVOLUTIONS_PWSH_FFI_H

#include <stddef.h>
#include <stdint.h>

#define DPS_PWSH_ABI_VERSION 2u
#define DPS_PWSH_ABI_MINIMUM_COMPATIBLE_VERSION 2u
#define DPS_PWSH_FEATURE_STRUCTURED_INVOCATION_ERRORS (UINT64_C(1) << 0)
#define DPS_PWSH_FEATURE_PER_CALL_DIAGNOSTICS (UINT64_C(1) << 1)
#define DPS_PWSH_FEATURE_UTF8_SPANS (UINT64_C(1) << 2)
#define DPS_PWSH_FEATURE_IMMUTABLE_RESULTS (UINT64_C(1) << 3)
#define DPS_PWSH_FEATURE_TAGGED_VALUES (UINT64_C(1) << 4)
#define DPS_PWSH_FEATURE_COMMAND_OPTIONS (UINT64_C(1) << 5)
#define DPS_PWSH_FEATURE_BOUNDED_INPUT (UINT64_C(1) << 6)
#define DPS_PWSH_FEATURE_INVOCATION_METADATA (UINT64_C(1) << 7)
#define DPS_PWSH_FEATURE_ASYNC_OPERATIONS (UINT64_C(1) << 8)
#define DPS_PWSH_FEATURE_PAYLOAD_MANIFEST (UINT64_C(1) << 9)
#define DPS_PWSH_FEATURE_SESSIONS (UINT64_C(1) << 10)
#define DPS_PWSH_FEATURE_SESSION_POLLING (UINT64_C(1) << 11)
#define DPS_PWSH_FEATURE_SESSION_POOL_REJECTION (UINT64_C(1) << 12)
#define DPS_PWSH_FEATURE_SNAPSHOT_PROJECTIONS (UINT64_C(1) << 13)
#define DPS_PWSH_FEATURE_SESSION_CONFIGURATION (UINT64_C(1) << 14)
#define DPS_PWSH_FEATURE_SESSION_VARIABLES (UINT64_C(1) << 15)
#define DPS_PWSH_FEATURE_CAPABILITY_RPC (UINT64_C(1) << 16)
#define DPS_PWSH_CALL_RESULT_DIAGNOSTIC_TRUNCATED UINT32_C(1)
#define DPS_PWSH_RESULT_TERMINATING_FAILURE UINT32_C(1)
#define DPS_PWSH_RESULT_SEQUENCE_TRUNCATED (UINT32_C(1) << 1)
#define DPS_PWSH_RESULT_STREAM_TRUNCATED UINT32_C(1)
#define DPS_PWSH_RESULT_RECORD_FIELDS_TRUNCATED UINT32_C(1)
#define DPS_PWSH_RESULT_RECORD_SCALAR_VALUE_PRESENT (UINT32_C(1) << 1)
#define DPS_PWSH_RESULT_RECORD_PROPERTY_BAG_PRESENT (UINT32_C(1) << 2)
#define DPS_PWSH_RESULT_RECORD_PROPERTY_BAG_TRUNCATED (UINT32_C(1) << 3)
#define DPS_PWSH_RESULT_RECORD_TYPE_NAMES_TRUNCATED (UINT32_C(1) << 4)
#define DPS_PWSH_RESULT_RECORD_ERROR_TARGET_VALUE_PRESENT (UINT32_C(1) << 5)

enum dps_pwsh_status {
    DPS_PWSH_SUCCESS = 0,
    DPS_PWSH_BUFFER_TOO_SMALL = 1,
    DPS_PWSH_INVALID_ARGUMENT = -1,
    DPS_PWSH_NOT_INITIALIZED = -2,
    DPS_PWSH_INCOMPATIBLE_PAYLOAD = -3,
    DPS_PWSH_INVALID_HANDLE = -4,
    DPS_PWSH_HOST_FAILURE = -5,
    DPS_PWSH_MANAGED_FAILURE = -6,
    DPS_PWSH_PANIC = -7,
    DPS_PWSH_INPUT_NOT_COMPLETED = -8,
    DPS_PWSH_BACKPRESSURE = -9,
    DPS_PWSH_UNSUPPORTED_VALUE = -10,
    DPS_PWSH_OPERATION_CANCELLED_STATUS = -11,
    DPS_PWSH_OPERATION_NOT_TERMINAL = -12,
    DPS_PWSH_PAYLOAD_MANIFEST_INVALID = -13,
    DPS_PWSH_PAYLOAD_UNTRUSTED = -14,
    DPS_PWSH_PAYLOAD_HASH_MISMATCH = -15,
    DPS_PWSH_PAYLOAD_INCOMPATIBLE = -16,
    DPS_PWSH_UNSUPPORTED_CAPABILITY = -17,
    DPS_PWSH_SESSION_POLICY_VIOLATION = -18
};

enum dps_pwsh_payload_trust_policy {
    DPS_PWSH_REQUIRE_HASH_PINNED_MANIFEST = 0,
    DPS_PWSH_ALLOW_UNTRUSTED_LOCAL_DEVELOPMENT = 1
};

enum dps_pwsh_value_kind {
    DPS_PWSH_VALUE_NULL = 0,
    DPS_PWSH_VALUE_STRING_UTF8 = 1,
    DPS_PWSH_VALUE_SWITCH = 2,
    DPS_PWSH_VALUE_BOOLEAN = 3,
    DPS_PWSH_VALUE_SIGNED_INTEGER = 4,
    DPS_PWSH_VALUE_UNSIGNED_INTEGER = 5,
    DPS_PWSH_VALUE_DOUBLE = 6,
    DPS_PWSH_VALUE_DECIMAL_UTF8 = 7,
    DPS_PWSH_VALUE_BYTES = 8,
    DPS_PWSH_VALUE_DATETIME = 9,
    DPS_PWSH_VALUE_DATETIME_OFFSET = 10,
    DPS_PWSH_VALUE_GUID_UTF8 = 11,
    DPS_PWSH_VALUE_URI_UTF8 = 12,
    DPS_PWSH_VALUE_ARRAY = 13,
    DPS_PWSH_VALUE_PROPERTY_BAG = 14
};

enum dps_pwsh_invocation_state {
    DPS_PWSH_INVOCATION_COMPLETED = 1,
    DPS_PWSH_INVOCATION_TERMINATED = 2
};

/*
 * Async operation states are monotonic. An operation reaches exactly one
 * terminal state: completed, cancelled, or failed. A cancellation request
 * wins over a concurrent successful invocation, so cancelled operations never
 * expose a potentially partial result snapshot.
 */
enum dps_pwsh_operation_state {
    DPS_PWSH_OPERATION_PENDING = 1,
    DPS_PWSH_OPERATION_RUNNING = 2,
    DPS_PWSH_OPERATION_COMPLETED = 3,
    DPS_PWSH_OPERATION_CANCELLED = 4,
    DPS_PWSH_OPERATION_FAILED = 5
};

/*
 * A session owns a reusable runspace. CurrentRunspace borrows the managed
 * thread's current default runspace and therefore accepts only the unconfigured
 * defaults. NewRunspace owns a new local runspace and may use the bounded
 * options below. No remoting connection, PSHost, callbacks, or arbitrary
 * InitialSessionState object crosses this ABI.
 */
enum dps_pwsh_session_runspace_mode {
    DPS_PWSH_SESSION_CURRENT_RUNSPACE = 0,
    DPS_PWSH_SESSION_NEW_RUNSPACE = 1
};

enum dps_pwsh_session_initial_configuration {
    DPS_PWSH_SESSION_CONFIGURATION_DEFAULT = 0,
    DPS_PWSH_SESSION_CONFIGURATION_CONSTRAINED_LANGUAGE = 1
};

enum dps_pwsh_session_history_mode {
    DPS_PWSH_SESSION_HISTORY_DISABLED = 0,
    DPS_PWSH_SESSION_HISTORY_ENABLED = 1
};

/*
 * Execution policy is deliberately a narrow, noninteractive configuration
 * choice; it is not a PowerShell security boundary.
 */
enum dps_pwsh_session_execution_policy {
    DPS_PWSH_SESSION_EXECUTION_POLICY_DEFAULT = 0,
    DPS_PWSH_SESSION_EXECUTION_POLICY_RESTRICTED = 1
};

/*
 * Preference settings intentionally expose only non-interactive values.
 * Inquire, Suspend, Break, and custom preference variables are unsupported
 * because the ABI has no prompt/callback channel.
 */
enum dps_pwsh_session_preference {
    DPS_PWSH_SESSION_PREFERENCE_INHERIT = 0,
    DPS_PWSH_SESSION_PREFERENCE_CONTINUE = 1,
    DPS_PWSH_SESSION_PREFERENCE_SILENTLY_CONTINUE = 2,
    DPS_PWSH_SESSION_PREFERENCE_STOP = 3
};

enum dps_pwsh_session_state {
    DPS_PWSH_SESSION_OPENED = 1,
    DPS_PWSH_SESSION_RUNNING = 2,
    DPS_PWSH_SESSION_CLOSED = 3,
    DPS_PWSH_SESSION_FAULTED = 4
};

#define DPS_PWSH_SESSION_EVENTS_TRUNCATED UINT32_C(1)

enum dps_pwsh_invocation_error_field {
    DPS_PWSH_INVOCATION_ERROR_MESSAGE = 0,
    DPS_PWSH_INVOCATION_ERROR_FULLY_QUALIFIED_ID = 1,
    DPS_PWSH_INVOCATION_ERROR_CATEGORY = 2,
    DPS_PWSH_INVOCATION_ERROR_EXCEPTION_TYPE = 3
};

enum dps_pwsh_result_stream {
    DPS_PWSH_RESULT_STREAM_OUTPUT = 0,
    DPS_PWSH_RESULT_STREAM_ERROR = 1,
    DPS_PWSH_RESULT_STREAM_WARNING = 2,
    DPS_PWSH_RESULT_STREAM_VERBOSE = 3,
    DPS_PWSH_RESULT_STREAM_DEBUG = 4,
    DPS_PWSH_RESULT_STREAM_INFORMATION = 5,
    DPS_PWSH_RESULT_STREAM_PROGRESS = 6
};

enum dps_pwsh_result_record_field {
    DPS_PWSH_RESULT_RECORD_DISPLAY_TEXT = 0,
    DPS_PWSH_RESULT_RECORD_TYPE_NAMES = 1,
    DPS_PWSH_RESULT_RECORD_FULLY_QUALIFIED_ERROR_ID = 2,
    DPS_PWSH_RESULT_RECORD_CATEGORY = 3,
    DPS_PWSH_RESULT_RECORD_EXCEPTION_TYPE = 4,
    DPS_PWSH_RESULT_RECORD_INVOCATION_NAME = 5,
    DPS_PWSH_RESULT_RECORD_POSITION_MESSAGE = 6,
    DPS_PWSH_RESULT_RECORD_SCRIPT_STACK_TRACE = 7,
    DPS_PWSH_RESULT_RECORD_CATEGORY_REASON = 8,
    DPS_PWSH_RESULT_RECORD_CATEGORY_ACTIVITY = 9,
    DPS_PWSH_RESULT_RECORD_CATEGORY_TARGET_NAME = 10,
    DPS_PWSH_RESULT_RECORD_CATEGORY_TARGET_TYPE = 11,
    DPS_PWSH_RESULT_RECORD_COMMAND_NAME = 12,
    DPS_PWSH_RESULT_RECORD_INVOCATION_LINE = 13,
    DPS_PWSH_RESULT_RECORD_OFFSET_IN_LINE = 14,
    DPS_PWSH_RESULT_RECORD_PIPELINE_LENGTH = 15,
    DPS_PWSH_RESULT_RECORD_PIPELINE_POSITION = 16,
    DPS_PWSH_RESULT_RECORD_ERROR_DETAILS_MESSAGE = 17,
    DPS_PWSH_RESULT_RECORD_RECOMMENDED_ACTION = 18,
    DPS_PWSH_RESULT_RECORD_TARGET_DISPLAY_TEXT = 19
};

/*
 * Snapshot value slots return a copied dps_pwsh_data_value kind and payload.
 * Scalar and property-bag values apply to output snapshots; ErrorTarget applies
 * to error snapshots. Complex values and enumerables are never enumerated or
 * represented by a slot. Property bags contain only copied note properties
 * whose values are safe tagged scalars.
 */
enum dps_pwsh_result_record_value_slot {
    DPS_PWSH_RESULT_RECORD_VALUE_SCALAR = 0,
    DPS_PWSH_RESULT_RECORD_VALUE_PROPERTY_BAG = 1,
    DPS_PWSH_RESULT_RECORD_VALUE_ERROR_TARGET = 2
};

/*
 * ABI v2 uses caller-owned sized structures. Set each `size` member to
 * sizeof(the structure) before calling. UTF-8 spans permit data == NULL only
 * when len == 0; all byte values must be valid UTF-8 and must not contain NUL.
 *
 * Every v2 operation accepts a call result. Diagnostics are copied into the
 * caller-provided storage; diagnostic_required always reports the full byte
 * count and diagnostic_written reports the copied byte count. The library
 * never retains any caller-provided pointer after an operation returns.
 */
struct dps_pwsh_abi_info {
    uint32_t size;
    uint32_t abi_version;
    uint64_t feature_flags;
    uint32_t minimum_compatible_abi_version;
    uint32_t reserved;
};

struct dps_pwsh_utf8_span {
    const uint8_t* data;
    size_t len;
};

/*
 * A data value is a tag plus bounded caller-owned payload bytes. `size` must
 * be sizeof(struct dps_pwsh_data_value), `flags` and `reserved` must be zero,
 * and the library never retains `data`. Scalar payloads use the documented
 * fixed encodings. Arrays and property bags use the nested binary envelope
 * documented in docs/in-process-ffi.md; they are not JSON or object handles.
 */
struct dps_pwsh_data_value {
    uint32_t size;
    uint32_t kind;
    uint32_t flags;
    uint32_t reserved;
    const uint8_t* data;
    size_t data_len;
};

/*
 * A trusted activation provides a SHA-256 pin for the complete manifest bytes.
 * The manifest then pins every payload file. The local-development policy is
 * deliberately unsafe: it accepts an unpinned manifest only and is never a
 * signature validation mechanism. flags and reserved must be zero.
 */
struct dps_pwsh_payload_activation {
    uint32_t size;
    uint32_t trust_policy;
    uint32_t flags;
    uint32_t reserved;
    struct dps_pwsh_utf8_span payload_path;
    struct dps_pwsh_utf8_span manifest_path;
    struct dps_pwsh_utf8_span manifest_sha256;
};

struct dps_pwsh_call_result {
    uint32_t size;
    int32_t status;
    uint32_t flags;
    uint32_t reserved;
    uint8_t* diagnostic;
    size_t diagnostic_capacity;
    size_t diagnostic_required;
    size_t diagnostic_written;
};

/*
 * A capability registration is a copied declarative PropertyBag schema plus
 * static cdecl callbacks. The native library copies the schema immediately and
 * retains only the function pointers until dps_pwsh_v2_capability_release.
 * `dispatch` receives only a registered canonical name and a bounded tagged
 * Array; it must not call any dps_pwsh_* API. `cancel` is advisory and must
 * return immediately. Neither callback may retain input/output pointers.
 */
typedef int32_t (*dps_pwsh_capability_dispatch_callback)(
    uint64_t registration_handle,
    uint64_t invocation_id,
    struct dps_pwsh_utf8_span name,
    const struct dps_pwsh_data_value* arguments,
    uint32_t argument_count,
    uint32_t deadline_milliseconds,
    uint32_t* response_kind,
    uint8_t* response_buffer,
    size_t response_capacity,
    size_t* response_required,
    struct dps_pwsh_call_result* result);
typedef void (*dps_pwsh_capability_cancel_callback)(
    uint64_t registration_handle,
    uint64_t invocation_id);

struct dps_pwsh_capability_registration {
    uint32_t size;
    uint32_t flags;
    const struct dps_pwsh_data_value* definitions;
    dps_pwsh_capability_dispatch_callback dispatch;
    dps_pwsh_capability_cancel_callback cancel;
};

/*
 * All fields are declarative copies. allowed_module_path is retained for
 * source compatibility and must be empty for new consumers; use
 * allowed_module_paths instead. Initial variables are a PropertyBag data
 * value, module imports and paths are Array-of-String data values, and
 * environment is a PropertyBag whose values are String data values. Every
 * collection is capped at 32 entries. Module paths/imports, working directory,
 * and environment keys must be approved by the activated payload manifest's
 * sessionPolicy; rejected policy diagnostics never echo supplied values.
 *
 * The appended fields preserve the v2 prefix: a caller compiled against the
 * 64-byte prefix may set size to 64 and receives empty/default appended
 * configuration. New callers set size to sizeof(struct dps_pwsh_session_options).
 * flags, reserved, and configuration_flags must be zero. CurrentRunspace requires every setting
 * except runspace_mode to be its zero/default value, avoiding mutation of an
 * application's ambient runspace.
 */
struct dps_pwsh_session_options {
    uint32_t size;
    uint32_t runspace_mode;
    uint32_t initial_configuration;
    uint32_t history_mode;
    uint32_t error_preference;
    uint32_t warning_preference;
    uint32_t verbose_preference;
    uint32_t debug_preference;
    uint32_t information_preference;
    uint32_t flags;
    uint32_t reserved;
    struct dps_pwsh_utf8_span allowed_module_path;
    uint32_t execution_policy;
    uint32_t configuration_flags;
    struct dps_pwsh_data_value initial_variables;
    struct dps_pwsh_data_value module_imports;
    struct dps_pwsh_data_value allowed_module_paths;
    struct dps_pwsh_utf8_span working_directory;
    struct dps_pwsh_data_value environment;
};

/*
 * This is a copied, point-in-time record. event_count is capped at 32 and
 * flags reports event-ring overflow. Session activity is serialized per
 * process/runspace; it is not a signal that callers may run pipelines
 * concurrently on a runspace.
 */
struct dps_pwsh_session_snapshot {
    uint32_t size;
    uint32_t state;
    uint32_t runspace_state;
    uint32_t flags;
    uint32_t active_pipeline_count;
    uint32_t event_count;
    uint64_t invocation_count;
    uint64_t history_count;
};

/*
 * A pool boundary is present so consumers receive a deterministic answer rather
 * than a deceptive pool. No pool handle is returned in this ABI version:
 * dps_pwsh_v2_session_pool_create always returns UNSUPPORTED_CAPABILITY after
 * validating this bounded configuration.
 */
struct dps_pwsh_session_pool_options {
    uint32_t size;
    uint32_t minimum_sessions;
    uint32_t maximum_sessions;
    uint32_t flags;
    uint32_t reserved;
};

uint32_t dps_pwsh_abi_version(void);
uint64_t dps_pwsh_feature_flags(void);

int32_t dps_pwsh_get_abi_info(struct dps_pwsh_abi_info* info);
/*
 * Compatibility-only local-development initializer. It loads the conventional
 * devolutions-pwsh-payload.json beside payload_path without a manifest pin.
 * Use dps_pwsh_v2_initialize_payload for every deployed application.
 */
int32_t dps_pwsh_v2_initialize_utf8(
    struct dps_pwsh_utf8_span payload_path,
    struct dps_pwsh_call_result* result);
/*
 * Validates canonical paths, manifest pin/schema, target RID/architecture,
 * all required payload-file hashes, and declared runtime/bindings compatibility
 * before hostfxr is loaded. The manifest itself may live outside the payload;
 * its SHA-256 pin is the trusted activation input.
 */
int32_t dps_pwsh_v2_initialize_payload(
    const struct dps_pwsh_payload_activation* activation,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_create(
    uint64_t* handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_release(
    uint64_t handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_command_utf8(
    uint64_t handle,
    struct dps_pwsh_utf8_span command,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_script_utf8(
    uint64_t handle,
    struct dps_pwsh_utf8_span script,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_argument_utf8(
    uint64_t handle,
    struct dps_pwsh_utf8_span argument,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_parameter_string_utf8(
    uint64_t handle,
    struct dps_pwsh_utf8_span name,
    struct dps_pwsh_utf8_span value,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_parameter_i64(
    uint64_t handle,
    struct dps_pwsh_utf8_span name,
    int64_t value,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_command_utf8_local(
    uint64_t handle,
    struct dps_pwsh_utf8_span command,
    uint32_t use_local_scope,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_script_utf8_local(
    uint64_t handle,
    struct dps_pwsh_utf8_span script,
    uint32_t use_local_scope,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_argument_value(
    uint64_t handle,
    const struct dps_pwsh_data_value* value,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_parameter_value(
    uint64_t handle,
    struct dps_pwsh_utf8_span name,
    const struct dps_pwsh_data_value* value,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_parameter_switch(
    uint64_t handle,
    struct dps_pwsh_utf8_span name,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_input_value(
    uint64_t handle,
    const struct dps_pwsh_data_value* value,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_complete_input(
    uint64_t handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_reset_input(
    uint64_t handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_add_statement(
    uint64_t handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_clear(
    uint64_t handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_invoke_utf8(
    uint64_t handle,
    uint8_t* buffer,
    size_t buffer_len,
    size_t* required_len,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_get_invocation_error_count(
    uint64_t handle,
    uint32_t* error_count,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_copy_invocation_error_field_utf8(
    uint64_t handle,
    uint32_t error_index,
    uint32_t field,
    uint8_t* buffer,
    size_t buffer_len,
    size_t* required_len,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_stop(
    uint64_t handle,
    struct dps_pwsh_call_result* result);
/*
 * Capability registrations are opt-in and runtime scoped. `definitions` is a
 * bounded tagged PropertyBag containing only schema metadata, never handlers
 * or CLR object identity. Attach a registration to a builder with
 * dps_pwsh_v2_set_capabilities; the attachment is consumed by its next
 * invocation. Releasing cancels active callbacks and revokes future calls.
 */
int32_t dps_pwsh_v2_capability_register(
    const struct dps_pwsh_capability_registration* registration,
    uint64_t* capability_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_capability_release(
    uint64_t capability_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_set_capabilities(
    uint64_t handle,
    uint64_t capability_handle,
    struct dps_pwsh_call_result* result);
/*
 * Result handles are immutable snapshots independent of their builder handle.
 * Release each successfully returned result with dps_pwsh_v2_result_release.
 * Streams retain at most 32 records and each returned UTF-8 field is limited
 * to 4,096 UTF-16 code units. Stream and record flags report dropped records
 * and truncated fields. Sequence entries form a global capture order across
 * every retained stream record; dropped records set RESULT_SEQUENCE_TRUNCATED.
 */
int32_t dps_pwsh_v2_invoke(
    uint64_t handle,
    uint64_t* result_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_release(
    uint64_t result_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_info(
    uint64_t result_handle,
    uint32_t* flags,
    uint32_t* sequence_count,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_metadata(
    uint64_t result_handle,
    uint32_t* state,
    uint64_t* invocation_id,
    uint32_t* had_errors,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_stream_info(
    uint64_t result_handle,
    uint32_t stream,
    uint32_t* record_count,
    uint32_t* flags,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_stream_record_info(
    uint64_t result_handle,
    uint32_t stream,
    uint32_t record_index,
    uint64_t* sequence,
    uint32_t* flags,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_copy_stream_record_field_utf8(
    uint64_t result_handle,
    uint32_t stream,
    uint32_t record_index,
    uint32_t field,
    uint8_t* buffer,
    size_t buffer_len,
    size_t* required_len,
    struct dps_pwsh_call_result* result);
/*
 * Total and dropped counts include records observed after a stream reached its
 * retained-record bound. They are independent of the retained record count
 * returned by result_get_stream_info.
 */
int32_t dps_pwsh_v2_result_get_stream_totals(
    uint64_t result_handle,
    uint32_t stream,
    uint64_t* total_record_count,
    uint64_t* dropped_record_count,
    struct dps_pwsh_call_result* result);
/*
 * Projection flags are the DPS_PWSH_RESULT_RECORD_*_PRESENT/TRUNCATED bits.
 * Counts describe the copied output object property/type-name projection; they
 * are zero for record kinds without that projection.
 */
int32_t dps_pwsh_v2_result_get_stream_record_projection_info(
    uint64_t result_handle,
    uint32_t stream,
    uint32_t record_index,
    uint32_t* property_entry_count,
    uint32_t* dropped_property_entry_count,
    uint32_t* type_name_count,
    uint32_t* dropped_type_name_count,
    uint32_t* projection_flags,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_copy_stream_record_value(
    uint64_t result_handle,
    uint32_t stream,
    uint32_t record_index,
    uint32_t value_slot,
    uint32_t* kind,
    uint8_t* buffer,
    size_t buffer_len,
    size_t* required_len,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_result_get_sequence_record(
    uint64_t result_handle,
    uint32_t sequence_index,
    uint32_t* stream,
    uint32_t* record_index,
    uint64_t* sequence,
    struct dps_pwsh_call_result* result);
/*
 * Async invocation consumes the builder for mutation while the operation is
 * active. Its bounded input collection must therefore be completed before
 * start; producers cannot feed input after this call returns. The operation
 * retains the builder until terminal completion, so builder/operation release
 * races cannot free a managed pipeline used by a native call.
 *
 * Stop is idempotent. Release also requests cancellation for a non-terminal
 * operation then detaches the caller's handle; a released native handle is no
 * longer valid. Poll and wait return an operation state and terminal
 * status. While pending/running their return status is SUCCESS. Terminal
 * cancellation/failure is returned both as the function status and in
 * terminal_status, with the bounded terminal diagnostic in call_result.
 * timeout_milliseconds == UINT32_MAX waits indefinitely.
 */
int32_t dps_pwsh_v2_invoke_async(
    uint64_t handle,
    uint64_t* operation_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_release(
    uint64_t operation_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_stop(
    uint64_t operation_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_poll(
    uint64_t operation_handle,
    uint32_t* state,
    int32_t* terminal_status,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_wait(
    uint64_t operation_handle,
    uint32_t timeout_milliseconds,
    uint32_t* state,
    int32_t* terminal_status,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_operation_get_result(
    uint64_t operation_handle,
    uint64_t* result_handle,
    struct dps_pwsh_call_result* result);
/*
 * Session ownership is independent from builder, result, and operation handles.
 * Releasing a session detaches its public handle immediately; managed builders
 * already created from it retain an internal lease until they are released.
 */
int32_t dps_pwsh_v2_session_create(
    const struct dps_pwsh_session_options* options,
    uint64_t* session_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_release(
    uint64_t session_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_create_builder(
    uint64_t session_handle,
    uint64_t* builder_handle,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_get_snapshot(
    uint64_t session_handle,
    struct dps_pwsh_session_snapshot* snapshot,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_get_event_info(
    uint64_t session_handle,
    uint32_t event_index,
    uint64_t* sequence,
    uint32_t* state,
    uint32_t* flags,
    struct dps_pwsh_call_result* result);
/*
 * Session variables are copied tagged values only. Names are bounded ASCII
 * identifiers. Mutation and reads reject a session with a pending or running
 * invocation; Get returns found == 0 for an absent variable and never exposes
 * a PSVariable, PSObject, or any live CLR object.
 */
int32_t dps_pwsh_v2_session_set_variable(
    uint64_t session_handle,
    struct dps_pwsh_utf8_span name,
    const struct dps_pwsh_data_value* value,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_remove_variable(
    uint64_t session_handle,
    struct dps_pwsh_utf8_span name,
    uint32_t* removed,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_get_variable_snapshot(
    uint64_t session_handle,
    struct dps_pwsh_utf8_span name,
    uint32_t* found,
    uint32_t* kind,
    uint8_t* buffer,
    size_t buffer_len,
    size_t* required_len,
    struct dps_pwsh_call_result* result);
int32_t dps_pwsh_v2_session_pool_create(
    const struct dps_pwsh_session_pool_options* options,
    uint64_t* pool_handle,
    struct dps_pwsh_call_result* result);

/*
 * ABI v1 exports are retained for preview compatibility only. New consumers
 * must use dps_pwsh_v2_initialize_payload with a hash-pinned manifest.
 * dps_pwsh_initialize_utf8 requires a conventional
 * devolutions-pwsh-payload.json beside the payload and is unsafe local
 * development compatibility only; last_error_utf8 is process-global.
 */
int32_t dps_pwsh_initialize_utf8(const uint8_t* payload_path, size_t payload_path_len);
int32_t dps_pwsh_last_error_utf8(uint8_t* buffer, size_t buffer_len, size_t* required_len);
int32_t dps_pwsh_create(uint64_t* handle);
int32_t dps_pwsh_release(uint64_t handle);
int32_t dps_pwsh_add_command_utf8(uint64_t handle, const uint8_t* command, size_t command_len);
int32_t dps_pwsh_add_script_utf8(uint64_t handle, const uint8_t* script, size_t script_len);
int32_t dps_pwsh_add_argument_utf8(uint64_t handle, const uint8_t* argument, size_t argument_len);
int32_t dps_pwsh_add_parameter_string_utf8(
    uint64_t handle,
    const uint8_t* name,
    size_t name_len,
    const uint8_t* value,
    size_t value_len);
int32_t dps_pwsh_add_parameter_i64(uint64_t handle, const uint8_t* name, size_t name_len, int64_t value);
int32_t dps_pwsh_add_statement(uint64_t handle);
int32_t dps_pwsh_clear(uint64_t handle);
int32_t dps_pwsh_invoke_utf8(
    uint64_t handle,
    uint8_t* buffer,
    size_t buffer_len,
    size_t* required_len);
int32_t dps_pwsh_get_invocation_error_count(uint64_t handle, uint32_t* error_count);
int32_t dps_pwsh_copy_invocation_error_field_utf8(
    uint64_t handle,
    uint32_t error_index,
    uint32_t field,
    uint8_t* buffer,
    size_t buffer_len,
    size_t* required_len);
int32_t dps_pwsh_stop(uint64_t handle);

#endif
