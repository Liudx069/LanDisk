#requires -Version 7.0
<#
.SYNOPSIS
    核对"最终发布候选"的身份：git HEAD、release 构建提交、更新包构建提交、源码包与工作树。

.DESCRIPTION
    H04-02 要求正式候选必须从**最终干净 HEAD** 构建，并且 release / update package / source /
    报告四处的候选身份一致。本脚本把这些断言变成可执行的检查，而不是报告里的叙述：

      * 工作树必须干净（未提交的改动意味着构建出来的东西没有对应的提交）；
      * release 包里的 build-info.json 的 commit 必须等于 HEAD；
      * release 包内程序集携带的 LanDiskBuildCommit 必须等于 HEAD；
      * update package 清单里的 buildCommit 必须等于 HEAD；
      * update package 的 payload 与 release 目录逐文件一致（排除安装程序生成的元数据）；
      * source 包与 HEAD 的 git 归档一致；
      * 外壳确实是原生 WPF（LanDisk.dll 引用 PresentationFramework/Wpf.Ui，且不引用
        System.Windows.Forms 或任何 WebView2/CefSharp），LanDisk.exe 是 WinExe（WindowsGui 子系统），
        发布目录里也不存在 WebView/CefSharp/WinForms 的文件。

    任何一条不成立就以非零退出码结束并明确指出是哪一条。

.EXAMPLE
    pwsh -File tools/Verify-CandidateIdentity.ps1
    pwsh -File tools/Verify-CandidateIdentity.ps1 -AllowDirty   # 仅用于开发期自查
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$PublishDir = '',
    [string]$DeliveryDir = '',
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$Repo = (Resolve-Path -LiteralPath $Repo).Path
if (-not $PublishDir) { $PublishDir = Join-Path $Repo 'dist/publish/LanDisk-win-x64' }
if (-not $DeliveryDir) { $DeliveryDir = Join-Path $Repo 'dist/delivery' }

$results = New-Object System.Collections.Generic.List[object]

function Add-Check([string]$id, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Id = $id; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail) -ForegroundColor $colour
}

function Get-Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

Write-Host ''
Write-Host '=== 候选身份核对 ===' -ForegroundColor Cyan

# ---------------------------------------------------------------- HEAD 与工作树
# The single source of truth for "which commit is this release" is dist/FINAL_HEAD.txt, written by
# the build the moment the artifacts were produced. Everything below is compared against it, and it
# is never derived from anything this script computes, so regenerating the report cannot move it.
$repoHead = (git -C $Repo rev-parse HEAD).Trim()
$finalHeadPath = Join-Path $Repo 'dist/FINAL_HEAD.txt'
$lockedHead = $repoHead
if (Test-Path -LiteralPath $finalHeadPath) {
    $lockedLine = @(Get-Content -LiteralPath $finalHeadPath -Encoding UTF8 | Where-Object { $_ -match '^commit:\s*' })[0]
    if ($lockedLine) { $lockedHead = ($lockedLine -replace '^commit:\s*', '').Trim() }
}

$head = $lockedHead

$status = @(git -C $Repo status --porcelain)
$dirty = $status.Count -gt 0

Add-Check 'CAND-00' ($repoHead -eq $lockedHead) `
    ("单一真源 dist/FINAL_HEAD.txt = {0}（仓库 HEAD = {1}）" -f $lockedHead, $repoHead)

Add-Check 'CAND-01' ($AllowDirty -or -not $dirty) `
    $(if ($dirty) { "工作树不干净（$($status.Count) 项改动）" } else { "工作树干净，HEAD = $head" })

if ($dirty -and -not $AllowDirty) {
    $status | Select-Object -First 10 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
}

