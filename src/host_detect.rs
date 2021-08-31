use regex::Regex;
use std::env;
use std::ffi::OsString;
use std::path::PathBuf;
use thiserror::Error;

#[allow(dead_code)]
#[derive(Debug, Error, PartialEq)]
pub enum EnvError {
    #[error("PATH undefined or unset in the environment.")]
    UndefOrUnset,
    #[error("PowerShell install dir not found in PATH")]
    Missing,
}

#[allow(dead_code)]
pub fn pwsh_host_detect(path: Option<OsString>) -> Result<PathBuf, EnvError> {
    match path {
        None => Err(EnvError::UndefOrUnset),
        Some(path) => match env::split_paths(&path)
            .find(|path| {
                Regex::new(r"(?i)powershell\D+?[^0-6]+?([.]?[2-9]+?|-preview)")
                    .unwrap()
                    .is_match(path.to_str().unwrap())
            })
            .or(None)
        {
            None => Err(EnvError::Missing),
            Some(path) => Ok(path),
        },
    }
}
