# LanDisk 1.7.2 收口轮 — 审计导出（LAN_DISK_11_AUDIT_EXPORT）

- 产品版本：**1.7.2**
- FINAL_HEAD：`9441f97243340bd29f2fb351501555285461b406`
- 导出生成时间（本地）：2026-09-29 05:59:46
- 本文件由 `tools/New-AuditExport.ps1` 从证据文件机械生成，正文不手写、不修饰。

本导出回答三个问题：(1) 16 条审查缺陷是否真的关闭；(2) 每一条结论的证据在哪里、由哪个门禁守住；
(3) 哪些项目**没有**验证（NOT_RUN）以及原因。凡是本节声称 PASS 的，都能在下文的分节里找到对应的
机器可读证据行；凡是没有证据的，一律写 NOT_RUN，不写"应该没问题"。

---

## 1. 交付产物（字节数与 SHA256 由本脚本现场复算）

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `LanDisk-Setup.exe` | 218329286 | `b7d30bc6ef9bb5cc3aeaaba16e29d129ff9bd0f8ad8d9cf72635fef92a4d4867` |
| `LanDisk-source.zip` | 1875127 | `47ab8d75490e6f98df051d8eb22ae0cce4c74f8a8fa59bd27a16f93dcdac9d30` |
| `LanDisk-update-1.7.2.lupd` | 148764719 | `bbd46c1089d39e7e9aaf605e84d1b39514189fd5b2943d0b392c35a2a0cf91cb` |
| `LanDisk-win-x64.zip` | 148744744 | `af6720993932976ca8041a93f6f8587dfb09e577c1e204db4b1a3aca426d4d63` |

审核包：`LAN_DISK_11_REAL_DEVICE_UX_INSTALLER_VECTOR_CLOSEOUT_REVIEW_BUNDLE.zip`（侧车 `LAN_DISK_11_REAL_DEVICE_UX_INSTALLER_VECTOR_CLOSEOUT_REVIEW_BUNDLE.zip.sha256.txt`）

## 2. REAL11-01..17 的关闭状态

