using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Services;

namespace Undefined.StringDumper.App.Services;

public sealed class CompositeResultSink(params IStringResultSink[] sinks) : IStringResultSink
{
    private readonly IStringResultSink[] _sinks = sinks.Length > 0
        ? sinks
        : throw new ArgumentException("At least one result sink is required.", nameof(sinks));

    public async ValueTask WriteAsync(IReadOnlyList<ExtractedString> batch, CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks)
        {
            await sink.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }
}
