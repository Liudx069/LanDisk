param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$RunId = '',
    [string]$EvidenceRoot = '',
    [string]$OutFile = '',
    [switch]$Diagnostic
)

# 需求—证据绑定表生成器。
#
# 设计要点（对应原任务第二节）：
#   * 每个需求声明**必须被测量的页面**。只用全局 SUMMARY 的非零总数，无法排除"需求所指的那个页面
#     根本没被检查过"，所以逐页（PAGE 行）建立对应。
#   * 页面上**本来就没有该目标**（例如 HTTPS 页没有工具栏按钮）记 NOT_APPLICABLE 并写明理由，
#     不凑数、也不因此判失败。
#   * 必须被测量的页面目标数为 0、或有失败，则该需求 BLOCKED —— 不会被标成 CLOSED。
#   * 导航正确不等于布局正确：本表只引用布局测量，不引用 UIE17 导航测试或 DEVICE12 删除语义测试。
#   * 正式候选尚未产生时，正式路径/哈希写 PENDING，整份输出标 DRAFT/NOT_READY。

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
if (-not $RunId) { $RunId = Resolve-EvidenceRunId -Repo $Repo }
if (-not $EvidenceRoot) { $EvidenceRoot = Join-Path $Repo "dist\evidence-11\$RunId" }
if (-not $OutFile) { $OutFile = Join-Path $Repo 'dist\delivery\EVIDENCE_BINDING_11.tsv' }

$version = (Select-String -Path (Join-Path $Repo 'src\LanDisk.App\LanDisk.App.csproj') -Pattern '<Version>(.*)</Version>').Matches.Groups[1].Value
$commit = (& git -C $Repo rev-parse HEAD).Trim()
$candidate = "$version@$commit"

# ---- locate the layout evidence (a run folder or a capture output folder)
$layoutPath = Join-Path $EvidenceRoot 'UI_LAYOUT_VERIFICATION.tsv'
if (-not (Test-Path $layoutPath)) {
    $found = Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -Filter 'UI_LAYOUT_VERIFICATION.tsv' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $layoutPath = $found.FullName }
}
if (-not (Test-Path $layoutPath)) { throw "找不到布局证据 UI_LAYOUT_VERIFICATION.tsv：$EvidenceRoot" }
$layoutDir = Split-Path $layoutPath -Parent
$envPath = Join-Path $layoutDir 'UI_CAPTURE_ENVIRONMENT.tsv'
$capturePath = Join-Path $layoutDir 'UI_CAPTURE.tsv'
$summaryPath = Join-Path $layoutDir 'UI_CAPTURE_SUMMARY.tsv'

function Sha([string]$p) { if (Test-Path $p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() } else { 'MISSING' } }
$layoutSha = Sha $layoutPath
$envSha = Sha $envPath
$captureSha = Sha $capturePath

# ---- read the evidence
$pageRows = @{}
foreach ($line in (Get-Content -LiteralPath $layoutPath -Encoding UTF8)) {
    $c = $line -split "`t"
    if ($c.Count -lt 9 -or $c[0] -ne 'PAGE') { continue }
    $pageRows[$c[1] + '||' + $c[3]] = [pscustomobject]@{ assertion = $c[1]; page = $c[3]; targets = [int]$c[4]; failures = [int]$c[5]; status = $c[6] }
}
$env = @{}
if (Test-Path $envPath) {
    foreach ($line in (Get-Content -LiteralPath $envPath -Encoding UTF8)) {
        $p = $line -split "`t"; if ($p.Count -ge 2) { $env[$p[0]] = $p[1] }
    }
}
$captureRows = @()
if (Test-Path $capturePath) {
    $captureRows = @(Get-Content -LiteralPath $capturePath -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ } |
        ForEach-Object { $c = $_ -split "`t"; if ($c.Count -ge 10) { [pscustomobject]@{ page = $c[4]; theme = $c[1]; scale = $c[2]; size = $c[3]; result = $c[8] } } })
}
$capturePass = @($captureRows | Where-Object { $_.result -eq 'PASS' }).Count
$captureFail = @($captureRows | Where-Object { $_.result -eq 'FAIL' }).Count
$pagesSeen = @($captureRows | Select-Object -ExpandProperty page -Unique | Sort-Object)

