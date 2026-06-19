use std::borrow::BorrowMut;
use std::ffi::OsStr;
use std::io;
use std::path::{Path, PathBuf};
use std::{env, ptr};

use dlopen::wrapper::{Container, WrapperApi};

use crate::context::{HostfxrContext, HostfxrHandle, InitializedForCommandLine, InitializedForRuntimeConfig};
use crate::host_detect::pwsh_host_detect;
use crate::host_exit_code::{HostExitCode, KnownHostExitCode};
use crate::pdcstring::{PdCStr, PdCString};

#[cfg(windows)]
#[allow(non_camel_case_types)]
pub type char_t = u16;
/// The char type used in nethost and hostfxr. Either u8 on unix systems or u16 on windows.
#[allow(non_camel_case_types)]
#[cfg(not(windows))]
pub type char_t = libc::c_char;

/// [`UnmanagedCallersOnlyAttribute`]: https://docs.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute
pub const UNMANAGED_CALLERS_ONLY_METHOD: *const char_t = usize::MAX as *const _;

#[repr(i32)]
#[allow(dead_code)]
pub enum HostfxrDelegateType {
    ComActivation,
    LoadInMemoryAssembly,
    WinrtActivation,
    ComRegister,
    ComUnregister,
    LoadAssemblyAndGetFunctionPointer,
    GetFunctionPointer,
    LoadAssembly,
    LoadAssemblyBytes,
}

#[repr(C)]
pub struct HostfxrInitializeParameters {
    pub size: usize,
    pub host_path: Box<PdCStr>,   //*const char_t,
    pub dotnet_root: Box<PdCStr>, //*const char_t,
}

pub type Hostfxrhandle = *mut libc::c_void;

#[derive(WrapperApi)]
pub struct HostfxrLib {
    hostfxr_initialize_for_dotnet_command_line: unsafe extern "C" fn(
        argc: i32,
        argv: *const *const char_t,
        parameters: *const HostfxrInitializeParameters,
        host_context_handle: Hostfxrhandle,
    ) -> i32,
    hostfxr_initialize_for_runtime_config: unsafe extern "C" fn(
        runtime_config_path: *const char_t,
        parameters: *const HostfxrInitializeParameters,
        host_context_handle: *mut Hostfxrhandle,
    ) -> i32,
    hostfxr_get_runtime_property_value:
        unsafe extern "C" fn(host_context_handle: Hostfxrhandle, name: *const char_t, value: *mut *const char_t) -> i32,
    hostfxr_set_runtime_property_value:
        unsafe extern "C" fn(host_context_handle: Hostfxrhandle, name: *const char_t, value: *const char_t) -> i32,
    hostfxr_get_runtime_properties: unsafe extern "C" fn(
        host_context_handle: Hostfxrhandle,
        count: *mut libc::size_t,
        keys: *mut *const char_t,
        values: *mut *const char_t,
    ) -> i32,
    hostfxr_run_app: unsafe extern "C" fn(host_context_handle: Hostfxrhandle) -> i32,
    hostfxr_get_runtime_delegate: unsafe extern "C" fn(
        host_context_handle: Hostfxrhandle,
        delegate_type: HostfxrDelegateType,
        delegate: *mut libc::c_void,
    ) -> i32,
    hostfxr_close: unsafe extern "C" fn(host_context_handle: Hostfxrhandle) -> i32,
}

impl HostfxrLib {
    #[allow(dead_code)]
    fn load_lib(path: impl AsRef<OsStr>) -> Result<Container<Self>, Box<dyn std::error::Error>> {
        Ok(unsafe { Container::load(path)? })
    }
}

pub type LoadAssemblyAndGetFunctionPointerFn = unsafe extern "system" fn(
    assembly_path: *const char_t,
    type_name: *const char_t,
    method_name: *const char_t,
    delegate_type_name: *const char_t,
    reserved: *const (),
    delegate: *mut libc::c_void,
) -> i32;

pub type GetFunctionPointerFn = unsafe extern "system" fn(
    type_name: *const char_t,
    method_name: *const char_t,
    delegate_type_name: *const char_t,
    load_context: *const (),
    reserved: *const (),
    delegate: *mut libc::c_void,
) -> i32;

