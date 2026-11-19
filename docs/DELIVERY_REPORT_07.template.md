# LAN_DISK_SETUP_FIRST_RUN_UX_07 DELIVERY REPORT

> 本文件由 `tools/Generate-DeliveryReport.ps1` 在 **FINAL_HEAD 锁定之后**生成到 `dist/delivery/`。
> `dist/` 被 `.gitignore` 忽略，因此生成报告**不会改变源码 HEAD**（05 轮建立的机制继续有效）。

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

## 1. Setup

```
SETUP_EXE:        {{SETUP_EXE}}
SETUP_BYTES:      {{SETUP_BYTES}}
SETUP_SHA256:     {{SETUP_SHA256}}
EMBEDDED_PAYLOAD_SHA256: {{EMBEDDED_PAYLOAD_SHA256}}
PAYLOAD_MATCHES_RELEASE: {{PAYLOAD_MATCHES_RELEASE}}
INSTALL_PATH:     {{INSTALL_PATH}}
DATA_PATH:        {{DATA_PATH}}
AUTHENTICODE:     {{AUTHENTICODE}}
SMARTSCREEN_NOTE: {{SMARTSCREEN_NOTE}}
```

安装器内嵌的就是被测试过的 `LanDisk-win-x64.zip`，并在程序集元数据里记录它的 SHA256；
写入任何字节之前先核对。上面两行哈希相等即说明"装上去的东西就是被测试过的东西"。

## 2. Firewall

```
UAC_MODEL:            {{UAC_MODEL}}
RULES_CREATED:        {{RULES_CREATED}}
PRIVATE_ONLY:         {{PRIVATE_ONLY}}
LOCALSUBNET_ONLY:     {{LOCALSUBNET_ONLY}}
PORT_8892:            {{PORT_8892}}
PORT_8890:            {{PORT_8890}}
PORT_8891_EXPOSED:    {{PORT_8891_EXPOSED}}
MDNS_RULE:            {{MDNS_RULE}}
PUBLIC_PROFILE_RULE:  {{PUBLIC_PROFILE_RULE}}
UNINSTALL_CLEANUP:    {{UNINSTALL_CLEANUP}}
FIREWALL_RUNTIME:     {{FIREWALL_RUNTIME}}
```

规则计划可离线审计：`LanDisk-Setup.exe --print-firewall-plan` 不需要管理员权限，
输出全部 `netsh` 参数与不变量检查结果。提权助手只接受两个固定动作与默认安装目录。

## 3. Certificate architecture

```
ROOT_CA_CREATED:               {{ROOT_CA_CREATED}}
ROOT_CA_ALGORITHM:             {{ROOT_CA_ALGORITHM}}
ROOT_CA_SUBJECT:               {{ROOT_CA_SUBJECT}}
ROOT_CA_FINGERPRINT:           {{ROOT_CA_FINGERPRINT}}
ROOT_CA_STORAGE:               {{ROOT_CA_STORAGE}}
ROOT_CA_PRIVATE_KEY_EXPORTABLE: {{ROOT_CA_PRIVATE_KEY_EXPORTABLE}}
ROOT_CA_PERSISTS_RESTART:      {{ROOT_CA_PERSISTS_RESTART}}
ROOT_CA_PERSISTS_UPDATE:       {{ROOT_CA_PERSISTS_UPDATE}}
LEAF_SUBJECT:                  {{LEAF_SUBJECT}}
LEAF_SAN:                      {{LEAF_SAN}}
LEAF_AUTO_REISSUE:             {{LEAF_AUTO_REISSUE}}
IP_CHANGE_RETRUST_REQUIRED:    {{IP_CHANGE_RETRUST_REQUIRED}}
CA_RESET_BEHAVIOR:             {{CA_RESET_BEHAVIOR}}
```

上面的指纹取自**本次候选真实运行**产生的那个安装目录（`.rcverify/<时间戳>/data/tls/`），
不是示例值。根 CA 每台机器不同，但"根不变、leaf 可变"这条性质对每次安装都成立。

## 4. Stable local hostname

```
HOSTNAME:               {{HOSTNAME}}
PERSISTENCE:            {{PERSISTENCE}}
MDNS_IMPLEMENTATION:    {{MDNS_IMPLEMENTATION}}
THIRD_PARTY_DEPENDENCY: {{THIRD_PARTY_DEPENDENCY}}
NO_BONJOUR_REQUIRED:    {{NO_BONJOUR_REQUIRED}}
COLLISION_HANDLING:     {{COLLISION_HANDLING}}
IP_CHANGE_RESOLUTION:   {{IP_CHANGE_RESOLUTION}}
```

