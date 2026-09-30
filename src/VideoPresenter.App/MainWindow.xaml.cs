using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VideoPresenter.App.Services;
using VideoPresenter.App.ViewModels;
using Windows.Graphics;
using Windows.Graphics.Imaging;

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
                // 说明：ColumnDefinition 不是 FrameworkElement，
            //   因此它的 Width 不能走 x:Bind（XAML 编译器会拒绝），
            //   这里由代码直接联动，最稳。
                if (RightPanelColumn is not null)
                    RightPanelColumn.Width = Vm.RightPanelWidth;
                break;
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