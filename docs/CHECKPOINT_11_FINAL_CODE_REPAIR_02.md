# LAN_DISK_11_FINAL_CODE_REPAIR_AND_ACCEPTANCE_01 — 增量断点（第二次）

保存时间：2026-09-30（本条额度用尽前）
项目：`D:\LanDisk`

---

## 1. 当前状态

| | |
| --- | --- |
| HEAD | `982d8050a3ee789dec9061ebd7aaca7252756312` |
| 工作树 | clean |
| 产品版本（源码） | **1.7.6**（已升版、**尚未构建**） |
| 已交付版本 | 1.7.5（`b598bb05…`，四产物在 `dist/delivery`，**未被覆盖**） |
| 最终状态 | **修复进行中 / 最终候选未构建 / 尚未放行** —— 不继承上一版 READY |

1.7.5 的历史构建身份以**实际产物内部记录**确定，四者一致：release `build-info.json`、
源码包 `SOURCE_HEAD.txt`、`.lupd` 的 `update-manifest.buildCommit`、runtime `reportedCommit`
**全部为 `b598bb055feb491837f964c45296d46b3928d2f6`**。

`0bc0f971ce46a28904efcb82a91390fec5e29850` 是**最后一次改动 `src/` 的提交**，用途是 `CODE_REVIEW` 的
**diff 基线**，不是构建身份；它同时也是 1.7.5 **第一次构建尝试**的提交 —— 那次因工作树不干净
（验收清单文件尚未提交）在 CAND-01 被判无效而中止。**旧 sidecar / 旧 run_id 里的构建字段是错的，
已按实际产物纠正**，不是"原本就正确"。

## 2. 本条完成的三项交付工具修正

### 2.1 报告与审计导出按实际表头读取字段
- 审计导出原先把 16 条需求**全部写死 CLOSED**，而绑定表实际只有 **6 个需求**（4 项布局 + 2 项运行检查）；
  其余需求根本没有绑定行 —— 那不是"已关闭"，是"没有证据行"。
- 改为解析表头按列名取 `requirement_id / assertion_id / combination_scope / status / page / …`；
  缺列直接报错，不静默取错列。
- **需求结论只看该需求的 REQUIRED 行**；INCIDENTAL 行为 `NOT_APPLICABLE` 时**不算通过、也不阻断**该需求。
  先前"任何一行不是 CLOSED 就判 BLOCKED"，于是 13–14 条 NOT_APPLICABLE 把 REAL11-03/04/05 拖成 **BLOCKED** —— 那正是
  "错误 BLOCKED 汇总"。修正后四项布局需求与两项运行检查**均为 CLOSED**，并分别列出记录行数、REQUIRED 数、
  NOT_APPLICABLE 数。
- 机械汇总：绑定表**记录行 48 条**（CLOSED 8、NOT_APPLICABLE 40）；**需求数量 6 项**；
  绑定表里没有的需求写 `NOT_IN_BINDING_TABLE`，绝不写成 CLOSED。

### 2.2 报告一致性检查（已修，可区分三种结果）
- 原先"选定原件"路径写死成上一轮的 `dist\delivery-174-final`，换候选后永远比不中 → 指示器**恒为 False**；
  而且只比 Manifest 的声明值 —— 那等于自己证明自己。
- 现在：**重新从磁盘打开封好的 ZIP**，解出该成员（解压本身校验 CRC），与选定原件按**完整字节**比较；
  选定路径由参数/交付目录决定，缺失即明确报缺文件。
- 结果分开：`PASS` / `REJECT_DIFFERENT` / `REJECT_MISSING_IN_PACKAGE` / `REJECT_MISSING_SELECTED`。
  **没有把指示器硬改为 True。**
- 新增 `tools/Test-ReportConsistency.ps1` 正反例，**4/4 PASS**：正确文件通过、换成不同内容被拒、
  包内缺成员被拒、选定原件缺失被拒。

### 2.3 同名负向结果选定唯一权威份
- 发现 **7 份**同名 `EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json`、**两种哈希**，且都不记录自己属于哪个候选，
  于是两份不同文件都自称"当前负向结果"。
- 生成器改为把 `candidateIdentity` 与 `runId` 写进结果，使每份自带身份；链测试同样处理。
- 权威份：`dist/delivery-175-final/EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json`
  （sha256 `48f47f96…`，`candidateIdentity=b598bb05…`，`runId=run-11-1.7.5-…`，9/9 PASS）。
  其余同名文件本地保留，逐份在 `dist/delivery-175-final/NEGATIVE_RESULT_AUTHORITY.md` 中标注**历史身份**。

