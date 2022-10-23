#![allow(dead_code)]

use crate::delegate_loader::{AssemblyDelegateLoader, MethodWithUnknownSignature};
use crate::error::Error;
use crate::pdcstr;
use crate::pdcstring::{PdCStr, PdCString};
use crate::loader::get_assembly_delegate_loader;
use std::ffi::CString;

pub type PowerShellHandle = *mut libc::c_void;

pub type FnPowerShellCreate = unsafe extern "system" fn() -> PowerShellHandle;

pub type FnPowerShellAddScript =
    unsafe extern "system" fn(handle: PowerShellHandle, script: *const libc::c_char);

pub type FnPowerShellAddStatement =
    unsafe extern "system" fn(handle: PowerShellHandle) -> PowerShellHandle;

pub type FnPowerShellInvoke = unsafe extern "system" fn(handle: PowerShellHandle);

pub struct IPowerShell {
    create: FnPowerShellCreate,
    add_script: FnPowerShellAddScript,
    add_statement: FnPowerShellAddStatement,
    invoke: FnPowerShellInvoke,
}

impl IPowerShell {
    pub fn new() -> Result<Self, Error> {
        let fn_loader = get_assembly_delegate_loader();
        Self::new_with_loader(&fn_loader)
    }

    pub fn new_with_loader(fnloader: &AssemblyDelegateLoader<PdCString>) -> Result<Self, Error> {
        fn get_function_pointer(
            fnloader: &AssemblyDelegateLoader<PdCString>,
            type_name: impl AsRef<PdCStr>,
            method_name: impl AsRef<PdCStr>,
        ) -> Result<MethodWithUnknownSignature, Error> {
            fnloader.get_function_pointer_for_unmanaged_callers_only_method(type_name, method_name)
        }
        let pwsh = Self {
            create: {
                let create_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_Create"),
                )?;
                unsafe { std::mem::transmute(create_fn) }
            },
            add_script: {
                let add_script_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddScript"),
                )?;
                unsafe { std::mem::transmute(add_script_fn) }
            },
            add_statement: {
                let add_statement_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddStatement"),
                )?;
                unsafe { std::mem::transmute(add_statement_fn) }
            },
            invoke: {
                let invoke_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_Invoke"),
                )?;
                unsafe { std::mem::transmute(invoke_fn) }
            },
        };
        Ok(pwsh)
    }

    pub fn create(&self) -> PowerShellHandle {
        unsafe { (self.create)() }
    }

    pub fn add_script(&self, handle: PowerShellHandle, script: CString) {
        unsafe { (self.add_script)(handle, script.as_ptr()) }
    }

    pub fn add_statement(&self, handle: PowerShellHandle) {
        unsafe { (self.add_statement)(handle); }
    }

    pub fn invoke(&self, handle: PowerShellHandle) {
        unsafe { (self.invoke)(handle) }
    }
}