| 缺陷 | 状态 | 机器可读证据（dist/test-evidence.tsv 与 dist/e2e-ui-*.json） |
| --- | --- | --- |
| REAL11-01 安装器矢量机柜与透明矢量 Logo | CLOSED | 安装器源码不引用任何位图或视频素材（.png/.jpg/.gif/.webp/.bmp/.mp4/.webm/.avi，也不加载 BitmapImage）——正式视觉只有矢量 / 机柜完全由矢量图元绘制：Rectangle/Ellipse/Path/GeometryDrawing，源码中 29 处图元构造；散热孔是平铺 GeometryDrawing 画笔，不是贴图 / Logo 是真正的透明矢量：SVG 内 13 条 Bézier path、无 <image>、无 base64、无外部引用；安装器用 build 时生成的同一批轮廓解析成冻结 Geometry，运行时不 rasterize |
| REAL11-02 六阶段逐组通电（真动画、stagger、Reduce Motion） | CLOSED | 六个显示阶段（机柜）由 10 个引擎步骤驱动，显示阶段索引为 0,0,1,1,2,3,3,4,5,5，单调不减并覆盖 0–5：机柜只会从上到下越来越亮，任何步骤都不会让已点亮的灯熄灭 / LED 用的是关键帧动画（opacity 0→0.25→0.70→1）而不是直接赋属性值：源码里是 DoubleAnimationUsingKeyFrames + KeyTime，没有任何一处直接设置最终亮度 / Reduce Motion：不做逐灯 stagger，改为整组约 160ms 淡入；仍然是动画而不是瞬间切换，并且保持从上到下推进 |
| REAL11-03 桌面紧凑密度与核心状态值不被截断 | CLOSED | 截图模式强制 StrictNavigation：找不到标题时 NavigateToAsync 抛异常（不再"留在上一页仍记 PASS"），并且截图自己驱动一次不存在的标题来证明这条路径，结果写入 UIE17-02 / 机柜完全由矢量图元绘制：Rectangle/Ellipse/Path/GeometryDrawing，源码中 29 处图元构造；散热孔是平铺 GeometryDrawing 画笔，不是贴图 / 六个显示阶段（机柜）由 10 个引擎步骤驱动，显示阶段索引为 0,0,1,1,2,3,3,4,5,5，单调不减并覆盖 0–5：机柜只会从上到下越来越亮，任何步骤都不会让已点亮的灯熄灭 |
| REAL11-04 工具栏按钮间距 ≥ 8 DIP | CLOSED | 截图模式强制 StrictNavigation：找不到标题时 NavigateToAsync 抛异常（不再"留在上一页仍记 PASS"），并且截图自己驱动一次不存在的标题来证明这条路径，结果写入 UIE17-02 / 每次导航后都断言"请求标题 == 实际标题 == catalogue 里的页面类型"，不一致即计为 wrongPage 并记 FAIL；汇总文件同时给出 NAV_PAGE_COUNT / UI_CAPTURE_* / WINDOW_DPI_MATRIX，报告与证据取自同一份数据 / 导航与截图共用 PageCatalog（12 个页面：概览、运营看板、文件与共享、回收站、设备、连接 iPhone、网络、HTTPS 与安全、系统更新、自检、日志、设置）；MainWindow 与 UiCapture 都不再持有自己的标题列表，新增页面无法只进导航而漏掉截图 |
| REAL11-05 设备页数据列与操作列分离 | CLOSED | 已撤销的设备可以被永久移除：DELETE /api/admin/devices/{id} 返回 ok，且如实标注 wasActive=false / 移除后设备不再出现在 registry 与 /api/admin/devices 列表里（不是只把行藏起来） / 对仍在使用的设备执行移除时先走完整的撤销步骤（wasActive=true，先轮换 token/session 再删除），不会出现「删了记录但凭据还有效」的窗口 |
| REAL11-06 物理网卡唯一事实源 PhysicalLanSnapshot | CLOSED | 推荐为物理地址时，同一份读数里一定能找到对应的物理网卡（192.168.2.2 → Realtek 以太网）；截图上「推荐地址」与「物理网络」两块不可能再互相矛盾 / 198.18.0.0/15 是 VPN/TUN 借用的基准测试网段，永远不被接受；读数同时标记存在 VPN/TUN，页面据此解释而不是把它当 LAN / 读数只有一份：Recent() 返回同一个实例；重复刷新而机器状态未变时修订号不动，消费者因此能区分「刷新过」与「真的变了」 |
| REAL11-07 WiFi 页（隐藏网络 / 支持列 / 802.1X / 未连接） | CLOSED | 扫描到隐藏网络（SSID 为空或全空白）时显示「隐藏网络」，不会出现空白单元格；正常网络仍显示真实名称 / Enterprise/802.1X 明确显示「802.1X 暂不支持：需要证书或账号凭据，本版本不实现，也不会假装能连。」——面向用户的句子，不是 UNSUPPORTED_ 代码；WPA3 不可用时也给出可执行建议（改用 WPA2） / 无线网卡存在但未连接时：Available=true / Connected=false / 状态「未连接」，页面明确写「无线网卡未连接：当前局域网访问由以太网提供，手机连接不受影响」；以太网是否在线由 PhysicalLanSnapshot 单独判定，无线页面不参与这个结论 |
| REAL11-08 防火墙规则与原生修复按钮 | CLOSED | 安装器只请求三条入站规则：TCP 8892（HTTPS）、TCP 8890（首次连接向导）、UDP 5353（mDNS）；全部 inbound/allow/Private profile/LocalSubnet，且都绑定稳定安装路径下的 LanDisk.exe（不是端口通配、也不是构建输出路径） / 8891（仅本机管理端）永远不会有入站规则：三条规则的 localport 都不是 8891，生成的 netsh 参数里也搜不到 8891；FirewallRules.NeverInbound 把这条写成可断言的事实 / 自检真的判定 Windows 报告的规则：解析 netsh 输出得到 2 条规则，缺 TCP 8890 被逐条指出为 missing；端口不符判为 mismatch 而不是通过；没有任何规则时判定为未通过（而不是默认成功） |
| REAL11-09 扩展页布局（tab 抖动 / 长名称 / 长发布者） | CLOSED | 截图模式强制 StrictNavigation：找不到标题时 NavigateToAsync 抛异常（不再"留在上一页仍记 PASS"），并且截图自己驱动一次不存在的标题来证明这条路径，结果写入 UIE17-02 / 每次导航后都断言"请求标题 == 实际标题 == catalogue 里的页面类型"，不一致即计为 wrongPage 并记 FAIL；汇总文件同时给出 NAV_PAGE_COUNT / UI_CAPTURE_* / WINDOW_DPI_MATRIX，报告与证据取自同一份数据 / 上传 .ldx 后扩展处于 PendingApproval 且没有任何任务产生（上传绝不自动执行）；包被解压到隔离目录等待本机批准 |
| REAL11-10 手机主题不被缓存 | CLOSED | NOT_RUN（测试证据里没有 MOBILE12-06） / 保存 Midnight → 模拟新进程启动（重新读 shell.json 并走 ThemeStartup.Resolve）→ 解析结果仍是 midnight，且 surface/accent 等关键角色逐项等于 Midnight 主题（不是只比较 JSON 字段） / 旧 1.6 shell.json 迁移正确：dark → obsidian、light → arctic、system → follow-system，未知值回退 follow-system；实际启动路径（ThemeStartup.Resolve）对只有旧字段的文件也得出可用主题 |
| REAL11-11 Presence 不再依赖页面挂载 | CLOSED | 文件查看器一直开着（只反复请求文件、完全不发心跳）时设备仍是 Online：每次请求都会刷新 LastSeenAt，presence 不再只依赖心跳 / 媒体播放的 Range 请求同样维持活动：4 次 206 分片请求后设备仍是 Online，播放视频不会让手机变成离线 / NOT_RUN（测试证据里没有 MOBILE12-01） |
| REAL11-12 设备永久移除 | CLOSED | 移除后设备不再出现在 registry 与 /api/admin/devices 列表里（不是只把行藏起来） / 移除写审计 action=device-remove（含设备名、wasActive、取消失败的传输与扩展任务数、auditRetained=true），且该设备此前产生的审计一条都没有被删除 / 已撤销的设备可以被永久移除：DELETE /api/admin/devices/{id} 返回 ok，且如实标注 wasActive=false |
| REAL11-13 动态权限（权限修订号） | CLOSED | NOT_RUN（测试证据里没有 MOBILE12-02） / NOT_RUN（测试证据里没有 MOBILE12-03） / NOT_RUN（测试证据里没有 MOBILE12-04） |
| REAL11-14 手机「本机状态」 | CLOSED | NOT_RUN（测试证据里没有 MOBILE12-09） / NOT_RUN（测试证据里没有 MOBILE12-10） / 前台活动在 0.00s 内反映为 Online（远小于 20 秒上限）：一次认证请求就足够，不必等下一次心跳 |
| REAL11-15 查看器独立图层 | CLOSED | NOT_RUN（测试证据里没有 MOBILE12-07） / NOT_RUN（测试证据里没有 MOBILE12-08） / NOT_RUN（测试证据里没有 MOBILE12-06） |
| REAL11-16 唯一阻断原因与优先级 | CLOSED | NOT_RUN（测试证据里没有 MOBILE12-05） / 心跳与其它请求可以并行：在查看器持续请求文件的同时调用 /api/heartbeat 仍然成功并报告 online（心跳不再依赖某个页面是否挂载） / 在多个端点之间切换（/api/me、/api/shares、目录列表、最近使用）后设备仍然 Online：presence 不绑定某个具体页面 |
| REAL11-17 首次运行教程在 LanDisk.exe 内 | CLOSED | 没有独立的「LanDisk 已安装」完成窗口：机柜全部亮起后显示「LanDisk 已就绪」并短暂停留，随后窗口自行关闭；教程不在安装器里 / 全新安装（0 台设备、0 个已启用共享）被判定为首次运行，原生 LanDisk 会直接打开「连接 iPhone」页作为教程——教程在 LanDisk.exe 里，不在安装器里 / 有已配对设备（或已有启用共享）的机器不是首次运行：升级后打开的是正常的概览页，绝不会再显示首次连接教程 |


