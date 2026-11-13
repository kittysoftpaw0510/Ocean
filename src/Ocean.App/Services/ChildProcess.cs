using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ocean.App.Services;

/// <summary>
/// Starts a helper process with its standard output and error going to handles of our choosing, for example
/// straight into another process's input, so data passes from one to the other without this app copying it.
/// Only these handles are inherited (an explicit handle list), so no other pipe of this app stays open in the
/// child by accident (which would keep a reader from ever seeing the end of its input).
/// </summary>
public static class ChildProcess
{
    /// <param name="stdout">Write end the child's output goes to.</param>
    /// <param name="stderr">Write end the child's error output goes to.</param>
    public static Process Start(string exe, IEnumerable<string> args, SafeHandle stdout, SafeHandle stderr)
    {
        var me = GetCurrentProcess();
        IntPtr outDup = IntPtr.Zero, errDup = IntPtr.Zero, attrs = IntPtr.Zero, handles = IntPtr.Zero;
        var pi = default(PROCESS_INFORMATION);
        try
        {
            // inheritable copies of exactly the two handles
            if (!DuplicateHandle(me, stdout.DangerousGetHandle(), me, out outDup, 0, true, 2 /* DUPLICATE_SAME_ACCESS */)) throw new Win32Exception();
            if (!DuplicateHandle(me, stderr.DangerousGetHandle(), me, out errDup, 0, true, 2)) throw new Win32Exception();

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrs = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handles, 0, outDup);
            Marshal.WriteIntPtr(handles, IntPtr.Size, errDup);
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)0x20002 /* PROC_THREAD_ATTRIBUTE_HANDLE_LIST */, handles, (IntPtr)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = 0x100; // STARTF_USESTDHANDLES
            si.StartupInfo.hStdInput = IntPtr.Zero;
            si.StartupInfo.hStdOutput = outDup;
            si.StartupInfo.hStdError = errDup;
            si.lpAttributeList = attrs;

            var cmd = new StringBuilder();
            Append(cmd, exe);
            foreach (var a in args) { cmd.Append(' '); Append(cmd, a); }
            const uint flags = 0x00080000 /* EXTENDED_STARTUPINFO_PRESENT */ | 0x00000004 /* CREATE_SUSPENDED */ | 0x08000000 /* CREATE_NO_WINDOW */ | 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */;
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, null, ref si, out pi)) throw new Win32Exception();

            // tied to this app (it can't outlive it) before it runs a single instruction
            var p = Process.GetProcessById(pi.dwProcessId);
            ScreenTools.TieToApp(p);
            ResumeThread(pi.hThread);
            return p;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (outDup != IntPtr.Zero) CloseHandle(outDup);
            if (errDup != IntPtr.Zero) CloseHandle(errDup);
            if (attrs != IntPtr.Zero) { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
    }

    /// <summary>One argument, quoted the way the C runtime reads it back (as .NET's own ArgumentList does).</summary>
    private static void Append(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) { sb.Append(arg); return; }
        sb.Append('"');
        for (var i = 0; i < arg.Length; i++)
        {
            var slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', slashes * 2); break; }
            if (arg[i] == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
            else { sb.Append('\\', slashes); sb.Append(arg[i]); }
        }
        sb.Append('"');
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
