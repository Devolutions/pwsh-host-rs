#[cfg(test)]
mod pwsh {
    use crate::bindings::{PowerShell};

    #[test]
    fn load_pwsh_sdk_invoke_api() {
        let pwsh = PowerShell::new().unwrap();
		
		let mut output_file = std::env::temp_dir();
        output_file.push("pwsh-cmds.txt");
		let output_file_str = output_file.to_str().unwrap();

		// Get-Command -CommandType Cmdlet -Name *-Object -Module Microsoft.PowerShell.Utility |
		// Select-Object -ExpandProperty Name | Set-Variable -Name UtilityCommands
		pwsh.add_command("Get-Command");
		pwsh.add_parameter_string("-CommandType", "Cmdlet");
		pwsh.add_parameter_string("-Name", "*-Object");
		pwsh.add_parameter_string("-Module", "Microsoft.PowerShell.Utility");
		pwsh.add_command("Select-Object");
		pwsh.add_parameter_string("-ExpandProperty", "Name");
		pwsh.add_command("Set-Variable");
		pwsh.add_parameter_string("-Name", "UtilityCommands");
		pwsh.add_statement();
		pwsh.invoke();

		// Get-Variable -Name UtilityCommands -ValueOnly | Set-Content -Path '/tmp/pwsh-cmds.txt'
		pwsh.add_command("Get-Variable");
		pwsh.add_parameter_string("-Name", "UtilityCommands");
		pwsh.add_argument_string("-ValueOnly");
		pwsh.add_statement();
		pwsh.add_command("Set-Content");
		pwsh.add_parameter_string("-Path", &output_file_str);
		pwsh.add_argument_string("-Force");

		let pwsh_cmds = std::fs::read_to_string(output_file.as_path()).unwrap();
		println!("{}", pwsh_cmds);
    }
}
