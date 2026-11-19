#requires -Version 7.0
<#
.SYNOPSIS
    汇总 dist/delivery/ 的全部交付文件并生成 SHA256 清单（清单本身不自引用）。

.DESCRIPTION
    把中文说明、构建说明、测试方案、已知限制、第三方许可、最终报告与测试证据一并放入交付目录，
    然后对交付目录中除清单自身以外的每个文件计算 SHA256。
#>
[CmdletBinding()]
param(
    [string]$DeliveryDir = '',
    [string]$PublishDir = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DeliveryDir) { $DeliveryDir = Join-Path $repoRoot 'dist/delivery' }
if (-not $PublishDir) { $PublishDir = Join-Path $repoRoot 'dist/publish/LanDisk-win-x64' }

New-Item -ItemType Directory -Force -Path $DeliveryDir | Out-Null

# --- 说明文档 ---------------------------------------------------------------
$docsOut = Join-Path $DeliveryDir 'docs'
New-Item -ItemType Directory -Force -Path $docsOut | Out-Null
Copy-Item (Join-Path $repoRoot 'README.md') $DeliveryDir -Force
foreach ($doc in Get-ChildItem (Join-Path $repoRoot 'docs') -Filter '*.md') {
    Copy-Item $doc.FullName $docsOut -Force
}

# --- 最终报告（同时放在交付根目录，便于直接打开） ---------------------------
# 交付报告：由构建在 FINAL_HEAD 锁定后生成到交付目录。若已存在（生成的版本），**不要**用仓库里的
# 旧报告覆盖它——否则报告里的候选身份又会变成构建之前的旧值。
$generatedReport = Join-Path $DeliveryDir 'DELIVERY_REPORT.md'
$report = Join-Path $repoRoot 'docs/DELIVERY_REPORT.md'
if (-not (Test-Path $generatedReport) -and (Test-Path $report)) { Copy-Item $report $DeliveryDir -Force }

# 候选身份与审计更正：审核者需要的两份独立材料。
foreach ($extra in @(
        'docs/AUDIT_CORRECTIONS_04.md', 'docs/AUDIT_CORRECTIONS_05.md', 'docs/AUDIT_CORRECTIONS_06.md',
        'docs/AUDIT_CORRECTIONS_07.md', 'docs/AUDIT_CORRECTIONS_08.md', 'docs/AUDIT_CORRECTIONS_09.md',
        'docs/AUDIT_CORRECTIONS_09_REPAIR_01.md')) {
    $source = Join-Path $repoRoot $extra
    if (Test-Path $source) { Copy-Item $source $DeliveryDir -Force }
}

# 第三方许可与版本声明（任务 08）：WPF-UI / QRCoder 等全部为 MIT，版本固定，审核者可逐条复核。
$notices = Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md'
if (Test-Path $notices) { Copy-Item $notices $DeliveryDir -Force }
$identityJson = Join-Path $DeliveryDir 'RELEASE_CANDIDATE_IDENTITY.json'
if (-not (Test-Path $identityJson)) {
    Write-Host '提示：尚未运行 tools/Verify-CandidateIdentity.ps1，交付目录缺少候选身份证据。' -ForegroundColor Yellow
}

# --- 测试证据 ---------------------------------------------------------------
$evidence = Join-Path $repoRoot 'dist/test-evidence.tsv'
if (Test-Path $evidence) { Copy-Item $evidence $DeliveryDir -Force }

$e2e = Join-Path $repoRoot 'dist/e2e-playback-result.json'
if (Test-Path $e2e) { Copy-Item $e2e $DeliveryDir -Force }

foreach ($engine in @('chromium', 'webkit')) {
    $uiResult = Join-Path $repoRoot "dist/e2e-ui-$engine.json"
    if (Test-Path $uiResult) { Copy-Item $uiResult $DeliveryDir -Force }
}

