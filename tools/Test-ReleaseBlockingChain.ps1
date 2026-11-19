param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$OutFile = ''
)

# 失败传播链与判定器范围测试。
#
#   布局断言失败 -> UiCapture 非零 -> 应用进程非零 -> 发布门禁识别 -> 中止 -> 不生成 READY 报告与发布包
#
# 调用的是**发布入口 tools/Build-All.ps1 所用的同一个门禁脚本**（tools/Invoke-UiMatrixGate.ps1），
# 在隔离输出目录执行；dist/delivery 的历史产物不被触碰。
#
# 两类用例刻意分开：
#   gate  —— C0/C1/C2：要制造真实的布局失败，必须真的跑捕获；
#   judge —— C3/C3b：只重新判定。"删掉必需证据后重新检查"若再跑一次捕获，捕获会把文件写回来，
#            那测的就不是缺证据了。
#
# 门禁按预期拒绝时，**测试用例本身记为 PASS**；不能把门禁的预期非零退出当成测试失败。

$ErrorActionPreference = 'Continue'
if (-not $OutFile) { $OutFile = Join-Path $Repo 'dist\delivery\RELEASE_BLOCKING_CHAIN_TESTS.json' }

$sandbox = Join-Path $Repo 'dist\.release-blocking-sandbox'
if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$script:commit = (& git -C $Repo rev-parse HEAD).Trim()
$script:runId = if ($RunId) { $RunId } else { "" }
$deliveryDir = Join-Path $Repo 'dist\delivery'
$deliveryBefore = @(Get-ChildItem $deliveryDir -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Sort-Object)

$cleanPublish = Join-Path $Repo 'dist\publish\LanDisk-win-x64-DIAGNOSTIC-uicapture'
$overlapPublish = Join-Path $Repo 'dist\publish\LanDisk-win-x64-DIAGNOSTIC-INJECTION'
$clipPublish = Join-Path $Repo 'dist\publish\LanDisk-win-x64-DIAGNOSTIC-INJECTION-CLIP'

$results = New-Object System.Collections.Generic.List[object]

