use crate::delegate_loader::AssemblyDelegateLoader;
use crate::error::Error;
use crate::host_exit_code::KnownHostExitCode;
use crate::host_detect::pwsh_host_detect;
use crate::host_exit_code::HostExitCode;
use crate::hostfxr::load_hostfxr_from_pwsh_dir;
use crate::hostfxr::load_hostfxr;
use crate::pdcstr;
use crate::pdcstring::PdCString;
use crate::context::HostfxrContext;
use crate::pwsh_cli::configure_startup_hooks_for_context;

pub const BINDINGS_DLL: &[u8] =
    include_bytes!("../../../dotnet/bindings/bin/Release/net8.0/Devolutions.PowerShell.SDK.Bindings.dll");

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
                Some(Error::Hostfxr(crate::host_exit_code::HostExitCode::Known(KnownHostExitCode::InvalidArgFailure)))
            );

            if !should_fallback {
                return Err(error);
            }

            let runtime_config = PdCString::from_os_str(pwsh_path.join("pwsh.runtimeconfig.json"))?;
            let ctx = hostfxr.initialize_for_runtime_config_path(&runtime_config)?;
            get_assembly_delegate_loader_from_context(&ctx, pwsh_path)?
        }
    };

    let load_assembly_from_native_memory = fn_loader.get_function_pointer_for_unmanaged_callers_only_method(
        pdcstr!("System.Management.Automation.PowerShellUnsafeAssemblyLoad, System.Management.Automation"),
        pdcstr!("LoadAssemblyFromNativeMemory"),
    )?;

    let load_assembly_from_native_memory: extern "system" fn(bytes: *const libc::c_uchar, size: libc::c_uint) -> i32 =
        unsafe { std::mem::transmute(load_assembly_from_native_memory) };
    let result = (load_assembly_from_native_memory)(BINDINGS_DLL.as_ptr(), BINDINGS_DLL.len() as u32);
    HostExitCode::from(result).into_result()?;

    Ok(fn_loader)
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

pub fn get_assembly_delegate_loader() -> AssemblyDelegateLoader<PdCString> {
    let pwsh_path = pwsh_host_detect();
    assert!(pwsh_path.is_ok());
    let pwsh_path = pwsh_path.unwrap();

    let hostfxr = load_hostfxr();
    assert!(hostfxr.is_ok());
    let _hostfxr = hostfxr.unwrap();

    get_assembly_delegate_loader_for_pwsh_dir(&pwsh_path).unwrap()
}
