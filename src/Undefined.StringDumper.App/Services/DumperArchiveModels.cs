using System.Text.Json.Serialization;

namespace Undefined.StringDumper.App.Services;

public sealed record DumperArchivePartState(
    int Index,
    string FileName,
    long PlainBytes,
    long CipherBytes,
    string CiphertextSha256,
    string Status = "local");

public sealed record DumperArchiveCheckpoint(
    string ArchiveId,
    string ProcessName,
    int ProcessId,
    string DumperVersion,
    int PartSizeBytes,
    DateTimeOffset CreatedAt,
    bool ScanCompleted,
    long TotalPlainBytes,
    long TotalCipherBytes,
    string PlaintextSha256,
    string CiphertextSha256,
    IReadOnlyList<DumperArchivePartState> Parts);

public sealed class DumperArchiveEnvelope
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("expectedArchiveId")]
    public string ExpectedArchiveId { get; init; } = string.Empty;

    [JsonPropertyName("dataKey")]
    public string DataKey { get; init; } = string.Empty;

    [JsonPropertyName("archive")]
    public DumperArchiveRemoteState? Archive { get; init; }
}

public sealed class DumperArchiveRemoteState
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("parts")]
    public IReadOnlyList<DumperArchiveRemotePart> Parts { get; init; } = [];
}

public sealed class DumperArchiveRemotePart
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("ciphertextSha256")]
    public string CiphertextSha256 { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

public sealed class DumperArchiveManifest
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("archiveId")]
    public string ArchiveId { get; init; } = string.Empty;

    [JsonPropertyName("partSizeBytes")]
    public int PartSizeBytes { get; init; }

    [JsonPropertyName("partCount")]
    public int PartCount { get; init; }

    [JsonPropertyName("totalPlainBytes")]
    public long TotalPlainBytes { get; init; }

    [JsonPropertyName("totalCipherBytes")]
    public long TotalCipherBytes { get; init; }

    [JsonPropertyName("plaintextSha256")]
    public string PlaintextSha256 { get; init; } = string.Empty;

    [JsonPropertyName("ciphertextSha256")]
    public string CiphertextSha256 { get; init; } = string.Empty;

    [JsonPropertyName("parts")]
    public IReadOnlyList<DumperArchiveManifestPart> Parts { get; init; } = [];
}

public sealed class DumperArchiveManifestPart
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("plainBytes")]
    public long PlainBytes { get; init; }

    [JsonPropertyName("cipherBytes")]
    public long CipherBytes { get; init; }

    [JsonPropertyName("ciphertextSha256")]
    public string CiphertextSha256 { get; init; } = string.Empty;
}

public sealed record DumperArchiveTransferProgress(
    string Phase,
    int CompletedParts,
    int TotalParts,
    long BytesTransferred,
    long TotalBytes,
    string Detail)
{
    public double Fraction => TotalBytes <= 0
        ? TotalParts <= 0 ? 0 : Math.Clamp((double)CompletedParts / TotalParts, 0, 1)
        : Math.Clamp((double)BytesTransferred / TotalBytes, 0, 1);
}
