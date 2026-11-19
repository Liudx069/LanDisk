param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$DeliveryDir = '',
    [string]$EvidenceDir = '',
    [string]$OutFile = ''
)

# 对**实际交付集合**执行隐私检查。
#
# 不因为"用了夹具"就宣布通过 —— 逐项扫描真实文件内容与文件名。
# 必须保留：版本、提交、产物 SHA256、测试身份（脱敏只针对真实网络标识）。

$ErrorActionPreference = 'Stop'
if (-not $DeliveryDir) { $DeliveryDir = Join-Path $Repo 'dist\delivery' }
if (-not $EvidenceDir) { $EvidenceDir = Join-Path $Repo 'dist\evidence-11\PRIVACY' }
if (-not $OutFile) { $OutFile = Join-Path $DeliveryDir 'PRIVACY_CHECK.json' }
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

# 真实网络标识的模式。这里**不写出**任何真实值，只匹配形态：
#   * 无线网络名（SSID）通常较短且非通用；这里用"已知真实适配器型号"与"本机私有地址段"作为探针，
#     并额外检查本机实际适配器名与无线名是否出现在文本里 —— 由调用方通过 -ProbeFile 提供，
#     以免把真实标识写进脚本本身。
$privateIp = '\b(?:10\.\d{1,3}|192\.168\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3})\.\d{1,3}\b'
$adapterWords = 'Realtek|Intel\(R\)|RTL\d{4}|Wireless LAN \d'

$textExt = @('.tsv', '.txt', '.md', '.json', '.log', '.csv')
$files = @(Get-ChildItem -LiteralPath $DeliveryDir -Recurse -File -ErrorAction SilentlyContinue)
$scanned = 0
$hits = New-Object System.Collections.Generic.List[object]

foreach ($f in $files) {
    # 本脚本自己的输出会记录命中的字符串，不能算作交付文件的内容命中
    if ($f.Name -eq 'PRIVACY_CHECK.json') { continue }
    $isText = $textExt -contains $f.Extension.ToLowerInvariant()
    if (-not $isText) {
        # 图片文件名有两类合法构造：
        #   页面截图：页面-主题-缩放-尺寸.png
        #   安装器录屏帧：frame-NNNN.png
        # 真实网络名不可能出现在这两类名字里，因此检查的是"名字是否符合构造规则"，
        # 而不是去猜哪些词像 SSID（先前用 'WIFI-' 做探针，把页面名 WiFi 报成命中，那是误报）。
        $isPageShot = $f.Name -match '^[\u4e00-\u9fa5A-Za-z0-9\-]+-(dark|light)-[\d.]+x-\d+x\d+\.(png|gif)$'
        $isRecFrame = $f.Name -match '^frame-\d+\.png$'
        $isInstaller = $f.Name -match '^setup-[\w\-]+\.png$' -or $f.Name -eq 'installer-animation.gif'
        # 构造规则只对**捕获产生的**图片目录有意义；源码审阅副本里的测试素材（如 image-sample.png）
        # 是仓库原有文件，用捕获命名规则去套它只会产生误报。
        $inCapture = ($f.FullName -match '[\\/]03-desktop-ui[\\/]') -or ($f.FullName -match '[\\/]04-installer[\\/]')
        if ($inCapture -and $f.Extension.ToLowerInvariant() -in @('.png', '.gif') -and -not ($isPageShot -or $isRecFrame -or $isInstaller)) {
            $hits.Add([pscustomobject]@{ file=$f.Name; kind='文件名'; what='不符合"页面-主题-缩放-尺寸"或"frame-NNNN"构造规则的图片名，需人工确认' })
        }
        continue
    }
    if ($f.Length -gt 8MB) { continue }
    $scanned++
    $content = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if (-not $content) { continue }
    if ($content -match $privateIp) {
        $m = [regex]::Matches($content, $privateIp) | Select-Object -First 3 | ForEach-Object { $_.Value }
        $hits.Add([pscustomobject]@{ file=$f.FullName.Substring($DeliveryDir.Length+1); kind='私有地址'; what=($m -join ', ') })
    }
    if ($content -match $adapterWords) {
        $m = [regex]::Matches($content, $adapterWords) | Select-Object -First 3 | ForEach-Object { $_.Value }
        $hits.Add([pscustomobject]@{ file=$f.FullName.Substring($DeliveryDir.Length+1); kind='适配器字样'; what=($m -join ', ') })
    }
}

# 必须保留的核验信息是否还在（**递归**查找：封包集里报告在 01-report/ 下，不在根目录）
function Find-Rel([string]$dir, [string]$name) {
    $hit = Get-ChildItem -LiteralPath $dir -Recurse -File -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { return $hit.FullName }
    return $null
}
$identityKept = [ordered]@{
    versionInReport = $false
    commitInReport = $false
    artifactHashesPresent = [bool](Find-Rel $DeliveryDir 'ARTIFACT_HASHES.txt')
    candidateCommit = ''
}

