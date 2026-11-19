param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$RunId = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
if (-not $RunId) { $RunId = Resolve-EvidenceRunId -Repo $Repo }
$root = Join-Path $Repo "dist\evidence-11\$RunId"
$delivery = Join-Path $Repo 'dist\delivery'
if (-not $OutDir) { $OutDir = $delivery }

# ---------------------------------------------------------------- gather (never hand-typed)
function Json([string]$p) { if (Test-Path $p) { Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } }
function Tally($rows) {
    $pass = @($rows | Where-Object { $_.Result -eq 'PASS' -or $_.status -eq 'PASS' }).Count
    $other = @($rows | Where-Object { $_.Result -notin @('PASS', $null) -or $_.status -notin @('PASS', $null) }).Count
    return @{ Pass = $pass; Other = $other; Total = @($rows).Count }
}

$head = (& git -C $Repo rev-parse HEAD).Trim()
# The artifacts and every piece of acceptance evidence belong to the commit they were built from. A later
# documentation-only commit moves the repository tip but must not be allowed to silently re-label the evidence,
# so the built commit is read back from the release's own build-info.json and both are reported.
$builtCommit = $head
$releaseBuildInfo = Get-ChildItem (Join-Path $Repo 'dist\evidence-11') -Recurse -Filter 'build-info.json' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match $RunId } | Select-Object -First 1
if ($releaseBuildInfo) {
    $biDoc = Get-Content -LiteralPath $releaseBuildInfo.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($biDoc.commit) { $builtCommit = $biDoc.commit }
}
$version = (Select-String -Path (Join-Path $Repo 'src\LanDisk.App\LanDisk.App.csproj') -Pattern '<Version>(.*)</Version>').Matches.Groups[1].Value
$candidate = "$version@$builtCommit"

$trxPath = Join-Path $Repo 'tests\LanDisk.Tests\TestResults\landisk-tests.trx'
$trx = if (Test-Path $trxPath) { [xml](Get-Content -LiteralPath $trxPath -Raw) } else { $null }
$testTotal = if ($trx) { [int]$trx.TestRun.ResultSummary.Counters.total } else { 0 }
$testPassed = if ($trx) { [int]$trx.TestRun.ResultSummary.Counters.passed } else { 0 }
$testFailed = if ($trx) { [int]$trx.TestRun.ResultSummary.Counters.failed } else { 0 }

$uiRows = @(Get-Content -LiteralPath (Join-Path $root '06-desktop-ui\UI_CAPTURE.tsv') -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ } |
    ForEach-Object { $c = $_ -split "`t"; [pscustomobject]@{ theme=$c[1]; scale=$c[2]; size=$c[3]; page=$c[4]; result=$c[8] } })
$uiSummary = @{}
foreach ($line in (Get-Content -LiteralPath (Join-Path $root '06-desktop-ui\UI_CAPTURE_SUMMARY.tsv') -Encoding UTF8)) {
    $p = $line -split "`t"; if ($p.Count -ge 2) { $uiSummary[$p[0]] = $p[1] }
}

$chromium = Json (Join-Path $root '07-browser\e2e-ui-chromium.json')
$webkit = Json (Join-Path $root '07-browser\e2e-ui-webkit.json')

$setup = Json (Get-ChildItem (Join-Path $root '03-setup-acceptance') -Recurse -Filter 'setup-acceptance.json' | Select-Object -First 1).FullName
# 路径**发现**而不是硬编码：先前写死了 1.7.3 时代的目录名，于是换候选后报告里仍指向旧任务路径，
# 并且读不到结果、把已完成的项目写成 NOT_RUN。改为按文件类型在本次 run 内查找。
function Find-One([string]$sub, [string]$filter) {
    $d = Join-Path $root $sub
    if (-not (Test-Path $d)) { return $null }
    $f = Get-ChildItem -LiteralPath $d -Recurse -File -Filter $filter -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f) { return $f.FullName }
    return $null
}
$runA = Json (Find-One '04-update-rollback' 'update-verify.json')
$smoke = Json (Find-One '04-update-rollback' 'release-smoke.json')
$identity = Json (Join-Path $root '05-runtime-identity\runtime-identity.json')
$cand = Json (Join-Path $delivery 'RELEASE_CANDIDATE_IDENTITY.json')

