#![allow(clippy::missing_safety_doc)]

mod payload;

use std::cell::Cell;
use std::collections::{HashMap, HashSet};
use std::convert::TryFrom;
use std::fs;
use std::mem;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;
use std::slice;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::time::{Duration, Instant};

use payload::{SessionPolicy, TrustPolicy, ValidationError, ValidationRequest, MANIFEST_FILE_NAME};
use pwsh_host::{
    FfiBindingError, FfiInvocationResult, FfiPowerShell, FfiPowerShellSession, FfiSessionEvent, FfiSessionSnapshot,
    FfiSnapshotValue, HostedRuntime,
};

const ABI_VERSION: u32 = 2;
const FEATURE_STRUCTURED_INVOCATION_ERRORS: u64 = 1;
const FEATURE_PER_CALL_DIAGNOSTICS: u64 = 1 << 1;
const FEATURE_UTF8_SPANS: u64 = 1 << 2;
const FEATURE_IMMUTABLE_RESULTS: u64 = 1 << 3;
const FEATURE_TAGGED_VALUES: u64 = 1 << 4;
const FEATURE_COMMAND_OPTIONS: u64 = 1 << 5;
const FEATURE_BOUNDED_INPUT: u64 = 1 << 6;
const FEATURE_INVOCATION_METADATA: u64 = 1 << 7;
const FEATURE_ASYNC_OPERATIONS: u64 = 1 << 8;
const FEATURE_PAYLOAD_MANIFEST: u64 = 1 << 9;
const FEATURE_SESSIONS: u64 = 1 << 10;
const FEATURE_SESSION_POLLING: u64 = 1 << 11;
const FEATURE_SESSION_POOL_REJECTION: u64 = 1 << 12;
const FEATURE_SNAPSHOT_PROJECTIONS: u64 = 1 << 13;
const FEATURE_SESSION_CONFIGURATION: u64 = 1 << 14;
const FEATURE_SESSION_VARIABLES: u64 = 1 << 15;
const FEATURE_CAPABILITY_RPC: u64 = 1 << 16;
const CALL_RESULT_DIAGNOSTIC_TRUNCATED: u32 = 1;
#[cfg(test)]
const RESULT_RECORD_SCALAR_VALUE_PRESENT: u32 = 1 << 1;
#[cfg(test)]
const RESULT_RECORD_PROPERTY_BAG_PRESENT: u32 = 1 << 2;
#[cfg(test)]
const RESULT_RECORD_ERROR_TARGET_VALUE_PRESENT: u32 = 1 << 5;
const MAX_VALUE_PAYLOAD_BYTES: usize = 64 * 1024;
const MAX_VALUE_CONTAINER_ENTRIES: u32 = 64;
const MAX_VALUE_DEPTH: u8 = 8;
const MAX_OPERATION_DIAGNOSTIC_BYTES: usize = 4096;
const MAX_SESSION_CONFIGURATION_ENTRIES: usize = 32;
const MAX_SESSION_PATH_BYTES: usize = 16 * 1024;
const SESSION_OPTIONS_PREFIX_SIZE: u32 = mem::size_of::<SessionOptionsPrefix>() as u32;
const EMPTY_VALUE_CONTAINER: [u8; 4] = [0; 4];
const VALUE_KIND_STRING: u32 = 1;
const VALUE_KIND_UNSIGNED_INTEGER: u32 = 5;
const VALUE_KIND_ARRAY: u32 = 13;
const VALUE_KIND_PROPERTY_BAG: u32 = 14;
const CAPABILITY_REGISTRATION_VERSION: u32 = 1;
const MAX_CAPABILITIES: usize = 16;
const MAX_CAPABILITY_NAME_BYTES: usize = 64;
const MAX_CAPABILITY_DEADLINE_MILLISECONDS: u32 = 30_000;
const MAX_CAPABILITY_PERMISSIONS: u32 = 0x0f;

#[repr(i32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Status {
    Success = 0,
    BufferTooSmall = 1,
    InvalidArgument = -1,
    NotInitialized = -2,
    IncompatiblePayload = -3,
    InvalidHandle = -4,
    HostFailure = -5,
    ManagedFailure = -6,
    Panic = -7,
    InputNotCompleted = -8,
    Backpressure = -9,
    UnsupportedValue = -10,
    OperationCancelled = -11,
    OperationNotTerminal = -12,
    PayloadManifestInvalid = -13,
    PayloadUntrusted = -14,
    PayloadHashMismatch = -15,
    PayloadIncompatible = -16,
    UnsupportedCapability = -17,
    SessionPolicyViolation = -18,
}

impl Status {
    fn value(self) -> i32 {
        self as i32
    }
}

#[repr(C)]
pub struct AbiInfo {
    size: u32,
    abi_version: u32,
    feature_flags: u64,
    minimum_compatible_abi_version: u32,
    _reserved: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Utf8Span {
    data: *const u8,
    len: usize,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct DataValue {
    size: u32,
    kind: u32,
    flags: u32,
    _reserved: u32,
    data: *const u8,
    data_len: usize,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct PayloadActivation {
    size: u32,
    trust_policy: u32,
    flags: u32,
    _reserved: u32,
    payload_path: Utf8Span,
    manifest_path: Utf8Span,
    manifest_sha256: Utf8Span,
}

#[repr(C)]
pub struct CallResult {
    size: u32,
    status: i32,
    flags: u32,
    _reserved: u32,
    diagnostic: *mut u8,
    diagnostic_capacity: usize,
    diagnostic_required: usize,
    diagnostic_written: usize,
}

type CapabilityDispatchCallback = unsafe extern "C" fn(
    u64,
    u64,
    Utf8Span,
    *const DataValue,
    u32,
    u32,
    *mut u32,
    *mut u8,
    usize,
    *mut usize,
    *mut CallResult,
) -> i32;
type CapabilityCancelCallback = unsafe extern "C" fn(u64, u64);

#[repr(C)]
pub struct CapabilityRegistration {
    size: u32,
    flags: u32,
    definitions: *const DataValue,
    dispatch: Option<CapabilityDispatchCallback>,
    cancel: Option<CapabilityCancelCallback>,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct SessionOptions {
    size: u32,
    runspace_mode: u32,
    initial_configuration: u32,
    history_mode: u32,
    error_preference: u32,
    warning_preference: u32,
    verbose_preference: u32,
    debug_preference: u32,
    information_preference: u32,
    flags: u32,
    _reserved: u32,
    allowed_module_path: Utf8Span,
    execution_policy: u32,
    configuration_flags: u32,
    initial_variables: DataValue,
    module_imports: DataValue,
    allowed_module_paths: DataValue,
    working_directory: Utf8Span,
    environment: DataValue,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct SessionOptionsPrefix {
    size: u32,
    runspace_mode: u32,
    initial_configuration: u32,
    history_mode: u32,
    error_preference: u32,
    warning_preference: u32,
    verbose_preference: u32,
    debug_preference: u32,
    information_preference: u32,
    flags: u32,
    _reserved: u32,
    allowed_module_path: Utf8Span,
}

#[repr(C)]
pub struct SessionSnapshot {
    size: u32,
    state: u32,
    runspace_state: u32,
    flags: u32,
    active_pipeline_count: u32,
    event_count: u32,
    invocation_count: u64,
    history_count: u64,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct SessionPoolOptions {
    size: u32,
    minimum_sessions: u32,
    maximum_sessions: u32,
    flags: u32,
    _reserved: u32,
}

struct State {
    runtime: Option<Arc<HostedRuntime>>,
    session_policy: Option<Arc<SessionPolicy>>,
    sessions: HashMap<u64, Arc<Session>>,
    runspace_sessions: HashMap<u64, Arc<RunspaceSession>>,
    results: HashMap<u64, Arc<InvocationResult>>,
    operations: HashMap<u64, Arc<Operation>>,
    capabilities: HashMap<u64, Arc<CapabilityRegistrationState>>,
    next_handle: u64,
    next_result_handle: u64,
    next_operation_handle: u64,
    next_runspace_session_handle: u64,
    next_capability_handle: u64,
    last_error: String,
}

struct Session {
    power_shell: FfiPowerShell,
    operation_active: Mutex<bool>,
    runspace_session: Option<Arc<RunspaceSession>>,
    capability_registration: Mutex<Option<Arc<CapabilityRegistrationState>>>,
    active_capability: Mutex<Option<CapabilityInvocation>>,
}

struct RunspaceSession {
    session: FfiPowerShellSession,
    operation_active: Mutex<bool>,
}

struct InvocationResult {
    result: FfiInvocationResult,
}

struct CapabilityDefinition {
    argument_kinds: Vec<Vec<u32>>,
    response_kinds: Vec<u32>,
    maximum_input_bytes: usize,
    maximum_output_bytes: usize,
    deadline_milliseconds: u32,
}

struct CapabilityRegistrationState {
    handle: u64,
    definitions: HashMap<String, CapabilityDefinition>,
    dispatch: CapabilityDispatchCallback,
    cancel: CapabilityCancelCallback,
    active: AtomicBool,
    invocations: Mutex<HashMap<u64, Arc<AtomicBool>>>,
}

#[derive(Clone)]
struct CapabilityInvocation {
    registration: Arc<CapabilityRegistrationState>,
    invocation_id: u64,
    cancelled: Arc<AtomicBool>,
}

#[repr(u32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum OperationState {
    Pending = 1,
    Running = 2,
    Completed = 3,
    Cancelled = 4,
    Failed = 5,
}

impl OperationState {
    fn is_terminal(self) -> bool {
        matches!(self, Self::Completed | Self::Cancelled | Self::Failed)
    }
}

struct OperationCompletion {
    state: OperationState,
    terminal_status: Status,
    diagnostic: String,
    result: Option<Arc<InvocationResult>>,
}

struct Operation {
    builder_handle: u64,
    session: Arc<Session>,
    runspace_session: Option<Arc<RunspaceSession>>,
    cancellation_requested: AtomicBool,
    capability: Option<CapabilityInvocation>,
    completion: (Mutex<OperationCompletion>, Condvar),
}

// Normal operations are serialized by SESSION_OPERATION_LOCK. Stop is the sole
// concurrent call and maps to PowerShell's supported cross-thread cancellation API.
unsafe impl Send for Session {}
unsafe impl Sync for Session {}
unsafe impl Send for RunspaceSession {}
unsafe impl Sync for RunspaceSession {}
unsafe impl Send for InvocationResult {}
unsafe impl Sync for InvocationResult {}
unsafe impl Send for Operation {}
unsafe impl Sync for Operation {}

impl Operation {
    fn new(builder_handle: u64, session: Arc<Session>, capability: Option<CapabilityInvocation>) -> Self {
        Self {
            builder_handle,
            runspace_session: session.runspace_session.clone(),
            session,
            cancellation_requested: AtomicBool::new(false),
            capability,
            completion: (
                Mutex::new(OperationCompletion {
                    state: OperationState::Pending,
                    terminal_status: Status::Success,
                    diagnostic: String::new(),
                    result: None,
                }),
                Condvar::new(),
            ),
        }
    }

    fn begin(&self) -> bool {
        let mut completion = self
            .completion
            .0
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if completion.state != OperationState::Pending {
            return false;
        }
        if self.cancellation_requested.load(Ordering::Acquire) {
            completion.state = OperationState::Cancelled;
            completion.terminal_status = Status::OperationCancelled;
            completion.diagnostic = "PowerShell async operation was cancelled before invocation started.".to_owned();
            drop(completion);
            self.completion.1.notify_all();
            self.clear_session_operation();
            self.finish_capability();
            return false;
        }

        completion.state = OperationState::Running;
        true
    }

    fn complete(
        &self,
        state: OperationState,
        terminal_status: Status,
        diagnostic: String,
        result: Option<Arc<InvocationResult>>,
    ) {
        let mut completion = self
            .completion
            .0
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if completion.state.is_terminal() {
            return;
        }

        if self.cancellation_requested() {
            completion.state = OperationState::Cancelled;
            completion.terminal_status = Status::OperationCancelled;
            completion.diagnostic = "PowerShell async operation was cancelled; no result is available.".to_owned();
            completion.result = None;
        } else {
            completion.state = state;
            completion.terminal_status = terminal_status;
            completion.diagnostic = bounded_operation_diagnostic(diagnostic);
            completion.result = result;
        }
        drop(completion);
        self.completion.1.notify_all();
        self.clear_session_operation();
    }

    fn request_stop(&self) {
        let should_stop = {
            let mut completion = self
                .completion
                .0
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            match completion.state {
                OperationState::Pending => {
                    self.cancellation_requested.store(true, Ordering::Release);
                    completion.state = OperationState::Cancelled;
                    completion.terminal_status = Status::OperationCancelled;
                    completion.diagnostic =
                        "PowerShell async operation was cancelled before invocation started.".to_owned();
                    drop(completion);
                    self.completion.1.notify_all();
                    self.clear_session_operation();
                    false
                }
                OperationState::Running => !self.cancellation_requested.swap(true, Ordering::AcqRel),
                OperationState::Completed | OperationState::Cancelled | OperationState::Failed => false,
            }
        };

        if should_stop {
            // A cancellation request wins even if the managed Stop call races a
            // natural completion; the worker discards any captured result.
            self.cancel_capability();
            let _ = self.session.power_shell.stop();
        }
    }

    fn snapshot(&self) -> (OperationState, Status, String, Option<Arc<InvocationResult>>) {
        let completion = self
            .completion
            .0
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        (
            completion.state,
            completion.terminal_status,
            completion.diagnostic.clone(),
            completion.result.clone(),
        )
    }

    fn wait(&self, timeout: Option<Duration>) -> (OperationState, Status, String, Option<Arc<InvocationResult>>) {
        let mut completion = self
            .completion
            .0
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if !completion.state.is_terminal() {
            if let Some(timeout) = timeout {
                let deadline = Instant::now()
                    .checked_add(timeout)
                    .unwrap_or_else(|| Instant::now() + timeout);
                while !completion.state.is_terminal() {
                    let remaining = deadline.saturating_duration_since(Instant::now());
                    if remaining.is_zero() {
                        break;
                    }
                    let (next, wait_result) = self
                        .completion
                        .1
                        .wait_timeout(completion, remaining)
                        .unwrap_or_else(|poisoned| poisoned.into_inner());
                    completion = next;
                    if wait_result.timed_out() {
                        break;
                    }
                }
            } else {
                while !completion.state.is_terminal() {
                    completion = self
                        .completion
                        .1
                        .wait(completion)
                        .unwrap_or_else(|poisoned| poisoned.into_inner());
                }
            }
        }

        (
            completion.state,
            completion.terminal_status,
            completion.diagnostic.clone(),
            completion.result.clone(),
        )
    }

    fn cancellation_requested(&self) -> bool {
        self.cancellation_requested.load(Ordering::Acquire)
    }

    fn clear_session_operation(&self) {
        let mut active = self
            .session
            .operation_active
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        *active = false;
        if let Some(runspace_session) = &self.runspace_session {
            let mut active = runspace_session
                .operation_active
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            *active = false;
        }
    }

    fn cancel_capability(&self) {
        if let Some(capability) = &self.capability {
            capability.cancel();
        }
    }

    fn finish_capability(&self) {
        if let Some(capability) = &self.capability {
            capability.registration.end_invocation(capability.invocation_id);
        }
    }
}

impl Default for State {
    fn default() -> Self {
        Self {
            runtime: None,
            session_policy: None,
            sessions: HashMap::new(),
            runspace_sessions: HashMap::new(),
            results: HashMap::new(),
            operations: HashMap::new(),
            capabilities: HashMap::new(),
            next_handle: 1,
            next_result_handle: 1_u64 << 63,
            next_operation_handle: 1_u64 << 62,
            next_runspace_session_handle: 1_u64 << 61,
            next_capability_handle: 1_u64 << 60,
            last_error: String::new(),
        }
    }
}

// The cdylib serializes all access through STATE. Managed delegates and handles
// never escape the mutex-protected State.
unsafe impl Send for State {}

static STATE: OnceLock<Mutex<State>> = OnceLock::new();
static SESSION_OPERATION_LOCK: Mutex<()> = Mutex::new(());
static NEXT_CAPABILITY_INVOCATION_ID: AtomicU64 = AtomicU64::new(1);
thread_local! {
    static CAPABILITY_CALLBACK_DEPTH: Cell<u32> = const { Cell::new(0) };
}

fn state() -> &'static Mutex<State> {
    STATE.get_or_init(|| Mutex::new(State::default()))
}

fn fail(state: &mut State, status: Status, message: impl Into<String>) -> i32 {
    state.last_error = message.into();
    status.value()
}

fn clear_error(state: &mut State) {
    state.last_error.clear();
}

unsafe fn utf8_input<'a>(ptr: *const u8, len: usize) -> Result<&'a str, Status> {
    if ptr.is_null() {
        return Err(Status::InvalidArgument);
    }

    let bytes = slice::from_raw_parts(ptr, len);
    let value = std::str::from_utf8(bytes).map_err(|_| Status::InvalidArgument)?;
    if value.as_bytes().contains(&0) {
        return Err(Status::InvalidArgument);
    }

    Ok(value)
}

unsafe fn utf8_span<'a>(value: Utf8Span) -> Result<&'a str, Status> {
    if value.len == 0 {
        return Ok("");
    }

    utf8_input(value.data, value.len)
}

unsafe fn data_value_input<'a>(value: *const DataValue) -> Result<(u32, &'a [u8]), (Status, String)> {
    if value.is_null() {
        return Err((Status::InvalidArgument, "tagged value pointer is null".to_owned()));
    }

    let value = &*value;
    if value.size < std::mem::size_of::<DataValue>() as u32 || value.flags != 0 || value._reserved != 0 {
        return Err((Status::InvalidArgument, "tagged value header is invalid".to_owned()));
    }
    if value.data_len > MAX_VALUE_PAYLOAD_BYTES || (value.data_len != 0 && value.data.is_null()) {
        return Err((
            Status::InvalidArgument,
            "tagged value payload is invalid or exceeds its bound".to_owned(),
        ));
    }

    let payload = if value.data_len == 0 {
        &[]
    } else {
        slice::from_raw_parts(value.data, value.data_len)
    };
    validate_value_payload(value.kind, payload, 0)?;
    Ok((value.kind, payload))
}

fn validate_value_payload(kind: u32, payload: &[u8], depth: u8) -> Result<(), (Status, String)> {
    if depth > MAX_VALUE_DEPTH {
        return Err((
            Status::InvalidArgument,
            "tagged value nesting exceeds its bound".to_owned(),
        ));
    }

    match kind {
        0 => require_value_length(payload, 0, "null"),
        1 | 7 | 11 | 12 => validate_value_utf8(payload),
        2 | 3 => {
            require_value_length(payload, 1, "boolean")?;
            if payload[0] > 1 {
                return Err((
                    Status::InvalidArgument,
                    "boolean payload must be zero or one".to_owned(),
                ));
            }
            Ok(())
        }
        4 | 5 | 6 | 9 => require_value_length(payload, 8, "numeric"),
        8 => Ok(()),
        10 => require_value_length(payload, 10, "date-time offset"),
        13 => validate_value_array(payload, depth + 1),
        14 => validate_value_property_bag(payload, depth + 1),
        _ => Err((
            Status::UnsupportedValue,
            format!("tagged value kind {} is not supported", kind),
        )),
    }
}

fn require_value_length(payload: &[u8], expected: usize, description: &str) -> Result<(), (Status, String)> {
    if payload.len() == expected {
        Ok(())
    } else {
        Err((
            Status::InvalidArgument,
            format!("{} tagged value payload length is invalid", description),
        ))
    }
}

fn validate_value_utf8(payload: &[u8]) -> Result<(), (Status, String)> {
    if payload.contains(&0) || std::str::from_utf8(payload).is_err() {
        return Err((Status::InvalidArgument, "tagged UTF-8 value is invalid".to_owned()));
    }
    Ok(())
}

fn read_value_u32(payload: &[u8], offset: &mut usize, description: &str) -> Result<u32, (Status, String)> {
    let bytes = read_value_bytes(payload, offset, 4, description)?;
    Ok(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
}

fn read_value_bytes<'a>(
    payload: &'a [u8],
    offset: &mut usize,
    length: usize,
    description: &str,
) -> Result<&'a [u8], (Status, String)> {
    let end = offset.checked_add(length).ok_or_else(|| {
        (
            Status::InvalidArgument,
            format!("{} tagged value payload is invalid", description),
        )
    })?;
    if end > payload.len() {
        return Err((
            Status::InvalidArgument,
            format!("{} tagged value payload is truncated", description),
        ));
    }
    let bytes = &payload[*offset..end];
    *offset = end;
    Ok(bytes)
}

fn validate_nested_value(payload: &[u8], offset: &mut usize, depth: u8) -> Result<(), (Status, String)> {
    let kind = read_value_u32(payload, offset, "nested")?;
    let length = read_value_u32(payload, offset, "nested")? as usize;
    let nested = read_value_bytes(payload, offset, length, "nested")?;
    validate_value_payload(kind, nested, depth)
}

fn validate_value_array(payload: &[u8], depth: u8) -> Result<(), (Status, String)> {
    let mut offset = 0;
    let count = read_value_u32(payload, &mut offset, "array count")?;
    if count > MAX_VALUE_CONTAINER_ENTRIES {
        return Err((Status::InvalidArgument, "array item count exceeds its bound".to_owned()));
    }
    for _ in 0..count {
        validate_nested_value(payload, &mut offset, depth)?;
    }
    if offset == payload.len() {
        Ok(())
    } else {
        Err((
            Status::InvalidArgument,
            "array payload contains trailing bytes".to_owned(),
        ))
    }
}

fn validate_value_property_bag(payload: &[u8], depth: u8) -> Result<(), (Status, String)> {
    let mut offset = 0;
    let count = read_value_u32(payload, &mut offset, "property bag count")?;
    if count > MAX_VALUE_CONTAINER_ENTRIES {
        return Err((
            Status::InvalidArgument,
            "property bag entry count exceeds its bound".to_owned(),
        ));
    }
    for _ in 0..count {
        let key_length = read_value_u32(payload, &mut offset, "property bag key")? as usize;
        let key = read_value_bytes(payload, &mut offset, key_length, "property bag key")?;
        if key.is_empty() {
            return Err((
                Status::InvalidArgument,
                "property bag keys must be non-empty".to_owned(),
            ));
        }
        validate_value_utf8(key)?;
        validate_nested_value(payload, &mut offset, depth)?;
    }
    if offset == payload.len() {
        Ok(())
    } else {
        Err((
            Status::InvalidArgument,
            "property bag payload contains trailing bytes".to_owned(),
        ))
    }
}

fn read_nested_value<'a>(
    payload: &'a [u8],
    offset: &mut usize,
    description: &str,
) -> Result<(u32, &'a [u8]), (Status, String)> {
    let kind = read_value_u32(payload, offset, description)?;
    let length = read_value_u32(payload, offset, description)? as usize;
    let value = read_value_bytes(payload, offset, length, description)?;
    validate_value_payload(kind, value, 0)?;
    Ok((kind, value))
}

fn read_value_array<'a>(payload: &'a [u8], description: &str) -> Result<Vec<(u32, &'a [u8])>, (Status, String)> {
    let mut offset = 0;
    let count = read_value_u32(payload, &mut offset, description)?;
    if count > MAX_VALUE_CONTAINER_ENTRIES {
        return Err((Status::InvalidArgument, format!("{} exceeds its bound", description)));
    }

    let mut values = Vec::with_capacity(count as usize);
    for _ in 0..count {
        values.push(read_nested_value(payload, &mut offset, description)?);
    }
    if offset != payload.len() {
        return Err((
            Status::InvalidArgument,
            format!("{} contains trailing bytes", description),
        ));
    }
    Ok(values)
}

fn read_property_bag<'a>(
    payload: &'a [u8],
    description: &str,
) -> Result<HashMap<&'a str, (u32, &'a [u8])>, (Status, String)> {
    let mut offset = 0;
    let count = read_value_u32(payload, &mut offset, description)?;
    if count > MAX_VALUE_CONTAINER_ENTRIES {
        return Err((Status::InvalidArgument, format!("{} exceeds its bound", description)));
    }

    let mut values = HashMap::with_capacity(count as usize);
    for _ in 0..count {
        let key_length = read_value_u32(payload, &mut offset, description)? as usize;
        let key = read_value_bytes(payload, &mut offset, key_length, description)?;
        let key = std::str::from_utf8(key)
            .map_err(|_| (Status::InvalidArgument, format!("{} has an invalid key", description)))?;
        if key.is_empty() || key.as_bytes().contains(&0) {
            return Err((Status::InvalidArgument, format!("{} has an invalid key", description)));
        }
        let value = read_nested_value(payload, &mut offset, description)?;
        if values.insert(key, value).is_some() {
            return Err((Status::InvalidArgument, format!("{} has duplicate keys", description)));
        }
    }
    if offset != payload.len() {
        return Err((
            Status::InvalidArgument,
            format!("{} contains trailing bytes", description),
        ));
    }
    Ok(values)
}

