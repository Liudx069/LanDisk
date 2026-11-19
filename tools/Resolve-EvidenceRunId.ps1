#requires -Version 7.0
<#
.SYNOPSIS
    解析**当前候选**的 evidence run id（供各发布工具共用）。

.DESCRIPTION
    真实缺陷：各发布工具各自硬编码了一个**历史**指针文件 ——
      Finalize-Delivery → .evidence-run-id-174.txt（1.7.4）
      Make-SmallBundle   → .evidence-run-id-176.txt（1.7.6）
      Assemble-Bundle02 / New-FinalReviewBundle → -175（1.7.5）
      New-EvidenceBinding / New-RuntimeIdentity / New-VerifiedReports / Test-EvidenceAggregator → -173（1.7.3）
    于是 dist/delivery 会被灌入**别的候选**的 runtime identity，正是"跨候选取证"。

    解析顺序：
      1. 显式传入的 -RunId（调用方最清楚）；
      2. dist\.evidence-run-id.txt（**无版本后缀**的规范指针，若其指向的 run 存在）；
      3. 按**候选提交**发现：在 dist\evidence-11\run-* 中找 CANDIDATE_IDENTITY.json 或
         展开包 build-info.json 与本仓库 HEAD 一致的那个（取最新）。
    找不到就抛错，绝不回退到历史候选。

.EXAMPLE
    . tools/Resolve-EvidenceRunId.ps1
    $run = Resolve-EvidenceRunId -Repo 'D:\LanDisk'
#>

function Resolve-EvidenceRunId {
    param(
        [string]$Repo = '',
        [string]$RunId = '',
        [string]$ExpectedCommit = ''
    )

    if (-not $Repo) { $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
    $evidenceBase = Join-Path $Repo 'dist\evidence-11'

    if ($RunId) { return $RunId }

    $canonical = Join-Path $Repo 'dist\.evidence-run-id.txt'
    if (Test-Path -LiteralPath $canonical) {
        $rid = (Get-Content -LiteralPath $canonical -Encoding UTF8).Trim()
        if ($rid -and (Test-Path -LiteralPath (Join-Path $evidenceBase $rid))) { return $rid }
    }

    if (-not $ExpectedCommit) {
        $ExpectedCommit = (& git -C $Repo rev-parse HEAD 2>$null)
        if ($ExpectedCommit) { $ExpectedCommit = $ExpectedCommit.Trim() }
    }

    $found = @()
    if ($ExpectedCommit -and (Test-Path -LiteralPath $evidenceBase)) {
        foreach ($dir in (Get-ChildItem -LiteralPath $evidenceBase -Directory -ErrorAction SilentlyContinue)) {
            $identity = Join-Path $dir.FullName 'CANDIDATE_IDENTITY.json'
            if (Test-Path -LiteralPath $identity) {
                try {
                    $j = Get-Content -LiteralPath $identity -Raw -Encoding UTF8 | ConvertFrom-Json
                    if ($j.productCommit -eq $ExpectedCommit) { $found += $dir; continue }
                } catch { }
            }
            foreach ($bi in (Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Filter 'build-info.json' -ErrorAction SilentlyContinue)) {
                try {
                    $b = Get-Content -LiteralPath $bi.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
                    if ($b.commit -eq $ExpectedCommit) { $found += $dir; break }
                } catch { }
            }
        }
    }

    if ($found.Count -gt 0) {
        return ($found | Sort-Object LastWriteTime -Descending | Select-Object -First 1).Name
    }

    throw ("找不到属于当前候选（$ExpectedCommit）的 evidence run：dist\evidence-11 下没有匹配的 " +
           "CANDIDATE_IDENTITY.json / build-info.json，也不存在 dist\.evidence-run-id.txt。" +
           "拒绝回退到任何历史候选。请先按顺序生成证据（07-browser 解压冻结包 → 05-runtime-identity）。")
}
