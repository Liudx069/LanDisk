# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第六段）

保存时间：2026-09-29（第六段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| HEAD | `78db24f00d3f5655fc57ae9dca85c843acd1202c` |
| 本段起点 | `b586ec9a6a8d8583f1efe8c7672f4c9afb46d020` |
| 工作树 | clean |
| 单元回归 | 676 / 676 PASS（上一段验证；本段改的是采集输出与生成器，需在下次全量回归时复跑） |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 | 未改写 |

## 2. 本段完成项

### A. 逐页布局证据（第二节"不能只拿全局 SUMMARY 总数"）
`src/LanDisk.App/UiCapture.cs` 现在除全局 `SUMMARY` 行外，每个 **(断言, 页面)** 各出一行 `PAGE`：
`kind / assertion_id / requirement / page / targets_checked / failures / status / page / detail`。

实测暴露的真实差别（`dist/evidence-11/DIAGNOSTIC-binding/`，630 组合、15 页、0 失败）：

| 断言 | 必需页面的目标数 | 无目标页面（应记 NOT_APPLICABLE） |
| --- | --- | --- |
| LAYOUT-CORE-VALUE-NOT-CLIPPED | 概览 589、运营看板 546 | 无 |
| LAYOUT-BUTTON-SPACING | 文件与共享 1092、设置 1932 | HTTPS 与安全、任务与命令、回收站、自检、运营看板 |
| LAYOUT-ACTION-OVERLAP | 设备 6393 | HTTPS 与安全、任务与命令、回收站、自检 |
| LAYOUT-EXTENSION-LONG-CONTENT | 扩展程序 84 | 其它页不产生该断言 |

### B. 绑定生成器 `tools/New-EvidenceBinding.ps1`（已纳入版本控制）
- 每个需求**声明必需页面**：REAL11-03→概览,运营看板；REAL11-04→文件与共享,设置；
  REAL11-05→设备；REAL11-09→扩展程序。
- 必需页面目标数为 0 / 有失败 / 缺记录 → `BLOCKED` / `EVIDENCE_MISSING`，绝不 `CLOSED`。
- 非必需页面记 `NOT_APPLICABLE` 并写明理由；非必需页面出现失败同样阻断。
- **只引用布局测量**；UIE17 导航测试与 DEVICE12 删除语义测试不构成本表任何一条的证据。
- 每行字段：需求、断言、实现文件、证据文件绝对路径 + 完整 SHA256、页面、组合范围、
  检查数量、失败数量、expected、actual、status、run_id、候选身份。
- 另出 `.md` 概览；诊断运行整份标 **DRAFT**，不预填正式路径与哈希。

诊断运行结果（`dist/evidence-11/DIAGNOSTIC-binding/EVIDENCE_BINDING_11.DRAFT.tsv`）：

| 需求 | 断言 | 必需页面 | 结论 |
| --- | --- | --- | --- |
| REAL11-03 | LAYOUT-CORE-VALUE-NOT-CLIPPED | 概览,运营看板 | CLOSED |
| REAL11-04 | LAYOUT-BUTTON-SPACING | 文件与共享,设置 | CLOSED |
| REAL11-05 | LAYOUT-ACTION-OVERLAP | 设备 | CLOSED |
| REAL11-09 | LAYOUT-EXTENSION-LONG-CONTENT | 扩展程序 | CLOSED |

整体：**DRAFT**。

### C. 绑定生成器定向负向验证（5/5 PASS）
`dist/tools/Test-BindingGenerator.ps1` → `dist/evidence-11/DIAGNOSTIC-binding/BINDING_GENERATOR_NEGATIVE_TESTS.json`

| 用例 | 注入 | 结果 |
| --- | --- | --- |
| B0 对照 | 未修改 | 四项 CLOSED |
| B1 | 必需页面目标数改 0 | REAL11-05/设备 BLOCKED |
| B2 | 必需页面失败数改 3 | REAL11-03/概览 BLOCKED |
| B3 | 删掉必需页面记录 | REAL11-09/扩展程序 EVIDENCE_MISSING |
| B4 | 非必需页面出现失败 | REAL11-04/日志 BLOCKED |

## 3. 两个环境约束（记录备查）

- 本机 PowerShell 解析器**不接受无花括号的 `if (条件) continue`**，必须写成 `if (…) { continue }`。
- 已用普通分隔符取代 `` `u{1f} `` 转义，避免转义在不同宿主下的差异。

## 4. 剩余项

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | 手机权限自动更新链路 | heartbeat `permissionRevision` → 自动 `/api/me` → 按钮更新 → 同会话写操作允许/拒绝 |
| 2 | HTTPS 隔离验证 | 更新前/后/回滚后的监听地址、实际端口、开关、进程身份；确认宿主传的是配置值还是实际绑定值 |
| 3 | Full 判定接入报告与封包 | `matrixReduced=false`、不启用 `SkipMatrixCompleteness`；必需失败或缺证即 BLOCKED，不生成正式 READY/审核包 |
| 4 | 隐私与历史说明 | 1.7.3 原包不改写 + 外置说明（含真实 SSID）；1.7.4 脱敏 |
| 5 | 升版 1.7.4 → 冻结 → 四产物 → 完整验收 → 正式报告/绑定表 → 封包 | 必须先把 1–4 做完 |

## 5. 续跑第一件事

手机权限自动更新链路：同一会话内让**页面自己**完成
heartbeat `permissionRevision` 变化 → 自动请求 `/api/me` → 按钮状态更新 → 在临时文件上真实写入成功或被拒。
不得重新配对、不得手工刷新、不得用测试程序主动查询 `/api/me` 代替页面行为。
先核对现有 `MOBILE12-02/03/04` 证据缺哪一段，只补缺的那一段。

## 6. 注意

- 诊断/注入构建与证据、`dist/.binding-negative-sandbox`、`dist/.release-blocking-sandbox`、`dist/.gate-probe*`
  **不得**作为 1.7.4 正式验收证据；注入从未进入产品源码。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保留。
- 绑定表的正式路径与哈希必须在最终候选验收后生成；不得把诊断表改名充当正式表。
- 冻结后若再改影响发布字节的代码，更新候选身份并重建验收，不得拼接旧结果。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
