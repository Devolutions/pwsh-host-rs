use crate::error::Error;
use num_enum::TryFromPrimitive;
use std::{
    convert::{TryFrom, TryInto},
    num::TryFromIntError,
};
/// The special error or exit codes for hostfxr operations.
///
/// Source: [https://github.com/dotnet/runtime/blob/main/docs/design/features/host-error-codes.md](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-error-codes.md)
#[non_exhaustive]
#[derive(Debug, PartialEq, Eq, Clone, Copy, TryFromPrimitive)]
#[repr(u32)]
pub enum KnownHostExitCode {
    /// Operation was successful.
    Success = 0x00000000, // 0
    /// Initialization was successful, but another host context is already initialized, so the returned context is "secondary". The requested context was otherwise fully compatible with the already initialized context. This is returned by `hostfxr_initialize_for_runtime_config` if it's called when the host is already initialized in the process. Comes from `corehost_initialize` in `hostpolicy`.
    SuccessHostAlreadyInitialized = 0x00000001, // 1
    /// Initialization was successful, but another host context is already initialized and the requested context specified some runtime properties which are not the same (either in value or in presence) to the already initialized context. This is returned by `hostfxr_initialize_for_runtime_config` if it's called when the host is already initialized in the process. Comes from `corehost_initialize` in `hostpolicy`.
    SuccessDifferentRuntimeProperties = 0x00000002, // 2
    /// One of the specified arguments for the operation is invalid."
    InvalidArgFailure = 0x80008081, // -2147450751
    /// There was a failure loading a dependent library. If any of the hosting components calls `LoadLibrary`/`dlopen` on a dependent library and the call fails, this error code is returned. The most common case for this failure is if the dependent library is missing some of its dependencies (for example the necessary CRT is missing on the machine), likely corrupt or incomplete install. This error code is also returned from `corehost_resolve_component_dependencies` if it's called on a `hostpolicy` which has not been initialized via the hosting layer. This would typically happen if `coreclr` is loaded directly without the hosting layer and then `AssemblyDependencyResolver` is used (which is an unsupported scenario).
    CoreHostLibLoadFailure = 0x80008082, // -2147450750
    /// One of the dependent libraries is missing. Typically when the `hostfxr`, `hostpolicy` or `coreclr` dynamic libraries are not present in the expected locations. Probably means corrupted or incomplete installation.
    CoreHostLibMissingFailure = 0x80008083, // -2147450749
    /// One of the dependent libraries is missing a required entry point.
    CoreHostEntryPointFailure = 0x80008084, // -2147450748
    /// If the hosting component is trying to use the path to the current module (the hosting component itself) and from it deduce the location of the installation. Either the location of the current module could not be determined (some weird OS call failure) or the location is not in the right place relative to other expected components. For example the `hostfxr` may look at its location and try to deduce the location of the `shared` folder with the framework from it. It assumes the typical install layout on disk. If this doesn't work, this error will be returned.
    CoreHostCurHostFindFailure = 0x80008085, // -2147450747
    /// If the `coreclr` library could not be found. The hosting layer (`hostpolicy`) looks for `coreclr` library either next to the app itself (for self-contained) or in the root framework (for framework-dependent). This search can be done purely by looking at disk or more commonly by looking into the respective `.deps.json`. If the `coreclr` library is missing in `.deps.json` or it's there but doesn't exist on disk, this error is returned.
    CoreClrResolveFailure = 0x80008087, // -2147450745
    /// The loaded `coreclr` library doesn't have one of the required entry points.
    CoreClrBindFailure = 0x80008088, // -2147450744
    /// The call to `coreclr_initialize` failed. The actual error returned by `coreclr` is reported in the error message.
    CoreClrInitFailure = 0x80008089, // -2147450743
    /// The call to `coreclr_execute_assembly` failed. Note that this does not mean anything about the app's exit code, this failure occurs if `coreclr` failed to run the app itself.
    CoreClrExeFailure = 0x8000808a, // -2147450742
    /// Initialization of the `hostpolicy` dependency resolver failed. This can be: <ul><li> One of the frameworks or the app is missing a required `.deps.json` file. </li><li> One of the `.deps.json` files is invalid (invalid JSON, or missing required properties and so on). </li></ul>
    ResolverInitFailure = 0x8000808b, // -2147450741
    /// Resolution of dependencies in `hostpolicy` failed. This can mean many different things, but in general one of the processed `.deps.json` contains entry for a file which could not found, or its resolution failed for some other reason (conflict for example).
    ResolverResolveFailure = 0x8000808c, // -2147450740
    /// Failure to determine the location of the current executable. The hosting layer uses the current executable path to deduce the install location in some cases. If this path can't be obtained (OS call fails, or the returned path doesn't exist), this error is returned.
    LibHostCurExeFindFailure = 0x8000808d, // -2147450739
    /// Initialization of the `hostpolicy` library failed. The `corehost_load` method takes a structure with lot of initialization parameters. If the version of this structure doesn't match the expected value, this error code is returned. This would in general mean incompatibility between the `hostfxr` and `hostpolicy`, which should really only happen if somehow a newer `hostpolicy` is used by older `hostfxr`. This typically means corrupted installation.
    LibHostInitFailure = 0x8000808e, // -2147450738
    /// Failure to find the requested SDK. This happens in the `hostfxr` when an SDK (also called CLI) command is used with `dotnet`. In this case the hosting layer tries to find an installed .NET SDK to run the command on. The search is based on deduced install location and on the requested version from potential `global.json` file. If either no matching SDK version can be found, or that version exists, but it's missing the `dotnet.dll` file, this error code is returned.
    LibHostSdkFindFailure = 0x80008091, // -2147450735
    /// Arguments to `hostpolicy` are invalid. This is used in three unrelated places in the `hostpolicy`, but in all cases it means the component calling `hostpolicy` did something wrong: <ul><li> Command line arguments for the app - the failure would typically mean that wrong argument was passed or such. For example if the application main assembly is not specified on the command line. On its own this should not happen as `hostfxr` should have parsed and validated all command line arguments. </li><li> `hostpolicy` context's `get_delegate` - if the requested delegate enum value is not recognized. Again this would mean `hostfxr` passed the wrong value. </li><li> `corehost_resolve_component_dependencies` - if something went wrong initializing `hostpolicy` internal structures. Would happen for example when the `component_main_assembly_path` argument is wrong. </li></ul>
    LibHostInvalidArgs = 0x80008092, // -2147450734
    /// The `.runtimeconfig.json` file is invalid. The reasons for this failure can be among these: <ul><li> Failure to read from the file </li><li> Invalid JSON </li><li> Invalid value for a property (for example number for property which requires a string) </li><li> Missing required property </li><li> Other inconsistencies (for example `rollForward` and `applyPatches` are not allowed to be specified in the same config file) </li><li> Any of the above failures reading the `.runtimecofig.dev.json` file </li><li> Self-contained `.runtimeconfig.json` used in `hostfxr_initialize_for_runtime_config`. Note that missing `.runtimconfig.json` is not an error (means self-contained app). This error code is also used when there is a problem reading the CLSID map file in `comhost`. </li></ul>
    InvalidConfigFile = 0x80008093, // -2147450733
    /// Used internally when the command line for `dotnet.exe` doesn't contain path to the application to run. In such case the command line is considered to be a CLI/SDK command. This error code should never be returned to external caller.
    AppArgNotRunnable = 0x80008094, // -2147450732
    /// `apphost` failed to determine which application to run. This can mean: <ul><li> The `apphost` binary has not been imprinted with the path to the app to run (so freshly built `apphost.exe` from the branch will fail to run like this) </li><li> The `apphost` is a bundle (single-file exe) and it failed to extract correctly. </li></ul>
    AppHostExeNotBoundFailure = 0x80008095, // -2147450731
    /// It was not possible to find a compatible framework version. This originates in `hostfxr` (`resolve_framework_reference`) and means that the app specified a reference to a framework in its `.runtimeconfig.json` which could not be resolved. The failure to resolve can mean that no such framework is available on the disk, or that the available frameworks don't match the minimum version specified or that the roll forward options specified excluded all available frameworks. Typically this would be used if a 3.0 app is trying to run on a machine which has no 3.0 installed. It would also be used for example if a 32bit 3.0 app is running on a machine which has 3.0 installed but only for 64bit.
    FrameworkMissingFailure = 0x80008096, // -2147450730
    /// Returned by `hostfxr_get_native_search_directories` if the `hostpolicy` could not calculate the `NATIVE_DLL_SEARCH_DIRECTORIES`.
    HostApiFailed = 0x80008097, // -2147450729
    /// Returned when the buffer specified to an API is not big enough to fit the requested value. Can be returned from: <ul><li> `hostfxr_get_runtime_properties` </li><li> `hostfxr_get_native_search_directories` </li><li> `get_hostfxr_path` </li></ul>
    HostApiBufferTooSmall = 0x80008098, // -2147450728
    /// Returned by `hostpolicy` if the `corehost_main_with_output_buffer` is called with unsupported host command. This error code means there is incompatibility between the `hostfxr` and `hostpolicy`. In reality this should pretty much never happen.
    LibHostUnknownCommand = 0x80008099, // -2147450727
    /// Returned by `apphost` if the imprinted application path doesn't exist. This would happen if the app is built with an executable (the `apphost`) and the main `app.dll` is missing.
    LibHostAppRootFindFailure = 0x8000809a, // -2147450726
    /// Returned from `hostfxr_resolve_sdk2` when it fails to find matching SDK. Similar to `LibHostSdkFindFailure` but only used in the `hostfxr_resolve_sdk2`.
    SdkResolverResolveFailure = 0x8000809b, // -2147450725
    /// During processing of `.runtimeconfig.json` there were two framework references to the same framework which were not compatible. This can happen if the app specified a framework reference to a lower-level framework which is also specified by a higher-level framework which is also used by the app. For example, this would happen if the app referenced `Microsoft.AspNet.App` version 2.0 and `Microsoft.NETCore.App` version 3.0. In such case the `Microsoft.AspNet.App` has `.runtimeconfig.json` which also references `Microsoft.NETCore.App` but it only allows versions 2.0 up to 2.9 (via roll forward options). So the version 3.0 requested by the app is incompatible.
    FrameworkCompatFailure = 0x8000809c, // -2147450724
    /// Error used internally if the processing of framework references from `.runtimeconfig.json` reached a point where it needs to reprocess another already processed framework reference. If this error is returned to the external caller, it would mean there's a bug in the framework resolution algorithm.
    FrameworkCompatRetry = 0x8000809d, // -2147450723
    /// Error reading the bundle footer metadata from a single-file `apphost`. This would mean a corrupted `apphost`.
    AppHostExeNotBundle = 0x8000809e, // -2147450722
    /// Error extracting single-file `apphost` bundle. This is used in case of any error related to the bundle itself. Typically would mean a corrupted bundle.
    BundleExtractionFailure = 0x8000809f, // -2147450721
    /// Error reading or writing files during single-file `apphost` bundle extraction.
    BundleExtractionIOError = 0x800080a0, // -2147450720
    /// The `.runtimeconfig.json` specified by the app contains a runtime property which is also produced by the hosting layer. For example if the `.runtimeconfig.json` would specify a property `TRUSTED_PLATFORM_ROOTS`, this error code would be returned. It is not allowed to specify properties which are otherwise populated by the hosting layer (`hostpolicy`) as there is not good way to resolve such conflicts.
    LibHostDuplicateProperty = 0x800080a1, // -2147450719
    /// Feature which requires certain version of the hosting layer binaries was used on a version which doesn't support it. For example if COM component specified to run on 2.0 `Microsoft.NETCore.App` - as that contains older version of `hostpolicy` which doesn't support the necessary features to provide COM services.
    HostApiUnsupportedVersion = 0x800080a2, // -2147450718
    /// Error code returned by the hosting APIs in `hostfxr` if the current state is incompatible with the requested operation. There are many such cases, please refer to the documentation of the hosting APIs for details. For example if `hostfxr_get_runtime_property_value` is called with the `host_context_handle` `nullptr` (meaning get property from the active runtime) but there's no active runtime in the process.
    HostInvalidState = 0x800080a3, // -2147450717
    /// property requested by `hostfxr_get_runtime_property_value` doesn't exist.
    HostPropertyNotFound = 0x800080a4, // -2147450716
    /// Error returned by `hostfxr_initialize_for_runtime_config` if the component being initialized requires framework which is not available or incompatible with the frameworks loaded by the runtime already in the process. For example trying to load a component which requires 3.0 into a process which is already running a 2.0 runtime.
    CoreHostIncompatibleConfig = 0x800080a5, // -2147450715
    /// Error returned by `hostfxr_get_runtime_delegate` when `hostfxr` doesn't currently support requesting the given delegate type using the given context.
    HostApiUnsupportedScenario = 0x800080a6, // -2147450714
}

