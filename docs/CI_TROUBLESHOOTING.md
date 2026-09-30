# CI 构建踩坑记录

> 本文记录首次跑通 GitHub Actions 云构建时遇到的全部问题（2026-09-29）。
> 每一条都是**实际报错 →定位过程 →修复方式**，不是理论推测。
> 保留它的意义：这些坑在 Windows 上"本地随便编译一下"往往不会遇到，
> 但在干净的 CI 环境里会一个接一个地暴露出来。

总计 14 个问题，跨 C++ / WinUI 3 / WiX / Inno Setup / CI 诊断五个层面。

---

## 一张速查表

| # | 层面 | 症状 | 根因 | 修复 |
| --- | --- | --- | --- | --- |
| 1 | C++ | `'swprintf_s': identifier not found` | 头文件在 `#include "VpIpc.h"` 之后才包含 | 让 `VpIpc.h` 自带 `<stdio.h>` / `<wchar.h>` |
| 2 | C++ / rc | `resource.h(5): fatal error RC1004` | `.rc` / `.h` 未以换行结尾 | 补末尾换行 + CI 兜底规范化 |
| 3 | C++ / rc | 同上报错（误判为编码问题） | rc.exe 按系统 ANSI 代码页解析 UTF-8 中文 | 加 `/c65001`（真正的病根是 #2） |
| 4 | C# | `No overload for method 'Copy' takes 2 arguments` | `SoftwareBitmap.Copy` 是**静态**方法 `Copy(source)` | 改为 `SoftwareBitmap.Copy(bitmap)` |
| 5 | XAML | `Cannot convert 'int' to 'string'` | `x:Bind` 是编译期强类型绑定，不做隐式转换 | 暴露 `SnapshotCountText` 字符串属性 |
| 6 | XAML | `x:Bind` 无法用在 ColumnDefinition | 它要求目标是 `FrameworkElement` | 列宽改由 code-behind 联动 |
| 7 | WinUI 3 | `MSB4062: ...Pri.Tasks.ExpandPriContent could not be loaded` | runner 预装 .NET 10 SDK，dotnet 默认用它；WinAppSDK 1.6 的 PRI 任务只在 v17.0 路径下 | 新增 `global.json` 锁定 SDK 8.0.x |
| 8 | WiX | `ComponentGroup contains an unexpected child element 'Files'` | WiX 4.0.4 的 `<ComponentGroup>` 不支持 `<Files>` 收割 | 自己扫描生成 fragment（等价 heat.exe） |
| 9 | WiX | `WIX0089: Multiple entry sections` | WixToolset.Sdk 已自动包含 `*.wxs`，我又显式列了一遍 | 删掉显式 `<Compile>` |
| 10 | WiX | `WIX0369` GUID 重复 / `WIX0204 ICE30` 文件冲突 | `Guid="*"` 对同名文件算出同一 GUID；且生成器把文件全挂在根目录，子目录结构丢失 | 基于相对路径算确定性 GUID + 还原完整目录树 |
| 11 | Inno | `Parsing [Setup] section, line 42` / `Couldn't open include file ...ChineseSimplified.isl` | `AppId={{GUID}` 花括号歧义；官方包不含中文语言文件 | AppId 用纯 GUID；用 ISPP `FileExists` 条件判断 |
| 12 | WiX | `WIX0311: ... not available in the specified database code page '1252'` | MSI 数据库默认代码页是 1252（Latin-1），装不下中文 | `<Package Codepage="936">` |
| 13 | WiX | `WIX0091: Duplicate symbol 'Property:ARPNOMODIFY'` | `WixUI_InstallDir` 的 wixlib 已声明该属性 | 删掉应用侧重复定义 |
| 14 | Inno | 许可协议中文变乱码 | 无 BOM 的 UTF-8 文本被按系统 ANSI（1252）解析 | 改用 `\uN?` 转义 + `\ansicpg936` 的 RTF |

---

## 1. `swprintf_s` 未声明

```
src/VideoPresenter.Launcher/VpIpc.h(73): error C3861: 'swprintf_s': identifier not found
```

**根因**：`VpIpc.h` 里用了 `swprintf_s`，但只在 `main.cpp` 里包含了 `<cstdio>`，
而那一行写在 `#include "VpIpc.h"` **之后**。编译器是单遍的。

