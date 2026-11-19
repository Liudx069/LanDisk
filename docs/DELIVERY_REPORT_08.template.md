# LAN_DISK_NATIVE_WINDOWS_UX_08 DELIVERY REPORT

> 本文件由 `tools/Generate-DeliveryReport.ps1` 在 **FINAL_HEAD 锁定之后**生成到 `dist/delivery/`。
> `dist/` 被 `.gitignore` 忽略，因此生成报告**不会改变源码 HEAD**。

## 状态

```
FINAL_STATUS:     {{FINAL_STATUS}}
PROJECT_PATH:     {{PROJECT_PATH}}
START_HEAD:       {{START_HEAD}}
FINAL_HEAD:       {{FINAL_HEAD}}
WORKTREE_STATUS:  {{WORKTREE_STATUS}}
PRODUCT_VERSION:  {{PRODUCT_VERSION}}
GENERATED_AT_UTC: {{GENERATED_AT_UTC}}
```

## 1. UI

```
FRAMEWORK:                   {{FRAMEWORK}}
WPF_UI_VERSION:              {{WPF_UI_VERSION}}
LICENSE:                     {{LICENSE}}
WEBVIEW_USED:                {{WEBVIEW_USED}}
NORMAL_FLOW_OPENS_BROWSER:   {{NORMAL_FLOW_OPENS_BROWSER}}
ADVANCED_RECOVERY_BROWSER_ONLY: {{ADVANCED_RECOVERY_BROWSER_ONLY}}
THIRD_PARTY_NOTICES:         {{THIRD_PARTY_NOTICES}}
WINDOWS10_FALLBACK:          {{WINDOWS10_FALLBACK}}
SHELL_RUNTIME_CHECK:         {{SHELL_RUNTIME_CHECK}}
```

## 2. Native pages

```
OVERVIEW:    {{PAGE_OVERVIEW}}
SHARES:      {{PAGE_SHARES}}
DEVICES:     {{PAGE_DEVICES}}
IPHONE:      {{PAGE_IPHONE}}
NETWORK:     {{PAGE_NETWORK}}
HTTPS:       {{PAGE_HTTPS}}
RECYCLE:     {{PAGE_RECYCLE}}
UPDATE:      {{PAGE_UPDATE}}
SELF_CHECK:  {{PAGE_SELFCHECK}}
LOGS:        {{PAGE_LOGS}}
SETTINGS:    {{PAGE_SETTINGS}}
FOLDER_PICKER: {{FOLDER_PICKER}}
FILE_PICKER:   {{FILE_PICKER}}
QR_IN_APP:     {{QR_IN_APP}}
```

## 3. DPI / 布局

```
RENDERED_COMBINATIONS:   {{UI_COMBINATIONS}}
LAYOUT_CHECKS:           {{UI_CHECK_SUMMARY}}
100:                     {{UI_100}}
125:                     {{UI_125}}
150:                     {{UI_150}}
175:                     {{UI_175}}
200:                     {{UI_200}}
1366x768:                {{UI_1366}}
1920x1080:               {{UI_1920}}
NO_VERTICAL_TEXT_WRAP:   {{UI_NOWRAP}}
PERMONITORV2:            {{PERMONITOR}}
SCREENSHOT_EVIDENCE:     {{UI_EVIDENCE}}
CROSS_MONITOR_DPI:       {{UI_CROSS_MONITOR}}
```

## 4. VPN / TUN

```
SELECTED_PHYSICAL_ADAPTER: {{VPN_SELECTED}}
VIRTUAL_ADAPTERS_EXCLUDED: {{VPN_EXCLUDED}}
MDNS_BIND_INTERFACE:       {{VPN_MDNS_BIND}}
ADVERTISED_ADDRESSES:      {{VPN_ADVERTISED}}
VPN_TUN_DETECTED:          {{VPN_DETECTED}}
VPN_OFF_RUNTIME:           {{VPN_OFF_RUNTIME}}
VPN_ON_RUNTIME:            {{VPN_ON_RUNTIME}}
IOS_VPN_LIMITATION_UI:     {{VPN_UI_TEXT}}
IP_FALLBACK:               {{VPN_IP_FALLBACK}}
ROUTING_UNTOUCHED:         {{VPN_ROUTING_UNTOUCHED}}
```

## 5. Safari 根路由

```
ROOT_ROUTE_FIX:          {{SAFARI_FIX}}
CONTENT_TYPE:            {{SAFARI_CONTENT_TYPE}}
CONTENT_DISPOSITION:     {{SAFARI_DISPOSITION}}
WEBKIT:                  {{SAFARI_WEBKIT}}
CHROMIUM:                {{SAFARI_CHROMIUM}}
IPHONE_PHYSICAL_IP:      {{SAFARI_IPHONE_IP}}
IPHONE_LOCAL_NAME:       {{SAFARI_IPHONE_LOCAL}}
DOCUMENT_TXT_REPRODUCES: {{SAFARI_REPRODUCES}}
```

## 6. Web 管理页（恢复入口）

```
FONT_FLOOR:      {{WEB_FONT_FLOOR}}
TABLE_NOWRAP:    {{WEB_TABLE_NOWRAP}}
PAIRING_TOKEN:   {{WEB_PAIRING_TOKEN}}
```

## 7. Regression

```
OLD_BASELINE:        {{OLD_BASELINE}}
NEW_TESTS:           {{NEW_TESTS}}
FULL_REGRESSION:     {{FULL_REGRESSION}}
UI_AUTOMATION:       {{UI_AUTOMATION}}
WINDOWS_RUNTIME:     {{WINDOWS_RUNTIME}}
SETUP_MIGRATION:     {{SETUP_MIGRATION}}
UPDATE_SUCCESS:      {{UPDATE_SUCCESS}}
UPDATE_ROLLBACK:     {{UPDATE_ROLLBACK}}
SIGNATURE_VERIFY:    {{SIGNATURE_VERIFY}}
SOURCE_VERIFY:       {{SOURCE_TREE_VERIFY}}
RELEASE_IDENTITY:    {{IDENTITY_MATCH}}
INSTALL_MANIFEST:    {{IMCB_VERDICT}}
```