## 5. iPhone onboarding

```
BOOTSTRAP_PORT:          {{BOOTSTRAP_PORT}}
BOOTSTRAP_HTTP_SCOPE:    {{BOOTSTRAP_HTTP_SCOPE}}
ROOT_CA_PROFILE_OR_CER:  {{ROOT_CA_PROFILE_OR_CER}}
HTTPS_TRUST_CHECK:       {{HTTPS_TRUST_CHECK}}
PAIRING_AFTER_HTTPS:     {{PAIRING_AFTER_HTTPS}}
HTTP_SECRET_SCAN:        {{HTTP_SECRET_SCAN}}
MANUAL_IOS_TRUST_STEP:   {{MANUAL_IOS_TRUST_STEP}}
```

## 6. Migration（从手工安装的 1.3.0 接管）

```
MANUAL_1_3_INSTALL_DETECTED: {{MANUAL_1_3_INSTALL_DETECTED}}
CONFIG_PRESERVED:            {{CONFIG_PRESERVED}}
SHARES_PRESERVED:            {{SHARES_PRESERVED}}
PAIRED_DEVICES_PRESERVED:    {{PAIRED_DEVICES_PRESERVED}}
PORTS_PRESERVED:             {{PORTS_PRESERVED}}
OLD_CERT_MIGRATION:          {{OLD_CERT_MIGRATION}}
DUPLICATE_INSTALL_CREATED:   {{DUPLICATE_INSTALL_CREATED}}
REAL_USER_DATA_TOUCHED:      {{REAL_USER_DATA_TOUCHED}}
```

## 7. Uninstall

```
REGISTERED:            {{REGISTERED}}
DATA_PRESERVED_DEFAULT: {{DATA_PRESERVED_DEFAULT}}
OPTIONAL_DATA_DELETE:  {{OPTIONAL_DATA_DELETE}}
REAL_SHARE_FILES_SAFE: {{REAL_SHARE_FILES_SAFE}}
```

## 8. Tests

```
OLD_BASELINE:              {{OLD_BASELINE}}
NEW_TESTS:                 {{NEW_TESTS}}
FULL_REGRESSION:           {{FULL_REGRESSION}}
SETUP_CLEAN:               {{SETUP_CLEAN}}
SETUP_REPAIR:              {{SETUP_REPAIR}}
SETUP_TAKEOVER:            {{SETUP_TAKEOVER}}
SETUP_SINGLE_INSTANCE:     {{SETUP_SINGLE_INSTANCE}}
SETUP_INTERRUPTION:        {{SETUP_INTERRUPTION}}
FIREWALL_PLAN:             {{FIREWALL_PLAN}}
CA_LIFECYCLE:              {{CA_LIFECYCLE}}
MDNS:                      {{MDNS}}
IP_CHANGE_SESSION:         {{IP_CHANGE_SESSION}}
UI_AUTOMATION:             {{UI_AUTOMATION}}
WINDOWS_RUNTIME:           {{WINDOWS_RUNTIME}}
UPDATE_ROLLBACK:           {{UPDATE_ROLLBACK}}
UPDATE_SUCCESS:            {{UPDATE_SUCCESS}}
SIGNATURE_VERIFY:          {{SIGNATURE_VERIFY}}
SOURCE_TREE_VERIFY:        {{SOURCE_TREE_VERIFY}}
IDENTITY_MATCH:            {{IDENTITY_MATCH}}
IPHONE_REAL_DEVICE:        {{IPHONE_REAL_DEVICE}}
PHYSICAL_REMOVABLE_DRIVE:  {{PHYSICAL_REMOVABLE_DRIVE}}
HTTPS_TRUST_REAL_IPHONE:   {{HTTPS_TRUST_REAL_IPHONE}}
```

自动化安装验收证据：`evidence/setup-acceptance.json`；逐条结果见
`RELEASE/setup-acceptance.tsv`。
`IP_CHANGE_SESSION` 的说明：地址变化后仍要求重新配对的那条路径无法在没有真实 iPhone 的情况下
端到端证明，本轮覆盖的是"根 CA 不变 + leaf 自动重签 + 稳定名称 origin 不变"这三条前提，
因此该行按证据如实标注，而不是写成 PASS。

## 9. User experience（普通用户实际要做的步骤）

