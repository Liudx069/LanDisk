#requires -Version 7.0
<#
.SYNOPSIS
    P02 隐私与清理检查：确认交付物中不含私钥、令牌、个人路径或用户文件。

.DESCRIPTION
    扫描 dist/delivery 与 dist/publish 中的文本内容与文件名，检查：
      * PEM/PKCS#12 私钥标记
      * 本机上实际生成过的一次性管理令牌与 TLS 口令（若能在数据目录中找到）
      * 具体的用户目录路径（C:\Users\<用户名>）
      * 交付包中是否混入了 *.pfx / *.key / admin-token.txt 等敏感文件名
      * 运行包的 appsettings 中是否包含密钥或连接字符串

    注意：本脚本只读取本任务自己的数据目录与交付目录，不搜索用户私人目录。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$deliveryDir = Join-Path $repoRoot 'dist/delivery'
$publishDir = Join-Path $repoRoot 'dist/publish/LanDisk-win-x64'
$dataRoot = Join-Path $env:LOCALAPPDATA 'LanDisk'

$findings = New-Object System.Collections.Generic.List[object]
function Add-Finding([string]$severity, [string]$where, [string]$what) {
    $findings.Add([pscustomobject]@{ Severity = $severity; Where = $where; What = $what })
}

# --- 1. 敏感文件名 ----------------------------------------------------------
$suspiciousNames = @('*.pfx', '*.p12', '*.key', '*.pem', 'admin-token.txt', 'tls-password.txt', 'shares.json', 'devices.json', 'recycle-index.json')
foreach ($root in @($deliveryDir, $publishDir)) {
    if (-not (Test-Path $root)) { continue }
    foreach ($pattern in $suspiciousNames) {
        $hits = Get-ChildItem -LiteralPath $root -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue
        foreach ($hit in $hits) {
            Add-Finding 'HIGH' $hit.FullName "交付物中出现不应包含的文件名（$pattern）"
        }
    }
}

# --- 2. 私钥与令牌内容 ------------------------------------------------------
$needles = New-Object System.Collections.Generic.List[string]
$needles.Add('-----BEGIN')
$needles.Add('PRIVATE KEY')

if (Test-Path (Join-Path $dataRoot 'secrets/admin-token.txt')) {
    $token = (Get-Content -LiteralPath (Join-Path $dataRoot 'secrets/admin-token.txt') -Raw).Trim()
    if ($token.Length -ge 32) { $needles.Add($token) }
}
if (Test-Path (Join-Path $dataRoot 'secrets/tls-password.txt')) {
    $password = (Get-Content -LiteralPath (Join-Path $dataRoot 'secrets/tls-password.txt') -Raw).Trim()
    if ($password.Length -ge 16) { $needles.Add($password) }
}

# 任何形如 admin#token=<长随机串> 的未脱敏凭据都不应出现在交付物中。
# 文档里的占位示例（"..."/"<...>"）长度不足，不会被误报。
$tokenPattern = 'admin#token=(?!<ADMIN-TOKEN-REDACTED>)([A-Za-z0-9_\-]{20,})'

# 本机用户目录前缀（只用于检测交付物是否泄露了开发机的个人路径）
$userProfile = $env:USERPROFILE
if ($userProfile) { $needles.Add($userProfile) }
$userName = $env:USERNAME
if ($userName -and $userName.Length -ge 3) { $needles.Add("C:\Users\$userName") }

$textExtensions = @('.json', '.md', '.txt', '.tsv', '.config', '.xml', '.js', '.css', '.html', '.ps1', '.mjs', '.cs', '.csproj', '.slnx', '.gitignore', '.yml', '.yaml')
foreach ($root in @($deliveryDir, $publishDir)) {
    if (-not (Test-Path $root)) { continue }
    $files = Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $textExtensions -contains $_.Extension.ToLowerInvariant() -or $_.Name -in @('SHA256SUMS.txt', 'README', 'LICENSE') }
    foreach ($file in $files) {
        if ($file.Length -gt 8MB) { continue }
        $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $content) { continue }
        foreach ($needle in $needles) {
            if ($content.Contains($needle, [System.StringComparison]::Ordinal)) {
                Add-Finding 'HIGH' $file.FullName ('内容中命中敏感串: ' + ($needle.Substring(0, [Math]::Min(12, $needle.Length))) + '…')
            }
        }
        if ($content -match $tokenPattern) {
            Add-Finding 'HIGH' $file.FullName '内容中出现未脱敏的 admin#token= 凭据'
        }
    }
}

# --- 3. 运行包配置 ----------------------------------------------------------
$appSettings = Join-Path $publishDir 'appsettings.json'
if (Test-Path $appSettings) {
    $content = Get-Content -LiteralPath $appSettings -Raw
    if ($content -match '(?i)(password|secret|connectionstring|apikey|token)') {
        Add-Finding 'MEDIUM' $appSettings 'appsettings.json 中出现疑似凭据字段'
    }
}

# --- 4. 用户文件是否被触碰 --------------------------------------------------
$samplesDir = Join-Path $repoRoot 'tests/samples'
$allowedExtensions = @('.mp4', '.webm', '.wav', '.m4a', '.mkv', '.png')
$straySamples = @()
if (Test-Path $samplesDir) {
    foreach ($file in Get-ChildItem -LiteralPath $samplesDir -File) {
        if ($allowedExtensions -notcontains $file.Extension.ToLowerInvariant()) { $straySamples += $file.Name }
    }
}
if ($straySamples.Count -gt 0) {
    Add-Finding 'MEDIUM' $samplesDir ('测试样本目录中出现非合成媒体文件: ' + ($straySamples -join ', '))
}

# --- 汇总 -------------------------------------------------------------------
Write-Host ''
Write-Host '=== P02 隐私与清理检查结果 ===' -ForegroundColor Cyan
if ($findings.Count -eq 0) {
    Write-Host '  [PASS] 未发现私钥、令牌、个人路径或用户文件混入交付物。' -ForegroundColor Green
    Write-Host ('  扫描范围: {0}' -f (@($deliveryDir, $publishDir) -join ' ; '))
}
else {
    foreach ($finding in $findings) {
        $color = if ($finding.Severity -eq 'HIGH') { 'Red' } else { 'Yellow' }
        Write-Host ("  [{0}] {1} - {2}" -f $finding.Severity, $finding.Where, $finding.What) -ForegroundColor $color
    }
    Write-Host '  [FAIL] 存在需要处理的问题。' -ForegroundColor Red
}

$reportPath = Join-Path $repoRoot 'dist/privacy-check.json'
$findings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8

if ($findings | Where-Object { $_.Severity -eq 'HIGH' }) { exit 1 } else { exit 0 }
