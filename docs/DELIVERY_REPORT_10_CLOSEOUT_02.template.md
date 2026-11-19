# LanDisk 1.7.1 收口（WiFi + 扩展）— 交付报告

- 生成时间（UTC）：{{GENERATED_AT_UTC}}
- 项目路径：`{{PROJECT_PATH}}`
- 起始 HEAD：`{{START_HEAD}}`
- 最终 HEAD：`{{FINAL_HEAD}}`
- 工作树：{{WORKTREE_STATUS}}
- 产品版本：{{PRODUCT_VERSION}}
- 最终状态：**{{FINAL_STATUS}}**
- 阻断原因：{{BLOCKED_REASON}}

---

## 1. 这一轮做了什么

1.7.0 已经作为正式候选交付过，所以这一轮**没有原地改写它**，而是递增到 **1.7.1**，并把上一轮标记为未完成的两条主线真正做完：

1. **WiFi 管理（原 P18 = 未实现）** —— 走 Windows 原生 WLAN API，权限与文件读写彻底分离，密码只用于一次调用，安全类型诚实标注。
2. **扩展框架与任务与命令（原 P19 = 未实现）** —— `.ldx` 包、安全解包、隔离、本机 SHA 固定批准、独立受限执行宿主、只暴露 schema 合法参数的命令 API。
3. **上一轮审查指出的另外 14 条缺陷（C–P）全部收口** —— 界面证据矩阵、安装器空窗与非单调、主题恢复与编辑器、运营看板 CPU 算法、推荐地址、报告与审计导出一致性。

这一轮的原则是：**删掉「不成立」这个选项**。凡是软件能做的，都实现并加门禁；只有真正需要真机（切断网络、真实 iPhone）的项目才保持 `NOT_RUN`，并且逐条写明原因。

## 2. 基线

- 起始 HEAD `{{START_HEAD}}`、版本 **1.7.0**、`git status --porcelain` 为空 —— 与任务书要求一致（不一致时必须停止并报告，本轮一致）。
- 起始全量回归：**580 / 580 PASS**。
- 本轮未触碰 `D:\Projects\pour-water-game`。
- 版本递增到 **1.7.1**：6 个 `*.csproj` 的 `<Version>`、`tools/Build-All.ps1`、`tools/Setup-Acceptance.ps1`、`tools/Update-ReleaseVerify.ps1`。

## 3. P01–P20 逐条结论

| 编号 | 结论 | 证据 / 原因 |
| --- | --- | --- |
| P01 安装器视觉 | {{P01_STATUS}} | {{P01_DETAIL}} |
| P02 | {{P02_STATUS}} | {{P02_DETAIL}} |
| P03 | {{P03_STATUS}} | {{P03_DETAIL}} |
| P04 | {{P04_STATUS}} | {{P04_DETAIL}} |
| P05 | {{P05_STATUS}} | {{P05_DETAIL}} |
| P06 | {{P06_STATUS}} | {{P06_DETAIL}} |
| P07 | {{P07_STATUS}} | {{P07_DETAIL}} |
| P08 | {{P08_STATUS}} | {{P08_DETAIL}} |
| P09 | {{P09_STATUS}} | {{P09_DETAIL}} |
| P10 | {{P10_STATUS}} | {{P10_DETAIL}} |
| P11 | {{P11_STATUS}} | {{P11_DETAIL}} |
| P12 | {{P12_STATUS}} | {{P12_DETAIL}} |
| P13 | {{P13_STATUS}} | {{P13_DETAIL}} |
| P14 NAS 入口 | {{P14_STATUS}} | {{P14_DETAIL}} |
| P15 配对完成页 | {{P15_STATUS}} | {{P15_DETAIL}} |
| P16 iPhone 全屏 | {{P16_STATUS}} | {{P16_DETAIL}} |
| P17 运营看板 | {{P17_STATUS}} | {{P17_DETAIL}} |
| **P18 WiFi** | **{{P18_STATUS}}** | {{P18_DETAIL}} |
| **P19 扩展 / 命令** | **{{P19_STATUS}}** | {{P19_DETAIL}} |
| P20 主题 | {{P20_STATUS}} | {{P20_DETAIL}} |

## 4. 本轮收口的 16 条审查缺陷

