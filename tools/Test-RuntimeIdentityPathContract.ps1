param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$OutFile = ''
)

# runtime identity 的**路径契约**回归：producer 的输出去处必须等于 consumer 的必需位置。
#
# 真实缺陷：New-RuntimeIdentity.ps1 写到 04-runtime-identity\，而 EvidenceVerdict.ps1 从
# 05-runtime-identity\runtime-identity.json 读取 —— 于是每次正式判定都必然报
# "必需证据缺失：runtime-identity.json"，与产品无关却阻断发布。
#
# 本测试同时给出**正反例**：把 producer 的路径改回 04 必须让检查变红，
# 否则这个检查就只是一个恒为真的指示器。

$ErrorActionPreference = 'Stop'
$producer = Join-Path $Repo 'tools\New-RuntimeIdentity.ps1'
$consumer = Join-Path $Repo 'tools\EvidenceVerdict.ps1'
foreach ($p in @($producer, $consumer)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "缺少文件：$p" }
}

# producer 输出目录（形如 NN-runtime-identity）
function Get-ProducedDir([string]$path) {
    $m = Select-String -LiteralPath $path -Pattern 'outDir\s*=\s*Join-Path\s+\$repo\s+"dist\\evidence-11\\\$runId\\([^"]+)"'
    if (-not $m) { return $null }
    return $m.Matches[0].Groups[1].Value
}

# consumer 必需位置（形如 NN-runtime-identity\runtime-identity.json）
function Get-RequiredDir([string]$path) {
    $m = Select-String -LiteralPath $path -Pattern "'runtime-identity\.json'\s*=\s*'([^'\\]+)\\runtime-identity\.json'"
    if (-not $m) { return $null }
    return $m.Matches[0].Groups[1].Value
}

# evidence run 里 runtime-identity 的写入位置（不变量：只允许 canonical 的 05）
#
# 收窄到 `evidence-11\$runId\<NN>-runtime-identity` 这一**证据运行路径形状**：
#   * 排除 08-runtime-identity —— 那是 bundle/audit 的另一套编号，与证据 run 无关；
#   * 排除本文件与源文件里作为**反例字符串/注释**出现的 04。
# 先前用带引号锚定的正则一处都匹配不到，于是"0 处引用"被当成"没有违规"，是个恒真的空检查。
function Get-AllProducerDirs([string]$toolsDir) {
    $dirs = @()
    foreach ($s in Get-ChildItem -LiteralPath $toolsDir -File -Filter '*.ps1') {
        foreach ($hit in (Select-String -LiteralPath $s.FullName -Pattern 'evidence-11\\\$runId\\([0-9]{2}-runtime-identity)' -AllMatches)) {
            foreach ($g in $hit.Matches) { $dirs += [pscustomobject]@{ Script = $s.Name; Dir = $g.Groups[1].Value } }
        }
    }
    return $dirs
}

$produced = Get-ProducedDir $producer
$required = Get-RequiredDir $consumer
$results = New-Object System.Collections.Generic.List[object]

function Add-Case([string]$id, [string]$desc, [bool]$pass, [string]$detail) {
    $script:results.Add([pscustomobject]@{ id = $id; description = $desc; passed = $pass; detail = $detail })
}

Add-Case 'RTIC-01' 'producer 输出目录可解析' ($null -ne $produced) ("produced=" + $produced)
Add-Case 'RTIC-02' 'consumer 必需目录可解析' ($null -ne $required) ("required=" + $required)
Add-Case 'RTIC-03' 'producer 输出 == consumer 必需' ($produced -eq $required) ("produced=$produced required=$required")

$all = Get-AllProducerDirs (Join-Path $Repo 'tools')
$offenders = @($all | Where-Object { $_.Dir -ne $required })
Add-Case 'RTIC-04' '工具中不存在非 canonical 的 runtime-identity 目录名' ($offenders.Count -eq 0) `
    ($(if ($offenders.Count -eq 0) { "全部指向 $required（$($all.Count) 处引用）" } else { ($offenders | ForEach-Object { "$($_.Script):$($_.Dir)" }) -join ', ' }))

# 反例：把 producer 的路径改成 04 必须让 RTIC-03 失败（否则检查是恒真的）
$sandbox = Join-Path $Repo 'dist\.rti-contract-sandbox'
if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$mutant = Join-Path $sandbox 'New-RuntimeIdentity.ps1'
(Get-Content -LiteralPath $producer -Raw -Encoding UTF8).Replace('05-runtime-identity', '04-runtime-identity') |
    Set-Content -LiteralPath $mutant -Encoding UTF8
$mutantProduced = Get-ProducedDir $mutant
$controlDetects = ($mutantProduced -ne $required)
Add-Case 'RTIC-05' '反例：producer 改回 04 时检查确实变红' $controlDetects ("变体 produced=$mutantProduced，required=$required，判定不同=$controlDetects")
Remove-Item $sandbox -Recurse -Force

$passed = @($results | Where-Object { $_.passed }).Count
Write-Host 'runtime-identity 路径契约：'
foreach ($r in $results) {
    $mark = if ($r.passed) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1} {2} - {3}" -f $mark, $r.id, $r.description, $r.detail)
}
$verdict = if ($passed -eq $results.Count) { 'PASS' } else { 'FAIL' }
Write-Host ''
Write-Host ("path contract: {0}/{1} -> {2}" -f $passed, $results.Count, $verdict)

if ($OutFile) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
    [pscustomobject]@{
        generatedAt = (Get-Date).ToString('o')
        producedDir = $produced
        requiredDir = $required
        total = $results.Count
        passedCount = $passed
        verdict = $verdict
        cases = $results
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutFile -Encoding UTF8
}
if ($verdict -ne 'PASS') { exit 2 }
