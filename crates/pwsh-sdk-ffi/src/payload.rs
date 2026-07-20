use std::collections::{HashMap, HashSet};
use std::fs;
use std::io::Read;
use std::path::{Component, Path, PathBuf};

use serde::Deserialize;
use sha2::{Digest, Sha256};

#[cfg(test)]
pub const MANIFEST_FILE_NAME: &str = "devolutions-pwsh-payload.json";
pub const REQUIRED_BINDINGS_ABI_VERSION: u32 = 2;
pub const REQUIRED_BINDINGS_FEATURES: u64 = (1 << 8) | (1 << 13) | (1 << 14) | (1 << 15) | (1 << 16);

const MAX_MANIFEST_BYTES: u64 = 1024 * 1024;
const MAX_MANIFEST_FILES: usize = 4096;
const MANIFEST_SCHEMA: &str = "devolutions-pwsh-payload";
const MANIFEST_SCHEMA_VERSION: u32 = 1;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TrustPolicy {
    RequireHashPinnedManifest,
    AllowUntrustedLocalDevelopment,
}

#[derive(Debug)]
pub enum ValidationError {
    InvalidArgument(String),
    ManifestInvalid(String),
    Untrusted(String),
    HashMismatch(String),
    Incompatible(String),
}

impl ValidationError {
    pub fn message(&self) -> &str {
        match self {
            Self::InvalidArgument(message)
            | Self::ManifestInvalid(message)
            | Self::Untrusted(message)
            | Self::HashMismatch(message)
            | Self::Incompatible(message) => message,
        }
    }
}

pub struct ValidationRequest<'a> {
    pub payload_path: &'a str,
    pub manifest_path: &'a str,
    pub manifest_sha256: &'a str,
    pub trust_policy: TrustPolicy,
}

pub struct ValidatedPayload {
    pub payload_root: PathBuf,
    pub manifest_path: PathBuf,
    pub manifest_sha256: String,
    pub session_policy: SessionPolicy,
    files: Vec<ValidatedFile>,
}

pub struct StagedPayload {
    pub payload_root: PathBuf,
    pub session_policy: SessionPolicy,
    pub staging: PayloadStaging,
}

pub struct PayloadStaging {
    root: PathBuf,
}

struct ValidatedFile {
    relative_path: PathBuf,
    sha256: String,
}

#[derive(Clone, Default)]
pub struct SessionPolicy {
    pub module_paths: HashSet<PathBuf>,
    pub working_directories: HashSet<PathBuf>,
    pub module_imports: HashSet<String>,
    pub module_identities: HashMap<String, ModuleIdentity>,
    pub environment_keys: HashSet<String>,
    pub(crate) staged_module_paths: HashMap<PathBuf, PathBuf>,
    pub(crate) staged_working_directories: HashMap<PathBuf, PathBuf>,
}

