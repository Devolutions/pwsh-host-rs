mod aliases;
mod error;
mod install;
mod layout;
mod mcp;
mod package;
mod platform;
mod release;
mod versions;

use std::collections::HashMap;
use std::env;
use std::ffi::{OsStr, OsString};
use std::fs;
use std::io::{self, Read, Write};
use std::path::{Component, Path, PathBuf};
use std::process;
#[cfg(test)]
use std::sync::Mutex;

use semver::Version;

use aliases::{
    create_or_update_alias, create_or_update_major_alias, create_or_update_named_alias, create_or_update_patch_alias,
    ensure_special_alias_policy, is_special_alias_command, is_supported_alias_command, parse_alias_command_selector,
    read_layout_hint, read_minor_pin, read_minor_pins, remove_alias, set_minor_pin, set_special_alias_policy,
    AliasSelector, PWSH_ALIAS, PWSH_LTS_ALIAS, PWSH_PREVIEW_ALIAS,
};
use error::{MultiPwshError, Result};
use install::{copy_asset_to_path, ensure_installed, validate_archive_checksum, ChecksumSource};
use layout::{path_env_var, InstallLayout};
use package::{
    load_package_metadata, package_layout, persist_installed_version_registration, persist_installer_properties,
    reconcile_shared_integrations, remove_installed_version_registration, run_install_time_actions,
    save_package_metadata, PackageInstallOptions, PackageScope, PACKAGE_METADATA_FILE,
};
use platform::{HostArch, HostOs};
use release::{
    load_or_default_powershell_manifest, save_powershell_manifest, AssetSource, OfflinePowerShellAsset,
    OfflinePowerShellManifest, OfflineReleaseClient, ReleaseClient, ResolvedRelease,
};
use versions::{
    is_current_lts_version, parse_exact_version, parse_install_selector, parse_major_minor_selector,
    parse_major_selector, MajorMinor, VersionSelector,
};

const POWERSHELL_UPDATECHECK_ENV_VAR: &str = "POWERSHELL_UPDATECHECK";
const POWERSHELL_UPDATECHECK_OFF: &str = "Off";
const MULTI_PWSH_OFFLINE_CACHE_ENV_VAR: &str = "MULTI_PWSH_OFFLINE_CACHE";
const VIRTUAL_ENVIRONMENT_FLAG: &str = "-virtualenvironment";
const VIRTUAL_ENVIRONMENT_SHORT_FLAG: &str = "-venv";

#[cfg(test)]
static TEST_ENV_LOCK: Mutex<()> = Mutex::new(());
const HELP_TOPICS: &[&str] = &[
    "install",
    "update",
    "uninstall",
    "list",
    "venv",
    "alias",
    "host",
    "doctor",
    "cache",
    "version",
];

fn usage_text() -> &'static str {
    "Usage:\n  multi-pwsh --version\n  multi-pwsh -V\n  multi-pwsh --help\n  multi-pwsh help [command]\n  multi-pwsh install <stable|preview|lts|version|major|major.minor|major.minor.x> [--scope <user|machine>] [--root <path>] [--arch <auto|x64|x86|arm64|arm32>] [--include-prerelease] [--offline-cache <path>] [--add-path|--no-add-path] [--register-manifest|--no-register-manifest] [--enable-psremoting] [--disable-telemetry] [--add-explorer-context-menu] [--add-file-context-menu]\n  multi-pwsh update <stable|preview|lts|major.minor> [--scope <user|machine>] [--root <path>] [--arch <auto|x64|x86|arm64|arm32>] [--include-prerelease] [--offline-cache <path>] [--add-path|--no-add-path] [--register-manifest|--no-register-manifest] [--enable-psremoting] [--disable-telemetry] [--add-explorer-context-menu] [--add-file-context-menu]\n  multi-pwsh uninstall <version> [--scope <user|machine>] [--root <path>] [--force]\n  multi-pwsh list [--scope <user|machine|all>] [--root <path>] [--available] [--include-prerelease] [--offline-cache <path>]\n  multi-pwsh cache warm <selector> [--os <windows|linux|macos|all>] [--arch <x64|x86|arm64|arm32|all>] [--include-prerelease] [--output <path>] [--product <powershell|multi-pwsh|all>]\n  multi-pwsh venv create <name>\n  multi-pwsh venv delete <name>\n  multi-pwsh venv export <name> <archive.zip>\n  multi-pwsh venv import <name> <archive.zip>\n  multi-pwsh venv list\n  multi-pwsh alias set <major.minor> <version|latest>\n  multi-pwsh alias set <pwsh|pwsh-preview|pwsh-lts> <stable|preview|lts|version>\n  multi-pwsh alias unset <major.minor|pwsh|pwsh-preview|pwsh-lts>\n  multi-pwsh host <version|major|major.minor|pwsh-alias> [-VirtualEnvironment <name>|-venv <name>] [pwsh arguments...]\n  multi-pwsh doctor --repair-aliases\n\nCommands:\n  install, update, uninstall, list, cache, venv, alias, host, doctor, version"
}

fn print_usage() {
    eprintln!("{}", usage_text());
}

fn print_global_help() {
    println!("{}", usage_text());
}

fn print_version() {
    println!("multi-pwsh {}", env!("CARGO_PKG_VERSION"));
}

fn is_help_flag(value: &str) -> bool {
    matches!(value, "-h" | "--help")
}

fn help_topic_text(topic: &str) -> Option<&'static str> {
    match topic {
        "install" => Some(
            "Usage:\n  multi-pwsh install <stable|preview|lts|version|major|major.minor|major.minor.x> [options]\n\nOptions:\n  --scope <user|machine>\n  --root <path>\n  --arch <auto|x64|x86|arm64|arm32>\n  --include-prerelease\n  --offline-cache <path>\n  --add-path | --no-add-path\n  --register-manifest | --no-register-manifest\n  --enable-psremoting\n  --disable-telemetry\n  --add-explorer-context-menu\n  --add-file-context-menu\n  --skip-hash-verification\n  --hash-file <url-or-path>\n\nNotes:\n  User scope is the default when --scope is omitted.\n  MULTI_PWSH_* path env vars affect only the default user layout.\n  Machine scope uses platform defaults unless --root overrides the install root.\n  --root requires --scope <user|machine> and does not mix in MULTI_PWSH_* child-dir overrides.\n  On Windows, --add-path updates persistent User/Machine PATH; machine scope may require elevation for install roots, registry integrations, and Machine PATH updates.\n  On macOS/Linux, --add-path is unsupported, --no-add-path is a no-op, and shell/profile PATH updates are manual.",
        ),
        "update" => Some(
            "Usage:\n  multi-pwsh update <stable|preview|lts|major.minor> [options]\n\nOptions:\n  --scope <user|machine>\n  --root <path>\n  --arch <auto|x64|x86|arm64|arm32>\n  --include-prerelease\n  --offline-cache <path>\n  --add-path | --no-add-path\n  --register-manifest | --no-register-manifest\n  --enable-psremoting\n  --disable-telemetry\n  --add-explorer-context-menu\n  --add-file-context-menu\n  --skip-hash-verification\n  --hash-file <url-or-path>\n\nNotes:\n  User scope is the default when --scope is omitted.\n  MULTI_PWSH_* path env vars affect only the default user layout.\n  Machine scope uses platform defaults unless --root overrides the install root.\n  --root requires --scope <user|machine> and does not mix in MULTI_PWSH_* child-dir overrides.\n  On Windows, --add-path updates persistent User/Machine PATH; machine scope may require elevation for install roots, registry integrations, and Machine PATH updates.\n  On macOS/Linux, --add-path is unsupported, --no-add-path is a no-op, and shell/profile PATH updates are manual.",
        ),
        "uninstall" => Some(
            "Usage:\n  multi-pwsh uninstall <version> [options]\n\nOptions:\n  --scope <user|machine>\n  --root <path>\n  --force\n\nNotes:\n  User scope is the default when --scope is omitted.\n  --root requires --scope <user|machine>.",
        ),
        "list" => Some(
            "Usage:\n  multi-pwsh list [options]\n\nOptions:\n  --scope <user|machine|all>\n  --root <path>\n  --available\n  --include-prerelease\n  --offline-cache <path>\n\nNotes:\n  User scope is the default when --scope is omitted.\n  --root requires --scope <user|machine>.\n  Installed listings include prerelease versions; --include-prerelease only changes --available listings.",
        ),
        "venv" => Some(
            "Usage:\n  multi-pwsh venv create <name>\n  multi-pwsh venv delete <name>\n  multi-pwsh venv export <name> <archive.zip>\n  multi-pwsh venv import <name> <archive.zip>\n  multi-pwsh venv list\n\nNotes:\n  Virtual environments live in the default user layout.",
        ),
        "alias" => Some(
            "Usage:\n  multi-pwsh alias set <major.minor> <version|latest>\n  multi-pwsh alias set <pwsh|pwsh-preview|pwsh-lts> <stable|preview|lts|version>\n  multi-pwsh alias unset <major.minor|pwsh|pwsh-preview|pwsh-lts>\n\nNotes:\n  Direct alias commands operate on the default user layout; machine-scope aliases are normally entered through generated machine-scope shims.",
        ),
        "host" => Some(
            "Usage:\n  multi-pwsh host <version|major|major.minor|pwsh-alias> [-VirtualEnvironment <name>|-venv <name>] [pwsh arguments...]\n  multi-pwsh host <version|major|major.minor|pwsh-alias> -mcp -McpCommands <command> [command ...] [-VirtualEnvironment <name>|-venv <name>]\n\nNotes:\n  Direct host commands resolve against the default user layout; generated machine-scope shims carry their own layout hints.",
        ),
        "doctor" => Some(
            "Usage:\n  multi-pwsh doctor --repair-aliases\n\nNotes:\n  Direct doctor commands repair the default user layout; generated machine-scope shims carry their own layout hints.",
        ),
        "cache" => Some(
            "Usage:\n  multi-pwsh cache warm <selector> [options]\n\nOptions:\n  --os <windows|linux|macos|all>\n  --arch <x64|x86|arm64|arm32|all>\n  --include-prerelease\n  --output <path>\n  --product <powershell|multi-pwsh|all>\n\nNotes:\n  If --output is omitted, cache warm writes to MULTI_PWSH_CACHE_DIR or the default user cache directory.",
        ),
        "package" => Some(
            "Usage:\n  multi-pwsh package install <stable|preview|lts|version|major|major.minor|major.minor.x> [options]\n  multi-pwsh package uninstall <version> [--scope <user|machine>] [--root <path>] [--force]\n  multi-pwsh package list [--scope <user|machine>] [--root <path>]\n\nAdvanced compatibility command; prefer the top-level install, update, uninstall, and list commands.\n\nNotes:\n  MULTI_PWSH_* path env vars affect only the default user layout.\n  Machine scope uses platform defaults unless --root overrides the install root.\n  --root requires --scope <user|machine>.",
        ),
        "version" => Some("Usage:\n  multi-pwsh --version\n  multi-pwsh -V\n  multi-pwsh version"),
        _ => None,
    }
}

fn print_help_topic(topic: &str) -> Result<()> {
    let Some(text) = help_topic_text(topic) else {
        return Err(MultiPwshError::InvalidArguments(format!(
            "unknown help topic '{}'. expected one of: {}",
            topic,
            HELP_TOPICS.join(", ")
        )));
    };

    println!("{}", text);
    Ok(())
}

fn run_help(args: &[String]) -> Result<()> {
    match args {
        [] => {
            print_global_help();
            Ok(())
        }
        [topic] => print_help_topic(topic),
        _ => Err(MultiPwshError::InvalidArguments(
            "help accepts at most one command topic".to_string(),
        )),
    }
}

#[cfg(test)]
struct ReleaseSelectionOptions {
    arch: Option<HostArch>,
    include_prerelease: bool,
    checksum_source: ChecksumSource,
    offline_cache: Option<PathBuf>,
}

#[derive(Debug)]
struct InstallCommandOptions {
    package: PackageInstallOptions,
    checksum_source: ChecksumSource,
    offline_cache: Option<PathBuf>,
}

#[derive(Clone, Debug)]
struct PackageLayoutOptions {
    scope: PackageScope,
    root: Option<PathBuf>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum WindowsListScope {
    CurrentUser,
    AllUsers,
    All,
}

impl WindowsListScope {
    fn parse(value: &str) -> Option<Self> {
        match value.to_ascii_lowercase().as_str() {
            "user" => Some(WindowsListScope::CurrentUser),
            "machine" => Some(WindowsListScope::AllUsers),
            "all" => Some(WindowsListScope::All),
            _ => None,
        }
    }
}

#[derive(Clone, Debug)]
struct WindowsUninstallOptions {
    scope: Option<PackageScope>,
    root: Option<PathBuf>,
    force: bool,
}

enum ListOption {
    Installed {
        scope: Option<WindowsListScope>,
        root: Option<PathBuf>,
    },
    Available {
        include_prerelease: bool,
        offline_cache: Option<PathBuf>,
    },
}

enum ReleaseResolver {
    Github(ReleaseClient),
    Offline(OfflineReleaseClient),
}

impl ReleaseResolver {
    fn new(offline_cache: Option<PathBuf>) -> Result<Self> {
        if let Some(path) = effective_offline_cache(offline_cache) {
            return Ok(ReleaseResolver::Offline(OfflineReleaseClient::new(path)?));
        }

        let token = env::var("GITHUB_TOKEN").ok();
        Ok(ReleaseResolver::Github(ReleaseClient::new(token)?))
    }

    fn http_client(&self) -> Option<&ureq::Agent> {
        match self {
            ReleaseResolver::Github(client) => Some(client.http_client()),
            ReleaseResolver::Offline(_) => None,
        }
    }

    fn resolve_selector(
        &self,
        selector: VersionSelector,
        os: HostOs,
        arch: HostArch,
        include_prerelease: bool,
    ) -> Result<ResolvedRelease> {
        match self {
            ReleaseResolver::Github(client) => client.resolve_selector(selector, os, arch, include_prerelease),
            ReleaseResolver::Offline(client) => client.resolve_selector(selector, os, arch, include_prerelease),
        }
    }

    fn resolve_all_in_line(
        &self,
        line: MajorMinor,
        os: HostOs,
        arch: HostArch,
        include_prerelease: bool,
    ) -> Result<Vec<ResolvedRelease>> {
        match self {
            ReleaseResolver::Github(client) => client.resolve_all_in_line(line, os, arch, include_prerelease),
            ReleaseResolver::Offline(client) => client.resolve_all_in_line(line, os, arch, include_prerelease),
        }
    }

    fn list_available_versions(&self, include_prerelease: bool) -> Result<Vec<Version>> {
        match self {
            ReleaseResolver::Github(client) => client.list_available_versions(include_prerelease),
            ReleaseResolver::Offline(client) => Ok(client.list_available_versions(include_prerelease)),
        }
    }