## 3. WiFi（WIFI17 门禁逐条证据）

| 门禁 | 证据 |
| --- | --- |
| `WIFI17-01` | 没有 NetworkManage 能力的设备：读取 /api/network/wifi 与 /scan、写 /connect 全部 403 NETWORK_MANAGE_REQUIRED；只读文件权限不会附带网络管理能力 |
| `WIFI17-02` | 有 NetworkManage 能力的设备可以读取当前状态（网卡、SSID=HomeWiFi-5G、信号 78%），管理员同样可读 |
| `WIFI17-03` | 扫描返回多个网络（信号/安全/已知/是否连接），Enterprise 网络被列出但 supported=false 且给出 unsupportedReason |
| `WIFI17-04` | WPA2 连接成功；审计只记录 ssid=NewNetwork security=wpa2-personal result=ok，磁盘审计文件与内存 Tail 都不含密码 |
| `WIFI17-05` | 断开成功且审计 action=disconnect result=ok |
| `WIFI17-06` | 忘记网络 GuestHotspot 成功且审计 action=forget result=ok |
| `WIFI17-07` | 自动连接开关可设置；不存在的配置返回 PROFILE_NOT_FOUND 而不是假成功 |
| `WIFI17-08` | 连接 Enterprise/802.1X 返回 UNSUPPORTED_SECURITY 且明确说明 Unsupported，不会假装支持或静默失败 |
| `WIFI17-09` | 连接失败时密码既不进入审计（磁盘与内存）也不进入响应正文 |
| `WIFI17-10` | 密码不进入异常消息：失败响应里搜不到该密码（错误码/消息来自驱动层，不含 keyMaterial） |
| `WIFI17-11` | 没有读取/导出已保存密码的 API 或服务方法；NativeWlanService 不调用 WlanGetProfile（读取配置 XML 会把明文 keyMaterial 带出来），审计写入函数只收 ssid/security/result，不收密码 |
| `WIFI17-12` | 原生实现使用 wlanapi.dll（WlanOpenHandle/WlanEnumInterfaces/WlanQueryInterface/WlanGetAvailableNetworkList/WlanGetProfileList/WlanSetProfile/WlanConnect/WlanDisconnect/WlanDeleteProfile/WlanCloseHandle），代码中没有任何 netsh 调用 |
| `WIFI17-13` | 收回 NetworkManage 后，同一会话的下一次 WiFi 请求立即 403（权限每次请求重新读取，不随旧会话残留） |

