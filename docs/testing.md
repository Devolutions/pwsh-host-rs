# Testing

## Scoped install smoke tests

Run the cross-platform scoped install smoke harness:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File .\tests\Invoke-ScopedInstallSmokeTest.ps1
```

## Venv cmdlet matrix tests

Use the local Pester harness to validate venv-sensitive cmdlet behavior across installed version aliases (`pwsh-x.y.z`).

Run all installed version aliases:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File .\tests\Invoke-VenvTestMatrix.ps1
```

Run one alias only:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File .\tests\Invoke-VenvTestMatrix.ps1 -Aliases pwsh-7.4.13
```

Include online install tests (`Install-PSResource` / `Install-Module`):

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File .\tests\Invoke-VenvTestMatrix.ps1 -EnableOnlineTests
```

Online mode details:

- The tests do not modify PSGallery trust policy.
- `Install-PSResource` uses `-TrustRepository -Quiet`.
- `Install-Module` uses `-Force -AcceptLicense -Confirm:$false`.
- The install checks use `Yayaml` to keep downloads and execution lightweight.

Useful flags:

- `-KeepVenv` keeps the temporary venvs for troubleshooting.
- `-ContinueOnFailure` keeps running after a failed alias.

Pester must be available in the host PowerShell session.
