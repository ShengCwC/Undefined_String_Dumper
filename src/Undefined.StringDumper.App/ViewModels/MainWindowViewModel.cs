using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using Undefined.StringDumper.App.Services;
using Undefined.StringDumper.Core.Models;
using Undefined.StringDumper.Core.Services;

namespace Undefined.StringDumper.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IProcessCatalog _processCatalog;
    private readonly IMemoryStringScanner _scanner;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _scanCancellation;
    private JavaProcessInfo? _selectedProcess;
    private bool _isRefreshing;
    private bool _isScanning;
    private bool _isExporting;
    private bool _consentConfirmed;
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private string _statusTitle = "等待选择进程";
    private string _statusDetail = "启动 Minecraft 后刷新进程列表。";
    private string _filterText = string.Empty;
    private double _progressFraction;
    private string _regionsMetric = "0 / 0";
    private string _dataMetric = "0 B";
    private string _stringsMetric = "0";
    private string _durationMetric = "--";
    private int _minimumLength = 4;
    private bool _detectAscii = true;
    private bool _detectUnicode = true;
    private bool _includePrivate = true;
    private bool _includeMapped = true;
    private bool _includeImage;
    private string? _lastExportPath;

    public MainWindowViewModel()
        : this(new ProcessCatalog(), new WindowsMemoryStringScanner(), Dispatcher.CurrentDispatcher)
    {
    }

    internal MainWindowViewModel(
        IProcessCatalog processCatalog,
        IMemoryStringScanner scanner,
        Dispatcher dispatcher)
    {
        _processCatalog = processCatalog;
        _scanner = scanner;
        _dispatcher = dispatcher;

        Processes = [];
        Results = [];
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.Filter = FilterResult;

        RefreshCommand = new AsyncRelayCommand(RefreshProcessesAsync, () => !IsScanning);
        StartScanCommand = new AsyncRelayCommand(StartScanAsync, CanStartScan);
        ExportCommand = new AsyncRelayCommand(ExportFullAsync, CanStartScan);
        CancelCommand = new RelayCommand(CancelScan, () => IsScanning);
        ClearResultsCommand = new RelayCommand(ClearResults, () => !IsScanning && Results.Count > 0);
    }

    public ObservableCollection<JavaProcessInfo> Processes { get; }

    public ObservableCollection<ExtractedString> Results { get; }

    public ICollectionView ResultsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand StartScanCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ClearResultsCommand { get; }

    public JavaProcessInfo? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetProperty(ref _selectedProcess, value))
            {
                OnPropertyChanged(nameof(HasSelectedProcess));
                OnPropertyChanged(nameof(SelectedProcessTitle));
                OnPropertyChanged(nameof(SelectedProcessSubtitle));
                RaiseCommandStates();
            }
        }
    }

    public bool HasSelectedProcess => SelectedProcess is not null;

    public string SelectedProcessTitle => SelectedProcess is null
        ? "尚未选择目标"
        : $"{SelectedProcess.ProcessLabel}  ·  {SelectedProcess.ProcessIdLabel}";

    public string SelectedProcessSubtitle => SelectedProcess is null
        ? "从左侧选择一个正在运行的 Java 游戏进程"
        : $"{SelectedProcess.MemoryLabel} 私有内存  ·  {SelectedProcess.DisplayName}";

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(RefreshButtonText));
            }
        }
    }

    public string RefreshButtonText => IsRefreshing ? "刷新中…" : "刷新进程";

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(PrimaryActionText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsIdle => !IsScanning;

    public string PrimaryActionText => IsScanning ? "正在读取内存…" : "开始一键扫描";

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
            {
                OnPropertyChanged(nameof(ExportActionText));
            }
        }
    }

    public string ExportActionText => IsExporting ? "正在导出…" : "完整导出";

    public bool ConsentConfirmed
    {
        get => _consentConfirmed;
        set
        {
            if (SetProperty(ref _consentConfirmed, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public int MinimumLength
    {
        get => _minimumLength;
        set
        {
            if (SetProperty(ref _minimumLength, Math.Clamp(value, 2, 1024)))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool DetectAscii
    {
        get => _detectAscii;
        set
        {
            if (SetProperty(ref _detectAscii, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool DetectUnicode
    {
        get => _detectUnicode;
        set
        {
            if (SetProperty(ref _detectUnicode, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IncludePrivate
    {
        get => _includePrivate;
        set
        {
            if (SetProperty(ref _includePrivate, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IncludeMapped
    {
        get => _includeMapped;
        set
        {
            if (SetProperty(ref _includeMapped, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IncludeImage
    {
        get => _includeImage;
        set
        {
            if (SetProperty(ref _includeImage, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                ResultsView.Refresh();
            }
        }
    }

    public double ProgressFraction
    {
        get => _progressFraction;
        private set
        {
            if (SetProperty(ref _progressFraction, value))
            {
                OnPropertyChanged(nameof(ProgressPercentText));
            }
        }
    }

    public string ProgressPercentText => $"{ProgressFraction:P0}";

    public string RegionsMetric
    {
        get => _regionsMetric;
        private set => SetProperty(ref _regionsMetric, value);
    }

    public string DataMetric
    {
        get => _dataMetric;
        private set => SetProperty(ref _dataMetric, value);
    }

    public string StringsMetric
    {
        get => _stringsMetric;
        private set => SetProperty(ref _stringsMetric, value);
    }

    public string DurationMetric
    {
        get => _durationMetric;
        private set => SetProperty(ref _durationMetric, value);
    }

    public string PreviewMetric => Results.Count.ToString("N0", CultureInfo.CurrentCulture);

    public string PreviewNotice
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_lastExportPath))
            {
                return $"完整结果已导出：{_lastExportPath}";
            }

            return Results.Count >= UiPreviewResultSink.DefaultPreviewLimit
                ? $"界面已达到 {UiPreviewResultSink.DefaultPreviewLimit:N0} 条预览上限；扫描统计仍保持完整。"
                : "本次结果仅保存在运行内存中，不会自动写入本地文件。";
        }
    }

    public async Task InitializeAsync() => await RefreshProcessesAsync();

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = null;
        Results.Clear();
    }

    private async Task RefreshProcessesAsync()
    {
        IsRefreshing = true;
        HasError = false;
        try
        {
            var previousPid = SelectedProcess?.ProcessId;
            var found = await _processCatalog.GetJavaProcessesAsync();

            Processes.Clear();
            foreach (var process in found)
            {
                Processes.Add(process);
            }

            SelectedProcess = previousPid.HasValue
                ? Processes.FirstOrDefault(process => process.ProcessId == previousPid.Value)
                : Processes.FirstOrDefault();

            if (Processes.Count == 0)
            {
                StatusTitle = "未发现 Java 游戏进程";
                StatusDetail = "请先启动 Minecraft 客户端，再点击刷新进程。";
            }
            else
            {
                StatusTitle = $"已发现 {Processes.Count} 个候选进程";
                StatusDetail = SelectedProcess is null
                    ? "请选择需要取证的游戏进程。"
                    : "已自动选择占用内存最大的候选进程，请核对 PID。";
            }
        }
        catch (Exception exception)
        {
            ShowError("刷新进程失败", exception.Message);
        }
        finally
        {
            IsRefreshing = false;
            RaiseCommandStates();
        }
    }

    private bool CanStartScan()
    {
        var hasEncoding = DetectAscii || DetectUnicode;
        var hasRegion = IncludePrivate || IncludeMapped || IncludeImage;
        return !IsScanning && SelectedProcess is not null && ConsentConfirmed && hasEncoding && hasRegion;
    }

    private async Task StartScanAsync()
    {
        await RunScanAsync(exportPath: null);
    }

    private async Task ExportFullAsync()
    {
        if (SelectedProcess is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".txt",
            FileName = $"USS_{SelectedProcess.ProcessName}_{SelectedProcess.ProcessId}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = "UTF-8 文本证据 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            OverwritePrompt = true,
            Title = "选择完整扫描结果的保存位置",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await RunScanAsync(dialog.FileName);
    }

    private async Task RunScanAsync(string? exportPath)
    {
        if (SelectedProcess is null)
        {
            return;
        }

        var process = SelectedProcess;
        var isExport = !string.IsNullOrWhiteSpace(exportPath);
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        _lastExportPath = null;

        Results.Clear();
        ResultsView.Refresh();
        OnPropertyChanged(nameof(PreviewMetric));
        OnPropertyChanged(nameof(PreviewNotice));
        HasError = false;
        ProgressFraction = 0;
        RegionsMetric = "0 / 0";
        DataMetric = "0 B";
        StringsMetric = "0";
        DurationMetric = "--";
        StatusTitle = "正在建立内存视图";
        StatusDetail = isExport
            ? "正在准备安全的本地导出文件…"
            : "枚举可读的 Private 与 Mapped 区域…";
        IsExporting = isExport;
        IsScanning = true;

        var started = DateTimeOffset.UtcNow;
        var previewSink = new UiPreviewResultSink(_dispatcher, Results);
        TextFileResultSink? exportSink = null;
        var progress = new Progress<ScanProgress>(update =>
        {
            ProgressFraction = update.Fraction;
            RegionsMetric = $"{update.RegionsCompleted:N0} / {update.TotalRegions:N0}";
            DataMetric = FormatBytes(update.BytesRead);
            StringsMetric = update.StringsFound.ToString("N0", CultureInfo.CurrentCulture);
            DurationMetric = FormatDuration(DateTimeOffset.UtcNow - started);
            StatusTitle = "正在提取内存字符串";
            StatusDetail = update.ReadFailures == 0
                ? isExport
                    ? "完整结果正在流式写入所选文件；界面仍只保留有限预览。"
                    : "数据仅进入内存预览，未写入本地磁盘。"
                : $"有 {update.ReadFailures:N0} 个动态内存区域在读取时已变化并被跳过。";
            OnPropertyChanged(nameof(PreviewMetric));
            OnPropertyChanged(nameof(PreviewNotice));
            ClearResultsCommand.RaiseCanExecuteChanged();
        });

        try
        {
            var options = new ScanOptions
            {
                MinimumLength = MinimumLength,
                DetectAscii = DetectAscii,
                DetectUnicode = DetectUnicode,
                IncludePrivate = IncludePrivate,
                IncludeMapped = IncludeMapped,
                IncludeImage = IncludeImage,
            };

            IStringResultSink resultSink = previewSink;
            if (isExport)
            {
                exportSink = await TextFileResultSink.CreateAsync(
                    exportPath!,
                    process,
                    options,
                    _scanCancellation.Token);
                resultSink = new CompositeResultSink(previewSink, exportSink);
            }

            var summary = await _scanner.ScanAsync(
                process.ProcessId,
                options,
                resultSink,
                progress,
                _scanCancellation.Token);

            if (exportSink is not null)
            {
                await exportSink.CompleteAsync(summary);
                _lastExportPath = exportSink.FinalPath;
                OnPropertyChanged(nameof(PreviewNotice));
            }

            ProgressFraction = 1;
            RegionsMetric = $"{summary.RegionsScanned:N0} / {summary.RegionsScanned:N0}";
            DataMetric = FormatBytes(summary.BytesRead);
            StringsMetric = summary.StringsFound.ToString("N0", CultureInfo.CurrentCulture);
            DurationMetric = FormatDuration(summary.Duration);
            StatusTitle = isExport ? "扫描与导出完成" : "扫描完成";
            if (isExport && !string.IsNullOrWhiteSpace(_lastExportPath))
            {
                var fileSize = new FileInfo(_lastExportPath).Length;
                StatusDetail = $"已保存全部 {summary.StringsFound:N0} 条结果，文件大小 {FormatBytes(fileSize)}。";
            }
            else
            {
                StatusDetail = previewSink.IsTruncated
                    ? "完整统计已完成；界面仅展示前 20,000 条，等待后续接入远端上传通道。"
                    : "全部结果已进入本次会话的内存预览。";
            }
        }
        catch (OperationCanceledException)
        {
            StatusTitle = "扫描已取消";
            StatusDetail = isExport
                ? "已停止读取；未完成的临时导出将被删除，原有目标文件不会被覆盖。"
                : "已停止读取；当前预览仍只保存在运行内存中。";
        }
        catch (Exception exception)
        {
            ShowError(isExport ? "无法完成扫描或导出" : "无法完成扫描", exception.Message);
        }
        finally
        {
            if (exportSink is not null)
            {
                await exportSink.DisposeAsync();
            }

            IsExporting = false;
            IsScanning = false;
            OnPropertyChanged(nameof(PreviewMetric));
            OnPropertyChanged(nameof(PreviewNotice));
            ClearResultsCommand.RaiseCanExecuteChanged();
        }
    }

    private void CancelScan() => _scanCancellation?.Cancel();

    private void ClearResults()
    {
        Results.Clear();
        FilterText = string.Empty;
        StatusTitle = "结果已从内存清除";
        StatusDetail = string.IsNullOrWhiteSpace(_lastExportPath)
            ? "没有创建或保留本地结果文件。"
            : "内存预览已清除；此前主动导出的本地文件不受影响。";
        StringsMetric = "0";
        OnPropertyChanged(nameof(PreviewMetric));
        OnPropertyChanged(nameof(PreviewNotice));
        ClearResultsCommand.RaiseCanExecuteChanged();
    }

    private bool FilterResult(object item)
    {
        if (item is not ExtractedString result || string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        return result.Value.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               result.AddressText.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowError(string title, string message)
    {
        HasError = true;
        ErrorMessage = message;
        StatusTitle = title;
        StatusDetail = "请核对目标进程、管理员权限与扫描选项后重试。";
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        StartScanCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ClearResultsCommand.RaiseCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}"
        : $"{duration.TotalSeconds:0.0}s";
}
