# AUDIT_CORRECTIONS_09_REPAIR_03 — trust probe 的门禁顺序，与"重新生成引导二维码"真的会重新生成

本文件对应 `LAN_DISK_NATIVE_PARITY_NETWORK_UX_HOTFIX_09_REPAIR_03`，基线 HEAD `4f74eeb`
（REPAIR_02 的最终候选，版本 1.6.2）。04–09 轮与 REPAIR_01 / REPAIR_02 的报告保持原样，未作修改。

版本号从 1.6.2 升到 **1.6.3**：1.6.2 已经产出正式候选，产物内容变了就必须换号。

这一轮只有两处改动，都是"已经写好的东西其实没有真正生效"这一类：

1. **trust probe 先信后验**：它先 `MarkTrusted`，再去看请求是不是 HTTPS、Host 合不合法。
2. **"重新生成引导二维码"是个空按钮**：它调用的 `Onboarding.Start()` 在向导已运行时是幂等返回，
   于是用户点它只会看到同一张二维码。

两处都不改"谁算合法地址""怎么排序""证书怎么签"这些已经通过的部分。

---

## 1. trust probe：信任必须是这次请求**证明**出来的结果

### 1.1 缺陷

```csharp
// REPAIR_02 及更早的实现（顺序错了）
runtime.Onboarding.MarkTrusted(...);              // 先无条件相信
if (context.Request.IsHttps)
    runtime.Onboarding.RecordVerifiedHttpsHost(...);   // 再看是不是 HTTPS
```

两个后果：

- **明文 HTTP 也能标记信任**：只要知道 bootstrap id，任何本机进程对
  `http://127.0.0.1:8891/api/onboarding/trust-probe?b=<id>` 发一次请求，就能把向导标记成
  "手机已完成信任"。向导一旦 Trusted，窗口第 3 步就会自动准备配对二维码 —— 也就是
  **一个从未接触过 HTTPS 的请求能推进整条配对流程**。
- **伪造 Host 也能标记信任**：HTTPS 请求带着 `evil.example` 之类的 Host 时，
  `RecordVerifiedHttpsHost` 会拒绝记录，但 `MarkTrusted` 已经执行过了，"信任"这个结论仍然成立。

### 1.2 修法（顺序即语义）

```csharp
string? id = context.Request.Query["b"];
if (!runtime.Onboarding.IsActive || !runtime.Onboarding.MatchesBootstrapId(id))
    return Results.StatusCode(StatusCodes.Status404NotFound);      // 1. 会话与 id

if (!context.Request.IsHttps)
    return Results.StatusCode(StatusCodes.Status404NotFound);      // 2. 必须真的走了 TLS

if (!runtime.Onboarding.RecordVerifiedHttpsHost(context.Request.Host.Host))
    return Results.StatusCode(StatusCodes.Status404NotFound);      // 3. Host 必须是本机合法地址

runtime.Onboarding.MarkTrusted(context.Connection.RemoteIpAddress?.ToString());  // 4. 才轮到"信任"
return Results.StatusCode(StatusCodes.Status204NoContent);         // 5. 204
```

于是：

| 请求 | 结果 |
|---|---|
| 明文 HTTP + 合法 bootstrap id | 404；`Trusted` / `TrustedAt` / `VerifiedHttpsHost` 全部保持未设置；**不会**触发自动配对请求 |
| HTTPS + 非法 Host（伪域名、公网、VPN、Hyper-V/WSL、陌生内网 IP） | 不标记 `Trusted`，不记录 host |
| HTTPS + 当前 stable hostname | 204、`Trusted=true`、记录 stable |
| HTTPS + 当前 eligible 物理 LAN IP | 204、`Trusted=true`、记录该 IP |

仍然保持：pairing token 只走 HTTPS 的 `/pair#t=`、8890 明文页 0 秘密、VPN/TUN 与虚拟网卡地址一律拒绝。

### 1.3 为什么"拒绝"用 404，而不是 400

