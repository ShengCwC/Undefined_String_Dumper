using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Services;
using ZstdSharp;

namespace Undefined.StringDumper.App.Services;

public sealed class EncryptedArchiveSink : IStringResultSink, IAsyncDisposable
{
    public const int DefaultPartSizeBytes = 8 * 1024 * 1024;
    private const string CheckpointFileName = "checkpoint.json";
    private static readonly byte[] Magic = "USDC0001"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _archiveId;
    private readonly string _spoolDirectory;
    private readonly byte[] _dataKey;
    private readonly int _partSizeBytes;
    private readonly string _processName;
    private readonly int _processId;
    private readonly string _dumperVersion;
    private readonly DateTimeOffset _createdAt;
    private readonly MemoryStream _plaintextBuffer;
    private readonly IncrementalHash _plaintextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash _ciphertextChainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<DumperArchivePartState> _parts = [];
    private bool _completed;
    private bool _disposed;
    private long _totalPlainBytes;
    private long _totalCipherBytes;

    private EncryptedArchiveSink(
        string archiveId,
        string spoolDirectory,
        byte[] dataKey,
        int partSizeBytes,
        JavaProcessInfo process,
        string dumperVersion)
    {
        _archiveId = archiveId;
        _spoolDirectory = spoolDirectory;
        _dataKey = dataKey;
        _partSizeBytes = partSizeBytes;
        _processName = process.ProcessName + ".exe";
        _processId = process.ProcessId;
        _dumperVersion = dumperVersion;
        _createdAt = DateTimeOffset.UtcNow;
        _plaintextBuffer = new MemoryStream(partSizeBytes);
    }

    public string ArchiveId => _archiveId;

    public string SpoolDirectory => _spoolDirectory;

    public static async Task<EncryptedArchiveSink> CreateAsync(
        string archiveId,
        byte[] dataKey,
        JavaProcessInfo process,
        ScanOptions options,
        int partSizeBytes = DefaultPartSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        if (!Guid.TryParse(archiveId, out _)) throw new ArgumentException("Archive ID must be a UUID.", nameof(archiveId));
        if (dataKey.Length != 32) throw new ArgumentException("Archive data key must contain 32 bytes.", nameof(dataKey));
        if (partSizeBytes is < 1024 * 1024 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(partSizeBytes));
        options.Validate();

        var spoolDirectory = ArchiveSpoolStore.GetArchiveDirectory(archiveId);
        if (Directory.Exists(spoolDirectory))
        {
            throw new IOException("This archive already has a local checkpoint. Resume or clear it before rescanning.");
        }
        Directory.CreateDirectory(spoolDirectory);
        var dumperVersion = typeof(EncryptedArchiveSink).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var sink = new EncryptedArchiveSink(archiveId, spoolDirectory, dataKey.ToArray(), partSizeBytes, process, dumperVersion);
        try
        {
            await sink.AppendBytesAsync(Encoding.UTF8.GetPreamble(), cancellationToken).ConfigureAwait(false);
            await sink.AppendTextAsync(EvidenceTextFormatter.FormatHeader(process, options), cancellationToken).ConfigureAwait(false);
            await sink.SaveCheckpointAsync(scanCompleted: false, "", "", cancellationToken).ConfigureAwait(false);
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
        if (_completed) throw new InvalidOperationException("The encrypted archive has already been completed.");
        await AppendTextAsync(EvidenceTextFormatter.FormatBatch(batch), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DumperArchiveCheckpoint> CompleteAsync(
        ScanSummary summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return await ArchiveSpoolStore.LoadCheckpointAsync(_archiveId, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Completed archive checkpoint is missing.");
        }

        await AppendTextAsync(EvidenceTextFormatter.FormatFooter(summary), cancellationToken).ConfigureAwait(false);
        await FlushPartAsync(cancellationToken).ConfigureAwait(false);
        var plaintextSha256 = Convert.ToHexString(_plaintextHash.GetHashAndReset()).ToLowerInvariant();
        var ciphertextSha256 = Convert.ToHexString(_ciphertextChainHash.GetHashAndReset()).ToLowerInvariant();
        var checkpoint = await SaveCheckpointAsync(
            scanCompleted: true,
            plaintextSha256,
            ciphertextSha256,
            cancellationToken).ConfigureAwait(false);
        _completed = true;
        return checkpoint;
    }

    public async Task AbortAsync()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_dataKey);
        _plaintextHash.Dispose();
        _ciphertextChainHash.Dispose();
        await _plaintextBuffer.DisposeAsync().ConfigureAwait(false);
        await ArchiveSpoolStore.DeleteAsync(_archiveId).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_dataKey);
        _plaintextHash.Dispose();
        _ciphertextChainHash.Dispose();
        await _plaintextBuffer.DisposeAsync().ConfigureAwait(false);
        if (!_completed)
        {
            await ArchiveSpoolStore.DeleteAsync(_archiveId).ConfigureAwait(false);
        }
    }

