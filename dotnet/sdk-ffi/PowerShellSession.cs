using System.Collections.Generic;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.PowerShell.Ffi;

public sealed unsafe class PowerShellSession : IDisposable
{
    private const uint EventsTruncated = 1;
    private const uint MaximumEvents = 32;
    private readonly PowerShellSessionHandle handle;
    private readonly IReadOnlyList<string> approvedModuleRoots;

    private PowerShellSession(PowerShellSessionHandle handle, IReadOnlyList<string> approvedModuleRoots)
    {
        this.handle = handle;
        this.approvedModuleRoots = approvedModuleRoots;
    }

    internal static PowerShellSession Create(PowerShellSessionOptions options)
    {
        PowerShell.EnsureSupportedAbi();
        return WithNativeOptions(options, nativeOptions =>
        {
            ulong nativeSessionHandle = 0;
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = NativeMethods.CreateSession(nativeOptions, &nativeSessionHandle, &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
            var session = new PowerShellSession(
                new PowerShellSessionHandle(nativeSessionHandle),
                options.Configuration.AllowedModulePaths
                    .Select(static path => CanonicalizeExistingPath(path, isDirectory: true))
                    .ToArray());
            try
            {
                foreach (PowerShellModuleImport moduleImport in options.Configuration.ModuleImportSpecifications
                    .Where(static import => !import.IsSimpleImport))
                {
                    session.ImportModule(moduleImport);
                }

                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        });
    }

    internal static PowerShellSessionPreflightReport Preflight(PowerShellSessionOptions options)
    {
        PowerShell.EnsureSupportedAbi();
        PowerShell.EnsureSessionPreflightSupported();
        return WithNativeOptions(options, nativeOptions =>
        {
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            nuint requiredLength = 0;
            int status = NativeMethods.PreflightSession(nativeOptions, null, 0, &requiredLength, &result);
            if (status != (int)PowerShellFfiStatus.Success &&
                status != (int)PowerShellFfiStatus.BufferTooSmall)
            {
                NativeCall.ThrowIfFailed(status, result, diagnostic);
            }
            if (requiredLength > PowerShellValue.MaximumPayloadLength)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned an oversized session preflight report.");
            }

            byte[] payload = new byte[checked((int)requiredLength)];
            fixed (byte* payloadPointer = payload)
            {
                result = NativeCall.CreateResult(diagnostic);
                status = NativeMethods.PreflightSession(
                    nativeOptions,
                    payloadPointer,
                    (nuint)payload.Length,
                    &requiredLength,
                    &result);
                NativeCall.ThrowIfFailed(status, result, diagnostic);
            }
            if (requiredLength != (nuint)payload.Length)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned an inconsistent session preflight report.");
            }

            return PowerShellSessionPreflightReport.FromNative(
                PowerShellValue.FromNative((uint)PowerShellValueKind.PropertyBag, payload));
        });
    }

