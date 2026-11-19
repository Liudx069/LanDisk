# LAN_DISK_11_FINAL_CODE_REPAIR_AND_ACCEPTANCE_01 — 增量断点

保存时间：2026-09-30（本条额度用尽前）
项目：`D:\LanDisk`

---

## 1. 身份冲突已查清 —— 不是冲突，是两个不同角色

以**实际产物内部记录**为准（不是报告参数、也不是当前 HEAD）：

| 来源 | 记录的提交 |
| --- | --- |
| release ZIP 的 `build-info.json` | `b598bb055feb491837f964c45296d46b3928d2f6`，version 1.7.5 |
| 源码包 `SOURCE_HEAD.txt` | `b598bb05…`，worktreeClean True |
| `.lupd` 的 `update-manifest.json` | `buildCommit = b598bb05…`，keyId `ac3ff1673b23518a` |
| runtime 原始结果 | `reportedVersion 1.7.5`、`reportedCommit b598bb05…`、`releaseZipSha256 a531b15a…` |

四个产物互相一致：**构建提交 = `b598bb05…`**。

`0bc0f971ce46a28904efcb82a91390fec5e29850` 是 **最后一次改动 `src/` 的提交**（即 1.7.5 的升版提交），
它在 `CODE_REVIEW` 里是 **diff 基线**，不是构建身份。两者用途不同，并非矛盾。
补充事实：`0bc0f97` 也是 1.7.5 **第一次构建尝试**的提交，那次因为工作树不干净（`docs/DEVICE_ACCEPTANCE_CHECKLIST_175.md`
尚未提交）在 CAND-01 被判定为无效候选并中止；随后提交该文件、从干净工作树重建，才得到 `b598bb05` 这一批产物。
`.frozen-candidate-175.txt` 已更正为 `b598bb05`。

1.7.4 的四个历史产物已先保全于 `dist/_history/1.7.4-artifacts/`（副本哈希逐项一致），未被覆盖。

## 2. 本条完成的产品修复（真实缺陷）

### 2.1 执行顺序缺陷 —— 确有其事，已修
原 `FirewallStatus.RunNetsh`：`ReadToEnd(stdout)` → `ReadToEnd(stderr)` → `WaitForExit(15000)`。两个问题超时都救不了：

- `ReadToEnd` **自身没有超时**。子进程不关闭 stdout 时第一步永不返回，后面的 15 秒 `WaitForExit` 根本执行不到 → **界面会一直等下去**。
- 顺序读**会死锁**：stderr 管道缓冲写满后子进程阻塞在写，我们阻塞在读 stdout。

**修复**：新增 `src/LanDisk.Core/Networking/BoundedProcessRunner.cs`（真实产品代码，`FirewallStatus` 调用它）：
并行消费两个流并与"进程退出"一起等待，**一个**总时限覆盖"读完输出 + 进程结束"；超时只终止**本次创建的进程**
（刻意不用 `Kill(entireProcessTree:true)`——`netsh` 无子进程，PID 复用后按树杀可能命中无关进程），
且被终止进程的等待也有界（3 秒）；只有两流读到结尾且正常退出才算 `Completed`，否则 `StdOut` 不得当作完整清单；
非零退出 / 空输出 / 启动失败 / 读取失败各有明确原因，不抛异常、不让调用方无限等待。

### 2.2 Enabled 三态 —— 确有其事，已修
原 `parse` 用 `sawEnabled ? enabled : false`，把"字段缺失"和"值无法识别"都折成 `false`，于是被判成**明确禁用**。
现 `FirewallRuleState.Enabled` 为 `bool?` 并保留 `EnabledText`：明确 Yes/No（含"是/否"）→ true/false；
缺失 → `unknown:缺少 Enabled 字段`；无法识别 → `unknown:Enabled 字段无法识别（实际 …）`。只有明确 `false` 才判 `disabled`。
此前补齐的程序路径/方向/动作/Profile/远端范围/协议/端口与同名多规则判定**全部保留**。

### 2.3 测试（均经过产品实际使用的执行函数）
- `FIREWALL14 ProcessRunnerTests`（8 项）：正常完成；非零退出；**进程不结束**（实测在时限附近返回，不等子进程跑完）；
  **输出流不关闭**；大量 stdout（>1MB）不卡死；**大量 stderr（>1MB，顺序读会死锁的那一种）**；两路同时大量输出；
  空输出可配置；可执行文件不存在不抛异常。
