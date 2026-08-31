using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Undefined.StringDumper.Core.Models;

namespace Undefined.StringDumper.App.Services;

public sealed class DumperArchiveClient : IDisposable
{
    public const string ProductionBaseAddress = "https://screenshare.cn/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public DumperArchiveClient()
        : this(new HttpClient
        {
            BaseAddress = new Uri(ProductionBaseAddress),
            Timeout = TimeSpan.FromMinutes(3),
        }, ownsClient: true)
    {
    }

    internal DumperArchiveClient(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    public async Task<(DumperArchiveRemoteState Archive, byte[] DataKey)> CreateOrResumeAsync(
        string credential,
        string archiveId,
        JavaProcessInfo process,
        int partSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        return await CreateOrResumeAsync(
            credential,
            archiveId,
            process.ProcessName + ".exe",
            process.ProcessId,
            typeof(DumperArchiveClient).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            partSizeBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(DumperArchiveRemoteState Archive, byte[] DataKey)> CreateOrResumeAsync(
        string credential,
        string archiveId,
        string processName,
        int processId,
        string dumperVersion,
        int partSizeBytes,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            processName,
            processId,
            dumperVersion,
            partSizeBytes,
        };
        using var request = CreateRequest(HttpMethod.Post, $"api/dumper/archives/{NormalizeArchiveId(archiveId)}", credential);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var envelope = await ReadEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        if (envelope.Archive is null || string.IsNullOrWhiteSpace(envelope.DataKey))
        {
            throw new DumperArchiveException("DUMPER_CREATE_RESPONSE_INVALID", "服务器没有返回完整的归档会话。", response.StatusCode);
        }
        var dataKey = DecodeBase64Url(envelope.DataKey);
        if (dataKey.Length != 32)
        {
            throw new DumperArchiveException("DUMPER_DATA_KEY_INVALID", "服务器返回的归档密钥长度无效。", response.StatusCode);
        }
        return (envelope.Archive, dataKey);
    }

    public async Task<DumperArchiveRemoteState> GetStatusAsync(
        string credential,
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/dumper/archives/{NormalizeArchiveId(archiveId)}", credential);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var envelope = await ReadEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        return envelope.Archive
            ?? throw new DumperArchiveException("DUMPER_STATUS_INVALID", "服务器没有返回归档状态。", response.StatusCode);
    }

    public async Task UploadCheckpointAsync(
        string credential,
        DumperArchiveCheckpoint checkpoint,
        IProgress<DumperArchiveTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!checkpoint.ScanCompleted) throw new InvalidOperationException("The local encrypted scan is incomplete.");
        var remote = await GetStatusAsync(credential, checkpoint.ArchiveId, cancellationToken).ConfigureAwait(false);
        var published = remote.Parts
            .Where(part => string.Equals(part.Status, "published", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(part => part.Index);
        long transferred = checkpoint.Parts
            .Where(part => published.TryGetValue(part.Index, out var existing) &&
                           string.Equals(existing.CiphertextSha256, part.CiphertextSha256, StringComparison.OrdinalIgnoreCase))
            .Sum(part => part.CipherBytes);
        var completed = published.Count;

        foreach (var part in checkpoint.Parts.OrderBy(part => part.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (published.TryGetValue(part.Index, out var existing))
            {
                if (!string.Equals(existing.CiphertextSha256, part.CiphertextSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DumperArchiveException("DUMPER_PART_CONFLICT", $"服务端分片 #{part.Index} 与本地密文不一致。", HttpStatusCode.Conflict);
                }
                progress?.Report(new DumperArchiveTransferProgress(
                    "upload",
                    completed,
                    checkpoint.Parts.Count,
                    transferred,
                    checkpoint.TotalCipherBytes,
                    $"分片 #{part.Index} 已存在，已跳过。"));
                continue;
            }

            var path = ArchiveSpoolStore.GetPartPath(checkpoint.ArchiveId, part.FileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != part.CipherBytes)
            {
                throw new IOException($"本地加密分片 #{part.Index} 缺失或大小已变化。");
            }
            await UploadPartAsync(credential, checkpoint.ArchiveId, part, path, cancellationToken).ConfigureAwait(false);
            completed += 1;
            transferred += part.CipherBytes;
            progress?.Report(new DumperArchiveTransferProgress(
                "upload",
                completed,
                checkpoint.Parts.Count,
                transferred,
                checkpoint.TotalCipherBytes,
                $"已上传并发布分片 #{part.Index}。"));
        }

        await SealAsync(credential, checkpoint, cancellationToken).ConfigureAwait(false);
        progress?.Report(new DumperArchiveTransferProgress(
            "sealed",
            checkpoint.Parts.Count,
            checkpoint.Parts.Count,
            checkpoint.TotalCipherBytes,
            checkpoint.TotalCipherBytes,
            "归档已封存，完整性清单已写入 KOOK。"));
    }

    public async Task<DumperArchiveManifest> DownloadManifestAsync(
        string credential,
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/dumper/archives/{NormalizeArchiveId(archiveId)}/manifest", credential);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync<DumperArchiveManifest>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (manifest is null || manifest.Kind != "undefined-string-dumper-archive" || manifest.Version != 1)
        {
            throw new DumperArchiveException("DUMPER_MANIFEST_INVALID", "归档清单格式无效。", response.StatusCode);
        }
        return manifest;
    }

    public async Task<string> ResolveRestoreArchiveIdAsync(
        string credential,
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        var requestedArchiveId = NormalizeArchiveId(archiveId);
        try
        {
            var manifest = await DownloadManifestAsync(credential, requestedArchiveId, cancellationToken)
                .ConfigureAwait(false);
            return NormalizeArchiveId(manifest.ArchiveId);
        }
        catch (DumperArchiveException exception) when (
            string.Equals(exception.Code, "DUMPER_TOKEN_ARCHIVE_MISMATCH", StringComparison.Ordinal) &&
            Guid.TryParse(exception.ExpectedArchiveId, out var expectedArchiveId))
        {
            return expectedArchiveId.ToString("D");
        }
    }

    public async Task<byte[]> GetRestoreKeyAsync(
        string credential,
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/dumper/archives/{NormalizeArchiveId(archiveId)}/key", credential);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var envelope = await ReadEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        var dataKey = DecodeBase64Url(envelope.DataKey);
        if (dataKey.Length != 32)
        {
            throw new DumperArchiveException("DUMPER_DATA_KEY_INVALID", "归档恢复密钥长度无效。", response.StatusCode);
        }
        return dataKey;
    }

    public async Task<Stream> DownloadPartAsync(
        string credential,
        string archiveId,
        int partIndex,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/dumper/archives/{NormalizeArchiveId(archiveId)}/parts/{partIndex}", credential);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return new ResponseOwnedStream(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    private async Task UploadPartAsync(
        string credential,
        string archiveId,
        DumperArchivePartState part,
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        using var request = CreateRequest(HttpMethod.Put, $"api/dumper/archives/{NormalizeArchiveId(archiveId)}/parts/{part.Index}", credential);
        request.Headers.TryAddWithoutValidation("X-USD-Cipher-SHA256", part.CiphertextSha256);
        request.Headers.TryAddWithoutValidation("X-USD-Plain-Bytes", part.PlainBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-USD-File-Name", part.FileName);
        request.Content = new StreamContent(stream, 128 * 1024);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = stream.Length;
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task SealAsync(
        string credential,
        DumperArchiveCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"api/dumper/archives/{NormalizeArchiveId(checkpoint.ArchiveId)}/seal", credential);
        request.Content = JsonContent.Create(new
        {
            partCount = checkpoint.Parts.Count,
            totalPlainBytes = checkpoint.TotalPlainBytes,
            totalCipherBytes = checkpoint.TotalCipherBytes,
            plaintextSha256 = checkpoint.PlaintextSha256,
            ciphertextSha256 = checkpoint.CiphertextSha256,
        }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string credential)
    {
        if (!credential.StartsWith("usd_", StringComparison.Ordinal) || credential.Length != 47)
        {
            throw new ArgumentException("请输入 screenshare.cn 签发的有效 Dumper 凭证。", nameof(credential));
        }
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.UserAgent.ParseAdd("UndefinedStringDumper/0.4");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<DumperArchiveEnvelope> ReadEnvelopeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<DumperArchiveEnvelope>(JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? new DumperArchiveEnvelope();
        if (!response.IsSuccessStatusCode || !envelope.Ok)
        {
            throw new DumperArchiveException(
                string.IsNullOrWhiteSpace(envelope.Code) ? "DUMPER_HTTP_ERROR" : envelope.Code,
                string.IsNullOrWhiteSpace(envelope.Message) ? $"归档服务返回 HTTP {(int)response.StatusCode}。" : envelope.Message,
                response.StatusCode,
                envelope.ExpectedArchiveId);
        }
        return envelope;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<DumperArchiveEnvelope>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            throw new DumperArchiveException(
                string.IsNullOrWhiteSpace(envelope?.Code) ? "DUMPER_HTTP_ERROR" : envelope.Code,
                string.IsNullOrWhiteSpace(envelope?.Message) ? $"归档服务返回 HTTP {(int)response.StatusCode}。" : envelope.Message,
                response.StatusCode,
                envelope?.ExpectedArchiveId ?? string.Empty);
        }
        catch (JsonException)
        {
            throw new DumperArchiveException("DUMPER_HTTP_ERROR", $"归档服务返回 HTTP {(int)response.StatusCode}。", response.StatusCode);
        }
    }

    private static string NormalizeArchiveId(string archiveId) =>
        Guid.TryParse(archiveId, out var parsed)
            ? parsed.ToString("D")
            : throw new ArgumentException("归档编号必须是有效 UUID。", nameof(archiveId));

    internal static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("归档服务返回了无效的 Base64URL 数据。", exception);
        }
    }

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

public sealed class DumperArchiveException(
    string code,
    string message,
    HttpStatusCode statusCode,
    string expectedArchiveId = "") : Exception(message)
{
    public string Code { get; } = code;

    public HttpStatusCode StatusCode { get; } = statusCode;

    public string ExpectedArchiveId { get; } = expectedArchiveId;
}
