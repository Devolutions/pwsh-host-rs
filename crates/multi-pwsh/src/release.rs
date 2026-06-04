use std::fs;
use std::path::{Path, PathBuf};

use semver::Version;
use serde::{Deserialize, Serialize};
use ureq::Agent;

use crate::error::{MultiPwshError, Result};
use crate::platform::{HostArch, HostOs};
use crate::versions::{is_current_lts_version, MajorMinor, VersionSelector};

const CHECKSUM_ASSET_NAME: &str = "hashes.sha256";
pub const POWERSHELL_MANIFEST_RELATIVE_PATH: &str = "manifests/powershell-releases.json";

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AssetSource {
    Url(String),
    File(PathBuf),
}

#[derive(Clone, Debug)]
pub struct ResolvedRelease {
    pub version: Version,
    pub asset_name: String,
    pub asset_source: AssetSource,
    pub checksum_asset_name: Option<String>,
    pub checksum_asset_source: Option<AssetSource>,
}

impl ResolvedRelease {
    pub fn version_line(&self) -> MajorMinor {
        MajorMinor::from_version(&self.version)
    }
}

#[derive(Clone)]
pub struct ReleaseClient {
    http: Agent,
    authorization_header: Option<String>,
}

impl ReleaseClient {
    pub fn new(github_token: Option<String>) -> Result<Self> {
        let authorization_header = github_token
            .filter(|token| !token.trim().is_empty())
            .map(|token| format!("Bearer {}", token));

        let http = ureq::AgentBuilder::new().build();

        Ok(ReleaseClient {
            http,
            authorization_header,
        })
    }

    pub fn http_client(&self) -> &Agent {
        &self.http
    }

    pub fn resolve_selector(
        &self,
        selector: VersionSelector,
        os: HostOs,
        arch: HostArch,
        include_prerelease: bool,
    ) -> Result<ResolvedRelease> {
        let releases = parse_github_releases(self.fetch_releases()?);
        resolve_selector_from_parsed(releases, selector, os, arch, include_prerelease)
    }

    pub fn resolve_all_in_line(
        &self,
        line: MajorMinor,
        os: HostOs,
        arch: HostArch,
        include_prerelease: bool,
    ) -> Result<Vec<ResolvedRelease>> {
        let releases = parse_github_releases(self.fetch_releases()?);
        resolve_all_in_line_from_parsed(releases, line, os, arch, include_prerelease)
    }

    pub fn list_available_versions(&self, include_prerelease: bool) -> Result<Vec<Version>> {
        let releases = parse_github_releases(self.fetch_releases()?);
        Ok(list_available_versions_from_parsed(releases, include_prerelease))
    }

    fn fetch_releases(&self) -> Result<Vec<GithubRelease>> {
        let mut all_releases = Vec::new();

        for page in 1..=10 {
            let url = format!(
                "https://api.github.com/repos/PowerShell/PowerShell/releases?per_page=100&page={}",
                page
            );

            let mut request = self
                .http
                .get(&url)
                .set("Accept", "application/vnd.github.v3+json")
                .set("User-Agent", "multi-pwsh");

            if let Some(value) = self.authorization_header.as_deref() {
                request = request.set("Authorization", value);
            }

            let response = request.call()?;
            let body = response.into_string()?;
            let mut page_releases: Vec<GithubRelease> = serde_json::from_str(&body)?;

            if page_releases.is_empty() {
                break;
            }

            let is_last_page = page_releases.len() < 100;
            all_releases.append(&mut page_releases);

            if is_last_page {
                break;
            }
        }
        Ok(all_releases)
    }
}

#[derive(Clone)]
pub struct OfflineReleaseClient {
    releases: Vec<ParsedRelease>,
}

impl OfflineReleaseClient {
    pub fn new(root: impl AsRef<Path>) -> Result<Self> {
        let root = root.as_ref();
        let manifest_path = powershell_manifest_path(root);
        let bundle_root = offline_bundle_root(root, &manifest_path);
        let manifest = load_powershell_manifest(root)?;
        let releases = manifest
            .releases
            .into_iter()
            .map(|release| ParsedRelease::from_offline_release(release, &bundle_root))
            .collect::<Result<Vec<_>>>()?;

        Ok(Self { releases })
    }