pub type LoadAssemblyBytesFn = unsafe extern "system" fn(
    assembly_bytes: *const libc::c_void,
    assembly_bytes_len: usize,
    symbols_bytes: *const libc::c_void,
    symbols_bytes_len: usize,
    load_context: *const (),
    reserved: *const (),
) -> i32;

#[repr(C)]
struct GetHostfxrParameters {
    size: libc::size_t,
    assembly_path: *const char_t,
    dotnet_root: *const char_t,
}

#[derive(WrapperApi)]
struct NethostLib {
    get_hostfxr_path: unsafe extern "C" fn(
        buffer: *mut char_t,
        buffer_size: *mut libc::size_t,
        parameters: *const GetHostfxrParameters,
    ) -> i32,
}

struct NethostCandidate {
    path: PathBuf,
    dotnet_root: Option<PathBuf>,
}

pub struct Hostfxr {
    pub lib: Container<HostfxrLib>,
}

impl Hostfxr {
    #[allow(dead_code)]
    pub fn load_from_path(path: impl AsRef<OsStr>) -> Result<Self, Box<dyn std::error::Error>> {
        Ok(Self {
            lib: HostfxrLib::load_lib(path)?,
        })
    }

    #[allow(dead_code)]
    pub fn initialize_for_dotnet_command_line(
        &self,
        pwsh_path: impl AsRef<OsStr>,
    ) -> Result<HostfxrContext<'_, InitializedForCommandLine>, Box<dyn std::error::Error>> {
        let args = &[PdCString::from_os_str(pwsh_path)?];
        self.initialize_for_dotnet_command_line_args(args)
    }

    #[allow(dead_code)]
    pub fn initialize_for_dotnet_command_line_args(
        &self,
        args: &[PdCString],
    ) -> Result<HostfxrContext<'_, InitializedForCommandLine>, Box<dyn std::error::Error>> {
        use std::ptr;

        use crate::host_exit_code::HostExitCode;

        if args.is_empty() {
            return Err(Box::new(io::Error::new(
                io::ErrorKind::InvalidInput,
                "hostfxr command line requires at least the application path",
            )));
        }

        let argv: Vec<*const char_t> = args.iter().map(|arg| arg.as_ptr()).collect();
        let mut host_context_handle = ptr::null::<Hostfxrhandle>() as Hostfxrhandle;

        let result = unsafe {
            self.lib.hostfxr_initialize_for_dotnet_command_line(
                argv.len() as i32,
                argv.as_ptr(),
                ptr::null(),
                host_context_handle.borrow_mut() as *mut _ as Hostfxrhandle, //Initialise nullptr
            )
        };

        HostExitCode::from(result).into_result()?;

        Ok(HostfxrContext::new(
            unsafe { HostfxrHandle::new_unchecked(host_context_handle) },
            self,
        ))
    }

    #[allow(dead_code)]
    pub fn initialize_for_runtime_config(
        &self,
        runtime_config_path: impl AsRef<PdCStr>,
        parameters: Box<HostfxrInitializeParameters>, //*const HostfxrInitializeParameters,
        host_context_handle: *mut Hostfxrhandle,
    ) -> i32 {
        unsafe {
            self.lib.hostfxr_initialize_for_runtime_config(
                runtime_config_path.as_ref().as_ptr(),
                parameters.as_ref(),
                host_context_handle,
            )
        }
    }

    #[allow(dead_code)]
    pub fn initialize_for_runtime_config_path(
        &self,
        runtime_config_path: impl AsRef<PdCStr>,
    ) -> Result<HostfxrContext<'_, InitializedForRuntimeConfig>, Box<dyn std::error::Error>> {
        use std::ptr;

        use crate::host_exit_code::HostExitCode;

        let mut host_context_handle = ptr::null::<Hostfxrhandle>() as Hostfxrhandle;
        let result = unsafe {
            self.lib.hostfxr_initialize_for_runtime_config(
                runtime_config_path.as_ref().as_ptr(),
                ptr::null(),
                &mut host_context_handle,
            )
        };

        HostExitCode::from(result).into_result()?;

        Ok(HostfxrContext::new(
            unsafe { HostfxrHandle::new_unchecked(host_context_handle) },
            self,
        ))
    }

    #[allow(dead_code)]
    pub fn get_runtime_property_value(
        &self,
        host_context_handle: Hostfxrhandle,
        name: impl AsRef<PdCStr>,  //*const char_t,
        value: impl AsRef<PdCStr>, //*mut *const char_t,
    ) -> i32 {
        unsafe {
            self.lib.hostfxr_get_runtime_property_value(
                host_context_handle,
                name.as_ref().as_ptr(),
                value.as_ref().as_ptr().borrow_mut(),
            )
        }
    }

    #[allow(dead_code)]
    pub fn set_runtime_property_value(
        &self,
        host_context_handle: Hostfxrhandle,
        name: impl AsRef<PdCStr>,  //*const char_t,
        value: impl AsRef<PdCStr>, //*const char_t,
    ) -> i32 {
        unsafe {
            self.lib.hostfxr_set_runtime_property_value(
                host_context_handle,
                name.as_ref().as_ptr(),
                value.as_ref().as_ptr(),
            )
        }
    }

    #[allow(dead_code)]
    pub fn get_runtime_properties(
        &self,
        host_context_handle: Hostfxrhandle,
        count: &mut usize,          //*mut libc::size_t,
        keys: impl AsRef<PdCStr>,   //*mut *const char_t,
        values: impl AsRef<PdCStr>, //*mut *const char_t,
    ) -> i32 {
        unsafe {
            self.lib.hostfxr_get_runtime_properties(
                host_context_handle,
                count,
                keys.as_ref().as_ptr().borrow_mut(),
                values.as_ref().as_ptr().borrow_mut(),
            )
        }
    }

    #[allow(dead_code)]
    pub fn run_app(&self, host_context_handle: Hostfxrhandle) -> i32 {
        unsafe { self.lib.hostfxr_run_app(host_context_handle) }
    }

    #[allow(dead_code)]
    pub fn get_runtime_delegate(
        &self,
        host_context_handle: Hostfxrhandle,
        delegate_type: HostfxrDelegateType,
        delegate: &mut libc::c_void, //*mut libc::c_void,
    ) -> i32 {
        unsafe {
            self.lib
                .hostfxr_get_runtime_delegate(host_context_handle, delegate_type, delegate)
        }
    }

    #[allow(dead_code)]
    pub fn close(&self, host_context_handle: Hostfxrhandle) -> i32 {
        unsafe { self.lib.hostfxr_close(host_context_handle) }
    }
}

