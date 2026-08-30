using System.Collections.ObjectModel;
using System.Windows.Threading;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Services;

namespace Undefined.StringDumper.App.Services;

public sealed class UiPreviewResultSink : IStringResultSink
{
    public const int DefaultPreviewLimit = 20_000;

    private readonly Dispatcher _dispatcher;
    private readonly ObservableCollection<ExtractedString> _target;
    private readonly int _previewLimit;
    private int _accepted;

    public UiPreviewResultSink(
        Dispatcher dispatcher,
        ObservableCollection<ExtractedString> target,
        int previewLimit = DefaultPreviewLimit)
    {
        _dispatcher = dispatcher;
        _target = target;
        _previewLimit = previewLimit;
    }

    public bool IsTruncated => _accepted >= _previewLimit;

    public async ValueTask WriteAsync(IReadOnlyList<ExtractedString> batch, CancellationToken cancellationToken)
    {
        var remaining = _previewLimit - _accepted;
        if (remaining <= 0)
        {
            return;
        }

        var accepted = batch.Take(remaining).ToArray();
        _accepted += accepted.Length;
        if (accepted.Length == 0)
        {
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                foreach (var item in accepted)
                {
                    _target.Add(item);
                }
            },
            DispatcherPriority.Background,
            cancellationToken);
    }
}
