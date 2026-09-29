using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using VideoPresenter.App.Boot;
using VideoPresenter.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace VideoPresenter.App.ViewModels;

/// <summary>一条拍摄记录。</summary>
public sealed class SnapshotItem : ObservableObject
{
    public required BitmapImage Thumbnail { get; init; }
    public required string FilePath { get; init; }
    public required DateTime Timestamp { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public required long FileSize { get; init; }

    public string FileName => Path.GetFileName(FilePath);
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public string SizeText => FileSize < 1024 * 1024
        ? $"{FileSize / 1024.0:F0} KB"
        : $"{FileSize / 1024.0 / 1024.0:F1} MB";

    public string DimensionText => $"{PixelWidth}×{PixelHeight}";
}

/// <summary>
/// 主界面视图模型（WinUI 3）。
///
/// <para>
/// <b>配色策略</b>：本类不持有任何颜色，也不提供"切换主题"开关。
/// 全部颜色由 XAML 的 <c>ThemeResource</c> 引用系统主题资源，
/// WinUI 运行时负责跟随系统深浅色与系统强调色自动刷新。
/// 这里只维护一个状态栏文本，告知用户"配色正在跟随系统"。
/// </para>
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICameraService _camera;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private CameraDevice? _selectedDevice;
    private bool _isPreviewing;
    private bool _isRefreshing;
    private bool _isRightPanelOpen = true;
    private bool _isComparing;
    private bool _isAnnotating;
    private bool _isTopmost;
    private bool _isFullScreen;
    private string _statusText = "就绪";
    private string _deviceText = "未连接设备";
    private string _fpsText = "—";
    private string _resolutionText = "—";
    private string _themeModeText = "跟随系统";
    private SnapshotItem? _selectedSnapshot;

