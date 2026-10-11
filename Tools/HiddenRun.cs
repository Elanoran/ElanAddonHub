using System;
using System.Runtime.InteropServices;

// Runs a program on its own, invisible Windows desktop (CreateDesktop), waits for it, and returns its exit code.
// Windows on that desktop are never shown to the user and can never take focus from them.
// Loaded from Tools\selftest.ps1 with Add-Type; compiles with the Windows PowerShell 5.1 C# compiler.
public static class HiddenRun
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktop(string name, string device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out int code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const uint GENERIC_ALL = 0x10000000;

    // returns the exit code, -1000 = could not start, -1001 = timed out (and the process was terminated)
    public static int Run(string exe, string args, string workDir, int timeoutMs, string desktopName)
    {
        IntPtr desk = CreateDesktop(desktopName, null, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (desk == IntPtr.Zero) return -1000;
        try
        {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = desktopName;
            PROCESS_INFORMATION pi;
            // environment is inherited (ELANSHUB_HIDDEN_DESKTOP is set by the caller)
            if (!CreateProcess(exe, "\"" + exe + "\" " + args, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workDir, ref si, out pi)) return -1000;
            try
            {
                if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != 0) { TerminateProcess(pi.hProcess, 1); return -1001; }
                int code; GetExitCodeProcess(pi.hProcess, out code); return code;
            }
            finally { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
        }
        finally { CloseDesktop(desk); }
    }
}
