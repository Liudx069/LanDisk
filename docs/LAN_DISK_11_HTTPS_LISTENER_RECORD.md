# LanDisk 监听与更新端点核验记录（HTTPS / 动态端口 / 更新重启）

对应 `LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02` 第二节。
本文记录**按实际源码与实际进程**核验的结果，以及由此发现并最小修复的一处真实缺陷。

- 核验时的构建身份：`1.7.3@f2f4f63`（诊断阶段；正式核验在 1.7.4 冻结后重做）
- 隔离方式：独立数据目录 + 独立日志目录 + 独立测试端口；未触碰 Owner 的真实端口、共享、证书与配对

---

## 1. 实际控制关系（读源码确认，非推测）

### 1.1 监听目标从哪里来（`src/LanDisk.Server/LanDiskHost.cs`）

```csharp
int httpPort = settings.HttpPort;
int httpsPort = settings.HttpsPort;

// Port 0 means "pick a free port"; resolve it once so that the IPv4 and IPv6 loopback
// listeners share the same number.
if (httpPort == 0) httpPort = FindFreePort();
if (httpsPort == 0) httpsPort = FindFreePort();

var listenTargets = new List<(IPAddress Address, int Port, bool Https)>
{
    (IPAddress.Loopback,     httpPort, false),
    (IPAddress.IPv6Loopback, httpPort, false),
    (IPAddress.Loopback,     httpsPort, true)      // ← 无条件
};

bool lanHttp  = settings.LanEnabled && !settings.HttpsEnabled && settings.HttpCompatModeEnabled;
bool lanHttps = settings.LanEnabled && settings.HttpsEnabled;
```

**结论（四条，均与常见误解相反）：**

| 项 | 实际语义 |
| --- | --- |
| `HttpsEnabled` | **只控制局域网侧**。它是"是否在物理网卡上提供 HTTPS"的开关，**不影响回环** |
| 回环 HTTPS | **无条件监听**。即使 `https-port 0`、即使 `HttpsEnabled=false`，回环 HTTPS 依然存在 |
| 端口 `0` | **"挑一个空闲端口"**，由 `FindFreePort()` 解析。**不是"关闭"** |
| 局域网监听 | 只在 `LanEnabled` 为真时添加，且**只绑物理网卡**（排除 Hyper-V 交换机与 VPN 隧道地址），可用 `LanBindAddress` 再收敛到一个地址 |

补充观察到的不对称（记录备查，未改动）：回环 **HTTP** 同时绑 IPv4 与 IPv6（`::1` 与 `127.0.0.1`），
而回环 **HTTPS** 只绑 IPv4。这是既有设计，本次按要求未删改任何既有回环行为。

### 1.2 实测（冻结的 1.7.3 服务器，`--port 0 --https-port 0`）

```
process id: 8928
实际监听：
  ::1:60600          ← 回环 HTTP（IPv6）
  127.0.0.1:60600    ← 回环 HTTP（IPv4）
  127.0.0.1:60601    ← 回环 HTTPS（无条件存在，尽管 https-port 传的是 0）
局域网监听：无（LanEnabled 默认 false）
settings.json：未写出
```

这四条实测结果与 1.1 的源码读法完全一致，并且**用实际监听证明了**：
- 端口 0 被解析成两个不同的空闲端口（60600 / 60601），而**不是**被当作关闭；
- 回环 HTTPS 在 `https-port 0` 下依然监听；
- 局域网 HTTPS 关闭时**没有**多出任何局域网监听。

### 1.3 局域网开关如何被保留

更新程序**不**传 `HttpsEnabled`；重启后的实例从 `settings.json` 读取它，而 `settings.json` 属于更新系统的
受管状态文件（回滚会按字节恢复）。因此"局域网 HTTPS 关闭后不得意外新增局域网监听"这一条依赖的是
**受管状态被正确恢复**，而不是命令行参数。T04-06 已逐字节验证受管状态恢复。

---

## 2. 发现的真实缺陷：更新请求拿到的是"未解析的配置值 0"