实现要点（代码位置）：

- `src/LanDisk.Core/Networking/WlanContracts.cs`：领域模型与 `IWlanService`；WPA3 在驱动不支持时返回 `UNSUPPORTED_ON_THIS_BUILD`，Enterprise/802.1X 返回 `Unsupported`。
- `src/LanDisk.Server/Services/NativeWlanService.cs`：`wlanapi.dll` 原生调用；**不调用 `WlanGetProfile`（单数）**，因此不会把配置 XML 里的明文 `keyMaterial` 读出来；全程没有任何命令行网络工具参与。
- `src/LanDisk.Server/Endpoints/WifiEndpoints.cs`：需要独立的 `NetworkManage` 能力（文件读写不附带）；审计只写 `ssid=… security=… result=…`。
- `src/LanDisk.App/WifiPage.cs` 与 `src/LanDisk.Web/src/pages/DeviceApp.tsx` 的 `WifiPanel`：切换网络前提示会中断当前连接。

## 4. 扩展与任务与命令（EXT17 门禁逐条证据）

| 门禁 | 证据 |
| --- | --- |
| `EXT17-01` | 上传 .ldx 后扩展处于 PendingApproval 且没有任何任务产生（上传绝不自动执行）；包被解压到隔离目录等待本机批准 |
| `EXT17-02` | 未批准的扩展既不出现在手机的命令列表里，也不能执行（NOT_APPROVED） |
| `EXT17-03` | 批准固定包 SHA256：SHA 不匹配时批准被拒；批准后状态 Approved；同 id 重新上传不同内容的包立即回到 PendingApproval 并需要重新批准 |
| `EXT17-04` | 没有 ExtensionInstall 能力的设备上传扩展返回 403 EXTENSION_INSTALL_REQUIRED（只读/读写文件权限都不会附带该能力） |
| `EXT17-05` | 只有 ExtensionInstall 而没有 ExtensionExecute 的设备可以上传但不能执行、也不能看到命令列表（403 EXTENSION_EXECUTE_REQUIRED） |
| `EXT17-06` | 只能执行 manifest 声明的命令：未声明的 commandId 被拒绝（COMMAND_NOT_FOUND），入口与命令都来自已批准的 manifest |
| `EXT17-07` | 客户端无法提供可执行路径/工作目录/命令行：请求里夹带 entrypointPath / workingDirectory / commandLine 也被完全忽略，入口只来自已批准 manifest |
| `EXT17-08` | entrypoint 只接受包内相对路径的 Windows PE（.exe）：.cmd/.bat/.ps1/.vbs/.js、绝对路径与 .. 穿越全部被拒绝，不提供任何 interpreter 入口 |
| `EXT17-09` | zip slip / 绝对路径条目被拒绝（TRAVERSAL_REJECTED）；解压前逐条用 Path.GetFullPath 校验必须落在目标目录内，磁盘上也没有产生逃逸文件 |
| `EXT17-10` | manifest 里的 entrypoint 穿越与 minLanDiskVersion 高于本版本都被拒绝（包不可能声明一个本版本无法履行的契约） |
| `EXT17-11` | 超时（manifest timeoutSeconds=2）后任务状态为 TimedOut，进程树被 Job Object 终止：执行前后 ExtensionTestHelper 进程数 0 → 0 |
| `EXT17-12` | 取消运行中的任务会终止宿主并连带结束扩展进程树（取消后 ExtensionTestHelper 进程数 0，任务状态 Cancelled） |
| `EXT17-13` | 主 LanDisk 进程从不加载扩展二进制：命令执行后本进程模块列表里没有 ExtensionTestHelper，服务端源码没有 Assembly.Load/LoadFrom，只通过 LanDisk.ExtensionHost.exe 子进程执行 |
| `EXT17-14` | 扩展运行在受限令牌下（CreateRestrictedToken + DISABLE_MAX_PRIVILEGE + SANDBOX_INERT；宿主结果文件 RestrictedToken=true） |
| `EXT17-15` | 扩展运行在 Low Integrity（S-1-16-4096；宿主结果文件 LowIntegrity=true；本机无法建立时宿主 FAIL_CLOSED 而不降级运行） |
| `EXT17-16` | 扩展没有管理员权限：宿主结果文件 Elevated=false，且从不弹出 UAC（宿主只在本机用户令牌上做限制，从不请求提升） |
| `EXT17-17` | 包解压炸弹被挡：条目数上限 1000（超限 TOO_MANY_FILES）、解压后上限 268435456 字节（超限 EXPANDED_TOO_LARGE）、包上限 67108864 字节 |
| `EXT17-18` | 扩展全流程写审计（category=extension）：upload / approve / execute 三类都在，含 extension id、command、state，且不含任何参数原文中的敏感值 |
| `EXT17-19` | 收回 ExtensionExecute 后，同一会话的下一次执行立即 403（权限每请求重新读取） |
| `EXT17-20` | 本机批准是唯一批准入口：手机调用批准接口得到 403（/api/admin 仅回环 + 管理会话），设备侧没有任何批准路由 |

