
#include <stdio.h>
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>

#include <pwsh-host.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <dlfcn.h>
#include <limits.h>
#endif

#include <nethost.h>

#include <coreclrhost.h>
#include <coreclr_delegates.h>
#include <hostfxr.h>

#ifdef _WIN32
#define PATH_SEPARATOR_CHR  '\\'
#define PATH_SEPARATOR_STR  "\\"
#define HOSTFXR_LIB_NAME "hostfxr.dll"
#define CORECLR_LIB_NAME "coreclr.dll"
#else
#define PATH_SEPARATOR_CHR  '/'
#define PATH_SEPARATOR_STR  "/"
#define HOSTFXR_LIB_NAME "libhostfxr.so"
#define CORECLR_LIB_NAME "libcoreclr.so"
#endif

#define HOSTFXR_MAX_PATH    1024

static char g_PWSH_BASE_PATH[HOSTFXR_MAX_PATH];

int get_env(const char* name, char* value, int cch)
{
	int status;

	int len;
	char* env;

	env = getenv(name);

	if (!env)
		return -1;

	len = (int) strlen(name);

	if (len < 1)
		return -1;

	status = len + 1;

	if (value && (cch > 0))
	{
		if (cch >= (len + 1))
		{
			strncpy(value, env, cch);
			value[cch - 1] = '\0';
			status = len;
		}
	}

	return status;
}

static void* load_library(const char* path)
{
#ifdef _WIN32
    HMODULE hModule = LoadLibraryA(path);
    return (void*) hModule;
#else
    void* handle = dlopen(path, RTLD_LAZY | RTLD_LOCAL);
    return handle;
#endif
}

static void* get_proc_address(void* handle, const char* name)
{
#ifdef _WIN32
    HMODULE hModule = (HMODULE) handle;
    void* symbol = GetProcAddress(hModule, name);
    return symbol;
#else
    void* symbol = dlsym(handle, name);
    return symbol;
#endif
}

static uint8_t* load_file(const char* filename, size_t* size)
{
	FILE* fp = NULL;
	uint8_t* data = NULL;

	if (!filename || !size)
		return NULL;

	*size = 0;

	fp = fopen(filename, "rb");

	if (!fp)
		return NULL;

	fseek(fp, 0, SEEK_END);
	*size = ftell(fp);
	fseek(fp, 0, SEEK_SET);

	data = malloc(*size + 1);

	if (!data)
		goto exit;

	if (fread(data, 1, *size, fp) != *size)
	{
		free(data);
		data = NULL;
		*size = 0;
        goto exit;
	}

    data[*size] = '\0';

exit:
	fclose(fp);
	return data;
}

#ifndef _WIN32
extern void pthread_create();

void linker_dummy()
{
    // force linking pthread library
    pthread_create();
}
#endif

#ifdef _WIN32
WCHAR* convert_string_to_utf16(const char* lpMultiByteStr)
{
    if (!lpMultiByteStr)
        return NULL;

    int cchWideChar = MultiByteToWideChar(CP_UTF8, 0, lpMultiByteStr, -1, NULL, 0);
    WCHAR* lpWideCharStr = (LPWSTR) calloc(cchWideChar + 1, sizeof(WCHAR));
    MultiByteToWideChar(CP_UTF8, 0, lpMultiByteStr, -1, lpWideCharStr, cchWideChar);

    return lpWideCharStr;
}
#endif

struct coreclr_context
{
    coreclr_initialize_fn initialize;
    coreclr_shutdown_fn shutdown;
    coreclr_shutdown_2_fn shutdown_2;
    coreclr_create_delegate_fn create_delegate;
    coreclr_execute_assembly_fn execute_assembly;
};
typedef struct coreclr_context CORECLR_CONTEXT;

static CORECLR_CONTEXT g_CORECLR_CONTEXT;

