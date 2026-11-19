# 第 10 轮（1.7.0）修正记录

本轮是产品化统一开发：实现 → 测试 → 全量回归 → 自动验收 → 封包。本文记录**改了什么、为什么这样改、以及哪些没有做**。
没有做到的部分一律写成 BLOCKED 并给出精确原因，不写成"部分支持"。

---

## 1. 动态权限：会话只证明身份

### 原来错在哪

会话 cookie 只带一个 token 哈希，`SessionMiddleware` 用它取回设备记录 —— 这一点本来是对的。真正的问题是**权限判定的消费点**：

1. 上传的分块、续传、提交三个端点只检查"这个上传会话是不是这台设备的"，**完全不检查共享权限**。于是把手机从读写改成只读之后，已经开始的上传会一路写完，用户看到的是"权限已经收回，文件还是出现了"。
2. 手机端只在页面加载时读一次 `/api/me`，之后一直用那份 `capabilities` 渲染按钮。服务端早就按实时记录判定了，但用户看不到任何变化 —— 报告里的"手机刷新仍不能获得新权限"就是这个。

### 现在怎么做

- `RequestSession.Permissions`（`DevicePermissions.For(device)`）与 `ApiHelpers.EffectiveLevel(session)` 都在**每次请求**从实时设备记录推导；文件类端点全部经由 `ApiHelpers.TryAuthorizeShare` 判定。
- 上传的 `POST /{id}/resume`、`PUT /{id}/chunk`、`POST /{id}/complete` 现在都调用同一个 `RevokedWhileUploading`：权限一旦不足，先 `Uploads.Cancel(...)` 取消会话（staging 立即停止增长，也不可能再发布），再返回 **403 `UPLOAD_PERMISSION_REVOKED`**。
- 手机端每 15 秒心跳一次；心跳返回 `permissions` 与 `presence`，并在 `writeFiles` 发生变化时重新读取 `/api/me`（共享级 `canUpload/canRename/canDelete` 来自那里）。按钮从"隐藏"改为"禁用 + 说明原因"，因为消失的按钮看起来像功能缺失。
- **权限修改不轮换 `SessionEpoch`**：只有撤销才轮换。这是"不需要重新配对"的实现含义，由 `AUTH17-06` 与 `AUTH17-07` 分别守住两个方向。

### 新增的设备能力

`DeviceCapability`（`FileWrite` / `NetworkManage` / `ExtensionInstall` / `ExtensionExecute` / `SystemView`）与文件读写等级**分离**，默认全为 `None`。
升级安全性：旧 `devices.json` 里没有这个字段，反序列化得到 `None` —— 升级永远不会悄悄扩大某个手机能做的事。
`SetDeviceCapabilities` 接受**完整集合**而不是增量：调用方必须明确说出它想要的最终权限，"少发一个字段"不会留下一个本该收回的能力。写入 `action=capabilities` 审计。

---

## 2. 在线状态：测量而不是推断

- 每个认证请求（`SessionMiddleware` 末尾）与每次心跳都调用 `Devices.Touch(...)`，只改内存、不写磁盘；`LanDiskHost` 的 60 秒清理循环与关停路径负责 `FlushActivity()`。15 秒心跳 → 每分钟一次磁盘写入。
- 阈值集中在 `DeviceActivity`：`Online ≤ 45 s`、`RecentlyActive ≤ 5 min`、否则 `Offline`；`Revoked` 单独一类，**永远不是 online**。
  在线窗口比心跳间隔宽三倍，是因为 Safari 在后台标签页会挂起定时器 —— 45 秒是"容忍三次丢失的心跳"，而不是随手取的数。
- 阈值有 `ONLINE17-03/04/05` 三个边界断言钉住（44 s / 46 s / 4.9 min / 5.1 min）。

---

## 3. 配对完成页：不要再推荐一个刚刚打不开的地址

`ConfirmPairing` 会在成功后关闭 8890 并清空"本次可达 host"提示 —— 而完成页是之后才渲染的。原来的实现因此在完成页回退到稳定名称，正是 VPN/TUN 开启时刚刚失败的那个地址。

修正是**在还知道的时候记下来**：`ConfirmPairing` 先把 `AdminOperations.RecommendedHttpsOrigin()`（本次已验证 host → 已发布的稳定名称 → 物理 LAN 地址）写进 `PairingRequest.RecommendedOrigin`，`PairingCompletion` 带着它返回给完成页。
它是这一次流程的展示信息：不持久化、不参与任何授权判定，随配对请求一起过期。

---

## 4. 主题系统

