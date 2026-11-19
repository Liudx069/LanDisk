# AUDIT_CORRECTIONS_09_REPAIR_01 — 首次连接向导的收口、完成判断与一键开启

本文件对应 `LAN_DISK_NATIVE_PARITY_NETWORK_UX_HOTFIX_09_REPAIR_01`，基线 HEAD `d51996f9`
（09 轮最终候选）。它是 09 轮的**最后一处修复轮**：只修三件事，全部落在"首次连接向导"上。
04–09 轮的报告保持原样，未作任何修改。

版本号从 1.6.0 升到 **1.6.1**，因为 1.6.0 已经产出过正式候选产物（Setup / ZIP / `.lupd` 与审核包）。
产物内容变了就必须换版本号，否则"用户装到的是哪一个 1.6.0"无法回答。

---

## 1. 三处缺陷，以及它们为什么能躲过 09 轮的门禁

09 轮的门禁盯的是"同一个动作在原生窗口和 HTTP 端点上是否给出相同答案"。这三处都不属于那个形状：
它们是**只实现了一半的流程**，而且半成品的那一半在窗口里，不在共享层里。

| # | 缺陷 | 用户看到的后果 | 为什么 09 轮没抓到 |
|---|---|---|---|
| 1 | 配对成功后没有人关掉 8890 上的明文引导页 | 手机已经配对好了，本机还在局域网上多开着一扇临时明文窗口，要用户自己去点"关闭向导并停止引导页" | 关掉它的动作在窗口的按钮里；HTTP 路径上根本没有这个按钮，所以"两条路径一致"的对照测试不会失败 |
| 2 | 用"设备列表里有没有未撤销设备"判断向导是否完成 | 电脑上已经有 iPhone A 时，给 iPhone B 打开的向导立刻显示"已完成"，而 B 什么都没做 | 这是窗口里的一个表达式，不是共享方法；单元测试测不到窗口 |
| 3 | 打开"连接 iPhone"页面后还要再点一下"开始首次连接向导" | 证书二维码不会出现，"一键连接"实际是两键 | 同上：开启向导的调用点只在窗口里 |

可以把这一轮理解成把 09 轮"少点两步"的承诺补完：09 轮去掉了手机上点"继续"和电脑上点
"生成配对请求"两步，但没有把**开启向导本身**和**结束向导本身**收进同一处。

**关于第 1 条的一个细节，值得写清楚**：09 轮的实现里确实有一条"手机完成证书信任后关掉引导页"的
规则（`LanDiskHost` 的 30 秒巡检）。但那**不是**"配对成功"这个事件：它在更早的信任步骤就触发，
而且一旦 HTTPS 探测因为任何原因没有发生（手机装了描述文件但没打开完全信任、mDNS 解析不到稳定名称、
探测被网络策略挡掉），8890 就会一直开到 20 分钟超时。也就是说：09 轮报告里"配对成功后引导页与 8890
端口会关闭"这句话，在真实流程里并不由"配对成功"保证，也不在两条路径上都成立。本轮把它变成真的。
那条信任规则本身**保留**：手机一旦能走 HTTPS，明文引导页确实已经完成使命。

## 2. 修复 1：配对成功即收口，且只在共享层收口

`AdminOperations.ConfirmPairing` 的最后（第 7 步写审计之后）增加：

```csharp
if (_runtime.Onboarding.IsActive)
{
    _runtime.Onboarding.Stop("配对成功，首次连接向导已自动结束");
}
```

三个位置上的约束都是刻意的：

1. **在共享层**，所以原生窗口的"确认配对"、HTTP 的 `POST /api/admin/pairing/{id}/confirm`、
   以及设备页上的同一个按钮，行为必然一致——它们本来就是同一个方法。窗口里不再有任何停止引导页
   的代码，`AdminEndpoints` 里也没有（源码闸门 `REPAIR09-01c` 守住这两点）。
2. **在设备落库与 `pairing.IssuedDeviceId = device.Id` 之后**，所以任何在上面被拒绝的确认
   （请求不存在 404 / 状态不符 409 / 已过期 410）都不会关掉用户正站在里面的那个向导。
   `REPAIR09-01b` 把这三种拒绝逐条跑了一遍，并断言 `IsActive` 与 `BootstrapId` 都没变。
3. **只关引导页**：`Onboarding.Stop` 释放的是 8890 上的 TCP 监听，HTTPS（8892）、已配对设备的会话、
   稳定 `.local` 名称与 mDNS 都不经过它（`REPAIR09-07` 在关闭之后继续用 HTTPS 读共享列表）。

