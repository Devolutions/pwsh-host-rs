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

### Four tracks. One laptop.
<!-- paginationStyle: dots -->

PSConfEU 2026 live terminal demo.

| In 10 minutes | You will see |
|---------------|--------------|
| versions | disposable PowerShell engines |
| aliases | safe upgrade and rollback |
| hosts | deterministic engine selection |
| venvs | portable module worlds |

Then we use the last 5 minutes for the rabbit hole you care about.

---

## Why this matters

---

### The hallway problem

Someone hands you a script between sessions.

* It worked yesterday on 7.4 LTS.
* The pipeline is testing stable.
* The hallway fix needs preview.
* The module stack might be Graph, Az, VMware, Exchange, or something internal.

You want to try it without turning your laptop into the experiment.

---

### Demo promise

Everything today is:

* terminal-native.
* reversible.
* disposable.
* safe to repeat after the talk.

No admin path. No Wi-Fi dependency. No "trust me, it works on my machine".

---

### The shape of the demo

| Beat | What changes | What stays stable |
|------|--------------|-------------------|
| install/list | available engines | your system PowerShell |
| alias pinning | target version | command name |
| host selection | runtime engine | command line |
| venv selection | module world | laptop state |
| export/import | machine boundary | working environment |

---

## Version control for PowerShell itself

---

### 1. See the engines

First, make version sprawl visible.

```powershell
multi-pwsh list
multi-pwsh install 7.4.12
multi-pwsh install 7.4.13
multi-pwsh install 7.5
multi-pwsh list
```

Point: the installed engines are explicit, named, and disposable.

---

### 2. Upgrade without renaming scripts

Production scripts can keep calling `pwsh-7.4`.

```powershell
multi-pwsh alias set 7.4 7.4.12
pwsh-7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'

multi-pwsh alias set 7.4 7.4.13
pwsh-7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
```

Same command name. Different pinned engine.

---

### 3. Rollback is the reveal

This is the part people should remember.

```powershell
multi-pwsh alias set 7.4 7.4.12
pwsh-7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
```

* Upgrade is one command.
* Rollback is one command.
* Scripts and PATH do not need surgery.

---

## Deterministic hosts

---

### 4. Stop guessing what `pwsh` means

Exact engine

```powershell
multi-pwsh host 7.4.12 -NoLogo -NoProfile -NonInteractive `
  -Command '$PSVersionTable.PSVersion.ToString()'
```

|||

Channel selector

```powershell
multi-pwsh host 7.4 -NoLogo -NoProfile -NonInteractive `
  -Command '$PSVersionTable.PSVersion.ToString()'
```

The caller chooses the runtime.

---

### Where that lands

| Scenario | Selector |
|----------|----------|
| reproduce a bug | exact version |
| follow a patched minor line | `7.4` |
| test current stable | `stable` |
| try tomorrow's behavior | `preview` |

That is useful in a shell, a test harness, and a GitHub Actions matrix.

---

## Module worlds

---

### 5. Two conference realities

Create two local module worlds without PSGallery.

```powershell
multi-pwsh venv create cloud
multi-pwsh venv create onprem
.\scripts\demo\Initialize-PSConfEUDemoWorlds.ps1
multi-pwsh venv list
```

The helper seeds tiny local modules for the live demo.

---

### Cloud and on-prem side by side

Cloud identity world

```powershell
multi-pwsh host 7.4.12 -venv cloud
```

Module: `Conference.CloudIdentity`

|||

On-prem automation world

```powershell
multi-pwsh host 7.4.12 -venv onprem
```

Module: `Conference.OnPremOps`

---

### 6. Same command, different reality

The selected venv decides what exists.

```powershell
$cmd = 'Import-Module Conference.CloudIdentity -ErrorAction SilentlyContinue; Import-Module Conference.OnPremOps -ErrorAction SilentlyContinue; Get-ConferenceReality'

multi-pwsh host 7.4.12 -venv cloud -NoLogo -NoProfile -NonInteractive `
  -Command $cmd

multi-pwsh host 7.4.12 -venv onprem -NoLogo -NoProfile -NonInteractive `
  -Command $cmd
```

Module conflicts become test data instead of laptop state.

---

## Portable confidence

---

### 7. Export what worked

The working module world becomes an artifact.

```powershell
$archive = Join-Path $env:TEMP 'multi-pwsh-cloud.zip'
multi-pwsh venv export cloud $archive
multi-pwsh venv import cloud-copy $archive

multi-pwsh host 7.4.12 -venv cloud-copy -NoLogo -NoProfile -NonInteractive `
  -Command $cmd
```

Now the environment can move to CI, a teammate, or tomorrow's Open Stage.

---

### 8. The CI and AI safety angle

Before trusting a generated script or pipeline change:

```powershell
foreach ($version in '7.4', '7.5') {
  multi-pwsh host $version -venv cloud -NoLogo -NoProfile -File .\Invoke-SmokeTest.ps1
}

multi-pwsh host 7.4 -venv onprem -NoLogo -NoProfile -File .\candidate.ps1
```

Same idea as a build matrix, but available locally first.

---

## What just happened

---

### The takeaways

* PowerShell versions became named, disposable engines.
* Aliases made upgrades reversible.
* `host` made runtime selection explicit.
* Venvs made module stacks portable.
* Export/import made the working state shareable.

The real feature is confidence before you run someone else's PowerShell.

---

### Pick the Q&A path

| If you ask about... | I will jump to... |
|---------------------|-------------------|
| Graph or Az | real PSGallery-backed venvs |
| preview releases | prerelease selectors |
| CI | version and venv matrix |
| repair | alias doctor demo |
| packaging | backend package commands |
| limitations | the honest tradeoff slide |

The full source deck has all of these ready.
