# AUDIT_CORRECTIONS_08 — 原生 Windows 界面、VPN 共存与根路由

本文件对应 `LAN_DISK_NATIVE_WINDOWS_UX_08`。它记录这一轮做了什么、为什么这样做、
以及**明确没有做**的部分。04–07 轮的报告保持原样，未作任何修改。

---

## 1. 为什么 Windows 端要重写而不是"美化一下"

旧外壳是 WinForms：一个托盘图标加一个窗口，管理功能全部在浏览器里。这带来三个具体后果：

1. **用户体验断裂**：共享目录、设备权限、更新、证书这些"设置类"操作都要跳到浏览器标签页，
   而浏览器不知道 LanDisk 的进程状态，刷新一下就可能显示过期信息。
2. **授权模型错位**：Web 管理页是 loopback + 管理令牌 + CSRF 的来源校验模型，
   对一个"已经在本机上运行的原生进程"来说，这套机制只是在补偿"网页不是本机进程"这件事。
3. **可读性问题无处安放**：字号、列宽、缩放这些在网页里靠 CSS 猜的东西，在原生界面里是
   DPI 感知、字体回退与布局约束，本来就应该由框架处理。

因此这轮把 `LanDisk.App` 从 WinForms 改成 **WPF**（`UseWPF=true`，WinForms 完全关闭），
并把管理页面做成 11 个原生页面。文件夹选择用 `Microsoft.Win32.OpenFolderDialog`、
更新包用 `OpenFileDialog`，两者都是 Windows 自己的对话框，因此不需要为了它们再链入 WinForms。

**"原生"在这里的含义是可验证的**：仓库里没有任何 WebView 引用，`LanDisk.exe` 的正常流程不调用
`Process.Start` 打开浏览器（唯一调用点在 设置 → 高级 → Web 管理恢复页，且点击前明确提示）。

## 2. 为什么必须有 `AdminOperations`

任务要求"WPF 与 server 共用同一套业务 service/validation，禁止复制第二套业务逻辑"。

最省事的做法是让原生界面调用 `http://127.0.0.1:8891` 的管理 API —— 任务明确禁止，
而且那等于把"本机进程管理自己"降级成"客户端调用服务端"。另一种做法是把端点里的校验逻辑
在界面里抄一遍 —— 那是两套答案开始漂移的经典起点。

实际做法是最小抽取：把**校验逻辑真正存在于端点内部**的操做搬进 `AdminOperations`，
端点与界面都调用它。具体是：

| 操作 | 原本在哪 | 现在 |
|---|---|---|
| 创建共享（路径校验、内部目录拒绝、reparse point 拒绝、卷身份捕获、审计） | `POST /shares` 内联 | `AdminOperations.CreateShare` |
| 修改共享（显示名/启用/权限） | `PATCH /shares/{id}` 内联 | `AdminOperations.UpdateShare` |
| 重新确认磁盘（重新读取卷身份） | `POST /shares/{id}/reconfirm` 内联 | `AdminOperations.ReconfirmShare` |
| 停止共享（只删记录，同时清理失效收藏） | `DELETE /shares/{id}` 内联 | `AdminOperations.DeleteShare` |
| 网络设置（TLS/兼容模式前置条件、CIDR、端口范围） | `POST /network` 内联 | `AdminOperations.UpdateNetworkSettings` |
| 配对链接排序（稳定名称优先、物理地址、回环兜底） | `POST /pairing` 内联 | `AdminOperations.PairingUrls` |

读操作（状态、共享列表、设备列表、回收站、审计、自检）本来就是对运行时对象的投影，
界面直接读同一批对象，没有第二份实现。

**网络设置顺带收紧了一处**：绑定地址过去接受任何本机私有 IPv4，现在只接受
**当前可用的物理局域网地址**。绑定到 Hyper-V 交换机或 VPN 的假地址是上一轮的真实现象，
没有理由继续允许。

## 3. VPN/TUN 共存：从"排名靠后"到"根本不使用"

旧代码有一个"虚拟网卡标记表"，但它只把虚拟网卡**排在后面**，仍然会把它们当作局域网地址：

- 服务器证书的 SAN 里出现了 Hyper-V 的 `172.26.224.1`（我在 07 轮的真实运行证据里就抓到了）；
- 服务器会在这些地址上监听；
- mDNS 绑 `0.0.0.0` 再靠路由表发出，而全局 TUN 占着默认路由。

真机实测（这台机器）：`iKuuuVPN` 占着 **198.18.0.1/16 与默认路由**，
`vEthernet (Default Switch)` 是 172.26.224.1/20，物理 `以太网` 是 192.168.2.2。

