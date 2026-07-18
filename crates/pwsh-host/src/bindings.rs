#![allow(dead_code)]

#[allow(clippy::missing_transmute_annotations)]
mod bindings_generated;
mod ffi;

use std::ffi::{CStr, CString};
use std::path::Path;
use std::sync::Arc;

pub(crate) use self::bindings_generated::Bindings;
use self::bindings_generated::PowerShellHandle;
pub(crate) use self::ffi::FfiBindings;
pub use self::ffi::{
    FfiBindingError, FfiInvocationResult, FfiPowerShell, FfiPowerShellSession, FfiSessionEvent, FfiSessionSnapshot,
    FfiSnapshotValue,
};
use crate::loader::HostedRuntime;

pub struct PowerShell {
    _runtime: Option<Arc<HostedRuntime>>,
    inner: Bindings,
    handle: PowerShellHandle,
}

impl PowerShell {
    pub fn new() -> Option<Self> {
        let bindings = Bindings::new().ok()?;
        let handle = unsafe { (bindings.create_fn)() };
        Some(Self {
            _runtime: None,
            inner: bindings,
            handle,
        })
    }

    // Arc retains the non-thread-safe hosted runtime for every session; it does not make it Send.
    #[allow(clippy::arc_with_non_send_sync)]
    pub fn new_for_pwsh_dir(pwsh_dir: impl AsRef<Path>) -> Result<Self, Box<dyn std::error::Error>> {
        let runtime = Arc::new(HostedRuntime::new_for_pwsh_dir(pwsh_dir)?);
        Self::new_for_runtime(runtime)
    }

    pub fn new_for_runtime(runtime: Arc<HostedRuntime>) -> Result<Self, Box<dyn std::error::Error>> {
        let bindings = runtime.bindings();
        let handle = unsafe { (bindings.create_fn)() };
        if handle.is_null() {
            return Err("managed PowerShell creation returned a null handle".into());
        }
        Ok(Self {
            _runtime: Some(runtime),
            inner: bindings,
            handle,
        })
    }

    pub fn add_argument_string(&self, argument: &str) {
        let argument_cstr = CString::new(argument).unwrap();
        unsafe {
            (self.inner.add_argument_string_fn)(self.handle, argument_cstr.as_ptr());
        }
    }

    pub fn add_parameter_string(&self, name: &str, value: &str) {
        let name_cstr = CString::new(name).unwrap();
        let value_cstr = CString::new(value).unwrap();
        unsafe {
            (self.inner.add_parameter_string_fn)(self.handle, name_cstr.as_ptr(), value_cstr.as_ptr());
        }
    }

    pub fn add_parameter_int(&self, name: &str, value: i32) {
        let name_cstr = CString::new(name).unwrap();
        unsafe {
            (self.inner.add_parameter_int_fn)(self.handle, name_cstr.as_ptr(), value);
        }
    }

    pub fn add_parameter_long(&self, name: &str, value: i64) {
        let name_cstr = CString::new(name).unwrap();
        unsafe {
            (self.inner.add_parameter_long_fn)(self.handle, name_cstr.as_ptr(), value);
        }
    }

    pub fn add_command(&self, command: &str) {
        let command_cstr = CString::new(command).unwrap();
        unsafe {
            (self.inner.add_command_fn)(self.handle, command_cstr.as_ptr());
        }
    }

    pub fn add_script(&self, script: &str) {
        let script_cstr = CString::new(script).unwrap();
        unsafe {
            (self.inner.add_script_fn)(self.handle, script_cstr.as_ptr());
        }
    }

    pub fn add_statement(&self) {
        unsafe {
            (self.inner.add_statement_fn)(self.handle);
        }
    }

    pub fn invoke(&self, clear: bool) {
        unsafe {
            (self.inner.invoke_fn)(self.handle);
            if clear {
                (self.inner.clear_fn)(self.handle);
            }
        }
    }

    pub fn invoke_to_string(&self) -> Result<String, i32> {
        let mut required_len = 0;
        let status = unsafe { (self.inner.invoke_to_utf8_fn)(self.handle, std::ptr::null_mut(), 0, &mut required_len) };
        if status != 0 && status != 1 {
            return Err(status);
        }

        let mut output = vec![0; required_len as usize];
        let status = unsafe {
            (self.inner.invoke_to_utf8_fn)(self.handle, output.as_mut_ptr(), required_len, &mut required_len)
        };
        if status != 0 {
            return Err(status);
        }

        String::from_utf8(output).map_err(|_| -3)
    }

    pub fn invocation_error_count(&self) -> Result<usize, i32> {
        let count = unsafe { (self.inner.get_invocation_error_count_fn)(self.handle) };
        if count < 0 {
            return Err(count);
        }

        Ok(count as usize)
    }

    pub fn invocation_error_field(&self, error_index: i32, field: i32) -> Result<String, i32> {
        let mut required_len = 0;
        let status = unsafe {
            (self.inner.copy_invocation_error_field_to_utf8_fn)(
                self.handle,
                error_index,
                field,
                std::ptr::null_mut(),
                0,
                &mut required_len,
            )
        };
        if status != 0 && status != 1 {
            return Err(status);
        }

        let mut value = vec![0; required_len as usize];
        let status = unsafe {
            (self.inner.copy_invocation_error_field_to_utf8_fn)(
                self.handle,
                error_index,
                field,
                value.as_mut_ptr(),
                required_len,
                &mut required_len,
            )
        };
        if status != 0 {
            return Err(status);
        }

        String::from_utf8(value).map_err(|_| -3)
    }