bool load_coreclr(CORECLR_CONTEXT* coreclr, const char* coreclr_path)
{
    void* lib_handle = load_library(coreclr_path);

    memset(coreclr, 0, sizeof(CORECLR_CONTEXT));

    if (!lib_handle) {
        printf("could not load %s\n", coreclr_path);
    }

    coreclr->initialize = (coreclr_initialize_fn) get_proc_address(lib_handle, "coreclr_initialize");
    coreclr->shutdown = (coreclr_shutdown_fn) get_proc_address(lib_handle, "coreclr_shutdown");
    coreclr->shutdown_2 = (coreclr_shutdown_2_fn) get_proc_address(lib_handle, "coreclr_shutdown_2");
    coreclr->create_delegate = (coreclr_create_delegate_fn) get_proc_address(lib_handle, "coreclr_create_delegate");
    coreclr->execute_assembly = (coreclr_execute_assembly_fn) get_proc_address(lib_handle, "coreclr_execute_assembly");

    if (!coreclr->initialize || !coreclr->shutdown || !coreclr->shutdown_2 ||
        !coreclr->create_delegate || !coreclr->execute_assembly)
    {
        printf("could not load CoreCLR functions\n");
        return false;
    }

    return true;
}

struct hostfxr_context
{
    hostfxr_initialize_for_dotnet_command_line_fn initialize_for_dotnet_command_line;
    hostfxr_initialize_for_runtime_config_fn initialize_for_runtime_config;
    hostfxr_get_runtime_property_value_fn get_runtime_property_value;
    hostfxr_set_runtime_property_value_fn set_runtime_property_value;
    hostfxr_get_runtime_properties_fn get_runtime_properties;
    hostfxr_run_app_fn run_app;
    hostfxr_get_runtime_delegate_fn get_runtime_delegate;
    hostfxr_close_fn close;

    load_assembly_and_get_function_pointer_fn load_assembly_and_get_function_pointer;
    get_function_pointer_fn get_function_pointer;
    hostfxr_handle context_handle;
};
typedef struct hostfxr_context HOSTFXR_CONTEXT;

static HOSTFXR_CONTEXT g_HOSTFXR_CONTEXT;

struct hostfxr_init_params
{
    size_t size;
    const char* host_path;
    const char* dotnet_root;
};
typedef struct hostfxr_init_params HOSTFXR_INIT_PARAMS;

static int32_t hostfxr_initialize_for_dotnet_command_line(int argc, const char** argv,
    const HOSTFXR_INIT_PARAMS* params, hostfxr_handle* host_context_handle)
{
    HOSTFXR_CONTEXT* hostfxr = &g_HOSTFXR_CONTEXT;
#ifdef _WIN32
    int32_t status;
    int32_t index;
    WCHAR** argv_w = NULL;
    struct hostfxr_initialize_parameters params_w;
    struct hostfxr_initialize_parameters* p_params = NULL;

    if (params) {
        params_w.size = sizeof(params_w);
        params_w.host_path = convert_string_to_utf16(params->host_path);
        params_w.dotnet_root = convert_string_to_utf16(params->dotnet_root);
        p_params = &params_w;
    }

    argv_w = (WCHAR**) calloc(argc, sizeof(WCHAR*));

    if (!argv_w)
            return -1;

    for (index = 0; index < argc; index++) {
        argv_w[index] = convert_string_to_utf16(argv[index]);
    }

    status = hostfxr->initialize_for_dotnet_command_line(argc, argv_w, p_params, host_context_handle);

    for (index = 0; index < argc; index++) {
        free(argv_w[index]);
    }
    free(argv_w);

    if (params) {
        free((void*) params_w.host_path);
        free((void*) params_w.dotnet_root);
    }

    return status;
#else
    return hostfxr->initialize_for_dotnet_command_line(argc, argv,
        (const struct hostfxr_initialize_parameters*) params, host_context_handle);
#endif
}

int32_t hostfxr_initialize_for_runtime_config(const char* runtime_config_path,
    const HOSTFXR_INIT_PARAMS* params, hostfxr_handle* host_context_handle)
{
    HOSTFXR_CONTEXT* hostfxr = &g_HOSTFXR_CONTEXT;
#ifdef _WIN32
    int32_t status;
    WCHAR* runtime_config_path_w = NULL;
    struct hostfxr_initialize_parameters params_w;
    struct hostfxr_initialize_parameters* p_params = NULL;

    runtime_config_path_w = convert_string_to_utf16(runtime_config_path);

    if (params) {
        params_w.size = sizeof(params_w);
        params_w.host_path = convert_string_to_utf16(params->host_path);
        params_w.dotnet_root = convert_string_to_utf16(params->dotnet_root);
        p_params = &params_w;
    }

    status = hostfxr->initialize_for_runtime_config(runtime_config_path_w, p_params, host_context_handle);

    if (params) {
        free((void*) params_w.host_path);
        free((void*) params_w.dotnet_root);
    }

    free(runtime_config_path_w);

    return status;
#else
    return hostfxr->initialize_for_runtime_config(runtime_config_path,
        (const struct hostfxr_initialize_parameters*) params, host_context_handle);
#endif
}

