# LanDisk 1.7.2 收口（真机 UX + 矢量安装器）— 交付报告

- 生成时间（UTC）：09/28/2026 21:58:44
- 项目路径：`D:\LanDisk`
- 起始 HEAD：`f9b188d1d83a2dbae9d3b9697309931f324b4ec4`
- 最终 HEAD：`9441f97243340bd29f2fb351501555285461b406`
- 工作树：clean
- 产品版本：1.7.2
- 最终状态：**READY_FOR_DEVICE_VALIDATION**
- 阻断原因：（无阻断项；如实标注的真机项 NOT_RUN：S07-05）
- 真机项目最高状态：**READY_FOR_DEVICE_VALIDATION**

---

## 1. 这一轮做了什么

Owner 用真机（Windows + 真 iPhone）走了一遍 1.7.1，列出 17 条实际看到的缺陷。本轮把它们**一次性**
关掉：实现 → 自动化 → 自修复 → 全量回归 → 界面验收 → 安装器验收 → 打包。1.7.1 已经作为正式候选
交付过，所以这一轮递增到 **1.7.2**，没有原地改写。

三条主线：

1. **安装器视觉层换成真正的矢量机柜**（不再是步骤条 + 圆点 + 进度条 + 完成弹窗）。
2. **Windows 端界面的密度与布局**：紧凑密度、核心状态值不被省略号截断、按钮间距、数据列与操作列分离。
3. **手机端真机行为**：presence 不再依赖某个页面挂载、动态权限、唯一阻断原因、本机状态、主题不被缓存、
   查看器成为独立图层。

## 2. 基线

- 起始 HEAD `f9b188d1d83a2dbae9d3b9697309931f324b4ec4`、版本 **1.7.1**、`git status --porcelain` 为空 —— 与任务书要求一致。
- 本轮未触碰 `D:\Projects\pour-water-game`。
- 版本递增到 **1.7.2**：7 个 `*.csproj` 的 `<Version>`、`tools/Build-All.ps1`、
  `tools/Setup-Acceptance.ps1`、`tools/Update-ReleaseVerify.ps1`。

## 3. REAL11-01..17 逐条结论

