---
background: "#101820"
foreground: "#f8f8f2"
border: "#4fd1c5"
borderStyle: "rounded"
h1: "small"
h1Color: "#4fd1c5"
h2: "small"
h2Color: "#ffd166"
h3: "mini"
h3Color: "#4fd1c5"
pagination: true
paginationStyle: "progress"
---

# multi-pwsh

---

### Complete PSConfEU field kit
<!-- paginationStyle: dots -->

Exhaustive source deck.

| Use it as | Outcome |
|-----------|---------|
| complete lab | exercise every feature deliberately |
| Q&A menu | jump to the audience's rabbit hole |
| source material | trim into a talk, workshop, or Open Stage slot |

Run everything, then trim later.

---

### Why this deck exists

This is not the 10-minute talk.

* Complete lab.
* Q&A menu.
* Open Stage backup.
* Source material to cut down later.

---

### PSConfEU framing

Real-life scripts

```powershell
pwsh-7.4
```

|||

DevOps matrices

```powershell
multi-pwsh host 7.5
```

|||

Cloud plus on-prem modules

```powershell
multi-pwsh host 7.4 -venv cloud
```

multi-pwsh is the terminal switchboard.

---

### Safety contract

| Default live path | Why |
|-------------------|-----|
| sandboxed multi-pwsh home | no machine-wide surprises |
| kept demo artifacts | inspectable after questions |
| no machine-scope changes | safe for conference laptops |
| offline-first chapters | room Wi-Fi is optional |

Admin and destructive features are still covered as safe cards.

---

### Chapter map

| Chapters | Coverage |
|----------|----------|
| 1-3 | bootstrap, help, release discovery, sandboxed roots |
| 4-6 | selectors, install lifecycle, scope/root/arch/platform flags |
| 7-9 | alias policies, native host mode, virtual environments |
| 10-12 | real modules, export/import, CI and AI safety |
| 13-15 | diagnostics, package backend, admin safe cards |
| 16-17 | Deck-mode scripted chapters, limitations, trim map |

---

### Presenter setup

Deck terminal

```powershell
.\scripts\demo\Show-PSConfEUDeck.ps1 -Full
```

|||

Live shell

```powershell
. .\scripts\demo\Initialize-PSConfEUFullDemo.ps1 -KeepArtifacts
```

|||

Reset

```powershell
.\scripts\demo\Reset-PSConfEUFullDemo.ps1
```

---

### Deck mechanics used here

This deck now follows the examples in `D:\dev\Deck`:

* `#` and `##` slides are visual title and chapter breaks.
* `###` slides carry the hands-on content.
* `*` bullets reveal one at a time.
* `|||` compares commands side by side.
* Markdown tables turn command lists into scan-friendly cards.

---

## Chapter 16

---

### Deck-mode scripted chapters

If a live chapter needs to become a single command, use the Deck runner:

```powershell
.\scripts\demo\Invoke-PSConfEUDeckDemo.ps1 -Demo AliasPinning -KeepArtifacts
.\scripts\demo\Invoke-PSConfEUDeckDemo.ps1 -Demo HostSelectors -KeepArtifacts
.\scripts\demo\Invoke-PSConfEUDeckDemo.ps1 -Demo PrereleaseInstall -KeepArtifacts
.\scripts\demo\Invoke-PSConfEUDeckDemo.ps1 -Demo VenvSupport -KeepArtifacts
```

The underlying `Demo-*.ps1` scripts keep their standalone mode and add `-Deck` for compact terminal output.

---

## Preflight

Build the local binary once.

```powershell
cargo build -p multi-pwsh --release
$env:PATH = "$(Resolve-Path .\target\release);$env:PATH"
multi-pwsh --version
```

For online chapters, preinstall the versions you want to avoid waiting on downloads.

---

## Chapter 1

## Bootstrap the tool

The first PSConfEU question is simple:

"How do I get this onto a machine without changing my whole PowerShell setup?"

---

## Windows bootstrap

Shown, not normally run during the exhaustive demo.

```powershell
irm https://raw.githubusercontent.com/Devolutions/multi-pwsh/refs/heads/master/tools/install-multi-pwsh.ps1 | iex
```

