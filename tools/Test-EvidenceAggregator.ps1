param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$RunId = '',
    [string]$OutFile = ''
)

# Negative tests for the readiness verdict.
#
# A gate that has never been seen to fail is not evidence. Each case injects one realistic defect into a copy
# of the real evidence and asserts that the verdict refuses READY; one control case asserts the untouched copy
# is READY, so the suite cannot pass by refusing everything.

$ErrorActionPreference = 'Stop'
. (Join-Path $Repo 'tools\EvidenceVerdict.ps1')

. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
if (-not $RunId) { $RunId = Resolve-EvidenceRunId -Repo $Repo }
if (-not $OutFile) { $OutFile = Join-Path $Repo 'dist\delivery\EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json' }

$source = Join-Path $Repo "dist\evidence-11\$RunId"
$biProbe = Get-ChildItem -LiteralPath $source -Recurse -Filter 'build-info.json' -ErrorAction SilentlyContinue | Select-Object -First 1
$script:expectedCandidate = if ($biProbe) { (Get-Content -LiteralPath $biProbe.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).commit } else { '' }
$script:runId = if ($RunId) { $RunId } else { "" }
if (-not $script:expectedCandidate) { throw "无法确定期望候选身份：$source 下没有 build-info.json" }
Write-Host ("expected candidate: " + $script:expectedCandidate)

$sandbox = Join-Path $Repo 'dist\.negative-tests-sandbox'
$results = New-Object System.Collections.Generic.List[object]

function New-Sandbox([string]$name) {
    $dir = Join-Path $sandbox $name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    # The matrix to copy is the ISOLATED one. The frozen 1.7.3 capture also exists in this run and still holds
    # the five unreproducible failures; copying that would make the control case fail for a reason that has
    # nothing to do with the injection under test.
    $matrix = if (Test-Path (Join-Path $source '06-desktop-ui-STRICT-FIXTURE')) { '06-desktop-ui-STRICT-FIXTURE' } else { '06-desktop-ui' }
    $script:matrixInSandbox = $matrix

    New-Item -ItemType Directory -Force -Path (Join-Path $dir $matrix) | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dir '05-runtime-identity') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dir '03-setup-acceptance\x') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dir '04-update-rollback\x') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dir '07-browser') | Out-Null
    foreach ($f in @('UI_CAPTURE.tsv','UI_CAPTURE_ENVIRONMENT.tsv','UI_PAGE_CATALOG.tsv','UI_CAPTURE_SUMMARY.tsv')) {
        $src = Join-Path (Join-Path $source $matrix) $f
        if (Test-Path $src) { Copy-Item $src (Join-Path (Join-Path $dir $matrix) $f) -Force }
    }
    foreach ($f in @('05-runtime-identity\runtime-identity.json','07-browser\e2e-ui-chromium.json','07-browser\e2e-ui-webkit.json')) {
        $src = Join-Path $source $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $dir $f) -Force }
    }
    foreach ($probe in @(@('03-setup-acceptance', 'setup-acceptance.json'), @('04-update-rollback', 'update-verify.json'))) {
        $found = Get-ChildItem (Join-Path $source $probe[0]) -Recurse -File -Filter $probe[1] -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { Copy-Item $found.FullName (Join-Path $dir ($probe[0] + '\x\' + $probe[1])) -Force }
    }
    $bi = Get-ChildItem -LiteralPath $source -Recurse -Filter 'build-info.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($bi) {
        New-Item -ItemType Directory -Force -Path (Join-Path $dir '07-browser\server') | Out-Null
        Copy-Item $bi.FullName (Join-Path $dir '07-browser\server\build-info.json') -Force
    }

    # 对照组必须是**完整**的证据集，否则 N0 会因为"沙箱里本来就没有那些文件"而变红，
    # 看上去像判定器过严，实际是夹具不全（本轮给 Full 增加了安装器/B 类/HTTPS/隐私四项必需证据后
    # 就出现了这种假阴性）。
    #
    # 只复制判定**真正读取**的那些文件：archived run 里带着上百 MB 的更新包与截图，
    # 整体复制既慢又没必要。需要判定的路径与 Invoke-ReleaseGate 的必需清单一致。
    $wanted = @(
        '02-installer-capture\setup-ui-index.txt',
        '02-installer-capture\recording\installer-animation.gif',
        '08-https\https-e2e-matrix.json',
        '08-https\B-dynamic-port\B-dynamic-port-update.json',
        'PRIVACY_CHECK.json'
    )
    foreach ($rel in $wanted) {
        $from = Join-Path $source $rel
        if (Test-Path -LiteralPath $from) {
            $to = Join-Path $dir $rel
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
            Copy-Item -LiteralPath $from -Destination $to -Force
        }
    }
    return $dir
}

function Invoke-VerdictOn([string]$Root) {
    Get-EvidenceVerdict -EvidenceRoot $Root -ExpectedCandidate $script:expectedCandidate
}

function Invoke-Case([string]$id, [string]$description, [scriptblock]$inject, [bool]$expectReady) {
    $dir = New-Sandbox $id
    & $inject $dir
    $verdict = Invoke-VerdictOn -Root $dir
    $ok = if ($expectReady) { $verdict.Ready } else { -not $verdict.Ready }
    $actual = if ($verdict.Ready) { 'READY' } else { 'NOT_READY' }
    $results.Add([pscustomobject]@{
        id = $id
        description = $description
        expectation = if ($expectReady) { 'READY' } else { 'NOT_READY' }
        actual = $actual
        passed = $ok
        reasons = @($verdict.Reasons)
    })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1} -> {2} ({3})" -f $mark, $id, $actual, $description)
    if (-not $ok) { @($verdict.Reasons) | ForEach-Object { Write-Host ('        - ' + $_) -ForegroundColor Yellow } }
}