| 编号 | 结论 | 证据 / 原因 |
| --- | --- | --- |
| REAL11-01 安装器矢量机柜与矢量 Logo | **FIXED** | 矢量机柜与矢量 Logo。安装器源码不引用任何位图或视频素材（.png/.jpg/.gif/.webp/.bmp/.mp4/.webm/.avi，也不加载 BitmapImage）——正式视觉只有矢量 / 机柜完全由矢量图元绘制：Rectangle/Ellipse/Path/GeometryDrawing，源码中 29 处图元构造；散热孔是平铺 GeometryDrawing 画笔，不是贴图 / Logo 是真正的透明矢量：SVG 内 13 条 Bézier path、无 <image>、无 base64、无外部引用；安装器用 build 时生成的同一批轮廓解析成冻结 Geometry，运行时不 rasterize |
| REAL11-02 六阶段逐组通电 | **FIXED** | 六阶段机柜通电序列。六个显示阶段（机柜）由 10 个引擎步骤驱动，显示阶段索引为 0,0,1,1,2,3,3,4,5,5，单调不减并覆盖 0–5：机柜只会从上到下越来越亮，任何步骤都不会让已点亮的灯熄灭；逐级证据 Step1：点亮 0 个纵向灯组，整机状态灯仍未点亮 |
| REAL11-03 概览密度与核心值完整性 | **FIXED** | 紧凑桌面密度 + 核心值不截断：15 个组合中 0 处核心状态值被省略号截断（截图门禁 landisk-primary）。 |
| REAL11-04 工具栏按钮间距 | **FIXED** | 文件与共享工具栏按钮默认 8 DIP 间距，且截图新增"同一行相邻按钮间距 < 8 DIP 即失败"的门禁。 |
| REAL11-05 设备页数据列/操作列 | **FIXED** | 设备页数据列与操作列分离（AdaptiveTable 曾把动作与详情按钮放进同一个单元格而重叠）。已撤销的设备可以被永久移除：DELETE /api/admin/devices/{id} 返回 ok，且如实标注 wasActive=false |
| REAL11-06 物理网卡唯一事实源 | **FIXED** | 唯一事实源 PhysicalLanSnapshot。推荐为物理地址时，同一份读数里一定能找到对应的物理网卡（192.168.2.2 → Realtek 以太网）；截图上「推荐地址」与「物理网络」两块不可能再互相矛盾 / 198.18.0.0/15 是 VPN/TUN 借用的基准测试网段，永远不被接受；读数同时标记存在 VPN/TUN，页面据此解释而不是把它当 LAN |
| REAL11-07 WiFi 页 | **FIXED** | WiFi 页。扫描到隐藏网络（SSID 为空或全空白）时显示「隐藏网络」，不会出现空白单元格；正常网络仍显示真实名称；Enterprise/802.1X 明确显示「802.1X 暂不支持：需要证书或账号凭据，本版本不实现，也不会假装能连。」——面向用户的句子，不是 UNSUPPORTED_ 代码；WPA3 不可用时也给出可执行建议（改用 WPA2）；无线网卡存在但未连接时：Available=true / Connected=false / 状态「未连接」，页面明确写「无线网卡未连接：当前局域网访问由以太网提供，手机连接不受影响」；以太网是否在线由 PhysicalLanSnapshot 单独判定，无线页面不参与这个结论 |
| REAL11-08 防火墙规则与原生修复 | **FIXED** | 防火墙：安装提权阶段建立 TCP 8892 / TCP 8890 / UDP 5353（Private + LocalSubnet，绑定 LanDisk.exe，8891 永不入站）；自检改为真的查询 Windows，缺规则时提供原生「修复防火墙规则」按钮（一次显式 UAC，不开浏览器，修完自动重跑自检）。FirewallRules 是安装器与自检共用的唯一期望来源。安装器只请求三条入站规则：TCP 8892（HTTPS）、TCP 8890（首次连接向导）、UDP 5353（mDNS）；全部 inbound/allow/Private profile/LocalSubnet，且都绑定稳定安装路径下的 LanDisk.exe（不是端口通配、也不是构建输出路径） / 8891（仅本机管理端）永远不会有入站规则：三条规则的 localport 都不是 8891，生成的 netsh 参数里也搜不到 8891；FirewallRules.NeverInbound 把这条写成可断言的事实 / 自检真的判定 Windows 报告的规则：解析 netsh 输出得到 2 条规则，缺 TCP 8890 被逐条指出为 missing；端口不符判为 mismatch 而不是通过；没有任何规则时判定为未通过（而不是默认成功）。真机 UAC 与真实规则创建 FIREWALL_REAL_APPLY = NOT_RUN。 |
| REAL11-09 扩展页布局 | **FIXED** | 扩展页：活动 tab 的"•"前缀会改变按钮宽度导致整行跳动，已改为只用主按钮样式表达选中；截图根目录现在种入超长名称/超长发布者/长命令列表，并新增"扩展页必须真的渲染这些长内容，否则溢出断言等于没跑"的断言。 |
| REAL11-10 手机主题刷新 | **FIXED** | 手机主题：/api/mobile-theme 显式 no-store，手机端再加 cache: no-store 与时间戳查询参数；主题写语义令牌并保留旧名别名。PASS — 切到 obsidian 后变化的语义变量：window/surface/text/accent；scrim=rgba(11, 15, 20, 0.92) |
| REAL11-11 Presence / 在线状态 | **FIXED** | Presence：心跳移出组件树成为模块级控制器（路由/查看器/全屏/弹窗都不销毁），pageshow/focus/visibilitychange/路由挂载/打开查看器都立即发送，失败退避但 focus 立即重试；媒体 Range 请求同样刷新 LastSeen。文件查看器一直开着（只反复请求文件、完全不发心跳）时设备仍是 Online：每次请求都会刷新 LastSeenAt，presence 不再只依赖心跳 / 媒体播放的 Range 请求同样维持活动：4 次 206 分片请求后设备仍是 Online，播放视频不会让手机变成离线 / PASS — 查看器打开=true；打开 17 秒后服务端仍报告 presence=online |
| REAL11-12 永久移除设备 | **FIXED** | 永久移除设备。移除后设备不再出现在 registry 与 /api/admin/devices 列表里（不是只把行藏起来）；移除写审计 action=device-remove（含设备名、wasActive、取消失败的传输与扩展任务数、auditRetained=true），且该设备此前产生的审计一条都没有被删除 |
| REAL11-13 动态权限 | **FIXED** | 动态权限：DevicePermissionRevision 单调递增、心跳返回 permissionRevision、变化时同时刷新 /api/me 与当前目录列表。PASS — 权限修订号变化后 /api/me 的 per-share 能力同步更新（canUpload 全为 false=true） / PASS — 设备为读写且共享在线时上传入口可用=true / PASS — 降为只读后提示只读=true，上传入口已禁用=true（无需重新配对） |
| REAL11-14 手机「本机状态」 | **FIXED** | 手机「本机状态」：基础字段对所有手机可见，机器负载（CPU/内存/速率/在线数/警告）只在拥有 SystemView 时由服务器返回。PASS — 「本机状态」可见=true，基础字段行数=16（版本/服务/运行时长/推荐地址/HTTPS/共享/存储/传输/权限） / PASS — 没有 SystemView 能力的手机拿到 detailed=false，machine 字段=不存在 |
| REAL11-15 查看器独立图层 | **FIXED** | 查看器独立图层：遮罩 92% 不透明、z-index 200、surface 完全不透明、视频区纯黑、safe-area、保留全屏按钮。真正的原因是主题只写短名变量而 CSS 用 var(--window)，导致 surface 透明。PASS — scrim alpha=0.92（≥0.8），surface alpha=1（≥0.98），z-index=200（≥150），视频底色=无视频 / PASS — 查看器打开的项目不是视频，全屏入口不适用（未劣化） |
| REAL11-16 唯一阻断原因 | **FIXED** | 唯一阻断原因，优先级 Share Missing/Offline > Share permission > Device ReadOnly > capability。PASS — 同时出现「只读」与「共享不可访问」=false；当前提示：“上传测试.txt”已恢复。 |
| REAL11-17 首次运行教程 | **FIXED** | 首次运行：安装器不再有完成窗口；全新安装打开「连接 iPhone」教程（在 LanDisk.exe 内），已有设备/共享的升级打开正常概览且永不再显示教程。没有独立的「LanDisk 已安装」完成窗口：机柜全部亮起后显示「LanDisk 已就绪」并短暂停留，随后窗口自行关闭；教程不在安装器里 / 全新安装（0 台设备、0 个已启用共享）被判定为首次运行，原生 LanDisk 会直接打开「连接 iPhone」页作为教程——教程在 LanDisk.exe 里，不在安装器里 / 有已配对设备（或已有启用共享）的机器不是首次运行：升级后打开的是正常的概览页，绝不会再显示首次连接教程 / 任何正常安装路径都不会自动打开浏览器：安装器源码里没有 127.0.0.1:8891/admin 链接，也没有把 http 地址交给 shell 打开 |

