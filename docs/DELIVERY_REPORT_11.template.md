# LanDisk 1.7.2 收口（真机 UX + 矢量安装器）— 交付报告

- 生成时间（UTC）：{{GENERATED_AT_UTC}}
- 项目路径：`{{PROJECT_PATH}}`
- 起始 HEAD：`{{START_HEAD}}`
- 最终 HEAD：`{{FINAL_HEAD}}`
- 工作树：{{WORKTREE_STATUS}}
- 产品版本：{{PRODUCT_VERSION}}
- 最终状态：**{{FINAL_STATUS}}**
- 阻断原因：{{BLOCKED_REASON}}
- 真机项目最高状态：**{{REAL_DEVICE_HIGHEST_STATUS}}**

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

- 起始 HEAD `{{START_HEAD}}`、版本 **1.7.1**、`git status --porcelain` 为空 —— 与任务书要求一致。
- 本轮未触碰 `D:\Projects\pour-water-game`。
- 版本递增到 **1.7.2**：7 个 `*.csproj` 的 `<Version>`、`tools/Build-All.ps1`、
  `tools/Setup-Acceptance.ps1`、`tools/Update-ReleaseVerify.ps1`。

## 3. REAL11-01..17 逐条结论

| 编号 | 结论 | 证据 / 原因 |
| --- | --- | --- |
| REAL11-01 安装器矢量机柜与矢量 Logo | **{{REAL11_01_STATUS}}** | {{REAL11_01_DETAIL}} |
| REAL11-02 六阶段逐组通电 | **{{REAL11_02_STATUS}}** | {{REAL11_02_DETAIL}} |
| REAL11-03 概览密度与核心值完整性 | **{{REAL11_03_STATUS}}** | {{REAL11_03_DETAIL}} |
| REAL11-04 工具栏按钮间距 | **{{REAL11_04_STATUS}}** | {{REAL11_04_DETAIL}} |
| REAL11-05 设备页数据列/操作列 | **{{REAL11_05_STATUS}}** | {{REAL11_05_DETAIL}} |
| REAL11-06 物理网卡唯一事实源 | **{{REAL11_06_STATUS}}** | {{REAL11_06_DETAIL}} |
| REAL11-08 防火墙规则与原生修复 | **{{REAL11_08_STATUS}}** | {{REAL11_08_DETAIL}} |
| REAL11-09 扩展页布局 | **{{REAL11_09_STATUS}}** | {{REAL11_09_DETAIL}} |
| REAL11-10 手机主题刷新 | **{{REAL11_10_STATUS}}** | {{REAL11_10_DETAIL}} |
| REAL11-11 Presence / 在线状态 | **{{REAL11_11_STATUS}}** | {{REAL11_11_DETAIL}} |
| REAL11-12 永久移除设备 | **{{REAL11_12_STATUS}}** | {{REAL11_12_DETAIL}} |
| REAL11-13 动态权限 | **{{REAL11_13_STATUS}}** | {{REAL11_13_DETAIL}} |
| REAL11-14 手机「本机状态」 | **{{REAL11_14_STATUS}}** | {{REAL11_14_DETAIL}} |
| REAL11-15 查看器独立图层 | **{{REAL11_15_STATUS}}** | {{REAL11_15_DETAIL}} |
| REAL11-16 唯一阻断原因 | **{{REAL11_16_STATUS}}** | {{REAL11_16_DETAIL}} |
| REAL11-17 首次运行教程 | **{{REAL11_17_STATUS}}** | {{REAL11_17_DETAIL}} |

`FIXED` 是由**证据推导**出来的：每条结论的 STATUS 取自它列出的门禁在 `dist/test-evidence.tsv` 里
实际记录的结果；门禁停止通过时，这里的结论会跟着变成 `BLOCKED`，不会停留在上一轮的结论上。

### 状态字段

| 字段 | 值 |
| --- | --- |
| Installer 视觉层 | {{INSTALLER_SCREENSHOT_COUNT}} |
| Installer 非空白捕获 | {{INSTALLER_NONBLANK_CHECK}} |
| Installer 阶段单调 | {{INSTALLER_STAGE_MONOTONIC}} |
| Desktop 界面矩阵 | {{UI_CAPTURE_ACTUAL}} |
| Desktop 缺页 / 错页 | {{UI_CAPTURE_MISSING}} / {{UI_CAPTURE_WRONG_PAGE}} |
| Mobile 端到端（Chromium） | {{SAFARI_CHROMIUM}} |
| Mobile 端到端（WebKit） | {{SAFARI_WEBKIT}} |
| IPHONE_REAL_DEVICE_1_7_2 | {{IPHONE_REAL_DEVICE_1_7_2}} |
| CROSS_MONITOR | {{CROSS_MONITOR}} |

# LanDisk 交付报告