#define UNMANAGEDCALLERSONLY_METHOD_A ((const char*)-1)

int32_t hostfxr_load_assembly_and_get_function_pointer(const char* assembly_path,
    const char* type_name, const char* method_name, const char* delegate_type_name,
    void* reserved, void** delegate)
{
    HOSTFXR_CONTEXT* hostfxr = &g_HOSTFXR_CONTEXT;
#ifdef _WIN32
    int32_t status;
    const WCHAR* assembly_path_w;
    const WCHAR* type_name_w;
    const WCHAR* method_name_w;
    const WCHAR* delegate_type_name_w;

    assembly_path_w = convert_string_to_utf16(assembly_path);
    type_name_w = convert_string_to_utf16(type_name);
    method_name_w = convert_string_to_utf16(method_name);

    if (delegate_type_name != UNMANAGEDCALLERSONLY_METHOD_A) {
            delegate_type_name_w = convert_string_to_utf16(delegate_type_name);
    }
    else {
            delegate_type_name_w = UNMANAGEDCALLERSONLY_METHOD;
    }

    status = hostfxr->load_assembly_and_get_function_pointer(assembly_path_w,
        type_name_w, method_name_w, delegate_type_name_w,
        reserved, delegate);

    free((void*) assembly_path_w);
    free((void*) type_name_w);
    free((void*) method_name_w);

    if (delegate_type_name != UNMANAGEDCALLERSONLY_METHOD_A)
        free((void*) delegate_type_name_w);

    return status;
#else
    return hostfxr->load_assembly_and_get_function_pointer(assembly_path,
        type_name, method_name, delegate_type_name,
        reserved, delegate);
#endif
}

int32_t hostfxr_get_function_pointer(const char* type_name,
    const char* method_name, const char* delegate_type_name,
    void* load_context, void* reserved, void** delegate)
{
    HOSTFXR_CONTEXT* hostfxr = &g_HOSTFXR_CONTEXT;
#ifdef _WIN32
    int32_t status;
    const WCHAR* type_name_w;
    const WCHAR* method_name_w;
    const WCHAR* delegate_type_name_w;

    type_name_w = convert_string_to_utf16(type_name);
    method_name_w = convert_string_to_utf16(method_name);

    if (delegate_type_name != UNMANAGEDCALLERSONLY_METHOD_A) {
            delegate_type_name_w = convert_string_to_utf16(delegate_type_name);
    }
    else {
            delegate_type_name_w = UNMANAGEDCALLERSONLY_METHOD;
    }

    status = hostfxr->get_function_pointer(type_name_w,
        method_name_w, delegate_type_name_w,
        load_context, reserved, delegate);

    free((void*) type_name_w);
    free((void*) method_name_w);

    if (delegate_type_name != UNMANAGEDCALLERSONLY_METHOD_A)
        free((void*) delegate_type_name_w);

    return status;
#else
    return hostfxr->get_function_pointer(type_name,
        method_name, delegate_type_name,
        load_context, reserved, delegate);
#endif
}

typedef int (CORECLR_DELEGATE_CALLTYPE *get_function_pointer_fn)(
    const char_t *type_name          /* Assembly qualified type name */,
    const char_t *method_name        /* Public static method name compatible with delegateType */,
    const char_t *delegate_type_name /* Assembly qualified delegate type name or null,
                                        or UNMANAGEDCALLERSONLY_METHOD if the method is marked with
                                        the UnmanagedCallersOnlyAttribute. */,
    void         *load_context       /* Extensibility parameter (currently unused and must be 0) */,
    void         *reserved           /* Extensibility parameter (currently unused and must be 0) */,
    /*out*/ void **delegate          /* Pointer where to store the function pointer result */);

