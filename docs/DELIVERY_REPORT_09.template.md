# LAN_DISK_NATIVE_PARITY_NETWORK_UX_HOTFIX_09_REPAIR_03 DELIVERY REPORT

> 本文件由 `tools/Generate-DeliveryReport.ps1` 在 **FINAL_HEAD 锁定之后**生成到 `dist/delivery/`。
> `dist/` 被 `.gitignore` 忽略，因此生成报告**不会改变源码 HEAD**。

## 0. 状态

```
FINAL_STATUS:     {{FINAL_STATUS}}
PROJECT_PATH:     {{PROJECT_PATH}}
START_HEAD:       {{START_HEAD}}
FINAL_HEAD:       {{FINAL_HEAD}}
WORKTREE_STATUS:  {{WORKTREE_STATUS}}
PRODUCT_VERSION:  {{PRODUCT_VERSION}}
GENERATED_AT_UTC: {{GENERATED_AT_UTC}}
```

## 1. 本轮修复（FIX-09-01 … FIX-09-06）

这一轮是**定点修复**，不是功能开发：六处"原生界面与 HTTP 行为不一致 / 网络与主题半实现"的问题，
每一处都由测试守住。

```
FIX-09-01_PAIRING:    {{FIX0901}}
FIX-09-02_DEVICES:    {{FIX0902}}
FIX-09-03_RECYCLE:    {{FIX0903}}
FIX-09-04_MDNS:       {{FIX0904}}
FIX-09-05_THEME:      {{FIX0905}}
FIX-09-06_ONBOARDING: {{FIX0906}}
```

### 1.1 为什么这六处要用"同一实现 + 对照测试"来修

原生窗口和 HTTP 端点是同一台机器上的两扇门。上一轮把界面从浏览器搬进 WPF 时，两扇门各自演化：

| 操作 | 浏览器（HTTP） | 原生窗口（修复前） | 修复后 |
|---|---|---|---|
| 确认配对 | 创建设备、写审计 | **调用 `Pairing.Cancel`** —— 确认等于取消，设备根本不存在 | 两边都调 `AdminOperations.ConfirmPairing` |
| 拒绝配对 | `Pairing.Cancel` | 直接 `Pairing.Cancel`（无审计） | 两边都调 `AdminOperations.RejectPairing` |
| 重命名设备 | `SanitizeDeviceName` 清洗 | `name.Trim()`，不清洗 | 两边都调 `AdminOperations.RenameDevice`（共享 `DeviceNames.Sanitize`） |
| 改为读写 | 改 `Level` + 审计 | 只改 `Level`，无审计 | 两边都调 `AdminOperations.SetDeviceAccess` |
| 撤销设备 | 标记 + 轮换 TokenHash + 轮换 SessionEpoch + 取消上传 + 审计 | **只标记 `Revoked`**（旧 cookie 继续可用） | 两边都调 `AdminOperations.RevokeDevice` |
| 回收站恢复 | `local-admin` 演员 | **`Devices.All().First()`** —— 0 台设备时抛异常，有设备时记到第一台手机头上 | 两边都收到显式的 `OperationActor.LocalAdmin` |

"确认配对"那一条是本轮最严重的缺陷：在电脑上点"确认"，配对请求被取消，手机永远拿不到设备凭证。

## 2. FIX-09-01 确认配对

抽出一个实现，两处调用：`AdminOperations.ConfirmPairing(id)`。它按端点原来的顺序做完全部检查，
产出的设备与 HTTP 路径逐字段一致（见 `FIX-09-01f`）。