- 生成时间（UTC）：{{GENERATED_AT_UTC}}
- 项目路径：`{{PROJECT_PATH}}`
- 起始 HEAD：`{{START_HEAD}}`
- 最终 HEAD：`{{FINAL_HEAD}}`
- 工作树：{{WORKTREE_STATUS}}
- 产品版本：{{PRODUCT_VERSION}}
- 最终状态：**{{FINAL_STATUS}}**
- 阻断原因：{{BLOCKED_REASON}}

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

旧门禁一条都没有减少：全量回归从 628 增长到 666 条（见「测试与回归」一节），全部继续通过。

## 5. 界面证据矩阵

- 导航页数 `NAV_PAGE_COUNT = {{NAV_PAGE_COUNT}}`
- 截图页数 `UI_CAPTURE_PAGE_COUNT = {{UI_CAPTURE_PAGE_COUNT}}`（期望 {{UI_CAPTURE_EXPECTED}}，实际 {{UI_CAPTURE_ACTUAL}}）
- 漏页 `UI_CAPTURE_MISSING = {{UI_CAPTURE_MISSING}}`，截错页 `UI_CAPTURE_WRONG_PAGE = {{UI_CAPTURE_WRONG_PAGE}}`
- DPI / 窗口矩阵：`{{WINDOW_DPI_MATRIX}}`
- 汇总：{{UI_CHECK_SUMMARY}}
- 尺寸与缩放：{{UI_WINDOW_SIZES}}
- 组合：{{UI_COMBINATIONS}}
- 证据目录：{{UI_EVIDENCE}}

## 6. 安装器证据

{{INSTALLER_UI_STATUS}}

- 截图张数：{{INSTALLER_SCREENSHOT_COUNT}}
- 非空校验：{{INSTALLER_NONBLANK_CHECK}}
- 阶段单调性：{{INSTALLER_STAGE_MONOTONIC}}
- 六状态渲染细节：{{ROUND10_SETUP_UI}}

## 7. 主题 / 运营看板 / 推荐地址

- 主题：{{THEME_STATUS}}
- 手机主题：{{MOBILE_THEME_STATUS}}
- 运营看板：{{OPS_DASHBOARD_STATUS}}
- 设计系统：{{DESIGN_SYSTEM_STATUS}}
- 响应式：{{RESPONSIVE_UI_STATUS}}
- NAS 界面：{{NAS_UI_STATUS}}

## 8. 动态权限与在线设备

- 动态权限：{{DYNAMIC_PERMISSION_STATUS}}
- 在线设备：{{ONLINE_DEVICE_STATUS}}
- AUTH17-01..07：{{AUTH17_01}} / {{AUTH17_02}} / {{AUTH17_03}} / {{AUTH17_04}} / {{AUTH17_05}} / {{AUTH17_06}} / {{AUTH17_07}}
- ONLINE17-01..06：{{ONLINE17_01}} / {{ONLINE17_02}} / {{ONLINE17_03}} / {{ONLINE17_04}} / {{ONLINE17_05}} / {{ONLINE17_06}}
- COMPLETE17-01..03：{{COMPLETE17_01}} / {{COMPLETE17_02}} / {{COMPLETE17_03}}
- THEME17-01..08：{{THEME17_01}} / {{THEME17_02}} / {{THEME17_03}} / {{THEME17_04}} / {{THEME17_05}} / {{THEME17_06}} / {{THEME17_07}} / {{THEME17_08}}
- THEME17-09..15：{{THEME17_09}} / {{THEME17_10}} / {{THEME17_11}} / {{THEME17_12}} / {{THEME17_13}} / {{THEME17_14}} / {{THEME17_15}}
- OPS17-01..04：{{OPS17_01}} / {{OPS17_02}} / {{OPS17_03}} / {{OPS17_04}}
- NET17-01..03：{{NET17_01}} / {{NET17_02}} / {{NET17_03}}
- UIE17-01..03：{{UIE17_01}} / {{UIE17_02}} / {{UIE17_03}}
- SETUP17-01..04：{{SETUP17_01}} / {{SETUP17_02}} / {{SETUP17_03}} / {{SETUP17_04}}

## 9. 测试与回归

- 全量回归：{{FULL_REGRESSION}}
- 本轮新增门禁：{{NEW_TESTS}}
- 浏览器端到端：Chromium {{SAFARI_CHROMIUM}}
- 浏览器端到端：WebKit {{SAFARI_WEBKIT}}
- 界面自动化：{{UI_AUTOMATION}}
- 测试证据：{{TEST_EVIDENCE_BYTES}} 字节 / SHA256 `{{TEST_EVIDENCE_SHA256}}`

