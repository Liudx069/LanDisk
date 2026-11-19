# AUDIT_CORRECTIONS_09 — 原生与 HTTP 的行为一致性、mDNS 回包接口、完整主题与更短的首次连接

本文件对应 `LAN_DISK_NATIVE_PARITY_NETWORK_UX_HOTFIX_09`，基线 HEAD `b3938d98`。
它记录这一轮修了什么、为什么这样修、以及**明确没有做**的部分。04–08 轮的报告保持原样，未作任何修改。

这一轮是**定点修复轮**，不是功能开发轮：六个问题都是"两条路径给出不同答案"或"某个模式只实现了一半"，
没有新增面向用户的功能。

---

## 1. 这类缺陷为什么会反复出现

08 轮把 Windows 外壳从 WinForms 换成 WPF 时，管理界面从浏览器搬进了原生窗口。搬的过程中，
"谁来做这件事"被复制了一份：端点里保留着原实现，窗口里又写了一遍。

于是同一个动作有两条路径、两套语义：

| 操作 | HTTP（浏览器） | 原生窗口 | 后果 |
|---|---|---|---|
| 确认配对 | 创建设备 | `Pairing.Cancel(id)` | **在电脑上点"确认"，配对请求被取消，手机永远拿不到凭证** |
| 撤销设备 | 标记 + 轮换 TokenHash + 轮换 SessionEpoch + 取消上传 + 审计 | 只标记 `Revoked` | 手机被"撤销"后，抓到的 cookie 仍然可用 |
| 重命名设备 | 清洗（去控制字符、截断 64） | `Trim()` | 同一个名字在浏览器里被改写成另一种样子 |
| 回收站恢复 | `local-admin` 演员 | `Devices.All().First()` | 0 台设备时直接抛异常；有设备时把本机操作记到第一台手机头上 |

修法只有一种是对的：把决策抽到一处（`AdminOperations`），两端都调它。
本轮为此写了 45 项测试，其中大部分是**对照测试**——同一个操作分别走原生路径与 HTTP 路径，
然后逐字段比较结果。这类测试的价值在于：它不会因为"两端都改错了同样的方向"而变绿。

## 2. FIX-09-01 确认配对

`AdminOperations.ConfirmPairing(id)` 现在包含端点原来的全部检查，顺序不变：

1. 请求存在（否则 `NOT_FOUND` / 404）；
2. 未过期（否则 `PAIRING_EXPIRED` / 410）；
3. 状态必须是 `AwaitingLocalConfirmation`（否则 `PAIRING_STATE` / 409）；
4. 置 `LocalConfirmationGranted = true`、`State = Redeemed`；
5. 创建设备：名字取请求里的（或由 User-Agent 推导）、权限默认只读、
   `TokenHash = Hash(NewToken())`、记录 `LastSeenIp` 与 `UserAgent`；
6. `pairing.IssuedDeviceId = device.Id`；
7. 写 `category=pairing, action=confirm` 审计，带 `source=local-admin` 与 `remote=<地址>`。

`RejectPairing(id)` 是其对偶：取消并写 `action=reject` 审计。WPF 的两个页面（设备页与连接 iPhone 页）
都改为调用这两个方法。

**一处刻意的行为变化**：HTTP 的 `DELETE /api/admin/pairing/{id}` 响应从 `{ok:true}` 变成
`{ok:true, existed:<bool>}`。多一个字段，便于审计，调用方不受影响。

## 3. FIX-09-02 设备管理语义

`RenameDevice` / `SetDeviceAccess` / `RevokeDevice` 进入 `AdminOperations`。
撤销的完整语义是五件事，缺一件都不算撤销：

```csharp
d.Revoked = true;
d.RevokedAt = DateTimeOffset.Now;
d.TokenHash = SecretTokens.Hash(SecretTokens.NewToken());   // 旧 cookie 永远无法重放
d.SessionEpoch = Guid.NewGuid().ToString("N");              // 撤销之前签发的会话立刻失效
int cancelled = _runtime.Uploads.CancelForDevice(id, "设备已被本机撤销");
```

