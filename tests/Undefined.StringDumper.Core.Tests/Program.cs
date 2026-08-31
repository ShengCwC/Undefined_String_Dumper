using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Services;

if (args.Contains("--memory-fixture", StringComparer.Ordinal))
{
    return RunMemoryFixture();
}

if (args.Length == 2 && string.Equals(args[0], "--scan-process", StringComparison.Ordinal))
{
    return RunProcessProbe(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
}

if (args.Length == 2 && string.Equals(args[0], "--inspect-java", StringComparison.Ordinal))
{
    return RunJavaProcessInspection(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
}

if (args.Length == 3 && string.Equals(args[0], "--filter-process", StringComparison.Ordinal))
{
    return RunFilterProbe(
        int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture),
        args[2]);
}

var tests = new (string Name, Action Run)[]
{
    ("ASCII strings survive chunk boundaries", TestAsciiChunkBoundary),
    ("Process Hacker wide strings survive chunk boundaries", TestUnicodeChunkBoundary),
    ("Arbitrary UTF-16 data is not treated as a wide string", TestArbitraryUnicodeRejected),
    ("Misaligned wide data produces one correctly addressed result", TestUnicodeAlignment),
    ("Process Hacker printable whitespace is preserved", TestPrintableWhitespace),
    ("Minimum length is enforced", TestMinimumLength),
    ("Long runs keep one result and cap only display text", TestMaximumLength),
    ("Memory region profile matches Process Hacker settings", TestRegionProfile),
    ("Invalid profiles are rejected", TestInvalidProfile),
    ("Sensitive command-line values are redacted", TestCommandLineRedaction),
    ("Case-insensitive full-scan filtering reaches late matches", TestContainsFilteringSink),
    ("Current process metadata is readable", TestCurrentProcessMetadata),
    ("Windows scanner reads a live child process", TestLiveProcessScan),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL  {test.Name}: {exception.Message}");
        Console.WriteLine(failures[^1]);
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static void TestAsciiChunkBoundary()
{
    var output = new List<ExtractedString>();
    var extractor = new StreamingStringExtractor(DefaultOptions(), MemoryRegionKind.Private);
    extractor.Consume("xx\0CHE"u8, 0x1000, output);
    extractor.Consume("AT\0"u8, 0x1006, output);
    extractor.Complete(output);

    var result = RequireSingle(output, value => value.Value == "CHEAT");
    Equal(0x1003UL, result.Address, "ASCII start address");
    Equal(EncodingKind.Ascii, result.Encoding, "ASCII encoding");
}

static void TestUnicodeChunkBoundary()
{
    var output = new List<ExtractedString>();
    var extractor = new StreamingStringExtractor(DefaultOptions() with { DetectAscii = false }, MemoryRegionKind.Mapped);
    var bytes = Encoding.Unicode.GetBytes("C:\\Windows\0");
    extractor.Consume(bytes.AsSpan(0, 5), 0x2000, output);
    extractor.Consume(bytes.AsSpan(5), 0x2005, output);
    extractor.Complete(output);

    var result = RequireSingle(output, value => value.Value == "C:\\Windows");
    Equal(0x2000UL, result.Address, "UTF-16 start address");
    Equal(20, result.Length, "UTF-16 byte length");
    Equal(EncodingKind.Utf16LittleEndian, result.Encoding, "UTF-16 encoding");
    Equal(MemoryRegionKind.Mapped, result.RegionKind, "region kind");
}

static void TestArbitraryUnicodeRejected()
{
    var output = new List<ExtractedString>();
    var extractor = new StreamingStringExtractor(
        DefaultOptions() with { DetectAscii = false },
        MemoryRegionKind.Private);
    extractor.Consume(Encoding.Unicode.GetBytes("作弊测试\0"), 0x2800, output);
    extractor.Complete(output);

    Equal(0, output.Count, "arbitrary UTF-16 result count");
}

static void TestUnicodeAlignment()
{
    var output = new List<ExtractedString>();
    var extractor = new StreamingStringExtractor(DefaultOptions(), MemoryRegionKind.Private);
    var wide = Encoding.Unicode.GetBytes("C:\\Windows\0");
    var bytes = new byte[wide.Length + 2];
    bytes[0] = 0xFF;
    wide.CopyTo(bytes, 1);
    bytes[^1] = 0xFF;

    extractor.Consume(bytes, 0x2A00, output);
    extractor.Complete(output);

    var result = RequireSingle(output, value => value.Value == "C:\\Windows");
    Equal(1, output.Count, "misaligned total result count");
    Equal(0x2A01UL, result.Address, "misaligned UTF-16 start address");
    Equal(20, result.Length, "misaligned UTF-16 byte length");
}

static void TestPrintableWhitespace()
{
    var output = new List<ExtractedString>();
    var extractor = new StreamingStringExtractor(
        DefaultOptions() with { DetectUnicode = false },
        MemoryRegionKind.Private);
    extractor.Consume("A\tB\rC\nD\0"u8, 0x2C00, output);
    extractor.Complete(output);

    var result = RequireSingle(output, value => value.Value == "A\tB\rC\nD");
    Equal(7, result.Length, "printable whitespace length");
}

static void TestMinimumLength()
{
    var output = new List<ExtractedString>();
    var extractor = new StreamingStringExtractor(DefaultOptions() with { DetectUnicode = false }, MemoryRegionKind.Private);
    extractor.Consume("abc\0abcd\0"u8, 0x3000, output);
    extractor.Complete(output);

    var result = RequireSingle(output, value => value.Encoding == EncodingKind.Ascii);
    Equal("abcd", result.Value, "minimum accepted run");
}

static void TestMaximumLength()
{
    var output = new List<ExtractedString>();
    var options = DefaultOptions() with
    {
        DetectUnicode = false,
        MaximumStringLength = 5,
    };
    var extractor = new StreamingStringExtractor(options, MemoryRegionKind.Private);
    extractor.Consume("abcdefghij\0"u8, 0x4000, output);
    extractor.Complete(output);

    var result = RequireSingle(output, value => value.Encoding == EncodingKind.Ascii);
    Equal("abcde", result.Value, "capped display value");
    Equal(10, result.Length, "full run byte length");
    Equal(0x4000UL, result.Address, "long run address");
}

static void TestRegionProfile()
{
    var options = DefaultOptions();
    Equal(MemoryRegionKind.Private, MemoryRegionClassifier.Classify(0x20000), "private type");
    Equal(MemoryRegionKind.Mapped, MemoryRegionClassifier.Classify(0x40000), "mapped type");
    Equal(MemoryRegionKind.Image, MemoryRegionClassifier.Classify(0x1000000), "image type");
    True(MemoryRegionClassifier.IsIncluded(MemoryRegionKind.Private, options), "private included");
    True(MemoryRegionClassifier.IsIncluded(MemoryRegionKind.Mapped, options), "mapped included");
    True(!MemoryRegionClassifier.IsIncluded(MemoryRegionKind.Image, options), "image excluded");
    True(MemoryRegionClassifier.IsReadable(0x1000, 0x04), "committed read/write page readable");
    True(MemoryRegionClassifier.IsReadable(0x1000, 0x10), "committed execute page readable");
    True(!MemoryRegionClassifier.IsReadable(0x1000, 0x104), "guard page skipped");
    True(!MemoryRegionClassifier.IsReadable(0x2000, 0x04), "non-committed page skipped");
}

static void TestInvalidProfile()
{
    var options = DefaultOptions() with
    {
        DetectAscii = false,
        DetectUnicode = false,
    };

    try
    {
        options.Validate();
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new InvalidOperationException("Expected invalid scan profile to throw.");
}

static void TestCommandLineRedaction()
{
    const string source = "java.exe --accessToken TOP-SECRET --clientToken=CLIENT-SECRET " +
        "-Dauth.token=JVM-SECRET --url https://example.test/login?token=URL-SECRET&name=player --username player";
    var redacted = SensitiveCommandLineRedactor.Redact(source) ??
        throw new InvalidOperationException("Redactor returned no command line.");

    True(!redacted.Contains("TOP-SECRET", StringComparison.Ordinal), "space-delimited token removed");
    True(!redacted.Contains("CLIENT-SECRET", StringComparison.Ordinal), "equals-delimited token removed");
    True(!redacted.Contains("JVM-SECRET", StringComparison.Ordinal), "Java property secret removed");
    True(!redacted.Contains("URL-SECRET", StringComparison.Ordinal), "URL token removed");
    True(redacted.Contains("[REDACTED]", StringComparison.Ordinal), "redaction marker present");
    True(redacted.Contains("--username player", StringComparison.Ordinal), "non-sensitive argument preserved");
}

static void TestContainsFilteringSink()
{
    var downstream = new CappedSink(1);
    var sink = new ContainsFilteringResultSink("MeTeOr", downstream);
    var earlyNoise = Enumerable.Range(0, 20_000)
        .Select(index => new ExtractedString(
            (ulong)index,
            8,
            $"unrelated-{index}",
            EncodingKind.Ascii,
            MemoryRegionKind.Private))
        .ToArray();
    var lateMatch = new ExtractedString(
        0xF00D,
        28,
        "meteorDevelopment.METEORClient",
        EncodingKind.Ascii,
        MemoryRegionKind.Mapped);

    sink.WriteAsync(earlyNoise, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    sink.WriteAsync([lateMatch], CancellationToken.None).AsTask().GetAwaiter().GetResult();
    sink.FlushAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    Equal(1L, sink.MatchesFound, "full-scan match count");
    Equal(1, downstream.Results.Count, "downstream result count");
    Equal(lateMatch, downstream.Results[0], "late case-insensitive match");
}

static void TestCurrentProcessMetadata()
{
    var details = ProcessMetadataReader.Read(Environment.ProcessId);
    var expectedDirectory = NormalizeDirectory(Environment.CurrentDirectory);
    var actualDirectory = NormalizeDirectory(details.CurrentDirectory ?? string.Empty);

    Equal(expectedDirectory, actualDirectory, "current directory");
    True(details.CommandLine?.Contains("Undefined.StringDumper.Core.Tests", StringComparison.OrdinalIgnoreCase) == true,
        "current command line");
    True(details.PebAddress?.StartsWith("0x", StringComparison.Ordinal) == true, "PEB address");
    Equal(Environment.Is64BitProcess ? "64-bit" : "32-bit", details.ImageType, "image type");
    True(!string.IsNullOrWhiteSpace(details.ParentProcess), "parent process");
    True(!string.IsNullOrWhiteSpace(details.Protection), "protection level");
}

static void TestLiveProcessScan()
{
    var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable.");
    using var fixture = Process.Start(new ProcessStartInfo
    {
        FileName = executablePath,
        Arguments = "--memory-fixture",
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("Unable to start the memory fixture process.");

    try
    {
        var readyLine = fixture.StandardOutput.ReadLine();
        True(readyLine?.StartsWith("READY ", StringComparison.Ordinal) == true, "fixture ready signal");

        var sink = new MatchingSink("USS_LIVE_ASCII_7F3A", "USS_LIVE_WIDE_429");
        var scanner = new WindowsMemoryStringScanner();
        var summary = scanner.ScanAsync(
                fixture.Id,
                DefaultOptions() with
                {
                    IncludeMapped = false,
                    ReadBufferSize = 64 * 1024,
                },
                sink)
            .GetAwaiter()
            .GetResult();

        True(summary.BytesRead > 0, "live process bytes read");
        True(sink.Found.Contains("USS_LIVE_ASCII_7F3A"), "live ASCII marker found");
        True(sink.Found.Contains("USS_LIVE_WIDE_429"), "live Process Hacker wide marker found");
    }
    finally
    {
        fixture.StandardInput.WriteLine();
        if (!fixture.WaitForExit(3000))
        {
            fixture.Kill(entireProcessTree: true);
        }
    }
}

static int RunMemoryFixture()
{
    var ascii = Encoding.ASCII.GetBytes("\0USS_LIVE_ASCII_7F3A\0");
    var unicode = Encoding.Unicode.GetBytes("\0USS_LIVE_WIDE_429\0");
    var asciiPointer = Marshal.AllocHGlobal(ascii.Length);
    var unicodePointer = Marshal.AllocHGlobal(unicode.Length);

    try
    {
        Marshal.Copy(ascii, 0, asciiPointer, ascii.Length);
        Marshal.Copy(unicode, 0, unicodePointer, unicode.Length);
        Console.WriteLine($"READY {Environment.ProcessId}");
        Console.Out.Flush();
        _ = Console.ReadLine();
        GC.KeepAlive(asciiPointer);
        GC.KeepAlive(unicodePointer);
        return 0;
    }
    finally
    {
        Marshal.FreeHGlobal(asciiPointer);
        Marshal.FreeHGlobal(unicodePointer);
    }
}

static int RunProcessProbe(int processId)
{
    var sink = new CountingSink();
    var scanner = new WindowsMemoryStringScanner();
    var summary = scanner.ScanAsync(processId, DefaultOptions(), sink)
        .GetAwaiter()
        .GetResult();

    Console.WriteLine($"ProcessId={summary.ProcessId}");
    Console.WriteLine($"Regions={summary.RegionsScanned}");
    Console.WriteLine($"BytesRead={summary.BytesRead}");
    Console.WriteLine($"Strings={summary.StringsFound}");
    Console.WriteLine($"ASCII={sink.AsciiCount}");
    Console.WriteLine($"Wide={sink.WideCount}");
    Console.WriteLine($"AdjacentAddressDeltaOne={sink.AdjacentAddressDeltaOne}");
    Console.WriteLine($"ReadFailures={summary.ReadFailures}");
    Console.WriteLine($"ElapsedSeconds={summary.Duration.TotalSeconds:F3}");
    return 0;
}

static int RunJavaProcessInspection(int processId)
{
    var process = new ProcessCatalog().GetJavaProcessesAsync().GetAwaiter().GetResult()
        .Single(candidate => candidate.ProcessId == processId);

    Console.WriteLine($"ProcessId={process.ProcessId}");
    Console.WriteLine($"DescriptionAvailable={!string.IsNullOrWhiteSpace(process.DisplayName)}");
    Console.WriteLine($"VersionAvailable={process.FileVersionExportValue != "Unavailable"}");
    Console.WriteLine($"Signature={process.SignatureExportValue}");
    Console.WriteLine($"SignerAvailable={process.SignerExportValue != "Unavailable"}");
    Console.WriteLine($"PathAvailable={!string.IsNullOrWhiteSpace(process.ExecutablePath)}");
    Console.WriteLine($"CommandLineAvailable={process.CommandLineExportValue != "Unavailable"}");
    Console.WriteLine($"CommandLineRedacted={!process.CommandLineExportValue.Contains("accessToken", StringComparison.OrdinalIgnoreCase) || process.CommandLineExportValue.Contains("[REDACTED]", StringComparison.Ordinal)}");
    Console.WriteLine($"CurrentDirectoryAvailable={process.CurrentDirectoryExportValue != "Unavailable"}");
    Console.WriteLine($"StartedAvailable={process.StartedExportLabel != "Unavailable"}");
    Console.WriteLine($"PebAvailable={process.PebAddressExportValue != "Unavailable"}");
    Console.WriteLine($"ImageType={process.ImageTypeExportValue}");
    Console.WriteLine($"ParentAvailable={process.ParentProcessExportValue != "Unavailable"}");
    Console.WriteLine($"MitigationsAvailable={process.MitigationPoliciesExportValue != "Unavailable"}");
    Console.WriteLine($"Protection={process.ProtectionExportValue}");
    return 0;
}

static int RunFilterProbe(int processId, string filterText)
{
    var downstream = new CountingSink();
    var filterSink = new ContainsFilteringResultSink(filterText, downstream);
    var scanner = new WindowsMemoryStringScanner();
    var summary = scanner.ScanAsync(processId, DefaultOptions(), filterSink)
        .GetAwaiter()
        .GetResult();
    filterSink.FlushAsync().AsTask().GetAwaiter().GetResult();

    Console.WriteLine($"ProcessId={summary.ProcessId}");
    Console.WriteLine($"StringsScanned={summary.StringsFound}");
    Console.WriteLine($"Matches={filterSink.MatchesFound}");
    Console.WriteLine($"BytesRead={summary.BytesRead}");
    Console.WriteLine($"ReadFailures={summary.ReadFailures}");
    Console.WriteLine($"ElapsedSeconds={summary.Duration.TotalSeconds:F3}");
    return 0;
}

static string NormalizeDirectory(string path) => Path.GetFullPath(path)
    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

static ScanOptions DefaultOptions() => new()
{
    MinimumLength = 4,
    DetectAscii = true,
    DetectUnicode = true,
    IncludePrivate = true,
    IncludeMapped = true,
    IncludeImage = false,
};

static ExtractedString RequireSingle(IEnumerable<ExtractedString> values, Func<ExtractedString, bool> predicate)
{
    var matches = values.Where(predicate).ToArray();
    Equal(1, matches.Length, "matching result count");
    return matches[0];
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }
}

static void True(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{label}: expected true.");
    }
}

sealed class MatchingSink(params string[] targets) : IStringResultSink
{
    private readonly HashSet<string> _targets = targets.ToHashSet(StringComparer.Ordinal);

    public HashSet<string> Found { get; } = new(StringComparer.Ordinal);

    public ValueTask WriteAsync(IReadOnlyList<ExtractedString> batch, CancellationToken cancellationToken)
    {
        foreach (var result in batch)
        {
            if (_targets.Contains(result.Value))
            {
                Found.Add(result.Value);
            }
        }

        return ValueTask.CompletedTask;
    }
}

sealed class CountingSink : IStringResultSink
{
    private ulong? _previousAddress;

    public long AsciiCount { get; private set; }

    public long WideCount { get; private set; }

    public long AdjacentAddressDeltaOne { get; private set; }

    public ValueTask WriteAsync(IReadOnlyList<ExtractedString> batch, CancellationToken cancellationToken)
    {
        foreach (var result in batch)
        {
            if (result.Encoding == EncodingKind.Ascii)
            {
                AsciiCount++;
            }
            else
            {
                WideCount++;
            }

            if (_previousAddress.HasValue && result.Address == _previousAddress.Value + 1)
            {
                AdjacentAddressDeltaOne++;
            }

            _previousAddress = result.Address;
        }

        return ValueTask.CompletedTask;
    }
}

sealed class CappedSink(int limit) : IStringResultSink
{
    public List<ExtractedString> Results { get; } = [];

    public ValueTask WriteAsync(IReadOnlyList<ExtractedString> batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = limit - Results.Count;
        if (remaining > 0)
        {
            Results.AddRange(batch.Take(remaining));
        }

        return ValueTask.CompletedTask;
    }
}
