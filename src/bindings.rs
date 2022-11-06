#![allow(dead_code)]

use crate::delegate_loader::{AssemblyDelegateLoader, MethodWithUnknownSignature};
use crate::error::Error;
use crate::pdcstr;
use crate::pdcstring::{PdCStr, PdCString};
use crate::loader::get_assembly_delegate_loader;
use std::ffi::{CStr,CString};

pub type PowerShellHandle = *mut libc::c_void;

pub type FnPowerShellCreate = unsafe extern "system" fn() -> PowerShellHandle;

pub type FnPowerShellAddArgumentString =
    unsafe extern "system" fn(handle: PowerShellHandle, argument: *const libc::c_char);

pub type FnPowerShellAddParameterString =
    unsafe extern "system" fn(handle: PowerShellHandle, name: *const libc::c_char, value: *const libc::c_char);

pub type FnPowerShellAddParameterInt =
    unsafe extern "system" fn(handle: PowerShellHandle, name: *const libc::c_char, value: i32);

pub type FnPowerShellAddParameterLong =
    unsafe extern "system" fn(handle: PowerShellHandle, name: *const libc::c_char, value: i64);

pub type FnPowerShellAddCommand =
    unsafe extern "system" fn(handle: PowerShellHandle, command: *const libc::c_char);

pub type FnPowerShellAddScript =
    unsafe extern "system" fn(handle: PowerShellHandle, script: *const libc::c_char);

pub type FnPowerShellAddStatement =
    unsafe extern "system" fn(handle: PowerShellHandle) -> PowerShellHandle;

pub type FnPowerShellInvoke = unsafe extern "system" fn(handle: PowerShellHandle);

pub type FnPowerShellExportVariable =
    unsafe extern "system" fn(handle: PowerShellHandle, name: *const libc::c_char) -> *const libc::c_char;

struct Bindings {
    create_fn: FnPowerShellCreate,
    add_argument_string_fn: FnPowerShellAddArgumentString,
    add_parameter_string_fn: FnPowerShellAddParameterString,
    add_parameter_int_fn: FnPowerShellAddParameterInt,
    add_parameter_long_fn: FnPowerShellAddParameterLong,
    add_command_fn: FnPowerShellAddCommand,
    add_script_fn: FnPowerShellAddScript,
    add_statement_fn: FnPowerShellAddStatement,
    invoke_fn: FnPowerShellInvoke,
    export_variable_fn: FnPowerShellExportVariable
}

impl Bindings {
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
            create_fn: {
                let create_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_Create"),
                )?;
                unsafe { std::mem::transmute(create_fn) }
            },
            add_argument_string_fn: {
                let add_argument_string_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddArgument_String"),
                )?;
                unsafe { std::mem::transmute(add_argument_string_fn) }
            },
            add_parameter_string_fn: {
                let add_parameter_string_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddParameter_String"),
                )?;
                unsafe { std::mem::transmute(add_parameter_string_fn) }
            },
            add_parameter_int_fn: {
                let add_parameter_int_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddParameter_Int"),
                )?;
                unsafe { std::mem::transmute(add_parameter_int_fn) }
            },
            add_parameter_long_fn: {
                let add_parameter_long_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddParameter_Long"),
                )?;
                unsafe { std::mem::transmute(add_parameter_long_fn) }
            },
            add_command_fn: {
                let add_command_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddCommand"),
                )?;
                unsafe { std::mem::transmute(add_command_fn) }
            },
            add_script_fn: {
                let add_script_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddScript"),
                )?;
                unsafe { std::mem::transmute(add_script_fn) }
            },
            add_statement_fn: {
                let add_statement_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_AddStatement"),
                )?;
                unsafe { std::mem::transmute(add_statement_fn) }
            },
            invoke_fn: {
                let invoke_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_Invoke"),
                )?;
                unsafe { std::mem::transmute(invoke_fn) }
            },
            export_variable_fn: {
                let export_variable_fn = get_function_pointer(
                    fnloader,
                    pdcstr!("NativeHost.Bindings, Bindings"),
                    pdcstr!("PowerShell_ExportVariable"),
                )?;
                unsafe { std::mem::transmute(export_variable_fn) }
            },
        };
        Ok(pwsh)
    }
}

pub struct PowerShell {
    inner: Bindings,
    handle: PowerShellHandle,
}

impl PowerShell {
    pub fn new() -> Option<Self> {
        let bindings = Bindings::new().ok()?;
        let handle = unsafe { (bindings.create_fn)() };
        Some(Self {
            inner: bindings,
            handle: handle,
        })
    }

    pub fn add_argument_string(&self, argument: &str) {
        let argument_cstr = CString::new(argument).unwrap();
        unsafe { (self.inner.add_argument_string_fn)(self.handle, argument_cstr.as_ptr()); }
    }

    pub fn add_parameter_string(&self, name: &str, value: &str) {
        let name_cstr = CString::new(name).unwrap();
        let value_cstr = CString::new(value).unwrap();
        unsafe { (self.inner.add_parameter_string_fn)(self.handle, name_cstr.as_ptr(), value_cstr.as_ptr()); }
    }

    pub fn add_parameter_int(&self, name: &str, value: i32) {
        let name_cstr = CString::new(name).unwrap();
        unsafe { (self.inner.add_parameter_int_fn)(self.handle, name_cstr.as_ptr(), value); }
    }

    pub fn add_parameter_long(&self, name: &str, value: i64) {
        let name_cstr = CString::new(name).unwrap();
        unsafe { (self.inner.add_parameter_long_fn)(self.handle, name_cstr.as_ptr(), value); }
    }

    pub fn add_command(&self, command: &str) {
        let command_cstr = CString::new(command).unwrap();
        unsafe { (self.inner.add_command_fn)(self.handle, command_cstr.as_ptr()); }
    }

    pub fn add_script(&self, script: &str) {
        let script_cstr = CString::new(script).unwrap();
        unsafe { (self.inner.add_script_fn)(self.handle, script_cstr.as_ptr()); }
    }

    pub fn add_statement(&self) {
        unsafe { (self.inner.add_statement_fn)(self.handle); }
    }

    pub fn invoke(&self) {
        unsafe { (self.inner.invoke_fn)(self.handle); }
    }

    pub fn export_variable(&self, name: &str) -> String {
        unsafe {
            let name_cstr = CString::new(name).unwrap();
            let cstr_ptr = (self.inner.export_variable_fn)(self.handle, name_cstr.as_ptr());
            let cstr = CStr::from_ptr(cstr_ptr);
            String::from_utf8_lossy(cstr.to_bytes()).to_string()
        }
    }
}