    fn is_offline(&self) -> bool {
        matches!(self, ReleaseResolver::Offline(_))
    }
}

#[derive(Debug, Default, Eq, PartialEq)]
struct HostLaunchOptions {
    pwsh_args: Vec<OsString>,
    virtual_environment: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct HostMcpOptions {
    commands: Vec<String>,
}

#[derive(Debug, Default, Eq, PartialEq)]
struct HostDispatchOptions {
    launch: HostLaunchOptions,
    mcp: Option<HostMcpOptions>,
}

struct ProcessEnvVarGuard {
    key: &'static str,
    previous: Option<OsString>,
}

impl ProcessEnvVarGuard {
    fn set(key: &'static str, value: impl AsRef<OsStr>) -> Self {
        let previous = env::var_os(key);
        unsafe { env::set_var(key, value) };
        Self { key, previous }
    }
}

impl Drop for ProcessEnvVarGuard {
    fn drop(&mut self) {
        match &self.previous {
            Some(value) => unsafe { env::set_var(self.key, value) },
            None => unsafe { env::remove_var(self.key) },
        }
    }
}

fn default_current_user_layout(os: HostOs) -> Result<InstallLayout> {
    InstallLayout::new(os)
}

fn configure_virtual_environment_host_env(os: HostOs, venv_dir: &Path) -> Result<Vec<ProcessEnvVarGuard>> {
    let _ = os;

    Ok(vec![
        ProcessEnvVarGuard::set(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR, venv_dir.as_os_str()),
        ProcessEnvVarGuard::set(
            pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR,
            pwsh_host::MODULE_PATH_STRATEGY,
        ),
    ])
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum HostSelector {
    NamedAlias(String),
    Major(u64),
    MajorMinor(MajorMinor),
    Exact(Version),
}

fn parse_host_selector(value: &str) -> Result<HostSelector> {
    if is_special_alias_command(value) {
        return Ok(HostSelector::NamedAlias(value.to_string()));
    }

    if let Some(selector) = parse_alias_command_selector(value) {
        return Ok(match selector {
            AliasSelector::Major(major) => HostSelector::Major(major),
            AliasSelector::MajorMinor(line) => HostSelector::MajorMinor(line),
            AliasSelector::Exact(version) => HostSelector::Exact(version),
        });
    }

    if let Ok(version) = parse_exact_version(value) {
        return Ok(HostSelector::Exact(version));
    }

    if let Ok(line) = parse_major_minor_selector(value) {
        return Ok(HostSelector::MajorMinor(line));
    }

    if let Ok(major) = parse_major_selector(value) {
        return Ok(HostSelector::Major(major));
    }

    Err(MultiPwshError::InvalidArguments(format!(
        "host selector '{}' is invalid; expected one of: <major>, <major.minor>, <major.minor.patch>, or pwsh-<selector>",
        value
    )))
}

fn resolve_host_version(layout: &InstallLayout, selector: &HostSelector) -> Result<Version> {
    match selector {
        HostSelector::NamedAlias(alias_name) => {
            let aliases = aliases::read_alias_metadata(layout)?;
            let version_text = aliases.get(alias_name).ok_or_else(|| {
                MultiPwshError::InvalidArguments(format!(
                    "alias '{}' is unresolved; configure it with: multi-pwsh alias set {} <stable|preview|lts|version>",
                    alias_name, alias_name
                ))
            })?;
            Ok(Version::parse(version_text)?)
        }
        HostSelector::Exact(version) => Ok(version.clone()),
        HostSelector::Major(major) => latest_installed_in_major(layout, *major)?.ok_or_else(|| {
            MultiPwshError::InvalidArguments(format!(
                "no installed PowerShell version found for major {}; install one with: multi-pwsh install {}",
                major, major
            ))
        }),
        HostSelector::MajorMinor(line) => {
            let pinned = read_minor_pin(layout, *line)?;
            if let Some(version) = pinned {
                return Ok(version);
            }

            latest_installed_in_line(layout, *line)?.ok_or_else(|| {
                MultiPwshError::InvalidArguments(format!(
                    "no installed PowerShell version found for line {}; install one with: multi-pwsh install {}",
                    line, line
                ))
            })
        }
    }
}

fn resolve_host_executable(layout: &InstallLayout, selector_input: &str) -> Result<(Version, PathBuf)> {
    let selector = parse_host_selector(selector_input)?;
    let version = resolve_host_version(layout, &selector)?;
    let executable = layout.version_executable(&version);

    if !executable.exists() {
        return Err(MultiPwshError::InvalidArguments(format!(
            "resolved host selector '{}' to {}, but executable was not found at {}",
            selector_input,
            version,
            executable.display()
        )));
    }

    Ok((version, executable))
}

fn validate_venv_name(value: &str) -> Result<&str> {
    if value.is_empty() {
        return Err(MultiPwshError::InvalidArguments(
            "virtual environment name cannot be empty".to_string(),
        ));
    }

    if value == "." || value == ".." {
        return Err(MultiPwshError::InvalidArguments(format!(
            "virtual environment name '{}' is reserved",
            value
        )));
    }

    if value
        .chars()
        .all(|character| character.is_ascii_alphanumeric() || matches!(character, '-' | '_' | '.'))
    {
        return Ok(value);
    }

    Err(MultiPwshError::InvalidArguments(format!(
        "virtual environment name '{}' is invalid; expected only ASCII letters, digits, '.', '-', or '_'",
        value
    )))
}

fn normalize_host_flag(arg: &OsStr) -> String {
    arg.to_string_lossy().to_ascii_lowercase()
}

fn is_virtual_environment_flag(arg: &OsStr) -> bool {
    matches!(
        normalize_host_flag(arg).as_str(),
        VIRTUAL_ENVIRONMENT_FLAG | VIRTUAL_ENVIRONMENT_SHORT_FLAG
    )
}

fn is_mcp_flag(arg: &OsStr) -> bool {
    matches!(normalize_host_flag(arg).as_str(), "-mcp" | "/mcp")
}

fn is_mcp_commands_flag(arg: &OsStr) -> bool {
    matches!(normalize_host_flag(arg).as_str(), "-mcpcommands" | "/mcpcommands")
}

fn is_option_like(arg: &OsStr) -> bool {
    let text = arg.to_string_lossy();
    text.starts_with('-') || text.starts_with('/')
}

fn parse_mcp_command_values(value: &OsStr) -> Vec<String> {
    value
        .to_string_lossy()
        .split([',', ';'])
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToOwned::to_owned)
        .collect()
}

fn extract_mcp_args(args: Vec<OsString>) -> Result<(Vec<OsString>, Option<HostMcpOptions>)> {
    let mut rewritten = Vec::with_capacity(args.len());
    let mut commands = Vec::new();
    let mut mcp_enabled = false;
    let mut index = 0;

    while index < args.len() {
        let arg = &args[index];

        if is_mcp_flag(arg.as_os_str()) {
            if mcp_enabled {
                return Err(MultiPwshError::InvalidArguments(
                    "-mcp can only be specified once".to_string(),
                ));
            }

            mcp_enabled = true;
            index += 1;
            continue;
        }

        if is_mcp_commands_flag(arg.as_os_str()) {
            if !commands.is_empty() {
                return Err(MultiPwshError::InvalidArguments(
                    "-McpCommands can only be specified once".to_string(),
                ));
            }

            index += 1;
            while index < args.len() {
                let value = &args[index];
                if is_option_like(value.as_os_str()) {
                    break;
                }

                commands.extend(parse_mcp_command_values(value.as_os_str()));
                index += 1;
            }

            if commands.is_empty() {
                return Err(MultiPwshError::InvalidArguments(
                    "-McpCommands requires at least one PowerShell command name".to_string(),
                ));
            }

            continue;
        }

        rewritten.push(arg.clone());
        index += 1;
    }

    if !mcp_enabled && !commands.is_empty() {
        return Err(MultiPwshError::InvalidArguments(
            "-McpCommands requires -mcp".to_string(),
        ));
    }

    if mcp_enabled && commands.is_empty() {
        return Err(MultiPwshError::InvalidArguments(
            "-mcp requires -McpCommands <command> [command ...]".to_string(),
        ));
    }

    let mcp = if mcp_enabled {
        Some(HostMcpOptions { commands })
    } else {
        None
    };

    Ok((rewritten, mcp))
}

fn is_command_flag(arg: &OsStr) -> bool {
    matches!(normalize_host_flag(arg).as_str(), "-command" | "-c" | "/command" | "/c")
}

fn is_file_flag(arg: &OsStr) -> bool {
    matches!(normalize_host_flag(arg).as_str(), "-file" | "-f" | "/file" | "/f")
}

fn escape_single_quoted_pwsh_literal(value: &str) -> String {
    value.replace('\'', "''")
}

fn virtual_environment_bootstrap_prelude() -> &'static str {
    concat!(
        "for ($__multiPwshAttempt = 0; $__multiPwshAttempt -lt 200; $__multiPwshAttempt++) { ",
        "  $__multiPwshImportModule = Get-Command Import-Module -ErrorAction SilentlyContinue; ",
        "  $__multiPwshInstalledModule = Get-Command Get-InstalledModule -ErrorAction SilentlyContinue; ",
        "  $__multiPwshPsRepository = Get-Command Get-PSRepository -ErrorAction SilentlyContinue; ",
        "  $__multiPwshInstalledPsResource = Get-Command Get-InstalledPSResource -ErrorAction SilentlyContinue; ",
        "  if ($__multiPwshImportModule -and $__multiPwshImportModule.CommandType -eq 'Alias' -and ",
        "      $__multiPwshInstalledModule -and $__multiPwshInstalledModule.CommandType -eq 'Alias' -and ",
        "      $__multiPwshPsRepository -and $__multiPwshPsRepository.CommandType -eq 'Alias' -and ",
        "      $__multiPwshInstalledPsResource -and $__multiPwshInstalledPsResource.CommandType -eq 'Alias') { break }; ",
        "  Start-Sleep -Milliseconds 10; ",
        "}; "
    )
}

fn bootstrap_virtual_environment_command(command: &OsStr) -> OsString {
    let escaped_command = escape_single_quoted_pwsh_literal(&command.to_string_lossy());
    OsString::from(format!(
        concat!(
            "$__multiPwshUserCommand = '{}'; ",
            "{}",
            "& ([scriptblock]::Create($__multiPwshUserCommand))"
        ),
        escaped_command,
        virtual_environment_bootstrap_prelude()
    ))
}

fn inject_virtual_environment_command_bootstrap(args: Vec<OsString>) -> Vec<OsString> {
    let mut rewritten = args;

    for index in 0..rewritten.len() {
        if !is_command_flag(rewritten[index].as_os_str()) {
            continue;
        }

        let Some(command) = rewritten.get(index + 1).cloned() else {
            break;
        };

        rewritten[index + 1] = bootstrap_virtual_environment_command(command.as_os_str());
        break;
    }

    rewritten
}

fn rewrite_virtual_environment_stdin_file(args: Vec<OsString>) -> Result<(Vec<OsString>, Option<tempfile::TempPath>)> {
    let mut rewritten = args;

    for index in 0..rewritten.len() {
        if !is_file_flag(rewritten[index].as_os_str()) {
            continue;
        }

        let Some(file_target) = rewritten.get(index + 1) else {
            break;
        };

        if file_target != OsStr::new("-") {
            break;
        }

        let mut user_script = String::new();
        io::stdin().read_to_string(&mut user_script)?;

        let mut script_file = tempfile::Builder::new()
            .prefix("multi-pwsh-venv-")
            .suffix(".ps1")
            .tempfile()?;
        script_file.write_all(virtual_environment_bootstrap_prelude().as_bytes())?;
        script_file.write_all(user_script.as_bytes())?;
        script_file.flush()?;

        let script_path = script_file.into_temp_path();
        rewritten[index + 1] = script_path.as_os_str().to_os_string();
        return Ok((rewritten, Some(script_path)));
    }

    Ok((rewritten, None))
}

fn extract_virtual_environment_arg(args: Vec<OsString>) -> Result<(Vec<OsString>, Option<String>)> {
    let mut virtual_environment_index = None;
    let mut virtual_environment_name = None;

    for (index, arg) in args.iter().enumerate() {
        if !is_virtual_environment_flag(arg.as_os_str()) {
            continue;
        }

        if virtual_environment_index.is_some() {
            return Err(MultiPwshError::InvalidArguments(
                "-VirtualEnvironment can only be specified once".to_string(),
            ));
        }

        let value = args.get(index + 1).ok_or_else(|| {
            MultiPwshError::InvalidArguments("-VirtualEnvironment requires a virtual environment name".to_string())
        })?;

        if is_option_like(value.as_os_str()) {
            return Err(MultiPwshError::InvalidArguments(
                "-VirtualEnvironment requires a virtual environment name".to_string(),
            ));
        }

        let value = value.to_string_lossy().into_owned();
        validate_venv_name(&value)?;

        virtual_environment_index = Some(index);
        virtual_environment_name = Some(value);
    }

    let Some(index) = virtual_environment_index else {
        return Ok((args, None));
    };

    let mut rewritten = Vec::with_capacity(args.len().saturating_sub(2));
    rewritten.extend_from_slice(&args[..index]);
    rewritten.extend_from_slice(&args[index + 2..]);

    Ok((rewritten, virtual_environment_name))
}

fn preprocess_host_args(args: Vec<OsString>) -> Result<HostDispatchOptions> {
    let (args, mcp) = extract_mcp_args(args)?;
    let (args, virtual_environment) = extract_virtual_environment_arg(args)?;
    let args = if virtual_environment.is_some() {
        inject_virtual_environment_command_bootstrap(args)
    } else {
        args
    };
    let pwsh_args = pwsh_host::preprocess_named_pipe_command_args(args)
        .map_err(|error| MultiPwshError::Host(format!("invalid host arguments: {}", error)))?;

    Ok(HostDispatchOptions {
        launch: HostLaunchOptions {
            pwsh_args,
            virtual_environment,
        },
        mcp,
    })
}

fn disable_powershell_update_notifications() {
    unsafe { env::set_var(POWERSHELL_UPDATECHECK_ENV_VAR, POWERSHELL_UPDATECHECK_OFF) };
}

fn resolve_virtual_environment_dir(layout: &InstallLayout, name: &str) -> Result<PathBuf> {
    let name = validate_venv_name(name)?;
    let venv_dir = layout.venv_dir(name);

    if !venv_dir.is_dir() {
        return Err(MultiPwshError::InvalidArguments(format!(
            "virtual environment '{}' was not found at {}; create it with: multi-pwsh venv create {}",
            name,
            venv_dir.display(),
            name
        )));
    }

    Ok(venv_dir)
}

fn zip_file_options() -> zip::write::FileOptions {
    zip::write::FileOptions::default().compression_method(zip::CompressionMethod::Deflated)
}

fn format_archive_entry_name(path: &Path) -> String {
    path.components()
        .filter_map(|component| match component {
            Component::Normal(value) => Some(value.to_string_lossy()),
            _ => None,
        })
        .collect::<Vec<_>>()
        .join("/")
}

fn append_directory_to_zip<W: io::Write + io::Seek>(
    writer: &mut zip::ZipWriter<W>,
    root_dir: &Path,
    current_dir: &Path,
) -> Result<()> {
    let mut entries: Vec<_> = fs::read_dir(current_dir)?.collect::<std::result::Result<Vec<_>, _>>()?;
    entries.sort_by_key(|entry| entry.file_name());

    for entry in entries {
        let entry_path = entry.path();
        let relative_path = entry_path.strip_prefix(root_dir).map_err(|error| {
            MultiPwshError::Archive(format!(
                "failed to strip archive root '{}' from '{}': {}",
                root_dir.display(),
                entry_path.display(),
                error
            ))
        })?;
        let archive_name = format_archive_entry_name(relative_path);

        if entry_path.is_dir() {
            writer.add_directory(format!("{}/", archive_name), zip_file_options())?;
            append_directory_to_zip(writer, root_dir, &entry_path)?;
            continue;
        }

        writer.start_file(archive_name, zip_file_options())?;
        let mut source = fs::File::open(&entry_path)?;
        io::copy(&mut source, writer)?;
    }

    Ok(())
}

fn export_virtual_environment_to_archive(venv_dir: &Path, archive_path: &Path) -> Result<()> {
    if let Some(parent) = archive_path.parent() {
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent)?;
        }
    }

    let archive_file = fs::File::create(archive_path)?;
    let mut writer = zip::ZipWriter::new(archive_file);
    append_directory_to_zip(&mut writer, venv_dir, venv_dir)?;
    writer.finish()?;
    Ok(())
}

fn sanitize_archive_entry_path(name: &str) -> Result<PathBuf> {
    let mut sanitized = PathBuf::new();

    for component in Path::new(name).components() {
        match component {
            Component::CurDir => {}
            Component::Normal(value) => sanitized.push(value),
            Component::Prefix(_) | Component::RootDir | Component::ParentDir => {
                return Err(MultiPwshError::Archive(format!(
                    "archive entry '{}' contains an invalid path",
                    name
                )));
            }
        }
    }

    Ok(sanitized)
}

fn import_virtual_environment_from_archive(venv_dir: &Path, archive_path: &Path) -> Result<()> {
    if !archive_path.is_file() {
        return Err(MultiPwshError::InvalidArguments(format!(
            "archive '{}' was not found",
            archive_path.display()
        )));
    }

    if venv_dir.exists() {
        return Err(MultiPwshError::InvalidArguments(format!(
            "virtual environment destination '{}' already exists",
            venv_dir.display()
        )));
    }

    fs::create_dir_all(venv_dir)?;

    let import_result = (|| -> Result<()> {
        let archive_file = fs::File::open(archive_path)?;
        let mut archive = zip::ZipArchive::new(archive_file)?;

        for index in 0..archive.len() {
            let mut entry = archive.by_index(index)?;
            let relative_path = sanitize_archive_entry_path(entry.name())?;

            if relative_path.as_os_str().is_empty() {
                continue;
            }

            let destination_path = venv_dir.join(relative_path);

            if entry.is_dir() {
                fs::create_dir_all(&destination_path)?;
                continue;
            }

            if let Some(parent) = destination_path.parent() {
                fs::create_dir_all(parent)?;
            }

            let mut destination = fs::File::create(&destination_path)?;
            io::copy(&mut entry, &mut destination)?;
        }

        Ok(())
    })();

    if let Err(error) = import_result {
        let _ = fs::remove_dir_all(venv_dir);
        return Err(error);
    }

    Ok(())
}

fn run_known_host_executable(
    executable: &Path,
    layout: Option<&InstallLayout>,
    selector_input: &str,
    pwsh_args: Vec<OsString>,
) -> Result<i32> {
    let pwsh_dir = executable.parent().ok_or_else(|| {
        MultiPwshError::Host(format!(
            "failed to determine the PowerShell home directory from {}",
            executable.display()
        ))
    })?;
    run_known_host_executable_for_pwsh_dir(pwsh_dir, layout, selector_input, pwsh_args)
}

fn run_known_host_executable_for_pwsh_dir(
    pwsh_dir: &Path,
    layout: Option<&InstallLayout>,
    selector_input: &str,
    pwsh_args: Vec<OsString>,
) -> Result<i32> {
    let os = HostOs::detect()?;
    let HostDispatchOptions { launch, mcp } = preprocess_host_args(pwsh_args)?;
    let HostLaunchOptions {
        pwsh_args,
        virtual_environment,
    } = launch;
    disable_powershell_update_notifications();

    let _virtual_environment_guards = virtual_environment
        .as_deref()
        .map(|name| {
            let layout = layout.ok_or_else(|| {
                MultiPwshError::InvalidArguments("-VirtualEnvironment requires a multi-pwsh layout".to_string())
            })?;
            resolve_virtual_environment_dir(layout, name)
        })
        .transpose()?
        .map(|venv_dir| configure_virtual_environment_host_env(os, &venv_dir))
        .transpose()?;

    if let Some(mcp) = mcp {
        if !pwsh_args.is_empty() {
            return Err(MultiPwshError::InvalidArguments(
                "host -mcp does not accept additional pwsh arguments; use -McpCommands to choose exposed commands"
                    .to_string(),
            ));
        }

        return mcp::run_stdio_mcp_server_for_pwsh_dir(pwsh_dir, &mcp.commands).map_err(|error| {
            MultiPwshError::Host(format!(
                "failed to start MCP host for selector '{}': {}",
                selector_input, error
            ))
        });
    }

    let (pwsh_args, _stdin_script_file) = if virtual_environment.is_some() {
        rewrite_virtual_environment_stdin_file(pwsh_args)?
    } else {
        (pwsh_args, None)
    };

    pwsh_host::run_pwsh_command_line_for_pwsh_dir(pwsh_dir, pwsh_args).map_err(|error| {
        MultiPwshError::Host(format!(
            "failed to start native host for selector '{}': {}",
            selector_input, error
        ))
    })
}

fn run_host_mode_with_layout(layout: InstallLayout, selector_input: &str, pwsh_args: Vec<OsString>) -> Result<i32> {
    layout.ensure_base_dirs()?;

    let (_version, executable) = resolve_host_executable(&layout, selector_input)?;
    run_known_host_executable(&executable, Some(&layout), selector_input, pwsh_args)
}

fn run_host_mode(selector_input: &str, pwsh_args: Vec<OsString>) -> Result<i32> {
    let os = HostOs::detect()?;
    let layout = default_current_user_layout(os)?;
    run_host_mode_with_layout(layout, selector_input, pwsh_args)
}

fn run_host_command(args: &[String]) -> Result<i32> {
    if args.is_empty() {
        return Err(MultiPwshError::InvalidArguments(
            "host requires: <version|major|major.minor|pwsh-alias> [-VirtualEnvironment <name>|-venv <name>] [pwsh arguments...] or -mcp -McpCommands <command> [command ...]"
                .to_string(),
        ));
    }

    let selector = &args[0];
    let pwsh_args: Vec<OsString> = args[1..].iter().map(OsString::from).collect();
    run_host_mode(selector, pwsh_args)
}

fn paths_refer_to_same_location(left: &Path, right: &Path) -> bool {
    match (fs::canonicalize(left), fs::canonicalize(right)) {
        (Ok(left), Ok(right)) => left == right,
        _ => left == right,
    }
}

fn executable_selector_name(executable_path: &Path) -> Option<String> {
    let file_name = executable_path.file_name()?.to_str()?;

    if file_name.len() > 4 && file_name.to_ascii_lowercase().ends_with(".exe") {
        return Some(file_name[..file_name.len() - 4].to_string());
    }

    Some(file_name.to_string())
}

fn detect_implicit_host_selector(bin_dir: &Path, executable_path: &Path) -> Option<String> {
    let selector = executable_selector_name(executable_path)?;
    if selector.eq_ignore_ascii_case("multi-pwsh") {
        return None;
    }

    if !is_supported_alias_command(&selector) {
        return None;
    }

    let parent = executable_path.parent()?;
    if !paths_refer_to_same_location(parent, bin_dir) {
        return None;
    }

    Some(selector)
}