# ---- the declaration: which pages a requirement MUST have measured
$requirements = @(
    @{ Id='REAL11-03'; Assertion='LAYOUT-CORE-VALUE-NOT-CLIPPED'; RequiredPages=@('概览','运营看板')
       Implementation='src/LanDisk.App/UiCapture.cs, src/LanDisk.App/Ui.cs (PrimaryTag), src/LanDisk.App/AdaptiveTable.cs'
       Detail='核心状态值不被省略号截断：比较文字实际需要的宽度与分配到的宽度（DesiredSize 已被测量约束夹住，无法用于此项）' },
    @{ Id='REAL11-04'; Assertion='LAYOUT-BUTTON-SPACING'; RequiredPages=@('文件与共享','设置')
       Implementation='src/LanDisk.App/UiCapture.cs, src/LanDisk.App/Ui.cs'
       Detail='同一行相邻按钮间距不得小于 8 DIP（重叠检测看不到"贴在一起"，宽度断言也看不到）' },
    @{ Id='REAL11-05'; Assertion='LAYOUT-ACTION-OVERLAP'; RequiredPages=@('设备')
       Implementation='src/LanDisk.App/UiCapture.cs, src/LanDisk.App/AdaptiveTable.cs'
       Detail='设备操作区的可见按钮矩形两两求交，任何相交即失败' },
    @{ Id='REAL11-09'; Assertion='LAYOUT-EXTENSION-LONG-CONTENT'; RequiredPages=@('扩展程序')
       Implementation='src/LanDisk.App/ExtensionsPage.cs, src/LanDisk.App/CaptureEnvironment.cs'
       Detail='扩展页必须真的渲染出超长名称/发布者示例（否则溢出断言等于没跑），并由上面的溢出/重叠测量覆盖' }
)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('requirement_id' + "`t" + 'assertion_id' + "`t" + 'implementation_file' + "`t" + 'evidence_file' + "`t" +
           'evidence_sha256' + "`t" + 'page' + "`t" + 'combination_scope' + "`t" + 'targets_checked' + "`t" +
           'failures' + "`t" + 'expected' + "`t" + 'actual' + "`t" + 'status' + "`t" + 'run_id' + "`t" + 'candidate_identity')

$summary = New-Object System.Collections.Generic.List[string]
$overall = 'DRAFT'
if ($captureFail -gt 0) { $overall = 'NOT_READY' }

foreach ($req in $requirements) {
    $requiredOk = $true
    # 必须被测量的页面
    foreach ($page in $req.RequiredPages) {
        $key = $req.Assertion + '||' + $page
        if (-not $pageRows.ContainsKey($key)) {
            $requiredOk = $false
            $lines.Add(($req.Id + "`t" + $req.Assertion + "`t" + $req.Implementation + "`t" + $layoutPath + "`t" + $layoutSha + "`t" +
                        $page + "`t" + 'REQUIRED' + "`t" + '0' + "`t" + '0' + "`t" + '该页面必须有测量记录' + "`t" +
                        '证据里没有这一页的该断言记录' + "`t" + 'EVIDENCE_MISSING' + "`t" + $RunId + "`t" + $candidate))
            continue
        }
        $r = $pageRows[$key]
        $status = if ($r.failures -gt 0) { 'BLOCKED' }
                  elseif ($r.targets -le 0) { 'BLOCKED' }
                  else { 'CLOSED' }
        if ($status -ne 'CLOSED') { $requiredOk = $false }
        $expected = if ($r.targets -le 0) { '该页面必须存在可测量的目标（目标数 > 0）' } else { '目标数 > 0 且失败数 = 0' }
        $actual = ("目标 {0}，失败 {1}" -f $r.targets, $r.failures)
        $lines.Add(($req.Id + "`t" + $req.Assertion + "`t" + $req.Implementation + "`t" + $layoutPath + "`t" + $layoutSha + "`t" +
                    $page + "`t" + 'REQUIRED' + "`t" + $r.targets + "`t" + $r.failures + "`t" + $expected + "`t" + $actual + "`t" +
                    $status + "`t" + $RunId + "`t" + $candidate))
    }
    # 其余页面：该页面上本来就没有这类目标 → NOT_APPLICABLE，写明理由，不凑数
    foreach ($page in $pagesSeen) {
        if ($req.RequiredPages -contains $page) { continue }
        $key = $req.Assertion + '||' + $page
        if (-not $pageRows.ContainsKey($key)) { continue }
        $r = $pageRows[$key]
        $status = if ($r.failures -gt 0) { 'BLOCKED' } else { 'NOT_APPLICABLE' }
        if ($r.failures -gt 0) { $requiredOk = $false }
        $reason = if ($r.targets -le 0) { '该页面没有这类目标（例如无工具栏按钮 / 非设备页），不计入必需覆盖' }
                  else { '该页面存在目标且无失败；不属于该需求的必需页，仅作附带覆盖记录' }
        if ($r.failures -gt 0) { $reason = '非必需页面上出现了失败，同样阻断' }
        $lines.Add(($req.Id + "`t" + $req.Assertion + "`t" + $req.Implementation + "`t" + $layoutPath + "`t" + $layoutSha + "`t" +
                    $page + "`t" + 'INCIDENTAL' + "`t" + $r.targets + "`t" + $r.failures + "`t" +
                    '非必需页面：目标数为 0 或无失败即可' + "`t" + ("目标 {0}，失败 {1}" -f $r.targets, $r.failures) + "`t" +
                    $status + "`t" + $RunId + "`t" + $candidate))
    }
    $summary.Add(($req.Id + "`t" + $req.Assertion + "`t" + ($req.RequiredPages -join ',') + "`t" + $(if ($requiredOk) { 'CLOSED' } else { 'BLOCKED' })))
}

