# LAN_DISK_11_FINAL_CODE_REPAIR_AND_ACCEPTANCE_01 — 增量断点（第三次）

保存时间：2026-09-30（本条额度用尽前）
项目：`D:\LanDisk`

---

## 1. 当前状态

| | |
| --- | --- |
| HEAD | `2664239dd26e834abf7281c52dd9f1aac3e526ee` |
| 工作树 | clean |
| **产品构建提交** | `2664239dd26e834abf7281c52dd9f1aac3e526ee`（= HEAD） |
| 源码冻结点（最后一次改 `src/`） | `982d8050a3ee789dec9061ebd7aaca7252756312` |
| 产品版本 | **1.7.6** |
| run_id | `run-11-1.7.6-20260930-122945` |
| 最终状态 | 自动化验收**全部成立**；精简包**尚未生成** |

**三个提交的角色不同，已分别记录**：产品构建提交 = `2664239`（四产物内部记录一致指向它）；
源码冻结点 = `982d805`（其后只有改测试/文档/交付工具的提交）；已审阅基线 = `a9b06eb`（1.7.4）。

## 2. 本段完成的 1.7.6 构建与验收

### 2.1 构建中暴露并修复的真实失败（不是"预期 NOT_READY"）
`Repair02VerifiedHostTests.No_route_dns_or_vpn_configuration_write_was_added` 失败：
它断言"启动 netsh 进程的文件 = {FirewallStatus.cs, FirewallHelper.cs}"，实际只剩后者 ——
因为执行链重构把进程启动集中到 `BoundedProcessRunner`，外壳不再 `new ProcessStartInfo("netsh")`。

按**新的真实结构**更新该门禁且不放松（提交 `2664239`）：
仍要求 netsh 只出现在两处；改为按"谁把 netsh 交给执行器"判定驱动方；
**新增更严的一条**：执行器代码行里不得出现 `netsh`/`advfirewall`（只看代码行，文档注释不算）。
不变量本意（产品不得写路由/DNS/VPN；netsh 只用于防火墙）未被削弱。修好后 **723/723 通过**。

### 2.2 四产物（现场复算）

| 产物 | bytes | 完整 SHA256 |
| --- | --- | --- |
| `LanDisk-Setup.exe` | 218,364,071 | `cdef06131412e0f70ef4f67c156f23f7975bdc5641691868fcec22e57108f594` |
| `LanDisk-win-x64.zip` | 148,773,600 | `2b5c72bdbe042690e9f77d08caa930a871a812bfd3065566c4b62c54ff0566d2` |
| `LanDisk-update-1.7.6.lupd` | 148,793,577 | `61b7dbccaabf25febbb305614e7cc8519b76ddcc0e5a0e38ee1d11e08fcb244a` |
| `LanDisk-source.zip` | 2,067,397 | `0e925cc1898803d658b709c78ac5d7fe9879cf2f1d34593a2c33d0b79a9b5c3f` |

内部身份：release `build-info.json` = `1.7.6 / 2664239`；源码包 `SOURCE_HEAD.txt` = `2664239`；
`.lupd` `update-manifest.buildCommit` = `2664239`；runtime `reportedCommit` = `2664239`。

### 2.3 验收结果（全部绑定本候选）

| 项目 | 结果 |
| --- | --- |
| 单元回归 | **723 / 723**（708 + 15 新增执行器/Enabled 测试） |
| 桌面矩阵 | **630 / 630**，15 页，WLAN 夹具（原生调用 0、哨兵 PASS） |
| Chromium / WebKit | **40 / 40 · 40 / 40** |
| Setup 安装与迁移 | **20 / 20**，NOT_RUN 1（真实 UAC） |
| A 类（交付的正式 `.lupd` 升级与回滚） | **41 / 41** |
| B 类（动态端口更新与失败回滚） | **PASS**（配置 0/0 → 实际 53819/53820 → 重启同端口 → 回滚 1.7.6@53819） |
| HTTPS 监听矩阵 | **4 / 4** |
| runtime 身份 / 安装清单 | 双匹配 / **15 / 15** |
| 安装器截图与录屏 | 9 状态（9/9）+ 真实安装（退出码 0、GIF 8 互异帧、ANIMATED） |
| 防火墙 / Enabled / 动画专项 | **55 / 55** |
| 聚合器负向 | **9 / 9** |
| 报告一致性正反例 | **4 / 4** |
| **Full 判定** | **READY** |
| **隐私检查** | **PASS**（0 命中） |

