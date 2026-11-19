# LanDisk 10 · 收口任务（WiFi + 扩展）审计修正记录

- 基线：`START_HEAD = 3bbfe1b61c1043cd8a7e705cbde51b905c701e3d`，`START_VERSION = 1.7.0`，`git status --porcelain` 为空（已核对）
- 本轮版本：**1.7.1**（1.7.0 已作为正式候选发布过，因此不做原地改写，只递增）
- 原则：本轮不把任何审查缺陷降级为「非阻塞说明」。每一条都必须有「代码改动 + 可复核证据 + 门禁」。

---

## A. P18：WiFi 完全没有实现

**审查结论**：产品里没有 WiFi 功能，只有说明文字。

**根因**：网络页只有「读取本机网卡地址」的能力，从未接入 Windows WLAN API，也没有任何 WiFi 领域模型。

**修正（已实现，不是说明）**

| 层 | 文件 | 内容 |
| --- | --- | --- |
| 契约 | `src/LanDisk.Core/Networking/WlanContracts.cs` | `WlanSecurity` / `WlanNetwork` / `WlanStatus` / `WlanResult` / `WlanProfile` / `IWlanService`（`Status`/`Scan`/`Profiles`/`Connect`/`Disconnect`/`Forget`/`SetAutoConnect`/`SupportsWpa3`）；`WlanSecurityNames.Support` 对 WPA3 返回 `UNSUPPORTED_ON_THIS_BUILD`、对 802.1X/Enterprise 返回 `Unsupported` |
| 实现 | `src/LanDisk.Server/Services/NativeWlanService.cs` | `wlanapi.dll` P/Invoke：`WlanOpenHandle`/`WlanEnumInterfaces`/`WlanQueryInterface`/`WlanGetAvailableNetworkList`/`WlanGetProfileList`/`WlanSetProfile`/`WlanConnect`/`WlanDisconnect`/`WlanDeleteProfile`/`WlanCloseHandle`。**不调用 `WlanGetProfile`（单数）**，因为那会把配置文件 XML 里的明文 `keyMaterial` 读出来；`netsh` 一次都没有出现 |
| 接口 | `src/LanDisk.Server/Endpoints/WifiEndpoints.cs` | `GET /api/network/wifi`、`/scan`，`POST /connect`、`/disconnect`、`/forget`，`PUT /profile/auto-connect` |
| 权限 | 同上 | `MayManage(session)`：本机管理端，或设备具备 `NetworkManage` 能力；否则 403 `NETWORK_MANAGE_REQUIRED`。**文件读写权限绝不附带网络管理** |
| 审计 | 同上 | 只写 `ssid=… security=… result=…`，密码从不进入审计（内存与磁盘都不含） |
| 桌面 | `src/LanDisk.App/WifiPage.cs` + `PageCatalog` | 新页面「WiFi」：当前连接（网卡/状态/SSID/信号/安全/IP/速率）、附近网络（含每行连接/忘记/不支持）、已保存网络；连接前弹「切换 WiFi 可能立即中断当前 LanDisk 连接。」确认，密码只在对话框作用域内存在 |
| 手机 | `src/LanDisk.Web/src/pages/DeviceApp.tsx` | `WifiPanel`，仅在 `permissions.manageNetwork` 时渲染，含同一句中断警告；密码只用于那一次请求 |

**证据**：`WIFI17-01` … `WIFI17-13`（13 项，全部通过，见 `tests/LanDisk.Tests/WifiManagementTests.cs`）。
真机交互连接标记 `WIFI_REAL_CONNECT = NOT_RUN`（原因：测试过程中切断运行机网络会破坏本次运行本身，而不是「没有设备所以不做」）。

**门禁**：`WIFI17-01`（无能力 403）、`WIFI17-02/03`（状态与扫描）、`WIFI17-04/05/06`（连接/断开/忘记 + 审计）、`WIFI17-07`（自动连接）`WIFI17-08`（Enterprise 明确不支持）、`WIFI17-09/10/11`（密码不进入审计/响应/错误，且没有读取已保存密码的 API）、`WIFI17-12`（wlanapi 且无 netsh）、`WIFI17-13`（收回能力后立即 403）。