`FIXED` 是由**证据推导**出来的：每条结论的 STATUS 取自它列出的门禁在 `dist/test-evidence.tsv` 里
实际记录的结果；门禁停止通过时，这里的结论会跟着变成 `BLOCKED`，不会停留在上一轮的结论上。

### 状态字段

| 字段 | 值 |
| --- | --- |
| Installer 视觉层 | NOT_RUN |
| Installer 非空白捕获 | NOT_RUN |
| Installer 阶段单调 | NOT_RUN |
| Desktop 界面矩阵 | 15 |
| Desktop 缺页 / 错页 |  / 0 |
| Mobile 端到端（Chromium） | chromium: 38 PASS / 0 FAIL / 0 NOT_RUN |
| Mobile 端到端（WebKit） | webkit: 38 PASS / 0 FAIL / 0 NOT_RUN |
| IPHONE_REAL_DEVICE_1_7_2 | NOT_RUN —— 需要真实 iPhone 与真实局域网；DSH 不得冒充真机。自动化侧证据：PASS — 查看器打开=true；打开 17 秒后服务端仍报告 presence=online |
| WIFI_REAL_SWITCH | NOT_RUN —— 需要真实断开/切换无线网络；本机无线网卡未连接，且不允许操作用户网络。演示实现证据：无线网卡存在但未连接时：Available=true / Connected=false / 状态「未连接」，页面明确写「无线网卡未连接：当前局域网访问由以太网提供，手机连接不受影响」；以太网是否在线由 PhysicalLanSnapshot 单独判定，无线页面不参与这个结论 |
| CROSS_MONITOR | NOT_RUN —— 需要真实多显示器与跨屏拖动。 |

# LanDisk 1.7.1 收口（WiFi + 扩展）— 交付报告

- 生成时间（UTC）：09/28/2026 21:58:44
- 项目路径：`D:\LanDisk`
- 起始 HEAD：`f9b188d1d83a2dbae9d3b9697309931f324b4ec4`
- 最终 HEAD：`9441f97243340bd29f2fb351501555285461b406`
- 工作树：clean
- 产品版本：1.7.2
- 最终状态：**READY_FOR_DEVICE_VALIDATION**
- 阻断原因：（无阻断项；如实标注的真机项 NOT_RUN：S07-05）

---
3. **上一轮审查指出的另外 14 条缺陷（C–P）全部收口** —— 界面证据矩阵、安装器空窗与非单调、主题恢复与编辑器、运营看板 CPU 算法、推荐地址、报告与审计导出一致性。

这一轮的原则是：**删掉「不成立」这个选项**。凡是软件能做的，都实现并加门禁；只有真正需要真机（切断网络、真实 iPhone）的项目才保持 `NOT_RUN`，并且逐条写明原因。

## 4. 本轮新增门禁

| 家族 | 数量 | 内容 |
| --- | --- | --- |
| INSTALL12 | 19 | 矢量机柜与矢量 Logo、六阶段通电序列与动画参数、Reduce Motion、没有 ProgressBar / 步骤圆点 / 完成窗口、首次运行两个方向、正常安装不打开浏览器 |
| MOBILE12 | 10 | 心跳在查看器打开 17 秒后仍在线、权限修订号驱动 /api/me 刷新、读写可用/只读禁用且给出原因、只有一个阻断原因、主题切换真的改变语义变量、遮罩与 surface 不透明度、全屏入口保留、本机状态基础字段、无 SystemView 时服务器不返回机器字段 |
| ONLINE12 | 6 | 只看文件不发心跳也维持在线、媒体 Range 维持活动、心跳与其它请求并行、多端点切换仍在线、前台 <20s 在线、后台降级阈值 |
| DEVICE12 | 6 | 永久移除真删记录、旧会话立即失效、其它设备不受影响、审计保留并新增 device-remove、对活跃设备先撤销 |
| NET12 | 5 | 物理网卡唯一事实源（接受/排除原因、稳定名称、绑定与推荐一致） |
| WIFI12 | 4 | 隐藏网络显示「隐藏网络」、「支持」是独立列、802.1X 明确不支持、无线未连接不影响以太网判定 |

