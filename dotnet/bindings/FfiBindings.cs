using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;

namespace NativeHost
{
    public static partial class Bindings
    {
        private const uint FfiBindingsAbiVersion = 2;
        private const uint FfiCallResultTruncatedDiagnostic = 1;
        private const int FfiStatusSuccess = 0;
        private const int FfiStatusInvalidArgument = -1;
        private const int FfiStatusInvalidHandle = -4;
        private const int FfiStatusManagedFailure = -6;
        private const int FfiStatusInputNotCompleted = -8;
        private const int FfiStatusBackpressure = -9;
        private const int FfiStatusUnsupportedValue = -10;
        private const int FfiMaxStreamRecords = 32;
        private const int FfiMaxStreamFieldLength = 4096;
        private const int FfiStreamCount = 7;
        private const int FfiMaxValuePayloadLength = 64 * 1024;
        private const int FfiMaxValueContainerEntries = 64;
        private const int FfiMaxValueDepth = 8;
        private const int FfiMaxInputValues = 64;
        private const int FfiMaxInputPayloadLength = 64 * 1024;
        private const int FfiMaxSnapshotPropertyEntries = 16;
        private const int FfiMaxSnapshotPropertyNameLength = 128;
        private const int FfiMaxSnapshotScalarPayloadLength = 1024;
        private const int FfiMaxSnapshotPropertyBagPayloadLength = 16 * 1024;
        private const uint FfiResultTerminatingFailure = 1;
        private const uint FfiResultSequenceTruncated = 2;
        private const uint FfiStreamTruncated = 1;
        private const uint FfiRecordFieldsTruncated = 1;
        private const uint FfiRecordScalarValuePresent = 1 << 1;
        private const uint FfiRecordPropertyBagPresent = 1 << 2;
        private const uint FfiRecordPropertyBagTruncated = 1 << 3;
        private const uint FfiRecordTypeNamesTruncated = 1 << 4;
        private const uint FfiRecordErrorTargetValuePresent = 1 << 5;
        private const ulong FfiFeatureAsyncOperationPrimitives = 1UL << 8;
        private const ulong FfiFeatureSessionPrimitives = 1UL << 10;
        private const ulong FfiFeatureSessionPolling = 1UL << 11;
        private const ulong FfiFeatureSnapshotProjections = 1UL << 13;
        private const ulong FfiFeatureSessionConfiguration = 1UL << 14;
        private const ulong FfiFeatureSessionVariables = 1UL << 15;
        private const ulong FfiFeatureCapabilityRpc = 1UL << 16;
        private const int FfiMaxSessionEvents = 32;

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct FfiCallResult
        {
            public uint Size;
            public int Status;
            public uint Flags;
            public byte* Diagnostic;
            public int DiagnosticCapacity;
            public int DiagnosticRequiredLength;
            public int DiagnosticWrittenLength;
        }

        private static bool TryGetConfigurationStrings(object[] values, out string[] result)
        {
            if (values.Length > FfiMaxSessionEvents)
            {
                result = Array.Empty<string>();
                return false;
            }

            result = new string[values.Length];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < values.Length; index++)
            {
                if (values[index] is not string value ||
                    string.IsNullOrWhiteSpace(value) ||
                    value.IndexOf('\0') >= 0 ||
                    value.Length > 4096 ||
                    !seen.Add(value))
                {
                    result = Array.Empty<string>();
                    return false;
                }
                result[index] = value;
            }

            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FfiApiV2
        {
            public nuint Size;
            public uint AbiVersion;
            public ulong FeatureFlags;
            public IntPtr PowerShell_Create;
            public IntPtr PowerShell_Release;
            public IntPtr PowerShell_AddArgumentUtf8;
            public IntPtr PowerShell_AddParameterStringUtf8;
            public IntPtr PowerShell_AddParameterInt64;
            public IntPtr PowerShell_AddCommandUtf8;
            public IntPtr PowerShell_AddScriptUtf8;
            public IntPtr PowerShell_AddStatement;
            public IntPtr PowerShell_InvokeToUtf8;
            public IntPtr PowerShell_GetInvocationErrorCount;
            public IntPtr PowerShell_CopyInvocationErrorFieldToUtf8;
            public IntPtr PowerShell_Clear;
            public IntPtr PowerShell_Stop;
            public IntPtr PowerShell_InvokeToResult;
            public IntPtr InvocationResult_Release;
            public IntPtr InvocationResult_GetInfo;
            public IntPtr InvocationResult_GetStreamInfo;
            public IntPtr InvocationResult_GetStreamRecordInfo;
            public IntPtr InvocationResult_CopyStreamRecordFieldToUtf8;
            public IntPtr InvocationResult_GetSequenceRecord;
            public IntPtr PowerShell_AddCommandUtf8Local;
            public IntPtr PowerShell_AddScriptUtf8Local;
            public IntPtr PowerShell_AddArgumentValue;
            public IntPtr PowerShell_AddParameterValue;
            public IntPtr PowerShell_AddParameterSwitch;
            public IntPtr PowerShell_AddInputValue;
            public IntPtr PowerShell_CompleteInput;
            public IntPtr PowerShell_ResetInput;
            public IntPtr InvocationResult_GetMetadata;
            public IntPtr PowerShellSession_Create;
            public IntPtr PowerShellSession_Release;
            public IntPtr PowerShellSession_CreateBuilder;
            public IntPtr PowerShellSession_GetSnapshot;
            public IntPtr PowerShellSession_GetEventInfo;
            public IntPtr InvocationResult_GetStreamTotals;
            public IntPtr InvocationResult_GetStreamRecordProjectionInfo;
            public IntPtr InvocationResult_CopyStreamRecordValue;
            public IntPtr PowerShellSession_CreateConfigured;
            public IntPtr PowerShellSession_SetVariable;
            public IntPtr PowerShellSession_RemoveVariable;
            public IntPtr PowerShellSession_GetVariableSnapshot;
            public IntPtr PowerShell_SetCapabilityContext;
        }

        private static string[] NormalizeDirectories(string[] directories, string description)
        {
            if (directories.Length > FfiMaxSessionEvents)
            {
                throw new InvalidOperationException($"{description} count exceeds its bound.");
            }
            var normalized = new string[directories.Length];
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < directories.Length; index++)
            {
                string directory = directories[index];
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                {
                    throw new InvalidOperationException($"{description} must be an absolute directory.");
                }
                string fullPath = Path.GetFullPath(directory);
                if (!Directory.Exists(fullPath) || !unique.Add(fullPath))
                {
                    throw new InvalidOperationException($"{description} must name unique existing directories.");
                }
                normalized[index] = fullPath;
            }
            return normalized;
        }

        private static string ResolveModuleImport(string[] allowedModulePaths, string moduleImport)
        {
            if (File.Exists(moduleImport))
            {
                string manifestPath = Path.GetFullPath(moduleImport);
                if (!string.Equals(Path.GetExtension(manifestPath), ".psd1", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(manifestPath))
                {
                    throw new InvalidOperationException("An approved module manifest path is invalid.");
                }

                return manifestPath;
            }

            if (string.IsNullOrWhiteSpace(moduleImport) ||
                moduleImport.Length > 128 ||
                !moduleImport.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-'))
            {
                throw new InvalidOperationException("Module import name is invalid.");
            }

            foreach (string root in NormalizeDirectories(allowedModulePaths, "Allowed module path"))
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(root, moduleImport, $"{moduleImport}.psd1"),
                    Path.Combine(root, moduleImport, $"{moduleImport}.psm1"),
                    Path.Combine(root, $"{moduleImport}.psd1"),
                    Path.Combine(root, $"{moduleImport}.psm1"),
                    Path.Combine(root, $"{moduleImport}.dll"),
                })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            throw new InvalidOperationException("An approved module import could not be resolved beneath an approved module path.");
        }

        private static readonly object FfiApiV2Lock = new object();
        private static readonly ConcurrentDictionary<IntPtr, InvocationResult> FfiInvocationResults =
            new ConcurrentDictionary<IntPtr, InvocationResult>();
        private static readonly ConcurrentDictionary<IntPtr, FfiInputBuffer> FfiInputBuffers =
            new ConcurrentDictionary<IntPtr, FfiInputBuffer>();
        private static long FfiNextInvocationId;
        private static IntPtr FfiApiV2Ptr = IntPtr.Zero;

