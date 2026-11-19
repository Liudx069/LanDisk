# AUDIT_CORRECTIONS_09_REPAIR_02 — 配对二维码改为优先使用"真实 iPhone 已经验证可达"的 HTTPS 地址

本文件对应 `LAN_DISK_NATIVE_PARITY_NETWORK_UX_HOTFIX_09_REPAIR_02`，基线 HEAD `0da1388`
（REPAIR_01 的最终候选，版本 1.6.1）。04–09 轮与 REPAIR_01 的报告保持原样，未作修改。

版本号从 1.6.1 升到 **1.6.2**：1.6.1 已经产出正式候选并被真实 iPhone 使用过，产物内容变了就必须换号。

---

## 1. 真实设备给出的证据（用户提供，不是 DSH 自己测出来的）

在真实 Windows + 真实 iPhone 上，VPN/TUN 开启、物理 LAN 为 `192.168.2.2`、稳定名
`landisk-a1b2c3d4.local`：

1. 电脑点一次"连接 iPhone"，8890 引导页正常打开；
2. iPhone 已经信任既有的 LanDisk Local CA；
3. 引导页的 HTTPS trust probe 自动成功，页面显示"安全连接已建立"；
4. 电脑侧自动出现第二张 pairing 二维码；
5. **扫第二张二维码**（内容以 `https://landisk-a1b2c3d4.local:8892/...` 开头）→ Safari
   "此网站无法提供安全连接"；
6. 保持 VPN/TUN 开启，手工打开 `https://192.168.2.2:8892/` → **正常打开 LanDisk**；
7. 1.6.1 真机上没有再出现 `document.txt` 下载。

这一组观察已经把范围钉死：HTTPS 本身没问题、根 CA 已被信任、leaf SAN 覆盖当前物理 IP、8892 正常、
物理 LAN 路由正常、`.local` 在 VPN/TUN 下不可达、而第二张二维码仍然固定优先 `.local`。也就是说
**trust probe 已经知道哪个 HTTPS host 真正成功，但 pairing URL 没有利用这个信息**。

本文件不重新猜测根因（例如"VPN 抢了路由"只是现象），只修"证据没有被使用"这一件事。

## 2. 修法：把"刚被真实手机访问成功过的 host"变成排序依据

### 2.1 记录（服务端事实，不看 JavaScript 的猜测）

`GET /api/onboarding/trust-probe` 只有在请求**真正带着有效 bootstrap id 到达服务端**时才会执行到记录点。
请求到达即已证明三件事：手机能到达该 host、TLS 握手成功、证书被这台手机接受。因此在同一个请求里记录：

```csharp
runtime.Onboarding.MarkTrusted(context.Connection.RemoteIpAddress?.ToString());
if (context.Request.IsHttps)
    runtime.Onboarding.RecordVerifiedHttpsHost(context.Request.Host.Host);
```

- 只记录 `IsHttps` 的请求：明文 HTTP 的探测不是"手机能走 HTTPS"的证据。
- 只记录 `Request.Host.Host`（不含端口、不含路径）。
- bootstrap JS 完全不需要判断 `img.onload/img.onerror` 谁更"安全" —— 服务端事实为准。

### 2.2 合法性（`OnboardingService.NormalizeVerifiedHost`）

Host 必须属于两类之一，否则**什么都不记录**：

| 允许 | 说明 |
|---|---|
| 本机当前 stable hostname | 与本机 `Identity.HostName` 比较，大小写不敏感 |
| 当前 `PhysicalLanSelector` 接受的物理 LAN 地址 | 先 `IPAddress.TryParse` 再按地址比较，不用字符串技巧 |
| 回环地址 | 允许记录（本机浏览器探测是合法事实），但**永不作为手机首选**，见 2.4 |