```
NATIVE_CONFIRM_CREATES_DEVICE:   {{FIX0901_CREATE}}
NATIVE_CONFIRM_NOT_CANCEL:       {{FIX0901_NOTCANCEL}}
MISSING_REFUSED:                 {{FIX0901_MISSING}}
EXPIRED_REFUSED:                 {{FIX0901_EXPIRED}}
WRONG_STATE_REFUSED:             {{FIX0901_STATE}}
SECOND_CONFIRM_REFUSED:          {{FIX0901_SECOND}}
REJECT_CANCELS:                  {{FIX0901_REJECT}}
NATIVE_VS_HTTP_FIELDS:           {{FIX0901_PARITY}}
AUDIT:                           {{FIX0901_AUDIT}}
```

## 3. FIX-09-02 设备管理语义

```
NATIVE_REVOKE_FULL:        {{FIX0902_REVOKE}}
SESSION_BEFORE_AFTER:      {{FIX0902_SESSION}}
TOKEN_HASH_ROTATED:        {{FIX0902_TOKEN}}
SESSION_EPOCH_ROTATED:     {{FIX0902_EPOCH}}
UPLOAD_CANCELLED:          {{FIX0902_UPLOAD}}
NATIVE_VS_HTTP_STATE:      {{FIX0902_PARITY}}
RENAME_SANITIZE:           {{FIX0902_RENAME}}
AUDIT:                     {{FIX0902_AUDIT}}
```

## 4. FIX-09-03 回收站不再借第一台手机的身份

新增 `OperationActor`（`Device` / `LocalAdmin` / `System`），并把它带进 `RecycleService`；
`Purge` 也一并改成显式演员。审计里新增 `source` 字段，审核者可以直接看到
`DeviceName = 本机管理端`、`source = local-admin`、`DeviceId` 为空。

```
RESTORE_ZERO_DEVICES:      {{FIX0903_RESTORE0}}
PURGE_ZERO_DEVICES:        {{FIX0903_PURGE0}}
TWO_DEVICES_NOT_ANY:       {{FIX0903_TWO}}
PHONE_RESTORE_UNCHANGED:   {{FIX0903_PHONE}}
PURGE_CONFIRM_UNCHANGED:   {{FIX0903_CONFIRM}}
ACTOR_NOT_PERSISTED:       {{FIX0903_ACTOR}}
```

## 5. FIX-09-04 mDNS 单播回包接口

修复前：`SendUnicast` 用 `socket.LocalEndPoint.Address == remote.Address` 选 socket。`remote.Address`
是 iPhone 的地址，永远不可能等于本机网卡地址，所以多物理网卡时几乎总是回落到 `sockets[0]`。

修复后：`HandleDatagram` 把**收到这个请求的 socket** 直接传给 `SendUnicast`；
两者都没有的多网卡情况下宁可不发，也不猜接口（并记日志说明原因）。

```
NIC_A_REPLY_FROM_A:        {{FIX0904_A}}
NIC_B_REPLY_FROM_B:        {{FIX0904_B}}
NOT_ALWAYS_SOCKETS_ZERO:   {{FIX0904_NOTFIRST}}
NO_RECEIVING_SOCKET:       {{FIX0904_NONE}}
MULTICAST_REGRESSION:      {{FIX0904_MULTICAST}}
VPN_TUN_NO_SOCKET:         {{FIX0904_VPN}}
```

## 6. FIX-09-05 主题（深色 / 浅色 / 跟随系统）

调色板现在是 `LanDisk.Core.Configuration.ThemePalette` 里的数据，WPF 只是把它变成画刷。
这样"浅色到底有没有真正实现"可以被断言，而不是靠看：每个角色两套值都在、两套确实不同、
每种前景色对所处表面的对比度都达标。

切换时同时做三件事：WPF-UI 的 `ApplicationThemeManager.Apply`、产品自己 11 个画刷的颜色
（就地改 `SolidColorBrush.Color`，页面已持有的实例会立刻重绘，不需要重建页面）、窗口 chrome 重绘。
选择写入 `shell.json`（属受管更新状态，升级与回滚都保留）。