Specific release:

```powershell
& (irm https://raw.githubusercontent.com/Devolutions/multi-pwsh/refs/heads/master/tools/install-multi-pwsh.ps1) -Version v0.10.0
```

---

## Unix bootstrap

For the cross-platform PSConfEU crowd.

```powershell
curl -fsSL https://raw.githubusercontent.com/Devolutions/multi-pwsh/refs/heads/master/tools/install-multi-pwsh.sh | bash
```

Specific release:

```powershell
curl -fsSL https://raw.githubusercontent.com/Devolutions/multi-pwsh/refs/heads/master/tools/install-multi-pwsh.sh | bash -s -- --version v0.10.0
```

---

## Bootstrap from a fork

Useful for testing a PR or workshop build.

```powershell
& (irm https://raw.githubusercontent.com/Devolutions/multi-pwsh/refs/heads/master/tools/install-multi-pwsh.ps1) `
  -Owner Devolutions `
  -Repository multi-pwsh `
  -Version v0.10.0
```

Point: the bootstrapper is part of the demo story, but not the only story.

---

## Bootstrap uninstall

Safe card.

```powershell
irm https://raw.githubusercontent.com/Devolutions/multi-pwsh/refs/heads/master/tools/uninstall-multi-pwsh.ps1 | iex
```

Talk track:

- removes the installed binary.
- removes the user PATH entry when it owns it.
- not part of the default live path because we need the tool for the rest of the deck.

---

## Help and version UX

Live:

```powershell
multi-pwsh --version
multi-pwsh -V
multi-pwsh version
multi-pwsh --help
multi-pwsh help install
multi-pwsh install --help
```

Appeal: discoverability matters when someone tries the tool after your session.

---

## Invalid input is a feature

Safe live moment:

```powershell
multi-pwsh help bananas
multi-pwsh alias set pwsh-preview stable
```

Expected point:

- clear rejection.
- no mutation.
- guardrails are visible.

---

## Chapter 2

## Discover installed and available releases

Inventory before mutation.

---

## Local inventory

Live:

```powershell
multi-pwsh list
```

What to point out:

- installed versions.
- alias names.
- unresolved policies.
- sandboxed root paths.

---

## Remote inventory

Online chapter.

```powershell
multi-pwsh list --available
multi-pwsh list --available --include-prerelease
```

PSConfEU angle:

- stable for production.
- LTS for conservative environments.
- preview for hallway experiments.

---

## Selector vocabulary

Use these throughout the deck.

- `stable`
- `preview`
- `lts`
- `7`
- `7.4`
- `7.4.x`
- `7.4.13`
- `7.6-preview6`
- `7.6-rc1`
- `7.6.0-rc.1`

The audience should leave knowing selectors are the language of the tool.

---

## Chapter 3

## Sandboxed roots and environment control

Everything live from here should happen inside a sandbox.

---

## Create the full demo context

Live:

```powershell
. .\scripts\demo\Initialize-PSConfEUFullDemo.ps1 -KeepArtifacts
```

It sets:

```powershell
$env:MULTI_PWSH_HOME
$env:MULTI_PWSH_BIN_DIR
$env:MULTI_PWSH_CACHE_DIR
$env:MULTI_PWSH_VENV_DIR
$env:MULTI_PWSH_CACHE_KEEP
```

---

## Why PSConfEU should care

This is the bridge to DevOps:

- put the home under a workspace.
- cache downloads.
- keep venvs next to test artifacts.
- delete the whole world when the job ends.

---

## Layout files and state

Show the filesystem after the first install.

```powershell
Get-ChildItem $env:MULTI_PWSH_HOME -Force
Get-ChildItem $env:MULTI_PWSH_BIN_DIR -Force
Get-ChildItem $env:MULTI_PWSH_VENV_DIR -Force
```

Mention:

- `multi-pwsh-layout.json`
- `aliases.json`
- cache directory.

---

## Chapter 4

## Install selectors

This is where versions become disposable.

---

## Install the common production story

Live or preloaded:

```powershell
multi-pwsh install 7.4.12
multi-pwsh install 7.4.13
multi-pwsh install 7.5
multi-pwsh list
```

Point: side-by-side versions are normal.

---

## Install by channel

Online chapter.

```powershell
multi-pwsh install stable
multi-pwsh install lts
multi-pwsh install preview --include-prerelease
```

Audience hook:

- stable for today's session.
- LTS for production.
- preview for "can I try the new thing?"

---

## Install by selector family

Online chapter.

```powershell
multi-pwsh install 7
multi-pwsh install 7.4
multi-pwsh install 7.4.x
multi-pwsh install 7.4.13
```

Talk track: selector precision can match your policy.

---

## Prerelease shorthand

Online chapter.

```powershell
multi-pwsh install 7.6-preview6 --include-prerelease
multi-pwsh install 7.6-rc1 --include-prerelease
multi-pwsh install 7.6.0-rc.1 --include-prerelease
```

Point: preview testing should not require replacing stable.

---

## Checksum controls

Security safe card.

```powershell
multi-pwsh install 7.4.13 --hash-file .\known-good.sha256
multi-pwsh install 7.4.13 --checksum-file .\known-good.sha256
```

Explicit caveat:

```powershell
multi-pwsh install 7.4.13 --skip-hash-verification
```

Do not make skip-verification the normal path.

---

## Chapter 5

## Update and uninstall lifecycle

Patch management, cleanup, and rollback visibility.

---

## Update installed lines

Live if versions are already present:

```powershell
multi-pwsh update stable
multi-pwsh update 7.4
multi-pwsh list
```

Talk track:

- update chooses the latest matching release.
- aliases reconcile after install/update.

---

## Uninstall

Live in sandbox:

```powershell
multi-pwsh uninstall 7.4.12
multi-pwsh list
```

Force safe card:

```powershell
multi-pwsh uninstall 7.4.12 --force
```

Point: destructive commands stay inside the demo root.

---

## Chapter 6

## Scope, root, arch, and platform flags

Enterprise knobs without enterprise risk.

---

## User scope and custom root

Live sandbox:

```powershell
$demoRoot = Join-Path $env:MULTI_PWSH_HOME 'custom-root'
multi-pwsh install 7.4.13 --scope user --root $demoRoot --arch auto --no-add-path
multi-pwsh list --scope user --root $demoRoot
multi-pwsh list --scope all
```

Appeal: test a deployment layout without changing the laptop.

---

## Architecture selection

Safe card.

```powershell
multi-pwsh install 7.5 --arch auto
multi-pwsh install 7.5 --arch x64
multi-pwsh install 7.5 --arch x86
multi-pwsh install 7.5 --arch arm64
multi-pwsh install 7.5 --arch arm32
```

Talk track: asset selection follows the platform and requested architecture.

---

## PATH behavior

Safe live:

```powershell
multi-pwsh install 7.5 --add-path
multi-pwsh install 7.5 --no-add-path
```

Point:

- developer laptops may want PATH.
- CI and demos often prefer explicit roots.

---

## Windows integration flags

Admin safe card.

```powershell
multi-pwsh install 7.5 --scope machine --register-manifest
multi-pwsh install 7.5 --scope machine --enable-psremoting
multi-pwsh install 7.5 --scope machine --disable-telemetry
multi-pwsh install 7.5 --scope machine --add-explorer-context-menu
multi-pwsh install 7.5 --scope machine --add-file-context-menu
```

Do not run live unless the session is explicitly about admin install behavior.

---

## Unix machine scope

Safe card.

```powershell
multi-pwsh install 7.5 --scope machine --root /opt/microsoft/powershell
multi-pwsh install 7.5 --scope machine --root /usr/local/microsoft/powershell
```

Point:

- multi-pwsh does not invoke sudo.
- caller chooses elevation model.

---

## Chapter 7

## Alias policies

This is the operator-friendly reveal.

---

## Versioned aliases

Live:

```powershell
multi-pwsh alias set 7.4 7.4.12
pwsh-7.4 --version

