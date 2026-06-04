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

Dispatch `.github/workflows/release.yml` from the branch or commit you want to release.

- For an actual publish, set `dry_run` to `false` and provide the matching `tag` value, for example `v0.9.0`.
- For an inspection build, set `dry_run` to `true`. This builds and packs artifacts, uploads the `.nupkg` as a workflow artifact, and skips GitHub release publishing.

You do **not** need to create the tag ahead of time. If the tag does not exist yet, the workflow creates it at the dispatched commit when it creates the GitHub release.

The workflow:

- validates that the tag input matches both crate versions
- builds artifacts from the commit you dispatched the workflow from
- creates the tag at that commit if needed
- uploads archives and `checksums.txt` to the GitHub release
- builds and uploads `Devolutions.MultiPwsh.Cli.<version>.nupkg` to the GitHub release
- uploads install/uninstall bootstrap scripts to the GitHub release so users do not need `raw.githubusercontent.com`

If the release already exists, the workflow uploads the refreshed assets with `--clobber`.

### Dry-run artifact download

When `dry_run` is `true`, download the `cli-nuget` artifact from the workflow run page to inspect `Devolutions.MultiPwsh.Cli.<version>.nupkg`.

## 4. Verify release assets

Confirm the release contains:

- all platform zip archives
- `Devolutions.MultiPwsh.Cli.<version>.nupkg`
- `checksums.txt`
- `install-multi-pwsh.ps1` and `install-multi-pwsh.sh`
- `uninstall-multi-pwsh.ps1` and `uninstall-multi-pwsh.sh`
- generated release notes or any required manual edits

## 5. Package consumption in .NET apps

Reference the package and build your project:

```xml
<ItemGroup>
  <PackageReference Include="Devolutions.MultiPwsh.Cli" Version="0.9.0" />
</ItemGroup>
```

The package contributes `multi-pwsh` payloads under `runtimes/<rid>/native/` and copies them to build/publish output.
