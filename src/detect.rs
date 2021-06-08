
use std::env;
use std::ffi::{OsStr, OsString};
use std::fs::{self, File};
use std::path::{Path, PathBuf};

fn find_in_path(path: &Path) -> Option<PathBuf> {
    if let Some(found) = env::split_paths(&env::var_os("PATH").unwrap_or(OsString::new()))
        .map(|p| p.join(path))
        .find(|p| fs::metadata(p).is_ok()) {
            return Some(found);
        }
    return None;
}

pub fn find_pwsh_executable(preview: bool) -> Option<PathBuf> {
    if preview {
        if let Some(path) = find_in_path(&Path::new("pwsh-preview.cmd")) {
            let path = path.parent().unwrap().parent().unwrap().join(Path::new("pwsh.exe"));
            return Some(path);
        }
        if let Some(path) = find_in_path(&Path::new("pwsh-preview")) {
            return Some(path);
        }
    } else {
        if let Some(path) = find_in_path(&Path::new("pwsh.exe")) {
            return Some(path);
        }
        if let Some(path) = find_in_path(&Path::new("pwsh")) {
            return Some(path);
        }
    }
    return None;
}

pub fn find_pwsh_install_path(preview: bool) -> Option<PathBuf> {
    if let Some(path) = find_pwsh_executable(preview) {
        let path = path.parent().unwrap().to_path_buf();
        return Some(path);
    }
    return None;
}

extern "C" {
    pub fn pwsh_host_detect() -> bool;
    pub fn pwsh_host_app() -> bool;
    pub fn pwsh_host_lib() -> bool;
}

#[cfg(test)]
mod tests {
    use crate::detect::*;

    #[test]
    fn test_detect() {
        let executable_path = find_pwsh_executable(true).unwrap();
        println!("executable: {}", executable_path.to_str().unwrap());

        let install_path = find_pwsh_install_path(true).unwrap();
        println!("install path: {}", install_path.to_str().unwrap());
    }

    #[test]
    fn host_app() {
        unsafe { let _ = pwsh_host_app(); }
    }

    #[test]
    fn host_lib() {
        unsafe { let _ = pwsh_host_lib(); }
    }
}