**修复**：让头文件自带依赖 —— 这是头文件的基本纪律，不能指望使用方的包含顺序。

```cpp
#include <windows.h>
#include <cstdint>
#include <stdio.h>   // swprintf_s
#include <wchar.h>   // wcscpy_s / wcscat_s / wcsrchr / wcslen
```

---

## 2. `RC1004: unexpected end of file found`（真正的坑）

```
resource.h(5): fatal error RC1004: unexpected end of file found
```

**排查过程**：先怀疑编码（见 #3），加了 `/c65001` —— **没用**。
关键线索是**行号指向文件最后一行**：这是"文件没有以换行符结束"的典型特征。

`rc.exe` 的预处理器要求 `.rc` / `.h` 以换行结尾，否则最后一条预处理指令
没有终止符，会一路读到物理 EOF。

**修复**（双管齐下）：

1. 仓库内给 `resource.h` / `Launcher.rc` 补上末尾换行；
2. CI 里加一步"规范化"，防止以后新增文件再踩：

```powershell
if (-not $t.EndsWith("`r`n")) { $t += "`r`n" }
```

同时在 `.gitattributes` 里把 `*.rc` / `*.h` / `*.cpp` 设为 `eol=crlf`
（MSVC 工具链在 Windows 上按 CRLF 处理最稳）。

---

## 3. rc.exe 与 UTF-8 中文

`Launcher.rc` 里有中文（产品名、版本信息）。`rc.exe` 默认按**系统 ANSI 代码页**
（中文 Windows 是 GBK）解析源码，遇到 UTF-8 多字节字符会字节错位。

**修复**：在 vcxproj 里给资源编译指定代码页：

```xml
<ResourceCompile>
  <AdditionalOptions>/c65001 %(AdditionalOptions)</AdditionalOptions>
</ResourceCompile>
```

> 注：这一条单独并不足以修好 #2，但 **仍然应该保留** ——
> 没有它，`Launcher.rc` 里的中文在非中文区域设置的机器上会变乱码。

---

## 4. `SoftwareBitmap.Copy` 是静态方法

```
error CS1501: No overload for method 'Copy' takes 2 arguments
error CS7036: There is no argument given that corresponds to the required parameter
             'source' of 'SoftwareBitmap.Copy(SoftwareBitmap)'
```

WinRT 的 `SoftwareBitmap`：

| 想要的效果 | 正确写法 |
| --- | --- |
| 同格式复制 | `SoftwareBitmap.Copy(source)` ← **静态** |
| 换格式复制 | `SoftwareBitmap.Convert(source, format, alpha)` ← 静态 |
| ~~`bitmap.Copy(...)`~~ | ❌ 不存在这样的实例方法 |

---

## 5. `x:Bind` 不会替你 `ToString()`

```xml
<!-- ❌ 编译失败：int 不能赋给 TextBlock.Text -->
<TextBlock Text="{x:Bind Vm.SnapshotCount}"/>

<!-- ✅ -->
<TextBlock Text="{x:Bind Vm.SnapshotCountText}"/>
```

WPF 的 `Binding` 会在运行时调用 `ToString()`，**`x:Bind` 不会** ——
它是编译期生成强类型代码，类型不匹配直接编译失败。

**约定**：ViewModel 里凡是绑到 `Text` 的数值，都额外暴露一个 `string` 属性。

---

## 6. `x:Bind` 只能用于 `FrameworkElement`

```xml
<!-- ❌ ColumnDefinition 不是 FrameworkElement -->
<ColumnDefinition Width="{x:Bind Vm.RightPanelWidth, Mode=OneWay}"/>
```

**修复**：给列起名，在 code-behind 里联动：

```csharp
case nameof(MainViewModel.IsRightPanelOpen):
    RightPanelColumn.Width = Vm.RightPanelWidth;
    break;
```

---

## 7. `MSB4062`：PRI 任务加载失败（最隐蔽的一个）

```
error MSB4062: The "Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent" task
could not be loaded from the assembly
C:\Program Files\dotnet\sdk\10.0.401\Microsoft\VisualStudio\v18.0\AppxPackage\...dll
```

