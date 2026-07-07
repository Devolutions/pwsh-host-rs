fn main() {
    println!("cargo:rerun-if-env-changed=CARGO_PKG_VERSION");
    println!("cargo:rerun-if-changed=assets/powershell.ico");

    if std::env::var_os("CARGO_CFG_WINDOWS").is_none() {
        return;
    }

    let version = std::env::var("CARGO_PKG_VERSION").expect("missing CARGO_PKG_VERSION");

    let mut resource = winresource::WindowsResource::new();
    resource
        .set("FileDescription", "Install and update side-by-side PowerShell versions")
        .set("ProductName", "multi-pwsh")
        .set("InternalName", "multi-pwsh")
        .set("CompanyName", "Devolutions Inc")
        .set("LegalCopyright", "Copyright 2021-2026 Devolutions Inc.")
        .set("OriginalFilename", "multi-pwsh.exe")
        .set("FileVersion", &version)
        .set("ProductVersion", &version);
    resource.set_icon("assets/powershell.ico");
    resource.set_manifest(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>"#,
    );

    resource.compile().expect("failed to compile Windows resources");
}
