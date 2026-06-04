use std::fmt;

use crate::error::{MultiPwshError, Result};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HostOs {
    Windows,
    Macos,
    Linux,
}

impl HostOs {
    pub fn detect() -> Result<Self> {
        match std::env::consts::OS {
            "windows" => Ok(HostOs::Windows),
            "macos" => Ok(HostOs::Macos),
            "linux" => Ok(HostOs::Linux),
            value => Err(MultiPwshError::UnsupportedPlatform(format!(
                "operating system '{}' is not supported",
                value
            ))),
        }
    }

    pub fn executable_name(self) -> &'static str {
        match self {
            HostOs::Windows => "pwsh.exe",
            HostOs::Macos | HostOs::Linux => "pwsh",
        }
    }

    pub fn parse(value: &str) -> Option<Self> {
        match value.to_ascii_lowercase().as_str() {
            "windows" | "win" => Some(HostOs::Windows),
            "macos" | "osx" | "darwin" => Some(HostOs::Macos),
            "linux" => Some(HostOs::Linux),
            _ => None,
        }
    }

    pub fn as_manifest_value(self) -> &'static str {
        match self {
            HostOs::Windows => "windows",
            HostOs::Macos => "macos",
            HostOs::Linux => "linux",
        }
    }
}

impl fmt::Display for HostOs {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_manifest_value())
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HostArch {
    X64,
    X86,
    Arm64,
    Arm32,
}

impl HostArch {
    pub fn detect() -> Self {
        match std::env::consts::ARCH {
            "x86_64" => HostArch::X64,
            "x86" | "i686" => HostArch::X86,
            "aarch64" => HostArch::Arm64,
            "arm" | "armv7" | "armv7l" => HostArch::Arm32,
            _ => HostArch::X64,
        }
    }

    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "x64" => Some(HostArch::X64),
            "x86" => Some(HostArch::X86),
            "arm64" => Some(HostArch::Arm64),
            "arm32" => Some(HostArch::Arm32),
            _ => None,
        }
    }

    pub fn as_manifest_value(self) -> &'static str {
        match self {
            HostArch::X64 => "x64",
            HostArch::X86 => "x86",
            HostArch::Arm64 => "arm64",
            HostArch::Arm32 => "arm32",
        }
    }
}

impl fmt::Display for HostArch {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_manifest_value())
    }
}
