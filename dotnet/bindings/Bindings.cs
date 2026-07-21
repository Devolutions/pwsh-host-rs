using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Management.Automation;

namespace NativeHost
{
    // PowerShell Class
    // https://learn.microsoft.com/en-us/dotnet/api/system.management.automation.powershell

    public static partial class Bindings
    {
        public const int AbiVersion = 2;
        private const int MaxInvocationErrors = 32;
        private const int MaxInvocationErrorFieldLength = 4096;
        private static readonly ConcurrentDictionary<IntPtr, InvocationResult> InvocationResults = new ConcurrentDictionary<IntPtr, InvocationResult>();

        internal sealed class InvocationResult
        {
            public InvocationResult(string output, InvocationError[] errors, int status)
            {
                Output = output;
                Errors = errors;
                Status = status;
            }

            public string Output { get; }

            public InvocationError[] Errors { get; }

            public int Status { get; }
        }

        internal sealed class InvocationError
        {
            public InvocationError(string message, string fullyQualifiedErrorId, string category, string exceptionType)
            {
                Message = message;
                FullyQualifiedErrorId = fullyQualifiedErrorId;
                Category = category;
                ExceptionType = exceptionType;
            }

            public string Message { get; }

            public string FullyQualifiedErrorId { get; }

            public string Category { get; }

            public string ExceptionType { get; }
        }

        [UnmanagedCallersOnly]
        public static int Bindings_GetAbiVersion()
        {
            return AbiVersion;
        }

        [UnmanagedCallersOnly]
        public static IntPtr PowerShell_Create()
        {
            // https://stackoverflow.com/a/32108252
            PowerShell ps = PowerShell.Create();
            GCHandle gch = GCHandle.Alloc(ps, GCHandleType.Normal);
            IntPtr ptrHandle = GCHandle.ToIntPtr(gch);
            return ptrHandle;
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddArgument_String(IntPtr ptrHandle, IntPtr ptrArgument)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            InvalidateInvocationOutput(ptrHandle);
            string argument = Marshal.PtrToStringUTF8(ptrArgument);
            ps.AddArgument(argument);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddParameter_String(IntPtr ptrHandle, IntPtr ptrName, IntPtr ptrValue)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            InvalidateInvocationOutput(ptrHandle);
            string name = Marshal.PtrToStringUTF8(ptrName);
            string value = Marshal.PtrToStringUTF8(ptrValue);
            ps.AddParameter(name, value);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddParameter_Int(IntPtr ptrHandle, IntPtr ptrName, int value)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            InvalidateInvocationOutput(ptrHandle);
            string name = Marshal.PtrToStringUTF8(ptrName);
            ps.AddParameter(name, value);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddParameter_Long(IntPtr ptrHandle, IntPtr ptrName, long value)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            InvalidateInvocationOutput(ptrHandle);
            string name = Marshal.PtrToStringUTF8(ptrName);
            ps.AddParameter(name, value);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddCommand(IntPtr ptrHandle, IntPtr ptrCommand)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            InvalidateInvocationOutput(ptrHandle);
            string command = Marshal.PtrToStringUTF8(ptrCommand);
            ps.AddCommand(command);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddScript(IntPtr ptrHandle, IntPtr ptrScript)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            InvalidateInvocationOutput(ptrHandle);
            string script = Marshal.PtrToStringUTF8(ptrScript);
            ps.AddScript(script);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddStatement(IntPtr ptrHandle)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            InvalidateInvocationOutput(ptrHandle);
            ps.AddStatement();
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_Invoke(IntPtr ptrHandle)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            ps.Invoke();
        }