fn required_property<'a>(
    properties: &'a HashMap<&str, (u32, &'a [u8])>,
    key: &str,
    description: &str,
) -> Result<(u32, &'a [u8]), (Status, String)> {
    properties
        .get(key)
        .copied()
        .ok_or_else(|| (Status::InvalidArgument, format!("{} is missing {}", description, key)))
}

fn read_unsigned_value(kind: u32, payload: &[u8], description: &str) -> Result<u64, (Status, String)> {
    if kind != VALUE_KIND_UNSIGNED_INTEGER || payload.len() != 8 {
        return Err((
            Status::InvalidArgument,
            format!("{} must be an unsigned integer", description),
        ));
    }
    Ok(u64::from_le_bytes([
        payload[0], payload[1], payload[2], payload[3], payload[4], payload[5], payload[6], payload[7],
    ]))
}

fn read_capability_kind_list(kind: u32, payload: &[u8], description: &str) -> Result<Vec<u32>, (Status, String)> {
    if kind != VALUE_KIND_ARRAY {
        return Err((Status::InvalidArgument, format!("{} must be an array", description)));
    }
    let values = read_value_array(payload, description)?;
    if values.is_empty() {
        return Err((Status::InvalidArgument, format!("{} must not be empty", description)));
    }
    let mut kinds = Vec::with_capacity(values.len());
    let mut seen = HashSet::new();
    for (kind, value) in values {
        let value_kind = read_unsigned_value(kind, value, description)?;
        let value_kind = u32::try_from(value_kind).map_err(|_| {
            (
                Status::InvalidArgument,
                format!("{} contains an invalid value kind", description),
            )
        })?;
        if value_kind > VALUE_KIND_PROPERTY_BAG || !seen.insert(value_kind) {
            return Err((
                Status::InvalidArgument,
                format!("{} contains an invalid or duplicate value kind", description),
            ));
        }
        kinds.push(value_kind);
    }
    Ok(kinds)
}

fn is_canonical_capability_name(value: &str) -> bool {
    let bytes = value.as_bytes();
    if bytes.is_empty()
        || bytes.len() > MAX_CAPABILITY_NAME_BYTES
        || !(value.starts_with("rdm.") || value.starts_with("host."))
    {
        return false;
    }

    let mut previous_separator = true;
    for byte in bytes {
        let separator = matches!(*byte, b'.' | b'-');
        if !matches!(*byte, b'a'..=b'z' | b'0'..=b'9' | b'.' | b'-') || (separator && previous_separator) {
            return false;
        }
        previous_separator = separator;
    }
    !previous_separator
}

fn parse_capability_definitions(
    kind: u32,
    payload: &[u8],
) -> Result<HashMap<String, CapabilityDefinition>, (Status, String)> {
    if kind != VALUE_KIND_PROPERTY_BAG {
        return Err((
            Status::InvalidArgument,
            "capability registration must be a tagged property bag".to_owned(),
        ));
    }
    let root = read_property_bag(payload, "capability registration")?;
    if root.len() != 2 {
        return Err((
            Status::InvalidArgument,
            "capability registration contains unsupported fields".to_owned(),
        ));
    }
    let (protocol_kind, protocol_value) = required_property(&root, "protocol", "capability registration")?;
    if read_unsigned_value(protocol_kind, protocol_value, "capability protocol")?
        != u64::from(CAPABILITY_REGISTRATION_VERSION)
    {
        return Err((
            Status::UnsupportedCapability,
            "capability protocol version is unsupported".to_owned(),
        ));
    }
    let (capabilities_kind, capabilities_value) = required_property(&root, "capabilities", "capability registration")?;
    if capabilities_kind != VALUE_KIND_ARRAY {
        return Err((Status::InvalidArgument, "capabilities must be an array".to_owned()));
    }

    let items = read_value_array(capabilities_value, "capabilities")?;
    if items.is_empty() || items.len() > MAX_CAPABILITIES {
        return Err((
            Status::InvalidArgument,
            "capability count is outside its bound".to_owned(),
        ));
    }
    let mut definitions = HashMap::with_capacity(items.len());
    for (kind, value) in items {
        if kind != VALUE_KIND_PROPERTY_BAG {
            return Err((
                Status::InvalidArgument,
                "capability definition must be a property bag".to_owned(),
            ));
        }
        let properties = read_property_bag(value, "capability definition")?;
        if properties.len() != 7 {
            return Err((
                Status::InvalidArgument,
                "capability definition contains unsupported fields".to_owned(),
            ));
        }
        let (name_kind, name_value) = required_property(&properties, "name", "capability definition")?;
        if name_kind != VALUE_KIND_STRING {
            return Err((Status::InvalidArgument, "capability name must be UTF-8 text".to_owned()));
        }
        let name = std::str::from_utf8(name_value)
            .map_err(|_| (Status::InvalidArgument, "capability name is invalid UTF-8".to_owned()))?;
        if !is_canonical_capability_name(name) {
            return Err((Status::InvalidArgument, "capability name is not canonical".to_owned()));
        }

        let (permissions_kind, permissions_value) =
            required_property(&properties, "permissions", "capability definition")?;
        let permissions = u32::try_from(read_unsigned_value(
            permissions_kind,
            permissions_value,
            "capability permissions",
        )?)
        .map_err(|_| (Status::InvalidArgument, "capability permissions are invalid".to_owned()))?;
        if permissions == 0 || permissions & !MAX_CAPABILITY_PERMISSIONS != 0 {
            return Err((Status::InvalidArgument, "capability permissions are invalid".to_owned()));
        }

        let (input_kind, input_value) = required_property(&properties, "maximumInputBytes", "capability definition")?;
        let maximum_input_bytes = usize::try_from(read_unsigned_value(
            input_kind,
            input_value,
            "capability maximum input bytes",
        )?)
        .map_err(|_| {
            (
                Status::InvalidArgument,
                "capability maximum input bytes are invalid".to_owned(),
            )
        })?;
        let (output_kind, output_value) =
            required_property(&properties, "maximumOutputBytes", "capability definition")?;
        let maximum_output_bytes = usize::try_from(read_unsigned_value(
            output_kind,
            output_value,
            "capability maximum output bytes",
        )?)
        .map_err(|_| {
            (
                Status::InvalidArgument,
                "capability maximum output bytes are invalid".to_owned(),
            )
        })?;
        if maximum_input_bytes == 0
            || maximum_input_bytes > MAX_VALUE_PAYLOAD_BYTES
            || maximum_output_bytes == 0
            || maximum_output_bytes > MAX_VALUE_PAYLOAD_BYTES
        {
            return Err((Status::InvalidArgument, "capability byte bounds are invalid".to_owned()));
        }

        let (deadline_kind, deadline_value) =
            required_property(&properties, "deadlineMilliseconds", "capability definition")?;
        let deadline_milliseconds = u32::try_from(read_unsigned_value(
            deadline_kind,
            deadline_value,
            "capability deadline",
        )?)
        .map_err(|_| (Status::InvalidArgument, "capability deadline is invalid".to_owned()))?;
        if deadline_milliseconds == 0 || deadline_milliseconds > MAX_CAPABILITY_DEADLINE_MILLISECONDS {
            return Err((Status::InvalidArgument, "capability deadline is invalid".to_owned()));
        }

        let (arguments_kind, arguments_value) = required_property(&properties, "arguments", "capability definition")?;
        if arguments_kind != VALUE_KIND_ARRAY {
            return Err((
                Status::InvalidArgument,
                "capability arguments must be an array".to_owned(),
            ));
        }
        let argument_values = read_value_array(arguments_value, "capability arguments")?;
        let mut argument_kinds = Vec::with_capacity(argument_values.len());
        for (argument_kind, argument_value) in argument_values {
            argument_kinds.push(read_capability_kind_list(
                argument_kind,
                argument_value,
                "capability argument schema",
            )?);
        }

        let (responses_kind, responses_value) =
            required_property(&properties, "responseKinds", "capability definition")?;
        let response_kinds = read_capability_kind_list(responses_kind, responses_value, "capability response schema")?;
        let definition = CapabilityDefinition {
            argument_kinds,
            response_kinds,
            maximum_input_bytes,
            maximum_output_bytes,
            deadline_milliseconds,
        };
        if definitions.insert(name.to_owned(), definition).is_some() {
            return Err((Status::InvalidArgument, "capability names must be unique".to_owned()));
        }
    }
    Ok(definitions)
}

struct CapabilityCallbackScope;

impl CapabilityCallbackScope {
    fn enter() -> Self {
        CAPABILITY_CALLBACK_DEPTH.with(|depth| depth.set(depth.get().saturating_add(1)));
        Self
    }
}

impl Drop for CapabilityCallbackScope {
    fn drop(&mut self) {
        CAPABILITY_CALLBACK_DEPTH.with(|depth| depth.set(depth.get().saturating_sub(1)));
    }
}

impl CapabilityRegistrationState {
    fn begin_invocation(self: &Arc<Self>, invocation_id: u64) -> Result<CapabilityInvocation, (Status, String)> {
        if !self.active.load(Ordering::Acquire) {
            return Err((
                Status::InvalidHandle,
                "capability registration has been released".to_owned(),
            ));
        }
        let cancelled = Arc::new(AtomicBool::new(false));
        let mut invocations = self.invocations.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if invocations.insert(invocation_id, Arc::clone(&cancelled)).is_some() {
            return Err((
                Status::ManagedFailure,
                "capability invocation identifier collision".to_owned(),
            ));
        }
        Ok(CapabilityInvocation {
            registration: Arc::clone(self),
            invocation_id,
            cancelled,
        })
    }

    fn end_invocation(&self, invocation_id: u64) {
        self.invocations
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&invocation_id);
    }

    fn cancel_invocation(&self, invocation_id: u64, cancelled: &AtomicBool) {
        if !cancelled.swap(true, Ordering::AcqRel) {
            unsafe {
                (self.cancel)(self.handle, invocation_id);
            }
        }
    }

    fn revoke(&self) {
        self.active.store(false, Ordering::Release);
        let active = self
            .invocations
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clone();
        for (invocation_id, cancelled) in active {
            self.cancel_invocation(invocation_id, &cancelled);
        }
    }
}

impl CapabilityInvocation {
    fn cancel(&self) {
        self.registration.cancel_invocation(self.invocation_id, &self.cancelled);
    }
}

fn take_session_capability(session: &Session) -> Result<Option<CapabilityInvocation>, (Status, String)> {
    let registration = session
        .capability_registration
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .take();
    let Some(registration) = registration else {
        return Ok(None);
    };
    let invocation_id = NEXT_CAPABILITY_INVOCATION_ID.fetch_add(1, Ordering::AcqRel);
    if invocation_id == 0 {
        return Err((
            Status::ManagedFailure,
            "capability invocation identifier allocation failed".to_owned(),
        ));
    }
    registration.begin_invocation(invocation_id).map(Some)
}

fn set_active_capability(session: &Session, capability: Option<CapabilityInvocation>) {
    *session
        .active_capability
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = capability;
}

fn cancel_active_capability(session: &Session) {
    if let Some(capability) = session
        .active_capability
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clone()
    {
        capability.cancel();
    }
}

fn invoke_with_capability(
    session: &Session,
    capability: Option<CapabilityInvocation>,
) -> Result<FfiInvocationResult, FfiBindingError> {
    if let Some(capability) = capability {
        set_active_capability(session, Some(capability.clone()));
        let configured = unsafe {
            session.power_shell.set_capability_context(
                capability.registration.handle,
                capability.invocation_id,
                capability_dispatch as *const () as *const _,
            )
        };
        if let Err(error) = configured {
            set_active_capability(session, None);
            capability.registration.end_invocation(capability.invocation_id);
            return Err(error);
        }

        let result = session.power_shell.invoke_to_result();
        let clear = unsafe { session.power_shell.set_capability_context(0, 0, std::ptr::null()) };
        set_active_capability(session, None);
        capability.registration.end_invocation(capability.invocation_id);
        result.and_then(|result| clear.map(|_| result))
    } else {
        session.power_shell.invoke_to_result()
    }
}

fn status_from_callback(value: i32) -> Status {
    match value {
        0 => Status::Success,
        1 => Status::BufferTooSmall,
        -1 => Status::InvalidArgument,
        -4 => Status::InvalidHandle,
        -6 => Status::ManagedFailure,
        -9 => Status::Backpressure,
        -10 => Status::UnsupportedValue,
        -11 => Status::OperationCancelled,
        -17 => Status::UnsupportedCapability,
        _ => Status::ManagedFailure,
    }
}

unsafe extern "C" fn capability_dispatch(
    registration_handle: u64,
    invocation_id: u64,
    name: Utf8Span,
    arguments: *const DataValue,
    argument_count: u32,
    deadline_milliseconds: u32,
    response_kind: *mut u32,
    response_buffer: *mut u8,
    response_capacity: usize,
    response_required: *mut usize,
    result: *mut CallResult,
) -> i32 {
    let result = match prepare_call_result(result) {
        Ok(result) => result,
        Err(status) => return status.value(),
    };
    let dispatch = || -> Result<Status, (Status, String)> {
        if response_kind.is_null() || response_required.is_null() {
            return Err((
                Status::InvalidArgument,
                "capability response output pointer is null".to_owned(),
            ));
        }
        let registration = {
            let state = state().lock().unwrap_or_else(|poisoned| poisoned.into_inner());
            state.capabilities.get(&registration_handle).cloned().ok_or_else(|| {
                (
                    Status::InvalidHandle,
                    "capability registration has been released".to_owned(),
                )
            })?
        };
        if !registration.active.load(Ordering::Acquire) {
            return Err((
                Status::InvalidHandle,
                "capability registration has been released".to_owned(),
            ));
        }
        let name = utf8_span(name).map_err(|status| (status, "capability name is invalid".to_owned()))?;
        if !is_canonical_capability_name(name) {
            return Err((Status::InvalidArgument, "capability name is not canonical".to_owned()));
        }
        let definition = registration.definitions.get(name).ok_or_else(|| {
            (
                Status::UnsupportedCapability,
                "capability is not registered for this invocation".to_owned(),
            )
        })?;
        if deadline_milliseconds == 0 {
            return Err((Status::InvalidArgument, "capability deadline is invalid".to_owned()));
        }
        let effective_deadline_milliseconds = deadline_milliseconds.min(definition.deadline_milliseconds);
        let cancelled = registration
            .invocations
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .get(&invocation_id)
            .cloned()
            .ok_or_else(|| {
                (
                    Status::InvalidHandle,
                    "capability invocation is no longer active".to_owned(),
                )
            })?;
        if cancelled.load(Ordering::Acquire) {
            return Err((
                Status::OperationCancelled,
                "capability invocation was cancelled".to_owned(),
            ));
        }
        let (argument_kind, argument_payload) = data_value_input(arguments)?;
        if argument_kind != VALUE_KIND_ARRAY
            || argument_payload.len() > definition.maximum_input_bytes
            || argument_count as usize != definition.argument_kinds.len()
        {
            return Err((
                Status::InvalidArgument,
                "capability argument envelope is invalid".to_owned(),
            ));
        }
        let values = read_value_array(argument_payload, "capability arguments")?;
        if values.len() != definition.argument_kinds.len() {
            return Err((
                Status::InvalidArgument,
                "capability argument count is invalid".to_owned(),
            ));
        }
        for ((kind, _), allowed) in values.iter().zip(definition.argument_kinds.iter()) {
            if !allowed.contains(kind) {
                return Err((
                    Status::InvalidArgument,
                    "capability argument type is invalid".to_owned(),
                ));
            }
        }
        if response_capacity < definition.maximum_output_bytes || response_buffer.is_null() {
            return Err((
                Status::InvalidArgument,
                "capability response buffer is invalid".to_owned(),
            ));
        }

        let mut callback_result = CallResult {
            size: mem::size_of::<CallResult>() as u32,
            status: Status::Success.value(),
            flags: 0,
            _reserved: 0,
            diagnostic: std::ptr::null_mut(),
            diagnostic_capacity: 0,
            diagnostic_required: 0,
            diagnostic_written: 0,
        };
        let _scope = CapabilityCallbackScope::enter();
        let status = (registration.dispatch)(
            registration_handle,
            invocation_id,
            Utf8Span {
                data: name.as_ptr(),
                len: name.len(),
            },
            arguments,
            argument_count,
            effective_deadline_milliseconds,
            response_kind,
            response_buffer,
            response_capacity,
            response_required,
            &mut callback_result,
        );
        let callback_status = status_from_callback(if callback_result.status != Status::Success.value() {
            callback_result.status
        } else {
            status
        });
        if callback_status != Status::Success {
            return Err((callback_status, "capability callback failed".to_owned()));
        }
        if cancelled.load(Ordering::Acquire) {
            return Err((
                Status::OperationCancelled,
                "capability invocation was cancelled".to_owned(),
            ));
        }
        if *response_required > definition.maximum_output_bytes || *response_required > response_capacity {
            return Err((
                Status::InvalidArgument,
                "capability response exceeds its bound".to_owned(),
            ));
        }
        let response = slice::from_raw_parts(response_buffer, *response_required);
        validate_value_payload(*response_kind, response, 0)?;
        if !definition.response_kinds.contains(&*response_kind) {
            return Err((
                Status::InvalidArgument,
                "capability response type is invalid".to_owned(),
            ));
        }
        Ok(Status::Success)
    };
    match catch_unwind(AssertUnwindSafe(dispatch)) {
        Ok(Ok(status)) => complete_call_result(result, status, ""),
        Ok(Err((status, _))) => complete_call_result(result, status, "capability dispatch failed"),
        Err(_) => complete_call_result(result, Status::Panic, "capability dispatch panic was contained"),
    }
}

unsafe fn write_bytes(buffer: *mut u8, buffer_len: usize, required_len: *mut usize, value: &[u8]) -> Status {
    if required_len.is_null() {
        return Status::InvalidArgument;
    }

    *required_len = value.len();
    if buffer_len < value.len() {
        return Status::BufferTooSmall;
    }

    if value.is_empty() {
        return Status::Success;
    }

    if buffer.is_null() {
        return Status::InvalidArgument;
    }

    std::ptr::copy_nonoverlapping(value.as_ptr(), buffer, value.len());
    Status::Success
}

unsafe fn write_utf8(buffer: *mut u8, buffer_len: usize, required_len: *mut usize, value: &str) -> Status {
    write_bytes(buffer, buffer_len, required_len, value.as_bytes())
}

fn execute<F>(operation: F) -> i32
where
    F: FnOnce(&mut State) -> i32,
{
    match catch_unwind(AssertUnwindSafe(|| {
        let mut state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        operation(&mut state)
    })) {
        Ok(status) => status,
        Err(_) => Status::Panic.value(),
    }
}

unsafe fn prepare_call_result<'a>(result: *mut CallResult) -> Result<&'a mut CallResult, Status> {
    if result.is_null() || (*result).size < std::mem::size_of::<CallResult>() as u32 {
        return Err(Status::InvalidArgument);
    }

    if (*result).diagnostic_capacity != 0 && (*result).diagnostic.is_null() {
        return Err(Status::InvalidArgument);
    }

    (*result).status = Status::Success.value();
    (*result).flags = 0;
    (*result).diagnostic_required = 0;
    (*result).diagnostic_written = 0;
    Ok(&mut *result)
}

unsafe fn complete_call_result(result: &mut CallResult, status: Status, diagnostic: &str) -> i32 {
    let diagnostic_bytes = diagnostic.as_bytes();
    result.status = status.value();
    result.flags = 0;
    result.diagnostic_required = diagnostic_bytes.len();
    result.diagnostic_written = diagnostic_bytes.len().min(result.diagnostic_capacity);

    if result.diagnostic_written != 0 {
        std::ptr::copy_nonoverlapping(diagnostic_bytes.as_ptr(), result.diagnostic, result.diagnostic_written);
    }
    if result.diagnostic_written != result.diagnostic_required {
        result.flags |= CALL_RESULT_DIAGNOSTIC_TRUNCATED;
    }

    status.value()
}

unsafe fn v2_call<F>(result: *mut CallResult, operation: F) -> i32
where
    F: FnOnce() -> Result<Status, (Status, String)>,
{
    let result = match prepare_call_result(result) {
        Ok(result) => result,
        Err(status) => return status.value(),
    };
    if CAPABILITY_CALLBACK_DEPTH.with(|depth| depth.get() != 0) {
        return complete_call_result(
            result,
            Status::Backpressure,
            "PowerShell FFI calls are not permitted from a capability callback.",
        );
    }

    match catch_unwind(AssertUnwindSafe(operation)) {
        Ok(Ok(status)) => complete_call_result(result, status, ""),
        Ok(Err((status, diagnostic))) => complete_call_result(result, status, &diagnostic),
        Err(_) => complete_call_result(
            result,
            Status::Panic,
            "an unexpected native panic was contained by the PowerShell FFI",
        ),
    }
}

fn managed_failure(error: FfiBindingError) -> (Status, String) {
    let status = match error.status() {
        -8 => Status::InputNotCompleted,
        -9 => Status::Backpressure,
        -10 => Status::UnsupportedValue,
        -11 => Status::OperationCancelled,
        _ => Status::ManagedFailure,
    };
    (status, error.to_string())
}

fn bounded_operation_diagnostic(mut diagnostic: String) -> String {
    if diagnostic.len() <= MAX_OPERATION_DIAGNOSTIC_BYTES {
        return diagnostic;
    }

    let mut end = MAX_OPERATION_DIAGNOSTIC_BYTES;
    while !diagnostic.is_char_boundary(end) {
        end -= 1;
    }
    diagnostic.truncate(end);
    diagnostic
}