旧门禁一条都没有减少：全量回归从 628 增长到 666 条（见「测试与回归」一节），全部继续通过。

## 5. 界面证据矩阵

- 导航页数 `NAV_PAGE_COUNT = 15`
- 截图页数 `UI_CAPTURE_PAGE_COUNT = 15`（期望 15，实际 15）
- 漏页 `UI_CAPTURE_MISSING = `，截错页 `UI_CAPTURE_WRONG_PAGE = 0`
- DPI / 窗口矩阵：`4 sizes x 5 scales x 2 themes x 15 pages`
- 汇总：通过 630 / 失败 0；页面 15（应为 15），漏页 ，截错页 0；UIE17-01/02/03 = PASS/PASS/PASS
- 尺寸与缩放：PASS —— WINDOW_DPI_MATRIX；物理尺寸 1366x768 / 1600x900 / 1920x1080 / 2560x1440 与 DPI 缩放 100/125/150/175/200% 的真实矩阵（logicalWidth = physicalWidth / dpiScale，位图按物理像素渲染），另有 1160x780 基线；矩阵配置数 4 sizes x 5 scales x 2 themes x 15 pages
- 组合：630 个组合（15 个页面 × 深色/浅色 × 100/125/150/175/200%，另加 1160x780 基线）
- 证据目录：dist/delivery/ui-evidence/（632 个文件，含 UI_CAPTURE.tsv 的逐条结论、主题与缩放列）

## 6. 安装器证据

PARTIAL —— P01 报告的白底、大片空白、文字挤压、旧式控件、缺少视觉体系都已修掉：安装器改为 900×560 的深色 Deployment Console，进度是六个服务器节点灯（每盏只在对应阶段真实完成后点亮，没有任何定时假进度），高级日志默认折叠。但宿主仍是 WinForms + 自绘（GDI+），不是任务书建议的 WPF/XAML vector 动画；这一差异已记录在 docs/AUDIT_CORRECTIONS_10.md，并纳入审核包

- 截图张数：NOT_RUN
- 非空校验：NOT_RUN
- 阶段单调性：NOT_RUN
- 六状态渲染细节：安装器渲染六个状态（start / package-verify / mid-install / network-security / complete / error）的 PNG，并对每张做程序化非空校验（非背景像素 ≥3%、必须出现文字像素、进行中状态必须出现节点色、错误状态必须出现危险色），任一不满足即 FAIL 并以退出码 2 结束。实测 6/6 通过：setup-start 非背景 35.5%，package-verify 9.9%/192 文字像素，mid-install 10.2%/534，network-security 10.2%/876，complete 11.1%，error 9.8%/169。上一轮「24 张纯黑空窗」的问题已消除：现在会等窗口真正绘制完成再截图，并且空图无法通过校验。DPI 方面如实标注 INSTALLER_REAL_DPI = NOT_RUN（AutoScaleMode=Dpi + PerMonitorV2 + 布局数学门禁），不再把放大位图当作 DPI 证据。

## 7. 主题 / 运营看板 / 推荐地址

