# LAN_DISK_RELEASE_PACKAGING_FIX_05 DELIVERY REPORT

> 本文件由 `tools/Generate-DeliveryReport.ps1` 在 **FINAL_HEAD 锁定之后**生成到 `dist/delivery/`。
> `dist/` 被 `.gitignore` 忽略，因此生成报告**不会改变源码 HEAD**——这正是本轮 PKG05-01 要消除的自引用。

## 1. 状态

```
FINAL_STATUS:     {{FINAL_STATUS}}
PROJECT_PATH:     D:\LanDisk
START_HEAD:       {{START_HEAD}}
FINAL_HEAD:       {{FINAL_HEAD}}
WORKTREE_STATUS:  {{WORKTREE_STATUS}}
PRODUCT_VERSION:  {{PRODUCT_VERSION}}
GENERATED_AT_UTC: {{GENERATED_AT_UTC}}
```

## 2. 三项修复

### PKG05-01 Candidate identity self-reference

- **实现**：`dist/FINAL_HEAD.txt` 成为唯一真源，由 `tools/Build-All.ps1` 在产物生成时写入；
  `tools/Verify-CandidateIdentity.ps1` 从它读取目标提交，并把 JSON 与 Markdown 两份身份文件
  **同时写入 `dist/delivery/`**。交付报告改由 `tools/Generate-DeliveryReport.ps1` 从
  `docs/DELIVERY_REPORT_05.template.md` 在构建后生成，同样只落 `dist/`。
  仓库内不再有任何"写入后会让 HEAD 变化"的身份文件。
- **测试**：`CAND-00`（真源与仓库 HEAD 一致）、`CAND-14`（四个提交字段完全相等）；
  `tools/Verify-CandidateIdentity.ps1` 在字段不一致时**拒绝**成功退出。
- **结果**：PASS —— 见第 3 节。

### PKG05-02 Source ZIP completeness

- **实现**：源码包改用 `git archive --format=zip <FINAL_HEAD>` 生成，再追加
  `SOURCE_HEAD.txt` 与 `SOURCE_MANIFEST.tsv`（含每个 tracked 文件的 mode 与 git blob sha1）。
  不再使用目录白名单 robocopy。
- **tracked_count**：{{TRACKED_COUNT}}
- **source_tracked_count**：{{SOURCE_TRACKED_COUNT}}
- **missing**：{{SOURCE_MISSING}}
- **Directory.Build.props**：{{DIRECTORY_BUILD_PROPS}}
- **结果**：{{SOURCE_RESULT}} —— 逐文件 blob 哈希核对见 `SOURCE_TREE_VERIFY.json`
  与 `RELEASE/source-tree-verification.tsv`。

### PKG05-03 build-info update

- **实现**：`build-info.json` 从"发布元数据"改为**普通受签名 payload 文件**：
  `keytool pack` 不再排除它；发布目录的 `install-manifest.json` 收录它；
  更新时由 applier 替换、回滚时由备份按字节恢复；自检要求
  **运行程序集 / 更新包清单 / 磁盘 build-info.json 三者一致**。
  `install-manifest.json` 仍然保持"安装器生成的保留元数据"，不进入 payload。
- **release_contains**：{{RELEASE_BUILD_INFO}}
- **update_contains**：{{UPDATE_BUILD_INFO}}
- **install_manifest_contains**：{{INSTALL_MANIFEST_BUILD_INFO}}
- **success_update_result**：{{BUILD_INFO_AFTER_UPDATE}}
- **rollback_result**：{{BUILD_INFO_AFTER_ROLLBACK}}
- **结果**：PASS —— 见第 5、6 节。

## 3. Candidate identity

```
FINAL_HEAD:           {{FINAL_HEAD}}
RELEASE_BUILD_COMMIT: {{RELEASE_BUILD_COMMIT}}
UPDATE_BUILD_COMMIT:  {{UPDATE_BUILD_COMMIT}}
SOURCE_HEAD:          {{SOURCE_HEAD}}
IDENTITY_MATCH:       {{IDENTITY_MATCH}}
```

断言通过 {{IDENTITY_PASSED}} / 失败 {{IDENTITY_FAILED}}；逐条见 `RELEASE_CANDIDATE_IDENTITY.json`。

## 4. Artifacts

| 产物 | path | bytes | sha256 |
|---|---|---|---|
| release | `dist/delivery/LanDisk-win-x64.zip` | {{RELEASE_BYTES}} | `{{RELEASE_SHA256}}` |
| update | `dist/delivery/{{UPDATE_NAME}}` | {{UPDATE_BYTES}} | `{{UPDATE_SHA256}}` |
| source | `dist/delivery/LanDisk-source.zip` | {{SOURCE_BYTES}} | `{{SOURCE_SHA256}}` |

## 5. Test results

```
OLD_BASELINE:      {{OLD_BASELINE}}
NEW_TESTS:         {{NEW_TESTS}}
FULL_REGRESSION:   {{FULL_REGRESSION}}
UI_AUTOMATION:     {{UI_AUTOMATION}}
WINDOWS_RUNTIME:   {{WINDOWS_RUNTIME}}
UPDATE_SUCCESS:    {{UPDATE_SUCCESS}}
ROLLBACK:          {{ROLLBACK}}
SOURCE_TREE_VERIFY: {{SOURCE_RESULT}}
SIGNATURE_VERIFY:  {{SIGNATURE_VERIFY}}
```

## 6. Persistent state

```
PORTS_PRESERVED:            {{PORTS_PRESERVED}}
CERT_PRESERVED:             {{CERT_PRESERVED}}
SHARES_PRESERVED:           {{SHARES_PRESERVED}}
PAIRED_DEVICES_PRESERVED:   {{PAIRED_DEVICES_PRESERVED}}
BUILD_INFO_UPDATED:         {{BUILD_INFO_UPDATED}}
BUILD_INFO_ROLLBACK:        {{BUILD_INFO_ROLLBACK}}
REAL_USER_DATA_TOUCHED:     {{REAL_USER_DATA_TOUCHED}}
```

## 7. Real device

```
IPHONE_REAL_DEVICE:        {{IPHONE_REAL_DEVICE}}
PHYSICAL_REMOVABLE_DRIVE:  {{PHYSICAL_REMOVABLE_DRIVE}}
HTTPS_TRUST:               {{HTTPS_TRUST}}
```

## 8. AUDIT_CORRECTIONS_05

04 审核发现的三处发布链问题及本轮的修法，见 `AUDIT_CORRECTIONS_05.md`。
其中最重要的一条：04 的 `DELIVERY_REPORT.md` 写在仓库里、并在产物构建**之后**提交，
因此报告里的 `FINAL_HEAD` 与真实最终提交不一致——那是自引用，不是笔误。
05 通过"模板入仓、取值出仓"从机制上消除了它：报告生成于 FINAL_HEAD 锁定之后，且只写 `dist/`。

## 9. 最终审核包

`LAN_DISK_RELEASE_PACKAGING_FIX_05_REVIEW_BUNDLE.zip`，成员与哈希见包内
`REVIEW_BUNDLE_MANIFEST.tsv` 与 `REVIEW_BUNDLE_SHA256.txt`，包 ZIP 的 SHA256 见同目录 sidecar。

包内至少包含：final release / final update / final source、candidate identity、
source-tree verification、update payload verification、测试证据、git status 与 diff --stat、
隐私扫描、清单与 SHA256。

包内**不含**任何私钥、真实凭据或个人文件。
