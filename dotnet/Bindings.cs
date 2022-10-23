﻿using System;
using System.Runtime.InteropServices;
using System.Management.Automation;

namespace NativeHost
{
    // PowerShell Class
    // https://learn.microsoft.com/en-us/dotnet/api/system.management.automation.powershell

    public static class Bindings
    {
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
            string argument = Marshal.PtrToStringUTF8(ptrArgument);
            ps.AddArgument(argument);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddParameter_String(IntPtr ptrHandle, IntPtr ptrName, IntPtr ptrValue)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            string name = Marshal.PtrToStringUTF8(ptrName);
            string value = Marshal.PtrToStringUTF8(ptrValue);
            ps.AddParameter(name, value);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddCommand(IntPtr ptrHandle, IntPtr ptrCommand)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            string command = Marshal.PtrToStringUTF8(ptrCommand);
            ps.AddCommand(command);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddScript(IntPtr ptrHandle, IntPtr ptrScript)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            string script = Marshal.PtrToStringUTF8(ptrScript);
            ps.AddScript(script);
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_AddStatement(IntPtr ptrHandle)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            ps.AddStatement();
        }

        [UnmanagedCallersOnly]
        public static void PowerShell_Invoke(IntPtr ptrHandle)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            ps.Invoke();
        }
    }
}
