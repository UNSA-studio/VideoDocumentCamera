// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
//
// 本文件属于视频展台（VideoPresenter）。分发时请随文件一并提供许可条款：
// https://polyformproject.org/licenses/noncommercial/1.0.0
//
using System.Diagnostics;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using VideoPresenter.App.Services;
using VideoPresenter.App.ViewModels;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace VideoPresenter.App;

/// <summary>
/// 主窗口（WinUI 3）。
///
/// <para><b>配色</b>：不设置任何自定义颜色，靠 XAML 里的系统主题资源自动跟随系统。
/// 本类只负责在系统主题发生变化时同步 DWM 非客户区，并更新状态栏提示文字。</para>
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly ICameraService _camera;

    /// <summary>x:Bind 的数据源（必须在 InitializeComponent 之前赋值）。</summary>
    public MainViewModel Vm { get; }

    private IntPtr _hwnd;
    private Win32.SUBCLASSPROC? _subclassProc;

    private SoftwareBitmapSource? _previewSource;
    private int _previewWidth;
    private int _previewHeight;

    /// <summary>上一帧还没画完时丢弃新帧，避免 UI 线程被采集帧淹没。</summary>
    private volatile bool _framePending;

    private bool _isFullScreen;

    public MainWindow()
    {
        _camera = new MediaFoundationCameraService();
        Vm = new MainViewModel(_camera);

        InitializeComponent();

        Title = "视频展台";

        // ── 自定义标题栏：右侧系统按钮保持原生绘制 ─────────────────────────
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // ── 窗口大小与居中 ─────────────────────────────────────────────────
        AppWindow.Resize(new SizeInt32(1180, 760));
        CenterOnScreen();

        // ── 系统材质：Win11 用 Mica，降级到 Acrylic；失败则退化为实色 ────────
        TryApplySystemBackdrop();

        // ── 事件 ───────────────────────────────────────────────────────────
        RootGrid.Loaded += OnRootLoaded;
        RootGrid.ActualThemeChanged += OnActualThemeChanged;

        Vm.PropertyChanged += OnViewModelPropertyChanged;
        Vm.FullScreenRequested += (_, on) => SetFullScreen(on);

        _camera.FrameArrived += OnFrameArrived;

        AppWindow.Closing += OnAppWindowClosing;

        // 拍照时若画面上有批注，把批注一起渲染进截图
        Vm.CaptureComposer = ComposeFrameWithAnnotationsAsync;

        // 设置变化时实时生效（热键开关 / 批注默认值）
        Vm.Settings.PropertyChanged += OnSettingsChanged;

        // 把设置里的批注默认值应用到工具栏
        ApplyInkSettings();

        // 旋转 90°/270° 时需要重算画面尺寸（WinUI 没有 LayoutTransform）
        PreviewContainer.SizeChanged += (_, _) => UpdateRotationLayout();
    }

    /// <summary>设置项变化时实时生效。</summary>
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.HotkeyCaptureEnabled):
            case nameof(AppSettings.HotkeyFullScreenEnabled):
                ApplyHotKeySettings();
                break;

            case nameof(AppSettings.InkColor):
            case nameof(AppSettings.InkThickness):
                ApplyInkSettings();
                break;
        }
    }

    /// <summary>按设置注册 / 注销全局热键。</summary>
    private void ApplyHotKeySettings()
    {
        if (_hwnd == IntPtr.Zero) return;

        // 先全部注销，再按开关重新注册
        WindowApiService.UnregisterHotKey(_hwnd, WindowApiService.HotKeyCapture);
        WindowApiService.UnregisterHotKey(_hwnd, WindowApiService.HotKeyFullScreen);

        if (Vm.Settings.HotkeyCaptureEnabled)
        {
            WindowApiService.RegisterHotKey(_hwnd, WindowApiService.HotKeyCapture,
                Win32.MOD_CONTROL | Win32.MOD_ALT, 0x50 /* P */);
        }

        if (Vm.Settings.HotkeyFullScreenEnabled)
        {
            WindowApiService.RegisterHotKey(_hwnd, WindowApiService.HotKeyFullScreen,
                Win32.MOD_CONTROL | Win32.MOD_ALT, 0x46 /* F */);
        }
    }
