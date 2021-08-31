#[cfg(test)]
mod tests {
    use crate::host_detect::pwsh_host_detect;

    #[test]
    fn host_detect() {
        use crate::host_detect::EnvError;
        use std::ffi::OsString;

        assert!(pwsh_host_detect(Some(OsString::from(
            "C:\\Program Files\\PowerShell\\7-preview"
        )))
        .is_ok());
        assert!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.2"))).is_ok()
        );
        assert!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.3"))).is_ok()
        );
        assert!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.4"))).is_ok()
        );
        assert!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.5"))).is_ok()
        );
        assert!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.6"))).is_ok()
        );
        assert!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.7"))).is_ok()
        );
        assert!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.8"))).is_ok()
        );
        assert!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.9"))).is_ok()
        );
        assert!(
            pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7-preview"))).is_ok()
        );
        assert!(pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.2"))).is_ok());
        assert!(pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.3"))).is_ok());
        assert!(pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.4"))).is_ok());
        assert!(pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.5"))).is_ok());
        assert!(pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.6"))).is_ok());
        assert!(pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.7"))).is_ok());
        assert!(pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.8"))).is_ok());
        assert!(pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.9"))).is_ok());
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.0"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\7.1"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\6"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\6.0"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("C:\\Program Files\\PowerShell\\6.1"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from(
                "C:\\Program Files\\PowerShell\\6-preview"
            ))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.0"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/7.1"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/6"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/6.0"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/6.1"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("/opt/microsoft/powershell/6-preview"))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from(""))),
            Err(EnvError::Missing)
        );
        assert_eq!(
            pwsh_host_detect(Some(OsString::from("/path/to/nowhere"))),
            Err(EnvError::Missing)
        );
        assert_eq!(pwsh_host_detect(None), Err(EnvError::UndefOrUnset));
    }

    #[test]
    fn load_pwsh_sdk_invoke_api() {
        use crate::host_exit_code::HostExitCode;
        use crate::hostfxr::load_hostfxr;
        use crate::ipwsh::IPowerShell;
        use crate::pdcstr;
        use crate::pdcstring::PdCString;
        use std::env;

        let pwsh_path = pwsh_host_detect(env::var_os("PATH"));
        assert!(pwsh_path.is_ok());
        let pwsh_path = pwsh_path.unwrap();

        let hostfxr = load_hostfxr();
        assert!(hostfxr.is_ok());
        let hostfxr = hostfxr.unwrap();

        let ctx = hostfxr.initialize_for_dotnet_command_line(pwsh_path.join("pwsh.dll"));
        assert!(ctx.is_ok());
        let ctx = ctx.unwrap();

        let assembly_path = PdCString::from_os_str(
            pwsh_path
                .join("System.Management.Automation.dll")
                .into_os_string(),
        );
        assert!(assembly_path.is_ok());
        let assembly_path = assembly_path.unwrap();

        let fn_loader = ctx.get_delegate_loader_for_assembly(assembly_path);
        assert!(fn_loader.is_ok());
        let fn_loader = fn_loader.unwrap();

        let load_assembly_from_native_memory = fn_loader.get_function_pointer_for_unmanaged_callers_only_method(
            pdcstr!("System.Management.Automation.PowerShellUnsafeAssemblyLoad, System.Management.Automation"),
        pdcstr!("LoadAssemblyFromNativeMemory"));
        assert!(load_assembly_from_native_memory.is_ok());
        let load_assembly_from_native_memory = load_assembly_from_native_memory.unwrap();

        extern "C" {
            static bindings_size: libc::c_uint;
            static bindings_data: [libc::c_uchar; 1usize];
        }

        let load_assembly_from_native_memory: extern "C" fn(
            bytes: *const libc::c_uchar,
            size: libc::c_uint,
        ) -> i32 = unsafe { std::mem::transmute(load_assembly_from_native_memory) };
        let result = unsafe {
            (load_assembly_from_native_memory)(bindings_data.as_ptr(), bindings_size.clone())
        };
        HostExitCode::from(result).into_result().unwrap();

        let pwsh = IPowerShell::new(&fn_loader);
        assert!(pwsh.is_ok());

        let pwsh = pwsh.unwrap();
        pwsh.call_sdk();
    }
}
