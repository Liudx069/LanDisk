#requires -Version 7.0
<#
.SYNOPSIS
    核对 LanDisk-source.zip 是否完整代表 FINAL_HEAD 的 tracked 源码树。

.DESCRIPTION
    对每个 tracked 文件计算 Git blob 哈希（sha1("blob <len>\0" + 内容)）并与
    `git ls-tree -r <commit>` 记录的对象名比较。这样"源码包完整"不再依赖人工白名单，
    也不受工作区换行符影响——包里的字节必须**就是** Git 里保存的那份。

    断言：
      * 0 missing（tracked 但没有出现在包里）
      * 0 unexpected（包里出现了 tracked 之外的文件，SOURCE_HEAD.txt / SOURCE_MANIFEST.tsv 除外）
      * 每个 tracked 文件的 blob 哈希一致
      * Directory.Build.props 存在
      * 根目录的 tracked .csproj / .slnx / .props / .targets 全部存在

.EXAMPLE
    pwsh -File tools/Verify-SourcePackage.ps1
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$SourceZip = '',
    [string]$Commit = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$Repo = (Resolve-Path -LiteralPath $Repo).Path
if (-not $SourceZip) { $SourceZip = Join-Path $Repo 'dist/delivery/LanDisk-source.zip' }
if (-not (Test-Path -LiteralPath $SourceZip)) { throw "找不到源码包：$SourceZip" }
if (-not $Commit) { $Commit = (git -C $Repo rev-parse HEAD).Trim() }

$results = New-Object System.Collections.Generic.List[object]
function Add-Check([string]$id, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Id = $id; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $id, $detail) -ForegroundColor $colour
}

# Git 的对象名：sha1("blob " + 长度 + NUL + 内容)
function Get-GitBlobHash([byte[]]$bytes) {
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $header = [System.Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
        $buffer = New-Object byte[] ($header.Length + $bytes.Length)
        [Array]::Copy($header, 0, $buffer, 0, $header.Length)
        [Array]::Copy($bytes, 0, $buffer, $header.Length, $bytes.Length)
        return [Convert]::ToHexString($sha1.ComputeHash($buffer)).ToLowerInvariant()
    }
    finally { $sha1.Dispose() }
}

Write-Host ''
Write-Host '=== 源码包完整性核对 ===' -ForegroundColor Cyan
Write-Host "commit : $Commit"
Write-Host "package: $SourceZip"

# ---------------------------------------------------------------- tracked tree
$tracked = @{}
foreach ($row in (git -C $Repo ls-tree -r $Commit)) {
    if ($row -match '^(\d+)\s+blob\s+([0-9a-f]{40})\s+(.+)$') {
        $tracked[$Matches[3]] = [pscustomobject]@{ Mode = $Matches[1]; Blob = $Matches[2] }
    }
}
Add-Check 'SRC-01' ($tracked.Count -gt 0) ("FINAL_HEAD 的 tracked 文件数 = {0}" -f $tracked.Count)

# ---------------------------------------------------------------- zip contents
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($SourceZip)
try {
    $generated = @('SOURCE_HEAD.txt', 'SOURCE_MANIFEST.tsv')
    $entries = @{}
    foreach ($e in $zip.Entries) {
        if ($e.FullName.EndsWith('/')) { continue }
        if ($generated -contains $e.FullName) { continue }
        $entries[$e.FullName] = $e
    }

    $entryNames = @($zip.Entries | ForEach-Object { $_.FullName })
    $missingGenerated = @($generated | Where-Object { $entryNames -notcontains $_ })
    Add-Check 'SRC-02' ($missingGenerated.Count -eq 0) `
        ("源码包内包含生成文件（缺失 {0} 个）" -f $missingGenerated.Count)

    $missing = New-Object System.Collections.Generic.List[string]
    $mismatched = New-Object System.Collections.Generic.List[string]
    foreach ($path in ($tracked.Keys | Sort-Object)) {
        if (-not $entries.ContainsKey($path)) { $missing.Add($path); continue }

        $stream = $entries[$path].Open()
        $memory = New-Object System.IO.MemoryStream
        $stream.CopyTo($memory)
        $stream.Dispose()
        $bytes = $memory.ToArray()
        $memory.Dispose()

        $blob = Get-GitBlobHash $bytes
        if ($blob -ne $tracked[$path].Blob) { $mismatched.Add("$path (期望 $($tracked[$path].Blob)，实际 $blob)") }
    }

    $missingNote = if ($missing.Count -gt 0) { '：' + (($missing | Select-Object -First 5) -join '、') } else { '' }
    Add-Check 'SRC-03' ($missing.Count -eq 0) ("tracked 但缺失的文件数 = $($missing.Count)$missingNote")

    $mismatchNote = if ($mismatched.Count -gt 0) { '：' + (($mismatched | Select-Object -First 3) -join '；') } else { '' }
    Add-Check 'SRC-04' ($mismatched.Count -eq 0) ("blob 哈希不一致的文件数 = $($mismatched.Count)$mismatchNote")

    $unexpected = @($entries.Keys | Where-Object { -not $tracked.ContainsKey($_) })
    $unexpectedNote = if ($unexpected.Count -gt 0) { '：' + (($unexpected | Select-Object -First 5) -join '、') } else { '' }
    Add-Check 'SRC-05' ($unexpected.Count -eq 0) ("包里出现 tracked 之外的文件数 = $($unexpected.Count)$unexpectedNote")

    # 明确要求：根目录的构建文件必须在包里。
    Add-Check 'SRC-06' ($entries.ContainsKey('Directory.Build.props')) 'Directory.Build.props 存在于源码包'

    $rootBuildFiles = @($tracked.Keys | Where-Object { $_ -notmatch '/' -and $_ -match '\.(csproj|slnx|props|targets|sln)$' })
    $rootMissing = @($rootBuildFiles | Where-Object { -not $entries.ContainsKey($_) })
    Add-Check 'SRC-07' ($rootMissing.Count -eq 0) `
        ("根目录 tracked 构建文件 {0} 个全部存在（缺失 {1}）" -f $rootBuildFiles.Count, $rootMissing.Count)

    Add-Check 'SRC-08' ($entries.ContainsKey('.gitignore')) '.gitignore 存在于源码包'
    Add-Check 'SRC-09' ($entries.ContainsKey('LanDisk.slnx')) 'LanDisk.slnx 存在于源码包'
}
finally { $zip.Dispose() }

Write-Host ''
$failed = 0
$passed = 0
$reportDir = Join-Path $Repo 'dist/delivery'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null


foreach ($entry in $results) {
    if ($entry.Result -eq 'FAIL') { $failed++ } else { $passed++ }
}

$reportPath = Join-Path $reportDir 'SOURCE_TREE_VERIFY.json'
$payload = @()
foreach ($entry in $results) {
    $payload += [pscustomobject]@{ Id = $entry.Id; Result = $entry.Result; Detail = $entry.Detail }
}

try {
    $json = ConvertTo-Json -InputObject $payload -Depth 4
    Set-Content -LiteralPath $reportPath -Value $json -Encoding UTF8
}
catch {
    Write-Host ("写入源码包核对报告失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
    Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkGray
}

$summary = '源码包完整性：通过 {0} / 失败 {1}（tracked {2} 个）' -f $passed, $failed, $tracked.Count
if ($failed -eq 0) { Write-Host $summary -ForegroundColor Green } else { Write-Host $summary -ForegroundColor Red }
Write-Host "证据文件: $reportPath"

if ($failed -gt 0) { exit 1 }
exit 0