```
PALETTE_ROLES:          {{FIX0905_ROLES}}
DARK_LIGHT_DIFFER:      {{FIX0905_DIFFER}}
WORST_CONTRAST_DARK:    {{FIX0905_CONTRAST_DARK}}
WORST_CONTRAST_LIGHT:   {{FIX0905_CONTRAST_LIGHT}}
LIGHT_VALUES_PINNED:    {{FIX0905_PINNED}}
PERSISTED_ACROSS_RESTART: {{FIX0905_PERSIST}}
ONE_MANAGER_ONLY:       {{FIX0905_GATE}}
UI_CAPTURE_THEMES:      {{UI_COMBINATIONS}}
```

## 7. FIX-09-06 首次连接少两步人工操作

去掉两步，保留苹果要求的那一步：

| 步骤 | 修复前 | 修复后 |
|---|---|---|
| 回到 Safari 点"继续" | 必须点 | **不需要**：引导页每 1.5 秒自动探测一次 HTTPS trust |
| 电脑上点"生成配对请求" | 必须点 | **不需要**：`Trusted` 后窗口自动复用或创建一个请求并显示二维码 |

苹果的"设置 → 通用 → 关于本机 → 证书信任设置 → 完全信任"仍然必须用户本人完成，不绕过。

安全性质不变：明文 HTTP 引导页不含配对令牌、一次性令牌只走 HTTPS、原始配对链接默认折叠、
Windows 本机"确认设备"仍然保留。配对成功后引导页与 8890 监听会被关掉。

```
BOOTSTRAP_AUTO_PROBE:      {{FIX0906_PROBE}}
NO_CONTINUE_BUTTON:        {{FIX0906_NOBUTTON}}
STABLE_NAME_FIRST:         {{FIX0906_PREFER}}
REQUEST_REUSED:            {{FIX0906_REUSE}}
REQUEST_REPLACED_ON_EXPIRY: {{FIX0906_EXPIRY}}
RAW_LINK_FOLDED:           {{FIX0906_FOLDED}}
TRUST_MARKS_TRUSTED:       {{FIX0906_TRUST}}
```

## 8. 回归

```
OLD_BASELINE:        449 / 449 PASS（08 轮开始前的基线）
PREVIOUS_SUITE:      547 / 547 PASS（09_REPAIR_02 最终候选，本轮的起点 HEAD）
NEW_TESTS:           {{NEW_TESTS}}
FULL_REGRESSION:     {{FULL_REGRESSION}}
UI_AUTOMATION:       {{UI_AUTOMATION}}
WINDOWS_RUNTIME:     {{WINDOWS_RUNTIME}}
SETUP_MIGRATION:     接管安装：{{SETUP_MIGRATION}}
UPDATE_SUCCESS:      {{UPDATE_SUCCESS}}
UPDATE_ROLLBACK:     {{UPDATE_ROLLBACK}}
SIGNATURE_VERIFY:    {{SIGNATURE_VERIFY}}
SOURCE_VERIFY:       {{SOURCE_TREE_VERIFY}}
RELEASE_IDENTITY:    {{IDENTITY_MATCH}}
INSTALL_MANIFEST:    {{IMCB_VERDICT}}
```

## 9. UI / DPI / 主题证据

```
RENDERED_COMBINATIONS:   {{UI_COMBINATIONS}}
LAYOUT_CHECKS:           {{UI_CHECK_SUMMARY}}
DARK_100:                {{UI_DARK_100}}
DARK_125:                {{UI_DARK_125}}
DARK_150:                {{UI_DARK_150}}
DARK_200:                {{UI_DARK_200}}
LIGHT_100:               {{UI_LIGHT_100}}
LIGHT_125:               {{UI_LIGHT_125}}
LIGHT_150:               {{UI_LIGHT_150}}
LIGHT_200:               {{UI_LIGHT_200}}
EXTRA_WINDOW_SIZES:      {{UI_WINDOW_SIZES}}
NO_VERTICAL_TEXT_WRAP:   {{UI_NOWRAP}}
NO_LOW_CONTRAST:         {{UI_CONTRAST}}
QR_SQUARE:               {{UI_QR}}
PERMONITORV2:            app.manifest 声明 dpiAwareness=PerMonitorV2，并保留 true/PM 以兼容较旧的 Windows 10
SCREENSHOT_EVIDENCE:     {{UI_EVIDENCE}}
CROSS_MONITOR_DPI:       {{UI_CROSS_MONITOR}}
```

