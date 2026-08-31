using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Undefined.StringDumper.Core.Native;

internal static class NativeMethods
{
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessQueryInformation = 0x0400;

    internal const uint MemCommit = 0x1000;
    internal const uint MemPrivate = 0x20000;
    internal const uint MemMapped = 0x40000;
    internal const uint MemImage = 0x1000000;

    internal const uint PageNoAccess = 0x01;
    internal const uint PageGuard = 0x100;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll")]
    internal static extern nuint VirtualQueryEx(
        SafeProcessHandle process,
        nint address,
        out MemoryBasicInformation buffer,
        nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint bytesRead);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    internal static extern int NtQueryInformationProcessBasic(
        SafeProcessHandle process,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    internal static extern int NtQueryInformationProcessPointer(
        SafeProcessHandle process,
        int processInformationClass,
        out nint processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    internal static extern int NtQueryInformationProcessByte(
        SafeProcessHandle process,
        int processInformationClass,
        out byte processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessMitigationPolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessDepPolicy(
        SafeProcessHandle process,
        int mitigationPolicy,
        out ProcessMitigationDepPolicy buffer,
        nuint length);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessMitigationPolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMitigationPolicyFlags(
        SafeProcessHandle process,
        int mitigationPolicy,
        out uint buffer,
        nuint length);

    [DllImport("kernel32.dll")]
    internal static extern void GetNativeSystemInfo(out SystemInfo systemInfo);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out SafeFileHandle tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeFileHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    internal static bool TryEnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0))
            {
                return false;
            }

            return Marshal.GetLastWin32Error() == 0;
        }
    }

    internal static Win32Exception CreateLastError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        internal nint BaseAddress;
        internal nint AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemInfo
    {
        internal ushort ProcessorArchitecture;
        internal ushort Reserved;
        internal uint PageSize;
        internal nint MinimumApplicationAddress;
        internal nint MaximumApplicationAddress;
        internal nuint ActiveProcessorMask;
        internal uint NumberOfProcessors;
        internal uint ProcessorType;
        internal uint AllocationGranularity;
        internal ushort ProcessorLevel;
        internal ushort ProcessorRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessBasicInformation
    {
        internal nint Reserved1;
        internal nint PebBaseAddress;
        internal nint Reserved2_0;
        internal nint Reserved2_1;
        internal nint UniqueProcessId;
        internal nint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMitigationDepPolicy
    {
        internal uint Flags;
        internal byte Permanent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        internal uint PrivilegeCount;
        internal Luid Luid;
        internal uint Attributes;
    }
}