**根因**：GitHub runner 预装了 **.NET 10 SDK**，`dotnet` CLI 默认选用**最新**的
10.0.401；而 Windows App SDK 1.6 的 PRI 生成任务只在 **v17.0** 路径下存在。

**修复**：新增 `global.json` 锁定 SDK：

```json
{
  "sdk": {
    "version": "8.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

> **教训**：任何依赖 Windows App SDK / WiX / 特定 MSBuild 行为的项目，
> 都应该在仓库里放 `global.json`。否则 CI 上的 SDK 一升级就可能突然挂掉。

---

## 8. WiX：`<Files>` 收割元素不被支持

```
error WIX0106: The ComponentGroup element contains an unexpected child element 'Files'.
```

WiX 4.0.4 的 `<ComponentGroup>` 不接受 `<Files>`。

**修复**：自己扫描发布目录生成标准 fragment —— 等价于 `heat.exe`，
但行为完全可控、不受 WiX 版本差异影响。生成逻辑在 `build/build.ps1` 的 MSI 阶段。

---

## 9. WiX：重复编译导致两个 entry section

```
error WIX0089: Multiple entry sections '*' and '*' found.
error WIX0091: Duplicate symbol 'Property:ProductCode'
```

**根因**：`WixToolset.Sdk` **自动包含项目目录下的所有 `.wxs`**，
而我又在 `.wixproj` 里显式写了一遍 `<Compile Include="Product.wxs"/>`
→ `Product.wxs` 被编译两次 → 两个 `<Package>`。

**修复**：删掉显式 `<Compile>`，依靠 SDK 默认 glob
（生成的 `GeneratedFiles.wxs` 同样会被自动包含）。

---

## 10. WiX：组件 GUID / ICE30

```
error WIX0369: Component/@Id='cmp0314' ... has a @Guid value '{1298F6A0-...}'
               that duplicates another component
error WIX0204: ICE30: The target file 'xxx.mui' is installed in '...'
               by two different components
```

**两个独立问题**：

1. **GUID 重复**：`Guid="*"` 让 WiX 对「不同目录下的同名文件」
   （各语言目录的 `Microsoft.UI.Xaml.Phone.dll.mui`）算出同一个 GUID。
   → 改为**基于相对路径的 MD5 确定性 GUID**（稳定 + 唯一）。

2. **ICE30 文件冲突**（更严重）：最初的生成器把**所有文件都挂在
   `INSTALLFOLDER` 根下**，子目录结构完全丢失 ——
   于是各语言目录的同名 `.mui` 撞在同一目标路径上；
   而且**即使编译通过，安装后目录也会被全部塌平**。
   → 重写生成器，构建目录树（`dirIds` / `childMap`），
   输出 `<DirectoryRef>` 嵌套节点，每个 `Component` 指向正确的 `Directory Id`。

> 这条如果只靠"编译通过"来判断，会留下一个**运行时才暴露的严重缺陷**。

---

## 11. Inno Setup：AppId 与中文语言文件

```
Parsing [Setup] section, line 42            ← AppId 行
Error on line 92: Couldn't open include file
      "C:\Program Files (x86)\Inno Setup 6\Languages\ChineseSimplified.isl"