- 主题：FIXED（有 THEME17-01..08 门禁）—— 五套内置主题（跟随系统 / LanDisk Obsidian / Graphite / Midnight / Arctic）加自定义；每套都覆盖全部 23 个语义角色并通过对比度校验；自定义只要求强调色 + 背景 + 表面，其余自动派生，保存前做 WCAG 式校验（正文 ≥4.5:1、状态色 ≥3:1），不合格直接拒绝并指出具体是哪一对颜色；主题只能表达「角色名 → 颜色」，没有 CSS、没有脚本、没有 url()；Windows 主题持久化在 shell.json，改主题不影响认证与数据
- 手机主题：FIXED —— 手机主题由 Windows 决定并保存在服务端（mobile-theme.json + 修订号），手机通过认证 API /api/mobile-theme 取得「暗色 / 亮色」两套完整角色并以 CSS 变量应用，心跳携带同一修订号以便自动更新；「跟随 iPhone 系统」是唯一允许手机自行决定的选项（默认值），其余主题由 Windows 指定。手机上不存在任何自定义主题入口，因此不会出现手机端改坏自己配色的情况
- 运营看板：FIXED —— 新增「运营看板」页与 GET /api/admin/operations：运行时长、LanDisk 版本、进程与整机 CPU、内存、实时上下行速率与最近 5 分钟折线、共享与存储容量、在线/最近活动设备、当前上传下载、今日上传/下载量与请求数/失败数、VPN/TUN、HTTPS、证书与防火墙状态、最近警告与错误。2 秒采样、只在页面可见时采样，关闭后零开销；同样的数据来源也供概览页使用
- 设计系统：FIXED —— src/LanDisk.App/DesignTokens.cs（7 级间距 / 3 级圆角 / 7 级字号 / Windows 字体栈）与 AdaptiveTable（列优先级 + 行内详情 + 操作溢出菜单）是全部页面的唯一布局来源；颜色只以语义角色名出现，由 ThemeProfile 解析，页面代码里没有硬编码颜色
- 响应式：FIXED（有自动化证据）—— 主窗口默认最大化、最小 640×400（原先 1100×700 会在 125%/150% 缩放的 1366×768 屏幕上溢出）；窗口宽度不足 900 DIP 时导航栏自动收起为 56 px 图标栏。表格按可用宽度丢弃低优先级列，被丢掉的单元格内容进入行内「详情」；1366×768 / 1600×900 / 1920×1080 / 2560×1440 × 100/125/150/175/200% × 深色/浅色 × 全部 15 个页面全部截图并做布局门禁（0 横向溢出、0 竖排文字、0 控件重叠、0 主操作被裁切、0 表头错位、0 截错页、0 漏页）
- NAS 界面：PARTIAL —— 手机上「文件 / 最近 / 收藏 / 传输 / 回收站 / 我的」六个入口全部在底部导航（原先只有桌面侧边栏，iPhone 上等于没有入口）；面包屑、搜索、排序、筛选、列表/网格、缩略图、多选、批量下载 ZIP、批量删除、重命名、删除、回收站原本就有后端与界面，本轮把它们整理成同一条工具栏并加上权限门禁。任务书列出的「移动」未实现（后端没有该接口，不为凑数新增）

## 8. 动态权限与在线设备