bool load_hostfxr(HOSTFXR_CONTEXT* hostfxr, const char* hostfxr_path)
{
    void* lib_handle = load_library(hostfxr_path);

    memset(hostfxr, 0, sizeof(HOSTFXR_CONTEXT));

    if (!lib_handle) {
        printf("could not load %s\n", hostfxr_path);
    }

    hostfxr->initialize_for_dotnet_command_line = (hostfxr_initialize_for_dotnet_command_line_fn)
        get_proc_address(lib_handle, "hostfxr_initialize_for_dotnet_command_line");
    hostfxr->initialize_for_runtime_config = (hostfxr_initialize_for_runtime_config_fn)
        get_proc_address(lib_handle, "hostfxr_initialize_for_runtime_config");
    hostfxr->get_runtime_property_value = (hostfxr_get_runtime_property_value_fn)
        get_proc_address(lib_handle, "hostfxr_get_runtime_property_value");
    hostfxr->set_runtime_property_value = (hostfxr_set_runtime_property_value_fn)
        get_proc_address(lib_handle, "hostfxr_set_runtime_property_value");
    hostfxr->get_runtime_properties = (hostfxr_get_runtime_properties_fn)
        get_proc_address(lib_handle, "hostfxr_get_runtime_properties");
    hostfxr->run_app = (hostfxr_run_app_fn)
        get_proc_address(lib_handle, "hostfxr_run_app");
    hostfxr->get_runtime_delegate = (hostfxr_get_runtime_delegate_fn)
        get_proc_address(lib_handle, "hostfxr_get_runtime_delegate");
    hostfxr->close = (hostfxr_close_fn)
        get_proc_address(lib_handle, "hostfxr_close");

    return true;
}

bool load_runtime(HOSTFXR_CONTEXT* hostfxr, const char* config_path)
{
    hostfxr_handle ctx = NULL;
    load_assembly_and_get_function_pointer_fn load_assembly_and_get_function_pointer = NULL;

    int rc = hostfxr_initialize_for_runtime_config(config_path, NULL, &ctx);

    if ((rc != 0) || (ctx == NULL)) {
        printf("initialize_for_runtime_config(%s) failure\n", config_path);
        return false;
    }

    rc = hostfxr->get_runtime_delegate(ctx,
        hdt_load_assembly_and_get_function_pointer,
        (void**) &load_assembly_and_get_function_pointer);

    if ((rc != 0) || (NULL == load_assembly_and_get_function_pointer)) {
        printf("get_runtime_delegate failure\n");
        return false;
    }

    hostfxr->close(ctx);

    hostfxr->load_assembly_and_get_function_pointer = load_assembly_and_get_function_pointer;

    return true;
}

typedef int32_t (CORECLR_DELEGATE_CALLTYPE * fnLoadAssemblyFromNativeMemory)(uint8_t* bytes, int32_t size);

static fnLoadAssemblyFromNativeMemory g_LoadAssemblyFromNativeMemory = NULL;

bool load_assembly_helper(HOSTFXR_CONTEXT* hostfxr, const char* helper_path, const char* type_name)
{
    int rc;

    rc =  hostfxr_load_assembly_and_get_function_pointer(helper_path,
        type_name, "LoadAssemblyFromNativeMemory",
        UNMANAGEDCALLERSONLY_METHOD_A, NULL, (void**) &g_LoadAssemblyFromNativeMemory);

    if (rc != 0) {
        printf("load_assembly_and_get_function_pointer(LoadAssemblyFromNativeMemory): 0x%08X\n", rc);
        return false;
    }

    return true;
}

typedef void* hPowerShell;
typedef hPowerShell (CORECLR_DELEGATE_CALLTYPE * fnPowerShell_Create)(void);
typedef void (CORECLR_DELEGATE_CALLTYPE * fnPowerShell_AddScript)(hPowerShell handle, const char* script);
typedef void (CORECLR_DELEGATE_CALLTYPE * fnPowerShell_Invoke)(hPowerShell handle);

typedef struct
{
    fnPowerShell_Create Create;
    fnPowerShell_AddScript AddScript;
    fnPowerShell_Invoke Invoke;
} iPowerShell;

extern const unsigned int bindings_size;
extern unsigned char bindings_data[];

