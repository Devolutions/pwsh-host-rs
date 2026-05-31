# Feature matrix and roadmap

This matrix reflects the current command surface and known gaps for `multi-pwsh`.

## CLI surface

| Area | Supported today | Notes and limitations |
| --- | --- | --- |
| Version info | `multi-pwsh --version`, `multi-pwsh -V`, `multi-pwsh version` | Prints the `multi-pwsh` package version without inspecting local install state. Extra arguments are rejected. |
| Help | `multi-pwsh --help`, `multi-pwsh -h`, `multi-pwsh help [command]`, `multi-pwsh <command> --help` | Focused command help is available without platform detection or local install state. |
| Install | `multi-pwsh install <stable\|preview\|lts\|version\|major\|major.minor\|major.minor.x>` | `stable`, `preview`, and `lts` resolve against GitHub PowerShell releases. `major.minor.x` installs every available patch release in that line. |
| Update | `multi-pwsh update <stable\|preview\|lts\|major.minor>` | Channel updates behave like installing the newest matching channel. Line updates refresh line, major, and managed named alias policies after installing the newest patch. |
| Uninstall | `multi-pwsh uninstall <version> [--scope <user\|machine>] [--root <path>] [--force]` | Removes managed files and updates aliases that referenced the removed version. |
| List | `multi-pwsh list [--scope <user\|machine\|all>] [--root <path>] [--available] [--include-prerelease]` | Installed listing shows paths, resolved aliases, named alias policies, and minor pins. Available listing queries GitHub releases. |
| Alias | `multi-pwsh alias set/unset` for `major.minor`, `pwsh`, `pwsh-preview`, and `pwsh-lts` | Minor aliases can be pinned or follow latest in line. Named aliases store policies and resolve only to installed versions. |
| Host | `multi-pwsh host <version\|major\|major.minor\|pwsh-alias> [pwsh arguments...]` | Runs through the native host. Alias shims can also invoke host mode implicitly when `pwsh-*` names are used from the managed bin directory. |
| Virtual environments | `multi-pwsh venv create/delete/export/import/list` plus host `-VirtualEnvironment` / `-venv` | Provides a managed module root for hosted PowerShell launches. |
| Doctor | `multi-pwsh doctor --repair-aliases` | Repairs host shims, alias files, and managed named alias policy resolutions. |
| Package subcommand | `multi-pwsh package install/uninstall/list` | Lower-level scoped install backend retained for explicit package-style operations. |

## Version selectors and channels

| Selector | Meaning | Alias side effects |
| --- | --- | --- |
| `stable` | Latest GA/non-preview PowerShell release with a matching platform asset. | Ensures `pwsh` follows `stable` if no `pwsh` policy exists, then refreshes policy aliases. |
| `preview` | Latest prerelease PowerShell release with a matching platform asset. | Ensures `pwsh-preview` follows `preview` if no policy exists, then refreshes policy aliases. |
| `lts` | Latest patch in the current encoded LTS line. | Ensures `pwsh-lts` follows `lts` if no policy exists, then refreshes policy aliases. |
| `<major>` | Latest matching major release. | Updates `pwsh-<major>` and patch aliases. |
| `<major>.<minor>` | Latest matching line release. | Updates `pwsh-<major>.<minor>`, `pwsh-<major>`, patch aliases, and named alias policies. |
| `<major>.<minor>.x` | Every available release in a line. | Updates patch aliases for all installed versions and line/major aliases. |
| Exact version | A specific PowerShell version, including normalized preview/RC shorthand. | Updates the exact patch alias plus line/major aliases. |

The current LTS line is encoded in source so LTS selection remains deterministic. Update that table when PowerShell changes the active LTS line.

## Alias behavior

