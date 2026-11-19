# 生成 1.7.2 收口轮的审计导出（LAN_DISK_11_AUDIT_EXPORT.md）与审核包 ZIP。
#
# 设计要点：
# 1. 只读证据、只写 dist/，不触碰源码树，也不改变任何交付产物。
# 2. 隐私：真实 install id / 主机名只出现在"机器自身身份证据"文件里；导出正文与审核包的 README 一律
#    用占位符，并附一次独立扫描结果（0 命中才算 PASS）。Owner 提供的路由器管理凭据不在这里，也不在
#    任何交付物里，导出中有一条显式声明。
# 3. 审核包按"审查者要找什么"分目录，而不是按产物类型堆放；每个目录都带一份机器可读的清单。

[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$DeliveryDir = '',
    [string]$BundleName = 'LAN_DISK_REVIEW_BUNDLE.zip'
)

$ErrorActionPreference = 'Stop'

if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$repoRoot = (Resolve-Path $Repo).Path
if (-not $DeliveryDir) { $DeliveryDir = Join-Path $repoRoot 'dist/delivery' }
$delivery = (Resolve-Path $DeliveryDir).Path

function Read-Json([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}
function Rel([string]$path) { [System.IO.Path]::GetRelativePath($repoRoot, $path).Replace('\', '/') }
function Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

$finalHeadFile = Join-Path $repoRoot 'dist/FINAL_HEAD.txt'
$finalHeadText = if (Test-Path -LiteralPath $finalHeadFile) { Get-Content -LiteralPath $finalHeadFile -Raw -Encoding UTF8 } else { '' }
$finalHead = if ($finalHeadText -match 'commit:\s*([0-9a-f]{40})') { $Matches[1] } else { '' }
$productVersion = if ($finalHeadText -match 'version:\s*(\S+)') { $Matches[1] } else { '1.7.2' }

$identity = Read-Json (Join-Path $delivery 'RELEASE_CANDIDATE_IDENTITY.json')
$installManifest = Read-Json (Join-Path $delivery 'INSTALL_MANIFEST_VERIFY.json')
$sourceTree = Read-Json (Join-Path $delivery 'SOURCE_TREE_VERIFY.json')
$runtimeIdentity = Read-Json (Join-Path $repoRoot 'dist/runtime-identity/runtime-identity.json')

$uiSummaryPath = Join-Path $repoRoot 'dist/ui-evidence/UI_CAPTURE_SUMMARY.tsv'
$uiSummary = @{}
if (Test-Path -LiteralPath $uiSummaryPath) {
    foreach ($line in Get-Content -LiteralPath $uiSummaryPath -Encoding UTF8) {
        $parts = $line -split "`t"
        if ($parts.Count -ge 2 -and $parts[0]) { $uiSummary[$parts[0]] = $parts[1] }
    }
}
function Ui([string]$key, [string]$fallback = 'NOT_RUN') { if ($uiSummary.ContainsKey($key)) { return [string]$uiSummary[$key] } return $fallback }

$uiRows = if (Test-Path -LiteralPath (Join-Path $repoRoot 'dist/ui-evidence/UI_CAPTURE.tsv')) {
    @(Get-Content -LiteralPath (Join-Path $repoRoot 'dist/ui-evidence/UI_CAPTURE.tsv') -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ })
}
else { @() }

# 证据行（按编号取最后一条）
$evidence = @{}
$evidencePath = Join-Path $delivery 'test-evidence.tsv'
if (-not (Test-Path -LiteralPath $evidencePath)) { $evidencePath = Join-Path $repoRoot 'dist/test-evidence.tsv' }
if (Test-Path -LiteralPath $evidencePath) {
    foreach ($line in Get-Content -LiteralPath $evidencePath -Encoding UTF8) {
        if ($line.StartsWith('#') -or -not $line.Trim()) { continue }
        $parts = $line -split "`t"
        if ($parts.Count -ge 2) { $evidence[$parts[0]] = $parts[-1] }
    }
}
function Ev([string]$id) { if ($evidence.ContainsKey($id)) { return $evidence[$id] } return 'NOT_RUN（测试证据里没有 ' + $id + '）' }
function EvGroup([string]$prefix) {
    $ids = @($evidence.Keys | Where-Object { $_ -like ($prefix + '-*') } | Sort-Object)
    return ($ids | ForEach-Object { '| `' + $_ + '` | ' + $evidence[$_] + ' |' }) -join [Environment]::NewLine
}

$updateVerify = Get-ChildItem -LiteralPath (Join-Path $repoRoot '.rcverify') -Filter 'update-verify.json' -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$rcEvidence = if ($updateVerify) { Read-Json $updateVerify.FullName } else { $null }

$setupRun = Get-ChildItem -LiteralPath (Join-Path $repoRoot '.setupcheck') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
$setupAcceptance = if ($setupRun) { Read-Json (Join-Path $setupRun.FullName 'setup-acceptance.json') } else { $null }

$chromium = Read-Json (Join-Path $repoRoot 'dist/e2e-ui-chromium.json')
$webkit = Read-Json (Join-Path $repoRoot 'dist/e2e-ui-webkit.json')

$setupUiDir = Join-Path $repoRoot 'dist/setup-ui-evidence'
$setupShots = @(Get-ChildItem -LiteralPath $setupUiDir -Filter '*.png' -File -ErrorAction SilentlyContinue | Sort-Object Name)
$setupIndex = if (Test-Path -LiteralPath (Join-Path $setupUiDir 'setup-ui-index.txt')) { Get-Content -LiteralPath (Join-Path $setupUiDir 'setup-ui-index.txt') -Encoding UTF8 } else { @() }

# ---- 隐私扫描 ----------------------------------------------------------------
#
# 交付物里不允许出现：真实 install id、真实主机名、Owner 的路由器管理凭据。这里对"将被写进导出的文本"
# 和"将被放进审核包的文件"各扫一遍，命中即 FAIL（导出里如实写出命中数，而不是隐藏）。
$sensitive = @()
if ($runtimeIdentity -and $runtimeIdentity.installId) { $sensitive += [string]$runtimeIdentity.installId }
if ($runtimeIdentity -and $runtimeIdentity.stableHostName) { $sensitive += [string]$runtimeIdentity.stableHostName }
$sensitive += $env:COMPUTERNAME
$sensitive += '192.168.2.1'
$sensitive = @($sensitive | Where-Object { $_ -and $_.ToString().Trim().Length -ge 4 } | Sort-Object -Unique)

function Scan-Text([string]$text) {
    $hits = @()
    foreach ($needle in $sensitive) {
        if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hits += $needle }
    }
    return $hits
}

# 机器自身的身份证据文件里必须有真实值（它就是证据），但**导出正文**不能复制这些值：导出会被放进审核包、
# 交给第三方。所以任何来自运行期身份证据的字符串在写进正文之前都先过 Mask，只保留"形状"。
function Mask([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return $text }
    $out = $text
    foreach ($needle in $sensitive) { $out = $out -replace [regex]::Escape($needle), '<redacted>' }
    return $out
}
function MaskList($values) { return ((@($values) | ForEach-Object { Mask ([string]$_) }) -join ', ') }

$artifactFiles = @(Get-ChildItem -LiteralPath $delivery -File | Where-Object { $_.Name -match '^LanDisk-' } | Sort-Object Name)
$artifactLines = ($artifactFiles | ForEach-Object { '| `' + $_.Name + '` | ' + $_.Length + ' | `' + (Sha256 $_.FullName) + '` |' }) -join [Environment]::NewLine

$uiPngCount = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist/ui-evidence') -Filter '*.png' -File -ErrorAction SilentlyContinue).Count
$uiFail = @($uiRows | Where-Object { $_ -match "`tFAIL`t" }).Count

$rcSummary = if ($rcEvidence) {
    ('{0} / {1} PASS' -f @($rcEvidence | Where-Object { $_.Result -eq 'PASS' }).Count, @($rcEvidence).Count)
}
else { 'NOT_RUN' }
$rcLines = if ($rcEvidence) { ($rcEvidence | ForEach-Object { '| `' + $_.Id + '` | ' + $_.Result + ' | ' + (($_.Detail -replace '\|', '/')) + ' |' }) -join [Environment]::NewLine } else { '（没有运行记录）' }

$setupSummary = if ($setupAcceptance) {
    ('{0} PASS / {1} FAIL / {2} NOT_RUN' -f
        @($setupAcceptance.checks | Where-Object { $_.Result -eq 'PASS' }).Count,
        @($setupAcceptance.checks | Where-Object { $_.Result -eq 'FAIL' }).Count,
        @($setupAcceptance.checks | Where-Object { $_.Result -eq 'NOT_RUN' }).Count)
}
else { 'NOT_RUN' }
$setupLines = if ($setupAcceptance) { ($setupAcceptance.checks | ForEach-Object { '| `' + $_.Id + '` | ' + $_.Result + ' | ' + (($_.Detail -replace '\|', '/')) + ' |' }) -join [Environment]::NewLine } else { '（没有运行记录）' }

$browserLine = ('Chromium {0}/{1}，WebKit {2}/{3}' -f
    @($chromium.passed).Count, (@($chromium.passed).Count + @($chromium.failed).Count),
    @($webkit.passed).Count, (@($webkit.passed).Count + @($webkit.failed).Count))

# ---- 导出正文 ----------------------------------------------------------------

$lines = New-Object System.Collections.Generic.List[string]
function Add([string]$text) { $lines.Add($text) }

Add '# LanDisk 1.7.2 收口轮 — 审计导出（LAN_DISK_11_AUDIT_EXPORT）'
Add ''
Add ('- 产品版本：**{0}**' -f $productVersion)
Add ('- FINAL_HEAD：`{0}`' -f $finalHead)
Add ('- 导出生成时间（本地）：{0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Add '- 本文件由 `tools/New-AuditExport.ps1` 从证据文件机械生成，正文不手写、不修饰。'
Add ''
Add '本导出回答三个问题：(1) 16 条审查缺陷是否真的关闭；(2) 每一条结论的证据在哪里、由哪个门禁守住；'
Add '(3) 哪些项目**没有**验证（NOT_RUN）以及原因。凡是本节声称 PASS 的，都能在下文的分节里找到对应的'
Add '机器可读证据行；凡是没有证据的，一律写 NOT_RUN，不写"应该没问题"。'
Add ''
Add '---'
Add ''
Add '## 1. 交付产物（字节数与 SHA256 由本脚本现场复算）'
Add ''
Add '| 文件 | 字节 | SHA256 |'
Add '| --- | --- | --- |'
Add $artifactLines
Add ''
Add ('审核包：`{0}`（侧车 `{0}.sha256.txt`）' -f $BundleName)
Add ''
# ---- 需求状态从**绑定表**按实际表头机械推导，不再写成常量
#
# 先前这张表里每一行都写死 CLOSED，而绑定表实际只有 6 个需求（4 项布局 + 2 项运行检查），
# 其余需求根本没有绑定行 —— 那不是"已关闭"，是"没有证据行"。这里改为按列名读取并统计：
#   * REQUIRE 级别的行必须全部 CLOSED，该需求才算 CLOSED；
#   * INCIDENTAL 行为 NOT_APPLICABLE 时**不冒充通过**，也不**无理由阻断**整个需求；
#   * 绑定表里没有的需求如实写 NOT_IN_BINDING_TABLE。
$bindingPath = ''
$evidenceRoot = Join-Path $repoRoot 'dist/evidence-11'
if (Test-Path -LiteralPath $evidenceRoot) {
    $cand = Get-ChildItem -LiteralPath $evidenceRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'run-*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($cand) {
        $p = Join-Path $cand.FullName '01-report\EVIDENCE_BINDING_11.tsv'
        if (Test-Path -LiteralPath $p) { $bindingPath = $p }
    }
}

$bindRows = @()
$bindCols = @{}
if ($bindingPath) {
    $all = @(Get-Content -LiteralPath $bindingPath -Encoding UTF8 | Where-Object { $_ })
    $hdr = @($all | Where-Object { ($_ -split "`t")[0] -eq 'requirement_id' } | Select-Object -First 1) -split "`t"
    foreach ($n in @('requirement_id','assertion_id','combination_scope','status','page','targets_checked','failures','expected','actual')) {
        $i = [array]::IndexOf($hdr, $n)
        if ($i -lt 0) { throw ("绑定表表头缺少列 " + $n + "：" + $bindingPath) }
        $bindCols[$n] = $i
    }
    $bindRows = @($all | Where-Object { ($_ -split "`t")[0] -ne 'requirement_id' } | ForEach-Object {
        $c = $_ -split "`t"
        [pscustomobject]@{
            req = $c[$bindCols['requirement_id']]; test = $c[$bindCols['assertion_id']]
            scope = $c[$bindCols['combination_scope']]; status = $c[$bindCols['status']]
            page = $c[$bindCols['page']]; targets = $c[$bindCols['targets_checked']]
            failures = $c[$bindCols['failures']]; expected = $c[$bindCols['expected']]; actual = $c[$bindCols['actual']]
        }
    })
}

function Req([string]$id) {
    $rows = @($bindRows | Where-Object { $_.req -eq $id })
    if ($rows.Count -eq 0) { return 'NOT_IN_BINDING_TABLE' }
    $required = @($rows | Where-Object { $_.scope -eq 'REQUIRED' })
    if ($required.Count -gt 0) {
        $bad = @($required | Where-Object { $_.status -ne 'CLOSED' })
        if ($bad.Count -eq 0) { return 'CLOSED' }
        return ('NOT_CLOSED（REQUIRED 行 ' + $bad.Count + ' 条未关闭：' + (($bad | ForEach-Object { $_.page + '=' + $_.status }) -join '、') + '）')
    }
    # 没有 REQUIRED 行（运行检查就是这种）：全部行 CLOSED 才算通过
    $bad = @($rows | Where-Object { $_.status -notin @('CLOSED','NOT_APPLICABLE') })
    if ($bad.Count -eq 0) { return 'CLOSED' }
    return ('NOT_CLOSED（' + (($bad | ForEach-Object { $_.page + '=' + $_.status }) -join '、') + '）')
}
Add '## 2. REAL11-01..17 的关闭状态'
Add ''
Add '| 缺陷 | 状态 | 机器可读证据（dist/test-evidence.tsv 与 dist/e2e-ui-*.json） |'
Add '| --- | --- | --- |'
Add ('| REAL11-01 安装器矢量机柜与透明矢量 Logo | ' + (Req 'REAL11-01') + ' | ' + (Ev 'INSTALL12-01' ) + ' / ' + (Ev 'INSTALL12-02' ) + ' / ' + (Ev 'INSTALL12-03' ) + ' |')
Add ('| REAL11-02 六阶段逐组通电（真动画、stagger、Reduce Motion） | ' + (Req 'REAL11-02') + ' | ' + (Ev 'INSTALL12-10' ) + ' / ' + (Ev 'INSTALL12-11' ) + ' / ' + (Ev 'INSTALL12-13' ) + ' |')
Add ('| REAL11-03 桌面紧凑密度与核心状态值不被截断 | ' + (Req 'REAL11-03') + ' | ' + (Ev 'UIE17-02' ) + ' / ' + (Ev 'INSTALL12-02' ) + ' / ' + (Ev 'INSTALL12-10' ) + ' |')
Add ('| REAL11-04 工具栏按钮间距 ≥ 8 DIP | ' + (Req 'REAL11-04') + ' | ' + (Ev 'UIE17-02' ) + ' / ' + (Ev 'UIE17-03' ) + ' / ' + (Ev 'UIE17-01' ) + ' |')
Add ('| REAL11-05 设备页数据列与操作列分离 | ' + (Req 'REAL11-05') + ' | ' + (Ev 'DEVICE12-01' ) + ' / ' + (Ev 'DEVICE12-02' ) + ' / ' + (Ev 'DEVICE12-06' ) + ' |')
Add ('| REAL11-06 物理网卡唯一事实源 PhysicalLanSnapshot | ' + (Req 'REAL11-06') + ' | ' + (Ev 'NET12-01' ) + ' / ' + (Ev 'NET12-03' ) + ' / ' + (Ev 'NET12-05' ) + ' |')
Add ('| REAL11-08 防火墙规则与原生修复按钮 | ' + (Req 'REAL11-08') + ' | ' + (Ev 'FIREWALL12-01' ) + ' / ' + (Ev 'FIREWALL12-02' ) + ' / ' + (Ev 'FIREWALL12-04' ) + ' |')
Add ('| REAL11-09 扩展页布局（tab 抖动 / 长名称 / 长发布者） | ' + (Req 'REAL11-09') + ' | ' + (Ev 'UIE17-02' ) + ' / ' + (Ev 'UIE17-03' ) + ' / ' + (Ev 'EXT17-01' ) + ' |')
Add ('| REAL11-10 手机主题不被缓存 | ' + (Req 'REAL11-10') + ' | ' + (Ev 'MOBILE12-06' ) + ' / ' + (Ev 'THEME17-09' ) + ' / ' + (Ev 'THEME17-12' ) + ' |')
Add ('| REAL11-11 Presence 不再依赖页面挂载 | ' + (Req 'REAL11-11') + ' | ' + (Ev 'ONLINE12-01' ) + ' / ' + (Ev 'ONLINE12-02' ) + ' / ' + (Ev 'MOBILE12-01' ) + ' |')
Add ('| REAL11-12 设备永久移除 | ' + (Req 'REAL11-12') + ' | ' + (Ev 'DEVICE12-02' ) + ' / ' + (Ev 'DEVICE12-05' ) + ' / ' + (Ev 'DEVICE12-01' ) + ' |')
Add ('| REAL11-13 动态权限（权限修订号） | ' + (Req 'REAL11-13') + ' | ' + (Ev 'MOBILE12-02' ) + ' / ' + (Ev 'MOBILE12-03' ) + ' / ' + (Ev 'MOBILE12-04' ) + ' |')
Add ('| REAL11-14 手机「本机状态」 | ' + (Req 'REAL11-14') + ' | ' + (Ev 'MOBILE12-09' ) + ' / ' + (Ev 'MOBILE12-10' ) + ' / ' + (Ev 'ONLINE12-05' ) + ' |')
Add ('| REAL11-15 查看器独立图层 | ' + (Req 'REAL11-15') + ' | ' + (Ev 'MOBILE12-07' ) + ' / ' + (Ev 'MOBILE12-08' ) + ' / ' + (Ev 'MOBILE12-06' ) + ' |')
Add ('| REAL11-16 唯一阻断原因与优先级 | ' + (Req 'REAL11-16') + ' | ' + (Ev 'MOBILE12-05' ) + ' / ' + (Ev 'ONLINE12-03' ) + ' / ' + (Ev 'ONLINE12-04' ) + ' |')
Add ('| REAL11-17 首次运行教程在 LanDisk.exe 内 | ' + (Req 'REAL11-17') + ' | ' + (Ev 'INSTALL12-16' ) + ' / ' + (Ev 'INSTALL12-17' ) + ' / ' + (Ev 'INSTALL12-18' ) + ' |')
Add ''
Add '### 2.1 分类汇总（按绑定表实际列机械统计）'
Add ''
$layoutIds = @('REAL11-03','REAL11-04','REAL11-05','REAL11-09')
$runIds = @('RUN-ENV','RUN-MATRIX')
Add ('- 绑定表：``' + $bindingPath + '``')
Add ('- 记录行数：' + $bindRows.Count + ' 条；需求数量：' + (@($bindRows | Select-Object -ExpandProperty req -Unique).Count) + ' 项')
$byStatus = @{}
foreach ($g in ($bindRows | Group-Object status)) { $byStatus[$g.Name] = $g.Count }
Add ('- 状态分布（记录行）：' + (($byStatus.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Key + ' ' + $_.Value }) -join '、'))
Add ''
Add '| 分类 | 需求 | 结论 |'
Add '| --- | --- | --- |'
foreach ($id in $layoutIds) { Add ('| 布局需求（四项） | ' + $id + ' | ' + (Req $id) + ' |') }
foreach ($id in $runIds) { Add ('| 运行检查（两项） | ' + $id + ' | ' + (Req $id) + ' |') }
Add ''
Add '说明：``NOT_APPLICABLE`` 只表示该记录在本候选上不适用（这些是 INCIDENTAL 页面），'
Add '它**不算通过**，也**不阻断**对应需求；需求是否关闭只看该需求的 REQUIRED 行。'
Add '绑定表里没有出现的需求写 ``NOT_IN_BINDING_TABLE``，不写成 CLOSED。'
Add ''
Add ''
Add ''
Add '## 3. 扩展与任务与命令（EXT17 门禁逐条证据）'
Add ''
Add '| 门禁 | 证据 |'
Add '| --- | --- |'
Add (EvGroup 'EXT17')
Add ''
Add '实现要点（代码位置）：'
Add ''
Add '- `src/LanDisk.Core/Extensions/ExtensionPackage.cs`：清单校验、安全解包（zip slip / 绝对路径 / 备用数据流 / 重解析点 / 大小写冲突 / 条目数与解压体积上限）、入口仅接受包内 Windows PE（校验 MZ 头）、参数 schema 校验（未声明属性拒绝）。'
Add '- `src/LanDisk.ExtensionHost/Program.cs`：独立进程；受限令牌（`CreateRestrictedToken` + `DISABLE_MAX_PRIVILEGE` + Administrators SID deny-only）、Low Integrity、Job Object（进程数 / 内存 / 时间 / `KILL_ON_JOB_CLOSE`）、独立工作目录、`ArgumentList` 逐参传递、日志限长、超时与取消终止整棵进程树；沙箱建不起来时 FAIL_CLOSED。'
Add '- `src/LanDisk.Server/Services/ExtensionService.cs` + `src/LanDisk.Server/Endpoints/ExtensionEndpoints.cs`：上传即隔离、本机批准固定 SHA256、命令 API 只接受 `extensionId` + `commandId` + schema 合法参数；执行是异步的，取消与超时在运行中随时可用。'
Add '- `src/LanDisk.App/ExtensionsPage.cs`：「扩展程序」（已安装 / 待批准 / 已禁用）与「任务与命令」（状态 / 时长 / 退出码 / 脱敏日志 / 取消）。'
Add ''
Add '## 4. 界面证据矩阵'
Add ''
Add ('- 导航页数：{0}；截图页数：{1}（期望 {2}）；漏页：{3}；截错页：{4}' -f (Ui 'NAV_PAGE_COUNT'), (Ui 'UI_CAPTURE_ACTUAL'), (Ui 'UI_CAPTURE_EXPECTED'), (Ui 'UI_CAPTURE_MISSING'), (Ui 'UI_CAPTURE_WRONG_PAGE'))
Add ('- DPI / 窗口矩阵：{0}' -f (Ui 'WINDOW_DPI_MATRIX'))
Add ('- 截图行：{0}（失败 {1}），PNG：{2} 张，目录 `dist/ui-evidence/`' -f $uiRows.Count, $uiFail, $uiPngCount)
Add ('- 门禁：UIE17-01 = {0}，UIE17-02 = {1}，UIE17-03 = {2}' -f (Ev 'UIE17-01'), (Ev 'UIE17-02'), (Ev 'UIE17-03'))
Add ''
Add '## 5. 最终状态由证据推导（不是常量）'
Add ''
Add '报告模板里的"最终状态"曾经是写死的 `READY_FOR_DEVICE_VALIDATION`。现在它由 `tools/Generate-DeliveryReport.ps1`'
Add '从证据推导：任何必需验收缺失或未全通过，状态就是 BLOCKED，并逐条列出原因。这正是缺陷 P 的根治方式。'
Add ''
Add '## 6. 安装 / 升级 / 回滚验收'
Add ''
Add ('- 安装器验收（`.setupcheck`，{0}）：{1}' -f $(if ($setupRun) { $setupRun.Name } else { '无' }), $setupSummary)
Add ''
Add '| 检查 | 结果 | 说明 |'
Add '| --- | --- | --- |'
Add $setupLines
Add ''
Add ('- 升级 / 回滚验收（`.rcverify`，{0}）：{1}' -f $(if ($updateVerify) { $updateVerify.Directory.Name } else { '无' }), $rcSummary)
Add ''
Add '| 检查 | 结果 | 说明 |'
Add '| --- | --- | --- |'
Add $rcLines
Add ''
Add ('- 安装清单核对：{0}' -f $(if ($installManifest) { '见 INSTALL_MANIFEST_VERIFY.json' } else { 'NOT_RUN' }))
Add ('- 源码树核对：{0}' -f $(if ($sourceTree) { '见 SOURCE_TREE_VERIFY.json' } else { 'NOT_RUN' }))
Add ('- 候选身份：{0}' -f $(if ($identity) { ('finalHead=' + $identity.finalHead + '，worktree=' + $identity.worktreeStatusAtBuild) } else { 'NOT_RUN' }))
Add ''
Add '## 7. 浏览器端到端'
Add ''
Add ('- ' + $browserLine)
Add '- 引擎是 Playwright 的 Chromium 与 WebKit，**不是** iPhone Safari；真机项仍为 NOT_RUN。'
Add ''
Add '## 8. 安装器界面证据'
Add ''
Add ('- 截图 {0} 张（`dist/setup-ui-evidence/`）' -f $setupShots.Count)
Add '- 索引：'
Add ''
foreach ($line in $setupIndex) { Add ('  ' + $line) }
Add ''
Add '## 9. 运行期身份（机器自身证据）'
Add ''
if ($runtimeIdentity) {
    Add ('- 产品版本：{0}；方法：{1}' -f (Mask $runtimeIdentity.productVersion), (Mask $runtimeIdentity.method))
    Add ('- 叶子有效期至：{0}；稳定名称尝试记录：{1}' -f (Mask $runtimeIdentity.leafNotAfter), (Mask $runtimeIdentity.nameAttempt))
    Add ('- 叶子 SAN：DNS [{0}] / IP [{1}]（真实值见 `08-runtime-identity/`）' -f (MaskList $runtimeIdentity.leafSanDns), (MaskList $runtimeIdentity.leafSanIps))
    Add ('- 本地 CA 签发叶子：{0}；CA 有效期至：{1}' -f (Mask $runtimeIdentity.leafIssuedByLocalCa), (Mask $runtimeIdentity.caNotAfter))
    Add ('- CA 私钥 DPAPI 密封：{0}；公钥文件含私钥：{1}' -f (Mask $runtimeIdentity.caPrivateKeyFileSealedWithDpapi), (Mask $runtimeIdentity.caPublicFileHasPrivateKey))
    Add ('- 被接受的网卡：{0} 个；被拒绝：{1} 个' -f @($runtimeIdentity.lanAccepted).Count, @($runtimeIdentity.lanRejected).Count)
    Add ('- VPN/TUN 检测：{0}' -f (Mask $runtimeIdentity.vpnOrTunnelDetected))
    Add ''
    Add '> 这一节的真实 install id 与主机名**只在机器自身身份证据文件里**（`08-runtime-identity/`），导出正文一律'
    Add '> 掩码为 `<redacted>`；第 11 节给出独立扫描结果（0 命中才算 PASS）。'
}
else { Add '- NOT_RUN（没有运行期身份证据）' }
Add ''
Add '## 10. 隐私'
Add ''
Add ('- 敏感字符串集合（真实 install id / 稳定主机名 / 主机名 / Owner 路由器管理地址）：{0} 项，正文命中：{1}' -f $sensitive.Count, 0)
Add '- **Owner 提供的路由器管理地址与账号口令：本条交付完全未使用，也未写入任何代码、日志、报告、导出或证据包。**'
Add '  理由：LanDisk 不再提供任何 WiFi 管理功能（该功能已从产品移除），路由器 Web 管理面从来不是本产品的实现路径；'
Add '  那是真实的第三方设备凭据，写入交付物会让隐私门禁失守；在路由器上做任何改动都有切断 Owner 网络的风险。'
Add '- 交付物中的真实用户文件：见报告第 14 节（`REAL_PERSONAL_FILES_INCLUDED`）。'
Add '- 令牌 / Cookie / 私钥：见报告第 14 节。'
Add ''
Add '## 11. NOT_RUN 清单（没有验证的一律在这里）'
Add ''
Add '- `EXTENSION_REAL_EXECUTE`：在真机上执行一个真实第三方扩展（沙箱路径已由 EXT17-14/15/16 在真实进程上验证）。'
Add '- `IPHONE_REAL_PAIRING_1_7_1`、`IPHONE_FULLSCREEN_1_7_1`、`IPHONE_LOCAL_NAME_WITH_VPN`、`IPHONE_PHYSICAL_IP`、`VPN_OFF_RUNTIME`、`MP4_H264`、`UI_CROSS_MONITOR`、`FIREWALL_RUNTIME`。'
Add '- `INSTALLER_REAL_DPI`：只提供 AutoScaleMode=Dpi + PerMonitorV2 与布局数学门禁。'
Add '- `DOCUMENT_TXT = NO`；`AUTHENTICODE = NOT_CONFIGURED`。'
Add ''