    pub fn clear(&self) {
        unsafe {
            (self.inner.clear_fn)(self.handle);
        }
    }

    pub fn stop(&self) {
        unsafe {
            (self.inner.power_shell_auto_stop_no_args_fn)(self.handle);
        }
    }

    pub fn export_to_xml(&self, name: &str) -> String {
        unsafe {
            let name_cstr = CString::new(name).unwrap();
            let cstr_ptr = (self.inner.export_to_xml_fn)(self.handle, name_cstr.as_ptr());
            let cstr = CStr::from_ptr(cstr_ptr);
            let rstr = String::from_utf8_lossy(cstr.to_bytes()).to_string();
            self.marshal_free_co_task_mem(cstr_ptr as *mut libc::c_void);
            rstr
        }
    }

    pub fn export_to_json(&self, name: &str) -> String {
        unsafe {
            let name_cstr = CString::new(name).unwrap();
            let cstr_ptr = (self.inner.export_to_json_fn)(self.handle, name_cstr.as_ptr());
            let cstr = CStr::from_ptr(cstr_ptr);
            let rstr = String::from_utf8_lossy(cstr.to_bytes()).to_string();
            self.marshal_free_co_task_mem(cstr_ptr as *mut libc::c_void);
            rstr
        }
    }

    pub fn export_to_string(&self, name: &str) -> String {
        unsafe {
            let name_cstr = CString::new(name).unwrap();
            let cstr_ptr = (self.inner.export_to_string_fn)(self.handle, name_cstr.as_ptr());
            let cstr = CStr::from_ptr(cstr_ptr);
            let rstr = String::from_utf8_lossy(cstr.to_bytes()).to_string();
            self.marshal_free_co_task_mem(cstr_ptr as *mut libc::c_void);
            rstr
        }
    }

    pub fn invoke_member_json(&self, member_name: &str, arguments_json: &str) -> String {
        unsafe {
            let member_name_cstr = CString::new(member_name).unwrap();
            let arguments_json_cstr = CString::new(arguments_json).unwrap();
            let cstr_ptr = (self.inner.invoke_member_json_fn)(
                self.handle,
                member_name_cstr.as_ptr(),
                arguments_json_cstr.as_ptr(),
            );
            let cstr = CStr::from_ptr(cstr_ptr);
            let rstr = String::from_utf8_lossy(cstr.to_bytes()).to_string();
            self.marshal_free_co_task_mem(cstr_ptr as *mut libc::c_void);
            rstr
        }
    }

    pub fn get_property_json(&self, property_name: &str) -> String {
        unsafe {
            let property_name_cstr = CString::new(property_name).unwrap();
            let cstr_ptr = (self.inner.get_property_json_fn)(self.handle, property_name_cstr.as_ptr());
            let cstr = CStr::from_ptr(cstr_ptr);
            let rstr = String::from_utf8_lossy(cstr.to_bytes()).to_string();
            self.marshal_free_co_task_mem(cstr_ptr as *mut libc::c_void);
            rstr
        }
    }

    pub fn set_property_json(&self, property_name: &str, value_json: &str) -> String {
        unsafe {
            let property_name_cstr = CString::new(property_name).unwrap();
            let value_json_cstr = CString::new(value_json).unwrap();
            let cstr_ptr =
                (self.inner.set_property_json_fn)(self.handle, property_name_cstr.as_ptr(), value_json_cstr.as_ptr());
            let cstr = CStr::from_ptr(cstr_ptr);
            let rstr = String::from_utf8_lossy(cstr.to_bytes()).to_string();
            self.marshal_free_co_task_mem(cstr_ptr as *mut libc::c_void);
            rstr
        }
    }

    pub fn invoke_static_member_json(&self, member_name: &str, arguments_json: &str) -> String {
        unsafe {
            let member_name_cstr = CString::new(member_name).unwrap();
            let arguments_json_cstr = CString::new(arguments_json).unwrap();
            let cstr_ptr =
                (self.inner.invoke_static_member_json_fn)(member_name_cstr.as_ptr(), arguments_json_cstr.as_ptr());
            let cstr = CStr::from_ptr(cstr_ptr);
            let rstr = String::from_utf8_lossy(cstr.to_bytes()).to_string();
            self.marshal_free_co_task_mem(cstr_ptr as *mut libc::c_void);
            rstr
        }
    }

    /// # Safety
    ///
    /// `handle` must be a valid GC handle previously returned by the bindings layer,
    /// and it must not be used again after this call returns.
    pub unsafe fn free_handle(&self, handle: PowerShellHandle) {
        unsafe {
            (self.inner.gc_handle_free_fn)(handle);
        }
    }

    fn marshal_free_co_task_mem(&self, ptr: *mut libc::c_void) {
        unsafe {
            (self.inner.marshal_free_co_task_mem_fn)(ptr);
        }
    }
}

impl Drop for PowerShell {
    fn drop(&mut self) {
        unsafe {
            (self.inner.gc_handle_free_fn)(self.handle);
        }
    }
}
