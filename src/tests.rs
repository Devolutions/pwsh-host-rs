#[cfg(test)]
mod pwsh {
    use crate::bindings::{PowerShell};

    #[test]
    fn load_pwsh_sdk_invoke_api() {
        let pwsh = PowerShell::new().unwrap();

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
		pwsh.invoke(true);

		let cmds_txt = pwsh.export_to_string("UtilityCommands");
		let pwsh_cmds: Vec<&str> = cmds_txt.lines().collect();

		println!("\nCommands (text):");
		for pwsh_cmd in &pwsh_cmds {
			println!("{}", &pwsh_cmd);
		}

		assert_eq!(pwsh_cmds.len(), 7);
		assert_eq!(pwsh_cmds.get(0), Some(&"Compare-Object"));
		assert_eq!(pwsh_cmds.get(1), Some(&"Group-Object"));
		assert_eq!(pwsh_cmds.get(2), Some(&"Measure-Object"));

		// Get-Date -UnixTimeSeconds 1577836800 | Set-Variable -Name Date
		pwsh.add_command("Get-Date");
		pwsh.add_parameter_long("-UnixTimeSeconds", 1577836800);
		pwsh.add_command("Set-Variable");
		pwsh.add_parameter_string("-Name", "Date");
		pwsh.add_statement();
		pwsh.invoke(true);

		let date_json = pwsh.export_to_json("Date");
		println!("\nDate (JSON):\n{}", &date_json);
		assert_eq!(&date_json, "\"2019-12-31T19:00:00-05:00\"");

		// Get-Verb -Verb Test | Set-Variable -Name Verb
		pwsh.add_script("Get-Verb -Verb Test");
		pwsh.add_command("Set-Variable");
		pwsh.add_parameter_string("-Name", "Verb");
		pwsh.add_statement();
		pwsh.invoke(true);

		let verb_xml = pwsh.export_to_xml("Verb");
		println!("\nVerb (XML):\n{}", &verb_xml);
		assert!(verb_xml.starts_with("<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"));
		assert!(verb_xml.find("<T>System.Management.Automation.VerbInfo</T>").is_some());
		assert!(verb_xml.find("<ToString>System.Management.Automation.VerbInfo</ToString>").is_some());
    }
}