multi-pwsh alias set 7.4 7.4.13
pwsh-7.4 --version

multi-pwsh alias set 7.4 latest
multi-pwsh list
```

Point: command name stays stable while target moves.

---

## Rollback

Live:

```powershell
multi-pwsh alias set 7.4 7.4.12
pwsh-7.4 --version
multi-pwsh list
```

PSConfEU hook: "This is what I want before upgrading production runbooks."

---

## Named channel aliases

Live:

```powershell
multi-pwsh alias set pwsh stable
multi-pwsh alias set pwsh lts
multi-pwsh alias set pwsh-preview preview
multi-pwsh alias set pwsh-lts lts
multi-pwsh list
```

Point:

- named aliases are policy channels.
- they resolve to installed versions.

---

## Alias guardrails

Safe card, expected to fail.

```powershell
multi-pwsh alias set pwsh-preview stable
multi-pwsh alias set pwsh-lts preview
```

Talk track:

- preview alias refuses stable policies.
- LTS alias refuses non-LTS policies.

---

## Alias cleanup

Live:

```powershell
multi-pwsh alias unset 7.4
multi-pwsh alias unset pwsh
multi-pwsh alias unset pwsh-preview
multi-pwsh alias unset pwsh-lts
multi-pwsh list
```

Useful when a demo wants to reset policy without deleting installed engines.

---

## Chapter 8

## Native host mode

For the deep PowerShell and tool-builder crowd.

---

## Exact and line selectors

Live:

```powershell
multi-pwsh host 7.4.13 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
multi-pwsh host 7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
multi-pwsh host 7 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
```

Point: the host selector chooses the engine deterministically.

---

## Alias selector and implicit shim mode

Live:

```powershell
multi-pwsh alias set 7.4 7.4.13
multi-pwsh host pwsh-7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
pwsh-7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
```

Point: generated shims enter host mode by their own name.

---

## Pass-through PowerShell arguments

Live:

```powershell
multi-pwsh host 7.4 -NoLogo -NoProfile -NonInteractive -Command 'Get-Date'
multi-pwsh host 7.4 -NoLogo -NoProfile -File .\Invoke-SmokeTest.ps1
```

The tool selects the engine; PowerShell still receives normal PowerShell arguments.

---

## Named pipe host command

Windows advanced safe card.

```powershell
multi-pwsh host 7.4 -NamedPipeCommand DemoPipe
```

Use with:

```powershell
.\scripts\Start-NamedPipeTextServer.ps1
```

Appeal: app embedding, command injection harnesses, and deep host demos.

---

## Chapter 9

## Virtual environments

Module worlds for cloud, on-prem, preview, and AI-safety demos.

---

## Create venvs

Live:

```powershell
multi-pwsh venv create cloud
multi-pwsh venv create onprem
multi-pwsh venv create preview
multi-pwsh venv create ai
multi-pwsh venv list
```

Point: venv names are cheap and explicit.

---

## Seed offline module worlds

Live:

```powershell
.\scripts\demo\Initialize-PSConfEUDemoWorlds.ps1 -Full
```

Creates local modules:

- `Conference.CloudIdentity`
- `Conference.OnPremOps`
- `Conference.PreviewLab`
- `Conference.AISafety`

No PSGallery. No Wi-Fi. Still a real module-discovery demo.

---

## Launch with short venv flag

Live:

```powershell
$cmd = 'Import-Module Conference.CloudIdentity -ErrorAction SilentlyContinue; Import-Module Conference.OnPremOps -ErrorAction SilentlyContinue; Import-Module Conference.PreviewLab -ErrorAction SilentlyContinue; Import-Module Conference.AISafety -ErrorAction SilentlyContinue; Get-ConferenceReality; $env:PSMODULE_VENV_PATH'

