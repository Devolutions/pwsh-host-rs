#!/usr/bin/env bash
set -euo pipefail

repo_owner="Devolutions"
repo_name="multi-pwsh"

version="${1:-latest}"
offline_cache=""
archive_path=""
checksum_path=""

if [[ $# -gt 0 && "${1}" == --* ]]; then
  version="latest"
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version|-v)
      version="$2"
      shift 2
      ;;
    --owner)
      repo_owner="$2"
      shift 2
      ;;
    --repository)
      repo_name="$2"
      shift 2
      ;;
    --offline-cache)
      offline_cache="$2"
      shift 2
      ;;
    --archive-path)
      archive_path="$2"
      shift 2
      ;;
    --checksum-path)
      checksum_path="$2"
      shift 2
      ;;
    *)
      if [[ "${version}" == "latest" ]]; then
        version="$1"
        shift
      else
        echo "Unexpected argument: $1" >&2
        exit 1
      fi
      ;;
  esac
done

install_home="${MULTI_PWSH_HOME:-${HOME}/.pwsh}"
bin_dir="${MULTI_PWSH_BIN_DIR:-${install_home}/bin}"

if [[ "${version}" == "latest" ]]; then
  release_path="latest/download"
  display_version="latest"
else
  if [[ "${version}" != v* ]]; then
    version="v${version}"
  fi
  release_path="download/${version}"
  display_version="${version}"
fi

uname_s="$(uname -s)"
case "${uname_s}" in
  Linux) os="linux" ;;
  Darwin) os="macos" ;;
  *)
    echo "Unsupported OS: ${uname_s}. Supported OS: Linux, macOS." >&2
    exit 1
    ;;
esac

uname_m="$(uname -m)"
case "${uname_m}" in
  x86_64 | amd64) arch="x64" ;;
  aarch64 | arm64) arch="arm64" ;;
  *)
    echo "Unsupported architecture: ${uname_m}. Supported arch: x86_64/amd64, aarch64/arm64." >&2
    exit 1
    ;;
esac

if ! command -v unzip >/dev/null 2>&1; then
  echo "unzip is required but was not found in PATH." >&2
  exit 1
fi

asset="multi-pwsh-${os}-${arch}.zip"
download_url="https://github.com/${repo_owner}/${repo_name}/releases/${release_path}/${asset}"
checksum_url="https://github.com/${repo_owner}/${repo_name}/releases/${release_path}/checksums.txt"