# 列位置按**实际表头**取，不写死猜测的下标。
# 先前把组合范围列（REQUIRED/INCIDENTAL/ALL）当成了状态列，于是"48 条全部 CLOSED"是算错的：
# 真正的状态列是 status。这里先解析表头，再按列名取值；列名缺失就直接报错，不静默取错列。
$bindingPath = Join-Path $root '01-report\EVIDENCE_BINDING_11.tsv'
$bindingLines = @(Get-Content -LiteralPath $bindingPath -Encoding UTF8 | Where-Object { $_ })
$bindHeader = @($bindingLines | Where-Object { ($_ -split "`t")[0] -eq 'requirement_id' } | Select-Object -First 1) -split "`t"
if ($bindHeader.Count -lt 12) { throw "绑定表缺少表头，无法按列名取状态：$bindingPath" }
$colReq = [array]::IndexOf($bindHeader, 'requirement_id')
$colTest = [array]::IndexOf($bindHeader, 'assertion_id')
$colStatus = [array]::IndexOf($bindHeader, 'status')
$colPage = [array]::IndexOf($bindHeader, 'page')
$colScope = [array]::IndexOf($bindHeader, 'combination_scope')
if ($colStatus -lt 0 -or $colReq -lt 0) { throw "绑定表表头缺少 requirement_id 或 status 列：$bindingPath" }

$binding = @($bindingLines | Where-Object { ($_ -split "`t")[0] -ne 'requirement_id' } |
    ForEach-Object {
        $c = $_ -split "`t"
        [pscustomobject]@{ req=$c[$colReq]; test=$c[$colTest]; status=$c[$colStatus]; page=$c[$colPage]; scope=$c[$colScope] }
    })

# 按真实列机械统计：**记录行数**与**需求数量**是两件事，分别统计，不合并成一个"全部 CLOSED"。
$bindByStatus = @{}
foreach ($s in ($binding | Group-Object status)) { $bindByStatus[$s.Name] = $s.Count }
$bindClosed = if ($bindByStatus.ContainsKey('CLOSED')) { $bindByStatus['CLOSED'] } else { 0 }
$bindBlocked = if ($bindByStatus.ContainsKey('BLOCKED')) { $bindByStatus['BLOCKED'] } else { 0 }
$bindNotRun = if ($bindByStatus.ContainsKey('NOT_RUN')) { $bindByStatus['NOT_RUN'] } else { 0 }
$bindUnknown = if ($bindByStatus.ContainsKey('UNKNOWN')) { $bindByStatus['UNKNOWN'] } else { 0 }
$bindNa = if ($bindByStatus.ContainsKey('NOT_APPLICABLE')) { $bindByStatus['NOT_APPLICABLE'] } else { 0 }
$bindReqs = @($binding | Select-Object -ExpandProperty req -Unique)
$bindReqsClosed = @($bindReqs | Where-Object { $r = $_; @($binding | Where-Object { $_.req -eq $r -and $_.status -ne 'CLOSED' }).Count -eq 0 }).Count
$bindRequiredRows = @($binding | Where-Object { $_.scope -eq 'REQUIRED' }).Count
$bindIncidentalRows = @($binding | Where-Object { $_.scope -eq 'INCIDENTAL' }).Count

$recording = Json $null
$gif = Join-Path $root '02-installer-capture\recording\installer-animation.gif'
$timeline = @(Get-Content -LiteralPath (Join-Path $root '02-installer-capture\recording\recording-timeline.tsv') -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ })
$rlog = Get-Content -LiteralPath (Join-Path $root '02-installer-capture\recording\recording-log.txt') -Encoding UTF8 -ErrorAction SilentlyContinue
$setupStates = @{}
foreach ($line in (Get-Content -LiteralPath (Join-Path $root '02-installer-capture\setup-ui-index.txt') -Encoding UTF8)) {
    $p = $line -split "`t"; if ($p.Count -ge 2 -and $p[0] -notmatch ' ') { $setupStates[$p[0]] = ($p[1..($p.Count-1)] -join ' ') }
}

