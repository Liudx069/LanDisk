# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第四段）

保存时间：2026-09-29（第四段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| 本段起点 HEAD | `31096efea5956dbdd8d669b1867902d144c65ad1` |
| 工作树 | 见提交后的实际值（本段改动：`tools/Build-All.ps1`、新增 `tools/Invoke-UiMatrixGate.ps1`、`dist/tools/EvidenceVerdict.ps1`、新增 `dist/tools/Test-ReleaseBlockingChain.ps1`） |
| 单元回归 | 676 / 676 PASS（本段未改产品行为，仅改发布门禁与判定器） |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 | 未改写 |

## 2. 本段发现并修复的真缺陷（这是本段最重要的产出）

### 2.1 发布入口根本没有跑桌面矩阵
`tools/Build-All.ps1` **从不调用** `--render-ui`。截图靠人手工跑，落在 `dist/ui-evidence`，
发布流程只是把它镜像进交付目录（`Finalize-Delivery.ps1:123`）。

因此：**矩阵失败在结构上不可能阻止发布** —— 没有任何人读捕获程序的退出码，
因为发布入口从来没调用过它。这正是原任务第二节所说的"不能只测试一个独立判定函数，
而正常发布入口仍跳过它"。

已修：`Build-All.ps1` 现在自己跑矩阵，并调用新脚本 `tools/Invoke-UiMatrixGate.ps1`，
非零即 `throw`，不生成正式候选。

### 2.2 门禁读不到 GUI 进程的退出码
`LanDisk.exe` 是 **Windows 子系统（GUI）** 可执行文件。PowerShell 的调用运算符
`& $exe ...` **不会等待 GUI 进程**，所以 `$LASTEXITCODE` 是 `$null`、输出文件还没写出来。
实测：干净的 630 组合跑完、630 张 PNG 已落盘，门禁却报"缺少 UI_CAPTURE.tsv"并阻断 ——
这会让**每一次正式发布都被无故阻断**（fail-closed 但毫无用处）。
已改用 `Start-Process -Wait -PassThru` 读取真实退出码。实测干净定向跑：退出码 0、门禁放行。

### 2.3 判定器对"候选身份"与"矩阵范围"的耦合
- 捕获只写 TSV 与 PNG，不写 `build-info.json`；判定器原先靠它推断期望候选，
  于是在发布入口里必然 NOT_READY。已新增 `-CandidateCommit`，由 `Build-All` 传入它正在构建的提交。
- 判定器原先按整份发布证据要求（浏览器 JSON、runtime、setup、update 等），
  而发布门禁只对**桌面矩阵**负责。已新增 `-Scope UI|Full`：门禁用 `UI`，报告与负向测试用 `Full`。
- 判定器原先假设矩阵文件在子目录里；发布门禁指向捕获自己的输出目录。已改为**定位**而非假设。
- 定向测试只跑一个页面，与 630 组合的完整性检查冲突。已新增 `-SkipMatrixCompleteness`，
  并在事实里记录 `matrixReduced` / `matrixReducedTo`，**声明式缩减，不静默容忍**。

## 3. 本段新增的失败传播链测试

`dist/tools/Test-ReleaseBlockingChain.ps1`：调用的是**发布入口所用的同一个门禁脚本**
（`tools/Invoke-UiMatrixGate.ps1`），在隔离输出目录执行，`dist/delivery` 的历史产物不被触碰。

用例：
| id | 内容 | 期望 |
| --- | --- | --- |
| C0-clean-control | 干净构建的定向跑 | 放行（否则门禁等于拒绝一切） |
| C1-overlap-blocks | 重叠断言失败 | 阻断 |
| C2-clip-blocks | 核心值截断断言失败 | 阻断 |
| C3-missing-evidence-blocks | 缺少 `UI_CAPTURE.tsv` | 阻断 |
| C3b-evidence-deleted | 运行后证据被删（集合无法建立） | 阻断 |

用例同时记录：捕获退出码、门禁退出码、判定结果、是否写出 READY 报告、
`dist/delivery` 是否新增文件（用前后文件集合比对，而不是声称）。

## 4. 已成立、本段未重复的成果

- 三段布局反例与正常对照（间距 335 处失败、重叠 6×33 DIP 相交、核心值 108→36 DIP 裁切，
  各自触发正确断言，退出码 2；干净矩阵 630 组合零误报）。
- 判定器 9 项正负对照。
- UI 截图输入隔离（`WLAN12-01` 原生调用 0、`WLAN12-02` 夹具哨兵 PASS、44671 个文本控件被检查）。
- 可计数布局证据（`UI_LAYOUT_VERIFICATION.tsv`）。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED`。

## 5. 剩余项

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | §四 绑定表消费 `UI_LAYOUT_VERIFICATION.tsv` | REAL11-03/04/05/09 绑到计数证据；记录页面/目标身份/检查数量/结果 SHA256/run_id/构建身份；正式路径与哈希在最终候选验收后填 |
| 2 | §四.3 手机权限自动更新链路 | heartbeat `permissionRevision` → 自动 `/api/me` → 按钮更新 → 同会话写操作允许/拒绝 |
| 3 | §五 HTTPS 隔离验证 | 监听地址/端口/开关值/进程身份前后对照；确认宿主传的是配置值还是实际绑定值；是否补 `UpdateHealth` 由证据决定 |
| 4 | §六 判定器接入报告流程 | `Full` 作用域判定器接入 `Generate-DeliveryReport` / 封包，缺失或失败即 BLOCKED |
| 5 | §六 隐私与历史说明 | 1.7.3 原包不改写 + 外置说明（含真实 SSID）；1.7.4 交付脱敏 |
| 6 | 升版 1.7.4 → 提交 → 冻结 → 构建四产物 | 必须先把 1–5 做完 |
| 7 | 完整验收 → 报告与绑定表 → 审核包 | 见原任务第六节 |

## 6. 续跑第一件事

把 `Full` 作用域的判定器接进报告/封包流程：在 `Generate-DeliveryReport.ps1` 与
`Finalize-Delivery.ps1` 之前调用 `EvidenceVerdict -Scope Full`，NOT_READY 即产出明确的
BLOCKED 失败报告并停止封包（允许保存失败报告与诊断，但不得称为正式可安装交付）。

## 7. 注意

- 诊断/注入构建（`dist/publish/LanDisk-win-x64-DIAGNOSTIC-*`、`dist/evidence-11/DIAGNOSTIC-*`、
  `dist/.release-blocking-sandbox`、`dist/.gate-probe*`）**不得**作为 1.7.4 正式验收证据。
- 负向测试里"预期失败被成功拦截"与正式矩阵的实际失败**分开统计**。
- 冻结后若再改影响发布字节的代码，更新候选身份并重建验收，不得拼接旧结果。
- 真机 NOT_RUN 仅限已批准白名单；可自动化却漏跑者不得混入。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
