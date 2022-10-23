#[cfg(test)]
mod pwsh {
    use crate::ipwsh::IPowerShell;
	use std::ffi::CString;

    #[test]
    fn load_pwsh_sdk_invoke_api() {
        let pwsh = IPowerShell::new();
        assert!(pwsh.is_ok());
        let pwsh = pwsh.unwrap();

		let handle = pwsh.create();
        let mut script = CString::new("$TempPath = [System.IO.Path]::GetTempPath();").unwrap();
        pwsh.add_script(handle, script);
        script = CString::new("Set-Content -Path $(Join-Path $TempPath pwsh-date.txt) -Value \"Microsoft.PowerShell.SDK: $(Get-Date)\";").unwrap();
        pwsh.add_script(handle, script);
        pwsh.invoke(handle);
        let mut output_file = std::env::temp_dir();
        output_file.push("pwsh-date.txt");
        let pwsh_date = std::fs::read_to_string(output_file.as_path()).unwrap();
        println!("{}", &pwsh_date);
    }
}