#[allow(dead_code)]
pub fn load_hostfxr() -> Result<Hostfxr, Box<dyn std::error::Error>> {
    let pwsh_path = pwsh_host_detect()?;
    load_hostfxr_from_pwsh_dir(pwsh_path)
}

pub fn load_hostfxr_from_pwsh_dir(pwsh_dir: impl AsRef<Path>) -> Result<Hostfxr, Box<dyn std::error::Error>> {
    let pwsh_dir = pwsh_dir.as_ref();
    let app_local_path = pwsh_dir.join(hostfxr_library_name());

    match Hostfxr::load_from_path(app_local_path.as_os_str()) {
        Ok(hostfxr) => Ok(hostfxr),
        Err(app_local_error) => {
            let fallback_path = resolve_hostfxr_path_from_global_install(pwsh_dir).map_err(|fallback_error| {
                io::Error::new(
                    io::ErrorKind::NotFound,
                    format!(
                        "failed to load app-local hostfxr at {}: {}; global hostfxr fallback failed: {}",
                        app_local_path.display(),
                        app_local_error,
                        fallback_error
                    ),
                )
            })?;

            Hostfxr::load_from_path(fallback_path.as_os_str()).map_err(|fallback_load_error| {
                Box::new(io::Error::new(
                    io::ErrorKind::NotFound,
                    format!(
                        "failed to load app-local hostfxr at {}: {}; failed to load global hostfxr at {}: {}",
                        app_local_path.display(),
                        app_local_error,
                        fallback_path.display(),
                        fallback_load_error
                    ),
                )) as Box<dyn std::error::Error>
            })
        }
    }
}