同时拒绝：任意其它名字（含 `localhost`、Windows 机器名、DNS 后缀）、任何解析不出地址的字符串、
公网地址、VPN/TUN 地址、Hyper-V / WSL 地址、198.18.0.0/15、以及带 `/`、`\`、`@`、空格、冒号的畸形值。
于是**伪造 `Host` 头无法让配对二维码指向攻击者地址**：不合法就不写，现有安全边界（明文页无秘密、
令牌只走 HTTPS）都不受影响。

### 2.3 生命周期（这是本轮最容易做错的地方）

真实流程里，**8890 会在手机完成信任的那一刻被关掉**（这是既有的"引导页已完成使命"规则），
而 pairing 二维码是在那之后才被扫的。所以：

| 事件 | 对 hint 的作用 |
|---|---|
| `Onboarding.Start()` 真正开启新一轮 | **清空**（这是新的一次连接尝试） |
| 已在运行时再调用 `Start()` | 不动（不要去打扰正在配对的手机） |
| trust probe 成功（HTTPS） | 写入 host + 时间戳 |
| `Onboarding.Stop()`（含关掉 8890 的信任规则） | **保留** —— 否则 hint 恰好在需要它的前一刻消失 |
| `ConfirmPairing` 成功 | 清空（本次连接流程结束，临时提示不再有意义） |
| 用户点"重新生成引导二维码"（走 `Start()`） | 清空，开始全新流程 |
| 超过 `VerifiedHostLifetime`（20 分钟） | 读取时视为不存在 |

**不持久化**：不进 `install.json`，不进 `devices.json`，不写任何文件。它只是"这一次连接尝试"的运行态信息。

### 2.4 读取时再验证（`AdminOperations.VerifiedPairingHost`）

hint 可能已经过期，也可能已经与现实不符，所以每次构造链接都重新验证：

- 已过期 / 为空 → 忽略；
- 是 stable 名且仍等于当前 `Identity.HostName` → 采用；
- 是物理 IP：必须仍在 `PhysicalLanSelector` 当前 eligible 集合里，
  并且**当前 leaf 的 SAN 仍然覆盖该地址**（`runtime.Certificate.IpAddresses`）；不满足就忽略；
- 是回环 → 忽略（手机永远不可能打开 127.0.0.1，它不能成为二维码的第一个地址）。

hint 失效时不做任何"补救式换证书"：仍按既有的证书规则走（地址变化时 `RefreshNetworkIdentity` 只重签 leaf，
根 CA 永不改动）。忽略 hint 只是回到正常的 stable → physical 回退顺序。

### 2.5 排序（`AdminOperations.PairingUrls`，原生窗口与 HTTP 端点唯一共用入口）

1. **本次仍合法的已验证 host**（stable 名或 eligible 物理 IP，且 HTTPS 在监听）
2. stable `.local`（正在通过 mDNS 发布时；**或**手机刚刚证明它可用时——那是比我们自己的 mDNS 记账更好的证据）
3. 当前其它 eligible 物理 LAN 地址（https 优先，明文 http 兼容模式只在没有 https 监听时作为兜底）
4. 分类器不认识的其它已绑定地址
5. loopback（放最后，仅本机诊断）

全表去重（按 URL 比较，大小写不敏感）。于是：

- 手机用 `.local` 成功 → 第一项仍是 `https://landisk-xxxx.local:8892/pair#t=…`；
- 手机用物理 IP 成功 → 第一项变成 `https://192.168.2.2:8892/pair#t=…`，`.local` 不再被推到手机面前；
- **没有**任何"检测到 VPN 就永远用 IP"的分支：`PairingUrls` / `VerifiedPairingHost` 源码里不出现
  `VpnOrTunnelDetected`（由 REPAIR02-03 守门）。

## 3. 原生界面

"连接 iPhone"第 3 步增加一行小字：`已验证可达地址：<host>`（次要样式，不含令牌、不加工程化信息、
不重做界面）。第二张二维码本来就直接用 `PairingUrls()` 的第一项，所以物理 IP 生效时**不需要用户手工选备用地址**。
高级折叠里的原始链接照旧默认隐藏。

UI 截图脚本 `UiCapture.ArmOnboarding()` 现在也记录一次已验证 host，好让"已验证可达地址"这一行和由它排序的
二维码**出现在 132 个渲染组合里**被布局断言覆盖，而不是只存在于代码里。

