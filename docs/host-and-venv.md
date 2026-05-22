# Native host mode and virtual environments

## Native host mode

- `multi-pwsh host <selector> ...` runs PowerShell through native hosting (`pwsh-host`) instead of launching a `pwsh` subprocess.
- `<selector>` supports `7`, `7.4`, `7.4.13`, or alias-form selectors such as `pwsh-7.4`.
- `-VirtualEnvironment <name>` and `-venv <name>` are consumed by `multi-pwsh` before handing control to PowerShell and set `PSModulePath` to the selected venv module root for that launch.
- `PSMODULE_VENV_PATH` can also be used as an explicit path-based venv selector for hosted launches. If it is already set in the environment, `multi-pwsh host` treats it as an intentional venv opt-in.
- Alias lifecycle maintains native host shims as hard links to `multi-pwsh` during install, update, and `doctor --repair-aliases`.
- On Windows, alias command paths are `pwsh-*.exe` host shims in `MULTI_PWSH_BIN_DIR` (default: `~/.pwsh/bin`).
- On Linux/macOS, alias command paths (`pwsh-*`) are hard links to `multi-pwsh`.
- `multi-pwsh doctor --repair-aliases` performs a shim health check and re-links broken hard links automatically.
- Copying or renaming `multi-pwsh.exe` to an alias-like name such as `pwsh-7.4.exe` also enters implicit host mode.
- `-NamedPipeCommand <pipeName>` is supported in host mode on Windows.

## Virtual environments

`multi-pwsh` virtual environments provide isolated PowerShell module roots for hosted launches.

By default, venvs live under `~/.pwsh/venv/<name>`. If `MULTI_PWSH_VENV_DIR` is set, they live under that directory instead.

Available commands:

- `multi-pwsh venv create <name>`
- `multi-pwsh venv delete <name>`
- `multi-pwsh venv export <name> <archive.zip>`
- `multi-pwsh venv import <name> <archive.zip>`
- `multi-pwsh venv list`

### Create and use a venv

```powershell
multi-pwsh venv create msgraph
multi-pwsh host 7.4 -venv msgraph -NoLogo -NoProfile
multi-pwsh host 7.4 -venv msgraph -NoLogo -NoProfile -Command "$env:PSModulePath"
```

You can also opt into a venv by path:

```powershell
$env:PSMODULE_VENV_PATH = Join-Path $HOME ".pwsh/venv/msgraph"
multi-pwsh host 7.4 -NoLogo -NoProfile
```

If both a venv flag and `PSMODULE_VENV_PATH` are present, the named flag wins for that launch.

### Populate a venv with modules

Modules should live under `<venv-root>\Modules\<ModuleName>`.

```powershell
$venvRoot = Join-Path $HOME ".pwsh/venv/msgraph"
$venvModules = Join-Path $venvRoot "Modules"
Save-Module -Name Microsoft.Graph.Authentication -Repository PSGallery -Path $venvModules -Force
Save-Module -Name Microsoft.Graph.Users -Repository PSGallery -Path $venvModules -Force
multi-pwsh host 7.4 -venv msgraph -NoLogo -NoProfile -Command "Get-Module -ListAvailable Microsoft.Graph.Authentication"
```

### Export and import a venv

```powershell
multi-pwsh venv export msgraph msgraph.zip
multi-pwsh venv import msgraph-copy msgraph.zip
multi-pwsh host 7.4 -venv msgraph-copy -NoLogo -NoProfile
```

Import is intentionally conservative: importing into an existing destination venv is rejected instead of merging archive contents.

### Current behavior and limitations

- Venv selection changes module discovery and import precedence for hosted launches.
- `Install-Module` and `Install-PSResource` use the venv `Modules` directory during hosted launches.
- PowerShell may still include some built-in or default module paths in the effective `PSModulePath`; the venv is a selected module root, not a full process sandbox.
- The feature applies to `multi-pwsh host ...` and implicit host shims such as `pwsh-7.4.exe`, not to arbitrary external `pwsh` processes.

## Managed paths

- `MULTI_PWSH_HOME`: override the multi-pwsh home directory.
- `MULTI_PWSH_BIN_DIR`: override the shim and launcher directory.
- `MULTI_PWSH_CACHE_DIR`: override the archive cache directory.
- `MULTI_PWSH_VENV_DIR`: override the virtual-environment root directory.
- `MULTI_PWSH_CACHE_KEEP`: keep downloaded archives after extraction when set to a truthy value.

CI cache example:

```powershell
$env:MULTI_PWSH_HOME = "$(Join-Path $HOME '.pwsh')"
$env:MULTI_PWSH_BIN_DIR = "$(Join-Path $env:MULTI_PWSH_HOME 'bin')"
$env:MULTI_PWSH_CACHE_DIR = "$(Join-Path $env:MULTI_PWSH_HOME 'cache')"
$env:MULTI_PWSH_VENV_DIR = "$(Join-Path $env:MULTI_PWSH_HOME 'venv')"
$env:MULTI_PWSH_CACHE_KEEP = "1"
multi-pwsh install 7.4.x
```