每个组合都断言：无横向溢出、没有单行文本被挤成竖排、没有低于 12 DIP 的字号、
没有文字与背景对比度低于 3.0（白底白字/黑底黑字）、每一张图片都是正方形（二维码可扫）。

## 10. VPN / TUN

```
SELECTED_PHYSICAL_ADAPTER: {{VPN_SELECTED}}
VIRTUAL_ADAPTERS_EXCLUDED: {{VPN_EXCLUDED}}
MDNS_BIND_INTERFACE:       {{VPN_MDNS_BIND}}
ADVERTISED_ADDRESSES:      {{VPN_ADVERTISED}}
VPN_TUN_ON:                {{VPN_DETECTED}}
VPN_OFF_RUNTIME:           {{VPN_OFF_RUNTIME}}
VPN_ON_RUNTIME:            {{VPN_ON_RUNTIME}}
IOS_VPN_LIMITATION_UI:     {{VPN_UI_TEXT}}
IP_FALLBACK:               {{VPN_IP_FALLBACK}}
ROUTING_UNTOUCHED:         {{VPN_ROUTING_UNTOUCHED}}
```

## 11. Safari 根路由与真机项

```
ROOT_ROUTE_FIX:          {{SAFARI_FIX}}
CONTENT_TYPE:            {{SAFARI_CONTENT_TYPE}}
CONTENT_DISPOSITION:     {{SAFARI_DISPOSITION}}
WEBKIT:                  {{SAFARI_WEBKIT}}
CHROMIUM:                {{SAFARI_CHROMIUM}}
IPHONE_PHYSICAL_IP:      {{SAFARI_IPHONE_IP}}
IPHONE_LOCAL_NAME:       {{SAFARI_IPHONE_LOCAL}}
DOCUMENT_TXT:            {{SAFARI_REPRODUCES}}
MP4_H264:                {{MP4_H264}}
PAIRING:                 {{PAIRING}}
HTTPS:                   {{HTTPS}}
```

## 12. 用户数据

```
REAL_DATA_TOUCHED_BY_AUTOMATION: {{REAL_USER_DATA_TOUCHED}}
SHARES_PRESERVED:                {{SHARES_PRESERVED}}
DEVICES_PRESERVED:               {{PAIRED_DEVICES_PRESERVED}}
ROOT_CA_PRESERVED:               {{ROOT_CA_PRESERVED}}
INSTALL_ID_PRESERVED:            {{INSTALL_ID_PRESERVED}}
HOSTNAME_PRESERVED:              {{HOSTNAME_PRESERVED}}
PORTS_PRESERVED:                 {{PORTS_PRESERVED}}
```

## 13. 已知 / NOT_RUN

{{KNOWN_NOT_RUN}}

## 14. 交付物

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
| 审核包 | `dist/delivery/LAN_DISK_NATIVE_PARITY_NETWORK_UX_HOTFIX_09_REPAIR_03_REVIEW_BUNDLE.zip` | 见 `REVIEW_BUNDLE_SHA256.txt` | 见同目录 sidecar |

## 15. 隐私 / 安全

```
PRIVATE_KEY_IN_ARTIFACTS:       {{PRIVATE_KEY_IN_ARTIFACTS}}
RELEASE_KEY_IN_ARTIFACTS:       {{RELEASE_SIGNING_PRIVATE_KEY_IN_ARTIFACTS}}
TOKENS_OR_COOKIES_IN_ARTIFACTS: {{TOKENS_OR_COOKIES_IN_ARTIFACTS}}
REAL_PERSONAL_FILES_INCLUDED:   {{REAL_PERSONAL_FILES_INCLUDED}}
```

