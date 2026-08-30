using Undefined.StringDumper.Core.Models;

namespace Undefined.StringDumper.Core.Services;

public interface IMemoryStringScanner
{
    Task<ScanSummary> ScanAsync(
        int processId,
        ScanOptions options,
        IStringResultSink resultSink,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
