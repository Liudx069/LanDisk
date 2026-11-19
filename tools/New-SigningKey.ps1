#requires -Version 7.0
<#
.SYNOPSIS
    生成 LanDisk 更新包的发布签名私钥（ECDSA P-256）。

.DESCRIPTION
    私钥默认写到仓库之外的 %LOCALAPPDATA%\LanDisk-ReleaseSigning\release-signing.pem，
    并尽量用 icacls 收紧为仅当前用户可读写。

    这个脚本不会把私钥写进仓库、构建产物或交付包；公钥需要人工复制到
    src/LanDisk.Update/ReleasePublicKey.cs 中（脚本会打印出可直接粘贴的内容）。

    如果目标私钥已经存在，脚本会拒绝覆盖：丢失私钥意味着以后无法再发布可被现有安装
    信任的更新包。

.EXAMPLE
    pwsh -File tools/New-SigningKey.ps1
    pwsh -File tools/New-SigningKey.ps1 -Force -Path D:\keys\release-signing.pem
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $env:LOCALAPPDATA 'LanDisk-ReleaseSigning\release-signing.pem'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$keyTool = Join-Path $repoRoot 'tools/keytool/KeyTool.csproj'

if (-not (Test-Path $keyTool)) { throw "找不到签名工具项目：$keyTool" }

$Path = [System.IO.Path]::GetFullPath($Path)

# 尽量阻止把私钥写进仓库本身。
if ($Path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝在仓库内生成发布签名私钥（$Path）。请使用仓库之外的路径。"
}

if ((Test-Path $Path) -and -not $Force) {
    Write-Host "私钥已存在，未做任何修改：$Path" -ForegroundColor Yellow
    Write-Host '如需重新生成，请显式加 -Force（这会使旧的更新包无法再被新构建信任）。'
    exit 0
}

if ((Test-Path $Path) -and $Force) {
    $backup = "$Path.bak-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))"
    Move-Item -LiteralPath $Path -Destination $backup
    Write-Host "已把原有私钥备份为 $backup" -ForegroundColor Yellow
}

Write-Host '正在生成发布签名私钥…' -ForegroundColor Cyan
& dotnet run --project $keyTool -c Release -- new-key $Path
if ($LASTEXITCODE -ne 0) { throw "生成签名私钥失败（退出码 $LASTEXITCODE）。" }

Write-Host ''
Write-Host '完成。下一步：' -ForegroundColor Green
Write-Host "  1. 妥善离线保存私钥：$Path"
Write-Host '  2. 把上面打印的 KeyId 与 Base64 公钥填进 src/LanDisk.Update/ReleasePublicKey.cs'
Write-Host '  3. 打包时让 Build-UpdatePackage.ps1 找到私钥（-KeyPath 或 LANDISK_SIGNING_KEY 环境变量）'
