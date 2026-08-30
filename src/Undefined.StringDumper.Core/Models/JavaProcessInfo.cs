namespace Undefined.StringDumper.Core.Models;

public sealed record JavaProcessInfo(
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string? ExecutablePath,
    long PrivateMemoryBytes,
    DateTimeOffset? StartTime)
{
    public string ProcessLabel => $"{ProcessName}.exe";

    public string ProcessIdLabel => $"PID {ProcessId}";

    public string MemoryLabel => FormatBytes(PrivateMemoryBytes);

    public string PathLabel => string.IsNullOrWhiteSpace(ExecutablePath)
        ? "路径受系统保护"
        : ExecutablePath;

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
}
