# Release process

## 1. Prepare the version

Update crate versions and the README tag example:

```powershell
.\scripts\Bump-CrateVersions.ps1 -Version 0.9.0
```

Review the resulting diff in:

- `crates\multi-pwsh\Cargo.toml`
- `crates\pwsh-host\Cargo.toml`
- `Cargo.lock`
- `README.md`

## 2. Run release checks

```powershell
rustup toolchain install stable --profile minimal
rustup default stable
rustup component add rustfmt clippy --toolchain stable
cargo fmt --all --check
cargo clippy --workspace --all-targets
cargo build --all-targets
cargo test --all-targets
dotnet build dotnet\bindings\Devolutions.PowerShell.SDK.Bindings.csproj -p:PwshExePath="pwsh-7.4"
dotnet test dotnet\bindings\Devolutions.PowerShell.SDK.Bindings.csproj --no-build -p:PwshExePath="pwsh-7.4"
```

## 3. Publish the release

Dispatch `.github/workflows/release.yml` from the branch or commit you want to release, with the matching tag value, for example `v0.9.0`.

You do **not** need to create the tag ahead of time. If the tag does not exist yet, the workflow creates it at the dispatched commit when it creates the GitHub release.

The workflow:

- validates that the tag input matches both crate versions
- builds artifacts from the commit you dispatched the workflow from
- creates the tag at that commit if needed
- uploads archives and `checksums.txt` to the GitHub release

If the release already exists, the workflow uploads the refreshed assets with `--clobber`.

## 4. Verify release assets

Confirm the release contains:

- all platform zip archives
- `checksums.txt`
- generated release notes or any required manual edits
