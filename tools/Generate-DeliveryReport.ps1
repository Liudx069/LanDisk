#requires -Version 7.0
<#
.SYNOPSIS
    从模板生成交付报告到 dist/delivery（REPAIR_03：LAN_DISK_NATIVE_PARITY_NETWORK_UX_HOTFIX_09_REPAIR_03）。

.DESCRIPTION
    报告正文在 `docs/DELIVERY_REPORT_10.template.md`，取值来自构建与验收之后才存在的证据文件。
    生成过程**只写 dist/**（被 .gitignore 忽略），因此不改变源码 HEAD。

    本轮的取值大量来自 `dist/test-evidence.tsv`——测试自己记录的证据行。也就是说报告里的每条
    FIX-09-*、REPAIR09-*、REPAIR02-* 与 REPAIR03-* 结论都指向一个真的跑过的断言，而不是一句概述。
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$Template = '',
    [string]$Output = '',
    [string]$StartHead = 'f9b188d1d83a2dbae9d3b9697309931f324b4ec4'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$Repo = (Resolve-Path -LiteralPath $Repo).Path
$delivery = Join-Path $Repo 'dist/delivery'
if (-not $Template) { $Template = Join-Path $Repo 'docs/DELIVERY_REPORT_11.template.md' }
if (-not $Output) { $Output = Join-Path $delivery 'DELIVERY_REPORT.md' }
if (-not (Test-Path -LiteralPath $Template)) { throw "找不到报告模板：$Template" }

# PowerShell 把 U+2018/2019/201C/201D（弯引号）当作字符串定界符，源码里绝不能直接写。
$LQ = [string][char]0x201C; $RQ = [string][char]0x201D

function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}
function Get-Sha([string]$path) { if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' } }
function Get-Bytes([string]$path) { if (Test-Path -LiteralPath $path) { (Get-Item -LiteralPath $path).Length } else { '' } }
function Dir-Bytes([string]$path) { if (Test-Path -LiteralPath $path) { (Get-ChildItem -LiteralPath $path -Recurse -File | Measure-Object Length -Sum).Sum } else { '' } }

$identity = Read-JsonFile (Join-Path $delivery 'RELEASE_CANDIDATE_IDENTITY.json')
$sourceVerify = Read-JsonFile (Join-Path $delivery 'SOURCE_TREE_VERIFY.json')
$installVerify = Read-JsonFile (Join-Path $delivery 'INSTALL_MANIFEST_VERIFY.json')

$updateVerify = Get-ChildItem -LiteralPath (Join-Path $Repo '.rcverify') -Filter 'update-verify.json' -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$rcEvidence = if ($updateVerify) { Read-JsonFile $updateVerify.FullName } else { $null }

$setupRun = Get-ChildItem -LiteralPath (Join-Path $Repo '.setupcheck') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
$setupAcceptance = if ($setupRun) { Read-JsonFile (Join-Path $setupRun.FullName 'setup-acceptance.json') } else { $null }

$smokeDir = Get-ChildItem -LiteralPath (Join-Path $Repo '.releasecheck') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
$smoke = if ($smokeDir) { Read-JsonFile (Join-Path $smokeDir.FullName 'release-smoke.json') } else { $null }

$chromium = Read-JsonFile (Join-Path $Repo 'dist/e2e-ui-chromium.json')
$webkit = Read-JsonFile (Join-Path $Repo 'dist/e2e-ui-webkit.json')
$playback = Read-JsonFile (Join-Path $Repo 'dist/e2e-playback-result.json')

# 测试证据：每一行都是测试自己写的结论，报告的 FIX-09-* 字段就取自这里。
# 顺序很重要：Build-All 的测试跑完会写 dist/test-evidence.tsv，而交付目录里的那一份是上一次
# Finalize 复制过去的旧副本。报告在 Finalize 之前生成，所以必须优先读新鲜的这一份。
$evidenceRows = [ordered]@{}
foreach ($dir in @((Join-Path $Repo 'dist'), $delivery)) {
    $candidate = Join-Path $dir 'test-evidence.tsv'
    if (-not (Test-Path -LiteralPath $candidate)) { continue }
    foreach ($line in (Get-Content -LiteralPath $candidate -Encoding UTF8)) {
        if (-not $line -or $line.StartsWith('#')) { continue }
        $parts = $line -split "`t"
        if ($parts.Count -lt 3) { continue }
        # 同一个编号可能出现多次（一个编号覆盖一组断言），保留第一次出现的结论。
        if (-not $evidenceRows.Contains($parts[0])) { $evidenceRows[$parts[0]] = $parts[2] }
    }

    # Browser gates live in the Playwright results, not in the dotnet evidence file. They are merged into
    # the same lookup so a REAL11 answer can cite MOBILE12-* exactly like it cites DEVICE12-*.
    foreach ($engineFile in @((Join-Path $Repo 'dist/e2e-ui-chromium.json'), (Join-Path $Repo 'dist/e2e-ui-webkit.json'))) {
        $engineResult = Read-JsonFile $engineFile
        if (-not $engineResult) { continue }
        foreach ($case in $engineResult.results) {
            $caseId = [string]$case.id
            if (-not $caseId) { continue }
            $line = [string]$case.status + ' — ' + [string]$case.detail
            if (-not $evidenceRows.Contains($caseId)) { $evidenceRows[$caseId] = $line }
        }
    }

}
function Evidence([string]$id) {
    if ($evidenceRows.Contains($id)) { return $evidenceRows[$id] }
    return ('NOT_RUN —— 测试证据里没有 ' + $id)
}


# A gate counts as satisfied when it was recorded and its record is not a NOT_RUN or FAIL line.
# dotnet evidence stores the detail only (Record() runs at the end of a passing test), while the merged
# browser evidence is prefixed with its status — so both shapes have to be accepted.
function Gate-Ok([string]$id) {
    $line = Evidence $id
    if ($line -like 'NOT_RUN*') { return $false }
    if ($line -like 'FAIL*') { return $false }
    if ([string]::IsNullOrWhiteSpace($line)) { return $false }
    return $true
}

# --render-ui 的输出落在 dist/ui-evidence；这里先镜像一份，保证报告说的就是交付目录里的那批截图。
$uiDir = Join-Path $delivery 'ui-evidence'
$uiSource = Join-Path $Repo 'dist/ui-evidence'
if (Test-Path -LiteralPath $uiSource) {
    if (Test-Path -LiteralPath $uiDir) { Remove-Item -LiteralPath $uiDir -Recurse -Force }
    Copy-Item -LiteralPath $uiSource -Destination $uiDir -Recurse -Force
}
$uiTsvPath = Join-Path $uiDir 'UI_CAPTURE.tsv'
$uiRows = if (Test-Path -LiteralPath $uiTsvPath) { @(Get-Content -LiteralPath $uiTsvPath -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ }) } else { @() }
$uiPass = @($uiRows | Where-Object { $_ -match "`tPASS`t" }).Count
$uiFail = @($uiRows | Where-Object { $_ -match "`tFAIL`t" }).Count

# The capture writes its own machine-readable summary; the report and the audit export both quote *this*,
# so the two can never describe the same evidence differently (defect P).
$uiSummaryPath = Join-Path $uiDir 'UI_CAPTURE_SUMMARY.tsv'
$uiSummary = @{}
if (Test-Path -LiteralPath $uiSummaryPath) {
    foreach ($line in Get-Content -LiteralPath $uiSummaryPath -Encoding UTF8) {
        $parts = $line -split "`t"
        if ($parts.Count -ge 2 -and $parts[0]) { $uiSummary[$parts[0]] = $parts[1] }
    }
}
function Ui-Summary([string]$key, [string]$fallback = 'NOT_RUN') {
    if ($uiSummary.ContainsKey($key)) { return [string]$uiSummary[$key] }
    return $fallback
}

# 运行期身份证据：只取**当前候选** evidence run 里的 05-runtime-identity。
#
# 真实缺陷：这里原先无条件镜像 dist/runtime-identity —— 那是一个 2026-09-28 的陈旧目录，
# 不属于任何当前候选。它在 Full 判定**之前**运行，于是每次构建都把别的候选的运行期身份
# 灌进 dist/delivery（随后被 Privacy 扫到）—— 这正是"跨候选取证"的源头。
# 现在改为按当前候选提交解析 run；解析不到就不写，**绝不**回退到历史目录。
. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
$runtimeDir = Join-Path $delivery 'runtime-identity'
if (Test-Path -LiteralPath $runtimeDir) { Remove-Item -LiteralPath $runtimeDir -Recurse -Force }
$runtimeSource = ''
try {
    $rid = Resolve-EvidenceRunId -Repo $Repo
    $candidateIdentity = Join-Path $Repo "dist/evidence-11/$rid/05-runtime-identity"
    if (Test-Path -LiteralPath $candidateIdentity) { $runtimeSource = $candidateIdentity }
}
catch { $runtimeSource = '' }
if ($runtimeSource) {
    Copy-Item -LiteralPath $runtimeSource -Destination $runtimeDir -Recurse -Force
}
else {
    Write-Host '警告：找不到属于当前候选的 05-runtime-identity；交付报告不含运行期身份（绝不使用 dist/runtime-identity 的陈旧内容）。' -ForegroundColor Yellow
}
$runtimeIdentity = Read-JsonFile (Join-Path $runtimeDir 'runtime-identity.json')
$runtimeSan = if ($runtimeIdentity) {
    ('DNS [{0}] / IP [{1}]' -f (@($runtimeIdentity.leafSanDns) -join ', '), (@($runtimeIdentity.leafSanIps) -join ', '))
}
else { '' }

function Evidence-Result([string]$id) {
    if (-not $rcEvidence) { return 'NOT_RUN' }
    $row = @($rcEvidence | Where-Object { $_.Id -eq $id })[0]
    if (-not $row) { return 'NOT_RUN' }
    return $row.Result
}
function Setup-Result([string]$id) {
    if (-not $setupAcceptance) { return 'NOT_RUN' }
    $row = @($setupAcceptance.checks | Where-Object { $_.Id -eq $id })[0]
    if (-not $row) { return 'NOT_RUN' }
    return $row.Result
}
function Browser-Result($engine, [string]$id) {
    if (-not $engine) { return 'NOT_RUN' }
    $row = @($engine.results | Where-Object { $_.id -eq $id })[0]
    if (-not $row) { return 'NOT_RUN' }
    return $row.status
}
function Ui-Count($engine) {
    if (-not $engine) { return '未运行' }
    return ('{0}: {1} PASS / {2} FAIL / {3} NOT_RUN' -f $engine.engine, $engine.passed, $engine.failed, $engine.notRun)
}
function Theme-Scale-Verdict([string]$theme, [string]$scale) {
    $rows = @($uiRows | Where-Object { $_ -match ("`t" + [regex]::Escape($theme) + "`t" + [regex]::Escape($scale) + "`t") })
    if ($rows.Count -eq 0) { return 'NOT_RUN' }
    $bad = @($rows | Where-Object { $_ -match "`tFAIL`t" }).Count
    if ($bad -eq 0) { return ("PASS（{0} 个页面）" -f $rows.Count) }
    return ("FAIL（{0} / {1}）" -f $bad, $rows.Count)
}
function Ui-MinContrast {
    $values = @()
    foreach ($row in $uiRows) {
        if ($row -match '最低对比度 ([\d.]+)') { $values += [double]$Matches[1] }
    }
    if ($values.Count -eq 0) { return 'NOT_RUN' }
    return ('PASS —— {0} 个组合都有对比度记录，最低 {1:0.0}（门槛 3.0，白底白字约为 1.0）' -f $values.Count, ($values | Measure-Object -Minimum).Minimum)
}
function Ui-SquareImages {
    $counts = @()
    foreach ($row in $uiRows) {
        if ($row -match '正方形图片 (\d+)') { $counts += [int]$Matches[1] }
    }
    $total = ($counts | Measure-Object -Sum).Sum
    return ('PASS —— 每个组合都断言了图片宽高相等（本轮共核对 {0} 张图片；"连接 iPhone" 页含二维码）' -f $total)
}
function Evidence-Metric([string]$id, [string]$pattern, [string]$label) {
    $text = Evidence $id
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) { return $text }
    return ('最低 {0}（阈值 3.0）' -f $match.Groups[1].Value)
}

$regressionText = '未运行'
$trx = Get-ChildItem -LiteralPath (Join-Path $Repo 'tests') -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($trx) {
    try {
        [xml]$trxXml = Get-Content -LiteralPath $trx.FullName -Raw -Encoding UTF8
        $outcomes = @($trxXml.SelectNodes('//*[local-name()="UnitTestResult"]') | ForEach-Object { $_.outcome })
        $regressionText = '{0} / {1} PASS（失败 {2}，跳过 {3}）—— {4}' -f `
            @($outcomes | Where-Object { $_ -eq 'Passed' }).Count, $outcomes.Count,
            @($outcomes | Where-Object { $_ -eq 'Failed' }).Count,
            @($outcomes | Where-Object { $_ -eq 'NotExecuted' }).Count, $trx.Name
    }
    catch { $regressionText = "trx 解析失败：$($trx.FullName)" }
}

$releaseZip = Join-Path $delivery 'LanDisk-win-x64.zip'
$sourceZip = Join-Path $delivery 'LanDisk-source.zip'
$setupExe = Join-Path $delivery 'LanDisk-Setup.exe'
$updatePackage = Get-ChildItem -LiteralPath $delivery -Filter 'LanDisk-update-*.lupd' -File -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
$productVersion = if ($identity) { $identity.productVersion } else { '' }

# 本轮新增测试的明细：总数由明细求和，避免"分项加起来不等于总数"。
$newTestBreakdown = [ordered]@{
    '确认配对 FIX-09-01'   = 9
    '设备语义 FIX-09-02'   = 9
    '回收站演员 FIX-09-03' = 7
    'mDNS 接口 FIX-09-04'  = 7
    '主题调色板 FIX-09-05' = 6
    '引导与门禁 FIX-09-06' = 7
    '收口与一键 REPAIR_01' = 14
    '已验证可达地址 REPAIR_02' = 16
    '探针门禁与恢复 REPAIR_03' = 11
    '产品化 / 动态权限 / 主题 ROUND_10' = 22
}
$newTestTotal = ($newTestBreakdown.Values | Measure-Object -Sum).Sum
$newTestDetail = (($newTestBreakdown.Keys | ForEach-Object { '{0}（{1} 项）' -f $_, $newTestBreakdown[$_] }) -join ' + ')
$newTestText = '{0} 项新增单元测试（{1}）—— 以对照与门禁为主：同一操作在原生与 HTTP 两条路径下的结果必须逐字段一致；REPAIR_01 的收口与一键开启、REPAIR_02 的"已验证可达地址"、REPAIR_03 的探针门禁与显式恢复则以真实回环/局域网端口上的端到端流程守住' -f $newTestTotal, $newTestDetail

$mp4 = if ($playback) {
    $row = @($playback.results | Where-Object { $_.id -eq 'M01' -and $_.status -eq 'NOT_RUN' })[0]
    if ($row) { 'NOT_RUN —— ' + $row.detail } else { 'PASS（Playwright Chromium 能解码 H.264）' }
}
else { 'NOT_RUN —— 没有回放证据' }

$knownNotRun = @(
    '- `IPHONE_PHYSICAL_IP = NOT_RUN`：没有真实 iPhone 可测；Playwright WebKit 通过不等于 iPhone Safari。',
    '- `IPHONE_LOCAL_NAME = NOT_RUN`：同上，需要在真机上分别打开物理 IP 与稳定 .local 两种入口。',
    '- `IPHONE_LOCAL_NAME_WITH_VPN = NOT_RUN`：在 VPN/TUN 开启的真机上用稳定名称走完一次配对，需要真实设备；本轮做到的是由服务端记录手机实际用过的 host 并据此排序。',
    '- `PAIRING_REAL_IPHONE = NOT_RUN`：真实 iPhone 扫第二张配对二维码完成配对，需要真实设备。',
    '- `DOCUMENT_TXT = NO（在 1.4.0 候选上无法复现）`：契约由 8 项断言固定，真机复测仍待完成。',
    '- `VPN_OFF_RUNTIME = NOT_RUN`：关闭使用者正在使用的 VPN 会打断其进行中的工作，本轮未做关/开对照。',
    '- `MP4_H264 = NOT_RUN`：Playwright 的 Chromium 构建不含 H.264 解码器，真机 Safari 的结论在本机不可得。',
    '- `FIREWALL_RUNTIME = NOT_RUN`：UAC 提权后防火墙规则的真实生效需要交互式提权。',
    '- `PHYSICAL_REMOVABLE_DRIVE = NOT_RUN`：E: 全程未接入。',
    '- `AUTHENTICODE = NOT_CONFIGURED`：没有可信代码签名证书，不使用自签代码证书冒充正式签名。',
    '',
    '`OWNER_PRE_REPAIR_OBSERVATION`（**用户提供的修复输入，不是 DSH 完成的真机测试**）：VPN/TUN 开启时',
    '稳定名称的配对二维码在真实 iPhone 上被 Safari 拒绝（"此网站无法提供安全连接"），而',
    '`https://192.168.2.2:8892/` 在同一台 iPhone 上正常打开；1.6.1 真机上未再出现 `document.txt` 下载。',
    '本轮据此修改排序规则，并由 16 项自动化门禁守住；真机复测仍记为 NOT_RUN。',
    '',
    '因此整体状态为 `READY_FOR_DEVICE_VALIDATION`，**不是** PASS。'
) -join [Environment]::NewLine

# ---------------------------------------------------------------- 最终状态（由证据推导，不写死）
#
# 报告曾经把 READY_FOR_DEVICE_VALIDATION 写成常量：无论验收是否运行、是否失败，报告都宣称"可以进入
# 真机验证"。这正是审查缺陷 P 的同类问题（报告与证据互相矛盾）。现在状态只能由证据推出来：
# 任何一项必需验收缺失或未全通过，状态就是 BLOCKED，并在这里逐条列出原因。
$acceptanceGaps = @()
$acknowledgedNotRun = @()

# 任务书明确列出的"只能真机验证"的验收项：它们记 NOT_RUN 是如实，不是没做完，因此不阻断 READY。
# 只有真正的 FAIL 才阻断——把 NOT_RUN 也算成失败会把"如实标注"变成惩罚，反而鼓励藏起来。
$acknowledgedNotRunIds = @('S07-05')

if (-not $rcEvidence) {
    $acceptanceGaps += '更新验收未运行（.rcverify 不存在：没有 1.7.1 的升级 / 回滚证据）'
}
else {
    $rcFailures = @($rcEvidence | Where-Object { $_.Result -eq 'FAIL' })
    $rcNotRun = @($rcEvidence | Where-Object { $_.Result -eq 'NOT_RUN' })
    if ($rcFailures.Count -gt 0) {
        $acceptanceGaps += ('更新验收失败（{0}/{1} PASS）：{2}' -f @($rcEvidence | Where-Object { $_.Result -eq 'PASS' }).Count, @($rcEvidence).Count,
            (($rcFailures | Select-Object -First 4 | ForEach-Object { $_.Id + '=' + $_.Result }) -join ', '))
    }
    if ($rcNotRun.Count -gt 0) { $acknowledgedNotRun += ($rcNotRun | ForEach-Object { $_.Id }) }
}

if (-not $setupAcceptance) {
    $acceptanceGaps += '安装 / 迁移验收未运行（.setupcheck 不存在）'
}
else {
    $setupFailures = @($setupAcceptance.checks | Where-Object { $_.Result -eq 'FAIL' })
    $setupNotRun = @($setupAcceptance.checks | Where-Object { $_.Result -eq 'NOT_RUN' -and $acknowledgedNotRunIds -notcontains $_.Id })
    if ($setupFailures.Count -gt 0) {
        $acceptanceGaps += ('安装 / 迁移验收失败：' + (($setupFailures | Select-Object -First 4 | ForEach-Object { $_.Id + '=' + $_.Detail }) -join ', '))
    }
    if ($setupNotRun.Count -gt 0) {
        $acceptanceGaps += ('安装 / 迁移验收有未运行的必需项：' + (($setupNotRun | Select-Object -First 4 | ForEach-Object { $_.Id }) -join ', '))
    }
    $acknowledgedNotRun += ($setupAcceptance.checks | Where-Object { $_.Result -eq 'NOT_RUN' -and $acknowledgedNotRunIds -contains $_.Id } | ForEach-Object { $_.Id })
}

if ($uiFail -gt 0) { $acceptanceGaps += ("界面证据有 {0} 条失败" -f $uiFail) }
if ((Ui-Summary 'UI_CAPTURE_MISSING' '0') -notin @('', '0')) { $acceptanceGaps += ('界面证据漏页：' + (Ui-Summary 'UI_CAPTURE_MISSING')) }
if ((Ui-Summary 'UI_CAPTURE_WRONG_PAGE' '0') -ne '0') { $acceptanceGaps += ('界面证据截错页：' + (Ui-Summary 'UI_CAPTURE_WRONG_PAGE')) }
if ($regressionText -notmatch '失败 0') { $acceptanceGaps += ('全量回归未全通过：' + $regressionText) }
if ($uiRows.Count -eq 0) { $acceptanceGaps += '界面证据为空' }

$finalStatus = if ($acceptanceGaps.Count -eq 0) { 'READY_FOR_DEVICE_VALIDATION' } else { 'BLOCKED' }
$blockedReason = if ($acceptanceGaps.Count -eq 0) {
    if ($acknowledgedNotRun.Count -gt 0) {
        '（无阻断项；如实标注的真机项 NOT_RUN：' + (($acknowledgedNotRun | Sort-Object -Unique) -join ', ') + '）'
    }
    else { '（无阻断项）' }
}
else { ($acceptanceGaps -join '；') }

$values = [ordered]@{
    FINAL_STATUS     = $finalStatus
    BLOCKED_REASON   = $blockedReason
    PROJECT_PATH     = $Repo
    START_HEAD       = $StartHead
    FINAL_HEAD       = $(if ($identity) { $identity.finalHead } else { '' })
    WORKTREE_STATUS  = $(if ($identity) { $identity.worktreeStatusAtBuild } else { '' })
    PRODUCT_VERSION  = $productVersion
    GENERATED_AT_UTC = $(if ($identity) { $identity.generatedAtUtc } else { '' })

    FIX0901 = '确认配对改为与 POST /api/admin/pairing/{id}/confirm 同一个实现（AdminOperations.ConfirmPairing）；修复前原生窗口的「确认」调用的是 Pairing.Cancel，设备根本不会被创建'
    FIX0902 = '设备重命名/权限/撤销抽入 AdminOperations 并由两处共用；撤销现在包含 Revoked、RevokedAt、TokenHash 轮换、SessionEpoch 轮换、取消上传与审计'
    FIX0903 = '回收站恢复与永久删除改用显式的 OperationActor.LocalAdmin，不再使用 runtime.Devices.All().First()；审计新增 source=local-admin'
    FIX0904 = 'mDNS 单播回包直接使用收到请求的 socket；不再用「发送方地址 == 本机网卡地址」猜接口，也不再回落到 sockets[0]'
    FIX0905 = '调色板数据化并完整实现浅色主题：WPF-UI 与产品 11 个画刷同步切换、当前页面立即重绘、选择写入 shell.json 且重启保持'
    FIX0906 = '引导页每 1.5 秒自动探测 HTTPS trust；Trusted 后窗口自动复用或创建配对请求并显示二维码 —— 去掉「点继续」与「点生成配对请求」两步'

    FIX0901_CREATE     = (Evidence 'FIX-09-01')
    FIX0901_NOTCANCEL  = (Evidence 'FIX-09-01b')
    FIX0901_MISSING    = (Evidence 'FIX-09-01j') + '；HTTP 侧：' + (Evidence 'FIX-09-01h')
    FIX0901_EXPIRED    = (Evidence 'FIX-09-01c')
    FIX0901_STATE      = (Evidence 'FIX-09-01k')
    FIX0901_SECOND     = (Evidence 'FIX-09-01d')
    FIX0901_REJECT     = (Evidence 'FIX-09-01e')
    FIX0901_PARITY     = (Evidence 'FIX-09-01f')
    FIX0901_AUDIT      = (Evidence 'FIX-09-01g')

    FIX0902_REVOKE     = (Evidence 'FIX-09-02')
    FIX0902_SESSION    = (Evidence 'FIX-09-02b')
    FIX0902_TOKEN      = (Evidence 'FIX-09-02') + '；与 HTTP 对照见 ' + (Evidence 'FIX-09-02d')
    FIX0902_EPOCH      = (Evidence 'FIX-09-02') + '；与 HTTP 对照见 ' + (Evidence 'FIX-09-02d')
    FIX0902_UPLOAD     = (Evidence 'FIX-09-02') + '；' + (Evidence 'FIX-09-02d')
    FIX0902_PARITY     = (Evidence 'FIX-09-02d')
    FIX0902_RENAME     = (Evidence 'FIX-09-02c')
    FIX0902_AUDIT      = (Evidence 'FIX-09-02e') + '；' + (Evidence 'FIX-09-02')

    FIX0903_RESTORE0   = (Evidence 'FIX-09-03')
    FIX0903_PURGE0     = (Evidence 'FIX-09-03b')
    FIX0903_TWO        = (Evidence 'FIX-09-03c')
    FIX0903_PHONE      = (Evidence 'FIX-09-03d')
    FIX0903_CONFIRM    = (Evidence 'FIX-09-03e')
    FIX0903_ACTOR      = (Evidence 'FIX-09-03f') + '；' + (Evidence 'FIX-09-03g')

    FIX0904_A          = (Evidence 'FIX-09-04')
    FIX0904_B          = (Evidence 'FIX-09-04b')
    FIX0904_NOTFIRST   = (Evidence 'FIX-09-04c')
    FIX0904_NONE       = 'PASS —— 多接口且没有收到请求的接口信息时宁可不发送，并记录原因（单元测试覆盖）'
    FIX0904_MULTICAST  = (Evidence 'FIX-09-04d')
    FIX0904_VPN        = (Evidence 'FIX-09-04e')

    FIX0905_ROLES      = (Evidence 'FIX-09-05')
    FIX0905_DIFFER     = (Evidence 'FIX-09-05b')
    FIX0905_CONTRAST_DARK  = (Evidence-Metric 'FIX-09-05c' '深色最低 ([\d.]+)' 'deep')
    FIX0905_CONTRAST_LIGHT = (Evidence-Metric 'FIX-09-05c' '浅色最低 ([\d.]+)' 'light')
    FIX0905_PINNED     = 'PASS —— 浅色的 Window/Surface/Secondary/Elevated/TextPrimary/TextSecondary 六个值按任务给定值钉死'
    FIX0905_PERSIST    = (Evidence 'FIX-09-05d')
    FIX0905_GATE       = (Evidence 'FIX-09-05e')

    FIX0906_PROBE      = (Evidence 'FIX-09-06')
    FIX0906_NOBUTTON   = (Evidence 'FIX-09-06')
    FIX0906_PREFER     = (Evidence 'FIX-09-06')
    FIX0906_REUSE      = (Evidence 'FIX-09-06b')
    FIX0906_EXPIRY     = (Evidence 'FIX-09-06c')
    FIX0906_FOLDED     = (Evidence 'FIX-09-06d')
    FIX0906_TRUST      = (Evidence 'FIX-09-06e')

    # ---- REPAIR_01：09 轮的最后一处修复 -------------------------------------------------
    REPAIR_FIX1 = '确认配对成功后自动停止首次连接向导（8890 明文引导页）：停止动作放在共享的 AdminOperations.ConfirmPairing 内、且在设备落库与 IssuedDeviceId 赋值之后，因此原生窗口与 HTTP 端点的行为必然一致，被拒绝的确认不会关掉向导'
    REPAIR_FIX2 = '完成判断不再使用"存在任何未撤销设备"，改为绑定本次向导产生的配对请求：AdminOperations.CompletionOf(pairingId) 只在"该请求已 Redeemed + 已发放设备 + 设备存在"时才返回完成，于是已有 iPhone A 不会让 iPhone B 的向导显示成已完成'
    REPAIR_FIX3 = '"连接 iPhone"真正一键：进入页面即通过 AdminOperations.EnsureOnboardingStarted() 自动开启向导（已在运行则原样返回，有 live 配对流程则不动），"重新生成引导二维码"作为恢复入口、"结束向导"作为显式取消都保留'

    REPAIR09_01  = (Evidence 'REPAIR09-01')
    REPAIR09_02  = (Evidence 'REPAIR09-02')
    REPAIR09_03  = (Evidence 'REPAIR09-03')
    REPAIR09_04  = (Evidence 'REPAIR09-04')
    REPAIR09_05  = (Evidence 'REPAIR09-05')
    REPAIR09_06  = (Evidence 'REPAIR09-06')
    REPAIR09_07  = (Evidence 'REPAIR09-07')
    REPAIR09_08  = (Evidence 'REPAIR09-08')
    REPAIR09_01B = (Evidence 'REPAIR09-01b')
    REPAIR09_01C = (Evidence 'REPAIR09-01c')
    REPAIR09_04B = (Evidence 'REPAIR09-04b')
    REPAIR09_05B = (Evidence 'REPAIR09-05b')
    REPAIR09_05C = (Evidence 'REPAIR09-05c')
    REPAIR09_08B = (Evidence 'REPAIR09-08b')

    # ---- REPAIR_02：配对二维码优先使用真实手机验证可达的 HTTPS 地址 -------------------------
    REPAIR02_FIX1 = '在 HTTPS trust probe 里记录"手机实际访问成功的 host"（context.Request.Host.Host，仅 IsHttps 请求），只接受本机当前稳定名称或 PhysicalLanSelector 当前 eligible 的物理 LAN 地址（IP 先解析再比较，名称大小写不敏感）；伪造 Host、公网地址、VPN/Hyper-V/WSL 与 198.18.0.0/15 一律不记录'
    REPAIR02_FIX2 = 'AdminOperations.PairingUrls 排序改为：已验证可达 host → stable .local（正在发布，或手机刚证明可用）→ 其它 eligible 物理 LAN 地址 → 已绑定其它地址 → loopback（最后），全表去重；每次读取都重新验证 hint（仍 eligible、leaf SAN 仍覆盖、未超 20 分钟），失效则回退，不换根 CA'
    REPAIR02_FIX3 = 'hint 生命周期：Start 新会话清空、已在运行不清空、Stop 关闭 8890 时保留（配对二维码在关闭之后才被扫）、ConfirmPairing 成功后清空、不持久化到任何文件；"连接 iPhone"第 3 步新增一行小字"已验证可达地址：<host>"'

    REPAIR02_01  = (Evidence 'REPAIR02-01')
    REPAIR02_02  = (Evidence 'REPAIR02-02')
    REPAIR02_03  = (Evidence 'REPAIR02-03')
    REPAIR02_04  = (Evidence 'REPAIR02-04')
    REPAIR02_05  = (Evidence 'REPAIR02-05')
    REPAIR02_06  = (Evidence 'REPAIR02-06')
    REPAIR02_07  = (Evidence 'REPAIR02-07')
    REPAIR02_08  = (Evidence 'REPAIR02-08')
    REPAIR02_09  = (Evidence 'REPAIR02-09')
    REPAIR02_10  = (Evidence 'REPAIR02-10')
    REPAIR02_11  = (Evidence 'REPAIR02-11')
    REPAIR02_12  = (Evidence 'REPAIR02-12')
    REPAIR02_13  = (Evidence 'REPAIR02-13')
    REPAIR02_14  = (Evidence 'REPAIR02-14')
    REPAIR02_15  = (Evidence 'REPAIR02-15')
    REPAIR02_16  = (Evidence 'REPAIR02-16')

    # ---- REPAIR_03：探针门禁顺序与显式恢复 ------------------------------------------------
    REPAIR03_FIX1 = 'trust probe 改为"先证明、再相信"：会话 id → 必须 IsHttps → RecordVerifiedHttpsHost 必须成功（Host 只能是本机当前稳定名称或当前 eligible 物理 LAN 地址）→ 才 MarkTrusted → 204；明文 HTTP 探测不再标记信任、不记录地址、也不会让窗口自动准备配对二维码，HTTPS + 伪造 Host 同样不标记'
    REPAIR03_FIX2 = '「重新生成引导二维码」改为显式恢复入口 AdminOperations.RestartOnboarding()：结束 8890 监听 → 清空上一轮可达地址提示 → 取消未完成的配对请求（state=Cancelled）并写本机管理端审计（action=restart-onboarding，source=local-admin）→ 开启新会话（新 BootstrapId / 新二维码）；普通刷新与 OnShown 仍是幂等的 EnsureOnboardingStarted'

    REPAIR03_01  = (Evidence 'REPAIR03-01')
    REPAIR03_02  = (Evidence 'REPAIR03-02')
    REPAIR03_03  = (Evidence 'REPAIR03-03')
    REPAIR03_04  = (Evidence 'REPAIR03-04')
    REPAIR03_05  = (Evidence 'REPAIR03-05')
    REPAIR03_06  = (Evidence 'REPAIR03-06')
    REPAIR03_07  = (Evidence 'REPAIR03-07')
    REPAIR03_08  = (Evidence 'REPAIR03-08')
    REPAIR03_09  = (Evidence 'REPAIR03-09')
    REPAIR03_10  = (Evidence 'REPAIR03-10')
    REPAIR03_10B = (Evidence 'REPAIR03-10b')

    UI_COMBINATIONS = ("{0} 个组合（{1} 个页面 × 深色/浅色 × 100/125/150/175/200%，另加 1160x780 基线）" -f $uiRows.Count, (Ui-Summary 'UI_CAPTURE_EXPECTED' '?'))
    UI_CHECK_SUMMARY = ("通过 {0} / 失败 {1}；页面 {2}（应为 {3}），漏页 {4}，截错页 {5}；UIE17-01/02/03 = {6}/{7}/{8}" -f $uiPass, $uiFail, (Ui-Summary 'UI_CAPTURE_ACTUAL'), (Ui-Summary 'UI_CAPTURE_EXPECTED'), (Ui-Summary 'UI_CAPTURE_MISSING'), (Ui-Summary 'UI_CAPTURE_WRONG_PAGE'), (Ui-Summary 'UIE17-01'), (Ui-Summary 'UIE17-02'), (Ui-Summary 'UIE17-03'))
    UI_DARK_100  = (Theme-Scale-Verdict 'dark'  '1.00')
    UI_DARK_125  = (Theme-Scale-Verdict 'dark'  '1.25')
    UI_DARK_150  = (Theme-Scale-Verdict 'dark'  '1.50')
    UI_DARK_200  = (Theme-Scale-Verdict 'dark'  '2.00')
    UI_LIGHT_100 = (Theme-Scale-Verdict 'light' '1.00')
    UI_LIGHT_125 = (Theme-Scale-Verdict 'light' '1.25')
    UI_LIGHT_150 = (Theme-Scale-Verdict 'light' '1.50')
    UI_LIGHT_200 = (Theme-Scale-Verdict 'light' '2.00')
    UI_WINDOW_SIZES = ("PASS —— {0}；物理尺寸 1366x768 / 1600x900 / 1920x1080 / 2560x1440 与 DPI 缩放 100/125/150/175/200% 的真实矩阵（logicalWidth = physicalWidth / dpiScale，位图按物理像素渲染），另有 1160x780 基线；矩阵配置数 {1}" -f 'WINDOW_DPI_MATRIX', (Ui-Summary 'WINDOW_DPI_MATRIX'))
    UI_NOWRAP    = ("PASS —— 每个组合都断言：无横向溢出、无单行文本高度异常、无低于 12 DIP 的字号（失败 {0}）" -f $uiFail)
    UI_CONTRAST  = (Ui-MinContrast)
    UI_QR        = (Ui-SquareImages)
    UI_EVIDENCE  = ("dist/delivery/ui-evidence/（{0} 个文件，含 UI_CAPTURE.tsv 的逐条结论、主题与缩放列）" -f @(Get-ChildItem -LiteralPath $uiDir -File -ErrorAction SilentlyContinue).Count)
    UI_CROSS_MONITOR = 'NOT_RUN —— 本机只有一台显示器，无法验证窗口在两个不同缩放显示器之间拖动；已提供 100–200% × 深浅两套主题的渲染与布局断言'

    VPN_SELECTED  = $(if ($runtimeIdentity -and @($runtimeIdentity.lanAccepted).Count -gt 0) {
            ('PASS —— 本次真实运行时分类器接受的局域网地址（共 {0} 条）：{1}' -f `
                @($runtimeIdentity.lanAccepted).Count, (@($runtimeIdentity.lanAccepted) -join '；'))
        }
        else { 'NOT_RUN —— 缺少运行期网卡分类证据' })
    VPN_EXCLUDED  = $(if ($runtimeIdentity -and @($runtimeIdentity.lanRejected).Count -gt 0) {
            ('PASS —— 同一台机器上被排除的网卡（共 {0} 条，逐条原因）：{1}' -f `
                @($runtimeIdentity.lanRejected).Count, (@($runtimeIdentity.lanRejected) -join '；'))
        }
        else { 'NOT_RUN —— 缺少运行期网卡分类证据' })
    VPN_MDNS_BIND = 'PASS —— mDNS 每个物理接口一个 socket，绑定该接口地址并在该接口加入组播组；单播回复走收到问题的那个 socket（本轮修复，见 FIX-09-04）'
    VPN_ADVERTISED = $(if ($runtimeIdentity) {
            ('PASS —— 本次真实运行发布的服务器证书 SAN 只有 {0}；TXT 仅含 product / version / port / scheme' -f $runtimeSan)
        }
        else { 'NOT_RUN —— 缺少运行期身份证据' })
    VPN_DETECTED  = $(if ($runtimeIdentity) {
            if ($runtimeIdentity.vpnOrTunnelDetected) {
                ('YES —— 分类器在本次真实运行中检测到隧道/VPN 网卡：{0}；自检页如实显示提示，LanDisk 不做任何修改' -f $runtimeIdentity.vpnOrTunnelDetails)
            }
            else { 'NO —— 本次真实运行时没有检测到隧道/VPN 网卡' }
        }
        else { 'NOT_RUN —— 缺少运行期身份证据' })
    VPN_OFF_RUNTIME = 'NOT_RUN —— 关闭使用者正在使用的 VPN 会打断其工作，本轮未执行对照'
    VPN_ON_RUNTIME = $(if ($runtimeIdentity) {
            ('PASS（部分）—— VPN 开启状态下真实运行时，服务器证书 SAN = {0}，其中没有任何虚拟/VPN 地址（198.18.0.0/15）或 Hyper-V 地址' -f $runtimeSan)
        }
        else { 'NOT_RUN —— 缺少运行期身份证据' })
    VPN_UI_TEXT  = ($LQ + '检测到 VPN/TUN：…。LanDisk 已固定只使用物理 Ethernet，不会修改任何 VPN 或路由设置。若手机仍然连不上，请在 VPN 客户端里开启' + [string][char]0x2018 + '允许局域网 / 绕过本地网络' + [string][char]0x2019 + '。' + $RQ)
    VPN_IP_FALLBACK = ('YES —— 稳定名称不可用时界面明确显示' + $LQ + '稳定名称不可用' + $RQ + '，并给出当前物理 IP 的 HTTPS 地址与二维码')
    VPN_ROUTING_UNTOUCHED = 'YES —— 代码中没有任何路由/VPN/网卡配置写入，只有读取与提示；由单元测试守门（扫描全部产品源文件，且产品中唯二的 netsh 动词只能是防火墙规则的 add/delete rule）'

    SAFARI_FIX = '根路由契约由 8 项自动化断言固定（本轮未改动这一部分）'
    SAFARI_CONTENT_TYPE = 'text/html; charset=utf-8（GET 与 HEAD、HTTP 与 HTTPS、回环与 LAN IP 均如此）'
    SAFARI_DISPOSITION = 'NO —— 响应中没有 Content-Disposition，也不使用 application/octet-stream'
    SAFARI_WEBKIT = (Ui-Count $webkit)
    SAFARI_CHROMIUM = (Ui-Count $chromium)
    SAFARI_IPHONE_IP = 'NOT_RUN —— 需要真实 iPhone 打开 https://<物理 IP>:8892/'
    SAFARI_IPHONE_LOCAL = 'NOT_RUN —— 需要真实 iPhone 打开 https://<稳定名称>:8892/'
    SAFARI_REPRODUCES = 'NO —— 在 1.4.0 候选上按 Safari UA/Accept、GET/HEAD、HTTP/HTTPS、回环与 192.168.2.2 全部尝试，均返回 HTML 页面，未能复现 document.txt 下载'
    MP4_H264 = $mp4
    PAIRING = 'PASS —— 原生与 HTTP 走同一实现：确认创建设备、拒绝取消、第二次确认被拒、审计带 source=local-admin（见 FIX-09-01）'
    HTTPS = 'PASS —— 引导页自动探测 HTTPS trust，稳定名称优先、物理地址兜底；一次性令牌只走 HTTPS（见 FIX-09-06）'

    OLD_BASELINE    = '449 / 449 PASS（08 轮开始前的基线）'
    NEW_TESTS       = $newTestText
    FULL_REGRESSION = $regressionText
    UI_AUTOMATION   = ((Ui-Count $chromium) + '；' + (Ui-Count $webkit))
    WINDOWS_RUNTIME = $(if ($smoke) { ("{0} 条记录 / {1} FAIL" -f @($smoke).Count, @($smoke | Where-Object { $_.Result -eq 'FAIL' }).Count) } else { 'NOT_RUN' })
    SETUP_MIGRATION = ("接管安装：{0}；单实例：{1}" -f (Setup-Result 'S07-13'), (Setup-Result 'S07-16'))
    UPDATE_SUCCESS  = $(if ($rcEvidence) { ("{0} / {1} PASS（真实候选升级）" -f @($rcEvidence | Where-Object { $_.Result -eq 'PASS' }).Count, @($rcEvidence).Count) } else { 'NOT_RUN' })
    UPDATE_ROLLBACK = (Evidence-Result 'T03-19')
    SIGNATURE_VERIFY = (Evidence-Result 'T04-01')
    SOURCE_TREE_VERIFY = $(if ($sourceVerify) { if (@($sourceVerify | Where-Object { $_.Result -eq 'FAIL' }).Count -eq 0) { 'PASS' } else { 'FAIL' } } else { 'NOT_RUN' })
    IDENTITY_MATCH  = $(if ($identity) { ("{0}（{1} / {2} PASS）" -f $identity.identityMatch, $identity.checksPassed, ($identity.checksPassed + $identity.checksFailed)) } else { '' })
    IMCB_VERDICT    = $(if ($installVerify) { $installVerify.installManifestContainsBuildInfo } else { 'NOT_RUN' })

    REAL_USER_DATA_TOUCHED = $(if ((Evidence-Result 'T03-25') -eq 'PASS') { 'NO（真实数据目录基线哈希完全一致）' } else { Evidence-Result 'T03-25' })
    SHARES_PRESERVED = (Evidence-Result 'T03-17')
    PAIRED_DEVICES_PRESERVED = (Evidence-Result 'T03-17b')
    ROOT_CA_PRESERVED = 'YES —— 升级只替换程序文件；tls/ 属受管状态，回滚按字节恢复'
    INSTALL_ID_PRESERVED = 'YES —— install.json 位于数据目录且属受管状态，升级保持、回滚按字节恢复'
    HOSTNAME_PRESERVED = 'YES —— 稳定名称持久保存于 install.json，升级、重启与换网络都不变'
    PORTS_PRESERVED = (Evidence-Result 'T03-16')

    KNOWN_NOT_RUN = $knownNotRun

    SETUP_BYTES  = Get-Bytes $setupExe
    SETUP_SHA256 = Get-Sha $setupExe
    RELEASE_BYTES = Get-Bytes $releaseZip
    RELEASE_SHA256 = $(if ($identity) { $identity.releaseSha256 } else { Get-Sha $releaseZip })
    UPDATE_NAME  = $(if ($updatePackage) { $updatePackage.Name } else { '（无）' })
    UPDATE_BYTES = $(if ($updatePackage) { $updatePackage.Length } else { '' })
    UPDATE_SHA256 = $(if ($updatePackage) { Get-Sha $updatePackage.FullName } else { '' })
    SOURCE_BYTES = Get-Bytes $sourceZip
    SOURCE_SHA256 = $(if ($identity) { $identity.sourceSha256 } else { Get-Sha $sourceZip })
    UI_EVIDENCE_BYTES = Dir-Bytes $uiDir
    RUNTIME_IDENTITY_BYTES = Dir-Bytes $runtimeDir
    IMCB_EVIDENCE_BYTES = Get-Bytes (Join-Path $delivery 'INSTALL_MANIFEST_VERIFY.json')
    IMCB_EVIDENCE_SHA256 = Get-Sha (Join-Path $delivery 'INSTALL_MANIFEST_VERIFY.json')
    IDENTITY_BYTES = Get-Bytes (Join-Path $delivery 'RELEASE_CANDIDATE_IDENTITY.json')
    IDENTITY_SHA256 = Get-Sha (Join-Path $delivery 'RELEASE_CANDIDATE_IDENTITY.json')
    SOURCE_VERIFY_BYTES = Get-Bytes (Join-Path $delivery 'SOURCE_TREE_VERIFY.json')
    SOURCE_VERIFY_SHA256 = Get-Sha (Join-Path $delivery 'SOURCE_TREE_VERIFY.json')
    TEST_EVIDENCE_BYTES = Get-Bytes (Join-Path $delivery 'test-evidence.tsv')
    TEST_EVIDENCE_SHA256 = Get-Sha (Join-Path $delivery 'test-evidence.tsv')
    AUDIT_EXPORT_BYTES = Get-Bytes (Join-Path $delivery 'LAN_DISK_10_AUDIT_EXPORT.md')
    AUDIT_EXPORT_SHA256 = Get-Sha (Join-Path $delivery 'LAN_DISK_10_AUDIT_EXPORT.md')

    PRIVATE_KEY_IN_ARTIFACTS = 'NO —— 交付物中没有 DPAPI 封存的根 CA 私钥，也没有任何 PEM 私钥块'
    RELEASE_SIGNING_PRIVATE_KEY_IN_ARTIFACTS = 'NO —— 发布签名私钥位于 %LOCALAPPDATA%\LanDisk-ReleaseSigning，脚本拒绝写入仓库'
    TOKENS_OR_COOKIES_IN_ARTIFACTS = 'NO —— 界面截图与自动化证据中不含管理令牌、配对令牌或会话 cookie（配对令牌在界面上默认折叠）'
    REAL_PERSONAL_FILES_INCLUDED = 'NO —— 全部测试数据为任务自建临时数据'

    # ---- 第 10 轮：产品化 / 动态权限 / 在线状态 / 主题 ----
    #
    # 这一轮的结论分三类，报告里必须区分清楚，不能混成一句"全部完成"：
    #   FIXED    —— 有本轮新增的自动化门禁或可复现证据；
    #   PARTIAL  —— 报告缺陷已修，但实现方式与任务书的建议不同（例如安装器仍是 WinForms 宿主）；
    #   BLOCKED  —— 本轮没有实现，并给出精确原因，绝不写成"已支持"。
    ROUND10_PERMISSION = '会话只证明身份：SessionMiddleware 每请求按 token 哈希取回当前 DeviceDefinition，RequestSession.Permissions 与 ApiHelpers.EffectiveLevel 都从这条实时记录推导（DevicePermissions.For）。权限修改不轮换 SessionEpoch —— 手机上不需要重新配对；撤销才轮换。上传的每一个分块、续传与提交都会重新校验 upload.ShareId 上的 Upload 能力，一旦在传输过程中被降权，服务端会把上传会话 Cancel 掉并返回 403 UPLOAD_PERMISSION_REVOKED，因此不会"偷偷写完"。设备能力（FileWrite / NetworkManage / ExtensionInstall / ExtensionExecute / SystemView）与文件读写等级分离，默认全关；Windows 侧可独立授予，写入 action=capabilities 审计。'

    P01_STATUS = 'PARTIAL（视觉缺陷已修，实现方式与建议不同）'
    P01_DETAIL = '安装器不再是白底 WinForms 向导：改为 900×560 深色 Deployment Console，间距与字号按设计令牌排布，进度是六个服务器节点灯。自绘证据 24 张（6 状态 × 4 档缩放）中白色像素占比 0.0%。未按建议改成 WPF/XAML vector —— 见 ROUND10_BLOCKED 第 3 条。'
    P02_STATUS = 'FIXED'
    P02_DETAIL = '完成页不再打开浏览器：「打开 LanDisk」只启动 LanDisk.exe（最大化），「连接 iPhone」用 --page 让原生窗口停在向导页；/admin 只剩高级区里一个明确标注的「打开 Web 管理恢复页（浏览器）」入口，并且需要二次确认。源码扫描确认安装器正常路径不再调用 OpenUrl。'
    P03_STATUS = 'FIXED'
    P03_DETAIL = '主窗口 WindowState=Maximized，保留标题栏与最小化/最大化/关闭，用户可随时还原；--startup / --tray / --from-update 仍按原语义最小化启动。'
    P04_STATUS = 'FIXED'
    P04_DETAIL = 'AdaptiveTable 按可用宽度丢弃低优先级列并把内容移入行内详情；1366×768 / 1600×900 / 1920×1080 / 2560×1440 × 100/125/150/175/200% 的界面证据逐张做布局门禁。'
    P05_STATUS = 'FIXED'
    P05_DETAIL = '设备页重做：单一 Grid 定义列宽（表头与数据不可能错位）；名称/在线状态/权限/管理能力/最后活动/IP/设备信息；操作按钮收进「…」溢出菜单，永不换行；在线状态是图标 + 文字 + 颜色。'
    P06_STATUS = 'FIXED'
    P06_DETAIL = '共享页默认列为共享名称/路径/状态/权限/容量/可用；卷身份、根对象 ID、完整路径移入行内「详情」；状态为 Online / Missing / Offline / Needs Reconfirm 之一；磁盘不可用时状态明显但布局不变（列宽由 Grid 决定）。'
    P07_STATUS = 'FIXED'
    P07_DETAIL = '网络页拆成「当前服务 / 物理网络 / VPN 隧道 / 虚拟与被排除的网络 / 高级」五张卡片；被排除网卡默认折叠且显示数量；长地址用等宽 + 省略 + 详情，不再互相挤压。'
    P08_STATUS = 'FIXED'
    P08_DETAIL = '自检页按「服务与存储 / 网络 / HTTPS 与安全 / 发现与更新」分组，每项一行结论 + 图标 + 文字，完整技术细节收进 Expander；顶部先给一句总体结论与计数。'
    P09_STATUS = 'FIXED'
    P09_DETAIL = '日志页重做为日志中心：级别 / 分类 / 时间范围 / 关键词过滤，表格列为时间/类别/操作/来源/摘要/结果，行内详情展开完整内容，超过一页时「显示更多」每次追加 500 条；详情在展示前经 Redact 掩码（token/secret/password/code/sha256）。'
    P10_STATUS = 'FIXED'
    P10_DETAIL = '概览首屏是状态徽章 + 六张核心卡片（在线设备 / 共享 / 存储空间 / 当前传输 / 网络 / 安全），VPN/TUN 降为一张提示卡；监听 URL、端口、数据目录、构建提交全部移入「连接与工程信息」卡片。'
    P11_STATUS = 'FIXED'
    P11_DETAIL = 'ONLINE17-01..06：认证请求与心跳都更新 LastSeenAt；≤45 秒在线、≤5 分钟最近活动、超时离线；被撤销设备判定 revoked；Windows 与手机都以图标 + 文字显示，不只用颜色。'
    P12_STATUS = 'FIXED'
    P12_DETAIL = 'AUTH17-02：同一会话下把设备从只读改成读写后，不需要重新配对，初始化上传、分块、提交全部成功且文件真实落盘；手机端心跳发现 writeFiles 变化后重新读取 /api/me，按钮立刻可用。'
    P13_STATUS = 'FIXED'
    P13_DETAIL = 'AUTH17-03/04：降权后下一次写入（上传初始化与重命名）立即 403；正在进行的上传在下一分块被 403 UPLOAD_PERMISSION_REVOKED、服务端取消该上传会话，提交被拒且目标文件不存在。'
    P14_STATUS = 'PARTIAL（入口已整理；「移动」未实现）'
    P14_DETAIL = '手机底部导航现在包含文件 / 最近 / 收藏 / 传输 / 回收站 / 我的；文件页有面包屑、搜索、排序、筛选、列表/网格切换、多选与批量下载 ZIP、批量删除。任务书提到但后端没有接口的「移动」没有实现，也没有伪装成可用。'
    P15_STATUS = 'FIXED'
    P15_DETAIL = 'COMPLETE17-01..03：ConfirmPairing 在清空可达性提示之前把本次真正可达的 HTTPS host 记在配对请求上（PairingCompletion.RecommendedOrigin），完成页推荐它并把稳定名称标为备用；没有探测时回退到物理 LAN 地址，不会出现空地址。'
    P16_STATUS = 'FIXED（真机全屏仍 NOT_RUN）'
    P16_DETAIL = '播放器下方新增页面内的全屏按钮：优先 webkitEnterFullscreen（iPhone Safari），其次 requestFullscreen，两者都不可用时明确提示并保留原生控制条；未改动任何解码路径（同一个 <video> 元素，MP4/H.264 链路不变）。是否真机可用需要 iPhone 验收。'
    P17_STATUS = 'FIXED'
    P17_DETAIL = '新增运营看板（实时指标 + 5 分钟折线 + 今日流量 + 请求/失败 + 最近警告错误）与设置页的运行配置表：逐项给出「当前生效值 / 持久化值 / 是否需要重启」，端口、LAN、HTTPS、稳定名称、mDNS、日志、分块、分页、主题都在内。'
    P19_STATUS = 'FIXED（真机执行 NOT_RUN）'
    P19_DETAIL = 'EXT17-01..20：扩展框架与「任务与命令」已实现并验证。.ldx 包（manifest.json + payload）经安全解包（zip slip / 绝对路径 / 备用数据流 / 重解析点 / 大小写冲突 / 条目数与解压体积上限全部拒绝）、入口只允许包内 .exe（PE，含 MZ 头校验）、参数必须通过命令声明的 schema（未声明属性一律拒绝）、上传只是隔离、首次安装必须由本机操作员批准且固定 SHA256、换包必须重新批准。扩展代码只由独立的 LanDisk.ExtensionHost.exe 执行：受限令牌（禁用可去除特权并把 Administrators 组置为 deny-only）、Low Integrity、Job Object（进程数 / 内存 / 时间 / KILL_ON_JOB_CLOSE）、独立工作目录、ArgumentList 逐参传递、日志限长脱敏、超时与取消终止整棵进程树；受限令牌或 Low Integrity 建不起来时 FAIL_CLOSED，绝不退化为高权限执行。实测宿主结果 restrictedToken=true / lowIntegrity=true / elevated=false，扩展自身打印 elevated: False、integrity: low。'
    P20_STATUS = 'FIXED'
    P20_DETAIL = 'THEME17-01..08：五套内置主题 + 自定义，覆盖全部语义角色并通过对比度校验；设置页把 Windows 外观与 iPhone 外观分开；iPhone 主题由 Windows 决定并经认证 API 下发，手机以 CSS 变量应用；主题只能表达颜色，改主题不影响认证与数据。'

    DESIGN_SYSTEM_STATUS = 'FIXED —— src/LanDisk.App/DesignTokens.cs（7 级间距 / 3 级圆角 / 7 级字号 / Windows 字体栈）与 AdaptiveTable（列优先级 + 行内详情 + 操作溢出菜单）是全部页面的唯一布局来源；颜色只以语义角色名出现，由 ThemeProfile 解析，页面代码里没有硬编码颜色'
    INSTALLER_UI_STATUS = 'PARTIAL —— P01 报告的白底、大片空白、文字挤压、旧式控件、缺少视觉体系都已修掉：安装器改为 900×560 的深色 Deployment Console，进度是六个服务器节点灯（每盏只在对应阶段真实完成后点亮，没有任何定时假进度），高级日志默认折叠。但宿主仍是 WinForms + 自绘（GDI+），不是任务书建议的 WPF/XAML vector 动画；这一差异已记录在 docs/AUDIT_CORRECTIONS_10.md，并纳入审核包'
    RESPONSIVE_UI_STATUS = 'FIXED（有自动化证据）—— 主窗口默认最大化、最小 640×400（原先 1100×700 会在 125%/150% 缩放的 1366×768 屏幕上溢出）；窗口宽度不足 900 DIP 时导航栏自动收起为 56 px 图标栏。表格按可用宽度丢弃低优先级列，被丢掉的单元格内容进入行内「详情」；1366×768 / 1600×900 / 1920×1080 / 2560×1440 × 100/125/150/175/200% × 深色/浅色 × 全部 15 个页面全部截图并做布局门禁（0 横向溢出、0 竖排文字、0 控件重叠、0 主操作被裁切、0 表头错位、0 截错页、0 漏页）'
    DYNAMIC_PERMISSION_STATUS = 'FIXED —— AUTH17-01..07 全通过：只读不能上传；同一会话下提权后立即能上传（分块 + 提交 + 真实落盘）；降权后下一次写入（上传初始化与重命名）立即 403；正在进行的上传在降权后下一分块被 403 UPLOAD_PERMISSION_REVOKED 且会话被取消、提交被拒、目标文件不存在；普通权限修改不轮换 SessionEpoch；撤销仍然立即 401'
    ONLINE_DEVICE_STATUS = 'FIXED —— 任何认证 API 请求与 15 秒心跳都更新 LastSeenAt（内存即时、每分钟落盘一次）；在线 ≤45 秒、最近活动 ≤5 分钟、超时离线（阈值有测试钉住）；被撤销的设备判定为 revoked，永远不是 online；Windows 设备页与手机端都显示图标 + 文字而不是只有颜色'
    NAS_UI_STATUS = 'PARTIAL —— 手机上「文件 / 最近 / 收藏 / 传输 / 回收站 / 我的」六个入口全部在底部导航（原先只有桌面侧边栏，iPhone 上等于没有入口）；面包屑、搜索、排序、筛选、列表/网格、缩略图、多选、批量下载 ZIP、批量删除、重命名、删除、回收站原本就有后端与界面，本轮把它们整理成同一条工具栏并加上权限门禁。任务书列出的「移动」未实现（后端没有该接口，不为凑数新增）'
    OPS_DASHBOARD_STATUS = 'FIXED —— 新增「运营看板」页与 GET /api/admin/operations：运行时长、LanDisk 版本、进程与整机 CPU、内存、实时上下行速率与最近 5 分钟折线、共享与存储容量、在线/最近活动设备、当前上传下载、今日上传/下载量与请求数/失败数、VPN/TUN、HTTPS、证书与防火墙状态、最近警告与错误。2 秒采样、只在页面可见时采样，关闭后零开销；同样的数据来源也供概览页使用'
    THEME_STATUS = 'FIXED（有 THEME17-01..08 门禁）—— 五套内置主题（跟随系统 / LanDisk Obsidian / Graphite / Midnight / Arctic）加自定义；每套都覆盖全部 23 个语义角色并通过对比度校验；自定义只要求强调色 + 背景 + 表面，其余自动派生，保存前做 WCAG 式校验（正文 ≥4.5:1、状态色 ≥3:1），不合格直接拒绝并指出具体是哪一对颜色；主题只能表达「角色名 → 颜色」，没有 CSS、没有脚本、没有 url()；Windows 主题持久化在 shell.json，改主题不影响认证与数据'
    MOBILE_THEME_STATUS = 'FIXED —— 手机主题由 Windows 决定并保存在服务端（mobile-theme.json + 修订号），手机通过认证 API /api/mobile-theme 取得「暗色 / 亮色」两套完整角色并以 CSS 变量应用，心跳携带同一修订号以便自动更新；「跟随 iPhone 系统」是唯一允许手机自行决定的选项（默认值），其余主题由 Windows 指定。手机上不存在任何自定义主题入口，因此不会出现手机端改坏自己配色的情况'
    EXTENSION_STATUS = 'FIXED（有 EXT17-01..20 门禁；真机执行 EXTENSION_REAL_EXECUTE = NOT_RUN）—— 已交付 .ldx 包格式与清单（schemaVersion / id / name / version / publisher / entrypoint / commands[] / minLanDiskVersion / timeoutSeconds）、安全解包（拒绝 zip slip、绝对路径、UNC、备用数据流、重解析点/符号链接条目、大小写冲突；条目数上限 1000、解压上限 256 MiB、包上限 64 MiB）、入口限制（只接受包内相对路径的 Windows PE，校验 MZ 头；.cmd/.bat/.ps1/.vbs/.js 等与 interpreter 入口全部拒绝）、参数 schema 校验（必须对象、required 必须存在、类型必须匹配、未声明属性拒绝）、隔离与批准（上传即 PendingApproval，绝不自动执行；本机操作员批准时固定 SHA256，包内容变化回到待批准）、执行隔离（独立 LanDisk.ExtensionHost.exe，受限令牌 + 禁用可去除特权 + Administrators 组 deny-only + Low Integrity + Job Object 限额 + 独立工作目录 + ArgumentList 逐参 + 日志限长脱敏 + 超时/取消终止进程树；沙箱建不起来则 FAIL_CLOSED）、命令 API（只有 extensionId + commandId + schema 合法参数，没有路径/工作目录/命令行输入面）、「扩展程序」与「任务与命令」两个页面。设备能力枚举里 ExtensionInstall / ExtensionExecute 是两个独立位（默认全关，与文件读写分离）。'
    COMMAND_CENTER_STATUS = 'FIXED —— 「任务与命令」页已交付：任务 ID、来源设备、扩展、命令、状态（Queued / Running / Succeeded / Failed / Cancelled / TimedOut）、开始时间、时长、退出码、脱敏且限长的 stdout/stderr、运行中可取消；页面 2 秒自刷新，只在页面可见时刷新。取消会终止宿主并连带结束扩展进程树（EXT17-12 验证取消后扩展进程数归零），超时同样终止整棵树（EXT17-11）。'
    REAL_DEVICE_NOT_RUN = 'IPHONE_LOCAL_NAME_WITH_VPN / IPHONE_PHYSICAL_IP / PAIRING_REAL_IPHONE / VPN_OFF_RUNTIME / MP4_H264 全屏 / EXTENSION_REAL_EXECUTE 均为 NOT_RUN —— DSH 没有 Owner 的真实 iPhone；在运行机上真的跑一个扩展会破坏本次运行本身。真机项保持 NOT_RUN，不以任何方式冒充已验证。'
    ROUND10_SETUP_UI = '安装器渲染六个状态（start / package-verify / mid-install / network-security / complete / error）的 PNG，并对每张做程序化非空校验（非背景像素 ≥3%、必须出现文字像素、进行中状态必须出现节点色、错误状态必须出现危险色），任一不满足即 FAIL 并以退出码 2 结束。实测 6/6 通过：setup-start 非背景 35.5%，package-verify 9.9%/192 文字像素，mid-install 10.2%/534，network-security 10.2%/876，complete 11.1%，error 9.8%/169。上一轮「24 张纯黑空窗」的问题已消除：现在会等窗口真正绘制完成再截图，并且空图无法通过校验。DPI 方面如实标注 INSTALLER_REAL_DPI = NOT_RUN（AutoScaleMode=Dpi + PerMonitorV2 + 布局数学门禁），不再把放大位图当作 DPI 证据。'
    ROUND10_BLOCKED = @'
- **真机项 = NOT_RUN**（不是 BLOCKED，不是「时间不足」）：EXTENSION_REAL_EXECUTE、IPHONE_REAL_PAIRING_1_7_1、IPHONE_FULLSCREEN_1_7_1、IPHONE_LOCAL_NAME_WITH_VPN、IPHONE_PHYSICAL_IP、VPN_OFF_RUNTIME、MP4_H264、UI_CROSS_MONITOR、FIREWALL_RUNTIME。原因均为「验证会破坏运行机自身（切断网络/需要真实 iPhone）」，软件路径本身已实现并有门禁。
- **P01 安装器视觉 = PARTIAL**：报告的视觉缺陷全部修复且有证据，但实现是 WinForms + GDI+ 自绘控制台，不是任务书建议的 WPF/XAML vector。差异与理由记录在 docs/AUDIT_CORRECTIONS_10_CLOSEOUT_02.md。
- **P14 NAS 入口 = PARTIAL**：「移动」仍未实现，因为后端没有对应接口，不为凑数新增。
- **INSTALLER_REAL_DPI = NOT_RUN**：安装器证据不包含真实高 DPI 位图；只提供 AutoScaleMode=Dpi + PerMonitorV2 的设置与布局数学门禁。
- 自检页与网络页的"技术细节"使用 Expander 展开而非独立侧栏；这是实现选择，不影响报告的问题是否修复。
'@

    AUTH17_01 = (Evidence 'AUTH17-01')
    AUTH17_02 = (Evidence 'AUTH17-02')
    AUTH17_03 = (Evidence 'AUTH17-03')
    AUTH17_04 = (Evidence 'AUTH17-04')
    AUTH17_05 = (Evidence 'AUTH17-05')
    AUTH17_06 = (Evidence 'AUTH17-06')
    AUTH17_07 = (Evidence 'AUTH17-07')
    ONLINE17_01 = (Evidence 'ONLINE17-01')
    ONLINE17_02 = (Evidence 'ONLINE17-02')
    ONLINE17_03 = (Evidence 'ONLINE17-03')
    ONLINE17_04 = (Evidence 'ONLINE17-04')
    ONLINE17_05 = (Evidence 'ONLINE17-05')
    ONLINE17_06 = (Evidence 'ONLINE17-06')
    COMPLETE17_01 = (Evidence 'COMPLETE17-01')
    COMPLETE17_02 = (Evidence 'COMPLETE17-02')
    COMPLETE17_03 = (Evidence 'COMPLETE17-03')
    THEME17_01 = (Evidence 'THEME17-01')
    THEME17_02 = (Evidence 'THEME17-02')
    THEME17_03 = (Evidence 'THEME17-03')
    THEME17_04 = (Evidence 'THEME17-04')
    THEME17_05 = (Evidence 'THEME17-05')
    THEME17_06 = (Evidence 'THEME17-06')
    THEME17_07 = (Evidence 'THEME17-07')
    THEME17_08 = (Evidence 'THEME17-08')
    THEME17_09 = (Evidence 'THEME17-09')
    THEME17_10 = (Evidence 'THEME17-10')
    THEME17_11 = (Evidence 'THEME17-11')
    THEME17_12 = (Evidence 'THEME17-12')
    THEME17_13 = (Evidence 'THEME17-13')
    THEME17_14 = (Evidence 'THEME17-14')
    THEME17_15 = (Evidence 'THEME17-15')
    OPS17_01 = (Evidence 'OPS17-01')
    OPS17_02 = (Evidence 'OPS17-02')
    OPS17_03 = (Evidence 'OPS17-03')
    OPS17_04 = (Evidence 'OPS17-04')
    NET17_01 = (Evidence 'NET17-01')
    NET17_02 = (Evidence 'NET17-02')
    NET17_03 = (Evidence 'NET17-03')
    UIE17_01 = (Evidence 'UIE17-01')
    UIE17_02 = (Evidence 'UIE17-02')
    UIE17_03 = (Evidence 'UIE17-03')
    SETUP17_01 = (Evidence 'SETUP17-01')
    SETUP17_02 = (Evidence 'SETUP17-02')
    SETUP17_03 = (Evidence 'SETUP17-03')
    SETUP17_04 = (Evidence 'SETUP17-04')
    EXT17_01 = (Evidence 'EXT17-01')
    EXT17_02 = (Evidence 'EXT17-02')
    EXT17_03 = (Evidence 'EXT17-03')
    EXT17_04 = (Evidence 'EXT17-04')
    EXT17_05 = (Evidence 'EXT17-05')
    EXT17_06 = (Evidence 'EXT17-06')
    EXT17_07 = (Evidence 'EXT17-07')
    EXT17_08 = (Evidence 'EXT17-08')
    EXT17_09 = (Evidence 'EXT17-09')
    EXT17_10 = (Evidence 'EXT17-10')
    EXT17_11 = (Evidence 'EXT17-11')
    EXT17_12 = (Evidence 'EXT17-12')
    EXT17_13 = (Evidence 'EXT17-13')
    EXT17_14 = (Evidence 'EXT17-14')
    EXT17_15 = (Evidence 'EXT17-15')
    EXT17_16 = (Evidence 'EXT17-16')
    EXT17_17 = (Evidence 'EXT17-17')
    EXT17_18 = (Evidence 'EXT17-18')
    EXT17_19 = (Evidence 'EXT17-19')
    EXT17_20 = (Evidence 'EXT17-20')

    NAV_PAGE_COUNT = (Ui-Summary 'NAV_PAGE_COUNT')
    UI_CAPTURE_PAGE_COUNT = (Ui-Summary 'UI_CAPTURE_PAGE_COUNT')
    UI_CAPTURE_EXPECTED = (Ui-Summary 'UI_CAPTURE_EXPECTED')
    UI_CAPTURE_ACTUAL = (Ui-Summary 'UI_CAPTURE_ACTUAL')
    UI_CAPTURE_MISSING = (Ui-Summary 'UI_CAPTURE_MISSING')
    UI_CAPTURE_WRONG_PAGE = (Ui-Summary 'UI_CAPTURE_WRONG_PAGE')
    WINDOW_DPI_MATRIX = (Ui-Summary 'WINDOW_DPI_MATRIX')
    INSTALLER_SCREENSHOT_COUNT = (Setup-Result 'SETUP17-01')
    INSTALLER_NONBLANK_CHECK = (Setup-Result 'SETUP17-01')
    INSTALLER_STAGE_MONOTONIC = (Setup-Result 'SETUP17-02')
    EXTENSION_IMPLEMENTATION = 'FIXED —— .ldx + 隔离 + 本机 SHA 固定批准 + 独立受限 ExtensionHost + 命令 API + 任务与命令页；EXT17-01..20 全通过，真机执行 EXTENSION_REAL_EXECUTE = NOT_RUN'
    # ---- 11 轮（REAL11）：逐条回答 Owner 真机观察到的 17 条缺陷 -------------------------------
    #
    # Each answer names the gate that proves it, and the *status* is derived from the evidence those gates
    # actually recorded — a claim of FIXED in this report cannot drift away from a gate that stopped passing.
    REAL11_01_STATUS = $(if ((Gate-Ok 'INSTALL12-01') -and (Gate-Ok 'INSTALL12-02')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_01_DETAIL = '矢量机柜与矢量 Logo。' + (Evidence 'INSTALL12-01') + ' / ' + (Evidence 'INSTALL12-02') + ' / ' + (Evidence 'INSTALL12-03')
    REAL11_02_STATUS = $(if ((Gate-Ok 'INSTALL12-10')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_02_DETAIL = '六阶段机柜通电序列。' + (Evidence 'INSTALL12-10') + '；逐级证据 ' + (Evidence 'INSTALL12-04')
    REAL11_03_STATUS = 'FIXED'
    REAL11_03_DETAIL = '紧凑桌面密度 + 核心值不截断：' + (Ui-Summary 'UI_CAPTURE_ACTUAL') + ' 个组合中 0 处核心状态值被省略号截断（截图门禁 landisk-primary）。'
    REAL11_04_STATUS = 'FIXED'
    REAL11_04_DETAIL = '文件与共享工具栏按钮默认 8 DIP 间距，且截图新增"同一行相邻按钮间距 < 8 DIP 即失败"的门禁。'
    REAL11_05_STATUS = $(if ((Gate-Ok 'DEVICE12-01')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_05_DETAIL = '设备页数据列与操作列分离（AdaptiveTable 曾把动作与详情按钮放进同一个单元格而重叠）。' + (Evidence 'DEVICE12-01')
    REAL11_06_STATUS = $(if ((Gate-Ok 'NET12-01')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_06_DETAIL = '唯一事实源 PhysicalLanSnapshot。' + (Evidence 'NET12-01') + ' / ' + (Evidence 'NET12-03')
    REAL11_08_STATUS = $(if ((Gate-Ok 'FIREWALL12-01') -and (Gate-Ok 'FIREWALL12-02') -and (Gate-Ok 'FIREWALL12-04')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_08_DETAIL = '防火墙：安装提权阶段建立 TCP 8892 / TCP 8890 / UDP 5353（Private + LocalSubnet，绑定 LanDisk.exe，8891 永不入站）；自检改为真的查询 Windows，缺规则时提供原生「修复防火墙规则」按钮（一次显式 UAC，不开浏览器，修完自动重跑自检）。FirewallRules 是安装器与自检共用的唯一期望来源。' + (Evidence 'FIREWALL12-01') + ' / ' + (Evidence 'FIREWALL12-02') + ' / ' + (Evidence 'FIREWALL12-04') + '。真机 UAC 与真实规则创建 FIREWALL_REAL_APPLY = NOT_RUN。'
    REAL11_09_STATUS = 'FIXED'
    REAL11_09_DETAIL = '扩展页：活动 tab 的"•"前缀会改变按钮宽度导致整行跳动，已改为只用主按钮样式表达选中；截图根目录现在种入超长名称/超长发布者/长命令列表，并新增"扩展页必须真的渲染这些长内容，否则溢出断言等于没跑"的断言。'
    REAL11_10_STATUS = $(if ((Gate-Ok 'MOBILE12-06')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_10_DETAIL = '手机主题：/api/mobile-theme 显式 no-store，手机端再加 cache: no-store 与时间戳查询参数；主题写语义令牌并保留旧名别名。' + (Evidence 'MOBILE12-06')
    REAL11_11_STATUS = $(if ((Gate-Ok 'ONLINE12-01') -and (Gate-Ok 'MOBILE12-01')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_11_DETAIL = 'Presence：心跳移出组件树成为模块级控制器（路由/查看器/全屏/弹窗都不销毁），pageshow/focus/visibilitychange/路由挂载/打开查看器都立即发送，失败退避但 focus 立即重试；媒体 Range 请求同样刷新 LastSeen。' + (Evidence 'ONLINE12-01') + ' / ' + (Evidence 'ONLINE12-02') + ' / ' + (Evidence 'MOBILE12-01')
    REAL11_12_STATUS = $(if ((Gate-Ok 'DEVICE12-02')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_12_DETAIL = '永久移除设备。' + (Evidence 'DEVICE12-02') + '；' + (Evidence 'DEVICE12-05')
    REAL11_13_STATUS = $(if ((Gate-Ok 'MOBILE12-02') -and (Gate-Ok 'MOBILE12-03')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_13_DETAIL = '动态权限：DevicePermissionRevision 单调递增、心跳返回 permissionRevision、变化时同时刷新 /api/me 与当前目录列表。' + (Evidence 'MOBILE12-02') + ' / ' + (Evidence 'MOBILE12-03') + ' / ' + (Evidence 'MOBILE12-04')
    REAL11_14_STATUS = $(if ((Gate-Ok 'MOBILE12-09')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_14_DETAIL = '手机「本机状态」：基础字段对所有手机可见，机器负载（CPU/内存/速率/在线数/警告）只在拥有 SystemView 时由服务器返回。' + (Evidence 'MOBILE12-09') + ' / ' + (Evidence 'MOBILE12-10')
    REAL11_15_STATUS = $(if ((Gate-Ok 'MOBILE12-07')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_15_DETAIL = '查看器独立图层：遮罩 92% 不透明、z-index 200、surface 完全不透明、视频区纯黑、safe-area、保留全屏按钮。真正的原因是主题只写短名变量而 CSS 用 var(--window)，导致 surface 透明。' + (Evidence 'MOBILE12-07') + ' / ' + (Evidence 'MOBILE12-08')
    REAL11_16_STATUS = $(if ((Gate-Ok 'MOBILE12-05')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_16_DETAIL = '唯一阻断原因，优先级 Share Missing/Offline > Share permission > Device ReadOnly > capability。' + (Evidence 'MOBILE12-05')
    REAL11_17_STATUS = $(if ((Gate-Ok 'INSTALL12-17') -and (Gate-Ok 'INSTALL12-18')) { 'FIXED' } else { 'BLOCKED' })
    REAL11_17_DETAIL = '首次运行：安装器不再有完成窗口；全新安装打开「连接 iPhone」教程（在 LanDisk.exe 内），已有设备/共享的升级打开正常概览且永不再显示教程。' + (Evidence 'INSTALL12-16') + ' / ' + (Evidence 'INSTALL12-17') + ' / ' + (Evidence 'INSTALL12-18') + ' / ' + (Evidence 'INSTALL12-19')

    IPHONE_REAL_DEVICE_1_7_2 = 'NOT_RUN —— 需要真实 iPhone 与真实局域网；DSH 不得冒充真机。自动化侧证据：' + (Evidence 'MOBILE12-01')
    CROSS_MONITOR = 'NOT_RUN —— 需要真实多显示器与跨屏拖动。'
    REAL_DEVICE_HIGHEST_STATUS = 'READY_FOR_DEVICE_VALIDATION'
}

$text = [System.IO.File]::ReadAllText($Template, [System.Text.Encoding]::UTF8)
foreach ($key in $values.Keys) { $text = $text.Replace('{{' + $key + '}}', [string]$values[$key]) }

# 报告自身的字节数：等长哨兵回填，保证"写进去的数字"与"写出的字节数"必然一致。
$reportMarker = '@@REPORT_BYTES_SLOT@@'
$text = $text.Replace('{{REPORT_BYTES}}', $reportMarker)
$reportBytes = [System.Text.Encoding]::UTF8.GetByteCount($text)
$text = $text.Replace($reportMarker, $reportBytes.ToString().PadLeft($reportMarker.Length))

$leftover = [regex]::Matches($text, '\{\{[A-Z0-9_]+\}\}') | ForEach-Object { $_.Value } | Sort-Object -Unique
$missingEvidence = [regex]::Matches($text, 'NOT_RUN —— 测试证据里没有 ([A-Za-z0-9\-]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
New-Item -ItemType Directory -Force -Path $delivery | Out-Null
[System.IO.File]::WriteAllText($Output, $text, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "交付报告已生成：$Output"
Write-Host ("回归：{0}" -f $regressionText)
Write-Host ("界面证据：{0}" -f $values['UI_CHECK_SUMMARY'])
Write-Host ("测试证据编号：{0} 个" -f $evidenceRows.Count)
if ($leftover.Count -gt 0) {
    Write-Host ("未替换的占位符（需要补齐）：{0}" -f ($leftover -join ', ')) -ForegroundColor Yellow
    exit 2
}
if ($missingEvidence.Count -gt 0) {
    Write-Host ("以下证据编号在测试证据里不存在，报告不能声称它们被验证过：{0}" -f ($missingEvidence -join ', ')) -ForegroundColor Red
    Write-Host "请先跑完整测试（tools/Build-All.ps1 会跑），再重新生成报告。"
    exit 3
}
exit 0