fn is_exact_pwsh_executable_name(executable_path: &Path) -> bool {
    executable_selector_name(executable_path)
        .map(|selector| selector.eq_ignore_ascii_case("pwsh"))
        .unwrap_or(false)
}

fn has_pwsh_payload_markers(pwsh_dir: &Path) -> bool {
    pwsh_dir.join("pwsh.dll").is_file() && pwsh_dir.join("pwsh.runtimeconfig.json").is_file()
}

fn path_file_name_eq_ignore_ascii_case(path: &Path, expected: &str) -> bool {
    path.file_name()
        .and_then(|value| value.to_str())
        .map(|value| value.eq_ignore_ascii_case(expected))
        .unwrap_or(false)
}

fn resolve_runtime_native_apphost_payload_dir(native_dir: &Path) -> Option<PathBuf> {
    if !path_file_name_eq_ignore_ascii_case(native_dir, "native") {
        return None;
    }

    let rid_dir = native_dir.parent()?;
    let runtimes_dir = rid_dir.parent()?;
    if !path_file_name_eq_ignore_ascii_case(runtimes_dir, "runtimes") {
        return None;
    }

    let shared_payload_dir = runtimes_dir.parent()?;
    if has_pwsh_payload_markers(shared_payload_dir) {
        return Some(shared_payload_dir.to_path_buf());
    }

    None
}

fn resolve_local_pwsh_apphost_payload_dir(executable_path: &Path) -> Option<PathBuf> {
    if !is_exact_pwsh_executable_name(executable_path) {
        return None;
    }

    let executable_dir = executable_path.parent()?;

    if has_pwsh_payload_markers(executable_dir) {
        return Some(executable_dir.to_path_buf());
    }

    resolve_runtime_native_apphost_payload_dir(executable_dir)
}

fn infer_layout_from_host_shim(os: HostOs, executable_path: &Path) -> Option<InstallLayout> {
    let selector = executable_selector_name(executable_path)?;
    if selector.eq_ignore_ascii_case("multi-pwsh") || !is_supported_alias_command(&selector) {
        return None;
    }

    let bin_dir = executable_path.parent()?;
    if let Some(layout) = read_layout_hint(bin_dir, os).ok().flatten() {
        if detect_implicit_host_selector(&layout.bin_dir(), executable_path).is_some() {
            return Some(layout);
        }
    }

    if !bin_dir
        .file_name()
        .and_then(|value| value.to_str())
        .map(|value| value.eq_ignore_ascii_case("bin"))
        .unwrap_or(false)
    {
        return None;
    }

    let home = bin_dir.parent()?.to_path_buf();
    let layout = if home.join(PACKAGE_METADATA_FILE).exists() {
        InstallLayout::from_root_with_versions_dir(os, home.clone(), home.clone()).ok()?
    } else {
        InstallLayout::from_root(os, home).ok()?
    };
    detect_implicit_host_selector(&layout.bin_dir(), executable_path)?;

    Some(layout)
}

fn run_implicit_host_mode_if_needed() -> Result<Option<i32>> {
    let executable_path = env::current_exe()?;

    let args: Vec<OsString> = env::args_os().skip(1).collect();
    if let Some(pwsh_dir) = resolve_local_pwsh_apphost_payload_dir(&executable_path) {
        let os = HostOs::detect()?;
        let venv_layout = default_current_user_layout(os)?;
        let exit_code =
            run_known_host_executable_for_pwsh_dir(&pwsh_dir, Some(&venv_layout), "local pwsh apphost", args)?;
        return Ok(Some(exit_code));
    }

    let selector_name = match executable_selector_name(&executable_path) {
        Some(selector_name) => selector_name,
        None => return Ok(None),
    };
    if selector_name.eq_ignore_ascii_case("multi-pwsh") || !is_supported_alias_command(&selector_name) {
        return Ok(None);
    }

    let os = HostOs::detect()?;
    let Some(layout) = infer_layout_from_host_shim(os, &executable_path) else {
        return Ok(None);
    };
    let selector = selector_name;

    let exit_code = run_host_mode_with_layout(layout, &selector, args)?;
    Ok(Some(exit_code))
}

fn latest_installed_in_major(layout: &InstallLayout, major: u64) -> Result<Option<Version>> {
    let versions = layout.installed_versions()?;
    Ok(versions.into_iter().find(|version| version.major == major))
}

fn latest_installed_in_line(layout: &InstallLayout, line: MajorMinor) -> Result<Option<Version>> {
    let versions = layout.installed_versions()?;
    Ok(versions
        .into_iter()
        .find(|version| version.major == line.major && version.minor == line.minor))
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum SpecialAliasPolicy {
    Stable,
    Preview,
    Lts,
    Exact(Version),
}

impl SpecialAliasPolicy {
    fn as_metadata_value(&self) -> String {
        match self {
            SpecialAliasPolicy::Stable => "stable".to_string(),
            SpecialAliasPolicy::Preview => "preview".to_string(),
            SpecialAliasPolicy::Lts => "lts".to_string(),
            SpecialAliasPolicy::Exact(version) => version.to_string(),
        }
    }
}

fn parse_special_alias_policy(value: &str) -> Result<SpecialAliasPolicy> {
    match value.trim().to_ascii_lowercase().as_str() {
        "stable" => return Ok(SpecialAliasPolicy::Stable),
        "preview" => return Ok(SpecialAliasPolicy::Preview),
        "lts" => return Ok(SpecialAliasPolicy::Lts),
        _ => {}
    }

    Ok(SpecialAliasPolicy::Exact(parse_exact_version(value)?))
}

fn validate_special_alias_policy(alias_name: &str, policy: &SpecialAliasPolicy) -> Result<()> {
    match (alias_name, policy) {
        (PWSH_PREVIEW_ALIAS, SpecialAliasPolicy::Preview) => Ok(()),
        (PWSH_PREVIEW_ALIAS, SpecialAliasPolicy::Exact(version)) if !version.pre.is_empty() => Ok(()),
        (PWSH_PREVIEW_ALIAS, _) => Err(MultiPwshError::InvalidArguments(
            "pwsh-preview can only track preview or an exact prerelease version".to_string(),
        )),
        (PWSH_LTS_ALIAS, SpecialAliasPolicy::Lts) => Ok(()),
        (PWSH_LTS_ALIAS, SpecialAliasPolicy::Exact(version)) if is_current_lts_version(version) => Ok(()),
        (PWSH_LTS_ALIAS, _) => Err(MultiPwshError::InvalidArguments(
            "pwsh-lts can only track lts or an exact current LTS version".to_string(),
        )),
        (PWSH_ALIAS, _) => Ok(()),
        _ => Err(MultiPwshError::InvalidArguments(format!(
            "unsupported named alias '{}'",
            alias_name
        ))),
    }
}

fn resolve_special_alias_policy_from_installed(
    installed_versions: &[Version],
    policy: &SpecialAliasPolicy,
) -> Option<Version> {
    match policy {
        SpecialAliasPolicy::Stable => installed_versions
            .iter()
            .find(|version| version.pre.is_empty())
            .cloned(),
        SpecialAliasPolicy::Preview => installed_versions
            .iter()
            .find(|version| !version.pre.is_empty())
            .cloned(),
        SpecialAliasPolicy::Lts => installed_versions
            .iter()
            .find(|version| version.pre.is_empty() && is_current_lts_version(version))
            .cloned(),
        SpecialAliasPolicy::Exact(version) => installed_versions
            .iter()
            .find(|candidate| *candidate == version)
            .cloned(),
    }
}

fn refresh_special_aliases(layout: &InstallLayout, os: HostOs) -> Result<()> {
    let policies = aliases::read_special_alias_policies(layout)?;
    if policies.is_empty() {
        return Ok(());
    }

    let mut items: Vec<_> = policies.into_iter().collect();
    items.sort_by(|a, b| a.0.cmp(&b.0));
    let installed_versions = layout.installed_versions()?;

    for (alias_name, policy_text) in items {
        if !is_special_alias_command(&alias_name) {
            eprintln!("Skipping alias policy {}: unsupported named alias", alias_name);
            continue;
        }

        let policy = match parse_special_alias_policy(&policy_text) {
            Ok(policy) => policy,
            Err(error) => {
                eprintln!("Skipping alias policy {}: {}", alias_name, error);
                continue;
            }
        };

        let Some(version) = resolve_special_alias_policy_from_installed(&installed_versions, &policy) else {
            remove_alias(layout, os, &alias_name)?;
            println!(
                "Alias {} remains configured for {} but unresolved (no installed matching version)",
                alias_name, policy_text
            );
            continue;
        };

        let target = layout.version_executable(&version);
        if !target.exists() {
            remove_alias(layout, os, &alias_name)?;
            println!(
                "Alias {} remains configured for {} but unresolved (target executable is missing)",
                alias_name, policy_text
            );
            continue;
        }

        let alias_path = create_or_update_named_alias(layout, os, &alias_name, &version, &target)?;
        println!("Updated alias: {} -> {}", alias_name, version);
        println!("Alias path: {}", alias_path.display());
    }

    Ok(())
}

fn ensure_default_special_policy(layout: &InstallLayout, selector: &VersionSelector) -> Result<()> {
    match selector {
        VersionSelector::Stable => {
            ensure_special_alias_policy(layout, PWSH_ALIAS, &SpecialAliasPolicy::Stable.as_metadata_value())
        }
        VersionSelector::Preview => ensure_special_alias_policy(
            layout,
            PWSH_PREVIEW_ALIAS,
            &SpecialAliasPolicy::Preview.as_metadata_value(),
        ),
        VersionSelector::Lts => {
            ensure_special_alias_policy(layout, PWSH_LTS_ALIAS, &SpecialAliasPolicy::Lts.as_metadata_value())
        }
        _ => Ok(()),
    }
}

fn sync_minor_alias(layout: &InstallLayout, os: HostOs, line: MajorMinor) -> Result<Option<PathBuf>> {
    let pinned = read_minor_pin(layout, line)?;
    let target_version = match pinned {
        Some(version) => Some(version),
        None => latest_installed_in_line(layout, line)?,
    };

    let Some(target_version) = target_version else {
        let alias_name = format!("pwsh-{}.{}", line.major, line.minor);
        remove_alias(layout, os, &alias_name)?;
        return Ok(None);
    };

    let target = layout.version_executable(&target_version);
    if !target.exists() {
        return Ok(None);
    }

    let path = create_or_update_alias(layout, os, line, &target_version, &target)?;
    Ok(Some(path))
}

fn parse_alias_set_target(target: &str) -> Result<Option<Version>> {
    if target.eq_ignore_ascii_case("latest") {
        return Ok(None);
    }

    let version = parse_exact_version(target)?;
    Ok(Some(version))
}

fn parse_update_selector(value: &str) -> Result<VersionSelector> {
    match value.trim().to_ascii_lowercase().as_str() {
        "stable" => Ok(VersionSelector::Stable),
        "preview" => Ok(VersionSelector::Preview),
        "lts" => Ok(VersionSelector::Lts),
        _ => parse_major_minor_selector(value).map(VersionSelector::MajorMinor).map_err(|_| {
            MultiPwshError::InvalidArguments(format!(
                "update accepts stable, preview, lts, or a major.minor selector; use `multi-pwsh install {}` for exact versions, major selectors, or wildcard selectors",
                value
            ))
        }),
    }
}

fn offline_cache_from_env() -> Option<PathBuf> {
    path_env_var(MULTI_PWSH_OFFLINE_CACHE_ENV_VAR)
}

fn effective_offline_cache(cli_value: Option<PathBuf>) -> Option<PathBuf> {
    cli_value.or_else(offline_cache_from_env)
}

fn parse_offline_cache_option(
    args: &[String],
    index: usize,
    offline_cache: &mut Option<PathBuf>,
) -> Result<Option<usize>> {
    match args[index].as_str() {
        "--offline-cache" => {
            if index + 1 >= args.len() {
                return Err(MultiPwshError::InvalidArguments(
                    "expected value after --offline-cache".to_string(),
                ));
            }

            if offline_cache.is_some() {
                return Err(MultiPwshError::InvalidArguments(
                    "--offline-cache can only be specified once".to_string(),
                ));
            }

            *offline_cache = Some(PathBuf::from(&args[index + 1]));
            Ok(Some(index + 2))
        }
        _ => Ok(None),
    }
}

struct CacheWarmOptions {
    target_oses: Vec<HostOs>,
    target_arches: Vec<HostArch>,
    include_prerelease: bool,
    output: Option<PathBuf>,
    product: CacheProduct,
    os_wildcard: bool,
    arch_wildcard: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CacheProduct {
    PowerShell,
    MultiPwsh,
    All,
}

impl CacheProduct {
    fn includes_powershell(self) -> bool {
        matches!(self, CacheProduct::PowerShell | CacheProduct::All)
    }

    fn includes_multi_pwsh(self) -> bool {
        matches!(self, CacheProduct::MultiPwsh | CacheProduct::All)
    }
}

fn all_host_oses() -> Vec<HostOs> {
    vec![HostOs::Windows, HostOs::Macos, HostOs::Linux]
}

fn all_host_arches() -> Vec<HostArch> {
    vec![HostArch::X64, HostArch::X86, HostArch::Arm64, HostArch::Arm32]
}

fn parse_cache_warm_options(args: &[String]) -> Result<CacheWarmOptions> {
    let mut target_oses = vec![HostOs::detect()?];
    let mut target_arches = vec![HostArch::detect()];
    let mut os_specified = false;
    let mut arch_specified = false;
    let mut os_wildcard = false;
    let mut arch_wildcard = false;
    let mut include_prerelease = false;
    let mut output = None;
    let mut product = CacheProduct::All;
    let mut product_specified = false;
    let mut index = 0usize;

    while index < args.len() {
        match args[index].as_str() {
            "--os" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --os".to_string(),
                    ));
                }
                if os_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--os can only be specified once".to_string(),
                    ));
                }
                if args[index + 1].eq_ignore_ascii_case("all") {
                    target_oses = all_host_oses();
                    os_wildcard = true;
                } else {
                    target_oses = vec![HostOs::parse(&args[index + 1]).ok_or_else(|| {
                        MultiPwshError::InvalidArguments(format!(
                            "unsupported operating system '{}', expected one of: windows, linux, macos, all",
                            args[index + 1]
                        ))
                    })?];
                }
                os_specified = true;
                index += 2;
            }
            "--arch" | "-a" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --arch".to_string(),
                    ));
                }
                if arch_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--arch can only be specified once".to_string(),
                    ));
                }
                if args[index + 1].eq_ignore_ascii_case("all") {
                    target_arches = all_host_arches();
                    arch_wildcard = true;
                } else {
                    target_arches = vec![HostArch::parse(&args[index + 1]).ok_or_else(|| {
                        MultiPwshError::InvalidArguments(format!(
                            "unsupported architecture '{}', expected one of: x64, x86, arm64, arm32, all",
                            args[index + 1]
                        ))
                    })?];
                }
                arch_specified = true;
                index += 2;
            }
            "--include-prerelease" | "--prerelease" => {
                include_prerelease = true;
                index += 1;
            }
            "--output" | "-o" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --output".to_string(),
                    ));
                }
                if output.is_some() {
                    return Err(MultiPwshError::InvalidArguments(
                        "--output can only be specified once".to_string(),
                    ));
                }
                output = Some(PathBuf::from(&args[index + 1]));
                index += 2;
            }
            "--product" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --product".to_string(),
                    ));
                }
                if product_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--product can only be specified once".to_string(),
                    ));
                }
                product = match args[index + 1].to_ascii_lowercase().as_str() {
                    "powershell" | "pwsh" => CacheProduct::PowerShell,
                    "multi-pwsh" | "multipwsh" | "self" => CacheProduct::MultiPwsh,
                    "all" => CacheProduct::All,
                    value => {
                        return Err(MultiPwshError::InvalidArguments(format!(
                            "unsupported product '{}', expected one of: powershell, multi-pwsh, all",
                            value
                        )));
                    }
                };
                product_specified = true;
                index += 2;
            }
            _ => {
                return Err(MultiPwshError::InvalidArguments(
                    "expected optional --os <windows|linux|macos|all>, --arch <x64|x86|arm64|arm32|all>, --include-prerelease, --output <path>, and/or --product <powershell|multi-pwsh|all>".to_string(),
                ));
            }
        }
    }

    Ok(CacheWarmOptions {
        target_oses,
        target_arches,
        include_prerelease,
        output,
        product,
        os_wildcard,
        arch_wildcard,
    })
}

fn bundle_relative_path(parts: &[String]) -> String {
    parts.join("/")
}

fn bundle_path(root: &Path, relative_path: &str) -> PathBuf {
    let mut path = root.to_path_buf();
    for part in relative_path.split('/') {
        path.push(part);
    }
    path
}

fn cache_target_error_can_be_skipped(error: &MultiPwshError) -> bool {
    matches!(
        error,
        MultiPwshError::UnsupportedPlatform(_) | MultiPwshError::AssetNotFound(_) | MultiPwshError::ReleaseNotFound(_)
    )
}

fn resolve_cache_releases(
    client: &ReleaseClient,
    selector: VersionSelector,
    target_os: HostOs,
    target_arch: HostArch,
    include_prerelease: bool,
) -> Result<Vec<ResolvedRelease>> {
    match selector {
        VersionSelector::MajorMinorWildcard(line) => {
            client.resolve_all_in_line(line, target_os, target_arch, include_prerelease)
        }
        _ => Ok(vec![client.resolve_selector(
            selector,
            target_os,
            target_arch,
            include_prerelease,
        )?]),
    }
}

fn cache_release_artifacts(
    client: &ReleaseClient,
    output_root: &Path,
    manifest: &mut OfflinePowerShellManifest,
    release: &ResolvedRelease,
    target_os: HostOs,
    target_arch: HostArch,
) -> Result<()> {
    let release_dir = format!("v{}", release.version);
    let asset_relative_path = bundle_relative_path(&[
        "PowerShell".to_string(),
        release_dir.clone(),
        release.asset_name.clone(),
    ]);
    let asset_path = bundle_path(output_root, &asset_relative_path);
    if !asset_path.exists() {
        copy_asset_to_path(Some(client.http_client()), &release.asset_source, &asset_path)?;
    }

    let checksum_name = release.checksum_asset_name.clone().ok_or_else(|| {
        MultiPwshError::Archive(format!(
            "release '{}' is missing checksum asset metadata and cannot be mirrored safely",
            release.asset_name
        ))
    })?;
    let checksum_source = release.checksum_asset_source.as_ref().ok_or_else(|| {
        MultiPwshError::Archive(format!(
            "release '{}' is missing checksum asset source and cannot be mirrored safely",
            release.asset_name
        ))
    })?;
    let checksum_relative_path = bundle_relative_path(&["PowerShell".to_string(), release_dir, checksum_name.clone()]);
    let checksum_path = bundle_path(output_root, &checksum_relative_path);
    if !checksum_path.exists() {
        copy_asset_to_path(Some(client.http_client()), checksum_source, &checksum_path)?;
    }

    let cached_release = ResolvedRelease {
        version: release.version.clone(),
        asset_name: release.asset_name.clone(),
        asset_source: AssetSource::File(asset_path.clone()),
        checksum_asset_name: Some(checksum_name.clone()),
        checksum_asset_source: Some(AssetSource::File(checksum_path)),
    };
    validate_archive_checksum(None, &cached_release, &ChecksumSource::ReleaseAsset, &asset_path)?;

    manifest.upsert_asset(
        &release.version,
        !release.version.pre.is_empty(),
        OfflinePowerShellAsset {
            name: release.asset_name.clone(),
            path: asset_relative_path,
            os: target_os.as_manifest_value().to_string(),
            arch: target_arch.as_manifest_value().to_string(),
            checksum_name: Some(checksum_name),
            checksum_path: Some(checksum_relative_path),
        },
    );

    println!(
        "Cached PowerShell {} for {} {}: {}",
        release.version, target_os, target_arch, release.asset_name
    );
    Ok(())
}

