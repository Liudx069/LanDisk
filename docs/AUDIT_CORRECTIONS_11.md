# LanDisk 1.7.2 审计更正说明（LAN_DISK_11_REAL_DEVICE_UX_INSTALLER_VECTOR_CLOSEOUT_01）

本文件记录本轮**有意的行为变更**、**门禁的修改**以及**为什么这些修改不是放宽**。凡是本产品中
"看起来像缺陷"但其实是设计决定的地方，都在这里写明依据；凡是本轮放宽的断言，都在这里写明它
仍然能抓到什么。

起始 HEAD：`f9b188d1d83a2dbae9d3b9697309931f324b4ec4`（1.7.1，工作树干净）。
本轮版本：**1.7.2**。1.7.1 已作为正式候选交付过，因此本轮所有内容变更都递增版本号，没有原地改写。

---

## 1. 行为变更（不是修复，是设计决定改变了）

### 1.1 安装器不再有完成窗口

1.7.1 结束时弹出一个独立的"LanDisk 已安装"窗口，上面有三个按钮（连接 iPhone / 打开 LanDisk / 完成）。
本轮删除了这个窗口：机柜全部亮起 → 显示"LanDisk 已就绪" → 窗口自行关闭。首次连接教程移进
`LanDisk.exe`（`FirstRunPolicy`）。

**依据**：Owner 明确要求"删除独立完成弹窗""fresh install 才进首次连接教程且教程在 LanDisk.exe 内"。
**影响**：安装器不再自己决定用户下一步做什么；教程的判定条件（0 台设备且 0 个已启用共享）由产品
自己持有，升级用户不会再看到它。
**门禁**：`INSTALL12-16`（没有独立完成窗口、没有从安装器启动教程）、`INSTALL12-17/18`（首次运行两个方向）。

### 1.2 自检的"防火墙"从提示变成测量

1.7.1 无论机器上有没有规则，都打印同一句"需要放行 TCP 8892 与 UDP 5353（专用网络、本地子网）"。
现在自检真的查询 Windows 并逐条给出结论，缺规则时提供原生「修复防火墙规则」按钮。

**依据**：Owner 的真机自检显示"防火墙 = 错误"却无法操作；一个不可能失败的检查不是检查。
**为什么不是扩大权限**：读取规则（`advfirewall firewall show rule`）不需要管理员权限，所以自检本身
不弹 UAC。只有用户按下修复按钮，才会有一次显式的 `runas`，参数固定为
`--elevated-firewall-add --install-dir <安装目录>`。
**规则本身没有变**：TCP 8892（HTTPS）、TCP 8890（首次连接向导）、UDP 5353（mDNS），全部 inbound、
Private profile、LocalSubnet、绑定稳定安装路径的 `LanDisk.exe`；8891 永不建立入站规则。
`FirewallRules.NeverInbound` 把"8891 永不入站"写成一个可断言的事实，而不是一句注释。

### 1.3 手机端只呈现一个阻断原因

1.7.1 会同时显示"共享根目录当前不可访问"与"这台手机目前是只读权限"。现在按
**共享不可访问 > 共享未开放 > 手机只读 > 具体能力缺失** 的优先级只显示一条。

**依据**：Owner 的截图里两条同时出现，读者无法判断到底卡在哪一条。
**门禁**：`MOBILE12-05`。

### 1.4 手机主题应用语义令牌

主题现在写入 `--window/--surface/--surface-secondary/--surface-elevated/--overlay-scrim/
--text-primary/--text-secondary/--accent/--accent-hover/--success/--warning/--danger`，并保留旧的短名
作为别名。

**依据**：这不是"新增第二套主题引擎"，而是把已有一套的输出改成规格里说的语义名。它同时修掉了一个
真实缺陷：CSS 里写的是 `var(--window)`，而主题只写 `--bg`，于是变量不存在、surface 背景解析为透明，
查看器下面那一层直接透出来——Owner 看到的"下层页面透出"就是这个。
`--surface-elevated` 由 surface 派生并强制去掉 alpha，因此红色 Accent 只会出现在按钮与选中态上。

### 1.5 设备可以永久移除