新的 `PhysicalLanSelector` 不再依赖单一信号，而是综合：网卡类型、连接状态、**是否有默认网关**、
地址是否 DHCP 分配、链路速率、DNS 后缀，并显式排除 `198.18.0.0/15`、APIPA、非私有地址以及
loopback/Hyper-V/WSL/TUN/TAP/Wintun/WireGuard/iKuuu/WAN Miniport/Wi-Fi Direct 等特征。
每条判定都带原因，界面可以解释而不是只拒绝。

它现在是**唯一**的局域网地址来源：证书 SAN、mDNS 宣告、首次连接引导地址、Kestrel 监听目标、
配对链接、界面显示全部由它决定。mDNS 也改成"每个物理接口一个 socket，绑定该接口地址、
在该接口入组、单播从收到问题的 socket 回"。

边界：**LanDisk 不修改 VPN、不修改路由、不关闭任何网络组件**，只提示用户在 VPN 客户端里
开启"允许局域网 / 绕过本地网络"，并提供物理 IP 直连回退。

## 4. Safari 根路由：无法复现的那一条，怎么处理

真机报告：`https://<LAN-IP>:8892/` 触发下载 `document.txt`。

我在 1.4.0 候选上做了完整复现尝试：HTTP 与 HTTPS、回环地址与 `192.168.2.2`、GET 与 HEAD、
iPhone Safari 的 User-Agent 与 Accept —— 全部返回 `200` + `text/html; charset=utf-8` +
**无 `Content-Disposition`**，body 是应用外壳。**没有复现。**

因此这一条**没有**写成 PASS。处理方式是：

- 把"根路由必须是页面、永远不是附件"写成 8 项回归测试（配对/未配对、HTTP/HTTPS、GET/HEAD、
  SPA 入口、未知 API 路径、文件形路径、静态资源缺失）；
- 在报告里如实写 `DOCUMENT_TXT_REPRODUCES = NO（在 1.4.0 候选上无法复现）`；
- 真机项保持 `NOT_RUN`，等待 iPhone 复测。

这段代码从 1.3.0 起就没有改过，所以更可能的解释是当时被访问的是别的构建（用户桌面上还留着
1.2.0 的解压副本），或者问题已经不在当前代码里。没有证据的部分不编故事。

## 5. 界面可读性：截图发现的两个真缺陷

新增 `--render-ui` 诊断入口，用 WPF 自己的渲染器把全部 11 个页面在
100/125/150/175/200% 与 1160×780 / 1366×768 / 1920×1080 下渲染成 PNG，
同时**检查**（横向溢出、单行文本高度异常、字号下限）并输出逐条结论：77 个组合，77 个 PASS。

第一次跑出来的截图直接暴露了两个我在代码里看不出来的缺陷：

1. **深色主题下 WPF-UI 的 `Card` 解析出浅色填充**：面板接近白色，而正文是浅色 —— 几乎不可读。
   面板改成显式 `Border` + 设计系统自己的画刷；内容区、根网格、页面宿主都显式上色，
   因为 `FluentWindow` 的模板会画一层浅色内容背景，而 Windows 10 没有 Mica 可以掩盖它。
2. **页面标题深色叠深色**：标题与小节标题的颜色来自窗口 chrome 的继承。
   现在显式指定。

这两条都不是"审美问题"，而是**在真机上会直接导致无法使用**的问题，只有看到图才能发现。

## 6. Web 管理页（恢复入口）

虽然主界面已经原生，Web 页仍然是恢复入口，因此三处报告的问题都修了：
字号下限、数据表不再挤压换行（改为横向滚动）、配对令牌收进折叠项。
三项都有 Chromium 与 WebKit 的自动化断言。

顺带记录一个真实教训：这三项检查第一次跑是**失败**的，因为 `dist/publish` 里还是上一版的
前端资源。过期产物正是"修好了但其实没发出去"的典型形态，这也正是要加这些断言的原因。

## 7. 明确未做

- **真实 iPhone 复测**（`.local` 与 LAN IP 两种入口）：`NOT_RUN`。
- **真实 VPN On/Off 对照**：本机 VPN 一直处于连接状态，关闭它会打断使用者正在进行的工作，
  因此 `VPN_OFF_RUNTIME = NOT_RUN`；分类器与 mDNS 绑定有单元测试与真机枚举证据。
- **跨显示器 DPI 切换**：本机只有一台显示器，无法验证窗口在两个不同缩放显示器之间拖动；
  已给出 100–200% 的渲染证据与布局断言，真机跨屏切换记 `NOT_RUN`。
