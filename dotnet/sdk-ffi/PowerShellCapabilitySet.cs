using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Devolutions.PowerShell.Ffi;

public sealed unsafe class PowerShellCapabilitySet : IDisposable
{
    private const uint RegistrationVersion = 1;
    private const uint RegistrationStructSize = 32;
    private const int DiagnosticCapacity = 512;
    private static readonly ConcurrentDictionary<ulong, PowerShellCapabilitySet> Registrations = new();
    private static readonly ConcurrentDictionary<CallbackKey, CancellationTokenSource> ActiveCallbacks = new();
    private static readonly ConcurrentDictionary<CallbackKey, byte> PendingCancellations = new();

    private readonly IReadOnlyDictionary<string, PowerShellCapabilityBinding> bindings;
    private readonly CapabilitySetHandle handle;

    private PowerShellCapabilitySet(PowerShellCapabilityBinding[] bindings)
    {
        this.bindings = bindings.ToDictionary(
            binding => binding.Definition.Name,
            StringComparer.Ordinal);
        handle = new CapabilitySetHandle();
    }

    public IReadOnlyCollection<PowerShellCapabilityDefinition> Definitions =>
        bindings.Values.Select(binding => binding.Definition).ToArray();

    public static PowerShellCapabilitySet Register(IEnumerable<PowerShellCapabilityBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        PowerShellCapabilityBinding[] bindingArray = bindings.ToArray();
        if (bindingArray.Length == 0 || bindingArray.Length > PowerShellCapabilityDefinition.MaximumCapabilities ||
            bindingArray.Any(binding => binding is null) ||
            bindingArray.Select(binding => binding.Definition.Name).Distinct(StringComparer.Ordinal).Count() != bindingArray.Length)
        {
            throw new ArgumentException("Capability registrations require one to sixteen uniquely named bindings.", nameof(bindings));
        }

        PowerShell.EnsureCapabilityRpcSupported();
        PowerShellCapabilitySet set = new(bindingArray);
        PowerShellValue definitions = SerializeDefinitions(bindingArray);
        byte[] payload = definitions.Payload;
        fixed (byte* payloadPointer = payload)
        {
            NativeDataValue nativeDefinitions = new()
            {
                Size = checked((uint)sizeof(NativeDataValue)),
                Kind = checked((uint)definitions.Kind),
                Data = payloadPointer,
                DataLength = (nuint)payload.Length,
            };
            NativeCapabilityRegistration registration = new()
            {
                Size = RegistrationStructSize,
                Flags = RegistrationVersion,
                Definitions = &nativeDefinitions,
                DispatchCallback = (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, NativeUtf8Span, NativeDataValue*, uint, uint, uint*, byte*, nuint, nuint*, NativeCallResult*, int>)&Dispatch,
                CancelCallback = (nint)(delegate* unmanaged[Cdecl]<ulong, ulong, void>)&Cancel,
            };
            byte* diagnostic = stackalloc byte[DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            ulong nativeHandle = 0;
            int status = NativeMethods.RegisterCapabilities(&registration, &nativeHandle, &result);
            NativeCall.ThrowIfFailed(status, result, diagnostic);
            set.handle.Initialize(nativeHandle);
            if (!Registrations.TryAdd(nativeHandle, set))
            {
                set.Dispose();
                throw new InvalidOperationException("The native PowerShell FFI returned a duplicate capability registration handle.");
            }
        }

        return set;
    }

    public void Dispose()
    {
        ulong nativeHandle = handle.Value;
        if (nativeHandle != 0)
        {
            Registrations.TryRemove(nativeHandle, out _);
        }

        handle.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void AttachTo(ulong builderHandle)
    {
        using CapabilitySetHandle.HandleLease lease = handle.Borrow();
        byte* diagnostic = stackalloc byte[NativeCall.DiagnosticCapacity];
        NativeCallResult result = NativeCall.CreateResult(diagnostic);
        int status = NativeMethods.SetCapabilities(builderHandle, lease.Value, &result);
        NativeCall.ThrowIfFailed(status, result, diagnostic);
    }

    private static PowerShellValue SerializeDefinitions(IEnumerable<PowerShellCapabilityBinding> bindings)
    {
        return PowerShellValue.PropertyBag(
        [
            new("protocol", PowerShellValue.UnsignedInteger(RegistrationVersion)),
            new("capabilities", PowerShellValue.Array(bindings.Select(SerializeDefinition))),
        ]);
    }

    private static PowerShellValue SerializeDefinition(PowerShellCapabilityBinding binding)
    {
        PowerShellCapabilityDefinition definition = binding.Definition;
        return PowerShellValue.PropertyBag(
        [
            new("name", PowerShellValue.String(definition.Name)),
            new("permissions", PowerShellValue.UnsignedInteger((uint)definition.Permissions)),
            new("maximumInputBytes", PowerShellValue.UnsignedInteger((uint)definition.MaximumInputBytes)),
            new("maximumOutputBytes", PowerShellValue.UnsignedInteger((uint)definition.MaximumOutputBytes)),
            new("deadlineMilliseconds", PowerShellValue.UnsignedInteger(definition.DeadlineMilliseconds)),
            new(
                "arguments",
                PowerShellValue.Array(definition.Arguments.Select(schema =>
                    PowerShellValue.Array(schema.AllowedKinds.Select(kind => PowerShellValue.UnsignedInteger((uint)kind)))))),
            new(
                "responseKinds",
                PowerShellValue.Array(definition.ResponseKinds.Select(kind => PowerShellValue.UnsignedInteger((uint)kind)))),
        ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Dispatch(
        ulong registrationHandle,
        ulong invocationId,
        NativeUtf8Span name,
        NativeDataValue* arguments,
        uint argumentCount,
        uint deadlineMilliseconds,
        uint* responseKind,
        byte* responseBuffer,
        nuint responseCapacity,
        nuint* responseRequired,
        NativeCallResult* result)
    {
        if (!PrepareCallbackResult(result, responseKind, responseRequired))
        {
            return (int)PowerShellFfiStatus.InvalidArgument;
        }
        if (arguments is null || responseBuffer is null || name.Data is null ||
            name.Length == 0 || name.Length > PowerShellCapabilityDefinition.MaximumNameLength)
        {
            return CompleteCallbackFailure(result, PowerShellFfiStatus.InvalidArgument);
        }

        try
        {
            if (!Registrations.TryGetValue(registrationHandle, out PowerShellCapabilitySet? set) ||
                !set.bindings.TryGetValue(DecodeName(name), out PowerShellCapabilityBinding? binding) ||
                argumentCount != binding.Definition.Arguments.Count ||
                deadlineMilliseconds == 0 || deadlineMilliseconds > binding.Definition.DeadlineMilliseconds)
            {
                return CompleteCallbackFailure(result, PowerShellFfiStatus.UnsupportedCapability);
            }
            if (arguments->Size < sizeof(NativeDataValue) ||
                arguments->Kind != (uint)PowerShellValueKind.Array ||
                arguments->Data is null || arguments->DataLength > (nuint)binding.Definition.MaximumInputBytes)
            {
                return CompleteCallbackFailure(result, PowerShellFfiStatus.InvalidArgument);
            }

            byte[] inputPayload = new ReadOnlySpan<byte>(arguments->Data, checked((int)arguments->DataLength)).ToArray();
            IReadOnlyList<PowerShellValue> values = PowerShellValue
                .FromNative(arguments->Kind, inputPayload)
                .GetArrayElements();
            if (values.Count != binding.Definition.Arguments.Count ||
                values.Where((value, index) => !binding.Definition.Arguments[index].AllowedKinds.Contains(value.Kind)).Any())
            {
                return CompleteCallbackFailure(result, PowerShellFfiStatus.InvalidArgument);
            }

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(deadlineMilliseconds));
            var callbackKey = new CallbackKey(registrationHandle, invocationId);
            if (!ActiveCallbacks.TryAdd(callbackKey, cancellation))
            {
                return CompleteCallbackFailure(result, PowerShellFfiStatus.Backpressure);
            }
            if (PendingCancellations.TryRemove(callbackKey, out _))
            {
                cancellation.Cancel();
            }

            PowerShellValue response;
            try
            {
                response = binding.Handler.Invoke(
                    new PowerShellCapabilityInvocation(binding.Definition, invocationId, cancellation.Token),
                    values);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                ActiveCallbacks.TryRemove(callbackKey, out _);
                PendingCancellations.TryRemove(callbackKey, out _);
            }
            ArgumentNullException.ThrowIfNull(response);
            if (!binding.Definition.ResponseKinds.Contains(response.Kind) ||
                response.Payload.Length > binding.Definition.MaximumOutputBytes)
            {
                return CompleteCallbackFailure(result, PowerShellFfiStatus.InvalidArgument);
            }
            if ((nuint)response.Payload.Length > responseCapacity)
            {
                *responseRequired = (nuint)response.Payload.Length;
                return CompleteCallbackFailure(result, PowerShellFfiStatus.BufferTooSmall);
            }

            response.Payload.CopyTo(new Span<byte>(responseBuffer, response.Payload.Length));
            *responseKind = (uint)response.Kind;
            *responseRequired = (nuint)response.Payload.Length;
            return CompleteCallback(result, PowerShellFfiStatus.Success);
        }
        catch (OperationCanceledException)
        {
            return CompleteCallbackFailure(result, PowerShellFfiStatus.OperationCancelled);
        }
        catch
        {
            return CompleteCallbackFailure(result, PowerShellFfiStatus.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Cancel(ulong registrationHandle, ulong invocationId)
    {
        var callbackKey = new CallbackKey(registrationHandle, invocationId);
        if (ActiveCallbacks.TryGetValue(callbackKey, out CancellationTokenSource? cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            return;
        }

        PendingCancellations.TryAdd(callbackKey, 0);
    }

    private static string DecodeName(NativeUtf8Span name)
    {
        return new UTF8Encoding(false, true).GetString(new ReadOnlySpan<byte>(name.Data, checked((int)name.Length)));
    }

    private static bool PrepareCallbackResult(NativeCallResult* result, uint* responseKind, nuint* responseRequired)
    {
        if (result is null || responseKind is null || responseRequired is null ||
            result->Size < (uint)sizeof(NativeCallResult) ||
            (result->DiagnosticCapacity != 0 && result->Diagnostic is null))
        {
            return false;
        }

        result->Status = (int)PowerShellFfiStatus.Success;
        result->Flags = 0;
        result->DiagnosticRequired = 0;
        result->DiagnosticWritten = 0;
        *responseKind = 0;
        *responseRequired = 0;
        return true;
    }

    private static int CompleteCallback(NativeCallResult* result, PowerShellFfiStatus status)
    {
        result->Status = (int)status;
        return (int)status;
    }

    private static int CompleteCallbackFailure(NativeCallResult* result, PowerShellFfiStatus status)
    {
        ReadOnlySpan<byte> diagnostic = "The capability callback failed."u8;
        result->Status = (int)status;
        result->DiagnosticRequired = (nuint)diagnostic.Length;
        result->DiagnosticWritten = Math.Min(result->DiagnosticCapacity, (nuint)diagnostic.Length);
        if (result->DiagnosticWritten != 0)
        {
            diagnostic[..checked((int)result->DiagnosticWritten)].CopyTo(
                new Span<byte>(result->Diagnostic, checked((int)result->DiagnosticWritten)));
        }
        if (result->DiagnosticWritten != result->DiagnosticRequired)
        {
            result->Flags |= 1;
        }

        return (int)status;
    }

    private sealed class CapabilitySetHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CapabilitySetHandle()
            : base(ownsHandle: true)
        {
        }

        internal ulong Value => IsClosed || IsInvalid ? 0 : unchecked((ulong)handle);

        internal void Initialize(ulong value)
        {
            SetHandle(unchecked((nint)value));
        }

        internal HandleLease Borrow()
        {
            bool addedReference = false;
            DangerousAddRef(ref addedReference);
            try
            {
                if (IsInvalid)
                {
                    throw new ObjectDisposedException(nameof(PowerShellCapabilitySet));
                }

                return new HandleLease(this, unchecked((ulong)DangerousGetHandle()));
            }
            catch
            {
                if (addedReference)
                {
                    DangerousRelease();
                }

                throw;
            }
        }

        protected override bool ReleaseHandle()
        {
            byte* diagnostic = stackalloc byte[DiagnosticCapacity];
            NativeCallResult result = NativeCall.CreateResult(diagnostic);
            _ = NativeMethods.ReleaseCapabilities(unchecked((ulong)handle), &result);
            return true;
        }

        internal sealed class HandleLease : IDisposable
        {
            private CapabilitySetHandle? owner;

            internal HandleLease(CapabilitySetHandle owner, ulong value)
            {
                this.owner = owner;
                Value = value;
            }

            internal ulong Value { get; }

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.DangerousRelease();
            }
        }
    }

    private readonly record struct CallbackKey(ulong RegistrationHandle, ulong InvocationId);
}