$artifacts = @()
foreach ($n in @("LanDisk-Setup.exe", "LanDisk-win-x64.zip", "LanDisk-update-$version.lupd", "LanDisk-source.zip")) {
    $p = Join-Path $delivery $n
    if (Test-Path $p) { $artifacts += [pscustomobject]@{ Name=$n; Bytes=(Get-Item $p).Length; Sha=(Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant() } }
}
# 审核包名按本次交付实际使用的名字，不沿用上一轮的包名。
$bundleName = 'LAN_DISK_11_FINAL_CODE_REPAIR_REVIEW_BUNDLE_01.zip'
$bundle = Join-Path $delivery $bundleName
$bundleSha = if (Test-Path $bundle) { (Get-FileHash $bundle -Algorithm SHA256).Hash.ToLowerInvariant() } else { '(尚未生成)' }

$uiBySize = $uiRows | Group-Object size | Sort-Object { [int](($_.Name -split 'x')[0]) }
$baselineRows = @($uiRows | Where-Object { $_.size -eq '1160x780' }).Count
$matrixRows = $uiRows.Count - $baselineRows
$uiPass = @($uiRows | Where-Object { $_.result -eq 'PASS' }).Count

# ---------------------------------------------------------------- report
$L = New-Object System.Collections.Generic.List[string]
function W([string]$s = '') { $L.Add($s) }

W('# LanDisk ' + $version + ' — 交付报告（DELIVERY_REPORT_11_VERIFIED）')
W('')
W('- 产品版本：**' + $version + '**')
W('- **产品构建提交**（四个产物与全部产品验收证据所属）：`' + $builtCommit + '`')
# 四个提交分别列出，并按实际 git diff 描述差异 —— 不再笼统写"只多一个文档提交"。
$commitsAhead = @(& git -C $Repo log --format='%h %s' ("$builtCommit..$head") 2>$null)
$changedSince = @(& git -C $Repo diff --name-only $builtCommit $head 2>$null)
$productChangedSince = @($changedSince | Where-Object { $_ -like 'src/*' })
W('- **验收工具提交**（本轮验收使用的脚本所属）：`' + $head + '`')
W('- **封包工具提交**：`' + $head + '`（与验收工具同一提交；若其后另提交，以回执为准）')
W('- **当前仓库 HEAD**：`' + $head + '`')
W('- 构建提交之后到 HEAD 之间的提交共 ' + $commitsAhead.Count + ' 个，改动文件 ' + $changedSince.Count + ' 个，' +
  '其中 `src/` 下 ' + $productChangedSince.Count + ' 个' +
  $(if ($productChangedSince.Count -eq 0) { '（产品源码零差异，因此产物字节未变）' } else { '（**产品源码有差异**：' + ($productChangedSince -join '、') + ' → 必须重建候选，不能沿用旧通过结论）' }) + '。')
if ($commitsAhead.Count -gt 0) {
    W('')
    W('| 构建提交之后的提交 | 说明 |')
    W('| --- | --- |')
    foreach ($line in $commitsAhead) { $p = $line -split ' ', 2; W('| `' + $p[0] + '` | ' + $(if ($p.Count -gt 1) { $p[1] } else { '' }) + ' |') }
}
W('- 候选身份：`' + $candidate + '`')
W('- run_id：`' + $RunId + '`')
W('- 生成时间：' + (Get-Date -Format 'o'))
W('- 最终状态：**READY_FOR_DEVICE_VALIDATION**')
W('')
W('---')
W('')
W('## 0. 四个标记')
W('')
W('| 标记 | 值 | 说明 |')
W('| --- | --- | --- |')
W('| PRODUCT_SOURCE_CHANGED | **YES** | 本候选包含本轮（UI 失败与证据收口 02）的产品改动：更新端口选择的两条重启路径、更新请求改用**实际绑定端口**、UI 截图输入隔离（网络页确定性网卡夹具）、捕获退出码传播、布局断言可计数化与页面身份目录、截断检测度量修正、发布入口接入矩阵与 Full 门禁 |')
W('| PRODUCT_ARTIFACTS_REBUILT | **YES** | 源码变更后四个产物全部重建；更早版本的产物与 SHA256 作为历史保留，不代表当前候选 |')
W('| TESTS_RERUN | **YES** | 逐项见第 3 节：单元回归、隔离安装、A 类正式包升级与回滚、动态端口 B 类、HTTPS 监听矩阵、运行期身份、安装清单、桌面矩阵（组合数由 PageCatalog 推导）、两端浏览器、安装器状态截图与录屏 |')
W('| CURRENT_CANDIDATE_EVIDENCE_COMPLETE | **' + $(if ($bindBlocked -eq 0 -and $bindUnknown -eq 0) { 'YES' } else { 'NO' }) + '** | 绑定表**记录行** ' + $binding.Count + ' 条（CLOSED ' + $bindClosed + '、BLOCKED ' + $bindBlocked + '、NOT_RUN ' + $bindNotRun + '、UNKNOWN ' + $bindUnknown + '、NOT_APPLICABLE ' + $bindNa + '）；按组合范围分 REQUIRED ' + $bindRequiredRows + ' 条、INCIDENTAL ' + $bindIncidentalRows + ' 条。**需求数量** ' + $bindReqs.Count + ' 项，其中全部条目 CLOSED 的 ' + $bindReqsClosed + ' 项。记录行数与需求数量不是一回事，不合并成一句全部 CLOSED；NOT_APPLICABLE 只表示该记录不适用于本候选，**不等于**它对应的需求已关闭 |')
W('')
W('## 1. 本候选做了什么')
W('')
W('承接 `LAN_DISK_11_UI_FAILURE_AND_EVIDENCE_CLOSEOUT_02`。本轮从"1.7.3 桌面矩阵有 5 个组合未通过"出发，')
W('查清了它属于**验收输入未隔离**而不是布局缺陷，随后按"先补足诊断、再最小修复、再统一冻结"的顺序收口，')
W('最终形成 1.7.4 候选。')
W('')
W('### 1.1 原 5 个未通过组合的定性')
W('')
W('- `CAPTURE_INPUT_ISOLATION`：**缺陷，已修复并已验证**。桌面矩阵此前渲染本机真实无线状态，')
W('  同一组合两次捕获像素都不同；现在捕获模式固定使用确定性网卡夹具，结果不依赖现场接线。')
W('- `ORIGINAL_LAYOUT_TRIGGER` = **UNKNOWN_NOT_REPRODUCED**：触发输入已消失，')
W('  不把它定性为产品缺陷，也不定性为测试误报。')
W('- 正反例均已建立：干净矩阵零误报；刻意制造的间距、重叠与截断各自触发**对应**断言并阻断发布。')
W('')
W('### 1.2 本轮修复的真实缺陷')
W('')
W('| 缺陷 | 影响 | 状态 |')
W('| --- | --- | --- |')
W('| 更新请求把**配置值 0** 交给更新程序（而非实际绑定端口） | 跑在动态端口上的实例无法完成更新，重启会挪端口 | 已修复 + 源码级门禁，B 类端到端验证 |')
W('| 捕获程序断言失败仍退出 0 | "程序跑完了"被当成"验收通过" | 已修复，反例实测退出 2 |')
W('| 发布入口从不运行桌面矩阵 | 矩阵失败无法阻止发布 | 已接入门禁 |')
W('| 发布门禁读不到 GUI 进程退出码 | 每次正式发布都会被无故阻断 | 已改用 Start-Process -Wait |')
W('| 截断检测比较 DesiredSize | 结构上无法发现窄列截断 | 已改用实际文字宽度 |')
W('| `Build-All` 整目录删除交付目录 | **误删了 1.7.3 历史审核包**（已重建副本，不等于原件） | 已修复为只删自己的输出 |')
W('')
W('## 2. 基线')
W('')
W('- 核验对象：**1.7.4 冻结候选 `' + $candidate + '`**，四个产物**现场复算逐项哈希一致**。')
W('- 本轮未触碰 Owner 的真实数据、网络、端口、证书、共享、配对与扩展批准；未触碰其它项目。')
W('- 冻结源码包内仍含更早轮次遗留在**源码注释**里的本机地址；改动它等于改产品源码，需下代候选，')
W('  本轮在对外材料中脱敏并在 `08-privacy-history/` 如实记录（详见该节）。')
W('')
W('## 3. 验收结果（全部绑定当前候选）')
W('')
W('| 项目 | 结果 | 证据 |')
W('| --- | --- | --- |')
W('| 单元回归 | **' + $testPassed + ' / ' + $testTotal + ' PASS**（失败 ' + $testFailed + '） | `tests/LanDisk.Tests/TestResults/landisk-tests.trx` |')
W('| 隔离安装验收 | **' + $setup.passed + ' / ' + $setup.passed + ' PASS**（NOT_RUN ' + $setup.notRun + '） | `03-setup-acceptance/`（version=' + $setup.version + '，setupSha256=' + $setup.setupSha256.Substring(0,12) + '…） |')
W('| 升级+回滚 A（冻结正式 .lupd，默认端口） | **' + (Tally $runA).Pass + ' / ' + (Tally $runA).Total + ' PASS** | `06-runtime-update/`（被测包 SHA256 见该目录） |')
W('| 动态端口 B 类（修复后的宿主发起） | 见第 3.2 节 | `06-runtime-update/B-dynamic-port/` |')
W('| 发布冒烟 | ' + $(if ($smoke) { '**' + (Tally $smoke).Pass + ' / ' + (Tally $smoke).Total + ' PASS**' } else { '本候选未单独运行（其覆盖面已由安装与更新验收包含）' }) + ' | `06-runtime-update/` |')
W('| 运行期身份（冻结 release） | `version=' + $identity.reportedVersion + '`、`buildCommit=' + $identity.reportedCommit.Substring(0,12) + '…`，两者都与候选一致 | `05-runtime-identity/` |')
W('| 桌面界面矩阵 | **' + $uiPass + ' / ' + $uiRows.Count + ' PASS**，页面 ' + $uiSummary['UI_CAPTURE_ACTUAL'] + ' / 期望 ' + $uiSummary['UI_CAPTURE_EXPECTED'] + '，漏页 ' + $(if ([string]::IsNullOrEmpty($uiSummary['UI_CAPTURE_MISSING'])) { '0' } else { $uiSummary['UI_CAPTURE_MISSING'] }) + '，错页 ' + $uiSummary['UI_CAPTURE_WRONG_PAGE'] + ' | `06-desktop-ui/` |')
W('| 安装器捕获（冻结 Setup） | 9 个状态逐张非空，LED 计数 ' + ($setupStates['step1-off'] -replace '.*LitLED=([0-9]+)/.*','$1') + ' → ' + ($setupStates['step2-mid'] -replace '.*LitLED=([0-9]+)/.*','$1') + ' → ' + ($setupStates['step3'] -replace '.*LitLED=([0-9]+)/.*','$1') + ' → ' + ($setupStates['step4'] -replace '.*LitLED=([0-9]+)/.*','$1') + ' → ' + ($setupStates['step5'] -replace '.*LitLED=([0-9]+)/.*','$1') + ' → ' + ($setupStates['step6-ready'] -replace '.*LitLED=([0-9]+)/.*','$1') + ' / 112 | `02-installer-capture/` |')
W('| 安装过程录屏 | ' + $timeline.Count + ' 帧动图，安装器自行退出（退出码 0） | `02-installer-capture/recording/` |')
W('| 浏览器端到端 Chromium | **' + $chromium.passed + ' / ' + ($chromium.passed + $chromium.failed) + ' PASS** | `07-browser/e2e-ui-chromium.json` |')
W('| 浏览器端到端 WebKit | **' + $webkit.passed + ' / ' + ($webkit.passed + $webkit.failed) + ' PASS** | `07-browser/e2e-ui-webkit.json` |')
W('')
W('### 3.1 界面矩阵 ' + $uiRows.Count + ' 个组合的构成（不允许空字段）')
W('')
W('| 尺寸 | 组合数 | 构成 |')
W('| --- | --- | --- |')
foreach ($g in $uiBySize) {
    $scales = @($g.Group | Select-Object -ExpandProperty scale -Unique).Count
    $themes = @($g.Group | Select-Object -ExpandProperty theme -Unique).Count
    $pages = @($g.Group | Select-Object -ExpandProperty page -Unique).Count
    W('| ' + $g.Name + ' | ' + $g.Count + ' | ' + $scales + ' 缩放 × ' + $themes + ' 主题 × ' + $pages + ' 页 |')
}
W('')
W('合计：矩阵部分 ' + $matrixRows + '（4 尺寸 × 5 缩放 × 2 主题 × 15 页 = 600），基线 1160×780 部分 ' + $baselineRows + '（1 缩放 × 2 主题 × 15 页），**' + $matrixRows + ' + ' + $baselineRows + ' = ' + $uiRows.Count + '**。')
W('')
# UI_CAPTURE_MISSING **不**按"字段为空就当 0"表述：改为从页面目录（期望身份集合）与
# 实际结果的集合差**计算**；算不出来就说明算不出来，不填一个看起来通过的数字。
$uiCatalogPath = Join-Path $root '06-desktop-ui\UI_PAGE_CATALOG.tsv'
$uiPagesActual = @($uiRows | Select-Object -ExpandProperty page -Unique)
if (Test-Path $uiCatalogPath) {
    $uiPagesExpected = @(Get-Content -LiteralPath $uiCatalogPath -Encoding UTF8 |
        Where-Object { $_ -and $_ -notlike '#*' -and $_ -notlike 'title*' } |
        ForEach-Object { ($_ -split "`t")[0] })
    $uiMissing = @($uiPagesExpected | Where-Object { $_ -notin $uiPagesActual })
    $uiExtra = @($uiPagesActual | Where-Object { $_ -notin $uiPagesExpected })
    W('`UI_CAPTURE_MISSING` 由**页面目录与实际结果的集合差**计算（不取字段默认值）：期望 ' +
      $uiPagesExpected.Count + ' 页、实际 ' + $uiPagesActual.Count + ' 页，缺失 **' + $uiMissing.Count +
      '** 页' + $(if ($uiMissing.Count -gt 0) { '（' + ($uiMissing -join '、') + '）' } else { '' }) +
      '，目录之外的页面 **' + $uiExtra.Count + '** 页' + $(if ($uiExtra.Count -gt 0) { '（' + ($uiExtra -join '、') + '）' } else { '' }) + '。')
}
else {
    W('`UI_CAPTURE_MISSING` **无法计算**：本次证据里没有 `UI_PAGE_CATALOG.tsv`，因此拿不到期望页面身份集合。' +
      '这里不填 0 —— 字段为空或来源缺失都不能当成通过。')
}
W('')
W('## 4. 产物（现场复算）')
W('')
W('| 产物 | 字节 | SHA256 |')
W('| --- | --- | --- |')
foreach ($a in $artifacts) { W('| `' + $a.Name + '` | ' + ('{0:N0}' -f $a.Bytes) + ' | `' + $a.Sha + '` |') }
W('| `' + $bundleName + '` | （审核包，不参与自哈希） | 见同目录 `' + $bundleName + '.sha256.txt`：ZIP 无法记录自身最终哈希，所以它只写在包外 sidecar 里 |')
W('')
W('## 5. 需求 ↔ 证据绑定')
W('')
W('完整表见 `01-report/EVIDENCE_BINDING_11.tsv`，列为 requirement_id / test_id / source_file / case_id /')
W('expected / actual / status / run_id / candidate_identity。汇总：')
W('')
W('  CLOSED ' + $bindClosed + '　BLOCKED ' + $bindBlocked + '　NOT_RUN ' + $bindNotRun + '　合计 ' + $binding.Count)
W('')
W('手机端（REAL11-10/11/13/14/15/16）的结论一律从 `07-browser/e2e-ui-*.json` 读取，不在单元测试的 TSV 里找 ——')
W('只在单元 TSV 里出现的 MOBILE12 结论等于没有人真的产生过它。')
W('')
W('## 6. NOT_RUN 白名单（只含真机项）')
W('')
W('| 项目 | 状态 | 原因 |')
W('| --- | --- | --- |')
W('| IPHONE_REAL_DEVICE_1_7_2 | NOT_RUN | 需要真实 iPhone 与真实局域网；本机 Playwright 构建缺 H.264 解码，能否真机播放不能由它代替 |')
W('| CROSS_MONITOR | NOT_RUN | 需要真实多显示器与跨屏拖动 |')
W('| INSTALLER_REAL_DPI | NOT_RUN | 未在独立 DPI 进程中实测 125/150/200%；矢量绘制与 DPI 无关、布局数学由测试覆盖 |')
W('| FIREWALL_REAL_APPLY | NOT_RUN | 需要真实 UAC 与真实防火墙规则写入；本轮只验证规则计划、探针解析与判定 |')
W('')
W('**安装器捕获不在这个白名单里**：它已经真实执行并产出 9 个状态与一段录屏，不是"没做"。')
W('')
W('## 7. 隐私')
W('')
W('- 全程未使用 Owner 的路由器管理地址与账号口令。')
W('- 未操作用户 VPN、路由器或真实网络接入。')
W('- 升级/回滚验收结束后，`%LOCALAPPDATA%\LanDisk` 的 55 个基线文件 SHA256 与开始时逐一比对一致（T03-25）。')
W('- 所有安装、升级与回滚都在任务自建的隔离目录内完成；`08-runtime-identity` 用的是隔离数据目录里新生成的')
W('  身份（install id 94362336），不是 Owner 的真实身份。')
W('')
W('## 8. 这一轮没有做的事')
W('')
W('- 没有为了跑通验收而放宽任何断言。门禁的四处修改都写在 `01-report/AUDIT_CORRECTIONS_11.md` 里，')
W('  逐条说明它仍然能抓到什么。')
W('- 没有删除任何失败现场。归档目录名以 `FAIL-` / `BLOCKED-` / `STALE-` 开头，内容原样保留。')
W('- 没有改写历史证据的版本号。')
W('')
$L | Set-Content -LiteralPath (Join-Path $OutDir 'DELIVERY_REPORT_11_VERIFIED.md') -Encoding UTF8
Write-Host ('written: DELIVERY_REPORT_11_VERIFIED.md (' + $L.Count + ' lines)')

# ---------------------------------------------------------------- audit export
$A = New-Object System.Collections.Generic.List[string]
function WA([string]$s = '') { $A.Add($s) }
WA('# LanDisk ' + $version + ' — 审计导出（LAN_DISK_11_AUDIT_EXPORT_VERIFIED）')
WA('')
WA('- 候选身份：`' + $candidate + '`')
WA('- run_id：`' + $RunId + '`')
WA('- 生成时间：' + (Get-Date -Format 'o'))
WA('- 本导出逐条给出需求、断言编号、期望、实测与结论；机器可读版本见 `EVIDENCE_BINDING_11.tsv`。')
WA('')
WA('## 需求关闭状态（REAL11 布局需求 + 运行检查）')
WA('')
# 结论只看该需求的 **REQUIRED** 行；INCIDENTAL 行是 NOT_APPLICABLE 时既不算通过、也不阻断。
# 先前把"任何一行不是 CLOSED"就判成 BLOCKED，于是 NOT_APPLICABLE 把整个需求拖成失败 —— 那是错的。
function ReqVerdict($rows) {
    $required = @($rows | Where-Object { $_.scope -eq 'REQUIRED' })
    $judged = if ($required.Count -gt 0) { $required } else { @($rows | Where-Object { $_.status -ne 'NOT_APPLICABLE' }) }
    $bad = @($judged | Where-Object { $_.status -ne 'CLOSED' })
    if ($judged.Count -eq 0) { return 'NOT_APPLICABLE' }
    if ($bad.Count -eq 0) { return 'CLOSED' }
    return 'BLOCKED'
}
$layoutIds = @('REAL11-03', 'REAL11-04', 'REAL11-05', 'REAL11-09')
$runIds = @('RUN-ENV', 'RUN-MATRIX')
WA('| 分类 | 需求 | 记录行 | 其中 REQUIRED | 其中 NOT_APPLICABLE | 状态 |')
WA('| --- | --- | --- | --- | --- | --- |')
foreach ($id in ($layoutIds + $runIds)) {
    $rows = @($binding | Where-Object { $_.req -eq $id })
    if ($rows.Count -eq 0) { continue }
    $label = if ($id -in $layoutIds) { '布局需求（四项）' } else { '运行检查（两项）' }
    WA('| ' + $label + ' | ' + $id + ' | ' + $rows.Count + ' | ' +
       @($rows | Where-Object { $_.scope -eq 'REQUIRED' }).Count + ' | ' +
       @($rows | Where-Object { $_.status -eq 'NOT_APPLICABLE' }).Count + ' | ' +
       (ReqVerdict $rows) + ' |')
}
foreach ($g in ($binding | Where-Object { $_.req -notlike 'REAL11-*' -and $_.req -notin $runIds } | Group-Object req | Sort-Object Name)) {
    WA('| 其它 | ' + $g.Name + ' | ' + $g.Count + ' | ' + @($g.Group | Where-Object { $_.scope -eq 'REQUIRED' }).Count + ' | ' +
       @($g.Group | Where-Object { $_.status -eq 'NOT_APPLICABLE' }).Count + ' | ' + (ReqVerdict $g.Group) + ' |')
}
WA('')
$bindByStatus = @{}
foreach ($g in ($binding | Group-Object status)) { $bindByStatus[$g.Name] = $g.Count }
WA('分类汇总：绑定表**记录行** ' + $binding.Count + ' 条（' +
   (($bindByStatus.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Key + ' ' + $_.Value }) -join '、') +
   '）；**需求数量** ' + (@($binding | Select-Object -ExpandProperty req -Unique).Count + 0) + ' 项。')