# ---- run-level facts, so the table cannot be read without its environment
$lines.Add(('RUN-ENV' + "`t" + 'UI_CAPTURE_ENVIRONMENT' + "`t" + '' + "`t" + $envPath + "`t" + $envSha + "`t" + '' + "`t" +
            'ALL' + "`t" + $env['UI_LAYOUT_TEXTBLOCKS_INSPECTED'] + "`t" + $env['UI_LAYOUT_ASSERTIONS_FAILED'] + "`t" +
            '网络页渲染确定性网卡夹具、检查到控件数 > 0' + "`t" +
            ("夹具渲染 {0}，可见物理网卡 {1}，检查控件 {2}" -f $env['R2_NATIVE_UI_RENDER'], $env['R2_VISIBLE_ADAPTER'], $env['UI_LAYOUT_TEXTBLOCKS_INSPECTED']) + "`t" +
            $(if ($env['R2_NATIVE_UI_RENDER'] -eq 'PASS' -and $env['R2_VISIBLE_ADAPTER'] -eq 'true') { 'CLOSED' } else { 'BLOCKED' }) + "`t" + $RunId + "`t" + $candidate))
$lines.Add(('RUN-MATRIX' + "`t" + 'UI_CAPTURE' + "`t" + '' + "`t" + $capturePath + "`t" + $captureSha + "`t" + '' + "`t" +
            ("{0} 个组合 / {1} 个页面" -f $captureRows.Count, $pagesSeen.Count) + "`t" + $captureRows.Count + "`t" + $captureFail + "`t" +
            '必需组合无失败' + "`t" + ("通过 {0}，失败 {1}" -f $capturePass, $captureFail) + "`t" +
            $(if ($captureFail -eq 0) { 'CLOSED' } else { 'BLOCKED' }) + "`t" + $RunId + "`t" + $candidate))

$lines | Set-Content -LiteralPath $OutFile -Encoding UTF8

$hdr = @('# 证据绑定表（需求—证据绑定）')
$hdr += ''
$hdr += ('- 生成时间：' + (Get-Date -Format 'o'))
$hdr += ('- run_id：' + $RunId)
$hdr += ('- 构建身份（生成本表时的 HEAD）：' + $commit)
$hdr += ('- 候选身份：' + $candidate)
$hdr += ('- 整体状态：**' + $overall + '**')
$hdr += ('- 证据来源目录：' + $layoutDir)
$hdr += ('- 证据文件 SHA256：布局 ' + $layoutSha.Substring(0, 16) + '… / 环境 ' + $envSha.Substring(0, 16) + '… / 矩阵 ' + $captureSha.Substring(0, 16) + '…')
$hdr += ''
$hdr += '> 本表只引用**布局测量**。导航测试（UIE17）与设备删除语义测试（DEVICE12）不构成本表中任何一条的证据。'
$hdr += '> 必需页面目标数为 0、或出现任何失败，该需求即为 BLOCKED，不会被标成 CLOSED。'
$hdr += '> 非必需页面若本来没有该目标，记 NOT_APPLICABLE 并写明理由，不凑数。'
$hdr += ''
$hdr += '| 需求 | 断言 | 必需页面 | 结论 |'
$hdr += '| --- | --- | --- | --- |'
foreach ($s in $summary) { $c = $s -split "`t"; $hdr += ('| ' + $c[0] + ' | `' + $c[1] + '` | ' + $c[2] + ' | ' + $c[3] + ' |') }
$hdr | Set-Content -LiteralPath ($OutFile + '.md') -Encoding UTF8

Write-Host ('written: ' + $OutFile)
Write-Host ('  evidence dir : ' + $layoutDir)
Write-Host ('  run_id       : ' + $RunId)
Write-Host ('  overall      : ' + $overall)
foreach ($s in $summary) { $c = $s -split "`t"; Write-Host ('  {0,-10} {1,-32} required={2,-14} {3}' -f $c[0], $c[1], $c[2], $c[3]) }
if ($Diagnostic) { Write-Host '  (diagnostic run: the formal candidate paths/hashes stay PENDING until the final acceptance)' }