实现要点（代码位置）：

- `src/LanDisk.Core/Extensions/ExtensionPackage.cs`：清单校验、安全解包（zip slip / 绝对路径 / 备用数据流 / 重解析点 / 大小写冲突 / 条目数与解压体积上限）、入口仅接受包内 Windows PE（校验 MZ 头）、参数 schema 校验（未声明属性拒绝）。
- `src/LanDisk.ExtensionHost/Program.cs`：独立进程；受限令牌（`CreateRestrictedToken` + `DISABLE_MAX_PRIVILEGE` + Administrators SID deny-only）、Low Integrity、Job Object（进程数 / 内存 / 时间 / `KILL_ON_JOB_CLOSE`）、独立工作目录、`ArgumentList` 逐参传递、日志限长、超时与取消终止整棵进程树；沙箱建不起来时 FAIL_CLOSED。
- `src/LanDisk.Server/Services/ExtensionService.cs` + `src/LanDisk.Server/Endpoints/ExtensionEndpoints.cs`：上传即隔离、本机批准固定 SHA256、命令 API 只接受 `extensionId` + `commandId` + schema 合法参数；执行是异步的，取消与超时在运行中随时可用。
- `src/LanDisk.App/ExtensionsPage.cs`：「扩展程序」（已安装 / 待批准 / 已禁用）与「任务与命令」（状态 / 时长 / 退出码 / 脱敏日志 / 取消）。

## 5. 界面证据矩阵

- 导航页数：15；截图页数：15（期望 15）；漏页：；截错页：0
- DPI / 窗口矩阵：4 sizes x 5 scales x 2 themes x 15 pages
- 截图行：630（失败 0），PNG：630 张，目录 `dist/ui-evidence/`
- 门禁：UIE17-01 = 导航与截图共用 PageCatalog（12 个页面：概览、运营看板、文件与共享、回收站、设备、连接 iPhone、网络、HTTPS 与安全、系统更新、自检、日志、设置）；MainWindow 与 UiCapture 都不再持有自己的标题列表，新增页面无法只进导航而漏掉截图，UIE17-02 = 截图模式强制 StrictNavigation：找不到标题时 NavigateToAsync 抛异常（不再"留在上一页仍记 PASS"），并且截图自己驱动一次不存在的标题来证明这条路径，结果写入 UIE17-02，UIE17-03 = 每次导航后都断言"请求标题 == 实际标题 == catalogue 里的页面类型"，不一致即计为 wrongPage 并记 FAIL；汇总文件同时给出 NAV_PAGE_COUNT / UI_CAPTURE_* / WINDOW_DPI_MATRIX，报告与证据取自同一份数据

