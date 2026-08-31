using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Native;

namespace Undefined.StringDumper.Core.Services;

/// <summary>
/// Reads target-process properties from documented process APIs and the PEB
/// process parameters used by Process Hacker/System Informer. Both native x64
/// and WOW64 targets are supported.
/// </summary>
public static class ProcessMetadataReader
{
    private const int ProcessBasicInformationClass = 0;
    private const int ProcessWow64InformationClass = 26;
    private const int ProcessProtectionInformationClass = 61;
    private const int Peb64ProcessParametersOffset = 0x20;
    private const int Peb32ProcessParametersOffset = 0x10;
    private const int ProcessParameters64CurrentDirectoryOffset = 0x38;
    private const int ProcessParameters32CurrentDirectoryOffset = 0x24;
    private const int ProcessParameters64CommandLineOffset = 0x70;
    private const int ProcessParameters32CommandLineOffset = 0x40;
    private const int UnicodeString64Size = 16;
    private const int UnicodeString32Size = 8;
    private const int MaximumRemoteStringBytes = ushort.MaxValue - 1;

    private const int ProcessDepPolicy = 0;
    private const int ProcessAslrPolicy = 1;
    private const int ProcessDynamicCodePolicy = 2;
    private const int ProcessControlFlowGuardPolicy = 7;
    private const int ProcessSignaturePolicy = 8;
    private const int ProcessFontDisablePolicy = 9;
    private const int ProcessImageLoadPolicy = 10;
    private const int ProcessChildProcessPolicy = 13;

    public static ProcessDetails Read(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsWindows())
        {
            return ProcessDetails.Empty;
        }

        try
        {
            using var process = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryInformation | NativeMethods.ProcessVmRead,
                false,
                processId);
            if (process.IsInvalid)
            {
                return ProcessDetails.Empty;
            }

            var basicStatus = NativeMethods.NtQueryInformationProcessBasic(
                process,
                ProcessBasicInformationClass,
                out var basicInformation,
                Marshal.SizeOf<NativeMethods.ProcessBasicInformation>(),
                out _);
            var hasBasicInformation = basicStatus >= 0 && basicInformation.PebBaseAddress != 0;

            var wow64Status = NativeMethods.NtQueryInformationProcessPointer(
                process,
                ProcessWow64InformationClass,
                out var wow64Peb,
                nint.Size,
                out _);
            var isWow64 = wow64Status >= 0 && wow64Peb != 0;

            string? currentDirectory = null;
            string? commandLine = null;
            nint displayedPeb = 0;
            if (isWow64)
            {
                displayedPeb = wow64Peb;
                var processParameters = TryReadPointer32(process, wow64Peb, Peb32ProcessParametersOffset);
                if (processParameters.HasValue)
                {
                    currentDirectory = TryReadUnicodeString32(
                        process,
                        processParameters.Value,
                        ProcessParameters32CurrentDirectoryOffset);
                    commandLine = TryReadUnicodeString32(
                        process,
                        processParameters.Value,
                        ProcessParameters32CommandLineOffset);
                }
            }
            else if (hasBasicInformation)
            {
                displayedPeb = basicInformation.PebBaseAddress;
                if (nint.Size == sizeof(ulong))
                {
                    var processParameters = TryReadPointer64(
                        process,
                        basicInformation.PebBaseAddress,
                        Peb64ProcessParametersOffset);
                    if (processParameters.HasValue)
                    {
                        currentDirectory = TryReadUnicodeString64(
                            process,
                            processParameters.Value,
                            ProcessParameters64CurrentDirectoryOffset);
                        commandLine = TryReadUnicodeString64(
                            process,
                            processParameters.Value,
                            ProcessParameters64CommandLineOffset);
                    }
                }
                else
                {
                    var processParameters = TryReadPointer32(
                        process,
                        basicInformation.PebBaseAddress,
                        Peb32ProcessParametersOffset);
                    if (processParameters.HasValue)
                    {
                        currentDirectory = TryReadUnicodeString32(
                            process,
                            processParameters.Value,
                            ProcessParameters32CurrentDirectoryOffset);
                        commandLine = TryReadUnicodeString32(
                            process,
                            processParameters.Value,
                            ProcessParameters32CommandLineOffset);
                    }
                }
            }