$smoke = Get-ChildItem (Join-Path $repoRoot '.releasecheck') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if ($smoke) {
    $smokeOut = Join-Path $DeliveryDir 'evidence'
    New-Item -ItemType Directory -Force -Path $smokeOut | Out-Null
    foreach ($name in @('release-smoke.json', 'server.log')) {
        $file = Join-Path $smoke.FullName $name
        if (Test-Path $file) { Copy-Item $file $smokeOut -Force }
    }
}

# --- 发布包实机升级/回滚验收证据（任务 03） ---------------------------------
$rc = Get-ChildItem (Join-Path $repoRoot '.rcverify') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if ($rc) {
    $rcOut = Join-Path $DeliveryDir 'evidence'
    New-Item -ItemType Directory -Force -Path $rcOut | Out-Null
    $rcReport = Join-Path $rc.FullName 'update-verify.json'
    if (Test-Path $rcReport) { Copy-Item $rcReport $rcOut -Force }

    # 更新程序自己的日志；一次性令牌在这里不会出现，但仍然按同一套规则脱敏。
    $updaterLog = Join-Path $rc.FullName 'data/updates/updater.log'
    if (Test-Path $updaterLog) {
        $text = Get-Content -LiteralPath $updaterLog -Raw -Encoding UTF8
        $text = $text -replace '(admin#token=)\S+', '$1<ADMIN-TOKEN-REDACTED>'
        Set-Content -LiteralPath (Join-Path $rcOut 'updater.log') -Value $text -Encoding UTF8
    }

    # 升级/回滚后的状态文件：说明最后一次更新是提交还是回滚。
    $updateState = Join-Path $rc.FullName 'data/updates/state.json'
    if (Test-Path $updateState) { Copy-Item $updateState (Join-Path $rcOut 'update-state.json') -Force }

    # 本次候选真实运行产生的证书体系：审核者可以自己复核根 CA 指纹与 leaf SAN，
    # 而不必相信报告里的叙述。公开证书不含私钥。
    foreach ($cert in @('landisk-ca.cer', 'landisk-leaf.cer')) {
        $certPath = Join-Path $rc.FullName "data/tls/$cert"
        if (Test-Path $certPath) { Copy-Item $certPath (Join-Path $rcOut $cert) -Force }
    }
    $installJson = Join-Path $rc.FullName 'data/install.json'
    if (Test-Path $installJson) { Copy-Item $installJson (Join-Path $rcOut 'install.json') -Force }
}

# --- 安装器实机验收证据（任务 07） ------------------------------------------
$setupRun = Get-ChildItem (Join-Path $repoRoot '.setupcheck') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if ($setupRun) {
    $setupOut = Join-Path $DeliveryDir 'evidence'
    New-Item -ItemType Directory -Force -Path $setupOut | Out-Null
    $setupReport = Join-Path $setupRun.FullName 'setup-acceptance.json'
    if (Test-Path $setupReport) { Copy-Item $setupReport $setupOut -Force }
}

# --- 原生界面渲染证据（任务 08） --------------------------------------------
# 11 个页面 × 100/125/150/175/200% 逐条渲染与布局断言的结果：PNG 直接来自被测候选的
# WPF 窗口（--render-ui），UI_CAPTURE.tsv 是同一批运行的逐条结论。审核者可以对着截图复核
# 字号、换行与列宽，而不必相信报告里的描述。
$uiEvidence = Join-Path $repoRoot 'dist/ui-evidence'
if (Test-Path $uiEvidence) {
    $uiOut = Join-Path $DeliveryDir 'ui-evidence'
    if (Test-Path $uiOut) { Remove-Item $uiOut -Recurse -Force }
    Copy-Item $uiEvidence $uiOut -Recurse -Force
    Write-Host ("界面证据: {0} 个文件 -> ui-evidence/" -f @(Get-ChildItem $uiOut -File).Count)
}
else {
    Write-Host '提示：dist/ui-evidence 不存在，交付目录缺少原生界面渲染证据。' -ForegroundColor Yellow
}

