using System;
using System.Runtime.InteropServices;
using System.Management.Automation;

namespace NativeHost
{
    public static class Bindings
    {
        [UnmanagedCallersOnly]
        public static void RunCommand(IntPtr ptrCommand)
        {
            string command = Marshal.PtrToStringUTF8(ptrCommand);
            PowerShell ps = PowerShell.Create();
            ps.AddScript(command);
            ps.Invoke();
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
        public static void PowerShell_Invoke(IntPtr ptrHandle)
        {
            GCHandle gch = GCHandle.FromIntPtr(ptrHandle);
            PowerShell ps = (PowerShell) gch.Target;
            ps.Invoke();
        }
    }
}