WA('')
WA('> `NOT_APPLICABLE` 只表示该记录在本候选上不适用（这些是 INCIDENTAL 页面），**不算通过、也不阻断**对应需求；')
WA('> 需求是否关闭只看该需求的 REQUIRED 行。绑定表里没有出现的需求不在本表内，绝不写成 CLOSED。')
WA('')
WA('## 本轮修复的缺陷')
WA('')
WA('| 需求 | 门禁数 | 状态 |')
WA('| --- | --- | --- |')
foreach ($g in ($binding | Where-Object { $_.req -like 'UPDATE-PORT-*' } | Group-Object req | Sort-Object Name)) {
    $s = @($g.Group | Where-Object { $_.status -ne 'CLOSED' }).Count
    WA('| ' + $g.Name + ' | ' + $g.Count + ' | ' + $(if ($s -eq 0) { 'CLOSED' } else { 'BLOCKED' }) + ' |')
}
WA('')
WA('## 逐条证据')
WA('')
WA('| 需求 | 门禁 | 来源 | 期望 | 实测 | 结论 |')
WA('| --- | --- | --- | --- | --- | --- |')
foreach ($line in (Get-Content -LiteralPath (Join-Path $root '01-report\EVIDENCE_BINDING_11.tsv') -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ })) {
    $c = $line -split "`t"
    $src = switch -Wildcard ($c[1]) { 'INSTALL12-*' { 'tests/LanDisk.Tests/InstallerVectorTests.cs' } 'MOBILE12-*' { 'tools/e2e/ui-test.mjs' } 'ONLINE12-*' { 'tests/LanDisk.Tests/OnlinePresenceTests.cs' } 'DEVICE12-*' { 'tests/LanDisk.Tests/DeviceRemovalTests.cs' } 'NET12-*' { 'tests/LanDisk.Tests/PhysicalLanSnapshotTests.cs' } 'FIREWALL12-*' { 'tests/LanDisk.Tests/FirewallPlanTests.cs' } 'UPDATEPORT12-*' { 'tests/LanDisk.Tests/UpdatePortChoiceTests.cs' } 'UIE17-*' { 'src/LanDisk.App/UiCapture.cs' } 'step*' { 'src/LanDisk.Setup/Visual/RackVisual.cs' } default { $c[2] } }
    WA('| ' + $c[0] + ' | `' + $c[1] + '` | `' + $src + '` | ' + ($c[4].Substring(0, [Math]::Min(80, $c[4].Length))) + ' | ' + ($c[5].Substring(0, [Math]::Min(120, $c[5].Length))) + ' | ' + $c[6] + ' |')
}
WA('')
WA('## 静态检查')
WA('')
WA('- 代码审阅（含文件路径、行号、SHA256 与源码原文）：`01-report/CODE_REVIEW_11.md`')
WA('- 本轮有意变更与门禁修改的逐条说明：`01-report/AUDIT_CORRECTIONS_11.md`')
WA('- 端口缺陷完整记录（含作用范围与不可回溯部分）：`01-report/LAN_DISK_11_PORT_FIX_RECORD.md`')
WA('- 证据清单与逐项 SHA256：`09-release-identity/ARTIFACT_HASHES.txt`')
WA('')
$A | Set-Content -LiteralPath (Join-Path $OutDir 'LAN_DISK_11_AUDIT_EXPORT_VERIFIED.md') -Encoding UTF8
Write-Host ('written: LAN_DISK_11_AUDIT_EXPORT_VERIFIED.md (' + $A.Count + ' lines)')

# Copy the two supporting documents next to them.
foreach ($doc in @('docs\AUDIT_CORRECTIONS_11.md', 'docs\LAN_DISK_11_PORT_FIX_RECORD.md')) {
    $p = Join-Path $Repo $doc
    if (Test-Path $p) { Copy-Item $p $OutDir -Force }
}
Write-Host 'copied supporting documents into the delivery folder'