# --- 运行期身份证据（任务 08） ----------------------------------------------
# 隔离数据目录里真实跑一次发布出来的 LanDisk.exe 之后收集：根 CA 与 leaf 的公开证书、
# install.json，以及 leaf 的 SAN 清单。
#
# 两处修正（都由实际交付检查发现）：
#   1. 来源优先取**属于当前候选**的证据目录（dist/evidence-11/<run>/05-runtime-identity）。
#      原先固定用 dist/runtime-identity，那是一个 **1.7.0 时代的旧文件**
#      （productVersion 1.7.0 / buildCommit 3bbfe1b6），会被当成当前候选的身份证据交付出去。
#   2. 镜像时做网络标识脱敏：旧文件里带着本机私网地址与网卡型号，属于不可对外分发的真实标识。
. (Join-Path $PSScriptRoot 'Mask-NetworkIdentity.ps1')

$runtimeIdentity = ''
# 解析**当前候选**的 run id。先前这里硬编码 dist/.evidence-run-id-174.txt（1.7.4 的指针），
# 于是换候选后会把**旧候选**的 runtime identity 镜像进交付目录 —— 跨候选取证。
. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
$rid = Resolve-EvidenceRunId -Repo $repoRoot -ExpectedCommit (& git -C $repoRoot rev-parse HEAD).Trim()
$candidate = Join-Path $repoRoot ("dist/evidence-11/$rid/05-runtime-identity")
if (Test-Path -LiteralPath $candidate) { $runtimeIdentity = $candidate }
if (-not $runtimeIdentity) {
    # **fail closed**：先前这里只是告警，然后回退去镜像 dist/runtime-identity —— 那可能属于别的候选
    # （实测出现过 1.7.0 / 3bbfe1b6 与 commit 1bb94fed 的旧身份）。把别的候选的运行期身份
    # 交付出去，正是本轮"假线索"的同一类错误，所以这里必须停下来，而不是带着警告继续。
    $legacy = Join-Path $repoRoot 'dist/runtime-identity'
    $hint = if (Test-Path -LiteralPath $legacy) { "（另存在 dist/runtime-identity，可能属于旧候选，已拒绝使用）" } else { '' }
    throw ("找不到属于当前候选的运行期身份目录：dist/evidence-11/<runId>/05-runtime-identity $hint。" +
           "请先按正确顺序生成证据（07-browser 解压冻结包 → 05-runtime-identity），再执行交付收尾。")
}

