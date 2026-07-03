use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;
use std::sync::{LockResult, Mutex, MutexGuard};

use rmcp::model::{
    CallToolRequestParams, CallToolResult, Implementation, JsonObject, ListToolsResult, PaginatedRequestParams,
    ServerCapabilities, ServerInfo, Tool,
};
use rmcp::service::RequestContext;
use rmcp::{ErrorData, RoleServer, ServerHandler, ServiceExt};
use serde::Deserialize;
use serde_json::{json, Map, Value};

const EXPORTED_RESULT_VARIABLE: &str = "__multiPwshMcpJson";
const COMMAND_METADATA_SCRIPT: &str = r#"
param([string]$CommandName)
$ErrorActionPreference = 'Stop'
try {
    $command = Get-Command -Name $CommandName -ErrorAction Stop | Select-Object -First 1
    $description = $null
    try {
        $help = Get-Help -Name $command.Name -ErrorAction Stop
        if ($help.Synopsis) {
            $description = [string]$help.Synopsis
        }
    }
    catch {
    }

    if (-not $description) {
        $description = [string]$command.Definition
    }

    $parameters = @(
        foreach ($entry in $command.Parameters.GetEnumerator() | Sort-Object Key) {
            $parameter = $entry.Value
            $parameterAttribute = $parameter.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } | Select-Object -First 1
            [pscustomobject]@{
                name = $parameter.Name
                aliases = @($parameter.Aliases)
                isMandatory = [bool]($parameterAttribute -and $parameterAttribute.Mandatory)
                isSwitch = $parameter.ParameterType.FullName -eq 'System.Management.Automation.SwitchParameter'
                type = $parameter.ParameterType.FullName
            }
        }
    )

    $__multiPwshMcpJson = [pscustomobject]@{
        ok = $true
        data = [pscustomobject]@{
            name = $command.Name
            description = $description
            parameters = $parameters
        }
    } | ConvertTo-Json -Depth 20 -Compress
}
catch {
    $__multiPwshMcpJson = [pscustomobject]@{
        ok = $false
        message = (($_ | Out-String).TrimEnd("`r", "`n"))
    } | ConvertTo-Json -Depth 20 -Compress
}
"#;

const COMMAND_INVOKE_SCRIPT: &str = r#"
param([string]$CommandName, [string]$ArgumentsJson)
$ErrorActionPreference = 'Stop'
try {
    $command = Get-Command -Name $CommandName -ErrorAction Stop | Select-Object -First 1
    $arguments = @{}
    if (-not [string]::IsNullOrWhiteSpace($ArgumentsJson)) {
        $parsed = ConvertFrom-Json -InputObject $ArgumentsJson -AsHashtable -Depth 20
        if ($parsed) {
            foreach ($entry in $parsed.GetEnumerator()) {
                if ($null -eq $entry.Value) {
                    continue
                }

                if ($entry.Value -is [System.Array] -and $entry.Value.Count -eq 0) {
                    continue
                }

                $parameter = $command.Parameters[$entry.Key]
                if (
                    $parameter -and
                    $parameter.ParameterType.FullName -eq 'System.Management.Automation.SwitchParameter' -and
                    $entry.Value -is [bool] -and
                    -not $entry.Value
                ) {
                    continue
                }

                $arguments[$entry.Key] = $entry.Value
            }
        }
    }

    $output = (& $CommandName @arguments | Out-String).TrimEnd("`r", "`n")
    $__multiPwshMcpJson = [pscustomobject]@{
        ok = $true
        output = $output
    } | ConvertTo-Json -Depth 20 -Compress
}
catch {
    $__multiPwshMcpJson = [pscustomobject]@{
        ok = $false
        message = (($_ | Out-String).TrimEnd("`r", "`n"))
    } | ConvertTo-Json -Depth 20 -Compress
}
"#;

#[derive(Clone)]
pub struct HostMcpServer {
    state: Arc<ServerState>,
}

struct ServerState {
    powershell: SharedPowerShell,
    tools: Vec<Tool>,
    tools_by_name: HashMap<String, ExposedTool>,
}