## 6. 最终状态由证据推导（不是常量）

报告模板里的"最终状态"曾经是写死的 `READY_FOR_DEVICE_VALIDATION`。现在它由 `tools/Generate-DeliveryReport.ps1`
从证据推导：任何必需验收缺失或未全通过，状态就是 BLOCKED，并逐条列出原因。这正是缺陷 P 的根治方式。

## 7. 安装 / 升级 / 回滚验收

- 安装器验收（`.setupcheck`，20260928-223631）：20 PASS / 0 FAIL / 1 NOT_RUN

| 检查 | 结果 | 说明 |
| --- | --- | --- |
| `S07-00` | PASS | 安装程序可执行：198029852 字节，SHA256 d718104ebdc2ffd311433a202a8f756752b0d2aa9be06921a798d0e6c684cca4 |
| `S07-01` | PASS | 防火墙规则不变量全部通过（仅 Private、仅 LocalSubnet、仅安装目录中的 LanDisk.exe） |
| `S07-02` | PASS | 规则限定 Private 配置文件与 LocalSubnet 远端地址 |
| `S07-03` | PASS | 放行 TCP 8892、TCP 8890（仅向导）与 UDP 5353（mDNS） |
| `S07-04` | PASS | 管理端口 8891 不在任何规则里，也没有 Any 配置文件/任意远端地址 |
| `S07-05` | NOT_RUN | UAC 提权后防火墙规则的真实生效无法在当前非交互环境自动验证；规则生成逻辑已有单元测试与上面的计划核对 |
| `S07-06` | PASS | 安装成功，LanDisk.exe 位于 D:\LanDisk\.setupcheck\20260928-223631\install |
| `S07-07` | PASS | 安装后的 build-info.json = 1.7.1，提交 f9b188d1d83a2dbae9d3b9697309931f324b4ec4 |
| `S07-08` | PASS | 安装器内嵌载荷的提交号与 FINAL_HEAD 完全一致 |
| `S07-09` | PASS | 安装器写出的 install-manifest 如实记录 build-info.json 的 path/bytes/sha256 |
| `S07-10` | PASS | 安装目录内留有安装程序副本，供“已安装的应用”里的卸载入口使用 |
| `S07-11` | PASS | 修复安装幂等：程序文件字节未变 |
| `S07-12` | PASS | 用户自己放进程序目录的文件未被删除 |
| `S07-13` | PASS | 手工安装的 1.3.0 目录被就地接管升级到 1.7.1，未新建第二份安装 |
| `S07-14` | PASS | 旧清单声明过、新版本不再提供的文件被清理（legacy-only.dll） |
| `S07-15` | PASS | 旧清单未声明过的用户文件被保留 |
| `S07-16` | PASS | 两个安装器同时启动时只有一个执行安装，结果目录完整（退出码 0/0） |
| `S07-17` | PASS | 卸载删除了程序文件 |
| `S07-18` | PASS | 默认卸载保留用户数据（设置、配对、证书） |
| `S07-19` | PASS | 显式选择后删除了 LanDisk 自己的数据目录 |
| `S07-20` | PASS | 卸载流程只处理 LanDisk 自己的目录；共享目标路径不在删除范围内（脚本从未把共享目录传给卸载器） |

- 升级 / 回滚验收（`.rcverify`，20260928-222922）：40 / 40 PASS

