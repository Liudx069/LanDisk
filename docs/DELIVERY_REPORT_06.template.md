# LAN_DISK_INSTALL_MANIFEST_FINAL_FIX_06 DELIVERY REPORT

> 本文件由 `tools/Generate-DeliveryReport.ps1` 在 **FINAL_HEAD 锁定之后**生成到 `dist/delivery/`。
> `dist/` 被 `.gitignore` 忽略，因此生成报告**不会改变源码 HEAD**（PKG05-01 建立的机制在本轮继续有效）。

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

## 1. 唯一修复

```
ROOT_CAUSE:                                {{ROOT_CAUSE}}
BUILD_INFO_GENERATED_BEFORE_INSTALL_MANIFEST: {{BUILD_INFO_GENERATED_BEFORE_INSTALL_MANIFEST}}
INSTALL_MANIFEST_CONTAINS_BUILD_INFO:      {{INSTALL_MANIFEST_CONTAINS_BUILD_INFO}}
BUILD_INFO_BYTES_MATCH:                    {{BUILD_INFO_BYTES_MATCH}}
BUILD_INFO_SHA256_MATCH:                   {{BUILD_INFO_SHA256_MATCH}}
```

修复前（05 产物，直接读自 `LanDisk-win-x64.zip`）：

```
LanDisk-win-x64/build-info.json        存在
LanDisk-win-x64/install-manifest.json  files[] 不含 build-info.json
```

修复后（本候选）：

```
install-manifest.files 收录 build-info.json: {{INSTALL_MANIFEST_LISTS_BUILD_INFO}}
install-manifest.buildInfo.path:             {{BUILD_INFO_RECORDED_PATH}}
记录 bytes  = {{BUILD_INFO_RECORDED_BYTES}}   （release ZIP 内真实 bytes  = {{BUILD_INFO_REAL_BYTES}}）
记录 sha256 = {{BUILD_INFO_RECORDED_SHA256}}
真实 sha256 = {{BUILD_INFO_REAL_SHA256}}
release ZIP 内 install-manifest.json 与发布目录逐字节一致: {{RELEASE_ZIP_MANIFEST_MATCHES}}
按明细通过 {{IMCB_PASSED}} / 失败 {{IMCB_FAILED}}（证据：`INSTALL_MANIFEST_VERIFY.json`、`RELEASE/install-manifest-verification.tsv`）
```

生成顺序（`tools/Build-All.ps1`）：

```
1) 写 build-info.json
2) 枚举发布目录的最终文件
3) 写 install-manifest.json（files + buildInfo{path,bytes,sha256}）
→ tools/Verify-InstallManifest.ps1 打开最终产物复算并比对；失败则非 0 退出，不生成正式候选
```

## 2. 候选身份

```
FINAL_HEAD:           {{FINAL_HEAD}}
RELEASE_BUILD_COMMIT: {{RELEASE_BUILD_COMMIT}}
UPDATE_BUILD_COMMIT:  {{UPDATE_BUILD_COMMIT}}
SOURCE_HEAD:          {{SOURCE_HEAD}}
IDENTITY_MATCH:       {{IDENTITY_MATCH}}
```

断言通过 {{IDENTITY_PASSED}} / 失败 {{IDENTITY_FAILED}}；逐条见 `RELEASE_CANDIDATE_IDENTITY.json`。

## 3. 回归

```
OLD_BASELINE:        {{OLD_BASELINE}}
NEW_TESTS:           {{NEW_TESTS}}
FULL_REGRESSION:     {{FULL_REGRESSION}}
UI_AUTOMATION:       {{UI_AUTOMATION}}
WINDOWS_RUNTIME:     {{WINDOWS_RUNTIME}}
UPDATE_SUCCESS:      {{UPDATE_SUCCESS}}
ROLLBACK:            {{ROLLBACK}}
SIGNATURE_VERIFY:    {{SIGNATURE_VERIFY}}
SOURCE_TREE_VERIFY:  {{SOURCE_TREE_VERIFY}}
```

### 3.1 05 已通过行为（本轮必须保持）

```
UPDATE_PAYLOAD_CONTAINS_BUILD_INFO: {{UPDATE_PAYLOAD_CONTAINS_BUILD_INFO}}
UPDATE_PAYLOAD_HAS_INSTALL_MANIFEST: {{UPDATE_PAYLOAD_HAS_INSTALL_MANIFEST}}
BUILD_INFO_AFTER_UPDATE:            {{BUILD_INFO_AFTER_UPDATE}}
BUILD_INFO_AFTER_ROLLBACK:          {{BUILD_INFO_AFTER_ROLLBACK}}
INSTALL_MANIFEST_AFTER_UPDATE:      {{INSTALL_MANIFEST_AFTER_UPDATE}}
INSTALL_MANIFEST_AFTER_ROLLBACK:    {{INSTALL_MANIFEST_AFTER_ROLLBACK}}
PORTS_PRESERVED:                    {{PORTS_PRESERVED}}
CERT_PRESERVED:                     {{CERT_PRESERVED}}
SHARES_PRESERVED:                   {{SHARES_PRESERVED}}
PAIRED_DEVICES_PRESERVED:           {{PAIRED_DEVICES_PRESERVED}}
REAL_USER_DATA_TOUCHED:             {{REAL_USER_DATA_TOUCHED}}
```

## 4. 真机

