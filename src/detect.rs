#[cfg(test)]
mod tests {
    use crate::host_detect::pwsh_host_detect;

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

        let bindings_dll = include_bytes!("../dotnet/bin/Release/net6.0/Bindings.dll");

        let load_assembly_from_native_memory: extern "system" fn(
            bytes: *const libc::c_uchar,
            size: libc::c_uint,
        ) -> i32 = unsafe { std::mem::transmute(load_assembly_from_native_memory) };
        let result = (load_assembly_from_native_memory)(bindings_dll.as_ptr(), bindings_dll.len() as u32);
        HostExitCode::from(result).into_result().unwrap();

        let pwsh = IPowerShell::new(&fn_loader);
        assert!(pwsh.is_ok());

        let pwsh = pwsh.unwrap();
        pwsh.call_sdk();
    }
}