multi-pwsh host 7.4 -venv cloud -NoLogo -NoProfile -NonInteractive -Command $cmd
multi-pwsh host 7.4 -venv onprem -NoLogo -NoProfile -NonInteractive -Command $cmd
```

Same command, different module world.

---

## Launch with long venv flag

Live:

```powershell
multi-pwsh host 7.4 -VirtualEnvironment preview -NoLogo -NoProfile -NonInteractive -Command $cmd
multi-pwsh host 7.4 -VirtualEnvironment ai -NoLogo -NoProfile -NonInteractive -Command $cmd
```

Point: both spellings exist; use the readable one in docs, the short one live.

---

## Launch with explicit path

Live:

```powershell
$env:PSMODULE_VENV_PATH = Join-Path $env:MULTI_PWSH_VENV_DIR 'cloud'
multi-pwsh host 7.4 -NoLogo -NoProfile -NonInteractive -Command $cmd
Remove-Item Env:\PSMODULE_VENV_PATH
```

Point: named venvs are convenient; explicit paths are useful in CI.

---

## Venv is module isolation

Honest boundary slide.

- It changes module discovery and install paths.
- It redirects PowerShellGet and PSResourceGet current-user module targets.
- It is not a full process/container sandbox.
- Built-in/default module paths can still exist.

This earns trust with a PowerShell crowd.

---

## Chapter 10

## Install real modules into venvs

Optional online chapter.

---

## PowerShellGet path

Online:

```powershell
multi-pwsh host 7.4 -venv graph -NoLogo -NoProfile -NonInteractive `
  -Command 'Install-Module Microsoft.Graph.Authentication -Scope CurrentUser -Force'
```

