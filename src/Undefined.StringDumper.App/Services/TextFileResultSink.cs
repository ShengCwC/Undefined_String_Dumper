using System.IO;
using System.Text;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Services;

namespace Undefined.StringDumper.App.Services;

public sealed class TextFileResultSink : IStringResultSink, IAsyncDisposable
{
    private readonly string _finalPath;
    private readonly string _partialPath;
    private readonly StreamWriter _writer;
    private bool _completed;
    private bool _disposed;

    private TextFileResultSink(string finalPath, string partialPath, StreamWriter writer)
    {
        _finalPath = finalPath;
        _partialPath = partialPath;
        _writer = writer;
    }

    public string FinalPath => _finalPath;

    public static async Task<TextFileResultSink> CreateAsync(
        string path,
        JavaProcessInfo process,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var finalPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(finalPath) ??
            throw new ArgumentException("Export path does not have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var partialName = $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.partial";
        var partialPath = Path.Combine(directory, partialName);
        var stream = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024);
        var sink = new TextFileResultSink(finalPath, partialPath, writer);

        try
        {
            await sink.WriteHeaderAsync(process, options, cancellationToken).ConfigureAwait(false);
            return sink;
        }
        catch
        {
            await sink.AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask WriteAsync(IReadOnlyList<ExtractedString> batch, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The export has already been completed.");
        }

        await _writer.WriteAsync(EvidenceTextFormatter.FormatBatch(batch).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CompleteAsync(ScanSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }

        await _writer.WriteAsync(EvidenceTextFormatter.FormatFooter(summary).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);

        File.Move(_partialPath, _finalPath, overwrite: true);
        _completed = true;
        _disposed = true;
    }

    public async Task AbortAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _writer.DisposeAsync().ConfigureAwait(false);
        try
        {
            File.Delete(_partialPath);
        }
        catch (IOException)
        {
            // A locked partial file is intentionally never promoted to the requested target.
        }
        catch (UnauthorizedAccessException)
        {
            // The original target remains untouched even if partial cleanup is denied.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await AbortAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteHeaderAsync(
        JavaProcessInfo process,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        await _writer.WriteAsync(EvidenceTextFormatter.FormatHeader(process, options).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

}