#[derive(Clone)]
pub struct ModuleIdentity {
    pub manifest_path: PathBuf,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PayloadManifest {
    schema: String,
    schema_version: u32,
    payload: PayloadIdentity,
    target: PayloadTarget,
    runtime: PayloadRuntime,
    files: Vec<PayloadFile>,
    #[serde(default)]
    trust: Option<ManifestTrust>,
    #[serde(default)]
    session_policy: Option<ManifestSessionPolicy>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PayloadIdentity {
    id: String,
    version: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PayloadTarget {
    rid: String,
    architecture: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PayloadRuntime {
    power_shell_version: String,
    dotnet_version: String,
    hostfxr_version: String,
    bindings_abi_version: u32,
    required_bindings_features: u64,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PayloadFile {
    path: String,
    sha256: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ManifestTrust {
    #[serde(default)]
    allow_symlinks: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ManifestSessionPolicy {
    #[serde(default)]
    module_paths: Vec<String>,
    #[serde(default)]
    working_directories: Vec<String>,
    #[serde(default)]
    module_imports: Vec<String>,
    #[serde(default)]
    module_identities: Vec<ManifestModuleIdentity>,
    #[serde(default)]
    environment_keys: Vec<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ManifestModuleIdentity {
    name: String,
    manifest_path: String,
    version: String,
    sha256: String,
}
pub fn validate(request: ValidationRequest<'_>) -> Result<ValidatedPayload, ValidationError> {
    let payload_root = canonical_directory(request.payload_path, "payload directory")?;
    let manifest_path = canonical_file(request.manifest_path, "payload manifest")?;
    let manifest_bytes = read_manifest(&manifest_path)?;
    let actual_manifest_sha256 = sha256_bytes(&manifest_bytes);

    match request.trust_policy {
        TrustPolicy::RequireHashPinnedManifest => {
            let expected_hash =
                normalize_sha256(request.manifest_sha256, "manifest SHA-256").map_err(ValidationError::Untrusted)?;
            if expected_hash != actual_manifest_sha256 {
                return Err(ValidationError::HashMismatch(
                    "payload manifest SHA-256 does not match the activation pin".to_owned(),
                ));
            }
            if manifest_path.starts_with(&payload_root) {
                return Err(ValidationError::ManifestInvalid(
                    "a hash-pinned payload manifest must reside outside the payload directory".to_owned(),
                ));
            }
        }
        TrustPolicy::AllowUntrustedLocalDevelopment => {
            if !request.manifest_sha256.is_empty() {
                return Err(ValidationError::InvalidArgument(
                    "untrusted local development activation must not provide a manifest SHA-256 pin".to_owned(),
                ));
            }
        }
    }

    let manifest: PayloadManifest = serde_json::from_slice(&manifest_bytes)
        .map_err(|error| ValidationError::ManifestInvalid(format!("payload manifest is not valid JSON: {}", error)))?;
    let (session_policy, files) = validate_manifest(
        &manifest,
        &payload_root,
        request.trust_policy == TrustPolicy::RequireHashPinnedManifest,
    )?;

    Ok(ValidatedPayload {
        payload_root,
        manifest_path,
        manifest_sha256: actual_manifest_sha256,
        session_policy,
        files,
    })
}

pub fn validate_direct_payload(payload_path: &str) -> Result<PathBuf, ValidationError> {
    canonical_directory(payload_path, "payload directory")
}

impl PayloadStaging {
    fn create() -> Result<Self, ValidationError> {
        static NEXT_STAGING: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);

        let parent = std::env::temp_dir();
        let timestamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or(0);
        for attempt in 0..32_u64 {
            let path = parent.join(format!(
                "pwsh-sdk-ffi-{}-{}-{}-{}",
                std::process::id(),
                timestamp,
                NEXT_STAGING.fetch_add(1, std::sync::atomic::Ordering::Relaxed),
                attempt,
            ));
            match fs::create_dir(&path) {
                Ok(()) => {
                    let metadata = fs::symlink_metadata(&path).map_err(|error| {
                        ValidationError::ManifestInvalid(format!(
                            "payload staging directory cannot be inspected: {}",
                            error
                        ))
                    })?;
                    if metadata.file_type().is_symlink() {
                        let _ = fs::remove_dir(&path);
                        return Err(ValidationError::ManifestInvalid(
                            "payload staging directory must not be a symlink".to_owned(),
                        ));
                    }
                    let root = fs::canonicalize(&path).map_err(|error| {
                        ValidationError::ManifestInvalid(format!(
                            "payload staging directory cannot be canonicalized: {}",
                            error
                        ))
                    })?;
                    return Ok(Self { root });
                }
                Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
                Err(error) => {
                    return Err(ValidationError::ManifestInvalid(format!(
                        "payload staging directory cannot be created: {}",
                        error
                    )));
                }
            }
        }
        Err(ValidationError::ManifestInvalid(
            "payload staging directory could not be allocated".to_owned(),
        ))
    }
}

impl Drop for PayloadStaging {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}

pub fn stage(validated: ValidatedPayload) -> Result<StagedPayload, ValidationError> {
    let staging = PayloadStaging::create()?;
    for file in &validated.files {
        let source = validated.payload_root.join(&file.relative_path);
        let destination = staging.root.join(&file.relative_path);
        let parent = destination
            .parent()
            .ok_or_else(|| ValidationError::ManifestInvalid("payload staging destination has no parent".to_owned()))?;
        fs::create_dir_all(parent).map_err(|error| {
            ValidationError::ManifestInvalid(format!("payload staging directory cannot be created: {}", error))
        })?;
        fs::copy(&source, &destination).map_err(|error| {
            ValidationError::ManifestInvalid(format!("payload file cannot be copied into staging: {}", error))
        })?;
    }

    let staged_paths = collect_regular_file_paths(&staging.root, false)?;
    let expected_paths = validated
        .files
        .iter()
        .map(|file| path_to_manifest_string(&file.relative_path))
        .collect::<Result<HashSet<_>, _>>()?;
    if staged_paths != expected_paths {
        return Err(ValidationError::ManifestInvalid(
            "payload staging contains files that are not declared by the manifest".to_owned(),
        ));
    }
    for file in &validated.files {
        let staged = staging.root.join(&file.relative_path);
        let actual = sha256_file(&staged).map_err(|error| {
            ValidationError::ManifestInvalid(format!("staged payload file cannot be hashed: {}", error))
        })?;
        if actual != file.sha256 {
            return Err(ValidationError::HashMismatch(
                "staged payload file SHA-256 does not match the manifest".to_owned(),
            ));
        }
    }

    let session_policy = rebase_session_policy(validated.session_policy, &validated.payload_root, &staging.root)?;
    Ok(StagedPayload {
        payload_root: staging.root.clone(),
        session_policy,
        staging,
    })
}

fn canonical_directory(path: &str, description: &str) -> Result<PathBuf, ValidationError> {
    if path.is_empty() {
        return Err(ValidationError::InvalidArgument(format!(
            "{} must be non-empty UTF-8",
            description
        )));
    }

    let canonical = fs::canonicalize(path).map_err(|error| {
        ValidationError::InvalidArgument(format!("{} cannot be canonicalized: {}", description, error))
    })?;
    if !canonical.is_dir() {
        return Err(ValidationError::InvalidArgument(format!(
            "{} is not a directory: {}",
            description,
            canonical.display()
        )));
    }
    Ok(canonical)
}

fn canonical_file(path: &str, description: &str) -> Result<PathBuf, ValidationError> {
    if path.is_empty() {
        return Err(ValidationError::InvalidArgument(format!(
            "{} must be non-empty UTF-8",
            description
        )));
    }

    let canonical = fs::canonicalize(path).map_err(|error| {
        ValidationError::InvalidArgument(format!("{} cannot be canonicalized: {}", description, error))
    })?;
    if !canonical.is_file() {
        return Err(ValidationError::InvalidArgument(format!(
            "{} is not a file: {}",
            description,
            canonical.display()
        )));
    }
    Ok(canonical)
}

fn read_manifest(path: &Path) -> Result<Vec<u8>, ValidationError> {
    let metadata = fs::metadata(path).map_err(|error| {
        ValidationError::ManifestInvalid(format!("payload manifest metadata cannot be read: {}", error))
    })?;
    if metadata.len() > MAX_MANIFEST_BYTES {
        return Err(ValidationError::ManifestInvalid(format!(
            "payload manifest exceeds the {} byte limit",
            MAX_MANIFEST_BYTES
        )));
    }
    fs::read(path)
        .map_err(|error| ValidationError::ManifestInvalid(format!("payload manifest cannot be read: {}", error)))
}

fn validate_manifest(
    manifest: &PayloadManifest,
    payload_root: &Path,
    require_complete_file_closure: bool,
) -> Result<(SessionPolicy, Vec<ValidatedFile>), ValidationError> {
    if manifest.schema != MANIFEST_SCHEMA || manifest.schema_version != MANIFEST_SCHEMA_VERSION {
        return Err(ValidationError::ManifestInvalid(format!(
            "payload manifest must declare schema '{}' version {}",
            MANIFEST_SCHEMA, MANIFEST_SCHEMA_VERSION
        )));
    }
    if manifest.payload.id != "PowerShell" || manifest.payload.version.is_empty() {
        return Err(ValidationError::ManifestInvalid(
            "payload manifest must identify a non-empty PowerShell payload version".to_owned(),
        ));
    }

    let (current_rid, current_architecture) = current_target();
    if manifest.target.rid != current_rid || manifest.target.architecture != current_architecture {
        return Err(ValidationError::Incompatible(format!(
            "payload target {} ({}) is incompatible with this process target {} ({})",
            manifest.target.rid, manifest.target.architecture, current_rid, current_architecture
        )));
    }

    if manifest.runtime.bindings_abi_version != REQUIRED_BINDINGS_ABI_VERSION
        || manifest.runtime.required_bindings_features & REQUIRED_BINDINGS_FEATURES != REQUIRED_BINDINGS_FEATURES
    {
        return Err(ValidationError::Incompatible(format!(
            "payload manifest bindings ABI/features are incompatible; requires ABI {} and feature mask 0x{:X}",
            REQUIRED_BINDINGS_ABI_VERSION, REQUIRED_BINDINGS_FEATURES
        )));
    }

    let allow_symlinks = manifest
        .trust
        .as_ref()
        .map(|trust| trust.allow_symlinks)
        .unwrap_or(false);
    let files = validate_payload_files(manifest, payload_root, allow_symlinks, require_complete_file_closure)?;
    validate_runtime_versions(manifest, payload_root)?;
    let session_policy = validate_session_policy(manifest, payload_root)?;
    Ok((session_policy, files))
}

fn validate_session_policy(manifest: &PayloadManifest, payload_root: &Path) -> Result<SessionPolicy, ValidationError> {
    let Some(policy) = &manifest.session_policy else {
        return Ok(SessionPolicy::default());
    };

    const MAX_POLICY_ENTRIES: usize = 32;
    for (description, values) in [
        ("modulePaths", &policy.module_paths),
        ("workingDirectories", &policy.working_directories),
        ("moduleImports", &policy.module_imports),
        ("environmentKeys", &policy.environment_keys),
    ] {
        if values.len() > MAX_POLICY_ENTRIES {
            return Err(ValidationError::ManifestInvalid(format!(
                "sessionPolicy.{} contains more than {} entries",
                description, MAX_POLICY_ENTRIES
            )));
        }
    }

    let module_paths = policy_directories(&policy.module_paths, payload_root, "module path")?;
    let working_directories = policy_directories(&policy.working_directories, payload_root, "working directory")?;
    let module_imports = policy_names(&policy.module_imports, "module import", is_module_import_name)?;
    let environment_keys = policy_names(&policy.environment_keys, "environment key", is_environment_key)?;
    let module_identities = policy_module_identities(&policy.module_identities, &manifest.files, payload_root)?;
    if !module_imports.is_empty() && module_paths.is_empty() {
        return Err(ValidationError::ManifestInvalid(
            "sessionPolicy.moduleImports requires at least one declared modulePaths entry".to_owned(),
        ));
    }
    if module_identities.len() > MAX_POLICY_ENTRIES {
        return Err(ValidationError::ManifestInvalid(format!(
            "sessionPolicy.moduleIdentities contains more than {} entries",
            MAX_POLICY_ENTRIES
        )));
    }
    if !module_imports.is_empty()
        && (module_identities.len() != module_imports.len()
            || !module_imports.iter().all(|name| module_identities.contains_key(name)))
    {
        return Err(ValidationError::ManifestInvalid(
            "sessionPolicy.moduleImports requires one exact module identity per approved import".to_owned(),
        ));
    }
    if module_identities.values().any(|identity| {
        !module_paths
            .iter()
            .any(|module_path| identity.manifest_path.starts_with(module_path))
    }) {
        return Err(ValidationError::ManifestInvalid(
            "each module identity manifest must reside beneath an approved module path".to_owned(),
        ));
    }

    Ok(SessionPolicy {
        staged_module_paths: module_paths.iter().cloned().map(|path| (path.clone(), path)).collect(),
        staged_working_directories: working_directories
            .iter()
            .cloned()
            .map(|path| (path.clone(), path))
            .collect(),
        module_paths,
        working_directories,
        module_imports,
        module_identities,
        environment_keys,
    })
}

fn policy_module_identities(
    values: &[ManifestModuleIdentity],
    files: &[PayloadFile],
    payload_root: &Path,
) -> Result<HashMap<String, ModuleIdentity>, ValidationError> {
    let mut result = HashMap::with_capacity(values.len());
    for value in values {
        if !is_module_import_name(&value.name) || !is_module_version(&value.version) {
            return Err(ValidationError::ManifestInvalid(
                "sessionPolicy module identity name or version is invalid".to_owned(),
            ));
        }
        let relative = parse_relative_file_path(&value.manifest_path)?;
        let manifest_path = fs::canonicalize(payload_root.join(relative)).map_err(|error| {
            ValidationError::ManifestInvalid(format!(
                "sessionPolicy module manifest '{}' cannot be canonicalized: {}",
                value.manifest_path, error
            ))
        })?;
        if !manifest_path.starts_with(payload_root)
            || !manifest_path.is_file()
            || !matches!(
                manifest_path.extension().and_then(|extension| extension.to_str()),
                Some("psd1")
            )
        {
            return Err(ValidationError::ManifestInvalid(
                "sessionPolicy module manifest must be a .psd1 file inside the payload".to_owned(),
            ));
        }
        if manifest_path.file_stem().and_then(|stem| stem.to_str()) != Some(value.name.as_str()) {
            return Err(ValidationError::ManifestInvalid(
                "sessionPolicy module manifest file name must match its module name".to_owned(),
            ));
        }
        let expected_hash =
            normalize_sha256(&value.sha256, "module manifest SHA-256").map_err(ValidationError::ManifestInvalid)?;
        let declared_file = files
            .iter()
            .find(|file| file.path == value.manifest_path)
            .ok_or_else(|| {
                ValidationError::ManifestInvalid(
                    "sessionPolicy module manifest must also be hash-pinned in files".to_owned(),
                )
            })?;
        if normalize_sha256(&declared_file.sha256, "payload file SHA-256").map_err(ValidationError::ManifestInvalid)?
            != expected_hash
            || sha256_file(&manifest_path).map_err(|error| {
                ValidationError::ManifestInvalid(format!("module manifest cannot be hashed: {}", error))
            })? != expected_hash
        {
            return Err(ValidationError::HashMismatch(
                "sessionPolicy module manifest hash does not match its pin".to_owned(),
            ));
        }
        validate_module_manifest_version(&manifest_path, &value.version)?;
        if result
            .insert(value.name.to_ascii_lowercase(), ModuleIdentity { manifest_path })
            .is_some()
        {
            return Err(ValidationError::ManifestInvalid(
                "sessionPolicy module identities must be uniquely named".to_owned(),
            ));
        }
    }
    Ok(result)
}

fn validate_module_manifest_version(path: &Path, expected: &str) -> Result<(), ValidationError> {
    const MAX_MODULE_MANIFEST_BYTES: u64 = 64 * 1024;
    let metadata = fs::metadata(path).map_err(|error| {
        ValidationError::ManifestInvalid(format!("module manifest metadata cannot be read: {}", error))
    })?;
    if metadata.len() > MAX_MODULE_MANIFEST_BYTES {
        return Err(ValidationError::ManifestInvalid(
            "module manifest exceeds the 64 KiB limit".to_owned(),
        ));
    }
    let content = fs::read_to_string(path).map_err(|error| {
        ValidationError::ManifestInvalid(format!("module manifest cannot be read as UTF-8: {}", error))
    })?;
    let actual = content.lines().find_map(|line| {
        let line = line.split_once('#').map_or(line, |(value, _)| value).trim();
        let (name, value) = line.split_once('=')?;
        if !name.trim().eq_ignore_ascii_case("ModuleVersion") {
            return None;
        }
        Some(value.trim().trim_matches(['\'', '"']).trim())
    });
    if actual != Some(expected) {
        return Err(ValidationError::ManifestInvalid(
            "module manifest ModuleVersion does not match its identity pin".to_owned(),
        ));
    }
    Ok(())
}

fn policy_directories(
    values: &[String],
    payload_root: &Path,
    description: &str,
) -> Result<HashSet<PathBuf>, ValidationError> {
    let mut result = HashSet::new();
    let mut keys = HashSet::new();
    for value in values {
        let relative = parse_relative_directory_path(value)?;
        if !keys.insert(normalized_path_key(value)) {
            return Err(ValidationError::ManifestInvalid(format!(
                "sessionPolicy contains duplicate {} '{}'",
                description, value
            )));
        }
        let canonical = fs::canonicalize(payload_root.join(relative)).map_err(|error| {
            ValidationError::ManifestInvalid(format!(
                "sessionPolicy {} '{}' cannot be canonicalized: {}",
                description, value, error
            ))
        })?;
        if !canonical.starts_with(payload_root) || !canonical.is_dir() {
            return Err(ValidationError::ManifestInvalid(format!(
                "sessionPolicy {} '{}' must resolve to a directory inside the payload",
                description, value
            )));
        }
        result.insert(canonical);
    }
    Ok(result)
}

fn policy_names(
    values: &[String],
    description: &str,
    is_valid: fn(&str) -> bool,
) -> Result<HashSet<String>, ValidationError> {
    let mut result = HashSet::new();
    for value in values {
        if !is_valid(value) || !result.insert(value.to_ascii_lowercase()) {
            return Err(ValidationError::ManifestInvalid(format!(
                "sessionPolicy {} '{}' is invalid or duplicated",
                description, value
            )));
        }
    }
    Ok(result)
}

fn is_module_import_name(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

fn is_module_version(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-'))
        && value.as_bytes()[0].is_ascii_digit()
        && !matches!(value.as_bytes().last(), Some(b'.' | b'-'))
}

fn is_environment_key(value: &str) -> bool {
    let mut bytes = value.bytes();
    match bytes.next() {
        Some(byte) if byte.is_ascii_alphabetic() || byte == b'_' => {}
        _ => return false,
    }
    value.len() <= 64 && bytes.all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
}

fn validate_payload_files(
    manifest: &PayloadManifest,
    payload_root: &Path,
    allow_symlinks: bool,
    require_complete_file_closure: bool,
) -> Result<Vec<ValidatedFile>, ValidationError> {
    if manifest.files.is_empty() || manifest.files.len() > MAX_MANIFEST_FILES {
        return Err(ValidationError::ManifestInvalid(format!(
            "payload manifest must contain between one and {} files",
            MAX_MANIFEST_FILES
        )));
    }

    let mut paths = HashSet::new();
    let mut files = Vec::with_capacity(manifest.files.len());
    for file in &manifest.files {
        let relative = parse_relative_file_path(&file.path)?;
        let path_key = normalized_path_key(&file.path);
        if !paths.insert(path_key) {
            return Err(ValidationError::ManifestInvalid(format!(
                "payload manifest contains duplicate file path '{}'",
                file.path
            )));
        }
        let expected_hash =
            normalize_sha256(&file.sha256, "payload file SHA-256").map_err(ValidationError::ManifestInvalid)?;
        let payload_file = payload_root.join(&relative);
        let metadata = fs::symlink_metadata(&payload_file).map_err(|error| {
            ValidationError::ManifestInvalid(format!("manifest file '{}' cannot be inspected: {}", file.path, error))
        })?;
        if !allow_symlinks && has_symlink_component(payload_root, &payload_file, &metadata)? {
            return Err(ValidationError::ManifestInvalid(format!(
                "manifest file '{}' traverses a symlink but the manifest does not permit symlinks",
                file.path
            )));
        }

        let canonical_file = fs::canonicalize(&payload_file).map_err(|error| {
            ValidationError::ManifestInvalid(format!(
                "manifest file '{}' cannot be canonicalized: {}",
                file.path, error
            ))
        })?;
        if !canonical_file.starts_with(payload_root) || !canonical_file.is_file() {
            return Err(ValidationError::ManifestInvalid(format!(
                "manifest file '{}' escapes the canonical payload directory",
                file.path
            )));
        }

        let actual_hash = sha256_file(&canonical_file).map_err(|error| {
            ValidationError::ManifestInvalid(format!("manifest file '{}' cannot be hashed: {}", file.path, error))
        })?;
        if expected_hash != actual_hash {
            return Err(ValidationError::HashMismatch(format!(
                "payload file '{}' SHA-256 does not match the manifest",
                file.path
            )));
        }
        files.push(ValidatedFile {
            relative_path: relative,
            sha256: expected_hash,
        });
    }

    for required_file in required_payload_files() {
        if !paths.contains(&normalized_path_key(required_file)) {
            return Err(ValidationError::ManifestInvalid(format!(
                "payload manifest does not hash required file '{}'",
                required_file
            )));
        }
    }
    if require_complete_file_closure {
        let actual_paths = collect_regular_file_paths(payload_root, allow_symlinks)?;
        for path in actual_paths {
            if !paths.contains(&normalized_path_key(&path)) {
                return Err(ValidationError::ManifestInvalid(format!(
                    "payload manifest does not hash regular file '{}'",
                    path
                )));
            }
        }
    }
    Ok(files)
}

fn collect_regular_file_paths(payload_root: &Path, allow_symlinks: bool) -> Result<HashSet<String>, ValidationError> {
    let mut result = HashSet::new();
    let mut ancestors = HashSet::new();
    collect_regular_file_paths_in(payload_root, payload_root, allow_symlinks, &mut ancestors, &mut result)?;
    Ok(result)
}

fn collect_regular_file_paths_in(
    payload_root: &Path,
    directory: &Path,
    allow_symlinks: bool,
    ancestors: &mut HashSet<PathBuf>,
    result: &mut HashSet<String>,
) -> Result<(), ValidationError> {
    let canonical_directory = fs::canonicalize(directory).map_err(|error| {
        ValidationError::ManifestInvalid(format!("payload directory cannot be canonicalized: {}", error))
    })?;
    if !canonical_directory.starts_with(payload_root) || !canonical_directory.is_dir() {
        return Err(ValidationError::ManifestInvalid(
            "payload directory escapes the canonical payload root".to_owned(),
        ));
    }
    if !ancestors.insert(canonical_directory.clone()) {
        return Err(ValidationError::ManifestInvalid(
            "payload directory contains a symlink cycle".to_owned(),
        ));
    }

    let outcome = (|| {
        for entry in fs::read_dir(directory)
            .map_err(|error| ValidationError::ManifestInvalid(format!("payload directory cannot be read: {}", error)))?
        {
            let entry = entry.map_err(|error| {
                ValidationError::ManifestInvalid(format!("payload directory entry cannot be read: {}", error))
            })?;
            let path = entry.path();
            let metadata = fs::symlink_metadata(&path).map_err(|error| {
                ValidationError::ManifestInvalid(format!("payload entry cannot be inspected: {}", error))
            })?;
            let file_type = metadata.file_type();
            if file_type.is_symlink() {
                if !allow_symlinks {
                    return Err(ValidationError::ManifestInvalid(
                        "payload contains a symlink but the manifest does not permit symlinks".to_owned(),
                    ));
                }
                let target = fs::canonicalize(&path).map_err(|error| {
                    ValidationError::ManifestInvalid(format!("payload symlink cannot be canonicalized: {}", error))
                })?;
                if !target.starts_with(payload_root) {
                    return Err(ValidationError::ManifestInvalid(
                        "payload symlink escapes the canonical payload root".to_owned(),
                    ));
                }
                if target.is_file() {
                    result.insert(relative_manifest_path(payload_root, &path)?);
                } else if target.is_dir() {
                    collect_regular_file_paths_in(payload_root, &path, allow_symlinks, ancestors, result)?;
                } else {
                    return Err(ValidationError::ManifestInvalid(
                        "payload symlink must resolve to a regular file or directory".to_owned(),
                    ));
                }
            } else if file_type.is_file() {
                result.insert(relative_manifest_path(payload_root, &path)?);
            } else if file_type.is_dir() {
                collect_regular_file_paths_in(payload_root, &path, allow_symlinks, ancestors, result)?;
            }
        }
        Ok(())
    })();
    ancestors.remove(&canonical_directory);
    outcome
}

fn relative_manifest_path(payload_root: &Path, path: &Path) -> Result<String, ValidationError> {
    let relative = path
        .strip_prefix(payload_root)
        .map_err(|_| ValidationError::ManifestInvalid("payload file is outside the payload root".to_owned()))?;
    let value = path_to_manifest_string(relative)?;
    parse_relative_file_path(&value)?;
    Ok(value)
}

fn path_to_manifest_string(path: &Path) -> Result<String, ValidationError> {
    let mut components = Vec::new();
    for component in path.components() {
        let Component::Normal(component) = component else {
            return Err(ValidationError::ManifestInvalid(
                "payload file path is not a canonical relative path".to_owned(),
            ));
        };
        let component = component
            .to_str()
            .ok_or_else(|| ValidationError::ManifestInvalid("payload file path is not valid UTF-8".to_owned()))?;
        components.push(component);
    }
    if components.is_empty() {
        return Err(ValidationError::ManifestInvalid(
            "payload file path is not a canonical relative path".to_owned(),
        ));
    }
    Ok(components.join("/"))
}

fn rebase_session_policy(
    mut policy: SessionPolicy,
    source_root: &Path,
    staging_root: &Path,
) -> Result<SessionPolicy, ValidationError> {
    policy.staged_module_paths = rebase_policy_directories(&policy.module_paths, source_root, staging_root)?;
    policy.staged_working_directories =
        rebase_policy_directories(&policy.working_directories, source_root, staging_root)?;
    for identity in policy.module_identities.values_mut() {
        identity.manifest_path = rebase_payload_path(&identity.manifest_path, source_root, staging_root)?;
        if !identity.manifest_path.is_file() {
            return Err(ValidationError::ManifestInvalid(
                "staged module manifest is unavailable".to_owned(),
            ));
        }
    }
    Ok(policy)
}

fn rebase_policy_directories(
    source_paths: &HashSet<PathBuf>,
    source_root: &Path,
    staging_root: &Path,
) -> Result<HashMap<PathBuf, PathBuf>, ValidationError> {
    let mut staged = HashMap::with_capacity(source_paths.len());
    for source in source_paths {
        let relative = source.strip_prefix(source_root).map_err(|_| {
            ValidationError::ManifestInvalid("session policy directory is outside the payload root".to_owned())
        })?;
        let target = staging_root.join(relative);
        fs::create_dir_all(&target).map_err(|error| {
            ValidationError::ManifestInvalid(format!("staged session policy directory cannot be created: {}", error))
        })?;
        let target = fs::canonicalize(&target).map_err(|error| {
            ValidationError::ManifestInvalid(format!(
                "staged session policy directory cannot be canonicalized: {}",
                error
            ))
        })?;
        if !target.starts_with(staging_root) || !target.is_dir() {
            return Err(ValidationError::ManifestInvalid(
                "staged session policy directory escapes the payload".to_owned(),
            ));
        }
        staged.insert(source.clone(), target);
    }
    Ok(staged)
}

fn rebase_payload_path(source: &Path, source_root: &Path, staging_root: &Path) -> Result<PathBuf, ValidationError> {
    let relative = source
        .strip_prefix(source_root)
        .map_err(|_| ValidationError::ManifestInvalid("session policy file is outside the payload root".to_owned()))?;
    let staged = fs::canonicalize(staging_root.join(relative)).map_err(|error| {
        ValidationError::ManifestInvalid(format!("staged session policy file cannot be canonicalized: {}", error))
    })?;
    if !staged.starts_with(staging_root) {
        return Err(ValidationError::ManifestInvalid(
            "staged session policy file escapes the payload".to_owned(),
        ));
    }
    Ok(staged)
}

fn has_symlink_component(
    payload_root: &Path,
    payload_file: &Path,
    file_metadata: &fs::Metadata,
) -> Result<bool, ValidationError> {
    if file_metadata.file_type().is_symlink() {
        return Ok(true);
    }

    let relative = payload_file
        .strip_prefix(payload_root)
        .map_err(|_| ValidationError::ManifestInvalid("manifest file is outside the payload root".to_owned()))?;
    let mut current = payload_root.to_path_buf();
    let components = relative.components().collect::<Vec<_>>();
    for component in components.iter().take(components.len().saturating_sub(1)) {
        current.push(component.as_os_str());
        let metadata = fs::symlink_metadata(&current).map_err(|error| {
            ValidationError::ManifestInvalid(format!("manifest file parent cannot be inspected: {}", error))
        })?;
        if metadata.file_type().is_symlink() {
            return Ok(true);
        }
    }
    Ok(false)
}

fn validate_runtime_versions(manifest: &PayloadManifest, payload_root: &Path) -> Result<(), ValidationError> {
    if manifest.runtime.power_shell_version != manifest.payload.version {
        return Err(ValidationError::ManifestInvalid(
            "payload.version and runtime.powerShellVersion must match".to_owned(),
        ));
    }
    if manifest.runtime.dotnet_version.is_empty() || manifest.runtime.hostfxr_version.is_empty() {
        return Err(ValidationError::ManifestInvalid(
            "payload manifest runtime versions must be non-empty".to_owned(),
        ));
    }

    let actual_power_shell_version = power_shell_version(payload_root)?;
    if manifest.runtime.power_shell_version != actual_power_shell_version {
        return Err(ValidationError::Incompatible(format!(
            "payload PowerShell version {} does not match manifest version {}",
            actual_power_shell_version, manifest.runtime.power_shell_version
        )));
    }

    let actual_dotnet_version = dotnet_version(payload_root)?;
    if manifest.runtime.dotnet_version != actual_dotnet_version {
        return Err(ValidationError::Incompatible(format!(
            "payload .NET runtime version {} does not match manifest version {}",
            actual_dotnet_version, manifest.runtime.dotnet_version
        )));
    }

    let actual_hostfxr_version = hostfxr_version(payload_root)?;
    if manifest.runtime.hostfxr_version != actual_hostfxr_version {
        return Err(ValidationError::Incompatible(format!(
            "payload hostfxr version {} does not match manifest version {}",
            actual_hostfxr_version, manifest.runtime.hostfxr_version
        )));
    }
    Ok(())
}

fn parse_relative_file_path(path: &str) -> Result<PathBuf, ValidationError> {
    if path.is_empty() || path.contains('\\') || path.starts_with('/') || path.ends_with('/') || path.contains("//") {
        return Err(ValidationError::ManifestInvalid(format!(
            "manifest file path '{}' is not a canonical relative path",
            path
        )));
    }

    let relative = Path::new(path);
    if relative
        .components()
        .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(ValidationError::ManifestInvalid(format!(
            "manifest file path '{}' must not contain traversal, root, or dot components",
            path
        )));
    }
    Ok(relative.to_path_buf())
}

fn parse_relative_directory_path(path: &str) -> Result<PathBuf, ValidationError> {
    if path == "." {
        return Ok(PathBuf::new());
    }
    parse_relative_file_path(path)
}

fn normalized_path_key(path: &str) -> String {
    if cfg!(windows) {
        path.to_lowercase()
    } else {
        path.to_owned()
    }
}

fn normalize_sha256(value: &str, description: &str) -> Result<String, String> {
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(format!("{} must be 64 hexadecimal characters", description));
    }
    Ok(value.to_ascii_lowercase())
}

fn sha256_file(path: &Path) -> std::io::Result<String> {
    let mut file = fs::File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn sha256_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn current_target() -> (&'static str, &'static str) {
    match (std::env::consts::OS, std::env::consts::ARCH) {
        ("windows", "x86_64") => ("win-x64", "x64"),
        ("windows", "aarch64") => ("win-arm64", "arm64"),
        ("windows", "x86") => ("win-x86", "x86"),
        ("linux", "x86_64") => ("linux-x64", "x64"),
        ("linux", "aarch64") => ("linux-arm64", "arm64"),
        ("macos", "x86_64") => ("osx-x64", "x64"),
        ("macos", "aarch64") => ("osx-arm64", "arm64"),
        (_, architecture) => ("unsupported", architecture),
    }
}

fn required_payload_files() -> &'static [&'static str] {
    if cfg!(windows) {
        &[
            "pwsh.dll",
            "pwsh.runtimeconfig.json",
            "pwsh.deps.json",
            "System.Management.Automation.dll",
            "hostfxr.dll",
            "coreclr.dll",
        ]
    } else {
        &[
            "pwsh.dll",
            "pwsh.runtimeconfig.json",
            "pwsh.deps.json",
            "System.Management.Automation.dll",
        ]
    }
}

fn power_shell_version(payload_root: &Path) -> Result<String, ValidationError> {
    let path = payload_root.join("pwsh.deps.json");
    let bytes = fs::read(&path).map_err(|error| {
        ValidationError::Incompatible(format!("PowerShell dependency manifest cannot be read: {}", error))
    })?;
    let document: serde_json::Value = serde_json::from_slice(&bytes).map_err(|error| {
        ValidationError::Incompatible(format!("PowerShell dependency manifest is invalid: {}", error))
    })?;
    let libraries = document
        .get("libraries")
        .and_then(serde_json::Value::as_object)
        .ok_or_else(|| ValidationError::Incompatible("PowerShell dependency manifest has no libraries".to_owned()))?;
    libraries
        .keys()
        .find_map(|name| name.strip_prefix("System.Management.Automation/").map(str::to_owned))
        .filter(|version| !version.is_empty())
        .ok_or_else(|| ValidationError::Incompatible("PowerShell dependency manifest has no SMA version".to_owned()))
}

fn dotnet_version(payload_root: &Path) -> Result<String, ValidationError> {
    let path = payload_root.join("pwsh.runtimeconfig.json");
    let bytes = fs::read(&path).map_err(|error| {
        ValidationError::Incompatible(format!("PowerShell runtime config cannot be read: {}", error))
    })?;
    let document: serde_json::Value = serde_json::from_slice(&bytes)
        .map_err(|error| ValidationError::Incompatible(format!("PowerShell runtime config is invalid: {}", error)))?;
    let runtime_options = document
        .get("runtimeOptions")
        .and_then(serde_json::Value::as_object)
        .ok_or_else(|| ValidationError::Incompatible("PowerShell runtime config has no runtimeOptions".to_owned()))?;

    let included_framework_version = runtime_options
        .get("includedFrameworks")
        .and_then(serde_json::Value::as_array)
        .and_then(|frameworks| {
            frameworks.iter().find_map(|framework| {
                (framework.get("name").and_then(serde_json::Value::as_str) == Some("Microsoft.NETCore.App"))
                    .then(|| framework.get("version").and_then(serde_json::Value::as_str))
                    .flatten()
            })
        });
    let framework_version = runtime_options
        .get("framework")
        .and_then(serde_json::Value::as_object)
        .and_then(|framework| {
            (framework.get("name").and_then(serde_json::Value::as_str) == Some("Microsoft.NETCore.App"))
                .then(|| framework.get("version").and_then(serde_json::Value::as_str))
                .flatten()
        });
    included_framework_version
        .or(framework_version)
        .filter(|version| !version.is_empty())
        .map(str::to_owned)
        .ok_or_else(|| {
            ValidationError::Incompatible("PowerShell runtime config has no Microsoft.NETCore.App version".to_owned())
        })
}

#[cfg(windows)]
fn hostfxr_version(payload_root: &Path) -> Result<String, ValidationError> {
    windows_product_version(&payload_root.join("hostfxr.dll"))
        .ok_or_else(|| ValidationError::Incompatible("hostfxr.dll has no readable product version".to_owned()))
}

#[cfg(not(windows))]
fn hostfxr_version(_payload_root: &Path) -> Result<String, ValidationError> {
    Err(ValidationError::Incompatible(
        "hostfxr product version validation is currently supported only on Windows".to_owned(),
    ))
}

#[cfg(windows)]
fn windows_product_version(path: &Path) -> Option<String> {
    use std::ffi::c_void;
    use std::os::windows::ffi::OsStrExt;

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct Translation {
        language: u16,
        code_page: u16,
    }

    #[link(name = "version")]
    extern "system" {
        fn GetFileVersionInfoSizeW(file_name: *const u16, handle: *mut u32) -> u32;
        fn GetFileVersionInfoW(file_name: *const u16, handle: u32, length: u32, data: *mut c_void) -> i32;
        fn VerQueryValueW(
            block: *const c_void,
            sub_block: *const u16,
            value: *mut *mut c_void,
            length: *mut u32,
        ) -> i32;
    }

    let mut file_name = path.as_os_str().encode_wide().collect::<Vec<_>>();
    file_name.push(0);
    let mut handle = 0;
    let length = unsafe { GetFileVersionInfoSizeW(file_name.as_ptr(), &mut handle) };
    if length == 0 {
        return None;
    }
    let mut data = vec![0_u8; length as usize];
    if unsafe { GetFileVersionInfoW(file_name.as_ptr(), 0, length, data.as_mut_ptr() as *mut c_void) } == 0 {
        return None;
    }

    let translation_key = [
        b'\\' as u16,
        b'V' as u16,
        b'a' as u16,
        b'r' as u16,
        b'F' as u16,
        b'i' as u16,
        b'l' as u16,
        b'e' as u16,
        b'I' as u16,
        b'n' as u16,
        b'f' as u16,
        b'o' as u16,
        b'\\' as u16,
        b'T' as u16,
        b'r' as u16,
        b'a' as u16,
        b'n' as u16,
        b's' as u16,
        b'l' as u16,
        b'a' as u16,
        b't' as u16,
        b'i' as u16,
        b'o' as u16,
        b'n' as u16,
        0,
    ];
    let mut translation_value = std::ptr::null_mut();
    let mut translation_length = 0;
    if unsafe {
        VerQueryValueW(
            data.as_ptr() as *const c_void,
            translation_key.as_ptr(),
            &mut translation_value,
            &mut translation_length,
        )
    } == 0
        || translation_length < 4
    {
        return None;
    }
    let translation = unsafe { *(translation_value as *const Translation) };
    let product_key = format!(
        "\\StringFileInfo\\{:04x}{:04x}\\ProductVersion",
        translation.language, translation.code_page
    );
    let mut product_key_wide = product_key.encode_utf16().collect::<Vec<_>>();
    product_key_wide.push(0);
    let mut product_value = std::ptr::null_mut();
    let mut product_length = 0;
    if unsafe {
        VerQueryValueW(
            data.as_ptr() as *const c_void,
            product_key_wide.as_ptr(),
            &mut product_value,
            &mut product_length,
        )
    } == 0
        || product_length == 0
    {
        return None;
    }
    let value = unsafe { std::slice::from_raw_parts(product_value as *const u16, product_length as usize) };
    let product_version = String::from_utf16_lossy(value)
        .trim_end_matches('\0')
        .split_whitespace()
        .next()?
        .to_owned();
    (!product_version.is_empty()).then_some(product_version)
}

#[cfg(test)]
pub fn create_test_manifest(payload_root: &Path) -> (PathBuf, String) {
    let payload_root = fs::canonicalize(payload_root).unwrap();
    let mut paths = collect_regular_file_paths(&payload_root, false)
        .unwrap()
        .into_iter()
        .collect::<Vec<_>>();
    paths.sort();
    let files = paths
        .iter()
        .map(|path| {
            serde_json::json!({
                "path": path,
                "sha256": sha256_file(&payload_root.join(path)).unwrap(),
            })
        })
        .collect::<Vec<_>>();
    let power_shell_version = power_shell_version(&payload_root).unwrap();
    let dotnet_version = dotnet_version(&payload_root).unwrap();
    let hostfxr_version = hostfxr_version(&payload_root).unwrap();
    let manifest = serde_json::json!({
        "schema": MANIFEST_SCHEMA,
        "schemaVersion": MANIFEST_SCHEMA_VERSION,
        "payload": {
            "id": "PowerShell",
            "version": power_shell_version,
        },
        "target": {
            "rid": current_target().0,
            "architecture": current_target().1,
        },
        "runtime": {
            "powerShellVersion": power_shell_version,
            "dotnetVersion": dotnet_version,
            "hostfxrVersion": hostfxr_version,
            "bindingsAbiVersion": REQUIRED_BINDINGS_ABI_VERSION,
            "requiredBindingsFeatures": REQUIRED_BINDINGS_FEATURES,
        },
        "files": files,
        "trust": {
            "allowSymlinks": false,
        },
    });
    let manifest_directory = std::env::current_dir()
        .unwrap()
        .join("target")
        .join(format!("pwsh-sdk-ffi-test-manifest-{}", std::process::id()));
    fs::create_dir_all(&manifest_directory).unwrap();
    let manifest_path = manifest_directory.join(MANIFEST_FILE_NAME);
    let manifest_bytes = serde_json::to_vec_pretty(&manifest).unwrap();
    let manifest_hash = sha256_bytes(&manifest_bytes);
    fs::write(&manifest_path, manifest_bytes).unwrap();
    (manifest_path, manifest_hash)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};

    static NEXT_FIXTURE: AtomicU64 = AtomicU64::new(1);

    fn fixture_root() -> PathBuf {
        let root = std::env::current_dir().unwrap().join("target").join(format!(
            "pwsh-sdk-ffi-payload-manifest-{}-{}",
            std::process::id(),
            NEXT_FIXTURE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir_all(&root).unwrap();
        root
    }

    fn cleanup(root: &Path) {
        let _ = fs::remove_dir_all(root);
        let manifest = root
            .parent()
            .unwrap()
            .join(format!("{}.manifest.json", root.file_name().unwrap().to_string_lossy()));
        let _ = fs::remove_file(manifest);
    }

    fn write_file(root: &Path, path: &str, content: &[u8]) {
        let path = root.join(path);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).unwrap();
        }
        fs::write(path, content).unwrap();
    }

    fn file_entry(root: &Path, path: &str) -> serde_json::Value {
        serde_json::json!({
            "path": path,
            "sha256": sha256_file(&root.join(path)).unwrap(),
        })
    }

    fn base_manifest(files: Vec<serde_json::Value>) -> serde_json::Value {
        serde_json::json!({
            "schema": MANIFEST_SCHEMA,
            "schemaVersion": MANIFEST_SCHEMA_VERSION,
            "payload": {
                "id": "PowerShell",
                "version": "7.4.99"
            },
            "target": {
                "rid": current_target().0,
                "architecture": current_target().1
            },
            "runtime": {
                "powerShellVersion": "7.4.99",
                "dotnetVersion": "8.0.99",
                "hostfxrVersion": "8.0.99",
                "bindingsAbiVersion": REQUIRED_BINDINGS_ABI_VERSION,
                "requiredBindingsFeatures": REQUIRED_BINDINGS_FEATURES
            },
            "files": files,
            "trust": {
                "allowSymlinks": false
            }
        })
    }

    fn write_manifest(root: &Path, manifest: serde_json::Value) -> (PathBuf, String) {
        let path = root
            .parent()
            .unwrap()
            .join(format!("{}.manifest.json", root.file_name().unwrap().to_string_lossy()));
        let bytes = serde_json::to_vec_pretty(&manifest).unwrap();
        let hash = sha256_bytes(&bytes);
        fs::write(&path, bytes).unwrap();
        (path, hash)
    }

    fn request<'a>(root: &'a Path, manifest: &'a Path, hash: &'a str) -> ValidationRequest<'a> {
        ValidationRequest {
            payload_path: root.to_str().unwrap(),
            manifest_path: manifest.to_str().unwrap(),
            manifest_sha256: hash,
            trust_policy: TrustPolicy::RequireHashPinnedManifest,
        }
    }

    #[test]
    fn rejects_manifest_with_missing_required_file_hashes() {
        let root = fixture_root();
        write_file(&root, "pwsh.dll", b"pwsh");
        let manifest = base_manifest(vec![file_entry(&root, "pwsh.dll")]);
        let (manifest_path, hash) = write_manifest(&root, manifest);

        let result = validate(request(&root, &manifest_path, &hash));
        assert!(matches!(result, Err(ValidationError::ManifestInvalid(message)) if message.contains("required file")));
        cleanup(&root);
    }

    #[test]
    fn rejects_hash_pinned_manifest_with_unhashed_nested_file() {
        let root = fixture_root();
        let files = required_payload_files()
            .iter()
            .map(|path| {
                write_file(&root, path, path.as_bytes());
                file_entry(&root, path)
            })
            .collect();
        write_file(&root, "Modules/Unpinned/Unpinned.dll", b"unhashed");
        let manifest = base_manifest(files);
        let (manifest_path, hash) = write_manifest(&root, manifest);

        let result = validate(request(&root, &manifest_path, &hash));
        assert!(
            matches!(result, Err(ValidationError::ManifestInvalid(message)) if message.contains("Modules/Unpinned/Unpinned.dll"))
        );
        cleanup(&root);
    }

    #[test]
    fn rejects_mismatched_and_modified_payload_file_hashes() {
        let root = fixture_root();
        write_file(&root, "pwsh.dll", b"original");
        let mut mismatched = base_manifest(vec![serde_json::json!({
            "path": "pwsh.dll",
            "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
        })]);
        let (manifest_path, hash) = write_manifest(&root, mismatched.take());
        let result = validate(request(&root, &manifest_path, &hash));
        assert!(matches!(result, Err(ValidationError::HashMismatch(message)) if message.contains("pwsh.dll")));

        let files = required_payload_files()
            .iter()
            .map(|path| {
                write_file(&root, path, path.as_bytes());
                file_entry(&root, path)
            })
            .collect();
        let manifest = base_manifest(files);
        let (manifest_path, hash) = write_manifest(&root, manifest);
        write_file(&root, "pwsh.dll", b"modified after manifest creation");
        let result = validate(request(&root, &manifest_path, &hash));
        assert!(matches!(result, Err(ValidationError::HashMismatch(message)) if message.contains("pwsh.dll")));
        cleanup(&root);
    }

    #[test]
    fn staging_uses_verified_copies_after_source_changes() {
        let root = fixture_root();
        let mut entries = required_payload_files()
            .iter()
            .map(|path| {
                write_file(&root, path, path.as_bytes());
                file_entry(&root, path)
            })
            .collect::<Vec<_>>();
        write_file(&root, "payload.dll", b"before");
        write_file(
            &root,
            "Modules/Example.Module/Example.Module.psd1",
            b"@{\n    ModuleVersion = '1.2.3'\n}\n",
        );
        entries.push(file_entry(&root, "payload.dll"));
        entries.push(file_entry(&root, "Modules/Example.Module/Example.Module.psd1"));
        let manifest: PayloadManifest = serde_json::from_value(base_manifest(entries)).unwrap();
        let canonical_root = fs::canonicalize(&root).unwrap();
        let files = validate_payload_files(&manifest, &canonical_root, false, false).unwrap();
        let module_path = fs::canonicalize(root.join("Modules")).unwrap();
        let module_manifest = fs::canonicalize(root.join("Modules/Example.Module/Example.Module.psd1")).unwrap();
        let mut module_identities = HashMap::new();
        module_identities.insert(
            "example.module".to_owned(),
            ModuleIdentity {
                manifest_path: module_manifest,
            },
        );
        let mut module_paths = HashSet::new();
        module_paths.insert(module_path.clone());
        let mut working_directories = HashSet::new();
        working_directories.insert(canonical_root.clone());
        let staged = stage(ValidatedPayload {
            payload_root: canonical_root.clone(),
            manifest_path: root.join("manifest.json"),
            manifest_sha256: String::new(),
            session_policy: SessionPolicy {
                module_paths,
                working_directories,
                module_identities,
                ..SessionPolicy::default()
            },
            files,
        })
        .unwrap();

        write_file(&root, "payload.dll", b"modified source");
        assert_ne!(staged.payload_root, canonical_root);
        assert_eq!(fs::read(staged.payload_root.join("payload.dll")).unwrap(), b"before");
        assert_eq!(
            fs::read(staged.payload_root.join("Modules/Example.Module/Example.Module.psd1")).unwrap(),
            b"@{\n    ModuleVersion = '1.2.3'\n}\n"
        );
        assert!(staged
            .session_policy
            .staged_module_paths
            .get(&module_path)
            .unwrap()
            .starts_with(&staged.payload_root));
        assert!(staged
            .session_policy
            .staged_working_directories
            .get(&canonical_root)
            .unwrap()
            .starts_with(&staged.payload_root));
        assert!(staged
            .session_policy
            .module_identities
            .get("example.module")
            .unwrap()
            .manifest_path
            .starts_with(&staged.payload_root));
        cleanup(&root);
    }

    #[test]
    fn staging_rejects_source_replaced_after_validation() {
        let root = fixture_root();
        let files = required_payload_files()
            .iter()
            .map(|path| {
                write_file(&root, path, path.as_bytes());
                file_entry(&root, path)
            })
            .collect();
        let manifest: PayloadManifest = serde_json::from_value(base_manifest(files)).unwrap();
        let canonical_root = fs::canonicalize(&root).unwrap();
        let files = validate_payload_files(&manifest, &canonical_root, false, true).unwrap();

        write_file(&root, "pwsh.dll", b"replaced after validation");

        assert!(matches!(
            stage(ValidatedPayload {
                payload_root: canonical_root,
                manifest_path: root.join("manifest.json"),
                manifest_sha256: String::new(),
                session_policy: SessionPolicy::default(),
                files,
            }),
            Err(ValidationError::HashMismatch(message)) if message.contains("staged payload file")
        ));
        cleanup(&root);
    }

    #[test]
    fn rejects_rid_mismatch_before_runtime_initialization() {
        let root = fixture_root();
        let mut manifest = base_manifest(Vec::new());
        let incompatible_rid = match current_target().0 {
            "win-x64" => "linux-x64",
            _ => "win-x64",
        };
        assert_ne!(incompatible_rid, current_target().0);
        manifest["target"]["rid"] = serde_json::Value::String(incompatible_rid.to_owned());
        let (manifest_path, hash) = write_manifest(&root, manifest);

        let result = validate(request(&root, &manifest_path, &hash));
        assert!(matches!(result, Err(ValidationError::Incompatible(message)) if message.contains("incompatible")));
        cleanup(&root);
    }

    #[test]
    fn session_policy_canonicalizes_only_declared_payload_directories() {
        let root = fixture_root();
        fs::create_dir_all(root.join("Modules")).unwrap();
        let module_manifest = "Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1";
        write_file(&root, module_manifest, b"@{\n    ModuleVersion = '1.0.0'\n}\n");
        let mut manifest = base_manifest(vec![file_entry(&root, module_manifest)]);
        manifest["sessionPolicy"] = serde_json::json!({
            "modulePaths": ["Modules"],
            "workingDirectories": ["."],
            "moduleImports": ["Microsoft.PowerShell.Utility"],
            "moduleIdentities": [{
                "name": "Microsoft.PowerShell.Utility",
                "manifestPath": module_manifest,
                "version": "1.0.0",
                "sha256": sha256_file(&root.join(module_manifest)).unwrap()
            }],
            "environmentKeys": ["DPS_FFI_TEST"]
        });
        let manifest: PayloadManifest = serde_json::from_value(manifest).unwrap();
        let canonical_root = fs::canonicalize(&root).unwrap();

        let policy = validate_session_policy(&manifest, &canonical_root).unwrap();
        assert!(policy
            .module_paths
            .contains(&fs::canonicalize(root.join("Modules")).unwrap()));
        assert!(policy.working_directories.contains(&canonical_root));
        assert!(policy.module_imports.contains("microsoft.powershell.utility"));
        assert!(policy.environment_keys.contains("dps_ffi_test"));
        cleanup(&root);
    }

    #[test]
    fn session_policy_requires_hash_pinned_module_identity_and_matching_version() {
        let root = fixture_root();
        let module_manifest = "Modules/Example.Module/Example.Module.psd1";
        write_file(&root, module_manifest, b"@{\n    ModuleVersion = '1.2.3'\n}\n");
        let files = vec![file_entry(&root, module_manifest)];
        let mut manifest = base_manifest(files);
        manifest["sessionPolicy"] = serde_json::json!({
            "modulePaths": ["Modules"],
            "moduleImports": ["Example.Module"]
        });
        let manifest: PayloadManifest = serde_json::from_value(manifest).unwrap();
        let canonical_root = fs::canonicalize(&root).unwrap();
        assert!(matches!(
            validate_session_policy(&manifest, &canonical_root),
            Err(ValidationError::ManifestInvalid(message)) if message.contains("exact module identity")
        ));

        let mut manifest = base_manifest(vec![file_entry(&root, module_manifest)]);
        manifest["sessionPolicy"] = serde_json::json!({
            "modulePaths": ["Modules"],
            "moduleImports": ["Example.Module"],
            "moduleIdentities": [{
                "name": "Example.Module",
                "manifestPath": module_manifest,
                "version": "1.2.4",
                "sha256": sha256_file(&root.join(module_manifest)).unwrap()
            }]
        });
        let manifest: PayloadManifest = serde_json::from_value(manifest).unwrap();
        assert!(matches!(
            validate_session_policy(&manifest, &canonical_root),
            Err(ValidationError::ManifestInvalid(message)) if message.contains("ModuleVersion")
        ));
        cleanup(&root);
    }

    #[test]
    fn session_policy_rejects_path_traversal() {
        let root = fixture_root();
        let mut manifest = base_manifest(Vec::new());
        manifest["sessionPolicy"] = serde_json::json!({
            "modulePaths": ["../outside"]
        });
        let manifest: PayloadManifest = serde_json::from_value(manifest).unwrap();
        let canonical_root = fs::canonicalize(&root).unwrap();

        assert!(matches!(
            validate_session_policy(&manifest, &canonical_root),
            Err(ValidationError::ManifestInvalid(message)) if message.contains("traversal")
        ));
        cleanup(&root);
    }

    #[test]
    fn rejects_file_path_traversal_before_runtime_initialization() {
        let root = fixture_root();
        let manifest = base_manifest(vec![serde_json::json!({
            "path": "../pwsh.dll",
            "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
        })]);
        let (manifest_path, hash) = write_manifest(&root, manifest);

        let result = validate(request(&root, &manifest_path, &hash));
        assert!(matches!(result, Err(ValidationError::ManifestInvalid(message)) if message.contains("traversal")));
        cleanup(&root);
    }

    #[test]
    fn rejects_unpinned_manifest_without_explicit_development_policy() {
        let root = fixture_root();
        let manifest_path = root.join("manifest.json");
        fs::write(&manifest_path, b"{}").unwrap();
        let result = validate(ValidationRequest {
            payload_path: root.to_str().unwrap(),
            manifest_path: manifest_path.to_str().unwrap(),
            manifest_sha256: "",
            trust_policy: TrustPolicy::RequireHashPinnedManifest,
        });
        assert!(matches!(result, Err(ValidationError::Untrusted(message)) if message.contains("SHA-256")));
        cleanup(&root);
    }
}