`REPAIR09-02` 不用"字段里没有监听了"来证明，而是先 GET 一次引导页拿到 200，确认后再 GET 同一个
地址，必须**连不上**；最后再把那个端口重新绑定一次，必须成功。

## 3. 修复 2：完成判断绑定"本次配对请求"

窗口里原来的判断是：

```csharp
Runtime.Devices.All().Any(d => !d.Revoked)   // 有未撤销设备 ⇒ 向导完成
```

它回答的是另一个问题。共享层新增：

```csharp
public PairingCompletion CompletionOf(string? pairingId)
{
    if (string.IsNullOrWhiteSpace(pairingId)) return PairingCompletion.NotCompleted;
    PairingRequest? request = _runtime.Pairing.Find(pairingId);
    if (request is null || request.State != PairingState.Redeemed
        || string.IsNullOrWhiteSpace(request.IssuedDeviceId))
        return PairingCompletion.NotCompleted;
    DeviceDefinition? device = _runtime.Devices.Find(request.IssuedDeviceId);
    if (device is null) return PairingCompletion.NotCompleted;
    return new PairingCompletion(true, device.Id, device.Name, device.Level.ToString());
}
```

于是"完成"是**一个请求的属性**：该请求已 `Redeemed`、已发放设备、设备确实还在。
`IphonePage` 记住本次向导产生的请求 id（`_pairingId`），刷新时先问 `CompletionOf(_pairingId)`，
完成就显示"配对完成"面板并停下，否则继续显示向导。

页面实例是每次进入页面时新建的（`MainWindow` 每次导航都 `entry.Create()`），所以下一次要给另一台
手机配对时，那个面板不会粘住页面——重新进入本页就是一个全新的向导。

`REPAIR09-03` 把最有说服力的那条摆在测试里：先把 iPhone A 配好，此时
`Devices.All().Any(d => !d.Revoked)` **为真**，然后断言 B 的向导 `Completed == false` 且仍在运行；
B 确认之后只有 B 完成，A 的名称、权限与未撤销状态一个都没变。

## 4. 修复 3：打开页面就是"一键"

共享层新增：

```csharp
public OnboardingStatus EnsureOnboardingStarted()
{
    OnboardingStatus status = _runtime.Onboarding.Status();
    if (status.Active) return status;                     // 已经在跑：原样返回，绝不重启
    if (_runtime.Pairing.Active().Count > 0) return status; // 有正在进行的配对流程：不要插手
    return _runtime.Onboarding.Start();
}
```

`IphonePage.OnShown()` 调用它（在 `try/catch` 里，把异常写进外壳日志，不让页面打不开）。
`Build()` 时页面还没有二维码，`OnShown` 之后第一次 `RefreshAsync` 就已经有了 —— 这就是"一键"。

**第二个判断条件不是可选的，它是这一轮最容易踩的坑。** 手机完成证书信任后，`LanDiskHost` 的巡检会
把引导页关掉（那是它的使命终点），而此时配对请求还很可能是 live 的。如果此时再开一个新向导：

- `OnboardingService.Start()` 会把 `Trusted` 复位为 `false`；
- 于是窗口会退回到"等待信任"那一步，配对二维码消失；
- 手机上正在轮询的 trust-probe 用的是**旧**的 bootstrap id，永远不可能再匹配上。

结果就是用户卡在半路，而且看起来像"信任又失效了"。所以只要还有 live 的配对请求，这个方法就什么都不做；
真要重来，用界面上显式的**"重新生成引导二维码"**（`REPAIR09-05b` 同时验证这两件事：
live 请求时 `EnsureOnboardingStarted` 不重启，而手动 `Start()` 仍可用）。

另外两条入口都保留，且都不是必须点的：

- **重新生成引导二维码** —— 恢复入口：二维码过期、换手机、流程中断时重来；
- **结束向导** —— 显式取消：想立刻关掉局域网上的明文窗口时用它。

`REPAIR09-05` 连续调 6 次 `EnsureOnboardingStarted`，断言 `BootstrapId`、`BootstrapUrl` 与监听列表
全部不变——页面的 1.5 秒刷新、手动"刷新"、以及再次进入页面都走这条路径。

## 5. 八项硬门禁与它们的证明方式

