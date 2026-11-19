#requires -Version 7.0
<#
.SYNOPSIS
    用发布签名私钥把一份已发布的程序目录打包成 LanDisk 更新包（.lupd）。

.DESCRIPTION
    更新包是一个 ZIP，内部包含：
        update-manifest.json        版本、最低来源版本、每个文件的 SHA256
        update-manifest.sig.json    对清单规范化文本的 ECDSA P-256 签名
        payload/…                   程序文件

    私钥必须位于仓库之外，通过 -KeyPath、LANDISK_SIGNING_KEY 环境变量，或默认的
    %LOCALAPPDATA%\LanDisk-ReleaseSigning\release-signing.pem 提供。

    脚本不会在缺少私钥时"降级为未签名包"：没有签名的更新包不会被任何安装接受，
    静默产出一个装不上的包只会浪费时间。

.EXAMPLE
    pwsh -File tools/Build-UpdatePackage.ps1 -PayloadDir dist/publish/LanDisk-win-x64 -Version 1.2.0 `
        -MinimumFrom 1.0.0 -NotesFile dist/release-notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$MinimumFrom = '1.0.0',
    [string]$OutDir,
    [string]$Commit = '',
    [string]$BuildId = '',
    [string]$Notes = '',
    [string]$NotesFile = '',
    [string]$EntryPoint = 'LanDisk.exe',
    [string]$KeyPath = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$keyTool = Join-Path $repoRoot 'tools/keytool/KeyTool.csproj'

$PayloadDir = [System.IO.Path]::GetFullPath($PayloadDir)
if (-not (Test-Path $PayloadDir)) { throw "发布目录不存在：$PayloadDir" }
if (-not (Test-Path (Join-Path $PayloadDir $EntryPoint))) {
    throw "发布目录中缺少入口程序 $EntryPoint：$PayloadDir"
}

if (-not $Version -match '^\d+\.\d+\.\d+') { throw "版本号格式不正确：$Version" }
if (-not $MinimumFrom -match '^\d+\.\d+\.\d+') { throw "最低来源版本格式不正确：$MinimumFrom" }

if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'dist/delivery' }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 私钥解析顺序：显式参数 → 环境变量 → 默认离线位置。
if (-not $KeyPath) { $KeyPath = $env:LANDISK_SIGNING_KEY }
if (-not $KeyPath) { $KeyPath = Join-Path $env:LOCALAPPDATA 'LanDisk-ReleaseSigning\release-signing.pem' }

if (-not (Test-Path $KeyPath)) {
    throw @"
找不到发布签名私钥：$KeyPath

签名是更新包唯一的信任来源，没有私钥就无法产出可安装的更新包。
请先运行：pwsh -File tools/New-SigningKey.ps1
或用 -KeyPath / LANDISK_SIGNING_KEY 指向已有的私钥。
"@
}

if ($KeyPath.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝使用仓库内的私钥（$KeyPath）。发布签名私钥必须离线保管。"
}

if ($NotesFile) {
    if (-not (Test-Path $NotesFile)) { throw "更新说明文件不存在：$NotesFile" }
    $Notes = Get-Content -LiteralPath $NotesFile -Raw -Encoding UTF8
}

if (-not $Notes) {
    throw '更新说明为空。更新说明的哈希会进入签名清单，空说明会让用户看不到任何发布信息。'
}

$outFile = Join-Path $OutDir "LanDisk-update-$Version.lupd"

$arguments = @(
    'pack',
    '--payload', $PayloadDir,
    '--out', $outFile,
    '--key', $KeyPath,
    '--version', $Version,
    '--min-upgrade', $MinimumFrom,
    '--entry', $EntryPoint,
    '--build-id', $BuildId,
    '--notes', $Notes
)
if ($Commit) { $arguments += @('--commit', $Commit) } else { throw '缺少 -Commit：更新包必须声明构建提交号。' }

Write-Host '正在签名并打包更新包…' -ForegroundColor Cyan
& dotnet run --project $keyTool -c Release -- @arguments
if ($LASTEXITCODE -ne 0) { throw "打包更新包失败（退出码 $LASTEXITCODE）。" }

if (-not (Test-Path $outFile)) { throw "打包完成但没有生成文件：$outFile" }

$hash = (Get-FileHash -LiteralPath $outFile -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $outFile).Length

Write-Host ''
Write-Host '更新包已生成：' -ForegroundColor Green
Write-Host "  文件:   $outFile"
Write-Host "  版本:   $Version（最低来源 $MinimumFrom）"
Write-Host "  大小:   $size 字节"
Write-Host "  SHA256: $hash"
