# LAN_DISK_12_REAL_DEVICE_REPAIR_01 — 断点（1.7.7 未完成）

保存：2026-09-30　项目：`D:\LanDisk`

## 0. 事实纠正（本条最重要的部分）

Owner 真机确认：**正式安装实例一切正常**——`LanEnabled=True`、`HttpsEnabled=True`、
监听含 `https://192.168.2.2:8892`、身份 `<install-id>`、iPhone 已能通过 `https://192.168.2.2:8892/` 进入。

因此**撤销**此前关于"升级把 LAN/HTTPS 重置为 false""LAN listener 失效""正式身份丢失"的全部推断：
- `cb5be599` / 端口 `53819` / `53820` 来自**我上一轮自动化验收遗留的测试实例**
  （`dist/evidence-11/run-11-1.7.6-*/07-browser/server/.../LanDisk.exe`），**不是**正式安装身份。
- 不新增"强制开启 LAN/HTTPS"的迁移；不改正常 listener 绑定规则；不重置 Root CA；不改 stable identity。

**当前进程实况（本条核查）**：仅剩 Owner 正式实例 `PID 7772`
（`<user>\AppData\Local\Programs\LanDisk\LanDisk.exe`）；**遗留测试进程已不存在**。
未对该实例做任何操作。

## 1. 缺陷 A / B：已在 `28661e4` 修复，待补测试

- **A**（`Pages.cs` 用显示文案 `StartsWith("使用")` 反推数据，而 Presenter 输出「用于手机访问」→
  物理网络卡片永远显示"没有可用物理网卡"）：已改为 `AdapterRow.接受` 结构化字段，由 `IsPhysical` 直接赋值。
- **B**（`RecommendedHttpsOrigin()` 只看网卡、不看是否在服务 → LAN/HTTPS 关闭时仍推荐不可达物理地址）：
  已加 `servingLanHttps = LanEnabled && HttpsEnabled` 门槛；不满足返回 `null` + kind=`off`。

**复核结论**：实现方向与本文要求一致，无需重写。**R1–R5 尚未新增**。

## 2. 缺陷 C：权限链已查到模型层（结论会改变"该怎么修"）

链路已逐段核对，**内部是自洽的**：

| 环节 | 位置 | 结论 |
| --- | --- | --- |
| 手机菜单渲染 | `Sheets.tsx:226` | `canDelete` 为真才渲染「删除（移入回收站）」✓ |
| 列表能力 | `FileEndpoints.cs:108-114` | `canDelete = LanDiskRuntime.Allows(share, level, Delete)` |
| 我的权限 | `AuthEndpoints.cs:287-298` | **同一个** `EffectiveCapabilities(share, level)` ✓ 两处不会打架 |
| 有效能力 | `LanDiskRuntime.cs:365-377` | `effective = share.Capabilities`；**仅当设备为 `ReadOnly` 时**收窄为 `Read`；share 未启用则 None |

**关键结论：删除权限是「按共享（share）」授权的，不是「按设备」。**
设备级只有 `DeviceAccessLevel`（ReadOnly / ReadWrite，对应 `Permissions.writeFiles`）——
`types.ts:91-99` 的 `Permissions` 里**没有任何 delete 字段**。

所以：Owner 在 Windows 端把**设备**改成读写，只会让该设备的 `level=ReadWrite`；
`canDelete` 仍取决于**该共享自身**是否启用了「允许删除」（Windows 的共享权限开关，`Pages.cs:272`）。
**这不是前端漏了按钮，也不是 capability 缓存问题**——`canDelete=false` 是当前模型下的正确结果。

**因此"最小修复"不是加按钮**，而是二选一（属产品决策，需 Owner 定夺）：
1. 保持模型，改进 Windows 端**措辞/入口**，让"删除要按共享开"一眼可见（仅文案与布局，不动协议）；
2. 把删除改为设备级能力（协议与两端口径都要改，范围明显更大）。

**在 Owner 明确之前不擅自改语义。** R6–R11 尚未新增。

## 3. 缺陷 D / E / F：**尚未调查**

- **D 手机主题不同步**：未查。需从 Windows theme setting → 持久化 → API → mobile bootstrap →
  `themeRevision`（`types.ts:126` 已存在）→ CSS 变量 逐层定位。
- **E 取消下载后传输永远"进行中"**：未查。需查 `RequestAborted` / `CancellationToken` →
  流式响应 → transfer tracker 终态；并覆盖 0 字节文件、连接中断、刷新后保持。
- **F 测试进程残留**：现象当前不存在（见第 0 节），但**根因未修**——
  自动化启动的 LanDisk 测试进程仍缺"成功/失败/超时/异常"四路统一清理与"suite 结束无 owned 进程"检查，
  aggregator 也尚未在发现自有进程存活时拒绝 READY。R22–R26 尚未新增。

## 4. 未完成清单（不得声称完成）

| 项 | 状态 |
| --- | --- |
| D/E 根因定位 | 未开始 |
| F 生命周期修复 | 未开始 |
| R1–R26 定向回归 | 全部未新增 |
| 全量回归（1.7.6 为 723 项） | 未跑 |
| 1.7.7 升版 / 四产物 / 完整验收 / Full / 隐私 | 未做 |
| 精简包 `LAN_DISK_12_REAL_DEVICE_REPAIR_SMALL_REVIEW_01.zip` | 未生成 |
| `REAL_DEVICE_FALSE_LEADS_CORRECTION.md` 等 12 份材料 | 未撰写（第 0 节已备好要点） |

## 5. 续跑顺序

1. 先修 **F**（最便宜、且它正是上轮误诊的成因）：统一 owned-process 生命周期 +
   suite 结束残留检查 + aggregator 拒绝 READY；R22–R26。
2. 再查 **D**、**E**（各自先定位层次，再最小修，禁止扩大协议）。
3. 补 **R1–R5**（A/B）与 **R6–R11**（C，视 Owner 对第 2 节二选一的决定）。
4. WiFi：**FROZEN / OUT_OF_SCOPE**，不进 gate，不伪造 PASS，不因 WiFi 失败阻塞 1.7.7。
5. 定向全绿 → 全量回归（保留首败与 rerun，区分 FACT/HYPOTHESIS/FIX/RETEST）→
   升 1.7.7 → 四产物 → 验收 → Full → 隐私 → 精简包（含 12 份材料）。
6. Authenticode 若仍未配置，如实写 `NOT_CONFIGURED`。

## 6. 边界（本条遵守）

未操作 Owner 的网络 / VPN / 防火墙 / iPhone / Root CA / 配对 / 共享 / `settings.json` / 用户文件；
未读写 release signing 私钥（`%LOCALAPPDATA%\LanDisk-ReleaseSigning\release-signing.pem` 未触碰）；
未碰 `D:\Projects\pour-water-game`；未覆盖 1.7.6 产物；未对 Owner 正式实例（PID 7772）做任何操作。