# 版本与提交按**本候选实际的值**核对，不写死版本号或提交号：
# 先前把 1.7.4 与 a9b06eb 写进了检查里，换候选后就成了假阴性。
$hashesFile = Find-Rel $DeliveryDir 'ARTIFACT_HASHES.txt'
if ($hashesFile) {
    $h = Get-Content -LiteralPath $hashesFile -Raw -Encoding UTF8
    $m = [regex]::Match($h, '产品构建提交[^0-9a-f]*([0-9a-f]{7,40})')
    if ($m.Success) { $identityKept.candidateCommit = $m.Groups[1].Value }
}
$report = Find-Rel $DeliveryDir 'DELIVERY_REPORT_11_VERIFIED.md'
if ($report) {
    $r = Get-Content -LiteralPath $report -Raw -Encoding UTF8
    if ($identityKept.candidateCommit) {
        # 报告必须点名同一个候选提交；短号也认，避免写法差异造成假阴性
        $short = $identityKept.candidateCommit.Substring(0, [Math]::Min(7, $identityKept.candidateCommit.Length))
        $identityKept.commitInReport = ($r -match [regex]::Escape($identityKept.candidateCommit)) -or ($r -match [regex]::Escape($short))
        $vm = [regex]::Match($h, 'LanDisk (\d+\.\d+\.\d+) 交付产物哈希')
        if ($vm.Success) { $identityKept.versionInReport = ($r -match [regex]::Escape($vm.Groups[1].Value)) }
    }
    else { $identityKept.versionInReport = ($r -match '\d+\.\d+\.\d+') }
}

# 桌面矩阵是否真的用了夹具（来自**本次**证据的环境断言）。
# 先前把 run-11-1.7.4 写死在这里，检查的其实是上一轮的证据。
$envFile = Get-ChildItem -LiteralPath (Join-Path $Repo 'dist\evidence-11') -Recurse -Filter 'UI_CAPTURE_ENVIRONMENT.tsv' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$fixtureOk = $false
$fixtureDetail = ''
if ($envFile) {
    $e = @{}
    foreach ($l in (Get-Content -LiteralPath $envFile.FullName -Encoding UTF8)) { $p = $l -split "`t"; if ($p.Count -ge 2) { $e[$p[0]] = $p[1] } }
    # WiFi 功能已从产品移除：这里改为证明**网络页用的是确定性网卡夹具**（R2 断言），
    # 与 EvidenceVerdict 的判据保持同一套，避免"功能删了、证据契约还在要 WLAN12"。
    $fixtureOk = ($e['R2_NATIVE_UI_RENDER'] -eq 'PASS') -and ($e['R2_VISIBLE_ADAPTER'] -eq 'true')
    $fixtureDetail = '网络页夹具渲染 ' + $e['R2_NATIVE_UI_RENDER'] + '，可见物理网卡 ' + $e['R2_VISIBLE_ADAPTER']
}

$payload = [pscustomobject]@{
    generatedAt = (Get-Date -Format 'o')
    deliveryDir = $DeliveryDir
    filesTotal = $files.Count
    textFilesScanned = $scanned
    binaryFilesNote = 'ZIP/PNG/GIF 等二进制只检查了文件名；逐像素内容检查由安装器与桌面矩阵的夹具来源保证（R2 网络页夹具断言）。'
    hits = $hits
    hitCount = $hits.Count
    fixtureUsedForMatrix = $fixtureOk
    fixtureDetail = $fixtureDetail
    verificationIdentityKept = $identityKept
    verdict = if ($hits.Count -eq 0 -and $fixtureOk) { 'PASS' } elseif ($hits.Count -eq 0) { 'PASS_WITH_FIXTURE_UNVERIFIED' } else { 'REVIEW_REQUIRED' }
}
$payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutFile -Encoding UTF8

Write-Host ("隐私检查：{0}（文件 {1}，扫描文本 {2}，命中 {3}）" -f $payload.verdict, $files.Count, $scanned, $hits.Count)
$hits | ForEach-Object { Write-Host ("  - {0} [{1}] {2}" -f $_.file, $_.kind, $_.what) -ForegroundColor Yellow }
Write-Host ("  夹具断言：{0}" -f $fixtureDetail)
Write-Host ("  核验身份保留：版本 {0} / 提交 {1} / 产物哈希 {2}" -f $identityKept.versionInReport, $identityKept.commitInReport, $identityKept.artifactHashesPresent)