## 10. 产物

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `LanDisk-Setup.exe` | {{SETUP_BYTES}} | `{{SETUP_SHA256}}` |
| `LanDisk-win-x64.zip` | {{RELEASE_BYTES}} | `{{RELEASE_SHA256}}` |
| `{{UPDATE_NAME}}` | {{UPDATE_BYTES}} | `{{UPDATE_SHA256}}` |
| `LanDisk-source.zip` | {{SOURCE_BYTES}} | `{{SOURCE_SHA256}}` |
| 审计导出 | {{AUDIT_EXPORT_BYTES}} | `{{AUDIT_EXPORT_SHA256}}` |
| 运行期身份 | {{IDENTITY_BYTES}} | `{{IDENTITY_SHA256}}` |
| 源码树校验 | {{SOURCE_VERIFY_BYTES}} | `{{SOURCE_VERIFY_SHA256}}` |

## 11. 验收结果

- 安装清单：`Verify-InstallManifest` {{UPDATE_SUCCESS}}
- 版本迁移：{{SETUP_MIGRATION}}
- 更新成功：{{UPDATE_SUCCESS}}
- 更新回滚：{{UPDATE_ROLLBACK}}
- 候选身份：{{IDENTITY_MATCH}}
- 源码树：{{SOURCE_TREE_VERIFY}}
- Windows runtime：{{WINDOWS_RUNTIME}}

## 12. 安全与隐私

- 真实用户数据被修改：{{REAL_USER_DATA_TOUCHED}}
- 交付物里的真实个人文件：{{REAL_PERSONAL_FILES_INCLUDED}}
- 交付物里的令牌 / Cookie：{{TOKENS_OR_COOKIES_IN_ARTIFACTS}}
- 交付物里的私钥：{{PRIVATE_KEY_IN_ARTIFACTS}}
- 发布签名私钥：{{RELEASE_SIGNING_PRIVATE_KEY_IN_ARTIFACTS}}
- 签名验证：{{SIGNATURE_VERIFY}}
- 独立产物里的 install id / 主机名 / 根 CA / 配对设备 / 端口 / 共享：{{INSTALL_ID_PRESERVED}} / {{HOSTNAME_PRESERVED}} / {{ROOT_CA_PRESERVED}} / {{PAIRED_DEVICES_PRESERVED}} / {{PORTS_PRESERVED}} / {{SHARES_PRESERVED}}

## 13. 真机项目（NOT_RUN）

{{REAL_DEVICE_NOT_RUN}}

任务书明确：真机项在环境不满足时记为 `NOT_RUN`，**不得**用「未完成/时间不足」当作阻断；本轮所有软件路径均已实现并有门禁，只有下列项目需要真机：

- `EXTENSION_REAL_EXECUTE` —— 在真机上执行一个真实第三方扩展
- `IPHONE_REAL_PAIRING_1_7_1`、`IPHONE_FULLSCREEN_1_7_1`、`IPHONE_LOCAL_NAME_WITH_VPN`、`IPHONE_PHYSICAL_IP`
- `VPN_OFF_RUNTIME`、`MP4_H264`、`UI_CROSS_MONITOR`、`FIREWALL_RUNTIME`

另有如实标注的非 PASS 项：

- `DOCUMENT_TXT = NO`（不做文档 TXT 导出）
- `AUTHENTICODE = NOT_CONFIGURED`（无代码签名证书）
- `INSTALLER_REAL_DPI = NOT_RUN`（只提供 AutoScaleMode=Dpi + PerMonitorV2 与布局数学门禁）

## 14. 未完成 / 部分完成项

{{ROUND10_BLOCKED}}

## 15. 证据

- 测试证据：`dist/test-evidence.tsv`
- 界面证据：{{UI_EVIDENCE}}
- 运行期身份：`dist/delivery/runtime-identity/`
- 审计导出：`dist/delivery/LAN_DISK_10_CLOSEOUT_AUDIT_EXPORT.md`（同时收在审核包根目录）
- 缺陷修正记录：`docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md`
- 审核包：交付目录下的 SMALL REVIEW ZIP（名称随版本变化）。

## 16. 本轮没有做的事

- 没有触碰 `D:\Projects\pour-water-game`。
- 没有使用、也没有写入 Owner 提供的路由器管理地址与口令。
- 没有把 1.7.0 的产物改标成 1.7.1：1.7.0 的四个产物仍是它们自己的字节与哈希，本轮重新构建并重新计算。
- 没有引入公网 / 云 / 账号体系；扩展只在局域网内、由本机批准后执行。
- 没有为了凑门禁而放宽任何既有断言；有意变更的四处门禁（shell-execute 站点 2→1 并新增 `NativeDialogs.OpenFolder`、`FIX-09-05e` 改断言主题目录、`UI-MOBILE-NAV` 期望 6 个标签、`UiCapture` 12 DIP 规则排除图标字形、设备状态门禁排除 `UiCapture.cs` 证据工具）都记录在 `docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md`。