```
IPHONE_REAL_DEVICE:        {{IPHONE_REAL_DEVICE}}
PHYSICAL_REMOVABLE_DRIVE:  {{PHYSICAL_REMOVABLE_DRIVE}}
HTTPS_TRUST:               {{HTTPS_TRUST}}
```

以上三项本轮未新增实测，因此整体状态为 `READY_FOR_DEVICE_VALIDATION`，**不是** PASS。

## 5. 交付物

| 产物 | path | bytes | sha256 |
|---|---|---|---|
| release | `dist/delivery/LanDisk-win-x64.zip` | {{RELEASE_BYTES}} | `{{RELEASE_SHA256}}` |
| update | `dist/delivery/{{UPDATE_NAME}}` | {{UPDATE_BYTES}} | `{{UPDATE_SHA256}}` |
| source | `dist/delivery/LanDisk-source.zip` | {{SOURCE_BYTES}} | `{{SOURCE_SHA256}}` |
| 安装清单专项验证 | `dist/delivery/INSTALL_MANIFEST_VERIFY.json` | {{IMCB_EVIDENCE_BYTES}} | `{{IMCB_EVIDENCE_SHA256}}` |
| 候选身份 | `dist/delivery/RELEASE_CANDIDATE_IDENTITY.json` | {{IDENTITY_BYTES}} | `{{IDENTITY_SHA256}}` |
| 源码树验证 | `dist/delivery/SOURCE_TREE_VERIFY.json` | {{SOURCE_VERIFY_BYTES}} | `{{SOURCE_VERIFY_SHA256}}` |
| 测试证据 | `dist/delivery/test-evidence.tsv` | {{TEST_EVIDENCE_BYTES}} | `{{TEST_EVIDENCE_SHA256}}` |
| 交付报告 | `dist/delivery/DELIVERY_REPORT.md` | {{REPORT_BYTES}} | （生成中，不含自身哈希） |
| 审核包 | `dist/delivery/LAN_DISK_INSTALL_MANIFEST_FINAL_FIX_06_REVIEW_BUNDLE.zip` | 见 `REVIEW_BUNDLE_SHA256.txt` | 见同目录 sidecar |

ZIP 内嵌时间戳：重新打包会得到不同哈希；跨构建稳定的身份见 `SHA256SUMS.txt` 与 `ARTIFACT_HASHES.txt`。

## 6. 隐私

```
PRIVATE_KEY_IN_RELEASE:        {{PRIVATE_KEY_IN_RELEASE}}
PRIVATE_KEY_IN_SOURCE:         {{PRIVATE_KEY_IN_SOURCE}}
PRIVATE_KEY_IN_REVIEW:         {{PRIVATE_KEY_IN_REVIEW}}
REAL_PERSONAL_FILES_INCLUDED:  {{REAL_PERSONAL_FILES_INCLUDED}}
```

## 7. 审核包

`LAN_DISK_INSTALL_MANIFEST_FINAL_FIX_06_REVIEW_BUNDLE.zip`，成员与哈希见包内
`REVIEW_BUNDLE_MANIFEST.tsv` 与 `REVIEW_BUNDLE_SHA256.txt`。

包内包含：final release / final update / final source、安装清单专项验证证据、
候选身份、源码树验证、更新载荷验证、测试证据、git status 与 diff --stat、
隐私扫描结果、清单与 SHA256。

包内**不含**任何私钥、发布签名私钥、HTTPS 私钥、token、cookie、device secret 或真实个人文件。

## 8. 本轮改动清单

| 文件 | 改动 |
|---|---|
| `tools/Build-All.ps1` | 生成顺序：先写 `build-info.json`，再枚举，最后写 `install-manifest.json`；清单新增 `buildInfo` 记录；写后立即断言；调用新的硬门禁 |
| `tools/Verify-InstallManifest.ps1` | 新增：打开 release ZIP / `.lupd` / 发布目录复算并比对，输出 `INSTALL_MANIFEST_CONTAINS_BUILD_INFO` |
| `src/LanDisk.Update/UpdatePlan.cs` | `InstallManifest` 新增可选 `BuildInfo` 记录与 `InstallManifestFileRecord.TryCapture` |
| `src/LanDisk.Update/UpdateApplier.cs` | 写新安装清单前按磁盘真实字节刷新 `BuildInfo` 记录 |
| `tests/LanDisk.Tests/InstallManifestIntegrityTests.cs` | 新增 7 项专项测试（顺序成因、字段契约、升级刷新、回滚恢复、旧安装兼容） |
| `tests/LanDisk.Tests/Infrastructure/UpdateTestKit.cs` | 测试安装目录按新的生成顺序写入清单与身份记录 |
| `tools/Update-ReleaseVerify.ps1` | 新增 `T06-*`：升级与回滚后安装清单记录的 `build-info` 摘要始终与磁盘一致 |
| `tools/Generate-DeliveryReport.ps1` / `Finalize-Delivery.ps1` | 使用 06 模板与 06 证据文件 |
| `docs/BUILD.md` §8.1 | 记录生成顺序与 `buildInfo` 契约 |
| `docs/AUDIT_CORRECTIONS_06.md` | 本缺陷的成因、修法与边界说明 |

未改动：UI、认证/配对、8891/8892 端口、文件浏览/上传/下载/删除/回收站、批量 ZIP / recent 逻辑、
Windows 壳交互、防火墙、证书信任、更新包格式（`.lupd` schema 2）、版本号（仍为 {{PRODUCT_VERSION}}）。