---

## B. P19：扩展 / 命令中心完全没有实现

**审查结论**：没有扩展框架，也没有任务与命令中心。

**修正（已实现）**

| 层 | 文件 | 内容 |
| --- | --- | --- |
| 包格式 | `src/LanDisk.Core/Extensions/ExtensionPackage.cs` | `.ldx` = ZIP，含 `manifest.json`（`schemaVersion`/`id`/`name`/`version`/`publisher`/`entrypoint`/`commands[]`/`minLanDiskVersion`/`timeoutSeconds`） |
| 安全解包 | 同上 | 逐条 `Path.GetFullPath` 校验必须落在解压目录内：拒绝 `..` 穿越、绝对路径、UNC、盘符/备用数据流冒号、重解析点/符号链接条目、大小写冲突条目；包体积上限 64 MiB、解压上限 256 MiB、条目数上限 1000 |
| 入口限制 | 同上 | `entrypoint` 只能是包内相对路径的 **Windows PE（.exe，含 MZ 头校验）**；`.cmd/.bat/.ps1/.vbs/.js/.jse/.wsf/.wsh/.com/.scr/.pif`、绝对路径、`..` 全部拒绝；没有 interpreter 入口 |
| 参数校验 | 同上 | `ValidateArguments`：必须是对象、`required` 必须存在、每个值的类型必须匹配 schema、**未声明的属性一律拒绝** |
| 宿主 | `src/LanDisk.ExtensionHost/LanDisk.ExtensionHost.csproj` + `Program.cs` | 独立进程，是唯一执行扩展代码的地方。受限令牌（`CreateRestrictedToken` + `DISABLE_MAX_PRIVILEGE` + 把 Administrators SID 置为 deny-only）、Low Integrity（`SetTokenInformation(TokenIntegrityLevel, S-1-16-4096)`）、Job Object（进程数 4 / 进程内存 256 MiB / 进程与作业时间上限 / `KILL_ON_JOB_CLOSE`）、按任务独立工作目录、`ArgumentList` 逐参传递、stdout/stderr 上限 64 KiB、超时与取消 `TerminateJobObject` 终止整棵进程树。**受限令牌或 Low Integrity 建不起来时 FAIL_CLOSED，绝不退化为高权限执行**；从不请求 UAC |
| 服务 | `src/LanDisk.Server/Services/ExtensionService.cs` | 上传→隔离（`PendingApproval`）→本机批准（固定 SHA256）→执行→任务记录；`Cancel` 先把状态改成 `Cancelled` 再杀宿主，`Finish` 不会把已取消的任务改写成失败 |
| 接口 | `src/LanDisk.Server/Endpoints/ExtensionEndpoints.cs` | 设备侧：`POST /api/extensions/upload`（需 `ExtensionInstall`）、`GET /commands`、`POST /execute`（需 `ExtensionExecute`）、`GET /tasks`（只能看自己的）。管理侧：`GET /api/admin/extensions/`、`POST /{id}/approve`、`POST /{id}/enabled`、`POST /tasks/{taskId}/cancel`。**设备只能给 extensionId + commandId + schema 合法参数**；没有路径、工作目录、命令行输入面 |
| 桌面 | `src/LanDisk.App/ExtensionsPage.cs` + `PageCatalog` | 「扩展程序」页：已安装/待批准/已禁用三个页签，显示名称/版本/发布者/SHA256/大小/入口/命令与 schema/状态；首次安装必须显式批准（对话框显示被固定的 SHA256）；「任务与命令」页：任务 ID、来源设备、扩展、命令、状态、开始时间、时长、退出码、脱敏且限长的日志、运行中取消；页面 2 秒自刷新 |
| 手机 | `src/LanDisk.Web/src/pages/DeviceApp.tsx` | `ExtensionsPanel`，仅在 `permissions.executeExtensions` 时渲染；参数按 schema 生成默认值并校验为 JSON |

