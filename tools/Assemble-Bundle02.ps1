param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$RunId = '',
    [string]$StageDir = '',
    [string]$OutDir = '',
    [string]$BundleName = 'LAN_DISK_11_174_FINAL_REVIEW_BUNDLE_02.zip'
)

# 组装**唯一**审核附件：一个自包含 ZIP。
#
# 原则：
#   * 不重跑任何会重建四产物的流程；不覆盖旧审核包；不改动任何原始证据；
#   * 包内**不放** Setup / release ZIP / .lupd 三个大产物（本地保留），但记录其完整身份；
#   * 源码以**脱敏审阅副本**入包，并附文件级映射；不把它的哈希冒充冻结源码 ZIP 的哈希；
#   * 隐私相关的对外副本明确标识，本地原件不动。

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
if (-not $RunId) { $RunId = Resolve-EvidenceRunId -Repo $Repo }
if (-not $StageDir) { $StageDir = Join-Path $Repo 'dist\.bundle02-stage' }
if (-not $OutDir) { $OutDir = Join-Path $Repo 'dist\delivery-174-final' }
$evidence = Join-Path $Repo "dist\evidence-11\$RunId"
$delivery = Join-Path $Repo 'dist\delivery'
$head = (& git -C $Repo rev-parse HEAD).Trim()
$productCommit = (& git -C $Repo log --format=%H -1 -- src).Trim()
$version = (Select-String -Path (Join-Path $Repo 'src\LanDisk.App\LanDisk.App.csproj') -Pattern '<Version>(.*)</Version>').Matches.Groups[1].Value

. (Join-Path $Repo 'tools\Mask-NetworkIdentity.ps1')

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $StageDir, $OutDir | Out-Null

function P([string]$rel) { return (Join-Path $StageDir $rel) }
function Ensure([string]$rel) { New-Item -ItemType Directory -Force -Path (P $rel) | Out-Null }

function Put([string]$src, [string]$rel, [switch]$Mask) {
    if (-not (Test-Path -LiteralPath $src)) { Write-Host ("  [skip] " + $rel) -ForegroundColor Yellow; return $false }
    $dst = P $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    if ($Mask -or $src -match '\.(json|tsv|md|txt|log|csv)$') {
        $null = Mask-NetworkFile -SourcePath $src -DestinationPath $dst
    }
    else { Copy-Item -LiteralPath $src -Destination $dst -Force }
    return $true
}
function PutDir([string]$src, [string]$rel) {
    if (-not (Test-Path -LiteralPath $src)) { Write-Host ("  [skip dir] " + $rel) -ForegroundColor Yellow; return }
    Copy-Item -LiteralPath $src -Destination (P $rel) -Recurse -Force
}

Write-Host '1/8 报告'
Ensure '01-report'
$null = Put (Join-Path $Repo ('dist\delivery-175-final\DELIVERY_REPORT_11_VERIFIED.md')) '01-report/DELIVERY_REPORT_11_VERIFIED.md'
$null = Put (Join-Path $evidence '01-report\EVIDENCE_BINDING_11.tsv') '01-report/EVIDENCE_BINDING_11.tsv'
if (-not (Test-Path (P '01-report/EVIDENCE_BINDING_11.tsv'))) { $null = Put (Join-Path $delivery 'EVIDENCE_BINDING_11.tsv') '01-report/EVIDENCE_BINDING_11.tsv' }
$null = Put (Join-Path $Repo 'dist\delivery-175-final\ARTIFACT_HASHES.txt') '01-report/ARTIFACT_HASHES.txt'
$null = Put (Join-Path $Repo 'dist\delivery-175-final\CODE_REVIEW_11.md') '01-report/CODE_REVIEW_11.md'
$null = Put (Join-Path $Repo 'dist\delivery-175-final\RELEASE_GATE.json') '01-report/RELEASE_GATE.json'
$null = Put (Join-Path $Repo 'dist\delivery-175-final\LAN_DISK_11_AUDIT_EXPORT_VERIFIED.md') '01-report/LAN_DISK_11_AUDIT_EXPORT_VERIFIED.md'
$null = Put (Join-Path $Repo 'docs\AUDIT_CORRECTIONS_11.md') '01-report/AUDIT_CORRECTIONS_11.md'

