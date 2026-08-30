using System.Diagnostics;
using Undefined.StringDumper.Core.Models;

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
                    long privateBytes = 0;
                    DateTimeOffset? startTime = null;

                    try
                    {
                        path = process.MainModule?.FileName;
                        description = process.MainModule?.FileVersionInfo.FileDescription;
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

                    results.Add(new JavaProcessInfo(
                        process.Id,
                        processName,
                        string.IsNullOrWhiteSpace(description) ? "Minecraft Java 进程" : description,
                        path,
                        privateBytes,
                        startTime));
                }
            }

            return results
                .OrderByDescending(item => item.PrivateMemoryBytes)
                .ThenBy(item => item.ProcessId)
                .ToArray();
        }, cancellationToken);
    }
}
