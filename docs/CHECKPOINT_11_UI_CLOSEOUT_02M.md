# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第十三段）

保存时间：2026-09-30（第十三段额度用尽前）
项目：`D:\LanDisk`　目标版本 1.7.4　**未升版、未构建正式产物**

---

## 1. 现状

| | |
| --- | --- |
| HEAD | `d9549952c20dc8d6b538141f12168014e74ee1c8` |
| 本段起点 | `5f22e70135eb0298c6dc0421fd7926fb1e4d4d66` |
| 工作树 | clean |
| 单元 677/677 · Chromium/WebKit 40/40 · 链 7/7 · Full 10/10 · 判定器 9/9 · 绑定 5/5 | 全部 PASS |
| HTTPS 监听矩阵 | **4/4 PASS**（本段新增） |
| 1.7.3 四产物 / 原失败 TSV / 五张截图 / 原包 | 未改写 |

## 2. 本段完成项：HTTPS 端到端脚本（第二节）

### 2.1 新增 `tools/Verify-HttpsEndToEnd.ps1`（已纳入版本控制）
三种模式：
- **Matrix** —— 只跑监听矩阵，不涉及更新；任何构建都能跑（本轮已实跑）。
- **A** —— 正常配置的旧版本 → 升级到**冻结的正式 `.lupd`**；**直接使用交付包**并记录其**完整 SHA256**，
  不现场重打替代包。
- **B** —— 修复后的宿主以**动态端口**启动，由它发起更新与失败回滚；更高版本测试包**明确标注测试用途**。
  **B 验证的是修复后的发起方**，不代替 A，也不用来声称"旧宿主的动态端口问题已被追溯修复"。

记录维度：版本与构建提交；宿主与更新程序身份；配置端口与实际绑定端口；宿主传给更新程序的参数；
实际监听地址；健康检查目标与结果。

### 2.2 关键设计：端口从**实际监听**反推，不读配置
- HTTP 端口：同时出现在 `127.0.0.1` 与 `::1`（回环 HTTP 绑 v4+v6）；
- HTTPS 端口：只出现在 `127.0.0.1`（回环 HTTPS 只绑 v4，当前设计）。

只有这样才能判定"局域网 HTTPS 关闭时有没有多出**局域网 HTTPS**监听"，而不是只看端口个数。

### 2.3 定向实跑：Matrix **4/4 PASS**
用**冻结 1.7.3 release** 实跑（监听构造在本次修复中未改动）：

| 用例 | 结果 |
| --- | --- |
| H1 局域网关闭 | 监听 3 个，**局域网 0 个**；端口 0 被解析为非 0；健康通过 |
| H2 `LanEnabled=true, HttpsEnabled=false`, 兼容模式 | 生效端口 http=49652 https=49653；局域网监听 `192.168.2.2:49652`（**HTTP 端口**）；落在 **HTTPS 端口上的 0 个** |
| H3 `LanEnabled=true, HttpsEnabled=true` | 局域网监听 `192.168.2.2:49657`，**正是 HTTPS 端口**（与 H2 形成对照） |
| H4 动态端口 | `settings` 保留用户写的 **0**（未被改写成固定端口），实际绑定端口非 0，健康通过 |

H2/H3 的对照正是本项的证明：`HttpsEnabled=false` 时局域网监听落在 HTTP 端口，
`=true` 时才落在 HTTPS 端口；**局域网 HTTPS 关闭后没有多出 HTTPS 监听**；回环 HTTPS 始终保留。

结果文件：`dist/evidence-11/HTTPS-E2E/https-e2e-matrix.json`

### 2.4 隐私提醒（已记入）
监听矩阵证据里会出现**本机真实局域网地址**，属诊断证据，按既定规则**不得作为对外分发材料**；
对外素材仍走脱敏夹具。这一点与本轮的历史隐私说明保持一致口径。

