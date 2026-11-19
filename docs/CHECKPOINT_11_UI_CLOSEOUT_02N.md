# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第十四段）

保存时间：2026-09-30（第十四段额度用尽前）
项目：`D:\LanDisk`

---

## 1. 已冻结候选

| | |
| --- | --- |
| 版本 | **1.7.4** |
| 构建提交 | `a9b06eb6ed0b15744556b31fc156748b93cc27dc` |
| 工作树 | clean |
| 本候选 run_id | `run-11-1.7.4-20260930-031659` |
| 1.7.3 原产物 / 原包 / 原始失败记录与截图 | 未改写（原包哈希仍为 `44b549c6…`） |

## 2. 四个**待验产物**（已构建，尚未宣布交付）

| 产物 | bytes | 完整 SHA256 |
| --- | --- | --- |
| `LanDisk-Setup.exe` | 218,338,344 | `edbfb1ba67c2d71fcef082008c513bc443b2faf014b29be3222bba87efed2de0` |
| `LanDisk-win-x64.zip` | 148,753,366 | `1768473e713689e288da913f945852e623fbd1a0c2445387ae2f0fcd422e1517` |
| `LanDisk-update-1.7.4.lupd` | 148,773,334 | `58de0a1ff097c6ab2d5758fc2b697aad33813aab180638baea6c11844f4adc36` |
| `LanDisk-source.zip` | 1,973,048 | `c01605fa888ae8fc47fb1eac189b491baf88b068368470f8c28a1cb6d8f8029c` |

包内身份：`version=1.7.4`、`commit=a9b06eb6ed0b15744556b31fc156748b93cc27dc`、`buildId=Release/win-x64`。
四个提交字段（FINAL_HEAD / RELEASE / UPDATE / SOURCE）一致，候选身份核对 **19/19 PASS**；
源码包完整性 **9/9 PASS**（310 个 tracked 文件）。

## 3. 对同一批精确产物已完成的验收

| 项目 | 结果 | 说明 |
| --- | --- | --- |
| 单元回归 | **677 / 677 PASS** | Build-All 内执行 |
| 桌面界面矩阵 | **630 / 630 PASS**，15 页，漏页 0，重复 0，额外 0 | 由**本次 1.7.4 Build-All** 产出，已绑定本 run |
| WLAN 输入隔离 | `WLAN12-01_NATIVE_WLAN_CALLS = 0`、`WLAN12-02_FIXTURE_SENTINEL_SEEN = PASS`、检查控件 44,673 | 同一次矩阵 |
| 页面身份目录 | `UI_PAGE_CATALOG.tsv`：version 1.7.4、commit `a9b06eb`、15 页 | 期望页面身份的锚点 |
| Chromium | **40 / 40 PASS** | 针对 1.7.4 release（`LanDisk.Server.exe` sha256 `ae1e3955…`），含 MOBILE12-11 |
| WebKit | **40 / 40 PASS** | 同上；MOBILE12-11 同样由页面自发 heartbeat→/api/me→按钮更新 |
| Setup 迁移（隔离安装） | **20 PASS / 0 FAIL / 1 NOT_RUN** | 冻结的 1.7.4 Setup |
| **A 类更新与回滚** | **41 / 41 PASS** | **直接使用交付的冻结 .lupd**，证据里记录其完整 SHA256 `58de0a1f…`；升级到 1.7.4、端口保持 8891/8892、失败包自动回滚、Owner 55 个基线文件未变 |
| HTTPS 监听矩阵（1.7.4） | **4 / 4 PASS** | H1 局域网关闭无局域网监听；H2 `HttpsEnabled=false` 时局域网监听落在 **HTTP 端口**（HTTPS 端口上 0 个）；H3 开启时落在 **HTTPS 端口**；H4 动态端口配置保持 0 且实际绑定非 0 |
| runtime identity | `version 1.7.4` / `commit a9b06eb` 双匹配 | |
| install manifest | **15 / 15 PASS** | `INSTALL_MANIFEST_CONTAINS_BUILD_INFO = PASS` |
| 正式绑定表 | 已生成（见下） | 由**本候选**原始结果生成，非诊断表改名 |

**Full 发布判定：READY（exit 0）** —— 在隔离探测目录上运行，确认全部必需证据齐备且属于本候选。

## 4. 正式绑定表

`dist/evidence-11/run-11-1.7.4-20260930-031659/01-report/EVIDENCE_BINDING_11.tsv`
（并已复制到 `dist/delivery/EVIDENCE_BINDING_11.tsv`）