## 16. 本轮改动摘要

见 `AUDIT_CORRECTIONS_09.md`、`AUDIT_CORRECTIONS_09_REPAIR_01.md`、`AUDIT_CORRECTIONS_09_REPAIR_02.md` 与 `AUDIT_CORRECTIONS_09_REPAIR_03.md`。摘要：确认/拒绝配对、设备重命名/权限/撤销、回收站恢复与永久删除
全部抽到 `AdminOperations` 并由原生窗口与 HTTP 端点共用；新增 `OperationActor` 让本机管理操作
不再借用手机身份，审计新增 `source`；mDNS 单播改用收到请求的 socket；调色板数据化并完整实现
浅色主题（含对比度与二维码方形断言）；首次连接去掉"点继续"与"点生成配对请求"两步；
REPAIR_01 再把配对成功后的收口、完成判断与"连接 iPhone"的一键开启补齐（见第 17 节）；
REPAIR_02 让配对二维码优先使用"真实 iPhone 刚刚验证可达"的 HTTPS 地址（见第 18 节）；
REPAIR_03 把 trust probe 改成"先证明、再相信"，并让"重新生成引导二维码"真正重新生成（见第 19 节）；
版本 1.6.3。

**未改动**：文件浏览/搜索/排序、查看/播放/下载、>4GiB 上传/续传、重命名/删除/回收站核心、
批量 ZIP、recent/favorites、PathResolver/卷身份/路径安全、设备权限模型、更新签名与回滚模型、
8891/8892 语义、Setup 与根 CA 架构、update package schema、ECDSA 签名。

## 17. REPAIR_01 —— 09 轮最后一处修复（本轮）

REPAIR_01 只做三件事，全部限定在"首次连接向导"的收口与完成判断上。第一件事修的是**用户还要自己
去关引导页**，第二件修的是**已经有一台手机时加第二台会显示成已完成**，第三件修的是
**"连接 iPhone"点一次还不出二维码**。

### 17.1 三处修复

```
REPAIR_01_FIX_1: {{REPAIR_FIX1}}
REPAIR_01_FIX_2: {{REPAIR_FIX2}}
REPAIR_01_FIX_3: {{REPAIR_FIX3}}
```

停止引导页这件事放在 `AdminOperations.ConfirmPairing` 里，而不是放在窗口里：网页端点和原生窗口
调用的是同一个方法，所以两条路径的行为必然一致；停止又排在"设备已落库 + 请求已指向该设备"之后，
所以任何在上面被拒绝的确认都不会关掉用户正站在里面的那个向导。

### 17.2 八项硬门禁（REPAIR09-01 … REPAIR09-08）

每一条都是真的跑过的断言，不是概述。行为类门禁跑在真实回环端口上：手机侧用真实的
`/api/pair/code` 认领、电脑侧调用共享的 `ConfirmPairing`，引导页是否真的关掉由**连接它的结果**
（连接被拒绝）证明，而不是读一个字段。

```
REPAIR09-01: {{REPAIR09_01}}
REPAIR09-02: {{REPAIR09_02}}
REPAIR09-03: {{REPAIR09_03}}
REPAIR09-04: {{REPAIR09_04}}
REPAIR09-05: {{REPAIR09_05}}
REPAIR09-06: {{REPAIR09_06}}
REPAIR09-07: {{REPAIR09_07}}
REPAIR09-08: {{REPAIR09_08}}
```

配套的补充断言（同一批测试里的同一组结论）：

