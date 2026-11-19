# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第二段）

保存时间：2026-09-29（第二段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| HEAD（完整） | `9f37e4e99fef62de4a5a473dcd90b6b17c82d1d8` |
| 工作树 | **clean** |
| 本段起点 | `a650575a2f18f359ddf01b4681a2825e1d5888ec` |
| 单元回归 | **676 / 676 PASS** |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 | 未改写 |

提交链（自 1.7.3 尖端 `347ed76`）：

```
9f37e4e  布局断言改为可计数证据 + 修复捕获退出码从未传播   ← 本段
a650575  保存 1.7.4 收口的可恢复断点（仅文档）
19d4e59  UI 截图输入隔离 + 证据判定器                     ← 上一段
```

`a650575` 与 `19d4e59` 的差异经 `git diff --stat` 核对为**仅**
`docs/CHECKPOINT_11_UI_CLOSEOUT_02.md` 一个文件，未凭猜测。

## 2. 本段完成项

### A. 布局断言可计数化（REAL11-03/04/05/09 的绑定基础）
`src/LanDisk.App/UiCapture.cs`：每次布局检查都记账，输出 `UI_LAYOUT_VERIFICATION.tsv`
（`SUMMARY` 行 = 断言 × 目标数 × 失败数 × 覆盖页面；`FAILURE` 行 = 元素身份与数值）。

诊断矩阵实测（`dist/evidence-11/DIAGNOSTIC-uicapture-counted/`）：

| 断言 | 需求 | 目标数 | 失败 |
| --- | --- | --- | --- |
| LAYOUT-ACTION-OVERLAP | REAL11-05 | 124119 | 0 |
| LAYOUT-BUTTON-SPACING | REAL11-04 | 7726 | 0 |
| LAYOUT-CORE-VALUE-NOT-CLIPPED | REAL11-03 | 4324 | 0 |
| LAYOUT-EXTENSION-LONG-CONTENT | REAL11-09 | 84 | 0 |
| LAYOUT-OVERFLOW-HORIZONTAL | REAL11-03 | 630 | 0 |
| LAYOUT-VISIBLE-TEXT | REAL11-03 | 630 | 0 |

目标数为 0 的断言按 `FAIL-NO-TARGETS` 处理并阻止干净退出。

### B. 正反例
- **正例**：630 组合、上述目标数、零失败。
- **反例（已完成）**：把同行相邻按钮 `Margin` 归零的一次性构建 → `LAYOUT-BUTTON-SPACING` 失败 **335**、
  `UI_CAPTURE_FAIL=335`、**进程退出码 2**。证据在 `dist/evidence-11/DIAGNOSTIC-negative-layout/`
  （含 `README-NEGATIVE.md` 说明它不属于交付）。注入代码已撤回，带注入的构建目录已删除。
- **反例（未完成，如实记录）**：
  - REAL11-05：注入只造成间距违规而非相交（目标 124119、失败 0）——重叠门禁反例**未证明**。
  - REAL11-03：`MaxWidth = 6` 未造成截断（目标 4324、失败 0）——截断门禁反例**未证明**。

### 真缺陷：捕获程序的退出码从未传播
`src/LanDisk.App/App.xaml.cs` 的 `CaptureAndExitAsync` **丢弃** `UiCapture.RunAsync` 的返回值，
`finally` 里无条件 `Environment.Exit(0)`。所以缺页、错页、断言失败一律退出 0。
这正是"5 个布局组合 FAIL 却退出 0"的真正原因——上一段只改了 `UiCapture` 的返回值，
而调用方根本没读它，那一处修改单独永远不会生效。已改为用捕获判定作为进程退出码（异常退出 3）。
实测：反例 2、正常 0。

### D. 更新端口与 HTTPS 语义（已读源码，未做隔离验证）
- `AppSettings.HttpsEnabled`（bool）才是"是否启用 HTTPS"的**显式开关**；
  `HttpsPort`（int，默认 8892）只是端口号。
- `LanDiskHost.cs:144` 的**回环 HTTPS 监听是无条件的**；`HttpsEnabled` 只控制局域网侧
  （`lanHttps`）。所以"更新前没有 HTTPS 监听"在回环上不成立。
- `LanDiskHost.cs:136`：`if (httpsPort == 0) httpsPort = FindFreePort();` ——
  **0 的含义是"随便挑一个空闲端口"，不是"关闭"**。
- `LanDisk.Updater/Program.cs:711`：`--https-port` 缺失与 `--https-port 0`（`TryParse` 成功得 0）
  **无法区分**，两者都得到 0。
- 结论：更新/回滚"意外新增 HTTPS 监听"的现实风险集中在**端口漂移**，而
  `UpdatePortChoice`（实时值优先）已经处理。**尚未做隔离验证**，也尚未确认
  `UpdateHealth` 只比较 adminPort/lanPort、未比较 HTTPS 监听集合这一缺口是否需要补。

## 3. 剩余项（按建议顺序）

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | 补齐 REAL11-05 / REAL11-03 的反例注入 | 需能真正制造相交与截断的注入方式 |
| 2 | 绑定表生成器消费 `UI_LAYOUT_VERIFICATION.tsv` | REAL11-03/04/05/09 绑到计数证据，不再绑 UIE17 |
| 3 | §四.3 手机权限自动更新链路 | heartbeat `permissionRevision` → 页面自动 `/api/me` → 按钮更新 → 写操作允许/拒绝 |
| 4 | §四.4 HTTPS 隔离验证 | 更新/回滚前后监听集合一致；决定是否补 `UpdateHealth` 的 HTTPS 比较 |
| 5 | §四.E 判定器接入发布流程 | 9 项负向测试阻断发布；上层传播捕获非零退出码 |
| 6 | §四.F 隐私与历史说明 | 1.7.3 原包不改写 + 外置说明（含真实 SSID，仅供本地历史审计） |
| 7 | 升版 1.7.4、提交、冻结 | 必须先把 1–6 做完 |
| 8 | 构建四产物 → 完整验收 → 报告与绑定表 → 封包 | 见原任务第六节 |

## 4. 续跑第一件事

补 REAL11-05 与 REAL11-03 的反例注入：写一个一次性构建，把**两个按钮的位置改成真正相交**
（例如给第二个按钮负 Margin 或把它平移进第一个按钮），并把一个**已标记的核心值**设为
`TextTrimming` + 极窄宽度使其真的被截断，跑一次完整矩阵确认
`LAYOUT-ACTION-OVERLAP` 与 `LAYOUT-CORE-VALUE-NOT-CLIPPED` 各自失败数 > 0、退出码非零，
然后撤回注入并删除该构建。

## 5. 注意

- 诊断构建（`dist/publish/LanDisk-win-x64-DIAGNOSTIC-*`、`dist/evidence-11/DIAGNOSTIC-*`）
  **不得**作为 1.7.4 正式验收证据。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 必须保留；不得写成"已证明不是布局问题"。
- 冻结后若再改产品代码，更新候选身份并重建验收，不得拼接旧字节结果。
- 每份正式结果记录候选身份、run_id、被测产物哈希、夹具身份。
- 真机 NOT_RUN 仅限已批准白名单；可自动化却漏跑者不得混入。
- 不改 Owner 真实数据、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
