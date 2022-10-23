#[cfg(test)]
mod pwsh {
    use crate::bindings::{PowerShell};

    #[test]
    fn load_pwsh_sdk_invoke_api() {
        let pwsh = PowerShell::new().unwrap();
		
		let mut output_file = std::env::temp_dir();
        output_file.push("pwsh-cmds.txt");

		pwsh.add_command("Get-Command");
		pwsh.add_parameter_string("-CommandType", "Cmdlet");
		pwsh.add_parameter_string("-Name", "Test-*");
		pwsh.add_command("Out-File");
		pwsh.add_parameter_string("-FilePath", output_file.to_str().unwrap());
		pwsh.invoke();
    }
}