bool load_pwsh_sdk(HOSTFXR_CONTEXT* hostfxr, iPowerShell* iface)
{
    int rc;
    size_t assembly_size = (size_t) bindings_size;
    uint8_t* assembly_data = (uint8_t*) &bindings_data;

    memset(iface, 0, sizeof(iPowerShell));

    rc = g_LoadAssemblyFromNativeMemory(assembly_data, (int32_t) assembly_size);

    if (rc < 0) {
        printf("LoadAssemblyFromNativeMemory failure: %d\n", rc);
        return false;
    }

    rc = hostfxr_get_function_pointer(
        "NativeHost.Bindings, Bindings", "PowerShell_Create",
        UNMANAGEDCALLERSONLY_METHOD_A, NULL, NULL, (void**) &iface->Create);

    if (rc != 0) {
        printf("get_function_pointer failure: 0x%08X\n", rc);
        return false;
    }

    rc = hostfxr_get_function_pointer(
        "NativeHost.Bindings, Bindings", "PowerShell_AddScript",
        UNMANAGEDCALLERSONLY_METHOD_A, NULL, NULL, (void**) &iface->AddScript);

    rc = hostfxr_get_function_pointer(
        "NativeHost.Bindings, Bindings", "PowerShell_Invoke",
        UNMANAGEDCALLERSONLY_METHOD_A, NULL, NULL, (void**) &iface->Invoke);

    return true;
}

bool call_pwsh_sdk(HOSTFXR_CONTEXT* hostfxr)
{
    iPowerShell iface;

    load_pwsh_sdk(hostfxr, &iface);

    hPowerShell handle = iface.Create();
    iface.AddScript(handle, "$TempPath = [System.IO.Path]::GetTempPath();");
    iface.AddScript(handle, "Set-Content -Path $(Join-Path $TempPath pwsh-date.txt) -Value \"Microsoft.PowerShell.SDK: $(Get-Date)\"");
    iface.Invoke(handle);

    return true;
}

bool load_command(HOSTFXR_CONTEXT* hostfxr, int argc, const char** argv, bool close_handle)
{
    hostfxr_handle ctx = NULL;
    load_assembly_and_get_function_pointer_fn load_assembly_and_get_function_pointer = NULL;
    get_function_pointer_fn get_function_pointer = NULL;

    int rc = hostfxr_initialize_for_dotnet_command_line(argc, argv, NULL, &ctx);

    if ((rc != 0) || (ctx == NULL)) {
        printf("hostfxr->initialize_for_dotnet_command_line() failure: 0x%08X\n", rc);
        return false;
    }

    rc = hostfxr->get_runtime_delegate(ctx,
        hdt_load_assembly_and_get_function_pointer,
        (void**) &load_assembly_and_get_function_pointer);

    if ((rc != 0) || (NULL == load_assembly_and_get_function_pointer)) {
        printf("get_runtime_delegate failure: 0x%08X\n", rc);
        return false;
    }

    rc = hostfxr->get_runtime_delegate(ctx,
        hdt_get_function_pointer,
        (void**) &get_function_pointer);

    if ((rc != 0) || (NULL == get_function_pointer)) {
        printf("get_runtime_delegate failure: 0x%08X\n", rc);
        return false;
    }

    hostfxr->context_handle = ctx;

    if (close_handle) {
        hostfxr->close(ctx);
    }

    hostfxr->load_assembly_and_get_function_pointer = load_assembly_and_get_function_pointer;
    hostfxr->get_function_pointer = get_function_pointer;

    return true;
}

bool run_pwsh_app()
{
    char base_path[HOSTFXR_MAX_PATH];
    char hostfxr_path[HOSTFXR_MAX_PATH];
    char runtime_config_path[HOSTFXR_MAX_PATH];
    char assembly_path[HOSTFXR_MAX_PATH];
    HOSTFXR_CONTEXT* hostfxr = &g_HOSTFXR_CONTEXT;

    strncpy(base_path, g_PWSH_BASE_PATH, HOSTFXR_MAX_PATH);
    snprintf(hostfxr_path, HOSTFXR_MAX_PATH, "%s%s%s", base_path, PATH_SEPARATOR_STR, HOSTFXR_LIB_NAME);

    if (!load_hostfxr(hostfxr, hostfxr_path)) {
        printf("failed to load hostfxr!\n");
        return false;
    }

    snprintf(runtime_config_path, HOSTFXR_MAX_PATH, "%s%s%s.runtimeconfig.json",
        base_path, PATH_SEPARATOR_STR, "pwsh");
    snprintf(assembly_path, HOSTFXR_MAX_PATH, "%s%s%s.dll", base_path, PATH_SEPARATOR_STR, "pwsh");

    char* command_args[] = {
        assembly_path,
        "-NoLogo",
        "-Command",
        "Write-Host 'Hello PowerShell Host'"
    };
    int command_argc = sizeof(command_args) / sizeof(char*);

    if (!load_command(hostfxr, command_argc, (const char**) command_args, false)) {
        printf("failed to load runtime!\n");
        return false;
    }

    hostfxr->run_app(hostfxr->context_handle);

    return true;
}