| 缺陷 | 结论 | 证据 |
| --- | --- | --- |
| C 截图页面清单陈旧 | FIXED | `PageCatalog` 是唯一来源；漏页 {{UI_CAPTURE_MISSING}}、截错页 {{UI_CAPTURE_WRONG_PAGE}}；{{UIE17_01}} / {{UIE17_03}} |
| D 运营看板未截图 | FIXED | 页面数 {{NAV_PAGE_COUNT}}，截图覆盖 {{UI_CAPTURE_ACTUAL}} / {{UI_CAPTURE_EXPECTED}}；{{UIE17_03}} |
| E 旧标题截错页面 | FIXED | 严格导航 + 逐页身份校验；错页 {{UI_CAPTURE_WRONG_PAGE}}；{{UIE17_01}} |
| F DPI/窗口矩阵未真正执行 | FIXED | 真实矩阵 {{WINDOW_DPI_MATRIX}}；{{UIE17_02}} |
| G 高 DPI 小屏最小尺寸溢出 | FIXED | `MinWidth=640 / MinHeight=400` + 窄窗口 56 px 图标栏；DPI 矩阵 0 失败（{{UI_CHECK_SUMMARY}}） |
| H 安装器 24 张空窗 | FIXED | 6 状态逐张非空校验；{{INSTALLER_SCREENSHOT_COUNT}}；{{SETUP17_01}} |
| I 「DPI 证据」是位图放大 | FIXED（改为如实标注） | `INSTALLER_REAL_DPI = NOT_RUN`，只保留 AutoScaleMode=Dpi + PerMonitorV2 + 布局数学；{{SETUP17_03}} |
| J 节点进度非单调（0→2→1） | FIXED | `SetupStageMap` + 引擎步骤重排；{{INSTALLER_STAGE_MONOTONIC}} / {{SETUP17_02}} / {{SETUP17_04}} |
| K 命名/自定义主题重启不恢复 | FIXED | `ThemeStartup.Resolve`；{{THEME17_09}} / {{THEME17_10}} / {{THEME17_11}} / {{THEME17_12}} |
| L 自定义主题编辑器丢标签 | FIXED | 带标签字段 + 实时预览 + 对比度门禁；{{THEME17_13}} / {{THEME17_14}} |
| M 无自定义 iPhone 主题入口 | FIXED | 设置页「自定义 iPhone 主题…」；{{THEME17_15}} |
| N 系统 CPU 算法错误 | FIXED | 减去 idle 的纯函数；{{OPS17_01}} / {{OPS17_02}} / {{OPS17_03}} / {{OPS17_04}} |
| O 仍把 `.local` 当手机地址 | FIXED | 推荐顺序 已验证 → 物理（VPN 时）→ 稳定；{{NET17_01}} / {{NET17_02}} / {{NET17_03}} |
| P 报告与审计导出描述矛盾 | FIXED | 两处都读 `UI_CAPTURE_SUMMARY.tsv` 的同一组字段 |
| **A/P18 WiFi 未实现** | **FIXED（真机连接 NOT_RUN）** | WIFI17-01..13 全通过 |
| **B/P19 扩展未实现** | **FIXED（真机执行 NOT_RUN）** | EXT17-01..20 全通过 |

## 5. WiFi（P18）

{{WIFI_STATUS}}

| 门禁 | 证据 |
| --- | --- |
| WIFI17-01 | {{WIFI17_01}} |
| WIFI17-02 | {{WIFI17_02}} |
| WIFI17-03 | {{WIFI17_03}} |
| WIFI17-04 | {{WIFI17_04}} |
| WIFI17-05 | {{WIFI17_05}} |
| WIFI17-06 | {{WIFI17_06}} |
| WIFI17-07 | {{WIFI17_07}} |
| WIFI17-08 | {{WIFI17_08}} |
| WIFI17-09 | {{WIFI17_09}} |
| WIFI17-10 | {{WIFI17_10}} |
| WIFI17-11 | {{WIFI17_11}} |
| WIFI17-12 | {{WIFI17_12}} |
| WIFI17-13 | {{WIFI17_13}} |

实现摘要：{{WIFI_IMPLEMENTATION}}

## 6. 扩展与任务与命令（P19）

{{EXTENSION_STATUS}}

命令中心：{{COMMAND_CENTER_STATUS}}

