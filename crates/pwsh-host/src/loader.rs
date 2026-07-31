use std::convert::TryFrom;
use std::ffi::c_void;
use std::path::{Path, PathBuf};

use crate::bindings::{Bindings, FfiBindingError, FfiBindings, FfiPayloadRuntimeDiagnostics};
use crate::context::HostfxrContext;
use crate::delegate_loader::AssemblyDelegateLoader;
use crate::error::Error;
use crate::host_detect::pwsh_host_detect;
use crate::host_exit_code::HostExitCode;
use crate::host_exit_code::KnownHostExitCode;
use crate::hostfxr::{load_hostfxr_from_pwsh_dir, Hostfxr};
use crate::pdcstr;
use crate::pdcstring::PdCString;
use crate::pwsh_cli::configure_startup_hooks_for_context;

pub const BINDINGS_DLL: &[u8] =
    include_bytes!("../../../dotnet/bindings/bin/Release/net8.0/Devolutions.PowerShell.SDK.Bindings.dll");

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LiveObjectContractPack {
    pub payload_adapter_assembly_path: PathBuf,
    pub payload_adapter_type_name: String,
}

pub struct HostedRuntime {
    bindings: Bindings,
    ffi_bindings: FfiBindings,
    host_context: crate::context::HostfxrHandle,
    hostfxr: crate::hostfxr::Hostfxr,
    pwsh_dir: PathBuf,
}

impl HostedRuntime {
    pub fn new_for_pwsh_dir(pwsh_dir: impl AsRef<Path>) -> Result<Self, Box<dyn std::error::Error>> {
        Self::new_for_pwsh_dir_with_contract_packs(pwsh_dir, &[])
    }

    pub fn new_for_pwsh_dir_with_contract_packs(
        pwsh_dir: impl AsRef<Path>,
        contract_packs: &[LiveObjectContractPack],
    ) -> Result<Self, Box<dyn std::error::Error>> {
        let pwsh_dir = validate_pwsh_payload(pwsh_dir.as_ref())?;
        let hostfxr = load_hostfxr_from_pwsh_dir(&pwsh_dir)?;
        let (host_context, bindings, ffi_bindings) =
            match hostfxr.initialize_for_dotnet_command_line(pwsh_dir.join("pwsh.dll")) {
                Ok(context) => load_bindings_from_context(&hostfxr, context, &pwsh_dir, contract_packs)?,
                Err(error) => {
                    let should_fallback = matches!(
                        error.downcast_ref::<Error>(),
                        Some(Error::Hostfxr(crate::host_exit_code::HostExitCode::Known(
                            KnownHostExitCode::InvalidArgFailure
                        )))
                    );

                    if !should_fallback {
                        return Err(error);
                    }

                    let runtime_config = PdCString::from_os_str(pwsh_dir.join("pwsh.runtimeconfig.json"))?;
                    let context = hostfxr.initialize_for_runtime_config_path(&runtime_config)?;
                    load_bindings_from_context(&hostfxr, context, &pwsh_dir, contract_packs)?
                }
            };

        Ok(Self {
            bindings,
            ffi_bindings,
            host_context,
            hostfxr,
            pwsh_dir,
        })
    }

    pub fn bindings(&self) -> Bindings {
        self.bindings
    }

    pub(crate) fn ffi_bindings(&self) -> FfiBindings {
        self.ffi_bindings
    }

    pub fn create_live_object_probe(&self, initial_count: i64) -> Result<*mut c_void, FfiBindingError> {
        self.ffi_bindings.create_live_object_probe(initial_count)
    }

    pub fn release_live_object_probe(&self, com_object: *mut c_void) -> Result<(), FfiBindingError> {
        self.ffi_bindings.release_live_object_probe(com_object)
    }

    pub fn unregister_live_object_probe(&self, com_object: *mut c_void) -> Result<(), FfiBindingError> {
        self.ffi_bindings.unregister_live_object_probe(com_object)
    }

    pub fn pwsh_dir(&self) -> &Path {
        &self.pwsh_dir
    }

    pub fn runtime_diagnostics(&self) -> Result<FfiPayloadRuntimeDiagnostics, FfiBindingError> {
        self.ffi_bindings.runtime_diagnostics()
    }
}

impl Drop for HostedRuntime {
    fn drop(&mut self) {
        let _ = self.hostfxr.close(self.host_context.as_raw());
    }
}

pub fn get_assembly_delegate_loader_for_pwsh_dir(
    pwsh_path: impl AsRef<std::path::Path>,
) -> Result<AssemblyDelegateLoader<PdCString>, Box<dyn std::error::Error>> {
    let pwsh_path = pwsh_path.as_ref();

    let hostfxr = load_hostfxr_from_pwsh_dir(pwsh_path)?;
    let fn_loader = match hostfxr.initialize_for_dotnet_command_line(pwsh_path.join("pwsh.dll")) {
        Ok(ctx) => get_assembly_delegate_loader_from_context(&ctx, pwsh_path)?,
        Err(error) => {
            let should_fallback = matches!(
                error.downcast_ref::<Error>(),
                Some(Error::Hostfxr(crate::host_exit_code::HostExitCode::Known(
                    KnownHostExitCode::InvalidArgFailure
                )))
            );

            if !should_fallback {
                return Err(error);
            }

            let runtime_config = PdCString::from_os_str(pwsh_path.join("pwsh.runtimeconfig.json"))?;
            let ctx = hostfxr.initialize_for_runtime_config_path(&runtime_config)?;
            get_assembly_delegate_loader_from_context(&ctx, pwsh_path)?
        }
    };

    load_bindings(&fn_loader)?;

    Ok(fn_loader)
}