名字清洗提取为 `LanDisk.Core.Security.DeviceNames.Sanitize`（原 `AuthEndpoints.SanitizeDeviceName`
改为转调它，保持既有调用点不变）。

测试里"撤销后旧会话失效"这一条是真的用两个 HTTP 客户端跑的：一个客户端先以设备身份登录成功
（`/api/me` 返回 `kind=device`），撤销之后同一个客户端必须拿到 401/403。

## 4. FIX-09-03 回收站不再借手机身份

新增 `LanDisk.Core.Model.OperationActor`：

- `ActorSource`：`Device` / `LocalAdmin` / `System`
- `OperationActor.FromDevice(device)` / `LocalAdmin` / `System`
- `SourceToken`：`device` / `local-admin` / `system`

`RecycleService.Delete/Restore/Purge` 的演员参数由 `DeviceDefinition` 换成 `OperationActor`；
`AuditEntry` 增加可空的 `Source` 字段（旧条目没有这个字段，读的时候按 `device` 处理）。

边界保持不变：

- 永久删除的确认规则**没有**放宽（`/api/admin/recycle/empty` 缺 `confirm=true` 仍然 400）；
- 手机端的回收站行为**没有**改动：手机恢复仍然记为发起操作的那台设备（`source=device`）；
- `OperationActor.LocalAdmin` 永远不会被写进 `devices.json`——它不是一台设备，只是一个演员。

## 5. FIX-09-04 mDNS 单播回包接口

修复前的代码：

```csharp
Socket? chosen = sockets.FirstOrDefault(s => (s.LocalEndPoint as IPEndPoint)?.Address.Equals(remote.Address) == true)
                 ?? sockets[0];
```

`remote.Address` 是提问方（iPhone）的地址，本机网卡地址列表里不可能有它，
所以这个比较**永远不成立**，永远走 `sockets[0]`。多物理网卡时，只有在第一块网卡上收到的问题
才会从正确的接口回复。

修复后 `HandleDatagram` 把收到这个请求的 socket 一路传下去，`SendUnicast` 直接用它：

```csharp
if (receiving is not null && sockets.Contains(receiving)) { _transport.Send(receiving, payload, remote); return; }
```

多接口且**没有**收到请求的那个 socket 时（只有测试入口会这样调用），宁可不发送并记录原因，
也不猜接口——回落到 `sockets[0]` 正是这个 bug 本身。

为了让"回复从哪块网卡出去"可被断言，发送被抽成 `IMdnsTransport`；生产的唯一实现是
`SocketMdnsTransport`，测试注入一个记录器。`UseSockets` / `Interfaces` 是 `internal`，
通过 `InternalsVisibleTo("LanDisk.Tests")` 对测试项目可见。

## 6. FIX-09-05 主题

调色板从 XAML 搬进 `LanDisk.Core.Configuration.ThemePalette`，作为数据存在：

- 11 个角色（Window / Surface / Secondary / Elevated / TextPrimary / TextSecondary /
  AccentCyan / AccentBlue / Success / Warning / Error），每个都有深色与浅色两个值；
- `Contrast` / `WorstForegroundContrast` 直接算出 WCAG 对比度，因此"白底白字"可以被测出来，
  而不是只能靠人眼在一张 PNG 上发现。

浅色值按任务给定的取：Window `#F4F7FB`、Surface `#FFFFFF`、Secondary `#E8EEF5`、
Elevated `#F8FAFC`、TextPrimary `#17202A`、TextSecondary `#5B6878`。

**立即刷新怎么做的**：页面在构建时通过 `Application.Current.Resources["LanDisk.X"]` 取到的是
`SolidColorBrush` 的**实例**并一直持有。切换主题时不重建页面（那会丢掉滚动位置，还可能和正在
进行的刷新打架），而是就地修改这些实例的 `Color`；WPF 会重绘，所有已经持有该画刷的控件跟着变。
`Theme.xaml` 里的画刷没有被冻结，所以这个修改是有效的（若将来有人给它们加
`PresentationOptions:Freeze="True"`，代码会退化成新建画刷，只影响那一个键，会在截图上暴露）。