#[derive(Clone)]
struct SharedPowerShell {
    inner: Arc<PowerShellLock>,
}

struct PowerShellLock(Mutex<pwsh_host::PowerShell>);

// SAFETY: run_mcp_server drives the MCP host on a current-thread runtime, and
// the mutex serializes all access across cloned handlers.
unsafe impl Send for PowerShellLock {}
unsafe impl Sync for PowerShellLock {}

impl PowerShellLock {
    fn new(powershell: pwsh_host::PowerShell) -> Self {
        Self(Mutex::new(powershell))
    }

    fn lock(&self) -> LockResult<MutexGuard<'_, pwsh_host::PowerShell>> {
        self.0.lock()
    }
}

#[derive(Clone, Debug)]
struct ExposedTool {
    command_name: String,
    tool: Tool,
}

#[derive(Debug, Deserialize)]
struct ScriptResponse<T> {
    ok: bool,
    data: Option<T>,
    message: Option<String>,
    output: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
struct CommandMetadata {
    name: String,
    description: String,
    parameters: Vec<CommandParameterMetadata>,
}

#[derive(Clone, Debug, Deserialize)]
struct CommandParameterMetadata {
    name: String,
    aliases: Vec<String>,
    #[serde(rename = "isMandatory")]
    is_mandatory: bool,
    #[serde(rename = "isSwitch")]
    is_switch: bool,
    #[serde(rename = "type")]
    type_name: String,
}

pub fn run_stdio_mcp_server_for_pwsh_dir(
    pwsh_dir: &Path,
    commands: &[String],
) -> Result<i32, Box<dyn std::error::Error>> {
    let server = HostMcpServer::new(pwsh_dir, commands)?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_io()
        .enable_time()
        .build()?;

    runtime.block_on(async move {
        server.serve(rmcp::transport::stdio()).await?.waiting().await?;
        Ok::<(), Box<dyn std::error::Error>>(())
    })?;

    Ok(0)
}

impl HostMcpServer {
    fn new(pwsh_dir: &Path, commands: &[String]) -> Result<Self, Box<dyn std::error::Error>> {
        let powershell = SharedPowerShell::new(pwsh_dir)?;
        let mut tools = Vec::with_capacity(commands.len());
        let mut tools_by_name = HashMap::with_capacity(commands.len());

        for command in commands {
            let metadata = query_command_metadata(&powershell, command)?;
            let tool_name = normalize_tool_name(&metadata.name);

            if tools_by_name.contains_key(&tool_name) {
                return Err(format!(
                    "multiple PowerShell commands map to the same MCP tool name '{}'; adjust -McpCommands to avoid collisions",
                    tool_name
                )
                .into());
            }

            let tool = Tool::new(
                tool_name.clone(),
                build_tool_description(&metadata),
                build_input_schema(&metadata.parameters),
            );

            tools.push(tool.clone());
            tools_by_name.insert(
                tool_name,
                ExposedTool {
                    command_name: metadata.name,
                    tool,
                },
            );
        }

        Ok(Self {
            state: Arc::new(ServerState {
                powershell,
                tools,
                tools_by_name,
            }),
        })
    }
}

impl SharedPowerShell {
    fn new(pwsh_dir: &Path) -> Result<Self, Box<dyn std::error::Error>> {
        Ok(Self {
            inner: Arc::new(PowerShellLock::new(pwsh_host::PowerShell::new_for_pwsh_dir(pwsh_dir)?)),
        })
    }

    fn invoke_script(&self, script: &str, arguments: &[&str]) -> Result<String, String> {
        let powershell = self
            .inner
            .lock()
            .map_err(|_| "PowerShell MCP host lock was poisoned".to_string())?;

        powershell.add_script(script);
        for argument in arguments {
            powershell.add_argument_string(argument);
        }
        powershell.invoke(true);

        Ok(powershell.export_to_string(EXPORTED_RESULT_VARIABLE))
    }
}

impl ServerHandler for HostMcpServer {
    fn get_info(&self) -> ServerInfo {
        ServerInfo::new(ServerCapabilities::builder().enable_tools().build())
            .with_server_info(
                Implementation::new("multi-pwsh", env!("CARGO_PKG_VERSION"))
                    .with_title("multi-pwsh")
                    .with_description("Expose selected PowerShell commands over MCP using a versioned multi-pwsh host."),
            )
            .with_instructions("Each tool forwards to the selected PowerShell version and honors multi-pwsh virtual environment startup hook settings.")
    }