```

**问题 A —— AppId 花括号**：Inno 里 `{` 是常量起始符、`{{` 表示字面 `{`，
`{{GUID}` 在部分版本上解析有歧义。
→ `AppId` 允许任意字符串，**用纯 GUID 文本最稳**。

**问题 B —— 中文语言文件**：Inno Setup 官方安装包**不包含**
`ChineseSimplified.isl`（由社区维护，不随官方发行版附带）。
→ 用 ISPP 条件判断，两端都不报错：

```
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
```

CI 里再加一步 `continue-on-error: true` 的下载，成功就能提供中文向导。

---

## 12. `WIX0311`：MSI 数据库代码页装不下中文

```
error WIX0311: A string was provided with characters that are not available
in the specified database code page '1252'
```

MSI 数据库默认使用 **1252（Latin-1）** 代码页，任何中文都会触发这个错误 ——
而且它是**批量报错**（每个含中文的属性各报一次），看起来像是文件被写坏了。

**修复**：在 `<Package>` 上显式声明语言与代码页：

```xml
<Package Name="视频展台"
         Language="2052"
         Codepage="936">
```

`2052` 是简体中文的语言 ID，`936` 是 GBK 代码页 —— 两者配套使用。

---

## 13. `WIX0091`：与 WiX UI 库重复定义属性

```
error WIX0091: Duplicate symbol 'Property:ARPNOMODIFY' found.
```

引入 `WixUI_InstallDir` 之后，UI 库的 wixlib 里**已经声明了 `ARPNOMODIFY`**，
应用侧如果自己也写一遍就冲突。删掉自己的那份即可。

> **规律**：`WixUI_*` 系列会自带一批标准 ARP 属性（`ARPNOMODIFY`、
> `ARPNOREPAIR` 等）。引入 UI 扩展后，应该回头检查自己定义过哪些 ——
> 保留业务特有的（`ARPPRODUCTICON` / `ARPHELPLINK` / `ARPCOMMENTS`），
> 删掉与库重复的。

---

## 14. 中文许可协议在安装向导里变乱码

`LicenseFile` 指向一个**无 BOM 的 UTF-8** 文本文件时，Inno Setup 会按
**系统 ANSI 代码页**解析 —— 本地中文 Windows（936）看着正常，
但 CI runner 是英文 Windows（**1252**），于是中文全变乱码。

**修复**：改用 RTF。中文以 `\u<码点>?` 转义、并声明 `\ansicpg936`，
任何代码页的系统都能正确显示。RTF 由 `build.ps1` 从 `EULA.txt` 自动转换：

```powershell
LicenseFile=..\EULA.rtf
```

代价是多了一个"文本 → RTF"的转换步骤，但换来：

* MSI 与 EXE 两个安装包**共用同一份许可协议源文件**（`installer/EULA.txt`）；
* 改协议只需改 `.txt`，不必碰 RTF 的转义；
* 跨区域设置（英文 / 日文 / 中文系统）都不会乱码。

---

## 附：CI 诊断方法本身

调试这 14 个问题的过程中，最有价值的经验是**如何让 CI 把话说清楚**：

### ① GitHub 的失败注解只能捕获"结构化输出"

| 能自动变成注解 | 不能 |
| --- | --- |
| MSBuild / C# 编译器（有 problem matcher） | `ISCC.exe` 等普通控制台程序 |
| MSVC 编译错误 | PowerShell 自己 `throw` 的消息 |

**对策**：在 build.ps1 顶部加 `trap`，把所有终止性错误手动转成注解：

```powershell
trap {
    Write-Host ('::error::[build.ps1] ' + ($_.Exception.Message -replace "`r?`n", ' | '))
    Write-Host ('::error::[位置] 行 ' + $_.InvocationInfo.ScriptLineNumber)
    exit 1
}
```

> 这一条是**整个调试过程的转折点** —— 在它之前，我们只能看到
> "Process completed with exit code 1"，完全无法定位。

### ② 把 workflow 拆成小 step

"哪一步红了"本身就是最精确的信息。现在拆成：
`① 启动器 → ② 主程序 → ③ MSI → ④ EXE`，失败点一目了然。

### ③ 注解有数量与长度上限

- 每个 job 约 **10 条**注解；
- **单条注解会被截断**（长消息只显示开头）。

**对策**：
- 不要"只取最后 N 行"，那可能把真正的错误挤掉；
- 必要时**分块成多条注解**输出（见 `build.ps1` 的 ISCC 处理）。

### ④ 失败时的产物快照

workflow 里保留了一个 `if: failure()` 的诊断步骤，
列出 `dist/` 与 `build/out/` 的产物，用来判断"构建进行到了哪一步"。

---

## 最终结果

```
JOB: 构建 Windows 安装包（MSI + EXE） -> success
  ①  编译启动器（原生 Win32 C++）        ✅
  ②  发布主程序（WinUI 3 自包含）         ✅
  ③  打包 MSI（WiX v4）                 ✅
  ④  打包 EXE（Inno Setup 6）           ✅
  上传 视频展台-安装包（108 MB）          ✅
  上传 视频展台-便携版（74.8 MB）         ✅
```