| 编号 | 断言 | 怎么证明的 |
|---|---|---|
| REPAIR09-01 | 确认成功自动停止向导 | 真实回环端口上的完整流程：手机经 `/api/pair/code` 认领 → 共享层确认 → `IsActive=false`、`BootstrapId` 与过期时间清空，手机轮询立刻拿到 `paired` |
| REPAIR09-02 | 8890 监听真的没了 | 确认前 GET 引导地址返回 200（含 CA 页面），确认后同一地址连接失败，且该端口可以重新绑定 |
| REPAIR09-03 | 已有 A 不影响 B | 断言"存在未撤销设备"为真的同时 `CompletionOf(B)=false`；B 经 HTTP 端点确认后才为真，A 逐字段不变 |
| REPAIR09-04 | 无向导时自动开启一次 | `EnsureOnboardingStarted` 返回 Active，且新引导地址**立即可访问**（200 且页面含该 bootstrap id） |
| REPAIR09-05 | 重复调用不重启 | 连续 6 次调用后 `BootstrapId` / `BootstrapUrl` / 监听列表均不变 |
| REPAIR09-06 | 拒绝不算完成、可重试 | 经 `DELETE /api/admin/pairing/{id}` 拒绝：`CompletionOf=false`、请求置 `Cancelled`、向导仍 Active、无设备；随后新请求确认成功并收口 |
| REPAIR09-07 | HTTPS 不受影响 | 整条配对走 HTTPS；关闭向导后 `/api/health`、`/api/me`、共享列表都仍可用，8891/8892/8890 常量与稳定名称不变 |
| REPAIR09-08 | 明文页仍无秘密 | 内存渲染版与实际 HTTP 返回版都核对：不含令牌 / claimSecret / 管理令牌 / cookie / csrf / 管理入口；含公开 CA 与 trust 探测；一次性令牌不出现在任何指向 8890 的链接里 |

**诚实的边界**：`REPAIR09-04` / `REPAIR09-05` 的"点击一次原生按钮"这件事，本工程的测试**做不到** ——
WPF 外壳是 WinExe，测试工程不引用它。因此这两项分两层：行为层验证共享决策方法（这是窗口唯一会调的
东西，也是唯一做决定的地方），源码层（`REPAIR09-04b`）验证窗口里那条旧的
`Devices.All().Any(...)` 规则与"关闭向导并停止引导页"按钮已经不存在、`OnShown` 里调的确实是
`EnsureOnboardingStarted()`。源码闸门证明"那条代码路径不存在"，**不**证明"有人点过一次按钮"；
报告里不把它写成后者。真正的窗口点击仍属于 `READY_FOR_DEVICE_VALIDATION` 里的人工项。

## 6. 明确没有做

- **没有改文件服务核心**：浏览 / 搜索 / 排序 / 查看 / 下载 / 上传 / 续传 / 重命名 / 删除 / 回收站 /
  批量 ZIP / 收藏 / 最近，一行都没动。
- **没有改 VPN 分类器**（`PhysicalLanSelector`）与任何路由、网卡配置。
- **没有改 mDNS 已通过的实现**（`MdnsResponder` / `IMdnsTransport` 与接口选择）。
- **没有改主题**（`ThemePalette` / `ThemeManager`）与外观持久化。
- **没有改更新架构**：`.lupd` schema 2、规范化 JSON 签名、keyId、回滚模型与备份范围都没动。
- **没有改根 CA 架构**：证书层级、DPAPI 封存、`tls/` 受管状态都没动。
- **没有改端口**：8891（仅回环管理面）、8892（HTTPS）、8890（引导页）、5353（mDNS）语义不变，
  本轮只是让 8890 在配对成功时按预期关闭。
- **没有改 Setup 主架构**：安装 / 修复 / 卸载 / 迁移与"接管安装"流程全部未动，本轮只是重新出品
  1.6.1 的安装程序。
- **没有扩大范围到界面重设计**：只删掉一个多余的按钮、把一处判断换成共享方法、在 `OnShown` 里加一次
  共享调用。文案只在"继续/下一步"这类与流程不符的地方改过。

## 7. 真实用户数据

`REPAIR_01` 的全部自动化测试都在各自的临时数据根目录里跑，**不写真实的 `%LOCALAPPDATA%\LanDisk`**：
引导页与配对流程在测试里用的是测试自己绑定的回环/私网端口与临时数据目录。真实安装目录的基线哈希
（26 个数据文件 / 384 个程序文件）在上一轮与本轮结束时逐字节一致，由 `Update-ReleaseVerify.ps1`
与 `Setup-Acceptance.ps1` 各自的运行前基线核对。