    public MainViewModel(ICameraService camera)
    {
        _camera = camera;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _camera.StatusChanged += OnCameraStatus;

        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => !_isRefreshing);
        TogglePreviewCommand = new RelayCommand(TogglePreview, () => SelectedDevice is not null);
        CaptureCommand = new RelayCommand(() => _ = CaptureAsync(), () => _isPreviewing);
        BurstCaptureCommand = new RelayCommand(() => _ = BurstCaptureAsync(), () => _isPreviewing);
        DeleteSnapshotCommand = new RelayCommand(DeleteSnapshot, () => _selectedSnapshot is not null);
        ClearSnapshotsCommand = new RelayCommand(ClearSnapshots, () => Snapshots.Count > 0);
        OpenFolderCommand = new RelayCommand(OpenSnapshotFolder);
        ToggleRightPanelCommand = new RelayCommand(() => IsRightPanelOpen = !IsRightPanelOpen);
        ToggleFullScreenCommand = new RelayCommand(() => FullScreenRequested?.Invoke(this, !_isFullScreen));
        ToggleTopmostCommand = new RelayCommand(() => IsTopmost = !IsTopmost);
    }

    /// <summary>窗口级动作（全屏）交由 View 处理。</summary>
    public event EventHandler<bool>? FullScreenRequested;

    // ───────────────────────── 集合与状态 ─────────────────────────

    public ObservableCollection<CameraDevice> Devices { get; } = new();
    public ObservableCollection<SnapshotItem> Snapshots { get; } = new();

    public CameraDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            if (value is not null) StartPreview(value);
        }
    }

    public SnapshotItem? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (SetProperty(ref _selectedSnapshot, value))
                (DeleteSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsPreviewing
    {
        get => _isPreviewing;
        private set
        {
            if (!SetProperty(ref _isPreviewing, value)) return;

            OnPropertyChanged(nameof(PreviewHintVisibility));
            OnPropertyChanged(nameof(PreviewVisibility));
            (CaptureCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (BurstCaptureCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>"未连接"引导层可见性。</summary>
    public Visibility PreviewHintVisibility => _isPreviewing ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>实时画面层可见性。</summary>
    public Visibility PreviewVisibility => _isPreviewing ? Visibility.Visible : Visibility.Collapsed;

    public bool IsRightPanelOpen
    {
        get => _isRightPanelOpen;
        set
        {
            if (SetProperty(ref _isRightPanelOpen, value))
                OnPropertyChanged(nameof(RightPanelWidth));
        }
    }

    /// <summary>素材栏宽度（折叠时为 0）。</summary>
    public GridLength RightPanelWidth => _isRightPanelOpen ? new GridLength(252) : new GridLength(0);

    public bool IsComparing
    {
        get => _isComparing;
        set => SetProperty(ref _isComparing, value);
    }

    public bool IsAnnotating
    {
        get => _isAnnotating;
        set => SetProperty(ref _isAnnotating, value);
    }

    public bool IsTopmost
    {
        get => _isTopmost;
        set => SetProperty(ref _isTopmost, value);
    }

    public bool IsFullScreen
    {
        get => _isFullScreen;
        internal set => SetProperty(ref _isFullScreen, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DeviceText
    {
        get => _deviceText;
        private set => SetProperty(ref _deviceText, value);
    }

    public string FpsText
    {
        get => _fpsText;
        private set => SetProperty(ref _fpsText, value);
    }

    public string ResolutionText
    {
        get => _resolutionText;
        private set => SetProperty(ref _resolutionText, value);
    }

    /// <summary>状态栏中的"跟随系统"指示（浅色 / 深色）。</summary>
    public string ThemeModeText
    {
        get => _themeModeText;
        set => SetProperty(ref _themeModeText, value);
    }

    public int SnapshotCount => Snapshots.Count;

    /// <summary>
    /// 数量的字符串形式。
    /// <para>
    /// 存在的理由：x:Bind 是<b>编译期强类型</b>绑定，不会像传统 Binding 那样自动
    /// 调用 ToString()。把 int 直接绑到 TextBlock.Text 会编译失败，
    /// 因此这里显式提供 string。
    /// </para>
    /// </summary>
    public string SnapshotCountText => Snapshots.Count.ToString();

    /// <summary>启动耗时（体现"启动器 1 秒内出界面"）。</summary>
    public string BootInfoText => BootSignal.Instance.HasSession
        ? $"启动耗时 {BootSignal.Instance.ElapsedMs} ms"
        : "开发模式";

    // ───────────────────────── 命令 ─────────────────────────

    public ICommand RefreshDevicesCommand { get; }
    public ICommand TogglePreviewCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand BurstCaptureCommand { get; }
    public ICommand DeleteSnapshotCommand { get; }
    public ICommand ClearSnapshotsCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ToggleRightPanelCommand { get; }
    public ICommand ToggleFullScreenCommand { get; }
    public ICommand ToggleTopmostCommand { get; }

    // ───────────────────────── 设备与预览 ─────────────────────────

    /// <summary>
    /// 首帧渲染完成后再执行的重活：枚举设备、自动连接。
    /// 让"能看见界面"与"能看见画面"解耦 —— 启动器能更早收到 AppReady 并自杀。
    /// </summary>
    public void Initialize()
    {
        RefreshDevices();
        OnPropertyChanged(nameof(BootInfoText));
    }

    /// <summary>由 View 在每帧到达时调用（已在 UI 线程）。</summary>
    public void UpdateFrameInfo(int width, int height)
    {
        ResolutionText = $"{width}×{height}";
        FpsText = _camera.Fps > 0 ? $"{_camera.Fps:F0} fps" : "—";
    }

    private void RefreshDevices()
    {
        _isRefreshing = true;
        (RefreshDevicesCommand as RelayCommand)?.RaiseCanExecuteChanged();

        try
        {
            _camera.RefreshDevices();
            Devices.Clear();
            foreach (var d in _camera.Devices) Devices.Add(d);

            StatusText = Devices.Count > 0
                ? $"已发现 {Devices.Count} 个视频设备"
                : "未发现视频设备，请检查 USB 连接与驱动";

            // 只有一个设备时自动连接 —— 双击图标即可见画面
            if (Devices.Count == 1 && SelectedDevice is null)
                SelectedDevice = Devices[0];
        }
        finally
        {
            _isRefreshing = false;
            (RefreshDevicesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void StartPreview(CameraDevice device)
    {
        if (_camera.Start(device))
        {
            IsPreviewing = true;
            DeviceText = device.Name;
            StatusText = $"正在预览：{device.Name}";
        }
        else
        {
            IsPreviewing = false;
            DeviceText = "连接失败";
        }
    }

    private void TogglePreview()
    {
        if (_isPreviewing)
        {
            _camera.Stop();
            IsPreviewing = false;
            StatusText = "预览已暂停";
            FpsText = "—";
        }
        else if (SelectedDevice is not null)
        {
            StartPreview(SelectedDevice);
        }
    }

    private void OnCameraStatus(object? sender, string message)
        => _dispatcher.TryEnqueue(() => StatusText = message);

    // ───────────────────────── 拍摄 ─────────────────────────

    private static string SnapshotFolder
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "视频展台");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private async Task CaptureAsync() => await SaveCurrentFrameAsync();

    private async Task BurstCaptureAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            await SaveCurrentFrameAsync();
            await Task.Delay(130);
        }
        StatusText = "连拍完成（3 张）";
    }

    private async Task SaveCurrentFrameAsync()
    {
        // GrabStill 返回副本，避免采集线程改写导致撕裂
        using var still = _camera.GrabStill();
        if (still is null)
        {
            StatusText = "当前没有可用画面";
            return;
        }

        try
        {
            var now = DateTime.Now;
            string fileName = $"展台_{now:yyyyMMdd_HHmmss_fff}.png";
            string path = Path.Combine(SnapshotFolder, fileName);

            // 用 WinRT PNG 编码器：无损保存，板书 / 试卷文本的最佳选择
            using (var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(still);
                await encoder.FlushAsync();
            }

            Snapshots.Insert(0, new SnapshotItem
            {
                Thumbnail = new BitmapImage(new Uri(path)),
                FilePath = path,
                Timestamp = now,
                PixelWidth = still.PixelWidth,
                PixelHeight = still.PixelHeight,
                FileSize = new FileInfo(path).Length,
            });

            OnPropertyChanged(nameof(SnapshotCount));
            OnPropertyChanged(nameof(SnapshotCountText));
            (ClearSnapshotsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            StatusText = $"已拍摄：{fileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
            Debug.WriteLine($"[VP] 保存照片失败：{ex}");
        }
    }

    private void DeleteSnapshot()
    {
        if (_selectedSnapshot is null) return;

        try
        {
            if (File.Exists(_selectedSnapshot.FilePath))
                File.Delete(_selectedSnapshot.FilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 删除文件失败：{ex.Message}");
        }

        Snapshots.Remove(_selectedSnapshot);
        SelectedSnapshot = null;
        OnPropertyChanged(nameof(SnapshotCount));
        OnPropertyChanged(nameof(SnapshotCountText));
        (ClearSnapshotsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusText = "已删除素材";
    }

    private void ClearSnapshots()
    {
        foreach (var s in Snapshots.ToList())
        {
            try
            {
                if (File.Exists(s.FilePath)) File.Delete(s.FilePath);
            }
            catch
            {
                // 忽略个别文件被占用
            }
        }
        Snapshots.Clear();
        OnPropertyChanged(nameof(SnapshotCount));
        OnPropertyChanged(nameof(SnapshotCountText));
        (ClearSnapshotsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusText = "已清空素材";
    }

    private void OpenSnapshotFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SnapshotFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 打开文件夹失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _camera.StatusChanged -= OnCameraStatus;
        _camera.Dispose();
    }
}