| 需求 | 断言 | 必需页面 | 结论 |
| --- | --- | --- | --- |
| REAL11-03 | `LAYOUT-CORE-VALUE-NOT-CLIPPED` | 概览, 运营看板 | CLOSED |
| REAL11-04 | `LAYOUT-BUTTON-SPACING` | 文件与共享, 设置 | CLOSED |
| REAL11-05 | `LAYOUT-ACTION-OVERLAP` | 设备 | CLOSED |
| REAL11-09 | `LAYOUT-EXTENSION-LONG-CONTENT` | 扩展程序 | CLOSED |

每行含：实现文件、证据文件绝对路径与完整 SHA256、页面、组合范围、检查数量、失败数量、
expected、actual、status、run_id、候选身份。

## 5. 尚未完成的（**不得宣布交付完成**）

| # | 项目 | 备注 |
| --- | --- | --- |
| 1 | **安装器视觉证据（绑最终 Setup）** | 从冻结的 1.7.4 Setup 取：全灭 / 过渡中 / Ready / 失败截图 + 实际安装录屏，绑定 `LanDisk-Setup.exe` 的 SHA256 `edbfb1ba…`；区分预览捕获与真实安装过程 |
| 2 | **B 类更新验证** | 修复后的 1.7.4 宿主以**动态端口**启动，由它发起更新与失败回滚；更高版本测试包标为测试用途；记录实际端口/监听范围/健康检查目标一致 |
| 3 | **隐私检查（针对实际交付集合）** | 对将要交付的文件逐一检查截图、录屏、文本、文件名、源码摘录与元数据；保留版本/提交/哈希/测试身份 |
| 4 | **正式报告、READY 标记与审核包** | 重新以 `-DeliveryDir dist\delivery` 运行 Full（当前 READY 是在隔离探测目录验证的），随后生成正式报告并封包；审核包需 Manifest / CRC / 成员集合 / 大小 / 完整 SHA256 + 外置 sidecar |
| 5 | `UI_FAILURES_11.tsv`、`ARTIFACT_HASHES.txt`、`CODE_REVIEW_11.md`、`DELIVERY_REPORT_11_VERIFIED.md` 等八份独立文件 | 由最终结果生成 |
| 6 | 安装器录屏与截图入审核包 | 与第 1 项同源 |

## 6. 续跑第一件事

1. **B 类更新验证**：用 `tools/Verify-HttpsEndToEnd.ps1 -Mode B`（需先补上 B 分支的执行体，
   当前脚本已实现 Matrix 与参数约定，A/B 分支为结构化占位）。要点：修复后的 1.7.4 宿主
   **以动态端口启动**，由它发起更新与失败回滚；测试包明确标注测试用途；
   记录配置端口与实际绑定端口、宿主传给更新程序的参数、实际监听与健康检查目标；
   断言回滚恢复到**失败更新之前的实际版本与服务端点**。
2. **安装器视觉证据**：对冻结的 1.7.4 Setup 跑一次捕获与真实安装录屏，取全灭/过渡中/Ready/失败截图，
   绑定 Setup SHA256。
3. **隐私检查**：对实际交付集合逐项检查并记录。
4. **正式封包**：`Invoke-ReleaseGate` 以 `-DeliveryDir dist\delivery` 运行 → READY 后写 `DELIVERY_READY.txt`
   → 生成正式报告 → 封审核包（含 `UI_PAGE_CATALOG.tsv`、原始验收结果、安装器截图与录屏、
   HTTPS A/B 与动态端口证据、历史隐私说明与最终隐私结果）→ 重新开包核对 Manifest / CRC / 成员集合 /
   大小 / SHA256 → ZIP 完整哈希放外置 sidecar。

**允许在同一批冻结产物上补齐证据后重新判定与 Finalize**；不得仅为再次进入封包流程而重新编译、
改变产物哈希使刚完成的证据失效。若确实改了影响发布字节的代码 → 更新候选身份并重建验收。

## 7. 边界与状态

- 最终最高状态仍为 **READY_FOR_DEVICE_VALIDATION**，不冒充 Owner 真机验收。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保持。
- 冻结 1.7.3 的浏览器/矩阵结果、诊断构建的布局结果、合成夹具结果**均保留原身份，未改标**。
- 历史失败、诊断结果、合成对照与正式候选验收分别标注。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
