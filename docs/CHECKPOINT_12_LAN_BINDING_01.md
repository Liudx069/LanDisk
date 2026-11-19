# LAN_DISK_12_REAL_LAN_BINDING_REPAIR_01 — 压缩状态

保存：2026-09-30　项目：`D:\LanDisk`

## 1. 位置

| | |
| --- | --- |
| HEAD | `28661e4d3e17236251d453aa43789342cfb1d8f8` |
| 工作树 | clean |
| 上一条已交付 | **1.7.6**@`2664239dd26e834abf7281c52dd9f1aac3e526ee`（四产物在 `dist/delivery`，**未被覆盖**） |
| 本条目标 | **1.7.7**（已改产品代码，**未升版、未构建、未验收、未封包**） |

1.7.6 精简包：`dist/delivery-final-code-repair-small/LAN_DISK_11_FINAL_CODE_REPAIR_SMALL_REVIEW_01.zip`，385,833 bytes，`ec12fd42…f53cb`。

## 2. 本条已确证的两个真机阻断根因（均已修，`28661e4`）

**根因 1 — 网络页用显示文案反推数据**
`Pages.cs:955` 写 `a.判定.StartsWith("使用")`；`Presenters.cs:131` 产出的是 **「用于手机访问」**（以「用」开头）。
→ `accepted` 永远为空 → 卡片永远渲染 `Ui.EmptyState("没有可用物理网卡")`，而同页「推荐访问地址」由 snapshot 正常给出 `192.168.2.2` → 页面自相矛盾。
修：`AdapterRow` 增加 `bool 接受`（由 `IsPhysical` 赋值），页面按**数据**过滤。

**根因 2 — 局域网关闭时仍推荐物理 HTTPS 地址**
`AdminOperations.RecommendedHttpsOrigin()` 只看网卡、不看是否在服务：`LanEnabled/HttpsEnabled` 皆 false、监听只有 loopback 时仍返回 `https://192.168.2.2:53820` 且 kind=`physical`。→ 手机必然连不上（**不是证书问题**）。
修：新增 `servingLanHttps = LanEnabled && HttpsEnabled`；不满足则返回 `null` + kind=`off`；`verified`/`stable` 分支语义不变；`Pages.RecommendedWithReason` 的 `_` 分支改为原样输出。

## 3. 已排除（查过，非缺陷）

- 设置**读取**：`LanDiskHost` / `ServerController` / `Program` 均 `JsonStore.Read(settings.json, () => new AppSettings())`；有文件即沿用用户值，**无启动覆盖路径**。
- 设置**写入**：`src` 内 `SaveSettings` 仅 `AdminOperations` 一处真实调用；参数 `bool?` 逐个 `?? 当前值`，replacement 逐字段复制，**不会重置 LAN/HTTPS**。
- **listener**：`lanHttps = LanEnabled && HttpsEnabled`，与现场"只有 loopback"吻合 → listener 行为正确，当时 `Settings.LanEnabled` 确为 false。
- **更新不动数据根**：`.lupd` 载荷是程序目录；`settings.json` 在数据根；`UpdateBackupScope` 仅在**回滚**时恢复它。

## 4. 待 Owner 确认（不猜）

Owner 机器 `Settings.LanEnabled / HttpsEnabled` 实为 **false**。代码中**未找到"升级重置"路径**。可能：该数据根下 `settings.json` 本来就是 false；或某次失败更新**回滚**恢复了更早版本（回滚恢复旧设置是**设计行为**）。
**未读取其 `settings.json`**（不得改 Owner 数据）。请查 `<数据根>\settings.json` 的 `lanEnabled` / `httpsEnabled` → 决定 1.7.7 是否需要额外迁移。

**注意**：只修根因 1 后，页面会如实把 192.168.2.2 列为「用于手机访问」，但 **`LanEnabled=false` 时 listener 仍不绑定它** → 1.7.7 现场仍需**打开局域网访问与 HTTPS**（或在确认后由迁移处理）。不得含糊。

## 5. 下一步（顺序）

1. 依据第 4 节结论决定是否加迁移。
2. 新增 A–G 定向回归：**A** 升级保持（旧 settings 开→仍开）　**B** 真实网卡判定（Ethernet 192.168.2.2+GW / WLAN 断开 / Hyper-V 在 / VPN 无 → 接受）　**C** VPN/TUN 在仍选 Ethernet　**D** listener 端到端（物理 IP HTTPS + loopback HTTPS + 管理端口仅 loopback）　**E** 状态一致（snapshot / listener / 推荐 / 网络页 / 概览）　**F** LAN 明确关闭 → 不建 LAN listener、UI 明示关闭、**不推荐不可监听地址**　**G** 网络刷新（unavailable→available 能重新发现；若设计要求重启则 UI 必须说明）
3. 全量回归（1.7.6 为 723 项）→ 失败就地修、复测。
4. 升 1.7.7 → 提交 → 干净树冻结 → 构四产物 → 完整验收 → 绑定表/权威报告 → Full → 隐私。
5. 精简包沿用**16 文件**结构，另加 **`REAL_LAN_BINDING_ROOT_CAUSE.md`**（真实根因 / 为何 1.7.6 自动化没抓到 / 具体修改 / 修复前后 listener 对照 / 设置迁移对照 / 网卡判定对照）+ 关键源码（`PhysicalLanSnapshot`、settings/migration、listener startup/binding、推荐地址计算、相关测试）。
6. 完成后停止，等 Owner 装 1.7.7 后**只**复测 `https://192.168.2.2:<实际HTTPS端口>/`，不重走全部真机步骤。

## 6. 已知会漏检的原因（写进根因文档）

- 根因 1 是**显示层字符串耦合**，任何布局/矩阵/夹具测试都不会覆盖"页面内部两处结论互相矛盾"。
- 根因 2 的单测只验证"有网卡时推荐物理地址"，**没有**验证"未在服务时不得推荐"。
- 1.7.6 的 630 组合矩阵是在**夹具网络**下跑的（`WLAN12-01_NATIVE_WLAN_CALLS=0`），天然看不到真实 LAN 绑定与推荐的一致性。

## 7. 边界

未操作 Owner 真实网络 / VPN / 防火墙 / 证书 / 配对 / 共享 / 数据；未触碰其它项目；未覆盖 1.7.6 产物。
`ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED` 保持；真机项继续 NOT_RUN。
