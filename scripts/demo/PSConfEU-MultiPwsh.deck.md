---
background: "#101820"
foreground: "#f8f8f2"
border: "#4fd1c5"
borderStyle: "rounded"
h1: "04B_03__"
h1Color: "#4fd1c5"
h2: "04B_03__"
h2Color: "#ffd166"
h3: "04B_03__"
h3Color: "#4fd1c5"
pagination: true
paginationStyle: "progress"
---

# multi-pwsh

---

### All the PowerShells. One laptop.
<!-- paginationStyle: dots -->

| If you need to... | multi-pwsh lets you... |
|-------------------|------------------------|
| try another PowerShell release | install it beside the one you already use |
| keep stable, LTS, and preview handy | use `pwsh`, `pwsh-lts`, and `pwsh-preview` |
| run a script on a specific version | launch exactly that PowerShell |
| test a risky module stack | use a disposable module venv |
| repeat the setup elsewhere | export and import the venv as a zip |

---

## Version control for PowerShell itself

---

### 1. See the PowerShells

First, make version sprawl visible.

```powershell
multi-pwsh install 7.4.12
multi-pwsh install 7.5.x
multi-pwsh install 7.6
multi-pwsh list
```

Point: the installed PowerShell versions are explicit, named, and disposable.

---

### 2. Keep release channels side by side

The useful everyday aliases are the release channels.

```powershell
multi-pwsh alias set pwsh stable
multi-pwsh alias set pwsh-lts lts
multi-pwsh alias set pwsh-preview preview

pwsh -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
pwsh-lts -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
pwsh-preview -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
```

Keep latest stable as your default, with LTS and preview always handy.

---

### 3. Upgrade and rollback without renaming scripts

Version-specific aliases are still there when a script needs a fixed line.

```powershell
multi-pwsh alias set 7.4 7.4.13
pwsh-7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'

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

Exact version

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

### 5. PowerShell module venvs

Borrow the Python idea, apply it to PowerShell modules.

```powershell
. .\scripts\demo\_DemoCommon.ps1
$context = New-DemoContext -DemoName 'psconfeu-deck' -KeepArtifacts
.\scripts\demo\Initialize-PSConfEUDemoWorlds.ps1
multi-pwsh venv list
```

Now a script can bring its dependencies with it, without polluting your real `PSModulePath`.

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

The working module environment becomes a repeatable artifact.

```powershell
$archive = Join-Path $env:TEMP 'multi-pwsh-cloud.zip'
multi-pwsh venv export cloud $archive
multi-pwsh venv import cloud-copy $archive

multi-pwsh host 7.4.12 -venv cloud-copy -NoLogo -NoProfile -NonInteractive `
  -Command $cmd
```

Now the dependency set can move to CI, a teammate, or tomorrow's Open Stage.

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

* PowerShell versions became named and disposable.
* Aliases made stable, LTS, and preview easy to keep side by side.
* `host` made runtime selection explicit.
* Venvs made module stacks disposable, isolated, and portable.
* Export/import made dependencies shareable.

The real feature is confidence before you run someone else's PowerShell.

---
