# AUDIT_CORRECTIONS_05 — 发布链三处问题的成因与修法

本文件对应 `LAN_DISK_RELEASE_PACKAGING_FIX_05`。它记录 04 轮审核发现的三处**发布链**问题
（不是产品功能问题），以及本轮如何在**机制上**消除它们，而不是在文字上补救。

04 轮的 `docs/AUDIT_CORRECTIONS_04.md` 与 `docs/DELIVERY_REPORT.md` 保持原样，未作任何修改。

---

## PKG05-01 — 候选身份自引用：报告改了 HEAD，报告就过期了

### 问题（04 实测）

04 轮交付目录里的两份材料互相矛盾：

| 文件 | 声称的 FINAL_HEAD |
|---|---|
| `dist/delivery/RELEASE_CANDIDATE_IDENTITY.json` | `5866d72a66ab6bc7f11d09ab0931db78b974cf44`（正确） |
| `dist/delivery/DELIVERY_REPORT.md` | `d25cb62549d1bda5d0f5074c3c8dea69d9306c28`（**陈旧**） |

### 成因

`DELIVERY_REPORT.md` 当时是仓库里的一个文件（`docs/DELIVERY_REPORT.md`）。构建流程是：

```
改代码 → 构建产物 → 写报告（含当时的 HEAD d25cb6…）→ 提交报告 → HEAD 变成 5866d72
```

于是"报告里写的 HEAD"与"真正的最终提交"必然差一次提交。这不是笔误，
而是**自引用**：任何存放在仓库里的身份文件，一旦在产物构建之后被提交，就会使自己的内容失效。

### 修法

把"事实"与"叙述"分开：

- **取值出仓**：`dist/FINAL_HEAD.txt`、`RELEASE_CANDIDATE_IDENTITY.json`、
  `RELEASE_CANDIDATE_IDENTITY.md`、`DELIVERY_REPORT.md` 全部由 `tools/Build-All.ps1`
  在产物生成**之后**写入 `dist/`；`dist/` 被 `.gitignore` 忽略，因此写它们不会改变 HEAD。
- **模板入仓**：报告的正文放在 `docs/DELIVERY_REPORT_05.template.md`，
  由 `tools/Generate-DeliveryReport.ps1` 用证据文件里的取值填充。模板是 HEAD 的一部分，
  但它不含任何随构建变化的数字。
- **单一真源**：`dist/FINAL_HEAD.txt` 记录本次构建使用的提交；
  `Verify-CandidateIdentity.ps1` 从它读取目标，并断言
  `FINAL_HEAD == RELEASE_BUILD_COMMIT == UPDATE_BUILD_COMMIT == SOURCE_HEAD`（`CAND-14`）；
  字段不一致时脚本**拒绝**成功退出，所以"报告生成成功但字段不一致"不可能再发生。
- **不再覆盖**：`Finalize-Delivery.ps1` 不再用仓库里的旧报告覆盖交付目录中已生成的新报告。

### 结果

`tools/Build-All.ps1` 的执行顺序现在是：
发布 → 打包 → 锁 `dist/FINAL_HEAD.txt` → 候选身份核对 → 源码包核对 → 生成交付报告 → 生成清单。
上述任何一步都不写仓库内的文件，因此报告的取值与 HEAD 永远一致。

---

## PKG05-02 — 源码包不完整（漏 `Directory.Build.props`）

### 问题（04 实测）

`LanDisk-source.zip` 用"目录白名单 robocopy"生成：

```powershell
foreach ($item in @('src', 'tests', 'tools', 'docs')) { robocopy ... }
foreach ($file in @('LanDisk.slnx', 'README.md')) { Copy-Item ... }
```

结果：包内**没有** `Directory.Build.props`，也**没有** `.gitignore` —— 而
`Directory.Build.props` 正是注入 `LanDiskBuildCommit` 的文件，缺了它就复现不出同一候选。

### 成因

"哪些文件属于源码"这个问题 **Git 已经知道答案**，手写白名单只是在重复维护一份注定会漂移的列表。