**证据**：`EXT17-01` … `EXT17-20`（20 项，全部通过，见 `tests/LanDisk.Tests/ExtensionFrameworkTests.cs`），载荷是真实 Windows PE（`tests/ExtensionTestHelper/ExtensionTestHelper.exe`）。

实测（本机，非模拟）：宿主结果文件 `restrictedToken=true`、`lowIntegrity=true`、`elevated=false`；扩展自身打印 `elevated: False`、`integrity: low`（即扩展进程内自证不是管理员且处于低完整性）。

**门禁**：`EXT17-01`（上传不执行）、`02`（未批准不能执行/不可见）、`03`（SHA 固定、换包需重批）、`04/05`（安装/执行能力分离）、`06`（只能跑声明过的命令）、`07`（客户端无法提供路径/命令行）、`08`（脚本入口拒绝）、`09`（zip slip）、`10`（manifest 穿越与高版本要求）、`11`（超时杀进程树）、`12`（取消杀进程树）、`13`（主进程不加载扩展二进制）、`14/15/16`（受限令牌 / Low Integrity / 无管理员权限）、`17`（解压炸弹）、`18`（全流程审计）、`19`（收回能力立即生效）、`20`（本机批准是唯一批准入口）。

---

## C. UI_CAPTURE 页面清单陈旧

**根因**：导航栏和截图脚本各自维护一份手写页面名列表，改版后两者漂移（共享→文件与共享、新增运营看板、HTTPS / 安全→HTTPS 与安全），截图脚本按旧名字导航失败后**停在上一页仍然写 PASS**。

**修正**：新增 `src/LanDisk.App/PageCatalog.cs` 作为唯一来源；导航栏（`MainWindow.BuildNavigation`）与 UI 截图（`UiCapture`）都从它读。截图对每个页面做**身份校验**（`actual.GetType() == descriptor.PageType && window.CurrentTitle == page`），不一致计入 `UI_CAPTURE_WRONG_PAGE`；`StrictNavigation = true` 时导航到不存在的标题直接抛异常。本轮页面数为 **15**。

**证据**：`UI_CAPTURE_PAGE_COUNT / EXPECTED / ACTUAL / MISSING / WRONG_PAGE` 全部写入 `UI_CAPTURE_SUMMARY.tsv`，`UIE17-01/02/03` 门禁断言 `MISSING=0`、`WRONG_PAGE=0`、页面数与 `PageCatalog.All` 一致。

---

## D. OperationsPage 从未被截图

**根因**：页面清单陈旧（见 C），「运营看板」根本不在列表里。

**修正**：清单来自 `PageCatalog.All`，运营看板自然进入矩阵；`UIE17-03` 断言 `NAV_PAGE_COUNT == UI_CAPTURE_PAGE_COUNT`，任何「加了页面但没截」都会失败。

---

## E. 旧标题「共享」「HTTPS / 安全」截错页面

**根因**：同 C，且旧实现导航失败后无声继续。

**修正**：同 C（唯一来源 + 身份校验 + 严格导航），并把标题写进证据行，使「截错页」不再可能静默通过。

---

## F. DPI / 窗口矩阵并未真正执行

**根因**：旧截图只用一个窗口尺寸，DPI 缩放没有参与，等于对一个固定配置截图后宣称覆盖了矩阵。

**修正**：`src/LanDisk.App/UiCapture.cs` 改为真实矩阵：
- 物理尺寸 `1366×768`、`1600×900`、`1920×1080`、`2560×1440`
- DPI 缩放 `1.00 / 1.25 / 1.50 / 1.75 / 2.00`
- 每组合 × 深色/浅色 × 每个页面
- `logicalWidth = physicalWidth / dpiScale`，位图按物理像素（`content.ActualWidth * scale`）渲染
- 另有 `1160×780` 基线

`WINDOW_DPI_MATRIX` 写入汇总文件；`UIE17-02` 断言矩阵配置数与断言表一致，且 `0` 缺失、`0` 错页。同时新增 `SeedWorstCaseData()`（4 个共享含超长名/离线/待确认、5 台设备含在线/最近/离线/已撤销/能力矩阵、50 条审计、60 次指标注入）与 `ArmOnboarding()`，确保截的不是空页面。