- `ThemeProfile` + `ThemeCatalog`：五套内置（跟随系统 / Obsidian / Graphite / Midnight / Arctic）与自定义；`ThemeRoles.All` 定义 23 个语义角色，每套内置主题都必须**完整覆盖**，否则 `Make` 直接抛错（构建期就失败，而不是页面上少一个颜色）。
- 自定义主题只要求三个输入（强调色 / 背景 / 表面）：悬停、按下、边框、文字色全部派生。保存前 `Validate` 按 WCAG 相对亮度做对比度检查，正文 ≥4.5:1、状态色 ≥3:1；不通过就**拒绝保存并指出具体是哪一对颜色**。
- 主题只能表达"角色名 → `#AARRGGBB`"：没有 CSS、没有选择器、没有脚本，非法颜色值在 `ThemePalette.Rgb` 阶段抛 `FormatException`（`THEME17-04` 用 `javascript:alert(1)` 验证了这一点）。
- Windows 与 iPhone 分开配置：`shell.json` 存 Windows 主题（含自定义角色的完整定义），`mobile-theme.json` 存手机主题与修订号。手机通过认证 API 取得**暗色/亮色两套完整角色**并以 CSS 变量应用；心跳携带修订号，变化时自动重新获取。
- 「跟随 iPhone 系统」是唯一允许手机自行决定的选项，也是默认值；其余主题完全由 Windows 指定，手机上不存在自定义入口。

---

## 5. 界面系统与响应式

- `DesignTokens.cs`：7 级间距、3 级圆角、7 级字号、Windows 字体栈；页面不再出现零散数字。
- `AdaptiveTable`：**一个** Grid 定义列宽（表头与数据在构造上不可能错位），列带最小宽度与优先级；宽度不足时从最低优先级开始丢弃，被丢弃单元格的内容进入行内「详情」；操作按钮收进「…」溢出菜单，永不换行。
  这直接修掉 P05/P06/P07/P09 的共同根因：旧实现"每行一个 Grid、各自按内容算列宽"。
- 颜色只以语义角色名出现（`Ui.Brush("TextSecondary")`），由当前主题解析；缺角色时返回洋红色 —— 在截图里显眼地坏掉，而不是悄悄画成背景色。
- 主窗口默认最大化、最小 1100×700。任务书提醒"不能仅靠设置 MinWidth 掩盖布局问题"，因此最小尺寸只是下限，页面本身仍然按宽度降级。

### 安装器（P01/P02）

- 深色 Deployment Console（900×560）、自绘 `ConsoleButton`、无传统进度条。
- 进度是六个服务器节点灯，由 `SetupEngine` 的**真实阶段回调**驱动：`Inspect/VerifyPayload → 0`、`StopRunning/InstallFiles → 1`、`PreserveUserData → 2`、`Shortcuts/Register → 3`、`Firewall → 4`、`Launch/Ready → 5`。节点只在对应阶段**完成后**点亮；当前节点轻微呼吸；`ReduceMotion` 时退化为静态高亮。没有任何定时器推进进度。
- 新增真实阶段 `VerifyPayload` 与 `PreserveUserData`（原引擎没有单独报告"用户数据保持不动"这一步）。
- **不再打开浏览器**：完成页的「打开 LanDisk」启动 `LanDisk.exe`（最大化），「连接 iPhone」用新增的 `--page "连接 iPhone"` 让原生窗口停在向导页。`/admin` 只剩高级折叠区里一个明确标注、需要二次确认的恢复入口。
- `LanDisk-Setup.exe --render-ui <dir>`：自绘六个状态 × 四档缩放 = 24 张 PNG。截图不依赖桌面 DPI，也不依赖窗口是否在最前。

### 与任务书建议的差异（如实记录）

安装器宿主**仍是 WinForms**，视觉层是 GDI+ 自绘，而不是任务书建议的 WPF/XAML vector + Storyboard 动画。原因：安装器是独立可执行文件，要在任何东西安装之前运行，改成 WPF 意味着重写部署界面与其证据管线；而本轮把预算优先给了权限语义、在线状态、主题系统与全部 Windows 页面。报告的视觉缺陷（白底、大片空白、文字挤压、旧式控件、无视觉体系）已全部修复并有 24 张自绘证据，但**实现方式与建议不同**，因此 P01 记为 PARTIAL，而不是 FIXED。

---

## 6. 明确没有做的事

- **P18 WiFi 管理：没有实现。** 没有任何 WiFi 页面、入口、API 或占位按钮。
  要做对需要 `wlanapi.dll` 原生调用（`WlanOpenHandle` / `WlanEnumInterfaces` / `WlanGetAvailableNetworkList` / `WlanSetProfile` / `WlanConnect` / `WlanDeleteProfile`），而任务书同时要求：不得以 netsh 为主实现、Enterprise/802.1X 必须明确 unsupported 而非假实现、WiFi 密码只允许在内存中用于连接且禁止进入日志/审计/截图/配置文件、永不读取或导出系统已有明文密码、自动化测试不得真的切断 Owner 当前网络。在没有可隔离 WLAN 适配器与真机连接验收条件的前提下，交付完整的 WIFI17-01..08 无法诚实地完成；放一个点了没反应的入口比没有更糟。
