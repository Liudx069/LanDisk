# AUDIT_CORRECTIONS_04 — 审计问题关闭记录与报告更正

本文件对应 `LAN_DISK_RELEASE_HARDENING_04`，围绕两件事：

1. **代码审计发现**（H04-01 … H04-09）：每项给出问题、修复位置、测试与实测证据；
2. **报告更正**：本轮修正了 03 轮报告与产物不一致的表述（这里只记录与本轮直接相关的部分；
   03 轮自身的更正在 `docs/AUDIT_CORRECTIONS.md` 中）。

所有数字都来自可复核的产物，命令与路径一并给出。

---

## 一、H04 问题关闭表

### H04-01 — releaseNotes / keyId 未完整受签名保护 → 已关闭

**问题**：schema 1 的签名输入是手工拼接的少数字段，`keyId` 与更新说明**不在**被签名的内容里。
后果是：攻击者可以保留一个有效的签名，同时把签名者标识换成别人、把更新说明换成任意文本
（例如把"安全修复"换成"此更新会删除你的数据"），而校验依然通过。

**修复**：

| 位置 | 内容 |
|---|---|
| `src/LanDisk.Update/UpdateManifest.cs` | schema 2：新增 `KeyId`、`ReleaseNotesEntry`、`ReleaseNotesSha256`、`ReleaseNotesBytes`、`PayloadDigest`、`BuildId`；`MinimumFromVersion` 更名 `MinimumUpgradeVersion` |
| `src/LanDisk.Update/UpdateSigning.cs` | `ComputeSignedPayload` 改为输出**规范 JSON**：固定字段顺序、无多余空白、字符串按 ASCII 转义（非 ASCII 一律 `\uXXXX`，避免依赖编码器行为）、文件列表按路径排序。手写 writer，字节布局不随序列化库版本漂移 |
| `src/LanDisk.Update/UpdatePackage.cs` | 校验顺序改为「格式版本 → 签名者标识与签名 → 其余策略」；新增 `ReadReleaseNotes` 用签名清单里的哈希验证更新说明；新增 `RESERVED_PATH`、`PAYLOAD_DIGEST_MISMATCH`、`LEGACY_SCHEMA_UNSUPPORTED` 等错误码 |
| `tools/keytool/Program.cs` | 打包时把更新说明写入包内 `release-notes.md` 并计算其哈希；把 `keyId`、`payloadDigest` 写入签名清单 |

**测试**（`tests/LanDisk.Tests/SignedSemanticsTests.cs`，21 个用例）：

| 用例 | 断言 |
|---|---|
| `Changing_the_release_notes_invalidates_the_signature` | 改 releaseNotes 哈希 → `SIGNATURE_INVALID` |
| `Release_notes_text_that_does_not_match_the_signed_hash_is_refused` | 包内说明文本与签名哈希不符 → `RELEASE_NOTES_MISMATCH` |
| `A_package_without_the_declared_release_notes_is_refused` | 缺少说明 → `RELEASE_NOTES_MISSING` |
| `Changing_the_key_id_invalidates_the_signature` | 改 keyId → 拒绝（`UNKNOWN_KEY_ID`，因为 keyId 先于签名被核对） |
| `An_unknown_key_id_is_refused_with_a_specific_reason` | 陌生密钥签名的包 → `UNKNOWN_KEY_ID`，消息里列出两个标识 |
| `Changing_the_build_commit_invalidates_the_signature` | 改 buildCommit → `SIGNATURE_INVALID` |
| `Changing_the_version_invalidates_the_signature` | 改 version → `SIGNATURE_INVALID` |
| `Changing_a_payload_file_hash_invalidates_the_signature` | 改文件哈希 → `SIGNATURE_INVALID` |
| `Changing_the_minimum_upgrade_version_invalidates_the_signature` | 改最低可升级版本 → `SIGNATURE_INVALID` |
| `Changing_the_architecture_invalidates_the_signature` | 改架构 → `SIGNATURE_INVALID` |
| `Changing_the_product_invalidates_the_signature` | 改产品名 → `SIGNATURE_INVALID` |
| `A_payload_digest_that_does_not_match_the_file_list_is_refused` | 内部不自洽的清单 → `PAYLOAD_DIGEST_MISMATCH` |
| `Reformatting_the_manifest_json_does_not_change_what_was_signed` | **仅重排键顺序 + 重新缩进** → 仍通过校验，且包内 JSON 字节确实不同 |
| `The_canonical_payload_is_valid_json_and_stable_across_calls` | 规范载荷是合法 JSON、纯 ASCII、可重复，且包含全部受保护字段名 |
| `The_file_list_is_sorted_so_ordering_cannot_change_the_signature` | 文件顺序不影响签名与 digest |
| `A_schema_1_package_is_refused_with_an_explanation_about_its_signature` | 旧格式 → `LEGACY_SCHEMA_UNSUPPORTED`，消息说明原因 |
| `An_unknown_future_schema_is_refused` | 未知格式版本 → `SCHEMA_UNSUPPORTED` |
| `A_manifest_without_a_build_commit_is_refused` | 缺 buildCommit → `BUILD_COMMIT_MISSING` |
| `A_manifest_without_a_key_id_is_refused` | 缺 keyId → `KEY_ID_MISSING` |
| `A_signature_over_a_manifest_the_signer_never_saw_is_refused` | 签名与清单不配对 → `SIGNATURE_INVALID` |
| `A_signature_that_is_not_base64_is_refused_with_a_clear_code` | 签名不是 Base64 → `SIGNATURE_MALFORMED` |
| `An_unsupported_signature_algorithm_is_refused` | 算法不符 → `ALGORITHM_UNSUPPORTED` |