fn session_has_active_operation(session: &Session) -> bool {
    *session
        .operation_active
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn runspace_session_has_active_operation(session: &RunspaceSession) -> bool {
    *session
        .operation_active
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn with_session_result<F>(handle: u64, serialize_operation: bool, operation: F) -> Result<Status, (Status, String)>
where
    F: FnOnce(&FfiPowerShell) -> Result<Status, (Status, String)>,
{
    let session = {
        let state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        match state.sessions.get(&handle) {
            Some(session) => Arc::clone(session),
            None => {
                return Err((Status::InvalidHandle, "PowerShell handle is invalid".to_owned()));
            }
        }
    };

    let _operation_lock = serialize_operation.then(|| {
        SESSION_OPERATION_LOCK
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    });
    if session_has_active_operation(&session) {
        return Err((
            Status::Backpressure,
            "PowerShell builder has an active async operation; await or release it before mutating the builder."
                .to_owned(),
        ));
    }
    if let Some(runspace_session) = &session.runspace_session {
        if *runspace_session
            .operation_active
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
        {
            return Err((
                Status::Backpressure,
                "PowerShell session has a pending or running async operation; await or release it before mutating a builder."
                    .to_owned(),
            ));
        }
    }
    operation(&session.power_shell)
}

fn with_session<F>(handle: u64, serialize_operation: bool, operation: F) -> i32
where
    F: FnOnce(&FfiPowerShell) -> Result<Status, (Status, String)>,
{
    let result = match catch_unwind(AssertUnwindSafe(|| {
        with_session_result(handle, serialize_operation, operation)
    })) {
        Ok(result) => result,
        Err(_) => Err((
            Status::Panic,
            "an unexpected native panic was contained by the PowerShell FFI".to_owned(),
        )),
    };

    execute(|state| match result {
        Ok(status) => {
            clear_error(state);
            status.value()
        }
        Err((status, message)) => fail(state, status, message),
    })
}

fn invoke_result(handle: u64) -> Result<u64, (Status, String)> {
    let session = {
        let state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        match state.sessions.get(&handle) {
            Some(session) => Arc::clone(session),
            None => {
                return Err((Status::InvalidHandle, "PowerShell handle is invalid".to_owned()));
            }
        }
    };
    let _operation_lock = SESSION_OPERATION_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if session_has_active_operation(&session) {
        return Err((
            Status::Backpressure,
            "PowerShell builder has an active async operation; await or release it before invoking again.".to_owned(),
        ));
    }
    if let Some(runspace_session) = &session.runspace_session {
        if *runspace_session
            .operation_active
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
        {
            return Err((
                Status::Backpressure,
                "PowerShell session has a pending or running async operation; await or release it before invoking again."
                    .to_owned(),
            ));
        }
    }
    let capability = take_session_capability(&session)?;
    let result = invoke_with_capability(&session, capability).map_err(managed_failure)?;
    let mut state = match state().lock() {
        Ok(state) => state,
        Err(poisoned) => poisoned.into_inner(),
    };
    let result_handle = state.next_result_handle;
    state.next_result_handle = state
        .next_result_handle
        .checked_add(1)
        .filter(|value| *value != 0)
        .unwrap_or(1_u64 << 63);
    state
        .results
        .insert(result_handle, Arc::new(InvocationResult { result }));
    Ok(result_handle)
}

fn run_operation(operation: Arc<Operation>) {
    if !operation.begin() {
        return;
    }

    let invocation = {
        let _operation_lock = SESSION_OPERATION_LOCK
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if operation.cancellation_requested() {
            None
        } else {
            Some(invoke_with_capability(&operation.session, operation.capability.clone()))
        }
    };

    if operation.cancellation_requested() {
        operation.finish_capability();
        operation.complete(
            OperationState::Cancelled,
            Status::OperationCancelled,
            "PowerShell async operation was cancelled; no result is available.".to_owned(),
            None,
        );
        return;
    }

    match invocation {
        Some(Ok(result)) => operation.complete(
            OperationState::Completed,
            Status::Success,
            String::new(),
            Some(Arc::new(InvocationResult { result })),
        ),
        Some(Err(error)) => {
            let (status, diagnostic) = managed_failure(error);
            operation.complete(OperationState::Failed, status, diagnostic, None);
        }
        None => operation.complete(
            OperationState::Cancelled,
            Status::OperationCancelled,
            "PowerShell async operation was cancelled before invocation started.".to_owned(),
            None,
        ),
    }
    operation.finish_capability();
}

fn start_operation(handle: u64) -> Result<u64, (Status, String)> {
    let _operation_lock = SESSION_OPERATION_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let (operation_handle, operation) = {
        let mut state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        let session = state
            .sessions
            .get(&handle)
            .cloned()
            .ok_or_else(|| (Status::InvalidHandle, "PowerShell handle is invalid".to_owned()))?;
        {
            let mut active = session
                .operation_active
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            if *active {
                return Err((
                    Status::Backpressure,
                    "PowerShell builder already has an active async operation.".to_owned(),
                ));
            }
            *active = true;
        }
        if let Some(runspace_session) = &session.runspace_session {
            let parent_active = {
                let mut active = runspace_session
                    .operation_active
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                if *active {
                    true
                } else {
                    *active = true;
                    false
                }
            };
            if parent_active {
                let mut builder_active = session
                    .operation_active
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                *builder_active = false;
                return Err((
                    Status::Backpressure,
                    "PowerShell session already has a pending or running async operation.".to_owned(),
                ));
            }
        }

        let operation_handle = state.next_operation_handle;
        state.next_operation_handle = state
            .next_operation_handle
            .checked_add(1)
            .filter(|value| *value != 0)
            .unwrap_or(1_u64 << 62);
        let capability = take_session_capability(&session)?;
        let operation = Arc::new(Operation::new(handle, session, capability));
        state.operations.insert(operation_handle, Arc::clone(&operation));
        (operation_handle, operation)
    };

    if std::thread::Builder::new()
        .name("devolutions-pwsh-ffi-operation".to_owned())
        .spawn({
            let operation = Arc::clone(&operation);
            move || run_operation(operation)
        })
        .is_err()
    {
        let mut state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        state.operations.remove(&operation_handle);
        operation.clear_session_operation();
        return Err((
            Status::HostFailure,
            "failed to create the native PowerShell async operation thread".to_owned(),
        ));
    }

    Ok(operation_handle)
}

fn with_operation<F>(handle: u64, operation: F) -> Result<Status, (Status, String)>
where
    F: FnOnce(&Arc<Operation>) -> Result<Status, (Status, String)>,
{
    let operation_handle = {
        let state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        state.operations.get(&handle).cloned().ok_or_else(|| {
            (
                Status::InvalidHandle,
                "PowerShell operation handle is invalid".to_owned(),
            )
        })?
    };
    operation(&operation_handle)
}

fn write_operation_state(
    state_output: *mut u32,
    terminal_status_output: *mut i32,
    snapshot: (OperationState, Status, String, Option<Arc<InvocationResult>>),
) -> Result<Status, (Status, String)> {
    if state_output.is_null() || terminal_status_output.is_null() {
        return Err((
            Status::InvalidArgument,
            "PowerShell operation state output pointer is null".to_owned(),
        ));
    }

    let (state, terminal_status, diagnostic, _) = snapshot;
    unsafe {
        *state_output = state as u32;
        *terminal_status_output = terminal_status.value();
    }
    if state.is_terminal() && terminal_status != Status::Success {
        return Err((terminal_status, diagnostic));
    }
    Ok(Status::Success)
}

fn poll_operation(
    handle: u64,
    state_output: *mut u32,
    terminal_status_output: *mut i32,
) -> Result<Status, (Status, String)> {
    with_operation(handle, |operation| {
        write_operation_state(state_output, terminal_status_output, operation.snapshot())
    })
}

fn wait_operation(
    handle: u64,
    timeout_milliseconds: u32,
    state_output: *mut u32,
    terminal_status_output: *mut i32,
) -> Result<Status, (Status, String)> {
    with_operation(handle, |operation| {
        let timeout = if timeout_milliseconds == u32::MAX {
            None
        } else {
            Some(Duration::from_millis(u64::from(timeout_milliseconds)))
        };
        write_operation_state(state_output, terminal_status_output, operation.wait(timeout))
    })
}

fn stop_operation(handle: u64) -> Result<Status, (Status, String)> {
    with_operation(handle, |operation| {
        operation.request_stop();
        Ok(Status::Success)
    })
}

fn release_operation(handle: u64) -> Result<Status, (Status, String)> {
    let operation = {
        let mut state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        state.operations.remove(&handle).ok_or_else(|| {
            (
                Status::InvalidHandle,
                "PowerShell operation handle is invalid".to_owned(),
            )
        })?
    };
    operation.request_stop();
    Ok(Status::Success)
}

fn operation_result(handle: u64) -> Result<u64, (Status, String)> {
    let operation = {
        let state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        state.operations.get(&handle).cloned().ok_or_else(|| {
            (
                Status::InvalidHandle,
                "PowerShell operation handle is invalid".to_owned(),
            )
        })?
    };
    let (operation_state, terminal_status, diagnostic, invocation_result) = operation.snapshot();
    if !operation_state.is_terminal() {
        return Err((
            Status::OperationNotTerminal,
            "PowerShell operation has not reached a terminal state.".to_owned(),
        ));
    }
    if terminal_status != Status::Success {
        return Err((terminal_status, diagnostic));
    }
    let invocation_result = invocation_result.ok_or_else(|| {
        (
            Status::ManagedFailure,
            "completed PowerShell operation has no immutable result snapshot".to_owned(),
        )
    })?;

    let mut state = match state().lock() {
        Ok(state) => state,
        Err(poisoned) => poisoned.into_inner(),
    };
    let result_handle = state.next_result_handle;
    state.next_result_handle = state
        .next_result_handle
        .checked_add(1)
        .filter(|value| *value != 0)
        .unwrap_or(1_u64 << 63);
    state.results.insert(result_handle, invocation_result);
    Ok(result_handle)
}

fn stop_session_operation(handle: u64) -> Result<Status, (Status, String)> {
    let (session, operation) = {
        let state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        let session = state
            .sessions
            .get(&handle)
            .cloned()
            .ok_or_else(|| (Status::InvalidHandle, "PowerShell handle is invalid".to_owned()))?;
        let operation = state
            .operations
            .values()
            .find(|operation| operation.builder_handle == handle && !operation.snapshot().0.is_terminal())
            .cloned();
        (session, operation)
    };

    if let Some(operation) = operation {
        operation.request_stop();
        return Ok(Status::Success);
    }

    cancel_active_capability(&session);
    session
        .power_shell
        .stop()
        .map(|_| Status::Success)
        .map_err(managed_failure)
}

fn release_result(handle: u64) -> Result<Status, (Status, String)> {
    let mut state = match state().lock() {
        Ok(state) => state,
        Err(poisoned) => poisoned.into_inner(),
    };
    state
        .results
        .remove(&handle)
        .map(|_| Status::Success)
        .ok_or_else(|| (Status::InvalidHandle, "Invocation result handle is invalid".to_owned()))
}

unsafe fn register_capabilities(registration: *const CapabilityRegistration) -> Result<u64, (Status, String)> {
    if registration.is_null() {
        return Err((
            Status::InvalidArgument,
            "capability registration pointer is null".to_owned(),
        ));
    }
    let registration = &*registration;
    if registration.size < mem::size_of::<CapabilityRegistration>() as u32
        || registration.flags != CAPABILITY_REGISTRATION_VERSION
    {
        return Err((
            Status::InvalidArgument,
            "capability registration header is invalid".to_owned(),
        ));
    }
    let dispatch = registration.dispatch.ok_or_else(|| {
        (
            Status::InvalidArgument,
            "capability registration dispatch callback is null".to_owned(),
        )
    })?;
    let cancel = registration.cancel.ok_or_else(|| {
        (
            Status::InvalidArgument,
            "capability registration cancellation callback is null".to_owned(),
        )
    })?;
    let (kind, payload) = data_value_input(registration.definitions)?;
    let definitions = parse_capability_definitions(kind, payload)?;

    let mut state = state().lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    if state.runtime.is_none() {
        return Err((
            Status::NotInitialized,
            "PowerShell runtime is not initialized".to_owned(),
        ));
    }
    let handle = state.next_capability_handle;
    state.next_capability_handle = state
        .next_capability_handle
        .checked_add(1)
        .filter(|value| *value != 0)
        .unwrap_or(1_u64 << 60);
    state.capabilities.insert(
        handle,
        Arc::new(CapabilityRegistrationState {
            handle,
            definitions,
            dispatch,
            cancel,
            active: AtomicBool::new(true),
            invocations: Mutex::new(HashMap::new()),
        }),
    );
    Ok(handle)
}

fn release_capabilities(handle: u64) -> Result<Status, (Status, String)> {
    let registration = state()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .capabilities
        .remove(&handle)
        .ok_or_else(|| {
            (
                Status::InvalidHandle,
                "capability registration handle is invalid".to_owned(),
            )
        })?;
    registration.revoke();
    Ok(Status::Success)
}

fn set_capabilities(handle: u64, capability_handle: u64) -> Result<Status, (Status, String)> {
    let (session, registration) = {
        let state = state().lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let session = state
            .sessions
            .get(&handle)
            .cloned()
            .ok_or_else(|| (Status::InvalidHandle, "PowerShell handle is invalid".to_owned()))?;
        let registration = state.capabilities.get(&capability_handle).cloned().ok_or_else(|| {
            (
                Status::InvalidHandle,
                "capability registration handle is invalid".to_owned(),
            )
        })?;
        (session, registration)
    };
    if !registration.active.load(Ordering::Acquire) {
        return Err((
            Status::InvalidHandle,
            "capability registration has been released".to_owned(),
        ));
    }
    if session_has_active_operation(&session)
        || session
            .runspace_session
            .as_ref()
            .is_some_and(|runspace| runspace_session_has_active_operation(runspace))
    {
        return Err((
            Status::Backpressure,
            "capability context cannot change while its session has an active invocation".to_owned(),
        ));
    }
    let mut attached = session
        .capability_registration
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if attached.is_some() {
        return Err((
            Status::Backpressure,
            "a capability context is already attached; invoke or clear the builder first".to_owned(),
        ));
    }
    *attached = Some(registration);
    Ok(Status::Success)
}

fn with_result<F>(handle: u64, operation: F) -> Result<Status, (Status, String)>
where
    F: FnOnce(&FfiInvocationResult) -> Result<Status, (Status, String)>,
{
    let result = {
        let state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        match state.results.get(&handle) {
            Some(result) => Arc::clone(result),
            None => {
                return Err((Status::InvalidHandle, "Invocation result handle is invalid".to_owned()));
            }
        }
    };

    let _operation_lock = SESSION_OPERATION_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    operation(&result.result)
}

#[allow(clippy::arc_with_non_send_sync)]
fn initialize_payload(
    payload_path: &str,
    manifest_path: &str,
    manifest_sha256: &str,
    trust_policy: TrustPolicy,
) -> Result<Status, (Status, String)> {
    let validated = payload::validate(ValidationRequest {
        payload_path,
        manifest_path,
        manifest_sha256,
        trust_policy,
    })
    .map_err(validation_failure)?;
    let payload_path = validated.payload_root;
    let session_policy = Arc::new(validated.session_policy);
    let mut state = match state().lock() {
        Ok(state) => state,
        Err(poisoned) => poisoned.into_inner(),
    };

    if let Some(runtime) = &state.runtime {
        return if runtime.pwsh_dir() == payload_path {
            Ok(Status::Success)
        } else {
            Err((
                Status::IncompatiblePayload,
                format!(
                    "PowerShell runtime is already initialized for {}; cannot select {}",
                    runtime.pwsh_dir().display(),
                    payload_path.display()
                ),
            ))
        };
    }

    let runtime =
        HostedRuntime::new_for_pwsh_dir(&payload_path).map_err(|error| (Status::HostFailure, error.to_string()))?;
    state.runtime = Some(Arc::new(runtime));
    state.session_policy = Some(session_policy);
    Ok(Status::Success)
}

fn initialize_unsafe_local_development(payload_path: &str) -> Result<Status, (Status, String)> {
    let manifest_path = PathBuf::from(payload_path).join(MANIFEST_FILE_NAME);
    let manifest_path = manifest_path.to_str().ok_or_else(|| {
        (
            Status::InvalidArgument,
            "default local development manifest path is not valid UTF-8".to_owned(),
        )
    })?;
    initialize_payload(
        payload_path,
        manifest_path,
        "",
        TrustPolicy::AllowUntrustedLocalDevelopment,
    )
}

fn validation_failure(error: ValidationError) -> (Status, String) {
    let status = match error {
        ValidationError::InvalidArgument(_) => Status::InvalidArgument,
        ValidationError::ManifestInvalid(_) => Status::PayloadManifestInvalid,
        ValidationError::Untrusted(_) => Status::PayloadUntrusted,
        ValidationError::HashMismatch(_) => Status::PayloadHashMismatch,
        ValidationError::Incompatible(_) => Status::PayloadIncompatible,
    };
    (status, error.message().to_owned())
}

fn parse_trust_policy(value: u32) -> Result<TrustPolicy, (Status, String)> {
    match value {
        0 => Ok(TrustPolicy::RequireHashPinnedManifest),
        1 => Ok(TrustPolicy::AllowUntrustedLocalDevelopment),
        _ => Err((
            Status::InvalidArgument,
            "payload activation trust policy is invalid".to_owned(),
        )),
    }
}

fn create_session_result() -> Result<u64, (Status, String)> {
    let mut state = match state().lock() {
        Ok(state) => state,
        Err(poisoned) => poisoned.into_inner(),
    };
    let runtime = state.runtime.as_ref().cloned().ok_or_else(|| {
        (
            Status::NotInitialized,
            "PowerShell runtime is not initialized".to_owned(),
        )
    })?;
    let power_shell =
        FfiPowerShell::new_for_runtime(runtime).map_err(|error| (Status::HostFailure, error.to_string()))?;
    let handle = state.next_handle;
    state.next_handle = state.next_handle.checked_add(1).unwrap_or(1);
    state.sessions.insert(
        handle,
        Arc::new(Session {
            power_shell,
            operation_active: Mutex::new(false),
            runspace_session: None,
            capability_registration: Mutex::new(None),
            active_capability: Mutex::new(None),
        }),
    );
    Ok(handle)
}

fn release_session_result(handle: u64) -> Result<Status, (Status, String)> {
    let operations = {
        let mut state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        state
            .sessions
            .remove(&handle)
            .ok_or_else(|| (Status::InvalidHandle, "PowerShell handle is invalid".to_owned()))?;
        state
            .operations
            .values()
            .filter(|operation| operation.builder_handle == handle)
            .cloned()
            .collect::<Vec<_>>()
    };
    for operation in operations {
        operation.request_stop();
    }
    Ok(Status::Success)
}

struct SessionOptionsInput<'a> {
    runspace_mode: u32,
    initial_configuration: u32,
    history_mode: u32,
    error_preference: u32,
    warning_preference: u32,
    verbose_preference: u32,
    debug_preference: u32,
    information_preference: u32,
    execution_policy: u32,
    initial_variables: &'a [u8],
    module_imports: Vec<&'a str>,
    allowed_module_paths: Vec<&'a str>,
    allowed_module_paths_payload: &'a [u8],
    working_directory: &'a str,
    environment: Vec<(&'a str, &'a str)>,
    environment_payload: &'a [u8],
}

unsafe fn session_options_input<'a>(
    options: *const SessionOptions,
) -> Result<SessionOptionsInput<'a>, (Status, String)> {
    if options.is_null() {
        return Err((
            Status::InvalidArgument,
            "PowerShell session options structure is null".to_owned(),
        ));
    }
    let prefix = &*(options as *const SessionOptionsPrefix);
    let extended_size = mem::size_of::<SessionOptions>() as u32;
    if prefix.size < SESSION_OPTIONS_PREFIX_SIZE
        || (prefix.size > SESSION_OPTIONS_PREFIX_SIZE && prefix.size < extended_size)
    {
        return Err((
            Status::InvalidArgument,
            "PowerShell session options structure is missing or too small".to_owned(),
        ));
    }
    let has_extended_configuration = prefix.size >= extended_size;
    if prefix.flags != 0 || prefix._reserved != 0 {
        return Err((
            Status::InvalidArgument,
            "PowerShell session options flags and reserved fields must be zero".to_owned(),
        ));
    }
    if prefix.runspace_mode > 1
        || prefix.initial_configuration > 1
        || prefix.history_mode > 1
        || prefix.error_preference > 3
        || prefix.warning_preference > 3
        || prefix.verbose_preference > 3
        || prefix.debug_preference > 3
        || prefix.information_preference > 3
    {
        return Err((
            Status::InvalidArgument,
            "PowerShell session options contain an unsupported enum value".to_owned(),
        ));
    }
    let legacy_allowed_module_path = utf8_span(prefix.allowed_module_path).map_err(|_| {
        (
            Status::InvalidArgument,
            "PowerShell session allowed module path must be UTF-8 without NUL".to_owned(),
        )
    })?;
    if !legacy_allowed_module_path.is_empty() {
        return Err((
            Status::InvalidArgument,
            "PowerShell session allowed_module_path is obsolete; use the appended allowed_module_paths configuration"
                .to_owned(),
        ));
    }
    let (
        execution_policy,
        initial_variables,
        module_imports,
        allowed_module_paths,
        allowed_module_paths_payload,
        working_directory,
        environment,
        environment_payload,
    ) = if has_extended_configuration {
        let options = &*options;
        if options.configuration_flags != 0 || options.execution_policy > 1 {
            return Err((
                Status::InvalidArgument,
                "PowerShell session configuration flags and execution policy are invalid".to_owned(),
            ));
        }
        let (initial_variables_kind, initial_variables) = data_value_input(&options.initial_variables)?;
        if initial_variables_kind != VALUE_KIND_PROPERTY_BAG {
            return Err((
                Status::InvalidArgument,
                "PowerShell session initial variables must be a tagged property bag".to_owned(),
            ));
        }
        validate_session_initial_variables(initial_variables)?;
        let (module_imports_kind, module_imports_payload) = data_value_input(&options.module_imports)?;
        let module_imports = session_string_array(module_imports_kind, module_imports_payload, "module imports")?;
        let (module_paths_kind, module_paths_payload) = data_value_input(&options.allowed_module_paths)?;
        let allowed_module_paths = session_string_array(module_paths_kind, module_paths_payload, "module paths")?;
        let working_directory = utf8_span(options.working_directory).map_err(|_| {
            (
                Status::InvalidArgument,
                "PowerShell session working directory must be UTF-8 without NUL".to_owned(),
            )
        })?;
        if working_directory.len() > MAX_SESSION_PATH_BYTES
            || (!working_directory.is_empty() && !std::path::Path::new(working_directory).is_absolute())
        {
            return Err((
                Status::InvalidArgument,
                "PowerShell session working directory must be an absolute bounded path".to_owned(),
            ));
        }
        let (environment_kind, environment_payload) = data_value_input(&options.environment)?;
        let environment = session_environment(environment_kind, environment_payload)?;
        (
            options.execution_policy,
            initial_variables,
            module_imports,
            allowed_module_paths,
            module_paths_payload,
            working_directory,
            environment,
            environment_payload,
        )
    } else {
        (
            0,
            &EMPTY_VALUE_CONTAINER[..],
            Vec::new(),
            Vec::new(),
            &EMPTY_VALUE_CONTAINER[..],
            "",
            Vec::new(),
            &EMPTY_VALUE_CONTAINER[..],
        )
    };
    if prefix.runspace_mode == 0
        && (prefix.initial_configuration != 0
            || prefix.history_mode != 0
            || prefix.error_preference != 0
            || prefix.warning_preference != 0
            || prefix.verbose_preference != 0
            || prefix.debug_preference != 0
            || prefix.information_preference != 0
            || execution_policy != 0
            || !initial_variables.is_empty()
            || !module_imports.is_empty()
            || !allowed_module_paths.is_empty()
            || !working_directory.is_empty()
            || !environment.is_empty())
    {
        return Err((
            Status::UnsupportedCapability,
            "current-runspace sessions cannot change configuration, history, preferences, variables, imports, paths, working directory, or environment".to_owned(),
        ));
    }

    Ok(SessionOptionsInput {
        runspace_mode: prefix.runspace_mode,
        initial_configuration: prefix.initial_configuration,
        history_mode: prefix.history_mode,
        error_preference: prefix.error_preference,
        warning_preference: prefix.warning_preference,
        verbose_preference: prefix.verbose_preference,
        debug_preference: prefix.debug_preference,
        information_preference: prefix.information_preference,
        execution_policy,
        initial_variables,
        module_imports,
        allowed_module_paths,
        allowed_module_paths_payload,
        working_directory,
        environment,
        environment_payload,
    })
}

fn session_string_array<'a>(kind: u32, payload: &'a [u8], description: &str) -> Result<Vec<&'a str>, (Status, String)> {
    if kind != VALUE_KIND_ARRAY {
        return Err((
            Status::InvalidArgument,
            format!("PowerShell session {} must be a tagged array", description),
        ));
    }
    let mut offset = 0;
    let count = read_value_u32(payload, &mut offset, description)? as usize;
    if count > MAX_SESSION_CONFIGURATION_ENTRIES {
        return Err((
            Status::InvalidArgument,
            format!(
                "PowerShell session {} exceed the bound of {}",
                description, MAX_SESSION_CONFIGURATION_ENTRIES
            ),
        ));
    }
    let mut values = Vec::with_capacity(count);
    for _ in 0..count {
        let nested_kind = read_value_u32(payload, &mut offset, description)?;
        let length = read_value_u32(payload, &mut offset, description)? as usize;
        let bytes = read_value_bytes(payload, &mut offset, length, description)?;
        if nested_kind != VALUE_KIND_STRING {
            return Err((
                Status::InvalidArgument,
                format!("PowerShell session {} must contain only tagged strings", description),
            ));
        }
        let value = std::str::from_utf8(bytes).map_err(|_| {
            (
                Status::InvalidArgument,
                format!("PowerShell session {} contains invalid UTF-8", description),
            )
        })?;
        if value.is_empty() || value.as_bytes().contains(&0) {
            return Err((
                Status::InvalidArgument,
                format!("PowerShell session {} contains an invalid string", description),
            ));
        }
        if description == "module imports" && !valid_module_import_name(value) {
            return Err((
                Status::InvalidArgument,
                "PowerShell session module imports must use bounded ASCII module names".to_owned(),
            ));
        }
        if description == "module paths"
            && (value.len() > MAX_SESSION_PATH_BYTES || !std::path::Path::new(value).is_absolute())
        {
            return Err((
                Status::InvalidArgument,
                "PowerShell session module paths must be absolute bounded paths".to_owned(),
            ));
        }
        values.push(value);
    }
    if offset != payload.len() {
        return Err((
            Status::InvalidArgument,
            format!("PowerShell session {} contain trailing bytes", description),
        ));
    }
    Ok(values)
}

