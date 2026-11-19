#requires -Version 7.0
<#
.SYNOPSIS
    构建 LanDisk 的全部交付产物：前端、测试、Windows 自包含发布包、源码包与 SHA256 清单。

.DESCRIPTION
    脚本只写入本仓库内的 src/、tests/、dist/ 目录，不会修改系统设置、防火墙、证书信任或开机启动项。

.EXAMPLE
    pwsh -File tools/Build-All.ps1
    pwsh -File tools/Build-All.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipWeb,
    [switch]$SkipUpdatePackage,
    [switch]$SkipDirtyCheck,
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Version = '',
    [string]$MinimumFromVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repoRoot 'src/LanDisk.Web'
$serverProject = Join-Path $repoRoot 'src/LanDisk.Server/LanDisk.Server.csproj'
$appProject = Join-Path $repoRoot 'src/LanDisk.App/LanDisk.App.csproj'
$updaterProject = Join-Path $repoRoot 'src/LanDisk.Updater/LanDisk.Updater.csproj'
$extensionHostProject = Join-Path $repoRoot 'src/LanDisk.ExtensionHost/LanDisk.ExtensionHost.csproj'
$publishDir = Join-Path $repoRoot "dist/publish/LanDisk-$RuntimeIdentifier"
$deliveryDir = Join-Path $repoRoot 'dist/delivery'
$stageDir = Join-Path $repoRoot 'dist/.stage'

$Commit = ''
try {
    $Commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $Commit = '' }
} catch { $Commit = '' }
if ($Commit) { $Commit = $Commit.Trim() }
$BuildId = "$Configuration/$RuntimeIdentifier"

# The product version lives in the project files; a hard-coded default here drifted to 1.7.6 while the
# product was already 1.8.0, so a plain `Build-All.ps1` would have produced an artifact that lies about
# its version. Read the version from LanDisk.Core.csproj unless the caller overrides it explicitly.
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$coreProject = Get-Content (Join-Path $repoRoot 'src/LanDisk.Core/LanDisk.Core.csproj')
    $Version = @($coreProject.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw '无法从 src/LanDisk.Core/LanDisk.Core.csproj 读取 <Version>；请用 -Version 显式指定。'
    }
}

$notesFile = Join-Path $repoRoot 'docs/RELEASE_NOTES.md'
if (-not (Test-Path $notesFile)) {
    throw "缺少更新说明文件：$notesFile（更新说明的哈希会进入签名清单，因此不能为空）"
}

if (-not $Commit) {
    Write-Host '警告：无法确定 git 提交号，本次构建不会携带候选身份，不能作为正式候选发布。' -ForegroundColor Yellow
}
elseif (-not $SkipDirtyCheck) {
    $dirty = @(git -C $repoRoot status --porcelain)
    if ($dirty.Count -gt 0) {
        Write-Host ("警告：工作树不干净（{0} 项改动），本次构建的候选身份无法与提交对应。" -f $dirty.Count) -ForegroundColor Yellow
    }
}

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "=== $message ===" -ForegroundColor Cyan
}