        [UnmanagedCallersOnly]
        public static IntPtr Bindings_GetFfiApiV2()
        {
            try
            {
                lock (FfiApiV2Lock)
                {
                    if (FfiApiV2Ptr == IntPtr.Zero)
                    {
                        FfiApiV2 api = CreateFfiApiV2();
                        FfiApiV2Ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf<FfiApiV2>());
                        Marshal.StructureToPtr(api, FfiApiV2Ptr, false);
                    }

                    return FfiApiV2Ptr;
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static unsafe FfiApiV2 CreateFfiApiV2()
        {
            return new FfiApiV2
            {
                Size = (nuint)Marshal.SizeOf<FfiApiV2>(),
                AbiVersion = FfiBindingsAbiVersion,
                FeatureFlags = (1UL << 4) | (1UL << 5) | (1UL << 6) | FfiFeatureAsyncOperationPrimitives |
                    FfiFeatureSessionPrimitives | FfiFeatureSessionPolling | FfiFeatureSnapshotProjections |
                    FfiFeatureSessionConfiguration | FfiFeatureSessionVariables | FfiFeatureCapabilityRpc,
                PowerShell_Create = (IntPtr)(delegate* unmanaged<IntPtr*, FfiCallResult*, int>)&FfiPowerShell_Create,
                PowerShell_Release = (IntPtr)(delegate* unmanaged<IntPtr, FfiCallResult*, int>)&FfiPowerShell_Release,
                PowerShell_AddArgumentUtf8 = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, FfiCallResult*, int>)&FfiPowerShell_AddArgumentUtf8,
                PowerShell_AddParameterStringUtf8 = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, byte*, int, FfiCallResult*, int>)&FfiPowerShell_AddParameterStringUtf8,
                PowerShell_AddParameterInt64 = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, long, FfiCallResult*, int>)&FfiPowerShell_AddParameterInt64,
                PowerShell_AddCommandUtf8 = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, FfiCallResult*, int>)&FfiPowerShell_AddCommandUtf8,
                PowerShell_AddScriptUtf8 = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, FfiCallResult*, int>)&FfiPowerShell_AddScriptUtf8,
                PowerShell_AddStatement = (IntPtr)(delegate* unmanaged<IntPtr, FfiCallResult*, int>)&FfiPowerShell_AddStatement,
                PowerShell_InvokeToUtf8 = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, int*, FfiCallResult*, int>)&FfiPowerShell_InvokeToUtf8,
                PowerShell_GetInvocationErrorCount = (IntPtr)(delegate* unmanaged<IntPtr, int*, FfiCallResult*, int>)&FfiPowerShell_GetInvocationErrorCount,
                PowerShell_CopyInvocationErrorFieldToUtf8 = (IntPtr)(delegate* unmanaged<IntPtr, int, int, byte*, int, int*, FfiCallResult*, int>)&FfiPowerShell_CopyInvocationErrorFieldToUtf8,
                PowerShell_Clear = (IntPtr)(delegate* unmanaged<IntPtr, FfiCallResult*, int>)&FfiPowerShell_Clear,
                PowerShell_Stop = (IntPtr)(delegate* unmanaged<IntPtr, FfiCallResult*, int>)&FfiPowerShell_Stop,
                PowerShell_InvokeToResult = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, FfiCallResult*, int>)&FfiPowerShell_InvokeToResult,
                InvocationResult_Release = (IntPtr)(delegate* unmanaged<IntPtr, FfiCallResult*, int>)&FfiInvocationResult_Release,
                InvocationResult_GetInfo = (IntPtr)(delegate* unmanaged<IntPtr, uint*, int*, FfiCallResult*, int>)&FfiInvocationResult_GetInfo,
                InvocationResult_GetStreamInfo = (IntPtr)(delegate* unmanaged<IntPtr, int, int*, uint*, FfiCallResult*, int>)&FfiInvocationResult_GetStreamInfo,
                InvocationResult_GetStreamRecordInfo = (IntPtr)(delegate* unmanaged<IntPtr, int, int, long*, uint*, FfiCallResult*, int>)&FfiInvocationResult_GetStreamRecordInfo,
                InvocationResult_CopyStreamRecordFieldToUtf8 = (IntPtr)(delegate* unmanaged<IntPtr, int, int, int, byte*, int, int*, FfiCallResult*, int>)&FfiInvocationResult_CopyStreamRecordFieldToUtf8,
                InvocationResult_GetSequenceRecord = (IntPtr)(delegate* unmanaged<IntPtr, int, int*, int*, long*, FfiCallResult*, int>)&FfiInvocationResult_GetSequenceRecord,
                PowerShell_AddCommandUtf8Local = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, int, FfiCallResult*, int>)&FfiPowerShell_AddCommandUtf8Local,
                PowerShell_AddScriptUtf8Local = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, int, FfiCallResult*, int>)&FfiPowerShell_AddScriptUtf8Local,
                PowerShell_AddArgumentValue = (IntPtr)(delegate* unmanaged<IntPtr, uint, byte*, int, FfiCallResult*, int>)&FfiPowerShell_AddArgumentValue,
                PowerShell_AddParameterValue = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, uint, byte*, int, FfiCallResult*, int>)&FfiPowerShell_AddParameterValue,
                PowerShell_AddParameterSwitch = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, FfiCallResult*, int>)&FfiPowerShell_AddParameterSwitch,
                PowerShell_AddInputValue = (IntPtr)(delegate* unmanaged<IntPtr, uint, byte*, int, FfiCallResult*, int>)&FfiPowerShell_AddInputValue,
                PowerShell_CompleteInput = (IntPtr)(delegate* unmanaged<IntPtr, FfiCallResult*, int>)&FfiPowerShell_CompleteInput,
                PowerShell_ResetInput = (IntPtr)(delegate* unmanaged<IntPtr, FfiCallResult*, int>)&FfiPowerShell_ResetInput,
                InvocationResult_GetMetadata = (IntPtr)(delegate* unmanaged<IntPtr, uint*, long*, int*, FfiCallResult*, int>)&FfiInvocationResult_GetMetadata,
                PowerShellSession_Create = (IntPtr)(delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, byte*, int, IntPtr*, FfiCallResult*, int>)&FfiPowerShellSession_Create,
                PowerShellSession_Release = (IntPtr)(delegate* unmanaged<IntPtr, FfiCallResult*, int>)&FfiPowerShellSession_Release,
                PowerShellSession_CreateBuilder = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, FfiCallResult*, int>)&FfiPowerShellSession_CreateBuilder,
                PowerShellSession_GetSnapshot = (IntPtr)(delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, uint*, long*, long*, FfiCallResult*, int>)&FfiPowerShellSession_GetSnapshot,
                PowerShellSession_GetEventInfo = (IntPtr)(delegate* unmanaged<IntPtr, int, long*, uint*, uint*, FfiCallResult*, int>)&FfiPowerShellSession_GetEventInfo,
                InvocationResult_GetStreamTotals = (IntPtr)(delegate* unmanaged<IntPtr, int, long*, long*, FfiCallResult*, int>)&FfiInvocationResult_GetStreamTotals,
                InvocationResult_GetStreamRecordProjectionInfo = (IntPtr)(delegate* unmanaged<IntPtr, int, int, int*, int*, int*, int*, int*, FfiCallResult*, int>)&FfiInvocationResult_GetStreamRecordProjectionInfo,
                InvocationResult_CopyStreamRecordValue = (IntPtr)(delegate* unmanaged<IntPtr, int, int, int, uint*, byte*, int, int*, FfiCallResult*, int>)&FfiInvocationResult_CopyStreamRecordValue,
                PowerShellSession_CreateConfigured = (IntPtr)(delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, uint, byte*, int, byte*, int, byte*, int, byte*, int, byte*, int, IntPtr*, FfiCallResult*, int>)&FfiPowerShellSession_CreateConfigured,
                PowerShellSession_SetVariable = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, uint, byte*, int, FfiCallResult*, int>)&FfiPowerShellSession_SetVariable,
                PowerShellSession_RemoveVariable = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, uint*, FfiCallResult*, int>)&FfiPowerShellSession_RemoveVariable,
                PowerShellSession_GetVariableSnapshot = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, uint*, uint*, byte*, int, int*, FfiCallResult*, int>)&FfiPowerShellSession_GetVariableSnapshot,
                PowerShell_SetCapabilityContext = (IntPtr)(delegate* unmanaged<IntPtr, ulong, ulong, IntPtr, FfiCallResult*, int>)&FfiPowerShell_SetCapabilityContext,
            };
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_Create(IntPtr* ptrHandle, FfiCallResult* result)
        {
            if (!TryInitializeResult(result))
            {
                return FfiStatusInvalidArgument;
            }

            if (ptrHandle == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "PowerShell handle output pointer is null.");
            }

            try
            {
                GCHandle handle = GCHandle.Alloc(new FfiPowerShellPipeline(PowerShell.Create(), null), GCHandleType.Normal);
                *ptrHandle = GCHandle.ToIntPtr(handle);
                return WriteSuccess(result);
            }
            catch (Exception exception)
            {
                return WriteFailure(result, FfiStatusManagedFailure, exception);
            }
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_Release(IntPtr ptrHandle, FfiCallResult* result)
        {
            if (!TryInitializeResult(result))
            {
                return FfiStatusInvalidArgument;
            }

            try
            {
                GCHandle handle = GCHandle.FromIntPtr(ptrHandle);
                if (!handle.IsAllocated || handle.Target is not FfiPowerShellPipeline pipeline)
                {
                    return WriteFailure(result, FfiStatusInvalidHandle, "PowerShell handle is invalid.");
                }

                FfiInvocationResults.TryRemove(ptrHandle, out _);
                FfiInputBuffers.TryRemove(ptrHandle, out _);
                pipeline.Dispose();
                handle.Free();
                return WriteSuccess(result);
            }
            catch (Exception exception)
            {
                return WriteFailure(result, FfiStatusInvalidHandle, exception);
            }
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_Create(
            uint runspaceMode,
            uint initialConfiguration,
            uint historyMode,
            uint errorPreference,
            uint warningPreference,
            uint verbosePreference,
            uint debugPreference,
            uint informationPreference,
            byte* allowedModulePath,
            int allowedModulePathLength,
            IntPtr* ptrSessionHandle,
            FfiCallResult* result)
        {
            if (ptrSessionHandle == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "PowerShell session handle output pointer is null.");
            }

            int status = ReadUtf8(allowedModulePath, allowedModulePathLength, result, out string modulePath);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                var session = FfiPowerShellSession.Create(
                    runspaceMode,
                    initialConfiguration,
                    historyMode,
                    errorPreference,
                    warningPreference,
                    verbosePreference,
                    debugPreference,
                    informationPreference,
                    modulePath);
                GCHandle handle = GCHandle.Alloc(session, GCHandleType.Normal);
                *ptrSessionHandle = GCHandle.ToIntPtr(handle);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_CreateConfigured(
            uint runspaceMode,
            uint initialConfiguration,
            uint historyMode,
            uint errorPreference,
            uint warningPreference,
            uint verbosePreference,
            uint debugPreference,
            uint informationPreference,
            uint executionPolicy,
            byte* initialVariables,
            int initialVariablesLength,
            byte* moduleImports,
            int moduleImportsLength,
            byte* allowedModulePaths,
            int allowedModulePathsLength,
            byte* workingDirectory,
            int workingDirectoryLength,
            byte* environment,
            int environmentLength,
            IntPtr* ptrSessionHandle,
            FfiCallResult* result)
        {
            if (ptrSessionHandle == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "PowerShell session handle output pointer is null.");
            }

            int status = ReadValue((uint)FfiValueKind.PropertyBag, initialVariables, initialVariablesLength, result, out object initialVariablesValue);
            if (status != FfiStatusSuccess)
            {
                return status;
            }
            status = ReadValue((uint)FfiValueKind.Array, moduleImports, moduleImportsLength, result, out object moduleImportsValue);
            if (status != FfiStatusSuccess)
            {
                return status;
            }
            status = ReadValue((uint)FfiValueKind.Array, allowedModulePaths, allowedModulePathsLength, result, out object modulePathsValue);
            if (status != FfiStatusSuccess)
            {
                return status;
            }
            status = ReadUtf8(workingDirectory, workingDirectoryLength, result, out string workingDirectoryValue);
            if (status != FfiStatusSuccess)
            {
                return status;
            }
            status = ReadValue((uint)FfiValueKind.PropertyBag, environment, environmentLength, result, out object environmentValue);
            if (status != FfiStatusSuccess)
            {
                return status;
            }
            if (initialVariablesValue is not PSObject initialVariablesObject ||
                moduleImportsValue is not object[] moduleImportObjects ||
                modulePathsValue is not object[] modulePathObjects ||
                environmentValue is not PSObject environmentObject)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "PowerShell session configuration has an invalid tagged shape.");
            }
            if (!TryGetConfigurationStrings(moduleImportObjects, out string[] moduleImportNames) ||
                !TryGetConfigurationStrings(modulePathObjects, out string[] modulePathNames))
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "PowerShell session module configuration must contain only strings.");
            }

            return Execute(result, () =>
            {
                var session = FfiPowerShellSession.CreateConfigured(
                    runspaceMode,
                    initialConfiguration,
                    historyMode,
                    errorPreference,
                    warningPreference,
                    verbosePreference,
                    debugPreference,
                    informationPreference,
                    executionPolicy,
                    initialVariablesObject,
                    moduleImportNames,
                    modulePathNames,
                    workingDirectoryValue,
                    environmentObject);
                GCHandle handle = GCHandle.Alloc(session, GCHandleType.Normal);
                *ptrSessionHandle = GCHandle.ToIntPtr(handle);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_Release(IntPtr ptrSessionHandle, FfiCallResult* result)
        {
            if (!TryInitializeResult(result))
            {
                return FfiStatusInvalidArgument;
            }

            try
            {
                GCHandle handle = GCHandle.FromIntPtr(ptrSessionHandle);
                if (!handle.IsAllocated || handle.Target is not FfiPowerShellSession session)
                {
                    return WriteFailure(result, FfiStatusInvalidHandle, "PowerShell session handle is invalid.");
                }

                session.ReleaseOwner();
                handle.Free();
                return WriteSuccess(result);
            }
            catch (Exception exception)
            {
                return WriteFailure(result, FfiStatusInvalidHandle, exception);
            }
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_CreateBuilder(
            IntPtr ptrSessionHandle,
            IntPtr* ptrBuilderHandle,
            FfiCallResult* result)
        {
            if (ptrBuilderHandle == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "PowerShell builder handle output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiPowerShellSession session = GetPowerShellSession(ptrSessionHandle);
                FfiPowerShellPipeline pipeline = session.CreatePipeline();
                GCHandle handle = GCHandle.Alloc(pipeline, GCHandleType.Normal);
                *ptrBuilderHandle = GCHandle.ToIntPtr(handle);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_GetSnapshot(
            IntPtr ptrSessionHandle,
            uint* state,
            uint* runspaceState,
            uint* flags,
            uint* activePipelineCount,
            uint* eventCount,
            long* invocationCount,
            long* historyCount,
            FfiCallResult* result)
        {
            if (state == null || runspaceState == null || flags == null || activePipelineCount == null ||
                eventCount == null || invocationCount == null || historyCount == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "PowerShell session snapshot output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiSessionSnapshot snapshot = GetPowerShellSession(ptrSessionHandle).Snapshot();
                *state = snapshot.State;
                *runspaceState = snapshot.RunspaceState;
                *flags = snapshot.Flags;
                *activePipelineCount = snapshot.ActivePipelineCount;
                *eventCount = snapshot.EventCount;
                *invocationCount = snapshot.InvocationCount;
                *historyCount = snapshot.HistoryCount;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_GetEventInfo(
            IntPtr ptrSessionHandle,
            int eventIndex,
            long* sequence,
            uint* state,
            uint* flags,
            FfiCallResult* result)
        {
            if (sequence == null || state == null || flags == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "PowerShell session event output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiSessionEvent sessionEvent = GetPowerShellSession(ptrSessionHandle).GetEvent(eventIndex);
                *sequence = sessionEvent.Sequence;
                *state = sessionEvent.State;
                *flags = sessionEvent.Flags;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_SetVariable(
            IntPtr ptrSessionHandle,
            byte* name,
            int nameLength,
            uint kind,
            byte* data,
            int dataLength,
            FfiCallResult* result)
        {
            int status = ReadUtf8(name, nameLength, result, out string nameText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            status = ReadValue(kind, data, dataLength, result, out object value);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () => GetPowerShellSession(ptrSessionHandle).SetVariable(nameText, value));
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_RemoveVariable(
            IntPtr ptrSessionHandle,
            byte* name,
            int nameLength,
            uint* removed,
            FfiCallResult* result)
        {
            if (removed == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Session variable removal output pointer is null.");
            }

            int status = ReadUtf8(name, nameLength, result, out string nameText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                *removed = GetPowerShellSession(ptrSessionHandle).RemoveVariable(nameText) ? 1u : 0u;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShellSession_GetVariableSnapshot(
            IntPtr ptrSessionHandle,
            byte* name,
            int nameLength,
            uint* found,
            uint* kind,
            byte* buffer,
            int bufferLength,
            int* requiredLength,
            FfiCallResult* result)
        {
            if (found == null || kind == null || requiredLength == null || bufferLength < 0)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Session variable snapshot output arguments are invalid.");
            }

            int status = ReadUtf8(name, nameLength, result, out string nameText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            IntPtr outputBuffer = (IntPtr)buffer;
            IntPtr outputRequiredLength = (IntPtr)requiredLength;
            return Execute(result, () =>
            {
                if (!GetPowerShellSession(ptrSessionHandle).TryGetVariableSnapshot(nameText, out FfiSnapshotValue snapshot))
                {
                    *found = 0;
                    *kind = 0;
                    Marshal.WriteInt32(outputRequiredLength, 0);
                    return;
                }

                *found = 1;
                *kind = snapshot.Kind;
                int required = snapshot.Payload.Length;
                Marshal.WriteInt32(outputRequiredLength, required);
                if (bufferLength < required)
                {
                    throw new BufferTooSmallException();
                }

                if (required != 0)
                {
                    Marshal.Copy(snapshot.Payload, 0, outputBuffer, required);
                }
            }, bufferTooSmallIsSuccess: true);
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddArgumentUtf8(
            IntPtr ptrHandle,
            byte* argument,
            int argumentLength,
            FfiCallResult* result)
        {
            int status = ReadUtf8(argument, argumentLength, result, out string argumentText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddArgument(argumentText);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddParameterStringUtf8(
            IntPtr ptrHandle,
            byte* name,
            int nameLength,
            byte* value,
            int valueLength,
            FfiCallResult* result)
        {
            int status = ReadUtf8(name, nameLength, result, out string nameText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            status = ReadUtf8(value, valueLength, result, out string valueText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddParameter(nameText, valueText);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddParameterInt64(
            IntPtr ptrHandle,
            byte* name,
            int nameLength,
            long value,
            FfiCallResult* result)
        {
            int status = ReadUtf8(name, nameLength, result, out string nameText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddParameter(nameText, value);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddCommandUtf8(
            IntPtr ptrHandle,
            byte* command,
            int commandLength,
            FfiCallResult* result)
        {
            int status = ReadUtf8(command, commandLength, result, out string commandText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddCommand(commandText);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddScriptUtf8(
            IntPtr ptrHandle,
            byte* script,
            int scriptLength,
            FfiCallResult* result)
        {
            int status = ReadUtf8(script, scriptLength, result, out string scriptText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddScript(scriptText);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddCommandUtf8Local(
            IntPtr ptrHandle,
            byte* command,
            int commandLength,
            int useLocalScope,
            FfiCallResult* result)
        {
            if (useLocalScope is not 0 and not 1)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Local scope must be zero or one.");
            }

            int status = ReadUtf8(command, commandLength, result, out string commandText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddCommand(commandText, useLocalScope != 0);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddScriptUtf8Local(
            IntPtr ptrHandle,
            byte* script,
            int scriptLength,
            int useLocalScope,
            FfiCallResult* result)
        {
            if (useLocalScope is not 0 and not 1)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Local scope must be zero or one.");
            }

            int status = ReadUtf8(script, scriptLength, result, out string scriptText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddScript(scriptText, useLocalScope != 0);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddArgumentValue(
            IntPtr ptrHandle,
            uint kind,
            byte* data,
            int dataLength,
            FfiCallResult* result)
        {
            int status = ReadValue(kind, data, dataLength, result, out object value);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddArgument(value);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddParameterValue(
            IntPtr ptrHandle,
            byte* name,
            int nameLength,
            uint kind,
            byte* data,
            int dataLength,
            FfiCallResult* result)
        {
            int status = ReadUtf8(name, nameLength, result, out string nameText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            status = ReadValue(kind, data, dataLength, result, out object value);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddParameter(nameText, value);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddParameterSwitch(
            IntPtr ptrHandle,
            byte* name,
            int nameLength,
            FfiCallResult* result)
        {
            int status = ReadUtf8(name, nameLength, result, out string nameText);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddParameter(nameText);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddInputValue(
            IntPtr ptrHandle,
            uint kind,
            byte* data,
            int dataLength,
            FfiCallResult* result)
        {
            int status = ReadValue(kind, data, dataLength, result, out object value);
            if (status != FfiStatusSuccess)
            {
                return status;
            }

            return Execute(result, () => AddInputValue(ptrHandle, value, dataLength));
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_CompleteInput(IntPtr ptrHandle, FfiCallResult* result)
        {
            return Execute(result, () => CompleteInput(ptrHandle));
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_ResetInput(IntPtr ptrHandle, FfiCallResult* result)
        {
            return Execute(result, () =>
            {
                GetPowerShell(ptrHandle);
                FfiInputBuffers.TryRemove(ptrHandle, out _);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_AddStatement(IntPtr ptrHandle, FfiCallResult* result)
        {
            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                ps.AddStatement();
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_InvokeToUtf8(
            IntPtr ptrHandle,
            byte* buffer,
            int bufferLength,
            int* requiredLength,
            FfiCallResult* result)
        {
            if (requiredLength == null || bufferLength < 0)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Output buffer arguments are invalid.");
            }

            IntPtr outputBuffer = (IntPtr)buffer;
            IntPtr outputRequiredLength = (IntPtr)requiredLength;
            return Execute(result, () =>
            {
                FfiPowerShellPipeline pipeline = GetPowerShellPipeline(ptrHandle);
                PowerShell ps = pipeline.PowerShell;
                InvocationResult invocation = FfiInvocationResults.GetOrAdd(
                    ptrHandle,
                    _ => InvokeAndCaptureLegacy(ps, TakeCompletedInput(ptrHandle), pipeline.Session));
                if (invocation.Status != FfiStatusSuccess)
                {
                    throw new InvalidOperationException(invocation.Errors.Length == 0
                        ? "PowerShell invocation failed."
                        : invocation.Errors[0].Message);
                }

                int required = Encoding.UTF8.GetByteCount(invocation.Output);
                Marshal.WriteInt32(outputRequiredLength, required);
                if (bufferLength < required)
                {
                    throw new BufferTooSmallException();
                }

                if (required > 0)
                {
                    byte[] output = Encoding.UTF8.GetBytes(invocation.Output);
                    Marshal.Copy(output, 0, outputBuffer, required);
                }
            }, bufferTooSmallIsSuccess: true);
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_GetInvocationErrorCount(
            IntPtr ptrHandle,
            int* errorCount,
            FfiCallResult* result)
        {
            if (errorCount == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Error count output pointer is null.");
            }

            IntPtr errorCountPointer = (IntPtr)errorCount;
            return Execute(result, () =>
            {
                GetPowerShell(ptrHandle);
                if (!FfiInvocationResults.TryGetValue(ptrHandle, out InvocationResult invocation))
                {
                    throw new InvalidOperationException("No invocation result is available.");
                }

                Marshal.WriteInt32(errorCountPointer, invocation.Errors.Length);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_CopyInvocationErrorFieldToUtf8(
            IntPtr ptrHandle,
            int errorIndex,
            int field,
            byte* buffer,
            int bufferLength,
            int* requiredLength,
            FfiCallResult* result)
        {
            if (requiredLength == null || bufferLength < 0)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Error buffer arguments are invalid.");
            }

            IntPtr errorBuffer = (IntPtr)buffer;
            IntPtr errorRequiredLength = (IntPtr)requiredLength;
            return Execute(result, () =>
            {
                GetPowerShell(ptrHandle);
                if (!FfiInvocationResults.TryGetValue(ptrHandle, out InvocationResult invocation) ||
                    errorIndex < 0 ||
                    errorIndex >= invocation.Errors.Length)
                {
                    throw new InvalidOperationException("Invocation error index is invalid.");
                }

                InvocationError error = invocation.Errors[errorIndex];
                string value = field switch
                {
                    0 => error.Message,
                    1 => error.FullyQualifiedErrorId,
                    2 => error.Category,
                    3 => error.ExceptionType,
                    _ => throw new InvalidOperationException("Invocation error field is invalid."),
                };

                int required = Encoding.UTF8.GetByteCount(value);
                Marshal.WriteInt32(errorRequiredLength, required);
                if (bufferLength < required)
                {
                    throw new BufferTooSmallException();
                }

                if (required > 0)
                {
                    byte[] valueBytes = Encoding.UTF8.GetBytes(value);
                    Marshal.Copy(valueBytes, 0, errorBuffer, required);
                }
            }, bufferTooSmallIsSuccess: true);
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_InvokeToResult(
            IntPtr ptrHandle,
            IntPtr* ptrResultHandle,
            FfiCallResult* result)
        {
            if (ptrResultHandle == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation result handle output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiPowerShellPipeline pipeline = GetPowerShellPipeline(ptrHandle);
                PowerShell ps = pipeline.PowerShell;
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                FfiInvocationResultSnapshot snapshot = InvokeAndCaptureStreamSnapshot(
                    ps,
                    TakeCompletedInput(ptrHandle),
                    pipeline.Session,
                    pipeline.TakeCapabilityContext());
                GCHandle handle = GCHandle.Alloc(snapshot, GCHandleType.Normal);
                *ptrResultHandle = GCHandle.ToIntPtr(handle);
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_SetCapabilityContext(
            IntPtr ptrHandle,
            ulong registrationHandle,
            ulong invocationId,
            IntPtr dispatcher,
            FfiCallResult* result)
        {
            return Execute(result, () =>
            {
                FfiPowerShellPipeline pipeline = GetPowerShellPipeline(ptrHandle);
                if (registrationHandle == 0 && invocationId == 0 && dispatcher == IntPtr.Zero)
                {
                    pipeline.ClearCapabilityContext();
                    return;
                }
                if (registrationHandle == 0 || invocationId == 0 || dispatcher == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Capability context is invalid.");
                }

                pipeline.SetCapabilityContext(new FfiCapabilityContext(registrationHandle, invocationId, dispatcher));
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_Release(IntPtr ptrResultHandle, FfiCallResult* result)
        {
            if (!TryInitializeResult(result))
            {
                return FfiStatusInvalidArgument;
            }

            try
            {
                GCHandle handle = GCHandle.FromIntPtr(ptrResultHandle);
                if (!handle.IsAllocated || handle.Target is not FfiInvocationResultSnapshot)
                {
                    return WriteFailure(result, FfiStatusInvalidHandle, "Invocation result handle is invalid.");
                }

                handle.Free();
                return WriteSuccess(result);
            }
            catch (Exception exception)
            {
                return WriteFailure(result, FfiStatusInvalidHandle, exception);
            }
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_GetInfo(
            IntPtr ptrResultHandle,
            uint* flags,
            int* sequenceCount,
            FfiCallResult* result)
        {
            if (flags == null || sequenceCount == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation result info output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiInvocationResultSnapshot snapshot = GetInvocationResultSnapshot(ptrResultHandle);
                *flags = snapshot.Flags;
                *sequenceCount = snapshot.Sequence.Length;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_GetMetadata(
            IntPtr ptrResultHandle,
            uint* state,
            long* invocationId,
            int* hadErrors,
            FfiCallResult* result)
        {
            if (state == null || invocationId == null || hadErrors == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation metadata output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiInvocationResultSnapshot snapshot = GetInvocationResultSnapshot(ptrResultHandle);
                *state = snapshot.State;
                *invocationId = snapshot.InvocationId;
                *hadErrors = snapshot.HadErrors ? 1 : 0;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_GetStreamInfo(
            IntPtr ptrResultHandle,
            int stream,
            int* recordCount,
            uint* flags,
            FfiCallResult* result)
        {
            if (recordCount == null || flags == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation stream info output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiStreamSnapshot snapshot = GetInvocationResultSnapshot(ptrResultHandle).GetStream(stream);
                *recordCount = snapshot.Records.Length;
                *flags = snapshot.Flags;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_GetStreamRecordInfo(
            IntPtr ptrResultHandle,
            int stream,
            int recordIndex,
            long* sequence,
            uint* flags,
            FfiCallResult* result)
        {
            if (sequence == null || flags == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation stream record info output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiStreamRecord record = GetInvocationResultSnapshot(ptrResultHandle).GetStream(stream).GetRecord(recordIndex);
                *sequence = record.Sequence;
                *flags = record.Flags;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_CopyStreamRecordFieldToUtf8(
            IntPtr ptrResultHandle,
            int stream,
            int recordIndex,
            int field,
            byte* buffer,
            int bufferLength,
            int* requiredLength,
            FfiCallResult* result)
        {
            if (requiredLength == null || bufferLength < 0)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation stream record buffer arguments are invalid.");
            }

            IntPtr outputBuffer = (IntPtr)buffer;
            IntPtr outputRequiredLength = (IntPtr)requiredLength;
            return Execute(result, () =>
            {
                FfiStreamRecord record = GetInvocationResultSnapshot(ptrResultHandle).GetStream(stream).GetRecord(recordIndex);
                string value = record.GetField(field);
                int required = Encoding.UTF8.GetByteCount(value);
                Marshal.WriteInt32(outputRequiredLength, required);
                if (bufferLength < required)
                {
                    throw new BufferTooSmallException();
                }

                if (required > 0)
                {
                    byte[] valueBytes = Encoding.UTF8.GetBytes(value);
                    Marshal.Copy(valueBytes, 0, outputBuffer, required);
                }
            }, bufferTooSmallIsSuccess: true);
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_GetStreamTotals(
            IntPtr ptrResultHandle,
            int stream,
            long* totalRecordCount,
            long* droppedRecordCount,
            FfiCallResult* result)
        {
            if (totalRecordCount == null || droppedRecordCount == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation stream totals output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiStreamSnapshot snapshot = GetInvocationResultSnapshot(ptrResultHandle).GetStream(stream);
                *totalRecordCount = snapshot.TotalRecordCount;
                *droppedRecordCount = snapshot.DroppedRecordCount;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_GetStreamRecordProjectionInfo(
            IntPtr ptrResultHandle,
            int stream,
            int recordIndex,
            int* propertyEntryCount,
            int* droppedPropertyEntryCount,
            int* typeNameCount,
            int* droppedTypeNameCount,
            int* projectionFlags,
            FfiCallResult* result)
        {
            if (propertyEntryCount == null ||
                droppedPropertyEntryCount == null ||
                typeNameCount == null ||
                droppedTypeNameCount == null ||
                projectionFlags == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation stream projection output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiStreamRecord record = GetInvocationResultSnapshot(ptrResultHandle).GetStream(stream).GetRecord(recordIndex);
                *propertyEntryCount = record.PropertyEntryCount;
                *droppedPropertyEntryCount = record.DroppedPropertyEntryCount;
                *typeNameCount = record.TypeNameCount;
                *droppedTypeNameCount = record.DroppedTypeNameCount;
                *projectionFlags = checked((int)(record.Flags &
                    (FfiRecordScalarValuePresent |
                     FfiRecordPropertyBagPresent |
                     FfiRecordPropertyBagTruncated |
                     FfiRecordTypeNamesTruncated |
                     FfiRecordErrorTargetValuePresent)));
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_CopyStreamRecordValue(
            IntPtr ptrResultHandle,
            int stream,
            int recordIndex,
            int valueSlot,
            uint* kind,
            byte* buffer,
            int bufferLength,
            int* requiredLength,
            FfiCallResult* result)
        {
            if (kind == null || requiredLength == null || bufferLength < 0)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation stream value buffer arguments are invalid.");
            }

            IntPtr outputBuffer = (IntPtr)buffer;
            IntPtr outputRequiredLength = (IntPtr)requiredLength;
            return Execute(result, () =>
            {
                FfiStreamRecord record = GetInvocationResultSnapshot(ptrResultHandle).GetStream(stream).GetRecord(recordIndex);
                FfiSnapshotValue value = record.GetValue(valueSlot);
                *kind = value.Kind;
                int required = value.Payload.Length;
                Marshal.WriteInt32(outputRequiredLength, required);
                if (bufferLength < required)
                {
                    throw new BufferTooSmallException();
                }

                if (required > 0)
                {
                    Marshal.Copy(value.Payload, 0, outputBuffer, required);
                }
            }, bufferTooSmallIsSuccess: true);
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiInvocationResult_GetSequenceRecord(
            IntPtr ptrResultHandle,
            int sequenceIndex,
            int* stream,
            int* recordIndex,
            long* sequence,
            FfiCallResult* result)
        {
            if (stream == null || recordIndex == null || sequence == null)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Invocation sequence record output pointer is null.");
            }

            return Execute(result, () =>
            {
                FfiSequenceRecord record = GetInvocationResultSnapshot(ptrResultHandle).GetSequenceRecord(sequenceIndex);
                *stream = record.Stream;
                *recordIndex = record.RecordIndex;
                *sequence = record.Sequence;
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_Clear(IntPtr ptrHandle, FfiCallResult* result)
        {
            return Execute(result, () =>
            {
                PowerShell ps = GetPowerShell(ptrHandle);
                FfiInvocationResults.TryRemove(ptrHandle, out _);
                FfiInputBuffers.TryRemove(ptrHandle, out _);
                ps.Commands.Clear();
            });
        }

        [UnmanagedCallersOnly]
        public static unsafe int FfiPowerShell_Stop(IntPtr ptrHandle, FfiCallResult* result)
        {
            return Execute(result, () => GetPowerShell(ptrHandle).Stop());
        }

        private enum FfiValueKind : uint
        {
            Null = 0,
            StringUtf8 = 1,
            Switch = 2,
            Boolean = 3,
            SignedInteger = 4,
            UnsignedInteger = 5,
            Double = 6,
            DecimalUtf8 = 7,
            Bytes = 8,
            DateTime = 9,
            DateTimeOffset = 10,
            GuidUtf8 = 11,
            UriUtf8 = 12,
            Array = 13,
            PropertyBag = 14,
        }

        private sealed class FfiInputBuffer
        {
            public object Gate { get; } = new object();

            public List<object> Values { get; } = new List<object>(FfiMaxInputValues);

            public int PayloadLength { get; set; }

            public bool IsCompleted { get; set; }
        }

        private sealed class FfiPowerShellPipeline : IDisposable
        {
            private int disposed;
            private FfiCapabilityContext capabilityContext;

            public FfiPowerShellPipeline(PowerShell powerShell, FfiPowerShellSession session)
            {
                PowerShell = powerShell;
                Session = session;
            }

            public PowerShell PowerShell { get; }

            public FfiPowerShellSession Session { get; }

            public void SetCapabilityContext(FfiCapabilityContext value)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(FfiPowerShellPipeline));
                }

                capabilityContext = value ?? throw new ArgumentNullException(nameof(value));
            }

            public FfiCapabilityContext TakeCapabilityContext()
            {
                FfiCapabilityContext value = capabilityContext;
                capabilityContext = null;
                return value;
            }

            public void ClearCapabilityContext()
            {
                capabilityContext = null;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                try
                {
                    PowerShell.Dispose();
                }
                finally
                {
                    Session?.ReleasePipeline();
                }
            }
        }

        private sealed class FfiPowerShellSession
        {
            private const uint CurrentRunspace = 0;
            private const uint NewRunspace = 1;
            private const uint DefaultConfiguration = 0;
            private const uint ConstrainedLanguageConfiguration = 1;
            private const uint HistoryDisabled = 0;
            private const uint HistoryEnabled = 1;
            private const uint PreferenceInherit = 0;
            private const uint PreferenceContinue = 1;
            private const uint PreferenceSilentlyContinue = 2;
            private const uint PreferenceStop = 3;
            private const uint StateOpened = 1;
            private const uint StateRunning = 2;
            private const uint StateClosed = 3;
            private const uint StateFaulted = 4;
            private const uint EventsTruncated = 1;

            private readonly object gate = new object();
            private readonly Runspace runspace;
            private readonly bool ownsRunspace;
            private readonly bool addToHistory;
            private readonly uint errorPreference;
            private readonly List<FfiSessionEvent> events = new List<FfiSessionEvent>(FfiMaxSessionEvents);
            private int leaseCount = 1;
            private int activePipelineCount;
            private long eventSequence;
            private long invocationCount;
            private long historyCount;
            private bool ownerReleased;
            private bool eventsTruncated;
            private bool disposed;

            private sealed class FfiApprovedModuleAuthorizationManager : AuthorizationManager
            {
                private readonly string[] approvedModuleRoots;

                public FfiApprovedModuleAuthorizationManager(string[] approvedModuleRoots)
                    : base("Devolutions.PowerShell.Ffi")
                {
                    this.approvedModuleRoots = approvedModuleRoots;
                }

                protected override bool ShouldRun(CommandInfo commandInfo, CommandOrigin origin, PSHost host, out Exception reason)
                {
                    if (commandInfo is ExternalScriptInfo script)
                    {
                        if (approvedModuleRoots.Any(root => IsBeneathRoot(root, script.Path)))
                        {
                            reason = null;
                            return true;
                        }

                        reason = new PSSecurityException(
                            "External scripts are allowed only from approved staged module roots.");
                        return false;
                    }

                    return base.ShouldRun(commandInfo, origin, host, out reason);
                }

                private static bool IsBeneathRoot(string root, string path)
                {
                    try
                    {
                        string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
                        return relative.Length != 0 &&
                            !Path.IsPathFullyQualified(relative) &&
                            relative != ".." &&
                            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }
            }

            private FfiPowerShellSession(Runspace runspace, bool ownsRunspace, bool addToHistory, uint errorPreference)
            {
                this.runspace = runspace;
                this.ownsRunspace = ownsRunspace;
                this.addToHistory = addToHistory;
                this.errorPreference = errorPreference;
                AddEventLocked(StateOpened);
            }

            public static FfiPowerShellSession Create(
                uint runspaceMode,
                uint initialConfiguration,
                uint historyMode,
                uint errorPreference,
                uint warningPreference,
                uint verbosePreference,
                uint debugPreference,
                uint informationPreference,
                string allowedModulePath)
            {
                return CreateConfigured(
                    runspaceMode,
                    initialConfiguration,
                    historyMode,
                    errorPreference,
                    warningPreference,
                    verbosePreference,
                    debugPreference,
                    informationPreference,
                    executionPolicy: 0,
                    new PSObject(),
                    Array.Empty<string>(),
                    string.IsNullOrEmpty(allowedModulePath) ? Array.Empty<string>() : [allowedModulePath],
                    string.Empty,
                    new PSObject());
            }

            public static FfiPowerShellSession CreateConfigured(
                uint runspaceMode,
                uint initialConfiguration,
                uint historyMode,
                uint errorPreference,
                uint warningPreference,
                uint verbosePreference,
                uint debugPreference,
                uint informationPreference,
                uint executionPolicy,
                PSObject initialVariables,
                string[] moduleImports,
                string[] allowedModulePaths,
                string workingDirectory,
                PSObject environment)
            {
                if (runspaceMode is not (CurrentRunspace or NewRunspace))
                {
                    throw new InvalidOperationException("PowerShell session runspace mode is invalid.");
                }
                if (initialConfiguration is not (DefaultConfiguration or ConstrainedLanguageConfiguration) ||
                    historyMode is not (HistoryDisabled or HistoryEnabled) ||
                    executionPolicy is > 1)
                {
                    throw new InvalidOperationException("PowerShell session configuration is invalid.");
                }
                ArgumentNullException.ThrowIfNull(initialVariables);
                ArgumentNullException.ThrowIfNull(moduleImports);
                ArgumentNullException.ThrowIfNull(allowedModulePaths);
                ArgumentNullException.ThrowIfNull(workingDirectory);
                ArgumentNullException.ThrowIfNull(environment);
                ValidatePreference(errorPreference, "error");
                ValidatePreference(warningPreference, "warning");
                ValidatePreference(verbosePreference, "verbose");
                ValidatePreference(debugPreference, "debug");
                ValidatePreference(informationPreference, "information");

                if (runspaceMode == CurrentRunspace)
                {
                    if (initialConfiguration != DefaultConfiguration ||
                        historyMode != HistoryDisabled ||
                        errorPreference != PreferenceInherit ||
                        warningPreference != PreferenceInherit ||
                        verbosePreference != PreferenceInherit ||
                        debugPreference != PreferenceInherit ||
                        informationPreference != PreferenceInherit ||
                        executionPolicy != 0 ||
                        initialVariables.Properties.Count() != 0 ||
                        moduleImports.Length != 0 ||
                        allowedModulePaths.Length != 0 ||
                        !string.IsNullOrEmpty(workingDirectory) ||
                        environment.Properties.Count() != 0)
                    {
                        throw new InvalidOperationException(
                            "Current-runspace sessions cannot change configuration, history, preferences, variables, imports, paths, working directory, or environment.");
                    }

                    Runspace current = Runspace.DefaultRunspace;
                    if (current is null || current.RunspaceStateInfo.State != RunspaceState.Opened)
                    {
                        throw new InvalidOperationException("No opened current default runspace is available.");
                    }

                    return new FfiPowerShellSession(
                        current,
                        ownsRunspace: false,
                        addToHistory: false,
                        errorPreference: PreferenceInherit);
                }

                InitialSessionState initialState = InitialSessionState.CreateDefault2();
                if (initialConfiguration == ConstrainedLanguageConfiguration)
                {
                    initialState.LanguageMode = PSLanguageMode.ConstrainedLanguage;
                }
                if (executionPolicy == 1)
                {
                    initialState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Restricted;
                }
                string[] fullModulePaths = NormalizeDirectories(allowedModulePaths, "Allowed module path");
                if (fullModulePaths.Length != 0)
                {
                    // Rust supplies only staged, manifest-approved roots. This authorization
                    // manager permits external scripts only beneath those roots.
                    initialState.AuthorizationManager = new FfiApprovedModuleAuthorizationManager(fullModulePaths);
                }
                foreach (string moduleImport in moduleImports)
                {
                    initialState.ImportPSModule(ResolveModuleImport(fullModulePaths, moduleImport));
                }

                Runspace runspace = RunspaceFactory.CreateRunspace(initialState);
                try
                {
                    runspace.Open();
                    if (fullModulePaths.Length != 0)
                    {
                        runspace.SessionStateProxy.SetVariable("env:PSModulePath", string.Join(Path.PathSeparator, fullModulePaths));
                    }
                    foreach (PSPropertyInfo variable in initialVariables.Properties)
                    {
                        runspace.SessionStateProxy.SetVariable(variable.Name, variable.Value);
                    }
                    foreach (PSPropertyInfo variable in environment.Properties)
                    {
                        if (variable.Value is not string value)
                        {
                            throw new InvalidOperationException("Session environment values must be strings.");
                        }
                        runspace.SessionStateProxy.SetVariable($"env:{variable.Name}", value);
                    }
                    if (!string.IsNullOrEmpty(workingDirectory))
                    {
                        string fullWorkingDirectory = NormalizeDirectories(new[] { workingDirectory }, "Working directory")[0];
                        runspace.SessionStateProxy.Path.SetLocation(fullWorkingDirectory);
                    }

                    ApplyPreference(runspace, "ErrorActionPreference", errorPreference);
                    ApplyPreference(runspace, "WarningPreference", warningPreference);
                    ApplyPreference(runspace, "VerbosePreference", verbosePreference);
                    ApplyPreference(runspace, "DebugPreference", debugPreference);
                    ApplyPreference(runspace, "InformationPreference", informationPreference);
                    return new FfiPowerShellSession(
                        runspace,
                        ownsRunspace: true,
                        addToHistory: historyMode == HistoryEnabled,
                        errorPreference: errorPreference);
                }
                catch
                {
                    runspace.Dispose();
                    throw;
                }
            }

            public FfiPowerShellPipeline CreatePipeline()
            {
                lock (gate)
                {
                    if (ownerReleased || disposed)
                    {
                        throw new InvalidOperationException("PowerShell session has been released.");
                    }

                    leaseCount++;
                    try
                    {
                        var powerShell = PowerShell.Create();
                        powerShell.Runspace = runspace;
                        return new FfiPowerShellPipeline(powerShell, this);
                    }
                    catch
                    {
                        leaseCount--;
                        throw;
                    }
                }
            }

            public void BeginInvocation()
            {
                lock (gate)
                {
                    if (disposed)
                    {
                        throw new InvalidOperationException("PowerShell session has been closed.");
                    }

                    activePipelineCount++;
                    AddEventLocked(StateRunning);
                }
            }

            public void EndInvocation(bool faulted)
            {
                lock (gate)
                {
                    activePipelineCount = Math.Max(0, activePipelineCount - 1);
                    invocationCount++;
                    if (addToHistory)
                    {
                        historyCount++;
                    }

                    AddEventLocked(faulted ? StateFaulted : StateOpened);
                }
            }

            public PSInvocationSettings CreateInvocationSettings()
            {
                if (!addToHistory && errorPreference == PreferenceInherit)
                {
                    return null;
                }

                var settings = new PSInvocationSettings
                {
                    AddToHistory = addToHistory,
                };
                if (errorPreference != PreferenceInherit)
                {
                    settings.ErrorActionPreference = ToActionPreference(errorPreference);
                }

                return settings;
            }

            public FfiSessionSnapshot Snapshot()
            {
                lock (gate)
                {
                    uint state = disposed
                        ? StateClosed
                        : activePipelineCount > 0
                            ? StateRunning
                            : MapRunspaceState(runspace.RunspaceStateInfo.State);
                    return new FfiSessionSnapshot(
                        state,
                        MapRunspaceState(runspace.RunspaceStateInfo.State),
                        eventsTruncated ? EventsTruncated : 0,
                        checked((uint)activePipelineCount),
                        checked((uint)events.Count),
                        invocationCount,
                        historyCount);
                }
            }

            public FfiSessionEvent GetEvent(int eventIndex)
            {
                lock (gate)
                {
                    if (eventIndex < 0 || eventIndex >= events.Count)
                    {
                        throw new InvalidOperationException("PowerShell session event index is invalid.");
                    }

                    return events[eventIndex];
                }
            }

            public void SetVariable(string name, object value)
            {
                lock (gate)
                {
                    EnsureVariableMutationAllowed();
                    ValidateVariableName(name);
                    runspace.SessionStateProxy.SetVariable(name, value);
                }
            }

            public bool RemoveVariable(string name)
            {
                lock (gate)
                {
                    EnsureVariableMutationAllowed();
                    ValidateVariableName(name);
                    PSVariable variable = runspace.SessionStateProxy.PSVariable.Get(name);
                    if (variable is null)
                    {
                        return false;
                    }

                    runspace.SessionStateProxy.PSVariable.Remove(name);
                    return true;
                }
            }

            public bool TryGetVariableSnapshot(string name, out FfiSnapshotValue snapshot)
            {
                lock (gate)
                {
                    EnsureVariableMutationAllowed();
                    ValidateVariableName(name);
                    PSVariable variable = runspace.SessionStateProxy.PSVariable.Get(name);
                    if (variable is null)
                    {
                        snapshot = null;
                        return false;
                    }

                    if (!FfiSnapshotCollector.TryEncodeCopiedValue(variable.Value, depth: 0, out snapshot))
                    {
                        throw new FfiUnsupportedValueException(
                            "The requested session variable cannot be represented as a bounded copied PowerShell value.");
                    }

                    return true;
                }
            }

            public void ReleaseOwner()
            {
                lock (gate)
                {
                    if (ownerReleased)
                    {
                        throw new InvalidOperationException("PowerShell session has already been released.");
                    }

                    ownerReleased = true;
                    ReleaseLeaseLocked();
                }
            }

            public void ReleasePipeline()
            {
                lock (gate)
                {
                    ReleaseLeaseLocked();
                }
            }

            private void EnsureVariableMutationAllowed()
            {
                if (ownerReleased || disposed)
                {
                    throw new InvalidOperationException("PowerShell session has been released.");
                }

                if (activePipelineCount != 0)
                {
                    throw new FfiInputBackpressureException(
                        "Session variables cannot be read or changed while a PowerShell invocation is pending or running.");
                }
            }

            private static void ValidateVariableName(string value)
            {
                if (string.IsNullOrEmpty(value) ||
                    value.Length > 64 ||
                    (!IsAsciiLetter(value[0]) && value[0] != '_') ||
                    !value.All(character => IsAsciiLetter(character) || IsAsciiDigit(character) || character == '_'))
                {
                    throw new InvalidOperationException("Session variable names must be bounded ASCII identifiers.");
                }
            }

            private static bool IsAsciiLetter(char value)
            {
                return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            }

            private static bool IsAsciiDigit(char value)
            {
                return value is >= '0' and <= '9';
            }

            private static void ValidatePreference(uint preference, string name)
            {
                if (preference is not (PreferenceInherit or PreferenceContinue or PreferenceSilentlyContinue or PreferenceStop))
                {
                    throw new InvalidOperationException(
                        $"PowerShell session {name} preference is unsupported; only inherit, continue, silently continue, and stop are allowed.");
                }
            }

            private static void ApplyPreference(Runspace runspace, string variable, uint preference)
            {
                if (preference == PreferenceInherit)
                {
                    return;
                }

                runspace.SessionStateProxy.SetVariable(variable, ToActionPreference(preference));
            }

            private static ActionPreference ToActionPreference(uint preference)
            {
                return preference switch
                {
                    PreferenceContinue => ActionPreference.Continue,
                    PreferenceSilentlyContinue => ActionPreference.SilentlyContinue,
                    PreferenceStop => ActionPreference.Stop,
                    _ => throw new InvalidOperationException("PowerShell session preference is invalid."),
                };
            }

            private static uint MapRunspaceState(RunspaceState state)
            {
                return state switch
                {
                    RunspaceState.Opened => StateOpened,
                    RunspaceState.Opening => StateRunning,
                    RunspaceState.Closing or RunspaceState.Closed => StateClosed,
                    _ => StateFaulted,
                };
            }

            private void AddEventLocked(uint state)
            {
                if (events.Count == FfiMaxSessionEvents)
                {
                    eventsTruncated = true;
                    return;
                }

                events.Add(new FfiSessionEvent(checked(++eventSequence), state, 0));
            }

            private void ReleaseLeaseLocked()
            {
                if (leaseCount <= 0)
                {
                    throw new InvalidOperationException("PowerShell session lifetime is invalid.");
                }

                leaseCount--;
                if (leaseCount != 0 || disposed)
                {
                    return;
                }

                disposed = true;
                AddEventLocked(StateClosed);
                if (ownsRunspace)
                {
                    runspace.Dispose();
                }
            }
        }

        private readonly struct FfiSessionSnapshot
        {
            public FfiSessionSnapshot(
                uint state,
                uint runspaceState,
                uint flags,
                uint activePipelineCount,
                uint eventCount,
                long invocationCount,
                long historyCount)
            {
                State = state;
                RunspaceState = runspaceState;
                Flags = flags;
                ActivePipelineCount = activePipelineCount;
                EventCount = eventCount;
                InvocationCount = invocationCount;
                HistoryCount = historyCount;
            }

            public uint State { get; }
            public uint RunspaceState { get; }
            public uint Flags { get; }
            public uint ActivePipelineCount { get; }
            public uint EventCount { get; }
            public long InvocationCount { get; }
            public long HistoryCount { get; }
        }

        private readonly struct FfiSessionEvent
        {
            public FfiSessionEvent(long sequence, uint state, uint flags)
            {
                Sequence = sequence;
                State = state;
                Flags = flags;
            }

            public long Sequence { get; }
            public uint State { get; }
            public uint Flags { get; }
        }

        private sealed class FfiValueFormatException : InvalidOperationException
        {
            public FfiValueFormatException(string message)
                : base(message)
            {
            }
        }

        private sealed class FfiUnsupportedValueException : InvalidOperationException
        {
            public FfiUnsupportedValueException(string message)
                : base(message)
            {
            }
        }

        private sealed class FfiInputNotCompletedException : InvalidOperationException
        {
            public FfiInputNotCompletedException()
                : base("Input was started but not completed. Call CompleteInput before Invoke.")
            {
            }
        }

        private sealed class FfiInputBackpressureException : InvalidOperationException
        {
            public FfiInputBackpressureException(string message)
                : base(message)
            {
            }
        }

        private static unsafe int ReadValue(uint kind, byte* data, int dataLength, FfiCallResult* result, out object value)
        {
            value = null;
            if (dataLength < 0 || dataLength > FfiMaxValuePayloadLength || (dataLength > 0 && data == null))
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "Tagged value payload is invalid or exceeds its bound.");
            }

            try
            {
                value = DecodeValue((FfiValueKind)kind, new ReadOnlySpan<byte>(data, dataLength), 0);
                return FfiStatusSuccess;
            }
            catch (FfiUnsupportedValueException exception)
            {
                return WriteFailure(result, FfiStatusUnsupportedValue, exception);
            }
            catch (FfiValueFormatException exception)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, exception);
            }
            catch (Exception exception)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, exception);
            }
        }

        private static object DecodeValue(FfiValueKind kind, ReadOnlySpan<byte> payload, int depth)
        {
            if (depth > FfiMaxValueDepth)
            {
                throw new FfiValueFormatException("Tagged value nesting exceeds its bound.");
            }

            return kind switch
            {
                FfiValueKind.Null => DecodeNull(payload),
                FfiValueKind.StringUtf8 => DecodeUtf8Value(payload),
                FfiValueKind.Switch => DecodeSwitch(payload),
                FfiValueKind.Boolean => DecodeBoolean(payload),
                FfiValueKind.SignedInteger => DecodeSignedInteger(payload),
                FfiValueKind.UnsignedInteger => DecodeUnsignedInteger(payload),
                FfiValueKind.Double => DecodeDouble(payload),
                FfiValueKind.DecimalUtf8 => DecodeDecimal(payload),
                FfiValueKind.Bytes => payload.ToArray(),
                FfiValueKind.DateTime => DecodeDateTime(payload),
                FfiValueKind.DateTimeOffset => DecodeDateTimeOffset(payload),
                FfiValueKind.GuidUtf8 => DecodeGuid(payload),
                FfiValueKind.UriUtf8 => DecodeUri(payload),
                FfiValueKind.Array => DecodeArray(payload, depth + 1),
                FfiValueKind.PropertyBag => DecodePropertyBag(payload, depth + 1),
                _ => throw new FfiUnsupportedValueException($"Tagged value kind {(uint)kind} is not supported."),
            };
        }

        private static object DecodeNull(ReadOnlySpan<byte> payload)
        {
            RequireLength(payload, 0, "Null");
            return null;
        }

        private static object DecodeSwitch(ReadOnlySpan<byte> payload)
        {
            return new SwitchParameter(DecodeByteBoolean(payload, "Switch"));
        }

        private static object DecodeBoolean(ReadOnlySpan<byte> payload)
        {
            return DecodeByteBoolean(payload, "Boolean");
        }

        private static bool DecodeByteBoolean(ReadOnlySpan<byte> payload, string description)
        {
            RequireLength(payload, 1, description);
            return payload[0] switch
            {
                0 => false,
                1 => true,
                _ => throw new FfiValueFormatException($"{description} payload must be zero or one."),
            };
        }

        private static object DecodeSignedInteger(ReadOnlySpan<byte> payload)
        {
            RequireLength(payload, sizeof(long), "Signed integer");
            return BinaryPrimitives.ReadInt64LittleEndian(payload);
        }

        private static object DecodeUnsignedInteger(ReadOnlySpan<byte> payload)
        {
            RequireLength(payload, sizeof(ulong), "Unsigned integer");
            return BinaryPrimitives.ReadUInt64LittleEndian(payload);
        }

        private static object DecodeDouble(ReadOnlySpan<byte> payload)
        {
            RequireLength(payload, sizeof(long), "Double");
            return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload));
        }

        private static object DecodeDecimal(ReadOnlySpan<byte> payload)
        {
            string text = DecodeUtf8Value(payload);
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
            {
                throw new FfiValueFormatException("Decimal payload is invalid.");
            }

            return value;
        }

        private static object DecodeDateTime(ReadOnlySpan<byte> payload)
        {
            RequireLength(payload, sizeof(long), "DateTime");
            return DateTime.FromBinary(BinaryPrimitives.ReadInt64LittleEndian(payload));
        }

        private static object DecodeDateTimeOffset(ReadOnlySpan<byte> payload)
        {
            RequireLength(payload, sizeof(long) + sizeof(short), "DateTimeOffset");
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload);
            short offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(sizeof(long)));
            return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
        }

        private static object DecodeGuid(ReadOnlySpan<byte> payload)
        {
            string text = DecodeUtf8Value(payload);
            if (!Guid.TryParseExact(text, "D", out Guid value))
            {
                throw new FfiValueFormatException("Guid payload must use the canonical D format.");
            }

            return value;
        }

        private static object DecodeUri(ReadOnlySpan<byte> payload)
        {
            string text = DecodeUtf8Value(payload);
            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri value))
            {
                throw new FfiValueFormatException("URI payload must be an absolute URI.");
            }

            return value;
        }

        private static object DecodeArray(ReadOnlySpan<byte> payload, int depth)
        {
            int offset = 0;
            uint count = ReadUInt32(payload, ref offset, "Array count");
            if (count > FfiMaxValueContainerEntries)
            {
                throw new FfiValueFormatException("Array item count exceeds its bound.");
            }

            var values = new object[checked((int)count)];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = DecodeNestedValue(payload, ref offset, depth);
            }

            RequireComplete(payload, offset, "Array");
            return values;
        }

        private static object DecodePropertyBag(ReadOnlySpan<byte> payload, int depth)
        {
            int offset = 0;
            uint count = ReadUInt32(payload, ref offset, "Property bag count");
            if (count > FfiMaxValueContainerEntries)
            {
                throw new FfiValueFormatException("Property bag entry count exceeds its bound.");
            }

            var propertyBag = new PSObject();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < count; index++)
            {
                uint keyLength = ReadUInt32(payload, ref offset, "Property bag key length");
                ReadOnlySpan<byte> keyBytes = ReadBytes(payload, ref offset, keyLength, "Property bag key");
                string name = DecodeUtf8Value(keyBytes);
                if (name.Length == 0 || !names.Add(name))
                {
                    throw new FfiValueFormatException("Property bag keys must be non-empty and unique.");
                }

                propertyBag.Properties.Add(new PSNoteProperty(name, DecodeNestedValue(payload, ref offset, depth)));
            }

            RequireComplete(payload, offset, "Property bag");
            return propertyBag;
        }

        private static object DecodeNestedValue(ReadOnlySpan<byte> payload, ref int offset, int depth)
        {
            uint rawKind = ReadUInt32(payload, ref offset, "Nested value kind");
            uint length = ReadUInt32(payload, ref offset, "Nested value length");
            return DecodeValue((FfiValueKind)rawKind, ReadBytes(payload, ref offset, length, "Nested value"), depth);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> payload, ref int offset, string description)
        {
            ReadOnlySpan<byte> bytes = ReadBytes(payload, ref offset, sizeof(uint), description);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> payload, ref int offset, uint length, string description)
        {
            if (offset < 0 || offset > payload.Length || length > (uint)(payload.Length - offset))
            {
                throw new FfiValueFormatException($"{description} is truncated.");
            }

            int valueLength = (int)length;
            ReadOnlySpan<byte> value = payload.Slice(offset, valueLength);
            offset += valueLength;
            return value;
        }

        private static string DecodeUtf8Value(ReadOnlySpan<byte> payload)
        {
            try
            {
                if (payload.IndexOf((byte)0) >= 0)
                {
                    throw new FfiValueFormatException("Tagged UTF-8 values cannot contain NUL.");
                }

                return new UTF8Encoding(false, true).GetString(payload);
            }
            catch (DecoderFallbackException exception)
            {
                throw new FfiValueFormatException(exception.Message);
            }
        }

        private static void RequireLength(ReadOnlySpan<byte> payload, int length, string description)
        {
            if (payload.Length != length)
            {
                throw new FfiValueFormatException($"{description} payload length is invalid.");
            }
        }

        private static void RequireComplete(ReadOnlySpan<byte> payload, int offset, string description)
        {
            if (offset != payload.Length)
            {
                throw new FfiValueFormatException($"{description} payload contains trailing bytes.");
            }
        }

        private static void AddInputValue(IntPtr ptrHandle, object value, int dataLength)
        {
            GetPowerShell(ptrHandle);
            FfiInputBuffer input = FfiInputBuffers.GetOrAdd(ptrHandle, _ => new FfiInputBuffer());
            lock (input.Gate)
            {
                if (input.IsCompleted)
                {
                    throw new FfiInputBackpressureException("Input is already completed. Call ResetInput before adding more values.");
                }

                if (input.Values.Count == FfiMaxInputValues ||
                    dataLength > FfiMaxInputPayloadLength - input.PayloadLength)
                {
                    throw new FfiInputBackpressureException("Input capacity has been reached. Invoke or reset the pipeline.");
                }

                input.Values.Add(value);
                input.PayloadLength += dataLength;
            }
        }

        private static void CompleteInput(IntPtr ptrHandle)
        {
            GetPowerShell(ptrHandle);
            FfiInputBuffer input = FfiInputBuffers.GetOrAdd(ptrHandle, _ => new FfiInputBuffer());
            lock (input.Gate)
            {
                input.IsCompleted = true;
            }
        }

        private static object[] TakeCompletedInput(IntPtr ptrHandle)
        {
            if (!FfiInputBuffers.TryGetValue(ptrHandle, out FfiInputBuffer input))
            {
                return null;
            }

            lock (input.Gate)
            {
                if (!input.IsCompleted)
                {
                    throw new FfiInputNotCompletedException();
                }

                object[] values = input.Values.ToArray();
                FfiInputBuffers.TryRemove(ptrHandle, out _);
                return values;
            }
        }

        private enum FfiStreamKind
        {
            Output = 0,
            Error = 1,
            Warning = 2,
            Verbose = 3,
            Debug = 4,
            Information = 5,
            Progress = 6,
        }

        private sealed class FfiInvocationResultSnapshot
        {
            public FfiInvocationResultSnapshot(
                FfiStreamSnapshot[] streams,
                FfiSequenceRecord[] sequence,
                uint flags,
                long invocationId)
            {
                Streams = streams;
                Sequence = sequence;
                Flags = flags;
                InvocationId = invocationId;
            }

            public FfiStreamSnapshot[] Streams { get; }

            public FfiSequenceRecord[] Sequence { get; }

            public uint Flags { get; }

            public long InvocationId { get; }

            public uint State => (Flags & FfiResultTerminatingFailure) != 0 ? 2u : 1u;

            public bool HadErrors => Streams[(int)FfiStreamKind.Error].TotalRecordCount != 0;

            public FfiStreamSnapshot GetStream(int stream)
            {
                if (stream < 0 || stream >= Streams.Length)
                {
                    throw new InvalidOperationException("Invocation stream is invalid.");
                }

                return Streams[stream];
            }

            public FfiSequenceRecord GetSequenceRecord(int index)
            {
                if (index < 0 || index >= Sequence.Length)
                {
                    throw new InvalidOperationException("Invocation sequence index is invalid.");
                }

                return Sequence[index];
            }
        }

        private sealed class FfiStreamSnapshot
        {
            public FfiStreamSnapshot(FfiStreamRecord[] records, uint flags, long totalRecordCount)
            {
                Records = records;
                Flags = flags;
                TotalRecordCount = totalRecordCount;
            }

            public FfiStreamRecord[] Records { get; }

            public uint Flags { get; }

            public long TotalRecordCount { get; }

            public long DroppedRecordCount => Math.Max(0, TotalRecordCount - Records.Length);

            public FfiStreamRecord GetRecord(int index)
            {
                if (index < 0 || index >= Records.Length)
                {
                    throw new InvalidOperationException("Invocation stream record index is invalid.");
                }

                return Records[index];
            }
        }

        private sealed class FfiStreamRecord
        {
            public FfiStreamRecord(
                long sequence,
                string[] fields,
                uint flags,
                FfiSnapshotValue scalarValue,
                FfiSnapshotValue propertyBagValue,
                FfiSnapshotValue errorTargetValue,
                int propertyEntryCount,
                int droppedPropertyEntryCount,
                int typeNameCount,
                int droppedTypeNameCount)
            {
                Sequence = sequence;
                Fields = fields;
                Flags = flags;
                ScalarValue = scalarValue;
                PropertyBagValue = propertyBagValue;
                ErrorTargetValue = errorTargetValue;
                PropertyEntryCount = propertyEntryCount;
                DroppedPropertyEntryCount = droppedPropertyEntryCount;
                TypeNameCount = typeNameCount;
                DroppedTypeNameCount = droppedTypeNameCount;
            }

            public long Sequence { get; }

            public string[] Fields { get; }

            public uint Flags { get; }

            public FfiSnapshotValue ScalarValue { get; }

            public FfiSnapshotValue PropertyBagValue { get; }

            public FfiSnapshotValue ErrorTargetValue { get; }

            public int PropertyEntryCount { get; }

            public int DroppedPropertyEntryCount { get; }

            public int TypeNameCount { get; }

            public int DroppedTypeNameCount { get; }

            public string GetField(int field)
            {
                if (field < 0 || field >= Fields.Length)
                {
                    throw new InvalidOperationException("Invocation stream record field is invalid.");
                }

                return Fields[field];
            }

            public FfiSnapshotValue GetValue(int slot)
            {
                return slot switch
                {
                    0 when ScalarValue != null => ScalarValue,
                    1 when PropertyBagValue != null => PropertyBagValue,
                    2 when ErrorTargetValue != null => ErrorTargetValue,
                    _ => throw new InvalidOperationException("Invocation stream record value is unavailable."),
                };
            }
        }

        private sealed class FfiSnapshotValue
        {
            public FfiSnapshotValue(uint kind, byte[] payload)
            {
                Kind = kind;
                Payload = payload;
            }

            public uint Kind { get; }

            public byte[] Payload { get; }
        }

        private sealed class FfiSequenceRecord
        {
            public FfiSequenceRecord(int stream, int recordIndex, long sequence)
            {
                Stream = stream;
                RecordIndex = recordIndex;
                Sequence = sequence;
            }

            public int Stream { get; }

            public int RecordIndex { get; }

            public long Sequence { get; }
        }

        private sealed class FfiSnapshotCollector
        {
            private const int FieldCount = 20;
            private readonly List<FfiStreamRecord>[] streams;
            private readonly uint[] streamFlags;
            private readonly long[] streamTotalRecordCounts;
            private readonly List<FfiSequenceRecord> sequence;
            private long nextSequence;
            private bool terminatingFailure;
            private bool sequenceTruncated;

            public FfiSnapshotCollector()
            {
                streams = new List<FfiStreamRecord>[FfiStreamCount];
                streamFlags = new uint[FfiStreamCount];
                streamTotalRecordCounts = new long[FfiStreamCount];
                for (int index = 0; index < streams.Length; index++)
                {
                    streams[index] = new List<FfiStreamRecord>(FfiMaxStreamRecords);
                }

                sequence = new List<FfiSequenceRecord>(FfiMaxStreamRecords * FfiStreamCount);
            }

            public long ErrorCount => streamTotalRecordCounts[(int)FfiStreamKind.Error];

            public void MarkTerminatingFailure()
            {
                terminatingFailure = true;
            }

            public void AddOutput(PSObject value)
            {
                bool truncated = false;
                string[] fields = CreateFields();
                fields[0] = Bound(SafeDisplayText(value), ref truncated);
                fields[1] = Bound(CaptureTypeNames(value, out int typeNameCount, out int droppedTypeNameCount, ref truncated), ref truncated);
                FfiSnapshotValue scalarValue = TryGetBaseObject(value, out object baseObject) &&
                    TryProjectScalar(baseObject, FfiMaxSnapshotScalarPayloadLength, out FfiSnapshotValue projectedScalar)
                    ? projectedScalar
                    : null;
                FfiSnapshotValue propertyBagValue = TryProjectPropertyBag(
                    value,
                    out int propertyEntryCount,
                    out int droppedPropertyEntryCount,
                    out bool propertyBagTruncated);
                uint projectionFlags = 0;
                if (scalarValue != null)
                {
                    projectionFlags |= FfiRecordScalarValuePresent;
                }
                if (propertyBagValue != null)
                {
                    projectionFlags |= FfiRecordPropertyBagPresent;
                }
                if (propertyBagTruncated)
                {
                    projectionFlags |= FfiRecordPropertyBagTruncated;
                }
                if (droppedTypeNameCount != 0)
                {
                    projectionFlags |= FfiRecordTypeNamesTruncated;
                }

                Add(
                    FfiStreamKind.Output,
                    fields,
                    truncated,
                    projectionFlags,
                    scalarValue,
                    propertyBagValue,
                    null,
                    propertyEntryCount,
                    droppedPropertyEntryCount,
                    typeNameCount,
                    droppedTypeNameCount);
            }

            public void AddError(ErrorRecord value, Exception fallbackException = null)
            {
                bool truncated = false;
                Exception exception = SafeGet(() => value?.Exception) ?? fallbackException;
                InvocationInfo invocation = SafeGet(() => value?.InvocationInfo);
                ErrorCategoryInfo category = SafeGet(() => value?.CategoryInfo);
                ErrorDetails details = SafeGet(() => value?.ErrorDetails);
                object target = SafeGet(() => value?.TargetObject);
                string[] fields = CreateFields();
                fields[0] = Bound(SafeGet(() => value?.ToString()) ?? fallbackException?.Message ?? string.Empty, ref truncated);
                fields[2] = Bound(SafeGet(() => value?.FullyQualifiedErrorId) ?? string.Empty, ref truncated);
                fields[3] = Bound(SafeGet(() => category?.Category.ToString()) ?? string.Empty, ref truncated);
                fields[4] = Bound(exception?.GetType().FullName ?? string.Empty, ref truncated);
                fields[5] = Bound(SafeGet(() => invocation?.InvocationName) ?? string.Empty, ref truncated);
                fields[6] = Bound(SafeGet(() => invocation?.PositionMessage) ?? string.Empty, ref truncated);
                fields[7] = Bound(SafeGet(() => value?.ScriptStackTrace) ?? string.Empty, ref truncated);
                fields[8] = Bound(SafeGet(() => category?.Reason) ?? string.Empty, ref truncated);
                fields[9] = Bound(SafeGet(() => category?.Activity) ?? string.Empty, ref truncated);
                fields[10] = Bound(SafeGet(() => category?.TargetName) ?? string.Empty, ref truncated);
                fields[11] = Bound(SafeGet(() => category?.TargetType) ?? string.Empty, ref truncated);
                fields[12] = Bound(SafeGet(() => invocation?.MyCommand?.Name) ?? string.Empty, ref truncated);
                fields[13] = Bound(SafeGet(() => invocation?.Line) ?? string.Empty, ref truncated);
                fields[14] = Bound(SafeGet(() => invocation?.OffsetInLine.ToString(CultureInfo.InvariantCulture)) ?? string.Empty, ref truncated);
                fields[15] = Bound(SafeGet(() => invocation?.PipelineLength.ToString(CultureInfo.InvariantCulture)) ?? string.Empty, ref truncated);
                fields[16] = Bound(SafeGet(() => invocation?.PipelinePosition.ToString(CultureInfo.InvariantCulture)) ?? string.Empty, ref truncated);
                fields[17] = Bound(SafeGet(() => details?.Message) ?? string.Empty, ref truncated);
                fields[18] = Bound(SafeGet(() => details?.RecommendedAction) ?? string.Empty, ref truncated);
                fields[19] = Bound(SafeDisplayText(target), ref truncated);
                FfiSnapshotValue targetValue = TryProjectScalar(target, FfiMaxSnapshotScalarPayloadLength, out FfiSnapshotValue projectedTarget)
                    ? projectedTarget
                    : null;
                Add(
                    FfiStreamKind.Error,
                    fields,
                    truncated,
                    targetValue == null ? 0 : FfiRecordErrorTargetValuePresent,
                    null,
                    null,
                    targetValue,
                    0,
                    0,
                    0,
                    0);
            }

            public void AddText(FfiStreamKind stream, object value)
            {
                bool truncated = false;
                string[] fields = CreateFields();
                fields[0] = Bound(SafeDisplayText(value), ref truncated);
                Add(stream, fields, truncated);
            }

            public FfiInvocationResultSnapshot Build()
            {
                var snapshotStreams = new FfiStreamSnapshot[FfiStreamCount];
                for (int index = 0; index < snapshotStreams.Length; index++)
                {
                    snapshotStreams[index] = new FfiStreamSnapshot(
                        streams[index].ToArray(),
                        streamFlags[index],
                        streamTotalRecordCounts[index]);
                }

                uint flags = 0;
                if (terminatingFailure)
                {
                    flags |= FfiResultTerminatingFailure;
                }

                if (sequenceTruncated)
                {
                    flags |= FfiResultSequenceTruncated;
                }

                return new FfiInvocationResultSnapshot(
                    snapshotStreams,
                    sequence.ToArray(),
                    flags,
                    Interlocked.Increment(ref FfiNextInvocationId));
            }

            private void Add(FfiStreamKind stream, string[] fields, bool fieldsTruncated)
            {
                Add(stream, fields, fieldsTruncated, 0, null, null, null, 0, 0, 0, 0);
            }

            private void Add(
                FfiStreamKind stream,
                string[] fields,
                bool fieldsTruncated,
                uint projectionFlags,
                FfiSnapshotValue scalarValue,
                FfiSnapshotValue propertyBagValue,
                FfiSnapshotValue errorTargetValue,
                int propertyEntryCount,
                int droppedPropertyEntryCount,
                int typeNameCount,
                int droppedTypeNameCount)
            {
                int streamIndex = (int)stream;
                if (streamTotalRecordCounts[streamIndex] != long.MaxValue)
                {
                    streamTotalRecordCounts[streamIndex]++;
                }

                long currentSequence = nextSequence == long.MaxValue ? long.MaxValue : nextSequence++;
                List<FfiStreamRecord> records = streams[streamIndex];
                if (records.Count == FfiMaxStreamRecords)
                {
                    streamFlags[streamIndex] |= FfiStreamTruncated;
                    sequenceTruncated = true;
                    return;
                }

                uint flags = projectionFlags;
                if (fieldsTruncated)
                {
                    flags |= FfiRecordFieldsTruncated;
                }
                int recordIndex = records.Count;
                records.Add(new FfiStreamRecord(
                    currentSequence,
                    fields,
                    flags,
                    scalarValue,
                    propertyBagValue,
                    errorTargetValue,
                    propertyEntryCount,
                    droppedPropertyEntryCount,
                    typeNameCount,
                    droppedTypeNameCount));
                sequence.Add(new FfiSequenceRecord(streamIndex, recordIndex, currentSequence));
            }

            private static string[] CreateFields()
            {
                string[] fields = new string[FieldCount];
                for (int index = 0; index < fields.Length; index++)
                {
                    fields[index] = string.Empty;
                }

                return fields;
            }

            private static string Bound(string value, ref bool truncated)
            {
                value ??= string.Empty;
                if (value.Length <= FfiMaxStreamFieldLength)
                {
                    return value;
                }

                truncated = true;
                return value.Substring(0, FfiMaxStreamFieldLength);
            }

            private static string SafeDisplayText(object value)
            {
                try
                {
                    return value?.ToString() ?? string.Empty;
                }
                catch (Exception exception)
                {
                    return $"<{exception.GetType().Name}: display text unavailable>";
                }
            }

            private static T SafeGet<T>(Func<T> getter)
            {
                try
                {
                    return getter();
                }
                catch
                {
                    return default;
                }
            }

            private static bool TryGetBaseObject(PSObject value, out object baseObject)
            {
                baseObject = null;
                try
                {
                    baseObject = value?.BaseObject;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static string CaptureTypeNames(
                PSObject value,
                out int retainedCount,
                out int droppedCount,
                ref bool truncated)
            {
                var names = new List<string>(8);
                droppedCount = 0;
                try
                {
                    if (value != null)
                    {
                        foreach (string name in value.TypeNames)
                        {
                            if (names.Count == 8)
                            {
                                IncrementSaturating(ref droppedCount);
                                continue;
                            }

                            string normalized = (name ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
                            names.Add(Bound(normalized, ref truncated));
                        }
                    }
                }
                catch
                {
                    IncrementSaturating(ref droppedCount);
                }

                retainedCount = names.Count;
                if (droppedCount != 0)
                {
                    truncated = true;
                }

                return string.Join("\n", names);
            }

            private static FfiSnapshotValue TryProjectPropertyBag(
                PSObject value,
                out int retainedCount,
                out int droppedCount,
                out bool truncated)
            {
                retainedCount = 0;
                droppedCount = 0;
                truncated = false;
                if (value == null)
                {
                    return null;
                }

                var entries = new List<KeyValuePair<string, FfiSnapshotValue>>(FfiMaxSnapshotPropertyEntries);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int encodedLength = sizeof(uint);
                bool hasNoteProperties = false;
                try
                {
                    foreach (PSPropertyInfo property in value.Properties)
                    {
                        if (property is not PSNoteProperty)
                        {
                            continue;
                        }

                        hasNoteProperties = true;
                        string name = property.Name;
                        if (string.IsNullOrEmpty(name) ||
                            name.Length > FfiMaxSnapshotPropertyNameLength ||
                            name.IndexOf('\0') >= 0 ||
                            !names.Add(name))
                        {
                            IncrementSaturating(ref droppedCount);
                            continue;
                        }

                        object propertyValue;
                        try
                        {
                            propertyValue = property.Value;
                        }
                        catch
                        {
                            IncrementSaturating(ref droppedCount);
                            continue;
                        }

                        if (!TryProjectScalar(propertyValue, FfiMaxSnapshotScalarPayloadLength, out FfiSnapshotValue scalar))
                        {
                            IncrementSaturating(ref droppedCount);
                            continue;
                        }

                        byte[] nameBytes = new UTF8Encoding(false, true).GetBytes(name);
                        int entryLength = sizeof(uint) + nameBytes.Length + sizeof(uint) + sizeof(uint) + scalar.Payload.Length;
                        if (entries.Count == FfiMaxSnapshotPropertyEntries ||
                            entryLength > FfiMaxSnapshotPropertyBagPayloadLength - encodedLength)
                        {
                            IncrementSaturating(ref droppedCount);
                            continue;
                        }

                        encodedLength += entryLength;
                        entries.Add(new KeyValuePair<string, FfiSnapshotValue>(name, scalar));
                    }
                }
                catch
                {
                    IncrementSaturating(ref droppedCount);
                }

                if (!hasNoteProperties)
                {
                    return null;
                }

                entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
                var bytes = new List<byte>(encodedLength);
                WriteUInt32(bytes, checked((uint)entries.Count));
                foreach (KeyValuePair<string, FfiSnapshotValue> entry in entries)
                {
                    byte[] name = new UTF8Encoding(false, true).GetBytes(entry.Key);
                    WriteUInt32(bytes, checked((uint)name.Length));
                    bytes.AddRange(name);
                    WriteUInt32(bytes, entry.Value.Kind);
                    WriteUInt32(bytes, checked((uint)entry.Value.Payload.Length));
                    bytes.AddRange(entry.Value.Payload);
                }

                retainedCount = entries.Count;
                truncated = droppedCount != 0;
                return new FfiSnapshotValue((uint)FfiValueKind.PropertyBag, bytes.ToArray());
            }

            private static bool TryProjectScalar(object value, int maximumPayloadLength, out FfiSnapshotValue scalar)
            {
                scalar = null;
                try
                {
                    uint kind;
                    byte[] payload;
                    switch (value)
                    {
                        case null:
                            kind = (uint)FfiValueKind.Null;
                            payload = Array.Empty<byte>();
                            break;
                        case string text when text.IndexOf('\0') < 0:
                            kind = (uint)FfiValueKind.StringUtf8;
                            payload = new UTF8Encoding(false, true).GetBytes(text);
                            break;
                        case SwitchParameter switchParameter:
                            kind = (uint)FfiValueKind.Switch;
                            payload = [switchParameter.IsPresent ? (byte)1 : (byte)0];
                            break;
                        case bool boolean:
                            kind = (uint)FfiValueKind.Boolean;
                            payload = [boolean ? (byte)1 : (byte)0];
                            break;
                        case sbyte signed:
                            kind = (uint)FfiValueKind.SignedInteger;
                            payload = BitConverter.GetBytes((long)signed);
                            break;
                        case short signed:
                            kind = (uint)FfiValueKind.SignedInteger;
                            payload = BitConverter.GetBytes((long)signed);
                            break;
                        case int signed:
                            kind = (uint)FfiValueKind.SignedInteger;
                            payload = BitConverter.GetBytes((long)signed);
                            break;
                        case long signed:
                            kind = (uint)FfiValueKind.SignedInteger;
                            payload = BitConverter.GetBytes(signed);
                            break;
                        case byte unsigned:
                            kind = (uint)FfiValueKind.UnsignedInteger;
                            payload = BitConverter.GetBytes((ulong)unsigned);
                            break;
                        case ushort unsigned:
                            kind = (uint)FfiValueKind.UnsignedInteger;
                            payload = BitConverter.GetBytes((ulong)unsigned);
                            break;
                        case uint unsigned:
                            kind = (uint)FfiValueKind.UnsignedInteger;
                            payload = BitConverter.GetBytes((ulong)unsigned);
                            break;
                        case ulong unsigned:
                            kind = (uint)FfiValueKind.UnsignedInteger;
                            payload = BitConverter.GetBytes(unsigned);
                            break;
                        case float floating:
                            kind = (uint)FfiValueKind.Double;
                            payload = BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(floating));
                            break;
                        case double floating:
                            kind = (uint)FfiValueKind.Double;
                            payload = BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(floating));
                            break;
                        case decimal decimalValue:
                            kind = (uint)FfiValueKind.DecimalUtf8;
                            payload = new UTF8Encoding(false, true).GetBytes(decimalValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case byte[] bytes:
                            kind = (uint)FfiValueKind.Bytes;
                            payload = (byte[])bytes.Clone();
                            break;
                        case DateTime dateTime:
                            kind = (uint)FfiValueKind.DateTime;
                            payload = BitConverter.GetBytes(dateTime.ToBinary());
                            break;
                        case DateTimeOffset dateTimeOffset:
                            kind = (uint)FfiValueKind.DateTimeOffset;
                            payload = new byte[sizeof(long) + sizeof(short)];
                            BinaryPrimitives.WriteInt64LittleEndian(payload, dateTimeOffset.Ticks);
                            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(sizeof(long)), checked((short)dateTimeOffset.Offset.TotalMinutes));
                            break;
                        case Guid guid:
                            kind = (uint)FfiValueKind.GuidUtf8;
                            payload = new UTF8Encoding(false, true).GetBytes(guid.ToString("D"));
                            break;
                        case Uri uri when uri.IsAbsoluteUri && uri.AbsoluteUri.IndexOf('\0') < 0:
                            kind = (uint)FfiValueKind.UriUtf8;
                            payload = new UTF8Encoding(false, true).GetBytes(uri.AbsoluteUri);
                            break;
                        default:
                            return false;
                    }

                    if (payload.Length > maximumPayloadLength)
                    {
                        return false;
                    }

                    scalar = new FfiSnapshotValue(kind, payload);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            internal static bool TryEncodeCopiedValue(object value, int depth, out FfiSnapshotValue copied)
            {
                return TryEncodeCopiedValue(
                    value,
                    depth,
                    new HashSet<object>(ReferenceEqualityComparer.Instance),
                    out copied);
            }

            private static bool TryEncodeCopiedValue(
                object value,
                int depth,
                HashSet<object> ancestors,
                out FfiSnapshotValue copied)
            {
                copied = null;
                if (depth > FfiMaxValueDepth)
                {
                    return false;
                }

                if (TryProjectScalar(value, FfiMaxValuePayloadLength, out copied))
                {
                    return true;
                }

                if (value is object[] array)
                {
                    if (array.Length > FfiMaxValueContainerEntries || !ancestors.Add(array))
                    {
                        return false;
                    }

                    try
                    {
                        var bytes = new List<byte>(sizeof(uint));
                        WriteUInt32(bytes, checked((uint)array.Length));
                        foreach (object item in array)
                        {
                            if (!TryEncodeCopiedValue(item, depth + 1, ancestors, out FfiSnapshotValue nested) ||
                                !TryAppendNestedValue(bytes, nested))
                            {
                                return false;
                            }
                        }

                        copied = new FfiSnapshotValue((uint)FfiValueKind.Array, bytes.ToArray());
                        return true;
                    }
                    finally
                    {
                        ancestors.Remove(array);
                    }
                }

                if (value is not PSObject propertyBag || !ancestors.Add(propertyBag))
                {
                    return false;
                }

                try
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var entries = new List<KeyValuePair<string, FfiSnapshotValue>>();
                    foreach (PSPropertyInfo property in propertyBag.Properties)
                    {
                        if (property is not PSNoteProperty ||
                            string.IsNullOrEmpty(property.Name) ||
                            property.Name.IndexOf('\0') >= 0 ||
                            !names.Add(property.Name) ||
                            entries.Count == FfiMaxValueContainerEntries ||
                            !TryEncodeCopiedValue(property.Value, depth + 1, ancestors, out FfiSnapshotValue nested))
                        {
                            return false;
                        }

                        entries.Add(new KeyValuePair<string, FfiSnapshotValue>(property.Name, nested));
                    }

                    var payload = new List<byte>(sizeof(uint));
                    WriteUInt32(payload, checked((uint)entries.Count));
                    foreach (KeyValuePair<string, FfiSnapshotValue> entry in entries)
                    {
                        byte[] name = new UTF8Encoding(false, true).GetBytes(entry.Key);
                        if (name.Length > FfiMaxValuePayloadLength - payload.Count - sizeof(uint))
                        {
                            return false;
                        }

                        WriteUInt32(payload, checked((uint)name.Length));
                        if (!TryAppendBytes(payload, name) || !TryAppendNestedValue(payload, entry.Value))
                        {
                            return false;
                        }
                    }

                    copied = new FfiSnapshotValue((uint)FfiValueKind.PropertyBag, payload.ToArray());
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    ancestors.Remove(propertyBag);
                }
            }

            private static bool TryAppendNestedValue(List<byte> destination, FfiSnapshotValue value)
            {
                if (value.Payload.Length > FfiMaxValuePayloadLength - destination.Count - (sizeof(uint) * 2))
                {
                    return false;
                }

                WriteUInt32(destination, value.Kind);
                WriteUInt32(destination, checked((uint)value.Payload.Length));
                return TryAppendBytes(destination, value.Payload);
            }

            private static bool TryAppendBytes(List<byte> destination, byte[] value)
            {
                if (value.Length > FfiMaxValuePayloadLength - destination.Count)
                {
                    return false;
                }

                destination.AddRange(value);
                return true;
            }

            private static void WriteUInt32(List<byte> bytes, uint value)
            {
                bytes.Add((byte)value);
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)(value >> 16));
                bytes.Add((byte)(value >> 24));
            }

            private static void IncrementSaturating(ref int value)
            {
                if (value != int.MaxValue)
                {
                    value++;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct FfiNativeUtf8Span
        {
            public byte* Data;
            public nuint Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct FfiNativeDataValue
        {
            public uint Size;
            public uint Kind;
            public uint Flags;
            public uint Reserved;
            public byte* Data;
            public nuint DataLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct FfiNativeCallResult
        {
            public uint Size;
            public int Status;
            public uint Flags;
            public uint Reserved;
            public byte* Diagnostic;
            public nuint DiagnosticCapacity;
            public nuint DiagnosticRequired;
            public nuint DiagnosticWritten;
        }

        private sealed unsafe class FfiCapabilityContext
        {
            private readonly ulong registrationHandle;
            private readonly ulong invocationId;
            private readonly IntPtr dispatcher;

            public FfiCapabilityContext(ulong registrationHandle, ulong invocationId, IntPtr dispatcher)
            {
                this.registrationHandle = registrationHandle;
                this.invocationId = invocationId;
                this.dispatcher = dispatcher;
            }

            public object Invoke(string capabilityName, object[] arguments)
            {
                if (string.IsNullOrEmpty(capabilityName) ||
                    capabilityName.Length > 64 ||
                    arguments.Length > FfiMaxValueContainerEntries ||
                    !FfiSnapshotCollector.TryEncodeCopiedValue(arguments, depth: 0, out FfiSnapshotValue encoded))
                {
                    throw new InvalidOperationException("Capability invocation arguments are invalid.");
                }

                byte[] name = new UTF8Encoding(false, true).GetBytes(capabilityName);
                if (name.Length == 0 || name.Length > 64 || encoded.Payload.Length > FfiMaxValuePayloadLength)
                {
                    throw new InvalidOperationException("Capability invocation arguments exceed their bound.");
                }

                byte[] response = new byte[FfiMaxValuePayloadLength];
                byte[] diagnostic = new byte[512];
                fixed (byte* namePointer = name)
                fixed (byte* inputPointer = encoded.Payload)
                fixed (byte* responsePointer = response)
                fixed (byte* diagnosticPointer = diagnostic)
                {
                    FfiNativeDataValue input = new()
                    {
                        Size = (uint)sizeof(FfiNativeDataValue),
                        Kind = encoded.Kind,
                        Data = inputPointer,
                        DataLength = (nuint)encoded.Payload.Length,
                    };
                    FfiNativeCallResult result = new()
                    {
                        Size = (uint)sizeof(FfiNativeCallResult),
                        Diagnostic = diagnosticPointer,
                        DiagnosticCapacity = (nuint)diagnostic.Length,
                    };
                    uint responseKind = 0;
                    nuint responseRequired = 0;
                    var dispatch = (delegate* unmanaged[Cdecl]<
                        ulong,
                        ulong,
                        FfiNativeUtf8Span,
                        FfiNativeDataValue*,
                        uint,
                        uint,
                        uint*,
                        byte*,
                        nuint,
                        nuint*,
                        FfiNativeCallResult*,
                        int>)dispatcher;
                    int status = dispatch(
                        registrationHandle,
                        invocationId,
                        new FfiNativeUtf8Span { Data = namePointer, Length = (nuint)name.Length },
                        &input,
                        (uint)arguments.Length,
                        30_000,
                        &responseKind,
                        responsePointer,
                        (nuint)response.Length,
                        &responseRequired,
                        &result);
                    if (status != FfiStatusSuccess || result.Status != FfiStatusSuccess ||
                        responseRequired > (nuint)response.Length)
                    {
                        throw new InvalidOperationException("The bounded capability invocation failed.");
                    }

                    return DecodeValue(
                        (FfiValueKind)responseKind,
                        new ReadOnlySpan<byte>(responsePointer, checked((int)responseRequired)),
                        depth: 0);
                }
            }
        }

        private sealed class FfiCapabilityBridge
        {
            private readonly FfiCapabilityContext context;

            public FfiCapabilityBridge(FfiCapabilityContext context)
            {
                this.context = context;
            }

            public object Invoke(string capabilityName, params object[] arguments)
            {
                return context.Invoke(capabilityName, arguments ?? Array.Empty<object>());
            }
        }

        private static FfiInvocationResultSnapshot InvokeAndCaptureStreamSnapshot(
            PowerShell ps,
            object[] input = null,
            FfiPowerShellSession session = null,
            FfiCapabilityContext capabilityContext = null)
        {
            var collector = new FfiSnapshotCollector();
            Exception terminatingException = null;
            ErrorRecord terminatingError = null;
            var output = new PSDataCollection<PSObject> { DataAddedCount = 1 };
            session?.BeginInvocation();
            PSInvocationSettings invocationSettings = session?.CreateInvocationSettings();
            Runspace capabilityRunspace = null;
            PSVariable previousCapabilityVariable = null;

            EventHandler<DataAddedEventArgs> outputAdded = (_, args) =>
            {
                collector.AddOutput(output[args.Index]);
                output.Clear();
            };
            EventHandler<DataAddedEventArgs> errorAdded = (_, args) =>
            {
                collector.AddError(ps.Streams.Error[args.Index]);
                ps.Streams.Error.Clear();
            };
            EventHandler<DataAddedEventArgs> warningAdded = (_, args) =>
            {
                collector.AddText(FfiStreamKind.Warning, ps.Streams.Warning[args.Index]);
                ps.Streams.Warning.Clear();
            };
            EventHandler<DataAddedEventArgs> verboseAdded = (_, args) =>
            {
                collector.AddText(FfiStreamKind.Verbose, ps.Streams.Verbose[args.Index]);
                ps.Streams.Verbose.Clear();
            };
            EventHandler<DataAddedEventArgs> debugAdded = (_, args) =>
            {
                collector.AddText(FfiStreamKind.Debug, ps.Streams.Debug[args.Index]);
                ps.Streams.Debug.Clear();
            };
            EventHandler<DataAddedEventArgs> informationAdded = (_, args) =>
            {
                collector.AddText(FfiStreamKind.Information, ps.Streams.Information[args.Index]);
                ps.Streams.Information.Clear();
            };
            EventHandler<DataAddedEventArgs> progressAdded = (_, args) =>
            {
                collector.AddText(FfiStreamKind.Progress, ps.Streams.Progress[args.Index]);
                ps.Streams.Progress.Clear();
            };

            ClearStreamBuffers(ps);
            output.DataAdded += outputAdded;
            ps.Streams.Error.DataAdded += errorAdded;
            ps.Streams.Warning.DataAdded += warningAdded;
            ps.Streams.Verbose.DataAdded += verboseAdded;
            ps.Streams.Debug.DataAdded += debugAdded;
            ps.Streams.Information.DataAdded += informationAdded;
            ps.Streams.Progress.DataAdded += progressAdded;
            try
            {
                if (capabilityContext != null)
                {
                    capabilityRunspace = ps.Runspace ?? throw new InvalidOperationException(
                        "Bounded capability RPC requires a PowerShell pipeline with an explicit local runspace.");
                    previousCapabilityVariable = capabilityRunspace.SessionStateProxy.PSVariable.Get("DpsCapabilities");
                    capabilityRunspace.SessionStateProxy.SetVariable(
                        "DpsCapabilities",
                        new FfiCapabilityBridge(capabilityContext));
                }
                if (input == null)
                {
                    ps.Invoke<PSObject, PSObject>(null, output, invocationSettings);
                }
                else
                {
                    var inputCollection = new PSDataCollection<object>();
                    foreach (object value in input)
                    {
                        inputCollection.Add(value);
                    }

                    inputCollection.Complete();
                    ps.Invoke<object, PSObject>(inputCollection, output, invocationSettings);
                    inputCollection.Clear();
                }
                foreach (PSObject value in output)
                {
                    collector.AddOutput(value);
                }
            }
            catch (RuntimeException exception)
            {
                collector.MarkTerminatingFailure();
                terminatingException = exception;
                terminatingError = exception.ErrorRecord;
            }
            catch (Exception exception)
            {
                collector.MarkTerminatingFailure();
                terminatingException = exception;
            }
            finally
            {
                output.DataAdded -= outputAdded;
                ps.Streams.Error.DataAdded -= errorAdded;
                ps.Streams.Warning.DataAdded -= warningAdded;
                ps.Streams.Verbose.DataAdded -= verboseAdded;
                ps.Streams.Debug.DataAdded -= debugAdded;
                ps.Streams.Information.DataAdded -= informationAdded;
                ps.Streams.Progress.DataAdded -= progressAdded;

                if (terminatingException != null && collector.ErrorCount == 0)
                {
                    collector.AddError(terminatingError, terminatingException);
                }

                output.Clear();
                ClearStreamBuffers(ps);
                if (capabilityRunspace != null)
                {
                    if (previousCapabilityVariable == null)
                    {
                        capabilityRunspace.SessionStateProxy.PSVariable.Remove("DpsCapabilities");
                    }
                    else
                    {
                        capabilityRunspace.SessionStateProxy.SetVariable(
                            "DpsCapabilities",
                            previousCapabilityVariable.Value);
                    }
                }
                session?.EndInvocation(terminatingException != null);
            }

            return collector.Build();
        }

        private static InvocationResult InvokeAndCaptureLegacy(
            PowerShell ps,
            object[] input = null,
            FfiPowerShellSession session = null)
        {
            FfiInvocationResultSnapshot snapshot = InvokeAndCaptureStreamSnapshot(ps, input, session);
            var output = new StringBuilder();
            foreach (FfiStreamRecord record in snapshot.GetStream((int)FfiStreamKind.Output).Records)
            {
                output.AppendLine(record.GetField(0));
            }

            FfiStreamRecord[] capturedErrors = snapshot.GetStream((int)FfiStreamKind.Error).Records;
            var errors = new InvocationError[capturedErrors.Length];
            for (int index = 0; index < capturedErrors.Length; index++)
            {
                FfiStreamRecord error = capturedErrors[index];
                errors[index] = new InvocationError(
                    error.GetField(0),
                    error.GetField(2),
                    error.GetField(3),
                    error.GetField(4));
            }

            return new InvocationResult(
                output.ToString(),
                errors,
                (snapshot.Flags & FfiResultTerminatingFailure) != 0 ? FfiStatusManagedFailure : FfiStatusSuccess);
        }

        private static void ClearStreamBuffers(PowerShell ps)
        {
            ps.Streams.Error.Clear();
            ps.Streams.Warning.Clear();
            ps.Streams.Verbose.Clear();
            ps.Streams.Debug.Clear();
            ps.Streams.Information.Clear();
            ps.Streams.Progress.Clear();
        }

        private static FfiInvocationResultSnapshot GetInvocationResultSnapshot(IntPtr ptrResultHandle)
        {
            if (ptrResultHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Invocation result handle is invalid.");
            }

            GCHandle handle = GCHandle.FromIntPtr(ptrResultHandle);
            if (!handle.IsAllocated || handle.Target is not FfiInvocationResultSnapshot snapshot)
            {
                throw new InvalidOperationException("Invocation result handle is invalid.");
            }

            return snapshot;
        }

        private sealed class BufferTooSmallException : Exception
        {
        }

        private static FfiPowerShellPipeline GetPowerShellPipeline(IntPtr ptrHandle)
        {
            if (ptrHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("PowerShell handle is invalid.");
            }

            GCHandle handle = GCHandle.FromIntPtr(ptrHandle);
            if (!handle.IsAllocated || handle.Target is not FfiPowerShellPipeline pipeline)
            {
                throw new InvalidOperationException("PowerShell handle is invalid.");
            }

            return pipeline;
        }

        private static PowerShell GetPowerShell(IntPtr ptrHandle)
        {
            return GetPowerShellPipeline(ptrHandle).PowerShell;
        }

        private static FfiPowerShellSession GetPowerShellSession(IntPtr ptrHandle)
        {
            if (ptrHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("PowerShell session handle is invalid.");
            }

            GCHandle handle = GCHandle.FromIntPtr(ptrHandle);
            if (!handle.IsAllocated || handle.Target is not FfiPowerShellSession session)
            {
                throw new InvalidOperationException("PowerShell session handle is invalid.");
            }

            return session;
        }

        private static unsafe int ReadUtf8(byte* value, int length, FfiCallResult* result, out string text)
        {
            text = string.Empty;
            if (length < 0 || (length > 0 && value == null))
            {
                return WriteFailure(result, FfiStatusInvalidArgument, "UTF-8 input span is invalid.");
            }

            try
            {
                ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>(value, length);
                if (bytes.IndexOf((byte)0) >= 0)
                {
                    return WriteFailure(result, FfiStatusInvalidArgument, "UTF-8 input cannot contain NUL.");
                }

                text = length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
                return FfiStatusSuccess;
            }
            catch (Exception exception)
            {
                return WriteFailure(result, FfiStatusInvalidArgument, exception);
            }
        }

        private static unsafe int Execute(FfiCallResult* result, Action operation, bool bufferTooSmallIsSuccess = false)
        {
            if (!TryInitializeResult(result))
            {
                return FfiStatusInvalidArgument;
            }

            try
            {
                operation();
                return WriteSuccess(result);
            }
            catch (BufferTooSmallException) when (bufferTooSmallIsSuccess)
            {
                result->Status = 1;
                return result->Status;
            }
            catch (FfiInputNotCompletedException exception)
            {
                return WriteFailure(result, FfiStatusInputNotCompleted, exception);
            }
            catch (FfiInputBackpressureException exception)
            {
                return WriteFailure(result, FfiStatusBackpressure, exception);
            }
            catch (FfiUnsupportedValueException exception)
            {
                return WriteFailure(result, FfiStatusUnsupportedValue, exception);
            }
            catch (Exception exception)
            {
                return WriteFailure(result, FfiStatusManagedFailure, exception);
            }
        }

        private static unsafe bool TryInitializeResult(FfiCallResult* result)
        {
            if (result == null || result->Size < (uint)sizeof(FfiCallResult))
            {
                return false;
            }

            result->Status = FfiStatusSuccess;
            result->Flags = 0;
            result->DiagnosticRequiredLength = 0;
            result->DiagnosticWrittenLength = 0;
            return true;
        }

        private static unsafe int WriteSuccess(FfiCallResult* result)
        {
            result->Status = FfiStatusSuccess;
            return FfiStatusSuccess;
        }

        private static unsafe int WriteFailure(FfiCallResult* result, int status, Exception exception)
        {
            return WriteFailure(result, status, exception.Message);
        }

        private static unsafe int WriteFailure(FfiCallResult* result, int status, string diagnostic)
        {
            if (!TryInitializeResult(result))
            {
                return FfiStatusInvalidArgument;
            }

            diagnostic ??= string.Empty;
            result->Status = status;
            result->DiagnosticRequiredLength = Encoding.UTF8.GetByteCount(diagnostic);
            if (result->DiagnosticCapacity <= 0 || result->Diagnostic == null)
            {
                if (result->DiagnosticRequiredLength > 0)
                {
                    result->Flags |= FfiCallResultTruncatedDiagnostic;
                }

                return status;
            }

            Encoder encoder = Encoding.UTF8.GetEncoder();
            encoder.Convert(
                diagnostic.AsSpan(),
                new Span<byte>(result->Diagnostic, result->DiagnosticCapacity),
                true,
                out _,
                out int written,
                out bool completed);

            result->DiagnosticWrittenLength = written;
            if (!completed)
            {
                result->Flags |= FfiCallResultTruncatedDiagnostic;
            }

            return status;
        }
    }
}