---

## G. `MinWidth=1100 / MinHeight=700` 在高 DPI 小屏溢出

**根因**：最小尺寸按逻辑像素写死，125%/150% 缩放下 1366×768 屏幕可用的逻辑宽度不足 1100，窗口直接被裁。

**修正**：`MainWindow.xaml` 改为 `MinWidth="640" MinHeight="400"`；导航栏在 `ActualWidth < 900` 时自动收起为 56 px 图标栏（`ApplyRailMode()` 调整 `RailColumn.Width`，并隐藏分组标题与文字）；对应 DPI 矩阵里 `1366×768 @1.50/1.75/2.00` 与 `1600×900 @1.75/2.00` 全部页面截图通过。

---

## H. 安装程序 24 张 UI 截图是纯黑空窗

**根因**：安装程序的 UI 证据用 `DrawToBitmap` 在窗口尚未真正布局/绘制时截图，得到未绘制的深色背景，却被当作「已覆盖 UI」。

**修正**：`src/LanDisk.Setup/Program.cs` 的 `RenderInstallerUi` 重写：离屏 `form.Show()` 后执行 8 次 `Application.DoEvents()` + `Thread.Sleep(30)` 等待绘制完成，再 `DrawToBitmap`；随后 `VerifyBitmap` 逐张校验（非背景像素占比 ≥3%、必须出现文字像素、进行中状态必须出现节点色、错误状态必须出现危险色），任一不满足即 FAIL 并返回退出码 2，同时写 `setup-ui-index.txt`。

**实测**：6/6 状态通过（`setup-start` 非背景 35.5%，`package-verify` 9.9%/192 文字像素，`mid-install` 10.2%/534，`network-security` 10.2%/876，`complete` 11.1%，`error` 9.8%/169）。

**门禁**：`SETUP17-01`（`INSTALLER_SCREENSHOT_COUNT == 6` 且每张非空校验通过）、`SETUP17-02`（状态列表覆盖 6 个状态）。

---

## I. 安装程序「DPI 证据」只是位图放大

**根因**：把 96 DPI 位图插值放大后说成高 DPI 证据。

**修正**：删除这种「证据」。安装程序改为 `ApplicationHighDpiMode=PerMonitorV2`（csproj）+ `AutoScaleMode = AutoScaleMode.Dpi`（`SetupForm`），并从 `app.manifest` 移除与新属性冲突的 `dpiAware`/`dpiAwareness`。证据中如实写 `INSTALLER_REAL_DPI: NOT_RUN`，只保留可复核的布局数学与 `SETUP17-03` 门禁（节点位置与可用宽度/高度的关系），不再把放大位图当 DPI 证据。

---

## J. 安装程序节点进度不单调（0→2→1）

**根因**：`SetupEngine` 把 `PreserveUserData`（保留用户数据）排在 `InstallFiles` 之前执行，而节点映射按「安装程序并保留用户数据」在「安装程序」之后，于是节点序出现回退。

**修正**：新增 `src/LanDisk.Setup/SetupStageMap.cs`：6 个阶段（验证安装包 / 停止旧版本·准备程序 / 安装程序并保留用户数据 / 配置本地服务 / 配置 HTTPS 与防火墙 / 启动并确认服务 Ready）、`StageIndex(SetupStep)`（未映射即抛异常）、`IsMonotonic(...)`、`SetupProgressState`（`Complete`/`SetActive`/`SetError`/`IsLit`/`IsError`）。`SetupEngine` 把 `PreserveUserData` 移到 `InstallFiles` 之后，`SetupStep` 增加 `VerifyPayload`/`PreserveUserData`，`EngineStepOrder` 与阶段表一一对应。`SetupForm.OnStep` 用 `StageIndex`，失败时 `OnStepFailed()` → `SetError(CurrentStage)` 并把进度页（而非完成页）与该失败节点一起显示；`SetupConsole.ServerNodeStrip` 也改为由 `SetupProgressState` 驱动，错误节点用危险色 + × 字形。

