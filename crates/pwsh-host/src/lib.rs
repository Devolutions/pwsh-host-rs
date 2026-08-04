mod bindings;
mod cli_xml;
mod context;
mod delegate_loader;
mod error;
mod host_detect;
mod host_exit_code;
mod hostfxr;
mod loader;
mod named_pipe_command;
mod pwsh_cli;
mod startup_hook;
mod tests;
mod time;

extern crate libc;
#[macro_use]
extern crate dlopen_derive;
extern crate dlopen;
#[macro_use]
extern crate quick_error;

/// Module for a platform dependent c-like string type.
#[macro_use]
mod pdcstring;

pub use bindings::{
    FfiBindingError, FfiBridgeContext, FfiInvocationResult, FfiLiveInvocation, FfiLiveObjectContractDescriptor,
    FfiLiveStreamBatch, FfiLiveStreamRecord, FfiObservedDiagnosticPage, FfiObservedDiagnosticRecord,
    FfiObservedInvocation, FfiPayloadRuntimeDiagnostics, FfiPowerShell, FfiPowerShellSession, FfiSessionEvent,
    FfiSessionSnapshot, FfiSnapshotValue, FfiTypedResultInvocation, FfiTypedResultPage, FfiTypedResultRecord,
    PowerShell,
};
pub use host_detect::find_pwsh_dir;
pub use loader::{get_assembly_delegate_loader_for_pwsh_dir, HostedRuntime, LiveObjectContractPack};
pub use named_pipe_command::{preprocess_named_pipe_command_args, NamedPipeCommandError};
pub use pwsh_cli::{run_pwsh_command_line, run_pwsh_command_line_for_pwsh_dir, run_pwsh_command_line_for_pwsh_exe};
pub use startup_hook::{MODULE_PATH_STRATEGY, STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR, STARTUP_HOOK_STRATEGY_ENV_VAR};
