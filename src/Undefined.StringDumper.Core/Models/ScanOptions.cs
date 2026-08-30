namespace Undefined.StringDumper.Core.Models;

public sealed record ScanOptions
{
    public int MinimumLength { get; init; } = 4;

    public int MaximumStringLength { get; init; } = 8191;

    public int ReadBufferSize { get; init; } = 1024 * 1024;

    public bool DetectAscii { get; init; } = true;

    public bool DetectUnicode { get; init; } = true;

    public bool IncludePrivate { get; init; } = true;

    public bool IncludeMapped { get; init; } = true;

    public bool IncludeImage { get; init; }

    public void Validate()
    {
        if (MinimumLength is < 2 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumLength), "Minimum length must be between 2 and 1024.");
        }

        if (MaximumStringLength < MinimumLength || MaximumStringLength > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStringLength));
        }

        if (ReadBufferSize is < 4096 or > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(ReadBufferSize));
        }

        if (!DetectAscii && !DetectUnicode)
        {
            throw new ArgumentException("At least one text encoding must be enabled.");
        }

        if (!IncludePrivate && !IncludeMapped && !IncludeImage)
        {
            throw new ArgumentException("At least one memory region kind must be enabled.");
        }
    }
}
