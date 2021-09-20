use crate::delegate_loader::{AssemblyDelegateLoader, MethodWithUnknownSignature};
use crate::error::Error;
use crate::pdcstr;
use crate::pdcstring::{PdCStr, PdCString};
use std::ffi::CString;

#[allow(dead_code)]
pub type PowerShellHandle = *mut libc::c_void;

#[allow(dead_code)]
pub type FnPowerShellCreate = unsafe extern "system" fn() -> PowerShellHandle;

#[allow(dead_code)]
pub type FnPowerShellAddScript =
    unsafe extern "system" fn(handle: PowerShellHandle, script: *const libc::c_char);

#[allow(dead_code)]
pub type FnPowerShellInvoke = unsafe extern "system" fn(handle: PowerShellHandle);

#[allow(dead_code)]
pub struct IPowerShell {
    _create: FnPowerShellCreate,
    _add_script: FnPowerShellAddScript,
    _invoke: FnPowerShellInvoke,
}

impl IPowerShell {
    #[allow(dead_code)]
    pub fn new(fnloader: &AssemblyDelegateLoader<PdCString>) -> Result<Self, Error> {
        #[allow(dead_code)]
        fn get_function_pointer(
            fnloader: &AssemblyDelegateLoader<PdCString>,
            type_name: impl AsRef<PdCStr>,
            method_name: impl AsRef<PdCStr>,
        ) -> Result<MethodWithUnknownSignature, Error> {
            fnloader.get_function_pointer_for_unmanaged_callers_only_method(type_name, method_name)
        }
        Ok(Self {
            _create: {
                let create_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_Create"),
                )?;
                unsafe { std::mem::transmute(create_fn) }
            },
            _add_script: {
                let add_script_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddScript"),
                )?;
                unsafe { std::mem::transmute(add_script_fn) }
            },
            _invoke: {
                let invoke_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_Invoke"),
                )?;
                unsafe { std::mem::transmute(invoke_fn) }
            },
        })
    }

    #[allow(dead_code)]
    pub fn call_sdk(&self) {
        let handle = self.create();
        let mut script = CString::new("$TempPath = [System.IO.Path]::GetTempPath();").unwrap();
        self.addscript(handle, script);
        script = CString::new("Set-Content -Path $(Join-Path $TempPath pwsh-date.txt) -Value \"Microsoft.PowerShell.SDK: $(Get-Date)\";").unwrap();
        self.addscript(handle, script);
        script = CString::new("Invoke-Item $(Join-Path $TempPath pwsh-date.txt);").unwrap();
        self.addscript(handle, script);
        self.invoke(handle);
    }

    pub fn create(&self) -> PowerShellHandle {
        unsafe { (self._create)() }
    }

    pub fn addscript(&self, handle: PowerShellHandle, script: CString) {
        unsafe { (self._add_script)(handle, script.as_ptr()) }
    }

    #[allow(dead_code)]
    pub fn invoke(&self, handle: PowerShellHandle) {
        unsafe { (self._invoke)(handle) }
    }
}
