using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Native;

namespace Undefined.StringDumper.Core.Services;

public sealed class WindowsMemoryStringScanner : IMemoryStringScanner
{
    private const int ResultBatchSize = 256;

    public Task<ScanSummary> ScanAsync(
        int processId,
        ScanOptions options,
        IStringResultSink resultSink,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resultSink);
        options.Validate();

        return Task.Run(
            () => ScanCoreAsync(processId, options, resultSink, progress, cancellationToken),
            cancellationToken);
    }

    private static async Task<ScanSummary> ScanCoreAsync(
        int processId,
        ScanOptions options,
        IStringResultSink resultSink,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _ = NativeMethods.TryEnableDebugPrivilege();

        using var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryInformation | NativeMethods.ProcessVmRead,
            false,
            processId);

        if (process.IsInvalid)
        {
            throw NativeMethods.CreateLastError("无法读取目标进程。请确认程序已使用管理员权限运行，且游戏进程仍在运行。");
        }

        var regions = EnumerateReadableRegions(process, options, cancellationToken);
        var totalBytes = regions.Aggregate<ReadableRegion, long>(0, (current, region) =>
        {
            var size = region.Size > long.MaxValue ? long.MaxValue : (long)region.Size;
            return current > long.MaxValue - size ? long.MaxValue : current + size;
        });

        var buffer = new byte[options.ReadBufferSize];
        var batch = new List<ExtractedString>(ResultBatchSize * 2);
        long bytesReadTotal = 0;
        long stringsFound = 0;
        var readFailures = 0;
        var regionsCompleted = 0;
        var lastProgressAt = Environment.TickCount64;

        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extractor = new StreamingStringExtractor(options, region.Kind);
            nuint offset = 0;

            while (offset < region.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = region.Size - offset;
                var requested = (nuint)Math.Min((ulong)buffer.Length, (ulong)remaining);
                var address = checked(region.BaseAddress + (ulong)offset);

                var readSucceeded = NativeMethods.ReadProcessMemory(
                    process,
                    (nint)address,
                    buffer,
                    requested,
                    out var bytesRead);

                if (bytesRead == 0)
                {
                    readFailures++;
                    break;
                }

                var count = checked((int)bytesRead);
                extractor.Consume(buffer.AsSpan(0, count), address, batch);
                bytesReadTotal += count;
                offset += bytesRead;

                if (!readSucceeded)
                {
                    readFailures++;
                }

                if (batch.Count >= ResultBatchSize)
                {
                    stringsFound += batch.Count;
                    await resultSink.WriteAsync(batch.ToArray(), cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }

                var now = Environment.TickCount64;
                if (now - lastProgressAt >= 120)
                {
                    progress?.Report(new ScanProgress(
                        bytesReadTotal,
                        totalBytes,
                        regionsCompleted,
                        regions.Count,
                        stringsFound + batch.Count,
                        readFailures));
                    lastProgressAt = now;
                }

                if (!readSucceeded)
                {
                    break;
                }
            }

            extractor.Complete(batch);
            if (batch.Count >= ResultBatchSize)
            {
                stringsFound += batch.Count;
                await resultSink.WriteAsync(batch.ToArray(), cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }

            regionsCompleted++;
        }

        if (batch.Count > 0)
        {
            stringsFound += batch.Count;
            await resultSink.WriteAsync(batch.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ScanProgress(
            bytesReadTotal,
            totalBytes,
            regionsCompleted,
            regions.Count,
            stringsFound,
            readFailures));

        return new ScanSummary(
            processId,
            startedAt,
            DateTimeOffset.UtcNow,
            regionsCompleted,
            bytesReadTotal,
            stringsFound,
            readFailures);
    }

    private static List<ReadableRegion> EnumerateReadableRegions(
        SafeProcessHandle process,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        NativeMethods.GetNativeSystemInfo(out var systemInfo);
        var address = (ulong)systemInfo.MinimumApplicationAddress;
        var maximumAddress = (ulong)systemInfo.MaximumApplicationAddress;
        var informationSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();
        var results = new List<ReadableRegion>();

        while (address < maximumAddress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queried = NativeMethods.VirtualQueryEx(
                process,
                (nint)address,
                out var information,
                informationSize);

            if (queried == 0)
            {
                break;
            }

            var baseAddress = (ulong)information.BaseAddress;
            var kind = MemoryRegionClassifier.Classify(information.Type);
            if (information.RegionSize > 0 &&
                MemoryRegionClassifier.IsReadable(information.State, information.Protect) &&
                MemoryRegionClassifier.IsIncluded(kind, options))
            {
                results.Add(new ReadableRegion(baseAddress, information.RegionSize, kind));
            }

            var nextAddress = baseAddress + (ulong)information.RegionSize;
            if (nextAddress <= address)
            {
                break;
            }

            address = nextAddress;
        }

        return results;
    }

    private sealed record ReadableRegion(ulong BaseAddress, nuint Size, MemoryRegionKind Kind);
}