### 修法

```powershell
git -C $repoRoot archive --format=zip --output=$sourceZip $Commit
```

`git archive` 直接输出该提交的完整 tracked tree，并且**不经过工作区**，
所以包内字节就是 Git blob 本身（不受 `core.autocrlf` 影响）。随后追加两个生成文件：

- `SOURCE_HEAD.txt`：`commit` / `buildId` / `packagedAt` / `worktreeClean`
- `SOURCE_MANIFEST.tsv`：每个 tracked 文件的 `mode` 与 **git blob sha1**

新增 `tools/Verify-SourcePackage.ps1` 逐文件复核：对包内每个文件计算
`sha1("blob <len>\0" + 内容)` 并与 `git ls-tree -r <commit>` 的对象名比较，
断言 0 missing、0 blob 不一致、0 tracked 之外的文件，并明确断言
`Directory.Build.props`、`.gitignore`、`LanDisk.slnx` 与根目录全部 tracked 构建文件存在。

---

## PKG05-03 — `build-info.json` 不随更新变化

### 问题（04 实测）

- release ZIP 里有 `build-info.json`；
- `.lupd` 的 payload **不含**它（`keytool pack` 把它当作"发布元数据"排除）；
- 发布目录的 `install-manifest.json` 也**不列**它；
- 因此 A→B 更新后，程序文件换成 B，而磁盘上的 `build-info.json` 仍然是 A 的信息。

### 成因

我在 04 轮把 `build-info.json` 归类为"发布身份元数据"，与 `install-manifest.json` 一起排除。
这个归类是错的：`install-manifest.json` 是**安装器生成的**记录（描述"当前装了什么"），
必须由安装过程自己产生；而 `build-info.json` 是**发布者声明的**内容（"这批文件是什么构建"），
属于被安装的内容，必须随更新一起替换。

### 修法

| 位置 | 改动 |
|---|---|
| `tools/keytool/Program.cs` | 只排除 `install-manifest.json`；`build-info.json` 成为普通受签名 payload 文件 |
| `tools/Build-All.ps1` | 发布目录的 `install-manifest.json` 收录 `build-info.json` |
| `src/LanDisk.Update/InstallBuildInfo.cs`（新增） | `build-info.json` 的模型与读写 |
| `src/LanDisk.Server/Endpoints/UpdateEndpoints.cs` | 自检新增 `build-info` 项：与运行中的程序集版本/提交比对；**缺失不报错**（老安装/开发树），**不一致报错** |
| `src/LanDisk.Update/UpdateHealth.cs` | 更新后的自检必须读到 `installBuildInfo`，且 product/version/buildCommit 与更新包清单一致，否则不 commit、直接回滚 |

保留为"安装器生成元数据"的仍然只有：`install-manifest.json`、更新程序的事务/状态/备份/暂存内部文件。

### 结果

`build-info.json` 现在走完整链路：签名 → 暂存 → 应用 → 回滚备份 → 自检。
实机证据是验收脚本的 `T05-02`（升级后为新版本）与 `T05-03`（回滚后恢复）。

---

## 本轮自身的核对方式

| 数字 | 产物 / 命令 |
|---|---|
| 候选身份 | `tools/Verify-CandidateIdentity.ps1` → `RELEASE_CANDIDATE_IDENTITY.json`（含 `CAND-00`/`CAND-14`） |
| 源码树完整性 | `tools/Verify-SourcePackage.ps1` → `SOURCE_TREE_VERIFY.json` |
| 更新包 payload | `RELEASE_CANDIDATE_IDENTITY.json` 的 `CAND-09`/`CAND-10`/`CAND-12`/`CAND-13` |
| build-info 前后 | `tools/Update-ReleaseVerify.ps1` → `evidence/update-verify.json` 的 `T05-02`/`T05-03` |
| 报告取值 | `tools/Generate-DeliveryReport.ps1`（占位符未替换即失败） |

存在真机 `NOT_RUN` 时，总体结论保持 `READY_FOR_DEVICE_VALIDATION`。