## 3. 剩余项

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | 升版 **1.7.4** | 改各 `csproj` 的 `<Version>` 与 `tools/Build-All.ps1` / `Setup-Acceptance.ps1` / `Update-ReleaseVerify.ps1` 的版本默认值 |
| 2 | 提交 → 工作树干净 → **冻结构建身份** | 构建提交即候选身份 |
| 3 | 构建**四个待验产物** | `LanDisk-Setup.exe` / `LanDisk-win-x64.zip` / `LanDisk-update-1.7.4.lupd` / `LanDisk-source.zip` |
| 4 | 对**同一批精确产物**执行完整验收 | 见第 4 节 |
| 5 | 生成**正式需求绑定表**（由最终候选结果重新生成） | 不得把诊断表改名充数 |
| 6 | **Full 判定** → 正式报告与封包 | Full 未通过前不得宣布 READY |

## 4. 完整验收范围（不缩水、不加码）

- 旧 **677** 项及实际新增测试；
- **15 页 × 42 组合 = 630** 规定桌面组合；
- Chromium / WebKit 各 **40** 项及实际新增专项（**必须重新绑定最终 1.7.4**，
  不得引用冻结 1.7.3 的 40/40）；
- Setup 迁移；冻结正式更新包的**升级与回滚**（A 类）；
- 默认 / 非默认 / **动态端口**与 HTTPS 场景（A、B 两类分别记录，`tools/Verify-HttpsEndToEnd.ps1`）；
- runtime identity、source identity、install manifest、**隐私检查**（针对实际交付集合）。

安装器保留现有透明 Logo、单机柜、由上往下点灯与连续过渡，**不重新设计**；
最终全灭 / 过渡中 / Ready / 失败截图及实际安装录屏**绑定最终 Setup SHA256**，不用历史素材代替。

## 5. 交付清单（保持不变）

四个正式产物 + 原约定八份独立文件 + `UI_PAGE_CATALOG.tsv` + 关键实际源码 + 原始验收结果 +
截图与录屏；审核包重新开包核对 Manifest / CRC / 成员集合 / 大小 / 完整 SHA256，
ZIP 最终哈希放**外置 sidecar**。

`ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保持。

## 6. 续跑第一件事（直接进入候选阶段，不再准备）

1. 升版 1.7.4：`src/*/**.csproj` 的 `<Version>` + `tools/Build-All.ps1`、`tools/Setup-Acceptance.ps1`、
   `tools/Update-ReleaseVerify.ps1` 的版本默认值 → 提交 → 确认工作树干净。
2. 运行 `tools/Build-All.ps1` 构建四个待验产物（它会先跑 UI gate 与 Full 判定；
   **Full 预期不会通过**，因为浏览器/runtime/setup/更新回滚证据尚未针对 1.7.4 产生 ——
   这是正确行为：待验产物已生成，但不得宣布 READY）。
3. 依次补齐：Chromium/WebKit（绑 1.7.4）、Setup 迁移、A/B 两类更新与回滚、HTTPS 矩阵、
   runtime/source identity、install manifest、隐私检查。
4. 生成正式绑定表 → 重新运行 Full 判定 → 通过后生成正式报告并封包。

**允许在同一批冻结产物上补齐证据后重新判定与 Finalize**；不得为再次进入封包流程而无故重新编译、
改变产物哈希使刚完成的证据失效。

## 7. 注意

- 诊断/注入构建与证据、`dist/.release-gate-sandbox`、`dist/.https-probe`、`dist/evidence-11/HTTPS-E2E`、
  `dist/.release-blocking-sandbox`、`dist/.binding-negative-sandbox`、`dist/.gate-probe*`
  **不得**作为 1.7.4 正式验收证据（`HTTPS-E2E` 的矩阵结果可作为监听语义的定向证据，
  但正式 A/B 更新证据必须重新产生）。
- 注入从未进入产品源码（`LANDISK_CAP_` 引用 0 处）。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