```
REPAIR09-01_REFUSED:  {{REPAIR09_01B}}
REPAIR09-01_WIRING:   {{REPAIR09_01C}}
REPAIR09-04_SOURCE:   {{REPAIR09_04B}}
REPAIR09-05_INFLIGHT: {{REPAIR09_05B}}
REPAIR09-05_SOURCE:   {{REPAIR09_05C}}
REPAIR09-08_SOURCE:   {{REPAIR09_08B}}
```

其中 `REPAIR09-04` / `REPAIR09-05` 涉及 WPF 窗口本身：测试工程是 WinExe 且不被引用，**无法在单元
测试里点击一次原生按钮**。因此这两项分两层：行为层验证共享决策方法（`EnsureOnboardingStarted`
的幂等与 `BootstrapId` 不变、`CompletionOf` 只认本次请求），源码层验证窗口里那条旧规则与那个手动
按钮已经不存在、`OnShown` 调的确实是共享方法。源码闸门证明的是"那条代码路径不存在"，不是"有人
点过一次按钮"——这一点在结论里不夸大。

### 17.3 REPAIR_01 候选的状态与产物

REPAIR_01 的候选版本是 1.6.1，其产物哈希记载在当时生成的交付报告里；本节只保留它作为**本轮起点**的
HEAD / 工作树 / 回归状态，当前候选（1.6.2）的状态与产物在第 18 节。

```
START_HEAD_AT_REPAIR01:  d51996f9b29085994e618db881e4837e214fcca5
FINAL_HEAD_AT_REPAIR01:  0da13885995cec19a0ea791af62ed531c5a5ccde
KEY_REPAIR01_PRODUCT:    1.6.1（已被 1.6.2 取代）
FULL_REGRESSION_THEN:    531 / 531 PASS
```

### 17.4 明确未改动

文件服务核心、VPN 分类器、mDNS 已通过实现、主题、更新架构、Root CA、端口（8891/8892/8890）、
Setup 主架构都没有改动。REPAIR_01 新增的测试也**不写真实的 `%LOCALAPPDATA%\LanDisk`**：全部落在各自的
临时数据根目录里，真实安装目录的基线哈希在每一轮结束时逐字节一致。

## 18. REPAIR_02 —— 配对二维码优先使用"真实 iPhone 已验证可达"的 HTTPS 地址（本轮）

真实设备（用户提供）给出的观察把范围钉死了：VPN/TUN 开启时，iPhone 打开 `https://192.168.2.2:8892/`
完全正常，但第二张配对二维码里的 `https://landisk-a1b2c3d4.local:8892/...` 让 Safari 报
"此网站无法提供安全连接"。证书、端口、HTTPS、leaf SAN、物理 LAN 路由都没有问题 ——
**trust probe 早就知道哪个 host 能用了，只是二维码没有用这个信息**。

### 18.1 修法

```
REPAIR_02_FIX_1: {{REPAIR02_FIX1}}
REPAIR_02_FIX_2: {{REPAIR02_FIX2}}
REPAIR_02_FIX_3: {{REPAIR02_FIX3}}
```

排序规则（原生窗口与 HTTP 端点共用同一个 `AdminOperations.PairingUrls`）：

```
VERIFIED_HOST  →  stable .local  →  其它 eligible 物理 LAN 地址  →  已绑定的其它地址  →  loopback（最后）
```

不是"检测到 VPN 就用 IP"：如果稳定名称就是刚刚成功的那个 host，它仍然排第一。hint 在每次生成链接时
都会重新核对（是否仍 eligible、leaf SAN 是否仍覆盖该地址、是否已超 20 分钟），失效就退回正常顺序；
它**不写入任何文件**，配对成功后清空，新一轮引导清空。

### 18.2 十四项硬门禁 + 两项补充（REPAIR02-01 … REPAIR02-14）

门禁跑在"像手机看到的那样"的测试主机上（`LanEnabled` + `HttpsEnabled`，服务端真的在物理 LAN 地址上
监听 TLS），HTTPS 探测用真实 HTTP 请求发出，服务端记录的正是同一个 `Request.Host.Host`。

