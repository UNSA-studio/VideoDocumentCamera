# Assets

本目录存放「视频展台」的图标资源。**图标已入库，克隆后即可直接编译。**

## 文件清单

| 文件 | 尺寸 | 用途 |
| --- | --- | --- |
| `VideoPresenter.ico` | 16 / 24 / 32 / 48 / 64 / 128 / 256 | 主程序 exe 图标、任务栏、快捷方式、控制面板 |
| `AppIcon.png` | 64×64 | 窗口标题栏左上角图标 |
| `AppIcon44.png` | 44×44 | 备用（高 DPI 标题栏 / 关于对话框） |

> ⚠ 启动器（C++ 工程）引用的是 `src/VideoPresenter.Launcher/Assets/VideoPresenter.ico`，
> 两个 `.ico` 内容一致，修改时请保持同步。

## 设计

* 底：Win11 强调色对角渐变 `#0067C0 → #4CC2FF`，圆角 22%
* 前：白色摄像机剪影（机身 + 取景条 + 镜头）
* 3×3 超采样抗锯齿，带完整 alpha 通道

## 替换 / 重新生成

**方式一（推荐）**：用设计稿直接覆盖本目录的 `.ico` 与 `.png`。

**方式二**：按品牌色重新生成

```powershell
# Windows
./build/tools/make-icon.ps1 -Force
```

```bash
# 非 Windows / CI
python build/tools/make_icon.py
```

两个脚本都会同时写入主程序与启动器的 Assets 目录。

## 被谁引用

* `VideoPresenter.App.csproj` → `ApplicationIcon`（exe 图标）+ `Content`（运行时资源）
* `MainWindow.xaml` → `ms-appx:///Assets/AppIcon.png`
* `src/VideoPresenter.Launcher/Launcher.rc` → `IDI_APP`
* `installer/wix/Product.wxs` → 快捷方式、`ARPPRODUCTICON`
* `installer/exe/setup.iss` → `SetupIconFile`、`UninstallDisplayIcon`