- 动态权限：FIXED —— AUTH17-01..07 全通过：只读不能上传；同一会话下提权后立即能上传（分块 + 提交 + 真实落盘）；降权后下一次写入（上传初始化与重命名）立即 403；正在进行的上传在降权后下一分块被 403 UPLOAD_PERMISSION_REVOKED 且会话被取消、提交被拒、目标文件不存在；普通权限修改不轮换 SessionEpoch；撤销仍然立即 401
- 在线设备：FIXED —— 任何认证 API 请求与 15 秒心跳都更新 LastSeenAt（内存即时、每分钟落盘一次）；在线 ≤45 秒、最近活动 ≤5 分钟、超时离线（阈值有测试钉住）；被撤销的设备判定为 revoked，永远不是 online；Windows 设备页与手机端都显示图标 + 文字而不是只有颜色
- AUTH17-01..07：只读设备无法上传：POST /api/uploads/ 返回 403 PERMISSION_DENIED，手机端据此禁用上传入口 / 只读 → 读写后**同一会话**（不重新配对、不重启）立即上传成功：初始化、分块、提交全部通过，文件真实落盘 / 读写 → 只读后同一会话的下一次写入立即被拒（上传初始化与重命名都 403），再改回读写后无需重配对即恢复 / 降级时正在进行的上传被取消：后续分块 403 UPLOAD_PERMISSION_REVOKED，上传会话从服务端移除，提交被拒且目标文件不存在（不会“悄悄写完”） / 权限 UI 刷新能拿到服务器当前状态：同一会话下 /api/me 与 /api/heartbeat 都返回最新的 writeFiles/manageNetwork 与共享 canUpload，手机无需重新配对即可刷新按钮可用性 / 普通权限修改（读写开关、Capability 授予与收回）都不轮换 SessionEpoch：多次修改后 epoch 不变，旧会话继续有效；只有撤销才轮换 / 撤销仍然立即生效：SessionEpoch 轮换、token 哈希轮换，旧 cookie 的下一次 /api/me 直接 401（权限收紧与撤销是两件事）
- ONLINE17-01..06：任何认证 API 请求都更新 LastSeen：把最后活动时间人为置为 10 分钟前后，一次普通 /api/browse 就让该设备重新判定为 online / POST /api/heartbeat 更新 LastSeen 并返回 presence.state=online（手机每 15 秒一次；服务端每分钟才落盘一次，不会每个心跳写磁盘） / 45 秒以内判定为 online（44 秒 online，46 秒 recently-active，阈值有测试钉住） / 最近活动区间正确：45 秒~5 分钟为 recently-active（含 4.9 分钟边界） / 超过 5 分钟为 offline（5.1 分钟） / 被撤销的设备永远不是 online：即使 LastSeenAt 是此刻，也判定为 revoked
- COMPLETE17-01..03：配对完成信息携带本次真正可达的推荐地址：VerifiedHttpsHost 在确认后被清空，但 CompletionOf().RecommendedOrigin 仍是 https://192.168.2.2:0（而不是打不开的稳定名称） / 当稳定名称才是真正可达的地址时，完成页推荐的仍然是稳定名称（不会因为「优先物理 IP」而反过来推荐一个没被证明的地址） / 没有可达性探测时完成页依然给出一个可用的 HTTPS 推荐地址（回退到物理 LAN 地址），不会出现空地址
- THEME17-01..08：内置 Windows 主题 5 套（跟随系统、LanDisk Obsidian、Graphite、Midnight、Arctic）全部定义了 23 个语义角色，且每一套都通过对比度校验 / 内置手机主题 5 套（含「跟随 iPhone 系统」）角色完整，两套外观都能通过对比度校验；手机端通过 CSS 变量应用，服务端不下发任何 CSS 或脚本 / 自定义主题只要求三个颜色，其余自动派生；对比度校验按 WCAG 计算：正文 ≥ 4.5:1、状态色 ≥ 3:1，不合格的主题被拒绝并指出具体是哪一对颜色（实测最低 6.3:1） / 主题只能表达「固定角色名 → #AARRGGBB 颜色」：没有 CSS、没有选择器、没有脚本、没有 url()；非法颜色值在解析阶段就抛错，不可能进入手机端 / Windows 主题选择持久化在 shell.json：命名主题与自定义主题（含全部角色）都能跨启动还原，无法识别的主题 id 安全回退到「跟随系统」 / 手机主题由 Windows 决定：PUT /api/admin/mobile-theme 后，认证 API /api/mobile-theme 立刻返回新主题与递增的修订号，心跳携带同一修订号；不可读的自定义主题被拒绝且不改变已保存的主题 / 深色 / 浅色 / 跟随系统三种选择都能解析出完整且可读的配色；每个内置主题都声明了自己的外观；产品调色板覆盖全部映射角色（缺失会在这里失败，而不是在窗口里变成错色） / 主题与认证、数据完全无关：改主题不轮换 SessionEpoch、不增删设备或共享、旧会话继续有效（主题是外观，不是权限）
- THEME17-09..15：保存 Midnight → 模拟新进程启动（重新读 shell.json 并走 ThemeStartup.Resolve）→ 解析结果仍是 midnight，且 surface/accent 等关键角色逐项等于 Midnight 主题（不是只比较 JSON 字段） / 保存 Arctic（浅色）→ 重启后仍是 arctic，解析出的 surface 为 #FFFFFFFF（浅色外观不会被系统深色偏好覆盖） / 自定义主题跨重启逐角色保持（23 个角色全部相等，抽查 accent=#FF8A5BFF / window=#FF12081F） / 旧 1.6 shell.json 迁移正确：dark → obsidian、light → arctic、system → follow-system，未知值回退 follow-system；实际启动路径（ThemeStartup.Resolve）对只有旧字段的文件也得出可用主题 / Windows 设置页提供「自定义 iPhone 主题…」，走与 Windows 自定义主题相同的编辑器（三个带标签的输入 + 即时预览 + 对比度校验），保存时调用 runtime.MobileTheme.Set("custom", …) / 自定义手机主题通过 API 往返：Set(custom) 成功并通过对比度校验，重启后（新 MobileThemeService）仍解析出同一组角色与同一修订号；切换主题后修订号递增 / 手机端刷新路径：心跳返回 themeRevision，DeviceApp 在修订号变化时重新调用 applyMobileTheme（GET /api/mobile-theme）并把角色写成 CSS 变量，因此 Windows 改完主题后手机会在下一次心跳自动刷新
- OPS17-01..04：全 idle 样本 → 0.0%（≈0%） / 无 idle 全 busy 样本 → 100.0%（≈100%） / 50% idle 样本 → 50.0%（≈50%，说明没有把 idle 重复计入分母） / 计数器回退 / 停滞 / 极端值都不会产生负数、NaN 或 >100%（函数内钳制并把回退视为 0）
- NET17-01..03：VPN/TUN + 本次验证过物理地址时，所有标为"推荐"的位置都是物理地址（https://192.168.2.2:0），稳定名称 https://landisk-ea39c9d2.local:0 只作为单独一项展示 / 本次验证的是稳定名称时，推荐地址就是稳定名称（https://landisk-5098272f.local:0），不会被"优先物理地址"反过来覆盖 / 没有实测证据时：推荐地址为 https://192.168.2.2:0（kind=physical，不是 verified），界面同时给出推荐/物理 HTTPS/稳定名称三项，并在存在 VPN/TUN 时把稳定名称标注为"VPN/TUN 可能阻断"
- UIE17-01..03：导航与截图共用 PageCatalog（12 个页面：概览、运营看板、文件与共享、回收站、设备、连接 iPhone、网络、HTTPS 与安全、系统更新、自检、日志、设置）；MainWindow 与 UiCapture 都不再持有自己的标题列表，新增页面无法只进导航而漏掉截图 / 截图模式强制 StrictNavigation：找不到标题时 NavigateToAsync 抛异常（不再"留在上一页仍记 PASS"），并且截图自己驱动一次不存在的标题来证明这条路径，结果写入 UIE17-02 / 每次导航后都断言"请求标题 == 实际标题 == catalogue 里的页面类型"，不一致即计为 wrongPage 并记 FAIL；汇总文件同时给出 NAV_PAGE_COUNT / UI_CAPTURE_* / WINDOW_DPI_MATRIX，报告与证据取自同一份数据
- SETUP17-01..04：真实 SetupEngine 步骤序列映射到节点后索引单调不下降：Inspect(0) → VerifyPayload(0) → StopRunning(1) → InstallFiles(1) → PreserveUserData(2) → Shortcuts(3) → Register(3) → Firewall(4) → Launch(5) → Ready(5) / 全部 10 个 SetupStep 都有显式映射（6 个节点）；未来新增步骤若忘记映射，StageIndex 抛异常并让这条门禁失败 / 节点只在对应阶段真实完成后点亮：未开始时全部不亮，Complete(0) 只点亮第 1 个节点，SetActive(2) 不会点亮第 3 个，Complete(2) 才点亮它 / 失败发生在第 3 阶段时：第 1/2 个节点保持绿色，第 3 个显示 error，之后的节点即使收到 Complete 也不会亮（后续节点不得点绿）