`DELETE /api/admin/devices/{id}` 从"直接删记录"改为完整流程：已撤销设备直接移除；仍然有效的设备
先完成撤销（轮换 token hash、轮换 session epoch、取消上传与扩展任务）再删除。写审计
`action=device-remove`，注明 `wasActive`、取消数量与 `auditRetained=true`；该设备的历史审计一条都不删。

**依据**：原先那个端点比"撤销"更弱——不轮换凭据、不取消任务、不写审计。"永久移除"必须是更强的操作。
**门禁**：`DEVICE12-01..06`。

---

## 2. 门禁的修改（以及为什么没有变松）

### 2.1 `The_only_netsh_use_in_the_product_is_the_firewall_rule_helper`

**改了什么**：允许 `netsh` 出现在 `src/LanDisk.App/FirewallStatus.cs`（新增的只读探针），并新增断言
该文件只包含 `advfirewall firewall show rule`，不得出现 `add/delete/set/reset/interface/winhttp/wlan`。
同时改为忽略注释行。

**为什么不是放宽**：这条门禁的**目的**是"LanDisk 永不触碰路由 / DNS / VPN 配置"。新增的调用点是
防火墙规则的**读**操作，仍然在"防火墙"这个目的之内，而且被进一步约束为只读。忽略注释是因为门禁
数的是代码——`FirewallRules.cs` 的文档注释里出现了 `netsh` 这个词，那是解释而不是调用。

**它仍然能抓到什么**：任何在 server、shell 或其它地方新增的 `netsh` 调用；任何新增的写动词；
任何把 `netsh interface` / `winhttp` 引进来的人。

### 2.2 `No_route_dns_or_vpn_configuration_write_was_added`

同上：`netshSites` 允许 `LanDisk.Setup/` 与 `LanDisk.App/FirewallStatus.cs`，`netshProcessStarts`
期望两个文件而不是一个，禁用动词列表一条未减，注释行同样忽略。

### 2.3 `The_shell_opens_a_browser_from_exactly_one_guarded_place`

**改了什么**：`NativeDialogs` 内的受控 `UseShellExecute` 调用点从 1 个变为 2 个（`OpenFolder` 与
新增的 `RunElevated`），全shell 从 2 个变为 3 个（含恢复页 URL）。

**为什么不是放宽**：提权无法在不使用 shell-execute 的情况下表达；把它放在调用方会让"只有一个地方
把字符串交给 shell"这条不变量失去意义。因此新增的调用点被放进**同一个受控助手**，并被断言：
必须带 `Verb = "runas"`、必须要求文件真实存在、必须拒绝 `://`。`OpenFolder` 的断言一字未改。

**它仍然能抓到什么**：任何在 `NativeDialogs` 之外新增 shell-execute 的地方；任何试图用 URL 提权的
写法；任何绕过 `OpenFolder` 打开目录的写法。

### 2.4 `单行文本高度异常`（UI 截图）

**改了什么**：只在文本**含有可见字形**时判定。空白、零宽字符与控制/格式类字符不参与。

**为什么不是放宽**：这条断言的意思是"一行文本不应该莫名其妙地有两行高"。KVP 布局会在行内放一个
不可见占位符来撑住列宽；当相邻的值换行时，占位符跟着变高——那是**对齐**，不是布局失败。断言现在
仍然对任何真正可读的短文本生效。

**它仍然能抓到什么**：真正的短文本被挤成两行（那正是它最初要抓的缺陷）。

### 2.5 `EXT17-12`（取消任务后进程树必须清空）

**改了什么**：取消后的固定 1.5 秒睡眠改为最多 15 秒的轮询。

**为什么不是放宽**：断言依然精确地是"一个都不剩"。进程树是异步终止的，在 Build-All 同时编译并跑
全量测试的负载下，固定睡眠会偶发失败——一个因为调度原因失败的发布门禁，实际效果是教人"重跑一次
就好"。轮询保留了断言强度，同时让结果确定。

---

## 3. 新增的门禁