if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

Write-Host 'negative tests:'

# Control: the untouched copy must be READY, otherwise the suite would pass by refusing everything.
$control = New-Sandbox 'N0-control'
$v0 = Invoke-VerdictOn $control
$results.Add([pscustomobject]@{ id='N0'; description='对照组：未修改的证据副本必须 READY'; expectation='READY'; actual=$(if($v0.Ready){'READY'}else{'NOT_READY'}); passed=$v0.Ready; reasons=@($v0.Reasons) })
Write-Host ("  [{0}] N0 -> {1} (对照组)" -f $(if ($v0.Ready) { 'PASS' } else { 'FAIL' }), $(if ($v0.Ready) { 'READY' } else { 'NOT_READY' }))
if (-not $v0.Ready) { @($v0.Reasons) | ForEach-Object { Write-Host ('        - ' + $_) -ForegroundColor Yellow } }

Invoke-Case 'N1' '注入一个必需布局 FAIL' {
    param($dir)
    $p = Join-Path (Join-Path $dir $script:matrixInSandbox) 'UI_CAPTURE.tsv'
    $lines = [System.IO.File]::ReadAllLines($p)
    for ($i = 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "`tPASS`t") { $lines[$i] = $lines[$i] -replace "`tPASS`t", "`tFAIL`t"; break }
    }
    [System.IO.File]::WriteAllLines($p, $lines)
} $false

Invoke-Case 'N2' '删掉一个必需组合（总数随之减少）' {
    param($dir)
    $p = Join-Path (Join-Path $dir $script:matrixInSandbox) 'UI_CAPTURE.tsv'
    $lines = [System.IO.File]::ReadAllLines($p)
    $kept = @($lines[0]) + @($lines[1..($lines.Count - 2)])
    [System.IO.File]::WriteAllLines($p, $kept)
} $false

Invoke-Case 'N3' '换入旧候选的 runtime 结果' {
    param($dir)
    $p = Join-Path $dir '05-runtime-identity\runtime-identity.json'
    $doc = Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json
    $doc.reportedCommit = '9441f97243340bd29f2fb351501555285461b406'
    $doc | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p -Encoding UTF8
} $false