**实机证据**：`T04-01`（更新包格式版本 2、签名覆盖 keyId 与说明、buildCommit 等于最终 HEAD）、
`T04-05`（更新历史记录 packageSha256）。证据文件 `evidence/update-verify.json`。

**结果**：关闭。schema 1 被**明确拒绝**——它的签名没有覆盖这两项，因此不能"升级成可信"。

---

### H04-02 — buildCommit 与最终 HEAD 不一致 → 已关闭

**问题**：03 轮的 1.2.0 包在构建时 HEAD 已经前进，包内 `buildCommit` 指向旧提交，
报告却按"同一候选"叙述。

**修复**：

| 位置 | 内容 |
|---|---|
| `Directory.Build.props`（新增） | 用 `-p:LanDiskBuildCommit=<sha>` 把提交号作为 `AssemblyMetadataAttribute` 注入**每个**程序集；未提供时不注入，开发构建不会谎称自己是候选 |
| `src/LanDisk.Core/Configuration/BuildIdentity.cs`（新增） | 读取内嵌提交号/构建标识/版本/构建时间 |
| `src/LanDisk.Server/Endpoints/AdminEndpoints.cs` | `/api/admin/status` 与 `/api/admin/health` 输出 `buildCommit`、`buildId`（字段始终存在，即使是空的） |
| `tools/Build-All.ps1` | 三次发布都带 `-p:LanDiskBuildCommit`；写出 `build-info.json`；源码包内写 `SOURCE_HEAD.txt`；工作树不干净时警告 |
| `tools/Verify-CandidateIdentity.ps1`（新增） | 把"同一候选"变成 11 条可执行断言 |

**测试**：`HardeningMiscTests.BuildIdentity_reports_an_empty_commit_for_a_development_build`、
`The_status_endpoint_exposes_the_build_commit_field_the_verifier_reads`。

**实测**（`dist/delivery/RELEASE_CANDIDATE_IDENTITY.json`，11/11 PASS）：

```
CAND-01 工作树干净，HEAD = b61cad5f04c54d5d923867799147c0fa87a623c1
CAND-02 release build-info.json commit = b61cad5...
CAND-05 release 程序集内嵌 LanDiskBuildCommit = b61cad5...
CAND-06 update manifest buildCommit = b61cad5...
CAND-09 update payload 与 release 目录文件集合一致（380 个，0 差异）
CAND-10 update payload 逐文件哈希与签名清单一致（不符 0 条）
CAND-11 源码包内 SOURCE_HEAD.txt commit = b61cad5...
```

**结果**：关闭。四处的候选身份由脚本断言，而不是由报告叙述。

---

### H04-03 — UI 线程同步等待异步停止 → 已关闭

**问题**：`TrayContext.Shutdown()` 在 UI 线程上调用 `_server.StopAsync().GetAwaiter().GetResult()`，
Kestrel 停止稍慢就会让界面看起来卡死。

**修复**（`src/LanDisk.App/TrayContext.cs`）：`BeginShutdown()` 改为 `async void` 编排——
UI 线程发起后立即回到消息循环，`await` 在 WinForms 同步上下文上继续，等停止（或硬超时）完成后
才释放托盘/图标/宿主并 `ExitThread()`。`UpdatePaths` 另加 `ServerController.StopTimeout = 20s`，
超时得到 `StopFailed` 状态与可读原因，**不会强杀进程**。

**结果**：关闭。代码路径上不存在 UI 线程同步等待；`ServerController.Dispose()` 仍保留同步兜底，
但它只在宿主**已经停止之后**被调用（正常路径下 `_app` 已为 null，立即返回）。

---

### H04-04 — server"假运行"与端口冲突 → 已关闭

**问题**：只要 `Task` 起来了就显示"运行中"；端口被占用时报一个通用的启动失败。

**修复**（`src/LanDisk.App/ServerController.cs`）：