| 编号 | 内容 |
| --- | --- |
| INSTALL12-01 | 安装器源码不引用任何位图或视频素材（`.png/.jpg/.gif/.webp/.bmp/.mp4/.webm/.avi`，也不加载 `BitmapImage`） |
| INSTALL12-02 | 机柜完全由矢量图元绘制（Rectangle/Ellipse/Path/GeometryDrawing；散热孔是平铺 GeometryDrawing 画笔） |
| INSTALL12-03 | Logo 是真正的透明矢量：SVG 内 13 条 Bézier path、无 `<image>`、无 base64、无外部引用；安装器解析同一批轮廓 |
| INSTALL12-04..09 | 每一步点亮哪几个纵向灯组（Step1 全暗 → Step6 四组全亮 + 四个整机状态灯） |
| INSTALL12-10 | 六个显示阶段由十个引擎步骤驱动，显示阶段索引单调不减并覆盖 0–5 |
| INSTALL12-11/12 | LED 用关键帧动画（0→0.25→0.70→1）、组内 35–90ms stagger、整组 350–750ms |
| INSTALL12-13 | Reduce Motion 退化为整组约 160ms 淡入，仍是动画 |
| INSTALL12-14/15 | 没有任何 ProgressBar；顶部步骤圆点/连线/1–6 数字/大 Card 全部移除 |
| INSTALL12-16 | 没有独立完成窗口；"LanDisk 已就绪"后窗口自行关闭 |
| INSTALL12-17/18 | 首次运行判定（全新安装 vs 已有设备/共享的升级） |
| INSTALL12-19 | 任何正常安装路径都不自动打开浏览器 |
| WIFI12-01..04 | 隐藏网络显示"隐藏网络"；"支持"是独立列；802.1X 明确不支持；无线未连接不影响以太网判定 |
| NET12-01..05 | 物理网卡唯一事实源（接受/排除原因、稳定名称、绑定与推荐一致） |
| DEVICE12-01..06 | 永久移除（真删记录、旧会话立即失效、其它设备不受影响、审计保留并新增 device-remove） |
| ONLINE12-01..06 | 查看器只请求文件也维持在线、媒体 Range 维持活动、多端点切换仍在线、前台 <20s 在线、后台降级阈值 |
| MOBILE12-01..10 | 心跳在查看器打开 17 秒后仍在线；权限修订号驱动 /api/me 刷新；读写可用/只读禁用；只有一个阻断原因；主题切换真的改变语义变量；遮罩 ≥0.8、surface ≥0.98、z-index ≥150；全屏入口保留；本机状态基础字段；无 SystemView 时服务器不返回机器字段 |

---

## 4. 本轮删除的代码（死代码清理，不是功能回退）

| 文件 | 原因 |
| --- | --- |
| `src/LanDisk.Setup/SetupForm.cs` | WinForms 安装界面，1.7.2 起交互路径已是 WPF 窗口，且没有任何引用 |
| `src/LanDisk.Setup/SetupConsole.cs` | 仅供上面那个窗体使用的控制台绘制助手，同样没有任何引用 |
| `Program.cs` 中的 `VerifyBitmap` | WinForms 位图校验器，已被 WPF 证据渲染（真实窗口 + RenderTargetBitmap）取代 |

删除它们让 `INSTALL12-14/15`（没有 ProgressBar、没有步骤圆点）成为源码级事实，而不是"当前没渲染"。

---

## 5. 已知的、本轮**没有**解决的问题（不隐瞒）

| 项目 | 状态 | 原因 |
| --- | --- | --- |
| `IPHONE_REAL_DEVICE_1_7_2` | NOT_RUN | 需要真实的 iPhone 与真实局域网；DSH 不得冒充真机 |
| `WIFI_REAL_SWITCH` | NOT_RUN | 需要真实地断开/切换无线网络；本机无线网卡未连接，且不允许操作用户网络 |
| `CROSS_MONITOR` | NOT_RUN | 需要真实的多显示器与跨屏拖动 |
| 手机端多选操作在真机上的手感 | 未验证 | 自动化能证明控件可用与权限正确，不能证明真机手势体验 |

以上四项由 Owner 真机复测，本轮最高状态为 **READY_FOR_DEVICE_VALIDATION**。