- **Windows 11 专属效果**（Mica、Snap Layout 增强）：本机是 Windows 10 19045，
  因此只验证纯色回退；未在 Windows 11 上运行。
- **Authenticode 代码签名**：继续 `NOT_CONFIGURED`。

## 8. 实机验收暴露的两处"检查本身有问题"

07 轮末的实机升级/回滚验收（`tools/Update-ReleaseVerify.ps1`，40 条）里有 2 条 FAIL，
两条都不是产品缺陷，而是**检查写得不对**。这一轮把它们修掉了，因为一个会误报的检查
比没有检查更糟：它会训练人忽略它。

### 8.1 `T03-25` 拿几小时前的快照做基准

检查内容是"%LOCALAPPDATA%\LanDisk 里用户真实数据没有被验收过程改动"，
但基准文件是 04 轮固化下来的固定路径 `%TEMP%\landisk-userdata-baseline-04.tsv`。
只要用户自己的 LanDisk 在两次验收之间正常运行过（写日志、写 `shell.json`、更新 `admin-url.txt`），
比对就会对这些**与验收无关**的变化报 FAIL。

现在改成**本次运行开始时现取现用**：基线写入 `.rcverify/<时间戳>/userdata-baseline.tsv`，
比对区间是"本次验收开始 → 本次验收结束"。任何差异都只可能来自这次验收过程。
用户实例正在运行时仍然记 `BLOCKED`（它自己在写数据目录，无法归因），这一点没有放宽。

### 8.2 `T04-06` 的白名单没跟上 07 轮新增的 `install.json`

`T04-06` 检查"回滚是否清掉了失败版本新增的受管状态文件"，做法是枚举数据根目录下的 `*.json`
再减去一个硬编码白名单。07 轮把安装身份文件 `install.json`（安装 id、稳定 `.local` 名称、
固定端口）放在数据根目录，白名单没有同步，于是这个**合法文件**被报成"残留"。

修法不是把 `install.json` 加进白名单了事，而是先纠正它背后的定义：

- `UpdateBackupScope.StateFiles` 增补 `install.json`。此前它不在受管状态范围内，
  意味着"升级与回滚不改动 install.json"只是**碰巧成立**；现在它是受管状态的一部分，
  升级保持原样、回滚按字节恢复、失败版本新建的也会被清掉。
- 白名单随之与产品自己的定义对齐，并加了一条单元测试
  （`The_install_identity_is_part_of_the_managed_state_scope`）钉住这个不变量。

### 8.3 `UI-PAIRING-TOKEN-HIDDEN` 用 `offsetParent` 判断"可见"

这一条是 08 轮新增的浏览器断言，第一次跑就报了 FAIL，但截图与 DOM 都说明**界面是对的**：
折叠标题下面只有一行 `▸ 高级：显示原始配对链接（含一次性令牌）`，输入框并没有出现在屏幕上。

原因是 Chromium 128+（以及同期的 WebKit）改用 `content-visibility` 隐藏已折叠 `<details>` 的内容，
而不是给子元素 `display: none`。于是被折叠的输入框**仍然有布局盒**：
`offsetParent` 非空、`getBoundingClientRect()` 依然给出 1146×44 的真实尺寸，
只有引擎自己的 `checkVisibility({ contentVisibilityAuto: true })` 说它不可见。

判定改成两个真正等价于"人能看到"的问题：元素是否在任何已折叠的 `<details>`/`<dialog>` 里，
以及引擎的 `checkVisibility` 是否认可。顺带把断言加严了一条：折叠标题本身也不得包含令牌。
现在两种引擎都是 28/28。

### 8.4 顺带：`tools/*.ps1` 统一为 UTF-8 BOM

`Verify-InstallManifest.ps1` 是全仓库唯一没有 BOM 的 PowerShell 脚本。用 `pwsh -File`
调用时不受影响（PowerShell 7 默认按 UTF-8 读脚本），但用 Windows PowerShell 5.1 打开就会
把中文注释解析成乱码。已统一，属于 1 字节前缀的改动，行为不变。

## 9. 实机验收抓到的真缺陷：换 WPF 之后，更新程序再也停不掉外壳

这是这一轮**唯一**由实机验收（而不是单元测试或截图）发现的严重缺陷，值得单独记一笔。

### 现象

`tools/Update-ReleaseVerify.ps1` 跑到应用 1.6.0 更新包时：