### 2.1 缺陷
`src/LanDisk.Server/Endpoints/UpdateEndpoints.cs` 原先写的是：

```csharp
ApplyRequest applied = runtime.Updates.RequestApply(
    runtime.Settings.HttpPort, TimeSpan.FromSeconds(60), runtime.Settings.HttpsPort);
```

`Settings.HttpPort` 是**用户配置值**，不是**实际绑定端口**。`LanDiskHost` 解析 0 时只改了本地变量
（`if (httpPort == 0) httpPort = FindFreePort();`），**没有写回 Settings，也没有写出 settings.json**
（1.2 实测：`settings.json` 未写出）。

于是当配置为 0 时：
1. 宿主实际绑在 60600 / 60601；
2. 更新请求把 **0** 交给更新程序；
3. 更新程序 `UpdatePortChoice.Resolve(… , 0, ReadConfiguredPort(…) = 0)` 得到 0；
4. 重启后的实例把 0 再解析一次 → **落到另一个空闲端口**；
5. 健康检查的目标也是 **0** → 探测失败 → 判定新版自检不通过 → 回滚。

**后果**：跑在动态分配端口上的 LanDisk **无法完成更新**，而且过程中的重启会把服务挪到别的端口。
这与原任务要求的"宿主传递的是需要恢复的实际服务端点，不是尚未解析的配置值 0"正好相反。

**可达性**（如实说明，不夸大）：管理页会拒绝保存 0（`AdminOperations.cs:239`：
`if (httpPort is not null && (httpPort < 1 || httpPort > 65535))`），所以用户**通过界面**设不出 0。
0 只能通过命令行 `--port 0` 或手改 `settings.json` 得到。因此这是**受支持但非界面可达**的模式，
不是绝大多数用户会遇到的路径 —— 但它确实是产品明确支持的模式（`FindFreePort` 就是为它写的）。

### 2.2 最小修复
1. `src/LanDisk.Server/Services/LanDiskRuntime.cs`：新增 `BoundHttpPort` / `BoundHttpsPort`
   （宿主解析后的**实际**绑定端口；`Settings` 保持用户配置值不被污染）。
2. `src/LanDisk.Server/LanDiskHost.cs`：解析完成后写入这两个属性。
3. 两处调用点改用绑定端口：
   - `src/LanDisk.Server/Endpoints/UpdateEndpoints.cs:176`
   - `src/LanDisk.App/Pages2.cs:191`
4. 新增回归门禁 `UPDATEPORT12-06`：源码级检查，任何 `RequestApply(...Settings.HttpPort...)`
   即失败。**已做反向验证**：把 `Pages2.cs` 改回旧写法，门禁立刻变红并指出
   `Pages2.cs:191`；改回后 6/6 通过。

未改动监听架构，未新增测试框架，未触碰回环既有无条件监听。

---

## 3. 本轮证据与位置

| 内容 | 位置 |
| --- | --- |
| 动态端口实测（监听列表与 settings.json 缺失） | 本文 1.2 节（隔离目录 `dist/.https-probe`，已清理） |
| 源码控制关系 | `src/LanDisk.Server/LanDiskHost.cs:130-165` |
| 修复点 | `LanDiskRuntime.cs`（新增属性）、`LanDiskHost.cs:141-142`、`UpdateEndpoints.cs:176`、`Pages2.cs:191` |
| 门禁与其反向验证 | `tests/LanDisk.Tests/UpdatePortChoiceTests.cs` → `UPDATEPORT12-06` |
| 单元回归 | 677 / 677 PASS（原 676 + 本门禁） |

## 4. 尚未完成（续跑时补）

- 更新前 / 更新后 / 回滚后的**实际监听地址、端口、`HttpsEnabled` 开关与宿主/更新程序进程身份**的
  前后对照（本记录只做了 1.1 的静态语义核验与 1.2 的启动实测；端到端对照要在 1.7.4 冻结后跑，
  因为需要正式更新包）。
- 用动态端口（`--port 0`）跑一次完整的更新 + 回滚，证明修复后端到端可用。