    public PowerShell CreatePowerShell()
    {
        using PowerShellSessionHandle.HandleLease lease = handle.Borrow();
        ulong nativeBuilderHandle = 0;
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.CreateSessionBuilder(lease.Value, &nativeBuilderHandle, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        return PowerShell.CreateFromNative(nativeBuilderHandle);
    }

    /// <summary>
    /// Imports a module into this persistent local session using a finite,
    /// declared import shape. Paths remain subject to the session's approved
    /// module-root policy in the payload.
    /// </summary>
    public void ImportModule(PowerShellModuleImport moduleImport)
    {
        ArgumentNullException.ThrowIfNull(moduleImport);
        EnsureApprovedModuleImport(moduleImport);
        using PowerShell powerShell = CreatePowerShell();
        powerShell.AddCommand("Import-Module")
            .AddParameter("Name", moduleImport.NameOrPath);
        if (moduleImport.RequiredVersion is not null)
        {
            powerShell.AddParameter("RequiredVersion", moduleImport.RequiredVersion.ToString());
        }
        if ((moduleImport.Options & PowerShellModuleImportOptions.Force) != 0)
        {
            powerShell.AddParameter("Force");
        }
        if ((moduleImport.Options & PowerShellModuleImportOptions.DisableNameChecking) != 0)
        {
            powerShell.AddParameter("DisableNameChecking");
        }
        if ((moduleImport.Options & PowerShellModuleImportOptions.SkipEditionCheck) != 0)
        {
            powerShell.AddParameter("SkipEditionCheck");
        }

        _ = powerShell.InvokeWithDiagnostics();
    }

    /// <summary>
    /// Creates a builder with one generated bridge proxy attached for its next
    /// asynchronous invocation.
    /// </summary>
    public PowerShell CreatePowerShell(PowerShellBridgeBinding binding, string variableName)
    {
        PowerShell powerShell = CreatePowerShell();
        try
        {
            return powerShell.WithBridge(binding, variableName);
        }
        catch
        {
            powerShell.Dispose();
            throw;
        }
    }

    public PowerShellSessionSnapshot GetSnapshot()
    {
        using PowerShellSessionHandle.HandleLease lease = handle.Borrow();
        NativeSessionSnapshot nativeSnapshot = new() { Size = checked((uint)sizeof(NativeSessionSnapshot)) };
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.GetSessionSnapshot(lease.Value, &nativeSnapshot, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);

        return new PowerShellSessionSnapshot(
            ToState(nativeSnapshot.State),
            ToState(nativeSnapshot.RunspaceState),
            (nativeSnapshot.Flags & EventsTruncated) != 0,
            nativeSnapshot.ActivePipelineCount,
            nativeSnapshot.InvocationCount,
            nativeSnapshot.HistoryCount);
    }

    public IReadOnlyList<PowerShellSessionEvent> GetEvents()
    {
        using PowerShellSessionHandle.HandleLease lease = handle.Borrow();
        NativeSessionSnapshot nativeSnapshot = new() { Size = checked((uint)sizeof(NativeSessionSnapshot)) };
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.GetSessionSnapshot(lease.Value, &nativeSnapshot, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
        if (nativeSnapshot.EventCount > MaximumEvents)
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an unbounded session event count.");
        }

        var events = new List<PowerShellSessionEvent>(checked((int)nativeSnapshot.EventCount));
        for (uint index = 0; index < nativeSnapshot.EventCount; index++)
        {
            ulong sequence = 0;
            uint state = 0;
            uint flags = 0;
            result = NativeCall.CreateResult(diagnostic);
            status = NativeMethods.GetSessionEventInfo(lease.Value, index, &sequence, &state, &flags, &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
            events.Add(new PowerShellSessionEvent(sequence, ToState(state), flags != 0));
        }

        return events;
    }

    /// <summary>
    /// Replaces a named session variable with a copied tagged value.
    /// </summary>
    public void SetVariable(string name, PowerShellValue value)
    {
        ValidateVariableName(name);
        ArgumentNullException.ThrowIfNull(value);

        byte[] nameBytes = PowerShell.EncodeUtf8(name);
        byte[] payload = value.Payload;
        using PowerShellSessionHandle.HandleLease lease = handle.Borrow();
        fixed (byte* namePointer = nameBytes)
        fixed (byte* payloadPointer = payload)
        {
            NativeDataValue nativeValue = PowerShell.CreateNativeValue(value, payloadPointer);
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = NativeMethods.SetSessionVariable(
                lease.Value,
                new NativeUtf8Span { Data = namePointer, Length = (nuint)nameBytes.Length },
                &nativeValue,
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
        }
    }

    /// <summary>
    /// Replaces a named session variable with an experimental .NET-owned live
    /// object probe. The probe must outlive its session-variable binding.
    /// </summary>
    public void SetLiveObjectVariable(string name, PowerShellSessionObjectProbe value)
    {
        ValidateVariableName(name);
        ArgumentNullException.ThrowIfNull(value);
        PowerShell.EnsureLiveSessionObjectProbeSupported();

        value.AssignToSession(pointer =>
        {
            byte[] nameBytes = PowerShell.EncodeUtf8(name);
            using PowerShellSessionHandle.HandleLease lease = handle.Borrow();
            fixed (byte* namePointer = nameBytes)
            {
                byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
                NativeCallResult result = NativeCall.CreateResult(diagnostic);
                int status = NativeMethods.SetSessionLiveObjectVariable(
                    lease.Value,
                    new NativeUtf8Span { Data = namePointer, Length = (nuint)nameBytes.Length },
                    pointer,
                    &result);
                NativeCall.ThrowIfFailed(status, result, diagnostic);
            }
        });
    }

    /// <summary>
    /// Replaces a named session variable with a registered consumer-owned live
    /// object. The selected payload must have loaded a matching contract pack.
    /// </summary>
    public void SetLiveObjectVariable<TContract>(string name, PowerShellLiveObject<TContract> value)
        where TContract : class
    {
        ValidateVariableName(name);
        ArgumentNullException.ThrowIfNull(value);
        if ((value.Contract.Directions & PowerShellLiveObjectDirection.ConsumerToSession) == 0)
        {
            throw new ArgumentException(
                "The live object contract does not support consumer-to-session projection.",
                nameof(value));
        }

        PowerShell.EnsureLiveObjectContractsSupported();
        value.AssignToSession(pointer =>
        {
            byte[] nameBytes = PowerShell.EncodeUtf8(name);
            NativeLiveObjectContractDescriptor contract = value.Contract.ToNative();
            using PowerShellSessionHandle.HandleLease lease = handle.Borrow();
            fixed (byte* namePointer = nameBytes)
            {
                byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
                NativeCallResult result = NativeCall.CreateResult(diagnostic);
                int status = NativeMethods.SetSessionLiveObjectContractVariable(
                    lease.Value,
                    new NativeUtf8Span { Data = namePointer, Length = (nuint)nameBytes.Length },
                    &contract,
                    pointer,
                    &result);
                NativeCall.ThrowIfFailed(status, result, diagnostic);
            }
        });
    }

    /// <summary>
    /// Replaces a named session variable with a copied property-bag DTO.
    /// </summary>
    public void SetPropertyBag(string name, IEnumerable<KeyValuePair<string, PowerShellValue>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        SetVariable(name, PowerShellValue.PropertyBag(properties));
    }

    /// <summary>
    /// Removes a named copied-value session variable.
    /// </summary>
    public bool RemoveVariable(string name)
    {
        ValidateVariableName(name);

        byte[] nameBytes = PowerShell.EncodeUtf8(name);
        using PowerShellSessionHandle.HandleLease lease = handle.Borrow();
        fixed (byte* namePointer = nameBytes)
        {
            uint removed = 0;
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = NativeMethods.RemoveSessionVariable(
                lease.Value,
                new NativeUtf8Span { Data = namePointer, Length = (nuint)nameBytes.Length },
                &removed,
                &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
            return removed != 0;
        }
    }

    /// <summary>
    /// Retrieves a copied snapshot of a named session variable.
    /// </summary>
    public bool TryGetVariable(string name, out PowerShellValue? value)
    {
        ValidateVariableName(name);

        byte[] nameBytes = PowerShell.EncodeUtf8(name);
        using PowerShellSessionHandle.HandleLease lease = handle.Borrow();
        fixed (byte* namePointer = nameBytes)
        {
            uint found = 0;
            uint kind = 0;
            nuint requiredLength = 0;
            byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            int status = NativeMethods.GetSessionVariableSnapshot(
                lease.Value,
                new NativeUtf8Span { Data = namePointer, Length = (nuint)nameBytes.Length },
                &found,
                &kind,
                null,
                0,
                &requiredLength,
                &result);
            if (status != (int)PowerShellFfiStatus.Success &&
                status != (int)PowerShellFfiStatus.BufferTooSmall)
            {
                NativeCall.ThrowIfFailed(status, result, diagnostic);
            }

            if (found == 0)
            {
                value = null;
                return false;
            }

            if (found != 1 || requiredLength > PowerShellValue.MaximumPayloadLength)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned an oversized session variable snapshot.");
            }

            byte[] payload = new byte[checked((int)requiredLength)];
            fixed (byte* payloadPointer = payload)
            {
                result = NativeCall.CreateResult(diagnostic);
                status = NativeMethods.GetSessionVariableSnapshot(
                    lease.Value,
                    new NativeUtf8Span { Data = namePointer, Length = (nuint)nameBytes.Length },
                    &found,
                    &kind,
                    payloadPointer,
                    (nuint)payload.Length,
                    &requiredLength,
                    &result);
                NativeCall.ThrowIfFailed(status, result, diagnostic);
            }

            if (found != 1 || requiredLength != (nuint)payload.Length)
            {
                throw new PowerShellFfiException(
                    PowerShellFfiStatus.ManagedFailure,
                    "Native PowerShell FFI returned an inconsistent session variable snapshot.");
            }

            value = PowerShellValue.FromNative(kind, payload);
            return true;
        }
    }

    /// <summary>
    /// Retrieves a copied property-bag DTO from a named session variable.
    /// </summary>
    public bool TryGetPropertyBag(
        string name,
        out IReadOnlyDictionary<string, PowerShellValue>? properties)
    {
        if (!TryGetVariable(name, out PowerShellValue? value))
        {
            properties = null;
            return false;
        }
        if (value is not { Kind: PowerShellValueKind.PropertyBag })
        {
            throw new InvalidOperationException("The named session variable is not a copied property bag.");
        }

        properties = value.GetPropertyBag();
        return true;
    }

    /// <summary>
    /// Invokes a copied script recipe in this session and reads its named copied
    /// result variable after synchronous completion.
    /// </summary>
    public PowerShellSessionScriptResult InvokeAndReadVariable(
        PowerShellScriptRecipe recipe,
        string resultVariableName,
        PowerShellCommandPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ValidateVariableName(resultVariableName);
        policy?.Validate(recipe);
        using PowerShell powerShell = CreatePowerShell();
        recipe.Apply(powerShell);
        PowerShellInvocationResult invocation = PowerShellRuntime.InvokeRecipe(
            powerShell,
            recipe.ResultSchema,
            recipe.Timeout);
        bool found = TryGetVariable(resultVariableName, out PowerShellValue? value);
        return new PowerShellSessionScriptResult(invocation, found, value);
    }

    public void Dispose()
    {
        handle.Dispose();
    }

    private static void ValidateVariableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            (!IsAsciiLetter(value[0]) && value[0] != '_') ||
            !value.All(character => IsAsciiLetter(character) || IsAsciiDigit(character) || character == '_'))
        {
            throw new ArgumentException(
                "Session variable names must use ASCII-like identifier characters and be at most 64 characters.",
                nameof(value));
        }
    }

    private void EnsureApprovedModuleImport(PowerShellModuleImport moduleImport)
        {
            if (approvedModuleRoots.Count == 0)
            {
                throw new InvalidOperationException(
                    "Module imports require at least one approved module root on the persistent session.");
            }

            if (!Path.IsPathFullyQualified(moduleImport.NameOrPath))
            {
                return;
            }

            string importPath = CanonicalizeExistingPath(moduleImport.NameOrPath, isDirectory: Directory.Exists(moduleImport.NameOrPath));
            if (!approvedModuleRoots.Any(root => IsBeneathRoot(root, importPath)))
            {
                throw new InvalidOperationException(
                    "Module import paths must resolve beneath an approved module root.");
            }
        }

    private static string CanonicalizeExistingPath(string path, bool isDirectory)
        {
            string fullPath = Path.GetFullPath(path);
            if (isDirectory)
            {
                fullPath = Path.TrimEndingDirectorySeparator(fullPath);
            }

            string root = Path.GetPathRoot(fullPath)
                ?? throw new ArgumentException("The path has no filesystem root.", nameof(path));
            string canonicalPath = root;
            foreach (string component in fullPath[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                canonicalPath = Path.Combine(canonicalPath, component);
                FileSystemInfo info = Directory.Exists(canonicalPath)
                    ? new DirectoryInfo(canonicalPath)
                    : new FileInfo(canonicalPath);
                if (!info.Exists)
                {
                    throw new IOException("The path does not exist.");
                }

                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    canonicalPath = Path.GetFullPath(target.FullName);
                }
            }

            return isDirectory ? Path.TrimEndingDirectorySeparator(canonicalPath) : canonicalPath;
        }

    private static bool IsBeneathRoot(string root, string path)
        {
            string relative = Path.GetRelativePath(root, path);
            return relative.Length != 0 &&
                !Path.IsPathFullyQualified(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsAsciiLetter(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }

    private static bool IsAsciiDigit(char value)
    {
        return value is >= '0' and <= '9';
    }

    private unsafe delegate T NativeSessionOptionsCallback<T>(NativeSessionOptions* options);

    private static T WithNativeOptions<T>(
        PowerShellSessionOptions options,
        NativeSessionOptionsCallback<T> callback)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(callback);
        PowerShellSessionConfiguration configuration = options.Configuration;
        PowerShellValue initialVariablesValue = configuration.InitialVariablesValue;
        PowerShellValue moduleImportsValue = configuration.ModuleImportsValue;
        PowerShellValue modulePathsValue = configuration.AllowedModulePathsValue;
        PowerShellValue environmentValue = configuration.EnvironmentValue;
        byte[] initialVariablesPayload = initialVariablesValue.Payload;
        byte[] moduleImportsPayload = moduleImportsValue.Payload;
        byte[] modulePathsPayload = modulePathsValue.Payload;
        byte[] workingDirectoryBytes = PowerShell.EncodeUtf8(configuration.WorkingDirectory);
        byte[] environmentPayload = environmentValue.Payload;
        fixed (byte* initialVariables = initialVariablesPayload)
        fixed (byte* moduleImports = moduleImportsPayload)
        fixed (byte* modulePaths = modulePathsPayload)
        fixed (byte* workingDirectory = workingDirectoryBytes)
        fixed (byte* environment = environmentPayload)
        {
            NativeSessionOptions nativeOptions = new()
            {
                Size = checked((uint)sizeof(NativeSessionOptions)),
                RunspaceMode = checked((uint)options.RunspaceMode),
                InitialConfiguration = checked((uint)options.InitialConfiguration),
                HistoryMode = checked((uint)options.HistoryMode),
                ErrorPreference = checked((uint)options.ErrorPreference),
                WarningPreference = checked((uint)options.WarningPreference),
                VerbosePreference = checked((uint)options.VerbosePreference),
                DebugPreference = checked((uint)options.DebugPreference),
                InformationPreference = checked((uint)options.InformationPreference),
                ExecutionPolicy = checked((uint)configuration.ExecutionPolicy),
                InitialVariables = PowerShell.CreateNativeValue(initialVariablesValue, initialVariables),
                ModuleImports = PowerShell.CreateNativeValue(moduleImportsValue, moduleImports),
                AllowedModulePaths = PowerShell.CreateNativeValue(modulePathsValue, modulePaths),
                WorkingDirectory = new NativeUtf8Span
                {
                    Data = workingDirectoryBytes.Length == 0 ? null : workingDirectory,
                    Length = (nuint)workingDirectoryBytes.Length,
                },
                Environment = PowerShell.CreateNativeValue(environmentValue, environment),
            };
            return callback(&nativeOptions);
        }
    }

    private static PowerShellSessionState ToState(uint value)
    {
        if (!Enum.IsDefined((PowerShellSessionState)value))
        {
            throw new PowerShellFfiException(
                PowerShellFfiStatus.ManagedFailure,
                "Native PowerShell FFI returned an invalid session state.");
        }

        return (PowerShellSessionState)value;
    }
}

public sealed class PowerShellSessionScriptResult
{
    internal PowerShellSessionScriptResult(
        PowerShellInvocationResult invocation,
        bool hasValue,
        PowerShellValue? value)
    {
        Invocation = invocation;
        HasValue = hasValue;
        Value = value;
    }

    public PowerShellInvocationResult Invocation { get; }

    public bool HasValue { get; }

    public PowerShellValue? Value { get; }
}