fn multi_pwsh_asset_name(target_os: HostOs, target_arch: HostArch) -> Result<String> {
    let os = match target_os {
        HostOs::Windows => "windows",
        HostOs::Macos => "macos",
        HostOs::Linux => "linux",
    };

    let arch = match (target_os, target_arch) {
        (_, HostArch::X64) => "x64",
        (HostOs::Windows | HostOs::Macos | HostOs::Linux, HostArch::Arm64) => "arm64",
        (HostOs::Linux, HostArch::Arm32) => "arm",
        _ => {
            return Err(MultiPwshError::UnsupportedPlatform(format!(
                "multi-pwsh does not publish an archive for {} {}",
                target_os, target_arch
            )));
        }
    };

    Ok(format!("multi-pwsh-{}-{}.zip", os, arch))
}

fn cache_multi_pwsh_artifact(
    client: &ReleaseClient,
    output_root: &Path,
    target_os: HostOs,
    target_arch: HostArch,
) -> Result<()> {
    let version = Version::parse(env!("CARGO_PKG_VERSION"))?;
    let tag = format!("v{}", version);
    let asset_name = multi_pwsh_asset_name(target_os, target_arch)?;
    let asset_relative_path = bundle_relative_path(&["multi-pwsh".to_string(), tag.clone(), asset_name.clone()]);
    let checksum_relative_path =
        bundle_relative_path(&["multi-pwsh".to_string(), tag.clone(), "checksums.txt".to_string()]);
    let asset_path = bundle_path(output_root, &asset_relative_path);
    let checksum_path = bundle_path(output_root, &checksum_relative_path);
    let release_base = format!("https://github.com/Devolutions/multi-pwsh/releases/download/{}", tag);

    if !asset_path.exists() {
        copy_asset_to_path(
            Some(client.http_client()),
            &AssetSource::Url(format!("{}/{}", release_base, asset_name)),
            &asset_path,
        )?;
    }

    if !checksum_path.exists() {
        copy_asset_to_path(
            Some(client.http_client()),
            &AssetSource::Url(format!("{}/checksums.txt", release_base)),
            &checksum_path,
        )?;
    }

    let cached_release = ResolvedRelease {
        version,
        asset_name: asset_name.clone(),
        asset_source: AssetSource::File(asset_path.clone()),
        checksum_asset_name: Some("checksums.txt".to_string()),
        checksum_asset_source: Some(AssetSource::File(checksum_path)),
    };
    validate_archive_checksum(None, &cached_release, &ChecksumSource::ReleaseAsset, &asset_path)?;

    println!(
        "Cached multi-pwsh {} for {} {}",
        env!("CARGO_PKG_VERSION"),
        target_os,
        target_arch
    );
    Ok(())
}

fn run_cache_warm(selector_input: &str, options: CacheWarmOptions) -> Result<()> {
    let selector = parse_install_selector(selector_input)?;
    let os = HostOs::detect().unwrap_or(HostOs::Windows);
    let output_root = options.output.unwrap_or_else(|| {
        path_env_var("MULTI_PWSH_CACHE_DIR").unwrap_or_else(|| {
            default_current_user_layout(os)
                .map(|layout| layout.cache_dir())
                .unwrap_or_else(|_| PathBuf::from("."))
        })
    });
    fs::create_dir_all(&output_root)?;

    let token = env::var("GITHUB_TOKEN").ok();
    let release_client = ReleaseClient::new(token)?;
    let mut manifest = load_or_default_powershell_manifest(&output_root)?;
    let mut cached_count = 0usize;
    let mut multi_pwsh_count = 0usize;

    if options.product.includes_powershell() {
        for target_os in &options.target_oses {
            for target_arch in &options.target_arches {
                let releases = match resolve_cache_releases(
                    &release_client,
                    selector.clone(),
                    *target_os,
                    *target_arch,
                    options.include_prerelease,
                ) {
                    Ok(releases) => releases,
                    Err(error)
                        if (options.os_wildcard || options.arch_wildcard)
                            && cache_target_error_can_be_skipped(&error) =>
                    {
                        eprintln!("Skipping {} {}: {}", target_os, target_arch, error);
                        continue;
                    }
                    Err(error) => return Err(error),
                };

                for release in releases {
                    cache_release_artifacts(
                        &release_client,
                        &output_root,
                        &mut manifest,
                        &release,
                        *target_os,
                        *target_arch,
                    )?;
                    cached_count += 1;
                }
            }
        }
        save_powershell_manifest(&output_root, &manifest)?;
    }

    if options.product.includes_multi_pwsh() {
        for target_os in &options.target_oses {
            for target_arch in &options.target_arches {
                match cache_multi_pwsh_artifact(&release_client, &output_root, *target_os, *target_arch) {
                    Ok(()) => multi_pwsh_count += 1,
                    Err(error)
                        if (options.os_wildcard || options.arch_wildcard)
                            && cache_target_error_can_be_skipped(&error) =>
                    {
                        eprintln!("Skipping multi-pwsh {} {}: {}", target_os, target_arch, error);
                    }
                    Err(error) => return Err(error),
                }
            }
        }
    }

    println!("Offline cache root: {}", output_root.display());
    println!("PowerShell artifacts cached: {}", cached_count);
    println!("multi-pwsh artifacts cached: {}", multi_pwsh_count);
    Ok(())
}

fn run_cache(args: &[String]) -> Result<()> {
    if args.is_empty() {
        return Err(MultiPwshError::InvalidArguments(
            "cache requires: warm <selector>".to_string(),
        ));
    }

    match args[0].as_str() {
        "warm" => {
            if args.len() < 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "cache warm requires <stable|preview|lts|version|major|major.minor|major.minor.x>".to_string(),
                ));
            }
            let options = parse_cache_warm_options(&args[2..])?;
            run_cache_warm(&args[1], options)
        }
        _ => Err(MultiPwshError::InvalidArguments(
            "cache requires: warm <selector>".to_string(),
        )),
    }
}

fn run_venv(args: &[String]) -> Result<()> {
    if args.is_empty() {
        return Err(MultiPwshError::InvalidArguments(
            "venv requires: create <name>, delete <name>, export <name> <archive.zip>, import <name> <archive.zip>, or list"
                .to_string(),
        ));
    }

    let os = HostOs::detect()?;
    let layout = InstallLayout::new(os)?;
    layout.ensure_base_dirs()?;

    match args[0].as_str() {
        "create" => {
            if args.len() != 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "venv create requires: <name>".to_string(),
                ));
            }

            let name = validate_venv_name(&args[1])?;
            let venv_dir = layout.venv_dir(name);
            fs::create_dir_all(&venv_dir)?;
            fs::create_dir_all(venv_dir.join("Modules"))?;

            println!("Virtual environment: {}", name);
            println!("Path: {}", venv_dir.display());
            Ok(())
        }
        "delete" => {
            if args.len() != 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "venv delete requires: <name>".to_string(),
                ));
            }

            let name = validate_venv_name(&args[1])?;
            let venv_dir = layout.venv_dir(name);

            if !venv_dir.is_dir() {
                return Err(MultiPwshError::InvalidArguments(format!(
                    "virtual environment '{}' was not found at {}",
                    name,
                    venv_dir.display()
                )));
            }

            fs::remove_dir_all(&venv_dir)?;

            println!("Deleted virtual environment: {}", name);
            println!("Path: {}", venv_dir.display());
            Ok(())
        }
        "export" => {
            if args.len() != 3 {
                return Err(MultiPwshError::InvalidArguments(
                    "venv export requires: <name> <archive.zip>".to_string(),
                ));
            }

            let name = validate_venv_name(&args[1])?;
            let venv_dir = resolve_virtual_environment_dir(&layout, name)?;
            let archive_path = PathBuf::from(&args[2]);

            export_virtual_environment_to_archive(&venv_dir, &archive_path)?;

            println!("Exported virtual environment: {}", name);
            println!("Archive: {}", archive_path.display());
            Ok(())
        }
        "import" => {
            if args.len() != 3 {
                return Err(MultiPwshError::InvalidArguments(
                    "venv import requires: <name> <archive.zip>".to_string(),
                ));
            }

            let name = validate_venv_name(&args[1])?;
            let venv_dir = layout.venv_dir(name);
            let archive_path = PathBuf::from(&args[2]);

            import_virtual_environment_from_archive(&venv_dir, &archive_path)?;

            println!("Imported virtual environment: {}", name);
            println!("Path: {}", venv_dir.display());
            println!("Archive: {}", archive_path.display());
            Ok(())
        }
        "list" => {
            if args.len() != 1 {
                return Err(MultiPwshError::InvalidArguments(
                    "venv list does not accept additional arguments".to_string(),
                ));
            }

            let venvs_dir = layout.venvs_dir();
            println!("Venv root: {}", venvs_dir.display());

            let mut entries: Vec<String> = fs::read_dir(&venvs_dir)?
                .filter_map(|entry| {
                    let entry = entry.ok()?;
                    if !entry.path().is_dir() {
                        return None;
                    }

                    entry.file_name().into_string().ok()
                })
                .collect();
            entries.sort();

            if entries.is_empty() {
                println!("Virtual environments: (none)");
            } else {
                println!("Virtual environments:");
                for entry in entries {
                    println!("  - {}", entry);
                }
            }

            Ok(())
        }
        _ => Err(MultiPwshError::InvalidArguments(
            "venv requires: create <name>, delete <name>, export <name> <archive.zip>, import <name> <archive.zip>, or list"
                .to_string(),
        )),
    }
}

fn run_alias(args: &[String]) -> Result<()> {
    if args.is_empty() {
        return Err(MultiPwshError::InvalidArguments(
            "alias requires: set <major.minor> <version|latest>, set <pwsh|pwsh-preview|pwsh-lts> <stable|preview|lts|version>, unset <major.minor>, or unset <pwsh|pwsh-preview|pwsh-lts>".to_string(),
        ));
    }

    let os = HostOs::detect()?;
    let layout = default_current_user_layout(os)?;
    layout.ensure_base_dirs()?;

    match args[0].as_str() {
        "set" => {
            if args.len() != 3 {
                return Err(MultiPwshError::InvalidArguments(
                    "alias set requires: <major.minor> <version|latest> or <pwsh|pwsh-preview|pwsh-lts> <stable|preview|lts|version>".to_string(),
                ));
            }

            if is_special_alias_command(&args[1]) {
                let policy = parse_special_alias_policy(&args[2])?;
                validate_special_alias_policy(&args[1], &policy)?;
                let policy_text = policy.as_metadata_value();
                set_special_alias_policy(&layout, &args[1], Some(&policy_text))?;
                refresh_special_aliases(&layout, os)?;
                println!("Configured alias {} to follow {}", args[1], policy_text);
                return Ok(());
            }

            let line = parse_major_minor_selector(&args[1])?;
            let target = parse_alias_set_target(&args[2])?;

            if let Some(version) = target.as_ref() {
                if version.major != line.major || version.minor != line.minor {
                    return Err(MultiPwshError::InvalidArguments(format!(
                        "version {} does not match alias line {}",
                        version, line
                    )));
                }
            }

            set_minor_pin(&layout, line, target.clone())?;

            let alias_name = format!("pwsh-{}.{}", line.major, line.minor);
            if let Some(version) = target {
                let target_path = layout.version_executable(&version);
                if target_path.exists() {
                    let alias_path = create_or_update_alias(&layout, os, line, &version, &target_path)?;
                    println!("Pinned alias {} to {}", alias_name, version);
                    println!("Updated alias: {}", alias_path.display());
                } else {
                    remove_alias(&layout, os, &alias_name)?;
                    println!(
                        "Pinned alias {} to {} (target is not currently installed; alias is unresolved)",
                        alias_name, version
                    );
                }
            } else {
                let alias_path = sync_minor_alias(&layout, os, line)?;
                println!("Unpinned alias {} (now follows latest in line)", alias_name);
                if let Some(path) = alias_path {
                    println!("Updated alias: {}", path.display());
                }
            }

            Ok(())
        }
        "unset" => {
            if args.len() != 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "alias unset requires: <major.minor> or <pwsh|pwsh-preview|pwsh-lts>".to_string(),
                ));
            }

            if is_special_alias_command(&args[1]) {
                set_special_alias_policy(&layout, &args[1], None)?;
                if remove_alias(&layout, os, &args[1])? {
                    println!("Removed alias {}", args[1]);
                }
                println!("Removed policy for {}", args[1]);
                return Ok(());
            }

            let line = parse_major_minor_selector(&args[1])?;
            set_minor_pin(&layout, line, None)?;

            let alias_path = sync_minor_alias(&layout, os, line)?;
            println!(
                "Removed pin for pwsh-{}.{}, now following latest in line",
                line.major, line.minor
            );
            if let Some(path) = alias_path {
                println!("Updated alias: {}", path.display());
            }
            Ok(())
        }
        _ => Err(MultiPwshError::InvalidArguments(
            "alias requires: set <major.minor> <version|latest>, set <pwsh|pwsh-preview|pwsh-lts> <stable|preview|lts|version>, unset <major.minor>, or unset <pwsh|pwsh-preview|pwsh-lts>".to_string(),
        )),
    }
}

#[cfg(test)]
fn parse_release_selection_options(args: &[String]) -> Result<ReleaseSelectionOptions> {
    let mut arch = None;
    let mut arch_specified = false;
    let mut include_prerelease = false;
    let mut checksum_source = ChecksumSource::ReleaseAsset;
    let mut checksum_source_specified = false;
    let mut offline_cache = None;

    let mut index = 0usize;
    while index < args.len() {
        if let Some(next_index) = parse_offline_cache_option(args, index, &mut offline_cache)? {
            index = next_index;
            continue;
        }

        if let Some(next_index) =
            parse_checksum_cli_option(args, index, &mut checksum_source, &mut checksum_source_specified)?
        {
            index = next_index;
            continue;
        }

        match args[index].as_str() {
            "--arch" | "-a" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --arch".to_string(),
                    ));
                }

                if arch_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--arch can only be specified once".to_string(),
                    ));
                }
                arch_specified = true;

                if args[index + 1] == "auto" {
                    arch = None;
                } else {
                    arch = Some(HostArch::parse(&args[index + 1]).ok_or_else(|| {
                        MultiPwshError::InvalidArguments(format!(
                            "unsupported architecture '{}', expected one of: auto, x64, x86, arm64, arm32",
                            args[index + 1]
                        ))
                    })?);
                }

                index += 2;
            }
            "--include-prerelease" | "--prerelease" => {
                include_prerelease = true;
                index += 1;
            }
            _ => {
                return Err(MultiPwshError::InvalidArguments(
                    "expected optional --arch <value>, --include-prerelease, --offline-cache <path>, --skip-hash-verification, and/or --hash-file <url-or-path>".to_string(),
                ));
            }
        }
    }

    Ok(ReleaseSelectionOptions {
        arch,
        include_prerelease,
        checksum_source,
        offline_cache,
    })
}

fn parse_checksum_cli_option(
    args: &[String],
    index: usize,
    checksum_source: &mut ChecksumSource,
    checksum_source_specified: &mut bool,
) -> Result<Option<usize>> {
    match args[index].as_str() {
        "--skip-hash-verification" | "--skip-checksum-verification" => {
            set_checksum_source(checksum_source, checksum_source_specified, ChecksumSource::Skip)?;
            Ok(Some(index + 1))
        }
        "--hash-file" | "--checksum-file" => {
            if index + 1 >= args.len() {
                return Err(MultiPwshError::InvalidArguments(
                    "expected value after --hash-file".to_string(),
                ));
            }

            let parsed = parse_checksum_source_argument(&args[index + 1])?;
            set_checksum_source(checksum_source, checksum_source_specified, parsed)?;
            Ok(Some(index + 2))
        }
        _ => Ok(None),
    }
}

fn set_checksum_source(
    checksum_source: &mut ChecksumSource,
    checksum_source_specified: &mut bool,
    next_source: ChecksumSource,
) -> Result<()> {
    if *checksum_source_specified {
        return Err(MultiPwshError::InvalidArguments(
            "checksum verification source can only be specified once; choose only one of --skip-hash-verification or --hash-file <url-or-path>".to_string(),
        ));
    }

    *checksum_source = next_source;
    *checksum_source_specified = true;
    Ok(())
}

fn parse_checksum_source_argument(value: &str) -> Result<ChecksumSource> {
    let normalized = value.to_ascii_lowercase();
    if normalized.starts_with("http://") || normalized.starts_with("https://") {
        return Ok(ChecksumSource::Url(value.to_string()));
    }

    if value.contains("://") {
        return Err(MultiPwshError::InvalidArguments(format!(
            "unsupported checksum URL '{}'; expected http:// or https://, or provide a local file path",
            value
        )));
    }

    Ok(ChecksumSource::File(PathBuf::from(value)))
}

fn parse_package_layout_options(args: &[String]) -> Result<PackageLayoutOptions> {
    let mut scope = PackageScope::CurrentUser;
    let mut scope_specified = false;
    let mut root = None;
    let mut index = 0usize;

    while index < args.len() {
        match args[index].as_str() {
            "--scope" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --scope".to_string(),
                    ));
                }

                if scope_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--scope can only be specified once".to_string(),
                    ));
                }

                scope = PackageScope::parse(&args[index + 1]).ok_or_else(|| {
                    MultiPwshError::InvalidArguments(format!(
                        "unsupported scope '{}', expected one of: user, machine",
                        args[index + 1]
                    ))
                })?;
                scope_specified = true;
                index += 2;
            }
            "--root" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --root".to_string(),
                    ));
                }

                if root.is_some() {
                    return Err(MultiPwshError::InvalidArguments(
                        "--root can only be specified once".to_string(),
                    ));
                }

                root = Some(PathBuf::from(&args[index + 1]));
                index += 2;
            }
            _ => {
                return Err(MultiPwshError::InvalidArguments(
                    "expected optional --scope <user|machine> and/or --root <path>".to_string(),
                ));
            }
        }
    }

    if root.is_some() && !scope_specified {
        return Err(MultiPwshError::InvalidArguments(
            "--root requires --scope <user|machine>".to_string(),
        ));
    }

    Ok(PackageLayoutOptions { scope, root })
}

