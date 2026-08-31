using Undefined.StringDumper.Core.Models;

namespace Undefined.StringDumper.Core.Services;

/// <summary>
/// Streams only case-insensitive substring matches to a downstream sink while
/// retaining an exact count across the complete scan.
/// </summary>
public sealed class ContainsFilteringResultSink : IStringResultSink
{
    private const int ForwardBatchSize = 256;
    private readonly string _filterText;
    private readonly IStringResultSink _downstream;
    private readonly List<ExtractedString> _pending = new(ForwardBatchSize);
    private long _matchesFound;

    public ContainsFilteringResultSink(string filterText, IStringResultSink downstream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filterText);
        ArgumentNullException.ThrowIfNull(downstream);

        _filterText = filterText;
        _downstream = downstream;
    }

    public long MatchesFound => Interlocked.Read(ref _matchesFound);

    public async ValueTask WriteAsync(IReadOnlyList<ExtractedString> batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var result in batch)
        {
            if (!result.Value.Contains(_filterText, StringComparison.OrdinalIgnoreCase) &&
                !result.AddressText.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _pending.Add(result);
            Interlocked.Increment(ref _matchesFound);
            if (_pending.Count >= ForwardBatchSize)
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pending.Count == 0)
        {
            return;
        }

        var batch = _pending.ToArray();
        _pending.Clear();
        await _downstream.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
    }
}
