# AUDIT_CORRECTIONS — 任务 03 的审计发现与报告更正

本文件记录两件事，都是"以实测为准，不以文档叙述为准"：

1. **代码审计发现**：上一轮交付中被指出的两个问题，以及本轮的修法与回归证据；
2. **报告更正**：上一轮 `docs/DELIVERY_REPORT.md` 中与实际产物不一致的表述，逐条更正并给出实测值。

---

## 一、代码审计发现与修复

### FIX-01 批量下载 ZIP 的选择集在生成前未被重新校验（TOCTOU）

**问题**：`POST /api/batch/ticket` 用计划的绝对路径生成票据，`GET` 流式写 ZIP 时直接使用票据里的路径。
在校验与写盘之间，如果文件被替换、改短或被换成符号链接，归档里就会混入**与用户选择不一致**的内容，
而且写入的是"当时的路径"而不是"当时的那份内容"。

**修复**：

- `BatchService.ZipPlanEntry` 不再携带可信绝对路径，改为携带
  `(EntryName, RelativePath, DisplayPath, VolumeGuidPath, ObjectId, Length, LastWriteTime)`；
- 新增 `BatchRevalidation` / `RevalidateAll(share, plan)` / `TryOpenValidated(...)` / `TryValidate(...)`：
  每个条目在**写出前逐条**重新经过 `PathResolver`，并比对卷身份、文件索引（ObjectId）、长度与修改时间；
- 判定规则最终定为 `objectIdMismatch || lengthMismatch || timeMismatch(> 2s)`：
  最初只用文件索引判断，实测发现**就地重写**（in-place rewrite）不改变文件索引，
  会让被改写的文件通过校验；补上长度与时间后才真正拦住；
- 不一致时返回 409 + 稳定错误码 `CHANGED_SELECTION`（`BatchService.ChangedSinceSelection`），
  并在 `extra.items` 中逐项给出路径与原因；
- 票据 GET 与一次性 POST 两条路径都执行同一套再校验。

**回归证据**：`AuditFixTests.FIX01_file_replaced_after_ticket` 等；
前端在收到该错误码时清空选择、提示具体哪些文件变化并刷新列表（`DeviceApp.reportSelectionChanged`）。

### FIX-02 "最近"视图静默截断（把部分结果当成完整结果展示）

**问题**：`/api/recent` 在多个共享上扫描时，一旦达到扫描条目上限或单个共享的结果上限就停止，
但响应里只有一个布尔 `truncated`，界面只说"文件较多"。用户无法判断**是扫描没扫完（可能有更新文件缺失）**
还是**结果只是被限制了条数**，两者的处理方式完全不同。

**修复**：

- `DirectoryBrowser` 拆出 `CollectedListing`（`Entries / ScannedCount / ScanLimitReached / ResultLimitReached /
  ScanMilliseconds / Truncated`），调用方可以先拿到完整的受限有序集再分页；
- `/api/recent` 的上限改为由设置驱动（`RecentMaxPerShare = 5000`、`RecentMaxScanEntries = 200000`），
  响应新增 `truncatedReasons`（`scan_limit:<共享名>` / `per_share_limit:<共享名>`）、
  `perShare`（每个共享的候选数、扫描数、是否被截断）与 `scanMs`；
- 前端 `describeRecentTruncation()` 把机器码翻译成可操作的中文：
  "……内的文件太多，本次扫描在读完之前就停下来了，列表里可能缺少较新的文件"（扫描上限）
  与"……中最近修改的文件超过了单个共享的显示上限，这里只列出最新的部分"（条数上限）分别说明。

**回归证据**：`AuditFixTests.FIX02_single_share_with_more_than_1000` 等。

> 这两个修复合计新增 16 个用例（`AuditFixTests`），全部通过。

---

## 二、上一轮报告的更正

以下四条的原文都在 `LAN_DISK_UX_AUTH_UPGRADE_02` 的 `docs/DELIVERY_REPORT.md` 中。
更正的依据不是重新叙述，而是本轮重新执行 `dotnet test --list-tests`、重新读取
`dist/test-evidence-final.tsv` 与 `.releasecheck/<最新>/release-smoke.json` 得到的实测值。