以上脚本在发布路径上，已复制进 `tools/` 纳入版本控制（提交 `9f3541b`）。

## 3. 本段已完成的产品修复（上一段提交 `f3c066f`）

- `BoundedProcessRunner`（Core，真实产品代码）：并行消费 stdout/stderr，**一个**总时限覆盖"读完输出 + 进程结束"；
  超时只终止本次创建的进程（刻意不用 `entireProcessTree`），清理等待有界；只有两流读到结尾且正常退出才算完成。
- `Enabled` 三态：明确启用 / 明确禁用 / 字段缺失 / 无法识别，缺失与不可识别都是 `unknown`，**不再折成 false**。
- `FIREWALL14 ProcessRunnerTests`（8 项）+ `FIREWALL15 FirewallEnabledStateTests`（7 项），
  均经过产品实际使用的执行函数（进程不结束、输出流不关闭、大量 stdout/stderr 等）。
- 动画修复（`RackVisual`：脉冲与点亮同一条动画、阶段幂等）与其 `ANIM13` 专项（9 项）已在仓库中，本条**未重做**。
- 定向结果：防火墙 / 执行器 / 动画 **51 项全部通过**。

## 4. 本条未完成（**不得声称完成**）

| # | 项目 | 状态 |
| --- | --- | --- |
| 1 | 构建 1.7.6 四个产物 | **未开始**（源码已升版并冻结于 `982d805`） |
| 2 | 1.7.6 完整验收 | **未开始**：原 708 项 + 实际新增；630 组合与 WLAN 隔离；Chromium/WebKit；Setup；A 类正式 `.lupd` 升级回滚；B 类动态端口；HTTPS Matrix；runtime/源码身份/安装清单；防火墙与动画专项；Full 与隐私 |
| 3 | 1.7.6 绑定表与权威报告 | 未生成（现有的是 1.7.5 的） |
| 4 | **精简审核包 `LAN_DISK_11_FINAL_CODE_REPAIR_SMALL_REVIEW_01.zip`** | **未生成**（16 文件结构、目标 ≤5 MB） |
| 5 | 动画专项证据整理入包 | 未整理（代码与结果在仓库中） |

## 5. 续跑第一件事（按顺序）

1. 确认工作树 clean 于 `982d805`（已 clean）。
2. `pwsh -NoProfile -File tools\Build-All.ps1` —— 会跑 UI gate 与 Full 门禁；
   **Full 预期 NOT_READY**（尚无属于 1.7.6 的证据目录），这是正确的待验阶段，四产物已生成。
   注意：`Build-All` 只会删除**它自己重新生成**的文件（整目录删除的缺陷早已修复），
   但 `LanDisk-Setup.exe` / `LanDisk-win-x64.zip` / `LanDisk-source.zip` 会**被 1.7.6 覆盖** ——
   运行前请先把 1.7.5 的四产物复制到 `dist/_history/1.7.5-artifacts/`（1.7.4 的副本已在 `dist/_history/1.7.4-artifacts/`）。
3. 建立 `run-11-1.7.6-*` 证据目录并绑定矩阵；依次做浏览器、Setup、A 类、B 类、HTTPS Matrix、
   runtime、安装清单、防火墙/动画专项。
4. 生成绑定表与权威报告（`tools/New-VerifiedReports.ps1`，已修正字段与汇总）→ Full 判定 → 隐私检查。
5. 组装**精简包**（16 文件、≤20 成员、目标 ≤5 MB）到新暂存目录，输出
   `dist/delivery-final-code-repair-small/LAN_DISK_11_FINAL_CODE_REPAIR_SMALL_REVIEW_01.zip`；
   封包后用 `tools/New-FinalReviewBundle.ps1`（已修好的报告一致性检查会重开 ZIP 比对完整字节）
   逐成员核 CRC / Manifest / 大小 / SHA256 / 重复与危险路径 / 大小写冲突。
6. 详见 `docs/CHECKPOINT_11_FINAL_CODE_REPAIR_01.md` 中关于 16 个文件各自内容的说明。

## 6. 边界

- 未写 Owner 的防火墙规则，未扩大放行范围，未关闭防火墙，未自动提权；未操作真实数据、网络、端口、
  VPN、路由器、WiFi、证书、共享、配对或扩展批准；未触碰其它项目。
- 最高状态仍为 **READY_FOR_DEVICE_VALIDATION**；`ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保持；
  真机项（真实 iPhone、真实切网、跨显示器、真实 UAC 写规则）继续 NOT_RUN。
- 保留：1.7.5 四产物、1.7.4 历史副本、1.7.5 审核包、全部原始证据。未清空任何交付目录。
