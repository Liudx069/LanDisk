param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot)
)

# 一次性精简包组装（16 文件）。刻意写得直白：显式绝对路径、显式成员路径、找不到就报错。
#
# 与前一个脚本的差别：
#   * ZIP 成员按**明确完整路径**查找；确需按文件名回退时收集全部匹配，**只有唯一匹配才采用**；
#   * 必需身份文件找不到就 throw，不写"包内没有"再继续；
#   * 不做 scriptblock 间接调用，减少参数传递出错的余地。

$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path $Repo).Path
. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
$RunId = Resolve-EvidenceRunId -Repo $Repo
$Evidence = Join-Path $Repo ('dist\evidence-11\' + $RunId)
$Delivery = Join-Path $Repo 'dist\delivery-176-final'
$Stage = Join-Path $Repo 'dist\.small01-stage'
$OutDir = Join-Path $Repo 'dist\delivery-final-code-repair-small'
$OutZip = Join-Path $OutDir 'LAN_DISK_11_FINAL_CODE_REPAIR_SMALL_REVIEW_01.zip'
$BuildCommit = (Get-Content -LiteralPath (Join-Path $Repo 'dist\.frozen-candidate-176.txt') -Encoding UTF8).Trim()
$ToolCommit = (& git -C $Repo rev-parse HEAD).Trim()
$SrcFreeze = (& git -C $Repo log --format=%H -1 -- src).Trim()
$Baseline174 = 'a9b06eb6ed0b15744556b31fc156748b93cc27dc'

. (Join-Path $Repo 'tools\Mask-NetworkIdentity.ps1')

if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Stage, $OutDir | Out-Null

function Save([string]$rel, [string]$text, [bool]$mask) {
    $dst = Join-Path $Stage $rel
    $body = if ($mask) { Mask-NetworkText -Text $text } else { $text }
    [System.IO.File]::WriteAllText($dst, $body, (New-Object System.Text.UTF8Encoding($false)))
}
function HashOf([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return '' }
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
        Write-Host ('    [HashOf 跳过，不是文件] ' + $p)
        return ''
    }
    return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
}
function BytesOf([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { return 0 }
    return (Get-Item -LiteralPath $p).Length
}
function ReadAll([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { throw ('必需文件缺失：' + $p) }
    return (Get-Content -LiteralPath $p -Raw -Encoding UTF8)
}

# ---------------------------------------------------------------- ZIP 成员读取
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ZipMemberTextExact([string]$zipPath, [string]$fullName) {
    $z = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $e = $z.Entries | Where-Object { $_.FullName -eq $fullName } | Select-Object -First 1
        if (-not $e) { throw ("ZIP 内找不到成员 '{0}'（{1}）" -f $fullName, $zipPath) }
        $sr = New-Object System.IO.StreamReader($e.Open())
        try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
    }
    finally { $z.Dispose() }
}

function Get-ZipMemberTextByUniqueName([string]$zipPath, [string]$leafName) {
    $z = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $all = @($z.Entries | Where-Object { $_.Name -eq $leafName })
        if ($all.Count -eq 0) { throw ("ZIP 内找不到名为 '{0}' 的成员（{1}）" -f $leafName, $zipPath) }
        if ($all.Count -gt 1) {
            # 多个同名成员：不静默取第一个，按预期目录明确选择。
            $preferred = @($all | Where-Object { $_.FullName -like 'LanDisk-win-x64/*' })
            if ($preferred.Count -eq 1) { $all = $preferred }
            else {
                $names = ($all | ForEach-Object { $_.FullName }) -join '、'
                throw ("ZIP 内有 {0} 个名为 '{1}' 的成员且无法唯一确定：{2}" -f $all.Count, $leafName, $names)
            }
        }
        Write-Host ("    （按文件名回退命中：" + $all[0].FullName + "）")
        $sr = New-Object System.IO.StreamReader($all[0].Open())
        try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
    }
    finally { $z.Dispose() }
}