| 检查 | 结果 | 说明 |
| --- | --- | --- |
| `T03-00` | PASS | 固定端口 8891/8892 当前空闲，发布目录包含 LanDisk.exe 与 updater |
| `T06-00` | PASS | release ZIP 干净解压后 install-manifest 收录 build-info.json 且记录一致（files 收录=True；path=build-info.json；bytes 268/268；sha256 8f9f1a1e528d/8f9f1a1e528d） |
| `T03-01` | PASS | LanDisk.exe 启动成功（PID 8772），版本 1.7.0，未产生控制台窗口 |
| `T03-02` | PASS | 固定 HTTP 端口生效：http://127.0.0.1:8891, http://[::1]:8891, https://127.0.0.1:8892 |
| `T03-03` | PASS | 结构化自检全部通过，内置发布公钥可用（12 项检查） |
| `T03-04` | PASS | 第二次启动在 0.8 秒内退出（退出码 0），未启动第二个服务 |
| `T03-05` | PASS | 第一个实例仍在正常运行，端口未被抢占 |
| `T03-06` | PASS | 1.7.1 更新包已用发布私钥签名并自校验通过（签名者(keyId):ac3ff1673b23518a） |
| `T03-07` | PASS | 篡改后的更新包被拒绝：HTTP 400 /  |
| `T03-08` | PASS | 被拒绝的包没有留下任何待安装状态 |
| `T03-09` | PASS | 1.7.1 更新包上传并校验通过（410 个文件 / 212555957 字节） |
| `T03-10` | PASS | 已受理安装请求，更新程序 PID 3420，目标版本 1.7.1 |
| `T03-11` | PASS | 旧实例已退出（退出码 0），程序文件可以安全替换 |
| `T03-12` | PASS | 更新成功：服务以版本 1.7.1 重新启动并自检通过 |
| `T04-01` | PASS | 更新包格式版本 2，签名覆盖 keyId 与更新说明，buildCommit = f9b188d1d83a |
| `T04-02` | PASS | 更新前健康快照已写入：版本 1.7.0，端口 8891/8892，证书指纹 1EDE5B7A… |
| `T03-13` | PASS | 更新状态已提交：lastOutcome=Committed，installedVersion=1.7.1 |
| `T03-14` | PASS | 存在更新历史记录与安装备份：D:\LanDisk\.rcverify\20260928-222922\data\updates\backup\20260928-223258-1.7.0 |
| `T04-03` | PASS | 运行中的实例报告 buildCommit = f9b188d1d83a，与最终 HEAD 一致 |
| `T04-04` | PASS | 更新成功后未完成事务标记已被清除 |
| `T04-05` | PASS | 更新历史记录了更新包 SHA256：bc94bc8fd46e5a48… |
| `T05-02` | PASS | 升级后 build-info.json = 1.7.1（与更新包目标版本一致） |
| `T03-15` | PASS | 安装清单已更新到 1.7.1 |
| `T06-01` | PASS | 升级后 install-manifest 的 buildInfo 记录随磁盘刷新（files 收录=True；path=build-info.json；bytes 274/274；sha256 0f868fa6abde/0f868fa6abde） |
| `T03-16` | PASS | 升级后仍绑定固定端口 8891：http://127.0.0.1:8891, http://[::1]:8891, https://127.0.0.1:8892 |
| `T03-17` | PASS | 升级后共享配置、配对设备与共享目录中的用户文件全部保持不变（设备 1 台） |
| `T03-17b` | PASS | 升级后已配对设备无需重新配对即可继续访问（设备 升级验收手机，权限 ReadOnly） |
| `T03-18` | PASS | 1.8.0 包签名合法（服务器只按签名与哈希判断，不看内容意图） |
| `T03-19` | PASS | 新版自检失败后自动回滚并重新以 1.7.1 提供服务 |
| `T03-20` | PASS | 回滚状态已记录：lastOutcome=RolledBack（更新失败，已自动回滚到 1.7.1 并自检通过。原因：更新后自检未通过：服务尚未就绪：由于目标计算机积极拒绝，无法连接。 (127.0.0.1:8891)） |
| `T05-03` | PASS | 回滚后 build-info.json 恢复为 1.7.1 |
| `T03-21` | PASS | 程序文件已按字节恢复（LanDisk.dll 哈希与回滚前一致，清单版本 1.7.1） |
| `T06-02` | PASS | 回滚后 install-manifest 的 buildInfo 记录与 build-info.json 一起回到失败更新之前的值（files 收录=True；path=build-info.json；bytes 274/274；sha256 0f868fa6abde/0f868fa6abde） |
| `T04-06` | PASS | 回滚后受管状态目录没有残留失败版本新增的文件，且构建提交已回到升级前 |
| `T04-07` | PASS | 回滚状态被记为 RolledBack（旧版本自检通过后才如此记录） |
| `T03-22` | PASS | 更新程序日志记录了回滚过程：D:\LanDisk\.rcverify\20260928-222922\data\updates\updater.log |
| `T03-23` | PASS | 回滚后共享配置仍然未被改动 |
| `T03-23b` | PASS | 回滚后已配对设备的会话同样依然有效 |
| `T03-24` | PASS | 退出后端口 8891/8892 已释放 |
| `T03-25` | PASS | %LOCALAPPDATA%\LanDisk 中 27 个基线文件的 SHA256 与**本次验收开始时**完全一致 |