if ($runtimeIdentity) {
    $runtimeOut = Join-Path $DeliveryDir 'runtime-identity'
    if (Test-Path $runtimeOut) { Remove-Item $runtimeOut -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $runtimeOut | Out-Null
    $maskedCount = 0
    foreach ($f in (Get-ChildItem -LiteralPath $runtimeIdentity -File -Recurse)) {
        $rel = $f.FullName.Substring($runtimeIdentity.Length).TrimStart('\', '/')
        $dest = Join-Path $runtimeOut $rel
        if ($f.Extension -in @('.json', '.txt', '.tsv', '.md')) {
            if (Mask-NetworkFile -SourcePath $f.FullName -DestinationPath $dest) { $maskedCount++ }
        }
        else {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        }
    }
    Write-Host ("运行期身份: {0} 个文件 -> runtime-identity/（来源 {1}，其中 {2} 个做了网络标识脱敏）" -f `
        @(Get-ChildItem $runtimeOut -File -Recurse).Count, $runtimeIdentity, $maskedCount)
}
else {
    Write-Host '提示：找不到运行期身份证据，交付目录将缺少该项。' -ForegroundColor Yellow
}

# --- 运行包内关键文件的哈希 ------------------------------------------------
$keyHashes = New-Object System.Collections.Generic.List[string]
foreach ($key in @(
    'LanDisk.exe',
    'LanDisk.Server.exe',
    'LanDisk.dll',
    'LanDisk.Server.dll',
    'LanDisk.Core.dll',
    'LanDisk.Update.dll',
    'updater/LanDisk.Updater.exe',
    'install-manifest.json',
    'build-info.json',
    'wwwroot/index.html')) {
    $full = Join-Path $PublishDir $key
    if (Test-Path $full) {
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item -LiteralPath $full).Length
        $keyHashes.Add("$hash  publish/$key  ($size bytes)")
    }
}

# --- SHA256 清单 ------------------------------------------------------------
$manifestPath = Join-Path $DeliveryDir 'SHA256SUMS.txt'
if (Test-Path $manifestPath) { Remove-Item $manifestPath -Force }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# LanDisk 交付文件 SHA256 清单')
$lines.Add("# 生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
$lines.Add('#')
$lines.Add('# 校验方法（Windows PowerShell / PowerShell 7）:')
$lines.Add('#   Get-FileHash -Algorithm SHA256 <文件>')
$lines.Add('# 或:  Get-Content SHA256SUMS.txt | ForEach-Object { if ($_ -notmatch "^#" -and $_.Trim()) { $p = ($_ -split "\s+")[2]; ... } }')
$lines.Add('#')
$lines.Add('# 注意: 本清单不包含自身，避免循环引用。')
$lines.Add('')
$lines.Add('## 交付压缩包与文档')
$lines.Add('')

$files = Get-ChildItem -LiteralPath $DeliveryDir -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object FullName

foreach ($file in $files) {
    $relative = [System.IO.Path]::GetRelativePath($DeliveryDir, $file.FullName).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines.Add(("{0}  {1}  ({2} bytes)" -f $hash, $relative, $file.Length))
}

$lines.Add('')
$lines.Add('## 运行包内关键文件（解压 LanDisk-win-x64.zip 后核对）')
$lines.Add('')
$lines.AddRange($keyHashes)

Set-Content -LiteralPath $manifestPath -Value $lines -Encoding UTF8

# 主要产物的大小与哈希单独成文件：报告在仓库里，产物哈希每次重新打包都会变，
# 把哈希写进仓库文档就会形成"提交→重建→哈希变化→再提交"的死循环。
$artifactLines = New-Object System.Collections.Generic.List[string]
$artifactLines.Add('# LanDisk 主要产物（本次打包）')
$artifactLines.Add("# 生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
$artifactLines.Add('# 注意: ZIP 内嵌时间戳，重新打包会得到不同哈希；跨构建稳定的身份见 SHA256SUMS.txt 内层哈希。')
$artifactLines.Add('')
$artifactLines.Add('sha256                                                            bytes        file')
foreach ($name in @('LanDisk-Setup.exe', 'LanDisk-win-x64.zip', 'LanDisk-source.zip')) {
    $full = Join-Path $DeliveryDir $name
    if (Test-Path $full) {
        $artifactLines.Add(("{0}  {1,-12} {2}" -f (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant(), (Get-Item $full).Length, $name))
    }
}
foreach ($lupd in (Get-ChildItem -LiteralPath $DeliveryDir -Filter 'LanDisk-update-*.lupd' -File -ErrorAction SilentlyContinue)) {
    $artifactLines.Add(("{0}  {1,-12} {2}" -f (Get-FileHash -LiteralPath $lupd.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $lupd.Length, $lupd.Name))
}
Set-Content -LiteralPath (Join-Path $DeliveryDir 'ARTIFACT_HASHES.txt') -Value $artifactLines -Encoding UTF8
Write-Host ("主要产物哈希: {0}" -f (Join-Path $DeliveryDir 'ARTIFACT_HASHES.txt'))

Write-Host "交付目录: $DeliveryDir"
Write-Host "清单:     $manifestPath"
Write-Host ("共 {0} 个文件纳入清单" -f $files.Count)
