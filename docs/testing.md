# Testing

## Bootstrap installer tests

Run the mocked Windows bootstrap installer harness:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File .\tests\Invoke-BootstrapInstallerTest.ps1
```

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

## CLI NuGet AppHost smoke tests

Build the native NuGet package first, then run the AppHost smoke harness:

```powershell
pwsh -NoLogo -NoProfile -File .\scripts\Build-NativeNuGetPackages.ps1 -RuntimeIdentifiers win-x64 -Clean
pwsh -NoLogo -NoProfile -NonInteractive -File .\tests\Invoke-AppHostNuGetPackageSmokeTest.ps1 -RuntimeIdentifier win-x64
```

The smoke harness creates a temporary SDK-style sample project, restores `Devolutions.MultiPwsh.Cli` from the local package source, validates AppHost build/publish output copying, and runs a renamed local apphost beside `pwsh.dll` and `pwsh.runtimeconfig.json` when a PowerShell payload is available.