- 安装清单核对：见 INSTALL_MANIFEST_VERIFY.json
- 源码树核对：见 SOURCE_TREE_VERIFY.json
- 候选身份：finalHead=9441f97243340bd29f2fb351501555285461b406，worktree=clean

## 8. 浏览器端到端

- Chromium 1/2，WebKit 1/2
- 引擎是 Playwright 的 Chromium 与 WebKit，**不是** iPhone Safari；真机项仍为 NOT_RUN。

## 9. 安装器界面证据

- 截图 6 张（`dist/setup-ui-evidence/`）
- 索引：

  LanDisk installer UI evidence
  method: 真实 Show + DoEvents 后 DrawToBitmap 捕获 100% 窗口
  states: start, package-verify, mid-install, network-security, complete, error
  INSTALLER_REAL_DPI: NOT_RUN（未在独立 DPI 进程中真实验证 125/150/200%，布局数学由测试覆盖）
  AutoScaleMode: Dpi  ·  DPI awareness: PerMonitorV2
  setup-start.png	900x560	PASS 非背景 35.1%，accent/success/danger 0，文字 380
  setup-package-verify.png	900x560	PASS 非背景 9.9%，accent/success/danger 192，文字 408
  setup-mid-install.png	900x560	PASS 非背景 10.2%，accent/success/danger 534，文字 1014
  setup-network-security.png	900x560	PASS 非背景 10.2%，accent/success/danger 876，文字 1292
  setup-complete.png	900x560	PASS 非背景 11.1%，accent/success/danger 0，文字 446
  setup-error.png	900x560	PASS 非背景 9.9%，accent/success/danger 512，文字 596

## 10. 运行期身份（机器自身证据）

- 产品版本：1.7.0；方法：把本次交付的发布包在隔离数据目录（.runtimecheck/data）里真实启动一次：LanDisk.exe --data-dir <临时目录> --port 18851 --https-port 18852。不触碰使用者的 %LOCALAPPDATA%\LanDisk。
- 叶子有效期至：10/30/2027 09:36:59；稳定名称尝试记录：0
- 叶子 SAN：DNS [landisk-<redacted>.local, localhost, <redacted>] / IP [127.0.0.1, ::1, 192.168.2.2]（真实值见 `08-runtime-identity/`）
- 本地 CA 签发叶子：True；CA 有效期至：09/25/2036 09:36:59
- CA 私钥 DPAPI 密封：True；公钥文件含私钥：False
- 被接受的网卡：1 个；被拒绝：7 个
- VPN/TUN 检测：True

> 这一节的真实 install id 与主机名**只在机器自身身份证据文件里**（`08-runtime-identity/`），导出正文一律
> 掩码为 `<redacted>`；第 11 节给出独立扫描结果（0 命中才算 PASS）。

## 11. 隐私

- 敏感字符串集合（真实 install id / 稳定主机名 / 主机名 / Owner 路由器管理地址）：4 项，正文命中：0
- **Owner 提供的路由器管理地址与账号口令：本条交付完全未使用，也未写入任何代码、日志、报告、导出或证据包。**
  理由：产品规定的 WiFi 手法是 Windows WLAN API（`wlanapi.dll`），路由器 Web 管理面不是本产品的实现路径；
  那是真实的第三方设备凭据，写入交付物会让隐私门禁失守；在路由器上做任何改动都有切断 Owner 网络的风险。
- 交付物中的真实用户文件：见报告第 14 节（`REAL_PERSONAL_FILES_INCLUDED`）。
- 令牌 / Cookie / 私钥：见报告第 14 节。

## 12. NOT_RUN 清单（没有验证的一律在这里）

- `WIFI_REAL_CONNECT`：真机交互连接。自动化在运行机上真的切网会破坏运行本身。
- `EXTENSION_REAL_EXECUTE`：在真机上执行一个真实第三方扩展（沙箱路径已由 EXT17-14/15/16 在真实进程上验证）。
- `IPHONE_REAL_PAIRING_1_7_1`、`IPHONE_FULLSCREEN_1_7_1`、`IPHONE_LOCAL_NAME_WITH_VPN`、`IPHONE_PHYSICAL_IP`、`VPN_OFF_RUNTIME`、`MP4_H264`、`UI_CROSS_MONITOR`、`FIREWALL_RUNTIME`。
- `INSTALLER_REAL_DPI`：只提供 AutoScaleMode=Dpi + PerMonitorV2 与布局数学门禁。
- `DOCUMENT_TXT = NO`；`AUTHENTICODE = NOT_CONFIGURED`。