选择写入 `shell.json` 的 `theme` 字段（`system` / `light` / `dark`）。`shell.json` 本来就在
`UpdateBackupScope.StateFiles` 里，所以升级与回滚都会保留用户的选择，不需要额外改动。
读到一个本构建不认识的取值时（例如更新的版本写的），按"跟随系统"处理，而不是崩掉。

## 7. FIX-09-06 首次连接

去掉两步人工操作，保留苹果强制的那一步：

**引导页（8890，明文 HTTP）**：以前是"装完证书 → 回到这里点继续"。现在页面自己每 1.5 秒
发起一次 HTTPS 探测（`<img>` 加载 `/api/onboarding/trust-probe`，成功与失败都会继续下一轮），
探测顺序是**稳定名称优先、物理 LAN 地址兜底**。`Trusted` 之后页面显示"✅ 安全连接已建立"，
不再需要任何点击。保留一个"立即再检测一次"按钮作为手动兜底（不是必须步骤）。

**原生窗口**：以前要用户点"生成配对请求"。现在页面在 `Trusted == true` 时自动调用
`AdminOperations.EnsurePairingRequest()`——有未过期的 live 请求就复用，没有（或已过期）才新建 ——
并立刻显示第二二维码、6 位短码与设备状态。连接 iPhone 页有一个 1.5 秒的定时器，
只在这个页面可见时运行（`NativePage.OnShown/OnHidden`），所以刷新不会堆积请求，
也不会在别的页面白白轮询。配对成功后给出"关闭向导并停止引导页"，关掉 8890 监听。

安全性质一条没变：明文引导页不含配对令牌；一次性令牌只走 HTTPS；原始配对链接在窗口里默认
折叠在"高级"展开项内；Windows 本机的"确认配对"按钮仍然保留，没有被自动化取代。

## 8. 明确未做

- **真实 iPhone 复测**（物理 IP 与稳定 `.local` 两种入口）：`NOT_RUN`。这一轮改动了引导页的
  探测逻辑，真机行为需要在设备上确认；Playwright WebKit 不等于 iPhone Safari，不用它冒充。
- **`document.txt` 真机复现**：仍是 `NO`（在 1.4.0 候选上无法复现），本轮未改动根路由。
- **`MP4_H264`**：Playwright 的 Chromium 构建不含 H.264 解码器，`NOT_RUN`。
- **VPN 关/开对照**：`NOT_RUN`（会打断使用者正在进行的工作）。
- **跨显示器 DPI**：本机只有一台显示器，`NOT_RUN`；已提供 100–200% × 深浅两套主题的渲染证据。
- **Authenticode 代码签名**：继续 `NOT_CONFIGURED`。
- **`Describe(device)` DTO 未扩展**：`/api/pairing/{id}/confirm` 与 `/api/admin/devices` 返回的设备
  字段仍是 `id/name/level/createdAt/lastSeenAt/revoked`，没有加 `lastSeenIp` 与 `userAgent`。
  对照测试因此同时比较"响应字段"与"存储字段"。扩展 DTO 会改变既有 API 形状，本轮按"不扩大范围"
  处理，没有做。

## 9. 一处需要留意的工具行为

`tools/Generate-DeliveryReport.ps1` 现在**优先读 `dist/test-evidence.tsv`**（本次构建测试跑出来的），
再回落到交付目录里的副本。这一轮之前它读的是交付目录里的那一份，而报告是在 Finalize 之前生成的，
所以报告可能引用上一次构建的测试证据。

同时新增一条硬门禁：如果报告里出现"测试证据里没有 FIX-09-xx"，生成器以退出码 3 失败。
也就是说，**不能用一次没跑过这些测试的运行去生成声称修过 FIX-09 的报告**。