tmp_dir="$(mktemp -d)"
cleanup() {
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

if [[ -z "${archive_path}" ]]; then
  archive_path="${tmp_dir}/${asset}"
fi
checksum_path_provided=1
if [[ -z "${checksum_path}" ]]; then
  checksum_path_provided=0
fi
if [[ -z "${checksum_path}" ]]; then
  checksum_path="${tmp_dir}/checksums.txt"
fi
extract_dir="${tmp_dir}/extract"

if [[ -n "${offline_cache}" ]]; then
  multi_root="${offline_cache}/multi-pwsh"
  if [[ "${version}" == "latest" ]]; then
    version_dir="$(find "${multi_root}" -mindepth 1 -maxdepth 1 -type d -exec test -f "{}/${asset}" \; -print | sort -r | head -n 1)"
    if [[ -z "${version_dir}" ]]; then
      echo "Offline cache does not contain ${asset} under ${multi_root}" >&2
      exit 1
    fi
  else
    version_dir="${multi_root}/${version}"
  fi

  source_archive="${version_dir}/${asset}"
  source_checksum="${version_dir}/checksums.txt"
  if [[ ! -f "${source_archive}" ]]; then
    echo "Offline archive was not found: ${source_archive}" >&2
    exit 1
  fi
  if [[ ! -f "${source_checksum}" ]]; then
    echo "Offline checksum file was not found: ${source_checksum}" >&2
    exit 1
  fi
  echo "Using offline ${asset} from ${source_archive}"
  if [[ "$(cd "$(dirname "${source_archive}")" && pwd -P)/$(basename "${source_archive}")" != "$(cd "$(dirname "${archive_path}")" && pwd -P)/$(basename "${archive_path}")" ]]; then
    cp "${source_archive}" "${archive_path}"
  fi
  if [[ "$(cd "$(dirname "${source_checksum}")" && pwd -P)/$(basename "${source_checksum}")" != "$(cd "$(dirname "${checksum_path}")" && pwd -P)/$(basename "${checksum_path}")" ]]; then
    cp "${source_checksum}" "${checksum_path}"
  fi
elif [[ "${archive_path}" == "${tmp_dir}/"* ]]; then
  if ! command -v curl >/dev/null 2>&1; then
    echo "curl is required but was not found in PATH." >&2
    exit 1
  fi
  echo "Downloading ${asset} (${display_version})..."
  curl -fsSL "${download_url}" -o "${archive_path}"
  curl -fsSL "${checksum_url}" -o "${checksum_path}"
elif [[ "${checksum_path_provided}" -eq 0 ]]; then
  echo "--checksum-path is required when --archive-path is used without --offline-cache" >&2
  exit 1
fi

expected_checksum="$(awk -v asset="${asset}" 'tolower($1) ~ /^[0-9a-f]{64}$/ { name=$2; sub(/^\*/, "", name); if (name == asset) { print tolower($1); exit } }' "${checksum_path}")"
if [[ -z "${expected_checksum}" ]]; then
  echo "Checksum entry for ${asset} was not found in ${checksum_path}" >&2
  exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
  actual_checksum="$(sha256sum "${archive_path}" | awk '{ print tolower($1) }')"
else
  actual_checksum="$(shasum -a 256 "${archive_path}" | awk '{ print tolower($1) }')"
fi

if [[ "${actual_checksum}" != "${expected_checksum}" ]]; then
  echo "Checksum mismatch for ${asset}: expected ${expected_checksum}, got ${actual_checksum}" >&2
  exit 1
fi

mkdir -p "${extract_dir}"
unzip -q "${archive_path}" -d "${extract_dir}"

binary_source="${extract_dir}/multi-pwsh"
if [[ ! -f "${binary_source}" ]]; then
  echo "Archive did not contain expected binary: multi-pwsh" >&2
  exit 1
fi

mkdir -p "${bin_dir}"
if command -v install >/dev/null 2>&1; then
  install -m 0755 "${binary_source}" "${bin_dir}/multi-pwsh"
else
  cp "${binary_source}" "${bin_dir}/multi-pwsh"
  chmod 0755 "${bin_dir}/multi-pwsh"
fi

if [[ ":${PATH}:" != *":${bin_dir}:"* ]]; then
  export PATH="${bin_dir}:${PATH}"
fi

profile_candidates=()
if [[ "${SHELL:-}" == *"zsh"* ]]; then
  profile_candidates+=("${HOME}/.zshrc")
fi
if [[ "${SHELL:-}" == *"bash"* ]]; then
  profile_candidates+=("${HOME}/.bashrc")
fi
profile_candidates+=("${HOME}/.profile")

profile_file=""
for candidate in "${profile_candidates[@]}"; do
  if [[ -f "${candidate}" ]]; then
    profile_file="${candidate}"
    break
  fi
done

if [[ -z "${profile_file}" ]]; then
  profile_file="${profile_candidates[0]}"
fi

escaped_bin_dir="${bin_dir//\\/\\\\}"
escaped_bin_dir="${escaped_bin_dir//\"/\\\"}"
escaped_bin_dir="${escaped_bin_dir//\$/\\$}"
profile_line="export PATH=\"${escaped_bin_dir}:\$PATH\""

touch "${profile_file}"
if grep -Fq "${profile_line}" "${profile_file}"; then
  path_status="PATH already contains ${bin_dir} in ${profile_file}"
else
  {
    echo ""
    echo "# Added by multi-pwsh installer"
    echo "${profile_line}"
  } >>"${profile_file}"
  path_status="Added ${bin_dir} to PATH in ${profile_file}"
fi

echo "Installed multi-pwsh to ${bin_dir}/multi-pwsh"
echo "${path_status}"
echo "Run: multi-pwsh --help"