### 更正 1 — `NEW_TESTS` 的分类数量与实测不符

| 项目 | 上一轮报告写的 | 本轮实测（`--list-tests` 展开后的用例数） |
|---|---|---|
| SortingTests | 9 | **9** |
| SearchFilterTests | 8 | **12** |
| LibraryTests | 12 | **11** |
| BatchTests | 12 | **12** |
| AdminAutoEntryTests | 19 | **17** |
| HeaderFormatTests | 8 | **8** |
| PerformanceTests | 2 | **2** |
| 合计 | 71（写"71 / 71 PASS"） | **71** |

**结论**：总数 71 与实测一致，但其中三个分类的数字写错了。
`[Theory]` 在源码里只占一个 `[Fact]/[Theory]` 标记却会展开成多个用例，
按标记数统计就会得到错误的分类数字——本文件与新的交付报告统一改用
`dotnet test --list-tests` 展开后的用例数。

### 更正 2 — `test-evidence.tsv` 的记录条数

- 上一轮报告：「`test-evidence.tsv`（**202 条**运行时实测记录）」
- 实测：该文件共 **202 行**，其中前 **3 行是以 `#` 开头的表头/说明**，
  真正的记录是 **199 条**。

**结论**：202 是行数，不是记录数。本轮报告在给出条数时明确区分"行数"与"记录数"，
并且随交付提供的证据文件本身可以复核（`Get-Content ... | Where-Object { $_ -notlike '#*' }`）。

### 更正 3 — 发布包冒烟验收的数量表述

- 上一轮报告：「`WINDOWS_RUNTIME: 17 / 17 PASS`」
- 实测 `.releasecheck/<最新>/release-smoke.json`：
  **19 条记录，0 条 FAIL**；按 `Id` 去重后是 **10 个检查项**
  （E02×4、E03×6、P01、A01、F01、M02、D01、U01、R01×1、R02×2）。

**结论**：报告里的"17"既不等于记录数（19）也不等于去重检查项数（10），
属于叙述与产物脱节。**产物（JSON）是唯一权威**：本轮报告只声明"18 条记录 / 0 FAIL"
这类可以直接在产物里数出来的数字，并同时给出产物路径。

### 更正 4 — 「列表不生成缩略图」是错的

- 上一轮 `docs/KNOWN_LIMITATIONS.md` 写：「图片缩略图批量生成 | 不实现 | **列表不生成缩略图**；预览时才请求原图。」
- 实测：`src/LanDisk.Web/src/components/EntryList.tsx` 中 `Thumbnail` 组件被用在**两处**——
  列表行（第 122 行，26px）与网格卡片（第 181 行，34px）。
  即**列表视图同样会为 `hasThumbnail` 的条目请求并显示缩略图**，只是尺寸更小。

**更正后的表述**：真实的限制是——**不提供"批量预生成"**：缩略图在列表/网格滚动到可见时按需生成
（`loading="lazy"`）并缓存，没有后台全量预生成任务；预览大图时才请求原图。
该条已在 `docs/KNOWN_LIMITATIONS.md` 中改写。

---

## 三、本轮新增产物的自查

为避免重复上一轮的"叙述与产物脱节"，本轮所有数字都来自可复核的产物，
并在 `docs/DELIVERY_REPORT.md` 中给出**产生该数字的命令**：

| 数字 | 来源 |
|---|---|
| 单元测试用例总数与逐类数量 | `dotnet test ... --list-tests` 的展开结果 |
| 单元测试通过数 | `dotnet test -c Release` 的汇总行 |
| 发布包实机升级/回滚结论 | `dist/.rcverify/<时间戳>/update-verify.json` |
| 发布包冒烟结论 | `.releasecheck/<时间戳>/release-smoke.json` |
| 浏览器界面结论 | `dist/e2e-ui-chromium.json`、`dist/e2e-ui-webkit.json` |
| 隐私扫描结论 | `dist/privacy-check.json` |

**未运行项一律记为 `NOT_RUN`，并且只要存在 `NOT_RUN`，总体结论就不写 PASS。**
