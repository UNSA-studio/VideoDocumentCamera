// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoPresenter.App.ViewModels;

namespace VideoPresenter.App.Services;

/// <summary>
/// 应用设置：JSON 持久化，改动立即生效并自动写盘。
///
/// <para><b>存放位置</b></para>
/// <para><c>%LOCALAPPDATA%\UNSA Studio\VideoDocumentCamera\settings.json</c></para>
///
/// <para><b>为什么自带防抖写盘</b></para>
/// <para>
/// 滑杆（笔迹粗细、JPEG 质量）拖动一次会触发几十次属性变更，
/// 每次都写文件既没必要也伤磁盘。这里统一延迟 400ms 合并写一次。
/// </para>
/// </summary>
public sealed class AppSettings : ObservableObject
{
    // ───────────────────────── 存储 ─────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>配置文件路径。</summary>
    public static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UNSA Studio", "VideoDocumentCamera");
            return Path.Combine(dir, "settings.json");
        }
    }

    private System.Threading.Timer? _saveTimer;

    /// <summary>
    /// 反序列化进行中。
    /// <para>
    /// 用静态字段包住整个 Deserialize 调用 —— 反序列化是同步的，
    /// 这样就能抑制"读配置的过程中又去写配置"。
    /// </para>
    /// </summary>
    private static bool s_loading;

    // ───────────────────────── 常规 ─────────────────────────

    private bool _startFullScreen = true;
    /// <summary>启动后自动进入全屏。</summary>
    public bool StartFullScreen
    {
        get => _startFullScreen;
        set { if (SetProperty(ref _startFullScreen, value)) ScheduleSave(); }
    }

    private bool _autoConnectSingleDevice = true;
    /// <summary>只检测到一个设备时自动连接。</summary>
    public bool AutoConnectSingleDevice
    {
        get => _autoConnectSingleDevice;
        set { if (SetProperty(ref _autoConnectSingleDevice, value)) ScheduleSave(); }
    }

    private bool _autoDetectDevices = true;
    /// <summary>自动感知设备插拔（否则只靠手动点刷新）。</summary>
    public bool AutoDetectDevices
    {
        get => _autoDetectDevices;
        set { if (SetProperty(ref _autoDetectDevices, value)) ScheduleSave(); }
    }

    private bool _showBootTime = true;
    /// <summary>状态栏显示启动耗时。</summary>
    public bool ShowBootTime
    {
        get => _showBootTime;
        set { if (SetProperty(ref _showBootTime, value)) ScheduleSave(); }
    }

    // ───────────────────────── 拍照 ─────────────────────────

    private string _photoFormat = "PNG";
    /// <summary>照片格式："PNG"（无损）或 "JPEG"（体积小）。</summary>
    public string PhotoFormat
    {
        get => _photoFormat;
        set { if (SetProperty(ref _photoFormat, value)) { ScheduleSave(); OnPropertyChanged(nameof(IsJpeg)); } }
    }

    /// <summary>是否使用 JPEG（可读写：界面上的开关直接绑这里）。</summary>
    [JsonIgnore]
    public bool IsJpeg
    {
        get => string.Equals(_photoFormat, "JPEG", StringComparison.OrdinalIgnoreCase);
        set => PhotoFormat = value ? "JPEG" : "PNG";
    }

    private double _jpegQuality = 92;
    /// <summary>
    /// JPEG 质量（60~100）。
    /// <para>用 double 而不是 int：WinUI 的 Slider.Value 是 double，
    /// 而 x:Bind 是编译期强类型绑定，不做隐式转换。</para>
    /// </summary>
    public double JpegQuality
    {
        get => _jpegQuality;
        set
        {
            double v = Math.Clamp(Math.Round(value), 60, 100);
            if (SetProperty(ref _jpegQuality, v)) ScheduleSave();
        }
    }

    private bool _copyPhotoToClipboard;
    /// <summary>拍照后把文件放进剪贴板（可直接粘贴到课件里）。</summary>
    public bool CopyPhotoToClipboard
    {
        get => _copyPhotoToClipboard;
        set { if (SetProperty(ref _copyPhotoToClipboard, value)) ScheduleSave(); }
    }

    private string _photoFolder = "";
    /// <summary>素材保存目录；留空表示默认的「图片\视频展台」。</summary>
    public string PhotoFolder
    {
        get => _photoFolder;
        set { if (SetProperty(ref _photoFolder, value ?? "")) { ScheduleSave(); OnPropertyChanged(nameof(PhotoFolderDisplay)); } }
    }

    /// <summary>界面上显示的目录（空值时的文案）。</summary>
    [JsonIgnore]
    public string PhotoFolderDisplay =>
        string.IsNullOrWhiteSpace(_photoFolder) ? "（默认）图片\\视频展台" : _photoFolder;

    // ───────────────────────── 录像 ─────────────────────────

    private int _recordFps = 30;
    /// <summary>录像帧率。</summary>
    public int RecordFps
    {
        get => _recordFps;
        set
        {
            int v = value is 15 or 30 or 60 ? value : 30;
            if (SetProperty(ref _recordFps, v)) ScheduleSave();
        }
    }

    private int _recordBitrateMbps = 8;
    /// <summary>录像码率（Mbps）。</summary>
    public int RecordBitrateMbps
    {
        get => _recordBitrateMbps;
        set
        {
            int v = value is 4 or 8 or 16 or 24 ? value : 8;
            if (SetProperty(ref _recordBitrateMbps, v)) ScheduleSave();
        }
    }

    // ───────────────────────── 批注 ─────────────────────────

    private string _inkColor = "#FFE53935";
    /// <summary>批注默认颜色（#AARRGGBB）。</summary>
    public string InkColor
    {
        get => _inkColor;
        set { if (SetProperty(ref _inkColor, value)) ScheduleSave(); }
    }

    private double _inkThickness = 4;
    /// <summary>批注默认粗细。</summary>
    public double InkThickness
    {
        get => _inkThickness;
        set
        {
            double v = Math.Clamp(value, 2, 12);
            if (SetProperty(ref _inkThickness, v)) ScheduleSave();
        }
    }

    private bool _burnAnnotationsIntoPhoto = true;
    /// <summary>截图时把批注一起烧录进图片。</summary>
    public bool BurnAnnotationsIntoPhoto
    {
        get => _burnAnnotationsIntoPhoto;
        set { if (SetProperty(ref _burnAnnotationsIntoPhoto, value)) ScheduleSave(); }
    }

    // ───────────────────────── 热键 ─────────────────────────

    private bool _hotkeyCaptureEnabled = true;
    /// <summary>启用全局拍照热键（Ctrl+Alt+P）。</summary>
    public bool HotkeyCaptureEnabled
    {
        get => _hotkeyCaptureEnabled;
        set { if (SetProperty(ref _hotkeyCaptureEnabled, value)) ScheduleSave(); }
    }

    private bool _hotkeyFullScreenEnabled = true;
    /// <summary>启用全局全屏热键（Ctrl+Alt+F）。</summary>
    public bool HotkeyFullScreenEnabled
    {
        get => _hotkeyFullScreenEnabled;
        set { if (SetProperty(ref _hotkeyFullScreenEnabled, value)) ScheduleSave(); }
    }

    // ───────────────────── 拍照（进阶）─────────────────────

    private string _fileNamePrefix = "展台";
    /// <summary>素材文件名前缀（如「展台_20261006_143012.png」）。</summary>
    public string FileNamePrefix
    {
        get => _fileNamePrefix;
        set
        {
            // 文件名非法字符直接剔掉，避免写盘失败
            string clean = new string((value ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            if (SetProperty(ref _fileNamePrefix, clean)) ScheduleSave();
        }
    }

    private double _burstCount = 3;
    /// <summary>连拍张数（2~9）。用 double 是因为 Slider.Value 是 double，x:Bind 不做隐式转换。</summary>
    public double BurstCount
    {
        get => _burstCount;
        set
        {
            double v = Math.Clamp(Math.Round(value), 2, 9);
            if (SetProperty(ref _burstCount, v)) ScheduleSave();
        }
    }

    private bool _captureSound;
    /// <summary>拍照时播放提示音。</summary>
    public bool CaptureSound
    {
        get => _captureSound;
        set { if (SetProperty(ref _captureSound, value)) ScheduleSave(); }
    }

    private bool _openFolderAfterCapture;
    /// <summary>拍照后自动打开素材文件夹。</summary>
    public bool OpenFolderAfterCapture
    {
        get => _openFolderAfterCapture;
        set { if (SetProperty(ref _openFolderAfterCapture, value)) ScheduleSave(); }
    }

    // ───────────────────── 录像（进阶）─────────────────────

    private double _recordMaxMinutes;
    /// <summary>单次录像时长上限（分钟）；0 表示不限制。同样用 double 以适配 Slider。</summary>
    public double RecordMaxMinutes
    {
        get => _recordMaxMinutes;
        set
        {
            double v = Math.Clamp(Math.Round(value), 0, 180);
            if (SetProperty(ref _recordMaxMinutes, v)) ScheduleSave();
        }
    }

    // ───────────────────── 批注（进阶）─────────────────────

    private double _eraserThickness = 20;
    /// <summary>橡皮擦大小。</summary>
    public double EraserThickness
    {
        get => _eraserThickness;
        set
        {
            double v = Math.Clamp(value, 8, 60);
            if (SetProperty(ref _eraserThickness, v)) ScheduleSave();
        }
    }

    // ───────────────────── 界面 ─────────────────────

    private bool _rightPanelOpenByDefault = true;
    /// <summary>启动时右侧素材栏是否展开。</summary>
    public bool RightPanelOpenByDefault
    {
        get => _rightPanelOpenByDefault;
        set { if (SetProperty(ref _rightPanelOpenByDefault, value)) ScheduleSave(); }
    }

    private string _statusBarPosition = "Bottom";
    /// <summary>
    /// 状态栏位置："Bottom"（默认）/ "Top" / "Left" / "Right"。
    /// </summary>
    public string StatusBarPosition
    {
        get => _statusBarPosition;
        set
        {
            string v = value is "Top" or "Left" or "Right" ? value : "Bottom";
            if (SetProperty(ref _statusBarPosition, v)) ScheduleSave();
        }
    }

    private bool _showGuides;
    /// <summary>画面上显示三分线辅助格。</summary>
    public bool ShowGuides
    {
        get => _showGuides;
        set { if (SetProperty(ref _showGuides, value)) ScheduleSave(); }
    }

    // ───────────────────── 设备 ─────────────────────

    private bool _rememberLastDevice = true;
    /// <summary>记住上次使用的设备，下次自动连接它。</summary>
    public bool RememberLastDevice
    {
        get => _rememberLastDevice;
        set { if (SetProperty(ref _rememberLastDevice, value)) ScheduleSave(); }
    }

    private string _lastDeviceName = "";
    /// <summary>上次成功连接的设备名。</summary>
    public string LastDeviceName
    {
        get => _lastDeviceName;
        set { if (SetProperty(ref _lastDeviceName, value ?? "")) ScheduleSave(); }
    }

    // ───────────────────── 启动 ─────────────────────

    private bool _autoStartWithWindows;
    /// <summary>随 Windows 启动（写 HKCU Run 项）。</summary>
    public bool AutoStartWithWindows
    {
        get => _autoStartWithWindows;
        set { if (SetProperty(ref _autoStartWithWindows, value)) ScheduleSave(); }
    }

    // ───────────────────────── 读写 ─────────────────────────

    /// <summary>从磁盘读取设置；文件不存在或损坏时返回默认值。</summary>
    public static AppSettings Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return new AppSettings();

            string json = File.ReadAllText(path);

            // 反序列化会逐个调用属性 setter —— 期间抑制写盘，避免"边读边写"
            s_loading = true;
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                return loaded ?? new AppSettings();
            }
            finally
            {
                s_loading = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP-CFG] 读取设置失败，使用默认值：{ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>立即写盘。</summary>
    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 先写临时文件再替换，避免断电 / 崩溃把配置写坏
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP-CFG] 保存设置失败：{ex.Message}");
        }
    }

    /// <summary>防抖：合并 400ms 内的多次改动，只写一次盘。</summary>
    private void ScheduleSave()
    {
        if (s_loading) return;

        _saveTimer ??= new System.Threading.Timer(_ => Save(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        _saveTimer.Change(400, System.Threading.Timeout.Infinite);
    }
}