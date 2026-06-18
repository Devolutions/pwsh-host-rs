# multi-pwsh

Install and manage side-by-side PowerShell versions with aliases and native hosting.

![multi-pwsh](docs/images/multi-pwsh.png)

## Bootstrap

Latest release bootstrap scripts:

```bash
curl -fsSL https://github.com/Devolutions/multi-pwsh/releases/latest/download/install-multi-pwsh.sh | bash
```

```powershell
irm https://github.com/Devolutions/multi-pwsh/releases/latest/download/install-multi-pwsh.ps1 | iex
```

Install a specific tag (example `v0.11.0`):

```bash
curl -fsSL https://github.com/Devolutions/multi-pwsh/releases/download/v0.11.0/install-multi-pwsh.sh | bash -s -- v0.11.0
```

```powershell
& ([scriptblock]::Create((irm https://github.com/Devolutions/multi-pwsh/releases/download/v0.11.0/install-multi-pwsh.ps1))) -Version v0.11.0
```

Uninstall bootstrap scripts:

```bash
curl -fsSL https://github.com/Devolutions/multi-pwsh/releases/latest/download/uninstall-multi-pwsh.sh | bash
```

```powershell
irm https://github.com/Devolutions/multi-pwsh/releases/latest/download/uninstall-multi-pwsh.ps1 | iex
```

## Install and verify aliases

```powershell
multi-pwsh install stable
multi-pwsh install preview
multi-pwsh install lts
multi-pwsh install 7.4
```

Verify aliases:

```powershell
pwsh --version
pwsh-preview --version
pwsh-lts --version
pwsh-7.4 --version
```

## Offline and network-share installs

GitHub remains the default source, but you can prefetch release artifacts on a connected machine and use them from a disconnected machine.

On a connected machine, warm an offline bundle into the same directory shape used by the download cache:

```powershell
multi-pwsh cache warm stable --os windows --arch x64 --output \\fileserver\multi-pwsh-cache
multi-pwsh cache warm 7.4.x --os all --arch all --output \\fileserver\multi-pwsh-cache
```

The bundle includes PowerShell archives, PowerShell `hashes.sha256`, a relocatable manifest, and the current `multi-pwsh` release archive/checksums for the requested platform targets. Copy that directory locally or expose it as a read-only network share, then use it offline:

```powershell
$env:MULTI_PWSH_OFFLINE_CACHE = '\\fileserver\multi-pwsh-cache'
multi-pwsh list --available
multi-pwsh install stable
multi-pwsh update 7.4
```

You can also pass `--offline-cache <path>` to `install`, `update`, `package install`, and `list --available`. Offline mode uses only the local manifest/artifacts and fails instead of falling back to GitHub when something is missing.

Disconnected bootstrap uses the same bundle:

```powershell
.\install-multi-pwsh.ps1 -OfflineCache \\fileserver\multi-pwsh-cache -Version latest
```

```bash
./install-multi-pwsh.sh --offline-cache /mnt/share/multi-pwsh-cache --version latest
```

Rerun the bootstrap script against a newer warmed bundle to update `multi-pwsh` itself. Archives are verified against the mirrored checksum files before installation.

## More docs

- [Native host mode and virtual environments](docs/host-and-venv.md)
- [Feature matrix and roadmap](docs/feature-matrix.md)
- [Testing guide](docs/testing.md)
- [Release process](RELEASE.md)

## Scoped installs

`multi-pwsh install`, `update`, `uninstall`, and `list` now support `--scope <user|machine>` across Windows, macOS, and Linux.

That means:

- extracted versions stay side-by-side under the selected install root
- aliases continue to live in one stable bin directory
- PATH only needs one entry per scope
- `user` is the default scope when `--scope` is omitted

Platform behavior:

- Windows uses the GitHub ZIP archives with MSI-like install roots and selected installer-style integrations that still make sense for archive installs.
- `user` installs default to `~/.pwsh` on every platform, with aliases in `~/.pwsh/bin`.
- Windows `machine` installs default to `%ProgramFiles%\PowerShell` with aliases in `%ProgramFiles%\PowerShell\bin`.
- macOS `machine` installs use the official `.tar.gz` archives under `/usr/local/microsoft/powershell` with aliases published to `/usr/local/bin`.
- Linux `machine` installs use the official `.tar.gz` archives under `/opt/microsoft/powershell` with aliases published to `/usr/local/bin`.
- Unix `machine` installs expect you to provide elevation yourself; `multi-pwsh` does not invoke `sudo`.

Examples:

```powershell
multi-pwsh install stable
multi-pwsh install preview
multi-pwsh install lts
multi-pwsh install 7.4
multi-pwsh install 7.5 --scope machine --enable-psremoting --add-explorer-context-menu
multi-pwsh install 7.5 --scope machine
multi-pwsh list --scope all
multi-pwsh uninstall 7.4.13 --scope machine
```

Windows scoped-install flags mirror the most useful MSI-style options:

- `--add-path` / `--no-add-path`
- `--register-manifest` / `--no-register-manifest`
- `--enable-psremoting`
- `--disable-telemetry`
- `--add-explorer-context-menu`
- `--add-file-context-menu`
- `--scope <user|machine>`
- `--root <path>`

Microsoft Update registration is intentionally out of scope for archive installs at the moment, even on Windows.

On macOS and Linux, scoped installs support:

- `--scope <user|machine>`
- `--root <path>`
- `--arch <auto|x64|x86|arm64|arm32>`
- `--include-prerelease`
- `--add-path` / `--no-add-path`

The Windows-only integration flags above currently return an error on macOS/Linux.

## Manage installed lines

### Channel installs and updates

```powershell
multi-pwsh install stable
multi-pwsh install preview
multi-pwsh install lts
multi-pwsh update stable
multi-pwsh update preview
multi-pwsh update lts
multi-pwsh list
multi-pwsh list --available
multi-pwsh list --available --include-prerelease
```

### Exact versions and lines

```powershell
multi-pwsh install 7.4.x
multi-pwsh install 7.6 --include-prerelease
multi-pwsh install 7.6-preview6
multi-pwsh install 7.6-rc1
multi-pwsh install 7.6.0-rc.1
multi-pwsh update 7.4
multi-pwsh update 7.5
multi-pwsh update 7.6 --include-prerelease
multi-pwsh uninstall 7.4.13
```

### Alias policy and host mode

```powershell
multi-pwsh alias set pwsh stable
multi-pwsh alias set pwsh lts
multi-pwsh alias set pwsh-preview preview
multi-pwsh alias set pwsh-lts lts
multi-pwsh alias set 7.4 7.4.11
multi-pwsh alias unset 7.4
multi-pwsh host 7.4 -venv msgraph -NoLogo -NoProfile -Command "$env:PSModulePath"
multi-pwsh doctor --repair-aliases
```

### Venv management

```powershell
multi-pwsh venv create msgraph
multi-pwsh venv export msgraph msgraph.zip
multi-pwsh venv import msgraph-copy msgraph.zip
multi-pwsh venv delete msgraph
multi-pwsh venv list
```

`multi-pwsh` usage reference:

```text
multi-pwsh --version
multi-pwsh --help
multi-pwsh help [command]
multi-pwsh install <stable|preview|lts|version|major|major.minor|major.minor.x> [--scope <user|machine>] [--root <path>] [--arch <auto|x64|x86|arm64|arm32>] [--include-prerelease] [--offline-cache <path>] [--add-path|--no-add-path] [--register-manifest|--no-register-manifest] [--enable-psremoting] [--disable-telemetry] [--add-explorer-context-menu] [--add-file-context-menu]
multi-pwsh update <stable|preview|lts|major.minor> [--scope <user|machine>] [--root <path>] [--arch <auto|x64|x86|arm64|arm32>] [--include-prerelease] [--offline-cache <path>] [--add-path|--no-add-path] [--register-manifest|--no-register-manifest] [--enable-psremoting] [--disable-telemetry] [--add-explorer-context-menu] [--add-file-context-menu]
multi-pwsh cache warm <selector> [--os <windows|linux|macos|all>] [--arch <x64|x86|arm64|arm32|all>] [--include-prerelease] [--output <path>] [--product <powershell|multi-pwsh|all>]
multi-pwsh uninstall <version> [--scope <user|machine>] [--root <path>] [--force]
multi-pwsh list [--scope <user|machine|all>] [--root <path>] [--available] [--include-prerelease] [--offline-cache <path>]
multi-pwsh venv create <name>
multi-pwsh venv delete <name>
multi-pwsh venv export <name> <archive.zip>
multi-pwsh venv import <name> <archive.zip>
multi-pwsh venv list
multi-pwsh alias set <major.minor> <version|latest>
multi-pwsh alias set <pwsh|pwsh-preview|pwsh-lts> <stable|preview|lts|version>
multi-pwsh alias unset <major.minor|pwsh|pwsh-preview|pwsh-lts>
multi-pwsh host <version|major|major.minor|pwsh-alias> [-VirtualEnvironment <name>|-venv <name>] [pwsh arguments...]
multi-pwsh doctor --repair-aliases
```

Use `multi-pwsh <command> --help` or `multi-pwsh help <command>` for focused command usage, for example `multi-pwsh install --help`.

The Windows integration flags in the `install` and `update` forms are limited to archive-friendly behaviors; on macOS/Linux, use `--scope`, `--root`, `--arch`, `--include-prerelease`, and `--add-path` controls. Legacy scope aliases such as `current-user` and `all-users` are still accepted for compatibility.

## Selector behavior

- `stable` installs the latest GA/non-preview release for your platform and configures `pwsh` to follow the latest installed stable release.
- `preview` installs the latest prerelease for your platform and configures `pwsh-preview` to follow the latest installed preview release.
- `lts` installs the latest patch from the current LTS line for your platform and configures `pwsh-lts` to follow the latest installed LTS release.
- `7` installs the latest available 7.x release for your platform.
- `7.4` installs the latest available 7.4.x release for your platform.
- `7.4.x` installs all available releases in that line for your platform.
- `7.4.11` installs that exact version.

`multi-pwsh install 7.4.x` installs every available patch release in that line for your current platform and creates per-version aliases such as `pwsh-7.4.11`.

The `pwsh-7.4` alias tracks latest by default; pin it with `multi-pwsh alias set 7.4 7.4.11` and unpin with `multi-pwsh alias unset 7.4`.

If a pinned target version is not installed, the pin remains in metadata and the alias stays unresolved until you install that version or unpin.

The bare `pwsh` alias is a managed policy alias. Configure it with `multi-pwsh alias set pwsh stable`, `multi-pwsh alias set pwsh lts`, `multi-pwsh alias set pwsh preview`, or an exact version. `pwsh-preview` tracks preview by default, and `pwsh-lts` tracks LTS by default. Policy aliases resolve only to installed versions; install or update the desired channel before pointing an alias at it.

Use `multi-pwsh list` to inspect both resolved aliases and managed named alias policies. The policy section shows whether aliases such as `pwsh`, `pwsh-preview`, and `pwsh-lts` currently resolve to an installed version or remain configured but unresolved.

The current LTS line is encoded in the tool; at the moment that is `7.6`.

## Native host mode and virtual environments

- `multi-pwsh host <selector> ...` runs PowerShell through native hosting instead of launching a `pwsh` subprocess.
- Use `-venv <name>` or `-VirtualEnvironment <name>` to select a managed module root for hosted launches.
- Use `multi-pwsh doctor --repair-aliases` to repair host shims and named aliases.

See [docs/host-and-venv.md](docs/host-and-venv.md) for host shims, venv layout, import/export, managed paths, and current limitations.

## Testing

- Scoped install smoke tests: `pwsh -NoLogo -NoProfile -NonInteractive -File .\tests\Invoke-ScopedInstallSmokeTest.ps1`
- Venv matrix tests: `pwsh -NoLogo -NoProfile -NonInteractive -File .\tests\Invoke-VenvTestMatrix.ps1`

See [docs/testing.md](docs/testing.md) for online test mode, alias-targeted runs, and troubleshooting flags.