- `FIREWALL15 FirewallEnabledStateTests`（7 项）：明确 Yes 通过；明确 No 为 disabled；**缺字段为 unknown 且未被折成 false**；
  无法识别的值保留原文并判 unknown；本地化取值显式处理；同名一启用一禁用不得被掩盖。

定向结果：防火墙 / 执行器 / 动画 **51 项全部通过**。

## 3. 本条未完成（**不得声称完成**）

| # | 项目 | 状态 |
| --- | --- | --- |
| 1 | 动画修复的**核对与保留** | 代码与专项已在仓库中（`RackVisual` + `ANIM13` 9 项通过）；本条未重做，也**尚未**把 `ApplyStage`/`SetGroup`/`RiseSchedule`/失败/ReduceMotion 的完整代码与结果整理进精简包 |
| 2 | 报告 / 审计导出的字段与汇总修正 | 上一条已修绑定表状态列（按表头取 `status`）与 `UI_CAPTURE_MISSING`；本条**尚未**按实际表头统一 `expected/actual/status`、修正审计导出的字段错位与错误 BLOCKED 汇总，也未区分"四项布局需求 / RUN-ENV / RUN-MATRIX / 绑定记录行" |
| 3 | "报告一致性"检查修复 | **未完成**。当前指示器仍为 False（上一条已查明是查清单键的实现问题，包内报告与原件哈希实测相同 `6c689ecc…`）。要求是"从 ZIP 实际读取所选报告、与正确原件比较；正确文件通过、不同文件拒绝、缺文件拒绝"，**不能把指示器硬改为 True** |
| 4 | 同名负向结果选定唯一权威份 | 未完成（`dist/evidence-11` 下多份 `EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json`，哈希不同，需选定当前权威并给其余标历史身份） |
| 5 | 新产品候选冻结与完整验收 | **未开始**。本条改了产品代码（`src/`），按原授权必须形成**下一补丁候选**（不覆盖已交付的 1.7.5 字节），再统一冻结、构建、完整验收 |
| 6 | 精简审核包 `LAN_DISK_11_FINAL_CODE_REPAIR_SMALL_REVIEW_01.zip` | **未生成** |

## 4. 续跑第一件事

1. 完成第 3 节第 2、3、4 项（都属工具与交付材料，**不需要**升版或重编译产品）。
2. 因第 2 节已改产品代码：升版（如 1.7.6）→ 提交 → 干净工作树 → 冻结构建身份 → 构建四个新产物
   → 对**同一批精确产物**执行完整验收（原 708 项 + 新增；630 组合与 WLAN 隔离；Chromium/WebKit；
   Setup；A 类正式 `.lupd` 升级与回滚；B 类动态端口；HTTPS Matrix；runtime/源码身份/安装清单；
   防火墙与动画专项；Full 与隐私）。1.7.5 的结果**不得**改标给新候选。
3. 生成**精简审核包**（16 个文件、≤20 个成员、目标 ≤5 MB），按本条的目录与章节要求整理：
   含 `03_IDENTITY_EVIDENCE.md`（原文收录四产物的内部身份记录并解释 `0bc0f97` 与 `b598bb05` 的关系）、
   `05_KEY_PRODUCT_SOURCE.md`（含 `BoundedProcessRunner`、`Evaluate`、`SetGroup`、超时清理的完整代码）、
   `07_FIX_DIFF.patch`（**以已审阅的 1.7.4 基线为起点**，并标注 1.7.5 之后的增量）、
   `09_UI_MATRIX_RAW.md`（只收文本证据，不收 630 张 PNG）等。
4. 封包后重开 ZIP 逐成员核 CRC / Manifest / 大小 / SHA256 / 重复与危险路径 / 大小写冲突，
   并**从 ZIP 实际读取**权威报告与选定原件比较。

## 5. 边界

- 未写 Owner 的防火墙规则，未扩大放行范围，未关闭防火墙，未自动提权；未操作真实数据、网络、端口、
  VPN、路由器、WiFi、证书、共享、配对或扩展批准；未触碰其它项目。
- 最高状态仍为 **READY_FOR_DEVICE_VALIDATION**；`ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保持。
- 真机项（真实 iPhone、真实切网、跨显示器、真实 UAC 写规则）继续 NOT_RUN。
- 当前 HEAD `f3c066f9625052c148bb65f0d5737909991807fa`，工作树 clean。