Invoke-Case 'N4' '制造不可计算的空字段（缺项集合无法建立）' {
    param($dir)
    $p = Join-Path (Join-Path $dir $script:matrixInSandbox) 'UI_CAPTURE_ENVIRONMENT.tsv'
    $lines = [System.IO.File]::ReadAllLines($p)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -like 'UI_LAYOUT_TEXTBLOCKS_INSPECTED*') { $lines[$i] = 'UI_LAYOUT_TEXTBLOCKS_INSPECTED' }
    }
    [System.IO.File]::WriteAllLines($p, $lines)
} $false

Invoke-Case 'N5' '用重复项冒充完整覆盖' {
    param($dir)
    $p = Join-Path (Join-Path $dir $script:matrixInSandbox) 'UI_CAPTURE.tsv'
    $lines = [System.IO.File]::ReadAllLines($p)
    $dup = $lines[1]
    $lines = @($lines[0]) + @($dup) + @($lines[1..($lines.Count - 1)])
    [System.IO.File]::WriteAllLines($p, $lines)
} $false

Invoke-Case 'N6' '删掉必要证据文件' {
    param($dir)
    Remove-Item (Join-Path $dir '07-browser\e2e-ui-webkit.json') -Force
} $false

Invoke-Case 'N7' '抓屏期间网络页没有渲染夹具里的物理网卡' {
    param($dir)
    $p = Join-Path (Join-Path $dir $script:matrixInSandbox) 'UI_CAPTURE_ENVIRONMENT.tsv'
    $lines = [System.IO.File]::ReadAllLines($p)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # WiFi 已移除：判据改为"网络页夹具渲染"这一组（EvidenceVerdict 现在读的就是它们）。
        if ($lines[$i] -like 'R2_VISIBLE_ADAPTER*') { $lines[$i] = "R2_VISIBLE_ADAPTER`tfalse" }
    }
    [System.IO.File]::WriteAllLines($p, $lines)
} $false

Invoke-Case 'N8' '夹具没有真正进入页面' {
    param($dir)
    $p = Join-Path (Join-Path $dir $script:matrixInSandbox) 'UI_CAPTURE_ENVIRONMENT.tsv'
    $lines = [System.IO.File]::ReadAllLines($p)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -like 'R2_NATIVE_UI_RENDER*') { $lines[$i] = "R2_NATIVE_UI_RENDER`tFAIL" }
    }
    [System.IO.File]::WriteAllLines($p, $lines)
} $false

$passed = @($results | Where-Object { $_.passed }).Count
$payload = [pscustomobject]@{
    generatedAt = (Get-Date -Format 'o')
    runId = $RunId
    suite = 'EVIDENCE_AGGREGATOR_NEGATIVE_TESTS'
    control = 'N0'
    total = $results.Count
    passed = $passed
    failed = $results.Count - $passed
    verdict = if ($passed -eq $results.Count) { 'PASS' } else { 'FAIL' }
    note = '每个用例向真实证据的副本注入一处缺陷，并要求判定器拒绝 READY；N0 为对照组，要求未修改副本仍为 READY，防止"一律拒绝"冒充通过。'
    cases = $results
}
# 结果自带候选身份与 run_id：先前同名文件有两份不同哈希却都自称"当前负向结果"，
# 无法分辨各自属于哪个候选。写进身份后，每份都能自证归属。
$payload | Add-Member -NotePropertyName candidateIdentity -NotePropertyValue $script:expectedCandidate -Force
$payload | Add-Member -NotePropertyName runId -NotePropertyValue $script:runId -Force
$payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutFile -Encoding UTF8
Write-Host ''
Write-Host ("negative tests: {0}/{1} passed -> {2}" -f $passed, $results.Count, $payload.verdict)
if ($passed -ne $results.Count) { exit 2 }