**门禁**：`SETUP17-02`（引擎步骤序列单调不减）、`SETUP17-03`（`IsLit` 语义：错误阶段之后不再点亮，且错误之前已完成的节点保持点亮）、`SETUP17-04`（每个 `SetupStep` 都能映射到阶段，未映射抛异常）。

---

## K. Windows 命名/自定义主题重启后不恢复

**根因**：启动时只按「深/浅」偏好重建预置主题，命名主题与自定义主题没有被解析回来。

**修正**：新增 `src/LanDisk.Core/Configuration/ThemeStartup.cs`：`MigrateLegacy(legacyTheme)`（dark→obsidian、light→arctic、其它→跟随系统）、`Resolve(preferences, systemPrefersDark)`（自定义 → 命名 → 旧字段 → 跟随系统）、`ResolveMobile(profileId, custom, devicePrefersDark)`。`ThemeManager.ApplyFromPreferences` 改为委托给 `ThemeStartup.Resolve` 并返回生效的 `ThemeProfile`，新增 `ActiveProfileId` 与 `ApplyCustom(profile)`。

**门禁**：`THEME17-09`（自定义优先于命名）、`THEME17-10`（命名优先于旧字段）、`THEME17-11`（旧字段迁移映射）、`THEME17-12`（跟随系统时按系统深浅选择）。

---

## L. 自定义 Windows 主题编辑器丢字段标签

**根因**：编辑器用一串无标签的输入框，用户不知道哪个框是什么。

**修正**：`SettingsPage` 的 `ShowThemeEditorAsync` 改为带标签的三个字段（`ThemeField(StackPanel Root, TextBox Input)`），并新增实时预览卡（`ThemePreviewCard`）与对比度门禁：正文 ≥4.5:1、状态色 ≥3:1（`ThemeCatalog.Validate`）。

**门禁**：`THEME17-13`（编辑器每个输入都有可见标签）、`THEME17-14`（对比度不足时拒绝应用）。

---

## M. Windows 没有入口创建自定义 iPhone 主题

**根因**：手机上只能选预置外观，桌面端没有编辑自定义 iPhone 主题的入口。

**修正**：`SettingsPage` 新增「自定义 iPhone 主题…」与「恢复默认（跟随 iPhone 系统）」按钮，`EditCustomMobileThemeAsync` 与 Windows 版共用 `ShowThemeEditorAsync`（同样的标签、预览与对比度门禁）；`ThemeStartup.ResolveMobile` 负责自定义 iPhone 主题的解析。

**门禁**：`THEME17-15`（自定义 iPhone 主题可达且被移动端解析优先）。

---

## N. 运营看板系统 CPU 算法错误（内核时间包含空闲）

**根因**：直接用 `kernelTime` 当「忙」时间，但 Windows 的 `KernelTime` 含 `IdleTime`，于是 CPU 占用被显著高估。

**修正**：`RuntimeMetrics.SystemCpuPercent(prevIdle, prevKernel, prevUser, idle, kernel, user)` 纯函数：`total = kernelDelta + userDelta`，`busy = max(0, total - idleDelta)`，结果裁剪到 0–100。另加 `InjectSample(up, down)`（仅截图用）与 `Snapshot()`（含 `Network.RecommendedOrigin/RecommendedKind/StableOrigin/PhysicalOrigin`）。

**门禁**：`OPS17-01`（空闲增量 → 0%）、`OPS17-02`（满负载 → 100%）、`OPS17-03`（内核时间含空闲时不会高估）、`OPS17-04`（负增量/回绕被裁剪，不出现负值或 >100）。

---

## O. 网络/看板仍把稳定的 `.local` 当手机地址

**根因**：推荐地址直接取稳定名称（mDNS `.local`），但 VPN/TUN 存在时 `.local` 常常不可达，用户按提示连不上。