Write-Host '2/8 测试'
Ensure '02-tests'
$null = Put (Join-Path $Repo 'tests\LanDisk.Tests\TestResults\landisk-tests.trx') '02-tests/landisk-tests.trx'
$null = Put (Join-Path $Repo 'dist\test-evidence.tsv') '02-tests/test-evidence.tsv'
$null = Put (Join-Path $evidence '07-browser\e2e-ui-chromium.json') '02-tests/e2e-ui-chromium.json'
$null = Put (Join-Path $evidence '07-browser\e2e-ui-webkit.json') '02-tests/e2e-ui-webkit.json'
$null = Put (Join-Path $delivery 'EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json') '02-tests/EVIDENCE_AGGREGATOR_NEGATIVE_TESTS.json'
$null = Put (Join-Path $Repo 'dist\delivery\RELEASE_BLOCKING_CHAIN_TESTS.json') '02-tests/RELEASE_BLOCKING_CHAIN_TESTS.json'
$null = Put (Join-Path $Repo 'dist\evidence-11\RELEASE_GATE_NEGATIVE_TESTS.json') '02-tests/RELEASE_GATE_NEGATIVE_TESTS.json'
$null = Put (Join-Path $Repo 'dist\evidence-11\DIAGNOSTIC-binding\BINDING_GENERATOR_NEGATIVE_TESTS.json') '02-tests/BINDING_GENERATOR_NEGATIVE_TESTS.json'
$null = Put (Join-Path $evidence '01-report\EVIDENCE_BINDING_11.tsv.md') '02-tests/EVIDENCE_BINDING_11.summary.md'

Write-Host '3/8 桌面界面'
Ensure '03-desktop-ui'
foreach ($f in @('UI_FAILURES_11.tsv','UI_CAPTURE.tsv','UI_LAYOUT_VERIFICATION.tsv','UI_CAPTURE_ENVIRONMENT.tsv','UI_CAPTURE_SUMMARY.tsv','UI_PAGE_CATALOG.tsv')) {
    $srcUi = if ($f -eq 'UI_FAILURES_11.tsv') { Join-Path $Repo 'dist\delivery-175-final\UI_FAILURES_11.tsv' } else { Join-Path $evidence "06-desktop-ui\$f" }
    $null = Put $srcUi (Join-Path '03-desktop-ui' $f)
}
PutDir (Join-Path $Repo 'dist\ui-evidence') '03-desktop-ui/screenshots'

Write-Host '4/8 安装器'
Ensure '04-installer'
PutDir (Join-Path $evidence '02-installer-capture') '04-installer'