## 4. 十四项门禁（REPAIR02-01 … REPAIR02-14）与两项补充

门禁跑在"像手机看到的那样"的测试主机上：`LanEnabled=true` + `HttpsEnabled=true`，即服务端真的在物理 LAN 地址上
监听 TLS，而不是只绑回环。HTTPS 探测通过真实 HTTP 请求发出（用 `Host` 头模拟手机使用的那个地址），
服务端记录的正是同一个 `Request.Host.Host`。

| 编号 | 断言 | 关键证明方式 |
|---|---|---|
| REPAIR02-01 | `.local` 探测成功 → hint=stable，链接首项=stable HTTPS | 真实 HTTPS 探测 + `PairingUrls[0]` 精确比对 |
| REPAIR02-02 | 只有物理 IP 探测成功 → hint=IP，链接首项=该 IP HTTPS | 同上，并先断言该地址确有 HTTPS 监听 |
| REPAIR02-03 | VPN/TUN 存在不等于强制 IP | 本机 VPN 检测为真时 stable 仍为首项；且 `PairingUrls` 源码不引用 `VpnOrTunnelDetected` |
| REPAIR02-04 | 伪造 Host 一律不记录 | `evil.example` / `8.8.8.8` / `198.18.0.1` / `localhost` / `10.9.9.9` + 本机全部被排除网卡地址（含 VPN 与 Hyper-V）逐个走真实探测；明文 HTTP 探测也不记录 |
| REPAIR02-05 | 物理 IP 必须来自当前 eligible 集合 | `10.9.9.9` / `192.168.77.77` / `172.20.5.5` 被拒；本机 eligible 地址被接受 |
| REPAIR02-06 | 旧 IP 在地址变化后失效 | 直接注入过期提示（模拟 DHCP 变化），断言链接里既不含它也不以它为首，正常回退 |
| REPAIR02-07 | `Stop()` 关掉 8890 后 hint 仍可用 | 关闭后 `IsActive=false`、监听清空，而 hint 与链接首项不变 |
| REPAIR02-08 | 新一轮 `Start()` 清空旧 hint | 已在运行时 `Start()` 不清空；`Stop` 后再 `Start` 后回到无提示基线 |
| REPAIR02-09 | `ConfirmPairing` 成功后清理 hint 且不影响设备 | 走完整 HTTPS 配对流程，确认后 hint 归 null、设备在库、`/api/me` 仍是 device |
| REPAIR02-10 | 顺序正确且去重 | 两种 hint 下逐项核对顺序（首项、物理位置、loopback 末位）与无重复项 |
| REPAIR02-11 | 明文引导页 0 令牌 / 0 管理令牌 / 0 cookie / 0 CSRF / 0 设备密钥 | 内存渲染版 + 实际 HTTP 返回版都核对，且用**本轮真实**的配对令牌、短码、claimSecret、管理令牌去比对 |
| REPAIR02-12 | 配对令牌只在 HTTPS `/pair#t=` | 非回环链接一律 `https://`；链接不指向引导端口；引导页不含令牌 |
| REPAIR02-13 | leaf SAN 不因本轮加入虚拟地址 | SAN 全部是回环或当前 eligible 物理地址；本机被排除的非回环网卡地址（198.18.0.x、Hyper-V）一个都不在其中 |
| REPAIR02-14 | 不新增 route / DNS / VPN 配置写入 | 扫描全部产品源文件 0 命中；`netsh` 仅存在于 Setup 防火墙助手，且唯一进程调用是 `advfirewall firewall add/delete rule` |
| REPAIR02-15（补充） | §十 根路由回归门禁 | 未配对/已配对 × HTTP/HTTPS × LAN IP/stable host 的 GET/HEAD `/`：`text/html; charset=utf-8`、无 `Content-Disposition`、无 `application/octet-stream`、不出现 `document.txt` |
| REPAIR02-16（补充） | 回环 hint 永不成为手机首选 | `127.0.0.1` 被记录，但链接里没有 `https://127.0.0.1…`，回环仍只是最后一项本机诊断链接 |

