namespace Undefined.StringDumper.Core.Models;

public sealed record ScanProgress(
    long BytesRead,
    long TotalReadableBytes,
    int RegionsCompleted,
    int TotalRegions,
    long StringsFound,
    int ReadFailures)
{
    public double Fraction => TotalReadableBytes <= 0
        ? 0
        : Math.Clamp((double)BytesRead / TotalReadableBytes, 0, 1);
}
