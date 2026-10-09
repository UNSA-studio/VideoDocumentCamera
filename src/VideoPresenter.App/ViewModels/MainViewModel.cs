// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using VideoPresenter.App.Boot;
using VideoPresenter.App.Services;
using Windows.ApplicationModel.DataTransfer;
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
/// 一条功能状态（用于"设置"面板里集中展示）。
/// <para>
/// 存在的意义：界面上有些按钮是占位（尚未实现），有些是可用。
/// 用户应该有一个地方能【一眼看清哪些能用、哪些还没做】，
/// 而不是靠一个个去点、去猜。
/// </para>
/// </summary>
public sealed class FeatureStatus
{
    public required string Icon { get; init; }     // ✅ 可用 / 🚧 未实现
    public required string Name { get; init; }
    public required string Detail { get; init; }
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

    /// <summary>应用设置（JSON 持久化，改动立即生效）。</summary>
    public AppSettings Settings { get; }

    public MainViewModel(ICameraService camera)
    {
        _camera = camera;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        Settings = AppSettings.Load();

        // 素材栏的初始展开状态由设置决定
        IsRightPanelOpen = Settings.RightPanelOpenByDefault;

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

    /// <summary>供 View（如旋转、批注等纯 UI 操作）回写状态栏文本。</summary>
    public void SetStatus(string text) => StatusText = text;

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

    /// <summary>设备枚举的诊断信息（显示在"未连接"引导层里，便于用户直接反馈）。</summary>
    public string DeviceDiagnosticsText => _camera.DiagnosticsText;

    /// <summary>设置面板里那行"上次设备"说明文字。</summary>
    public string LastDeviceText
    {
        get
        {
            string name = Settings.LastDeviceName;
            return string.IsNullOrWhiteSpace(name)
                ? "尚未记录（成功连接一次设备后会自动记住）"
                : $"上次设备：{name}";
        }
    }

    /// <summary>启动耗时（体现"启动器 1 秒内出界面"；可在设置里关闭显示）。</summary>
    public string BootInfoText
    {
        get
        {
            if (!Settings.ShowBootTime) return string.Empty;

            return BootSignal.Instance.HasSession
                ? $"启动耗时 {BootSignal.Instance.ElapsedMs} ms"
                : "开发模式";
        }
    }

    /// <summary>
    /// 诊断日志的完整路径（显示在"未连接设备"的引导层里）。
    /// <para>
    /// 相机枚举失败的原因（系统缺 Media Foundation、设备被占用、驱动异常）
    /// 只靠界面没法判断，日志文件才是排查依据。
    /// </para>
    /// </summary>
    public string LogFilePathText => "诊断日志：" + Services.WinRtCameraService.LogFilePath;

    /// <summary>版本信息（设置面板里展示）。</summary>
    public string VersionText => "视频展台 v1.0.0  ·  UNSA Studio";

    /// <summary>
    /// 拍照帧的合成钩子。
    /// <para>
    /// 当画面上有批注时，View 在这里把「实时画面 + 批注」渲染成一张图，
    /// 交给 ViewModel 保存 —— 这样批注才会被烧录进截图。
    /// 返回 null 表示没有批注（那就用原始帧，保持全分辨率）。
    /// </para>
    /// </summary>
    public Func<Task<SoftwareBitmap?>>? CaptureComposer { get; set; }

    /// <summary>
    /// 功能状态清单（设置面板里展示）。
    /// <para>
    /// 诚实列出"已实现 / 尚未实现"，避免用户对着占位按钮反复尝试。
    /// 每完成一项，把它从 🚧 改成 ✅ 即可。
    /// </para>
    /// </summary>
    public ObservableCollection<FeatureStatus> Features { get; } = new()
    {
        new() { Icon = "✅", Name = "拍照",       Detail = "全局快捷键 Ctrl+Alt+P，PNG 无损保存到「图片\\视频展台」" },
        new() { Icon = "✅", Name = "连拍",       Detail = "连续抓拍 3 张（间隔约 130 ms）" },
        new() { Icon = "✅", Name = "素材管理",   Detail = "右侧栏：缩略图、删除、清空、打开文件夹" },
        new() { Icon = "✅", Name = "全屏",       Detail = "Esc / F11 切换；全局快捷键 Ctrl+Alt+F" },
        new() { Icon = "✅", Name = "窗口置顶",   Detail = "命令栏「置顶」按钮" },
        new() { Icon = "✅", Name = "跟随系统配色", Detail = "深浅色与系统强调色自动同步，程序不提供主题开关" },
        new() { Icon = "✅", Name = "设备热插拔", Detail = "命令栏「刷新」按钮重新扫描设备" },

        new() { Icon = "✅", Name = "批注",       Detail = "鼠标 / 触摸 / 手写笔均可；5 色 + 粗细可调；橡皮按笔删除；撤销 / 清空；✔ 截图会包含批注" },
        new() { Icon = "✅", Name = "旋转",       Detail = "顺时针 90° 循环" },
        new() { Icon = "✅", Name = "对比",       Detail = "左侧为选中素材、右侧为实时画面，并排查看" },
        new() { Icon = "✅", Name = "OCR",        Detail = "识别画面文字，结果可复制（需系统装有中文 OCR 语言包）" },
        new() { Icon = "✅", Name = "录像",       Detail = "录制为 MP4（H.264，系统自带编码器），保存到「图片\\视频展台\\录像」" },
    };

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

        // 显示两个帧率：
        //   前一个 = 采集帧率（设备实际给帧速度，反映设备能力）
        //   后一个 = 显示帧率（渲染上屏速度，远程桌面下会明显偏低）
        // 两者差距大 → 瓶颈在界面/网络，而不是设备。
        if (_camera.Fps <= 0)
        {
            FpsText = "—";
        }
        else if (_camera is Services.WinRtCameraService winrt)
        {
            FpsText = $"{_camera.Fps:F0}/{winrt.DisplayFps:F0} fps";
        }
        else
        {
            FpsText = $"{_camera.Fps:F0} fps";
        }
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

            // 只有一个设备时自动连接（可在设置里关闭）
            if (Settings.AutoConnectSingleDevice && Devices.Count == 1 && SelectedDevice is null)
                SelectedDevice = Devices[0];
        }
        finally
        {
            _isRefreshing = false;
            (RefreshDevicesCommand as RelayCommand)?.RaiseCanExecuteChanged();

            // 诊断文本是在枚举过程中生成的，刷新后要通知界面重新取值
            OnPropertyChanged(nameof(DeviceDiagnosticsText));
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
            // 记住这次用的设备，下次可直接自动连接（可在设置里关闭）
            if (Settings.RememberLastDevice) Settings.LastDeviceName = SelectedDevice.Name;

            StartPreview(SelectedDevice);
        }
    }

    private void OnCameraStatus(object? sender, string message)
        => _dispatcher.TryEnqueue(() => StatusText = message);

    // ───────────────────────── 拍摄 ─────────────────────────

    /// <summary>素材保存目录（由设置决定，留空则用默认的「图片\视频展台」）。</summary>
    private string SnapshotFolder
    {
        get
        {
            string dir = Settings.PhotoFolder;

            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "视频展台");
            }

            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private async Task CaptureAsync() => await SaveCurrentFrameAsync();

    /// <summary>
    /// 取得要保存的一帧。
    /// <para>
    /// 优先让 View 合成（画面上有批注时），失败或没有批注则退回相机原始帧。
    /// </para>
    /// </summary>
    private async Task<SoftwareBitmap?> AcquireFrameAsync()
    {
        if (CaptureComposer is not null)
        {
            try
            {
                var composed = await CaptureComposer();
                if (composed is not null) return composed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VP] 批注合成失败，回退到原始帧：{ex.Message}");
            }
        }

        return _camera.GrabStill();
    }

    private async Task BurstCaptureAsync()
    {
        int count = (int)Settings.BurstCount;   // 张数由设置决定（2~9）
        int interval = 130;

        for (int i = 0; i < count; i++)
        {
            await SaveCurrentFrameAsync();
            await Task.Delay(130);
        }
        StatusText = $"连拍完成（{count} 张）";
    }

    private async Task SaveCurrentFrameAsync()
    {
        // GrabStill 返回副本，避免采集线程改写导致撕裂
        using var still = await AcquireFrameAsync();
        if (still is null)
        {
            StatusText = "当前没有可用画面";
            return;
        }

        try
        {
            var now = DateTime.Now;

            // ── 输出格式由设置决定（PNG 无损 / JPEG 体积小且可调质量）──
            bool jpeg = Settings.IsJpeg;
            string ext = jpeg ? ".jpg" : ".png";
            string prefix = string.IsNullOrWhiteSpace(Settings.FileNamePrefix) ? "展台" : Settings.FileNamePrefix;
            string fileName = $"{prefix}_{now:yyyyMMdd_HHmmss_fff}{ext}";
            string path = Path.Combine(SnapshotFolder, fileName);

            using (var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite))
            {
                BitmapEncoder encoder;

                if (jpeg)
                {
                    // JPEG 可以指定画质（设置里 60~100）
                    var props = new BitmapPropertySet
                    {
                        ["ImageQuality"] = new BitmapTypedValue(Settings.JpegQuality / 100f, Windows.Foundation.PropertyType.Single),
                    };
                    encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, props);
                }
                else
                {
                    // PNG 无损：板书 / 试卷等文本内容的最佳选择
                    encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                }

                encoder.SetSoftwareBitmap(still);
                await encoder.FlushAsync();
            }

            // 拍照提示音（可在设置里开关）
            if (Settings.CaptureSound)
            {
                try { Services.AppSounds.Capture(); }
                catch (Exception ex) { Debug.WriteLine($"[VP] 提示音失败：{ex.Message}"); }
            }

            // 拍完自动打开文件夹
            if (Settings.OpenFolderAfterCapture)
            {
                try { Process.Start("explorer.exe", SnapshotFolder); }
                catch (Exception ex) { Debug.WriteLine($"[VP] 打开文件夹失败：{ex.Message}"); }
            }

            // 可选：把文件放进剪贴板，方便直接粘进课件
            if (Settings.CopyPhotoToClipboard)
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var dp = new DataPackage();
                    dp.SetStorageItems(new[] { file });
                    Clipboard.SetContent(dp);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VP] 复制到剪贴板失败：{ex.Message}");
                }
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