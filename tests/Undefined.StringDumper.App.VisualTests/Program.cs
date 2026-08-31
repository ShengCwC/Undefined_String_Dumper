using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Undefined.StringDumper.App;
using Undefined.StringDumper.App.Services;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Services;

namespace Undefined.StringDumper.App.VisualTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], "--export-process", StringComparison.Ordinal))
        {
            return ExportProcess(
                int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture),
                args[2]);
        }

        if (!VerifyTextExport())
        {
            return 1;
        }

        if (!VerifyAbortedExportPreservesTarget())
        {
            return 1;
        }

        var outputPath = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "ui-preview.png");
        var application = new App();
        application.InitializeComponent();

        var window = new MainWindow
        {
            Width = 1380,
            Height = 860,
            Left = -20_000,
            Top = -20_000,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();

        var bitmap = new RenderTargetBitmap(1380, 860, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var stream = File.Create(outputPath))
        {
            encoder.Save(stream);
        }

        window.Close();
        application.Shutdown();

        var file = new FileInfo(outputPath);
        if (!file.Exists || file.Length < 10_000)
        {
            Console.Error.WriteLine("Visual render output is missing or unexpectedly small.");
            return 1;
        }

        Console.WriteLine($"Rendered {file.FullName} ({file.Length:N0} bytes)");
        return 0;
    }

    private static int ExportProcess(int processId, string outputPath)
    {
        var process = new ProcessCatalog().GetJavaProcessesAsync().GetAwaiter().GetResult()
            .Single(candidate => candidate.ProcessId == processId);
        var options = new ScanOptions();
        var sink = TextFileResultSink.CreateAsync(outputPath, process, options, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        try
        {
            var summary = new WindowsMemoryStringScanner().ScanAsync(
                    processId,
                    options,
                    sink,
                    cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            sink.CompleteAsync(summary).GetAwaiter().GetResult();

            Console.WriteLine($"ProcessId={summary.ProcessId}");
            Console.WriteLine($"Regions={summary.RegionsScanned}");
            Console.WriteLine($"BytesRead={summary.BytesRead}");
            Console.WriteLine($"Strings={summary.StringsFound}");
            Console.WriteLine($"ReadFailures={summary.ReadFailures}");
            Console.WriteLine($"ElapsedSeconds={summary.Duration.TotalSeconds:F3}");
            Console.WriteLine($"FileBytes={new FileInfo(outputPath).Length}");
            return 0;
        }
        catch
        {
            sink.AbortAsync().GetAwaiter().GetResult();
            throw;
        }
        finally
        {
            sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool VerifyTextExport()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"uss-export-test-{Guid.NewGuid():N}.txt");
        var options = new ScanOptions();
        const string fixtureSecret = "DO-NOT-EXPORT-THIS-TOKEN";
        var process = new JavaProcessInfo(
            4242,
            "javaw",
            "Export fixture",
            @"C:\Games\Java\javaw.exe",
            1024,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new ProcessDetails
            {
                FileVersion = "22.0.2.0",
                SignatureStatus = "已验证",
                SignerName = "Oracle America, Inc.",
                CommandLine = $"javaw.exe --accessToken {fixtureSecret} --username player",
                CurrentDirectory = @"C:\Games\Minecraft",
                PebAddress = "0x1234abcd",
                ImageType = "64-bit",
                ParentProcess = "launcher.exe (31337)",
                MitigationPolicies = "DEP (permanent); ASLR (high entropy)",
                Protection = "None",
            });
        TextFileResultSink? sink = null;

        try
        {
            sink = TextFileResultSink.CreateAsync(outputPath, process, options, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            sink.WriteAsync(
                    new[]
                    {
                        new ExtractedString(0x1234, 7, "abc\tdef", EncodingKind.Ascii, MemoryRegionKind.Private),
                        new ExtractedString(0x5678, 8, "作弊测试", EncodingKind.Utf16LittleEndian, MemoryRegionKind.Mapped),
                    },
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            sink.CompleteAsync(new ScanSummary(
                    4242,
                    DateTimeOffset.UtcNow.AddSeconds(-1),
                    DateTimeOffset.UtcNow,
                    2,
                    4096,
                    2,
                    0))
                .GetAwaiter()
                .GetResult();

            var contents = File.ReadAllText(outputPath);
            var valid = contents.Contains("Undefined String Dumper 0.3.2", StringComparison.Ordinal) &&
                        contents.Contains("Process Hacker 2.39 compatible", StringComparison.Ordinal) &&
                        contents.Contains("Description: Export fixture", StringComparison.Ordinal) &&
                        contents.Contains("Signature: 已验证; Signer: Oracle America, Inc.", StringComparison.Ordinal) &&
                        contents.Contains("Version: 22.0.2.0", StringComparison.Ordinal) &&
                        contents.Contains(@"Image file name: C:\Games\Java\javaw.exe", StringComparison.Ordinal) &&
                        contents.Contains("Command line: javaw.exe --accessToken [REDACTED] --username player", StringComparison.Ordinal) &&
                        !contents.Contains(fixtureSecret, StringComparison.Ordinal) &&
                        contents.Contains(@"Current directory: C:\Games\Minecraft", StringComparison.Ordinal) &&
                        contents.Contains("Started:", StringComparison.Ordinal) &&
                        contents.Contains("PEB address: 0x1234abcd; Image type: 64-bit", StringComparison.Ordinal) &&
                        contents.Contains("Parent: launcher.exe (31337)", StringComparison.Ordinal) &&
                        contents.Contains("Mitigation policies: DEP (permanent); ASLR (high entropy)", StringComparison.Ordinal) &&
                        contents.Contains("Protection: None", StringComparison.Ordinal) &&
                        contents.Contains("0x1234 (7): abc\tdef", StringComparison.Ordinal) &&
                        contents.Contains("0x5678 (8): 作弊测试", StringComparison.Ordinal) &&
                        contents.Contains("# StringsFound: 2", StringComparison.Ordinal);
            if (!valid)
            {
                Console.Error.WriteLine("Text export content validation failed.");
                return false;
            }

            Console.WriteLine("PASS  Full UTF-8 text export");
            return true;
        }
        finally
        {
            if (sink is not null)
            {
                sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static bool VerifyAbortedExportPreservesTarget()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"uss-export-preserve-{Guid.NewGuid():N}.txt");
        const string originalContents = "ORIGINAL-CONTENTS";
        File.WriteAllText(outputPath, originalContents);
        TextFileResultSink? sink = null;

        try
        {
            sink = TextFileResultSink.CreateAsync(
                    outputPath,
                    new JavaProcessInfo(4242, "java", "Export fixture", null, 0, null),
                    new ScanOptions(),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            sink.WriteAsync(
                    new[]
                    {
                        new ExtractedString(0x9000, 4, "test", EncodingKind.Ascii, MemoryRegionKind.Private),
                    },
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            sink.AbortAsync().GetAwaiter().GetResult();

            if (!string.Equals(File.ReadAllText(outputPath), originalContents, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Aborted export changed the existing target file.");
                return false;
            }

            Console.WriteLine("PASS  Aborted export preserves existing target");
            return true;
        }
        finally
        {
            if (sink is not null)
            {
                sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