    pub fn resolve_selector(
        &self,
        selector: VersionSelector,
        os: HostOs,
        arch: HostArch,
        include_prerelease: bool,
    ) -> Result<ResolvedRelease> {
        resolve_selector_from_parsed(self.releases.clone(), selector, os, arch, include_prerelease)
    }

    pub fn resolve_all_in_line(
        &self,
        line: MajorMinor,
        os: HostOs,
        arch: HostArch,
        include_prerelease: bool,
    ) -> Result<Vec<ResolvedRelease>> {
        resolve_all_in_line_from_parsed(self.releases.clone(), line, os, arch, include_prerelease)
    }

    pub fn list_available_versions(&self, include_prerelease: bool) -> Vec<Version> {
        list_available_versions_from_parsed(self.releases.clone(), include_prerelease)
    }
}

fn parse_github_releases(releases: Vec<GithubRelease>) -> Vec<ParsedRelease> {
    releases
        .into_iter()
        .filter_map(ParsedRelease::from_github_release)
        .collect()
}

fn sorted_candidates(releases: Vec<ParsedRelease>, predicate: impl Fn(&ParsedRelease) -> bool) -> Vec<ParsedRelease> {
    let mut candidates: Vec<ParsedRelease> = releases.into_iter().filter(|parsed| predicate(parsed)).collect();

    candidates.sort_by(|a, b| b.version.cmp(&a.version));
    candidates
}

fn resolve_selector_from_parsed(
    releases: Vec<ParsedRelease>,
    selector: VersionSelector,
    os: HostOs,
    arch: HostArch,
    include_prerelease: bool,
) -> Result<ResolvedRelease> {
    match selector {
        VersionSelector::Stable => {
            let candidates = sorted_candidates(releases, |parsed| !parsed.prerelease && parsed.version.pre.is_empty());
            resolve_first_candidate_asset(candidates, os, arch, "no stable release found")
        }
        VersionSelector::Preview => {
            let candidates = sorted_candidates(releases, |parsed| parsed.prerelease && !parsed.version.pre.is_empty());
            resolve_first_candidate_asset(candidates, os, arch, "no preview release found")
        }
        VersionSelector::Lts => {
            let candidates = sorted_candidates(releases, |parsed| {
                !parsed.prerelease && parsed.version.pre.is_empty() && is_current_lts_version(&parsed.version)
            });
            resolve_first_candidate_asset(candidates, os, arch, "no current LTS release found")
        }
        VersionSelector::Major(major) => {
            let mut candidates: Vec<_> = releases
                .into_iter()
                .filter(|release| include_prerelease || !release.prerelease)
                .filter(|release| release.version.major == major)
                .collect();
            candidates.sort_by(|a, b| b.version.cmp(&a.version));
            let release = candidates
                .into_iter()
                .next()
                .ok_or_else(|| MultiPwshError::ReleaseNotFound(format!("no release found for major {}", major)))?;
            resolve_release_asset(release, os, arch)
        }
        VersionSelector::Exact(version) => {
            let release = releases
                .into_iter()
                .find(|release| release.version == version)
                .ok_or_else(|| MultiPwshError::ReleaseNotFound(format!("version {}", version)))?;
            resolve_release_asset(release, os, arch)
        }
        VersionSelector::MajorMinor(line) | VersionSelector::MajorMinorWildcard(line) => {
            let mut candidates: Vec<_> = releases
                .into_iter()
                .filter(|release| include_prerelease || !release.prerelease)
                .filter(|release| release.version.major == line.major && release.version.minor == line.minor)
                .collect();
            candidates.sort_by(|a, b| b.version.cmp(&a.version));
            let release = candidates
                .into_iter()
                .next()
                .ok_or_else(|| MultiPwshError::ReleaseNotFound(format!("no release found for line {}", line)))?;
            resolve_release_asset(release, os, arch)
        }
    }
}