bool run_pwsh_lib()
{
    char base_path[HOSTFXR_MAX_PATH];
    char hostfxr_path[HOSTFXR_MAX_PATH];
    char coreclr_path[HOSTFXR_MAX_PATH];
    char runtime_config_path[HOSTFXR_MAX_PATH];
    char assembly_path[HOSTFXR_MAX_PATH];
    HOSTFXR_CONTEXT* hostfxr = &g_HOSTFXR_CONTEXT;
    CORECLR_CONTEXT* coreclr = &g_CORECLR_CONTEXT;

    strncpy(base_path, g_PWSH_BASE_PATH, HOSTFXR_MAX_PATH);
    snprintf(hostfxr_path, HOSTFXR_MAX_PATH, "%s%s%s", base_path, PATH_SEPARATOR_STR, HOSTFXR_LIB_NAME);
    snprintf(coreclr_path, HOSTFXR_MAX_PATH, "%s%s%s", base_path, PATH_SEPARATOR_STR, CORECLR_LIB_NAME);

    if (!load_hostfxr(hostfxr, hostfxr_path)) {
        printf("failed to load hostfxr!\n");
        return false;
    }

    if (!load_coreclr(coreclr, coreclr_path)) {
        printf("failed to load coreclr!\n");
        return false;
    }

    snprintf(runtime_config_path, HOSTFXR_MAX_PATH, "%s%s%s.runtimeconfig.json",
        base_path, PATH_SEPARATOR_STR, "pwsh");
    snprintf(assembly_path, HOSTFXR_MAX_PATH, "%s%s%s.dll", base_path, PATH_SEPARATOR_STR, "pwsh");

    printf("loading %s\n", runtime_config_path);

    char* command_args[] = {
        assembly_path
    };
    int command_argc = sizeof(command_args) / sizeof(char*);

    if (!load_command(hostfxr, command_argc, (const char**) command_args, false)) {
        printf("failed to load runtime!\n");
        return false;
    }

    char helper_assembly_path[HOSTFXR_MAX_PATH];

    snprintf(helper_assembly_path, HOSTFXR_MAX_PATH, "%s%sSystem.Management.Automation.dll", base_path, PATH_SEPARATOR_STR);
    if (!load_assembly_helper(hostfxr, helper_assembly_path,
            "System.Management.Automation.PowerShellUnsafeAssemblyLoad, System.Management.Automation")) {
        printf("failed to load PowerShellUnsafeAssemblyLoad helper function!\n");
        return false;
    }

    call_pwsh_sdk(hostfxr);

    return true;
}

bool pwsh_host_detect()
{
    // TODO: proper detect PowerShell installation path

    if (get_env("PWSH_BASE_PATH", g_PWSH_BASE_PATH, HOSTFXR_MAX_PATH) < 1) {
#ifdef _WIN32
        strncpy(g_PWSH_BASE_PATH, "C:\\Program Files\\PowerShell\\7-preview", HOSTFXR_MAX_PATH);
#else
        strncpy(g_PWSH_BASE_PATH, "/opt/microsoft/powershell/7-preview", HOSTFXR_MAX_PATH);
#endif
        printf("Set PWSH_BASE_PATH environment variable to point to PowerShell installation path\n");
        printf("using hardcoded PowerShell installation path: \"%s\"\n", g_PWSH_BASE_PATH);
    }

    return true;
}

bool pwsh_host_app()
{
    pwsh_host_detect();
    run_pwsh_app();
    return true;
}

bool pwsh_host_lib()
{
    pwsh_host_detect();
    run_pwsh_lib();
    return true;
}
