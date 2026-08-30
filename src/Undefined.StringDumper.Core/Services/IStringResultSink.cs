using Undefined.StringDumper.Core.Models;

namespace Undefined.StringDumper.Core.Services;

public interface IStringResultSink
{
    ValueTask WriteAsync(IReadOnlyList<ExtractedString> batch, CancellationToken cancellationToken);
}