Windows 侧，一次安装：

1. 双击 `LanDisk-Setup.exe`；
2. 同意一次 UAC（用途写明是防火墙规则）；
3. 等安装完成，LanDisk 自动启动；
4. 点「连接 iPhone」。

iPhone 侧，一次信任 + 一次配对：

5. 扫描电脑上的第一个二维码，Safari 打开引导页；
6. 点「安装 LanDisk 安全证书」，在 设置 → 通用 → VPN与设备管理 中安装；
7. 在 设置 → 通用 → 关于本机 → 证书信任设置 中打开对 `LanDisk Local CA` 的完全信任；
8. 回到 Safari 点「继续」（页面会确认 HTTPS 已可用）；
9. 扫描电脑上的**第二个**二维码完成配对。

之后：iPhone 打开书签即自动登录。IP 变化、证书续期、LanDisk 升级都不需要重复以上任何一步。

## 10. Artifacts

| 产物 | path | bytes | sha256 |
|---|---|---|---|
| setup | `dist/delivery/LanDisk-Setup.exe` | {{SETUP_BYTES}} | `{{SETUP_SHA256}}` |
| release | `dist/delivery/LanDisk-win-x64.zip` | {{RELEASE_BYTES}} | `{{RELEASE_SHA256}}` |
| update | `dist/delivery/{{UPDATE_NAME}}` | {{UPDATE_BYTES}} | `{{UPDATE_SHA256}}` |
| source | `dist/delivery/LanDisk-source.zip` | {{SOURCE_BYTES}} | `{{SOURCE_SHA256}}` |
| setup 信息 | `dist/delivery/setup-info.json` | {{SETUP_INFO_BYTES}} | `{{SETUP_INFO_SHA256}}` |
| 安装清单专项验证 | `dist/delivery/INSTALL_MANIFEST_VERIFY.json` | {{IMCB_EVIDENCE_BYTES}} | `{{IMCB_EVIDENCE_SHA256}}` |
| 候选身份 | `dist/delivery/RELEASE_CANDIDATE_IDENTITY.json` | {{IDENTITY_BYTES}} | `{{IDENTITY_SHA256}}` |
| 源码树验证 | `dist/delivery/SOURCE_TREE_VERIFY.json` | {{SOURCE_VERIFY_BYTES}} | `{{SOURCE_VERIFY_SHA256}}` |
| 测试证据 | `dist/delivery/test-evidence.tsv` | {{TEST_EVIDENCE_BYTES}} | `{{TEST_EVIDENCE_SHA256}}` |
| 交付报告 | `dist/delivery/DELIVERY_REPORT.md` | {{REPORT_BYTES}} | （本文件，不含自身哈希） |
| 审核包 | `dist/delivery/LAN_DISK_SETUP_FIRST_RUN_UX_07_REVIEW_BUNDLE.zip` | 见 `REVIEW_BUNDLE_SHA256.txt` | 见同目录 sidecar |

ZIP 与 EXE 内嵌时间戳：重新打包会得到不同哈希；跨构建稳定的身份见 `SHA256SUMS.txt` 与 `ARTIFACT_HASHES.txt`。

## 11. Privacy / security

```
ROOT_CA_PRIVATE_KEY_IN_ARTIFACTS:        {{ROOT_CA_PRIVATE_KEY_IN_ARTIFACTS}}
RELEASE_SIGNING_PRIVATE_KEY_IN_ARTIFACTS: {{RELEASE_SIGNING_PRIVATE_KEY_IN_ARTIFACTS}}
TOKENS_OR_COOKIES_IN_ARTIFACTS:          {{TOKENS_OR_COOKIES_IN_ARTIFACTS}}
REAL_PERSONAL_FILES_INCLUDED:            {{REAL_PERSONAL_FILES_INCLUDED}}
```

## 12. 本轮改动清单

见 `AUDIT_CORRECTIONS_07.md`。摘要：新增 `LanDisk-Setup.exe`（`src/LanDisk.Setup/`）、
本地 CA 证书体系、自实现 mDNS responder、8890 首次连接引导、管理页「连接 iPhone」向导；
版本号 1.3.0 → 1.4.0。

未改动：文件浏览/搜索/排序、查看/播放/下载、>4GiB 上传/续传、重命名/删除/回收站、批量 ZIP、
recent/favorites、PathResolver/卷身份/路径安全、设备权限模型、更新签名与回滚模型、8891/8892 语义。
