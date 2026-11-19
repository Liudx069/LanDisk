param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$StageDir = '',
    [string]$OutDir = '',
    [string]$BundleName = 'LAN_DISK_11_174_FINAL_REVIEW_BUNDLE.zip',
    [string]$RunId = '',
    [string]$SelectedDir = '',
    [string]$SelectedReport = ''
)

# 生成最终审核包：包内 MANIFEST_SHA256.tsv（不含自身）+ 外置 sidecar，并重新开包逐项核对。
#
# 说明：
#   * 包内**不重复**塞 Setup / release ZIP / .lupd 三个大产物（保留在本地供安装），
#     但源码包在内，截图与关键原始结果也都在内；
#   * ZIP 自身最终哈希**不写进包内**（自引用），只放外置 sidecar；
#   * 核对内容：CRC、成员集合、重复路径与危险路径、每个成员的大小与完整 SHA256。

$ErrorActionPreference = 'Stop'
if (-not $StageDir) { $StageDir = Join-Path $Repo 'dist\delivery-174-final' }
if (-not $OutDir) { $OutDir = Join-Path $Repo 'dist\delivery-174-final-release' }
. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
if (-not $RunId) { $RunId = Resolve-EvidenceRunId -Repo $Repo }
if (-not $SelectedDir) { $SelectedDir = Join-Path $Repo 'dist\delivery-175-final' }

