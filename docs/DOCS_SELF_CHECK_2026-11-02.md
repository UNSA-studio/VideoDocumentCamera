# Microsoft Docs 合同核查（2026-11-02 起，非实机验收报告）

## 本轮不是“读过几篇文章就宣布修复”

上一轮遗漏了同一条异步规则在不同原生操作上的应用。审查按“官方约束 → 实际调用路径 → 故障分支 → 验证方式”记录。列为已修改的项目仍须区分代码改动、自动测试与硬件验收。未关闭的严重项写入 `build/release-gate.json`，CI 仍构建内部产物，但不得覆盖公开 dev/ 或发布 Release；本轮不替换 VPS 旧包。产品版本不变。

## 约束、证据和处理

| 项目 | 上版代码中的具体问题 | 本轮处理／剩余工作 |
|---|---|---|
| WaitAsync 只限制等待 | 只有初始化使用延后释放；SetFormat 超时仍继续读格式；CreateFrameReader 超时丢失晚到结果；Start/Stop 超时仍可能 Dispose | 引入 NativeOperationGuard 和独立 CameraSession，所有原生操作纳入所有权；退休会话等待原生任务结束及帧回调退出才释放；清理未完成禁止重连。11 项可控任务检查，不等于 WinRT 驱动实测 |
| 取消后的原生调用 | CancellationToken 取消等待，但原生调用可能继续 | 不假装已经取消设备操作；会话保留资源，日志记录 CLEANUP_DEFERRED。关闭进程仍可能使异步清理无法完成，不能承诺解决阻塞在原生 API 调用本身的驱动问题 |
| 回调准入与停止竞态 | 回调检查当前 reader 后，尚未置 busy；停止方可能认为没有回调而释放 reader | 准入与失效使用同一把 frameGate 锁；停机后不准进入新回调，已进入的回调须退出后才能释放 |
| 晚到的 Failed 事件 | 没有核对 sender，旧采集器事件可能改变新会话状态 | 只接受当前会话 Capture 的 Failed，启动置 running 前在同一锁下检查失败状态 |
| 规范化后的后续异常 | _formatLogged 一旦 true，后续转换／通知异常永远不写日志 | 独立错误限流，每五秒至多记录一次；不再以“格式已打过日志”为理由吞异常 |
| 首帧超时与原生超时 | 两种超时不能混为一谈 | 首帧等待超时可在 reader 确认停止后试下一输出子类型；原生调用超时必须退役会话，不能重叠试下一个 |
| 无效尺寸、帧率 | 没有拒绝零尺寸、零帧率；低于25fps时先选最大分辨率，可能选4fps而不选20fps | 明确尺寸/帧率下限；低帧率集合按帧率优先。但仍非穷举所有源/所有输入格式，也不是 CPU/GPU 能力检测器 |
| 拍照／连拍结果 | 从状态字符串或旧素材推断成功；连拍即便失败也显示目标张数 | 返回实际新文件路径；连拍统计真实成功数 |
| 照片文件发布 | ReplaceExisting 会覆盖同毫秒同名文件；编码失败留半成品 | 使用唯一 pending 文件，编码并关闭流后 RenameAsync GenerateUniqueName，后续使用实际文件路径；失败清理 pending 文件 |
| 设置异常分支 | Save 的 finally 删除临时文件仍可能抛出未处理异常，尤其在线程池 Timer 上 | 删除失败另行捕获并写日志，不让清理异常绕过保存异常处理 |
| MF GUID | RecordingService.MF_MT_MAJOR_TYPE 与 SDK 不一致 | 按官方 mfapi.h 改为 48eba18e-f8c9-4687-bf11-0a74c9f96a8f；27处声明离线校验，附微软源头 URL 与文件 SHA256；核查器接入 CI |
| MF HRESULT | CreateVideoType 忽略 SetGUID/SetUINT32/SetUINT64 的 HRESULT | 显式检查 HRESULT；失败释放创建的媒体类型。不能由此推断所有 ComImport 调用失效 |
| 录像 MF 初始化（阻断发布） | MFStartup 只有声明，没有主动调用；不能依赖 WinRT 采集内部如何初始化 MF | REC-001，待明确 COM/MF 工作线程所有权、Startup/Shutdown 配对 |
| 录像停机（阻断发布） | Join(5000) 不检查返回值，随后 Finalize / Release 可能与工作线程 WriteSample 重叠；普通停止还在 UI 调用 | REC-002，待改成工作线程串行完成写入、Finalize、释放，UI 异步等待，不在 Join 超时后强制释放 |
| 录像时钟（阻断发布） | _frameIndex 只在成功写帧后递增，掉帧时压缩时间轴 | REC-003，待使用单调采集时间线并验证录像时长、丢帧和可播放性 |
| COM 派生接口布局 | GUID 对不等于 vtable 对；录像仍用手工接口声明 | REC-004，尚未验证，不凭印象断言正确/错误，更不再用猜 vtable 的方式改录像 |
| 日志归档并发 | 目前哈希核对之后再 Delete，尚有核对/删除间源文件变化的窗口 | 待做原件原子认领/恢复与跨进程竞态测试；现有12项归档日志测试不覆盖该窗口 |
| Users 路径边界 | 字符串前缀校验不等于 NTFS 重解析点安全隔离；数据树组权限不是用户间隔离 | 仍需 Windows ACL、链接和迁移专项测试；不宣称所有数据路径安全问题已消除 |

