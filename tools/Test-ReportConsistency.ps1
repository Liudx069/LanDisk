param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$OutFile = ''
)

# 报告一致性检查的**正反例**测试。
#
# 被检查的行为（必须能区分，而不是一个恒为 True 的指示器）：
#   正确文件 → PASS
#   包内换成不同内容 → REJECT_DIFFERENT
#   包内缺该成员 → REJECT_MISSING_IN_PACKAGE
#   选定的原件不存在 → REJECT_MISSING_SELECTED
#
# 用极小的合成暂存集，不触碰任何真实交付物。

$ErrorActionPreference = 'Stop'
if (-not $OutFile) { $OutFile = Join-Path $Repo 'dist\delivery-175-final\REPORT_CONSISTENCY_TESTS.json' }

$bundleTool = Join-Path $Repo 'dist\tools\New-FinalReviewBundle.ps1'
$sandbox = Join-Path $Repo 'dist\.report-consistency-sandbox'
if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$cases = New-Object System.Collections.Generic.List[object]

# 权威报告内容（与真实报告无关的合成内容，仅用于比较）
$reportText = "# 合成权威报告`r`n- 产品版本：1.7.6`r`n- 产品构建提交：deadbeef`r`n本文件用于验证报告一致性检查本身。`r`n"

function Invoke-Case([string]$id, [string]$description, [scriptblock]$Prepare, [string]$Expected) {
    $dir = Join-Path $sandbox $id
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'stage\01-report') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'selected') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'out') | Out-Null

    # 选定原件
    $selected = Join-Path $dir 'selected\DELIVERY_REPORT_11_VERIFIED.md'
    Set-Content -LiteralPath $selected -Value $reportText -Encoding UTF8 -NoNewline
    # 包内成员（默认与原件相同）
    $staged = Join-Path $dir 'stage\01-report\DELIVERY_REPORT_11_VERIFIED.md'
    Set-Content -LiteralPath $staged -Value $reportText -Encoding UTF8 -NoNewline

    & $Prepare $dir

    $out = & pwsh -NoProfile -File $bundleTool -Repo $Repo -StageDir (Join-Path $dir 'stage') -OutDir (Join-Path $dir 'out') `
        -BundleName 'probe.zip' -SelectedReport $selected 2>&1 | Out-String

    $actual = 'UNKNOWN'
    if ($out -match '报告一致性检查:\s*(\S+)') { $actual = $Matches[1] }

    $ok = ($actual -eq $Expected)
    $cases.Add([pscustomobject]@{
        id = $id; description = $description; expectation = $Expected; actual = $actual; passed = $ok
        toolOutput = ($out -split "`n" | Where-Object { $_ -match '报告一致性检查|重复路径' } | ForEach-Object { $_.Trim() }) -join ' | '
    })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}  期望 {2}，实际 {3}" -f $mark, $id, $Expected, $actual)
}

Write-Host 'report consistency check:'

Invoke-Case 'R1-correct-file-passes' '包内报告与选定原件一致 → 通过' { param($d) } 'PASS'

Invoke-Case 'R2-different-content-rejected' '包内换成不同内容 → 拒绝' {
    param($d)
    Set-Content -LiteralPath (Join-Path $d 'stage\01-report\DELIVERY_REPORT_11_VERIFIED.md') -Value "# 另一份报告`r`n结论不同。`r`n" -Encoding UTF8 -NoNewline
} 'REJECT_DIFFERENT'

Invoke-Case 'R3-missing-in-package-rejected' '包内缺少该成员 → 拒绝' {
    param($d)
    Remove-Item -LiteralPath (Join-Path $d 'stage\01-report\DELIVERY_REPORT_11_VERIFIED.md') -Force
} 'REJECT_MISSING_IN_PACKAGE'

Invoke-Case 'R4-missing-selected-rejected' '选定的原件不存在 → 拒绝' {
    param($d)
    Remove-Item -LiteralPath (Join-Path $d 'selected\DELIVERY_REPORT_11_VERIFIED.md') -Force
} 'REJECT_MISSING_SELECTED'

$passed = @($cases | Where-Object { $_.passed }).Count
$payload = [pscustomobject]@{
    generatedAt = (Get-Date -Format 'o')
    tool = 'dist/tools/New-FinalReviewBundle.ps1'
    note = '从封好的 ZIP 重新打开并解出该成员，与选定原件的**完整字节**比较；不比较 Manifest 的声明值。'
    total = $cases.Count
    passed = $passed
    failed = $cases.Count - $passed
    verdict = if ($passed -eq $cases.Count -and $cases.Count -gt 0) { 'PASS' } else { 'FAIL' }
    cases = $cases
}
$payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutFile -Encoding UTF8
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ("report consistency negative/positive tests: {0}/{1} passed -> {2}" -f $passed, $cases.Count, $payload.verdict)
if ($passed -ne $cases.Count) { exit 2 }