/// <summary>
    /// 设备热插拔检测。
    /// <para>
    /// 原先只有手动「刷新」按钮。这里用轻量轮询：<b>只在当前没有任何设备时才重扫</b> ——
    /// 这样把展台插上后会自动出现在下拉框里，而已有设备时不会白白消耗 CPU。
    /// </para>
    /// <para>
    /// 之所以不用 WinRT 的 DeviceWatcher：unpackaged 应用里它需要额外的线程与事件订阅管理，
    /// 而"数量变了就重扫"用轮询已经足够。
    /// </para>
    /// </summary>
    private void StartDeviceWatcher()
    {
        if (_deviceTimer is not null) return;

        _deviceTimer = DispatcherQueue.CreateTimer();
        _deviceTimer.Interval = TimeSpan.FromSeconds(3);
        _deviceTimer.Tick += (_, _) =>
        {
            if (!Vm.Settings.AutoDetectDevices) return;

            // 已经有设备就不再反复扫；只有"一个都没找到"时才持续重试
            if (Vm.Devices.Count == 0 && Vm.RefreshDevicesCommand.CanExecute(null))
            {
                Vm.RefreshDevicesCommand.Execute(null);
            }
        };
        _deviceTimer.Start();
    }

    /// <summary>把设置里的批注默认颜色 / 粗细应用到工具栏。</summary>
    private void ApplyInkSettings()
    {
        try
        {
            string s = Vm.Settings.InkColor.TrimStart('#');
            if (s.Length >= 8)
            {
                _annotationColor = Color.FromArgb(
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16),
                    Convert.ToByte(s.Substring(6, 2), 16));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 解析设置里的批注颜色失败：{ex.Message}");
        }

        AnnotationThickness.Value = Vm.Settings.InkThickness;
    }

    // ══════════════════════ 初始化 ══════════════════════

    private void TryApplySystemBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                // BaseAlt：更接近资源管理器 / 设置应用的观感
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                Debug.WriteLine("[VP] 已启用 Mica 材质");
            }
            else if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                Debug.WriteLine("[VP] 已启用 Acrylic 材质（系统不支持 Mica）");
            }
        }
        catch (Exception ex)
        {
            // Win10 等环境下静默退化：界面使用系统主题的实色背景，观感依旧正确
            Debug.WriteLine($"[VP] 系统材质不可用，退化为实色背景：{ex.Message}");
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootLoaded;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // DWM 圆角 + 非客户区深浅色（窗口阴影 / 边框与内容一致）
        WindowApiService.ApplyFluentChrome(_hwnd, RootGrid.ActualTheme == ElementTheme.Dark);

        // 接收 WM_HOTKEY / 激活广播
        _subclassProc = SubclassProc;
        Win32.SetWindowSubclass(_hwnd, _subclassProc, 1, UIntPtr.Zero);

        // 热键是否注册由设置决定（可在设置面板里开关）
        ApplyHotKeySettings();

        // 设备热插拔：没设备时自动重扫，插上就能自动出现
        StartDeviceWatcher();

        UpdateThemeIndicator();

        // ★ 首帧已经画完 —— 等一个低优先级回合。
        //   顺序很重要：先全屏，再报告就绪。
        //   这样启动器的 Splash 消失时，用户看到的就是已经全屏的界面，
        //   而不是先看到一个 1180×760 的小窗再"啪"地跳成全屏。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // 是否全屏由设置决定（默认开）
            if (Vm.Settings.StartFullScreen) SetFullScreen(true);

            // 把存放在设置里的开关同步到实际状态（注册表 / 辅助线 / 状态栏位置）
            ApplyGuides();
            ApplyStatusBarPosition();
            ApplyAutoStart(Vm.Settings.AutoStartWithWindows);

            App.ReportWindowReady(this);
            Vm.Initialize();
        });
    }

    // ══════════════════════ 窗口消息 ══════════════════════

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
                                UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        // 另一个实例请求前置
        if (uMsg == WindowApiService.WM_VP_ACTIVATE)
        {
            BringToFront();
            return IntPtr.Zero;
        }

        if (uMsg == Win32.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case WindowApiService.HotKeyCapture:
                    if (Vm.CaptureCommand.CanExecute(null)) Vm.CaptureCommand.Execute(null);
                    return IntPtr.Zero;

                case WindowApiService.HotKeyFullScreen:
                    SetFullScreen(!_isFullScreen);
                    return IntPtr.Zero;
            }
        }

        // Esc：设置浮层开着就先关浮层（优先于"退出全屏"）。
        // 用窗口消息而不是 XAML 的 KeyboardAccelerator —— 后者在浮层与焦点元素之间
        // 容易互相干扰（加速器作用域、Popup 焦点），窗口级消息最直接可靠。
        if (uMsg == 0x0100 /* WM_KEYDOWN */
            && wParam.ToInt32() == 0x1B /* VK_ESCAPE */
            && _settingsOpen)
        {
            HideSettings();
            return IntPtr.Zero;
        }

        return Win32.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void BringToFront()
    {
        Activate();
        if (_hwnd != IntPtr.Zero) Win32.SetForegroundWindow(_hwnd);
    }

    // ══════════════════════ 主题跟随 ══════════════════════

    /// <summary>
    /// WinUI 运行时会在系统主题变化时自动刷新所有 ThemeResource。
    /// 这里只做两件"系统资源管不到"的事：
    /// ① 同步 DWM 非客户区（窗口边框 / 阴影）；
    /// ② 更新状态栏提示文字。
    /// </summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        WindowApiService.SetImmersiveDarkMode(_hwnd, sender.ActualTheme == ElementTheme.Dark);
        UpdateThemeIndicator();
    }

    private void UpdateThemeIndicator()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        string mode = dark ? "深色" : "浅色";
        ThemeModeText.Text = $"跟随系统（{mode}）";
        Vm.ThemeModeText = ThemeModeText.Text;
    }

    // ══════════════════════ 采集帧 ══════════════════════

    private void OnFrameArrived(object? sender, SoftwareBitmap bitmap)
    {
        // ══ 录像：把原始像素送进录制器（只在录制时做，避免无谓的内存拷贝）══
        if (_recorder is { IsRecording: true } && !_recordFrameBusy)
        {
            _recordFrameBusy = true;
            try
            {
                var bytes = _camera.GrabFrameBytes();
                if (bytes is not null) _recorder.WriteFrame(bytes);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VP] 录像写帧失败：{ex.Message}");
            }
            finally
            {
                _recordFrameBusy = false;
            }
        }

        // 背压：上一帧尚未上屏则直接丢弃本帧
        if (_framePending) return;
        _framePending = true;

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_previewSource is null
                    || _previewWidth != bitmap.PixelWidth
                    || _previewHeight != bitmap.PixelHeight)
                {
                    _previewSource = new SoftwareBitmapSource();
                    _previewWidth = bitmap.PixelWidth;
                    _previewHeight = bitmap.PixelHeight;
                    PreviewSurface.Source = _previewSource;
                }

                // 复用同一个 SoftwareBitmap 不断上屏 —— 零额外分配
                await _previewSource.SetBitmapAsync(bitmap);

                // 对比模式下，右侧的实时画面与主画面共用同一个帧源
                CompareLiveImage.Source = _previewSource;

                Vm.UpdateFrameInfo(bitmap.PixelWidth, bitmap.PixelHeight);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VP] 帧上屏失败：{ex.Message}");
            }
            finally
            {
                _framePending = false;
            }
        });
    }

    // ── 窗口级动作 ─────────────────────────────────────────────────────
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsTopmost):
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.IsAlwaysOnTop = Vm.IsTopmost;
                break;

            case nameof(MainViewModel.IsRightPanelOpen):
                AnimateRightPanel(Vm.IsRightPanelOpen);
                break;

            case nameof(MainViewModel.IsAnnotating):
                SetAnnotationMode(Vm.IsAnnotating);
                break;

            case nameof(MainViewModel.IsComparing):
                SetCompareMode(Vm.IsComparing);
                break;
        }
    }

    /// <summary>
    /// 素材栏的折叠 / 展开动画。
    /// <para>
    /// 为什么不用直接设 Visibility：那样是"啪"地一下消失/出现，非常生硬。
    /// 这里对 Border.Width 做 240ms 缓动（GridLength 本身不可动画，所以要绕到 Width 上），
    /// 折叠动画播完后再设 Collapsed，避免内容溢出。
    /// </para>
    /// </summary>
    private void AnimateRightPanel(bool open)
    {
        if (RightPanel is null) return;

        if (open) RightPanel.Visibility = Visibility.Visible;

        var animation = new DoubleAnimation
        {
            To = open ? 252.0 : 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            // Width 会影响布局，必须显式允许"依赖动画"，否则会被跳过
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(animation, RightPanel);
        Storyboard.SetTargetProperty(animation, "Width");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);

        if (!open)
        {
            storyboard.Completed += (_, _) => RightPanel.Visibility = Visibility.Collapsed;
        }

        storyboard.Begin();
    }

    // ══════════════════════ 批注 ══════════════════════
    //
    //  WinUI 3 没有 InkCanvas（那是 UWP / WPF 的控件），所以笔迹要自己实现：
    //     PointerPressed  → 新建一条 Polyline 并捕获指针
    //     PointerMoved    → 往 Polyline 里追加点
    //     PointerReleased → 结束这一笔
    //  鼠标、触摸、手写笔走的是同一套 Pointer 事件，因此三种输入都支持。

    private readonly List<Microsoft.UI.Xaml.Shapes.Polyline> _annotations = new();
    private Microsoft.UI.Xaml.Shapes.Polyline? _currentStroke;
    private uint _activePointerId;
    private bool _eraserMode;
    private Color _annotationColor = Colors.Red;
    private double _rotationAngle;

    /// <summary>
    /// 进入 / 退出批注模式。
    /// <para>
    /// <b>⚠ 画布不能跟着隐藏。</b>
    /// 批注的语义是「画在画面上并留在画面上」——
    /// 退出批注模式只是不再接受新的笔迹，已有的笔迹必须继续可见。
    /// 所以这里只切换 IsHitTestVisible（是否响应指针），Visibility 恒为 Visible。
    /// 笔迹的清除只有两条途径：用户用橡皮擦擦、或退出软件。
    /// </para>
    /// </summary>
    private void SetAnnotationMode(bool on)
    {
        AnnotationCanvas.Visibility = Visibility.Visible;
        AnnotationCanvas.IsHitTestVisible = on;
        AnnotationToolbar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (!on) EndStroke(null);
    }

    private void AnnotationCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(AnnotationCanvas);
        _activePointerId = pt.PointerId;

        // ── 橡皮擦：擦掉笔画所经过的那一段（不是整笔）──
        if (_eraserMode)
        {
            // 捕获指针，这样拖到画布之外也能持续收到 Moved
            try { AnnotationCanvas.CapturePointer(e.Pointer); }
            catch { /* 忽略 */ }

            ResetEraseThrottle();
            EraseAtThrottled(pt.Position);
            return;
        }

        // 只响应主键：鼠标需要左键按下；触摸 / 笔尖天然是按下的。
        // ⚠ WinUI 3 的 PointerPoint 【没有】 PointerDevice 属性（那是 UWP 版 API），
        //   设备类型必须从 PointerRoutedEventArgs.Pointer 上取。
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            !pt.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _currentStroke = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(_annotationColor),
            StrokeThickness = AnnotationThickness.Value,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _currentStroke.Points.Add(pt.Position);

        AnnotationCanvas.Children.Add(_currentStroke);
        _annotations.Add(_currentStroke);

        AnnotationCanvas.CapturePointer(e.Pointer);
    }

    private void AnnotationCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(AnnotationCanvas);

        // ── 橡皮擦拖动：连续擦除 ──
        if (_eraserMode)
        {
            if (pt.PointerId != _activePointerId) return;

            // 鼠标可能在按键松开后仍然送来 Moved，需要主动收尾
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
                !pt.Properties.IsLeftButtonPressed)
            {
                EndStroke(e);
                return;
            }

            EraseAtThrottled(pt.Position);
            return;
        }

        if (_currentStroke is null) return;

        if (pt.PointerId != _activePointerId) return;

        // 鼠标可能在按键松开后仍然送来 Moved，需要主动收尾
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            !pt.Properties.IsLeftButtonPressed)
        {
            EndStroke(e);
            return;
        }

        _currentStroke.Points.Add(pt.Position);
    }

    private void AnnotationCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndStroke(e);

    private void EndStroke(PointerRoutedEventArgs? e)
    {
        if (_currentStroke is null) return;

        if (e is not null)
        {
            try { AnnotationCanvas.ReleasePointerCapture(e.Pointer); }
            catch { /* 指针已释放 */ }
        }

        _currentStroke = null;
    }

    /// <summary>橡皮擦半径（画布像素），取自设置。</summary>
    private double EraserRadius => Vm.Settings.EraserThickness;

    /// <summary>上一次执行擦除的位置（用于节流，避免每帧重建笔画导致卡顿）。</summary>
    private Point _lastErasePoint;
    private bool _hasErasedOnce;

    /// <summary>
    /// 橡皮擦入口（带节流）。
    ///
    /// <para><b>为什么要节流</b></para>
    /// <para>
    /// 局部擦除需要遍历所有笔迹的所有顶点，并重建被擦中的笔画。
    /// PointerMoved 每秒能来 100+ 次，每次都做全量重建会明显卡顿 ——
    /// 表现就是"一帧一帧地擦"。这里要求指针至少移动了 1/4 橡皮半径才处理一次。
    /// </para>
    /// </summary>
    private void EraseAtThrottled(Point p)
    {
        if (_hasErasedOnce)
        {
            double dx = p.X - _lastErasePoint.X;
            double dy = p.Y - _lastErasePoint.Y;
            double minStep = Math.Max(2.0, EraserRadius * 0.25);

            if (dx * dx + dy * dy < minStep * minStep) return;
        }

        _lastErasePoint = p;
        _hasErasedOnce = true;

        EraseAt(p);
    }

    /// <summary>一次擦除动作结束时调用（下次按下时重新开始）。</summary>
    private void ResetEraseThrottle() => _hasErasedOnce = false;

    /// <summary>
    /// 局部擦除：把落在橡皮范围内的顶点从笔画中剔除，剩余部分按连续性拆成若干条新笔画。
    ///
    /// <para><b>为什么不能简单地删掉点</b></para>
    /// <para>
    /// WinUI 3 没有 InkCanvas（那是 UWP / WPF 的），笔画就是一条 Polyline。
    /// 若只删掉中间的点，Polyline 会把前后两点直接连起来 ——
    /// 视觉上就是"擦出一个窟窿但多了一条横线"。所以必须按连续性把剩下的顶点拆成多段。
    /// </para>
    /// </summary>
    private void EraseAt(Point p)
    {
        double r2 = EraserRadius * EraserRadius;

        var victims = new List<Microsoft.UI.Xaml.Shapes.Polyline>();
        var additions = new List<Microsoft.UI.Xaml.Shapes.Polyline>();

        foreach (var stroke in _annotations)
        {
            if (stroke.Points.Count == 0) continue;

            var segments = new List<List<Point>>();
            var current = new List<Point>();

            foreach (var v in stroke.Points)
            {
                double dx = v.X - p.X;
                double dy = v.Y - p.Y;

                if (dx * dx + dy * dy <= r2)
                {
                    // 命中：断开当前段
                    if (current.Count >= 2) segments.Add(current);
                    current = new List<Point>();
                }
                else
                {
                    current.Add(v);
                }
            }

            if (current.Count >= 2) segments.Add(current);

            // 本笔完全没被擦到 → 保持原样
            if (segments.Count == 1 && segments[0].Count == stroke.Points.Count) continue;

            victims.Add(stroke);

            foreach (var seg in segments)
            {
                var nl = new Microsoft.UI.Xaml.Shapes.Polyline
                {
                    Stroke = stroke.Stroke,
                    StrokeThickness = stroke.StrokeThickness,
                    StrokeLineJoin = stroke.StrokeLineJoin,
                    StrokeStartLineCap = stroke.StrokeStartLineCap,
                    StrokeEndLineCap = stroke.StrokeEndLineCap,
                };

                foreach (var v in seg) nl.Points.Add(v);
                additions.Add(nl);
            }
        }

        if (victims.Count == 0) return;

        foreach (var v in victims)
        {
            AnnotationCanvas.Children.Remove(v);
            _annotations.Remove(v);
        }

        foreach (var a in additions)
        {
            AnnotationCanvas.Children.Add(a);
            _annotations.Add(a);
        }
    }

    /// <summary>命中测试：点是否落在某条笔画的顶点附近（顶点足够密集，够用）。</summary>
    private static bool HitTestStroke(Microsoft.UI.Xaml.Shapes.Polyline stroke, Point p, double tolerance = 10)
    {
        double t2 = tolerance * tolerance;

        foreach (var v in stroke.Points)
        {
            double dx = v.X - p.X;
            double dy = v.Y - p.Y;
            if (dx * dx + dy * dy <= t2) return true;
        }

        return false;
    }

    private void PenButton_Click(object sender, RoutedEventArgs e)
    {
        _eraserMode = false;
        PenButton.IsChecked = true;
        EraserButton.IsChecked = false;
    }

    private void EraserButton_Click(object sender, RoutedEventArgs e)
    {
        _eraserMode = true;
        EraserButton.IsChecked = true;
        PenButton.IsChecked = false;
    }

    private void AnnotationColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;

        try
        {
            string s = hex.TrimStart('#');

            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            byte a = s.Length >= 8 ? Convert.ToByte(s.Substring(6, 2), 16) : (byte)255;

            _annotationColor = Color.FromArgb(a, r, g, b);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 解析批注颜色失败：{ex.Message}");
        }
    }

    private void UndoAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (_annotations.Count == 0) return;

        var last = _annotations[^1];
        _annotations.RemoveAt(_annotations.Count - 1);
        AnnotationCanvas.Children.Remove(last);
    }

    private void ClearAnnotation_Click(object sender, RoutedEventArgs e)
    {
        _annotations.Clear();
        AnnotationCanvas.Children.Clear();
    }

    private void ExitAnnotation_Click(object sender, RoutedEventArgs e)
        => Vm.IsAnnotating = false;

    // ══════════════════════ 对比 ══════════════════════

    /// <summary>进入 / 退出对比模式（左：选中素材；右：实时画面）。</summary>
    private void SetCompareMode(bool on)
    {
        if (on)
        {
            var snap = Vm.SelectedSnapshot;
            if (snap is null)
            {
                Vm.SetStatus("对比模式：请先在右侧素材栏选择一张图片");
                Vm.IsComparing = false;
                return;
            }

            // 左侧加载素材原图
            CompareSnapshotImage.Source = new BitmapImage { UriSource = new Uri(snap.FilePath) };

            // 右侧用当前的实时帧
            CompareLiveImage.Source = _previewSource;

            CompareLayer.Visibility = Visibility.Visible;
            Vm.SetStatus($"对比中：{snap.FileName}（左） ⟷ 实时画面（右）");
        }
        else
        {
            CompareLayer.Visibility = Visibility.Collapsed;
            CompareSnapshotImage.Source = null;
            CompareLiveImage.Source = null;
        }
    }

    // ══════════════════════ OCR ══════════════════════

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        var frame = _camera.GrabStill();
        if (frame is null)
        {
            Vm.SetStatus("OCR：当前没有可用画面");
            return;
        }

        try
        {
            // 优先中文，其次跟随系统，最后英文。
            // 具体能用哪种，取决于系统是否安装了对应的 OCR 语言包。
            var engine =
                Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"))
                ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

            if (engine is null)
            {
                await ShowTextDialogAsync("OCR 不可用",
                    "系统未安装任何 OCR 语言包。\n\n" +
                    "可到「设置 → 时间和语言 → 语言和区域」中，\n" +
                    "为中文或英文语言添加「光学字符识别」可选功能，然后重试。");
                return;
            }

            Vm.SetStatus($"OCR 识别中…（语言：{engine.RecognizerLanguage.DisplayName}）");

            var result = await engine.RecognizeAsync(frame);
            string text = result?.Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                await ShowTextDialogAsync("OCR 结果",
                    "未识别到文字。\n\n提示：让文字尽量占满画面、光线均匀、避免反光。");
                Vm.SetStatus("OCR 完成：未识别到文字");
            }
            else
            {
                // 顺手复制到剪贴板，方便直接粘贴到课件里
                try
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(text);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                }
                catch { /* 剪贴板被占用时忽略 */ }

                await ShowTextDialogAsync("OCR 结果（已复制到剪贴板）", text);
                Vm.SetStatus($"OCR 完成：识别到 {text.Length} 个字符，已复制到剪贴板");
            }
        }
        catch (Exception ex)
        {
            Vm.SetStatus($"OCR 失败：{ex.Message}");
            Debug.WriteLine($"[VP] OCR 失败：{ex}");
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>弹出一个可选中 / 复制的文本对话框。</summary>
    private async Task ShowTextDialogAsync(string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = box, MaxHeight = 420 },
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    // ══════════════════════ 录像 ══════════════════════

    private RecordingService? _recorder;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _recordTimer;
    private volatile bool _recordFrameBusy;

    /// <summary>设备热插拔轮询定时器。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _deviceTimer;

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is { IsRecording: true })
        {
            StopRecording();
            return;
        }

        StartRecording();
    }

    private void StartRecording()
    {
        if (!Vm.IsPreviewing)
        {
            Vm.SetStatus("录像：请先连接设备并开始预览");
            return;
        }

        // 用当前帧尺寸作为录制分辨率
        var probe = _camera.GrabStill();
        if (probe is null)
        {
            Vm.SetStatus("录像：当前没有可用画面");
            return;
        }

        int width = probe.PixelWidth;
        int height = probe.PixelHeight;
        probe.Dispose();

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "视频展台", "录像");
        string path = Path.Combine(dir, $"录像_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        _recorder ??= new RecordingService();

        // 帧率与码率取自设置
        int fps = Vm.Settings.RecordFps;
        int bitrate = Vm.Settings.RecordBitrateMbps * 1_000_000;

        if (!_recorder.Start(path, width, height, fps, bitrate))
        {
            Vm.SetStatus("录像：启动失败（详见诊断日志）");
            return;
        }

        // 按钮切到「停止」状态
        RecordIcon.Glyph = "\uE71A";                       // Stop
        RecordIcon.Foreground = new SolidColorBrush(Colors.Red);
        RecordLabel.Text = "停止";

        // 每秒刷新一次状态栏，显示录制时长
        _recordTimer = DispatcherQueue.CreateTimer();
        _recordTimer.Interval = TimeSpan.FromSeconds(1);
        _recordTimer.Tick += (_, _) =>
        {
            if (_recorder is not { IsRecording: true }) return;

            Vm.SetStatus($"● 录像中 {_recorder.Duration:mm\\:ss}   ·   {Path.GetFileName(_recorder.OutputPath)}");

            // 时长上限（0 = 不限）：到点自动停，避免忘关录像把磁盘写满
            double limitMinutes = Vm.Settings.RecordMaxMinutes;
            if (limitMinutes > 0 && _recorder.Duration.TotalMinutes >= limitMinutes)
            {
                StopRecording();
                Vm.SetStatus($"已达录像时长上限（{limitMinutes:0} 分钟），已自动停止");
            }
        };
        _recordTimer.Start();

        Vm.SetStatus($"● 录像中 00:00   ·   {Path.GetFileName(path)}");
    }

    private void StopRecording()
    {
        _recordTimer?.Stop();
        _recordTimer = null;

        string? path = _recorder?.OutputPath;
        _recorder?.Stop();

        // 恢复按钮
        RecordIcon.Glyph = "\uE714";
        RecordIcon.ClearValue(FontIcon.ForegroundProperty);   // 回到主题默认前景色
        RecordLabel.Text = "录像";

        if (!string.IsNullOrEmpty(path))
        {
            Vm.SetStatus($"录像已保存：{Path.GetFileName(path)}");
        }
    }

    // ══════════════════════ 批注合成（烧录进截图） ══════════════════════

    /// <summary>
    /// 把「实时画面 + 批注」渲染成一张位图，交给 ViewModel 保存。
    /// <para>
    /// 没有批注时返回 null —— 调用方会用相机原始帧（保持全分辨率）。
    /// 有批注时用 RenderTargetBitmap，分辨率等于预览控件在屏幕上的实际尺寸。
    /// </para>
    /// </summary>
    private async Task<SoftwareBitmap?> ComposeFrameWithAnnotationsAsync()
    {
        // 设置里可以关闭"截图包含批注"
        if (!Vm.Settings.BurnAnnotationsIntoPhoto) return null;

        if (_annotations.Count == 0) return null;

        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(PreviewContainer);

        var pixels = await rtb.GetPixelsAsync();

        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels,
            BitmapPixelFormat.Bgra8,
            rtb.PixelWidth,
            rtb.PixelHeight,
            BitmapAlphaMode.Premultiplied);
    }

    // ══════════════════════ 旋转尺寸补偿 ══════════════════════

    /// <summary>
    /// 旋转 90° / 270° 时的尺寸补偿。
    /// <para>
    /// WinUI 没有 WPF 的 LayoutTransform，RenderTransform 不参与布局测量，
    /// 所以旋转后画面仍按「原宽 × 原高」的比例做 Uniform 缩放，视觉上会留白。
    /// 这里把 Image 的布局尺寸交换一下，让 Uniform 按旋转后的比例计算。
    /// </para>
    /// </summary>
    private void UpdateRotationLayout()
    {
        bool swap = Math.Abs(_rotationAngle % 180) == 90;

        if (swap)
        {
            double w = PreviewContainer.ActualWidth;
            double h = PreviewContainer.ActualHeight;

            if (w <= 0 || h <= 0) return;

            PreviewSurface.Width = h;
            PreviewSurface.Height = w;
        }
        else
        {
            // 回到自动尺寸
            PreviewSurface.Width = double.NaN;
            PreviewSurface.Height = double.NaN;
        }
    }

    // ══════════════════════ 旋转 ══════════════════════

    /// <summary>顺时针旋转 90°（0° → 90° → 180° → 270° 循环）。</summary>
    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        _rotationAngle = (_rotationAngle + 90) % 360;
        PreviewRotate.Angle = _rotationAngle;

        // 90°/270° 时需要交换画面布局尺寸，否则会留白
        UpdateRotationLayout();

        Vm.SetStatus(_rotationAngle == 0
            ? "画面方向：正常"
            : $"画面方向：已旋转 {_rotationAngle:0}°");
    }

    // ══════════════════════ 设置面板 ══════════════════════

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        await ShowSettingsAsync();
    }

    // ══════════════════════ 设置浮层 ══════════════════════

    private bool _settingsOpen;

    /// <summary>
    /// 打开设置浮层。
    /// <para>
    /// <b>每次打开都重置到动画起始状态</b> —— 这正是当初用 ContentDialog 的毛病：
    /// 它复用同一份内容元素，而 XAML 过渡动画只在元素首次加载时播放，
    /// 所以第二次打开起就没有动画了。自绘浮层由 Storyboard 显式驱动，不存在这个问题。
    /// </para>
    /// </summary>
    private Task ShowSettingsAsync()
    {
        if (_settingsOpen) return Task.CompletedTask;
        _settingsOpen = true;

        SyncSettingsControls();

        // ① 重置到起始状态
        //
        // ⚠ 关键：不要在 Overlay 上用 Opacity 做淡入。
        //   如果 Storyboard 因任何原因没能生效（目标无效 / 动画被系统禁用），
        //   Opacity 就会【永久停在 0】—— 表现就是"设置打不开"。
        //   现在遮罩保持完全不透明，动画只作用在卡片的位移与缩放上：
        //   即使动画失败，浮层也是可见的。
        SettingsCardTransform.TranslateY = 24;
        SettingsCardTransform.ScaleX = 0.98;
        SettingsCardTransform.ScaleY = 0.98;
        SettingsOverlay.Opacity = 1;
        SettingsOverlay.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sb = new Storyboard();

        // ② 卡片上浮
        var moveY = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(moveY, SettingsCardTransform);
        Storyboard.SetTargetProperty(moveY, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        sb.Children.Add(moveY);

        // ④ 卡片轻微放大（0.98 → 1）
        var scaleX = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(scaleX, SettingsCardTransform);
        Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(scaleY, SettingsCardTransform);
        Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
        sb.Children.Add(scaleY);

        sb.Begin();

        return Task.CompletedTask;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => HideSettings();

    private void SettingsOverlay_Tapped(object sender, TappedRoutedEventArgs e) => HideSettings();

    /// <summary>关闭设置浮层（带出场动画）。</summary>
    private void HideSettings()
    {
        if (!_settingsOpen) return;
        _settingsOpen = false;

        // 出场同样不用 Opacity（理由见 ShowSettingsAsync）
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>退出程序（带二次确认，避免误点）。</summary>
    private async void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "退出视频展台",
                Content = "确定要退出吗？",
                PrimaryButtonText = "退出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        }
        catch (Exception ex)
        {
            // 确认框本身出错也不能卡住用户，直接退
            Debug.WriteLine($"[VP] 退出确认框失败：{ex.Message}");
        }

        Close();
    }

    /// <summary>恢复默认设置（只重置"可调项"，不动上次设备等记忆值）。</summary>
    private async void ResetAllSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirm = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "恢复默认设置",
                Content = "所有可调设置项将回到出厂默认值，此操作不可撤销。",
                PrimaryButtonText = "恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            var s = Vm.Settings;

            s.StartFullScreen = true;
            s.AutoConnectSingleDevice = true;
            s.AutoDetectDevices = true;
            s.ShowBootTime = true;

            s.PhotoFormat = "PNG";
            s.JpegQuality = 92;
            s.CopyPhotoToClipboard = false;
            s.PhotoFolder = string.Empty;
            s.FileNamePrefix = "展台";
            s.BurstCount = 3;
            s.CaptureSound = false;
            s.OpenFolderAfterCapture = false;

            s.RecordFps = 30;
            s.RecordBitrateMbps = 8;
            s.RecordMaxMinutes = 0;

            s.InkColor = "#FFE53935";
            s.InkThickness = 4;
            s.EraserThickness = 20;
            s.BurnAnnotationsIntoPhoto = true;

            s.HotkeyCaptureEnabled = true;
            s.HotkeyFullScreenEnabled = true;

            s.RightPanelOpenByDefault = true;
            s.ShowGuides = false;
            s.RememberLastDevice = true;

            s.AutoStartWithWindows = false;

            // 立即把副作用同步出去（这些不是绑定能覆盖的）
            ApplyAutoStart(false);
            ApplyInkSettings();
            ApplyGuides();
            SyncSettingsControls();

            Vm.SetStatus("设置已恢复为默认值");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 恢复默认设置失败：{ex}");
        }
    }

    /// <summary>「随 Windows 启动」开关：写 / 删 HKCU 下的 Run 项。</summary>
    private void AutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch sw) ApplyAutoStart(sw.IsOn);
    }

    private void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (key is null) return;

            const string valueName = "VideoPresenter";

            if (enable)
            {
                // 自启动直接拉起主程序本体，跳过启动器（开机时不需要看那 380ms 的进度条）
                string exe = Path.Combine(AppContext.BaseDirectory, "VideoPresenter.exe");
                key.SetValue(valueName, $"\"{exe}\"");
                Vm.SetStatus("已设置随 Windows 启动");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
                Vm.SetStatus("已取消随 Windows 启动");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 设置开机自启失败：{ex.Message}");
            Vm.SetStatus($"设置开机自启失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按设置把状态栏放到 上 / 下 / 左 / 右。
    ///
    /// <para><b>为什么是"改坐标"而不是"搬 XAML"</b></para>
    /// <para>
    /// 所有元素都住在同一个 4 行 × 3 列 的网格里，换位置只需改
    /// Grid.SetRow / SetColumn 与行列尺寸。若真去重构 XAML 树，
    /// 一旦行列配错整个界面会错位 —— 而这里没有实机可测，风险太高。
    /// </para>
    ///
    /// <para><b>侧边状态栏为什么隐藏详细信息</b></para>
    /// <para>
    /// 左右两侧是窄条（限宽 180），设备名 + 分辨率 + 帧率 + 启动耗时
    /// 挤在一行里没法看。所以侧边时只保留主状态文本，并允许换行。
    /// </para>
    /// </summary>
    private void ApplyStatusBarPosition()
    {
        if (RootGrid is null || StatusBar is null || ContentArea is null) return;

        var rows = RootGrid.RowDefinitions;

        // ── ① 先还原成"底部"形态 ──
        StatusBar.Visibility = Visibility.Visible;
        StatusBar.Padding = new Thickness(16, 0, 16, 0);
        StatusBar.MinHeight = 30;
        StatusBar.MaxWidth = double.PositiveInfinity;
        StatusBar.VerticalAlignment = VerticalAlignment.Stretch;
        StatusBar.HorizontalAlignment = HorizontalAlignment.Stretch;

        StatusBarInfo.Visibility = Visibility.Visible;
        StatusBarText.TextWrapping = TextWrapping.NoWrap;

        rows[2].Height = new GridLength(1, GridUnitType.Star);
        rows[3].Height = GridLength.Auto;

        Grid.SetRow(StatusBar, 3);
        Grid.SetColumn(StatusBar, 0);
        Grid.SetColumnSpan(StatusBar, 3);

        Grid.SetRow(ContentArea, 2);
        Grid.SetColumn(ContentArea, 0);
        Grid.SetColumnSpan(ContentArea, 3);

        // ── ② 按设置调整 ──
        switch (Vm.Settings.StatusBarPosition)
        {
            case "Top":
            {
                // 状态栏占第 2 行（Auto），内容让到第 3 行
                rows[2].Height = GridLength.Auto;
                rows[3].Height = new GridLength(1, GridUnitType.Star);

                Grid.SetRow(StatusBar, 2);
                Grid.SetRow(ContentArea, 3);
                break;
            }

            case "Left":
            case "Right":
            {
                bool left = Vm.Settings.StatusBarPosition == "Left";

                // 窄竖条：只保留主状态文本
                StatusBarInfo.Visibility = Visibility.Collapsed;
                StatusBarText.TextWrapping = TextWrapping.Wrap;
                StatusBar.Padding = new Thickness(10, 12, 10, 12);
                StatusBar.MaxWidth = 190;
                StatusBar.MinHeight = 0;

                Grid.SetColumn(StatusBar, left ? 0 : 2);
                Grid.SetColumnSpan(StatusBar, 1);
                Grid.SetRow(StatusBar, 2);
                Grid.SetRowSpan(StatusBar, 1);

                Grid.SetColumn(ContentArea, 1);
                Grid.SetColumnSpan(ContentArea, 1);
                Grid.SetRow(ContentArea, 2);
                break;
            }
        }

        // 侧边 / 底部切换时，复原主体区域的行跨度
        Grid.SetRowSpan(ContentArea, 1);
    }

    /// <summary>显示 / 隐藏三分线辅助格。</summary>
    private void ApplyGuides()
    {
        if (GuidesLayer is null) return;
        GuidesLayer.Visibility = Vm.Settings.ShowGuides ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>状态栏位置下拉：写回设置并立即重排布局。</summary>
    private void StatusBarPos_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusBarPosBox is null) return;

        string pos = StatusBarPosBox.SelectedIndex switch
        {
            1 => "Top",
            2 => "Left",
            3 => "Right",
            _ => "Bottom",
        };

        Vm.Settings.StatusBarPosition = pos;

        // 立即生效（不等下次启动）
        ApplyStatusBarPosition();
    }

    /// <summary>把设置面板里的下拉框同步为当前设置值。</summary>
    private void SyncSettingsControls()
    {
        StatusBarPosBox.SelectedIndex = Vm.Settings.StatusBarPosition switch
        {
            "Top" => 1,
            "Left" => 2,
            "Right" => 3,
            _ => 0,
        };

        RecordFpsBox.SelectedIndex = Vm.Settings.RecordFps switch
        {
            15 => 0,
            60 => 2,
            _ => 1,
        };

        RecordBitrateBox.SelectedIndex = Vm.Settings.RecordBitrateMbps switch
        {
            4 => 0,
            16 => 2,
            24 => 3,
            _ => 1,
        };
    }

    // ───────────────────────── 设置面板事件 ─────────────────────────

    private async void PickPhotoFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");

            // unpackaged 应用必须先把选择器关联到窗口句柄
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                Vm.Settings.PhotoFolder = folder.Path;
                Vm.SetStatus($"素材目录已改为：{folder.Path}");
            }
        }
        catch (Exception ex)
        {
            Vm.SetStatus($"选择文件夹失败：{ex.Message}");
            Debug.WriteLine($"[VP] 选择文件夹失败：{ex}");
        }
    }

    private void ResetPhotoFolder_Click(object sender, RoutedEventArgs e)
    {
        Vm.Settings.PhotoFolder = string.Empty;
        Vm.SetStatus(@"素材目录已恢复为默认（图片\视频展台）");
    }

    private void RecordFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordFpsBox is null) return;

        Vm.Settings.RecordFps = RecordFpsBox.SelectedIndex switch
        {
            0 => 15,
            2 => 60,
            _ => 30,
        };
    }

    private void RecordBitrate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordBitrateBox is null) return;

        Vm.Settings.RecordBitrateMbps = RecordBitrateBox.SelectedIndex switch
        {
            0 => 4,
            2 => 16,
            3 => 24,
            _ => 8,
        };
    }

    private void SettingsInkColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;
        Vm.Settings.InkColor = hex.ToUpperInvariant();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.GetDirectoryName(MediaFoundationCameraService.LogFilePath)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 打开日志目录失败：{ex.Message}");
        }
    }

    private void OpenLicense_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/UNSA-studio/VideoDocumentCamera/blob/main/LICENSE",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 打开许可页失败：{ex.Message}");
        }
    }

    /// <summary>全屏切换：直接使用 AppWindow 的原生呈现器，最稳。</summary>
    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == _isFullScreen) return;

        try
        {
            AppWindow.SetPresenter(fullScreen
                ? AppWindowPresenterKind.FullScreen
                : AppWindowPresenterKind.Default);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 全屏切换失败：{ex.Message}");
            return;
        }

        _isFullScreen = fullScreen;
        Vm.IsFullScreen = fullScreen;

        // 注意：这里【不】自动置顶。
        //   全屏 + 置顶会让用户根本无法切到其他窗口（备课时反而碍事）。
        //   需要钉在最上层时，点工具栏的「置顶」按钮即可。
        //   退出全屏：Esc 或 F11，或点工具栏的「全屏」按钮。
    }

    /// <summary>Esc / F11 —— 切换全屏。</summary>
    private void FullScreenAccelerator_Invoked(KeyboardAccelerator sender,
                                               KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFullScreen(!_isFullScreen);
        args.Handled = true;
    }

    private void CenterOnScreen()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var size = AppWindow.Size;
            var x = area.WorkArea.X + (area.WorkArea.Width - size.Width) / 2;
            var y = area.WorkArea.Y + (area.WorkArea.Height - size.Height) / 2;
            AppWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 窗口居中失败：{ex.Message}");
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowApiService.UnregisterHotKey(_hwnd, WindowApiService.HotKeyCapture);
            WindowApiService.UnregisterHotKey(_hwnd, WindowApiService.HotKeyFullScreen);

            if (_subclassProc is not null)
                Win32.RemoveWindowSubclass(_hwnd, _subclassProc, 1);
        }

        // 停止各类定时器
        _deviceTimer?.Stop();
        _deviceTimer = null;

        // 录制中直接关窗 → 先收尾，保证 MP4 的索引信息被写入
        _recordTimer?.Stop();
        _recorder?.Stop();
        _recorder?.Dispose();

        _camera.FrameArrived -= OnFrameArrived;
        Vm.Dispose();
    }
}