# DaV 保留运行状态确认（2026-11-10）

按用户指示：**不发布新版本，但保持 DaV（视频展台 vdc 当前装机版）正常运行**。

## 采取的措施（全部不动公开包，只保可用性）

1. **公开安装包未替换**
   - VPS 160.202.237.166 的 8090 分发（vdc-http 服务）仍指向 2026-11-02 验证过的旧包
     `vdc-1.0.0-x64-setup.exe`（52,498,364 字节，MD5 a2fca84832487e1a1ef79f41f56a8612），实测 HTTP 200 可下载
   - `/var/www/music/vdc/` 与 `/srv/dl/vdc/` 两处文件一致，vdc-http 服务 active
   - 回滚包 `vdc-1.0.0-x64-setup.prev.exe` 原样保留

2. **CI 产物不进入公开渠道**
   - run 42758097861 的安装包/便携版仅存在 GitHub Actions Artifacts（需登录才能下载）
   - 发布门禁 `Public distribution gate` 因 release-gate.json `allowPublicDistribution: false` 仍生效
     →「发布 Release」与「发布产物到仓库 dev/」步骤被跳过，公开 dev/ 目录没有新包
   - raw.githubusercontent.com 上 dev/vdc-1.0.0-x64-setup.exe 返回 404（dev 目录未写入），符合预期

3. **主分支代码未回退**
   - 提交链：ccaa3be（发布门禁 + 契约核查 + Windows 运行时测试）→ 2e380fc（编码修复）
   - 这些改动只影响 CI 与源码，不改变已安装机器上的行为；已装机版本功能不受影响

## 未验证项（保持诚实标注，与 docs/DOCS_SELF_CHECK_2026-11-02.md 一致）

- 录像链路 REC-001~004 未修复：MFStartup 未主动调用、Stop 竞态、时间轴压缩、COM vtable 布局
  → **录像功能不可作为可靠功能宣传**
- 两台真实摄像头（F3-B2 CAM / SC06）的出画面、绿屏、选设备卡死、1080p 帧率验收仍待实机
- 拍照、批注、OCR、对比等已实机验证过的功能不受本轮改动影响

## 后续建议

- REC-001（MFStartup）/ REC-002（Stop 竞态）风险最高，建议下一轮优先修复，修完后由用户实机验收录像
- 契约检查与 Windows 运行时测试已在 CI 稳定运行，后续改动会自动拦截标识符/编码回归