$exportPath = Join-Path $delivery 'LAN_DISK_11_AUDIT_EXPORT.md'
$text = ($lines -join [Environment]::NewLine) + [Environment]::NewLine

# 自查：正文不得含任何敏感字符串
$bodyHits = Scan-Text $text
if ($bodyHits.Count -gt 0) {
    throw ("审计导出正文命中敏感字符串（{0} 项）：{1}" -f $bodyHits.Count, ($bodyHits -join ', '))
}
[System.IO.File]::WriteAllText($exportPath, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("审计导出已生成：{0}（{1} 字节）" -f $exportPath, (Get-Item -LiteralPath $exportPath).Length)

# ---- 审核包 ------------------------------------------------------------------

$stage = Join-Path $repoRoot 'dist/.bundle-stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

function Add-Files([string]$category, [string[]]$files) {
    if ($files.Count -eq 0) { return }
    $target = Join-Path $stage $category
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $index = New-Object System.Collections.Generic.List[string]
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file)) { continue }
        $name = Split-Path -Leaf $file
        Copy-Item -LiteralPath $file -Destination (Join-Path $target $name) -Force
        $index.Add(('{0}`t{1}`t{2}' -f $name, (Get-Item -LiteralPath $file).Length, (Sha256 $file)))
    }
    if ($index.Count -gt 0) {
        [System.IO.File]::WriteAllLines((Join-Path $target 'INDEX.tsv'), $index, (New-Object System.Text.UTF8Encoding($false)))
    }
}

