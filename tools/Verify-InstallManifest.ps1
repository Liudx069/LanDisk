#requires -Version 7.0
<#
.SYNOPSIS
    核对发布产物的 install-manifest.json 是否完整记录了 build-info.json（任务 06）。

.DESCRIPTION
    这是本轮的硬门禁。它不看脚本自己的中间变量，而是直接打开最终产物：

      * dist/publish/…/install-manifest.json 与 build-info.json
      * dist/delivery/LanDisk-win-x64.zip（release）
      * dist/delivery/LanDisk-update-<version>.lupd（签名更新包）

    然后复算 build-info.json 的真实字节数与 SHA256，与清单里**记录**的值逐项比对：

      1) install-manifest.files 明确包含 "build-info.json"
      2) install-manifest.buildInfo.path == "build-info.json"
      3) 记录的 bytes   == release ZIP 内真实 build-info.json 的字节数
      4) 记录的 sha256  == release ZIP 内真实 build-info.json 的 SHA256
      5) .lupd payload 内的 build-info.json 与记录值完全一致
      6) .lupd 内**不**含 install-manifest.json（它必须继续留在包外）

    任一项失败即以非 0 退出，发布脚本据此中止，不生成正式候选。

    顶层结论字段：INSTALL_MANIFEST_CONTAINS_BUILD_INFO = PASS / FAIL。

.EXAMPLE
    pwsh -File tools/Verify-InstallManifest.ps1
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$PublishDir = '',
    [string]$RuntimeZip = '',
    [string]$UpdatePackage = '',
    [string]$DeliveryDir = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$Repo = (Resolve-Path -LiteralPath $Repo).Path
if (-not $PublishDir) { $PublishDir = Join-Path $Repo 'dist/publish/LanDisk-win-x64' }
if (-not $DeliveryDir) { $DeliveryDir = Join-Path $Repo 'dist/delivery' }
if (-not $RuntimeZip) { $RuntimeZip = Join-Path $DeliveryDir 'LanDisk-win-x64.zip' }
if (-not $UpdatePackage) {
    $found = @(Get-ChildItem -LiteralPath $DeliveryDir -Filter 'LanDisk-update-*.lupd' -File -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending)
    if ($found.Count -gt 0) { $UpdatePackage = $found[0].FullName }
}

$results = New-Object System.Collections.Generic.List[object]
function Add-Check([string]$id, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Id = $id; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail) -ForegroundColor $colour
}