```
REPAIR02-01: {{REPAIR02_01}}
REPAIR02-02: {{REPAIR02_02}}
REPAIR02-03: {{REPAIR02_03}}
REPAIR02-04: {{REPAIR02_04}}
REPAIR02-05: {{REPAIR02_05}}
REPAIR02-06: {{REPAIR02_06}}
REPAIR02-07: {{REPAIR02_07}}
REPAIR02-08: {{REPAIR02_08}}
REPAIR02-09: {{REPAIR02_09}}
REPAIR02-10: {{REPAIR02_10}}
REPAIR02-11: {{REPAIR02_11}}
REPAIR02-12: {{REPAIR02_12}}
REPAIR02-13: {{REPAIR02_13}}
REPAIR02-14: {{REPAIR02_14}}
```

```
REPAIR02-15_ROOT_ROUTE:  {{REPAIR02_15}}
REPAIR02-16_LOOPBACK:    {{REPAIR02_16}}
```

### 18.3 用户提供的真机观察（不是 DSH 完成的真机测试）

```
OWNER_PRE_REPAIR_OBSERVATION:
  VPN/TUN ON
  stable .local pairing QR -> Safari "此网站无法提供安全连接"
  https://192.168.2.2:8892/ -> 真实 iPhone 正常打开 LanDisk
  document.txt 在 1.6.1 真机上未再出现
```

以上是**用户提供的修复输入**，DSH 没有真实 iPhone，因此**没有**据此自我签署任何真机结论。

### 18.4 REPAIR_02 候选的状态与产物

REPAIR_02 的候选版本是 1.6.2，其产物哈希记载在当时生成的交付报告里；本节只保留它作为**上一轮起点**的
状态，当前候选（1.6.3）的状态与产物见第 19 节。

```
START_HEAD_AT_REPAIR02:  0da13885995cec19a0ea791af62ed531c5a5ccde
FINAL_HEAD_AT_REPAIR02:  4f74eebf76268e659923d265c4b926a651e1ad6e
KEY_REPAIR02_PRODUCT:    1.6.2（已被 1.6.3 取代）
FULL_REGRESSION_THEN:    547 / 547 PASS
```

### 18.5 明确未改动

mDNS 的实现与接口选择、`PhysicalLanSelector` 的分类规则、根 CA 架构与 leaf 签发规则、端口语义
（8891/8892/8890/5353）、更新签名与回滚、Setup 主架构、主题、回收站、设备语义、`OperationActor`、
`PathResolver`、卷身份、上传/续传、浏览/搜索/排序、收藏/最近、批量 ZIP 全部未改动；
没有新增任何 route / DNS / VPN / 网卡配置写入，也没有要求用户关闭 VPN；根路由只做回归门禁（18.2 的
REPAIR02-15），未作修改。

## 19. REPAIR_03 —— trust probe 的门禁顺序与真正的"重新生成引导二维码"（本轮）

这一轮两处改动都属于"已经写好的东西其实没有真正生效"：探针**先相信再验证**，恢复按钮接在一个
**故意幂等**的调用上。

### 19.1 两处修复

```
REPAIR_03_FIX_1: {{REPAIR03_FIX1}}
REPAIR_03_FIX_2: {{REPAIR03_FIX2}}
```

探针现在的判定顺序（顺序本身就是语义）：会话 id → 必须走 TLS → Host 必须是本机合法地址 →
**才**标记 Trusted → 204。明文 HTTP 的探测不标记信任、不记录地址、也不会让窗口自动准备配对二维码。

"重新生成引导二维码"走的是显式恢复入口 `AdminOperations.RestartOnboarding()`：结束 8890 监听 →
清空上一轮的可达地址提示 → 取消未完成的配对请求（`state=Cancelled`）并写本机管理端审计 →
开启新会话（新 BootstrapId / 新二维码）。普通刷新与 `OnShown` 仍走幂等的
`EnsureOnboardingStarted()`，手机正在看的二维码不会被换掉。