    fn get_tool(&self, name: &str) -> Option<Tool> {
        self.state.tools_by_name.get(name).map(|tool| tool.tool.clone())
    }

    async fn list_tools(
        &self,
        _request: Option<PaginatedRequestParams>,
        _context: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, ErrorData> {
        Ok(ListToolsResult::with_all_items(self.state.tools.clone()))
    }

    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _context: RequestContext<RoleServer>,
    ) -> Result<CallToolResult, ErrorData> {
        let Some(tool) = self.state.tools_by_name.get(request.name.as_ref()) else {
            return Ok(CallToolResult::structured_error(json!({
                "message": format!("unknown tool '{}'", request.name),
            })));
        };

        let arguments_json = serde_json::to_string(&request.arguments).unwrap_or_else(|_| "null".to_string());
        match invoke_command(&self.state.powershell, &tool.command_name, &arguments_json) {
            Ok(output) => Ok(CallToolResult::structured(json!({
                "command": tool.command_name,
                "output": output,
            }))),
            Err(error) => Ok(CallToolResult::structured_error(json!({
                "command": tool.command_name,
                "message": error,
            }))),
        }
    }
}

fn query_command_metadata(
    powershell: &SharedPowerShell,
    command_name: &str,
) -> Result<CommandMetadata, Box<dyn std::error::Error>> {
    let response: ScriptResponse<CommandMetadata> =
        invoke_script(powershell, COMMAND_METADATA_SCRIPT, &[command_name])?;
    if !response.ok {
        return Err(response
            .message
            .unwrap_or_else(|| format!("failed to describe PowerShell command '{}'", command_name))
            .into());
    }

    response
        .data
        .ok_or_else(|| format!("PowerShell command '{}' returned no metadata", command_name).into())
}

fn invoke_command(powershell: &SharedPowerShell, command_name: &str, arguments_json: &str) -> Result<String, String> {
    let response: ScriptResponse<Value> =
        invoke_script(powershell, COMMAND_INVOKE_SCRIPT, &[command_name, arguments_json])
            .map_err(|error| error.to_string())?;

    if response.ok {
        Ok(response.output.unwrap_or_default())
    } else {
        Err(response
            .message
            .unwrap_or_else(|| format!("PowerShell command '{}' failed", command_name)))
    }
}

fn invoke_script<T>(
    powershell: &SharedPowerShell,
    script: &'static str,
    arguments: &[&str],
) -> Result<T, Box<dyn std::error::Error>>
where
    T: for<'de> Deserialize<'de>,
{
    let raw_json = powershell
        .invoke_script(script, arguments)
        .map_err(|error| -> Box<dyn std::error::Error> { error.into() })?;
    serde_json::from_str(&raw_json).map_err(|error| {
        format!(
            "failed to parse PowerShell MCP bridge payload '{}': {}",
            raw_json, error
        )
        .into()
    })
}

fn build_tool_description(metadata: &CommandMetadata) -> String {
    let description = metadata.description.trim();
    if description.is_empty() {
        format!("Invoke PowerShell command {}", metadata.name)
    } else {
        description.to_string()
    }
}

fn build_input_schema(parameters: &[CommandParameterMetadata]) -> JsonObject {
    let mut properties = Map::with_capacity(parameters.len());
    let mut required = Vec::new();

    for parameter in parameters {
        properties.insert(parameter.name.clone(), build_parameter_schema(parameter));
        if parameter.is_mandatory {
            required.push(Value::String(parameter.name.clone()));
        }
    }

    let mut schema = Map::new();
    schema.insert("type".to_string(), Value::String("object".to_string()));
    schema.insert("properties".to_string(), Value::Object(properties));
    schema.insert("required".to_string(), Value::Array(required));
    schema.insert("additionalProperties".to_string(), Value::Bool(false));
    schema
}

fn build_parameter_schema(parameter: &CommandParameterMetadata) -> Value {
    let mut schema = match normalize_parameter_type(parameter) {
        ParameterSchemaType::Boolean => json!({ "type": "boolean" }),
        ParameterSchemaType::Integer => json!({ "type": "integer" }),
        ParameterSchemaType::Number => json!({ "type": "number" }),
        ParameterSchemaType::Array(item_type) => json!({
            "type": "array",
            "items": build_primitive_schema(item_type),
        }),
        ParameterSchemaType::Object => json!({ "type": "object" }),
        ParameterSchemaType::String => json!({ "type": "string" }),
    };

    if let Value::Object(ref mut map) = schema {
        let mut description = String::new();
        if !parameter.aliases.is_empty() {
            description.push_str("Aliases: ");
            description.push_str(&parameter.aliases.join(", "));
            description.push_str(". ");
        }
        description.push_str("PowerShell type: ");
        description.push_str(&parameter.type_name);
        map.insert("description".to_string(), Value::String(description));
    }

    schema
}

fn build_primitive_schema(schema_type: PrimitiveSchemaType) -> Value {
    match schema_type {
        PrimitiveSchemaType::Boolean => json!({ "type": "boolean" }),
        PrimitiveSchemaType::Integer => json!({ "type": "integer" }),
        PrimitiveSchemaType::Number => json!({ "type": "number" }),
        PrimitiveSchemaType::Object => json!({ "type": "object" }),
        PrimitiveSchemaType::String => json!({ "type": "string" }),
    }
}

fn normalize_tool_name(command_name: &str) -> String {
    let mut name = String::from("powershell_");
    let mut previous_was_separator = true;

    for character in command_name.chars() {
        if character.is_ascii_alphanumeric() {
            name.push(character.to_ascii_lowercase());
            previous_was_separator = false;
            continue;
        }

        if !previous_was_separator {
            name.push('_');
            previous_was_separator = true;
        }
    }

    if name.ends_with('_') {
        name.pop();
    }

    name
}

fn normalize_parameter_type(parameter: &CommandParameterMetadata) -> ParameterSchemaType {
    if parameter.is_switch {
        return ParameterSchemaType::Boolean;
    }

    let type_name = parameter.type_name.as_str();
    if let Some(inner) = type_name.strip_suffix("[]") {
        return ParameterSchemaType::Array(normalize_primitive_schema_type(inner));
    }

    match normalize_primitive_schema_type(type_name) {
        PrimitiveSchemaType::Boolean => ParameterSchemaType::Boolean,
        PrimitiveSchemaType::Integer => ParameterSchemaType::Integer,
        PrimitiveSchemaType::Number => ParameterSchemaType::Number,
        PrimitiveSchemaType::Object => ParameterSchemaType::Object,
        PrimitiveSchemaType::String => ParameterSchemaType::String,
    }
}

fn normalize_primitive_schema_type(type_name: &str) -> PrimitiveSchemaType {
    match type_name {
        "System.Boolean" | "System.Management.Automation.SwitchParameter" => PrimitiveSchemaType::Boolean,
        "System.SByte" | "System.Byte" | "System.Int16" | "System.UInt16" | "System.Int32" | "System.UInt32"
        | "System.Int64" | "System.UInt64" => PrimitiveSchemaType::Integer,
        "System.Single" | "System.Double" | "System.Decimal" => PrimitiveSchemaType::Number,
        "System.Collections.Hashtable" | "System.Object" => PrimitiveSchemaType::Object,
        _ => PrimitiveSchemaType::String,
    }
}

#[derive(Clone, Copy, Debug)]
enum PrimitiveSchemaType {
    Boolean,
    Integer,
    Number,
    Object,
    String,
}

#[derive(Clone, Copy, Debug)]
enum ParameterSchemaType {
    Boolean,
    Integer,
    Number,
    Array(PrimitiveSchemaType),
    Object,
    String,
}
