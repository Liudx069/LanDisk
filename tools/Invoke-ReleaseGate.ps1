param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$CandidateCommit = '',
    [string]$EvidenceRoot = '',
    [string]$DeliveryDir = '',
    [switch]$AllowMissingEvidenceForDiagnostics
)

# 正式发布判定：Full 作用域。
#
# 与 UI gate（tools/Invoke-UiMatrixGate.ps1）的分工：
#   UI gate   —— 只负责桌面矩阵：跑捕获、看矩阵与夹具隔离。它通过只说明 **UI 范围**成立。
#   Release gate（本脚本）—— 整份发布：UI 证据 + 浏览器两端 + runtime + setup + 更新回滚。
#                             只有它通过，才允许生成正式 READY 报告与正式封包。
#
# 两条硬约束：
#   1. 正式路径**不使用** -SkipMatrixCompleteness / -ReducedMatrix 或任何诊断豁免；
#   2. 证据必须**属于当前候选**：默认按 build-info.json 里的提交去找本次运行的证据目录，
#      找不到就阻断 —— 不允许拿别的候选、别的运行或历史结果顶替。
#
# 为了让"缺证 / 失败 / 错候选"都能被真实检验，本脚本可在隔离证据副本上单独运行（见
# dist/tools/Test-ReleaseBlockingChain.ps1 的 judge 用例）。

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'EvidenceVerdict.ps1')

if (-not $CandidateCommit) { $CandidateCommit = (& git -C $Repo rev-parse HEAD).Trim() }
if (-not $DeliveryDir) { $DeliveryDir = Join-Path $Repo 'dist\delivery' }

$gateFile = Join-Path $DeliveryDir 'RELEASE_GATE.json'
$blockedReport = Join-Path $DeliveryDir 'DELIVERY_BLOCKED.md'

function Write-Gate([string]$verdict, [string]$scope, [string[]]$reasons, [string]$evidencePath) {
    New-Item -ItemType Directory -Force -Path $DeliveryDir | Out-Null
    [pscustomobject]@{
        entryPoint      = 'tools/Invoke-ReleaseGate.ps1'
        scope           = $scope
        candidateCommit = $CandidateCommit
        evidenceRoot    = $evidencePath
        verdict         = $verdict
        ready           = ($verdict -eq 'READY')
        reducedMatrix   = $false
        skipMatrixCompleteness = $false
        reasons         = @($reasons)
        checkedAt       = (Get-Date -Format 'o')
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $gateFile -Encoding UTF8
}

# ---- 1. 证据必须属于当前候选
if (-not $EvidenceRoot) {
    $candidates = @()
    $evidenceBase = Join-Path $Repo 'dist\evidence-11'
    if (Test-Path $evidenceBase) {
        $candidates = @(Get-ChildItem -LiteralPath $evidenceBase -Recurse -Filter 'build-info.json' -ErrorAction SilentlyContinue |
            Where-Object {
                try { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).commit -eq $CandidateCommit }
                catch { $false }
            } | ForEach-Object {
                # 从 build-info.json 所在目录**向上走到 dist\evidence-11 的直接子目录**（run 根）。
                #
                # 真实缺陷：原实现固定上溯两级（Split-Path (Split-Path <file> -Parent) -Parent），
                # 只在 build-info.json 恰好位于 run\<包目录>\ 时才成立。真实 run 里它在
                # 07-browser\server\LanDisk-win-x64\ 这类更深的位置，于是 EvidenceRoot 被解析成
                # ...\07-browser\server，所有必需成员都被判为"缺失"——Full 因此永远无法通过。
                $dir = Split-Path $_.FullName -Parent
                while ($dir) {
                    $parent = Split-Path $dir -Parent
                    if (-not $parent -or $parent -eq $dir) { break }
                    if ($parent -eq $evidenceBase) { break }
                    $dir = $parent
                }
                $dir
            } | Select-Object -Unique)
    }
    if ($candidates.Count -eq 0) {
        $reason = '找不到属于当前候选（' + $CandidateCommit.Substring(0, 12) + '…）的证据目录：正式发布不得使用其它候选或历史结果。'
        Write-Gate 'NOT_READY' 'Full' @($reason) ''
        @(
            '# 正式交付：BLOCKED'
            ''
            ('- 时间：' + (Get-Date -Format 'o'))
            ('- 候选提交：' + $CandidateCommit)
            '- 结论：**NOT_READY**（未生成正式 READY 交付）'
            ''
            '## 阻断原因'
            ''
            ('- ' + $reason)
            ''
            '> 这是失败说明，不是正式可交付候选。诊断证据仍然保留在 dist/ 下。'
        ) | Set-Content -LiteralPath $blockedReport -Encoding UTF8
        Write-Host ('正式发布被阻断：' + $reason) -ForegroundColor Red
        exit 5
    }
    if ($candidates.Count -gt 1) {
        $reason = ('找到多个属于该候选的证据目录，无法确定使用哪一个：' + ($candidates -join ' ; '))
        Write-Gate 'NOT_READY' 'Full' @($reason) ''
        Write-Host ('正式发布被阻断：' + $reason) -ForegroundColor Red
        exit 5
    }
    $EvidenceRoot = $candidates[0]
}

# ---- 2. Full 判定（正式路径：不传任何缩减/豁免开关）
$verdict = Get-EvidenceVerdict -EvidenceRoot $EvidenceRoot -ExpectedCandidate $CandidateCommit -Scope Full
$ready = [bool]$verdict.Ready

if ($ready) {
    Write-Gate 'READY' 'Full' @() $EvidenceRoot
    Write-Host ('正式发布判定：READY（证据 ' + $EvidenceRoot + '）') -ForegroundColor Green
    exit 0
}

$reasons = @($verdict.Reasons)
Write-Gate 'NOT_READY' 'Full' $reasons $EvidenceRoot
@(
    '# 正式交付：BLOCKED'
    ''
    ('- 时间：' + (Get-Date -Format 'o'))
    ('- 候选提交：' + $CandidateCommit)
    ('- 证据目录：' + $EvidenceRoot)
    '- 判定范围：**Full**（未使用任何缩减矩阵或诊断豁免开关）'
    '- 结论：**NOT_READY**（未生成正式 READY 交付，未更新正式发布成功标记）'
    ''
    '## 阻断原因'
    ''
    ($reasons | ForEach-Object { '- ' + $_ })
    ''
    '## 说明'
    ''
    '- UI 范围通过不等于产品 READY：桌面矩阵只是必需证据之一。'
    '- 诊断绑定表（DRAFT）不能作为正式发布批准。'
    '- 本文件是失败说明，不是正式可交付候选；待测产物仍可用于继续验收，但不得对外宣称已通过。'
) | Set-Content -LiteralPath $blockedReport -Encoding UTF8
if (Test-Path (Join-Path $DeliveryDir 'DELIVERY_READY.txt')) { Remove-Item (Join-Path $DeliveryDir 'DELIVERY_READY.txt') -Force }
Write-Host ('正式发布被阻断（NOT_READY，' + $reasons.Count + ' 条）：') -ForegroundColor Red
$reasons | ForEach-Object { Write-Host ('  - ' + $_) -ForegroundColor Yellow }
exit 5