fn hostfxr_library_name() -> &'static str {
    if cfg!(target_os = "windows") {
        "hostfxr.dll"
    } else if cfg!(target_os = "linux") {
        "libhostfxr.so"
    } else {
        "libhostfxr.dylib"
    }
}

fn nethost_library_name() -> &'static str {
    if cfg!(target_os = "windows") {
        "nethost.dll"
    } else if cfg!(target_os = "linux") {
        "libnethost.so"
    } else {
        "libnethost.dylib"
    }
}

fn runtime_identifier() -> &'static str {
    if cfg!(all(target_os = "windows", target_arch = "x86_64")) {
        "win-x64"
    } else if cfg!(all(target_os = "windows", target_arch = "aarch64")) {
        "win-arm64"
    } else if cfg!(all(target_os = "windows", target_arch = "x86")) {
        "win-x86"
    } else if cfg!(all(target_os = "linux", target_arch = "x86_64")) {
        "linux-x64"
    } else if cfg!(all(target_os = "linux", target_arch = "aarch64")) {
        "linux-arm64"
    } else if cfg!(all(target_os = "linux", target_arch = "arm")) {
        "linux-arm"
    } else if cfg!(all(target_os = "macos", target_arch = "x86_64")) {
        "osx-x64"
    } else if cfg!(all(target_os = "macos", target_arch = "aarch64")) {
        "osx-arm64"
    } else {
        ""
    }
}

fn resolve_hostfxr_path_from_global_install(pwsh_dir: &Path) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let mut errors = Vec::new();
    for candidate in nethost_candidates(pwsh_dir) {
        match get_hostfxr_path_from_nethost(&candidate, pwsh_dir) {
            Ok(path) => return Ok(path),
            Err(error) => errors.push(format!("{}: {}", candidate.path.display(), error)),
        }
    }

    for path in global_hostfxr_paths() {
        if path.is_file() {
            return Ok(path);
        }
    }

    let detail = if errors.is_empty() {
        "no nethost candidates were found".to_string()
    } else {
        errors.join("; ")
    };
    Err(Box::new(io::Error::new(
        io::ErrorKind::NotFound,
        format!(
            "failed to resolve hostfxr with nethost and standard .NET roots ({})",
            detail
        ),
    )))
}

fn nethost_candidates(pwsh_dir: &Path) -> Vec<NethostCandidate> {
    let mut candidates = Vec::new();
    push_nethost_candidate(&mut candidates, pwsh_dir.join(nethost_library_name()), None);

    if let Ok(current_exe) = env::current_exe() {
        if let Some(current_exe_dir) = current_exe.parent() {
            push_nethost_candidate(&mut candidates, current_exe_dir.join(nethost_library_name()), None);
        }
    }

    for dotnet_root in dotnet_roots() {
        for nethost_path in nethost_paths_in_dotnet_root(&dotnet_root) {
            push_nethost_candidate(&mut candidates, nethost_path, Some(dotnet_root.clone()));
        }
    }

    candidates
}

fn push_nethost_candidate(candidates: &mut Vec<NethostCandidate>, path: PathBuf, dotnet_root: Option<PathBuf>) {
    if !path.is_file() {
        return;
    }

    if candidates
        .iter()
        .any(|candidate| paths_refer_to_same_file(&candidate.path, &path))
    {
        return;
    }

    candidates.push(NethostCandidate { path, dotnet_root });
}

fn nethost_paths_in_dotnet_root(dotnet_root: &Path) -> Vec<PathBuf> {
    let rid = runtime_identifier();
    if rid.is_empty() {
        return Vec::new();
    }

    let host_pack_dir = dotnet_root
        .join("packs")
        .join(format!("Microsoft.NETCore.App.Host.{}", rid));
    let Ok(version_dirs) = std::fs::read_dir(host_pack_dir) else {
        return Vec::new();
    };

    let mut paths = Vec::new();
    for entry in version_dirs.flatten() {
        let version_name = entry.file_name();
        let path = entry
            .path()
            .join("runtimes")
            .join(rid)
            .join("native")
            .join(nethost_library_name());
        if path.is_file() {
            paths.push((version_key(&version_name), path));
        }
    }

    paths.sort_by(|left, right| right.0.cmp(&left.0));
    paths.into_iter().map(|(_, path)| path).collect()
}

