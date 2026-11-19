# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第五段）

保存时间：2026-09-29（第五段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| HEAD | `a07a8cbcd49cd77e690abaaa8e18f730f8fa0044` |
| 本段起点 | `37395f67bd60e8752d17bcaf7402e44f652eb699` |
| 工作树 | clean |
| 单元回归 | 676 / 676 PASS（本段未改产品行为） |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 | 未改写（35 个交付文件哈希见归档） |

## 2. 本段完成项

### A. 传播链成果固定（第一节）
归档目录 `dist/evidence-11/DIAGNOSTIC-chain/`，含**原文副本 + SHA256**：

| 角色 | sha256（前 16） |
| --- | --- |
| `tools/Invoke-UiMatrixGate.ps1`（发布路径） | `51f9325f3b16fe88` |
| `tools/EvidenceVerdict.ps1`（**已纳入版本控制**） | `6e46cbd9e27bad6e` |
| `tools/Build-All.ps1`（发布入口） | `73e3d37ded7335cf` |
| `dist/tools/Test-ReleaseBlockingChain.ps1`（一次性诊断） | `da7e60ebb43c1f09` |
| `dist/tools/Test-EvidenceAggregator.ps1`（一次性诊断） | `dc94ae2d2fc6818e` |
| `dist/tools/Apply-LayoutInjection.ps1`（注入工具） | `0a7aa4b42dd7c8be` |
| `RELEASE_BLOCKING_CHAIN_TESTS.json`（7/7 PASS） | `5a8191af5012cd39` |
| `EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json`（9/9 PASS） | `206a788076ddf367` |

另存 `DELIVERY_DIR_HASHES.txt`（35 个交付文件的逐文件 SHA256），用于证明历史交付未被改写
—— 文件数量只作辅助，不作证据。

**发布路径必需的脚本已纳入版本控制**：`EvidenceVerdict.ps1` 原先在 gitignore 的 `dist/tools` 下，
却被发布门禁 dot-source；已移到 `tools/` 并提交。一次性诊断与注入工具保持归档 + 哈希。

### B. 结果补强（第一节）
结果 JSON 中每例新增：
- `judge_invocation` —— 判定器的**实际调用命令行**（证明 judge 只 dot-source 判定器、不调用捕获）；
- `judge_calls_capture = false`；
- `deleted_file_absent_after_judgement` —— 判定后被删文件仍不存在；
- `deleted_file` / `deleted_file_sha256_before` —— 绝对路径与删除前哈希；
- `recaptured_during_judgement` —— 用 PNG 数量变化检测"判定时偷偷又跑了一次捕获"。

### C. 两个套件在路径迁移后复测通过
- 判定器负向测试 **9/9 PASS**（对照组 READY；8 类注入全部阻断）
- 传播链 **7/7 PASS**：C0 capture=0 gate=0；C1 capture=2 gate=4（重叠 7/2885）；
  C2 capture=2 gate=4（截断 10/101）；C3/C3b-UI/C3b-Full 阻断；C3c UI 范围不受 Full 证据影响

### D. 本段两次自伤（如实记录）
1. 用 `String.Replace` 时误传了**字符**（数组被展平），导致 `.` 与 `'` 被全局替换：
   `tools/Invoke-UiMatrixGate.ps1`（已 git checkout 恢复）、`dist/tools/Test-EvidenceAggregator.ps1`
   与 `Test-ReleaseBlockingChain.ps1`（均在 gitignore 下，已按原文重写）。
2. 更早一次行级编辑把 `Test-ReleaseBlockingChain.ps1` 截成一行 `param(`，同样重写恢复。

**教训**：`dist/` 不受版本控制，脚本编辑一律用 `edit`/`write`，不用行级或 `String.Replace` 手术；
`.NET` 文件 API 用**绝对路径**（相对路径按进程 CWD 解析，不是 shell 位置）。

## 3. 剩余项（按建议顺序）

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | REAL11-03/04/05/09 绑定生成逻辑 | 消费 `UI_LAYOUT_VERIFICATION.tsv` 的 SUMMARY 计数行 + 环境/结果文件；记录实现文件、证据文件及 SHA256、页面/目标身份、检查数量、断言与实际结果、run_id、构建身份；仅预期有目标的场景要求非零目标数；正式路径与哈希在最终验收后填 |
| 2 | 手机权限自动更新链路 | heartbeat `permissionRevision` → 自动 `/api/me` → 按钮更新 → 同会话写操作允许/拒绝 |
| 3 | HTTPS 隔离验证 | 更新前/后/回滚后的监听地址、实际端口、开关、进程身份；确认宿主传的是配置值还是实际绑定值 |
| 4 | Full 判定接入报告与封包 | `matrixReduced=false`、不启用 `SkipMatrixCompleteness`；失败即 BLOCKED 且不生成正式 READY/审核包 |
| 5 | 隐私与历史说明 | 1.7.3 原包不改写 + 外置说明；1.7.4 脱敏 |
| 6 | 升版 1.7.4 → 冻结 → 四产物 → 完整验收 → 报告/绑定表 → 封包 | 必须先把 1–5 做完 |

## 4. 续跑第一件事

写绑定生成逻辑：读 `UI_LAYOUT_VERIFICATION.tsv` 的 `SUMMARY` 行
（`LAYOUT-CORE-VALUE-NOT-CLIPPED`→REAL11-03、`LAYOUT-BUTTON-SPACING`→REAL11-04、
`LAYOUT-ACTION-OVERLAP`→REAL11-05、`LAYOUT-EXTENSION-LONG-CONTENT`→REAL11-09），
配上 `UI_CAPTURE_ENVIRONMENT.tsv` 的检查数量与 `UI_CAPTURE.tsv` 的页面/组合，
产出 `EVIDENCE_BINDING_11.tsv` 中这四项的行；先对诊断矩阵做定向验证，正式哈希留空待最终验收填。

## 5. 注意

- 诊断/注入构建与证据（`dist/publish/LanDisk-win-x64-DIAGNOSTIC-*`、
  `dist/evidence-11/DIAGNOSTIC-*`、`dist/.release-blocking-sandbox`、`dist/.gate-probe*`）
  **不得**作为 1.7.4 正式验收证据；注入从未进入产品源码（撤回后引用为 0）。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保留。
- 负向测试里"预期失败被成功拦截"与正式矩阵的实际失败分开统计。
- 冻结后若再改影响发布字节的代码，更新候选身份并重建验收，不得拼接旧结果。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