fn parse_package_install_options(args: &[String]) -> Result<InstallCommandOptions> {
    let os = HostOs::detect()?;
    parse_package_install_options_for_os(args, os)
}

fn parse_package_install_options_for_os(args: &[String], os: HostOs) -> Result<InstallCommandOptions> {
    let mut options = PackageInstallOptions::with_platform_defaults(PackageScope::CurrentUser, os);
    let mut scope_specified = false;
    let mut root_specified = false;
    let mut arch_specified = false;
    let mut add_path_specified = false;
    let mut register_manifest_specified = false;
    let mut enable_psremoting_specified = false;
    let mut disable_telemetry_specified = false;
    let mut add_explorer_context_menu_specified = false;
    let mut add_file_context_menu_specified = false;
    let mut use_mu_specified = false;
    let mut enable_mu_specified = false;
    let mut checksum_source = ChecksumSource::ReleaseAsset;
    let mut checksum_source_specified = false;
    let mut offline_cache = None;
    let mut index = 0usize;

    while index < args.len() {
        if let Some(next_index) = parse_offline_cache_option(args, index, &mut offline_cache)? {
            index = next_index;
            continue;
        }

        if let Some(next_index) =
            parse_checksum_cli_option(args, index, &mut checksum_source, &mut checksum_source_specified)?
        {
            index = next_index;
            continue;
        }

        match args[index].as_str() {
            "--scope" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --scope".to_string(),
                    ));
                }

                if scope_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--scope can only be specified once".to_string(),
                    ));
                }

                let scope = PackageScope::parse(&args[index + 1]).ok_or_else(|| {
                    MultiPwshError::InvalidArguments(format!(
                        "unsupported scope '{}', expected one of: user, machine",
                        args[index + 1]
                    ))
                })?;
                let previous = options.clone();
                options = PackageInstallOptions::with_platform_defaults(scope, os);
                options.arch = previous.arch;
                options.include_prerelease = previous.include_prerelease;
                options.install_root = previous.install_root;
                if add_path_specified {
                    options.add_path = previous.add_path;
                }
                if register_manifest_specified {
                    options.register_manifest = previous.register_manifest;
                }
                if enable_psremoting_specified {
                    options.enable_psremoting = previous.enable_psremoting;
                }
                if disable_telemetry_specified {
                    options.disable_telemetry = previous.disable_telemetry;
                }
                if add_explorer_context_menu_specified {
                    options.add_explorer_context_menu = previous.add_explorer_context_menu;
                }
                if add_file_context_menu_specified {
                    options.add_file_context_menu = previous.add_file_context_menu;
                }
                if use_mu_specified {
                    options.use_mu = previous.use_mu;
                }
                if enable_mu_specified {
                    options.enable_mu = previous.enable_mu;
                }
                scope_specified = true;
                index += 2;
            }
            "--root" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --root".to_string(),
                    ));
                }

                if root_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--root can only be specified once".to_string(),
                    ));
                }

                options.install_root = Some(PathBuf::from(&args[index + 1]));
                root_specified = true;
                index += 2;
            }
            "--arch" | "-a" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --arch".to_string(),
                    ));
                }

                if arch_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--arch can only be specified once".to_string(),
                    ));
                }

                options.arch = if args[index + 1] == "auto" {
                    None
                } else {
                    Some(HostArch::parse(&args[index + 1]).ok_or_else(|| {
                        MultiPwshError::InvalidArguments(format!(
                            "unsupported architecture '{}', expected one of: auto, x64, x86, arm64, arm32",
                            args[index + 1]
                        ))
                    })?)
                };
                arch_specified = true;
                index += 2;
            }
            "--include-prerelease" | "--prerelease" => {
                options.include_prerelease = true;
                index += 1;
            }
            "--add-path" => {
                if os != HostOs::Windows {
                    return Err(MultiPwshError::InvalidArguments(
                        "--add-path is supported only on Windows; add the alias bin directory to PATH manually on macOS/Linux"
                            .to_string(),
                    ));
                }
                options.add_path = true;
                add_path_specified = true;
                index += 1;
            }
            "--no-add-path" => {
                options.add_path = false;
                add_path_specified = true;
                index += 1;
            }
            "--register-manifest" => {
                options.register_manifest = true;
                register_manifest_specified = true;
                index += 1;
            }
            "--no-register-manifest" => {
                options.register_manifest = false;
                register_manifest_specified = true;
                index += 1;
            }
            "--enable-psremoting" => {
                options.enable_psremoting = true;
                enable_psremoting_specified = true;
                index += 1;
            }
            "--disable-telemetry" => {
                options.disable_telemetry = true;
                disable_telemetry_specified = true;
                index += 1;
            }
            "--add-explorer-context-menu" => {
                options.add_explorer_context_menu = true;
                add_explorer_context_menu_specified = true;
                index += 1;
            }
            "--add-file-context-menu" => {
                options.add_file_context_menu = true;
                add_file_context_menu_specified = true;
                index += 1;
            }
            "--use-mu" => {
                options.use_mu = true;
                use_mu_specified = true;
                index += 1;
            }
            "--no-use-mu" => {
                options.use_mu = false;
                use_mu_specified = true;
                index += 1;
            }
            "--enable-mu" => {
                options.enable_mu = true;
                enable_mu_specified = true;
                index += 1;
            }
            "--no-enable-mu" => {
                options.enable_mu = false;
                enable_mu_specified = true;
                index += 1;
            }
            _ => {
                return Err(MultiPwshError::InvalidArguments(
                    "unexpected install options; expected scope/root/arch/include-prerelease/checksum flags and Windows integration flags"
                        .to_string(),
                ));
            }
        }
    }

    if root_specified && !scope_specified {
        return Err(MultiPwshError::InvalidArguments(
            "--root requires --scope <user|machine> for install and update".to_string(),
        ));
    }

    options.validate(os)?;
    Ok(InstallCommandOptions {
        package: options,
        checksum_source,
        offline_cache,
    })
}

fn parse_package_uninstall_options(args: &[String]) -> Result<(PackageLayoutOptions, bool)> {
    let mut scope = PackageScope::CurrentUser;
    let mut scope_specified = false;
    let mut root = None;
    let mut force = false;
    let mut index = 0usize;

    while index < args.len() {
        match args[index].as_str() {
            "--scope" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --scope".to_string(),
                    ));
                }

                if scope_specified {
                    return Err(MultiPwshError::InvalidArguments(
                        "--scope can only be specified once".to_string(),
                    ));
                }

                scope = PackageScope::parse(&args[index + 1]).ok_or_else(|| {
                    MultiPwshError::InvalidArguments(format!(
                        "unsupported scope '{}', expected one of: user, machine",
                        args[index + 1]
                    ))
                })?;
                scope_specified = true;
                index += 2;
            }
            "--root" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --root".to_string(),
                    ));
                }

                if root.is_some() {
                    return Err(MultiPwshError::InvalidArguments(
                        "--root can only be specified once".to_string(),
                    ));
                }

                root = Some(PathBuf::from(&args[index + 1]));
                index += 2;
            }
            "--force" => {
                force = true;
                index += 1;
            }
            _ => {
                return Err(MultiPwshError::InvalidArguments(
                    "expected optional --scope <user|machine>, --root <path>, and/or --force".to_string(),
                ));
            }
        }
    }

    if root.is_some() && !scope_specified {
        return Err(MultiPwshError::InvalidArguments(
            "--root requires --scope <user|machine>".to_string(),
        ));
    }

    Ok((PackageLayoutOptions { scope, root }, force))
}

fn parse_windows_uninstall_options(args: &[String]) -> Result<WindowsUninstallOptions> {
    let mut scope = None;
    let mut root = None;
    let mut force = false;
    let mut index = 0usize;

    while index < args.len() {
        match args[index].as_str() {
            "--scope" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --scope".to_string(),
                    ));
                }

                if scope.is_some() {
                    return Err(MultiPwshError::InvalidArguments(
                        "--scope can only be specified once".to_string(),
                    ));
                }

                scope = Some(PackageScope::parse(&args[index + 1]).ok_or_else(|| {
                    MultiPwshError::InvalidArguments(format!(
                        "unsupported scope '{}', expected one of: user, machine",
                        args[index + 1]
                    ))
                })?);
                index += 2;
            }
            "--root" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --root".to_string(),
                    ));
                }

                if root.is_some() {
                    return Err(MultiPwshError::InvalidArguments(
                        "--root can only be specified once".to_string(),
                    ));
                }

                root = Some(PathBuf::from(&args[index + 1]));
                index += 2;
            }
            "--force" => {
                force = true;
                index += 1;
            }
            _ => {
                return Err(MultiPwshError::InvalidArguments(
                    "expected optional --scope <user|machine>, --root <path>, and/or --force".to_string(),
                ));
            }
        }
    }

    Ok(WindowsUninstallOptions { scope, root, force })
}

fn run_package_install(selector_input: &str, install_options: InstallCommandOptions) -> Result<()> {
    let selector = parse_install_selector(selector_input)?;
    let os = HostOs::detect()?;
    let options = install_options.package;
    let checksum_source = install_options.checksum_source;
    let offline_cache = install_options.offline_cache;
    let arch = options.arch.unwrap_or_else(HostArch::detect);
    let layout = package_layout(os, arch, options.scope, options.install_root.clone())?;
    layout.ensure_base_dirs()?;

    let release_resolver = ReleaseResolver::new(offline_cache)?;
    let releases = match selector.clone() {
        VersionSelector::MajorMinorWildcard(line) => {
            release_resolver.resolve_all_in_line(line, os, arch, options.include_prerelease)?
        }
        _ => vec![release_resolver.resolve_selector(selector.clone(), os, arch, options.include_prerelease)?],
    };

    let mut metadata = load_package_metadata(&layout)?;
    let mut touched_lines: Vec<MajorMinor> = Vec::new();
    let mut touched_majors: Vec<u64> = Vec::new();

    for release in releases {
        let executable_path =
            ensure_installed(&layout, release_resolver.http_client(), os, &release, &checksum_source)?;
        let patch_alias = create_or_update_patch_alias(&layout, os, &release.version, &executable_path)?;
        let version_path = executable_path.parent().unwrap_or_else(|| Path::new(""));

        run_install_time_actions(&executable_path, &options)?;
        persist_installer_properties(&layout, options.scope, &release.version, &options)?;
        persist_installed_version_registration(options.scope, &release.version, &executable_path)?;
        metadata.upsert_install(&release.version, &layout, &options);

        println!("Installed PowerShell {}", release.version);
        println!("Scope: {}", options.scope.display_name());
        println!("Install root: {}", layout.home().display());
        println!("Version path: {}", version_path.display());
        println!("Updated patch alias: {}", patch_alias.display());

        let line = release.version_line();
        if !touched_lines.contains(&line) {
            touched_lines.push(line);
        }
        if !touched_majors.contains(&release.version.major) {
            touched_majors.push(release.version.major);
        }
    }

    save_package_metadata(&layout, &metadata)?;
    reconcile_shared_integrations(&layout, options.scope, &metadata)?;

    touched_lines.sort();
    touched_majors.sort();

    for line in touched_lines {
        let pinned = read_minor_pin(&layout, line)?;
        let alias_path = sync_minor_alias(&layout, os, line)?;
        match alias_path {
            Some(path) => println!("Updated alias: {}", path.display()),
            None if pinned.is_some() => {
                println!(
                    "Alias pwsh-{}.{} remains pinned but unresolved (target is not installed)",
                    line.major, line.minor
                );
            }
            None => {}
        }
    }

    for major in touched_majors {
        let major_alias_path = latest_installed_in_major(&layout, major)?
            .map(|version| {
                let target = layout.version_executable(&version);
                create_or_update_major_alias(&layout, os, version.major, &version, &target)
            })
            .transpose()?;

        if let Some(path) = major_alias_path {
            println!("Updated major alias: {}", path.display());
        }
    }

    ensure_default_special_policy(&layout, &selector)?;
    refresh_special_aliases(&layout, os)?;

    println!("Alias bin: {}", layout.bin_dir().display());
    match os {
        HostOs::Windows if options.add_path => {
            println!(
                "PATH entry updated for scope {}: {}",
                options.scope.display_name(),
                layout.bin_dir().display()
            );
        }
        HostOs::Windows => {
            println!(
                "PATH update skipped; add manually if needed: {}",
                layout.bin_dir().display()
            );
        }
        HostOs::Linux | HostOs::Macos => {
            println!("Add to PATH manually for this scope: {}", layout.bin_dir().display());
        }
    }

    Ok(())
}

fn run_package_uninstall(version_input: &str, layout_options: PackageLayoutOptions, force: bool) -> Result<()> {
    let version = parse_exact_version(version_input)?;
    let os = HostOs::detect()?;
    let arch = HostArch::detect();
    let layout = package_layout(os, arch, layout_options.scope, layout_options.root)?;
    layout.ensure_base_dirs()?;

    let mut metadata = load_package_metadata(&layout)?;
    let removed_files = layout.remove_version_dirs(&version)?;
    let removed_metadata = metadata.remove_install(&version);

    if !removed_files && !removed_metadata && !force {
        return Err(MultiPwshError::InvalidArguments(format!(
            "version {} is not installed in scope {} (use --force to ignore)",
            version,
            layout_options.scope.display_name()
        )));
    }

    if removed_files || removed_metadata {
        println!("Removed PowerShell {}", version);
        println!("Scope: {}", layout_options.scope.display_name());
    } else {
        println!(
            "PowerShell {} is not installed; continuing because --force was provided",
            version
        );
    }

    remove_installed_version_registration(layout_options.scope, &version)?;
    save_package_metadata(&layout, &metadata)?;
    reconcile_shared_integrations(&layout, layout_options.scope, &metadata)?;
    cleanup_aliases_for_removed_version(&layout, os, &version)?;
    Ok(())
}

fn run_package_list(layout_options: PackageLayoutOptions) -> Result<()> {
    let os = HostOs::detect()?;
    let arch = HostArch::detect();
    let layout = package_layout(os, arch, layout_options.scope, layout_options.root)?;
    let metadata = load_package_metadata(&layout)?;

    println!("Scope: {}", layout_options.scope.display_name());
    println!("Install root: {}", layout.home().display());
    println!("Alias bin: {}", layout.bin_dir().display());
    println!("Versions dir: {}", layout.versions_dir().display());
    println!("Metadata file: {}", package::package_metadata_file(&layout).display());
    println!();

    let records = metadata.resolved_records()?;
    if records.is_empty() {
        println!("Installed versions: (none)");
        print_alias_metadata(&layout)?;
        return Ok(());
    }

    println!("Installed versions:");
    for record in records {
        println!("  - {}", record.version);
        println!("    path: {}", record.record.install_dir);
        println!(
            "    options: add_path={}, register_manifest={}, enable_psremoting={}, disable_telemetry={}, add_explorer_context_menu={}, add_file_context_menu={}",
            record.record.add_path,
            record.record.register_manifest,
            record.record.enable_psremoting,
            record.record.disable_telemetry,
            record.record.add_explorer_context_menu,
            record.record.add_file_context_menu
        );
    }

    print_alias_metadata(&layout)?;

    Ok(())
}

fn package_version_present(layout: &InstallLayout, version: &Version) -> Result<bool> {
    if layout.version_dir(version).exists() {
        return Ok(true);
    }

    let metadata = load_package_metadata(layout)?;
    Ok(metadata
        .resolved_records()?
        .iter()
        .any(|record| record.version == *version))
}

fn run_scoped_uninstall(version_input: &str, options: WindowsUninstallOptions) -> Result<()> {
    if options.root.is_some() && options.scope.is_none() {
        return Err(MultiPwshError::InvalidArguments(
            "--root requires --scope <user|machine> for uninstall".to_string(),
        ));
    }

    if options.scope.is_none() && options.root.is_none() {
        let version = parse_exact_version(version_input)?;
        let os = HostOs::detect()?;
        let arch = HostArch::detect();
        let user_layout = package_layout(os, arch, PackageScope::CurrentUser, None)?;

        if !package_version_present(&user_layout, &version)? {
            let machine_layout = package_layout(os, arch, PackageScope::AllUsers, None)?;
            if package_version_present(&machine_layout, &version)? {
                return Err(MultiPwshError::InvalidArguments(format!(
                    "version {} is not installed in scope user but is installed in scope machine; rerun with --scope machine to uninstall it",
                    version
                )));
            }
        }
    }

    let scope = options.scope.unwrap_or(PackageScope::CurrentUser);

    run_package_uninstall(
        version_input,
        PackageLayoutOptions {
            scope,
            root: options.root,
        },
        options.force,
    )
}

fn format_special_alias_policy_line(alias_name: &str, policy_text: &str, aliases: &HashMap<String, String>) -> String {
    match aliases.get(alias_name) {
        Some(version) => format!("  - {} follows {} -> {}", alias_name, policy_text, version),
        None => format!("  - {} follows {} -> unresolved", alias_name, policy_text),
    }
}

fn print_alias_metadata(layout: &InstallLayout) -> Result<()> {
    let aliases = aliases::read_alias_metadata(layout)?;
    let policies = aliases::read_special_alias_policies(layout)?;
    let pins = read_minor_pins(layout)?;

    println!();
    if aliases.is_empty() {
        println!("Aliases: (none)");
    } else {
        println!("Aliases:");
        let mut items: Vec<_> = aliases.iter().collect();
        items.sort_by(|a, b| a.0.cmp(b.0));
        for (alias, version) in items {
            println!("  - {} -> {}", alias, version);
        }
    }

    println!();
    if policies.is_empty() {
        println!("Named alias policies: (none)");
    } else {
        println!("Named alias policies:");
        let mut items: Vec<_> = policies.into_iter().collect();
        items.sort_by(|a, b| a.0.cmp(&b.0));
        for (alias, policy) in items {
            println!("{}", format_special_alias_policy_line(&alias, &policy, &aliases));
        }
    }

    println!();
    if pins.is_empty() {
        println!("Minor alias pins: (none)");
    } else {
        println!("Minor alias pins:");
        let mut items: Vec<_> = pins.into_iter().collect();
        items.sort_by(|a, b| a.0.cmp(&b.0));
        for (line, version) in items {
            println!("  - {} -> {}", line, version);
        }
    }

    Ok(())
}

fn run_scoped_list_scope(scope: PackageScope, root: Option<PathBuf>) -> Result<()> {
    run_package_list(PackageLayoutOptions { scope, root })
}