fn global_hostfxr_paths() -> Vec<PathBuf> {
    let mut paths = Vec::new();
    for dotnet_root in dotnet_roots() {
        paths.extend(hostfxr_paths_in_dotnet_root(&dotnet_root));
    }
    paths
}

fn hostfxr_paths_in_dotnet_root(dotnet_root: &Path) -> Vec<PathBuf> {
    let Ok(version_dirs) = std::fs::read_dir(dotnet_root.join("host").join("fxr")) else {
        return Vec::new();
    };

    let mut paths = Vec::new();
    for entry in version_dirs.flatten() {
        let version_name = entry.file_name();
        let path = entry.path().join(hostfxr_library_name());
        if path.is_file() {
            paths.push((version_key(&version_name), path));
        }
    }

    paths.sort_by(|left, right| right.0.cmp(&left.0));
    paths.into_iter().map(|(_, path)| path).collect()
}

fn dotnet_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();

    for name in dotnet_root_env_var_names() {
        if let Some(value) = env::var_os(name) {
            push_dotnet_root(&mut roots, PathBuf::from(value));
        }
    }

    for path in default_dotnet_roots() {
        push_dotnet_root(&mut roots, path);
    }

    roots
}

fn dotnet_root_env_var_names() -> Vec<&'static str> {
    let mut names = Vec::new();
    if cfg!(target_arch = "x86_64") {
        names.push("DOTNET_ROOT_X64");
    } else if cfg!(target_arch = "aarch64") {
        names.push("DOTNET_ROOT_ARM64");
    } else if cfg!(target_arch = "x86") {
        names.push("DOTNET_ROOT_X86");
    }

    names.push("DOTNET_ROOT");

    if cfg!(all(target_os = "windows", target_arch = "x86")) {
        names.push("DOTNET_ROOT(x86)");
    }

    names
}

fn default_dotnet_roots() -> Vec<PathBuf> {
    if cfg!(target_os = "windows") {
        let mut roots = Vec::new();
        if cfg!(target_arch = "x86") {
            roots.push(PathBuf::from(r"C:\Program Files (x86)\dotnet"));
        }
        roots.push(PathBuf::from(r"C:\Program Files\dotnet"));
        roots
    } else if cfg!(target_os = "macos") {
        vec![
            PathBuf::from("/usr/local/share/dotnet"),
            PathBuf::from("/opt/homebrew/share/dotnet"),
        ]
    } else {
        vec![
            PathBuf::from("/usr/share/dotnet"),
            PathBuf::from("/usr/local/share/dotnet"),
        ]
    }
}

fn push_dotnet_root(roots: &mut Vec<PathBuf>, path: PathBuf) {
    if !path.is_dir() {
        return;
    }

    if roots.iter().any(|root| paths_refer_to_same_file(root, &path)) {
        return;
    }

    roots.push(path);
}

fn paths_refer_to_same_file(left: &Path, right: &Path) -> bool {
    match (std::fs::canonicalize(left), std::fs::canonicalize(right)) {
        (Ok(left), Ok(right)) => left == right,
        _ => left == right,
    }
}

fn version_key(name: &OsStr) -> Vec<u64> {
    name.to_string_lossy()
        .split(|ch: char| !ch.is_ascii_digit())
        .filter(|part| !part.is_empty())
        .map(|part| part.parse::<u64>().unwrap_or(0))
        .collect()
}