Write-Host '5/8 手机'
Ensure '05-mobile'
foreach ($f in @('MOBILE12-11-PAGE-DRIVEN-PERMISSION.md')) {
    $src = Join-Path $Repo "docs\$f"; if (Test-Path $src) { $null = Put $src (Join-Path '05-mobile' $f) }
}
@(
    '# 手机相关证据',
    '',
    '浏览器的逐项原始结果在 `02-tests/e2e-ui-chromium.json` 与 `02-tests/e2e-ui-webkit.json`（两端各 40 项）。',
    '',
    '其中与本候选直接相关的：',
    '',
    '| 项目 | 结论 | 证据位置 |',
    '| --- | --- | --- |',
    '| MOBILE12-11 权限自动更新（页面自发） | PASS（Chromium 与 WebKit 均通过） | `02-tests/e2e-ui-*.json` 的 MOBILE12-11 行 |',
    '| MOBILE12-01 在线状态 70 秒 | PASS | 同上 |',
    '| MOBILE12-06 查看器内主题切换 | PASS | 同上 |',
    '| MOBILE12-07/08 视频查看器图层与真实媒体 | PASS（H.264 解码单列为真机项） | 同上 |',
    '| MOBILE12-09/10 本机状态按能力分级 | PASS | 同上 |',
    '',
    'MOBILE12-11 的关键点：测试端**从不调用** `/api/me`。由页面自己的 heartbeat 收到新修订号后',
    '自行请求 `/api/me`、自行更新上传按钮；降权后再次自动刷新并禁用。记录里含管理端修改时间、',
    '修订号变化、页面自发请求、按钮状态变化，且未重新配对、未重新加载页面。',
    '',
    '真机项（真实 iPhone 播放、跨显示器）仍为 **NOT_RUN**，不冒充真机验收。'
) | Set-Content -LiteralPath (P '05-mobile/README.md') -Encoding UTF8

Write-Host '6/8 运行与更新'
Ensure '06-runtime-update'
$null = Put ((Get-ChildItem (Join-Path $evidence '03-setup-acceptance') -Recurse -File -Filter 'setup-acceptance.json' | Select-Object -First 1).FullName) '06-runtime-update/setup-acceptance.json'
$null = Put ((Get-ChildItem (Join-Path $evidence '04-update-rollback') -Recurse -File -Filter 'update-verify.json' | Select-Object -First 1).FullName) '06-runtime-update/A-class-update-verify.json'
$null = Put ((Get-ChildItem (Join-Path $evidence '04-update-rollback') -Recurse -File -Filter 'updater.log' | Select-Object -First 1).FullName) '06-runtime-update/A-class-updater.log'
$null = Put (Join-Path $evidence '08-https\B-dynamic-port\B-dynamic-port-update.json') '06-runtime-update/B-dynamic-port/B-dynamic-port-update.json'
$null = Put (Join-Path $evidence '08-https\https-e2e-matrix.json') '06-runtime-update/https-e2e-matrix.json'
$null = Put (Join-Path $evidence '05-runtime-identity\runtime-identity.json') '06-runtime-update/runtime-identity.json'
$null = Put (Join-Path $delivery 'RELEASE_CANDIDATE_IDENTITY.json') '06-runtime-update/release-candidate-identity.json'
$null = Put (Join-Path $delivery 'SOURCE_TREE_VERIFY.json') '06-runtime-update/source-tree-verify.json'
$null = Put (Join-Path $delivery 'INSTALL_MANIFEST_VERIFY.json') '06-runtime-update/install-manifest-verify.json'

