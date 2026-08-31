using System.Diagnostics;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Native;

namespace Undefined.StringDumper.Core.Services;

public sealed class ProcessCatalog : IProcessCatalog
{
    private static readonly HashSet<string> TargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "java",
        "javaw",
    };

    public Task<IReadOnlyList<JavaProcessInfo>> GetJavaProcessesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<JavaProcessInfo>>(() =>
        {
            var results = new List<JavaProcessInfo>();
            _ = NativeMethods.TryEnableDebugPrivilege();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string processName;
                    try
                    {
                        processName = process.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!TargetNames.Contains(processName))
                    {
                        continue;
                    }

                    string? path = null;
                    string? description = null;
                    string? fileVersion = null;
                    long privateBytes = 0;
                    DateTimeOffset? startTime = null;

                    try
                    {
                        var mainModule = process.MainModule;
                        path = mainModule?.FileName;
                        description = mainModule?.FileVersionInfo.FileDescription;
                        fileVersion = mainModule?.FileVersionInfo.FileVersion;
                    }
                    catch
                    {
                        // Protected processes can still be listed and selected.
                    }

                    try
                    {
                        privateBytes = process.PrivateMemorySize64;
                    }
                    catch
                    {
                        // Leave the optional metric unavailable.
                    }

                    try
                    {
                        startTime = process.StartTime;
                    }
                    catch
                    {
                        // Leave the optional start time unavailable.
                    }

                    var details = ProcessMetadataReader.Read(process.Id);
                    var signature = AuthenticodeInspector.Inspect(path);
                    details = details with
                    {
                        FileVersion = fileVersion,
                        SignatureStatus = signature.Status,
                        SignerName = signature.SignerName,
                    };

                    results.Add(new JavaProcessInfo(
                        process.Id,
                        processName,
                        string.IsNullOrWhiteSpace(description) ? "Minecraft Java 进程" : description,
                        path,
                        privateBytes,
                        startTime,
                        details));
                }
            }

            return results
                .OrderByDescending(item => item.PrivateMemoryBytes)
                .ThenBy(item => item.ProcessId)
                .ToArray();
        }, cancellationToken);
    }
}