这一层拒绝的是"**这不是一次可以据以相信的探测**"，而不是"你的请求格式错了"。用 404 表示
"这里没有你要的东西"，不透露端点存在与否；而且它同时覆盖了"Host 不属于本机"这一种情况之外的
全部失败原因，行为可预期。真正的格式错误仍由既有的两层 Host 校验以 400 处理（见下）。

### 1.4 两层 Host 校验，各管一段（本轮实测出来的事实）

这一轮的 REPAIR03-02 逐条打印了每种伪造 Host 的状态码，结果值得写下来：

| Host | 状态码 | 谁拒绝的 |
|---|---|---|
| `evil.example`、`8.8.8.8`、`198.18.0.1`、`10.9.9.9`、`169.254.x`、`172.30.240.1` | **400** `HOST_NOT_ALLOWED` | 既有的 `HostValidationMiddleware`（绑定地址 + 稳定名称的白名单），在路由之前就拒绝 |
| `localhost` | **404** | 白名单允许它（本机管理页要用），但本轮的 trust probe 校验要求"手机可达"，于是拒绝 |

两层是互补的：外层挡"这台机器根本不该回应的名字"，内层挡"这个名字不是手机能用、因此不能拿来当
配对地址的名字"。两次拒绝的结论一致：**什么都没被标记**。

## 2. "重新生成引导二维码"：空按钮变成真正的恢复操作

### 2.1 缺陷

界面在向导已运行时把按钮写成"重新生成引导二维码"，但处理函数是：

```csharp
OnboardingStatus started = Runtime.Onboarding.Start();   // 幂等：IsActive 时直接返回原状态
```

`Onboarding.Start()` 的幂等性是**故意的**（普通刷新绝不能换掉手机正在看的二维码），但它同时意味着
这个按钮什么都不会发生：二维码、bootstrap id、8890 监听全都原样保留。用户以为换了一张新码。

### 2.2 修法：显式恢复入口 `AdminOperations.RestartOnboarding()`

```csharp
public OnboardingStatus RestartOnboarding()
{
    _runtime.Onboarding.Stop("用户要求重新生成引导二维码");          // 旧二维码立刻失效
    _runtime.Onboarding.ClearVerifiedHttpsHost("重新生成引导二维码"); // 新一轮不继承上一轮的证据
    IReadOnlyList<PairingRequest> live = _runtime.Pairing.Active();
    foreach (PairingRequest request in live) _runtime.Pairing.Cancel(request.Id);   // 不留并行流程
    _runtime.Audit.Write(new AuditEntry { ... Action = "restart-onboarding",
                                          Source = OperationActor.LocalAdmin.SourceToken, ... });
    return _runtime.Onboarding.Start();                              // 新的 BootstrapId / 新监听
}
```

四条性质，逐条有门禁：

- 结束当前 listener：旧引导地址在重启后不再返回 200（REPAIR03-08）；
- 清空 verified-host 提示：新会话不继承上一轮的可达地址证据（REPAIR03-07）；
- 新 BootstrapId / 新二维码：与旧的不同（REPAIR03-07、REPAIR03-08）；
- 不留并行流程：未完成的配对请求被**显式取消**（`state=Cancelled`）并写本机管理端审计，
  `ActivePairingRequests` 归零（REPAIR03-09）。

普通路径完全不受影响：`OnShown` / 1.5 秒刷新仍然是幂等的 `EnsureOnboardingStarted()`
（REPAIR03-10），窗口里也不再直接调用 `Onboarding.Start()`（REPAIR03-10b 源码闸门）。
"结束向导"（显式停止、不重启）保持原样。

### 2.3 一处既有门禁的收紧（如实记录）

REPAIR02-04 原来断言"明文 HTTP 探测返回 204 且不记录 host"。本轮把 HTTP 探测改成**直接拒绝**，
因此该断言的期望值从 `204` 收紧为 `404`，并追加了 `Trusted == false` / `TrustedAt == null`。
这不是放宽：它多断言了两件事（不被信任、没有时间戳），只是状态码跟着本轮的行为变了。
REPAIR03-06 专门守住"REPAIR02-01..16 十六条门禁一条都没少"。