Write-Host '7/8 源码审阅副本（脱敏）+ 工具修复'
Ensure '07-source-review/source'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$srcZip = Join-Path $delivery 'LanDisk-source.zip'
$extract = Join-Path $StageDir '07-source-review\.extract'
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
[System.IO.Compression.ZipFile]::ExtractToDirectory($srcZip, $extract)
$mapLines = New-Object System.Collections.Generic.List[string]
$mapLines.Add("# 冻结源码包 -> 脱敏审阅副本 文件级映射")
$mapLines.Add("# 原始源码包：LanDisk-source.zip")
$mapLines.Add(("# 原始 bytes: " + (Get-Item $srcZip).Length))
$mapLines.Add(("# 原始 SHA256: " + (Get-FileHash $srcZip -Algorithm SHA256).Hash.ToLowerInvariant()))
$mapLines.Add(("# 构建提交: " + $productCommit))
$mapLines.Add("#")
$mapLines.Add("# 注意：这是**脱敏审阅副本**，不是原始发布源码包。")
$mapLines.Add("#       它的任何哈希都不能代替冻结源码 ZIP 的哈希。")
$mapLines.Add("#")
$mapLines.Add("relative_path`toriginal_sha256`treview_sha256`tprocessed")
$processed = 0
$total = 0
foreach ($f in (Get-ChildItem -LiteralPath $extract -Recurse -File)) {
    $total++
    $rel = $f.FullName.Substring($extract.Length).TrimStart('\')
    $origSha = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $dst = Join-Path (P '07-source-review/source') $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    if ($f.Extension -in @('.cs','.ts','.tsx','.js','.mjs','.json','.md','.txt','.ps1','.csproj','.xaml','.html','.css','.props','.slnx','.yml','.yaml','.xml')) {
        $changed = Mask-NetworkFile -SourcePath $f.FullName -DestinationPath $dst
        $revSha = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($changed) { $processed++; $mapLines.Add(($rel + "`t" + $origSha + "`t" + $revSha + "`tYES-脱敏")) }
        else { $mapLines.Add(($rel + "`t" + $origSha + "`t" + $revSha + "`tNO-原字节")) }
    }
    else { Copy-Item -LiteralPath $f.FullName -Destination $dst -Force }
}
Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
$mapLines.Add("#")
$mapLines.Add(("# 合计文件 " + $total + "，其中做了脱敏 " + $processed))
$mapLines | Set-Content -LiteralPath (P '07-source-review/MASKING_MAP.tsv') -Encoding UTF8

$null = Put (Join-Path $Repo 'docs\CHECKPOINT_11_UI_CLOSEOUT_02P.md') '07-source-review/CHECKPOINT_02P.md'
$null = Put (Join-Path $Repo 'docs\LAN_DISK_11_HTTPS_LISTENER_RECORD.md') '07-source-review/LAN_DISK_11_HTTPS_LISTENER_RECORD.md'
$null = Put (Join-Path $Repo 'docs\LAN_DISK_11_PORT_FIX_RECORD.md') '07-source-review/LAN_DISK_11_PORT_FIX_RECORD.md'
Ensure '07-source-review/delivery-tools'
foreach ($t in @('Build-All.ps1','Finalize-Delivery.ps1','Invoke-UiMatrixGate.ps1','Invoke-ReleaseGate.ps1',
                 'EvidenceVerdict.ps1','Mask-NetworkIdentity.ps1','New-EvidenceBinding.ps1','Generate-DeliveryReport.ps1')) {
    Copy-Item -LiteralPath (Join-Path $Repo "tools\$t") -Destination (P '07-source-review/delivery-tools') -Force -ErrorAction SilentlyContinue
}
@(
    '# 冻结候选之后的交付工具修复（不包含在冻结源码包内）',
    '',
    ('冻结产品候选：`' + $version + '@' + $productCommit + '`'),
    ('当前仓库 HEAD（含下述工具修复）：`' + $head + '`'),
    '',
    '**下述修改都与产品源码无关**，属于交付/验收工具，因此不改变四个冻结产物的字节；',
    '但它们**不在**冻结的 `LanDisk-source.zip` 内 —— 从冻结源码重建时若只用该 ZIP，',
    '会遇到下面这些已修复的问题。',
    '',
    '## 1. Build-All.ps1：不再整目录删除交付目录',
    '',
    '原先打包前执行 `Remove-Item $deliveryDir -Recurse -Force`，把整个交付目录连同上一轮已交付的',
    '材料一起删除。实际后果：**1.7.3 历史审核包（516,033,414 字节，SHA256 `44b549c6…`）被误删**，',
    '`-Force` 绕过回收站无法恢复；其后用幸存的暂存目录重建了副本',
    '（`dce206a0…`，132 个成员，**不等于原件**）。',
    '',
    '修复后的行为：只删除本次构建自己会重新生成的那些文件（四个产物、报告、身份与门禁文件、ui-evidence），',
    '不再碰目录里的其它内容。',
    '',
    '## 2. Finalize-Delivery.ps1：运行期身份的来源与脱敏',
    '',
    '原先固定镜像 `dist/runtime-identity/`，那是一个 **1.7.0 时代**的旧文件',
    '（productVersion 1.7.0 / buildCommit 3bbfe1b6），会被当成当前候选的身份证据交付；',
    '且其中含本机私网地址与网卡型号。现在改为优先取**属于当前候选**的证据目录，并在镜像时脱敏。',
    '',
    '## 3. 其它工具',
    '',
    '- `Invoke-UiMatrixGate.ps1`（新增）：发布入口接入桌面矩阵门禁。',
    '- `Invoke-ReleaseGate.ps1`（新增）：Full 发布判定，证据必须属于当前候选。',
    '- `EvidenceVerdict.ps1`（新增）：判定器；矩阵按页面身份集合核对，不再只比数量。',
    '- `Mask-NetworkIdentity.ps1`（新增）：网络标识脱敏。',
    '- `New-EvidenceBinding.ps1`（新增）：需求—证据绑定表生成。',
    '',
    '上述文件的原文全部随本包放在 `07-source-review/delivery-tools/`。'
) | Set-Content -LiteralPath (P '07-source-review/TOOL_FIXES.md') -Encoding UTF8

Write-Host '8/8 隐私与历史'
Ensure '08-privacy-history'
$null = Put (Join-Path $Repo 'dist\delivery-175-final\PRIVACY_CHECK.json') '08-privacy-history/PRIVACY_CHECK.json'
$null = Put (Join-Path $Repo 'docs\LAN_DISK_11_173_BUNDLE_PRIVACY_NOTICE.md') '08-privacy-history/HISTORICAL_BUNDLE_PRIVACY_NOTICE.md'
$null = Put (Join-Path $Repo 'tools\Mask-NetworkIdentity.ps1') '08-privacy-history/Mask-NetworkIdentity.ps1'
@(
    '# 隐私：范围、关系与遗留',
    '',
    '## 1. 最终隐私检查',
    '',
    '见同目录 `PRIVACY_CHECK.json`：对**实际准备封包的交付集合**逐项扫描文本内容与图片命名规则。',
    '扫描范围包含嵌套归档成员、源码审阅副本、截图、录屏、文本与元数据。',
    '',
    '## 2. 原件与对外副本的关系',
    '',
    '- **本地原件一律未覆盖、未删除、未改写**；对外副本在包内，并明确标识为脱敏副本。',
    '- 保留的核验信息：version、commit、端口、证书指纹、测试结果、产物 SHA256、测试身份。',
    '- 掩码一致（同一类值总是同一个标识），且**不使用虚构地址**冒充真实监听值。',
    '',
    '## 3. 已知遗留：冻结源码包内仍有真实网络标识',
    '',
    '冻结的 `LanDisk-source.zip`（`c01605fa…`）中，**源码注释**里含本机私网地址与网卡型号',
    '（更早轮次遗留，例如 `src/LanDisk.App/UiCapture.cs` 的说明性注释）。',
    '',
    '本包内的源码是**脱敏审阅副本**（见 `07-source-review/`），因此包内材料不含该标识；',
    '但**冻结源码 ZIP 本身仍含**。改动它等于修改产品源码 → 必须建立新候选并重建全部验收，',
    '不在本轮授权范围内。此处如实记录，未静默处理、也不声称已修复。',
    '',
    '## 4. 历史误删事故',
    '',
    '见 `HISTORICAL_BUNDLE_PRIVACY_NOTICE.md` 第 0 节：1.7.3 历史审核包原件被 Build-All 的整目录删除',
    '误删；重建副本 `dce206a0…` 与原件**不是同一份字节**，本包不把它冒充原件。',
    '找回历史原件不是本次封包的前置任务。'
) | Set-Content -LiteralPath (P '08-privacy-history/README.md') -Encoding UTF8

Write-Host ''
Write-Host ("staged: {0} files" -f @(Get-ChildItem -LiteralPath $StageDir -Recurse -File).Count)