        [UnmanagedCallersOnly]
        public static unsafe int PowerShell_InvokeToUtf8(IntPtr ptrHandle, byte* buffer, int bufferLength, int* requiredLength)
        {
            if (requiredLength == null || bufferLength < 0)
            {
                return -1;
            }

            try
            {
                GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
                PowerShell ps = (PowerShell) gch.Target;
                InvocationResult result = InvocationResults.GetOrAdd(ptrHandle, _ => InvokeAndCapture(ps));
                if (result.Status != 0)
                {
                    return result.Status;
                }

                int required = Encoding.UTF8.GetByteCount(result.Output);
                *requiredLength = required;
                if (bufferLength < required)
                {
                    return 1;
                }

                if (required > 0)
                {
                    Encoding.UTF8.GetBytes(result.Output, new Span<byte>(buffer, required));
                }

                return 0;
            }
            catch
            {
                *requiredLength = 0;
                return -2;
            }
        }

        [UnmanagedCallersOnly]
        public static int PowerShell_GetInvocationErrorCount(IntPtr ptrHandle)
        {
            return InvocationResults.TryGetValue(ptrHandle, out InvocationResult result)
                ? result.Errors.Length
                : -1;
        }

        [UnmanagedCallersOnly]
        public static unsafe int PowerShell_CopyInvocationErrorFieldToUtf8(
            IntPtr ptrHandle,
            int errorIndex,
            int field,
            byte* buffer,
            int bufferLength,
            int* requiredLength)
        {
            if (requiredLength == null || bufferLength < 0)
            {
                return -1;
            }

            try
            {
                if (!InvocationResults.TryGetValue(ptrHandle, out InvocationResult result) ||
                    errorIndex < 0 ||
                    errorIndex >= result.Errors.Length)
                {
                    *requiredLength = 0;
                    return -1;
                }

                InvocationError error = result.Errors[errorIndex];
                string value;
                switch (field)
                {
                    case 0:
                        value = error.Message;
                        break;
                    case 1:
                        value = error.FullyQualifiedErrorId;
                        break;
                    case 2:
                        value = error.Category;
                        break;
                    case 3:
                        value = error.ExceptionType;
                        break;
                    default:
                        *requiredLength = 0;
                        return -1;
                }

                int required = Encoding.UTF8.GetByteCount(value);
                *requiredLength = required;
                if (bufferLength < required)
                {
                    return 1;
                }

                if (required > 0)
                {
                    Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, required));
                }