function Invoke-Checked([string]$file, [string[]]$arguments, [string]$workingDirectory) {
    Write-Host "> $file $($arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $workingDirectory
    try {
        & $file @arguments
        if ($LASTEXITCODE -ne 0) { throw "$file 退出码 $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
}

# ---------------------------------------------------------------- 前端
if (-not $SkipWeb) {
    Write-Step '构建前端 (React + TypeScript + Vite)'
    if (-not (Test-Path (Join-Path $webDir 'node_modules'))) {
        Invoke-Checked 'npm.cmd' @('install', '--no-audit', '--no-fund') $webDir
    }
    Invoke-Checked 'npm.cmd' @('run', 'build') $webDir

    $wwwroot = Join-Path $repoRoot 'src/LanDisk.Server/wwwroot/index.html'
    if (-not (Test-Path $wwwroot)) { throw '前端构建未生成 wwwroot/index.html' }
}

# ---------------------------------------------------------------- 测试
if (-not $SkipTests) {
    Write-Step '运行自动化测试'
    Invoke-Checked 'dotnet' @(
        'test', (Join-Path $repoRoot 'tests/LanDisk.Tests/LanDisk.Tests.csproj'),
        '-c', $Configuration, '--nologo', '--logger', 'trx;LogFileName=landisk-tests.trx'
    ) $repoRoot
}

# ---------------------------------------------------------------- 发布
Write-Step "发布自包含运行包 ($RuntimeIdentifier, $Configuration)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# 1) 桌面外壳：LanDisk.exe（双击即用，无控制台窗口，托盘常驻）
Invoke-Checked 'dotnet' @(
    'publish', $appProject,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=embedded',
    '-o', $publishDir,
    '--nologo'
) $repoRoot

# 2) 无界面服务端：LanDisk.Server.exe（保留给命令行与实机冒烟测试使用）
#    与外壳发布到同一目录：两者都是同一 runtime 的自包含 win-x64 发布，共享文件完全一致。
Invoke-Checked 'dotnet' @(
    'publish', $serverProject,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=embedded',
    "-p:LanDiskBuildCommit=$Commit",
    "-p:LanDiskBuildId=$BuildId",
    '-o', $publishDir,
    '--nologo'
) $repoRoot

# 3) 更新程序：单文件、自包含，放在 updater\ 下
$updaterDir = Join-Path $publishDir 'updater'
Invoke-Checked 'dotnet' @(
    'publish', $updaterProject,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    "-p:LanDiskBuildCommit=$Commit",
    "-p:LanDiskBuildId=$BuildId",
    '-o', $updaterDir,
    '--nologo'
) $repoRoot

# 4) 扩展执行宿主：LanDisk.ExtensionHost.exe，放在 extensions\ 下。
#    这是唯一会执行扩展代码的进程；主程序从不加载扩展二进制，只把任务描述写进文件后启动它。
$extensionHostDir = Join-Path $publishDir 'extensions'
Invoke-Checked 'dotnet' @(
    'publish', $extensionHostProject,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=embedded',
    "-p:LanDiskBuildCommit=$Commit",
    "-p:LanDiskBuildId=$BuildId",
    '-o', $extensionHostDir,
    '--nologo'
) $repoRoot

foreach ($required in @('LanDisk.exe', 'LanDisk.Server.exe', 'wwwroot/index.html', 'updater/LanDisk.Updater.exe', 'extensions/LanDisk.ExtensionHost.exe')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "发布目录缺少 ${required}：$publishDir"
    }
}

# ---------------------------------------------------------------- 候选身份（必须在安装清单之前）
#
# 生成顺序在这里是有语义的，顺序错了就会产生真实缺陷：
#   1) 先写 build-info.json —— 它是运行包对自己身份的唯一声明；
#   2) 再枚举安装目录 —— 此时 build-info.json 已经存在，才会被枚举到；
#   3) 最后写 install-manifest.json —— 它是"本次安装了哪些文件"的记录。
#
# 06 轮修复的缺陷就是顺序颠倒：先枚举、后写 build-info.json，于是 release ZIP 里
# build-info.json 存在，install-manifest.json 的 files 清单里却永远没有它。
#
# 核对脚本会比较它、程序集内嵌值、更新包清单里的 buildCommit 与当前 HEAD 四者是否一致。
Write-Step '写入 build-info.json（候选身份，先于安装清单）'
$buildInfoPath = Join-Path $publishDir 'build-info.json'
[ordered]@{
    schemaVersion = 1
    product       = 'LanDisk'
    version       = $Version
    commit        = $Commit
    buildId       = $BuildId
    builtAt       = [DateTimeOffset]::Now.ToString('o')
    runtime       = $RuntimeIdentifier
    selfContained = $true
} | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath $buildInfoPath -Encoding UTF8

$buildInfoBytes = (Get-Item -LiteralPath $buildInfoPath).Length
$buildInfoSha256 = (Get-FileHash -LiteralPath $buildInfoPath -Algorithm SHA256).Hash.ToLowerInvariant()

# ---------------------------------------------------------------- 安装清单（在身份文件之后）
#
# 记录本次安装包含哪些文件。更新时只有这份清单里的文件才允许被删除，
# 用户自己放进安装目录的东西永远不会被更新或回滚清理掉。
#
# 注意：install-manifest.json 自身不属于发布内容。打包脚本会把它排除在更新包之外，
# 因为它是"安装了什么"的记录，只能由安装/更新过程自己生成，不能由包来声明。
$installManifest = [ordered]@{
    schemaVersion = 1
    product       = 'LanDisk'
    version       = $Version
    buildCommit   = $Commit
    installedAt   = [DateTimeOffset]::Now.ToString('o')
    files         = @(
        Get-ChildItem -LiteralPath $publishDir -Recurse -File |
            Where-Object { $_.Name -ne 'install-manifest.json' } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') } |
            Sort-Object
    )

    # 发布者声明的身份文件必须在清单里可核对：路径 + 字节数 + SHA-256。
    # 这三个值必须与 release ZIP 内真实的 build-info.json 逐字节一致，
    # 由 tools/Verify-InstallManifest.ps1 独立复算并作为硬门禁。
    buildInfo     = [ordered]@{
        path   = 'build-info.json'
        bytes  = $buildInfoBytes
        sha256 = $buildInfoSha256
    }
}
$installManifest | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $publishDir 'install-manifest.json') -Encoding UTF8

