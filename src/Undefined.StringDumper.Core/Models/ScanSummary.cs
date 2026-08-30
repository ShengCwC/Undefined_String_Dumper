namespace Undefined.StringDumper.Core.Models;

public sealed record ScanSummary(
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RegionsScanned,
    long BytesRead,
    long StringsFound,
    int ReadFailures)
{
    public TimeSpan Duration => CompletedAt - StartedAt;
}