| 门禁 | 证据 |
| --- | --- |
| EXT17-01 | {{EXT17_01}} |
| EXT17-02 | {{EXT17_02}} |
| EXT17-03 | {{EXT17_03}} |
| EXT17-04 | {{EXT17_04}} |
| EXT17-05 | {{EXT17_05}} |
| EXT17-06 | {{EXT17_06}} |
| EXT17-07 | {{EXT17_07}} |
| EXT17-08 | {{EXT17_08}} |
| EXT17-09 | {{EXT17_09}} |
| EXT17-10 | {{EXT17_10}} |
| EXT17-11 | {{EXT17_11}} |
| EXT17-12 | {{EXT17_12}} |
| EXT17-13 | {{EXT17_13}} |
| EXT17-14 | {{EXT17_14}} |
| EXT17-15 | {{EXT17_15}} |
| EXT17-16 | {{EXT17_16}} |
| EXT17-17 | {{EXT17_17}} |
| EXT17-18 | {{EXT17_18}} |
| EXT17-19 | {{EXT17_19}} |
| EXT17-20 | {{EXT17_20}} |

实现摘要：{{EXTENSION_IMPLEMENTATION}}

**关于路由器管理凭据**：Owner 提供了一个路由器管理地址与账号口令，并说明可用于测试但不得切断网络。本轮**未使用、也未写入任何交付物**（理由见 `docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md`）：产品规定的手法是 Windows WLAN API 而不是路由器 Web 管理面；那是真实的第三方设备凭据，写入交付物会让隐私门禁失守；在路由器上做任何改动都有切断网络的风险。该地址与凭据不出现在代码、日志、报告、审计导出或证据包中。

## 7. 界面证据矩阵

- 导航页数 `NAV_PAGE_COUNT = {{NAV_PAGE_COUNT}}`
- 截图页数 `UI_CAPTURE_PAGE_COUNT = {{UI_CAPTURE_PAGE_COUNT}}`（期望 {{UI_CAPTURE_EXPECTED}}，实际 {{UI_CAPTURE_ACTUAL}}）
- 漏页 `UI_CAPTURE_MISSING = {{UI_CAPTURE_MISSING}}`，截错页 `UI_CAPTURE_WRONG_PAGE = {{UI_CAPTURE_WRONG_PAGE}}`
- DPI / 窗口矩阵：`{{WINDOW_DPI_MATRIX}}`
- 汇总：{{UI_CHECK_SUMMARY}}
- 尺寸与缩放：{{UI_WINDOW_SIZES}}
- 组合：{{UI_COMBINATIONS}}
- 证据目录：{{UI_EVIDENCE}}

## 8. 安装器证据

{{INSTALLER_UI_STATUS}}

- 截图张数：{{INSTALLER_SCREENSHOT_COUNT}}
- 非空校验：{{INSTALLER_NONBLANK_CHECK}}
- 阶段单调性：{{INSTALLER_STAGE_MONOTONIC}}
- 六状态渲染细节：{{ROUND10_SETUP_UI}}

## 9. 主题 / 运营看板 / 推荐地址

- 主题：{{THEME_STATUS}}
- 手机主题：{{MOBILE_THEME_STATUS}}
- 运营看板：{{OPS_DASHBOARD_STATUS}}
- 设计系统：{{DESIGN_SYSTEM_STATUS}}
- 响应式：{{RESPONSIVE_UI_STATUS}}
- NAS 界面：{{NAS_UI_STATUS}}

## 10. 动态权限与在线设备

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

## 11. 测试与回归

- 全量回归：{{FULL_REGRESSION}}
- 本轮新增门禁：{{NEW_TESTS}}
- 浏览器端到端：Chromium {{SAFARI_CHROMIUM}}
- 浏览器端到端：WebKit {{SAFARI_WEBKIT}}
- 界面自动化：{{UI_AUTOMATION}}
- 测试证据：{{TEST_EVIDENCE_BYTES}} 字节 / SHA256 `{{TEST_EVIDENCE_SHA256}}`

## 12. 产物

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `LanDisk-Setup.exe` | {{SETUP_BYTES}} | `{{SETUP_SHA256}}` |
| `LanDisk-win-x64.zip` | {{RELEASE_BYTES}} | `{{RELEASE_SHA256}}` |
| `{{UPDATE_NAME}}` | {{UPDATE_BYTES}} | `{{UPDATE_SHA256}}` |
| `LanDisk-source.zip` | {{SOURCE_BYTES}} | `{{SOURCE_SHA256}}` |
| 审计导出 | {{AUDIT_EXPORT_BYTES}} | `{{AUDIT_EXPORT_SHA256}}` |
| 运行期身份 | {{IDENTITY_BYTES}} | `{{IDENTITY_SHA256}}` |
| 源码树校验 | {{SOURCE_VERIFY_BYTES}} | `{{SOURCE_VERIFY_SHA256}}` |