**修正**：`AdminOperations` 新增 `RecommendedHostKind` 与 `RecommendedHttpsOrigin()` 的解析顺序：已验证地址 → 物理地址（检测到 VPN/TUN 时）→ 稳定名称（mDNS 正常时）→ 物理地址；`StableHttpsOrigin()`、`PhysicalHttpsOrigin()`。`NetworkPage`「当前服务」改为显示 `推荐访问地址`（附原因）、`物理 HTTPS 地址`、`稳定名称（VPN/TUN 可能阻断）`；运营看板推荐徽标同样来自该解析。

**门禁**：`NET17-01`（推荐顺序：已验证优先）、`NET17-02`（VPN 存在时不推荐 `.local`）、`NET17-03`（稳定名称仍单独可见，且带阻断提示）。

---

## P. 报告与审计导出的 UI 证据描述互相矛盾

**根因**：报告生成器与审计导出各自用不同的措辞描述同一批 UI 证据（一处说「已覆盖 12 页 × 4 尺寸 × 5 DPI」，另一处说「抽样」），两者都不完全属实。

**修正**：两处改为读同一份 `UI_CAPTURE_SUMMARY.tsv` 的字段（`NAV_PAGE_COUNT`、`UI_CAPTURE_PAGE_COUNT/EXPECTED/ACTUAL/MISSING/WRONG_PAGE`、`WINDOW_DPI_MATRIX`、`UIE17-01/02/03`）与安装程序证据（`INSTALLER_SCREENSHOT_COUNT`、`INSTALLER_NONBLANK_CHECK`、`INSTALLER_STAGE_MONOTONIC`），不再各自手写描述。本文件即两处的共同来源。

---

## 有意变更的既有门禁（不是放宽，是行为改变）

| 门禁 | 变更 | 原因 |
| --- | --- | --- |
| `ShellInvariantTests`（浏览器打开） | 允许的 shell-execute 站点 2 → 1，新增 `NativeDialogs.OpenFolder` 且拒绝任何含 `://` 的输入 | 目录打开不再经过 shell 字符串拼接 |
| `FIX-09-05e` | 改为断言主题目录（`ThemeCatalog`）而非旧的枚举 | 主题改为目录驱动 |
| `UI-MOBILE-NAV` | 期望 6 个标签页且标签全部存在 | 手机底部导航定稿 |
| `UiCapture` 12 DIP 字号规则 | 排除 WPF-UI `SymbolIcon` 字形（11 DIP 折叠箭头） | 图标字形不是文字，规则仍对文字生效 |

## 必须保持 NOT_RUN 的真机项

`IPHONE_REAL_PAIRING_1_7_1`、`IPHONE_FULLSCREEN_1_7_1`、`WIFI_REAL_CONNECT`（本轮新增），以及既有的 `IPHONE_LOCAL_NAME_WITH_VPN`、`IPHONE_PHYSICAL_IP`、`VPN_OFF_RUNTIME`、`MP4_H264`、`UI_CROSS_MONITOR`、`FIREWALL_RUNTIME`；`DOCUMENT_TXT = NO`；`AUTHENTICODE = NOT_CONFIGURED`；`INSTALLER_REAL_DPI = NOT_RUN`（仅 AutoScaleMode=Dpi + PerMonitorV2 + 布局数学）。

## 关于用户提供的路由器管理地址

用户提供了一个路由器管理页面地址与账号口令，并说明可以用于测试但**不得切断任何网络**。

**决定：不使用，也不写入任何交付物。** 原因：
1. 产品规定的手法是 Windows WLAN API（`wlanapi.dll`），路由器的 Web 管理面不是本产品的实现路径，用它做「验证」不能证明产品成立；
2. 那是真实的第三方设备凭据，写进任何交付物都会让隐私门禁失守；
3. 在路由器上做任何更改都有切断网络的风险，与「不得切断网络」相冲突。

因此该地址与凭据未出现在代码、日志、报告、审计导出或证据包中；WiFi 的验证完全走 `FakeWlanService`（`WIFI17-*`）与 Windows WLAN API 的代码路径检查，真机连接保持 `NOT_RUN`。
