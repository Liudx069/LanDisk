# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第九段）

保存时间：2026-09-30（第九段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| HEAD | `7536809bfef94486d22185ddd2c235020436dba7` |
| 本段起点 | `43bd898003232420b7f2cb483cf08a4e12614201` |
| 工作树 | clean |
| 单元回归 | 677 / 677 PASS（上一段；本段未改产品代码，只加发布判定脚本） |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 | 未改写 |

## 2. 本段完成项：Full 判定接入正式报告与封包（第二节）

### 2.1 新增 `tools/Invoke-ReleaseGate.ps1`（已纳入版本控制）
与 UI gate 的分工明确写清：
- **UI gate**（`tools/Invoke-UiMatrixGate.ps1`）：只负责桌面矩阵 —— 跑捕获、判矩阵与夹具隔离。
  它通过**只说明 UI 范围**成立。
- **Release gate**（本脚本）：整份发布 —— UI 证据 + 浏览器两端 + runtime + setup + 更新回滚。

硬约束：
- `Scope=Full`；正式路径**不使用** `-SkipMatrixCompleteness` / `-ReducedMatrix` 或任何诊断豁免；
- **证据必须属于当前候选**：默认在 `dist/evidence-11` 下按 `build-info.json` 的提交寻找本次运行的
  证据目录；找不到、或找到多个，一律阻断。不允许拿别的候选、别的运行或历史结果顶替；
- `NOT_READY` → 写出明确的 `DELIVERY_BLOCKED.md` 失败说明，并**删除** `DELIVERY_READY.txt`；
  `READY` 才写 `RELEASE_GATE.json` 与发布成功标记；
- **依赖无环**：只读源证据，不依赖它在通过之后才会生成的报告、审核 ZIP 或 ZIP 自身哈希。

### 2.2 接入 `tools/Build-All.ps1`
在 UI gate 之后、`Generate-DeliveryReport` / `Finalize-Delivery` 之前调用；非零即 `throw`，
不生成正式候选、不写发布成功标记。待测产物（Setup / release ZIP / 签名 .lupd）仍会先生成，
否则无法做精确产物验收 —— Full 未通过前它们只是**待验产物**，不是验收通过的交付。

### 2.3 定向验证 `dist/tools/Test-ReleaseGate.ps1` → **6/6 PASS**
| 用例 | 注入 | 结果 |
| --- | --- | --- |
| FG0 | 合成夹具（完整 42 行矩阵） | READY，exit 0，无 BLOCKED 说明 |
| FG1 | 缺 WebKit 证据 | NOT_READY exit 5 + BLOCKED 说明 |
| FG2 | 必需断言失败 | NOT_READY |
| FG3 | 候选身份不符 | NOT_READY |
| FG4 | 证据不可解析 | NOT_READY |
| FG5 | 真实诊断证据属于旧候选 | NOT_READY（候选绑定生效） |

所有阻断场景均**未**写出发布成功标记。

**FG0 的定位（重要）**：它是**合成夹具**，只证明判定器不是一律拒绝，**不是**产品通过证据。
真正的 Full 正向放行必须在最终候选全部验收之后由正式证据产生 —— 不得为此填入模拟通过结果。

**一次判 FAIL 的原因**：第一次跑 FG0 只给了 1 行矩阵，而正式路径**正确地**拒绝不完整矩阵
（要求 42 行）。这恰好证明门禁没有被这条捷径绕过；已把夹具改为完整 42 行。

## 3. 本段确认的"不替代"边界（按要求逐项记录）

| 已通过的 | 它**不能**替代 |
| --- | --- |
| `UPDATEPORT12-06` 源码级调用点门禁 | 完整更新/回滚端到端通过 |
| UI gate 通过 | 产品 READY |
| 诊断绑定表（DRAFT） | 正式发布批准 |
| FG0 合成对照 READY | 最终候选的 Full 正向放行 |

## 4. 剩余项

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | WebKit 复跑 | 含 MOBILE12-11 与既有回归；与 Chromium 同条件；必须仍由页面自己产生 heartbeat → /api/me → 按钮更新 |
| 2 | 隐私与历史说明 | 1.7.3 原件不改写 + 外置说明（含真实网络标识，仅供本地历史审计）；1.7.4 对外材料用脱敏夹具（截图/录屏/文本/元数据一并检查） |
| 3 | HTTPS 端到端脚本 | 区分 `LanEnabled=false`／`LanEnabled=true,HttpsEnabled=false`／正常局域网 HTTPS／固定与动态端口；对动态端口记录启动配置值 0、实际绑定端口、传给更新程序的参数、重启参数、实际监听与健康检查目标。**构建后执行** |
| 4 | 升版 1.7.4 → 冻结 → 四产物 → 完整验收 → 正式报告/绑定表 → Full 放行后封包 | 必须先把 1–3 做完 |

### 4.1 HTTPS 端到端的两类升级证据（第四节的区分，务必保持）
- **A**：旧版本按正常配置升级到**冻结的正式 1.7.4 `.lupd`** —— 必须实际使用交付的正式包并记录完整 SHA256。
- **B**：已安装修复后的 1.7.4 宿主，用**动态端口**启动后发起更新与失败回滚 ——
  需要更高版本测试包时按既有工具生成并标明测试用途，**不代替 A**。
  B 验证的是**修复后的发起方**；不得暗中替换旧基线程序后宣称"旧版动态端口升级已修复"。

失败回滚应恢复到失败更新之前的**实际版本与服务端点**，不要机械断言"回到 1.7.4"。
settings 保留用户配置值，不得为测试通过把动态 0 写成固定端口。
局域网 HTTPS 关闭时不得新增物理网卡或通配地址的 HTTPS 监听；既有回环 HTTPS 按原设计验证。

## 5. 续跑第一件事

**WebKit 复跑**：与 Chromium 相同条件运行
`node tools/e2e/ui-test.mjs --engine webkit --server <冻结 release 的 LanDisk.Server.exe>`，
确认 MOBILE12-11 在 WebKit 下同样由页面自身产生
heartbeat 修订号变化 → 自行请求 `/api/me` → 上传按钮自动更新；
其余既有项目一并通过。之后进入隐私与历史说明。

## 6. 注意

- 诊断/注入构建与证据、`dist/.release-gate-sandbox`、`dist/.https-probe`、
  `dist/.release-blocking-sandbox`、`dist/.binding-negative-sandbox`、`dist/.gate-probe*`
  **不得**作为 1.7.4 正式验收证据；注入从未进入产品源码。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保留。
- 正式绑定表必须由最终候选结果重新生成，不得把诊断表改名充数。
- 冻结后若再改影响发布字节的代码，更新候选身份并重建验收，不得拼接旧结果。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
