using System.Globalization;
using Undefined.StringDumper.Core.Services;

namespace Undefined.StringDumper.Core.Models;

public sealed record JavaProcessInfo(
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string? ExecutablePath,
    long PrivateMemoryBytes,
    DateTimeOffset? StartTime,
    ProcessDetails? Details = null)
{
    private ProcessDetails ProcessDetails => Details ?? global::Undefined.StringDumper.Core.Models.ProcessDetails.Empty;

    public string ProcessLabel => $"{ProcessName}.exe";

    public string ProcessIdLabel => $"PID {ProcessId}";

    public string MemoryLabel => FormatBytes(PrivateMemoryBytes);

    public string PathLabel => string.IsNullOrWhiteSpace(ExecutablePath)
        ? "路径受系统保护"
        : ExecutablePath;

    public string DescriptionLabel => ValueOrUnavailable(DisplayName);

    public string FileVersionLabel => ValueOrProtected(ProcessDetails.FileVersion);

    public string SignatureLabel
    {
        get
        {
            var status = ValueOrProtected(ProcessDetails.SignatureStatus);
            return string.IsNullOrWhiteSpace(ProcessDetails.SignerName)
                ? status
                : $"{status} · {ProcessDetails.SignerName}";
        }
    }

    public string CommandLineLabel => ValueOrProtected(SensitiveCommandLineRedactor.Redact(ProcessDetails.CommandLine));

    public string CurrentDirectoryLabel => string.IsNullOrWhiteSpace(ProcessDetails.CurrentDirectory)
        ? "目录受系统保护"
        : ProcessDetails.CurrentDirectory;

    public string PebAddressLabel => ValueOrProtected(ProcessDetails.PebAddress);

    public string ImageTypeLabel => ValueOrProtected(ProcessDetails.ImageType);

    public string ParentProcessLabel => ValueOrProtected(ProcessDetails.ParentProcess);

    public string MitigationPoliciesLabel => ValueOrProtected(ProcessDetails.MitigationPolicies);

    public string ProtectionLabel => ValueOrProtected(ProcessDetails.Protection);

    public string StartedLabel
    {
        get
        {
            if (!StartTime.HasValue)
            {
                return "启动时间受系统保护";
            }

            var localStartTime = StartTime.Value.ToLocalTime();
            var elapsed = DateTimeOffset.Now - StartTime.Value;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            return $"已运行 {FormatDuration(elapsed)} · {localStartTime:yyyy/M/d HH:mm:ss}";
        }
    }

    public string StartedExportLabel => StartTime.HasValue
        ? StartTime.Value.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)
        : "Unavailable";

    public string UptimeExportLabel
    {
        get
        {
            if (!StartTime.HasValue)
            {
                return "Unavailable";
            }

            var elapsed = DateTimeOffset.Now - StartTime.Value;
            return FormatDuration(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
        }
    }

    public string FileVersionExportValue => ValueOrUnavailable(ProcessDetails.FileVersion);

    public string SignatureExportValue => ValueOrUnavailable(ProcessDetails.SignatureStatus);

    public string SignerExportValue => ValueOrUnavailable(ProcessDetails.SignerName);

    public string ExecutablePathExportValue => ValueOrUnavailable(ExecutablePath);

    public string CommandLineExportValue => ValueOrUnavailable(SensitiveCommandLineRedactor.Redact(ProcessDetails.CommandLine));

    public string CurrentDirectoryExportValue => ValueOrUnavailable(ProcessDetails.CurrentDirectory);

    public string PebAddressExportValue => ValueOrUnavailable(ProcessDetails.PebAddress);

    public string ImageTypeExportValue => ValueOrUnavailable(ProcessDetails.ImageType);

    public string ParentProcessExportValue => ValueOrUnavailable(ProcessDetails.ParentProcess);

    public string MitigationPoliciesExportValue => ValueOrUnavailable(ProcessDetails.MitigationPolicies);

    public string ProtectionExportValue => ValueOrUnavailable(ProcessDetails.Protection);

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "--";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays} 天 {duration.Hours} 小时";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes} 分钟";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)} 秒";
    }

    private static string ValueOrProtected(string? value) => string.IsNullOrWhiteSpace(value)
        ? "受系统保护或不可用"
        : value;

    private static string ValueOrUnavailable(string? value) => string.IsNullOrWhiteSpace(value)
        ? "Unavailable"
        : value;
}