fn load_bindings(fn_loader: &AssemblyDelegateLoader<PdCString>) -> Result<(), Box<dyn std::error::Error>> {
    load_assembly_from_native_memory(fn_loader, BINDINGS_DLL)
}

fn load_assembly_from_native_memory(
    fn_loader: &AssemblyDelegateLoader<PdCString>,
    assembly: &[u8],
) -> Result<(), Box<dyn std::error::Error>> {
    if assembly.is_empty() || assembly.len() > u32::MAX as usize {
        return Err("managed assembly payload is invalid".into());
    }

    let load_assembly_from_native_memory = fn_loader.get_function_pointer_for_unmanaged_callers_only_method(
        pdcstr!("System.Management.Automation.PowerShellUnsafeAssemblyLoad, System.Management.Automation"),
        pdcstr!("LoadAssemblyFromNativeMemory"),
    )?;

    let load_assembly_from_native_memory: extern "system" fn(bytes: *const libc::c_uchar, size: libc::c_uint) -> i32 =
        unsafe { std::mem::transmute(load_assembly_from_native_memory) };
    let result = (load_assembly_from_native_memory)(assembly.as_ptr(), assembly.len() as u32);
    HostExitCode::from(result).into_result()?;
    Ok(())
}

fn load_live_object_contract_pack(
    fn_loader: &AssemblyDelegateLoader<PdCString>,
    pack: &LiveObjectContractPack,
) -> Result<*mut c_void, Box<dyn std::error::Error>> {
    let assembly = std::fs::read(&pack.payload_adapter_assembly_path)?;
    load_assembly_from_native_memory(fn_loader, &assembly)?;

    let type_name = PdCString::try_from(pack.payload_adapter_type_name.as_str())?;
    let get_pack_api = fn_loader
        .get_function_pointer_for_unmanaged_callers_only_method(type_name, pdcstr!("GetLiveObjectContractPackV1"))?;
    let get_pack_api: unsafe extern "system" fn() -> *mut c_void = unsafe { std::mem::transmute(get_pack_api) };
    Ok(unsafe { get_pack_api() })
}

fn load_live_object_contract_packs(
    fn_loader: &AssemblyDelegateLoader<PdCString>,
    ffi_bindings: &FfiBindings,
    contract_packs: &[LiveObjectContractPack],
) -> Result<(), Box<dyn std::error::Error>> {
    let mut pack_apis = Vec::with_capacity(contract_packs.len());
    for pack in contract_packs {
        pack_apis.push(load_live_object_contract_pack(fn_loader, pack)?);
    }
    if pack_apis.is_empty() {
        return Ok(());
    }

    unsafe { ffi_bindings.register_live_object_contract_packs(&pack_apis) }
        .map_err(|error| Box::new(error) as Box<dyn std::error::Error>)
}

fn load_bindings_from_context<I>(
    hostfxr: &Hostfxr,
    context: HostfxrContext<'_, I>,
    pwsh_dir: &Path,
    contract_packs: &[LiveObjectContractPack],
) -> Result<(crate::context::HostfxrHandle, Bindings, FfiBindings), Box<dyn std::error::Error>> {
    let host_context = context.handle();
    let result = (|| {
        let fn_loader = get_assembly_delegate_loader_from_context(&context, pwsh_dir)?;
        load_bindings(&fn_loader)?;
        let bindings =
            Bindings::new_with_loader(&fn_loader).map_err(|error| Box::new(error) as Box<dyn std::error::Error>)?;
        let ffi_bindings =
            FfiBindings::new_with_loader(&fn_loader).map_err(|error| Box::new(error) as Box<dyn std::error::Error>)?;
        load_live_object_contract_packs(&fn_loader, &ffi_bindings, contract_packs)?;
        Ok::<_, Box<dyn std::error::Error>>((bindings, ffi_bindings))
    })();
    if result.is_err() {
        let _ = hostfxr.close(host_context.as_raw());
    }
    result.map(|(bindings, ffi_bindings)| (host_context, bindings, ffi_bindings))
}

fn get_assembly_delegate_loader_from_context<I>(
    ctx: &HostfxrContext<'_, I>,
    pwsh_path: &std::path::Path,
) -> Result<AssemblyDelegateLoader<PdCString>, Box<dyn std::error::Error>> {
    configure_startup_hooks_for_context(ctx)?;
    let assembly_path = PdCString::from_os_str(pwsh_path.join("System.Management.Automation.dll").into_os_string())?;
    let fn_loader = ctx.get_delegate_loader_for_assembly(assembly_path)?;
    Ok(fn_loader)
}

fn validate_pwsh_payload(pwsh_dir: &Path) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let pwsh_dir = std::fs::canonicalize(pwsh_dir)?;
    if !pwsh_dir.is_dir() {
        return Err(format!(
            "PowerShell payload directory is not a directory: {}",
            pwsh_dir.display()
        )
        .into());
    }

    for required_file in &[
        "pwsh.dll",
        "pwsh.runtimeconfig.json",
        "System.Management.Automation.dll",
    ] {
        let path = pwsh_dir.join(required_file);
        if !path.is_file() {
            return Err(format!("PowerShell payload is missing required file: {}", path.display()).into());
        }
    }

    Ok(pwsh_dir)
}

pub fn get_assembly_delegate_loader() -> AssemblyDelegateLoader<PdCString> {
    let pwsh_path = pwsh_host_detect();
    assert!(pwsh_path.is_ok());
    let pwsh_path = pwsh_path.unwrap();

    get_assembly_delegate_loader_for_pwsh_dir(&pwsh_path).unwrap()
}