$version = (Select-String -Path (Join-Path $Repo 'src\LanDisk.App\LanDisk.App.csproj') -Pattern '<Version>(.*)</Version>').Matches.Groups[1].Value
$productCommit = (& git -C $Repo log --format=%H -1 -- src).Trim()
$toolCommit = (& git -C $Repo rev-parse HEAD).Trim()

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$zipPath = Join-Path $OutDir $BundleName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not ('Crc32Helper' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
public static class Crc32Helper
{
    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1); t[i] = c; }
        return t;
    }
    public static uint File(string path)
    {
        uint crc = 0xFFFFFFFFu; byte[] buf = new byte[1 << 20];
        using (var s = System.IO.File.OpenRead(path))
        {
            int n; while ((n = s.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++) crc = Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
'@
}

# ---- 包内清单（不含 MANIFEST 自身）
$members = @(Get-ChildItem -LiteralPath $StageDir -Recurse -File | Sort-Object FullName)
$manifest = New-Object System.Collections.Generic.List[string]
$manifest.Add("path`tbytes`tsha256")
foreach ($m in $members) {
    if ($m.Name -eq 'MANIFEST_SHA256.tsv') { continue }
    $rel = $m.FullName.Substring($StageDir.Length + 1).Replace('\', '/')
    $manifest.Add(($rel + "`t" + $m.Length + "`t" + (Get-FileHash -LiteralPath $m.FullName -Algorithm SHA256).Hash.ToLowerInvariant()))
}
$manifest | Set-Content -LiteralPath (Join-Path $StageDir 'MANIFEST_SHA256.tsv') -Encoding UTF8
Write-Host ("manifest: {0} members" -f ($manifest.Count - 1))

[System.IO.Compression.ZipFile]::CreateFromDirectory($StageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# ---- 重新开包核对
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$entries = @($zip.Entries | Where-Object { $_.Length -gt 0 -or $_.FullName -notlike '*/' })
$dupes = @($entries | Group-Object FullName | Where-Object { $_.Count -gt 1 })
$danger = @($entries | Where-Object { $_.FullName -match '^/|\.\./|^[A-Za-z]:' })
$mismatch = New-Object System.Collections.Generic.List[string]
$manifestRows = @{}
foreach ($line in (Get-Content -LiteralPath (Join-Path $StageDir 'MANIFEST_SHA256.tsv') -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ })) {
    $c = $line -split "`t"; if ($c.Count -ge 3) { $manifestRows[$c[0]] = @{ bytes = [int64]$c[1]; sha = $c[2] } }
}
foreach ($e in $entries) {
    if ($e.FullName -eq 'MANIFEST_SHA256.tsv') { continue }
    if (-not $manifestRows.ContainsKey($e.FullName)) { $mismatch.Add('清单缺少成员 ' + $e.FullName); continue }
    $tmp = Join-Path $env:TEMP ('zipcheck-' + [guid]::NewGuid().ToString('n'))
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $tmp, $true)
    $sha = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
    $len = (Get-Item $tmp).Length
    Remove-Item $tmp -Force
    if ($len -ne $manifestRows[$e.FullName].bytes) { $mismatch.Add("大小不符 $($e.FullName)：包内 $len / 清单 $($manifestRows[$e.FullName].bytes)") }
    if ($sha -ne $manifestRows[$e.FullName].sha) { $mismatch.Add("SHA256 不符 $($e.FullName)") }
}
$zip.Dispose()

$item = Get-Item $zipPath
$sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$crc = '{0:x8}' -f [Crc32Helper]::File($zipPath)

# 大小写冲突（同一路径仅大小写不同，在 Windows 上会互相覆盖）
$caseConflicts = @($entries | Group-Object { $_.FullName.ToLowerInvariant() } | Where-Object { $_.Count -gt 1 })

# 包内权威报告必须与**选定的原件**逐字节一致。
#
# 这一检查必须从 ZIP 里**实际读出成员字节**再比，不能只比 Manifest 里声明的哈希：
# Manifest 是封包时自己写的，比它等于自己证明自己。同时它必须能区分三种结果 ——
# 正确文件通过、不同文件被拒、缺文件被拒 —— 而不是一个"永远 True"的指示器。
#
# 先前这里的选定路径写死成 dist\delivery-174-final（上一轮的目录），于是换候选后永远比不中，
# 指示器恒为 False。现在由参数/交付目录决定，缺失即明确报"缺文件"。
$reportRel = '01-report/DELIVERY_REPORT_11_VERIFIED.md'
if (-not $SelectedReport) { $SelectedReport = Join-Path $SelectedDir 'DELIVERY_REPORT_11_VERIFIED.md' }

$reportCheck = 'MISSING_SELECTED'
$reportIdentical = $false
$reportDetail = ''
if (-not (Test-Path -LiteralPath $SelectedReport)) {
    $reportCheck = 'REJECT_MISSING_SELECTED'
    $reportDetail = '选定的原件不存在：' + $SelectedReport
}
elseif (-not ($entries | Where-Object { $_.FullName -eq $reportRel })) {
    $reportCheck = 'REJECT_MISSING_IN_PACKAGE'
    $reportDetail = '包内没有该成员：' + $reportRel
}
else {
    # 包内报告是**按约定脱敏后**的副本，所以把选定原件按同一规则脱敏后再比，
    # 否则会把"脱敏本身"误判成"不一致"。
    . (Join-Path $Repo 'tools\Mask-NetworkIdentity.ps1')
    $tmpMasked = Join-Path $env:TEMP ('maskedreport-' + [guid]::NewGuid().ToString('n') + '.md')
    $null = Mask-NetworkFile -SourcePath $SelectedReport -DestinationPath $tmpMasked
    $expectedSha = (Get-FileHash -LiteralPath $tmpMasked -Algorithm SHA256).Hash.ToLowerInvariant()
    Remove-Item $tmpMasked -Force

    # 重新从磁盘打开封好的 ZIP 读取该成员 —— 不是复用内存里的对象，
    # 也不是只看 Manifest 的声明值。（解压过程本身也会校验该成员的 CRC。）
    $zr = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    $entry = $zr.Entries | Where-Object { $_.FullName -eq $reportRel } | Select-Object -First 1
    $tmpActual = Join-Path $env:TEMP ('packedreport-' + [guid]::NewGuid().ToString('n') + '.md')
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmpActual, $true)
    $actualSha = (Get-FileHash -LiteralPath $tmpActual -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualBytes = (Get-Item -LiteralPath $tmpActual).Length
    $zr.Dispose()
    $expectedBytes = (Get-Item -LiteralPath $SelectedReport).Length
    Remove-Item $tmpActual -Force

    $reportIdentical = ($actualSha -eq $expectedSha)
    $reportCheck = if ($reportIdentical) { 'PASS' } else { 'REJECT_DIFFERENT' }
    $reportDetail = "包内 sha256=$actualSha ($actualBytes bytes) / 选定原件脱敏后 sha256=$expectedSha ($expectedBytes bytes)"
}
# README 与绑定表引用的包内路径必须真实存在
$refMissing = New-Object System.Collections.Generic.List[string]
$readme = Join-Path $StageDir '00-README.md'
if (Test-Path $readme) {
    foreach ($m in [regex]::Matches((Get-Content $readme -Raw -Encoding UTF8), '`([0-9A-Za-z_\-]+/[0-9A-Za-z_\-./]+)`')) {
        $rel = $m.Groups[1].Value
        if ($rel -match '\.(md|tsv|json|txt)$' -and -not $manifestRows.ContainsKey($rel) -and $rel -notmatch '^07-source-review/source') {
            $refMissing.Add($rel)
        }
    }
}

@(
    ('文件: ' + $BundleName),
    ('bytes: ' + $item.Length),
    ('sha256: ' + $sha256),
    ('crc32: ' + $crc),
    ('成员数: ' + $entries.Count),
    ('产品版本: ' + $version),
    ('构建提交（产物与证据所属）: ' + $productCommit),
    ('工具提交（当前 HEAD）: ' + $toolCommit),
    ('run_id: ' + $RunId),
    ('生成时间: ' + (Get-Date -Format 'o'))
) | Set-Content -LiteralPath ($zipPath + '.sha256.txt') -Encoding UTF8

Write-Host ''
Write-Host ("bundle : {0}" -f $zipPath)
Write-Host ("         {0:N0} bytes / {1} members / crc32 {2}" -f $item.Length, $entries.Count, $crc)
Write-Host ("sha256 : {0}" -f $sha256)
Write-Host ("重复路径: {0}  危险路径: {1}  成员核对不符: {2}  大小写冲突: {3}" -f $dupes.Count, $danger.Count, $mismatch.Count, $caseConflicts.Count)
Write-Host ("报告一致性检查: {0}  {1}" -f $reportCheck, $reportDetail)
Write-Host ("README 引用但包内缺失的路径: {0}" -f $refMissing.Count)
$mismatch | Select-Object -First 5 | ForEach-Object { Write-Host ('  - ' + $_) -ForegroundColor Yellow }
$refMissing | Select-Object -First 5 | ForEach-Object { Write-Host ('  - 缺失引用 ' + $_) -ForegroundColor Yellow }
if ($dupes.Count -gt 0 -or $danger.Count -gt 0 -or $mismatch.Count -gt 0 -or $caseConflicts.Count -gt 0 -or -not $reportIdentical -or $refMissing.Count -gt 0) { exit 2 }