impl KnownHostExitCode {
    #[inline]
    pub fn is_success(&self) -> bool {
        matches!(
            self,
            KnownHostExitCode::Success
                | KnownHostExitCode::SuccessDifferentRuntimeProperties
                | KnownHostExitCode::SuccessHostAlreadyInitialized
        )
    }

    #[inline]
    pub fn is_error(&self) -> bool {
        !self.is_success()
    }

    #[inline]
    pub fn value(&self) -> u32 {
        *self as u32
    }

    #[inline]
    pub fn into_result(self) -> Result<(), Error> {
        if self.is_success() {
            Ok(())
        } else {
            Err(Error::Hostfxr(HostExitCode::Known(self)))
        }
    }
}

impl From<KnownHostExitCode> for u32 {
    fn from(code: KnownHostExitCode) -> Self {
        code.value()
    }
}

/// Represents an exit code from a hostfxr operation.
/// This may be one of the special code from [`KnownHostExitCode`],
/// the exit code of the application or an error code resulting from violating
/// some unchecked hostfxr constraint.
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum HostExitCode {
    Known(KnownHostExitCode),
    Unknown(u32),
}

impl HostExitCode {
    /// Constructs a new instance from the given value.
    pub fn new(value: u32) -> Self {
        match value.try_into() {
            Ok(known) => HostExitCode::Known(known),
            Err(_) => HostExitCode::Unknown(value),
        }
    }

