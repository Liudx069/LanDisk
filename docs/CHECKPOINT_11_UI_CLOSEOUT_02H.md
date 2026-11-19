# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第八段）

保存时间：2026-09-30（第八段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| HEAD | `7c5b4da43668009ee37221952a6f2033bbbf3c5f` |
| 本段起点 | `f2f4f63bbded90727ce17801c8f7d18da095e300` |
| 工作树 | clean |
| 单元回归 | **677 / 677 PASS**（原 676 + 本段新增门禁 `UPDATEPORT12-06`） |
| Chromium e2e | 40 / 40 PASS（上一段；本段未改前端） |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 | 未改写 |

## 2. 本段完成项：HTTPS / 动态端口 / 更新端点核验（第二节）

完整记录见 **`docs/LAN_DISK_11_HTTPS_LISTENER_RECORD.md`**。要点：

### 2.1 实际控制关系（读源码 + 实跑，不预设"0 = 关闭"）
| 项 | 实际语义 |
| --- | --- |
| `HttpsEnabled` | 只控制**局域网侧**；不影响回环 |
| 回环 HTTPS | **无条件监听**（`https-port 0`、`HttpsEnabled=false` 时依然存在） |
| 端口 `0` | `FindFreePort()`「挑一个空闲端口」，**不是关闭** |
| 局域网监听 | 仅 `LanEnabled` 为真时添加，且只绑**物理网卡**（排除 Hyper-V 交换机与 VPN 隧道地址） |

**实测**（冻结 1.7.3 服务器，`--port 0 --https-port 0`，隔离目录）：
监听 `::1:60600`、`127.0.0.1:60600`（回环 HTTP）、`127.0.0.1:60601`（回环 HTTPS）；
**无**局域网监听；**未写出 `settings.json`**。
记录到既有不对称（未改动）：回环 HTTP 绑 IPv4+IPv6，回环 HTTPS 只绑 IPv4。

局域网开关的保留方式：更新程序**不**传 `HttpsEnabled`，重启后从受管状态 `settings.json` 读取，
而受管状态由回滚逐字节恢复（T04-06 已证）。

### 2.2 发现的真实缺陷
`UpdateEndpoints.cs` 与 `Pages2.cs` 把 `runtime.Settings.HttpPort/HttpsPort` 传给 `RequestApply`
—— 那是**用户配置值**，不是**实际绑定端口**（`LanDiskHost` 解析 0 时只改本地变量，不写回 Settings、
不写 settings.json，已实测确认）。配置为 0 时：宿主实际绑 60600/60601，更新程序收到 0，
重启后把 0 再解析一次 → **落到另一个端口**，健康检查目标也是 0 → 失败 → 回滚。
即：**跑在动态端口上的实例无法完成更新，且重启会挪端口**。

**可达性如实说明**：管理页拒绝保存 0（`AdminOperations.cs:239` 要求 1..65535），界面设不出 0；
只能由命令行或手改 `settings.json` 得到。属"受支持但非界面可达"，不夸大影响面。

### 2.3 最小修复 + 门禁
- `LanDiskRuntime` 新增 `BoundHttpPort` / `BoundHttpsPort`（实际绑定端口；`Settings` 不被污染）
- `LanDiskHost.cs:141-142` 解析后写入
- `UpdateEndpoints.cs:176`、`Pages2.cs:191` 改用绑定端口
- 新增源码级门禁 `UPDATEPORT12-06`；**已反向验证**：改回旧写法立刻变红并指出 `Pages2.cs:191`

未改监听架构，未删既有回环行为，未新增测试框架。

## 3. 剩余项

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | HTTPS 端到端对照 | 更新前/后/回滚后的实际监听地址、端口、`HttpsEnabled`、宿主与更新程序进程身份、宿主传给更新程序的实际参数、健康检查目标与结果；另用 `--port 0` 跑一次完整更新+回滚。**需要正式更新包 → 1.7.4 冻结后做** |
| 2 | WebKit 复跑 | 含 MOBILE12-11 与既有回归；与 Chromium 同条件 |
| 3 | Full 判定接入报告与封包 | `matrixReduced=false`、不启用 `SkipMatrixCompleteness`；`Scope=Full`；缺证/错候选/必需失败即阻止正式 READY 与封包。允许先生成待测产物供验收，但不得宣布为正式可交付候选 |
| 4 | 隐私与历史说明 | 1.7.3 原包不改写 + 外置说明（含真实 SSID）；1.7.4 脱敏（截图/录屏/诊断文本/文件名/元数据） |
| 5 | 升版 1.7.4 → 冻结 → 四产物 → 完整验收 → 正式报告/绑定表 → 封包 | 必须先把 1–4 做完 |

## 4. 续跑第一件事

**Full 判定接入真正的报告与封包入口**（第三节）：
在 `tools/Finalize-Delivery.ps1` 与 `tools/Generate-DeliveryReport.ps1` 之前调用
`Get-EvidenceVerdict -Scope Full`（`tools/EvidenceVerdict.ps1`）；
NOT_READY 即产出明确标注的 **BLOCKED 失败报告**并**停止封包**；
正式路径**不得**传入 `-SkipMatrixCompleteness` 或 `-ReducedMatrix`。
保留 UI gate（`tools/Invoke-UiMatrixGate.ps1`）作为矩阵专项，二者关系写清：UI 通过 ≠ 产品 READY。

## 5. 注意

- 诊断/注入构建与证据、`dist/.https-probe`、`dist/.release-blocking-sandbox`、
  `dist/.binding-negative-sandbox`、`dist/.gate-probe*` **不得**作为 1.7.4 正式验收证据；
  注入从未进入产品源码。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保留。
- 正式绑定表必须由最终候选结果重新生成，不得把诊断表改名充数。
- 冻结后若再改影响发布字节的代码，更新候选身份并重建验收，不得拼接旧结果。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
