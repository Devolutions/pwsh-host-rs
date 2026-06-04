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

### 1. Bootstrapping multi-pwsh

Bootstrap multi-pwsh executable

From bash (if you don't have pwsh):

```bash
curl -fsSL https://github.com/Devolutions/multi-pwsh/releases/latest/download/install-multi-pwsh.sh | bash
```

From Windows PowerShell (if you don't have pwsh):

```powershell
irm https://github.com/Devolutions/multi-pwsh/releases/latest/download/install-multi-pwsh.ps1 | iex
```

multi-pwsh doesn't require PowerShell to install PowerShell

---

---

### 2. Install PowerShell versions

First, make version sprawl visible.

```powershell
multi-pwsh install 7.4.12
multi-pwsh install 7.5.x
multi-pwsh install 7.6
```

```powershell
pwsh-7.4.12 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
pwsh-7.5 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
pwsh-7.6 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
```

```powershell
multi-pwsh list
```

Point: the installed PowerShell versions are explicit, named, and disposable.

---

### 3. Install PowerShell release channels

The useful everyday aliases are the release channels.

```powershell
multi-pwsh install stable
multi-pwsh install lts
multi-pwsh install preview

pwsh -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
pwsh-lts -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
pwsh-preview -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
```

```powershell
multi-pwsh list
```

Keep latest stable as your default, with LTS and preview always handy.

---

### 4. Upgrade and rollback without renaming scripts

Version-specific aliases are still there when a script needs a fixed line.

```powershell
multi-pwsh alias set 7.4 7.4.13
pwsh-7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'

multi-pwsh alias set 7.4 7.4.12
pwsh-7.4 -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'

multi-pwsh alias set stable 7.6
pwsh -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion'
```

No explicit launching of pwsh in a custom path - just use one of the aliases

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

## Virtual Environments

---

### 5. Creating PowerShell venv

Borrow the Python idea, apply it to PowerShell modules.

```powershell
multi-pwsh venv create jake
multi-pwsh venv list
```

```powershell
pwsh-lts -venv jake
@('Deck','Stepper') | % { Install-Module -Name $_ -Force }
```

Now a script can bring its dependencies with it, without polluting your real `PSModulePath`.

---

## Environment Exportability

---

### 6. Exporting PowerShell venv

The working module environment becomes a repeatable artifact.

```powershell
$archive = Join-Path $env:TEMP 'jake-venv.zip'
multi-pwsh venv export jake $archive
multi-pwsh venv import horse $archive
pwsh-lts -venv horse -Command { Get-Module }
```

Now the dependency set can move to CI, a teammate, or tomorrow's Open Stage.

---

### 8. The CI and AI safety angle

Before trusting a generated script or pipeline change:

```powershell
foreach ($version in '7.4', '7.5') {
  multi-pwsh host $version -venv jake -NoLogo -NoProfile -Command { .\Invoke-Deck.ps1 }
}
```

```powershell
pwsh-lts -venv jake -NoLogo -NoProfile -Command { .\Invoke-Stepper.ps1 }
```

Same idea as a build matrix, but available locally first.

---

## What just happened

---

### The takeaways

* PowerShell versions and release channels easily available
* Aliases made stable, LTS, and preview easy to keep side by side.
* Venvs made script dependencies isolated, exportable, and portable.

The real feature is confidence before you run someone else's PowerShell.

---