            return new ProcessDetails
            {
                CommandLine = SensitiveCommandLineRedactor.Redact(commandLine),
                CurrentDirectory = currentDirectory,
                PebAddress = displayedPeb == 0
                    ? null
                    : $"0x{displayedPeb.ToInt64().ToString("x", CultureInfo.InvariantCulture)}",
                ImageType = !Environment.Is64BitOperatingSystem
                    ? "32-bit"
                    : wow64Status < 0
                        ? null
                        : isWow64 ? "32-bit" : "64-bit",
                ParentProcess = hasBasicInformation
                    ? TryFormatParentProcess(basicInformation.InheritedFromUniqueProcessId)
                    : null,
                MitigationPolicies = TryFormatMitigationPolicies(process),
                Protection = TryFormatProtection(process),
            };
        }
        catch (Exception exception) when (exception is
            OverflowException or
            ArgumentException or
            InvalidOperationException or
            UnauthorizedAccessException or
            Win32Exception or
            NotSupportedException)
        {
            return ProcessDetails.Empty;
        }
    }

    public static string? TryGetCurrentDirectory(int processId) => Read(processId).CurrentDirectory;

    private static uint? TryReadPointer32(SafeProcessHandle process, nint address, int offset)
    {
        var pointerBytes = new byte[sizeof(uint)];
        return TryReadExact(process, AddOffset(address, offset), pointerBytes)
            ? BinaryPrimitives.ReadUInt32LittleEndian(pointerBytes)
            : null;
    }

    private static ulong? TryReadPointer64(SafeProcessHandle process, nint address, int offset)
    {
        var pointerBytes = new byte[sizeof(ulong)];
        return TryReadExact(process, AddOffset(address, offset), pointerBytes)
            ? BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes)
            : null;
    }

    private static string? TryReadUnicodeString64(SafeProcessHandle process, ulong processParameters, int offset)
    {
        if (processParameters == 0 || processParameters > long.MaxValue)
        {
            return null;
        }

        var unicodeString = new byte[UnicodeString64Size];
        if (!TryReadExact(process, AddOffset((nint)(long)processParameters, offset), unicodeString))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(unicodeString);
        var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(unicodeString.AsSpan(8));
        return bufferAddress <= long.MaxValue
            ? TryReadUnicodeValue(process, length, (nint)(long)bufferAddress)
            : null;
    }

    private static string? TryReadUnicodeString32(SafeProcessHandle process, uint processParameters, int offset)
    {
        if (processParameters == 0)
        {
            return null;
        }

        var unicodeString = new byte[UnicodeString32Size];
        if (!TryReadExact(process, AddOffset((nint)(long)processParameters, offset), unicodeString))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(unicodeString);
        var bufferAddress = BinaryPrimitives.ReadUInt32LittleEndian(unicodeString.AsSpan(4));
        return TryReadUnicodeValue(process, length, (nint)(long)bufferAddress);
    }

    private static string? TryReadUnicodeValue(SafeProcessHandle process, int length, nint bufferAddress)
    {
        if (length <= 0 || length > MaximumRemoteStringBytes || (length & 1) != 0 || bufferAddress == 0)
        {
            return null;
        }

        var valueBytes = new byte[length];
        if (!TryReadExact(process, bufferAddress, valueBytes))
        {
            return null;
        }

        var value = Encoding.Unicode.GetString(valueBytes).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? TryFormatParentProcess(nint parentProcessIdValue)
    {
        var parentProcessId = parentProcessIdValue.ToInt64();
        if (parentProcessId <= 0 || parentProcessId > int.MaxValue)
        {
            return null;
        }

        try
        {
            using var parent = Process.GetProcessById((int)parentProcessId);
            return $"{parent.ProcessName}.exe ({parentProcessId.ToString(CultureInfo.InvariantCulture)})";
        }
        catch (ArgumentException)
        {
            return $"PID {parentProcessId.ToString(CultureInfo.InvariantCulture)}（已退出）";
        }
        catch (InvalidOperationException)
        {
            return $"PID {parentProcessId.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Win32Exception)
        {
            return $"PID {parentProcessId.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (NotSupportedException)
        {
            return $"PID {parentProcessId.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    private static string? TryFormatMitigationPolicies(SafeProcessHandle process)
    {
        var policies = new List<string>();
        var queried = false;

        if (NativeMethods.GetProcessDepPolicy(
                process,
                ProcessDepPolicy,
                out var dep,
                (nuint)Marshal.SizeOf<NativeMethods.ProcessMitigationDepPolicy>()))
        {
            queried = true;
            if ((dep.Flags & 0x1) != 0)
            {
                policies.Add(dep.Permanent != 0 ? "DEP (permanent)" : "DEP");
            }
        }

        if (TryGetPolicyFlags(process, ProcessAslrPolicy, out var aslr))
        {
            queried = true;
            var features = new List<string>();
            AddFlag(features, aslr, 0x1, "bottom-up");
            AddFlag(features, aslr, 0x2, "force relocate");
            AddFlag(features, aslr, 0x4, "high entropy");
            AddFlag(features, aslr, 0x8, "disallow stripped");
            if (features.Count > 0)
            {
                policies.Add($"ASLR ({string.Join(", ", features)})");
            }
        }

        AddSimplePolicy(process, ProcessDynamicCodePolicy, 0x1, "Dynamic code prohibited", policies, ref queried);
        AddSimplePolicy(process, ProcessControlFlowGuardPolicy, 0x1, "CFG", policies, ref queried);
        AddSimplePolicy(process, ProcessSignaturePolicy, 0x1, "Microsoft-signed binaries only", policies, ref queried);
        AddSimplePolicy(process, ProcessFontDisablePolicy, 0x1, "Non-system fonts disabled", policies, ref queried);
        AddSimplePolicy(process, ProcessImageLoadPolicy, 0x1, "Remote images blocked", policies, ref queried);
        AddSimplePolicy(process, ProcessChildProcessPolicy, 0x1, "Child process creation blocked", policies, ref queried);

        if (!queried)
        {
            return null;
        }

        return policies.Count == 0 ? "None" : string.Join("; ", policies);
    }

    private static void AddSimplePolicy(
        SafeProcessHandle process,
        int policy,
        uint enabledFlag,
        string label,
        ICollection<string> policies,
        ref bool queried)
    {
        if (!TryGetPolicyFlags(process, policy, out var flags))
        {
            return;
        }

        queried = true;
        if ((flags & enabledFlag) != 0)
        {
            policies.Add(label);
        }
    }

    private static bool TryGetPolicyFlags(SafeProcessHandle process, int policy, out uint flags) =>
        NativeMethods.GetProcessMitigationPolicyFlags(process, policy, out flags, sizeof(uint));

    private static void AddFlag(ICollection<string> values, uint flags, uint flag, string label)
    {
        if ((flags & flag) != 0)
        {
            values.Add(label);
        }
    }

    private static string? TryFormatProtection(SafeProcessHandle process)
    {
        var status = NativeMethods.NtQueryInformationProcessByte(
            process,
            ProcessProtectionInformationClass,
            out var protection,
            sizeof(byte),
            out _);
        if (status < 0)
        {
            return null;
        }

        var type = protection & 0x7;
        if (type == 0)
        {
            return "None";
        }

        var typeLabel = type switch
        {
            1 => "PPL",
            2 => "Protected",
            _ => $"Type {type.ToString(CultureInfo.InvariantCulture)}",
        };
        var signer = (protection >> 4) & 0xF;
        var signerLabel = signer switch
        {
            0 => "None",
            1 => "Authenticode",
            2 => "CodeGen",
            3 => "Antimalware",
            4 => "LSA",
            5 => "Windows",
            6 => "WinTcb",
            7 => "WinSystem",
            8 => "App",
            _ => signer.ToString(CultureInfo.InvariantCulture),
        };
        return $"{typeLabel} ({signerLabel})";
    }

    private static bool TryReadExact(SafeProcessHandle process, nint address, byte[] buffer) =>
        NativeMethods.ReadProcessMemory(
            process,
            address,
            buffer,
            (nuint)buffer.Length,
            out var bytesRead) &&
        bytesRead == (nuint)buffer.Length;

    private static nint AddOffset(nint address, int offset) =>
        (nint)checked(address.ToInt64() + offset);
}