- 引入 `ServerState`：`Idle / Starting / Running / Stopping / Stopped / StopFailed / Failed / PortConflict`；
- **启动前逐一预检端口**：被占用时直接进入 `PortConflict` 并给出端口号，**不启动宿主、不改端口**；
- 用 `app.StartAsync()` 而不是 `RunAsync()`，随后用 `/api/health` 做**回环就绪探测**（最多 30 秒）；
  没有通过就停止并进入 `Failed`，绝不显示 `Running`；
- 绑定异常按类型映射为 `PORT_IN_USE` / `CERTIFICATE_ERROR` / `PERMISSION_DENIED` / `START_FAILED`。

**结果**：关闭。状态与实际可达性一致。

---

### H04-05 — health check 不够严格 → 已关闭

**问题**：自检只说"进程活着"，无法证明"还是原来那套安装"。

**修复**：`HealthReport`（`src/LanDisk.Server/Endpoints/UpdateEndpoints.cs`）新增
product、version、buildCommit、buildId、**生效的** adminPort / lanPort（配置为 0 时从已绑定地址解析）、
证书指纹（只要存在证书就报告，与 HTTPS 是否启用无关）、共享库与配对设备库的加载状态/数量/哈希、
以及**隔离目录浏览自检**（在临时目录上走真实的 `PathResolver` + `DirectoryBrowser`，
不读取任何用户数据）。

`UpdateService.CaptureSnapshot` / `PreUpdateSnapshot` 在**应用更新前**把上述事实写成
`updates\pre-update-health.json`；`UpdateHealthExpectation` + `UpdateHealth.Evaluate` 在重启后逐项比对，
汇总所有不一致项。任何一项不通过 → 不 commit，直接回滚。

**测试**：`HealthAndRollbackHardeningTests` 中 14 个自检用例（版本/提交/端口/证书/两个状态库/
隔离浏览/dataRoot/ok=false/非 JSON/回滚期望/快照字段），加两个实机接口用例。

**结果**：关闭。健康检查从"能连上"升级为"是同一套安装且状态可用"。

---

### H04-06 — update history packageSha256 缺失 → 已关闭

**修复**：

- `UpdateService.StageAsync` 记录 `PendingPackageSha256` 与 `PendingPayloadDigest`，
  并写入一条 `Staged` 历史；
- 被拒绝的上传写入 `Rejected` 历史：**只要字节可读就记下 SHA256**；确实无法计算时
  （超过上限、内容为空）在消息里写明原因，而不是留下无法解释的 null；
- `RequestApply` 把 `--package-sha256` 与 `--expect-commit` 传给更新程序，并写 `Applying` 历史；
- 更新程序在 `Committed` / `RolledBack` / `RecoveryRequired` 历史中都带上同一个包哈希与 payload digest；
- 申请安装被拒（缺 updater、已在更新中、暂存校验失败、快照写入失败、拉起失败）也写 `REFUSED`/`FAILED` 历史。

**测试**：`A_successful_update_records_the_package_hash_in_the_history`、
`A_rejected_package_records_its_hash_when_the_bytes_were_readable`、
`A_rejection_that_never_read_the_bytes_says_why_there_is_no_hash`、
`An_apply_refusal_also_keeps_the_hash_and_digest`。

**实机证据**：`T04-05`（`Committed` 历史含 64 位 packageSha256）。

**结果**：关闭。

---

### H04-07 — rollback 未清理新增受管状态文件 → 已关闭

**修复**：

- `UpdateBackupScope.Enumerate/IsManaged` 统一"受管状态"的定义（settings/shares/devices/
  recycle-index/transfers/favorites/preferences/**shell.json**/admin-url.txt + `secrets/` + `tls/`；
  **不含** `updates/`、`thumbs/`、`logs`、`store`）；
- `UpdateApplier.Backup` 写出 `state-snapshot.json`（逐文件 bytes + sha256），并在
  `updates\pre-update-state.json` 留一份镜像；
- `Rollback` 先**删除失败版本新增的受管状态文件**，再恢复备份内容，最后**逐文件复核**是否与快照一致，
  并把不一致写进 `StateMismatches`；
- 回滚后旧版本自检不通过时记为 `RECOVERY_REQUIRED`，**保留**备份与暂存目录，不声称回滚成功。

**测试**：`Rollback_removes_managed_state_files_the_failed_version_added`、
`Rollback_never_touches_files_outside_the_managed_scope`、
`Rollback_reports_a_mismatch_instead_of_claiming_success`、
`A_rollback_without_a_snapshot_is_reported_as_unverifiable`、
`The_managed_state_scope_covers_the_files_a_version_may_add`。

**实机证据**：`T04-06`（回滚后受管状态目录无残留、构建提交回到升级前）、`T04-07`（`RolledBack`）。

**结果**：关闭。用户真实共享内容始终不在受管范围内，测试用"用户自己放的文件/缓存/日志/updates"
四类反例验证了这一点。