fn resolve_all_in_line_from_parsed(
    releases: Vec<ParsedRelease>,
    line: MajorMinor,
    os: HostOs,
    arch: HostArch,
    include_prerelease: bool,
) -> Result<Vec<ResolvedRelease>> {
    let mut candidates: Vec<_> = releases
        .into_iter()
        .filter(|release| include_prerelease || !release.prerelease)
        .filter(|release| release.version.major == line.major && release.version.minor == line.minor)
        .collect();
    candidates.sort_by(|a, b| b.version.cmp(&a.version));

    let mut resolved = Vec::new();
    for candidate in candidates {
        if let Ok(release) = resolve_release_asset(candidate, os, arch) {
            resolved.push(release);
        }
    }

    if resolved.is_empty() {
        return Err(MultiPwshError::ReleaseNotFound(format!(
            "no release found for line {}",
            line
        )));
    }

    Ok(resolved)
}

fn list_available_versions_from_parsed(releases: Vec<ParsedRelease>, include_prerelease: bool) -> Vec<Version> {
    let mut versions: Vec<_> = releases
        .into_iter()
        .filter(|release| include_prerelease || !release.prerelease)
        .map(|release| release.version)
        .collect();
    versions.sort_by(|a, b| b.cmp(a));
    versions.dedup();
    versions
}

fn resolve_first_candidate_asset(
    candidates: Vec<ParsedRelease>,
    os: HostOs,
    arch: HostArch,
    not_found_message: &str,
) -> Result<ResolvedRelease> {
    let mut last_asset_error = None;

    for candidate in candidates {
        match resolve_release_asset(candidate, os, arch) {
            Ok(release) => return Ok(release),
            Err(error) => last_asset_error = Some(error),
        }
    }

    Err(last_asset_error.unwrap_or_else(|| MultiPwshError::ReleaseNotFound(not_found_message.to_string())))
}

fn resolve_release_asset(release: ParsedRelease, os: HostOs, arch: HostArch) -> Result<ResolvedRelease> {
    let pattern = asset_pattern(os, arch)?;
    let tag_name = release.tag_name.clone();
    let checksum_asset = release
        .assets
        .iter()
        .find(|asset| asset.name == CHECKSUM_ASSET_NAME)
        .cloned();
    let asset = release
        .assets
        .into_iter()
        .find(|asset| wildcard_match(pattern, &asset.name))
        .ok_or_else(|| {
            MultiPwshError::AssetNotFound(format!("no asset found for pattern '{}' in {}", pattern, tag_name))
        })?;

    Ok(ResolvedRelease {
        version: release.version,
        asset_name: asset.name,
        asset_source: asset.source,
        checksum_asset_name: checksum_asset.as_ref().map(|asset| asset.name.clone()),
        checksum_asset_source: checksum_asset.map(|asset| asset.source),
    })
}

fn asset_pattern(os: HostOs, arch: HostArch) -> Result<&'static str> {
    match os {
        HostOs::Windows => match arch {
            HostArch::X64 => Ok("PowerShell-*-win-x64.zip"),
            HostArch::X86 => Ok("PowerShell-*-win-x86.zip"),
            HostArch::Arm64 => Ok("PowerShell-*-win-arm64.zip"),
            HostArch::Arm32 => Err(MultiPwshError::UnsupportedPlatform(
                "arm32 is not supported on windows".to_string(),
            )),
        },
        HostOs::Macos => match arch {
            HostArch::X64 => Ok("powershell-*-osx-x64.tar.gz"),
            HostArch::Arm64 => Ok("powershell-*-osx-arm64.tar.gz"),
            HostArch::X86 | HostArch::Arm32 => Err(MultiPwshError::UnsupportedPlatform(
                "architecture is not supported on macos".to_string(),
            )),
        },
        HostOs::Linux => match arch {
            HostArch::X64 => Ok("powershell-*-linux-x64.tar.gz"),
            HostArch::Arm64 => Ok("powershell-*-linux-arm64.tar.gz"),
            HostArch::Arm32 => Ok("powershell-*-linux-arm32.tar.gz"),
            HostArch::X86 => Err(MultiPwshError::UnsupportedPlatform(
                "x86 is not supported on linux".to_string(),
            )),
        },
    }
}