## 3. 十项门禁 + 一项补充（REPAIR03-01 … REPAIR03-10）

| 编号 | 断言 |
|---|---|
| REPAIR03-01 | 明文 HTTP 探测（带合法 id）404，且 `Trusted`/`TrustedAt`/`VerifiedHttpsHost` 全部未设置；同一请求走 HTTPS 才 204 + Trusted |
| REPAIR03-02 | HTTPS + 非法 Host（含伪域名、公网、198.18.0.1、localhost、本机全部被排除网卡）不标记 Trusted，逐条打印状态码 |
| REPAIR03-03 | HTTPS + stable hostname → 204、Trusted、记录 stable |
| REPAIR03-04 | HTTPS + eligible 物理 LAN IP → 204、Trusted、记录该 IP |
| REPAIR03-05 | 被拒绝的探测不产生配对请求（`ActivePairingRequests` 恒空），且探针端点不创建请求、窗口只在 `status.Trusted` 分支创建（源码顺序闸门） |
| REPAIR03-06 | REPAIR02-01..16 十六条门禁逐条仍在源码里，本轮全量回归全部 PASS |
| REPAIR03-07 | 运行中调用 `RestartOnboarding()` → 新 BootstrapId、新 URL、Trusted 与提示归零 |
| REPAIR03-08 | 重启后旧引导地址不再返回 200、旧 id 不再匹配，而新地址立刻 200 且页面含新 id |
| REPAIR03-09 | 重启后只剩一个向导流程：旧 live 请求被取消、无设备产生、审计 `action=restart-onboarding`（`source=local-admin`，`cancelled=1`） |
| REPAIR03-10 | 普通刷新 / OnShown 仍幂等（连续 5 次 `EnsureOnboardingStarted` id/URL/监听不变）；源码里 `RestartOnboarding` 只出现在恢复按钮里（REPAIR03-10b） |

## 4. 明确没有做

没有改 verified-host 的排序算法（仍然"已验证可达 host → stable → 其它 eligible 物理地址 →
loopback 最后"）、没有改 `PhysicalLanSelector` 分类规则、没有改 mDNS、没有改根 CA / leaf 签发架构、
没有改 8891/8892/8890/5353 语义、没有改 WPF 界面设计（只换了按钮背后的调用）、没有改主题、
没有改设备与配对的共享语义、没有改回收站、没有改文件核心与上传下载、没有改
`PathResolver` / 卷身份、没有改更新签名与回滚、没有改 Setup 主架构。
没有新增任何 route / DNS / VPN / 网卡配置写入，也没有要求用户关闭 VPN。

## 5. 真机项与状态

DSH 没有真实 iPhone，本轮**没有**做真机复测：`IPHONE_LOCAL_NAME_WITH_VPN`、`IPHONE_PHYSICAL_IP`、
`PAIRING_REAL_IPHONE`、`VPN_OFF_RUNTIME`、`MP4_H264`、`UI_CROSS_MONITOR`、`FIREWALL_RUNTIME` 均为
`NOT_RUN`，`DOCUMENT_TXT = NO`，`AUTHENTICODE = NOT_CONFIGURED`；整体状态保持
`READY_FOR_DEVICE_VALIDATION`。用户提供的修复输入（VPN/TUN 开启时 `.local` 二维码被 Safari 拒绝、
`192.168.2.2:8892` 真机能打开、1.6.1 未再出现 `document.txt`）单独记录，并明确标注**不是 DSH 完成的
真机测试**。

## 6. 真实用户数据

全部自动化测试仍在各自的临时数据根目录与自选端口里跑，**不写真实的 `%LOCALAPPDATA%\LanDisk`**；
本轮实机验收用自己的运行前基线证明数据目录在运行前后逐字节一致，安装身份（`install.json`：安装 id
与稳定名称）与更早的基线逐字节一致。
