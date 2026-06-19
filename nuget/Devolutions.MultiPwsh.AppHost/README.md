# Devolutions.MultiPwsh.AppHost

`Devolutions.MultiPwsh.AppHost` ships RID-specific `multi-pwsh` native binaries and opt-in MSBuild targets for projects that need a reusable PowerShell replacement apphost.

The package is inert by default. Set `MultiPwshAppHostEnabled=true` to copy the selected RID binary to build and publish outputs.

```xml
<ItemGroup>
  <PackageReference Include="Devolutions.MultiPwsh.AppHost" Version="0.13.0" PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <MultiPwshAppHostEnabled>true</MultiPwshAppHostEnabled>
  <MultiPwshAppHostOutputBaseName>pwsh</MultiPwshAppHostOutputBaseName>
</PropertyGroup>
```

Downstream SDK packages can copy the binary as `pwsh` or `pwsh.exe` beside their own `pwsh.dll` and `pwsh.runtimeconfig.json`. In that layout, `multi-pwsh` runs the adjacent payload directly instead of resolving `pwsh` from PATH.