fn get_hostfxr_path_from_nethost(
    candidate: &NethostCandidate,
    pwsh_dir: &Path,
) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let nethost: Container<NethostLib> = unsafe { Container::load(candidate.path.as_os_str())? };

    let assembly_path = PdCString::from_os_str(pwsh_dir.join("pwsh.dll"))?;
    let dotnet_root = candidate.dotnet_root.as_ref().map(PdCString::from_os_str).transpose()?;
    let parameters = GetHostfxrParameters {
        size: std::mem::size_of::<GetHostfxrParameters>(),
        assembly_path: assembly_path.as_ptr(),
        dotnet_root: dotnet_root.as_ref().map(|value| value.as_ptr()).unwrap_or(ptr::null()),
    };

    let mut buffer_size: libc::size_t = 260;
    loop {
        let previous_size = buffer_size as usize;
        let mut buffer = vec![0 as char_t; buffer_size as usize];
        let result = unsafe { nethost.get_hostfxr_path(buffer.as_mut_ptr(), &mut buffer_size, &parameters) };
        let exit_code = HostExitCode::from(result);
        if exit_code.is_success() {
            return Ok(path_from_char_buffer(&buffer));
        }

        if exit_code == HostExitCode::Known(KnownHostExitCode::HostApiBufferTooSmall) {
            if buffer_size as usize <= previous_size {
                buffer_size = (previous_size.saturating_mul(2).max(1)) as libc::size_t;
            }
            continue;
        }

        return Err(Box::new(crate::error::Error::Hostfxr(exit_code)));
    }
}

#[cfg(windows)]
fn path_from_char_buffer(buffer: &[char_t]) -> PathBuf {
    use std::ffi::OsString;
    use std::os::windows::ffi::OsStringExt;

    let len = buffer.iter().position(|value| *value == 0).unwrap_or(buffer.len());
    PathBuf::from(OsString::from_wide(&buffer[..len]))
}

#[cfg(not(windows))]
fn path_from_char_buffer(buffer: &[char_t]) -> PathBuf {
    use std::ffi::OsString;
    use std::os::unix::ffi::OsStringExt;

    let len = buffer.iter().position(|value| *value == 0).unwrap_or(buffer.len());
    let bytes = buffer[..len].iter().map(|value| *value as u8).collect();
    PathBuf::from(OsString::from_vec(bytes))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::sync::atomic::{AtomicUsize, Ordering};

    static NEXT_TEST_DIR_ID: AtomicUsize = AtomicUsize::new(0);

    struct TestDir(PathBuf);

    impl TestDir {
        fn new() -> Self {
            let path = env::temp_dir().join(format!(
                "pwsh-host-hostfxr-test-{}-{}",
                std::process::id(),
                NEXT_TEST_DIR_ID.fetch_add(1, Ordering::SeqCst)
            ));
            let _ = fs::remove_dir_all(&path);
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TestDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn version_key_compares_numeric_segments() {
        assert!(version_key(OsStr::new("10.0.9")) > version_key(OsStr::new("8.0.28")));
        assert!(version_key(OsStr::new("8.0.28")) > version_key(OsStr::new("8.0.9")));
    }

    #[test]
    fn nethost_paths_in_dotnet_root_returns_newest_first() {
        let rid = runtime_identifier();
        if rid.is_empty() {
            return;
        }

        let temp_dir = TestDir::new();
        for version in ["8.0.28", "10.0.9"] {
            let native_dir = temp_dir
                .path()
                .join("packs")
                .join(format!("Microsoft.NETCore.App.Host.{}", rid))
                .join(version)
                .join("runtimes")
                .join(rid)
                .join("native");
            fs::create_dir_all(&native_dir).unwrap();
            fs::write(native_dir.join(nethost_library_name()), "").unwrap();
        }

        let paths = nethost_paths_in_dotnet_root(temp_dir.path());
        assert_eq!(paths.len(), 2);
        assert!(paths[0].display().to_string().contains("10.0.9"));
        assert!(paths[1].display().to_string().contains("8.0.28"));
    }

    #[test]
    fn hostfxr_paths_in_dotnet_root_returns_newest_first() {
        let temp_dir = TestDir::new();
        for version in ["8.0.28", "10.0.9"] {
            let fxr_dir = temp_dir.path().join("host").join("fxr").join(version);
            fs::create_dir_all(&fxr_dir).unwrap();
            fs::write(fxr_dir.join(hostfxr_library_name()), "").unwrap();
        }

        let paths = hostfxr_paths_in_dotnet_root(temp_dir.path());
        assert_eq!(paths.len(), 2);
        assert!(paths[0].display().to_string().contains("10.0.9"));
        assert!(paths[1].display().to_string().contains("8.0.28"));
    }
}