| Alias | Default policy | Allowed policies | Resolution behavior |
| --- | --- | --- | --- |
| `pwsh` | `stable` after `install stable` | `stable`, `preview`, `lts`, or exact version | Resolves to the newest installed version matching the policy. |
| `pwsh-preview` | `preview` after `install preview` | `preview` or exact prerelease | Refuses stable/LTS policies to avoid accidentally launching GA builds. |
| `pwsh-lts` | `lts` after `install lts` | `lts` or exact current-LTS version | Refuses non-LTS policies. |
| `pwsh-<major>` | Latest installed version in the major | Managed automatically | Recomputed after install/update/uninstall. |
| `pwsh-<major>.<minor>` | Latest installed version in the line | Can be pinned to an exact version or unpinned with `latest` | Pinned aliases remain configured but unresolved if their target is not installed. |
| `pwsh-<exact>` | Exact installed version | Managed automatically | Removed when the exact version is uninstalled. |

Named alias policies are stored separately from resolved alias metadata. `list` shows both the currently resolved aliases and whether each named alias policy is resolved or unresolved.

## Platform and scope support

| Platform | User scope | Machine scope | Notes |
| --- | --- | --- | --- |
| Windows | Supported | Supported | Uses official ZIP assets with MSI-like install roots. Archive-safe integration flags are supported; Microsoft Update registration is intentionally not supported. |
| macOS | Supported | Supported | Machine installs use `/usr/local/microsoft/powershell` with aliases in `/usr/local/bin`; callers must provide elevation when needed. |
| Linux | Supported | Supported | Machine installs use `/opt/microsoft/powershell` with aliases in `/usr/local/bin`; callers must provide elevation when needed. |

Architectures are `auto`, `x64`, `x86`, `arm64`, and `arm32`, subject to platform asset availability. Unsupported OS/architecture combinations fail before download.

## State, lifecycle, and diagnostics

| State file or directory | Purpose |
| --- | --- |
| Install root | Stores side-by-side PowerShell payloads and package metadata. |
| `bin` directory | Contains hard-linked or copied `multi-pwsh` host shims named as aliases. Add this directory to `PATH` once per scope. |
| `aliases.json` | Stores resolved aliases, minor pins, and named alias policies. |
| `cache` directory | Stores downloaded release assets and checksum assets. |
| `venv` directory | Stores managed virtual environment module roots. |
| `multi-pwsh-layout.json` | Lets host shims recover their layout when the bin directory is shared or overridden. |

Install, update, uninstall, and `doctor --repair-aliases` all reconcile aliases. `list` is the primary status surface for local state; `list --available` is the online release inventory surface.

## Host and startup-hook support

| Feature | Supported today | Notes |
| --- | --- | --- |
| Native host launch | Yes | `multi-pwsh host` resolves selectors to installed executables and runs through `pwsh-host`. |
| Implicit shim host mode | Yes | Alias shims detect their own name and layout, then run the matching selector. |
| Virtual environment module path | Yes | Host mode sets startup-hook environment variables and bootstraps module cmdlet aliases for `-Command` and stdin `-File -` scenarios. |
| Venv archive import/export | Yes | ZIP import rejects absolute paths and parent-directory traversal. |

## Practical roadmap gaps

| Gap | Value | Notes |
| --- | --- | --- |
| Dry-run planning for install/update/uninstall | High | Would let users preview selected release, download URL, alias changes, and integration changes without mutating state. |
| Dedicated `status` command | High | `list` now includes policy state, but a purpose-built status command could add PATH checks, shim link health, stale cache information, and recommended repairs. |
| Shell integration diagnostics | Medium | Detect whether the managed bin directory is on `PATH` for the current shell and scope. |
| LTS metadata source | Medium | The active LTS line is encoded in source. A release-time check or metadata source would reduce manual updates. |
| Rich uninstall selectors | Medium | Uninstall currently requires an exact version. Channel or line uninstall would need careful safety prompts or dry-run first. |
| Machine-scope privilege guidance | Medium | Unix machine installs intentionally do not invoke `sudo`; diagnostics could make permission failures more actionable. |
| Structured output | Low | JSON output for `list`, `status`, and `--available` would help automation. |
| Shell completions | Low | Completion script generation is not implemented yet. |