function Get-ShaOfBytes([byte[]]$bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash($bytes)).ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-ShaOfFile([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Get-EntryBytes($zip, [string]$name) {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
    if (-not $entry) { return $null }
    $stream = $entry.Open()
    $memory = New-Object System.IO.MemoryStream
    try { $stream.CopyTo($memory); return $memory.ToArray() }
    finally { $memory.Dispose(); $stream.Dispose() }
}

Write-Host ''
Write-Host '=== 安装清单 / build-info 专项核对 ===' -ForegroundColor Cyan
Write-Host "publish: $PublishDir"
Write-Host "release: $RuntimeZip"
Write-Host "update : $(if ($UpdatePackage) { $UpdatePackage } else { '(缺失)' })"

$installManifestPath = Join-Path $PublishDir 'install-manifest.json'
$buildInfoPath = Join-Path $PublishDir 'build-info.json'

# ---------------------------------------------------------------- 发布目录
$hasManifest = Test-Path -LiteralPath $installManifestPath
$hasBuildInfo = Test-Path -LiteralPath $buildInfoPath
Add-Check 'IMCB-01' $hasBuildInfo '发布目录包含 build-info.json'
Add-Check 'IMCB-02' $hasManifest '发布目录包含 install-manifest.json'

$manifest = $null
if ($hasManifest) {
    try { $manifest = Get-Content -LiteralPath $installManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch {
        $manifest = $null
        Add-Check 'IMCB-02b' $false ("install-manifest.json 无法解析：{0}" -f $_.Exception.Message)
    }
}

$recordedPath = ''
$recordedBytes = $null
$recordedSha = ''
$filesList = @()
if ($manifest) {
    $filesList = @($manifest.files)
    if ($manifest.buildInfo) {
        $recordedPath = [string]$manifest.buildInfo.path
        $recordedBytes = $manifest.buildInfo.bytes
        $recordedSha = ([string]$manifest.buildInfo.sha256).ToLowerInvariant()
    }
}

# IMCB-03 是本轮的核心断言：files 清单必须明确列出 build-info.json。
$listsBuildInfo = $manifest -and ($filesList -contains 'build-info.json')
Add-Check 'IMCB-03' $listsBuildInfo `
    ("install-manifest.files 包含 build-info.json（共 {0} 个文件）" -f $filesList.Count)

# 保留元数据不能自己声明自己。
$listsItself = $manifest -and ($filesList -contains 'install-manifest.json')
Add-Check 'IMCB-04' (-not $listsItself) 'install-manifest.files 不包含 install-manifest.json（安装器保留元数据）'

Add-Check 'IMCB-05' ($recordedPath -eq 'build-info.json') `
    ("install-manifest.buildInfo.path == 'build-info.json'（实际 '{0}'）" -f $recordedPath)

# 真实值：以发布目录里的文件为准。
$realPublishBytes = $null
$realPublishSha = ''
if ($hasBuildInfo) {
    $realPublishBytes = (Get-Item -LiteralPath $buildInfoPath).Length
    $realPublishSha = Get-ShaOfFile $buildInfoPath
}

$bytesMatchPublish = ($null -ne $recordedBytes) -and ($recordedBytes -eq $realPublishBytes)
$shaMatchPublish = ($recordedSha -ne '') -and ($recordedSha -eq $realPublishSha)
Add-Check 'IMCB-06' $bytesMatchPublish `
    ("记录的 bytes = $recordedBytes，发布目录真实 bytes = $realPublishBytes")
Add-Check 'IMCB-07' $shaMatchPublish `
    ("记录的 sha256 = $recordedSha，发布目录真实 sha256 = $realPublishSha")

# 版本/提交号三处一致：清单、build-info.json、以及清单里记录的提交。
$versionAgrees = $false
$commitAgrees = $false
if ($hasBuildInfo -and $manifest) {
    try {
        $buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $versionAgrees = ([string]$manifest.version -eq [string]$buildInfo.version)
        $commitAgrees = ([string]$manifest.buildCommit -eq [string]$buildInfo.commit)
    }
    catch { }
}
Add-Check 'IMCB-08' $versionAgrees 'install-manifest.version 与 build-info.json.version 一致'
Add-Check 'IMCB-09' $commitAgrees 'install-manifest.buildCommit 与 build-info.json.commit 一致'

# ---------------------------------------------------------------- release ZIP
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipBytes = $null
$zipSha = ''
$zipManifestAgrees = $false
$zipManifestFilesOk = $false
if (-not (Test-Path -LiteralPath $RuntimeZip)) {
    Add-Check 'IMCB-10' $false "找不到 release ZIP：$RuntimeZip"
}
else {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($RuntimeZip)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
        $prefix = ''
        $first = @($names | Where-Object { $_ -match '/' })[0]
        if ($first) { $prefix = ($first -split '/')[0] }

        $entryName = if ($prefix) { "$prefix/build-info.json" } else { 'build-info.json' }
        $entryBytes = Get-EntryBytes $zip $entryName
        $zipHasBuildInfo = $null -ne $entryBytes

        if ($zipHasBuildInfo) {
            $zipSha = Get-ShaOfBytes $entryBytes
            $zipSize = $entryBytes.Length
            $bytesMatchZip = ($null -ne $recordedBytes) -and ($recordedBytes -eq $zipSize)
            $shaMatchZip = ($recordedSha -ne '') -and ($recordedSha -eq $zipSha)
            Add-Check 'IMCB-10' $bytesMatchZip `
                ("记录的 bytes = $recordedBytes，release ZIP 内真实 bytes = $zipSize")
            Add-Check 'IMCB-11' $shaMatchZip `
                ("记录的 sha256 = $recordedSha，release ZIP 内真实 sha256 = $zipSha")
        }
        else {
            Add-Check 'IMCB-10' $false "release ZIP 内缺少 $entryName"
            Add-Check 'IMCB-11' $false 'release ZIP 内缺少 build-info.json，无法比对 SHA256'
        }

        # release ZIP 里的 install-manifest.json 必须与发布目录里的逐字节一致。
        $manifestEntryName = if ($prefix) { "$prefix/install-manifest.json" } else { 'install-manifest.json' }
        $manifestEntryBytes = Get-EntryBytes $zip $manifestEntryName
        if ($null -ne $manifestEntryBytes -and $hasManifest) {
            $zipManifestSha = Get-ShaOfBytes $manifestEntryBytes
            $publishManifestSha = Get-ShaOfFile $installManifestPath
            $zipManifestAgrees = ($zipManifestSha -eq $publishManifestSha)
            try {
                $zipManifest = [System.Text.Encoding]::UTF8.GetString($manifestEntryBytes) | ConvertFrom-Json
                $zipManifestFilesOk = (@($zipManifest.files) -contains 'build-info.json')
            }
            catch { $zipManifestFilesOk = $false }
        }
        Add-Check 'IMCB-12' $zipManifestAgrees `
            ("release ZIP 内的 install-manifest.json 与发布目录逐字节一致（$manifestEntryName）")
    }
    finally { $zip.Dispose() }
}

# ---------------------------------------------------------------- .lupd
$lupdHasBuildInfo = $false
$lupdHasManifest = $true
$lupdBytesMatch = $false
$lupdShaMatch = $false
$lupdChecksRan = $false
if (-not $UpdatePackage -or -not (Test-Path -LiteralPath $UpdatePackage)) {
    Add-Check 'IMCB-13' $false "没有可核对的签名更新包（$UpdatePackage）"
    Add-Check 'IMCB-14' $false '没有可核对的签名更新包，无法断言 payload 内容'
    Add-Check 'IMCB-15' $false '没有可核对的签名更新包，无法断言 install-manifest.json 未进入 payload'
}
else {
    $lupdChecksRan = $true
    $lupd = [System.IO.Compression.ZipFile]::OpenRead($UpdatePackage)
    try {
        $lupdNames = @($lupd.Entries | ForEach-Object { $_.FullName })
        $payloadEntry = @($lupdNames | Where-Object { $_ -like '*build-info.json' })[0]
        $lupdHasBuildInfo = $null -ne $payloadEntry
        $lupdHasManifest = (@($lupdNames | Where-Object { $_ -like '*install-manifest.json' }).Count -gt 0)

        if ($lupdHasBuildInfo) {
            $lupdEntryBytes = Get-EntryBytes $lupd $payloadEntry
            $lupdSha = Get-ShaOfBytes $lupdEntryBytes
            $lupdBytesMatch = ($null -ne $recordedBytes) -and ($recordedBytes -eq $lupdEntryBytes.Length)
            $lupdShaMatch = ($recordedSha -ne '') -and ($recordedSha -eq $lupdSha)
            Add-Check 'IMCB-13' $lupdBytesMatch `
                ("记录的 bytes = $recordedBytes，.lupd payload 内 $payloadEntry 的 bytes = $($lupdEntryBytes.Length)")
            Add-Check 'IMCB-14' $lupdShaMatch `
                ("记录的 sha256 = $recordedSha，.lupd payload 内真实 sha256 = $lupdSha")
        }
        else {
            Add-Check 'IMCB-13' $false '.lupd payload 内缺少 build-info.json'
            Add-Check 'IMCB-14' $false '.lupd payload 内缺少 build-info.json，无法比对 SHA256'
        }

        Add-Check 'IMCB-15' (-not $lupdHasManifest) `
            ("签名更新包内不含 install-manifest.json（匹配到 {0} 个）" -f @($lupdNames | Where-Object { $_ -like '*install-manifest.json' }).Count)
    }
    finally { $lupd.Dispose() }
}

# ---------------------------------------------------------------- 结论
$failed = @($results | Where-Object { $_.Result -eq 'FAIL' })
$passed = @($results | Where-Object { $_.Result -eq 'PASS' })
$byId = @{}
foreach ($row in $results) { $byId[$row.Id] = [string]$row.Result }
function Verdict-Of([string[]]$ids) {
    foreach ($id in $ids) { if ($byId[$id] -ne 'PASS') { return 'FAIL' } }
    return 'PASS'
}

# 命名断言的语义：清单里必须有它、路径必须对、记录的 bytes/sha256 必须与
# release ZIP 及 .lupd payload 内的真实文件完全一致。
$headlineIds = @('IMCB-03', 'IMCB-05', 'IMCB-06', 'IMCB-07', 'IMCB-10', 'IMCB-11', 'IMCB-13', 'IMCB-14')
$headline = Verdict-Of $headlineIds
$overall = if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }

# 单值结论先算成普通字符串，取值语句不写进有序哈希字面量，便于逐条核对。
$bytesVerdict = Verdict-Of @('IMCB-06', 'IMCB-10')
$shaVerdict = Verdict-Of @('IMCB-07', 'IMCB-11')
$updatePayloadBuildInfoVerdict = Verdict-Of @('IMCB-13', 'IMCB-14')

$releaseZipText = $RuntimeZip
$releaseZipSha = if (Test-Path -LiteralPath $RuntimeZip) { Get-ShaOfFile $RuntimeZip } else { '' }
$updatePackageText = $UpdatePackage

Write-Host ''
Write-Host ("INSTALL_MANIFEST_CONTAINS_BUILD_INFO = {0}" -f $headline) `
    -ForegroundColor $(if ($headline -eq 'PASS') { 'Green' } else { 'Red' })

$reportDir = $DeliveryDir
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$reportPath = Join-Path $reportDir 'INSTALL_MANIFEST_VERIFY.json'

$reportObject = [ordered]@{
    generatedAtUtc                     = [DateTimeOffset]::UtcNow.ToString('o')
    repo                               = $Repo
    publishDir                         = $PublishDir
    releaseZip                         = $releaseZipText
    releaseZipSha256                   = $releaseZipSha
    updatePackage                      = $updatePackageText
    installManifestContainsBuildInfo   = $headline
    buildInfoRecordedPath              = $recordedPath
    buildInfoRecordedBytes             = $recordedBytes
    buildInfoRecordedSha256            = $recordedSha
    buildInfoRealBytes                 = $realPublishBytes
    buildInfoRealSha256                = $realPublishSha
    buildInfoBytesMatch                = $bytesVerdict
    buildInfoSha256Match               = $shaVerdict
    updatePackageChecksRan             = $lupdChecksRan
    updatePayloadContainsBuildInfo     = $lupdHasBuildInfo
    updatePayloadHasInstallManifest    = $lupdHasManifest
    updatePayloadBuildInfoDigestMatch  = $updatePayloadBuildInfoVerdict
    installManifestFileCount           = $filesList.Count
    installManifestListsBuildInfo      = $listsBuildInfo
    installManifestListsItself         = $listsItself
    releaseZipManifestMatchesPublish   = $zipManifestAgrees
    releaseZipManifestListsBuildInfo   = $zipManifestFilesOk
    checksPassed                       = $passed.Count
    checksFailed                       = $failed.Count

    # 注意：这里必须是 [object[]] 而不是 @($results)。$results 是 List[object]，
    # PowerShell 7 的 PSToObjectArrayBinder.MaybeDebase 对它生成的条件表达式会抛
    # "Argument types do not match"（List<object> 与 object[] 无法放进同一个 Condition）。
    checks                             = [object[]]$results
}

try {
    $json = ConvertTo-Json -InputObject $reportObject -Depth 6
    Set-Content -LiteralPath $reportPath -Value $json -Encoding UTF8
}
catch {
    Write-Host ("写入安装清单核对报告失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
    Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkGray
}

$summary = '安装清单核对：通过 {0} / 失败 {1}' -f $passed.Count, $failed.Count
if ($failed.Count -eq 0) { Write-Host $summary -ForegroundColor Green } else { Write-Host $summary -ForegroundColor Red }
Write-Host "证据文件: $reportPath"

if ($overall -ne 'PASS') { exit 1 }
exit 0
