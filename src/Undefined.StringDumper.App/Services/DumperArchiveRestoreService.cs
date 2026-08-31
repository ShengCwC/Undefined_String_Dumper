using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using ZstdSharp;

namespace Undefined.StringDumper.App.Services;

public sealed class DumperArchiveRestoreService(DumperArchiveClient client)
{
    private readonly DumperArchiveClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task RestoreAsync(
        string credential,
        string archiveId,
        string destinationPath,
        IProgress<DumperArchiveTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(archiveId, out var archiveGuid)) throw new ArgumentException("归档编号必须是有效 UUID。", nameof(archiveId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var manifest = await _client.DownloadManifestAsync(credential, archiveId, cancellationToken).ConfigureAwait(false);
        ValidateManifest(manifest, archiveGuid);
        var dataKey = await _client.GetRestoreKeyAsync(credential, archiveId, cancellationToken).ConfigureAwait(false);
        var finalPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException("恢复路径没有父目录。", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var partialPath = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.partial");
        long downloadedBytes = 0;
        long restoredBytes = 0;

        using var plaintextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var ciphertextChainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            foreach (var manifestPart in manifest.Parts.OrderBy(part => part.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var source = await _client.DownloadPartAsync(
                    credential,
                    archiveId,
                    manifestPart.Index,
                    cancellationToken).ConfigureAwait(false);
                var encrypted = await ReadExactlyBoundedAsync(source, manifestPart.CipherBytes, cancellationToken)
                    .ConfigureAwait(false);
                var actualHash = Convert.ToHexString(SHA256.HashData(encrypted)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(manifestPart.CiphertextSha256)))
                {
                    throw new InvalidDataException($"分片 #{manifestPart.Index} 的密文 SHA-256 校验失败。");
                }
                ciphertextChainHash.AppendData(Convert.FromHexString(actualHash));
                var plaintext = await Task.Run(
                    () => DecryptPart(encrypted, archiveGuid, manifestPart, dataKey, manifest.PartSizeBytes),
                    cancellationToken).ConfigureAwait(false);
                plaintextHash.AppendData(plaintext);
                await output.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
                restoredBytes += plaintext.LongLength;
                downloadedBytes += encrypted.LongLength;
                CryptographicOperations.ZeroMemory(plaintext);
                progress?.Report(new DumperArchiveTransferProgress(
                    "restore",
                    manifestPart.Index + 1,
                    manifest.PartCount,
                    downloadedBytes,
                    manifest.TotalCipherBytes,
                    $"已校验并恢复分片 #{manifestPart.Index}。"));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();

            var plaintextSha256 = Convert.ToHexString(plaintextHash.GetHashAndReset()).ToLowerInvariant();
            var ciphertextSha256 = Convert.ToHexString(ciphertextChainHash.GetHashAndReset()).ToLowerInvariant();
            if (restoredBytes != manifest.TotalPlainBytes || downloadedBytes != manifest.TotalCipherBytes)
            {
                throw new InvalidDataException("恢复后的字节总量与封存清单不一致。");
            }
            if (!string.Equals(plaintextSha256, manifest.PlaintextSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("恢复文件的明文 SHA-256 与封存清单不一致。");
            }
            if (!string.Equals(ciphertextSha256, manifest.CiphertextSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("分片链 SHA-256 与封存清单不一致。");
            }

            File.Move(partialPath, finalPath, overwrite: true);
            progress?.Report(new DumperArchiveTransferProgress(
                "restored",
                manifest.PartCount,
                manifest.PartCount,
                downloadedBytes,
                manifest.TotalCipherBytes,
                "归档已完整恢复并通过全部哈希校验。"));
        }
        catch
        {
            TryDeletePartial(partialPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    internal static byte[] DecryptPart(
        byte[] encrypted,
        Guid expectedArchiveId,
        DumperArchiveManifestPart manifestPart,
        byte[] dataKey,
        int partSizeBytes)
    {
        const int headerLength = 8 + 16 + sizeof(int) + sizeof(long) + sizeof(long) + 12 + 16;
        if (encrypted.Length < headerLength) throw new InvalidDataException("加密分片头部不完整。");
        var span = encrypted.AsSpan();
        if (!span[..8].SequenceEqual(EncryptedArchiveSink.PartMagic)) throw new InvalidDataException("加密分片格式标记无效。");
        var archiveId = new Guid(span.Slice(8, 16));
        var partIndex = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(24, sizeof(int)));
        var plainBytes = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(28, sizeof(long)));
        var compressedBytes = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(36, sizeof(long)));
        if (archiveId != expectedArchiveId || partIndex != manifestPart.Index)
        {
            throw new InvalidDataException("加密分片不属于当前归档或编号顺序错误。");
        }
        if (plainBytes != manifestPart.PlainBytes || plainBytes < 0 || plainBytes > partSizeBytes)
        {
            throw new InvalidDataException("加密分片声明的明文长度无效。");
        }
        if (compressedBytes < 1 || compressedBytes > partSizeBytes + 1024 * 1024 || headerLength + compressedBytes != encrypted.LongLength)
        {
            throw new InvalidDataException("加密分片声明的压缩长度无效。");
        }

        var nonce = span.Slice(44, 12);
        var tag = span.Slice(56, 16);
        var ciphertext = span.Slice(headerLength, checked((int)compressedBytes));
        var compressed = new byte[checked((int)compressedBytes)];
        using (var aes = new AesGcm(dataKey, 16))
        {
            aes.Decrypt(
                nonce,
                ciphertext,
                tag,
                compressed,
                EncryptedArchiveSink.BuildAdditionalData(expectedArchiveId.ToString("D"), partIndex, plainBytes));
        }
        try
        {
            using var decompressor = new Decompressor();
            var plaintext = decompressor.Unwrap(compressed, checked((int)plainBytes)).ToArray();
            if (plaintext.LongLength != plainBytes) throw new InvalidDataException("解压后的明文长度与分片清单不一致。");
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(compressed);
        }
    }

    private static async Task<byte[]> ReadExactlyBoundedAsync(
        Stream source,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (expectedBytes is <= 0 or > 17 * 1024 * 1024) throw new InvalidDataException("分片大小超出客户端安全范围。");
        var result = new byte[checked((int)expectedBytes)];
        var offset = 0;
        while (offset < result.Length)
        {
            var read = await source.ReadAsync(result.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("加密分片提前结束。");
            offset += read;
        }
        var extra = new byte[1];
        if (await source.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("加密分片超过清单声明的大小。");
        }
        return result;
    }

    private static void ValidateManifest(DumperArchiveManifest manifest, Guid archiveId)
    {
        if (!Guid.TryParse(manifest.ArchiveId, out var parsed) || parsed != archiveId)
        {
            throw new InvalidDataException("归档清单编号与请求不一致。");
        }
        if (manifest.PartSizeBytes is < 1024 * 1024 or > 16 * 1024 * 1024 || manifest.PartCount < 1)
        {
            throw new InvalidDataException("归档清单的分片参数无效。");
        }
        if (manifest.Parts.Count != manifest.PartCount || manifest.Parts.Where((part, index) => part.Index != index).Any())
        {
            throw new InvalidDataException("归档清单中的分片编号不连续。");
        }
        if (manifest.Parts.Sum(part => part.PlainBytes) != manifest.TotalPlainBytes ||
            manifest.Parts.Sum(part => part.CipherBytes) != manifest.TotalCipherBytes)
        {
            throw new InvalidDataException("归档清单中的字节总量不一致。");
        }
        if (!IsSha256(manifest.PlaintextSha256) || !IsSha256(manifest.CiphertextSha256) ||
            manifest.Parts.Any(part => !IsSha256(part.CiphertextSha256)))
        {
            throw new InvalidDataException("归档清单包含无效的 SHA-256 值。");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // An incomplete restore is never promoted to the requested destination.
        }
        catch (UnauthorizedAccessException)
        {
            // The requested destination remains untouched.
        }
    }
}