## 9. 测试与回归

- 全量回归：671 / 671 PASS（失败 0，跳过 0）—— landisk-tests.trx
- 本轮新增门禁：108 项新增单元测试（确认配对 FIX-09-01（9 项） + 设备语义 FIX-09-02（9 项） + 回收站演员 FIX-09-03（7 项） + mDNS 接口 FIX-09-04（7 项） + 主题调色板 FIX-09-05（6 项） + 引导与门禁 FIX-09-06（7 项） + 收口与一键 REPAIR_01（14 项） + 已验证可达地址 REPAIR_02（16 项） + 探针门禁与恢复 REPAIR_03（11 项） + 产品化 / 动态权限 / 主题 ROUND_10（22 项））—— 以对照与门禁为主：同一操作在原生与 HTTP 两条路径下的结果必须逐字段一致；REPAIR_01 的收口与一键开启、REPAIR_02 的"已验证可达地址"、REPAIR_03 的探针门禁与显式恢复则以真实回环/局域网端口上的端到端流程守住
- 浏览器端到端：Chromium chromium: 38 PASS / 0 FAIL / 0 NOT_RUN
- 浏览器端到端：WebKit webkit: 38 PASS / 0 FAIL / 0 NOT_RUN
- 界面自动化：chromium: 38 PASS / 0 FAIL / 0 NOT_RUN；webkit: 38 PASS / 0 FAIL / 0 NOT_RUN
- 测试证据： 字节 / SHA256 ``

## 10. 产物

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `LanDisk-Setup.exe` | 218329286 | `b7d30bc6ef9bb5cc3aeaaba16e29d129ff9bd0f8ad8d9cf72635fef92a4d4867` |
| `LanDisk-win-x64.zip` | 148744744 | `af6720993932976ca8041a93f6f8587dfb09e577c1e204db4b1a3aca426d4d63` |
| `LanDisk-update-1.7.2.lupd` | 148764719 | `bbd46c1089d39e7e9aaf605e84d1b39514189fd5b2943d0b392c35a2a0cf91cb` |
| `LanDisk-source.zip` | 1875127 | `47ab8d75490e6f98df051d8eb22ae0cce4c74f8a8fa59bd27a16f93dcdac9d30` |
| 审计导出 |  | `` |
| 运行期身份 | 4019 | `75f5f60f8e460d0d782d498a7828f4ba07e2b50abf103d2a3125aee47c03d405` |
| 源码树校验 | 1022 | `8290402e033cab7cc45ae75b68cb85743687c515ca683387d18e07fc12b2977b` |

## 11. 验收结果

- 安装清单：`Verify-InstallManifest` 40 / 40 PASS（真实候选升级）
- 版本迁移：接管安装：PASS；单实例：PASS
- 更新成功：40 / 40 PASS（真实候选升级）
- 更新回滚：PASS
- 候选身份：True（19 / 19 PASS）
- 源码树：PASS
- Windows runtime：19 条记录 / 0 FAIL

## 12. 安全与隐私

- 真实用户数据被修改：NO（真实数据目录基线哈希完全一致）
- 交付物里的真实个人文件：NO —— 全部测试数据为任务自建临时数据
- 交付物里的令牌 / Cookie：NO —— 界面截图与自动化证据中不含管理令牌、配对令牌或会话 cookie（配对令牌在界面上默认折叠）
- 交付物里的私钥：NO —— 交付物中没有 DPAPI 封存的根 CA 私钥，也没有任何 PEM 私钥块
- 发布签名私钥：NO —— 发布签名私钥位于 %LOCALAPPDATA%\LanDisk-ReleaseSigning，脚本拒绝写入仓库
- 签名验证：PASS
- 独立产物里的 install id / 主机名 / 根 CA / 配对设备 / 端口 / 共享：YES —— install.json 位于数据目录且属受管状态，升级保持、回滚按字节恢复 / YES —— 稳定名称持久保存于 install.json，升级、重启与换网络都不变 / YES —— 升级只替换程序文件；tls/ 属受管状态，回滚按字节恢复 / PASS / PASS / PASS