Then:

```powershell
multi-pwsh host 7.4 -venv graph -NoLogo -NoProfile -NonInteractive `
  -Command 'Get-Module -ListAvailable Microsoft.Graph.Authentication,Az.Accounts'
```

---

## PSResourceGet path

Online:

```powershell
multi-pwsh host 7.4 -venv az -NoLogo -NoProfile -NonInteractive `
  -Command 'Install-PSResource Az.Accounts -TrustRepository -Quiet -Reinstall'
```

Then:

```powershell
multi-pwsh host 7.4 -venv az -NoLogo -NoProfile -NonInteractive `
  -Command 'Get-Module -ListAvailable Microsoft.Graph.Authentication,Az.Accounts'
```

---

## Chapter 11

## Venv export, import, and delete

Turn a working module world into a handoff artifact.

---

## Export and import

Live:

```powershell
$archive = Join-Path $env:MULTI_PWSH_HOME 'cloud.zip'
multi-pwsh venv export cloud $archive
multi-pwsh venv import cloud-copy $archive
multi-pwsh host 7.4 -venv cloud-copy -NoLogo -NoProfile -NonInteractive -Command $cmd
```

Appeal: send the exact working module world to CI or a teammate.

---

## Delete venvs

Live:

```powershell
multi-pwsh venv delete cloud-copy
multi-pwsh venv list
```

Safe card:

- import refuses absolute archive paths.
- import refuses parent-directory traversal.
- import refuses to merge into an existing venv.

---

## Chapter 12

## CI and AI safety

The PSConfEU 2026 angle: test generated or borrowed code before trust.

---

## CI matrix shape

Shown or live if a test script exists.

```powershell
foreach ($version in '7.4', '7.5') {
  multi-pwsh host $version -venv cloud -NoLogo -NoProfile -File .\Invoke-SmokeTest.ps1
}
```

Maps directly to a GitHub Actions matrix.

---

## AI candidate script safety

Shown or live with a prepared candidate file.

```powershell
multi-pwsh host 7.4 -venv ai -NoLogo -NoProfile -File .\candidate.ps1
multi-pwsh host 7.5 -venv ai -NoLogo -NoProfile -File .\candidate.ps1
```

Point: "try it in a disposable PowerShell and module world first."

---

## Cacheable CI layout

Shown:

```powershell
$env:MULTI_PWSH_HOME = Join-Path $PWD '.pwsh'
$env:MULTI_PWSH_BIN_DIR = Join-Path $env:MULTI_PWSH_HOME 'bin'
$env:MULTI_PWSH_CACHE_DIR = Join-Path $env:MULTI_PWSH_HOME 'cache'
$env:MULTI_PWSH_VENV_DIR = Join-Path $env:MULTI_PWSH_HOME 'venv'
$env:MULTI_PWSH_CACHE_KEEP = '1'
```

Cache the `.pwsh` directory between CI runs.

---

## Chapter 13

## Diagnostics and repair

Every good operations tool needs a doctor.

---

## Repair aliases

Live:

```powershell
multi-pwsh alias set 7.4 7.4.13
.\scripts\demo\Break-PSConfEUAliasShim.ps1 -AliasName pwsh-7.4
Get-Command pwsh-7.4
multi-pwsh doctor --repair-aliases
Get-Command pwsh-7.4
multi-pwsh list
```

Point: repair is visible and scoped to the demo bin.

---

## What doctor repairs

- host shims.
- alias links/copies.
- named alias policy resolutions.
- stale or missing generated aliases.

Show `list` before and after repair.

---

## Chapter 14

## Package backend

Lower-level plumbing for people who want to know the layers.

---

## Package list

Live:

```powershell
multi-pwsh package list --scope user
```

Explain:

- normal users start with `install`, `update`, `list`.
- package mode exposes package-style operations explicitly.

---

## Package install and uninstall

Sandboxed live:

```powershell
$packageRoot = Join-Path $env:MULTI_PWSH_HOME 'package-root'
multi-pwsh package install 7.4.13 --scope user --root $packageRoot --no-add-path
multi-pwsh package list --scope user --root $packageRoot
multi-pwsh package uninstall 7.4.13 --scope user --root $packageRoot --force
```

Point: same engine, lower-level workflow.

---

## Chapter 15

## Admin and destructive safe cards

Exhaustive coverage does not mean reckless live changes.

---

## Machine-scope install

Safe card.

```powershell
multi-pwsh install 7.5 --scope machine
```

Use when:

- installing for all users.
- preparing a shared server.
- you have a planned elevation model.

Do not run during a generic conference demo.

---

## PSRemoting and manifest registration

Safe card.

```powershell
multi-pwsh install 7.5 --scope machine --enable-psremoting
multi-pwsh install 7.5 --scope machine --register-manifest
multi-pwsh install 7.5 --scope machine --no-register-manifest
```

On-prem hook: server and remoting scenarios.

---

## Explorer and file context menus

Safe card.

```powershell
multi-pwsh install 7.5 --scope machine --add-explorer-context-menu
multi-pwsh install 7.5 --scope machine --add-file-context-menu
```

Windows shell integration belongs in an admin demo, not the default path.

---

## Telemetry and checksum caveats

Safe card.

```powershell
multi-pwsh install 7.5 --disable-telemetry
multi-pwsh install 7.5 --skip-checksum-verification
```

Talk track:

- disabling telemetry is an install option.
- skipping checksum verification should be rare and deliberate.

---

## Chapter 17

## Honest limitations

PSConfEU audiences appreciate tradeoffs.

---

## Boundaries

- Archive installs do not register with Microsoft Update.
- Venvs isolate module roots, not whole processes.
- Unix machine scope expects caller-managed elevation.
- Windows-only integration flags are rejected on Unix.
- JSON output is not part of the current CLI.
- Shell completions are not part of the current CLI.

This is useful Q&A material.

---

## Trim map

10 minutes:

- sandbox.
- install/list.
- alias rollback.
- host selector.
- venv module worlds.
- export/import.

20 minutes:

- add bootstrap.
- add update/uninstall.
- add CI/AI safety.

---

## Longer variants

45 minutes:

- add scopes, roots, arch.
- add real Graph/Az module install.
- add doctor repair.
- add package backend.

Workshop:

- run every chapter.
- make attendees break and repair aliases.
- make attendees export venvs and trade artifacts.

---

## Choose your Q&A path

Ask the room:

- rollback?
- preview?
- Graph and Az?
- on-prem?
- native hosting?
- CI?
- AI safety?
- repair?
- package plumbing?

Then jump with:

```powershell
.\scripts\demo\Show-PSConfEUDeck.ps1 -Full -StartSlide 42
```

---

## Final message

multi-pwsh makes PowerShell versions and module worlds disposable:

- install side-by-side.
- name them predictably.
- select hosts deterministically.
- isolate module stacks.
- export what worked.
- repair what drifted.

That is a PSConfEU hallway-track superpower.