fn run_scoped_list(scope: Option<WindowsListScope>, root: Option<PathBuf>) -> Result<()> {
    match scope {
        None => run_scoped_list_scope(PackageScope::CurrentUser, root),
        Some(WindowsListScope::CurrentUser) => run_scoped_list_scope(PackageScope::CurrentUser, root),
        Some(WindowsListScope::AllUsers) => run_scoped_list_scope(PackageScope::AllUsers, root),
        Some(WindowsListScope::All) => {
            if root.is_some() {
                return Err(MultiPwshError::InvalidArguments(
                    "--root cannot be used with --scope all".to_string(),
                ));
            }

            run_scoped_list_scope(PackageScope::CurrentUser, None)?;
            println!();
            run_scoped_list_scope(PackageScope::AllUsers, None)
        }
    }
}

fn run_package(args: &[String]) -> Result<()> {
    if args.is_empty() {
        return Err(MultiPwshError::InvalidArguments(
            "package requires: install <selector>, uninstall <version>, or list".to_string(),
        ));
    }

    match args[0].as_str() {
        "install" => {
            if args.len() < 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "package install requires <stable|preview|lts|version|major|major.minor|major.minor.x>".to_string(),
                ));
            }

            let options = parse_package_install_options(&args[2..])?;
            run_package_install(&args[1], options)
        }
        "uninstall" => {
            if args.len() < 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "package uninstall requires <version>".to_string(),
                ));
            }

            let (layout_options, force) = parse_package_uninstall_options(&args[2..])?;
            run_package_uninstall(&args[1], layout_options, force)
        }
        "list" => {
            let layout_options = parse_package_layout_options(&args[1..])?;
            run_package_list(layout_options)
        }
        _ => Err(MultiPwshError::InvalidArguments(
            "package requires: install <selector>, uninstall <version>, or list".to_string(),
        )),
    }
}

fn cleanup_aliases_for_removed_version(layout: &InstallLayout, os: HostOs, version: &Version) -> Result<()> {
    let aliases = aliases::read_alias_metadata(layout)?;
    let removed_version_text = version.to_string();
    let mut affected_aliases: Vec<String> = aliases
        .into_iter()
        .filter_map(|(alias_name, alias_version)| {
            if alias_version == removed_version_text {
                Some(alias_name)
            } else {
                None
            }
        })
        .collect();

    if affected_aliases.is_empty() {
        println!("No aliases referenced version {}", version);
        return Ok(());
    }

    affected_aliases.sort();
    let installed_versions = layout.installed_versions()?;

    let mut updated_aliases = 0usize;
    let mut removed_aliases = 0usize;
    let mut unresolved_pinned_aliases = 0usize;

    for alias_name in affected_aliases {
        let alias_selector = parse_alias_command_selector(&alias_name);
        let fallback_version = match alias_selector {
            Some(AliasSelector::MajorMinor(line)) => {
                let pinned = read_minor_pin(layout, line)?;
                if pinned.as_ref() == Some(version) {
                    println!(
                        "Keeping pinned alias {} -> {} (target is now unresolved)",
                        alias_name, version
                    );
                    unresolved_pinned_aliases += 1;
                    continue;
                }

                installed_versions
                    .iter()
                    .find(|candidate| MajorMinor::from_version(candidate) == line)
                    .cloned()
            }
            Some(AliasSelector::Major(major)) => installed_versions
                .iter()
                .find(|candidate| candidate.major == major)
                .cloned(),
            Some(AliasSelector::Exact(_)) => None,
            None => None,
        };

        if let Some(fallback_version) = fallback_version {
            let target = layout.version_executable(&fallback_version);
            let alias_path = match alias_selector {
                Some(AliasSelector::MajorMinor(line)) => {
                    create_or_update_alias(layout, os, line, &fallback_version, &target)?
                }
                Some(AliasSelector::Major(major)) => {
                    create_or_update_major_alias(layout, os, major, &fallback_version, &target)?
                }
                Some(AliasSelector::Exact(_)) => create_or_update_patch_alias(layout, os, &fallback_version, &target)?,
                None => continue,
            };
            println!("Updated alias: {} -> {}", alias_name, fallback_version);
            println!("Alias path: {}", alias_path.display());
            updated_aliases += 1;
            continue;
        }

        if remove_alias(layout, os, &alias_name)? {
            println!("Removed alias: {}", alias_name);
        }
        removed_aliases += 1;
    }

    println!(
        "Alias cleanup complete: {} updated, {} removed, {} pinned unresolved",
        updated_aliases, removed_aliases, unresolved_pinned_aliases
    );

    refresh_special_aliases(layout, os)?;

    Ok(())
}

#[cfg(test)]
fn parse_force_option(args: &[String]) -> Result<bool> {
    if args.is_empty() {
        return Ok(false);
    }

    if args.len() == 1 && args[0] == "--force" {
        return Ok(true);
    }

    Err(MultiPwshError::InvalidArguments(
        "expected optional --force".to_string(),
    ))
}

fn parse_list_option(args: &[String]) -> Result<ListOption> {
    let mut available = false;
    let mut include_prerelease = false;
    let mut offline_cache = None;
    let mut scope = None;
    let mut root = None;
    let mut index = 0usize;

    while index < args.len() {
        if let Some(next_index) = parse_offline_cache_option(args, index, &mut offline_cache)? {
            index = next_index;
            continue;
        }

        match args[index].as_str() {
            "--available" | "--online" => {
                available = true;
                index += 1;
            }
            "--include-prerelease" | "--prerelease" => {
                include_prerelease = true;
                index += 1;
            }
            "--scope" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --scope".to_string(),
                    ));
                }

                if scope.is_some() {
                    return Err(MultiPwshError::InvalidArguments(
                        "--scope can only be specified once".to_string(),
                    ));
                }

                scope = Some(WindowsListScope::parse(&args[index + 1]).ok_or_else(|| {
                    MultiPwshError::InvalidArguments(format!(
                        "unsupported scope '{}', expected one of: user, machine, all",
                        args[index + 1]
                    ))
                })?);
                index += 2;
            }
            "--root" => {
                if index + 1 >= args.len() {
                    return Err(MultiPwshError::InvalidArguments(
                        "expected value after --root".to_string(),
                    ));
                }

                if root.is_some() {
                    return Err(MultiPwshError::InvalidArguments(
                        "--root can only be specified once".to_string(),
                    ));
                }

                root = Some(PathBuf::from(&args[index + 1]));
                index += 2;
            }
            _ => {
                return Err(MultiPwshError::InvalidArguments(
                    "expected optional --scope <user|machine|all>, --root <path>, --available, --include-prerelease, and/or --offline-cache <path>"
                        .to_string(),
                ));
            }
        }
    }

    if available {
        if scope.is_some() || root.is_some() {
            return Err(MultiPwshError::InvalidArguments(
                "--scope and --root are only supported for installed-version listings".to_string(),
            ));
        }
        return Ok(ListOption::Available {
            include_prerelease,
            offline_cache,
        });
    }

    if offline_cache.is_some() {
        return Err(MultiPwshError::InvalidArguments(
            "--offline-cache requires --available".to_string(),
        ));
    }

    Ok(ListOption::Installed { scope, root })
}

fn run_list(option: ListOption) -> Result<()> {
    match option {
        ListOption::Installed { scope, root } => {
            if root.is_some() && scope.is_none() {
                return Err(MultiPwshError::InvalidArguments(
                    "--root requires --scope <user|machine> for installed-version listings".to_string(),
                ));
            }

            run_scoped_list(scope, root)
        }
        ListOption::Available {
            include_prerelease,
            offline_cache,
        } => {
            let release_resolver = ReleaseResolver::new(offline_cache)?;
            let versions = release_resolver.list_available_versions(include_prerelease)?;

            if versions.is_empty() {
                if release_resolver.is_offline() {
                    println!("Available offline bundle versions: (none)");
                } else {
                    println!("Available online versions: (none)");
                }
                return Ok(());
            }

            if release_resolver.is_offline() {
                if include_prerelease {
                    println!("Available offline bundle versions (including prerelease):");
                } else {
                    println!("Available offline bundle versions:");
                }
            } else if include_prerelease {
                println!("Available online versions (including prerelease):");
            } else {
                println!("Available online versions:");
            }
            for version in versions {
                println!("  - {}", version);
            }

            Ok(())
        }
    }
}

fn run_doctor(args: &[String]) -> Result<()> {
    if args.len() != 1 || args[0] != "--repair-aliases" {
        return Err(MultiPwshError::InvalidArguments(
            "doctor currently supports only: --repair-aliases".to_string(),
        ));
    }

    let os = HostOs::detect()?;
    let layout = default_current_user_layout(os)?;
    layout.ensure_base_dirs()?;

    refresh_special_aliases(&layout, os)?;

    let aliases = aliases::read_alias_metadata(&layout)?;
    if aliases.is_empty() {
        println!("No aliases found in metadata.");
        return Ok(());
    }

    let mut repaired = 0usize;
    let mut skipped = 0usize;
    let mut relinked_shims = 0usize;

    let mut items: Vec<_> = aliases.into_iter().collect();
    items.sort_by(|a, b| a.0.cmp(&b.0));

    for (alias_name, version_text) in items {
        let version = match Version::parse(&version_text) {
            Ok(version) => version,
            Err(_) => {
                eprintln!("Skipping alias {}: invalid version '{}'", alias_name, version_text);
                skipped += 1;
                continue;
            }
        };

        let target = layout.version_executable(&version);
        if !target.exists() {
            eprintln!(
                "Skipping alias {}: target executable not found at {}",
                alias_name,
                target.display()
            );
            skipped += 1;
            continue;
        }

        if aliases::repair_host_shim_if_needed(&layout, os, &alias_name)? {
            println!(
                "Relinked host shim: {}",
                if os == HostOs::Windows {
                    layout
                        .bin_dir()
                        .join(format!("{}.exe", alias_name))
                        .display()
                        .to_string()
                } else {
                    layout.bin_dir().join(&alias_name).display().to_string()
                }
            );
            relinked_shims += 1;
        }

        let alias_path = match parse_alias_command_selector(&alias_name) {
            Some(AliasSelector::MajorMinor(line)) => create_or_update_alias(&layout, os, line, &version, &target)?,
            Some(AliasSelector::Major(major)) => create_or_update_major_alias(&layout, os, major, &version, &target)?,
            Some(AliasSelector::Exact(_)) => create_or_update_patch_alias(&layout, os, &version, &target)?,
            None if is_special_alias_command(&alias_name) => {
                create_or_update_named_alias(&layout, os, &alias_name, &version, &target)?
            }
            None => {
                eprintln!("Skipping alias {}: unsupported alias name format", alias_name);
                skipped += 1;
                continue;
            }
        };
        println!("Repaired alias: {}", alias_path.display());
        repaired += 1;
    }

    if matches!(os, HostOs::Windows | HostOs::Linux | HostOs::Macos) {
        println!(
            "Repair complete: {} repaired, {} skipped, {} host shims relinked",
            repaired, skipped, relinked_shims
        );
    } else {
        println!("Repair complete: {} repaired, {} skipped", repaired, skipped);
    }
    Ok(())
}

fn run() -> Result<()> {
    let args: Vec<String> = env::args().skip(1).collect();
    if args.is_empty() {
        print_usage();
        return Err(MultiPwshError::InvalidArguments("missing command".to_string()));
    }

    if is_help_flag(&args[0]) {
        if args.len() != 1 {
            return Err(MultiPwshError::InvalidArguments(
                "--help does not accept additional arguments; use: multi-pwsh help <command>".to_string(),
            ));
        }
        print_global_help();
        return Ok(());
    }

    if args[0] == "help" {
        return run_help(&args[1..]);
    }

    if args[0] == "version" && args.len() == 2 && is_help_flag(&args[1]) {
        return print_help_topic("version");
    }

    if matches!(args[0].as_str(), "--version" | "-V" | "version") {
        if args.len() != 1 {
            return Err(MultiPwshError::InvalidArguments(format!(
                "{} does not accept additional arguments",
                args[0]
            )));
        }
        print_version();
        return Ok(());
    }

    if args.len() == 2 && is_help_flag(&args[1]) {
        return print_help_topic(&args[0]);
    }

    match args[0].as_str() {
        "install" => {
            if args.len() < 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "install requires <stable|preview|lts|version|major|major.minor|major.minor.x>".to_string(),
                ));
            }

            let options = parse_package_install_options(&args[2..])?;
            run_package_install(&args[1], options)
        }
        "update" => {
            if args.len() < 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "update requires <major.minor|stable|preview|lts>".to_string(),
                ));
            }
            parse_update_selector(&args[1])?;
            let options = parse_package_install_options(&args[2..])?;
            run_package_install(&args[1], options)
        }
        "uninstall" => {
            if args.len() < 2 {
                return Err(MultiPwshError::InvalidArguments(
                    "uninstall requires <version>".to_string(),
                ));
            }
            let options = parse_windows_uninstall_options(&args[2..])?;
            run_scoped_uninstall(&args[1], options)
        }
        "list" => {
            let list_option = parse_list_option(&args[1..])?;
            run_list(list_option)
        }
        "package" => run_package(&args[1..]),
        "cache" => run_cache(&args[1..]),
        "venv" => run_venv(&args[1..]),
        "alias" => run_alias(&args[1..]),
        "host" => {
            let exit_code = run_host_command(&args[1..])?;
            process::exit(exit_code);
        }
        "doctor" => run_doctor(&args[1..]),
        command => Err(MultiPwshError::InvalidArguments(format!(
            "unknown command '{}'. expected: install, update, uninstall, list, cache, venv, alias, host, doctor, version",
            command
        ))),
    }
}

fn main_impl() -> Result<()> {
    if let Some(exit_code) = run_implicit_host_mode_if_needed()? {
        process::exit(exit_code);
    }

    run()
}