### 19.2 十项硬门禁（REPAIR03-01 … REPAIR03-10）

```
REPAIR03-01: {{REPAIR03_01}}
REPAIR03-02: {{REPAIR03_02}}
REPAIR03-03: {{REPAIR03_03}}
REPAIR03-04: {{REPAIR03_04}}
REPAIR03-05: {{REPAIR03_05}}
REPAIR03-06: {{REPAIR03_06}}
REPAIR03-07: {{REPAIR03_07}}
REPAIR03-08: {{REPAIR03_08}}
REPAIR03-09: {{REPAIR03_09}}
REPAIR03-10: {{REPAIR03_10}}
```

```
REPAIR03-10_SOURCE: {{REPAIR03_10B}}
REPAIR09-05_SOURCE: {{REPAIR09_05C}}
```

REPAIR03-02 的实测结果值得记下来：伪域名/公网/198.18.0.1/APIPA/Hyper-V 这些 Host 由**既有的**
`HostValidationMiddleware` 在路由前以 400 `HOST_NOT_ALLOWED` 拒绝，而 `localhost` 通过了白名单、
由本轮的探针校验以 404 拒绝 —— 两层互补，结论一致：什么都没被标记。

REPAIR_01 的源码闸门（`REPAIR09-05c`）本轮随之收紧：原来它把"只有一处 `Onboarding.Start()`"的范围
划到 `CompletionOf` 为止，而显式恢复路径现在确实要在同一文件里再开一次会话，因此该断言被限定在
`EnsureOnboardingStarted` 自己的方法体内，并新增了对显式路径的执行顺序要求
（停止 → 清提示 → 取消在途请求 → 写审计 → 开启新会话）。这不是放宽：它多断言了一整条顺序。

### 19.3 本轮必须列出的状态

```
FINAL_HEAD:      {{FINAL_HEAD}}
WORKTREE_STATUS: {{WORKTREE_STATUS}}
PRODUCT_VERSION: {{PRODUCT_VERSION}}
FULL_REGRESSION: {{FULL_REGRESSION}}
UPDATE_SUCCESS:  {{UPDATE_SUCCESS}}
UPDATE_ROLLBACK: {{UPDATE_ROLLBACK}}
```

本轮产物（1.6.3）：

| 产物 | bytes | sha256 |
|---|---|---|
| `LanDisk-Setup.exe` | {{SETUP_BYTES}} | `{{SETUP_SHA256}}` |
| `LanDisk-win-x64.zip` | {{RELEASE_BYTES}} | `{{RELEASE_SHA256}}` |
| `{{UPDATE_NAME}}` | {{UPDATE_BYTES}} | `{{UPDATE_SHA256}}` |
| `LanDisk-source.zip` | {{SOURCE_BYTES}} | `{{SOURCE_SHA256}}` |
| `LAN_DISK_NATIVE_PARITY_NETWORK_UX_HOTFIX_09_REPAIR_03_REVIEW_BUNDLE.zip` | 见同目录 sidecar | 见同目录 sidecar |
| `LAN_DISK_09_REPAIR_03_AUDIT_EXPORT.md` | {{AUDIT_EXPORT_BYTES}} | {{AUDIT_EXPORT_SHA256}} |

### 19.4 明确未改动

verified-host 的排序算法、`PhysicalLanSelector` 分类规则、mDNS、根 CA 与 leaf 签发架构、端口语义
（8891/8892/8890/5353）、WPF 界面设计、主题、设备与配对的共享语义、回收站、文件核心、上传/下载、
`PathResolver` / 卷身份、更新签名与回滚、Setup 主架构全部未改动；没有新增任何 route / DNS / VPN /
网卡配置写入，也没有要求用户关闭 VPN。