---

### H04-08 — LAN 地址展示 → 已关闭

**修复**（`src/LanDisk.Core/Networking/LanInfo.cs`）：新增 `LanAddressCandidate` 与
`RankForDevices()`：排除 loopback 与 `0.0.0.0`；私有地址优先；`vEthernet/Hyper-V/WSL/Docker/
VirtualBox/VMware/VPN/Bluetooth/Default Switch` 等虚拟网卡**排在最后并给出原因**；
APIPA（169.254.x.x）额外降权；多网卡时按 rank 排序给出可理解的选择。
桌面外壳改为列表展示（可复制），配对链接按同一顺序生成。
**只影响展示，不修改任何绑定配置**。

**测试**：`Loopback_is_never_offered_as_a_phone_address`、
`Virtual_adapters_are_ranked_after_physical_private_addresses`、
`Ranking_never_changes_the_configured_bind_address`、`Every_candidate_carries_a_reason_and_a_usable_url`、
`An_address_filter_restricts_the_list_to_the_bound_addresses`。

**结果**：关闭。

---

### H04-09 — 首次最小化到托盘提示不可见 → 已关闭

**问题**：提示写进了**即将被隐藏**的窗口里的记录框，用户永远看不到。

**修复**：

- 新增 `src/LanDisk.App/TrayNoticeDialog.cs`：一个真正可见的对话框，文字说明"仍在后台运行，
  手机依然可以访问"，提供「知道了」与「退出 LanDisk」，并有**不再提示**复选框；
- `MainForm.HideToTray` 在隐藏**之前**触发对话框，隐藏后额外弹一次托盘气泡；
- 选择保存在 `shell.json`（`ShellPreferences`，已移入 `LanDisk.Core` 以便测试），
  该文件属于受管状态，因此升级与回滚都会保留。

**测试**：`The_tray_notice_preference_round_trips`、
`A_damaged_preference_file_falls_back_to_defaults_instead_of_crashing`、
`The_shell_preference_file_is_part_of_the_managed_state_scope`。

**未自动化部分**：对话框的实际视觉效果与托盘菜单逐项点击需要人工操作桌面，
在报告第 12 节记为未自动化。

**结果**：关闭（代码路径与持久化已测试；纯 UI 外观未自动化）。

---

## 二、上一轮报告的更正（本轮直接相关部分）

| # | 上一轮报告/文档的表述 | 实测 | 更正 |
|---|---|---|---|
| 1 | 更新包签名"覆盖 manifest 中所有安全/用户可见语义" | schema 1 的签名输入**不含** `keyId` 与 `releaseNotes` | 本轮改为 schema 2 并逐字段测试；旧格式明确拒绝 |
| 2 | `buildCommit` 与最终 HEAD 一致 | 03 轮包内 `buildCommit` 指向构建时刻的旧 HEAD | 本轮把提交号注入程序集 + `build-info.json` + 清单，并用 11 条断言核对 |
| 3 | 更新历史记录记录了更新包哈希 | `RollbackAndRestart` 的历史条目 `packageSha256` 为 `null`；`--package-sha256` 从未真正传给更新程序 | 本轮全链路打通，并对"无法计算哈希"的情况写明原因 |
| 4 | 回滚恢复受管状态 | 只恢复备份里有的文件，**不删除**失败版本新增的状态文件 | 本轮加入状态快照 + 新增文件清理 + 逐字节复核 |
| 5 | 首次最小化提示"可见" | 提示写在即将隐藏的窗口内 | 本轮改为真正可见的对话框并记住偏好 |
| 6 | 自检"全部通过" | 自检只有 8 项、不含版本/提交/端口/证书/状态库/浏览 | 本轮扩展到 11 项，并区分 error 与 warn |

---

## 三、本轮自身的自查

为避免再次出现"叙述与产物脱节"，本轮所有数字都来自可复核产物：

| 数字 | 产物 / 命令 |
|---|---|
| 387 个单元测试 | `dotnet test tests/LanDisk.Tests/LanDisk.Tests.csproj -c Release` |
| 逐类用例数 | `dotnet test ... --list-tests` 展开后的用例数 |
| 11 项候选身份 | `tools/Verify-CandidateIdentity.ps1` → `RELEASE_CANDIDATE_IDENTITY.json` |
| 升级/回滚结论 | `tools/Update-ReleaseVerify.ps1` → `.rcverify/<时间戳>/update-verify.json` |
| 发布包冒烟 | `.releasecheck/<时间戳>/release-smoke.json` |
| 界面自动化 | `dist/e2e-ui-chromium.json`、`dist/e2e-ui-webkit.json` |
| 隐私扫描 | `dist/privacy-check.json` |

存在真机 `NOT_RUN` 时，总体结论不写 `PASS`。
