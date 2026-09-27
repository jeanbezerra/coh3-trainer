using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Coh3Trainer.Interop;

internal static partial class NativeMethods
{
    internal const uint ProcessVmOperation = 0x0008;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessVmWrite = 0x0020;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ThreadSuspendResume = 0x0002;
    internal const uint MemCommit = 0x1000;
    internal const uint MemReserve = 0x2000;
    internal const uint PageGuard = 0x100;
    internal const uint PageNoAccess = 0x01;
    internal const uint PageExecuteReadWrite = 0x40;
    internal const uint ModNoRepeat = 0x4000;
    internal const int WmHotkey = 0x0312;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, nuint size, out nuint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, nuint size, out nuint bytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocationType, uint protection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint newProtection, out uint oldProtection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushInstructionCache(SafeProcessHandle process, nint baseAddress, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeWaitHandle OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint SuspendThread(SafeWaitHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint ResumeThread(SafeWaitHandle thread);

    [LibraryImport("kernel32.dll")]
    internal static partial nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryBasicInformation buffer, nuint length);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint windowHandle, int id);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint windowHandle, uint attribute, ref int value, uint valueSize);

    internal static void EnableDarkTitleBar(nint windowHandle)
    {
        const uint immersiveDarkMode = 20;
        const uint immersiveDarkModeBefore20H1 = 19;
        var enabled = 1;
        if (DwmSetWindowAttribute(windowHandle, immersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(windowHandle, immersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        private uint Alignment1;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        private uint Alignment2;
    }
}
