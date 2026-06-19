# Devolutions.MultiPwsh.Cli

`Devolutions.MultiPwsh.Cli` ships RID-specific `multi-pwsh` native binaries for .NET projects. It also includes opt-in AppHost MSBuild targets for projects that need to copy the same binary as a PowerShell replacement apphost.

The normal CLI payload is copied under `runtimes/<rid>/native/` for build and publish outputs. AppHost mode is inert by default; set `MultiPwshAppHostEnabled=true` to copy the selected RID binary as `multi-pwsh`, `pwsh`, or another explicit file name.

```xml
<ItemGroup>
  <PackageReference Include="Devolutions.MultiPwsh.Cli" Version="0.13.0" PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <MultiPwshAppHostEnabled>true</MultiPwshAppHostEnabled>
  <MultiPwshAppHostOutputBaseName>pwsh</MultiPwshAppHostOutputBaseName>
</PropertyGroup>
```

Downstream SDK packages can copy the binary as `pwsh` or `pwsh.exe` beside their own `pwsh.dll` and `pwsh.runtimeconfig.json`. In that layout, `multi-pwsh` runs the adjacent payload directly instead of resolving `pwsh` from PATH.