## 自动检查分别证明什么

1. `Diagnostics.Tests`：原有12项日志/归档断言，加11项 NativeOperationGuard 断言。后者控制一个不会自动完成的任务，验证超时不取消原任务、不重叠调用工厂、清理等待、晚到失败被观察、晚到资源被释放、退休会话不可复用。不是完整相机状态机或真实驱动测试。
2. `verify-mf-contracts.py`：从微软四份 SDK 头文件提取常量和 IID，保存原始来源与哈希，正常 CI 离线核查27处声明。只校验标识符，不证明 COM 方法布局、参数封送或运行正常。
3. `Windows.Contracts.Tests`：在 Windows 运行真实 SoftwareBitmap、CopyFromBuffer/CopyToBuffer、PNG/JPEG 编解码、预乘 Alpha 和 StorageFile RenameAsync。同名发布测试必须保留旧文件。不是 WinUI Image 上屏测试，不是摄像头测试。
4. 主程序构建和安装包构建：编译／打包检查。不能替代以上运行检查，更不能替代两台真实设备验收。

## 本轮直接核对的微软原始资料

- Task.WaitAsync：等待任务何时完成、超时和取消的合同。
  https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync
- MediaCapture.CreateFrameReaderAsync：各重载、帧内存池与复制行为、输出子类型/尺寸参数。
  https://learn.microsoft.com/en-us/uwp/api/windows.media.capture.mediacapture.createframereaderasync
- Process media frames with MediaFrameReader：帧所有权、UI派发、初始化/格式/停止与释放顺序。
  https://learn.microsoft.com/en-us/windows/apps/develop/camera/process-media-frames-with-mediaframereader
- MFCreateSinkWriterFromURL：调用前 CoInitialize(Ex)、MFStartup 的要求。
  https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-mfcreatesinkwriterfromurl
- IMFSinkWriter.Finalize：输入样本全部送完后调用；未配置异步回调时会阻塞；之后 WriteSample 会失败。
  https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsinkwriter-finalize
- 微软 Windows SDK 重编译头文件 mfapi.h / mfobjects.h / mfreadwrite.h / mfidl.h，地址和 SHA256 见 tests/Diagnostics.Tests/microsoft-sdk-identifiers.json。

未覆盖完成：WinUI 动画和触屏交互自动化、安装/卸载 ACL 真机检查、完整录制回归、设备插拔/占用/权限和实际颜色/帧率验收。不得将本文件标题解释为“整个应用已无缺陷”。