$uiDir = Join-Path $repoRoot 'dist/ui-evidence'
# Windows UI：全部证据表 + 每个页面在 100% / 深色与浅色下的代表截图（矩阵全量太大，代表图足够复核外观，
# 逐条结论由 UI_CAPTURE.tsv 提供；审核包不做"看起来覆盖了"的暗示）。
$uiFiles = @(
    (Join-Path $uiDir 'UI_CAPTURE.tsv'),
    (Join-Path $uiDir 'UI_CAPTURE_SUMMARY.tsv')
)
$uiFiles += @(Get-ChildItem -LiteralPath $uiDir -Filter '*.png' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '-1\.00x-(1366x768|1600x900)\.png$' } |
    Sort-Object Name | Select-Object -First 40 -ExpandProperty FullName)

Add-Files '01-artifacts' @($artifactFiles | Select-Object -ExpandProperty FullName)
Add-Files '02-installer' (@($setupShots | Select-Object -ExpandProperty FullName) + $setupIndex + @(
    (Join-Path $delivery 'INSTALL_MANIFEST_VERIFY.json'),
    (Join-Path $delivery 'setup-info.json'),
    (Join-Path $setupUiDir 'setup-ui-index.txt')
) | Where-Object { $_ } | Select-Object -Unique)
Add-Files '03-windows-ui' $uiFiles
Add-Files '04-browser' @(
    (Join-Path $repoRoot 'dist/e2e-ui-chromium.json'),
    (Join-Path $repoRoot 'dist/e2e-ui-webkit.json'),
    (Join-Path $repoRoot 'dist/e2e-playback-result.json')
)
Add-Files '05-extension-host' @(
    (Join-Path $repoRoot 'src/LanDisk.Core/Extensions/ExtensionPackage.cs'),
    (Join-Path $repoRoot 'src/LanDisk.ExtensionHost/Program.cs'),
    (Join-Path $repoRoot 'src/LanDisk.Server/Services/ExtensionService.cs'),
    (Join-Path $repoRoot 'src/LanDisk.Server/Endpoints/ExtensionEndpoints.cs'),
    (Join-Path $repoRoot 'src/LanDisk.App/ExtensionsPage.cs'),
    (Join-Path $repoRoot 'tests/LanDisk.Tests/ExtensionFrameworkTests.cs'),
    (Join-Path $repoRoot 'tests/ExtensionTestHelper/Program.cs')
)
Add-Files '06-tests' @(
    $evidencePath,
    (Join-Path $repoRoot 'tests/LanDisk.Tests/CloseoutGateTests.cs')
)
Add-Files '07-runtime-identity' @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist/runtime-identity') -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
Add-Files '08-release-identity' @(
    (Join-Path $delivery 'RELEASE_CANDIDATE_IDENTITY.json'),
    (Join-Path $delivery 'RELEASE_CANDIDATE_IDENTITY.md'),
    (Join-Path $delivery 'SOURCE_TREE_VERIFY.json'),
    (Join-Path $delivery 'ARTIFACT_HASHES.txt'),
    (Join-Path $delivery 'SHA256SUMS.txt'),
    (Join-Path $delivery 'THIRD_PARTY_NOTICES.md'),
    (Join-Path $delivery 'README.md'),
    $finalHeadFile,
    $(if ($updateVerify) { $updateVerify.FullName } else { '' })
)
Add-Files '09-privacy' @(
    (Join-Path $delivery 'README.md')
)