- **P19 扩展程序与命令中心：没有实现。** 没有任何扩展上传、批准、执行或任务列表入口。
  要做对需要独立的 `LanDisk.ExtensionHost.exe`（受限令牌、去除可继承特权、Low Integrity、独立工作目录、Job Object、时间/内存/并发限制、可取消、不弹 UAC、需要管理员权限的扩展第一版直接 unsupported），以及 13 项安全门禁（zip slip、manifest 路径穿越、包 SHA 固定与改动后重新批准、任意 executable/cmd/powershell 字符串拒绝、超时与取消能杀掉 worker、日志与审计完整等）。这是独立的安全执行子系统，本轮预算不足；半成品会违反任务书"禁止任意远程 shell / 禁止上传后静默执行"的硬性要求。
  设备能力位 `ExtensionInstall` / `ExtensionExecute` 已经存在、默认全关、与文件读写分离，Windows 设备页可以授予或收回并写审计 —— 但**没有**它们能驱动的功能。
- **P14 的「移动」**：后端没有移动接口，未实现，也没有伪装。
- 没有实现公网访问、云账户或远程互联网控制。
- 没有改动：`PathResolver`、卷身份、上传/下载核心、回收站核心、配对密码学、Root CA 模型、更新签名与回滚模型、mDNS 修复后的套接字语义、`PhysicalLanSelector` 规则。

---

## 7. 真实用户数据

自动测试全部使用临时目录。`%LOCALAPPDATA%\LanDisk` 只在 setup/update 验收中按既有安全流程被升级，并在前后逐文件比较 SHA256（26 个基线文件，0 处变化；`install.json` 的安装 id 与稳定名称与 08 基线一致）。

## 8. 真机项目

`IPHONE_LOCAL_NAME_WITH_VPN`、`IPHONE_PHYSICAL_IP`、`PAIRING_REAL_IPHONE`、`VPN_OFF_RUNTIME`、**视频全屏（真机）**、**WiFi 真实连接**、**扩展真实执行** 全部保持 NOT_RUN —— DSH 没有 Owner 的真实 iPhone，也没有可隔离的 WLAN 适配器，不得冒充真机验证。

---

## 9. 有意改动的既有门禁（必须记录，不能悄悄放宽）

两处源码闸门在本轮**被收紧或改变了断言对象**，原因是产品行为有意变化。记录在此，便于审核对照：

1. `ShellInvariantTests.The_shell_opens_a_browser_from_exactly_one_guarded_place`（任务 08 门禁）
   - 原来允许外壳中有**两个** `UseShellExecute = true` 调用点（打开日志目录、打开 Web 管理恢复页）。
   - 本轮共享页新增了"打开共享目录"操作，于是"打开文件夹"的调用点变成两处。这个门禁读不出意图，因此正确的修法不是放宽数量，而是**把打开文件夹收敛到一个带约束的助手**：`NativeDialogs.OpenFolder` 会拒绝任何包含 `://` 的字符串，也就是从结构上不可能变成第二条通往浏览器的路。
   - 现在断言：外壳里**只有一个** `UseShellExecute = true` 代码调用点，且它必须位于带 `NativeDialogs.Confirm(...)`、写明"故障恢复 / 浏览器"的恢复入口内；同时断言 `OpenFolder` 助手存在且包含 URL 拒绝逻辑，日志目录必须经由它打开。
   - 结论：**比原来更严**（2 → 1 个调用点，并新增助手约束）。

2. `NativeParityGateTests.The_theme_switch_goes_through_the_one_theme_manager`（任务 09 FIX-09-05e）
   - 原来断言设置页源码里同时出现 `ShellTheme.Light` / `ShellTheme.Dark` / `ShellTheme.System` 三个入口。
   - 本轮主题系统改为命名主题 + 自定义，"三个按钮"的实现形式确实消失了，但这条门禁真正要保护的是"浅色不能被悄悄丢掉"。因此断言对象改成**主题目录本身**：`ThemeCatalog.Windows` 与 `ThemeCatalog.Mobile` 必须存在，`follow-system`、`obsidian`、`arctic` 必须齐备（即跟随系统与深/浅两套具名主题都在）。
   - 同时保留原有的一半：不得存在只切 WPF-UI 的 `App.ApplyTheme(bool)`，且必须经由 `ThemeManager.ApplyProfile` + `ThemeManager.Save` + `ShellPreferences` 持久化。
   - 结论：**断言更强**（从"某文件里有三个字符串"变成"主题目录必须完整"，并由 THEME17-01/02/07 在运行期进一步校验每套主题的角色完整性与对比度）。

两处都遵守同一条规则：产品行为有意变化时，**改门禁的期望并记录**，而不是把门禁删掉或写一条模糊的新断言。