## 8. User data

```
REAL_DATA_TOUCHED_BY_AUTOMATION: {{REAL_USER_DATA_TOUCHED}}
SHARES_PRESERVED:                {{SHARES_PRESERVED}}
DEVICES_PRESERVED:               {{PAIRED_DEVICES_PRESERVED}}
ROOT_CA_PRESERVED:               {{ROOT_CA_PRESERVED}}
INSTALL_ID_PRESERVED:            {{INSTALL_ID_PRESERVED}}
HOSTNAME_PRESERVED:              {{HOSTNAME_PRESERVED}}
PORTS_PRESERVED:                 {{PORTS_PRESERVED}}
```

## 9. 普通用户实际要做的步骤

Windows 侧，一次安装：双击 `LanDisk-Setup.exe` → 同意一次 UAC（用途写明是防火墙规则）→
等安装完成，**LanDisk 以原生窗口启动** → 点「连接 iPhone」。之后所有管理都在这个窗口里完成，
不需要浏览器、不需要记端口、不需要看 IP。

iPhone 侧，一次信任 + 一次配对：扫描窗口里的第一个二维码 → 安装 LanDisk 安全证书 →
设置 → 通用 → 关于本机 → 证书信任设置 → 打开对 `LanDisk Local CA` 的完全信任 →
回到 Safari 点「继续」→ 扫描窗口里的第二个二维码完成配对。

## 10. 已知 / NOT_RUN

{{KNOWN_NOT_RUN}}

## 11. 交付物

| 产物 | path | bytes | sha256 |
|---|---|---|---|
| setup | `dist/delivery/LanDisk-Setup.exe` | {{SETUP_BYTES}} | `{{SETUP_SHA256}}` |
| release | `dist/delivery/LanDisk-win-x64.zip` | {{RELEASE_BYTES}} | `{{RELEASE_SHA256}}` |
| update | `dist/delivery/{{UPDATE_NAME}}` | {{UPDATE_BYTES}} | `{{UPDATE_SHA256}}` |
| source | `dist/delivery/LanDisk-source.zip` | {{SOURCE_BYTES}} | `{{SOURCE_SHA256}}` |
| UI 截图证据 | `dist/delivery/ui-evidence/` | {{UI_EVIDENCE_BYTES}} | 见 `ui-evidence/UI_CAPTURE.tsv` |
| 运行期身份（真实运行） | `dist/delivery/runtime-identity/` | {{RUNTIME_IDENTITY_BYTES}} | 见 `runtime-identity/runtime-identity.json` |
| 安装清单专项验证 | `dist/delivery/INSTALL_MANIFEST_VERIFY.json` | {{IMCB_EVIDENCE_BYTES}} | `{{IMCB_EVIDENCE_SHA256}}` |
| 候选身份 | `dist/delivery/RELEASE_CANDIDATE_IDENTITY.json` | {{IDENTITY_BYTES}} | `{{IDENTITY_SHA256}}` |
| 源码树验证 | `dist/delivery/SOURCE_TREE_VERIFY.json` | {{SOURCE_VERIFY_BYTES}} | `{{SOURCE_VERIFY_SHA256}}` |
| 测试证据 | `dist/delivery/test-evidence.tsv` | {{TEST_EVIDENCE_BYTES}} | `{{TEST_EVIDENCE_SHA256}}` |
| 交付报告 | `dist/delivery/DELIVERY_REPORT.md` | {{REPORT_BYTES}} | （本文件，不含自身哈希） |
| 审核包 | `dist/delivery/LAN_DISK_NATIVE_WINDOWS_UX_08_REVIEW_BUNDLE.zip` | 见 `REVIEW_BUNDLE_SHA256.txt` | 见同目录 sidecar |

## 12. 隐私 / 安全

```
PRIVATE_KEY_IN_ARTIFACTS:       {{PRIVATE_KEY_IN_ARTIFACTS}}
RELEASE_KEY_IN_ARTIFACTS:       {{RELEASE_SIGNING_PRIVATE_KEY_IN_ARTIFACTS}}
TOKENS_OR_COOKIES_IN_ARTIFACTS: {{TOKENS_OR_COOKIES_IN_ARTIFACTS}}
REAL_PERSONAL_FILES_INCLUDED:   {{REAL_PERSONAL_FILES_INCLUDED}}
```

## 13. 本轮改动摘要

见 `AUDIT_CORRECTIONS_08.md`。摘要：`LanDisk.App` 由 WinForms 改为 WPF（WPF-UI 4.3.0 + .NET 10，
11 个原生页面、托盘、单实例、PerMonitorV2、Fluent Obsidian 主题）；新增 `AdminOperations`
让端点与原生界面共用同一套业务与校验；新增 `PhysicalLanSelector` 并接入证书 SAN、mDNS
（逐接口 socket）、引导地址与监听目标；修 Web 管理页字号/列挤压/配对令牌暴露；
修 Safari 根路由契约（用测试固定，真机项 NOT_RUN）；版本 1.5.0。

未改动：文件浏览/搜索/排序、查看/播放/下载、>4GiB 上传/续传、重命名/删除/回收站、批量 ZIP、
recent/favorites、PathResolver/卷身份/路径安全、设备权限模型、更新签名与回滚模型、
8891/8892 语义、Setup 与根 CA 模型。