fn session_environment(kind: u32, payload: &[u8]) -> Result<Vec<(&str, &str)>, (Status, String)> {
    if kind != VALUE_KIND_PROPERTY_BAG {
        return Err((
            Status::InvalidArgument,
            "PowerShell session environment must be a tagged property bag".to_owned(),
        ));
    }
    let mut offset = 0;
    let count = read_value_u32(payload, &mut offset, "environment")? as usize;
    if count > MAX_SESSION_CONFIGURATION_ENTRIES {
        return Err((
            Status::InvalidArgument,
            format!(
                "PowerShell session environment exceeds the bound of {}",
                MAX_SESSION_CONFIGURATION_ENTRIES
            ),
        ));
    }
    let mut entries: Vec<(&str, &str)> = Vec::with_capacity(count);
    for _ in 0..count {
        let key_length = read_value_u32(payload, &mut offset, "environment key")? as usize;
        let key_bytes = read_value_bytes(payload, &mut offset, key_length, "environment key")?;
        let key = std::str::from_utf8(key_bytes).map_err(|_| {
            (
                Status::InvalidArgument,
                "PowerShell session environment key is invalid UTF-8".to_owned(),
            )
        })?;
        let value_kind = read_value_u32(payload, &mut offset, "environment value")?;
        let value_length = read_value_u32(payload, &mut offset, "environment value")? as usize;
        let value_bytes = read_value_bytes(payload, &mut offset, value_length, "environment value")?;
        if value_kind != VALUE_KIND_STRING {
            return Err((
                Status::InvalidArgument,
                "PowerShell session environment values must be tagged strings".to_owned(),
            ));
        }
        let value = std::str::from_utf8(value_bytes).map_err(|_| {
            (
                Status::InvalidArgument,
                "PowerShell session environment value is invalid UTF-8".to_owned(),
            )
        })?;
        if !valid_session_name(key) || value.as_bytes().contains(&0) || value.len() > 4096 {
            return Err((
                Status::InvalidArgument,
                "PowerShell session environment entry is invalid".to_owned(),
            ));
        }
        if entries.iter().any(|(existing, _)| existing.eq_ignore_ascii_case(key)) {
            return Err((
                Status::InvalidArgument,
                "PowerShell session environment keys must be unique".to_owned(),
            ));
        }
        entries.push((key, value));
    }
    if offset != payload.len() {
        return Err((
            Status::InvalidArgument,
            "PowerShell session environment contains trailing bytes".to_owned(),
        ));
    }
    Ok(entries)
}

fn valid_session_name(value: &str) -> bool {
    let mut bytes = value.bytes();
    match bytes.next() {
        Some(byte) if byte.is_ascii_alphabetic() || byte == b'_' => {}
        _ => return false,
    }

    value.len() <= 64 && bytes.all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
}

fn validate_session_initial_variables(payload: &[u8]) -> Result<(), (Status, String)> {
    let mut offset = 0;
    let count = read_value_u32(payload, &mut offset, "initial variable count")? as usize;
    if count > MAX_SESSION_CONFIGURATION_ENTRIES {
        return Err((
            Status::InvalidArgument,
            format!(
                "PowerShell session initial variables exceed the bound of {}",
                MAX_SESSION_CONFIGURATION_ENTRIES
            ),
        ));
    }
    let mut names: Vec<&str> = Vec::with_capacity(count);
    for _ in 0..count {
        let key_length = read_value_u32(payload, &mut offset, "initial variable name")? as usize;
        let key_bytes = read_value_bytes(payload, &mut offset, key_length, "initial variable name")?;
        let key = std::str::from_utf8(key_bytes).map_err(|_| {
            (
                Status::InvalidArgument,
                "PowerShell session initial variable name is invalid UTF-8".to_owned(),
            )
        })?;
        if !valid_session_name(key) || names.iter().any(|existing| existing.eq_ignore_ascii_case(key)) {
            return Err((
                Status::InvalidArgument,
                "PowerShell session initial variable names must be unique identifiers".to_owned(),
            ));
        }
        names.push(key);
        validate_nested_value(payload, &mut offset, 1)?;
    }
    if offset == payload.len() {
        Ok(())
    } else {
        Err((
            Status::InvalidArgument,
            "PowerShell session initial variables contain trailing bytes".to_owned(),
        ))
    }
}

fn create_runspace_session(options: SessionOptionsInput<'_>) -> Result<u64, (Status, String)> {
    let mut state = match state().lock() {
        Ok(state) => state,
        Err(poisoned) => poisoned.into_inner(),
    };
    let runtime = state.runtime.as_ref().cloned().ok_or_else(|| {
        (
            Status::NotInitialized,
            "PowerShell runtime is not initialized".to_owned(),
        )
    })?;
    let session_policy = state.session_policy.as_ref().cloned().ok_or_else(|| {
        (
            Status::NotInitialized,
            "PowerShell payload session policy is unavailable".to_owned(),
        )
    })?;
    let resolved_module_imports = enforce_session_policy(&options, &session_policy)?;
    let resolved_module_imports_payload = encode_string_array(&resolved_module_imports)?;
    let session = FfiPowerShellSession::new_for_runtime(
        runtime,
        options.runspace_mode,
        options.initial_configuration,
        options.history_mode,
        options.error_preference,
        options.warning_preference,
        options.verbose_preference,
        options.debug_preference,
        options.information_preference,
        options.execution_policy,
        options.initial_variables,
        &resolved_module_imports_payload,
        options.allowed_module_paths_payload,
        options.working_directory,
        options.environment_payload,
    )
    .map_err(managed_failure)?;
    let handle = state.next_runspace_session_handle;
    state.next_runspace_session_handle = state
        .next_runspace_session_handle
        .checked_add(1)
        .filter(|value| *value != 0)
        .unwrap_or(1_u64 << 61);
    state.runspace_sessions.insert(
        handle,
        Arc::new(RunspaceSession {
            session,
            operation_active: Mutex::new(false),
        }),
    );
    Ok(handle)
}

fn enforce_session_policy(
    options: &SessionOptionsInput<'_>,
    policy: &SessionPolicy,
) -> Result<Vec<PathBuf>, (Status, String)> {
    if !options.allowed_module_paths.is_empty() {
        if policy.module_paths.is_empty() {
            return Err((
                Status::SessionPolicyViolation,
                "payload session policy does not permit module paths".to_owned(),
            ));
        }
        for path in &options.allowed_module_paths {
            let canonical = fs::canonicalize(path).map_err(|_| {
                (
                    Status::SessionPolicyViolation,
                    "a requested module path is not an approved existing directory".to_owned(),
                )
            })?;
            if !canonical.is_dir() || !policy.module_paths.contains(&canonical) {
                return Err((
                    Status::SessionPolicyViolation,
                    "a requested module path is not approved by the payload session policy".to_owned(),
                ));
            }
        }
    }
    let mut resolved_module_imports = Vec::with_capacity(options.module_imports.len());
    if !options.module_imports.is_empty() {
        if policy.module_imports.is_empty() || options.allowed_module_paths.is_empty() {
            return Err((
                Status::SessionPolicyViolation,
                "payload session policy does not permit the requested module imports".to_owned(),
            ));
        }
        if options
            .module_imports
            .iter()
            .any(|name| !valid_module_import_name(name))
            || options
                .module_imports
                .iter()
                .any(|name| !policy.module_imports.contains(&name.to_ascii_lowercase()))
        {
            return Err((
                Status::SessionPolicyViolation,
                "a requested module import is not approved by the payload session policy".to_owned(),
            ));
        }
        for name in &options.module_imports {
            let identity = policy
                .module_identities
                .get(&name.to_ascii_lowercase())
                .ok_or_else(|| {
                    (
                        Status::SessionPolicyViolation,
                        "a requested module import does not have an approved exact manifest identity".to_owned(),
                    )
                })?;
            let is_within_requested_path = options.allowed_module_paths.iter().any(|path| {
                fs::canonicalize(path)
                    .map(|canonical| identity.manifest_path.starts_with(canonical))
                    .unwrap_or(false)
            });
            if !is_within_requested_path {
                return Err((
                    Status::SessionPolicyViolation,
                    "an approved module manifest is outside the requested approved module paths".to_owned(),
                ));
            }
            resolved_module_imports.push(identity.manifest_path.clone());
        }
    }
    if !options.working_directory.is_empty() {
        let canonical = fs::canonicalize(options.working_directory).map_err(|_| {
            (
                Status::SessionPolicyViolation,
                "the requested working directory is not an approved existing directory".to_owned(),
            )
        })?;
        if !canonical.is_dir() || !policy.working_directories.contains(&canonical) {
            return Err((
                Status::SessionPolicyViolation,
                "the requested working directory is not approved by the payload session policy".to_owned(),
            ));
        }
    }
    if !options.environment.is_empty()
        && (policy.environment_keys.is_empty()
            || options
                .environment
                .iter()
                .any(|(key, _)| !policy.environment_keys.contains(&key.to_ascii_lowercase())))
    {
        return Err((
            Status::SessionPolicyViolation,
            "a requested environment key is not approved by the payload session policy".to_owned(),
        ));
    }
    Ok(resolved_module_imports)
}