    #[inline]
    pub fn is_success(&self) -> bool {
        match self {
            HostExitCode::Known(code) => code.is_success(),
            HostExitCode::Unknown(_) => false,
        }
    }

    #[inline]
    pub fn is_error(&self) -> bool {
        !self.is_success()
    }

    #[inline]
    pub fn value(&self) -> u32 {
        match self {
            HostExitCode::Known(code) => code.value(),
            HostExitCode::Unknown(code) => *code,
        }
    }

    #[inline]
    pub fn into_result(self) -> Result<(), Error> {
        match self {
            HostExitCode::Known(code) => code.into_result(),
            HostExitCode::Unknown(_) => Err(Error::Hostfxr(self)),
        }
    }
}

impl From<i32> for HostExitCode {
    fn from(value: i32) -> Self {
        HostExitCode::new(value as u32)
    }
}

impl From<u32> for HostExitCode {
    fn from(value: u32) -> Self {
        HostExitCode::new(value)
    }
}

impl TryFrom<HostExitCode> for i32 {
    type Error = TryFromIntError;

    fn try_from(code: HostExitCode) -> Result<Self, Self::Error> {
        Self::try_from(code.value())
    }
}

impl From<HostExitCode> for u32 {
    fn from(code: HostExitCode) -> Self {
        code.value()
    }
}