**一处既有门禁的改写（如实记录）**：REPAIR_01 的 `REPAIR09-08b` 原本断言"`PairingUrls` 里完全不出现
`Onboarding`"，因为当时它确实不需要看向导。REPAIR_02 之后这条不变量变了形状——它现在**只读取一个成员**：
已验证可达 host。因此该门禁被改写为更强的形式：整个方法体里 `_runtime.Onboarding.` 恰好出现 **1 次**
且是 `VerifiedHttpsHost`，同时仍然不出现 `Onboarding.Start/Stop/Port`、`BootstrapUrl`、`DefaultBootstrapPort`。
"配对链接不可能指向明文引导页"这条性质没有被放宽，反而被写得更精确。

## 5. 明确没有做

- **没有**改 mDNS 的实现与接口选择规则（`MdnsResponder` / `IMdnsTransport` 一行未动）。
- **没有**改 `PhysicalLanSelector` 的分类规则（哪些网卡合格，判定完全沿用）。
- **没有**改根 CA 架构、DPAPI 封存、leaf 的签发规则（只是读取 SAN 做校验）。
- **没有**改端口语义：8891 / 8892 / 8890 / 5353 行为和从前一样；8890 仍然在信任完成或配对成功时关闭。
- **没有**改更新签名、回滚模型、Setup 主架构、主题、回收站、设备语义、`OperationActor`、
  `PathResolver`、卷身份、上传/续传、浏览/搜索/排序、收藏/最近、批量 ZIP。
- **没有**新增任何 route / DNS / VPN / 网卡配置写入，也**没有**要求用户关闭 VPN。
- **没有**碰 WPF UI 的结构：只在第 3 步加了一行小字状态。
- **没有**改根路由：§十 只做回归门禁。

## 6. 真机项与状态

DSH 没有真实 iPhone，本轮**没有**做真机复测，因此：

- `IPHONE_LOCAL_NAME_WITH_VPN = NOT_RUN`
- `IPHONE_PHYSICAL_IP = NOT_RUN`
- `PAIRING_REAL_IPHONE = NOT_RUN`
- `OWNER_PRE_REPAIR_OBSERVATION` 单独记录用户提供的修复输入（VPN/TUN 开启时 `.local` 二维码失败、
  `192.168.2.2:8892` 真机能打开、1.6.1 未再出现 `document.txt`），并明确标注这是**用户提供的观察**，
  不是 DSH 完成的真机测试。

整体状态保持 `READY_FOR_DEVICE_VALIDATION`。

## 7. 真实用户数据

全部自动化测试都在各自的临时数据根目录与自选端口里跑，**不写真实的 `%LOCALAPPDATA%\LanDisk`**。
真实安装目录的数据与程序文件基线哈希在本轮结束时仍逐字节一致（由 `Update-ReleaseVerify.ps1` 与
`Setup-Acceptance.ps1` 的运行前基线核对）。

## 8. 一份被自己的门禁抓出来的隐私问题（本轮真实发生）

本轮的审核包第一次生成时，隐私扫描报了 **10 处 HIGH**：`GIT/*.patch`、交付报告、纯文本审计导出、
本审计文档与报告模板各自命中"真实凭据/个人路径"。

原因不是密钥泄漏，而是**真实设备的稳定名称**：为了让缺陷描述具体，上面第 1 节最初直接写了用户
机器上的 `landisk-<真实 install id>.local`。审核包自带的扫描把"真实 install id 与 hostLabel"当作针，
于是这些文档全被抓住 —— 这正是它的用途。

修法是换成一个明确标注为占位的虚构名称 `landisk-a1b2c3d4.local`，并且**重写**本轮那一个提交
（而不是再加一个"删掉它"的提交），因为后者的 diff 里仍然会留着那串真实标识。修好之后重建候选、
重跑全部证据与审核包，隐私扫描回到 0 问题。

这件事记在这里而不是悄悄改掉：一个只在交付物里出现、不在代码里出现的个人标识符，靠人眼复核很容易漏掉，
本轮就是被机器抓住的。