                return 0;
            }
            catch
            {
                *requiredLength = 0;
                return -2;
            }
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_Clear(IntPtr ptrHandle)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            InvalidateInvocationOutput(ptrHandle);
            ps.Commands.Clear();
        }

        [UnmanagedCallersOnly]
        public static IntPtr PowerShell_ExportToXml(IntPtr ptrHandle, IntPtr ptrName)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            string name = Marshal.PtrToStringUTF8(ptrName);
            ps.AddScript(string.Format("[System.Management.Automation.PSSerializer]::Serialize(${0})", name));
            ps.AddStatement();
            Collection<PSObject> results = ps.Invoke();
            string result = results[0].ToString().Trim();
            ps.Commands.Clear();
            return Marshal.StringToCoTaskMemUTF8(result);
        }

        [UnmanagedCallersOnly]
        public static IntPtr PowerShell_ExportToJson(IntPtr ptrHandle, IntPtr ptrName)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            string name = Marshal.PtrToStringUTF8(ptrName);
            ps.AddScript(string.Format("${0} | ConvertTo-Json", name));
            ps.AddStatement();
            Collection<PSObject> results = ps.Invoke();
            string result = results[0].ToString().Trim();
            ps.Commands.Clear();
            return Marshal.StringToCoTaskMemUTF8(result);
        }

        [UnmanagedCallersOnly]
        public static IntPtr PowerShell_ExportToString(IntPtr ptrHandle, IntPtr ptrName)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            string name = Marshal.PtrToStringUTF8(ptrName);
            ps.AddScript(string.Format("${0} | Out-String", name));
            ps.AddStatement();
            Collection<PSObject> results = ps.Invoke();
            string result = results[0].ToString().Trim();
            ps.Commands.Clear();
            return Marshal.StringToCoTaskMemUTF8(result);
        }

        [UnmanagedCallersOnly]
        public static IntPtr Bindings_InvokeMemberJson(IntPtr ptrHandle, IntPtr ptrMemberName, IntPtr ptrArgumentsJson)
        {
            try
            {
                GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
                PowerShell ps = (PowerShell) gch.Target;
                string memberName = Marshal.PtrToStringUTF8(ptrMemberName) ?? string.Empty;
                string argsJson = Marshal.PtrToStringUTF8(ptrArgumentsJson) ?? "[]";
                object[] args = ParseJsonArray(argsJson);
                object result = ps.GetType().InvokeMember(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod, null, ps, args);
                return Marshal.StringToCoTaskMemUTF8(SerializeSuccess(result));
            }
            catch (Exception ex)
            {
                return Marshal.StringToCoTaskMemUTF8(SerializeError(ex));
            }
        }

        [UnmanagedCallersOnly]
        public static IntPtr Bindings_GetPropertyJson(IntPtr ptrHandle, IntPtr ptrPropertyName)
        {
            try
            {
                GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
                PowerShell ps = (PowerShell) gch.Target;
                string propertyName = Marshal.PtrToStringUTF8(ptrPropertyName) ?? string.Empty;
                object result = ps.GetType().InvokeMember(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty, null, ps, null);
                return Marshal.StringToCoTaskMemUTF8(SerializeSuccess(result));
            }
            catch (Exception ex)
            {
                return Marshal.StringToCoTaskMemUTF8(SerializeError(ex));
            }
        }

        [UnmanagedCallersOnly]
        public static IntPtr Bindings_SetPropertyJson(IntPtr ptrHandle, IntPtr ptrPropertyName, IntPtr ptrValueJson)
        {
            try
            {
                GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
                PowerShell ps = (PowerShell) gch.Target;
                string propertyName = Marshal.PtrToStringUTF8(ptrPropertyName) ?? string.Empty;
                string valueJson = Marshal.PtrToStringUTF8(ptrValueJson) ?? "null";
                object value = ParseJsonValue(valueJson);
                ps.GetType().InvokeMember(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.SetProperty, null, ps, new object[] { value });
                return Marshal.StringToCoTaskMemUTF8(SerializeSuccess(null));
            }
            catch (Exception ex)
            {
                return Marshal.StringToCoTaskMemUTF8(SerializeError(ex));
            }
        }

        [UnmanagedCallersOnly]
        public static IntPtr Bindings_InvokeStaticMemberJson(IntPtr ptrMemberName, IntPtr ptrArgumentsJson)
        {
            try
            {
                string memberName = Marshal.PtrToStringUTF8(ptrMemberName) ?? string.Empty;
                string argsJson = Marshal.PtrToStringUTF8(ptrArgumentsJson) ?? "[]";
                object[] args = ParseJsonArray(argsJson);
                object result = typeof(PowerShell).InvokeMember(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod, null, null, args);
                return Marshal.StringToCoTaskMemUTF8(SerializeSuccess(result));
            }
            catch (Exception ex)
            {
                return Marshal.StringToCoTaskMemUTF8(SerializeError(ex));
            }
        }

        [UnmanagedCallersOnly]
        public static void GCHandle_Free(IntPtr ptrHandle)
        {
            InvalidateInvocationOutput(ptrHandle);
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            if (gch.IsAllocated)
            {
                gch.Free();
            }
        }

        internal static void InvalidateInvocationOutput(IntPtr ptrHandle)
        {
            InvocationResults.TryRemove(ptrHandle, out _);
        }

        internal static InvocationResult InvokeAndCapture(PowerShell ps)
        {
            try
            {
                ps.Streams.Error.Clear();
                Collection<PSObject> results = ps.Invoke();
                StringBuilder builder = new StringBuilder();
                foreach (PSObject result in results)
                {
                    builder.AppendLine(result?.ToString());
                }

                List<InvocationError> errors = new List<InvocationError>();
                foreach (ErrorRecord error in ps.Streams.Error)
                {
                    if (errors.Count == MaxInvocationErrors)
                    {
                        break;
                    }

                    errors.Add(CreateInvocationError(error));
                }

                return new InvocationResult(builder.ToString(), errors.ToArray(), 0);
            }
            catch (RuntimeException exception)
            {
                return new InvocationResult(
                    string.Empty,
                    new[] { CreateInvocationError(exception.ErrorRecord, exception) },
                    -2);
            }
            catch (Exception exception)
            {
                return new InvocationResult(
                    string.Empty,
                    new[] { CreateInvocationError(null, exception) },
                    -2);
            }
        }

        private static InvocationError CreateInvocationError(ErrorRecord error, Exception fallbackException = null)
        {
            Exception exception = error?.Exception ?? fallbackException;
            return new InvocationError(
                BoundInvocationErrorField(error?.ToString() ?? fallbackException?.Message ?? string.Empty),
                BoundInvocationErrorField(error?.FullyQualifiedErrorId ?? string.Empty),
                BoundInvocationErrorField(error?.CategoryInfo.Category.ToString() ?? string.Empty),
                BoundInvocationErrorField(exception?.GetType().FullName ?? string.Empty));
        }

        private static string BoundInvocationErrorField(string value)
        {
            if (value == null || value.Length <= MaxInvocationErrorFieldLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, MaxInvocationErrorFieldLength);
        }

        private static object[] ParseJsonArray(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Arguments JSON must be an array.");
            }

            List<object> values = new List<object>();
            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                values.Add(ParseElement(element));
            }
            return values.ToArray();
        }

        private static object ParseJsonValue(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return ParseElement(doc.RootElement);
        }

        private static object ParseElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetBoolean();
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out int intValue))
                    {
                        return intValue;
                    }
                    if (element.TryGetInt64(out long longValue))
                    {
                        return longValue;
                    }
                    return element.GetDouble();
                case JsonValueKind.Array:
                    List<object> list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        list.Add(ParseElement(item));
                    }
                    return list.ToArray();
                case JsonValueKind.Object:
                    if (element.TryGetProperty("kind", out JsonElement kindElement) && kindElement.ValueKind == JsonValueKind.String)
                    {
                        string kind = kindElement.GetString();
                        if (string.Equals(kind, "handle", StringComparison.OrdinalIgnoreCase) && element.TryGetProperty("handle", out JsonElement handleElement))
                        {
                            long handleValue = handleElement.ValueKind == JsonValueKind.String ? long.Parse(handleElement.GetString()) : handleElement.GetInt64();
                            IntPtr ptrHandle = new IntPtr(handleValue);
                            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
                            return gch.Target;
                        }
                    }

                    Hashtable table = new Hashtable();
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        table[property.Name] = ParseElement(property.Value);
                    }
                    return table;
                default:
                    throw new InvalidOperationException($"Unsupported JSON value kind: {element.ValueKind}");
            }
        }

        private static string SerializeSuccess(object result)
        {
            object value = SerializeResultValue(result);
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["result"] = value,
            });
        }

        private static string SerializeError(Exception ex)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["ok"] = false,
                ["errorType"] = ex.GetType().FullName,
                ["errorMessage"] = ex.Message,
            });
        }

        private static object SerializeResultValue(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string || value is bool || value is int || value is long || value is double || value is float || value is decimal)
            {
                return value;
            }

            Type valueType = value.GetType();
            if (valueType.IsEnum)
            {
                return value.ToString();
            }

            GCHandle handle = GCHandle.Alloc(value, GCHandleType.Normal);
            IntPtr ptrHandle = GCHandle.ToIntPtr(handle);
            return new Dictionary<string, object>
            {
                ["kind"] = "handle",
                ["handle"] = ptrHandle.ToInt64(),
                ["type"] = valueType.FullName,
            };
        }

        // Marshal Class
        // https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal

        [UnmanagedCallersOnly]
        public static void Marshal_FreeCoTaskMem(IntPtr ptr)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }
}