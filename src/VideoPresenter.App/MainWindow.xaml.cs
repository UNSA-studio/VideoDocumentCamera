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

        WindowApiService.RegisterHotKey(_hwnd, WindowApiService.HotKeyCapture,
            Win32.MOD_CONTROL | Win32.MOD_ALT, 0x50 /* P */);
        WindowApiService.RegisterHotKey(_hwnd, WindowApiService.HotKeyFullScreen,
            Win32.MOD_CONTROL | Win32.MOD_ALT, 0x46 /* F */);

        UpdateThemeIndicator();

        // ★ 首帧已经画完 —— 等一个低优先级回合。
        //   顺序很重要：先全屏，再报告就绪。
        //   这样启动器的 Splash 消失时，用户看到的就是已经全屏的界面，
        //   而不是先看到一个 1180×760 的小窗再"啪"地跳成全屏。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            SetFullScreen(true);
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

    /// <summary>进入 / 退出批注模式。</summary>
    private void SetAnnotationMode(bool on)
    {
        AnnotationCanvas.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        AnnotationToolbar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (!on) EndStroke(null);
    }

    private void AnnotationCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(AnnotationCanvas);
        _activePointerId = pt.PointerId;

        // ── 橡皮擦：点中哪一笔就删哪一笔 ──
        if (_eraserMode)
        {
            for (int i = _annotations.Count - 1; i >= 0; i--)
            {
                if (HitTestStroke(_annotations[i], pt.Position))
                {
                    AnnotationCanvas.Children.Remove(_annotations[i]);
                    _annotations.RemoveAt(i);
                    break;
                }
            }
            return;
        }

        // 只响应主键：鼠标需要左键按下；触摸 / 笔尖天然是按下的
        if (pt.PointerDevice.PointerDeviceType == PointerDeviceType.Mouse &&
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
        if (_currentStroke is null) return;

        var pt = e.GetCurrentPoint(AnnotationCanvas);
        if (pt.PointerId != _activePointerId) return;

        // 鼠标可能在按键松开后仍然送来 Moved，需要主动收尾
        if (pt.PointerDevice.PointerDeviceType == PointerDeviceType.Mouse &&
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

    // ══════════════════════ 旋转 ══════════════════════

    /// <summary>顺时针旋转 90°（0° → 90° → 180° → 270° 循环）。</summary>
    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        _rotationAngle = (_rotationAngle + 90) % 360;
        PreviewRotate.Angle = _rotationAngle;

        Vm.SetStatus(_rotationAngle == 0
            ? "画面方向：正常"
            : $"画面方向：已旋转 {_rotationAngle:0}°");

        // ⚠ 已知限制：WinUI 没有 WPF 的 LayoutTransform，RenderTransform 不参与布局测量，
        //   所以旋转 90° / 270° 时画面不会自动交换宽高 —— 可能出现留白。
        //   后续可用「按旋转后的宽高比重算 Stretch」来消除，属于细调项。
    }

    // ══════════════════════ 设置面板 ══════════════════════

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // ContentDialog 在 WinUI 3 中必须挂到 XamlRoot 上
            SettingsDialog.XamlRoot = RootGrid.XamlRoot;
            await SettingsDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VP] 打开设置失败：{ex.Message}");
        }
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

        _camera.FrameArrived -= OnFrameArrived;
        Vm.Dispose();
    }
}