fn main() {
    if let Err(error) = main_impl() {
        eprintln!("error: {}", error);
        process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    fn with_env_var<T>(key: &str, value: Option<&str>, action: impl FnOnce() -> T) -> T {
        let _guard = crate::TEST_ENV_LOCK.lock().unwrap();
        let previous = env::var_os(key);

        match value {
            Some(value) => unsafe { env::set_var(key, value) },
            None => unsafe { env::remove_var(key) },
        }

        let result = action();

        match previous {
            Some(value) => unsafe { env::set_var(key, value) },
            None => unsafe { env::remove_var(key) },
        }

        result
    }

    fn with_env_vars<T>(values: &[(&str, Option<&Path>)], action: impl FnOnce() -> T) -> T {
        let _guard = crate::TEST_ENV_LOCK.lock().unwrap();
        let previous: Vec<_> = values.iter().map(|(key, _)| (*key, env::var_os(key))).collect();

        for (key, value) in values {
            match value {
                Some(value) => unsafe { env::set_var(*key, value) },
                None => unsafe { env::remove_var(*key) },
            }
        }

        let result = action();

        for (key, value) in previous {
            match value {
                Some(value) => unsafe { env::set_var(key, value) },
                None => unsafe { env::remove_var(key) },
            }
        }

        result
    }

    fn with_env_var_texts<T>(values: &[(&str, Option<&str>)], action: impl FnOnce() -> T) -> T {
        let _guard = crate::TEST_ENV_LOCK.lock().unwrap();
        let previous: Vec<_> = values.iter().map(|(key, _)| (*key, env::var_os(key))).collect();

        for (key, value) in values {
            match value {
                Some(value) => unsafe { env::set_var(*key, value) },
                None => unsafe { env::remove_var(*key) },
            }
        }

        let result = action();

        for (key, value) in previous {
            match value {
                Some(value) => unsafe { env::set_var(key, value) },
                None => unsafe { env::remove_var(key) },
            }
        }

        result
    }

    #[test]
    fn default_current_user_layout_uses_user_home_layout_on_windows() {
        let temp_dir = TempDir::new().unwrap();
        let home = temp_dir.path().join("multi-pwsh-home");
        let venvs_dir = temp_dir.path().join("custom-venvs");

        with_env_vars(
            &[
                ("MULTI_PWSH_HOME", Some(home.as_path())),
                ("MULTI_PWSH_BIN_DIR", None),
                ("MULTI_PWSH_CACHE_DIR", None),
                ("MULTI_PWSH_VENV_DIR", Some(venvs_dir.as_path())),
            ],
            || {
                let layout = default_current_user_layout(HostOs::Windows).unwrap();

                assert_eq!(layout.home(), home.as_path());
                assert_eq!(layout.bin_dir(), home.join("bin"));
                assert_eq!(layout.cache_dir(), home.join("cache"));
                assert_eq!(layout.venvs_dir(), venvs_dir);
                assert_eq!(layout.versions_dir(), home.join("multi"));
            },
        );
    }

    #[test]
    fn default_current_user_layout_honors_explicit_overrides_on_windows() {
        let temp_dir = TempDir::new().unwrap();
        let home = temp_dir.path().join("multi-pwsh-home");
        let bin_dir = temp_dir.path().join("bin-root");
        let cache_dir = temp_dir.path().join("cache-root");
        let venv_dir = temp_dir.path().join("venv-root");

        with_env_vars(
            &[
                ("MULTI_PWSH_HOME", Some(home.as_path())),
                ("MULTI_PWSH_BIN_DIR", Some(bin_dir.as_path())),
                ("MULTI_PWSH_CACHE_DIR", Some(cache_dir.as_path())),
                ("MULTI_PWSH_VENV_DIR", Some(venv_dir.as_path())),
            ],
            || {
                let layout = default_current_user_layout(HostOs::Windows).unwrap();

                assert_eq!(layout.home(), home.as_path());
                assert_eq!(layout.bin_dir(), bin_dir);
                assert_eq!(layout.cache_dir(), cache_dir);
                assert_eq!(layout.venvs_dir(), venv_dir);
                assert_eq!(layout.versions_dir(), home.join("multi"));
            },
        );
    }

    #[test]
    fn parse_force_option_defaults_to_false() {
        let args: Vec<String> = Vec::new();
        assert!(!parse_force_option(&args).unwrap());
    }

    #[test]
    fn parse_force_option_accepts_force_flag() {
        let args = vec!["--force".to_string()];
        assert!(parse_force_option(&args).unwrap());
    }

    #[test]
    fn parse_force_option_rejects_unexpected_args() {
        let args = vec!["--arch".to_string(), "x64".to_string()];
        assert!(parse_force_option(&args).is_err());
    }

    #[test]
    fn parse_list_option_defaults_to_installed() {
        let args: Vec<String> = Vec::new();
        assert!(matches!(
            parse_list_option(&args).unwrap(),
            ListOption::Installed {
                scope: None,
                root: None
            }
        ));
    }

    #[test]
    fn parse_list_option_accepts_available() {
        let args = vec!["--available".to_string()];
        assert!(matches!(
            parse_list_option(&args).unwrap(),
            ListOption::Available {
                include_prerelease: false,
                offline_cache: None
            }
        ));
    }

    #[test]
    fn parse_list_option_accepts_available_with_prerelease() {
        let args = vec!["--available".to_string(), "--include-prerelease".to_string()];
        assert!(matches!(
            parse_list_option(&args).unwrap(),
            ListOption::Available {
                include_prerelease: true,
                offline_cache: None
            }
        ));
    }

    #[test]
    fn parse_list_option_accepts_available_with_offline_cache() {
        let args = vec![
            "--available".to_string(),
            "--offline-cache".to_string(),
            "C:\\cache".to_string(),
        ];
        assert!(matches!(
            parse_list_option(&args).unwrap(),
            ListOption::Available {
                include_prerelease: false,
                offline_cache: Some(path)
            } if path == Path::new("C:\\cache")
        ));
    }

    #[test]
    fn parse_list_option_rejects_offline_cache_without_available() {
        let args = vec!["--offline-cache".to_string(), "C:\\cache".to_string()];
        assert!(parse_list_option(&args).is_err());
    }

    #[test]
    fn parse_list_option_rejects_unexpected_args() {
        let args = vec!["--arch".to_string(), "x64".to_string()];
        assert!(parse_list_option(&args).is_err());
    }

    #[test]
    fn parse_list_option_accepts_prerelease_for_installed_listing() {
        let args = vec!["--include-prerelease".to_string()];
        assert!(matches!(
            parse_list_option(&args).unwrap(),
            ListOption::Installed {
                scope: None,
                root: None
            }
        ));
    }

    #[test]
    fn parse_list_option_accepts_scope_all() {
        let args = vec!["--scope".to_string(), "All".to_string()];
        assert!(matches!(
            parse_list_option(&args).unwrap(),
            ListOption::Installed {
                scope: Some(WindowsListScope::All),
                root: None
            }
        ));
    }

    #[test]
    fn parse_list_option_rejects_scope_aliases() {
        for alias in ["current-user", "all-users", "system"] {
            let args = vec!["--scope".to_string(), alias.to_string()];
            assert!(parse_list_option(&args).is_err(), "expected {} to be rejected", alias);
        }
    }

    #[test]
    fn parse_list_option_rejects_scope_for_available() {
        let args = vec!["--available".to_string(), "--scope".to_string(), "machine".to_string()];
        assert!(parse_list_option(&args).is_err());
    }

    #[test]
    fn parse_package_install_options_rejects_microsoft_update_flags() {
        let args = vec!["--scope".to_string(), "machine".to_string(), "--use-mu".to_string()];
        let error = parse_package_install_options(&args).unwrap_err();
        assert!(error
            .to_string()
            .contains("Microsoft Update integration is not supported for archive installs yet"));
    }

    #[test]
    fn parse_release_selection_options_accepts_offline_cache() {
        let args = vec!["--offline-cache".to_string(), "C:\\cache".to_string()];
        let options = parse_release_selection_options(&args).unwrap();

        assert_eq!(options.offline_cache, Some(PathBuf::from("C:\\cache")));
    }

    #[test]
    fn parse_package_install_options_accepts_offline_cache() {
        let args = vec![
            "--scope".to_string(),
            "user".to_string(),
            "--offline-cache".to_string(),
            "C:\\cache".to_string(),
        ];
        let options = parse_package_install_options(&args).unwrap();

        assert_eq!(options.offline_cache, Some(PathBuf::from("C:\\cache")));
        assert_eq!(options.package.scope, PackageScope::CurrentUser);
    }

    #[test]
    fn parse_package_install_options_rejects_scope_aliases() {
        for alias in ["current-user", "all-users", "system"] {
            let args = vec!["--scope".to_string(), alias.to_string()];
            assert!(
                parse_package_install_options_for_os(&args, HostOs::Windows).is_err(),
                "expected {} to be rejected",
                alias
            );
        }
    }

    #[test]
    fn parse_package_install_options_rejects_add_path_on_unix() {
        let args = vec!["--add-path".to_string()];
        let error = parse_package_install_options_for_os(&args, HostOs::Linux).unwrap_err();
        assert!(error.to_string().contains("supported only on Windows"));
    }

    #[test]
    fn parse_package_install_options_accepts_no_add_path_on_unix() {
        let args = vec!["--no-add-path".to_string()];
        let options = parse_package_install_options_for_os(&args, HostOs::Linux).unwrap();

        assert!(!options.package.add_path);
    }

    #[test]
    fn parse_package_install_options_requires_scope_with_root() {
        let args = vec!["--root".to_string(), "C:\\PowerShell".to_string()];
        let error = parse_package_install_options_for_os(&args, HostOs::Windows).unwrap_err();
        assert!(error
            .to_string()
            .contains("--root requires --scope <user|machine> for install and update"));

        let args = vec![
            "--scope".to_string(),
            "user".to_string(),
            "--root".to_string(),
            "C:\\PowerShell".to_string(),
        ];
        assert!(parse_package_install_options_for_os(&args, HostOs::Windows).is_ok());
    }

    #[test]
    fn parse_package_install_options_preserves_flags_before_scope() {
        let args = vec![
            "--no-add-path".to_string(),
            "--disable-telemetry".to_string(),
            "--scope".to_string(),
            "machine".to_string(),
        ];
        let options = parse_package_install_options_for_os(&args, HostOs::Windows).unwrap();

        assert_eq!(options.package.scope, PackageScope::AllUsers);
        assert!(!options.package.add_path);
        assert!(options.package.disable_telemetry);
        assert!(options.package.register_manifest);
    }

    #[test]
    fn parse_package_install_options_applies_scope_defaults_when_flags_are_not_specified() {
        let args = vec!["--scope".to_string(), "machine".to_string()];
        let options = parse_package_install_options_for_os(&args, HostOs::Windows).unwrap();

        assert!(options.package.add_path);
        assert!(options.package.register_manifest);
    }

    #[test]
    fn parse_package_layout_options_requires_scope_with_root() {
        let args = vec!["--root".to_string(), "C:\\PowerShell".to_string()];
        let error = parse_package_layout_options(&args).unwrap_err();
        assert!(error.to_string().contains("--root requires --scope <user|machine>"));

        let args = vec![
            "--scope".to_string(),
            "machine".to_string(),
            "--root".to_string(),
            "C:\\PowerShell".to_string(),
        ];
        assert!(parse_package_layout_options(&args).is_ok());
    }

    #[test]
    fn parse_package_uninstall_options_requires_scope_with_root() {
        let args = vec!["--root".to_string(), "C:\\PowerShell".to_string()];
        let error = parse_package_uninstall_options(&args).unwrap_err();
        assert!(error.to_string().contains("--root requires --scope <user|machine>"));

        let args = vec![
            "--scope".to_string(),
            "machine".to_string(),
            "--root".to_string(),
            "C:\\PowerShell".to_string(),
        ];
        assert!(parse_package_uninstall_options(&args).is_ok());
    }

    #[test]
    fn parse_package_install_options_defaults_add_path_by_platform() {
        let args: Vec<String> = Vec::new();

        assert!(
            parse_package_install_options_for_os(&args, HostOs::Windows)
                .unwrap()
                .package
                .add_path
        );
        assert!(
            !parse_package_install_options_for_os(&args, HostOs::Linux)
                .unwrap()
                .package
                .add_path
        );
    }

    #[test]
    fn parse_update_selector_reports_actionable_error_for_exact_versions() {
        let error = parse_update_selector("7.4.13").unwrap_err();
        assert!(error.to_string().contains("update accepts stable, preview, lts"));
        assert!(error.to_string().contains("multi-pwsh install 7.4.13"));
    }

    #[cfg(windows)]
    #[test]
    fn default_uninstall_reports_machine_scope_when_user_scope_is_missing() {
        let temp_dir = TempDir::new().unwrap();
        let user_home = temp_dir.path().join("user-home");
        let program_files = temp_dir.path().join("program-files");
        let machine_version_dir = program_files.join("PowerShell").join("7.4.13");
        fs::create_dir_all(&machine_version_dir).unwrap();

        with_env_vars(
            &[
                ("MULTI_PWSH_HOME", Some(user_home.as_path())),
                ("ProgramFiles", Some(program_files.as_path())),
                ("ProgramFiles(x86)", None),
            ],
            || {
                let error = run_scoped_uninstall(
                    "7.4.13",
                    WindowsUninstallOptions {
                        scope: None,
                        root: None,
                        force: false,
                    },
                )
                .unwrap_err();
                let message = error.to_string();
                assert!(message.contains("not installed in scope user"));
                assert!(message.contains("installed in scope machine"));
                assert!(message.contains("--scope machine"));
            },
        );
    }

    #[test]
    fn offline_cache_from_env_ignores_empty_and_whitespace_values() {
        with_env_var_texts(&[(MULTI_PWSH_OFFLINE_CACHE_ENV_VAR, Some(" \t "))], || {
            assert_eq!(offline_cache_from_env(), None);
        });
    }

    #[test]
    fn offline_cache_from_env_reads_offline_cache_value() {
        with_env_var_texts(&[(MULTI_PWSH_OFFLINE_CACHE_ENV_VAR, Some("C:\\offline-cache"))], || {
            assert_eq!(offline_cache_from_env(), Some(PathBuf::from("C:\\offline-cache")));
        });
    }

    #[test]
    fn parse_cache_warm_options_accepts_cross_platform_all_products() {
        let args = vec![
            "--os".to_string(),
            "all".to_string(),
            "--arch".to_string(),
            "all".to_string(),
            "--product".to_string(),
            "all".to_string(),
            "--output".to_string(),
            "C:\\cache".to_string(),
        ];
        let options = parse_cache_warm_options(&args).unwrap();

        assert_eq!(options.target_oses, all_host_oses());
        assert_eq!(options.target_arches, all_host_arches());
        assert_eq!(options.product, CacheProduct::All);
        assert_eq!(options.output, Some(PathBuf::from("C:\\cache")));
        assert!(options.os_wildcard);
        assert!(options.arch_wildcard);
    }

    #[test]
    fn parse_host_selector_supports_alias_name() {
        let selector = parse_host_selector("pwsh-7.4").unwrap();
        assert_eq!(selector, HostSelector::MajorMinor(MajorMinor { major: 7, minor: 4 }));
    }

    #[test]
    fn parse_host_selector_supports_exact_version() {
        let selector = parse_host_selector("7.4.13").unwrap();
        assert_eq!(selector, HostSelector::Exact(Version::parse("7.4.13").unwrap()));
    }

    #[test]
    fn extract_mcp_args_accepts_mcp_mode_with_multiple_commands() {
        let args = vec![
            OsString::from("-mcp"),
            OsString::from("-McpCommands"),
            OsString::from("Get-Help"),
            OsString::from("Get-Command,Get-Date"),
            OsString::from("-venv"),
            OsString::from("demo"),
        ];

        let (rewritten, mcp) = extract_mcp_args(args).unwrap();

        assert_eq!(rewritten, vec![OsString::from("-venv"), OsString::from("demo")]);
        assert_eq!(
            mcp,
            Some(HostMcpOptions {
                commands: vec![
                    "Get-Help".to_string(),
                    "Get-Command".to_string(),
                    "Get-Date".to_string(),
                ],
            })
        );
    }

    #[test]
    fn extract_mcp_args_rejects_commands_without_mcp_flag() {
        let args = vec![OsString::from("-McpCommands"), OsString::from("Get-Help")];
        let error = extract_mcp_args(args).unwrap_err();
        assert!(error.to_string().contains("-McpCommands requires -mcp"));
    }

    #[test]
    fn extract_mcp_args_requires_command_names() {
        let args = vec![OsString::from("-mcp"), OsString::from("-McpCommands")];
        let error = extract_mcp_args(args).unwrap_err();
        assert!(error
            .to_string()
            .contains("-McpCommands requires at least one PowerShell command name"));
    }

    #[test]
    fn disable_powershell_update_notifications_sets_off() {
        with_env_var(POWERSHELL_UPDATECHECK_ENV_VAR, Some("LTS"), || {
            disable_powershell_update_notifications();
            assert_eq!(
                env::var(POWERSHELL_UPDATECHECK_ENV_VAR).unwrap(),
                POWERSHELL_UPDATECHECK_OFF
            );
        });
    }

    #[test]
    fn detect_implicit_host_selector_accepts_alias_in_bin_dir() {
        let bin_dir = PathBuf::from("C:/Users/test/.pwsh/bin");

        let selector = detect_implicit_host_selector(&bin_dir, &bin_dir.join("pwsh-7.4.exe"));
        assert_eq!(selector, Some("pwsh-7.4".to_string()));
    }

    #[test]
    fn detect_implicit_host_selector_accepts_bare_pwsh_alias() {
        let bin_dir = PathBuf::from("C:/Users/test/.pwsh/bin");

        let selector = detect_implicit_host_selector(&bin_dir, &bin_dir.join("pwsh.exe"));
        assert_eq!(selector, Some("pwsh".to_string()));
    }

    #[test]
    fn detect_implicit_host_selector_accepts_preview_alias() {
        let bin_dir = PathBuf::from("C:/Users/test/.pwsh/bin");

        let selector = detect_implicit_host_selector(&bin_dir, &bin_dir.join("pwsh-preview.exe"));
        assert_eq!(selector, Some("pwsh-preview".to_string()));
    }

    #[test]
    fn detect_implicit_host_selector_accepts_lts_alias() {
        let bin_dir = PathBuf::from("C:/Users/test/.pwsh/bin");

        let selector = detect_implicit_host_selector(&bin_dir, &bin_dir.join("pwsh-lts.exe"));
        assert_eq!(selector, Some("pwsh-lts".to_string()));
    }

    #[test]
    fn detect_implicit_host_selector_accepts_posix_alias_with_dot() {
        let bin_dir = PathBuf::from("/home/test/.pwsh/bin");

        let selector = detect_implicit_host_selector(&bin_dir, &bin_dir.join("pwsh-7.4"));
        assert_eq!(selector, Some("pwsh-7.4".to_string()));
    }

    #[test]
    fn detect_implicit_host_selector_accepts_alias_in_overridden_bin_dir() {
        let bin_dir = PathBuf::from("D:/tools/multi-pwsh/bin");

        let selector = detect_implicit_host_selector(&bin_dir, &bin_dir.join("pwsh-7.5.exe"));
        assert_eq!(selector, Some("pwsh-7.5".to_string()));
    }

    #[test]
    fn detect_implicit_host_selector_rejects_multi_pwsh_name() {
        let bin_dir = PathBuf::from("C:/Users/test/.pwsh/bin");

        let selector = detect_implicit_host_selector(&bin_dir, &bin_dir.join("multi-pwsh.exe"));
        assert!(selector.is_none());
    }

    #[test]
    fn detect_implicit_host_selector_rejects_outside_bin_dir() {
        let bin_dir = PathBuf::from("C:/Users/test/.pwsh/bin");

        let selector = detect_implicit_host_selector(&bin_dir, &PathBuf::from("C:/Users/test/other/pwsh-7.4.exe"));
        assert!(selector.is_none());
    }

    #[test]
    fn is_local_pwsh_apphost_accepts_exact_pwsh_with_adjacent_sdk_payload() {
        let temp_dir = TempDir::new().unwrap();
        let executable_path = temp_dir.path().join("pwsh.exe");
        fs::write(&executable_path, "").unwrap();
        fs::write(temp_dir.path().join("pwsh.dll"), "").unwrap();
        fs::write(temp_dir.path().join("pwsh.runtimeconfig.json"), "{}").unwrap();

        assert_eq!(
            resolve_local_pwsh_apphost_payload_dir(&executable_path).as_deref(),
            Some(temp_dir.path())
        );
    }

    #[test]
    fn is_local_pwsh_apphost_accepts_exact_pwsh_without_optional_payload_signals() {
        let temp_dir = TempDir::new().unwrap();
        let executable_path = temp_dir.path().join("pwsh");
        fs::write(&executable_path, "").unwrap();
        fs::write(temp_dir.path().join("pwsh.dll"), "").unwrap();
        fs::write(temp_dir.path().join("pwsh.runtimeconfig.json"), "{}").unwrap();

        assert_eq!(
            resolve_local_pwsh_apphost_payload_dir(&executable_path).as_deref(),
            Some(temp_dir.path())
        );
    }

    #[test]
    fn is_local_pwsh_apphost_accepts_runtime_native_shared_payload() {
        let temp_dir = TempDir::new().unwrap();
        let native_dir = temp_dir.path().join("runtimes").join("win-x64").join("native");
        fs::create_dir_all(&native_dir).unwrap();
        let executable_path = native_dir.join("pwsh.exe");
        fs::write(&executable_path, "").unwrap();
        fs::write(temp_dir.path().join("pwsh.dll"), "").unwrap();
        fs::write(temp_dir.path().join("pwsh.runtimeconfig.json"), "{}").unwrap();

        assert_eq!(
            resolve_local_pwsh_apphost_payload_dir(&executable_path).as_deref(),
            Some(temp_dir.path())
        );
    }

    #[test]
    fn is_local_pwsh_apphost_rejects_alias_name_with_local_payloads() {
        let temp_dir = TempDir::new().unwrap();
        let executable_path = temp_dir.path().join("pwsh-preview.exe");
        fs::write(&executable_path, "").unwrap();
        fs::write(temp_dir.path().join("pwsh.dll"), "").unwrap();
        fs::write(temp_dir.path().join("pwsh.runtimeconfig.json"), "{}").unwrap();

        assert!(resolve_local_pwsh_apphost_payload_dir(&executable_path).is_none());

        let native_dir = temp_dir.path().join("runtimes").join("win-x64").join("native");
        fs::create_dir_all(&native_dir).unwrap();
        let runtime_native_executable_path = native_dir.join("pwsh-preview.exe");
        fs::write(&runtime_native_executable_path, "").unwrap();

        assert!(resolve_local_pwsh_apphost_payload_dir(&runtime_native_executable_path).is_none());
    }

    #[test]
    fn is_local_pwsh_apphost_rejects_missing_required_payload_files() {
        let temp_dir = TempDir::new().unwrap();
        let executable_path = temp_dir.path().join("pwsh.exe");
        fs::write(&executable_path, "").unwrap();

        assert!(resolve_local_pwsh_apphost_payload_dir(&executable_path).is_none());

        fs::write(temp_dir.path().join("pwsh.dll"), "").unwrap();
        assert!(resolve_local_pwsh_apphost_payload_dir(&executable_path).is_none());

        fs::remove_file(temp_dir.path().join("pwsh.dll")).unwrap();
        fs::write(temp_dir.path().join("pwsh.runtimeconfig.json"), "{}").unwrap();
        assert!(resolve_local_pwsh_apphost_payload_dir(&executable_path).is_none());
    }

    #[test]
    fn is_local_pwsh_apphost_rejects_runtime_native_missing_shared_payload() {
        let temp_dir = TempDir::new().unwrap();
        let native_dir = temp_dir.path().join("runtimes").join("linux-x64").join("native");
        fs::create_dir_all(&native_dir).unwrap();
        let executable_path = native_dir.join("pwsh");
        fs::write(&executable_path, "").unwrap();

        assert!(resolve_local_pwsh_apphost_payload_dir(&executable_path).is_none());

        fs::write(temp_dir.path().join("pwsh.dll"), "").unwrap();
        assert!(resolve_local_pwsh_apphost_payload_dir(&executable_path).is_none());

        fs::remove_file(temp_dir.path().join("pwsh.dll")).unwrap();
        fs::write(temp_dir.path().join("pwsh.runtimeconfig.json"), "{}").unwrap();
        assert!(resolve_local_pwsh_apphost_payload_dir(&executable_path).is_none());
    }

    #[test]
    fn infer_layout_from_host_shim_uses_parent_of_bin_dir() {
        let executable_path = PathBuf::from("C:/Program Files/PowerShell/bin/pwsh-7.4.exe");

        let layout = infer_layout_from_host_shim(HostOs::Windows, &executable_path).unwrap();
        assert_eq!(layout.home(), Path::new("C:/Program Files/PowerShell"));
        assert_eq!(layout.bin_dir(), PathBuf::from("C:/Program Files/PowerShell/bin"));
        assert_eq!(layout.cache_dir(), PathBuf::from("C:/Program Files/PowerShell/cache"));
        assert_eq!(
            layout.versions_dir(),
            PathBuf::from("C:/Program Files/PowerShell/multi")
        );
    }

    #[test]
    fn infer_layout_from_host_shim_uses_package_root_when_metadata_exists() {
        let temp_dir = TempDir::new().unwrap();
        let home = temp_dir.path().join("PowerShell");
        fs::create_dir_all(home.join("bin")).unwrap();
        fs::write(home.join(PACKAGE_METADATA_FILE), "{}").unwrap();
        let executable_path = home.join("bin").join("pwsh-7.4.exe");

        let layout = infer_layout_from_host_shim(HostOs::Windows, &executable_path).unwrap();
        assert_eq!(layout.home(), home.as_path());
        assert_eq!(layout.bin_dir(), home.join("bin"));
        assert_eq!(layout.versions_dir(), home);
    }

    #[test]
    fn infer_layout_from_host_shim_rejects_non_bin_parent() {
        let executable_path = PathBuf::from("C:/Program Files/PowerShell/shims/pwsh-7.4.exe");
        assert!(infer_layout_from_host_shim(HostOs::Windows, &executable_path).is_none());
    }

    #[test]
    fn infer_layout_from_host_shim_uses_layout_hint_for_shared_bin() {
        let temp_dir = TempDir::new().unwrap();
        let home = temp_dir.path().join("payload-root");
        let bin_dir = temp_dir.path().join("shared-bin");
        let cache_dir = temp_dir.path().join("cache-root");
        let venvs_dir = temp_dir.path().join("venv-root");
        let versions_dir = temp_dir.path().join("versions-root");
        fs::create_dir_all(&bin_dir).unwrap();
        let layout = InstallLayout::from_parts(
            HostOs::Linux,
            home.clone(),
            bin_dir.clone(),
            cache_dir.clone(),
            venvs_dir.clone(),
            versions_dir.clone(),
        )
        .unwrap();
        let hint = serde_json::json!({
            "home": home.display().to_string(),
            "bin_dir": bin_dir.display().to_string(),
            "cache_dir": cache_dir.display().to_string(),
            "venvs_dir": venvs_dir.display().to_string(),
            "versions_dir": versions_dir.display().to_string()
        });
        fs::write(
            bin_dir.join("multi-pwsh-layout.json"),
            serde_json::to_string_pretty(&hint).unwrap(),
        )
        .unwrap();
        let executable_path = bin_dir.join("pwsh-7.4");

        let inferred = infer_layout_from_host_shim(HostOs::Linux, &executable_path).unwrap();
        assert_eq!(inferred.home(), layout.home());
        assert_eq!(inferred.bin_dir(), layout.bin_dir());
        assert_eq!(inferred.cache_dir(), layout.cache_dir());
        assert_eq!(inferred.venvs_dir(), layout.venvs_dir());
        assert_eq!(inferred.versions_dir(), layout.versions_dir());
    }

    #[test]
    fn parse_release_selection_options_accepts_prerelease() {
        let args = vec!["--include-prerelease".to_string()];
        let options = parse_release_selection_options(&args).unwrap();
        assert!(options.include_prerelease);
        assert!(options.arch.is_none());
        assert_eq!(options.checksum_source, ChecksumSource::ReleaseAsset);
    }

    #[test]
    fn multi_pwsh_asset_name_supports_linux_arm32() {
        let asset_name = multi_pwsh_asset_name(HostOs::Linux, HostArch::Arm32).unwrap();

        assert_eq!(asset_name, "multi-pwsh-linux-arm.zip");
    }

    #[test]
    fn parse_release_selection_options_accepts_arch_and_prerelease() {
        let args = vec![
            "--arch".to_string(),
            "x64".to_string(),
            "--include-prerelease".to_string(),
        ];
        let options = parse_release_selection_options(&args).unwrap();
        assert!(options.include_prerelease);
        assert!(matches!(options.arch, Some(HostArch::X64)));
    }

    #[test]
    fn parse_release_selection_options_accepts_skip_hash_verification() {
        let args = vec!["--skip-hash-verification".to_string()];
        let options = parse_release_selection_options(&args).unwrap();

        assert_eq!(options.checksum_source, ChecksumSource::Skip);
    }

    #[test]
    fn parse_release_selection_options_accepts_checksum_file_url() {
        let args = vec![
            "--hash-file".to_string(),
            "https://example.invalid/hashes.sha256".to_string(),
        ];
        let options = parse_release_selection_options(&args).unwrap();

        assert_eq!(
            options.checksum_source,
            ChecksumSource::Url("https://example.invalid/hashes.sha256".to_string())
        );
    }

    #[test]
    fn parse_release_selection_options_accepts_checksum_file_path() {
        let args = vec!["--hash-file".to_string(), "C:\\temp\\hashes.sha256".to_string()];
        let options = parse_release_selection_options(&args).unwrap();

        assert_eq!(
            options.checksum_source,
            ChecksumSource::File(PathBuf::from("C:\\temp\\hashes.sha256"))
        );
    }

    #[test]
    fn parse_release_selection_options_rejects_multiple_checksum_sources() {
        let args = vec![
            "--skip-hash-verification".to_string(),
            "--hash-file".to_string(),
            "hashes.sha256".to_string(),
        ];

        assert!(parse_release_selection_options(&args).is_err());
    }

    #[test]
    fn parse_package_install_options_accepts_checksum_file_url() {
        let args = vec![
            "--scope".to_string(),
            "user".to_string(),
            "--hash-file".to_string(),
            "https://example.invalid/hashes.sha256".to_string(),
        ];
        let options = parse_package_install_options(&args).unwrap();

        assert_eq!(
            options.checksum_source,
            ChecksumSource::Url("https://example.invalid/hashes.sha256".to_string())
        );
        assert_eq!(options.package.scope, PackageScope::CurrentUser);
    }

    #[test]
    fn parse_package_install_options_accepts_skip_hash_verification() {
        let args = vec!["--skip-hash-verification".to_string()];
        let options = parse_package_install_options(&args).unwrap();

        assert_eq!(options.checksum_source, ChecksumSource::Skip);
    }

    #[test]
    fn parse_alias_set_target_accepts_latest() {
        assert!(parse_alias_set_target("latest").unwrap().is_none());
        assert!(parse_alias_set_target("LATEST").unwrap().is_none());
    }

    #[test]
    fn parse_alias_set_target_accepts_exact_version() {
        let version = parse_alias_set_target("7.4.11").unwrap().unwrap();
        assert_eq!(version, Version::parse("7.4.11").unwrap());
    }

    #[test]
    fn parse_special_alias_policy_accepts_channels() {
        assert_eq!(
            parse_special_alias_policy("stable").unwrap(),
            SpecialAliasPolicy::Stable
        );
        assert_eq!(
            parse_special_alias_policy("PREVIEW").unwrap(),
            SpecialAliasPolicy::Preview
        );
        assert_eq!(parse_special_alias_policy("lts").unwrap(), SpecialAliasPolicy::Lts);
    }

    #[test]
    fn parse_special_alias_policy_accepts_exact_version() {
        assert_eq!(
            parse_special_alias_policy("7.6.2").unwrap(),
            SpecialAliasPolicy::Exact(Version::parse("7.6.2").unwrap())
        );
    }

    #[test]
    fn validate_special_alias_policy_restricts_preview_alias() {
        assert!(validate_special_alias_policy(PWSH_PREVIEW_ALIAS, &SpecialAliasPolicy::Preview).is_ok());
        assert!(validate_special_alias_policy(
            PWSH_PREVIEW_ALIAS,
            &SpecialAliasPolicy::Exact(Version::parse("7.7.0-preview.1").unwrap())
        )
        .is_ok());
        assert!(validate_special_alias_policy(PWSH_PREVIEW_ALIAS, &SpecialAliasPolicy::Stable).is_err());
    }

    #[test]
    fn validate_special_alias_policy_restricts_lts_alias() {
        assert!(validate_special_alias_policy(PWSH_LTS_ALIAS, &SpecialAliasPolicy::Lts).is_ok());
        assert!(validate_special_alias_policy(
            PWSH_LTS_ALIAS,
            &SpecialAliasPolicy::Exact(Version::parse("7.6.2").unwrap())
        )
        .is_ok());
        assert!(validate_special_alias_policy(PWSH_LTS_ALIAS, &SpecialAliasPolicy::Stable).is_err());
        assert!(validate_special_alias_policy(
            PWSH_LTS_ALIAS,
            &SpecialAliasPolicy::Exact(Version::parse("7.5.7").unwrap())
        )
        .is_err());
    }

    #[test]
    fn resolve_special_alias_policy_selects_expected_versions() {
        let versions = vec![
            Version::parse("7.7.0-preview.1").unwrap(),
            Version::parse("7.6.2").unwrap(),
            Version::parse("7.5.7").unwrap(),
        ];

        assert_eq!(
            resolve_special_alias_policy_from_installed(&versions, &SpecialAliasPolicy::Stable),
            Some(Version::parse("7.6.2").unwrap())
        );
        assert_eq!(
            resolve_special_alias_policy_from_installed(&versions, &SpecialAliasPolicy::Preview),
            Some(Version::parse("7.7.0-preview.1").unwrap())
        );
        assert_eq!(
            resolve_special_alias_policy_from_installed(&versions, &SpecialAliasPolicy::Lts),
            Some(Version::parse("7.6.2").unwrap())
        );
    }

    #[test]
    fn format_special_alias_policy_line_shows_resolved_and_unresolved_state() {
        let mut aliases = HashMap::new();
        aliases.insert(PWSH_LTS_ALIAS.to_string(), "7.6.2".to_string());

        assert_eq!(
            format_special_alias_policy_line(PWSH_LTS_ALIAS, "lts", &aliases),
            "  - pwsh-lts follows lts -> 7.6.2"
        );
        assert_eq!(
            format_special_alias_policy_line(PWSH_PREVIEW_ALIAS, "preview", &aliases),
            "  - pwsh-preview follows preview -> unresolved"
        );
    }

    #[test]
    fn refresh_special_aliases_updates_policy_to_newest_matching_installed_version() {
        let temp_dir = TempDir::new().unwrap();
        let layout = InstallLayout::from_root(HostOs::detect().unwrap(), temp_dir.path().join("home")).unwrap();
        layout.ensure_base_dirs().unwrap();

        for version in ["7.6.1", "7.6.2"] {
            let version = Version::parse(version).unwrap();
            let executable = layout.version_executable(&version);
            fs::create_dir_all(executable.parent().unwrap()).unwrap();
            fs::write(executable, b"").unwrap();
        }

        set_special_alias_policy(&layout, PWSH_LTS_ALIAS, Some("lts")).unwrap();
        refresh_special_aliases(&layout, layout.os()).unwrap();

        let aliases = aliases::read_alias_metadata(&layout).unwrap();
        assert_eq!(aliases.get(PWSH_LTS_ALIAS).map(String::as_str), Some("7.6.2"));
    }

    #[test]
    fn validate_venv_name_accepts_simple_name() {
        assert_eq!(validate_venv_name("msgraph").unwrap(), "msgraph");
        assert_eq!(validate_venv_name("graph-sdk_1.0").unwrap(), "graph-sdk_1.0");
    }

    #[test]
    fn validate_venv_name_rejects_reserved_or_path_like_values() {
        assert!(validate_venv_name("").is_err());
        assert!(validate_venv_name("..").is_err());
        assert!(validate_venv_name("msgraph/tools").is_err());
    }

    #[test]
    fn extract_virtual_environment_arg_removes_host_only_pair() {
        let args = vec![
            OsString::from("-NoProfile"),
            OsString::from("-VirtualEnvironment"),
            OsString::from("msgraph"),
            OsString::from("-Command"),
            OsString::from("$env:PSModulePath"),
        ];

        let (rewritten, virtual_environment) = extract_virtual_environment_arg(args).unwrap();

        assert_eq!(virtual_environment, Some("msgraph".to_string()));
        assert_eq!(
            rewritten,
            vec![
                OsString::from("-NoProfile"),
                OsString::from("-Command"),
                OsString::from("$env:PSModulePath"),
            ]
        );
    }

    #[test]
    fn extract_virtual_environment_arg_rejects_duplicate_flag() {
        let args = vec![
            OsString::from("-VirtualEnvironment"),
            OsString::from("one"),
            OsString::from("-venv"),
            OsString::from("two"),
        ];

        assert!(extract_virtual_environment_arg(args).is_err());
    }

    #[test]
    fn extract_virtual_environment_arg_accepts_short_flag() {
        let args = vec![
            OsString::from("-venv"),
            OsString::from("msgraph"),
            OsString::from("-NoProfile"),
        ];

        let (rewritten, virtual_environment) = extract_virtual_environment_arg(args).unwrap();

        assert_eq!(virtual_environment, Some("msgraph".to_string()));
        assert_eq!(rewritten, vec![OsString::from("-NoProfile")]);
    }

    #[test]
    fn preprocess_host_args_combines_virtual_environment_and_named_pipe_processing() {
        let args = vec![
            OsString::from("-VirtualEnvironment"),
            OsString::from("msgraph"),
            OsString::from("-NoProfile"),
        ];

        let options = preprocess_host_args(args).unwrap();
        assert_eq!(options.launch.virtual_environment, Some("msgraph".to_string()));
        assert_eq!(options.launch.pwsh_args, vec![OsString::from("-NoProfile")]);
    }

    #[test]
    fn preprocess_host_args_bootstraps_command_when_virtual_environment_is_present() {
        let args = vec![
            OsString::from("-venv"),
            OsString::from("msgraph"),
            OsString::from("-Command"),
            OsString::from("Get-InstalledModule Pester"),
        ];

        let options = preprocess_host_args(args).unwrap();
        assert_eq!(options.launch.virtual_environment, Some("msgraph".to_string()));
        assert_eq!(options.launch.pwsh_args.len(), 2);
        assert_eq!(options.launch.pwsh_args[0], OsString::from("-Command"));

        let command = options.launch.pwsh_args[1].to_string_lossy();
        assert!(command.contains("Get-Command Import-Module -ErrorAction SilentlyContinue"));
        assert!(command.contains("$__multiPwshImportModule.CommandType -eq 'Alias'"));
        assert!(command.contains("Get-InstalledModule Pester"));
    }

    #[test]
    fn preprocess_host_args_preserves_stdin_file_when_virtual_environment_is_present() {
        let args = vec![
            OsString::from("-venv"),
            OsString::from("msgraph"),
            OsString::from("-File"),
            OsString::from("-"),
        ];

        let options = preprocess_host_args(args).unwrap();
        assert_eq!(options.launch.virtual_environment, Some("msgraph".to_string()));
        assert_eq!(
            options.launch.pwsh_args,
            vec![OsString::from("-File"), OsString::from("-")]
        );
    }

    #[test]
    fn process_env_var_guard_restores_previous_value() {
        with_env_var("PSModulePath", Some("original"), || {
            {
                let _guard = ProcessEnvVarGuard::set("PSModulePath", "override");
                assert_eq!(env::var("PSModulePath").unwrap(), "override");
            }

            assert_eq!(env::var("PSModulePath").unwrap(), "original");
        });
    }

    #[test]
    fn configure_virtual_environment_host_env_sets_and_restores_windows_startup_hook_variables() {
        let _guard = crate::TEST_ENV_LOCK.lock().unwrap();
        let temp_dir = tempfile::tempdir().unwrap();
        let forced_path = temp_dir.path().join("venv");
        fs::create_dir_all(&forced_path).unwrap();

        let previous_forced_path = env::var_os(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR);
        let previous_strategy = env::var_os(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR);

        unsafe {
            env::remove_var(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR);
            env::remove_var(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR);
        }

        {
            let _guards = configure_virtual_environment_host_env(HostOs::Windows, &forced_path).unwrap();
            assert_eq!(
                PathBuf::from(
                    env::var_os(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR)
                        .expect("module venv path should be set")
                ),
                forced_path
            );
            assert_eq!(
                env::var(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR).unwrap(),
                pwsh_host::MODULE_PATH_STRATEGY
            );
        }

        match previous_forced_path {
            Some(value) => unsafe { env::set_var(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR, value) },
            None => unsafe { env::remove_var(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR) },
        }

        match previous_strategy {
            Some(value) => unsafe { env::set_var(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR, value) },
            None => unsafe { env::remove_var(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR) },
        }
    }

    #[test]
    fn configure_virtual_environment_host_env_sets_and_restores_unix_startup_hook_variables() {
        let _guard = crate::TEST_ENV_LOCK.lock().unwrap();
        let temp_dir = tempfile::tempdir().unwrap();
        let forced_path = temp_dir.path().join("venv");
        fs::create_dir_all(&forced_path).unwrap();

        let previous_forced_path = env::var_os(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR);
        let previous_strategy = env::var_os(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR);

        unsafe {
            env::remove_var(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR);
            env::remove_var(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR);
        }

        {
            let _guards = configure_virtual_environment_host_env(HostOs::Linux, &forced_path).unwrap();
            assert_eq!(
                PathBuf::from(
                    env::var_os(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR)
                        .expect("module venv path should be set")
                ),
                forced_path
            );
            assert_eq!(
                env::var(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR).unwrap(),
                pwsh_host::MODULE_PATH_STRATEGY
            );
        }

        match previous_forced_path {
            Some(value) => unsafe { env::set_var(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR, value) },
            None => unsafe { env::remove_var(pwsh_host::STARTUP_HOOK_MODULE_VENV_PATH_ENV_VAR) },
        }

        match previous_strategy {
            Some(value) => unsafe { env::set_var(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR, value) },
            None => unsafe { env::remove_var(pwsh_host::STARTUP_HOOK_STRATEGY_ENV_VAR) },
        }
    }

    #[test]
    fn sanitize_archive_entry_path_accepts_normal_relative_paths() {
        assert_eq!(
            sanitize_archive_entry_path("Module/1.0.0/Module.psm1").unwrap(),
            PathBuf::from("Module").join("1.0.0").join("Module.psm1")
        );
    }

    #[test]
    fn sanitize_archive_entry_path_rejects_parent_segments() {
        assert!(sanitize_archive_entry_path("../escape.txt").is_err());
    }
}