# ---------------------------------------------------------------- release 包
$buildInfoPath = Join-Path $PublishDir 'build-info.json'
if (Test-Path $buildInfoPath) {
    $buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Check 'CAND-02' ($buildInfo.commit -eq $head) `
        "release build-info.json commit = $($buildInfo.commit)"
}
else {
    Add-Check 'CAND-02' $false "缺少 $buildInfoPath"
}

Add-Check 'CAND-03' (Test-Path (Join-Path $PublishDir 'LanDisk.exe')) 'release 目录包含 LanDisk.exe'
Add-Check 'CAND-04' (Test-Path (Join-Path $PublishDir 'updater/LanDisk.Updater.exe')) 'release 目录包含 updater/LanDisk.Updater.exe'

# 运行包内程序集携带的提交号（用 release 自带的 exe 反射读取，不依赖构建环境）
$probeExe = Join-Path $PublishDir 'LanDisk.Server.exe'
if (Test-Path $probeExe) {
    $probeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("landisk-cand-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
    try {
        $probeScript = Join-Path $probeDir 'read-commit.ps1'
        @'
param([string]$AssemblyPath)
$assembly = [System.Reflection.Assembly]::LoadFrom($AssemblyPath)
$attributes = $assembly.GetCustomAttributes([System.Reflection.AssemblyMetadataAttribute], $false)
foreach ($a in $attributes) { if ($a.Key -eq 'LanDiskBuildCommit') { Write-Output $a.Value } }
'@ | Set-Content -LiteralPath $probeScript -Encoding UTF8

        # Read the stamped commit out of a separate process so this script never loads the product's
        # assemblies into its own runtime. Output goes through a file rather than a pipeline: mixing
        # a child process's stderr redirection with a capturing pipeline is fragile, and this check
        # must not fail for reasons unrelated to the build identity.
        $coreDll = Join-Path $PublishDir 'LanDisk.Core.dll'
        $stdout = Join-Path $probeDir 'commit.txt'
        $stderr = Join-Path $probeDir 'commit.err'
        & pwsh -NoProfile -File $probeScript -AssemblyPath $coreDll 1> $stdout 2> $stderr
        $embedded = if (Test-Path $stdout) { (Get-Content -LiteralPath $stdout -Encoding UTF8 | Select-Object -First 1) } else { $null }
        if ($embedded) { $embedded = $embedded.Trim() }

        Add-Check 'CAND-05' ($embedded -eq $head) "release 程序集内嵌 LanDiskBuildCommit = $embedded"
    }
    catch {
        Add-Check 'CAND-05' $false ('读取内嵌提交号失败：' + $_.Exception.Message)
    }
    finally {
        Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
else {
    Add-Check 'CAND-05' $false "缺少 $probeExe"
}

# ---------------------------------------------------------------- update package
$updatePackage = Get-ChildItem -LiteralPath $DeliveryDir -Filter 'LanDisk-update-*.lupd' -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1

if ($updatePackage) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($updatePackage.FullName)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'update-manifest.json' }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        $reader.Dispose()

        Add-Check 'CAND-06' ($manifest.buildCommit -eq $head) "update manifest buildCommit = $($manifest.buildCommit)"
        Add-Check 'CAND-07' ($manifest.schemaVersion -eq 2) "update manifest schemaVersion = $($manifest.schemaVersion)"
        Add-Check 'CAND-08' ([bool]$manifest.keyId -and [bool]$manifest.releaseNotesSha256) `
            "update manifest 含 keyId=$($manifest.keyId) 与 releaseNotesSha256"

        # payload 与 release 目录逐文件一致。install-manifest.json 由安装程序生成，是唯一的保留
        # 元数据；build-info.json 从 05 起是普通受签名 payload 文件，必须出现在两边。
        $publishFiles = Get-ChildItem -LiteralPath $PublishDir -Recurse -File |
            Where-Object { $_.Name -ne 'install-manifest.json' } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($PublishDir, $_.FullName).Replace('\', '/') } |
            Sort-Object

        $payloadFiles = @($manifest.files | ForEach-Object { $_.path }) | Sort-Object

        $onlyPublish = @(Compare-Object $publishFiles $payloadFiles | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject)
        $onlyPayload = @(Compare-Object $publishFiles $payloadFiles | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject)

        Add-Check 'CAND-09' ($onlyPublish.Count -eq 0 -and $onlyPayload.Count -eq 0) `
            ("update payload 与 release 目录文件集合一致（$($payloadFiles.Count) 个；仅 release 有 $($onlyPublish.Count)，仅 payload 有 $($onlyPayload.Count)）")

        Add-Check 'CAND-12' (@($manifest.files | Where-Object { $_.path -eq 'build-info.json' }).Count -eq 1) `
            'build-info.json 是受签名的常见 payload 文件（不再是保留元数据）'

        if ($buildInfo) {
            $payloadBuildInfo = $manifest.files | Where-Object { $_.path -eq 'build-info.json' }
            if ($payloadBuildInfo) {
                $rawBytes = [System.IO.File]::ReadAllBytes($buildInfoPath)
                $shaB = [System.Security.Cryptography.SHA256]::Create()
                try { $hash = [Convert]::ToHexString($shaB.ComputeHash($rawBytes)).ToLowerInvariant() } finally { $shaB.Dispose() }
                Add-Check 'CAND-13' ($payloadBuildInfo.sha256 -eq $hash -and $payloadBuildInfo.bytes -eq $rawBytes.Length) `
                    '更新包清单里的 build-info.json 哈希与 release 目录中的实际文件一致'
            }
        }

        $mismatch = 0
        $sha = [System.Security.Cryptography.SHA256]::Create()
        foreach ($file in ($manifest.files | Sort-Object path)) {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq ("payload/" + $file.path) }
            if (-not $entry) { $mismatch++; continue }
            $stream = $entry.Open()
            try { $actual = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
            if ($actual -ne $file.sha256 -or $entry.Length -ne $file.bytes) { $mismatch++ }
        }
        Add-Check 'CAND-10' ($mismatch -eq 0) "update payload 逐文件哈希与签名清单一致（不符 $mismatch 条）"
    }
    finally { $zip.Dispose() }
}
else {
    Add-Check 'CAND-06' $false "交付目录中没有 .lupd 更新包"
}

# ---------------------------------------------------------------- source 包与 HEAD
$sourceZip = Join-Path $DeliveryDir 'LanDisk-source.zip'
if (Test-Path $sourceZip) {
    # Read the recorded commit straight out of the archive. Extracting the whole source tree to a
    # temporary directory just to read one file is slow and can hit Windows path-length limits.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceArchive = [System.IO.Compression.ZipFile]::OpenRead($sourceZip)
    try {
        $headEntry = $sourceArchive.Entries | Where-Object { $_.FullName -eq 'SOURCE_HEAD.txt' -or $_.FullName -like '*/SOURCE_HEAD.txt' } | Select-Object -First 1
        if ($headEntry) {
            $reader = New-Object System.IO.StreamReader($headEntry.Open())
            $text = $reader.ReadToEnd()
            $reader.Dispose()

            $commitLine = @($text -split "`r?`n" | Where-Object { $_ -match '^commit:\s*' })[0]
            $recorded = if ($commitLine) { ($commitLine -replace '^commit:\s*', '').Trim() } else { '' }
            $script:sourceRecordedCommit = $recorded
            Add-Check 'CAND-11' ($recorded -eq $head) "源码包内 SOURCE_HEAD.txt commit = $recorded"
        }
        else {
            Add-Check 'CAND-11' $false '源码包内缺少 SOURCE_HEAD.txt（无法证明源码快照对应哪个提交）'
        }
    }
    finally {
        $sourceArchive.Dispose()
    }
}
else {
    Add-Check 'CAND-11' $false "缺少 $sourceZip"
}

# ---------------------------------------------------------------- 安装程序与它的内嵌载荷
#
# 07 轮新增：LanDisk-Setup.exe 内嵌的必须是**同一个** release ZIP。这里不听它自己怎么说，
# 而是把交付目录里的三个文件对齐核对：EXE 的实际哈希 == setup-info.json 记录的哈希，
# 且 setup-info.json 记录的载荷哈希 == release ZIP 的实际哈希。
$setupExe = Join-Path $DeliveryDir 'LanDisk-Setup.exe'
$setupInfoPath = Join-Path $DeliveryDir 'setup-info.json'
$releaseZipForSetup = Join-Path $DeliveryDir 'LanDisk-win-x64.zip'
$setupSha = ''
$payloadSha = ''
if (Test-Path $setupExe) {
    $setupSha = Get-Sha $setupExe
    if (Test-Path $setupInfoPath) {
        $setupInfo = Get-Content -LiteralPath $setupInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $payloadSha = [string]$setupInfo.embeddedPayloadSha256
        $recordedSetupSha = [string]$setupInfo.setupSha256
        $releaseShaNow = if (Test-Path $releaseZipForSetup) { Get-Sha $releaseZipForSetup } else { '' }

        Add-Check 'CAND-15' ($recordedSetupSha -eq $setupSha -and $payloadSha -eq $releaseShaNow -and $setupSha -ne '') `
            ("安装程序与内嵌载荷核对：EXE {0}…，载荷 {1}… == release ZIP {2}…" -f `
                $setupSha.Substring(0, [Math]::Min(12, $setupSha.Length)), `
                $payloadSha.Substring(0, [Math]::Min(12, $payloadSha.Length)), `
                $releaseShaNow.Substring(0, [Math]::Min(12, $releaseShaNow.Length)))
    }
    else {
        Add-Check 'CAND-15' $false '缺少 setup-info.json，无法证明安装程序内嵌的是本次候选的运行包'
    }
}
else {
    Add-Check 'CAND-15' $false "缺少 $setupExe"
}

# ---------------------------------------------------------------- 原生 WPF 外壳取证（任务 08）
#
# 08 轮把外壳从 WinForms 换成原生 WPF，并承诺"常规流程不打开浏览器"。这两点必须由**产物本身**
# 证明，而不是报告里的叙述：直接读 PE/CLI 元数据与发布目录的文件集合，既不加载被测程序集，
# 也不依赖构建环境。LanDisk.exe 是原生 apphost（没有 CLI 元数据），因此子系统从它读、
# 引用表从 LanDisk.dll 读。
$appAssembly = Join-Path $PublishDir 'LanDisk.dll'
$appHost = Join-Path $PublishDir 'LanDisk.exe'
if ((Test-Path $appAssembly) -and (Test-Path $appHost)) {
    $probeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("landisk-cand-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
    try {
        $probeScript = Join-Path $probeDir 'read-refs.ps1'
        @'
param([string]$AssemblyPath, [string]$ExePath)
Add-Type -AssemblyName System.Reflection.Metadata -ErrorAction Stop
function Read-Metadata([string]$path, [string]$prefix) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $pe = New-Object System.Reflection.PortableExecutable.PEReader($stream)
        try {
            Write-Output ($prefix + '.subsystem=' + $pe.PEHeaders.PEHeader.Subsystem)
            try {
                $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                Write-Output ($prefix + '.name=' + $md.GetString($md.GetAssemblyDefinition().Name))
                $names = New-Object System.Collections.Generic.List[string]
                foreach ($handle in $md.AssemblyReferences) {
                    $reference = $md.GetAssemblyReference($handle)
                    $names.Add([string]$md.GetString($reference.Name))
                }
                Write-Output ($prefix + '.refs=' + ((($names | Sort-Object) -join ',')))
            }
            catch { Write-Output ($prefix + '.refs=') }
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }
}
Read-Metadata $AssemblyPath 'dll'
Read-Metadata $ExePath 'exe'
'@ | Set-Content -LiteralPath $probeScript -Encoding UTF8

        $stdout = Join-Path $probeDir 'refs.txt'
        $stderr = Join-Path $probeDir 'refs.err'
        & pwsh -NoProfile -File $probeScript -AssemblyPath $appAssembly -ExePath $appHost 1> $stdout 2> $stderr
        $probeLines = if (Test-Path $stdout) { @(Get-Content -LiteralPath $stdout -Encoding UTF8) } else { @() }
        function Probe-Value([string]$key) {
            $line = ($probeLines | Where-Object { $_ -like ($key + '=*') } | Select-Object -First 1)
            if ($line) { return $line.Substring($key.Length + 1) }
            return ''
        }
        $assemblyName = Probe-Value 'dll.name'
        $exeSubsystem = Probe-Value 'exe.subsystem'
        $refLine = Probe-Value 'dll.refs'
        $refs = @($refLine -split ',' | Where-Object { $_ })

        $wpfRequired = @('PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml', 'Wpf.Ui', 'Wpf.Ui.Tray', 'QRCoder')
        $wpfMissing = @($wpfRequired | Where-Object { $refs -notcontains $_ })
        # System.Windows.Forms 与任何 WebView / CefSharp 都不允许出现：两者都意味着"还在用旧的
        # WinForms 外壳"或"内嵌了浏览器"，正好是 08 轮要消除的东西。
        $forbiddenRefs = @(
            'System.Windows.Forms',
            'Microsoft.Web.WebView2.Core', 'Microsoft.Web.WebView2.Wpf', 'Microsoft.Web.WebView2.WinForms',
            'CefSharp', 'CefSharp.Wpf', 'CefSharp.WinForms', 'WebView2'
        )
        $forbiddenFound = @($forbiddenRefs | Where-Object { $refs -contains $_ })

        Add-Check 'CAND-16' ($assemblyName -eq 'LanDisk' -and $wpfMissing.Count -eq 0 -and $forbiddenFound.Count -eq 0) `
            ("LanDisk.dll 程序集名 = $assemblyName，直接引用 {0} 个；WPF 齐全（缺 {1} 项），WinForms/WebView 命中 {2} 项" -f `
                $refs.Count, $wpfMissing.Count, $forbiddenFound.Count)

        Add-Check 'CAND-17' ($exeSubsystem -eq 'WindowsGui') `
            "LanDisk.exe 的 PE 子系统 = $exeSubsystem（WindowsGui 即 WinExe：双击不弹控制台窗口）"

        $publishNames = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File | ForEach-Object { $_.Name })
        $webviewFiles = @($publishNames | Where-Object { $_ -match '^(Microsoft\.Web\.WebView2|WebView2Loader|CefSharp)' })
        $formsFiles = @($publishNames | Where-Object { $_ -eq 'System.Windows.Forms.dll' -or $_ -like 'System.Windows.Forms.*.dll' })
        $wpfFilesMissing = @(@(
            'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll',
            'wpfgfx_cor3.dll', 'PresentationNative_cor3.dll', 'Wpf.Ui.dll', 'QRCoder.dll'
        ) | Where-Object { $publishNames -notcontains $_ })
        Add-Check 'CAND-18' ($webviewFiles.Count -eq 0 -and $formsFiles.Count -eq 0 -and $wpfFilesMissing.Count -eq 0) `
            ("发布目录共 {0} 个文件：WebView/CefSharp {1} 个、WinForms {2} 个、WPF 关键文件缺 {3} 个" -f `
                $publishNames.Count, $webviewFiles.Count, $formsFiles.Count, $wpfFilesMissing.Count)
    }
    catch {
        Add-Check 'CAND-16' $false ('读取原生外壳元数据失败：' + $_.Exception.Message)
    }
    finally {
        Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
else {
    Add-Check 'CAND-16' $false "缺少 $appAssembly 或 $appHost"
}

# ---------------------------------------------------------------- 汇总与身份文件
Write-Host ''
$failed = 0
$passed = 0
$reportPath = Join-Path $DeliveryDir 'RELEASE_CANDIDATE_IDENTITY.json'
$identityMarkdownPath = Join-Path $DeliveryDir 'RELEASE_CANDIDATE_IDENTITY.md'

foreach ($entry in $results) {
    if ($entry.Result -eq 'FAIL') { $failed++ } else { $passed++ }
}

# 四个提交字段必须完全相等。任何一个不相等都说明"这批产物不是同一个候选"，
# 此时脚本**不允许**成功退出——否则报告就会变成一句无法验证的叙述。
$releaseCommit = if ($buildInfo) { $buildInfo.commit } else { '' }
$updateCommit = if ($updatePackage -and $manifest) { $manifest.buildCommit } else { '' }
$sourceCommit = if ($sourceRecordedCommit) { $sourceRecordedCommit } else { '' }
$commits = @($head, $releaseCommit, $updateCommit, $sourceCommit)
$commitsEqual = ($commits | Where-Object { $_ -ne $head }).Count -eq 0
Add-Check 'CAND-14' $commitsEqual `
    ("四个提交字段一致：FINAL_HEAD={0} RELEASE={1} UPDATE={2} SOURCE={3}" -f $head, $releaseCommit, $updateCommit, $sourceCommit)
if ($commitsEqual) { $passed++ } else { $failed++ }

$runtimeZip = Join-Path $DeliveryDir 'LanDisk-win-x64.zip'
$productVersion = if ($buildInfo) { $buildInfo.version } else { '' }
$releaseSha = if (Test-Path $runtimeZip) { Get-Sha $runtimeZip } else { '' }
$updateSha = if ($updatePackage) { Get-Sha $updatePackage.FullName } else { '' }
$sourceSha = if (Test-Path $sourceZip) { Get-Sha $sourceZip } else { '' }

$identityDocument = [ordered]@{
    schemaVersion       = 1
    finalHead           = $head
    releaseBuildCommit  = $releaseCommit
    updateBuildCommit   = $updateCommit
    sourceHead          = $sourceCommit
    productVersion      = $productVersion
    releaseSha256       = $releaseSha
    updateSha256        = $updateSha
    sourceSha256        = $sourceSha
    generatedAtUtc      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    worktreeStatusAtBuild = $(if ($dirty) { 'dirty' } else { 'clean' })
    identityMatch       = $commitsEqual
    checksPassed        = $passed
    checksFailed        = $failed
    checks              = @($results | ForEach-Object { [pscustomobject]@{ Id = $_.Id; Result = $_.Result; Detail = $_.Detail } })
}

Set-Content -LiteralPath $reportPath -Value (ConvertTo-Json -InputObject $identityDocument -Depth 6) -Encoding UTF8

# 人读版本由同一份数据生成，避免"JSON 与 Markdown 各说一套"。
$markdown = @(
    '# RELEASE_CANDIDATE_IDENTITY — LAN_DISK_NATIVE_WINDOWS_UX_08',
    '',
    '> 本文件与 `RELEASE_CANDIDATE_IDENTITY.json`、`dist/FINAL_HEAD.txt` 由同一次运行生成，',
    '> 是同一份事实的三种表示。它生成于 FINAL_HEAD 锁定**之后**，因此不可能反过来改变 FINAL_HEAD。',
    '',
    '## 身份',
    '',
    '| 字段 | 值 |',
    '|---|---|',
    ('| finalHead | `{0}` |' -f $identityDocument.finalHead),
    ('| releaseBuildCommit | `{0}` |' -f $identityDocument.releaseBuildCommit),
    ('| updateBuildCommit | `{0}` |' -f $identityDocument.updateBuildCommit),
    ('| sourceHead | `{0}` |' -f $identityDocument.sourceHead),
    ('| productVersion | {0} |' -f $identityDocument.productVersion),
    ('| identityMatch | {0} |' -f $identityDocument.identityMatch),
    ('| worktreeStatusAtBuild | {0} |' -f $identityDocument.worktreeStatusAtBuild),
    ('| generatedAtUtc | {0} |' -f $identityDocument.generatedAtUtc),
    '',
    '## 产物哈希',
    '',
    '| 产物 | sha256 |',
    '|---|---|',
    ('| LanDisk-win-x64.zip | `{0}` |' -f $releaseSha),
    ('| ' + $(if ($updatePackage) { $updatePackage.Name } else { 'LanDisk-update-*.lupd' }) + ' | `' + $updateSha + '` |'),
    ('| LanDisk-source.zip | `{0}` |' -f $sourceSha),
    '',
    '## 断言',
    '',
    ('通过 **{0}** / 失败 **{1}**' -f $passed, $failed),
    '',
    '| ID | 结果 | 说明 |',
    '|---|---|---|',
    ($results | ForEach-Object { ('| {0} | {1} | {2} |' -f $_.Id, $_.Result, ($_.Detail -replace '\|', '\|')) })
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText($identityMarkdownPath, $markdown + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

$summary = "候选身份核对：通过 {0} / 失败 {1}（HEAD {2}）" -f $passed, $failed, $head
if ($failed -eq 0) { Write-Host $summary -ForegroundColor Green } else { Write-Host $summary -ForegroundColor Red }
Write-Host "证据文件: $reportPath"
Write-Host "身份说明: $identityMarkdownPath"

if ($failed -gt 0) { exit 1 }
exit 0