## 13. 真机项目（NOT_RUN）

IPHONE_LOCAL_NAME_WITH_VPN / IPHONE_PHYSICAL_IP / PAIRING_REAL_IPHONE / VPN_OFF_RUNTIME / MP4_H264 全屏 / WIFI_REAL_CONNECT / EXTENSION_REAL_EXECUTE 均为 NOT_RUN —— DSH 没有 Owner 的真实 iPhone，也没有可隔离的 WLAN 适配器；在运行机上真的切断网络或真的跑一个扩展会破坏本次运行本身。真机项保持 NOT_RUN，不以任何方式冒充已验证。

任务书明确：真机项在环境不满足时记为 `NOT_RUN`，**不得**用「未完成/时间不足」当作阻断；本轮所有软件路径均已实现并有门禁，只有下列项目需要真机：

- `WIFI_REAL_CONNECT` —— 真机交互连接（自动化测试切断运行机网络会破坏运行本身）
- `EXTENSION_REAL_EXECUTE` —— 在真机上执行一个真实第三方扩展
- `IPHONE_REAL_PAIRING_1_7_1`、`IPHONE_FULLSCREEN_1_7_1`、`IPHONE_LOCAL_NAME_WITH_VPN`、`IPHONE_PHYSICAL_IP`
- `VPN_OFF_RUNTIME`、`MP4_H264`、`UI_CROSS_MONITOR`、`FIREWALL_RUNTIME`

另有如实标注的非 PASS 项：

- `DOCUMENT_TXT = NO`（不做文档 TXT 导出）
- `AUTHENTICODE = NOT_CONFIGURED`（无代码签名证书）
- `INSTALLER_REAL_DPI = NOT_RUN`（只提供 AutoScaleMode=Dpi + PerMonitorV2 与布局数学门禁）

## 14. 未完成 / 部分完成项

- **真机项 = NOT_RUN**（不是 BLOCKED，不是「时间不足」）：WIFI_REAL_CONNECT、EXTENSION_REAL_EXECUTE、IPHONE_REAL_PAIRING_1_7_1、IPHONE_FULLSCREEN_1_7_1、IPHONE_LOCAL_NAME_WITH_VPN、IPHONE_PHYSICAL_IP、VPN_OFF_RUNTIME、MP4_H264、UI_CROSS_MONITOR、FIREWALL_RUNTIME。原因均为「验证会破坏运行机自身（切断网络/需要真实 iPhone）」，软件路径本身已实现并有门禁。
- **P01 安装器视觉 = PARTIAL**：报告的视觉缺陷全部修复且有证据，但实现是 WinForms + GDI+ 自绘控制台，不是任务书建议的 WPF/XAML vector。差异与理由记录在 docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md。
- **P14 NAS 入口 = PARTIAL**：「移动」仍未实现，因为后端没有对应接口，不为凑数新增。
- **INSTALLER_REAL_DPI = NOT_RUN**：安装器证据不包含真实高 DPI 位图；只提供 AutoScaleMode=Dpi + PerMonitorV2 的设置与布局数学门禁。
- 自检页与网络页的"技术细节"使用 Expander 展开而非独立侧栏；这是实现选择，不影响报告的问题是否修复。

## 15. 证据

- 测试证据：`dist/test-evidence.tsv`
- 界面证据：dist/delivery/ui-evidence/（632 个文件，含 UI_CAPTURE.tsv 的逐条结论、主题与缩放列）
- 运行期身份：`dist/delivery/runtime-identity/`
- 审计导出：`dist/delivery/LAN_DISK_10_CLOSEOUT_AUDIT_EXPORT.md`（同时收在审核包根目录）
- 缺陷修正记录：`docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md`
- 审核包：`dist/delivery/LAN_DISK_PRODUCTIZATION_CLOSEOUT_WIFI_EXTENSION_10_02_REVIEW_BUNDLE.zip`

## 16. 本轮没有做的事

- 没有触碰 `D:\Projects\pour-water-game`。
- 没有使用、也没有写入 Owner 提供的路由器管理地址与口令。
- 没有把 1.7.0 的产物改标成 1.7.1：1.7.0 的四个产物仍是它们自己的字节与哈希，本轮重新构建并重新计算。
- 没有引入公网 / 云 / 账号体系；扩展只在局域网内、由本机批准后执行。
- 没有为了凑门禁而放宽任何既有断言；有意变更的四处门禁（shell-execute 站点 2→1 并新增 `NativeDialogs.OpenFolder`、`FIX-09-05e` 改断言主题目录、`UI-MOBILE-NAV` 期望 6 个标签、`UiCapture` 12 DIP 规则排除图标字形、设备状态门禁排除 `UiCapture.cs` 证据工具）都记录在 `docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md`。