```
[FAIL] T03-11 - 旧实例在 60 秒内没有退出，更新程序无法替换程序文件
[FAIL] T03-12 - 更新后未能在 240 秒内以版本 1.6.0 恢复服务
```

### 原因

更新程序在替换 `LanDisk.exe` 之前，会走管理端口的 `POST /api/admin/shutdown` 让旧实例退出
（"退出 LanDisk"按钮走的也是同一个入口）。宿主停机时 `ServerController` 会触发
`ExitRequested`，由外壳决定"服务没了，窗口也该走"。

WinForms 版本的 `TrayContext` 订阅了这个事件。**WPF 重写时这个订阅没有被搬过来**，
`ExitRequested` 只剩下触发方，没有任何订阅方——宿主停掉了，外壳却一直坐在托盘里，
更新程序只能超时回滚。

单元测试覆盖不到这一条：`LanDisk.App` 是一个 WinExe，测试项目引用不到它的窗口生命周期，
只有"真的装一次、真的升一次"才会暴露。

### 修法

`App.OnStartup` 里补上订阅，并把"退出"与"关窗口"两件事分开：

- `Host.ExitRequested` → 切到 UI 线程 → `Shell.PrepareForExit()`（让 `Closing` 不再取消）
  → `ExitAsync()`（停服务 + `Shutdown()`）；
- 如果 20 秒内没能正常退出，直接 `Environment.Exit(0)`：更新程序在等这个进程消失，
  卡住不走比硬退出更糟；
- 同时修掉 `ReportFatal` 里硬编码的 `%LOCALAPPDATA%\LanDisk`：崩溃日志现在写到命令行指定的
  数据目录，隔离运行时不会再往使用者真实数据目录里写东西。

修复后单点验证：发布出来的外壳在收到外部停机请求后 **0.8 秒**退出（退出码 0，修复前 60 秒不退）。

## 10. 把三句叙述变成可执行门禁

交付报告里有三句话，在很长一段时间里只能靠"我读过代码"来支持。这一轮把它们做成了会失败的断言
（`tests/LanDisk.Tests/ShellInvariantTests.cs`，5 项）：

| 报告里的断言 | 现在的门禁 |
|---|---|
| `NORMAL_FLOW_OPENS_BROWSER = NO` | `UX08-10`：外壳源码里 `UseShellExecute = true` **只能有两处** —— 一处打开日志目录（并断言它周围没有 `http`/`Url`），一处是 Web 管理恢复页且必须紧跟在 `NativeDialogs.Confirm` 之后；同时禁止 `WebBrowser` / `WebView2` / `LaunchUri` / `ShellExecute(` / `OpenBrowser` 这些第二入口 |
| `VPN_ROUTING_UNTOUCHED = YES` | `UX08-12`：扫描全部 87 个产品源文件，14 种路由/VPN/网卡配置写入模式**一个都不许命中** |
| 同上 | `UX08-13`：产品里所有 `netsh` 出现处必须都在安装器内，而安装器能用的动词只有 `advfirewall firewall add rule` / `delete rule` 两个 |
| `FRAMEWORK` / `WEBVIEW_USED`（工程侧） | `UX08-11`：工程文件必须是 `UseWPF=true` + `UseWindowsForms=false` + `AssemblyName=LanDisk` + `net10.0-windows`，且 WPF-UI 版本必须是写死的 `4.3.0` |

三条自我校验，避免门禁退化成"永远绿"：

1. 源文件枚举少于 5 个就直接失败（扫描读不到文件时不能算通过）；
2. 禁止模式探测器本身用植入样本自检（`route add …` / `Set-NetIPInterface …` 必须被抓到，
   纯读取的 `NetworkInterface.GetAllNetworkInterfaces()` 与写着 "Wintun / WireGuard" 的注释不能被误判）；
3. `UX08-14`：本轮证据编号必须各自只属于一个测试文件。这一条是被自己踩出来的——最初给外壳门禁用
   的 `UX08-02`/`UX08-03` 与根路由契约的两个编号撞了车，审核者按编号查证据会查到两条不相干的结论。
   （整套测试里 `BATCH x12`、`SORT x9` 这类"一个编号覆盖一组断言"是既有惯例，因此这里只约束
   本轮的 `UX07-*`/`UX08-*` 编号范围，不去动历史用法。）

这不是运行时证明（它证明不了"某个进程真的没打开浏览器"），它能证明的是"不存在这样一条代码路径"。
配合 `CAND-16/17/18` 从产物元数据证明"外壳是原生 WPF、没有任何 WebView"，这一组断言才是审核者
可以自己复核的东西。