### 2.4 一次偶发失败（如实记录）
Chromium 首次运行 `UI-ADMIN` 失败（"管理端未自动进入：标签数 0，密码框数 0"），WebKit 同项通过。
**原样复测一次即 40/40 全部通过**。结论：页面加载/观察时序的偶发，不是产品缺陷；
采用复测后的 40/40，并把首次偶发写入 `07-browser/BROWSER_RESULTS.md`，未删除、未掩盖。

## 3. 本段未完成

| # | 项目 | 说明 |
| --- | --- | --- |
| 1 | **精简审核包 `LAN_DISK_11_FINAL_CODE_REPAIR_SMALL_REVIEW_01.zip`** | **未生成**。组装脚本 `dist/tools/Assemble-SmallBundle.ps1` 已写好并在跑第 4 步（身份证据）时抛 `参数 'Path' 为 null`，**尚未定位**；诊断已确认 `$zipPath/$srcZipPath/$lupdPath/$rtPath/$evidence` **均非 null**，故障点在步骤 4 内部的某个 cmdlet 调用。暂存目录只落了 3 个文件 |
| 2 | 动画专项证据整理入包 | 随第 1 项 |
| 3 | 正式 Finalize/READY 标记 | 未跑 `Finalize-Delivery`（精简包不走它） |

**注意**：`03_IDENTITY_EVIDENCE.md` 依赖的 `ZipEntryText` 用**精确成员名**匹配，
而 release ZIP 里 `build-info.json` 位于 `LanDisk-win-x64/build-info.json`（嵌套），
当前写法匹配不到、会返回"(包内没有 …)"——**这条也要一并修**（改用 `-or $_.Name -eq $name`）。

## 4. 续跑第一件事

修 `dist/tools/Assemble-SmallBundle.ps1` 第 4 步：
1. `ZipEntryText` 改用 `$_.FullName -eq $name -or $_.Name -eq $name`（嵌套成员）；
2. 在第 4 步内部逐句加 `Write-Host` 定位那个 null（`Section`/`Sha`/`ReadText` 已加空值保护，
   故障应在 `$manifestObj = $manifest | ConvertFrom-Json` 或 `ZipEntryText` 的 `OpenRead`/`StreamReader` 上——
   `StreamReader` 与 `ZipFile.OpenRead` 的参数名都叫 `path`，收到 null 时正是这个报错文案）；
3. 通过后按已定的 16 文件结构出包，用 `tools/New-FinalReviewBundle.ps1`
   （**已修好的报告一致性检查会重开 ZIP 比对完整字节**）逐成员核 CRC / Manifest / bytes / SHA256 /
   重复、大小写冲突、危险路径。

**不要**为此重新编译产品 —— 1.7.6 的验收已全部成立，只需补封包材料。

## 5. 保全核对

- 1.7.5 四产物：**逐文件 bytes + 完整 SHA256 与冻结记录一致**，存于 `dist/_history/1.7.5-artifacts/`（含 `SHA256SUMS.txt`）。
- 1.7.4 四产物：`dist/_history/1.7.4-artifacts/`（5 个文件）。
- 1.7.5 审核包：`dist/delivery-final-code-repair/…BUNDLE_01.zip` 仍在。
- 原始证据：`dist/evidence-11/` 全部保留；未清空任何交付目录。
- 本段未写 Owner 的防火墙规则、未关闭防火墙、未扩大放行、未自动提权；
  未操作真实数据、网络、端口、VPN、路由器、WiFi、证书、共享、配对或扩展批准；未触碰其它项目。
- 最高状态 **READY_FOR_DEVICE_VALIDATION**；真机项继续 NOT_RUN；
  `ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保持。
