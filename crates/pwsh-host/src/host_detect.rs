use std::fs;
use std::path::{Path, PathBuf};

use thiserror::Error;

#[allow(dead_code)]
#[derive(Debug, Error, PartialEq)]
pub enum EnvError {
    #[error("PATH undefined or unset in the environment.")]
    UndefOrUnset,
    #[error("PowerShell install dir not found in PATH")]
    Missing,
}

fn hostfxr_file_name() -> &'static str {
    if cfg!(target_os = "windows") {
        "hostfxr.dll"
    } else if cfg!(target_os = "linux") {
        "libhostfxr.so"
    } else {
        "libhostfxr.dylib"
    }
}

fn resolve_link_target(path: &Path) -> PathBuf {
    match fs::read_link(path) {
        Ok(link) if link.is_relative() => path.parent().unwrap_or_else(|| Path::new("")).join(link),
        Ok(link) => link,
        Err(_) => path.to_path_buf(),
    }
}

fn has_pwsh_runtime(dir: &Path) -> bool {
    dir.join("pwsh.dll").exists() && dir.join(hostfxr_file_name()).exists()
}

fn select_pwsh_exe<I>(candidates: I) -> Option<PathBuf>
where
    I: IntoIterator<Item = PathBuf>,
{
    let mut fallback: Option<PathBuf> = None;

    for candidate in candidates {
        let resolved = resolve_link_target(&candidate);
        let is_runtime_candidate = resolved.parent().map(has_pwsh_runtime).unwrap_or(false);

        if is_runtime_candidate {
            return Some(resolved);
        }

        if fallback.is_none() {
            fallback = Some(resolved);
        }
    }

    fallback
}

fn pwsh_candidates_from_path() -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    let path = match std::env::var_os("PATH") {
        Some(path) => path,
        None => return candidates,
    };

    for dir in std::env::split_paths(&path) {
        let candidate = dir.join(format!("pwsh{}", std::env::consts::EXE_SUFFIX));
        if candidate.exists() {
            candidates.push(candidate);
        }
    }

    candidates
}

pub fn find_pwsh_exe() -> Option<PathBuf> {
    let candidates = pwsh_candidates_from_path();
    if !candidates.is_empty() {
        return select_pwsh_exe(candidates);
    }

    if let Ok(pwsh_exe) = which::which("pwsh") {
        return Some(resolve_link_target(&pwsh_exe));
    }

    None
}

pub fn find_pwsh_dir() -> Option<PathBuf> {
    if let Some(mut pwsh_exe) = find_pwsh_exe() {
        pwsh_exe.pop();
        return Some(pwsh_exe);
    }
    None
}

#[allow(dead_code)]
pub fn pwsh_host_detect() -> Result<PathBuf, EnvError> {
    find_pwsh_dir().ok_or(EnvError::Missing)
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::PathBuf;
    use std::time::{SystemTime, UNIX_EPOCH};

    use super::hostfxr_file_name;
    use super::pwsh_candidates_from_path;
    use super::select_pwsh_exe;

    fn create_temp_dir(prefix: &str) -> PathBuf {
        let timestamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let dir = std::env::temp_dir().join(format!("multi-pwsh-{}-{}", prefix, timestamp));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn select_pwsh_exe_prefers_runtime_candidate() {
        let temp = create_temp_dir("prefer-runtime");

        let shim_dir = temp.join("shim");
        let runtime_dir = temp.join("runtime");
        fs::create_dir_all(&shim_dir).unwrap();
        fs::create_dir_all(&runtime_dir).unwrap();

        let shim_pwsh = shim_dir.join(format!("pwsh{}", std::env::consts::EXE_SUFFIX));
        fs::write(&shim_pwsh, "").unwrap();

        let runtime_pwsh = runtime_dir.join(format!("pwsh{}", std::env::consts::EXE_SUFFIX));
        fs::write(&runtime_pwsh, "").unwrap();
        fs::write(runtime_dir.join("pwsh.dll"), "").unwrap();
        fs::write(runtime_dir.join(hostfxr_file_name()), "").unwrap();

        let selected = select_pwsh_exe(vec![shim_pwsh.clone(), runtime_pwsh.clone()]).unwrap();
        assert_eq!(selected, runtime_pwsh);

        let _ = fs::remove_dir_all(temp);
    }

    #[test]
    fn select_pwsh_exe_falls_back_to_first_candidate() {
        let temp = create_temp_dir("fallback-first");
        let first_dir = temp.join("first");
        let second_dir = temp.join("second");
        fs::create_dir_all(&first_dir).unwrap();
        fs::create_dir_all(&second_dir).unwrap();

        let first_pwsh = first_dir.join(format!("pwsh{}", std::env::consts::EXE_SUFFIX));
        let second_pwsh = second_dir.join(format!("pwsh{}", std::env::consts::EXE_SUFFIX));
        fs::write(&first_pwsh, "").unwrap();
        fs::write(&second_pwsh, "").unwrap();

        let selected = select_pwsh_exe(vec![first_pwsh.clone(), second_pwsh]).unwrap();
        assert_eq!(selected, first_pwsh);

        let _ = fs::remove_dir_all(temp);
    }

    #[test]
    fn pwsh_candidates_from_path_collects_candidates() {
        let temp = create_temp_dir("path-candidates");
        let first_dir = temp.join("first");
        let second_dir = temp.join("second");
        fs::create_dir_all(&first_dir).unwrap();
        fs::create_dir_all(&second_dir).unwrap();

        let first_pwsh = first_dir.join(format!("pwsh{}", std::env::consts::EXE_SUFFIX));
        let second_pwsh = second_dir.join(format!("pwsh{}", std::env::consts::EXE_SUFFIX));
        fs::write(&first_pwsh, "").unwrap();
        fs::write(&second_pwsh, "").unwrap();

        let original_path = std::env::var_os("PATH");
        let composed = std::env::join_paths([first_dir.clone(), second_dir.clone()]).unwrap();
        unsafe {
            std::env::set_var("PATH", composed);
        }

        let candidates = pwsh_candidates_from_path();
        assert_eq!(candidates, vec![first_pwsh, second_pwsh]);

        match original_path {
            Some(path) => unsafe { std::env::set_var("PATH", path) },
            None => unsafe { std::env::remove_var("PATH") },
        }
        let _ = fs::remove_dir_all(temp);
    }
}
