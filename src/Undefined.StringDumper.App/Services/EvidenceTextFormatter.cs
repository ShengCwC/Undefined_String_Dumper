using System.Globalization;
using System.Text;
using Undefined.StringDumper.Core.Models;

namespace Undefined.StringDumper.App.Services;

public static class EvidenceTextFormatter
{
    private static readonly string ProductVersion =
        typeof(EvidenceTextFormatter).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string FormatHeader(JavaProcessInfo process, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var regionProfile = string.Join(
            ",",
            new[]
            {
                options.IncludePrivate ? "Private" : null,
                options.IncludeMapped ? "Mapped" : null,
                options.IncludeImage ? "Image" : null,
            }.Where(value => value is not null));
        var encodings = string.Join(
            ",",
            new[]
            {
                options.DetectAscii ? "ASCII" : null,
                options.DetectUnicode ? "Unicode(PH-wide)" : null,
            }.Where(value => value is not null));

        var architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
        return new StringBuilder()
            .Append("Undefined String Dumper ")
            .Append(ProductVersion)
            .AppendLine(" (Process Hacker 2.39 compatible)")
            .AppendLine($"Windows NT {Environment.OSVersion.Version} ({architecture})")
            .AppendLine(DateTime.Now.ToString("yyyy/M/d H:mm:ss", CultureInfo.InvariantCulture))
            .AppendLine($"Target: {process.ProcessLabel}; PID: {process.ProcessId.ToString(CultureInfo.InvariantCulture)}; Minimum length: {options.MinimumLength.ToString(CultureInfo.InvariantCulture)}; Encodings: {encodings}; Regions: {regionProfile}")
            .AppendLine($"Description: {process.DescriptionLabel}")
            .AppendLine($"Signature: {process.SignatureExportValue}; Signer: {process.SignerExportValue}")
            .AppendLine($"Version: {process.FileVersionExportValue}")
            .AppendLine($"Image file name: {process.ExecutablePathExportValue}")
            .AppendLine($"Command line: {process.CommandLineExportValue}")
            .AppendLine($"Current directory: {process.CurrentDirectoryExportValue}")
            .AppendLine($"Started: {process.StartedExportLabel}; Uptime at capture: {process.UptimeExportLabel}")
            .AppendLine($"PEB address: {process.PebAddressExportValue}; Image type: {process.ImageTypeExportValue}")
            .AppendLine($"Parent: {process.ParentProcessExportValue}")
            .AppendLine($"Mitigation policies: {process.MitigationPoliciesExportValue}")
            .AppendLine($"Protection: {process.ProtectionExportValue}")
            .AppendLine()
            .ToString();
    }

    public static string FormatBatch(IReadOnlyList<ExtractedString> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var buffer = new StringBuilder(Math.Max(1024, batch.Count * 64));
        foreach (var result in batch)
        {
            buffer.Append("0x")
                .Append(result.Address.ToString("x", CultureInfo.InvariantCulture))
                .Append(" (")
                .Append(result.Length.ToString(CultureInfo.InvariantCulture))
                .Append("): ")
                .Append(result.Value)
                .AppendLine();
        }

        return buffer.ToString();
    }

    public static string FormatFooter(ScanSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new StringBuilder()
            .AppendLine()
            .AppendLine($"# CompletedUtc: {summary.CompletedAt:O}")
            .AppendLine($"# RegionsScanned: {summary.RegionsScanned.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine($"# BytesRead: {summary.BytesRead.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine($"# StringsFound: {summary.StringsFound.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine($"# ReadFailures: {summary.ReadFailures.ToString(CultureInfo.InvariantCulture)}")
            .ToString();
    }
}