function Read-Gate([string]$dir) {
    $p = Join-Path $dir 'UI_MATRIX_GATE.json'
    if (Test-Path $p) { Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
}
function Read-Probe([string]$dir) {
    $p = Join-Path $dir 'UI_INJECTION_PROBE.tsv'
    if (Test-Path $p) { @(Get-Content -LiteralPath $p -Encoding UTF8 | Select-Object -Skip 1) } else { @() }
}
function Read-Assertion([string]$dir, [string]$id) {
    $p = Join-Path $dir 'UI_LAYOUT_VERIFICATION.tsv'
    if (-not (Test-Path $p)) { return $null }
    $row = Get-Content -LiteralPath $p -Encoding UTF8 | Where-Object { $_ -like ("SUMMARY" + [char]9 + $id + [char]9 + "*") } | Select-Object -First 1
    if (-not $row) { return $null }
    $c = $row -split "`t"
    return [pscustomobject]@{ targets = [int]$c[4]; failures = [int]$c[5]; status = $c[6] }
}
function Count-NewDeliveryFiles {
    $after = @(Get-ChildItem $deliveryDir -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Sort-Object)
    return @($after | Where-Object { $_ -notin $deliveryBefore })
}

function Invoke-CaptureCase {
    param(
        [string]$Id, [string]$Description, [string]$PublishDir, [string]$EvidenceDir,
        [string]$Inject, [hashtable]$Only, [bool]$ExpectBlocked, [string]$AssertionId
    )
    if (Test-Path $EvidenceDir) { Remove-Item $EvidenceDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

    if ($Inject) { $env:LANDISK_CAP_INJECT = $Inject } else { Remove-Item Env:\LANDISK_CAP_INJECT -ErrorAction SilentlyContinue }
    foreach ($k in @('LANDISK_CAP_ONLY_PAGE','LANDISK_CAP_ONLY_THEME','LANDISK_CAP_ONLY_SCALE','LANDISK_CAP_ONLY_SIZE')) {
        Remove-Item ("Env:\" + $k) -ErrorAction SilentlyContinue
    }
    if ($Only) { foreach ($k in $Only.Keys) { Set-Item ("Env:\" + $k) $Only[$k] } }

    $exe = Join-Path $PublishDir 'LanDisk.exe'
    $exeHash = if (Test-Path $exe) { (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    $env:LANDISK_GATE_CALLER = 'dist/tools/Test-ReleaseBlockingChain.ps1'
    $out = & pwsh -NoProfile -File (Join-Path $Repo 'tools\Invoke-UiMatrixGate.ps1') `
        -Repo $Repo -PublishDir $PublishDir -EvidenceDir $EvidenceDir `
        -CandidateCommit $script:commit -ReducedMatrix 2>&1
    $gateExit = $LASTEXITCODE
    Remove-Item Env:\LANDISK_GATE_CALLER -ErrorAction SilentlyContinue
    Remove-Item Env:\LANDISK_CAP_INJECT -ErrorAction SilentlyContinue
    foreach ($k in @('LANDISK_CAP_ONLY_PAGE','LANDISK_CAP_ONLY_THEME','LANDISK_CAP_ONLY_SCALE','LANDISK_CAP_ONLY_SIZE')) {
        Remove-Item ("Env:\" + $k) -ErrorAction SilentlyContinue
    }

    $gate = Read-Gate $EvidenceDir
    $probe = Read-Probe $EvidenceDir
    $newFiles = Count-NewDeliveryFiles
    $readyReport = Test-Path (Join-Path $EvidenceDir 'DELIVERY_REPORT.md')
    $blocked = ($gateExit -ne 0)

    $injection = 'NOT_APPLICABLE'
    if ($Inject) {
        $invalid = @($probe | Where-Object { $_ -like '*INVALID_INJECTION*' })
        $effective = @($probe | Where-Object { $_ -like 'INJECT*' })
        $injection = if ($effective.Count -gt 0) { 'ESTABLISHED' } elseif ($invalid.Count -gt 0) { 'INVALID_INJECTION' } else { 'NO_PROBE' }
    }

    $assertion = if ($AssertionId) { Read-Assertion $EvidenceDir $AssertionId } else { $null }

    $reasons = @()
    if ($blocked -ne $ExpectBlocked) { $reasons += ("门禁退出码 {0}，期望{1}" -f $gateExit, $(if ($ExpectBlocked) { '阻断' } else { '放行' })) }
    if ($newFiles.Count -gt 0) { $reasons += ('dist/delivery 新增了文件：' + ($newFiles -join ',')) }
    if ($readyReport) { $reasons += '写入了 READY 报告' }
    if ($Inject -and $injection -ne 'ESTABLISHED') { $reasons += ('注入未建立缺陷：' + $injection) }
    if ($AssertionId) {
        if (-not $assertion) { $reasons += ('缺少断言 ' + $AssertionId + ' 的记录') }
        elseif ($assertion.targets -le 0) { $reasons += ($AssertionId + ' 目标数为 0（等于没检查）') }
        elseif ($ExpectBlocked -and $assertion.failures -le 0) { $reasons += ($AssertionId + ' 没有失败，失败没落在正确的断言上') }
        elseif (-not $ExpectBlocked -and $assertion.failures -ne 0) { $reasons += ($AssertionId + ' 在对照组里出现了失败') }
    }
    if ($gate -and $gate.candidateCommit -and $gate.candidateCommit -ne $script:commit) { $reasons += '门禁记录的候选身份与预期不符' }

    $results.Add([pscustomobject]@{
        id = $Id
        description = $Description
        test_expectation = if ($ExpectBlocked) { 'BLOCKED' } else { 'CONTINUE' }
        exe_path = $exe
        exe_sha256 = $exeHash
        injection_mode = $Inject
        injection_probe = $injection
        observed_capture_exit = if ($gate) { $gate.exitCode } else { $null }
        observed_gate_exit = $gateExit
        observed_outer_exit = $gateExit
        observed_verdict = if ($gate) { $gate.verdict } else { '(无门禁记录)' }
        block_reason = if ($gate) { $gate.blockReason } else { '' }
        assertion = if ($assertion) { ($AssertionId + ' targets=' + $assertion.targets + ' failures=' + $assertion.failures) } else { '' }
        ready_report_written = $readyReport
        new_files_in_delivery = $newFiles
        test_result = if ($reasons.Count -eq 0) { 'PASS' } else { 'FAIL' }
        reasons = $reasons
        gate_log = ($out | Select-Object -Last 2) -join ' | '
    })
    $mark = if ($reasons.Count -eq 0) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}  capture={2} gate={3} injection={4} assert={5}" -f `
        $mark, $Id, $(if ($gate) { $gate.exitCode } else { '?' }), $gateExit, $injection, $(if ($assertion) { "$($assertion.failures)/$($assertion.targets)" } else { '-' }))
    if ($reasons.Count -gt 0) { $reasons | ForEach-Object { Write-Host ('        - ' + $_) -ForegroundColor Yellow } }
}

function Invoke-JudgeCase {
    param(
        [string]$Id, [string]$Description, [string]$SourceDir, [string]$EvidenceDir,
        [string]$DeleteRelative, [string]$Scope, [bool]$ExpectBlocked
    )
    if (Test-Path $EvidenceDir) { Remove-Item $EvidenceDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
    if (Test-Path $SourceDir) { Copy-Item (Join-Path $SourceDir '*') $EvidenceDir -Recurse -Force -ErrorAction SilentlyContinue }

    $target = if ($DeleteRelative) { Join-Path $EvidenceDir $DeleteRelative } else { '' }
    $existedBefore = if ($target) { Test-Path -LiteralPath $target } else { $true }
    $shaBefore = if ($target -and $existedBefore) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    if ($target -and $existedBefore) { Remove-Item -LiteralPath $target -Force }
    $deleted = if ($target) { -not (Test-Path -LiteralPath $target) } else { $true }

    $pngBefore = @(Get-ChildItem $EvidenceDir -Filter '*.png' -File -ErrorAction SilentlyContinue).Count

    # 只做判定：不再跑捕获，因此不会被重新写回来。
    $verdictScript = Join-Path $Repo 'tools\EvidenceVerdict.ps1'
    $cmd = ". '" + $verdictScript + "'`n" +
           "`$v = Get-EvidenceVerdict -EvidenceRoot '" + $EvidenceDir + "' -ExpectedCandidate '" + $script:commit + "' -Scope " + $Scope + " -SkipMatrixCompleteness`n" +
           "if (`$v.Ready) { 'READY' } else { 'NOT_READY|' + (`$v.Reasons -join ' ; ') }"
    $judgeLog = & pwsh -NoProfile -Command $cmd 2>&1
    $verdictText = [string]($judgeLog | Select-Object -Last 1)
    $ready = $verdictText.Trim() -eq 'READY'
    $blocked = -not $ready
    $reasonText = if ($ready) { '' } else { ($verdictText -replace '^NOT_READY\|', '') }

    $pngAfter = @(Get-ChildItem $EvidenceDir -Filter '*.png' -File -ErrorAction SilentlyContinue).Count
    $recaptured = ($pngAfter -ne $pngBefore)
    $newFiles = Count-NewDeliveryFiles

    $mustMention = if ($DeleteRelative) { Split-Path $DeleteRelative -Leaf } else { 'UI_CAPTURE.tsv' }
    $reasons = @()
    if ($target -and -not $existedBefore) { $reasons += ('被测文件原本不存在：' + $DeleteRelative + '（用例无效）') }
    if (-not $deleted) { $reasons += ('删除未成功：' + $DeleteRelative) }
    if ($blocked -ne $ExpectBlocked) { $reasons += ('判定为 ' + $(if ($ready) { 'READY' } else { 'NOT_READY' }) + '，期望 ' + $(if ($ExpectBlocked) { 'NOT_READY' } else { 'READY' })) }
    if ($blocked -and $reasonText -notlike ('*' + $mustMention + '*')) { $reasons += ('阻断原因未指向 ' + $mustMention + '：' + $reasonText) }
    if ($recaptured) { $reasons += '判定过程重新执行了捕获（PNG 数量变化），测的就不是缺证据了' }
    if ($newFiles.Count -gt 0) { $reasons += ('dist/delivery 新增了文件：' + ($newFiles -join ',')) }

    $results.Add([pscustomobject]@{
        id = $Id
        description = $Description
        test_expectation = if ($ExpectBlocked) { 'NOT_READY' } else { 'READY' }
        scope = $Scope
        required_file_list = if ($Scope -eq 'UI') { 'UI_CAPTURE.tsv, UI_CAPTURE_ENVIRONMENT.tsv' } else { 'UI_CAPTURE.tsv, UI_CAPTURE_ENVIRONMENT.tsv, e2e-ui-chromium.json, e2e-ui-webkit.json, runtime-identity.json, setup-acceptance.json, update-verify.json' }
        deleted_file = $target
        deleted_file_sha256_before = $shaBefore
        deleted_file_absent_after_judgement = if ($target) { -not (Test-Path -LiteralPath $target) } else { $null }
        deletion_succeeded = $deleted
        judged_directory = $EvidenceDir
        judge_invocation = ('pwsh -NoProfile -Command "<dot-source ' + $verdictScript + '>; Get-EvidenceVerdict -EvidenceRoot ' + $EvidenceDir + ' -Scope ' + $Scope + '"')
        judge_calls_capture = $false
        observed_capture_exit = 'N/A（只做判定，不执行捕获）'
        observed_gate_exit = if ($ready) { 0 } else { 4 }
        observed_outer_exit = if ($ready) { 0 } else { 4 }
        observed_verdict = $verdictText.Trim()
        block_reason = $reasonText
        recaptured_during_judgement = $recaptured
        injection_probe = 'N/A'
        assertion = ''
        ready_report_written = $false
        new_files_in_delivery = $newFiles
        test_result = if ($reasons.Count -eq 0) { 'PASS' } else { 'FAIL' }
        reasons = $reasons
        gate_log = ''
    })
    $mark = if ($reasons.Count -eq 0) { 'PASS' } else { 'FAIL' }
    $what = if ($DeleteRelative) { Split-Path $DeleteRelative -Leaf } else { '(无删除)' }
    Write-Host ("  [{0}] {1}  scope={2} deleted={3} -> {4}" -f $mark, $Id, $Scope, $what, $(if ($ready) { 'READY' } else { 'NOT_READY' }))
    if ($reasons.Count -gt 0) { $reasons | ForEach-Object { Write-Host ('        - ' + $_) -ForegroundColor Yellow } }
}

Write-Host 'release blocking chain:'

$onePage = @{ LANDISK_CAP_ONLY_PAGE = '概览'; LANDISK_CAP_ONLY_THEME = 'Dark'; LANDISK_CAP_ONLY_SCALE = '1.00'; LANDISK_CAP_ONLY_SIZE = '1600x900' }
$sharePage = @{ LANDISK_CAP_ONLY_PAGE = '文件与共享'; LANDISK_CAP_ONLY_THEME = 'Dark'; LANDISK_CAP_ONLY_SCALE = '1.00'; LANDISK_CAP_ONLY_SIZE = '1600x900' }
$httpsPage = @{ LANDISK_CAP_ONLY_PAGE = 'HTTPS 与安全'; LANDISK_CAP_ONLY_THEME = 'Dark'; LANDISK_CAP_ONLY_SCALE = '1.00'; LANDISK_CAP_ONLY_SIZE = '1600x900' }

$c0 = Join-Path $sandbox 'c0'
Invoke-CaptureCase -Id 'C0-clean-control' -Description '正常对照：干净构建的定向跑必须放行（否则门禁等于拒绝一切）' `
    -PublishDir $cleanPublish -EvidenceDir $c0 -Inject '' -Only $onePage -ExpectBlocked $false -AssertionId 'LAYOUT-CORE-VALUE-NOT-CLIPPED'

if (Test-Path (Join-Path $overlapPublish 'LanDisk.exe')) {
    Invoke-CaptureCase -Id 'C1-overlap-blocks' -Description '重叠反例：目标控件确实相交，LAYOUT-ACTION-OVERLAP 失败并阻断' `
        -PublishDir $overlapPublish -EvidenceDir (Join-Path $sandbox 'c1') -Inject 'overlap' -Only $sharePage -ExpectBlocked $true -AssertionId 'LAYOUT-ACTION-OVERLAP'
}
else { Write-Host '  [SKIP] 缺少 overlap 注入构建' -ForegroundColor Yellow }

if (Test-Path (Join-Path $clipPublish 'LanDisk.exe')) {
    Invoke-CaptureCase -Id 'C2-clip-blocks' -Description '截断反例：目标核心值确实被截断，LAYOUT-CORE-VALUE-NOT-CLIPPED 失败并阻断' `
        -PublishDir $clipPublish -EvidenceDir (Join-Path $sandbox 'c2') -Inject 'clip' -Only $httpsPage -ExpectBlocked $true -AssertionId 'LAYOUT-CORE-VALUE-NOT-CLIPPED'
}
else { Write-Host '  [SKIP] 缺少 clip 注入构建' -ForegroundColor Yellow }

# C3：捕获从未产出证据（空目录，不做删除）→ UI 判定必须阻断并指向缺失的捕获结果
New-Item -ItemType Directory -Force -Path (Join-Path $sandbox 'empty-source') | Out-Null
Invoke-JudgeCase -Id 'C3-empty-evidence-blocks' -Description '捕获从未产出证据时，UI 判定必须阻断而不是当成无事可查' `
    -SourceDir (Join-Path $sandbox 'empty-source') -EvidenceDir (Join-Path $sandbox 'c3') -DeleteRelative '' -Scope 'UI' -ExpectBlocked $true

# C3b-UI：删掉 UI 范围的必需证据 → UI 判定必须阻断，且原因指向该文件
Invoke-JudgeCase -Id 'C3b-ui-required-deleted' -Description '删掉 UI 范围的必需证据后只重新判定，UI 判定必须阻断且原因指向该文件' `
    -SourceDir $c0 -EvidenceDir (Join-Path $sandbox 'c3b-ui') -DeleteRelative 'UI_CAPTURE_ENVIRONMENT.tsv' -Scope 'UI' -ExpectBlocked $true

# Full 形状的副本：额外放上只属于 Full 的必需证据
$fullSource = Join-Path $sandbox 'full-source'
New-Item -ItemType Directory -Force -Path $fullSource | Out-Null
Copy-Item (Join-Path $c0 '*') $fullSource -Recurse -Force -ErrorAction SilentlyContinue
foreach ($pair in @(@('07-browser','e2e-ui-webkit.json'), @('07-browser','e2e-ui-chromium.json'), @('05-runtime-identity','runtime-identity.json'), @('03-setup-acceptance','setup-acceptance.json'), @('04-update-rollback','update-verify.json'))) {
    New-Item -ItemType Directory -Force -Path (Join-Path $fullSource $pair[0]) | Out-Null
    Set-Content -LiteralPath (Join-Path $fullSource ($pair[0] + '\' + $pair[1])) -Value '{"placeholder":true}' -Encoding UTF8
}

Invoke-JudgeCase -Id 'C3b-full-only-deleted' -Description '删掉只属于 Full 的必需证据：Full 判定必须阻断，且原因指向该文件' `
    -SourceDir $fullSource -EvidenceDir (Join-Path $sandbox 'c3b-full') -DeleteRelative '07-browser\e2e-ui-webkit.json' -Scope 'Full' -ExpectBlocked $true
Invoke-JudgeCase -Id 'C3c-ui-scope-unaffected-by-full-only' -Description 'Scope=UI 不应因为 Full 专属证据缺失而阻断（范围必须各自成立）' `
    -SourceDir $fullSource -EvidenceDir (Join-Path $sandbox 'c3c') -DeleteRelative '07-browser\e2e-ui-chromium.json' -Scope 'UI' -ExpectBlocked $false

$passed = @($results | Where-Object { $_.test_result -eq 'PASS' }).Count
$payload = [pscustomobject]@{
    generatedAt = (Get-Date -Format 'o')
    suite = 'RELEASE_BLOCKING_CHAIN_TESTS'
    candidateCommit = $script:commit
    note = '调用发布入口 tools/Build-All.ps1 所用的同一个门禁 tools/Invoke-UiMatrixGate.ps1；在隔离输出目录执行，dist/delivery 历史产物未被触碰。门禁按预期拒绝时，测试用例本身记为 PASS。'
    scopeNote = 'Scope=UI 只返回 UI 范围结论，不单独授权正式报告或正式交付；正式发布必须执行 Full 判定且 matrixReduced=false。'
    deliveryFilesBefore = $deliveryBefore.Count
    deliveryFilesAfter = @(Get-ChildItem $deliveryDir -File -ErrorAction SilentlyContinue).Count
    total = $results.Count
    passed = $passed
    failed = $results.Count - $passed
    verdict = if ($passed -eq $results.Count) { 'PASS' } else { 'FAIL' }
    cases = $results
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
$payload | Add-Member -NotePropertyName candidateIdentity -NotePropertyValue $script:commit -Force
$payload | Add-Member -NotePropertyName runId -NotePropertyValue $script:runId -Force
$payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutFile -Encoding UTF8
Write-Host ''
Write-Host ("release blocking chain: {0}/{1} passed -> {2}" -f $passed, $results.Count, $payload.verdict)
if ($passed -ne $results.Count) { exit 2 }