fn wildcard_match(pattern: &str, text: &str) -> bool {
    if pattern == "*" {
        return true;
    }

    let starts_with_wildcard = pattern.starts_with('*');
    let ends_with_wildcard = pattern.ends_with('*');
    let parts: Vec<&str> = pattern.split('*').filter(|part| !part.is_empty()).collect();

    if parts.is_empty() {
        return true;
    }

    let mut cursor = 0usize;
    for (index, part) in parts.iter().enumerate() {
        if index == 0 && !starts_with_wildcard {
            if !text[cursor..].starts_with(part) {
                return false;
            }
            cursor += part.len();
            continue;
        }

        if index == parts.len() - 1 && !ends_with_wildcard {
            if let Some(found) = text[cursor..].rfind(part) {
                let absolute = cursor + found;
                if absolute + part.len() != text.len() {
                    return false;
                }
                cursor = absolute + part.len();
            } else {
                return false;
            }
            continue;
        }

        if let Some(found) = text[cursor..].find(part) {
            cursor += found + part.len();
        } else {
            return false;
        }
    }

    true
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct GithubRelease {
    pub tag_name: String,
    pub prerelease: bool,
    pub assets: Vec<GithubAsset>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct GithubAsset {
    pub name: String,
    pub browser_download_url: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct OfflinePowerShellManifest {
    pub schema_version: u32,
    pub releases: Vec<OfflinePowerShellRelease>,
}

impl Default for OfflinePowerShellManifest {
    fn default() -> Self {
        Self {
            schema_version: 1,
            releases: Vec::new(),
        }
    }
}

impl OfflinePowerShellManifest {
    pub fn upsert_asset(&mut self, version: &Version, prerelease: bool, new_asset: OfflinePowerShellAsset) {
        let tag_name = format!("v{}", version);
        let release = match self
            .releases
            .iter_mut()
            .find(|release| release.version == version.to_string())
        {
            Some(release) => release,
            None => {
                self.releases.push(OfflinePowerShellRelease {
                    tag_name,
                    version: version.to_string(),
                    prerelease,
                    assets: Vec::new(),
                });
                self.releases.last_mut().expect("release was just inserted")
            }
        };

        release.tag_name = format!("v{}", version);
        release.prerelease = prerelease;

        if let Some(existing) = release
            .assets
            .iter_mut()
            .find(|asset| asset.name == new_asset.name && asset.os == new_asset.os && asset.arch == new_asset.arch)
        {
            *existing = new_asset;
            return;
        }

        release.assets.push(new_asset);
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct OfflinePowerShellRelease {
    pub tag_name: String,
    pub version: String,
    pub prerelease: bool,
    pub assets: Vec<OfflinePowerShellAsset>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct OfflinePowerShellAsset {
    pub name: String,
    pub path: String,
    pub os: String,
    pub arch: String,
    pub checksum_name: Option<String>,
    pub checksum_path: Option<String>,
}

#[derive(Clone, Debug)]
struct ReleaseAsset {
    name: String,
    source: AssetSource,
}

#[derive(Clone, Debug)]
struct ParsedRelease {
    tag_name: String,
    prerelease: bool,
    version: Version,
    assets: Vec<ReleaseAsset>,
}

impl ParsedRelease {
    fn from_github_release(release: GithubRelease) -> Option<Self> {
        let version_text = release.tag_name.trim_start_matches('v');
        let version = Version::parse(version_text).ok()?;

        Some(ParsedRelease {
            tag_name: release.tag_name.clone(),
            prerelease: release.prerelease,
            version,
            assets: release
                .assets
                .into_iter()
                .map(|asset| ReleaseAsset {
                    name: asset.name,
                    source: AssetSource::Url(asset.browser_download_url),
                })
                .collect(),
        })
    }

    fn from_offline_release(release: OfflinePowerShellRelease, bundle_root: &Path) -> Result<Self> {
        let version = Version::parse(release.version.trim_start_matches('v'))?;
        let mut assets = Vec::new();

        for asset in release.assets {
            assets.push(ReleaseAsset {
                name: asset.name,
                source: AssetSource::File(bundle_root.join(asset.path)),
            });

            if let (Some(checksum_name), Some(checksum_path)) = (asset.checksum_name, asset.checksum_path) {
                if !assets.iter().any(|existing| existing.name == checksum_name) {
                    assets.push(ReleaseAsset {
                        name: checksum_name,
                        source: AssetSource::File(bundle_root.join(checksum_path)),
                    });
                }
            }
        }

        Ok(ParsedRelease {
            tag_name: release.tag_name,
            prerelease: release.prerelease,
            version,
            assets,
        })
    }
}

pub fn powershell_manifest_path(root: &Path) -> PathBuf {
    if root.is_file() {
        return root.to_path_buf();
    }

    root.join(POWERSHELL_MANIFEST_RELATIVE_PATH)
}

fn offline_bundle_root(root: &Path, manifest_path: &Path) -> PathBuf {
    if root.is_dir() {
        return root.to_path_buf();
    }

    let Some(parent) = manifest_path.parent() else {
        return PathBuf::from(".");
    };

    if parent
        .file_name()
        .and_then(|name| name.to_str())
        .map(|name| name.eq_ignore_ascii_case("manifests"))
        .unwrap_or(false)
    {
        parent.parent().unwrap_or(parent).to_path_buf()
    } else {
        parent.to_path_buf()
    }
}

pub fn load_powershell_manifest(root: &Path) -> Result<OfflinePowerShellManifest> {
    let manifest_path = powershell_manifest_path(root);
    let bytes = fs::read(&manifest_path).map_err(|error| {
        MultiPwshError::Io(std::io::Error::new(
            error.kind(),
            format!(
                "failed to read offline PowerShell manifest '{}': {}",
                manifest_path.display(),
                error
            ),
        ))
    })?;
    let manifest = serde_json::from_slice(&bytes)?;
    Ok(manifest)
}

pub fn save_powershell_manifest(root: &Path, manifest: &OfflinePowerShellManifest) -> Result<()> {
    let manifest_path = powershell_manifest_path(root);
    if let Some(parent) = manifest_path.parent() {
        fs::create_dir_all(parent)?;
    }

    let temp_path = manifest_path.with_extension("json.tmp");
    let json = serde_json::to_vec_pretty(manifest)?;
    fs::write(&temp_path, json)?;
    fs::rename(temp_path, manifest_path)?;
    Ok(())
}

pub fn load_or_default_powershell_manifest(root: &Path) -> Result<OfflinePowerShellManifest> {
    let manifest_path = powershell_manifest_path(root);
    if !manifest_path.exists() {
        return Ok(OfflinePowerShellManifest::default());
    }

    load_powershell_manifest(root)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wildcard_match_supports_single_star_segments() {
        assert!(wildcard_match(
            "PowerShell-*-win-x64.zip",
            "PowerShell-7.4.13-win-x64.zip"
        ));
        assert!(wildcard_match(
            "powershell-*-linux-arm64.tar.gz",
            "powershell-7.5.1-linux-arm64.tar.gz"
        ));
        assert!(!wildcard_match(
            "powershell-*-linux-arm64.tar.gz",
            "powershell-7.5.1-linux-x64.tar.gz"
        ));
    }

    #[test]
    fn resolve_release_asset_includes_checksum_asset() {
        let release = ParsedRelease {
            tag_name: "v7.4.13".to_string(),
            prerelease: false,
            version: Version::parse("7.4.13").unwrap(),
            assets: vec![
                ReleaseAsset {
                    name: CHECKSUM_ASSET_NAME.to_string(),
                    source: AssetSource::Url("https://example.invalid/hashes.sha256".to_string()),
                },
                ReleaseAsset {
                    name: "PowerShell-7.4.13-win-x64.zip".to_string(),
                    source: AssetSource::Url("https://example.invalid/PowerShell-7.4.13-win-x64.zip".to_string()),
                },
            ],
        };

        let resolved = resolve_release_asset(release, HostOs::Windows, HostArch::X64).unwrap();

        assert_eq!(resolved.asset_name, "PowerShell-7.4.13-win-x64.zip");
        assert_eq!(resolved.checksum_asset_name.as_deref(), Some(CHECKSUM_ASSET_NAME));
        assert_eq!(
            resolved.checksum_asset_source,
            Some(AssetSource::Url("https://example.invalid/hashes.sha256".to_string()))
        );
    }

    #[test]
    fn resolve_release_asset_allows_missing_checksum_asset() {
        let release = ParsedRelease {
            tag_name: "v7.4.13".to_string(),
            prerelease: false,
            version: Version::parse("7.4.13").unwrap(),
            assets: vec![ReleaseAsset {
                name: "PowerShell-7.4.13-win-x64.zip".to_string(),
                source: AssetSource::Url("https://example.invalid/PowerShell-7.4.13-win-x64.zip".to_string()),
            }],
        };

        let resolved = resolve_release_asset(release, HostOs::Windows, HostArch::X64).unwrap();

        assert!(resolved.checksum_asset_name.is_none());
        assert!(resolved.checksum_asset_source.is_none());
    }

    fn github_release(tag_name: &str, prerelease: bool) -> GithubRelease {
        GithubRelease {
            tag_name: tag_name.to_string(),
            prerelease,
            assets: vec![GithubAsset {
                name: format!("PowerShell-{}-win-x64.zip", tag_name.trim_start_matches('v')),
                browser_download_url: format!("https://example.invalid/{}.zip", tag_name),
            }],
        }
    }

    #[test]
    fn sorted_candidates_can_filter_stable_releases() {
        let candidates = sorted_candidates(
            parse_github_releases(vec![
                github_release("v7.7.0-preview.1", true),
                github_release("v7.6.2", false),
                github_release("v7.5.7", false),
            ]),
            |parsed| !parsed.prerelease && parsed.version.pre.is_empty(),
        );

        assert_eq!(candidates[0].version, Version::parse("7.6.2").unwrap());
        assert_eq!(candidates[1].version, Version::parse("7.5.7").unwrap());
    }

    #[test]
    fn sorted_candidates_can_filter_current_lts_releases() {
        let candidates = sorted_candidates(
            parse_github_releases(vec![
                github_release("v7.7.0-preview.1", true),
                github_release("v7.6.2", false),
                github_release("v7.4.16", false),
            ]),
            |parsed| !parsed.prerelease && parsed.version.pre.is_empty() && is_current_lts_version(&parsed.version),
        );

        assert_eq!(candidates.len(), 1);
        assert_eq!(candidates[0].version, Version::parse("7.6.2").unwrap());
    }

    #[test]
    fn offline_release_client_resolves_relative_artifact_paths() {
        let temp_dir = tempfile::tempdir().unwrap();
        let root = temp_dir.path();
        let mut manifest = OfflinePowerShellManifest::default();
        manifest.upsert_asset(
            &Version::parse("7.6.2").unwrap(),
            false,
            OfflinePowerShellAsset {
                name: "PowerShell-7.6.2-win-x64.zip".to_string(),
                path: "PowerShell/v7.6.2/PowerShell-7.6.2-win-x64.zip".to_string(),
                os: HostOs::Windows.as_manifest_value().to_string(),
                arch: HostArch::X64.as_manifest_value().to_string(),
                checksum_name: Some(CHECKSUM_ASSET_NAME.to_string()),
                checksum_path: Some("PowerShell/v7.6.2/hashes.sha256".to_string()),
            },
        );
        save_powershell_manifest(root, &manifest).unwrap();

        let client = OfflineReleaseClient::new(root).unwrap();
        let release = client
            .resolve_selector(VersionSelector::Stable, HostOs::Windows, HostArch::X64, false)
            .unwrap();

        assert_eq!(release.version, Version::parse("7.6.2").unwrap());
        assert_eq!(
            release.asset_source,
            AssetSource::File(root.join("PowerShell/v7.6.2/PowerShell-7.6.2-win-x64.zip"))
        );
        assert_eq!(
            release.checksum_asset_source,
            Some(AssetSource::File(root.join("PowerShell/v7.6.2/hashes.sha256")))
        );
    }
}