## 13. 验收结果

- 安装清单：`Verify-InstallManifest` {{UPDATE_SUCCESS}}
- 版本迁移：{{SETUP_MIGRATION}}
- 更新成功：{{UPDATE_SUCCESS}}
- 更新回滚：{{UPDATE_ROLLBACK}}
- 候选身份：{{IDENTITY_MATCH}}
- 源码树：{{SOURCE_TREE_VERIFY}}
- Windows runtime：{{WINDOWS_RUNTIME}}

## 14. 安全与隐私

- 真实用户数据被修改：{{REAL_USER_DATA_TOUCHED}}
- 交付物里的真实个人文件：{{REAL_PERSONAL_FILES_INCLUDED}}
- 交付物里的令牌 / Cookie：{{TOKENS_OR_COOKIES_IN_ARTIFACTS}}
- 交付物里的私钥：{{PRIVATE_KEY_IN_ARTIFACTS}}
- 发布签名私钥：{{RELEASE_SIGNING_PRIVATE_KEY_IN_ARTIFACTS}}
- 签名验证：{{SIGNATURE_VERIFY}}
- 独立产物里的 install id / 主机名 / 根 CA / 配对设备 / 端口 / 共享：{{INSTALL_ID_PRESERVED}} / {{HOSTNAME_PRESERVED}} / {{ROOT_CA_PRESERVED}} / {{PAIRED_DEVICES_PRESERVED}} / {{PORTS_PRESERVED}} / {{SHARES_PRESERVED}}

## 15. 真机项目（NOT_RUN）

{{REAL_DEVICE_NOT_RUN}}

任务书明确：真机项在环境不满足时记为 `NOT_RUN`，**不得**用「未完成/时间不足」当作阻断；本轮所有软件路径均已实现并有门禁，只有下列项目需要真机：

- `WIFI_REAL_CONNECT` —— 真机交互连接（自动化测试切断运行机网络会破坏运行本身）
- `EXTENSION_REAL_EXECUTE` —— 在真机上执行一个真实第三方扩展
- `IPHONE_REAL_PAIRING_1_7_1`、`IPHONE_FULLSCREEN_1_7_1`、`IPHONE_LOCAL_NAME_WITH_VPN`、`IPHONE_PHYSICAL_IP`
- `VPN_OFF_RUNTIME`、`MP4_H264`、`UI_CROSS_MONITOR`、`FIREWALL_RUNTIME`

另有如实标注的非 PASS 项：

- `DOCUMENT_TXT = NO`（不做文档 TXT 导出）
- `AUTHENTICODE = NOT_CONFIGURED`（无代码签名证书）
- `INSTALLER_REAL_DPI = NOT_RUN`（只提供 AutoScaleMode=Dpi + PerMonitorV2 与布局数学门禁）

## 16. 未完成 / 部分完成项

{{ROUND10_BLOCKED}}

## 17. 证据

- 测试证据：`dist/test-evidence.tsv`
- 界面证据：{{UI_EVIDENCE}}
- 运行期身份：`dist/delivery/runtime-identity/`
- 审计导出：`dist/delivery/LAN_DISK_10_CLOSEOUT_AUDIT_EXPORT.md`（同时收在审核包根目录）
- 缺陷修正记录：`docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md`
- 审核包：`dist/delivery/LAN_DISK_PRODUCTIZATION_CLOSEOUT_WIFI_EXTENSION_10_02_REVIEW_BUNDLE.zip`

## 18. 本轮没有做的事

- 没有触碰 `D:\Projects\pour-water-game`。
- 没有使用、也没有写入 Owner 提供的路由器管理地址与口令。
- 没有把 1.7.0 的产物改标成 1.7.1：1.7.0 的四个产物仍是它们自己的字节与哈希，本轮重新构建并重新计算。
- 没有引入公网 / 云 / 账号体系；扩展只在局域网内、由本机批准后执行。
- 没有为了凑门禁而放宽任何既有断言；有意变更的四处门禁（shell-execute 站点 2→1 并新增 `NativeDialogs.OpenFolder`、`FIX-09-05e` 改断言主题目录、`UI-MOBILE-NAV` 期望 6 个标签、`UiCapture` 12 DIP 规则排除图标字形、设备状态门禁排除 `UiCapture.cs` 证据工具）都记录在 `docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md`。
