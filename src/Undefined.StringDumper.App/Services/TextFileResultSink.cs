using System.Globalization;
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

        await _writer.WriteAsync(buffer.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(ScanSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }

        await _writer.WriteLineAsync().ConfigureAwait(false);
        await _writer.WriteLineAsync($"# CompletedUtc: {summary.CompletedAt:O}").ConfigureAwait(false);
        await _writer.WriteLineAsync($"# RegionsScanned: {summary.RegionsScanned.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        await _writer.WriteLineAsync($"# BytesRead: {summary.BytesRead.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        await _writer.WriteLineAsync($"# StringsFound: {summary.StringsFound.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        await _writer.WriteLineAsync($"# ReadFailures: {summary.ReadFailures.ToString(CultureInfo.InvariantCulture)}")
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
        var header = new StringBuilder()
            .AppendLine("Undefined String Dumper 0.3.0 (Process Hacker 2.39 compatible)")
            .AppendLine($"Windows NT {Environment.OSVersion.Version} ({architecture})")
            .AppendLine(DateTime.Now.ToString("yyyy/M/d H:mm:ss", CultureInfo.InvariantCulture))
            .AppendLine($"Target: {process.ProcessLabel}; PID: {process.ProcessId.ToString(CultureInfo.InvariantCulture)}; Minimum length: {options.MinimumLength.ToString(CultureInfo.InvariantCulture)}; Encodings: {encodings}; Regions: {regionProfile}")
            .AppendLine()
            .ToString();

        await _writer.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

}