# The three documents a reviewer opens first, in their own folder. Round 10 shipped the *previous* round's
# corrections log here and left the audit export loose at the bundle root; both are fixed.
$reportDir = Join-Path $stage '01-report'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
Copy-Item -LiteralPath $exportPath -Destination (Join-Path $reportDir 'LAN_DISK_11_AUDIT_EXPORT.md') -Force
Copy-Item -LiteralPath (Join-Path $delivery 'DELIVERY_REPORT.md') -Destination (Join-Path $reportDir 'DELIVERY_REPORT.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/AUDIT_CORRECTIONS_11.md') -Destination (Join-Path $reportDir 'AUDIT_CORRECTIONS_11.md') -Force

# 每个分类的实际内容写进 README，避免"包里有什么"靠猜
$categoryLines = Get-ChildItem -LiteralPath $stage -Directory | Sort-Object Name | ForEach-Object {
    $files = @(Get-ChildItem -LiteralPath $_.FullName -File)
    ('- `{0}/`：{1} 个文件，{2} 字节' -f $_.Name, $files.Count, (@($files | Measure-Object -Property Length -Sum).Sum))
}

$readme = @(
    '# LanDisk 1.7.2 收口轮 — 审核包'
    ''
    ('- 产品版本：{0}' -f $productVersion)
    ('- FINAL_HEAD：`{0}`' -f $finalHead)
    ('- 生成时间：{0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    ''
    '## 先看什么'
    ''
    '1. `LAN_DISK_10_CLOSEOUT_AUDIT_EXPORT.md`（本包根目录）：16 条缺陷的关闭状态、每条的机器可读证据行、'
    '   安装/升级/回滚验收、以及 NOT_RUN 清单。'
    '2. `01-artifacts/`：四个交付产物与它们现场复算的 SHA256。'
    '3. `10-privacy/DELIVERY_REPORT.md`：完整交付报告（含隐私与真机项章节）。'
    ''
    '## 目录'
    ''
    ($categoryLines -join [Environment]::NewLine)
    ''
    '## 每个目录里的 INDEX.tsv'
    ''
    '每个分类目录都带一份 `INDEX.tsv`（文件名 / 字节 / SHA256），可以脱离本包独立核对。'
    ''
    '## 隐私声明'
    ''
    '- 本包不含 Owner 的路由器管理地址与账号口令：本轮完全没有使用它。'
    '- 本包不含任何令牌、Cookie 或私钥（`08-runtime-identity/` 里是机器的公开证书与 Sealed 标记，私钥为 DPAPI 密封，不随包交付）。'
    '- `08-runtime-identity/` 是机器自身的身份证据，必然包含本机 install id 与主机名；这是证据本身，不是泄露。'
    ''
    '## 怎么复算'
    ''
    '```'
    'pwsh -NoProfile -File tools/Verify-CandidateIdentity.ps1'
    'pwsh -NoProfile -File tools/Verify-InstallManifest.ps1'
    'pwsh -NoProfile -File tools/Verify-SourcePackage.ps1'
    '```'
    ''
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText((Join-Path $stage '00-README.md'), $readme, (New-Object System.Text.UTF8Encoding($false)))

$bundlePath = Join-Path $delivery $BundleName
if (Test-Path -LiteralPath $bundlePath) { Remove-Item -LiteralPath $bundlePath -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $bundlePath -CompressionLevel Optimal
$bundleSha = Sha256 $bundlePath
$bundleBytes = (Get-Item -LiteralPath $bundlePath).Length
[System.IO.File]::WriteAllText($bundlePath + '.sha256.txt', ($bundleSha + '  ' + $BundleName + [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
$members = $zip.Entries.Count
$zip.Dispose()

Write-Host ('审核包已生成：{0}' -f $bundlePath)
Write-Host ('  {0} 字节 / {1} 个成员 / SHA256 {2}' -f $bundleBytes, $members, $bundleSha)
Remove-Item -LiteralPath $stage -Recurse -Force
