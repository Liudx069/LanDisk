# LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02 — 增量断点（第十五段）

保存时间：2026-09-30（第十五段额度用尽前）
项目：`D:\LanDisk`

---

## 0. 本段发生的数据丢失事件（必读）

`tools/Build-All.ps1:232` 原先执行 `Remove-Item $deliveryDir -Recurse -Force`（整目录删除）。
1.7.4 构建时它把交付目录连同**上一轮已交付的材料**一起删掉了：

- **1.7.3 历史审核包** `LAN_DISK_11_EXACT_CANDIDATE_EVIDENCE_REPAIR_01_REVIEW_BUNDLE.zip`
  （516,033,414 字节，SHA256 `44b549c6…`）
- 它的 `.sha256.txt` sidecar
- 该历史隐私说明的交付副本

`-Force` 绕过回收站，**无法恢复**。

**已修复**：`Build-All.ps1` 改为只删除它自己重新生成的文件（四个产物 + 报告 + 身份与门禁文件 +
`ui-evidence`），不再删整个目录。发布脚本应当只拥有自己的输出。

**已重建**：打包暂存目录 `dist/.bundle-173-stage` 幸存（132 个文件），据此重建副本
`dist/_history/…REVIEW_BUNDLE.REBUILT.zip` —— 515,554,535 字节、132 个成员、
SHA256 `dce206a0f23b40867cd02f7af42d00a893395f96edf4816a8fb118749113636e`。
**它与原件不是同一份字节**（ZIP 元数据不同），因此不能用它的哈希印证任何引用原哈希的记录。

**已更正**：外置隐私说明新增第 0 节完整记录此事，并声明"凡引用 `44b549c6…` 之处指向的都是已被销毁的原件"。
说明同步存于 `docs/`（受控）与 `dist/_history/`（与重建副本同处）。

**未受影响的**：1.7.4 的四产物在缺陷发生之后、修复之前生成，字节未变；已完成的 1.7.4 验收证据均未涉及被删文件。

---

## 1. 候选与产物

| | |
| --- | --- |
| 版本 | **1.7.4** |
| 构建提交（产物与全部证据所属） | `a9b06eb6ed0b15744556b31fc156748b93cc27dc` |
| 当前 HEAD | `a6f46f6957058d17bcb73ef54f041e0966fb59f8`（其后仅 `tools/Build-All.ps1` 工具修复 + 文档） |
| 工作树 | clean |
| 本候选 run_id | `run-11-1.7.4-20260930-031659` |

`tools/Build-All.ps1` 属构建工具、**不随产品发布**，其改动**不改变**四产物的字节；
候选身份仍以 `a9b06eb` 为准。

| 产物 | bytes | 完整 SHA256 |
| --- | --- | --- |
| `LanDisk-Setup.exe` | 218,338,344 | `edbfb1ba67c2d71fcef082008c513bc443b2faf014b29be3222bba87efed2de0` |
| `LanDisk-win-x64.zip` | 148,753,366 | `1768473e713689e288da913f945852e623fbd1a0c2445387ae2f0fcd422e1517` |
| `LanDisk-update-1.7.4.lupd` | 148,773,334 | `58de0a1ff097c6ab2d5758fc2b697aad33813aab180638baea6c11844f4adc36` |
| `LanDisk-source.zip` | 1,973,048 | `c01605fa888ae8fc47fb1eac189b491baf88b068368470f8c28a1cb6d8f8029c` |

包内身份 `version=1.7.4`、`commit=a9b06eb…`、`buildId=Release/win-x64`；
候选身份核对 19/19、源码包完整性 9/9。

## 2. 已完成的 1.7.4 验收（全部绑定本候选）

| 项目 | 结果 |
| --- | --- |
| 单元回归 | **677 / 677 PASS** |
| 桌面矩阵 | **630 / 630 PASS**（15 页、漏页 0、重复 0、额外 0），由 1.7.4 Build-All 产出 |
| WLAN 输入隔离 | 原生调用 0、夹具哨兵 PASS、检查控件 44,673 |
| 页面身份目录 | 15 页，version 1.7.4 / commit `a9b06eb` |
| Chromium / WebKit | **40 / 40 · 40 / 40**（含 MOBILE12-11，页面自发链路；服务器 `LanDisk.Server.exe` sha256 `ae1e3955…`） |
| Setup 迁移 | **20 PASS / 0 FAIL / 1 NOT_RUN** |
| A 类更新与回滚 | **41 / 41 PASS**，直接用交付包 `58de0a1f…` |
| HTTPS 监听矩阵 | **4 / 4 PASS** |
| runtime identity | `1.7.4` / `a9b06eb` 双匹配 |
| install manifest | **15 / 15 PASS** |
| **Full 发布判定** | **READY（exit 0）**（隔离探测目录验证） |
| 正式绑定表 | 已生成，REAL11-03/04/05/09 均 CLOSED |

## 3. 剩余项（**不得宣布交付完成**）

| # | 项目 |
| --- | --- |
| 1 | **安装器视觉证据**：从冻结的 1.7.4 Setup 取全灭 / 过渡中 / Ready / 失败截图 + 实际安装录屏，绑定 `LanDisk-Setup.exe` SHA256 `edbfb1ba…`；区分预览捕获与真实安装过程 |
| 2 | **B 类更新验证**：修复后的 1.7.4 宿主以**动态端口**启动并发起更新与失败回滚；测试包标为测试用途；`tools/Verify-HttpsEndToEnd.ps1 -Mode B` 的执行体尚待补 |
| 3 | **隐私检查（针对实际交付集合）**：截图、录屏、文本、文件名、源码摘录、元数据 |
| 4 | **正式放行与封包**：以 `-DeliveryDir dist\delivery` 重跑 Full → 写 `DELIVERY_READY.txt` → 生成 `DELIVERY_REPORT_11_VERIFIED.md` 等八份独立文件 → 封审核包（含 `UI_PAGE_CATALOG.tsv`、原始验收结果、安装器截图与录屏、HTTPS A/B 与动态端口证据、历史隐私说明与最终隐私结果）→ 重新开包核对 Manifest / CRC / 成员集合 / 大小 / SHA256 → ZIP 完整哈希放外置 sidecar |
| 5 | `UI_FAILURES_11.tsv`、`ARTIFACT_HASHES.txt`、`CODE_REVIEW_11.md` 等尚未生成 |

## 4. 续跑第一件事

**安装器视觉证据**（第 1 项）：对 `dist/delivery/LanDisk-Setup.exe` 跑
`dist/tools/InstallerRecorder`（UI Automation 驱动真实安装 + `PrintWindow` 抓帧）
与 `--render-ui` 状态捕获，取全灭 / 过渡中 / Ready / 失败截图与实际安装录屏，
全部绑定 `edbfb1ba67c2d71fcef082008c513bc443b2faf014b29be3222bba87efed2de0`。
随后补 B 类更新验证执行体，再做隐私检查，最后正式放行与封包。

**允许在同一批冻结产物上补齐证据后重新判定与 Finalize**；不得仅为再次进入封包流程而重新编译、
改变产物哈希使刚完成的证据失效。

## 5. 边界

- 最终最高状态仍为 **READY_FOR_DEVICE_VALIDATION**，不冒充 Owner 真机验收。
- `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保持。
- 冻结 1.7.3 的浏览器/矩阵结果、诊断构建的布局结果、合成夹具结果**保留原身份，未改标**。
- 不改 Owner 真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对、扩展批准；不触碰其它项目。
- 执行：主执行 1、子智能体 0、外部付费 API 0；DSH 模型配置不可见记 UNKNOWN。