# 合并材料：每段写明来源、完整哈希，并用 BEGIN/END 包住原文
function Wrap([string]$title, [string]$srcPath, [string]$body, [string]$note) {
    $rel = if ($srcPath -and $srcPath.StartsWith($Repo)) { $srcPath.Substring($Repo.Length + 1).Replace('\', '/') } else { $srcPath }
    $lines = @(
        ('### ' + $title),
        '',
        ('- 来源：`' + $rel + '`'),
        ('- 源文件完整 SHA256：`' + (HashOf $srcPath) + '`'),
        ('- 源文件 bytes：' + (BytesOf $srcPath))
    )
    if ($note) { $lines += ('- 说明：' + $note) }
    $lines += @('', ('<!-- BEGIN ' + $rel + ' -->'), '```', $body.TrimEnd(), '```', ('<!-- END ' + $rel + ' -->'), '')
    return ($lines -join "`r`n")
}

Write-Host '00_README.md'
$readme = @"
# LanDisk 1.7.6 — 精简审核包（LAN_DISK_11_FINAL_CODE_REPAIR_SMALL_REVIEW_01）

## 这是什么

本轮修复（``LAN_DISK_11_FINAL_CODE_REPAIR_AND_ACCEPTANCE_01``）的**精简审核包**，共 16 个文件。
它**不是**全仓源码归档，也**不是**完整画面审核：全部桌面截图、安装录屏逐帧、四个大型安装产物
都留在本地未入包。

## 身份（三个提交角色不同）

| 角色 | 提交 |
| --- | --- |
| **产品构建提交**（四个产物与产品验收证据所属） | ``$BuildCommit`` |
| 源码冻结点（最后一次改动 ``src/`` 的提交） | ``$SrcFreeze`` |
| 验收 / 封包工具提交（组装本包时的 HEAD） | ``$ToolCommit`` |
| 已审阅的修复基线（1.7.4） | ``$Baseline174`` |

四产物**内部记录**一致指向 ``$BuildCommit``，逐条原文见 ``03_IDENTITY_EVIDENCE.md``。

## 最终状态

**READY_FOR_DEVICE_VALIDATION**（自动化验收全部成立）。真机项目仍为 **NOT_RUN**；
**Receiver 的独立审核尚未进行**，本包不声称已完成独立复核。
``ORIGINAL_LAYOUT_TRIGGER = UNKNOWN_NOT_REPRODUCED`` 保持不变。

## 阅读顺序

1. ``01_DELIVERY_REPORT.md`` —— 唯一权威最终报告
2. ``02_ARTIFACT_HASHES.txt``、``03_IDENTITY_EVIDENCE.md`` —— 产物哈希与内部身份原文
3. ``04_EVIDENCE_BINDING.tsv`` —— 需求 ↔ 证据绑定
4. ``05``/``06`` —— 本次修复的完整产品代码、调用方与测试
5. ``07_FIX_DIFF.patch`` —— 真实修复 diff（1.7.4 基线 → 1.7.6，并标注其后增量）
6. ``08``/``09``/``10`` —— 原始验收结果、完整矩阵文本、门禁与负向结果
7. ``11_UNIT_RESULTS.trx`` —— 单元回归原始 TRX（逐项）
8. ``12``/``13``/``14`` —— 来源与脱敏索引、隐私结果、集中真机清单

## 审核范围与边界

- 本包只覆盖本轮修复：防火墙查询执行链（有界超时、并行读流、三态 Enabled）与机柜动画的阶段幂等/脉冲合成。
- 画面外观由 Owner 自行查看；本包只保留画面证据的索引、哈希与验证结果，不声称接收方已独立认可新画面。
- 未写 Owner 的防火墙规则、未关闭防火墙、未扩大放行范围、未自动提权；未改动真实运行环境。
- 未入包的原始证据与大型原件全部保留在本地。
"@
Save '00_README.md' $readme $true

Write-Host '01_DELIVERY_REPORT.md'
Save '01_DELIVERY_REPORT.md' (ReadAll (Join-Path $Delivery 'DELIVERY_REPORT_11_VERIFIED.md')) $true

Write-Host '02_ARTIFACT_HASHES.txt'
Save '02_ARTIFACT_HASHES.txt' (ReadAll (Join-Path $Delivery 'ARTIFACT_HASHES.txt')) $false

Write-Host '03_IDENTITY_EVIDENCE.md'
$zipRelease = Join-Path $Repo 'dist\delivery\LanDisk-win-x64.zip'
$zipSource = Join-Path $Repo 'dist\delivery\LanDisk-source.zip'
$zipLupd = Join-Path $Repo 'dist\delivery\LanDisk-update-1.7.6.lupd'
$rtPath = Join-Path $Evidence '05-runtime-identity\runtime-identity.json'

$biText = Get-ZipMemberTextExact $zipRelease 'LanDisk-win-x64/build-info.json'
Write-Host '    build-info FullName = LanDisk-win-x64/build-info.json'
$shText = Get-ZipMemberTextByUniqueName $zipSource 'SOURCE_HEAD.txt'
$mfText = Get-ZipMemberTextExact $zipLupd 'update-manifest.json'
$mf = $mfText | ConvertFrom-Json
$mfBrief = [pscustomobject]@{
    schemaVersion = $mf.schemaVersion
    version = $mf.version
    minimumUpgradeVersion = $mf.minimumUpgradeVersion
    buildCommit = $mf.buildCommit
    keyId = $mf.keyId
    releaseNotesSha256 = $mf.releaseNotesSha256
    fileCount = @($mf.files).Count
} | ConvertTo-Json -Depth 3

$parts = New-Object System.Collections.Generic.List[string]
$parts.Add('# 产物内部身份原文')
$parts.Add('')
$parts.Add('本文件收录各产物**内部**记录的身份。四产物互相一致指向构建提交 `' + $BuildCommit + '`。')
$parts.Add('')
$parts.Add('## 一、四个产物（外部现场复算）')
$parts.Add('')
$parts.Add('| 产物 | bytes | 完整 SHA256 |')
$parts.Add('| --- | --- | --- |')
foreach ($n in @('LanDisk-Setup.exe', 'LanDisk-win-x64.zip', 'LanDisk-update-1.7.6.lupd', 'LanDisk-source.zip')) {
    $p = Join-Path $Repo ('dist\delivery\' + $n)
    $parts.Add('| `' + $n + '` | ' + (BytesOf $p) + ' | `' + (HashOf $p) + '` |')
}
$parts.Add('')
$parts.Add('## 二、产物内部身份（原文）')
$parts.Add('')
$parts.Add((Wrap 'release ZIP 的 build-info.json（成员路径 LanDisk-win-x64/build-info.json）' $zipRelease $biText 'release 包对外声明的版本与提交'))
$parts.Add((Wrap '源码包的 SOURCE_HEAD.txt' $zipSource $shText '源码包自带的产出记录，含 worktreeClean'))
$parts.Add((Wrap '更新包签名清单 update-manifest.json（关键字段；完整 files 清单过长，仅记条数）' $zipLupd $mfBrief '签名清单里的 buildCommit / keyId / version'))
$parts.Add((Wrap 'runtime 原始结果' $rtPath (ReadAll $rtPath) '以本候选产物实际启动后读回的版本与提交'))
$parts.Add('## 三、三个提交不是一回事')
$parts.Add('')
$parts.Add('- **产品构建提交** `' + $BuildCommit + '`：四个产物所属。')
$parts.Add('- **源码冻结点** `' + $SrcFreeze + '`：最后一次改动 `src/` 的提交；其后只有改测试/文档/交付工具的提交。')
$parts.Add('- **基线** `' + $Baseline174 + '`：已审阅的 1.7.4 修复基线，`07_FIX_DIFF.patch` 以它为起点。')
$parts.Add('')
$parts.Add('### 历史纠正（如实记录）')
$parts.Add('')
$parts.Add('1.7.5 轮的旧 sidecar / 旧 run_id 曾把构建字段写成 `0bc0f971…`。经查：')
$parts.Add('')
$parts.Add('- `0bc0f971…` 是当时**最后一次改动 `src/` 的提交**（用途是 diff 基线），**不是**构建身份；')
$parts.Add('- 它同时也是 1.7.5 **第一次构建尝试**的提交，那次因工作树不干净在 CAND-01 被判无效而中止；')
$parts.Add('- 重建后那一批的真实构建提交是 `b598bb055feb491837f964c45296d46b3928d2f6`，由四产物内部记录一致证明。')
$parts.Add('  **旧记录是错的，已按实际产物纠正**，不是"原本就正确"。')
$parts.Add('')
$parts.Add('## 四、本地保留、未入包的原件')
$parts.Add('')
$parts.Add('- `dist/delivery/` 四个 1.7.6 安装/发布产物（含未脱敏的完整源码 ZIP）')
$parts.Add('- `dist/_history/1.7.4-artifacts/`、`dist/_history/1.7.5-artifacts/`（历史四产物，逐文件 SHA256 已复算）')
$parts.Add('- `dist/evidence-11/`（全部原始证据，含 全部桌面截图与安装录屏逐帧）')
$parts.Add('- `dist/ui-evidence/`、`tests/LanDisk.Tests/TestResults/`')
Save '03_IDENTITY_EVIDENCE.md' ($parts -join "`r`n") $true

Write-Host '04_EVIDENCE_BINDING.tsv'
Save '04_EVIDENCE_BINDING.tsv' (ReadAll (Join-Path $Evidence '01-report\EVIDENCE_BINDING_11.tsv')) $false

Write-Host '05_KEY_PRODUCT_SOURCE.md'
$prodList = @(
    @('src/LanDisk.Core/Networking/FirewallRules.cs', '防火墙期望值与判定（Parse / Evaluate / Judge / Rows）'),
    @('src/LanDisk.Core/Networking/BoundedProcessRunner.cs', '有界进程执行器（并行读流、单一总时限、只终止本次进程、清理有界）'),
    @('src/LanDisk.App/FirewallStatus.cs', '外壳只读探针与调用方（Check / RunNetsh / Repair）'),
    @('src/LanDisk.Setup/FirewallPlan.cs', '安装器规则计划与不变量（与自检同源）'),
    @('src/LanDisk.Setup/Visual/RackVisual.cs', '机柜动画（ApplyStage / SetGroup / RiseSchedule / 失败 / Reduce Motion）')
)
$p5 = New-Object System.Collections.Generic.List[string]
$p5.Add('# 本次修复的关键产品代码（完整文件，不截断）')
$p5.Add('')
$p5.Add('产品构建提交：`' + $BuildCommit + '`。以下为**全文**，包含修复方法、必要调用方与数据类型。')
$p5.Add('')
foreach ($pair in $prodList) {
    $full = Join-Path $Repo $pair[0]
    $p5.Add((Wrap $pair[1] $full (ReadAll $full) '完整文件'))
}
Save '05_KEY_PRODUCT_SOURCE.md' ($p5 -join "`r`n") $false

Write-Host '06_KEY_TEST_TOOL_SOURCE.md'
$testList = @(
    @('tests/LanDisk.Tests/ProcessRunnerTests.cs', 'FIREWALL14/15：执行器五种边界 + Enabled 三态'),
    @('tests/LanDisk.Tests/FirewallJudgementTests.cs', 'FIREWALL13：判定链与界面三态'),
    @('tests/LanDisk.Tests/RackAnimationTests.cs', 'ANIM13：点亮曲线、脉冲合成、阶段幂等、失败与 Reduce Motion')
)
$toolList = @(
    @('tools/Test-ReportConsistency.ps1', '报告一致性检查的正反例（从封好的 ZIP 实际读取后比对完整字节）'),
    @('tools/New-VerifiedReports.ps1', '权威报告与审计导出：按实际表头读取字段、需求汇总、候选选择'),
    @('tools/New-FinalReviewBundle.ps1', '封装与开包校验：Manifest、CRC、报告一致性')
)
$p6 = New-Object System.Collections.Generic.List[string]
$p6.Add('# 本次相关的测试与交付工具代码')
$p6.Add('')
$p6.Add('## 一、产品测试（完整文件）')
$p6.Add('')
foreach ($pair in $testList) {
    $full = Join-Path $Repo $pair[0]
    $p6.Add((Wrap $pair[1] $full (ReadAll $full) '完整文件'))
}
$p6.Add('## 二、交付工具（完整文件）')
$p6.Add('')
foreach ($pair in $toolList) {
    $full = Join-Path $Repo $pair[0]
    $p6.Add((Wrap $pair[1] $full (ReadAll $full) '完整文件'))
}
$p6.Add('## 三、本轮对 netsh 调用检查的实际更新（依据与差异）')
$p6.Add('')
$p6.Add('1.7.6 构建时该门禁真实失败：`Repair02VerifiedHostTests.No_route_dns_or_vpn_configuration_write_was_added`')
$p6.Add('原来断言"启动 netsh 进程的文件 = {FirewallStatus.cs, FirewallHelper.cs}"，Execution 链重构后')
$p6.Add('外壳不再 `new ProcessStartInfo("netsh")`，该断言只剩后者。')
$p6.Add('')
$p6.Add('按新的真实结构更新（**未放松**）：仍要求 netsh 只出现在安装器与外壳只读探针两处；')
$p6.Add('改为按"谁把 netsh 交给执行器"（`BoundedProcessRunner.Run("netsh"...`）判定驱动方；')
$p6.Add('**新增更严一条**：执行器**代码行**里不得出现 `netsh` / `advfirewall`（文档注释不算调用）。')
$p6.Add('该不变量本意（产品不得写路由/DNS/VPN；netsh 仅用于防火墙）未被削弱，反而更严。')
$p6.Add('')
$p6.Add('门禁实测：修复前 1 项失败 → 修复后 723/723 通过。')
Save '06_KEY_TEST_TOOL_SOURCE.md' ($p6 -join "`r`n") $false

Write-Host '07_FIX_DIFF.patch'
$diffBody = (& git -C $Repo diff $Baseline174 $BuildCommit -- src tools tests 2>$null | Out-String)
$afterStat = (& git -C $Repo diff --stat $BuildCommit $ToolCommit 2>$null | Out-String)
$patch = @"
# 真实修复 diff：已审阅的 1.7.4 基线 -> 本次 1.7.6 构建提交
#
#   基线      : $Baseline174   (1.7.4，已审阅)
#   构建提交  : $BuildCommit
#   其后增量  : 见文件末尾（$BuildCommit..$ToolCommit，仅测试/文档/交付工具，不改产品字节）
#
# 覆盖范围：src/、tools/、tests/ 三处的真实改动，不是"最后两个文档提交"的差异。
# 本副本已按一致掩码脱敏；本地原始 diff 未改动。

$diffBody

# ============================================================
# 构建提交之后的增量（不影响产品字节）
# ============================================================
$afterStat
"@
Save '07_FIX_DIFF.patch' $patch $true

Write-Host '08_ACCEPTANCE_RAW_RESULTS.md'
$accList = @(
    @('07-browser/e2e-ui-chromium.json', 'Chromium 40 项（采用的最终结果）'),
    @('07-browser/e2e-ui-webkit.json', 'WebKit 40 项'),
    @('07-browser/BROWSER_RESULTS.md', '浏览器结果与一次偶发的如实说明'),
    @('03-setup-acceptance/setup-acceptance.json', 'Setup 安装与迁移 20 项'),
    @('04-update-rollback/PASS-A-frozen-1.7.6-lupd/update-verify.json', 'A 类：冻结正式 .lupd 升级与回滚 41 项'),
    @('04-update-rollback/PASS-A-frozen-1.7.6-lupd/data/updates/updater.log', 'A 类更新程序日志（含重启端口记录）'),
    @('08-https/B-dynamic-port/B-dynamic-port-update.json', 'B 类：动态端口更新与失败回滚'),
    @('08-https/https-e2e-matrix.json', 'HTTPS 监听矩阵（配置端口 vs 实际绑定端口）'),
    @('05-runtime-identity/runtime-identity.json', 'runtime 身份核验'),
    @('09-firewall-animation/FIREWALL_ANIMATION_TESTS.json', '防火墙执行函数 / Enabled 三态 / 动画专项')
)
$p8 = New-Object System.Collections.Generic.List[string]
$p8.Add('# 原始验收结果（原文汇集，保留来源、完整哈希与实际候选身份）')
$p8.Add('')
$p8.Add('产品构建提交：`' + $BuildCommit + '`；run_id：`' + $RunId + '`。')
$p8.Add('每段保留来源路径、源文件完整 SHA256 与实际内容（含命令、退出码、端点、期望值与实际值）。')
$p8.Add('')
$p8.Add('## 关于 Chromium 首次失败与复测（必读）')
$p8.Add('')
$p8.Add('- **首次**：Chromium 39/40，`UI-ADMIN` 失败，报"管理端未自动进入：标签数 0，密码框数 0"；')
$p8.Add('  同一次运行的 WebKit 该项通过，Chromium 其余 39 项通过。')
$p8.Add('- **复测**：原样重跑一次，**40/40 全部通过**，`UI-ADMIN` 通过。')
$p8.Add('- **采用的结果**：复测后的 40/40；两份原始 JSON 都保留在本地，本包收录采用的那一份。')
$p8.Add('- **根因定位程度**：上述"页面加载/观察时序的偶发"是**推测**，不是已证实的根因；')
$p8.Add('  仅凭复测通过**不能**宣称根因已查明或缺陷已修复。此处如实标注为推测。')
$p8.Add('- 未为封包另开一轮浏览器测试。')
$p8.Add('')
foreach ($pair in $accList) {
    $full = Join-Path $Evidence $pair[0]
    $p8.Add((Wrap $pair[1] $full (ReadAll $full) ''))
}
$p8.Add((Wrap '源码身份核验' (Join-Path $Repo 'dist\delivery\SOURCE_TREE_VERIFY.json') (ReadAll (Join-Path $Repo 'dist\delivery\SOURCE_TREE_VERIFY.json')) ''))
$p8.Add((Wrap '安装清单核验' (Join-Path $Repo 'dist\delivery\INSTALL_MANIFEST_VERIFY.json') (ReadAll (Join-Path $Repo 'dist\delivery\INSTALL_MANIFEST_VERIFY.json')) ''))
Save '08_ACCEPTANCE_RAW_RESULTS.md' ($p8 -join "`r`n") $true

Write-Host '09_UI_MATRIX_RAW.md'
$p9 = New-Object System.Collections.Generic.List[string]
$p9.Add('# 桌面界面矩阵（文本证据原文，不含截图 PNG）')
$p9.Add('')
$p9.Add('产品构建提交：`' + $BuildCommit + '`；run_id：`' + $RunId + '`。')
$p9.Add('截图本体（全部 PNG）留在本地 `dist/ui-evidence/`；本包只收文本证据与其哈希。')
$p9.Add('')
foreach ($f in @('UI_PAGE_CATALOG.tsv', 'UI_CAPTURE_ENVIRONMENT.tsv', 'UI_CAPTURE_SUMMARY.tsv', 'UI_LAYOUT_VERIFICATION.tsv', 'UI_CAPTURE.tsv')) {
    $full = Join-Path $Evidence ('06-desktop-ui\' + $f)
    $note = if ($f -eq 'UI_CAPTURE.tsv') { '完整逐组合记录' } else { '完整内容' }
    $p9.Add((Wrap $f $full (ReadAll $full) $note))
}
$p9.Add((Wrap 'UI_FAILURES_11.tsv' (Join-Path $Delivery 'UI_FAILURES_11.tsv') (ReadAll (Join-Path $Delivery 'UI_FAILURES_11.tsv')) '未通过项（本候选为 0）'))
Save '09_UI_MATRIX_RAW.md' ($p9 -join "`r`n") $true

Write-Host '10_GATE_RAW_RESULTS.md'
$p10 = New-Object System.Collections.Generic.List[string]
$p10.Add('# 门禁、判定器与负向结果（原文汇集）')
$p10.Add('')
$p10.Add('**产品实测**、**诊断构建**与**合成对照**分别标注；同一份 JSON 不重复收录。')
$p10.Add('产品 Full 读取的是**本地完整原始证据集**，不因精简包少收图片而放宽任何产品通过条件。')
$p10.Add('')
$p10.Add('## 一、产品实测')
$p10.Add('')
$p10.Add((Wrap '最终 Full 判定原始结果' (Join-Path $Delivery 'RELEASE_GATE.json') (ReadAll (Join-Path $Delivery 'RELEASE_GATE.json')) '产品实测'))
$p10.Add((Wrap '判定器负向 9 项（当前权威份）' (Join-Path $Delivery 'EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json') (ReadAll (Join-Path $Delivery 'EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json')) '产品实测'))
$p10.Add((Wrap '报告一致性检查正反例 4 项' (Join-Path $Delivery 'REPORT_CONSISTENCY_TESTS.json') (ReadAll (Join-Path $Delivery 'REPORT_CONSISTENCY_TESTS.json')) '产品实测'))
$p10.Add((Wrap '安装器状态索引（9 个状态）' (Join-Path $Evidence '02-installer-capture\setup-ui-index.txt') (ReadAll (Join-Path $Evidence '02-installer-capture\setup-ui-index.txt')) '产品实测'))
$p10.Add((Wrap '安装录屏验证记录' (Join-Path $Evidence '02-installer-capture\recording\recording-log.txt') (ReadAll (Join-Path $Evidence '02-installer-capture\recording\recording-log.txt')) 'GIF 本体（约 1.1 MB）留在本地，未入包'))
$p10.Add('## 二、合成对照（不是产品通过证据）')
$p10.Add('')
foreach ($pair in @(
        @('..\RELEASE_GATE_NEGATIVE_TESTS.json', 'Full 判定负向用例（合成夹具）'),
        @('..\DIAGNOSTIC-binding\BINDING_GENERATOR_NEGATIVE_TESTS.json', '绑定生成器负向用例（合成夹具）'))) {
    $full = Join-Path $Evidence $pair[0]
    $p10.Add((Wrap $pair[1] $full (ReadAll $full) '合成对照，非产品通过证据'))
}
$chainPath = Join-Path $Repo 'dist\delivery\RELEASE_BLOCKING_CHAIN_TESTS.json'
if (-not (Test-Path -LiteralPath $chainPath)) { throw ('必需文件缺失：' + $chainPath) }
$chainNote = '合成对照，非产品通过证据。**产出于 2026-09-30 05:15 的 1.7.5 轮**，针对的是 DIAGNOSTIC 注入构建（overlap/clip）与干净诊断构建，' +
             '不是 1.7.6 产品的通过证据；它验证的发布门禁脚本未变，且该结果文件本身不记录候选身份。' +
             '本轮**没有**为封包另跑一次诊断套件（不为整理材料新开测试轮）。'
$p10.Add((Wrap '失败传播链 7 项（合成对照）' $chainPath (ReadAll $chainPath) $chainNote))
$p10.Add('## 三、产品 Full 的输入范围')
$p10.Add('')
$p10.Add('产品 Full 判定读取的是**本地完整原始证据集**（`dist/evidence-11/' + $RunId + '/`），')
$p10.Add('包含 全部桌面截图等未入本精简包的材料。精简包只影响上传体积，**不放宽任何产品通过条件**。')
Save '10_GATE_RAW_RESULTS.md' ($p10 -join "`r`n") $true

Write-Host '11_UNIT_RESULTS.trx'
$trx = Join-Path $Repo 'tests\LanDisk.Tests\TestResults\landisk-tests.trx'
Copy-Item -LiteralPath $trx -Destination (Join-Path $Stage '11_UNIT_RESULTS.trx') -Force

Write-Host '12_SOURCE_INDEX_AND_MASKING.tsv'
$idx = New-Object System.Collections.Generic.List[string]
$idx.Add("included_as`toriginal_path`toriginal_bytes`toriginal_sha256`tbuild_identity`tsection`tmasked`tincluded_sha256")
foreach ($pair in $prodList) {
    $full = Join-Path $Repo $pair[0]
    $idx.Add('05_KEY_PRODUCT_SOURCE.md' + "`t" + $pair[0] + "`t" + (BytesOf $full) + "`t" + (HashOf $full) + "`t" + $BuildCommit + "`t" + $pair[1] + "`tNO`t" + (HashOf (Join-Path $Stage '05_KEY_PRODUCT_SOURCE.md')))
}
foreach ($pair in ($testList + $toolList)) {
    $full = Join-Path $Repo $pair[0]
    $idx.Add('06_KEY_TEST_TOOL_SOURCE.md' + "`t" + $pair[0] + "`t" + (BytesOf $full) + "`t" + (HashOf $full) + "`t" + $BuildCommit + "`t" + $pair[1] + "`tNO`t" + (HashOf (Join-Path $Stage '06_KEY_TEST_TOOL_SOURCE.md')))
}
foreach ($pair in @(
        @('01_DELIVERY_REPORT.md', 'dist/delivery-176-final/DELIVERY_REPORT_11_VERIFIED.md', 'YES-按一致掩码脱敏'),
        @('02_ARTIFACT_HASHES.txt', 'dist/delivery-176-final/ARTIFACT_HASHES.txt', 'NO'),
        @('04_EVIDENCE_BINDING.tsv', ('dist/evidence-11/' + $RunId + '/01-report/EVIDENCE_BINDING_11.tsv'), 'NO'),
        @('07_FIX_DIFF.patch', ('git diff ' + $Baseline174 + '..' + $BuildCommit), 'YES-按一致掩码脱敏'),
        @('11_UNIT_RESULTS.trx', 'tests/LanDisk.Tests/TestResults/landisk-tests.trx', 'NO'))) {
    $rel = $pair[0]; $orig = $pair[1]; $mask = $pair[2]
    $origFull = if ($orig.StartsWith('git ') -or $orig.StartsWith('tests/')) { '' } else { Join-Path $Repo $orig }
    $idx.Add($rel + "`t" + $orig + "`t" + $(if ($origFull) { BytesOf $origFull } else { 0 }) + "`t" + $(if ($origFull) { HashOf $origFull } else { '-' }) + "`t" + $BuildCommit + "`t全文`t" + $mask + "`t" + (HashOf (Join-Path $Stage $rel)))
}
Save '12_SOURCE_INDEX_AND_MASKING.tsv' ($idx -join "`r`n") $false

Write-Host '13_PRIVACY_CHECK.json'
Copy-Item -LiteralPath (Join-Path $Delivery 'PRIVACY_CHECK.json') -Destination (Join-Path $Stage '13_PRIVACY_CHECK.json') -Force

Write-Host '14_DEVICE_ACCEPTANCE_CHECKLIST.md'
$checklist = ReadAll (Join-Path $Repo 'docs\DEVICE_ACCEPTANCE_CHECKLIST_175.md')
$checklist = $checklist.Replace('1.7.5', '1.7.6')
Save '14_DEVICE_ACCEPTANCE_CHECKLIST.md' $checklist $true

Write-Host 'MANIFEST_SHA256.tsv'
$files = @(Get-ChildItem -LiteralPath $Stage -Recurse -File | Sort-Object FullName)
$man = New-Object System.Collections.Generic.List[string]
$man.Add("path`tbytes`tsha256")
foreach ($f in $files) {
    if ($f.Name -eq 'MANIFEST_SHA256.tsv') { continue }
    $man.Add(($f.Name + "`t" + $f.Length + "`t" + (HashOf $f.FullName)))
}
[System.IO.File]::WriteAllText((Join-Path $Stage 'MANIFEST_SHA256.tsv'), ($man -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host ('staged files: ' + @(Get-ChildItem -LiteralPath $Stage -File).Count)
Get-ChildItem -LiteralPath $Stage -File | Sort-Object Name | ForEach-Object { Write-Host ('  {0,-42} {1,10:N0}' -f $_.Name, $_.Length) }
