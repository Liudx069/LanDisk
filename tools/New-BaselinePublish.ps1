# 生成一个"上一版本"基线发布目录，供 tools/Update-ReleaseVerify.ps1 做 1.7.0 → 1.7.1 的升级 / 回滚验收。
#
# 为什么需要它：升级验收的场景是"装好版本 A，再升到版本 B，再用一个坏包升级并回滚到 B"。B（1.7.1）
# 由 Update-ReleaseVerify 自己构建；A 必须是真实可安装的上一版本发布目录（含 build-info.json 与
# install-manifest.json）。dist/publish 只保留最新一次的产物，所以这里按 Build-All 的同一套规则重建 A。
#
# 这个脚本只写 dist/（已被 .gitignore 忽略），不触碰源码树，也不改任何交付产物。

[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$BaselineVersion = '1.7.0',
    [string]$Commit = '',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'

if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$repoRoot = (Resolve-Path $Repo).Path
if (-not $Commit) { $Commit = (& git -C $repoRoot rev-parse HEAD).Trim() }

$outDir = Join-Path $repoRoot "dist/publish/LanDisk-$RuntimeIdentifier-$BaselineVersion"
if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }

$appProject = Join-Path $repoRoot 'src/LanDisk.App/LanDisk.App.csproj'
$serverProject = Join-Path $repoRoot 'src/LanDisk.Server/LanDisk.Server.csproj'
$updaterProject = Join-Path $repoRoot 'src/LanDisk.Updater/LanDisk.Updater.csproj'

function Invoke-Publish([string]$project, [string]$destination, [switch]$SingleFile) {
    # The single-file flag is materialised into a variable first: an inline "'a' + $(if ...)" inside an array
    # literal is easy to misparse, and a misparse silently splits "-p:PublishSingleFile=false" into two
    # arguments — which is exactly how the first attempt failed.
    $singleFileValue = if ($SingleFile) { 'true' } else { 'false' }
    $arguments = @(
        'publish', $project,
        '-c', 'Release',
        '-r', $RuntimeIdentifier,
        '--self-contained', 'true',
        "-p:PublishSingleFile=$singleFileValue",
        "-p:Version=$BaselineVersion",
        "-p:LanDiskBuildCommit=$Commit",
        "-p:LanDiskBuildId=Release/$RuntimeIdentifier/$BaselineVersion",
        '-o', $destination,
        '--nologo'
    )
    if (-not $SingleFile) { $arguments += '-p:DebugType=embedded' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$project" }
}

Write-Host "构建 $BaselineVersion 基线发布（$Commit）…"
Invoke-Publish $appProject $outDir
Invoke-Publish $serverProject $outDir
Invoke-Publish $updaterProject (Join-Path $outDir 'updater') -SingleFile

# 与 Build-All 完全相同的两个身份文件：先 build-info.json，再 install-manifest.json。
$buildInfoPath = Join-Path $outDir 'build-info.json'
[ordered]@{
    schemaVersion = 1
    product       = 'LanDisk'
    version       = $BaselineVersion
    commit        = $Commit
    buildId       = "Release/$RuntimeIdentifier/$BaselineVersion"
    builtAt       = [DateTimeOffset]::Now.ToString('o')
    runtime       = $RuntimeIdentifier
    selfContained = $true
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8

$installManifest = [ordered]@{
    schemaVersion = 1
    product       = 'LanDisk'
    version       = $BaselineVersion
    buildCommit   = $Commit
    installedAt   = [DateTimeOffset]::Now.ToString('o')
    files         = @(
        Get-ChildItem -LiteralPath $outDir -Recurse -File |
            Where-Object { $_.Name -ne 'install-manifest.json' } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($outDir, $_.FullName).Replace('\', '/') } |
            Sort-Object
    )
    buildInfo     = [ordered]@{
        path   = 'build-info.json'
        bytes  = (Get-Item -LiteralPath $buildInfoPath).Length
        sha256 = (Get-FileHash -LiteralPath $buildInfoPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$installManifest | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $outDir 'install-manifest.json') -Encoding UTF8

foreach ($required in @('LanDisk.exe', 'LanDisk.Server.exe', 'wwwroot/index.html', 'updater/LanDisk.Updater.exe', 'build-info.json', 'install-manifest.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $outDir $required))) { throw "基线发布缺少 $required" }
}

# 版本号从 PE 版本资源读取。
#
# 不要用 "LanDisk.exe --version"：那会真的把外壳拉起来（托盘常驻、占用固定端口 8891/8892），
# 于是一次"只想知道版本号"的调用会把后续的升级 / 回滚验收全部挡在端口占用上。这里只读文件版本信息，
# 不启动任何进程。
$exePath = Join-Path $outDir 'LanDisk.exe'
$fileVersion = (Get-Item -LiteralPath $exePath).VersionInfo
$version = if ($fileVersion.ProductVersion) { $fileVersion.ProductVersion } else { $fileVersion.FileVersion }
Write-Host "基线发布: $outDir"
Write-Host "  文件数: $(@(Get-ChildItem -LiteralPath $outDir -Recurse -File).Count)"
Write-Host "  版本:   $version"
Write-Host "BASE_PUBLISH_DIR=$outDir"