fn encode_string_array(values: &[PathBuf]) -> Result<Vec<u8>, (Status, String)> {
    if values.len() > MAX_SESSION_CONFIGURATION_ENTRIES {
        return Err((
            Status::InvalidArgument,
            "PowerShell session module imports exceed their bound".to_owned(),
        ));
    }
    let mut payload = Vec::with_capacity(4 + values.len() * 16);
    payload.extend_from_slice(&(values.len() as u32).to_le_bytes());
    for value in values {
        let value = value.to_str().ok_or_else(|| {
            (
                Status::SessionPolicyViolation,
                "an approved module manifest path is not valid UTF-8".to_owned(),
            )
        })?;
        #[cfg(windows)]
        let value = value.strip_prefix(r"\\?\").unwrap_or(value);
        if value.len() > MAX_SESSION_PATH_BYTES || value.as_bytes().contains(&0) {
            return Err((
                Status::SessionPolicyViolation,
                "an approved module manifest path exceeds its bound".to_owned(),
            ));
        }
        payload.extend_from_slice(&VALUE_KIND_STRING.to_le_bytes());
        payload.extend_from_slice(&(value.len() as u32).to_le_bytes());
        payload.extend_from_slice(value.as_bytes());
    }
    if payload.len() > MAX_VALUE_PAYLOAD_BYTES {
        return Err((
            Status::SessionPolicyViolation,
            "approved module identity payload exceeds the tagged-value bound".to_owned(),
        ));
    }
    Ok(payload)
}

fn valid_module_import_name(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

fn release_runspace_session(handle: u64) -> Result<Status, (Status, String)> {
    let mut state = match state().lock() {
        Ok(state) => state,
        Err(poisoned) => poisoned.into_inner(),
    };
    state
        .runspace_sessions
        .remove(&handle)
        .map(|_| Status::Success)
        .ok_or_else(|| (Status::InvalidHandle, "PowerShell session handle is invalid".to_owned()))
}

fn with_runspace_session<F>(handle: u64, operation: F) -> Result<Status, (Status, String)>
where
    F: FnOnce(&FfiPowerShellSession) -> Result<Status, (Status, String)>,
{
    let session = {
        let state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        state
            .runspace_sessions
            .get(&handle)
            .cloned()
            .ok_or_else(|| (Status::InvalidHandle, "PowerShell session handle is invalid".to_owned()))?
    };
    operation(&session.session)
}

fn with_runspace_session_mutation<F>(handle: u64, operation: F) -> Result<Status, (Status, String)>
where
    F: FnOnce(&FfiPowerShellSession) -> Result<Status, (Status, String)>,
{
    let session = {
        let state = match state().lock() {
            Ok(state) => state,
            Err(poisoned) => poisoned.into_inner(),
        };
        state
            .runspace_sessions
            .get(&handle)
            .cloned()
            .ok_or_else(|| (Status::InvalidHandle, "PowerShell session handle is invalid".to_owned()))?
    };
    if runspace_session_has_active_operation(&session) {
        return Err((
            Status::Backpressure,
            "PowerShell session variables cannot be read or changed while an async operation is pending or running."
                .to_owned(),
        ));
    }
    let _operation_lock = SESSION_OPERATION_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if runspace_session_has_active_operation(&session) {
        return Err((
            Status::Backpressure,
            "PowerShell session variables cannot be read or changed while an async operation is pending or running."
                .to_owned(),
        ));
    }
    operation(&session.session)
}

fn create_session_builder(handle: u64) -> Result<u64, (Status, String)> {
    let mut state = match state().lock() {
        Ok(state) => state,
        Err(poisoned) => poisoned.into_inner(),
    };
    let runspace_session = state
        .runspace_sessions
        .get(&handle)
        .cloned()
        .ok_or_else(|| (Status::InvalidHandle, "PowerShell session handle is invalid".to_owned()))?;
    // Keep STATE locked through the managed lease acquisition. Session release
    // removes the public handle under the same lock, so it cannot race this
    // successful builder creation and produce a post-release builder.
    let power_shell = runspace_session.session.create_builder().map_err(managed_failure)?;
    let builder_handle = state.next_handle;
    state.next_handle = state.next_handle.checked_add(1).unwrap_or(1);
    state.sessions.insert(
        builder_handle,
        Arc::new(Session {
            power_shell,
            operation_active: Mutex::new(false),
            runspace_session: Some(runspace_session),
            capability_registration: Mutex::new(None),
            active_capability: Mutex::new(None),
        }),
    );
    Ok(builder_handle)
}

fn write_session_snapshot(
    snapshot: *mut SessionSnapshot,
    value: FfiSessionSnapshot,
) -> Result<Status, (Status, String)> {
    if snapshot.is_null() || unsafe { (*snapshot).size } < mem::size_of::<SessionSnapshot>() as u32 {
        return Err((
            Status::InvalidArgument,
            "PowerShell session snapshot structure is missing or too small".to_owned(),
        ));
    }
    if value.event_count > 32 {
        return Err((
            Status::ManagedFailure,
            "managed PowerShell session event count exceeds its bound".to_owned(),
        ));
    }
    unsafe {
        (*snapshot).state = value.state;
        (*snapshot).runspace_state = value.runspace_state;
        (*snapshot).flags = value.flags;
        (*snapshot).active_pipeline_count = value.active_pipeline_count;
        (*snapshot).event_count = value.event_count;
        (*snapshot).invocation_count = value.invocation_count;
        (*snapshot).history_count = value.history_count;
    }
    Ok(Status::Success)
}

fn validate_pool_options(options: *const SessionPoolOptions) -> Result<(), (Status, String)> {
    if options.is_null() || unsafe { (*options).size } < mem::size_of::<SessionPoolOptions>() as u32 {
        return Err((
            Status::InvalidArgument,
            "PowerShell session pool options structure is missing or too small".to_owned(),
        ));
    }
    let options = unsafe { &*options };
    if options.flags != 0 || options._reserved != 0 || options.maximum_sessions == 0 || options.maximum_sessions > 64 {
        return Err((
            Status::InvalidArgument,
            "PowerShell session pool options are invalid or exceed the bound of 64 sessions".to_owned(),
        ));
    }
    if options.minimum_sessions > options.maximum_sessions {
        return Err((
            Status::InvalidArgument,
            "PowerShell session pool minimum sessions exceeds maximum sessions".to_owned(),
        ));
    }
    Ok(())
}

#[no_mangle]
pub extern "C" fn dps_pwsh_abi_version() -> u32 {
    ABI_VERSION
}

#[no_mangle]
pub extern "C" fn dps_pwsh_feature_flags() -> u64 {
    FEATURE_STRUCTURED_INVOCATION_ERRORS
        | FEATURE_PER_CALL_DIAGNOSTICS
        | FEATURE_UTF8_SPANS
        | FEATURE_IMMUTABLE_RESULTS
        | FEATURE_TAGGED_VALUES
        | FEATURE_COMMAND_OPTIONS
        | FEATURE_BOUNDED_INPUT
        | FEATURE_INVOCATION_METADATA
        | FEATURE_ASYNC_OPERATIONS
        | FEATURE_PAYLOAD_MANIFEST
        | FEATURE_SESSIONS
        | FEATURE_SESSION_POLLING
        | FEATURE_SESSION_POOL_REJECTION
        | FEATURE_SNAPSHOT_PROJECTIONS
        | FEATURE_SESSION_CONFIGURATION
        | FEATURE_SESSION_VARIABLES
        | FEATURE_CAPABILITY_RPC
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_get_abi_info(info: *mut AbiInfo) -> i32 {
    if info.is_null() || (*info).size < std::mem::size_of::<AbiInfo>() as u32 {
        return Status::InvalidArgument.value();
    }

    (*info).abi_version = ABI_VERSION;
    (*info).feature_flags = dps_pwsh_feature_flags();
    (*info).minimum_compatible_abi_version = ABI_VERSION;
    (*info)._reserved = 0;
    Status::Success.value()
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_initialize_utf8(payload_path: Utf8Span, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        let payload_path = utf8_span(payload_path).map_err(|_| {
            (
                Status::InvalidArgument,
                "payload path must be UTF-8 without NUL".to_owned(),
            )
        })?;
        initialize_unsafe_local_development(payload_path)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_initialize_payload(
    activation: *const PayloadActivation,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if activation.is_null() || (*activation).size < std::mem::size_of::<PayloadActivation>() as u32 {
            return Err((
                Status::InvalidArgument,
                "payload activation structure is missing or too small".to_owned(),
            ));
        }
        if (*activation).flags != 0 || (*activation)._reserved != 0 {
            return Err((
                Status::InvalidArgument,
                "payload activation flags and reserved fields must be zero".to_owned(),
            ));
        }
        let payload_path = utf8_span((*activation).payload_path).map_err(|_| {
            (
                Status::InvalidArgument,
                "payload activation payload path must be UTF-8 without NUL".to_owned(),
            )
        })?;
        let manifest_path = utf8_span((*activation).manifest_path).map_err(|_| {
            (
                Status::InvalidArgument,
                "payload activation manifest path must be UTF-8 without NUL".to_owned(),
            )
        })?;
        let manifest_sha256 = utf8_span((*activation).manifest_sha256).map_err(|_| {
            (
                Status::InvalidArgument,
                "payload activation manifest SHA-256 must be UTF-8 without NUL".to_owned(),
            )
        })?;
        let trust_policy = parse_trust_policy((*activation).trust_policy)?;
        initialize_payload(payload_path, manifest_path, manifest_sha256, trust_policy)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_create(handle: *mut u64, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        if handle.is_null() {
            return Err((Status::InvalidArgument, "handle output pointer is null".to_owned()));
        }

        let value = create_session_result()?;
        *handle = value;
        Ok(Status::Success)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_release(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || release_session_result(handle))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_command_utf8(handle: u64, command: Utf8Span, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        let command = utf8_span(command)
            .map_err(|_| (Status::InvalidArgument, "command must be UTF-8 without NUL".to_owned()))?;
        with_session_result(handle, true, |session| {
            session
                .add_command(command)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_script_utf8(handle: u64, script: Utf8Span, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        let script =
            utf8_span(script).map_err(|_| (Status::InvalidArgument, "script must be UTF-8 without NUL".to_owned()))?;
        with_session_result(handle, true, |session| {
            session
                .add_script(script)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_command_utf8_local(
    handle: u64,
    command: Utf8Span,
    use_local_scope: u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if use_local_scope > 1 {
            return Err((Status::InvalidArgument, "local scope must be zero or one".to_owned()));
        }
        let command = utf8_span(command)
            .map_err(|_| (Status::InvalidArgument, "command must be UTF-8 without NUL".to_owned()))?;
        with_session_result(handle, true, |session| {
            session
                .add_command_scoped(command, use_local_scope != 0)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_script_utf8_local(
    handle: u64,
    script: Utf8Span,
    use_local_scope: u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if use_local_scope > 1 {
            return Err((Status::InvalidArgument, "local scope must be zero or one".to_owned()));
        }
        let script =
            utf8_span(script).map_err(|_| (Status::InvalidArgument, "script must be UTF-8 without NUL".to_owned()))?;
        with_session_result(handle, true, |session| {
            session
                .add_script_scoped(script, use_local_scope != 0)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_argument_utf8(
    handle: u64,
    argument: Utf8Span,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let argument = utf8_span(argument)
            .map_err(|_| (Status::InvalidArgument, "argument must be UTF-8 without NUL".to_owned()))?;
        with_session_result(handle, true, |session| {
            session
                .add_argument_string(argument)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_argument_value(
    handle: u64,
    value: *const DataValue,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let (kind, payload) = data_value_input(value)?;
        with_session_result(handle, true, |session| {
            session
                .add_argument_value(kind, payload)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_parameter_string_utf8(
    handle: u64,
    name: Utf8Span,
    value: Utf8Span,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let name = utf8_span(name).map_err(|_| {
            (
                Status::InvalidArgument,
                "parameter name must be UTF-8 without NUL".to_owned(),
            )
        })?;
        let value = utf8_span(value).map_err(|_| {
            (
                Status::InvalidArgument,
                "parameter value must be UTF-8 without NUL".to_owned(),
            )
        })?;
        with_session_result(handle, true, |session| {
            session
                .add_parameter_string(name, value)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_parameter_i64(
    handle: u64,
    name: Utf8Span,
    value: i64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let name = utf8_span(name).map_err(|_| {
            (
                Status::InvalidArgument,
                "parameter name must be UTF-8 without NUL".to_owned(),
            )
        })?;
        with_session_result(handle, true, |session| {
            session
                .add_parameter_long(name, value)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_parameter_value(
    handle: u64,
    name: Utf8Span,
    value: *const DataValue,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let name = utf8_span(name).map_err(|_| {
            (
                Status::InvalidArgument,
                "parameter name must be UTF-8 without NUL".to_owned(),
            )
        })?;
        let (kind, payload) = data_value_input(value)?;
        with_session_result(handle, true, |session| {
            session
                .add_parameter_value(name, kind, payload)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_parameter_switch(handle: u64, name: Utf8Span, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        let name = utf8_span(name).map_err(|_| {
            (
                Status::InvalidArgument,
                "parameter name must be UTF-8 without NUL".to_owned(),
            )
        })?;
        with_session_result(handle, true, |session| {
            session
                .add_parameter_switch(name)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_input_value(
    handle: u64,
    value: *const DataValue,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let (kind, payload) = data_value_input(value)?;
        with_session_result(handle, true, |session| {
            session
                .add_input_value(kind, payload)
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_complete_input(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        with_session_result(handle, true, |session| {
            session
                .complete_input()
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_reset_input(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        with_session_result(handle, true, |session| {
            session.reset_input().map(|_| Status::Success).map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_add_statement(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        with_session_result(handle, true, |session| {
            session
                .add_statement()
                .map(|_| Status::Success)
                .map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_clear(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        with_session_result(handle, true, |session| {
            session.clear().map(|_| Status::Success).map_err(managed_failure)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_invoke_utf8(
    handle: u64,
    buffer: *mut u8,
    buffer_len: usize,
    required_len: *mut usize,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        with_session_result(handle, true, |session| {
            let output = session.invoke_to_string().map_err(managed_failure)?;
            let status = write_utf8(buffer, buffer_len, required_len, &output);
            match status {
                Status::Success | Status::BufferTooSmall => Ok(status),
                Status::InvalidArgument => Err((
                    Status::InvalidArgument,
                    "output buffer arguments are invalid".to_owned(),
                )),
                _ => unreachable!(),
            }
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_get_invocation_error_count(
    handle: u64,
    error_count: *mut u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if error_count.is_null() {
            return Err((Status::InvalidArgument, "error count output pointer is null".to_owned()));
        }

        with_session_result(handle, true, |session| {
            let count = session.invocation_error_count().map_err(managed_failure)?;
            *error_count = u32::try_from(count).map_err(|_| {
                (
                    Status::ManagedFailure,
                    "managed error count exceeds the ABI limit".to_owned(),
                )
            })?;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_copy_invocation_error_field_utf8(
    handle: u64,
    error_index: u32,
    field: u32,
    buffer: *mut u8,
    buffer_len: usize,
    required_len: *mut usize,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let error_index = i32::try_from(error_index).map_err(|_| {
            (
                Status::InvalidArgument,
                "error index exceeds the managed ABI limit".to_owned(),
            )
        })?;
        let field = i32::try_from(field).map_err(|_| {
            (
                Status::InvalidArgument,
                "error field exceeds the managed ABI limit".to_owned(),
            )
        })?;
        with_session_result(handle, true, |session| {
            let value = session
                .invocation_error_field(error_index, field)
                .map_err(managed_failure)?;
            let status = write_utf8(buffer, buffer_len, required_len, &value);
            match status {
                Status::Success | Status::BufferTooSmall => Ok(status),
                Status::InvalidArgument => Err((
                    Status::InvalidArgument,
                    "output buffer arguments are invalid".to_owned(),
                )),
                _ => unreachable!(),
            }
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_stop(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || stop_session_operation(handle))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_capability_register(
    registration: *const CapabilityRegistration,
    capability_handle: *mut u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if capability_handle.is_null() {
            return Err((
                Status::InvalidArgument,
                "capability registration output pointer is null".to_owned(),
            ));
        }
        *capability_handle = register_capabilities(registration)?;
        Ok(Status::Success)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_capability_release(capability_handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || release_capabilities(capability_handle))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_set_capabilities(
    handle: u64,
    capability_handle: u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || set_capabilities(handle, capability_handle))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_invoke(handle: u64, result_handle: *mut u64, result: *mut CallResult) -> i32 {
    v2_call(result, || {
        if result_handle.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation result handle output pointer is null".to_owned(),
            ));
        }

        *result_handle = invoke_result(handle)?;
        Ok(Status::Success)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_release(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || release_result(handle))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_get_info(
    handle: u64,
    flags: *mut u32,
    sequence_count: *mut u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if flags.is_null() || sequence_count.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation result info output pointer is null".to_owned(),
            ));
        }

        with_result(handle, |snapshot| {
            let (snapshot_flags, count) = snapshot.info().map_err(managed_failure)?;
            *flags = snapshot_flags;
            *sequence_count = u32::try_from(count).map_err(|_| {
                (
                    Status::ManagedFailure,
                    "managed invocation sequence count exceeds the ABI limit".to_owned(),
                )
            })?;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_get_metadata(
    handle: u64,
    state: *mut u32,
    invocation_id: *mut u64,
    had_errors: *mut u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if state.is_null() || invocation_id.is_null() || had_errors.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation metadata output pointer is null".to_owned(),
            ));
        }
        with_result(handle, |snapshot| {
            let (metadata_state, metadata_invocation_id, metadata_had_errors) =
                snapshot.metadata().map_err(managed_failure)?;
            *state = metadata_state;
            *invocation_id = metadata_invocation_id;
            *had_errors = u32::from(metadata_had_errors);
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_get_stream_info(
    handle: u64,
    stream: u32,
    record_count: *mut u32,
    flags: *mut u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if record_count.is_null() || flags.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation stream info output pointer is null".to_owned(),
            ));
        }

        let stream = i32::try_from(stream).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream exceeds the managed ABI limit".to_owned(),
            )
        })?;
        with_result(handle, |snapshot| {
            let (count, stream_flags) = snapshot.stream_info(stream).map_err(managed_failure)?;
            *record_count = u32::try_from(count).map_err(|_| {
                (
                    Status::ManagedFailure,
                    "managed invocation stream count exceeds the ABI limit".to_owned(),
                )
            })?;
            *flags = stream_flags;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_get_stream_record_info(
    handle: u64,
    stream: u32,
    record_index: u32,
    sequence: *mut u64,
    flags: *mut u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if sequence.is_null() || flags.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation stream record info output pointer is null".to_owned(),
            ));
        }

        let stream = i32::try_from(stream).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream exceeds the managed ABI limit".to_owned(),
            )
        })?;
        let record_index = i32::try_from(record_index).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream record index exceeds the managed ABI limit".to_owned(),
            )
        })?;
        with_result(handle, |snapshot| {
            let (record_sequence, record_flags) = snapshot
                .stream_record_info(stream, record_index)
                .map_err(managed_failure)?;
            *sequence = u64::try_from(record_sequence).map_err(|_| {
                (
                    Status::ManagedFailure,
                    "managed invocation sequence is invalid".to_owned(),
                )
            })?;
            *flags = record_flags;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_copy_stream_record_field_utf8(
    handle: u64,
    stream: u32,
    record_index: u32,
    field: u32,
    buffer: *mut u8,
    buffer_len: usize,
    required_len: *mut usize,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let stream = i32::try_from(stream).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream exceeds the managed ABI limit".to_owned(),
            )
        })?;
        let record_index = i32::try_from(record_index).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream record index exceeds the managed ABI limit".to_owned(),
            )
        })?;
        let field = i32::try_from(field).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream field exceeds the managed ABI limit".to_owned(),
            )
        })?;
        with_result(handle, |snapshot| {
            let value = snapshot
                .stream_record_field(stream, record_index, field)
                .map_err(managed_failure)?;
            let status = write_utf8(buffer, buffer_len, required_len, &value);
            match status {
                Status::Success | Status::BufferTooSmall => Ok(status),
                Status::InvalidArgument => Err((
                    Status::InvalidArgument,
                    "invocation stream field buffer arguments are invalid".to_owned(),
                )),
                _ => unreachable!(),
            }
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_get_stream_totals(
    handle: u64,
    stream: u32,
    total_record_count: *mut u64,
    dropped_record_count: *mut u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if total_record_count.is_null() || dropped_record_count.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation stream totals output pointer is null".to_owned(),
            ));
        }
        let stream = i32::try_from(stream).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream exceeds the managed ABI limit".to_owned(),
            )
        })?;
        with_result(handle, |snapshot| {
            let (total, dropped) = snapshot.stream_totals(stream).map_err(managed_failure)?;
            *total_record_count = total;
            *dropped_record_count = dropped;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_get_stream_record_projection_info(
    handle: u64,
    stream: u32,
    record_index: u32,
    property_entry_count: *mut u32,
    dropped_property_entry_count: *mut u32,
    type_name_count: *mut u32,
    dropped_type_name_count: *mut u32,
    projection_flags: *mut u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if property_entry_count.is_null()
            || dropped_property_entry_count.is_null()
            || type_name_count.is_null()
            || dropped_type_name_count.is_null()
            || projection_flags.is_null()
        {
            return Err((
                Status::InvalidArgument,
                "invocation stream projection output pointer is null".to_owned(),
            ));
        }
        let stream = i32::try_from(stream).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream exceeds the managed ABI limit".to_owned(),
            )
        })?;
        let record_index = i32::try_from(record_index).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream record index exceeds the managed ABI limit".to_owned(),
            )
        })?;
        with_result(handle, |snapshot| {
            let info = snapshot
                .stream_record_projection_info(stream, record_index)
                .map_err(managed_failure)?;
            *property_entry_count = info.property_entry_count;
            *dropped_property_entry_count = info.dropped_property_entry_count;
            *type_name_count = info.type_name_count;
            *dropped_type_name_count = info.dropped_type_name_count;
            *projection_flags = info.flags;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_copy_stream_record_value(
    handle: u64,
    stream: u32,
    record_index: u32,
    value_slot: u32,
    kind: *mut u32,
    buffer: *mut u8,
    buffer_len: usize,
    required_len: *mut usize,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if kind.is_null() || required_len.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation stream value output pointer is null".to_owned(),
            ));
        }
        let stream = i32::try_from(stream).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream exceeds the managed ABI limit".to_owned(),
            )
        })?;
        let record_index = i32::try_from(record_index).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream record index exceeds the managed ABI limit".to_owned(),
            )
        })?;
        let value_slot = i32::try_from(value_slot).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation stream value slot exceeds the managed ABI limit".to_owned(),
            )
        })?;
        with_result(handle, |snapshot| {
            let value = snapshot
                .stream_record_value(stream, record_index, value_slot)
                .map_err(managed_failure)?;
            *kind = value.kind;
            let status = write_bytes(buffer, buffer_len, required_len, &value.payload);
            match status {
                Status::Success | Status::BufferTooSmall => Ok(status),
                Status::InvalidArgument => Err((
                    Status::InvalidArgument,
                    "invocation stream value buffer arguments are invalid".to_owned(),
                )),
                _ => unreachable!(),
            }
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_result_get_sequence_record(
    handle: u64,
    sequence_index: u32,
    stream: *mut u32,
    record_index: *mut u32,
    sequence: *mut u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if stream.is_null() || record_index.is_null() || sequence.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation sequence record output pointer is null".to_owned(),
            ));
        }

        let sequence_index = i32::try_from(sequence_index).map_err(|_| {
            (
                Status::InvalidArgument,
                "invocation sequence index exceeds the managed ABI limit".to_owned(),
            )
        })?;
        with_result(handle, |snapshot| {
            let (record_stream, record_index_value, record_sequence) =
                snapshot.sequence_record(sequence_index).map_err(managed_failure)?;
            *stream = u32::try_from(record_stream).map_err(|_| {
                (
                    Status::ManagedFailure,
                    "managed invocation sequence stream is invalid".to_owned(),
                )
            })?;
            *record_index = u32::try_from(record_index_value).map_err(|_| {
                (
                    Status::ManagedFailure,
                    "managed invocation sequence record index is invalid".to_owned(),
                )
            })?;
            *sequence = u64::try_from(record_sequence).map_err(|_| {
                (
                    Status::ManagedFailure,
                    "managed invocation sequence is invalid".to_owned(),
                )
            })?;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_invoke_async(
    handle: u64,
    operation_handle: *mut u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if operation_handle.is_null() {
            return Err((
                Status::InvalidArgument,
                "PowerShell operation handle output pointer is null".to_owned(),
            ));
        }
        *operation_handle = start_operation(handle)?;
        Ok(Status::Success)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_operation_release(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || release_operation(handle))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_operation_stop(handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || stop_operation(handle))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_operation_poll(
    handle: u64,
    operation_state: *mut u32,
    terminal_status: *mut i32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || poll_operation(handle, operation_state, terminal_status))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_operation_wait(
    handle: u64,
    timeout_milliseconds: u32,
    operation_state: *mut u32,
    terminal_status: *mut i32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        wait_operation(handle, timeout_milliseconds, operation_state, terminal_status)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_operation_get_result(
    handle: u64,
    result_handle: *mut u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if result_handle.is_null() {
            return Err((
                Status::InvalidArgument,
                "invocation result handle output pointer is null".to_owned(),
            ));
        }
        *result_handle = operation_result(handle)?;
        Ok(Status::Success)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_create(
    options: *const SessionOptions,
    session_handle: *mut u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if session_handle.is_null() {
            return Err((
                Status::InvalidArgument,
                "PowerShell session handle output pointer is null".to_owned(),
            ));
        }
        *session_handle = create_runspace_session(session_options_input(options)?)?;
        Ok(Status::Success)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_release(session_handle: u64, result: *mut CallResult) -> i32 {
    v2_call(result, || release_runspace_session(session_handle))
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_create_builder(
    session_handle: u64,
    builder_handle: *mut u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if builder_handle.is_null() {
            return Err((
                Status::InvalidArgument,
                "PowerShell session builder handle output pointer is null".to_owned(),
            ));
        }
        *builder_handle = create_session_builder(session_handle)?;
        Ok(Status::Success)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_get_snapshot(
    session_handle: u64,
    snapshot: *mut SessionSnapshot,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        with_runspace_session(session_handle, |session| {
            let snapshot_value = session.snapshot().map_err(managed_failure)?;
            write_session_snapshot(snapshot, snapshot_value)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_get_event_info(
    session_handle: u64,
    event_index: u32,
    sequence: *mut u64,
    event_state: *mut u32,
    flags: *mut u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if sequence.is_null() || event_state.is_null() || flags.is_null() {
            return Err((
                Status::InvalidArgument,
                "PowerShell session event output pointer is null".to_owned(),
            ));
        }
        with_runspace_session(session_handle, |session| {
            let FfiSessionEvent {
                sequence: event_sequence,
                state,
                flags: event_flags,
            } = session.event(event_index).map_err(managed_failure)?;
            *sequence = event_sequence;
            *event_state = state;
            *flags = event_flags;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_set_variable(
    session_handle: u64,
    name: Utf8Span,
    value: *const DataValue,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        let name = utf8_span(name).map_err(|_| {
            (
                Status::InvalidArgument,
                "PowerShell session variable name must be UTF-8 without NUL".to_owned(),
            )
        })?;
        if !valid_session_name(name) {
            return Err((
                Status::InvalidArgument,
                "PowerShell session variable name must be a bounded ASCII identifier".to_owned(),
            ));
        }
        let (kind, payload) = data_value_input(value)?;
        with_runspace_session_mutation(session_handle, |session| {
            session.set_variable(name, kind, payload).map_err(managed_failure)?;
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_remove_variable(
    session_handle: u64,
    name: Utf8Span,
    removed: *mut u32,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if removed.is_null() {
            return Err((
                Status::InvalidArgument,
                "PowerShell session variable removal output pointer is null".to_owned(),
            ));
        }
        let name = utf8_span(name).map_err(|_| {
            (
                Status::InvalidArgument,
                "PowerShell session variable name must be UTF-8 without NUL".to_owned(),
            )
        })?;
        if !valid_session_name(name) {
            return Err((
                Status::InvalidArgument,
                "PowerShell session variable name must be a bounded ASCII identifier".to_owned(),
            ));
        }
        with_runspace_session_mutation(session_handle, |session| {
            *removed = u32::from(session.remove_variable(name).map_err(managed_failure)?);
            Ok(Status::Success)
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_get_variable_snapshot(
    session_handle: u64,
    name: Utf8Span,
    found: *mut u32,
    kind: *mut u32,
    buffer: *mut u8,
    buffer_len: usize,
    required_len: *mut usize,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if found.is_null() || kind.is_null() || required_len.is_null() {
            return Err((
                Status::InvalidArgument,
                "PowerShell session variable snapshot output pointer is null".to_owned(),
            ));
        }
        let name = utf8_span(name).map_err(|_| {
            (
                Status::InvalidArgument,
                "PowerShell session variable name must be UTF-8 without NUL".to_owned(),
            )
        })?;
        if !valid_session_name(name) {
            return Err((
                Status::InvalidArgument,
                "PowerShell session variable name must be a bounded ASCII identifier".to_owned(),
            ));
        }
        *found = 0;
        *kind = 0;
        *required_len = 0;
        with_runspace_session_mutation(session_handle, |session| {
            let Some(FfiSnapshotValue {
                kind: value_kind,
                payload,
            }) = session.variable_snapshot(name).map_err(managed_failure)?
            else {
                return Ok(Status::Success);
            };
            *found = 1;
            *kind = value_kind;
            Ok(write_bytes(buffer, buffer_len, required_len, &payload))
        })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_v2_session_pool_create(
    options: *const SessionPoolOptions,
    pool_handle: *mut u64,
    result: *mut CallResult,
) -> i32 {
    v2_call(result, || {
        if pool_handle.is_null() {
            return Err((
                Status::InvalidArgument,
                "PowerShell session pool handle output pointer is null".to_owned(),
            ));
        }
        validate_pool_options(options)?;
        *pool_handle = 0;
        Err((
            Status::UnsupportedCapability,
            "PowerShell session pools are intentionally unsupported: a shared in-process runspace cannot safely provide concurrent pool semantics."
                .to_owned(),
        ))
    })
}

#[no_mangle]
#[allow(clippy::arc_with_non_send_sync)]
pub unsafe extern "C" fn dps_pwsh_initialize_utf8(payload_path: *const u8, payload_path_len: usize) -> i32 {
    let payload_path = match utf8_input(payload_path, payload_path_len) {
        Ok(value) => value,
        Err(_) => {
            return execute(|state| fail(state, Status::InvalidArgument, "payload path must be UTF-8 without NUL"))
        }
    };
    let initialization = catch_unwind(AssertUnwindSafe(|| initialize_unsafe_local_development(payload_path)));
    execute(|state| match initialization {
        Ok(Ok(status)) => {
            clear_error(state);
            status.value()
        }
        Ok(Err((status, message))) => fail(state, status, message),
        Err(_) => fail(state, Status::Panic, "native PowerShell FFI initialization panicked"),
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_last_error_utf8(buffer: *mut u8, buffer_len: usize, required_len: *mut usize) -> i32 {
    execute(|state| write_utf8(buffer, buffer_len, required_len, &state.last_error).value())
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_create(handle: *mut u64) -> i32 {
    execute(|state| {
        if handle.is_null() {
            return fail(state, Status::InvalidArgument, "handle output pointer is null");
        }

        let runtime = match &state.runtime {
            Some(runtime) => Arc::clone(runtime),
            None => return fail(state, Status::NotInitialized, "PowerShell runtime is not initialized"),
        };
        let power_shell = match FfiPowerShell::new_for_runtime(runtime) {
            Ok(power_shell) => power_shell,
            Err(error) => return fail(state, Status::HostFailure, error.to_string()),
        };
        let next_handle = state.next_handle;
        state.next_handle = state.next_handle.checked_add(1).unwrap_or(1);
        state.sessions.insert(
            next_handle,
            Arc::new(Session {
                power_shell,
                operation_active: Mutex::new(false),
                runspace_session: None,
                capability_registration: Mutex::new(None),
                active_capability: Mutex::new(None),
            }),
        );
        *handle = next_handle;
        clear_error(state);
        Status::Success.value()
    })
}

#[no_mangle]
pub extern "C" fn dps_pwsh_release(handle: u64) -> i32 {
    execute(|state| {
        if state.sessions.remove(&handle).is_none() {
            return fail(state, Status::InvalidHandle, "PowerShell handle is invalid");
        }

        clear_error(state);
        Status::Success.value()
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_add_command_utf8(handle: u64, command: *const u8, command_len: usize) -> i32 {
    let command = match utf8_input(command, command_len) {
        Ok(value) => value,
        Err(_) => return execute(|state| fail(state, Status::InvalidArgument, "command must be UTF-8 without NUL")),
    };
    with_session(handle, true, |session| {
        session
            .add_command(command)
            .map(|_| Status::Success)
            .map_err(managed_failure)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_add_script_utf8(handle: u64, script: *const u8, script_len: usize) -> i32 {
    let script = match utf8_input(script, script_len) {
        Ok(value) => value,
        Err(_) => return execute(|state| fail(state, Status::InvalidArgument, "script must be UTF-8 without NUL")),
    };
    with_session(handle, true, |session| {
        session
            .add_script(script)
            .map(|_| Status::Success)
            .map_err(managed_failure)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_add_argument_utf8(handle: u64, argument: *const u8, argument_len: usize) -> i32 {
    let argument = match utf8_input(argument, argument_len) {
        Ok(value) => value,
        Err(_) => return execute(|state| fail(state, Status::InvalidArgument, "argument must be UTF-8 without NUL")),
    };
    with_session(handle, true, |session| {
        session
            .add_argument_string(argument)
            .map(|_| Status::Success)
            .map_err(managed_failure)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_add_parameter_string_utf8(
    handle: u64,
    name: *const u8,
    name_len: usize,
    value: *const u8,
    value_len: usize,
) -> i32 {
    let name = match utf8_input(name, name_len) {
        Ok(value) => value,
        Err(_) => {
            return execute(|state| {
                fail(
                    state,
                    Status::InvalidArgument,
                    "parameter name must be UTF-8 without NUL",
                )
            })
        }
    };
    let value = match utf8_input(value, value_len) {
        Ok(value) => value,
        Err(_) => {
            return execute(|state| {
                fail(
                    state,
                    Status::InvalidArgument,
                    "parameter value must be UTF-8 without NUL",
                )
            })
        }
    };
    with_session(handle, true, |session| {
        session
            .add_parameter_string(name, value)
            .map(|_| Status::Success)
            .map_err(managed_failure)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_add_parameter_i64(handle: u64, name: *const u8, name_len: usize, value: i64) -> i32 {
    let name = match utf8_input(name, name_len) {
        Ok(value) => value,
        Err(_) => {
            return execute(|state| {
                fail(
                    state,
                    Status::InvalidArgument,
                    "parameter name must be UTF-8 without NUL",
                )
            })
        }
    };
    with_session(handle, true, |session| {
        session
            .add_parameter_long(name, value)
            .map(|_| Status::Success)
            .map_err(managed_failure)
    })
}

#[no_mangle]
pub extern "C" fn dps_pwsh_add_statement(handle: u64) -> i32 {
    with_session(handle, true, |session| {
        session
            .add_statement()
            .map(|_| Status::Success)
            .map_err(managed_failure)
    })
}

#[no_mangle]
pub extern "C" fn dps_pwsh_clear(handle: u64) -> i32 {
    with_session(handle, true, |session| {
        session.clear().map(|_| Status::Success).map_err(managed_failure)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_invoke_utf8(
    handle: u64,
    buffer: *mut u8,
    buffer_len: usize,
    required_len: *mut usize,
) -> i32 {
    with_session(handle, true, |session| {
        let output = match session.invoke_to_string() {
            Ok(output) => output,
            Err(error) => return Err(managed_failure(error)),
        };
        Ok(unsafe { write_utf8(buffer, buffer_len, required_len, &output) })
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_get_invocation_error_count(handle: u64, error_count: *mut u32) -> i32 {
    if error_count.is_null() {
        return execute(|state| fail(state, Status::InvalidArgument, "error count output pointer is null"));
    }

    with_session(handle, true, |session| {
        let count = session.invocation_error_count().map_err(managed_failure)?;
        let count = u32::try_from(count).map_err(|_| {
            (
                Status::ManagedFailure,
                "managed error count exceeds the ABI limit".to_owned(),
            )
        })?;
        *error_count = count;
        Ok(Status::Success)
    })
}

#[no_mangle]
pub unsafe extern "C" fn dps_pwsh_copy_invocation_error_field_utf8(
    handle: u64,
    error_index: u32,
    field: u32,
    buffer: *mut u8,
    buffer_len: usize,
    required_len: *mut usize,
) -> i32 {
    let error_index = match i32::try_from(error_index) {
        Ok(value) => value,
        Err(_) => {
            return execute(|state| {
                fail(
                    state,
                    Status::InvalidArgument,
                    "error index exceeds the managed ABI limit",
                )
            })
        }
    };
    let field = match i32::try_from(field) {
        Ok(value) => value,
        Err(_) => {
            return execute(|state| {
                fail(
                    state,
                    Status::InvalidArgument,
                    "error field exceeds the managed ABI limit",
                )
            })
        }
    };

    with_session(handle, true, |session| {
        let value = session
            .invocation_error_field(error_index, field)
            .map_err(managed_failure)?;
        Ok(write_utf8(buffer, buffer_len, required_len, &value))
    })
}

#[no_mangle]
pub extern "C" fn dps_pwsh_stop(handle: u64) -> i32 {
    with_session(handle, false, |session| {
        session.stop().map(|_| Status::Success).map_err(managed_failure)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    static EMPTY_VALUE_CONTAINER: [u8; 4] = [0; 4];

    fn empty_data_value(kind: u32) -> DataValue {
        DataValue {
            size: mem::size_of::<DataValue>() as u32,
            kind,
            flags: 0,
            _reserved: 0,
            data: EMPTY_VALUE_CONTAINER.as_ptr(),
            data_len: EMPTY_VALUE_CONTAINER.len(),
        }
    }

    fn empty_session_options() -> SessionOptions {
        SessionOptions {
            size: mem::size_of::<SessionOptions>() as u32,
            runspace_mode: 1,
            initial_configuration: 0,
            history_mode: 0,
            error_preference: 0,
            warning_preference: 0,
            verbose_preference: 0,
            debug_preference: 0,
            information_preference: 0,
            flags: 0,
            _reserved: 0,
            allowed_module_path: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
            execution_policy: 0,
            configuration_flags: 0,
            initial_variables: empty_data_value(VALUE_KIND_PROPERTY_BAG),
            module_imports: empty_data_value(VALUE_KIND_ARRAY),
            allowed_module_paths: empty_data_value(VALUE_KIND_ARRAY),
            working_directory: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
            environment: empty_data_value(VALUE_KIND_PROPERTY_BAG),
        }
    }

    fn append_u32(output: &mut Vec<u8>, value: u32) {
        output.extend_from_slice(&value.to_le_bytes());
    }

    fn encode_nested(output: &mut Vec<u8>, kind: u32, value: &[u8]) {
        append_u32(output, kind);
        append_u32(output, value.len() as u32);
        output.extend_from_slice(value);
    }

    fn encode_array(values: Vec<(u32, Vec<u8>)>) -> Vec<u8> {
        let mut output = Vec::new();
        append_u32(&mut output, values.len() as u32);
        for (kind, value) in values {
            encode_nested(&mut output, kind, &value);
        }
        output
    }

    fn encode_property_bag(values: Vec<(&str, u32, Vec<u8>)>) -> Vec<u8> {
        let mut output = Vec::new();
        append_u32(&mut output, values.len() as u32);
        for (name, kind, value) in values {
            append_u32(&mut output, name.len() as u32);
            output.extend_from_slice(name.as_bytes());
            encode_nested(&mut output, kind, &value);
        }
        output
    }

    fn encode_unsigned(value: u64) -> Vec<u8> {
        value.to_le_bytes().to_vec()
    }

    fn encode_capability_definition(name: &str) -> Vec<u8> {
        encode_property_bag(vec![
            ("name", VALUE_KIND_STRING, name.as_bytes().to_vec()),
            ("permissions", VALUE_KIND_UNSIGNED_INTEGER, encode_unsigned(1)),
            ("maximumInputBytes", VALUE_KIND_UNSIGNED_INTEGER, encode_unsigned(64)),
            ("maximumOutputBytes", VALUE_KIND_UNSIGNED_INTEGER, encode_unsigned(64)),
            (
                "deadlineMilliseconds",
                VALUE_KIND_UNSIGNED_INTEGER,
                encode_unsigned(100),
            ),
            ("arguments", VALUE_KIND_ARRAY, encode_array(vec![])),
            (
                "responseKinds",
                VALUE_KIND_ARRAY,
                encode_array(vec![(
                    VALUE_KIND_UNSIGNED_INTEGER,
                    encode_unsigned(VALUE_KIND_STRING as u64),
                )]),
            ),
        ])
    }

    fn encode_capability_registration(names: &[&str]) -> Vec<u8> {
        encode_property_bag(vec![
            (
                "protocol",
                VALUE_KIND_UNSIGNED_INTEGER,
                encode_unsigned(CAPABILITY_REGISTRATION_VERSION as u64),
            ),
            (
                "capabilities",
                VALUE_KIND_ARRAY,
                encode_array(
                    names
                        .iter()
                        .map(|name| (VALUE_KIND_PROPERTY_BAG, encode_capability_definition(name)))
                        .collect(),
                ),
            ),
        ])
    }

    #[test]
    fn write_utf8_reports_required_length_before_copying() {
        let mut required = 0;
        let status = unsafe { write_utf8(std::ptr::null_mut(), 0, &mut required, "héllo") };
        assert!(matches!(status, Status::BufferTooSmall));
        assert_eq!(required, "héllo".len());
    }

    #[test]
    fn write_utf8_copies_complete_utf8_values() {
        let mut output = [0_u8; 8];
        let mut required = 0;
        let status = unsafe { write_utf8(output.as_mut_ptr(), output.len(), &mut required, "héllo") };
        assert!(matches!(status, Status::Success));
        assert_eq!(&output[..required], "héllo".as_bytes());
    }

    #[test]
    fn capability_schema_parser_rejects_noncanonical_and_duplicate_definitions() {
        let valid = encode_capability_registration(&["rdm.get-connection-name"]);
        let definitions = parse_capability_definitions(VALUE_KIND_PROPERTY_BAG, &valid).unwrap();
        assert_eq!(definitions.len(), 1);
        assert!(definitions.contains_key("rdm.get-connection-name"));

        let duplicate = encode_capability_registration(&["rdm.get-connection-name", "rdm.get-connection-name"]);
        assert!(matches!(
            parse_capability_definitions(VALUE_KIND_PROPERTY_BAG, &duplicate),
            Err((Status::InvalidArgument, _))
        ));

        let noncanonical = encode_capability_registration(&["RDM.get-connection-name"]);
        assert!(matches!(
            parse_capability_definitions(VALUE_KIND_PROPERTY_BAG, &noncanonical),
            Err((Status::InvalidArgument, _))
        ));
        assert!(matches!(
            parse_capability_definitions(VALUE_KIND_STRING, b"not-a-registration"),
            Err((Status::InvalidArgument, _))
        ));
    }

    #[test]
    fn callback_scope_rejects_reentrant_ffi_calls_before_runtime_access() {
        let _callback_scope = CapabilityCallbackScope::enter();
        let mut diagnostic = [0_u8; 64];
        let mut result = CallResult {
            size: mem::size_of::<CallResult>() as u32,
            status: 0,
            flags: 0,
            _reserved: 0,
            diagnostic: diagnostic.as_mut_ptr(),
            diagnostic_capacity: diagnostic.len(),
            diagnostic_required: 0,
            diagnostic_written: 0,
        };
        let mut handle = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_create(&mut handle, &mut result) },
            Status::Backpressure.value()
        );
        assert_eq!(result.status, Status::Backpressure.value());
        assert_eq!(handle, 0);
    }

    #[test]
    fn abi_helpers_and_sized_calls_enforce_the_contract_without_a_payload() {
        const REQUIRED_FEATURES: u64 = FEATURE_STRUCTURED_INVOCATION_ERRORS
            | FEATURE_PER_CALL_DIAGNOSTICS
            | FEATURE_UTF8_SPANS
            | FEATURE_IMMUTABLE_RESULTS
            | FEATURE_TAGGED_VALUES
            | FEATURE_COMMAND_OPTIONS
            | FEATURE_BOUNDED_INPUT
            | FEATURE_INVOCATION_METADATA
            | FEATURE_ASYNC_OPERATIONS
            | FEATURE_PAYLOAD_MANIFEST
            | FEATURE_SESSIONS
            | FEATURE_SESSION_POLLING
            | FEATURE_SESSION_POOL_REJECTION
            | FEATURE_SNAPSHOT_PROJECTIONS
            | FEATURE_SESSION_CONFIGURATION
            | FEATURE_SESSION_VARIABLES
            | FEATURE_CAPABILITY_RPC;

        assert_eq!(dps_pwsh_abi_version(), ABI_VERSION);
        assert_eq!(dps_pwsh_feature_flags(), REQUIRED_FEATURES);

        let mut abi_info = AbiInfo {
            size: mem::size_of::<AbiInfo>() as u32,
            abi_version: 0,
            feature_flags: 0,
            minimum_compatible_abi_version: 0,
            _reserved: u32::MAX,
        };
        assert_eq!(unsafe { dps_pwsh_get_abi_info(&mut abi_info) }, Status::Success.value());
        assert_eq!(abi_info.abi_version, ABI_VERSION);
        assert_eq!(abi_info.minimum_compatible_abi_version, ABI_VERSION);
        assert_eq!(abi_info.feature_flags, REQUIRED_FEATURES);
        assert_eq!(abi_info._reserved, 0);

        let mut undersized_abi_info = AbiInfo {
            size: (mem::size_of::<AbiInfo>() - 1) as u32,
            abi_version: 0,
            feature_flags: 0,
            minimum_compatible_abi_version: 0,
            _reserved: 0,
        };
        assert_eq!(
            unsafe { dps_pwsh_get_abi_info(&mut undersized_abi_info) },
            Status::InvalidArgument.value()
        );

        let mut diagnostic = [0_u8; 128];
        let mut call_result = CallResult {
            size: mem::size_of::<CallResult>() as u32,
            status: 0,
            flags: 0,
            _reserved: 0,
            diagnostic: diagnostic.as_mut_ptr(),
            diagnostic_capacity: diagnostic.len(),
            diagnostic_required: 0,
            diagnostic_written: 0,
        };
        let invalid_activation = PayloadActivation {
            size: (mem::size_of::<PayloadActivation>() - 1) as u32,
            trust_policy: 0,
            flags: 0,
            _reserved: 0,
            payload_path: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
            manifest_path: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
            manifest_sha256: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_initialize_payload(&invalid_activation, &mut call_result) },
            Status::InvalidArgument.value()
        );
        assert_eq!(call_result.status, Status::InvalidArgument.value());
        assert_ne!(call_result.diagnostic_written, 0);

        let mut undersized_call_result = CallResult {
            size: (mem::size_of::<CallResult>() - 1) as u32,
            status: 0,
            flags: 0,
            _reserved: 0,
            diagnostic: std::ptr::null_mut(),
            diagnostic_capacity: 0,
            diagnostic_required: 0,
            diagnostic_written: 0,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_create(std::ptr::null_mut(), &mut undersized_call_result) },
            Status::InvalidArgument.value()
        );
    }

    #[test]
    fn v2_negative_matrix_rejects_defined_malformed_inputs_without_a_payload() {
        let mut diagnostic = [0_u8; 8];
        let mut call_result = v2_call_result(&mut diagnostic);

        assert_eq!(
            unsafe { dps_pwsh_v2_release(u64::MAX, &mut call_result) },
            Status::InvalidHandle.value()
        );
        assert_eq!(call_result.status, Status::InvalidHandle.value());
        assert_eq!(call_result.diagnostic_written, diagnostic.len());
        assert!(call_result.diagnostic_required > call_result.diagnostic_written);
        assert_ne!(call_result.flags & CALL_RESULT_DIAGNOSTIC_TRUNCATED, 0);

        let mut no_diagnostic_storage = CallResult {
            size: mem::size_of::<CallResult>() as u32,
            status: 0,
            flags: 0,
            _reserved: 0,
            diagnostic: std::ptr::null_mut(),
            diagnostic_capacity: 1,
            diagnostic_required: 0,
            diagnostic_written: 0,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_release(u64::MAX, &mut no_diagnostic_storage) },
            Status::InvalidArgument.value()
        );

        let mut undersized_call_result = v2_call_result(&mut diagnostic);
        undersized_call_result.size = (mem::size_of::<CallResult>() - 1) as u32;
        assert_eq!(
            unsafe { dps_pwsh_v2_release(u64::MAX, &mut undersized_call_result) },
            Status::InvalidArgument.value()
        );

        let mut activation = PayloadActivation {
            size: mem::size_of::<PayloadActivation>() as u32,
            trust_policy: 0,
            flags: 0,
            _reserved: 0,
            payload_path: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
            manifest_path: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
            manifest_sha256: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
        };
        activation.size = (mem::size_of::<PayloadActivation>() - 1) as u32;
        assert_eq!(
            unsafe { dps_pwsh_v2_initialize_payload(&activation, &mut call_result) },
            Status::InvalidArgument.value()
        );
        activation.size = mem::size_of::<PayloadActivation>() as u32;
        activation.flags = 1;
        assert_eq!(
            unsafe { dps_pwsh_v2_initialize_payload(&activation, &mut call_result) },
            Status::InvalidArgument.value()
        );
        activation.flags = 0;
        activation.payload_path = Utf8Span {
            data: std::ptr::null(),
            len: 1,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_initialize_payload(&activation, &mut call_result) },
            Status::InvalidArgument.value()
        );
        let invalid_utf8 = [0xff_u8];
        activation.payload_path = Utf8Span {
            data: invalid_utf8.as_ptr(),
            len: invalid_utf8.len(),
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_initialize_payload(&activation, &mut call_result) },
            Status::InvalidArgument.value()
        );
        let nul_utf8 = [b'x', 0];
        activation.payload_path = Utf8Span {
            data: nul_utf8.as_ptr(),
            len: nul_utf8.len(),
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_initialize_payload(&activation, &mut call_result) },
            Status::InvalidArgument.value()
        );

        let empty = Utf8Span {
            data: std::ptr::null(),
            len: 0,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_add_script_utf8(u64::MAX, empty, &mut call_result) },
            Status::InvalidHandle.value()
        );
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    u64::MAX,
                    Utf8Span {
                        data: std::ptr::null(),
                        len: 1,
                    },
                    &mut call_result,
                )
            },
            Status::InvalidArgument.value()
        );
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    u64::MAX,
                    Utf8Span {
                        data: invalid_utf8.as_ptr(),
                        len: invalid_utf8.len(),
                    },
                    &mut call_result,
                )
            },
            Status::InvalidArgument.value()
        );
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    u64::MAX,
                    Utf8Span {
                        data: nul_utf8.as_ptr(),
                        len: nul_utf8.len(),
                    },
                    &mut call_result,
                )
            },
            Status::InvalidArgument.value()
        );

        assert_eq!(
            unsafe { dps_pwsh_v2_add_argument_value(u64::MAX, std::ptr::null(), &mut call_result) },
            Status::InvalidArgument.value()
        );
        let mut data_value = DataValue {
            size: (mem::size_of::<DataValue>() - 1) as u32,
            kind: 1,
            flags: 0,
            _reserved: 0,
            data: invalid_utf8.as_ptr(),
            data_len: invalid_utf8.len(),
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_add_argument_value(u64::MAX, &data_value, &mut call_result) },
            Status::InvalidArgument.value()
        );
        data_value.size = mem::size_of::<DataValue>() as u32;
        data_value.flags = 1;
        assert_eq!(
            unsafe { dps_pwsh_v2_add_argument_value(u64::MAX, &data_value, &mut call_result) },
            Status::InvalidArgument.value()
        );
        data_value.flags = 0;
        data_value.data = std::ptr::null();
        data_value.data_len = 1;
        assert_eq!(
            unsafe { dps_pwsh_v2_add_argument_value(u64::MAX, &data_value, &mut call_result) },
            Status::InvalidArgument.value()
        );
        data_value.data = invalid_utf8.as_ptr();
        data_value.data_len = invalid_utf8.len();
        assert_eq!(
            unsafe { dps_pwsh_v2_add_argument_value(u64::MAX, &data_value, &mut call_result) },
            Status::InvalidArgument.value()
        );
        data_value.data = nul_utf8.as_ptr();
        data_value.data_len = nul_utf8.len();
        assert_eq!(
            unsafe { dps_pwsh_v2_add_argument_value(u64::MAX, &data_value, &mut call_result) },
            Status::InvalidArgument.value()
        );

        let mut session_options = SessionOptions {
            size: (mem::size_of::<SessionOptions>() - 1) as u32,
            allowed_module_path: empty,
            ..empty_session_options()
        };
        let mut session_handle = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create(&session_options, &mut session_handle, &mut call_result) },
            Status::InvalidArgument.value()
        );
        session_options.size = mem::size_of::<SessionOptions>() as u32;
        session_options._reserved = 1;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create(&session_options, &mut session_handle, &mut call_result) },
            Status::InvalidArgument.value()
        );
        session_options._reserved = 0;
        session_options.allowed_module_path = Utf8Span {
            data: std::ptr::null(),
            len: 1,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create(&session_options, &mut session_handle, &mut call_result) },
            Status::InvalidArgument.value()
        );

        let mut pool_options = SessionPoolOptions {
            size: (mem::size_of::<SessionPoolOptions>() - 1) as u32,
            minimum_sessions: 0,
            maximum_sessions: 1,
            flags: 0,
            _reserved: 0,
        };
        let mut pool_handle = u64::MAX;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_pool_create(&pool_options, &mut pool_handle, &mut call_result) },
            Status::InvalidArgument.value()
        );
        pool_options.size = mem::size_of::<SessionPoolOptions>() as u32;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_pool_create(&pool_options, &mut pool_handle, &mut call_result) },
            Status::UnsupportedCapability.value()
        );
        assert_eq!(pool_handle, 0);

        let mut required = 0;
        assert!(matches!(
            unsafe { write_utf8(std::ptr::null_mut(), 0, &mut required, "é") },
            Status::BufferTooSmall
        ));
        assert_eq!(required, "é".len());
        assert!(matches!(
            unsafe { write_utf8(std::ptr::null_mut(), required, &mut required, "é") },
            Status::InvalidArgument
        ));
    }

    #[test]
    #[ignore = "requires PWSH_FFI_PAYLOAD to be an explicit PowerShell payload directory"]
    fn untrusted_local_development_requires_explicit_opt_in() {
        let payload = std::env::var("PWSH_FFI_PAYLOAD")
            .expect("PWSH_FFI_PAYLOAD must name an explicit PowerShell payload directory");
        let (manifest_path, _) = payload::create_test_manifest(&PathBuf::from(&payload));
        let manifest_path = manifest_path.to_str().unwrap();

        assert!(payload::validate(ValidationRequest {
            payload_path: &payload,
            manifest_path,
            manifest_sha256: "",
            trust_policy: TrustPolicy::AllowUntrustedLocalDevelopment,
        })
        .is_ok());
        assert!(matches!(
            payload::validate(ValidationRequest {
                payload_path: &payload,
                manifest_path,
                manifest_sha256: "",
                trust_policy: TrustPolicy::RequireHashPinnedManifest,
            }),
            Err(ValidationError::Untrusted(_))
        ));
    }

    #[test]
    #[ignore = "requires PWSH_FFI_PAYLOAD to be an explicit PowerShell payload directory"]
    fn explicit_payload_round_trip_uses_the_exported_abi() {
        let payload = std::env::var("PWSH_FFI_PAYLOAD")
            .expect("PWSH_FFI_PAYLOAD must name an explicit PowerShell payload directory");
        let mut abi_info = AbiInfo {
            size: std::mem::size_of::<AbiInfo>() as u32,
            abi_version: 0,
            feature_flags: 0,
            minimum_compatible_abi_version: 0,
            _reserved: 0,
        };
        assert_eq!(unsafe { dps_pwsh_get_abi_info(&mut abi_info) }, Status::Success.value());
        assert_eq!(abi_info.abi_version, ABI_VERSION);
        assert_eq!(abi_info.minimum_compatible_abi_version, ABI_VERSION);
        assert_ne!(abi_info.feature_flags & FEATURE_PER_CALL_DIAGNOSTICS, 0);
        assert_ne!(abi_info.feature_flags & FEATURE_UTF8_SPANS, 0);
        assert_ne!(abi_info.feature_flags & FEATURE_PAYLOAD_MANIFEST, 0);

        let payload_span = Utf8Span {
            data: payload.as_ptr(),
            len: payload.len(),
        };
        let (manifest_path, manifest_sha256) = payload::create_test_manifest(&PathBuf::from(&payload));
        let manifest_path = manifest_path.to_str().unwrap();
        let mut diagnostic = [0_u8; 64];
        let mut call_result = CallResult {
            size: std::mem::size_of::<CallResult>() as u32,
            status: 0,
            flags: 0,
            _reserved: 0,
            diagnostic: diagnostic.as_mut_ptr(),
            diagnostic_capacity: diagnostic.len(),
            diagnostic_required: 0,
            diagnostic_written: 0,
        };
        assert_eq!(
            unsafe {
                dps_pwsh_v2_initialize_payload(
                    &PayloadActivation {
                        size: std::mem::size_of::<PayloadActivation>() as u32,
                        trust_policy: 0,
                        flags: 0,
                        _reserved: 0,
                        payload_path: payload_span,
                        manifest_path: Utf8Span {
                            data: manifest_path.as_ptr(),
                            len: manifest_path.len(),
                        },
                        manifest_sha256: Utf8Span {
                            data: manifest_sha256.as_ptr(),
                            len: manifest_sha256.len(),
                        },
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(call_result.status, Status::Success.value());

        let mut v2_handle = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_create(&mut v2_handle, &mut call_result) },
            Status::Success.value()
        );
        let empty = Utf8Span {
            data: std::ptr::null(),
            len: 0,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_add_script_utf8(v2_handle, empty, &mut call_result) },
            Status::Success.value()
        );
        let mut required = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_utf8(v2_handle, std::ptr::null_mut(), 0, &mut required, &mut call_result,) },
            Status::Success.value()
        );
        assert_eq!(required, 0);
        assert_eq!(
            unsafe { dps_pwsh_v2_release(v2_handle, &mut call_result) },
            Status::Success.value()
        );

        let mut immutable_builder = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_create(&mut immutable_builder, &mut call_result) },
            Status::Success.value()
        );
        let immutable_script = r#"
            Write-Output 'immutable-output'
            Write-Output ([pscustomobject]@{ Name = 'projection'; Count = 2; Nested = @{ Value = 1 }; Items = 1, 2 })
            Write-Error -Message 'immutable-error' -Category InvalidOperation -TargetObject 42
            Write-Warning 'immutable-warning'
            Write-Verbose 'immutable-verbose' -Verbose
            Write-Debug 'immutable-debug' -Debug
            Write-Information 'immutable-information' -InformationAction Continue
            Write-Progress -Activity 'immutable-progress' -Status 'running' -PercentComplete 50
        "#;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    immutable_builder,
                    Utf8Span {
                        data: immutable_script.as_ptr(),
                        len: immutable_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let mut immutable_result = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke(immutable_builder, &mut immutable_result, &mut call_result) },
            Status::Success.value()
        );
        assert_ne!(immutable_result, 0);
        let mut immutable_flags = 0;
        let mut immutable_sequence_count = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_info(
                    immutable_result,
                    &mut immutable_flags,
                    &mut immutable_sequence_count,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(immutable_flags, 0);
        assert!(
            immutable_sequence_count >= 7,
            "expected all stream records, got {}",
            immutable_sequence_count
        );
        for stream in 0..7 {
            let mut count = 0;
            let mut flags = 0;
            assert_eq!(
                unsafe {
                    dps_pwsh_v2_result_get_stream_info(
                        immutable_result,
                        stream,
                        &mut count,
                        &mut flags,
                        &mut call_result,
                    )
                },
                Status::Success.value()
            );
            assert_eq!(
                count,
                if stream == 0 { 2 } else { 1 },
                "stream {} did not retain its record",
                stream
            );
            assert_eq!(flags, 0);
        }
        let mut required = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_field_utf8(
                    immutable_result,
                    0,
                    0,
                    0,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    &mut call_result,
                )
            },
            Status::BufferTooSmall.value()
        );
        let mut immutable_output = vec![0_u8; required];
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_field_utf8(
                    immutable_result,
                    0,
                    0,
                    0,
                    immutable_output.as_mut_ptr(),
                    immutable_output.len(),
                    &mut required,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(String::from_utf8(immutable_output).unwrap(), "immutable-output");
        let mut output_total = 0;
        let mut output_dropped = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_totals(
                    immutable_result,
                    0,
                    &mut output_total,
                    &mut output_dropped,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!((output_total, output_dropped), (2, 0));

        let mut scalar_property_entries = 0;
        let mut scalar_dropped_properties = 0;
        let mut scalar_type_names = 0;
        let mut scalar_dropped_type_names = 0;
        let mut scalar_projection_flags = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_record_projection_info(
                    immutable_result,
                    0,
                    0,
                    &mut scalar_property_entries,
                    &mut scalar_dropped_properties,
                    &mut scalar_type_names,
                    &mut scalar_dropped_type_names,
                    &mut scalar_projection_flags,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_ne!(scalar_projection_flags & RESULT_RECORD_SCALAR_VALUE_PRESENT, 0);
        let mut scalar_kind = 0;
        required = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_value(
                    immutable_result,
                    0,
                    0,
                    0,
                    &mut scalar_kind,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    &mut call_result,
                )
            },
            Status::BufferTooSmall.value()
        );
        assert_eq!(scalar_kind, 1);
        let mut scalar_payload = vec![0_u8; required];
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_value(
                    immutable_result,
                    0,
                    0,
                    0,
                    &mut scalar_kind,
                    scalar_payload.as_mut_ptr(),
                    scalar_payload.len(),
                    &mut required,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(String::from_utf8(scalar_payload).unwrap(), "immutable-output");

        let mut property_entries = 0;
        let mut dropped_properties = 0;
        let mut type_names = 0;
        let mut dropped_type_names = 0;
        let mut property_projection_flags = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_record_projection_info(
                    immutable_result,
                    0,
                    1,
                    &mut property_entries,
                    &mut dropped_properties,
                    &mut type_names,
                    &mut dropped_type_names,
                    &mut property_projection_flags,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!((property_entries, dropped_properties), (2, 2));
        assert_ne!(property_projection_flags & RESULT_RECORD_PROPERTY_BAG_PRESENT, 0);
        assert_ne!(type_names, 0);
        let mut property_bag_kind = 0;
        required = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_value(
                    immutable_result,
                    0,
                    1,
                    1,
                    &mut property_bag_kind,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    &mut call_result,
                )
            },
            Status::BufferTooSmall.value()
        );
        assert_eq!(property_bag_kind, 14);
        assert!(required > 4);

        let mut error_total = 0;
        let mut error_dropped = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_totals(
                    immutable_result,
                    1,
                    &mut error_total,
                    &mut error_dropped,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!((error_total, error_dropped), (1, 0));
        let mut error_projection_flags = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_record_projection_info(
                    immutable_result,
                    1,
                    0,
                    &mut property_entries,
                    &mut dropped_properties,
                    &mut type_names,
                    &mut dropped_type_names,
                    &mut error_projection_flags,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_ne!(error_projection_flags & RESULT_RECORD_ERROR_TARGET_VALUE_PRESENT, 0);
        let mut target_kind = 0;
        required = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_value(
                    immutable_result,
                    1,
                    0,
                    2,
                    &mut target_kind,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    &mut call_result,
                )
            },
            Status::BufferTooSmall.value()
        );
        assert_eq!((target_kind, required), (4, 8));
        assert_eq!(
            unsafe { dps_pwsh_v2_clear(immutable_builder, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(immutable_builder, &mut call_result) },
            Status::Success.value()
        );
        let mut output_count_after_builder_release = 0;
        let mut output_flags_after_builder_release = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_info(
                    immutable_result,
                    0,
                    &mut output_count_after_builder_release,
                    &mut output_flags_after_builder_release,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(output_count_after_builder_release, 2);
        let mut retained_property_bag_kind = 0;
        required = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_value(
                    immutable_result,
                    0,
                    1,
                    1,
                    &mut retained_property_bag_kind,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    &mut call_result,
                )
            },
            Status::BufferTooSmall.value()
        );
        assert_eq!(retained_property_bag_kind, 14);
        assert!(required > 4);
        required = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_field_utf8(
                    immutable_result,
                    0,
                    0,
                    0,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    &mut call_result,
                )
            },
            Status::BufferTooSmall.value()
        );
        let mut immutable_output_after_builder_release = vec![0_u8; required];
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_copy_stream_record_field_utf8(
                    immutable_result,
                    0,
                    0,
                    0,
                    immutable_output_after_builder_release.as_mut_ptr(),
                    immutable_output_after_builder_release.len(),
                    &mut required,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(
            String::from_utf8(immutable_output_after_builder_release).unwrap(),
            "immutable-output"
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(immutable_result, &mut call_result) },
            Status::Success.value()
        );

        let mut bounded_builder = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_create(&mut bounded_builder, &mut call_result) },
            Status::Success.value()
        );
        let bounded_script = "1..40 | ForEach-Object { Write-Output $_; Write-Warning $_ }";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    bounded_builder,
                    Utf8Span {
                        data: bounded_script.as_ptr(),
                        len: bounded_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let mut bounded_result = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke(bounded_builder, &mut bounded_result, &mut call_result) },
            Status::Success.value()
        );
        let mut bounded_warning_count = 0;
        let mut bounded_warning_flags = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_info(
                    bounded_result,
                    2,
                    &mut bounded_warning_count,
                    &mut bounded_warning_flags,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(bounded_warning_count, 32);
        assert_ne!(bounded_warning_flags, 0);
        let mut bounded_output_count = 0;
        let mut bounded_output_flags = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_info(
                    bounded_result,
                    0,
                    &mut bounded_output_count,
                    &mut bounded_output_flags,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(bounded_output_count, 32);
        assert_ne!(bounded_output_flags, 0);
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(bounded_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_clear(bounded_builder, &mut call_result) },
            Status::Success.value()
        );
        let replacement_script = "Write-Output 'stream-buffers-replaced'";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    bounded_builder,
                    Utf8Span {
                        data: replacement_script.as_ptr(),
                        len: replacement_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let mut replacement_result = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke(bounded_builder, &mut replacement_result, &mut call_result) },
            Status::Success.value()
        );
        let mut replacement_warning_count = 0;
        let mut replacement_warning_flags = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_stream_info(
                    replacement_result,
                    2,
                    &mut replacement_warning_count,
                    &mut replacement_warning_flags,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(replacement_warning_count, 0);
        assert_eq!(replacement_warning_flags, 0);
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(replacement_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(bounded_builder, &mut call_result) },
            Status::Success.value()
        );

        let mut invalid_state_handle = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_create(&mut invalid_state_handle, &mut call_result) },
            Status::Success.value()
        );
        let mut error_count = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_get_invocation_error_count(invalid_state_handle, &mut error_count, &mut call_result) },
            Status::ManagedFailure.value()
        );
        assert_eq!(call_result.status, Status::ManagedFailure.value());
        let diagnostic = std::str::from_utf8(&diagnostic[..call_result.diagnostic_written]).unwrap();
        assert!(
            diagnostic.contains("No invocation"),
            "unexpected diagnostic: {}",
            diagnostic
        );
        assert_ne!(call_result.flags & CALL_RESULT_DIAGNOSTIC_TRUNCATED, 0);
        assert!(call_result.diagnostic_required > call_result.diagnostic_written);
        assert_eq!(
            unsafe { dps_pwsh_v2_release(invalid_state_handle, &mut call_result) },
            Status::Success.value()
        );

        assert_ne!(dps_pwsh_feature_flags() & FEATURE_STRUCTURED_INVOCATION_ERRORS, 0);

        let handle = create_session();

        let script = "'ffi-explicit-payload'";
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(handle, script.as_ptr(), script.len()) },
            Status::Success.value()
        );
        assert_eq!(invoke_output(handle), "ffi-explicit-payload\r\n");
        assert_eq!(dps_pwsh_release(handle), Status::Success.value());

        let unicode_handle = create_session();
        let unicode_script = "'héllo'";
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(unicode_handle, unicode_script.as_ptr(), unicode_script.len(),) },
            Status::Success.value()
        );
        assert_eq!(invoke_output(unicode_handle), "héllo\r\n");
        assert_eq!(dps_pwsh_release(unicode_handle), Status::Success.value());

        let parameter_handle = create_session();
        let command = "Write-Output";
        let parameter = "InputObject";
        assert_eq!(
            unsafe { dps_pwsh_add_command_utf8(parameter_handle, command.as_ptr(), command.len()) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_add_parameter_i64(parameter_handle, parameter.as_ptr(), parameter.len(), 42,) },
            Status::Success.value()
        );
        assert_eq!(invoke_output(parameter_handle), "42\r\n");
        assert_eq!(dps_pwsh_release(parameter_handle), Status::Success.value());

        let statement_handle = create_session();
        let first = "'first'";
        let second = "'second'";
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(statement_handle, first.as_ptr(), first.len()) },
            Status::Success.value()
        );
        assert_eq!(dps_pwsh_add_statement(statement_handle), Status::Success.value());
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(statement_handle, second.as_ptr(), second.len()) },
            Status::Success.value()
        );
        assert_eq!(invoke_output(statement_handle), "first\r\nsecond\r\n");
        assert_eq!(dps_pwsh_release(statement_handle), Status::Success.value());

        let clear_handle = create_session();
        let discarded = "'discarded'";
        let kept = "'kept'";
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(clear_handle, discarded.as_ptr(), discarded.len()) },
            Status::Success.value()
        );
        assert_eq!(dps_pwsh_clear(clear_handle), Status::Success.value());
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(clear_handle, kept.as_ptr(), kept.len()) },
            Status::Success.value()
        );
        assert_eq!(invoke_output(clear_handle), "kept\r\n");
        assert_eq!(dps_pwsh_release(clear_handle), Status::Success.value());
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(clear_handle, kept.as_ptr(), kept.len()) },
            Status::InvalidHandle.value()
        );

        let cached_output_handle = create_session();
        let increment = "$global:DpsPwshFfiInvocationCounter = 0; $global:DpsPwshFfiInvocationCounter++; $global:DpsPwshFfiInvocationCounter";
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(cached_output_handle, increment.as_ptr(), increment.len(),) },
            Status::Success.value()
        );
        assert_eq!(invoke_output(cached_output_handle), "1\r\n");
        assert_eq!(invoke_output(cached_output_handle), "1\r\n");
        assert_eq!(dps_pwsh_release(cached_output_handle), Status::Success.value());

        let cancellation_handle = create_session();
        let blocking_script = "Start-Sleep -Seconds 30; 'unexpected completion'";
        assert_eq!(
            unsafe { dps_pwsh_add_script_utf8(cancellation_handle, blocking_script.as_ptr(), blocking_script.len(),) },
            Status::Success.value()
        );
        let (completion_sender, completion_receiver) = std::sync::mpsc::channel();
        let invoker = std::thread::spawn(move || {
            let mut required = 0;
            let status = unsafe { dps_pwsh_invoke_utf8(cancellation_handle, std::ptr::null_mut(), 0, &mut required) };
            completion_sender.send((status, required)).unwrap();
        });
        std::thread::sleep(std::time::Duration::from_millis(250));
        assert_eq!(dps_pwsh_stop(cancellation_handle), Status::Success.value());
        let (status, required) = completion_receiver
            .recv_timeout(std::time::Duration::from_secs(5))
            .expect("Stop must complete the active invocation");
        assert!(
            status == Status::ManagedFailure.value() || status == Status::Success.value(),
            "unexpected cancellation status {}",
            status
        );
        assert_eq!(required, 0, "stopped invocation must not return script output");
        invoker.join().unwrap();
        assert_eq!(dps_pwsh_release(cancellation_handle), Status::Success.value());

        let non_terminating_error_handle = create_session();
        let non_terminating_error = "Write-Error -Message 'ffi-non-terminating-error'";
        assert_eq!(
            unsafe {
                dps_pwsh_add_script_utf8(
                    non_terminating_error_handle,
                    non_terminating_error.as_ptr(),
                    non_terminating_error.len(),
                )
            },
            Status::Success.value()
        );
        assert_eq!(invoke_output(non_terminating_error_handle), "");
        assert_eq!(invocation_error_count(non_terminating_error_handle), 1);
        assert!(invocation_error_field(non_terminating_error_handle, 0, 0).contains("ffi-non-terminating-error"));
        assert!(!invocation_error_field(non_terminating_error_handle, 0, 3).is_empty());
        assert_eq!(dps_pwsh_release(non_terminating_error_handle), Status::Success.value());

        let terminating_error_handle = create_session();
        let terminating_error = "throw 'ffi-terminating-error'";
        assert_eq!(
            unsafe {
                dps_pwsh_add_script_utf8(
                    terminating_error_handle,
                    terminating_error.as_ptr(),
                    terminating_error.len(),
                )
            },
            Status::Success.value()
        );
        let mut required = 0;
        assert_eq!(
            unsafe { dps_pwsh_invoke_utf8(terminating_error_handle, std::ptr::null_mut(), 0, &mut required,) },
            Status::ManagedFailure.value()
        );
        assert_eq!(invocation_error_count(terminating_error_handle), 1);
        assert!(invocation_error_field(terminating_error_handle, 0, 0).contains("ffi-terminating-error"));
        assert_eq!(dps_pwsh_release(terminating_error_handle), Status::Success.value());
    }

    #[test]
    #[ignore = "requires PWSH_FFI_PAYLOAD to be an explicit PowerShell payload directory"]
    fn explicit_payload_async_operations_are_terminal_and_lifetime_safe() {
        let payload = std::env::var("PWSH_FFI_PAYLOAD")
            .expect("PWSH_FFI_PAYLOAD must name an explicit PowerShell payload directory");
        let mut diagnostic = [0_u8; 512];
        let mut call_result = v2_call_result(&mut diagnostic);
        initialize_v2_trusted(&payload, &mut call_result);
        assert_ne!(dps_pwsh_feature_flags() & FEATURE_ASYNC_OPERATIONS, 0);

        let success_builder = v2_create_session(&mut call_result);
        let success_script = "$input | ForEach-Object { Start-Sleep -Milliseconds 250; $_ * 2 }";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    success_builder,
                    Utf8Span {
                        data: success_script.as_ptr(),
                        len: success_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        add_v2_input_value(success_builder, 4, &3_i64.to_le_bytes(), &mut call_result);
        assert_eq!(
            unsafe { dps_pwsh_v2_complete_input(success_builder, &mut call_result) },
            Status::Success.value()
        );
        let mut success_operation = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(success_builder, &mut success_operation, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_complete_input(success_builder, &mut call_result) },
            Status::Backpressure.value()
        );
        let mut operation_state = 0;
        let mut terminal_status = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_get_result(success_operation, std::ptr::null_mut(), &mut call_result) },
            Status::InvalidArgument.value()
        );
        let mut result_before_terminal = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_operation_get_result(success_operation, &mut result_before_terminal, &mut call_result)
            },
            Status::OperationNotTerminal.value()
        );
        assert_eq!(call_result.status, Status::OperationNotTerminal.value());
        assert_eq!(
            unsafe {
                dps_pwsh_v2_operation_wait(
                    success_operation,
                    5_000,
                    &mut operation_state,
                    &mut terminal_status,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(operation_state, OperationState::Completed as u32);
        assert_eq!(terminal_status, Status::Success.value());
        let mut success_result = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_get_result(success_operation, &mut success_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(v2_result_output(success_result, &mut call_result), vec!["6".to_owned()]);
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(success_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_release(success_operation, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(success_builder, &mut call_result) },
            Status::Success.value()
        );

        let cancellation_builder = v2_create_session(&mut call_result);
        let cancellation_script = "Start-Sleep -Seconds 30; 'unexpected completion'";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    cancellation_builder,
                    Utf8Span {
                        data: cancellation_script.as_ptr(),
                        len: cancellation_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let mut cancellation_operation = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(cancellation_builder, &mut cancellation_operation, &mut call_result) },
            Status::Success.value()
        );
        std::thread::sleep(Duration::from_millis(100));
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_stop(cancellation_operation, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_stop(cancellation_operation, &mut call_result) },
            Status::Success.value()
        );
        operation_state = 0;
        terminal_status = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_operation_wait(
                    cancellation_operation,
                    5_000,
                    &mut operation_state,
                    &mut terminal_status,
                    &mut call_result,
                )
            },
            Status::OperationCancelled.value()
        );
        assert_eq!(operation_state, OperationState::Cancelled as u32);
        assert_eq!(terminal_status, Status::OperationCancelled.value());
        assert!(std::str::from_utf8(&diagnostic[..call_result.diagnostic_written])
            .unwrap()
            .contains("cancelled"));
        let mut cancelled_result = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_operation_get_result(cancellation_operation, &mut cancelled_result, &mut call_result)
            },
            Status::OperationCancelled.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_release(cancellation_operation, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(cancellation_builder, &mut call_result) },
            Status::Success.value()
        );

        let race_builder = v2_create_session(&mut call_result);
        let race_script = "Start-Sleep -Seconds 30; 'release-race'";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    race_builder,
                    Utf8Span {
                        data: race_script.as_ptr(),
                        len: race_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let mut race_operation = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(race_builder, &mut race_operation, &mut call_result) },
            Status::Success.value()
        );
        let poller = std::thread::spawn(move || {
            for _ in 0..32 {
                let mut state = 0;
                let mut status = 0;
                let mut call_result = CallResult {
                    size: std::mem::size_of::<CallResult>() as u32,
                    status: 0,
                    flags: 0,
                    _reserved: 0,
                    diagnostic: std::ptr::null_mut(),
                    diagnostic_capacity: 0,
                    diagnostic_required: 0,
                    diagnostic_written: 0,
                };
                let poll_status =
                    unsafe { dps_pwsh_v2_operation_poll(race_operation, &mut state, &mut status, &mut call_result) };
                assert!(
                    poll_status == Status::Success.value()
                        || poll_status == Status::OperationCancelled.value()
                        || poll_status == Status::InvalidHandle.value(),
                    "unexpected release-race poll status {}",
                    poll_status
                );
            }
        });
        std::thread::sleep(Duration::from_millis(50));
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_release(race_operation, &mut call_result) },
            Status::Success.value()
        );
        poller.join().unwrap();
        assert_eq!(
            unsafe { dps_pwsh_v2_release(race_builder, &mut call_result) },
            Status::Success.value()
        );
    }

    #[test]
    #[ignore = "requires PWSH_FFI_PAYLOAD to be an explicit PowerShell payload directory"]
    fn explicit_payload_lifecycle_stress_enforces_serialization_and_lifetime_contracts() {
        let payload = std::env::var("PWSH_FFI_PAYLOAD")
            .expect("PWSH_FFI_PAYLOAD must name an explicit PowerShell payload directory");
        let mut diagnostic = [0_u8; 512];
        let mut call_result = v2_call_result(&mut diagnostic);
        initialize_v2_trusted(&payload, &mut call_result);

        let stale_builder = v2_create_session(&mut call_result);
        assert_eq!(
            unsafe { dps_pwsh_v2_release(stale_builder, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(stale_builder, &mut call_result) },
            Status::InvalidHandle.value()
        );
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    stale_builder,
                    Utf8Span {
                        data: b"'stale'".as_ptr(),
                        len: b"'stale'".len(),
                    },
                    &mut call_result,
                )
            },
            Status::InvalidHandle.value()
        );
        let fresh_builder = v2_create_session(&mut call_result);
        assert_ne!(
            fresh_builder, stale_builder,
            "released builder handles must not be reused"
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(fresh_builder, &mut call_result) },
            Status::Success.value()
        );

        let cancellation_builder = v2_create_session(&mut call_result);
        let cancellation_script = "1..50 | ForEach-Object { Start-Sleep -Milliseconds 100; Write-Output $_ }";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    cancellation_builder,
                    Utf8Span {
                        data: cancellation_script.as_ptr(),
                        len: cancellation_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let mut cancellation_operation = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(cancellation_builder, &mut cancellation_operation, &mut call_result,) },
            Status::Success.value()
        );

        let barrier = Arc::new(std::sync::Barrier::new(4));
        let operation_stop = {
            let barrier = Arc::clone(&barrier);
            std::thread::spawn(move || {
                let mut result = v2_call_result_without_diagnostic();
                barrier.wait();
                unsafe { dps_pwsh_v2_operation_stop(cancellation_operation, &mut result) }
            })
        };
        let repeated_operation_stop = {
            let barrier = Arc::clone(&barrier);
            std::thread::spawn(move || {
                let mut result = v2_call_result_without_diagnostic();
                barrier.wait();
                unsafe { dps_pwsh_v2_operation_stop(cancellation_operation, &mut result) }
            })
        };
        let builder_stop = {
            let barrier = Arc::clone(&barrier);
            std::thread::spawn(move || {
                let mut result = v2_call_result_without_diagnostic();
                barrier.wait();
                unsafe { dps_pwsh_v2_stop(cancellation_builder, &mut result) }
            })
        };
        let builder_release = {
            let barrier = Arc::clone(&barrier);
            std::thread::spawn(move || {
                let mut result = v2_call_result_without_diagnostic();
                barrier.wait();
                unsafe { dps_pwsh_v2_release(cancellation_builder, &mut result) }
            })
        };
        assert_eq!(operation_stop.join().unwrap(), Status::Success.value());
        assert_eq!(repeated_operation_stop.join().unwrap(), Status::Success.value());
        assert!(
            matches!(
                builder_stop.join().unwrap(),
                value if value == Status::Success.value() || value == Status::InvalidHandle.value()
            ),
            "builder stop may race the releasing owner, but must remain defined"
        );
        assert_eq!(builder_release.join().unwrap(), Status::Success.value());

        let mut operation_state = 0;
        let mut terminal_status = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_operation_wait(
                    cancellation_operation,
                    5_000,
                    &mut operation_state,
                    &mut terminal_status,
                    &mut call_result,
                )
            },
            Status::OperationCancelled.value()
        );
        assert_eq!(operation_state, OperationState::Cancelled as u32);
        assert_eq!(terminal_status, Status::OperationCancelled.value());
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_stop(cancellation_operation, &mut call_result) },
            Status::Success.value()
        );
        let mut cancelled_result = u64::MAX;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_operation_get_result(cancellation_operation, &mut cancelled_result, &mut call_result)
            },
            Status::OperationCancelled.value()
        );
        assert_eq!(
            cancelled_result,
            u64::MAX,
            "cancelled operations must not return successful partial output"
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_release(cancellation_operation, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_release(cancellation_operation, &mut call_result) },
            Status::InvalidHandle.value()
        );
        assert_eq!(
            unsafe {
                dps_pwsh_v2_operation_poll(
                    cancellation_operation,
                    &mut operation_state,
                    &mut terminal_status,
                    &mut call_result,
                )
            },
            Status::InvalidHandle.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(cancellation_builder, &mut call_result) },
            Status::InvalidHandle.value()
        );

        let result_builder = v2_create_session(&mut call_result);
        let result_script = "'result-outlives-builder'";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    result_builder,
                    Utf8Span {
                        data: result_script.as_ptr(),
                        len: result_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let result_handle = v2_invoke(result_builder, &mut call_result);
        assert_eq!(
            unsafe { dps_pwsh_v2_release(result_builder, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            v2_result_output(result_handle, &mut call_result),
            vec!["result-outlives-builder".to_owned()]
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(result_handle, &mut call_result) },
            Status::Success.value()
        );
        let mut result_flags = 0;
        let mut sequence_count = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_info(result_handle, &mut result_flags, &mut sequence_count, &mut call_result)
            },
            Status::InvalidHandle.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(result_handle, &mut call_result) },
            Status::InvalidHandle.value()
        );

        let session_options = empty_session_options();
        let mut session_handle = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create(&session_options, &mut session_handle, &mut call_result) },
            Status::Success.value()
        );
        let mut session_builder = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create_builder(session_handle, &mut session_builder, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_session_release(session_handle, &mut call_result) },
            Status::Success.value()
        );
        let mut rejected_builder = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create_builder(session_handle, &mut rejected_builder, &mut call_result) },
            Status::InvalidHandle.value()
        );
        let mut stale_snapshot = SessionSnapshot {
            size: mem::size_of::<SessionSnapshot>() as u32,
            state: 0,
            runspace_state: 0,
            flags: 0,
            active_pipeline_count: 0,
            event_count: 0,
            invocation_count: 0,
            history_count: 0,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_session_get_snapshot(session_handle, &mut stale_snapshot, &mut call_result) },
            Status::InvalidHandle.value()
        );
        let session_script = "'builder-outlives-session'";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    session_builder,
                    Utf8Span {
                        data: session_script.as_ptr(),
                        len: session_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let session_result = v2_invoke(session_builder, &mut call_result);
        assert_eq!(
            v2_result_output(session_result, &mut call_result),
            vec!["builder-outlives-session".to_owned()]
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(session_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(session_builder, &mut call_result) },
            Status::Success.value()
        );

        let first_builder = v2_create_session(&mut call_result);
        let second_builder = v2_create_session(&mut call_result);
        let serialized_script = "Start-Sleep -Milliseconds 300; 'serialized'";
        for builder in [first_builder, second_builder] {
            assert_eq!(
                unsafe {
                    dps_pwsh_v2_add_script_utf8(
                        builder,
                        Utf8Span {
                            data: serialized_script.as_ptr(),
                            len: serialized_script.len(),
                        },
                        &mut call_result,
                    )
                },
                Status::Success.value()
            );
        }
        let started = Instant::now();
        let mut first_operation = 0;
        let mut second_operation = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(first_builder, &mut first_operation, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(second_builder, &mut second_operation, &mut call_result) },
            Status::Success.value()
        );
        for operation in [first_operation, second_operation] {
            operation_state = 0;
            terminal_status = 0;
            assert_eq!(
                unsafe {
                    dps_pwsh_v2_operation_wait(
                        operation,
                        5_000,
                        &mut operation_state,
                        &mut terminal_status,
                        &mut call_result,
                    )
                },
                Status::Success.value()
            );
            assert_eq!(operation_state, OperationState::Completed as u32);
            assert_eq!(terminal_status, Status::Success.value());
            let mut result = 0;
            assert_eq!(
                unsafe { dps_pwsh_v2_operation_get_result(operation, &mut result, &mut call_result) },
                Status::Success.value()
            );
            assert_eq!(
                unsafe { dps_pwsh_v2_result_release(result, &mut call_result) },
                Status::Success.value()
            );
            assert_eq!(
                unsafe { dps_pwsh_v2_operation_release(operation, &mut call_result) },
                Status::Success.value()
            );
        }
        assert!(
            started.elapsed() >= Duration::from_millis(500),
            "process-global normal-operation serialization must make two 300 ms invocations sequential"
        );
        for builder in [first_builder, second_builder] {
            assert_eq!(
                unsafe { dps_pwsh_v2_release(builder, &mut call_result) },
                Status::Success.value()
            );
        }
    }

    #[test]
    #[ignore = "requires PWSH_FFI_PAYLOAD to be an explicit PowerShell payload directory"]
    fn explicit_payload_increment_6_sessions_are_bounded_and_lifetime_safe() {
        let payload = std::env::var("PWSH_FFI_PAYLOAD")
            .expect("PWSH_FFI_PAYLOAD must name an explicit PowerShell payload directory");
        let mut diagnostic = [0_u8; 512];
        let mut call_result = v2_call_result(&mut diagnostic);
        initialize_v2_trusted(&payload, &mut call_result);
        assert_ne!(dps_pwsh_feature_flags() & FEATURE_SESSIONS, 0);
        assert_ne!(dps_pwsh_feature_flags() & FEATURE_SESSION_POLLING, 0);
        assert_ne!(dps_pwsh_feature_flags() & FEATURE_SESSION_POOL_REJECTION, 0);
        assert_ne!(dps_pwsh_feature_flags() & FEATURE_SESSION_VARIABLES, 0);

        let configured_options = SessionOptions {
            history_mode: 1,
            error_preference: 3,
            warning_preference: 1,
            verbose_preference: 1,
            information_preference: 1,
            ..empty_session_options()
        };
        let mut configured_session = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create(&configured_options, &mut configured_session, &mut call_result) },
            Status::Success.value()
        );
        assert_ne!(configured_session, 0);
        let mut snapshot = SessionSnapshot {
            size: mem::size_of::<SessionSnapshot>() as u32,
            state: 0,
            runspace_state: 0,
            flags: 0,
            active_pipeline_count: 0,
            event_count: 0,
            invocation_count: 0,
            history_count: 0,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_session_get_snapshot(configured_session, &mut snapshot, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(snapshot.state, 1);
        assert_eq!(snapshot.active_pipeline_count, 0);
        assert!(snapshot.event_count >= 1);

        let variable_name = "FfiCopied";
        let variable_payload = b"copied-session-variable";
        let variable_value = DataValue {
            size: mem::size_of::<DataValue>() as u32,
            kind: VALUE_KIND_STRING,
            flags: 0,
            _reserved: 0,
            data: variable_payload.as_ptr(),
            data_len: variable_payload.len(),
        };
        assert_eq!(
            unsafe {
                dps_pwsh_v2_session_set_variable(
                    configured_session,
                    Utf8Span {
                        data: variable_name.as_ptr(),
                        len: variable_name.len(),
                    },
                    &variable_value,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let mut found = 0;
        let mut variable_kind = 0;
        let mut required_length = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_session_get_variable_snapshot(
                    configured_session,
                    Utf8Span {
                        data: variable_name.as_ptr(),
                        len: variable_name.len(),
                    },
                    &mut found,
                    &mut variable_kind,
                    std::ptr::null_mut(),
                    0,
                    &mut required_length,
                    &mut call_result,
                )
            },
            Status::BufferTooSmall.value()
        );
        assert_eq!(found, 1);
        assert_eq!(variable_kind, VALUE_KIND_STRING);
        assert_eq!(required_length, variable_payload.len());
        let mut variable_snapshot = vec![0_u8; required_length];
        assert_eq!(
            unsafe {
                dps_pwsh_v2_session_get_variable_snapshot(
                    configured_session,
                    Utf8Span {
                        data: variable_name.as_ptr(),
                        len: variable_name.len(),
                    },
                    &mut found,
                    &mut variable_kind,
                    variable_snapshot.as_mut_ptr(),
                    variable_snapshot.len(),
                    &mut required_length,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(found, 1);
        assert_eq!(variable_kind, VALUE_KIND_STRING);
        assert_eq!(variable_snapshot, variable_payload);
        let mut removed = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_session_remove_variable(
                    configured_session,
                    Utf8Span {
                        data: variable_name.as_ptr(),
                        len: variable_name.len(),
                    },
                    &mut removed,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(removed, 1);
        assert_eq!(
            unsafe {
                dps_pwsh_v2_session_remove_variable(
                    configured_session,
                    Utf8Span {
                        data: variable_name.as_ptr(),
                        len: variable_name.len(),
                    },
                    &mut removed,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(removed, 0);

        let mut configured_builder = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_session_create_builder(configured_session, &mut configured_builder, &mut call_result)
            },
            Status::Success.value()
        );
        let configured_script = "Write-Output \"$ErrorActionPreference|$WarningPreference|$VerbosePreference\"";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    configured_builder,
                    Utf8Span {
                        data: configured_script.as_ptr(),
                        len: configured_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let configured_result = v2_invoke(configured_builder, &mut call_result);
        assert_eq!(
            v2_result_output(configured_result, &mut call_result),
            vec!["Stop|Continue|Continue".to_owned()]
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(configured_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_session_get_snapshot(configured_session, &mut snapshot, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(snapshot.active_pipeline_count, 0);
        assert_eq!(snapshot.invocation_count, 1);
        assert_eq!(snapshot.history_count, 1);
        let mut event_sequence = 0;
        let mut event_state = 0;
        let mut event_flags = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_session_get_event_info(
                    configured_session,
                    0,
                    &mut event_sequence,
                    &mut event_state,
                    &mut event_flags,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_ne!(event_sequence, 0);
        assert_eq!(event_state, 1);

        let first_script = "Start-Sleep -Milliseconds 100; 'session-first'";
        let second_script = "Start-Sleep -Milliseconds 100; 'session-second'";
        let mut first_builder = 0;
        let mut second_builder = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create_builder(configured_session, &mut first_builder, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create_builder(configured_session, &mut second_builder, &mut call_result) },
            Status::Success.value()
        );
        for (builder, script) in [(first_builder, first_script), (second_builder, second_script)] {
            assert_eq!(
                unsafe {
                    dps_pwsh_v2_add_script_utf8(
                        builder,
                        Utf8Span {
                            data: script.as_ptr(),
                            len: script.len(),
                        },
                        &mut call_result,
                    )
                },
                Status::Success.value()
            );
        }
        let mut first_operation = 0;
        let mut second_operation = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(first_builder, &mut first_operation, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(second_builder, &mut second_operation, &mut call_result) },
            Status::Backpressure.value()
        );
        assert_eq!(second_operation, 0);
        for operation in [first_operation] {
            let mut operation_state = 0;
            let mut terminal_status = 0;
            assert_eq!(
                unsafe {
                    dps_pwsh_v2_operation_wait(
                        operation,
                        5_000,
                        &mut operation_state,
                        &mut terminal_status,
                        &mut call_result,
                    )
                },
                Status::Success.value()
            );
            assert_eq!(operation_state, OperationState::Completed as u32);
            assert_eq!(terminal_status, Status::Success.value());
            let mut result = 0;
            assert_eq!(
                unsafe { dps_pwsh_v2_operation_get_result(operation, &mut result, &mut call_result) },
                Status::Success.value()
            );
            assert_eq!(
                unsafe { dps_pwsh_v2_result_release(result, &mut call_result) },
                Status::Success.value()
            );
            assert_eq!(
                unsafe { dps_pwsh_v2_operation_release(operation, &mut call_result) },
                Status::Success.value()
            );
        }
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke_async(second_builder, &mut second_operation, &mut call_result) },
            Status::Success.value()
        );
        let mut operation_state = 0;
        let mut terminal_status = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_operation_wait(
                    second_operation,
                    5_000,
                    &mut operation_state,
                    &mut terminal_status,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(operation_state, OperationState::Completed as u32);
        assert_eq!(terminal_status, Status::Success.value());
        let mut second_result = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_get_result(second_operation, &mut second_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(second_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_operation_release(second_operation, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(first_builder, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(second_builder, &mut call_result) },
            Status::Success.value()
        );
        for _ in 0..13 {
            let event_result = v2_invoke(configured_builder, &mut call_result);
            assert_eq!(
                unsafe { dps_pwsh_v2_result_release(event_result, &mut call_result) },
                Status::Success.value()
            );
        }
        assert_eq!(
            unsafe { dps_pwsh_v2_session_get_snapshot(configured_session, &mut snapshot, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(snapshot.event_count, 32);
        assert_ne!(snapshot.flags & 1, 0);

        assert_eq!(
            unsafe { dps_pwsh_v2_session_release(configured_session, &mut call_result) },
            Status::Success.value()
        );
        let mut lifetime_result = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke(configured_builder, &mut lifetime_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            v2_result_output(lifetime_result, &mut call_result),
            vec!["Stop|Continue|Continue".to_owned()]
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(lifetime_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(configured_builder, &mut call_result) },
            Status::Success.value()
        );

        let rejected_current_options = SessionOptions {
            runspace_mode: 0,
            history_mode: 1,
            ..empty_session_options()
        };
        let mut rejected_session = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_create(&rejected_current_options, &mut rejected_session, &mut call_result) },
            Status::UnsupportedCapability.value()
        );
        assert_eq!(rejected_session, 0);
        assert!(std::str::from_utf8(&diagnostic[..call_result.diagnostic_written])
            .unwrap()
            .contains("current-runspace"));

        let pool_options = SessionPoolOptions {
            size: mem::size_of::<SessionPoolOptions>() as u32,
            minimum_sessions: 0,
            maximum_sessions: 2,
            flags: 0,
            _reserved: 0,
        };
        let mut pool_handle = u64::MAX;
        assert_eq!(
            unsafe { dps_pwsh_v2_session_pool_create(&pool_options, &mut pool_handle, &mut call_result) },
            Status::UnsupportedCapability.value()
        );
        assert_eq!(pool_handle, 0);
    }

    #[test]
    fn tagged_value_validation_rejects_unknown_and_malformed_payloads() {
        let unknown = DataValue {
            size: std::mem::size_of::<DataValue>() as u32,
            kind: u32::MAX,
            flags: 0,
            _reserved: 0,
            data: std::ptr::null(),
            data_len: 0,
        };
        assert!(matches!(
            unsafe { data_value_input(&unknown) },
            Err((Status::UnsupportedValue, _))
        ));

        let malformed = DataValue {
            size: std::mem::size_of::<DataValue>() as u32,
            kind: 3,
            flags: 0,
            _reserved: 0,
            data: [2_u8].as_ptr(),
            data_len: 1,
        };
        assert!(matches!(
            unsafe { data_value_input(&malformed) },
            Err((Status::InvalidArgument, _))
        ));
    }

    #[test]
    fn session_options_and_pool_bounds_reject_unsupported_configurations() {
        let legacy_prefix = SessionOptionsPrefix {
            size: SESSION_OPTIONS_PREFIX_SIZE,
            runspace_mode: 1,
            initial_configuration: 0,
            history_mode: 0,
            error_preference: 0,
            warning_preference: 0,
            verbose_preference: 0,
            debug_preference: 0,
            information_preference: 0,
            flags: 0,
            _reserved: 0,
            allowed_module_path: Utf8Span {
                data: std::ptr::null(),
                len: 0,
            },
        };
        assert!(unsafe {
            session_options_input(&legacy_prefix as *const SessionOptionsPrefix as *const SessionOptions)
        }
        .is_ok());

        let current_with_history = SessionOptions {
            runspace_mode: 0,
            history_mode: 1,
            ..empty_session_options()
        };
        assert!(matches!(
            unsafe { session_options_input(&current_with_history) },
            Err((Status::UnsupportedCapability, _))
        ));

        let unsupported_preference = SessionOptions {
            error_preference: 4,
            ..current_with_history
        };
        assert!(matches!(
            unsafe { session_options_input(&unsupported_preference) },
            Err((Status::InvalidArgument, _))
        ));

        let invalid_pool = SessionPoolOptions {
            size: mem::size_of::<SessionPoolOptions>() as u32,
            minimum_sessions: 0,
            maximum_sessions: 0,
            flags: 0,
            _reserved: 0,
        };
        assert!(matches!(
            validate_pool_options(&invalid_pool),
            Err((Status::InvalidArgument, _))
        ));

        let bounded_pool = SessionPoolOptions {
            maximum_sessions: 64,
            ..invalid_pool
        };
        assert!(validate_pool_options(&bounded_pool).is_ok());
    }

    #[test]
    #[ignore = "requires PWSH_FFI_PAYLOAD to be an explicit PowerShell payload directory"]
    fn explicit_payload_increment_3_tagged_values_commands_and_input_are_bounded() {
        let payload = std::env::var("PWSH_FFI_PAYLOAD")
            .expect("PWSH_FFI_PAYLOAD must name an explicit PowerShell payload directory");
        let mut diagnostic = [0_u8; 512];
        let mut call_result = v2_call_result(&mut diagnostic);
        initialize_v2_trusted(&payload, &mut call_result);
        assert_ne!(dps_pwsh_feature_flags() & FEATURE_TAGGED_VALUES, 0);
        assert_ne!(dps_pwsh_feature_flags() & FEATURE_COMMAND_OPTIONS, 0);
        assert_ne!(dps_pwsh_feature_flags() & FEATURE_BOUNDED_INPUT, 0);

        let value_handle = v2_create_session(&mut call_result);
        let value_script = r#"
            param($Nothing, $String, $Boolean, $Signed, $Unsigned, $Double, $Decimal, $Bytes, $When, $Offset, $Guid, $Uri, $Array, $Bag, $Switch)
            @(
                ($null -eq $Nothing),
                ($String -eq 'tagged'),
                ($Boolean -eq $true),
                ($Signed -eq -7),
                ($Unsigned -eq 9),
                ($Double -eq 2.5),
                ($Decimal -eq [decimal]42.5),
                ($Bytes.Length -eq 3),
                ($When.Ticks -eq 0),
                ($Offset.Ticks -eq 621355968000000000),
                ($Guid -eq [guid]'01234567-89ab-cdef-0123-456789abcdef'),
                ($Uri.Scheme -eq 'https'),
                ($Array.Count -eq 2 -and $Array[0] -eq 1 -and $Array[1] -eq 'two'),
                ($Bag.Name -eq 'snapshot' -and $Bag.Count -eq 3),
                ($Switch.IsPresent)
            ) -join '|'
        "#;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    value_handle,
                    Utf8Span {
                        data: value_script.as_ptr(),
                        len: value_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        add_v2_parameter_value(value_handle, "Nothing", 0, &[], &mut call_result);
        add_v2_parameter_value(value_handle, "String", 1, b"tagged", &mut call_result);
        add_v2_parameter_value(value_handle, "Boolean", 3, &[1], &mut call_result);
        add_v2_parameter_value(value_handle, "Signed", 4, &(-7_i64).to_le_bytes(), &mut call_result);
        add_v2_parameter_value(value_handle, "Unsigned", 5, &(9_u64).to_le_bytes(), &mut call_result);
        add_v2_parameter_value(
            value_handle,
            "Double",
            6,
            &2.5_f64.to_bits().to_le_bytes(),
            &mut call_result,
        );
        add_v2_parameter_value(value_handle, "Decimal", 7, b"42.5", &mut call_result);
        add_v2_parameter_value(value_handle, "Bytes", 8, b"abc", &mut call_result);
        add_v2_parameter_value(value_handle, "When", 9, &0_i64.to_le_bytes(), &mut call_result);
        let mut offset = Vec::from(621355968000000000_i64.to_le_bytes());
        offset.extend_from_slice(&0_i16.to_le_bytes());
        add_v2_parameter_value(value_handle, "Offset", 10, &offset, &mut call_result);
        add_v2_parameter_value(
            value_handle,
            "Guid",
            11,
            b"01234567-89ab-cdef-0123-456789abcdef",
            &mut call_result,
        );
        add_v2_parameter_value(value_handle, "Uri", 12, b"https://example.test/path", &mut call_result);
        let first_array_item = 1_i64.to_le_bytes();
        let array = array_payload(&[(4, first_array_item.as_slice()), (1, b"two".as_slice())]);
        add_v2_parameter_value(value_handle, "Array", 13, &array, &mut call_result);
        let bag = property_bag_payload(&[("Name", 1, b"snapshot"), ("Count", 4, &3_i64.to_le_bytes())]);
        add_v2_parameter_value(value_handle, "Bag", 14, &bag, &mut call_result);
        add_v2_parameter_value(value_handle, "Switch", 2, &[1], &mut call_result);
        let value_result = v2_invoke(value_handle, &mut call_result);
        let mut state = 0;
        let mut invocation_id = 0;
        let mut had_errors = 0;
        assert_eq!(
            unsafe {
                dps_pwsh_v2_result_get_metadata(
                    value_result,
                    &mut state,
                    &mut invocation_id,
                    &mut had_errors,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(state, 1);
        assert_ne!(invocation_id, 0);
        assert_eq!(had_errors, 0);
        assert_eq!(
            v2_result_output(value_result, &mut call_result),
            vec!["True|True|True|True|True|True|True|True|True|True|True|True|True|True|True".to_owned()]
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(value_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(value_handle, &mut call_result) },
            Status::Success.value()
        );

        let command_handle = v2_create_session(&mut call_result);
        let switch_script = "param([switch] $Flag) if ($Flag) { 'switch' }";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8_local(
                    command_handle,
                    Utf8Span {
                        data: switch_script.as_ptr(),
                        len: switch_script.len(),
                    },
                    1,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let flag = "Flag";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_parameter_switch(
                    command_handle,
                    Utf8Span {
                        data: flag.as_ptr(),
                        len: flag.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_add_statement(command_handle, &mut call_result) },
            Status::Success.value()
        );
        let second_statement = "'multiple-statements'";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8_local(
                    command_handle,
                    Utf8Span {
                        data: second_statement.as_ptr(),
                        len: second_statement.len(),
                    },
                    0,
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        let command_result = v2_invoke(command_handle, &mut call_result);
        assert_eq!(
            v2_result_output(command_result, &mut call_result),
            vec!["switch".to_owned(), "multiple-statements".to_owned()]
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(command_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(command_handle, &mut call_result) },
            Status::Success.value()
        );

        let input_handle = v2_create_session(&mut call_result);
        let input_script = "$input | ForEach-Object { $_ * 2 }";
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_script_utf8(
                    input_handle,
                    Utf8Span {
                        data: input_script.as_ptr(),
                        len: input_script.len(),
                    },
                    &mut call_result,
                )
            },
            Status::Success.value()
        );
        add_v2_input_value(input_handle, 4, &3_i64.to_le_bytes(), &mut call_result);
        let mut incomplete_result = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke(input_handle, &mut incomplete_result, &mut call_result) },
            Status::InputNotCompleted.value()
        );
        assert_eq!(call_result.status, Status::InputNotCompleted.value());
        assert!(std::str::from_utf8(&diagnostic[..call_result.diagnostic_written])
            .unwrap()
            .contains("CompleteInput"));
        assert_eq!(
            unsafe { dps_pwsh_v2_reset_input(input_handle, &mut call_result) },
            Status::Success.value()
        );
        add_v2_input_value(input_handle, 4, &3_i64.to_le_bytes(), &mut call_result);
        add_v2_input_value(input_handle, 4, &4_i64.to_le_bytes(), &mut call_result);
        assert_eq!(
            unsafe { dps_pwsh_v2_complete_input(input_handle, &mut call_result) },
            Status::Success.value()
        );
        let input_result = v2_invoke(input_handle, &mut call_result);
        assert_eq!(
            v2_result_output(input_result, &mut call_result),
            vec!["6".to_owned(), "8".to_owned()]
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_result_release(input_result, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(input_handle, &mut call_result) },
            Status::Success.value()
        );

        let backpressure_handle = v2_create_session(&mut call_result);
        for _ in 0..64 {
            add_v2_input_value(backpressure_handle, 4, &1_i64.to_le_bytes(), &mut call_result);
        }
        let extra_payload = 1_i64.to_le_bytes();
        let value = DataValue {
            size: std::mem::size_of::<DataValue>() as u32,
            kind: 4,
            flags: 0,
            _reserved: 0,
            data: extra_payload.as_ptr(),
            data_len: 8,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_add_input_value(backpressure_handle, &value, &mut call_result) },
            Status::Backpressure.value()
        );
        assert_eq!(call_result.status, Status::Backpressure.value());
        assert_eq!(
            unsafe { dps_pwsh_v2_reset_input(backpressure_handle, &mut call_result) },
            Status::Success.value()
        );
        assert_eq!(
            unsafe { dps_pwsh_v2_release(backpressure_handle, &mut call_result) },
            Status::Success.value()
        );

        let rejection_handle = v2_create_session(&mut call_result);
        let unsupported = DataValue {
            size: std::mem::size_of::<DataValue>() as u32,
            kind: u32::MAX,
            flags: 0,
            _reserved: 0,
            data: std::ptr::null(),
            data_len: 0,
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_add_argument_value(rejection_handle, &unsupported, &mut call_result) },
            Status::UnsupportedValue.value()
        );
        assert_eq!(call_result.status, Status::UnsupportedValue.value());
        assert_eq!(
            unsafe { dps_pwsh_v2_release(rejection_handle, &mut call_result) },
            Status::Success.value()
        );
    }

    fn v2_call_result(diagnostic: &mut [u8]) -> CallResult {
        CallResult {
            size: std::mem::size_of::<CallResult>() as u32,
            status: 0,
            flags: 0,
            _reserved: 0,
            diagnostic: diagnostic.as_mut_ptr(),
            diagnostic_capacity: diagnostic.len(),
            diagnostic_required: 0,
            diagnostic_written: 0,
        }
    }

    fn v2_call_result_without_diagnostic() -> CallResult {
        CallResult {
            size: std::mem::size_of::<CallResult>() as u32,
            status: 0,
            flags: 0,
            _reserved: 0,
            diagnostic: std::ptr::null_mut(),
            diagnostic_capacity: 0,
            diagnostic_required: 0,
            diagnostic_written: 0,
        }
    }

    fn initialize_v2_trusted(payload: &str, call_result: &mut CallResult) {
        let (manifest_path, manifest_sha256) = payload::create_test_manifest(&PathBuf::from(payload));
        let manifest_path = manifest_path.to_str().unwrap();
        assert_eq!(
            unsafe {
                dps_pwsh_v2_initialize_payload(
                    &PayloadActivation {
                        size: std::mem::size_of::<PayloadActivation>() as u32,
                        trust_policy: 0,
                        flags: 0,
                        _reserved: 0,
                        payload_path: Utf8Span {
                            data: payload.as_ptr(),
                            len: payload.len(),
                        },
                        manifest_path: Utf8Span {
                            data: manifest_path.as_ptr(),
                            len: manifest_path.len(),
                        },
                        manifest_sha256: Utf8Span {
                            data: manifest_sha256.as_ptr(),
                            len: manifest_sha256.len(),
                        },
                    },
                    call_result,
                )
            },
            Status::Success.value()
        );
        assert_eq!(call_result.status, Status::Success.value());
    }

    fn v2_create_session(call_result: &mut CallResult) -> u64 {
        let mut handle = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_create(&mut handle, call_result) },
            Status::Success.value()
        );
        handle
    }

    fn add_v2_parameter_value(handle: u64, name: &str, kind: u32, payload: &[u8], call_result: &mut CallResult) {
        let value = DataValue {
            size: std::mem::size_of::<DataValue>() as u32,
            kind,
            flags: 0,
            _reserved: 0,
            data: payload.as_ptr(),
            data_len: payload.len(),
        };
        assert_eq!(
            unsafe {
                dps_pwsh_v2_add_parameter_value(
                    handle,
                    Utf8Span {
                        data: name.as_ptr(),
                        len: name.len(),
                    },
                    &value,
                    call_result,
                )
            },
            Status::Success.value()
        );
    }

    fn add_v2_input_value(handle: u64, kind: u32, payload: &[u8], call_result: &mut CallResult) {
        let value = DataValue {
            size: std::mem::size_of::<DataValue>() as u32,
            kind,
            flags: 0,
            _reserved: 0,
            data: payload.as_ptr(),
            data_len: payload.len(),
        };
        assert_eq!(
            unsafe { dps_pwsh_v2_add_input_value(handle, &value, call_result) },
            Status::Success.value()
        );
    }

    fn v2_invoke(handle: u64, call_result: &mut CallResult) -> u64 {
        let mut result_handle = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_invoke(handle, &mut result_handle, call_result) },
            Status::Success.value()
        );
        result_handle
    }

    fn v2_result_output(result_handle: u64, call_result: &mut CallResult) -> Vec<String> {
        let mut count = 0;
        let mut flags = 0;
        assert_eq!(
            unsafe { dps_pwsh_v2_result_get_stream_info(result_handle, 0, &mut count, &mut flags, call_result) },
            Status::Success.value()
        );
        let mut values = Vec::with_capacity(count as usize);
        for index in 0..count {
            let mut required = 0;
            let status = unsafe {
                dps_pwsh_v2_result_copy_stream_record_field_utf8(
                    result_handle,
                    0,
                    index,
                    0,
                    std::ptr::null_mut(),
                    0,
                    &mut required,
                    call_result,
                )
            };
            assert!(status == Status::Success.value() || status == Status::BufferTooSmall.value());
            let mut value = vec![0_u8; required];
            assert_eq!(
                unsafe {
                    dps_pwsh_v2_result_copy_stream_record_field_utf8(
                        result_handle,
                        0,
                        index,
                        0,
                        value.as_mut_ptr(),
                        value.len(),
                        &mut required,
                        call_result,
                    )
                },
                Status::Success.value()
            );
            values.push(String::from_utf8(value).unwrap());
        }
        values
    }

    fn property_bag_payload(entries: &[(&str, u32, &[u8])]) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(&(entries.len() as u32).to_le_bytes());
        for (name, kind, value) in entries {
            payload.extend_from_slice(&(name.len() as u32).to_le_bytes());
            payload.extend_from_slice(name.as_bytes());
            payload.extend_from_slice(&kind.to_le_bytes());
            payload.extend_from_slice(&(value.len() as u32).to_le_bytes());
            payload.extend_from_slice(value);
        }
        payload
    }

    fn array_payload(entries: &[(u32, &[u8])]) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(&(entries.len() as u32).to_le_bytes());
        for (kind, value) in entries {
            payload.extend_from_slice(&kind.to_le_bytes());
            payload.extend_from_slice(&(value.len() as u32).to_le_bytes());
            payload.extend_from_slice(value);
        }
        payload
    }

    fn create_session() -> u64 {
        let mut handle = 0;
        assert_eq!(unsafe { dps_pwsh_create(&mut handle) }, Status::Success.value());
        handle
    }

    fn invoke_output(handle: u64) -> String {
        let mut required = 0;
        let status = unsafe { dps_pwsh_invoke_utf8(handle, std::ptr::null_mut(), 0, &mut required) };
        if status == Status::Success.value() {
            assert_eq!(required, 0);
            return String::new();
        }
        assert_eq!(status, Status::BufferTooSmall.value());

        let mut output = vec![0; required];
        assert_eq!(
            unsafe { dps_pwsh_invoke_utf8(handle, output.as_mut_ptr(), output.len(), &mut required) },
            Status::Success.value()
        );
        String::from_utf8(output).unwrap()
    }

    fn invocation_error_count(handle: u64) -> u32 {
        let mut count = 0;
        assert_eq!(
            unsafe { dps_pwsh_get_invocation_error_count(handle, &mut count) },
            Status::Success.value()
        );
        count
    }

    fn invocation_error_field(handle: u64, error_index: u32, field: u32) -> String {
        let mut required = 0;
        let status = unsafe {
            dps_pwsh_copy_invocation_error_field_utf8(
                handle,
                error_index,
                field,
                std::ptr::null_mut(),
                0,
                &mut required,
            )
        };
        assert!(status == Status::Success.value() || status == Status::BufferTooSmall.value());

        let mut value = vec![0; required];
        assert_eq!(
            unsafe {
                dps_pwsh_copy_invocation_error_field_utf8(
                    handle,
                    error_index,
                    field,
                    value.as_mut_ptr(),
                    value.len(),
                    &mut required,
                )
            },
            Status::Success.value()
        );
        String::from_utf8(value).unwrap()
    }
}
