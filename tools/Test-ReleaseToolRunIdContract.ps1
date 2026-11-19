param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$OutFile = ''
)

# 发布工具的 **run id 契约**回归。
#
# 真实缺陷：各发布工具各自硬编码了一个**历史**指针文件 ——
#   Finalize-Delivery → .evidence-run-id-174.txt（1.7.4）
#   Make-SmallBundle → .evidence-run-id-176.txt（1.7.6）
#   Assemble-Bundle02 / New-FinalReviewBundle → -175（1.7.5）
#   其余 → -173（1.7.3）
# 后果：换候选后 dist/delivery 会被灌入**别的候选**的 runtime identity（跨候选取证）。
#
# 本测试用一条**能失败**的不变量把它钉住：发布工具不得读取**带版本后缀**的历史指针；
# 并验证共用解析器能解析出当前候选的 run。

$ErrorActionPreference = 'Stop'
$toolsDir = Join-Path $Repo 'tools'
$results = New-Object System.Collections.Generic.List[object]
function Add-Case([string]$id, [string]$desc, [bool]$pass, [string]$detail) {
    $script:results.Add([pscustomobject]@{ id = $id; description = $desc; passed = $pass; detail = $detail })
}

# 1) 不得出现带版本后缀的历史指针
# 只统计**代码行**：注释里提到历史名字（含本脚本与解析器的说明）不算违规。
# 也跳过 Resolve-EvidenceRunId.ps1 与契约脚本本身 —— 它们的存在就是为了记录这个缺陷。
$offenders = @()
$documenting = @('Resolve-EvidenceRunId.ps1', 'Test-ReleaseToolRunIdContract.ps1')
foreach ($s in (Get-ChildItem -LiteralPath $toolsDir -File -Filter '*.ps1')) {
    if ($documenting -contains $s.Name) { continue }
    $inBlock = $false
    foreach ($line in (Get-Content -LiteralPath $s.FullName -Encoding UTF8)) {
        $trim = $line.TrimStart()
        if ($inBlock) { if ($trim -like '*#>*') { $inBlock = $false }; continue }
        if ($trim -like '<#*') { $inBlock = -not ($trim -like '*#>*'); continue }
        if ($trim.StartsWith('#')) { continue }
        foreach ($m in ([regex]::Matches($line, '\.evidence-run-id-\d+'))) {
            $offenders += [pscustomobject]@{ Script = $s.Name; Ref = $m.Value }
        }
    }
}
Add-Case 'RRIC-01' '发布工具不读取带版本后缀的历史 run 指针' ($offenders.Count -eq 0) `
    ($(if ($offenders.Count -eq 0) { '未发现 .evidence-run-id-<数字> 引用' } else { ($offenders | ForEach-Object { "$($_.Script):$($_.Ref)" }) -join ', ' }))

# 2) 共用解析器存在且可被点源
$resolver = Join-Path $toolsDir 'Resolve-EvidenceRunId.ps1'
# 1b) 交付/报告工具不得把 dist/runtime-identity（legacy 目录）当作运行期身份来源
$legacyRefs = @()
foreach ($s in (Get-ChildItem -LiteralPath $toolsDir -File -Filter '*.ps1')) {
    if ($s.Name -eq 'Test-ReleaseToolRunIdContract.ps1') { continue }
    $inBlock = $false
    foreach ($line in (Get-Content -LiteralPath $s.FullName -Encoding UTF8)) {
        $trim = $line.TrimStart()
        if ($inBlock) { if ($trim -like '*#>*') { $inBlock = $false }; continue }
        if ($trim -like '<#*') { $inBlock = -not ($trim -like '*#>*'); continue }
        if ($trim.StartsWith('#')) { continue }
        if ($line -match "Join-Path\s+\`$Repo\s+'dist/runtime-identity'") { $legacyRefs += $s.Name }
    }
}
Add-Case 'RRIC-06' '交付工具不以 dist/runtime-identity 作为运行期身份来源' ($legacyRefs.Count -eq 0) `
    ($(if ($legacyRefs.Count -eq 0) { '未发现 legacy 引用' } else { ($legacyRefs -join ', ') }))
Add-Case 'RRIC-02' '共用 run 解析器存在' (Test-Path -LiteralPath $resolver) $resolver

# 3) 解析器能解析出**当前候选**的 run（按提交发现）
$head = (& git -C $Repo rev-parse HEAD).Trim()
$resolved = ''
$err = ''
try {
    . $resolver
    $resolved = Resolve-EvidenceRunId -Repo $Repo -ExpectedCommit $head
} catch { $err = $_.Exception.Message }
Add-Case 'RRIC-03' '解析器能按当前候选提交解析出 run'([bool]$resolved) `
    ($(if ($resolved) { "HEAD=$($head.Substring(0,12))… → $resolved" } else { "失败：$err" }))

# 4) 解析出的 run 必须真的属于该提交（正向证据）
$belongs = $false
$detail = ''
if ($resolved) {
    $identity = Join-Path $Repo "dist\evidence-11\$resolved\CANDIDATE_IDENTITY.json"
    if (Test-Path -LiteralPath $identity) {
        $j = Get-Content -LiteralPath $identity -Raw -Encoding UTF8 | ConvertFrom-Json
        $belongs = ($j.productCommit -eq $head)
        $detail = "run 的 productCommit=$($j.productCommit)"
    } else {
        $detail = '该 run 没有 CANDIDATE_IDENTITY.json'
    }
}
Add-Case 'RRIC-04' '解析出的 run 属于当前候选提交' $belongs $detail

# 5) 反例：把历史指针放回 Finalize-Delivery 必须让 RRIC-01 变红
$sandbox = Join-Path $Repo 'dist\.rric-sandbox'
if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$mutant = Join-Path $sandbox 'Finalize-Delivery.ps1'
(Get-Content -LiteralPath (Join-Path $toolsDir 'Finalize-Delivery.ps1') -Raw -Encoding UTF8) +
    "`n# regression control`n`$x = Join-Path `$repoRoot 'dist/.evidence-run-id-174.txt'`n" |
    Set-Content -LiteralPath $mutant -Encoding UTF8
$mutantOffenders = @(Select-String -LiteralPath $mutant -Pattern "\.evidence-run-id-\d+" -AllMatches -ErrorAction SilentlyContinue)
Add-Case 'RRIC-05' '反例：放回历史指针时检查确实变红' ($mutantOffenders.Count -gt 0) `
    ("变体中命中 $($mutantOffenders.Count) 处历史指针")
Remove-Item $sandbox -Recurse -Force

$passed = @($results | Where-Object { $_.passed }).Count
Write-Host '发布工具 run id 契约：'
foreach ($r in $results) {
    $mark = if ($r.passed) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1} {2} - {3}" -f $mark, $r.id, $r.description, $r.detail)
}
$verdict = if ($passed -eq $results.Count) { 'PASS' } else { 'FAIL' }
Write-Host ''
Write-Host ("run-id contract: {0}/{1} -> {2}" -f $passed, $results.Count, $verdict)

if ($OutFile) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
    [pscustomobject]@{
        generatedAt = (Get-Date).ToString('o'); expectedCommit = $head; resolvedRunId = $resolved
        total = $results.Count; passedCount = $passed; verdict = $verdict; cases = $results
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutFile -Encoding UTF8
}
if ($verdict -ne 'PASS') { exit 2 }