if ($installManifest.files -notcontains 'build-info.json') {
    throw 'install-manifest.json 的 files 清单没有包含 build-info.json（生成顺序被改坏了）。'
}

# ---------------------------------------------------------------- 打包
Write-Step '生成交付压缩包'
# Remove only the files this build regenerates.
#
# This used to be `Remove-Item $deliveryDir -Recurse -Force`, which wiped the whole delivery folder — including
# a previously delivered review bundle, its sidecar and a historical privacy notice. A release script must own
# its own outputs, not the directory: anything placed there by an earlier round is somebody else's artefact and
# deleting it is data loss. This is not hypothetical — the 1.7.3 review bundle was destroyed exactly this way.
New-Item -ItemType Directory -Force -Path $deliveryDir | Out-Null
$ownOutputs = @(
    "LanDisk-$RuntimeIdentifier.zip", 'LanDisk-Setup.exe', 'LanDisk-source.zip',
    "LanDisk-update-$Version.lupd", 'DELIVERY_REPORT.md', 'SHA256SUMS.txt', 'ARTIFACT_HASHES.txt',
    'RELEASE_CANDIDATE_IDENTITY.json', 'RELEASE_CANDIDATE_IDENTITY.md', 'SOURCE_TREE_VERIFY.json',
    'INSTALL_MANIFEST_VERIFY.json', 'setup-info.json', 'DELIVERY_READY.txt', 'DELIVERY_BLOCKED.md',
    'RELEASE_GATE.json'
)
foreach ($name in $ownOutputs) {
    $p = Join-Path $deliveryDir $name
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
}
if (Test-Path -LiteralPath (Join-Path $deliveryDir 'ui-evidence')) {
    Remove-Item -LiteralPath (Join-Path $deliveryDir 'ui-evidence') -Recurse -Force
}
# runtime-identity 同样是**候选作用域**的目录。先前它不在清理清单里，于是别的候选
# （甚至 1.7.0 时代 / commit 1bb94fed）留下的身份文件会跨构建存活，被当成当前候选的身份交付出去。
# 这里与 ui-evidence 一样按目录整体重建，随后由 Finalize-Delivery 写入本候选自己的身份。
if (Test-Path -LiteralPath (Join-Path $deliveryDir 'runtime-identity')) {
    Remove-Item -LiteralPath (Join-Path $deliveryDir 'runtime-identity') -Recurse -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem

$runtimeZip = Join-Path $deliveryDir "LanDisk-$RuntimeIdentifier.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir, $runtimeZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$runtimeZipSha256 = (Get-FileHash -LiteralPath $runtimeZip -Algorithm SHA256).Hash.ToLowerInvariant()

# ---------------------------------------------------------------- 安装程序
#
# LanDisk-Setup.exe 把上面这个运行包**原样**嵌进自己：用户在 Windows 侧第一次接触 LanDisk 时只需要
# 双击一个文件，不需要理解"解压 300 多个文件"这件事。嵌入时同时把载荷摘要写进程序集元数据，
# 安装器在写入任何字节之前会先核对它，因此"装上去的东西就是被测试过的东西"是可验证的，而不是一句承诺。
Write-Step "构建安装程序 LanDisk-Setup.exe（内嵌 release 载荷）"
$setupProject = Join-Path $repoRoot 'src/LanDisk.Setup/LanDisk.Setup.csproj'
$setupDir = Join-Path $repoRoot "dist/publish/LanDisk-Setup-$RuntimeIdentifier"
if (Test-Path $setupDir) { Remove-Item $setupDir -Recurse -Force }

Invoke-Checked 'dotnet' @(
    'publish', $setupProject,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    "-p:LanDiskPayloadZip=$runtimeZip",
    "-p:LanDiskPayloadSha256=$runtimeZipSha256",
    "-p:LanDiskBuildCommit=$Commit",
    "-p:LanDiskBuildId=$BuildId",
    '-o', $setupDir,
    '--nologo'
) $repoRoot

$setupExe = Join-Path $setupDir 'LanDisk-Setup.exe'
if (-not (Test-Path $setupExe)) { throw "安装程序未生成：$setupExe" }

Copy-Item $setupExe (Join-Path $deliveryDir 'LanDisk-Setup.exe') -Force
[ordered]@{
    schemaVersion         = 1
    product               = 'LanDisk'
    version               = $Version
    commit                = $Commit
    buildId               = $BuildId
    setupExe              = 'LanDisk-Setup.exe'
    setupBytes            = (Get-Item $setupExe).Length
    setupSha256           = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
    embeddedPayload       = 'LanDisk-win-x64.zip'
    embeddedPayloadSha256 = $runtimeZipSha256
    embeddedPayloadBytes  = (Get-Item $runtimeZip).Length
    selfContained         = $true
    bundledRuntime        = $RuntimeIdentifier
    generatedAt           = [DateTimeOffset]::Now.ToString('o')
} | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath (Join-Path $deliveryDir 'setup-info.json') -Encoding UTF8

Write-Host ("安装程序：{0}（{1} 字节）" -f $setupExe, (Get-Item $setupExe).Length)
Write-Host ("          SHA256 {0}" -f (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Host ("          内嵌载荷 SHA256 {0}（与 release ZIP 一致）" -f $runtimeZipSha256)

# 源码包：直接取 FINAL_HEAD 的完整 tracked tree。
#
# 以前这里是"按目录白名单 robocopy"，结果漏掉了根目录的 Directory.Build.props 与 .gitignore，
# 而"哪些文件属于源码"这个问题 Git 已经知道答案了。git archive 保证 0 遗漏、0 多余，
# 并且产出的字节就是 Git blob（不受工作区换行符影响）。
$sourceZip = Join-Path $deliveryDir 'LanDisk-source.zip'
if (-not $Commit) { throw '缺少 git 提交号，无法生成可核对的源码包。' }

# core.autocrlf=true would make git archive rewrite LF as CRLF, so the package would not contain the
# bytes Git actually stores. The check attributes are irrelevant here: the goal is the blob itself.
Write-Host "> git archive (autocrlf=false) --format=zip -o $sourceZip $Commit" -ForegroundColor DarkGray
& git -C $repoRoot -c core.autocrlf=false -c core.eol=lf archive --format=zip --output=$sourceZip $Commit
if ($LASTEXITCODE -ne 0) { throw "git archive 失败（退出码 $LASTEXITCODE）" }

# 生成的文件追加进包内；它们不改变源码树本身，只是说明这个快照对应哪个提交。
$sourceHeadText = @(
    "commit: $Commit",
    "buildId: $BuildId",
    "packagedAt: $([DateTimeOffset]::Now.ToString('o'))",
    "worktreeClean: $((@(git -C $repoRoot status --porcelain).Count -eq 0))"
) -join [Environment]::NewLine

$manifestLines = New-Object System.Collections.Generic.List[string]
$manifestLines.Add("# SOURCE_MANIFEST.tsv —— FINAL_HEAD 的每个 tracked 文件")
$manifestLines.Add("# commit: $Commit")
$manifestLines.Add("# 列: mode <TAB> blob_sha1 <TAB> path（blob_sha1 是 git cat-file 的对象名，可独立复核）")
$manifestLines.Add("mode`tblob_sha1`tpath")
foreach ($row in (git -C $repoRoot ls-tree -r $Commit)) {
    if ($row -match '^(\d+)\s+blob\s+([0-9a-f]{40})\s+(.+)$') {
        $manifestLines.Add(("{0}`t{1}`t{2}" -f $Matches[1], $Matches[2], $Matches[3]))
    }
}
$manifestText = ($manifestLines -join [Environment]::NewLine) + [Environment]::NewLine

$zip = [System.IO.Compression.ZipFile]::Open($sourceZip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    foreach ($pair in @(@('SOURCE_HEAD.txt', $sourceHeadText), @('SOURCE_MANIFEST.tsv', $manifestText))) {
        $entry = $zip.CreateEntry($pair[0], [System.IO.Compression.CompressionLevel]::Optimal)
        $writer = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding($false)))
        $writer.Write($pair[1])
        $writer.Dispose()
    }
}
finally { $zip.Dispose() }

Write-Host ("源码包已生成：{0}（tracked 文件 {1} 个 + SOURCE_HEAD.txt + SOURCE_MANIFEST.tsv）" -f $sourceZip, (($manifestLines.Count - 3)))

# ---------------------------------------------------------------- 更新包
$updatePackage = $null
if (-not $SkipUpdatePackage) {
    Write-Step "生成签名更新包 $Version.lupd"
    try {
        & (Join-Path $PSScriptRoot 'Build-UpdatePackage.ps1') `
            -PayloadDir $publishDir `
            -Version $Version `
            -MinimumFrom $MinimumFromVersion `
            -OutDir $deliveryDir `
            -Commit $Commit `
            -BuildId $BuildId `
            -NotesFile $notesFile
        if ($LASTEXITCODE -ne 0) { throw 'Build-UpdatePackage.ps1 失败' }

        $updatePackage = Join-Path $deliveryDir "LanDisk-update-$Version.lupd"

        # 立刻用私钥对应的公钥回读一遍：打包工具自己报告成功不算数，
        # 只有能通过 Extract 的签名与逐文件哈希校验，这个包才真的可安装。
        & dotnet run --project (Join-Path $repoRoot 'tools/keytool/KeyTool.csproj') -c $Configuration -- `
            verify $updatePackage (Join-Path $env:LOCALAPPDATA 'LanDisk-ReleaseSigning/release-signing.pem')
        if ($LASTEXITCODE -ne 0) { throw '刚生成的更新包无法通过校验。' }
    }
    catch {
        if ($_.Exception.Message -like '*找不到发布签名私钥*') {
            Write-Host ''
            Write-Host '未找到发布签名私钥，跳过更新包生成。' -ForegroundColor Yellow
            Write-Host '  pwsh -File tools/New-SigningKey.ps1 生成私钥后重新运行本脚本。'
            Write-Host '  运行包与源码包不受影响，但它们不含 .lupd 更新包。'
            $updatePackage = $null
        }
        else {
            throw
        }
    }
}


$runtimeHash = (Get-FileHash -LiteralPath $runtimeZip -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceHash = (Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash.ToLowerInvariant()

# ---------------------------------------------------------------- 安装清单硬断言
#
# 这一关必须在产物生成之后、FINAL_HEAD 锁定之前跑：它直接打开 release ZIP 与 .lupd，
# 复算 build-info.json 的真实字节数与 SHA256，再和 install-manifest.json 里记录的值比对。
# 只要 INSTALL_MANIFEST_CONTAINS_BUILD_INFO 不为 PASS，就**不允许**继续生成正式候选。
Write-Step '安装清单 / build-info 专项核对'
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Verify-InstallManifest.ps1') `
    -Repo $repoRoot `
    -PublishDir $publishDir `
    -RuntimeZip $runtimeZip `
    -UpdatePackage $updatePackage `
    -DeliveryDir $deliveryDir
if ($LASTEXITCODE -ne 0) {
    throw 'INSTALL_MANIFEST_CONTAINS_BUILD_INFO 未通过：已中止，不生成正式候选。'
}

# ---------------------------------------------------------------- 候选身份与报告（只写 dist/）
#
# 这些文件全部落在被 .gitignore 忽略的 dist/ 下，因此**生成报告不会改变源码 HEAD**。
# 04 轮的自引用正是"报告写进仓库、又在产物构建之后提交"造成的。
Write-Step '锁定 FINAL_HEAD 并生成候选身份与交付报告'

if (-not $Commit) { throw '构建缺少提交号，无法锁定 FINAL_HEAD。' }

$finalHeadFile = Join-Path $repoRoot 'dist/FINAL_HEAD.txt'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $finalHeadFile) | Out-Null
Set-Content -LiteralPath $finalHeadFile -Encoding UTF8 -Value @(
    "commit: $Commit",
    "version: $Version",
    "buildId: $BuildId",
    "lockedAt: $([DateTimeOffset]::Now.ToString('o'))",
    "worktreeClean: $((@(git -C $repoRoot status --porcelain).Count -eq 0))"
)
Write-Host "FINAL_HEAD: $Commit"

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Verify-CandidateIdentity.ps1') -Repo $repoRoot -PublishDir $publishDir -DeliveryDir $deliveryDir
if ($LASTEXITCODE -ne 0) { throw '候选身份核对失败。' }

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Verify-SourcePackage.ps1') -Repo $repoRoot -SourceZip $sourceZip -Commit $Commit
if ($LASTEXITCODE -ne 0) { throw '源码包完整性核对失败。' }

# ---------------------------------------------------------------- 桌面界面矩阵（正式验收的一部分）
#
# 这一步以前**不在发布流程里**：截图由人手工跑，结果落在 dist/ui-evidence，发布流程只是把它镜像进交付目录。
# 于是矩阵失败根本无法阻止发布 —— 没有任何人读捕获程序的退出码，因为它根本没被发布入口调用过。
# "某个独立判定函数会返回非零"与"发布入口真的会因此停下"是两件不同的事。
#
# 现在由发布入口自己跑矩阵。捕获模式不取外壳的单实例互斥量，因此可与用户正在运行的实例共存。
Write-Step '桌面界面矩阵（页面与组合数由 PageCatalog 单一事实源决定）'

$uiEvidenceDir = Join-Path $repoRoot 'dist/ui-evidence'
if (Test-Path -LiteralPath $uiEvidenceDir) { Remove-Item -LiteralPath $uiEvidenceDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $uiEvidenceDir | Out-Null

# The decision lives in one script so the release flow and the blocking test share it verbatim.
$env:LANDISK_GATE_CALLER = 'tools/Build-All.ps1'
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-UiMatrixGate.ps1') `
    -Repo $repoRoot -PublishDir $publishDir -EvidenceDir $uiEvidenceDir -CandidateCommit $Commit
$gateCode = $LASTEXITCODE
Remove-Item Env:\LANDISK_GATE_CALLER -ErrorAction SilentlyContinue
if ($gateCode -ne 0) {
    throw ("桌面界面矩阵未通过（发布门禁退出码 {0}）：已中止，不生成正式候选。截图、诊断与 UI_MATRIX_GATE.json 保留在 dist/ui-evidence。" -f $gateCode)
}

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Generate-DeliveryReport.ps1') -Repo $repoRoot -Output (Join-Path $deliveryDir 'DELIVERY_REPORT.md')
if ($LASTEXITCODE -ne 0) { throw '交付报告生成失败（可能有未替换的占位符）。' }

# ---------------------------------------------------------------- 正式发布判定（Full 作用域）
#
# UI gate 只证明 UI 范围。整份发布还需要浏览器两端、runtime、setup 与更新回滚的证据，而且这些证据
# 必须**属于当前候选**。Full 未通过时写出明确 BLOCKED 的失败说明并停止封包，不生成正式 READY 交付，
# 也不写发布成功标记。
#
# 依赖顺序是刻意的：本判定只读源证据（dist/evidence-11 下属于该候选的运行目录），不依赖它在通过之后
# 才会生成的报告、审核 ZIP 或 ZIP 自身哈希，因此不构成循环。
Write-Step '正式发布判定（Full）'

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-ReleaseGate.ps1') `
    -Repo $repoRoot -CandidateCommit $Commit -DeliveryDir $deliveryDir
$releaseGateCode = $LASTEXITCODE
if ($releaseGateCode -ne 0) {
    throw ("正式发布判定未通过（退出码 {0}）：已中止，不生成正式候选，不写发布成功标记。失败说明见 dist/delivery/DELIVERY_BLOCKED.md。" -f $releaseGateCode)
}
Set-Content -LiteralPath (Join-Path $deliveryDir 'DELIVERY_READY.txt') -Encoding UTF8 -Value @(
    "commit: $Commit",
    "version: $Version",
    "verifiedAt: $([DateTimeOffset]::Now.ToString('o'))"
)

# 报告与身份文件是这次构建的产物，纳入清单（清单本身不自引用）。
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Finalize-Delivery.ps1') -DeliveryDir $deliveryDir -PublishDir $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Finalize-Delivery.ps1 失败' }

Write-Step '完成'
Write-Host "运行包:   $runtimeZip"
Write-Host "源码包:   $sourceZip"
Write-Host "SHA256:   运行包 $runtimeHash"
Write-Host "         源码包 $sourceHash"
if ($updatePackage) {
    $updateHash = (Get-FileHash -LiteralPath $updatePackage -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "更新包:   $updatePackage"
    Write-Host "         $updateHash"
}
else {
    Write-Host '更新包:   未生成（缺少发布签名私钥）'
}
Write-Host "清单:     $(Join-Path $deliveryDir 'SHA256SUMS.txt')"