    private async Task AppendTextAsync(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await AppendBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = _partSizeBytes - checked((int)_plaintextBuffer.Length);
            var count = Math.Min(available, bytes.Length - offset);
            var slice = bytes.Slice(offset, count);
            await _plaintextBuffer.WriteAsync(slice, cancellationToken).ConfigureAwait(false);
            _plaintextHash.AppendData(slice.Span);
            _totalPlainBytes += count;
            offset += count;
            if (_plaintextBuffer.Length >= _partSizeBytes)
            {
                await FlushPartAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushPartAsync(CancellationToken cancellationToken)
    {
        if (_plaintextBuffer.Length == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = _plaintextBuffer.ToArray();
        _plaintextBuffer.SetLength(0);
        var partIndex = _parts.Count;
        EncryptedPart encrypted;
        try
        {
            encrypted = await Task.Run(
                () => EncryptPart(_archiveId, partIndex, plaintext, _dataKey),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var fileName = $"{_archiveId}.part-{partIndex:D6}.usdc";
        var path = Path.Combine(_spoolDirectory, fileName);
        await File.WriteAllBytesAsync(path, encrypted.Bytes, cancellationToken).ConfigureAwait(false);
        var hashBytes = Convert.FromHexString(encrypted.Sha256);
        _ciphertextChainHash.AppendData(hashBytes);
        _totalCipherBytes += encrypted.Bytes.LongLength;
        _parts.Add(new DumperArchivePartState(
            partIndex,
            fileName,
            encrypted.PlainBytes,
            encrypted.Bytes.LongLength,
            encrypted.Sha256));
        await SaveCheckpointAsync(scanCompleted: false, "", "", cancellationToken).ConfigureAwait(false);
    }

    private async Task<DumperArchiveCheckpoint> SaveCheckpointAsync(
        bool scanCompleted,
        string plaintextSha256,
        string ciphertextSha256,
        CancellationToken cancellationToken)
    {
        var checkpoint = new DumperArchiveCheckpoint(
            _archiveId,
            _processName,
            _processId,
            _dumperVersion,
            _partSizeBytes,
            _createdAt,
            scanCompleted,
            _totalPlainBytes,
            _totalCipherBytes,
            plaintextSha256,
            ciphertextSha256,
            _parts.ToArray());
        await ArchiveSpoolStore.SaveCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    internal static EncryptedPart EncryptPart(string archiveId, int partIndex, byte[] plaintext, byte[] dataKey)
    {
        using var compressor = new Compressor(3);
        var compressed = compressor.Wrap(plaintext).ToArray();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[compressed.Length];
            var aad = BuildAdditionalData(archiveId, partIndex, plaintext.LongLength);
            using (var aes = new AesGcm(dataKey, 16))
            {
                aes.Encrypt(nonce, compressed, ciphertext, tag, aad);
            }

            var headerLength = Magic.Length + 16 + sizeof(int) + sizeof(long) + sizeof(long) + nonce.Length + tag.Length;
            var output = new byte[headerLength + ciphertext.Length];
            var offset = 0;
            Magic.CopyTo(output, offset);
            offset += Magic.Length;
            Guid.Parse(archiveId).ToByteArray().CopyTo(output, offset);
            offset += 16;
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offset, sizeof(int)), partIndex);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(offset, sizeof(long)), plaintext.LongLength);
            offset += sizeof(long);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(offset, sizeof(long)), compressed.LongLength);
            offset += sizeof(long);
            nonce.CopyTo(output, offset);
            offset += nonce.Length;
            tag.CopyTo(output, offset);
            offset += tag.Length;
            ciphertext.CopyTo(output, offset);

            var sha256 = Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant();
            return new EncryptedPart(output, plaintext.LongLength, sha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(compressed);
        }
    }

    internal static byte[] BuildAdditionalData(string archiveId, int partIndex, long plainBytes) =>
        Encoding.UTF8.GetBytes($"usd-part:v1:{archiveId}:{partIndex}:{plainBytes}");

    internal static ReadOnlySpan<byte> PartMagic => Magic;

    internal sealed record EncryptedPart(byte[] Bytes, long PlainBytes, string Sha256);
}

public static class ArchiveSpoolStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string RootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UndefinedStringDumper",
        "Uploads");

    public static string GetArchiveDirectory(string archiveId)
    {
        if (!Guid.TryParse(archiveId, out var parsed)) throw new ArgumentException("Archive ID must be a UUID.", nameof(archiveId));
        return Path.Combine(RootPath, parsed.ToString("D"));
    }

    public static string GetPartPath(string archiveId, string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".usdc", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Archive part file name is invalid.");
        }
        return Path.Combine(GetArchiveDirectory(archiveId), fileName);
    }

    public static async Task SaveCheckpointAsync(
        DumperArchiveCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        var directory = GetArchiveDirectory(checkpoint.ArchiveId);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, "checkpoint.json");
        var temporaryPath = Path.Combine(directory, $"checkpoint.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(checkpoint, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, finalPath, overwrite: true);
    }

    public static async Task<DumperArchiveCheckpoint?> LoadCheckpointAsync(
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(GetArchiveDirectory(archiveId), "checkpoint.json");
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, true);
        return await JsonSerializer.DeserializeAsync<DumperArchiveCheckpoint>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task DeleteAsync(string archiveId)
    {
        var target = Path.GetFullPath(GetArchiveDirectory(archiveId));
        var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || target.Length <= root.Length)
        {
            throw new IOException("Refusing to remove an unsafe archive spool path.");
        }
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        return Task.CompletedTask;
    }
}
