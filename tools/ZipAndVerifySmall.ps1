param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot)
)

# 封装并**重新从磁盘打开**逐成员校验精简包。
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path $Repo).Path
$Stage = Join-Path $Repo 'dist\.small01-stage'
$OutDir = Join-Path $Repo 'dist\delivery-final-code-repair-small'
$Zip = Join-Path $OutDir 'LAN_DISK_11_FINAL_CODE_REPAIR_SMALL_REVIEW_01.zip'
$Selected = Join-Path $Repo 'dist\delivery-176-final\DELIVERY_REPORT_11_VERIFIED.md'
. (Join-Path $Repo 'tools\Mask-NetworkIdentity.ps1')

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 成员数先核对：必须恰好 16 个（含 Manifest）
$stageFiles = @(Get-ChildItem -LiteralPath $Stage -File | Sort-Object Name)
if ($stageFiles.Count -ne 16) { throw ('暂存目录应有 16 个文件，实际 ' + $stageFiles.Count) }

[System.IO.Compression.ZipFile]::CreateFromDirectory($Stage, $Zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# ---- 重新打开校验
$z = [System.IO.Compression.ZipFile]::OpenRead($Zip)
$entries = @($z.Entries)
$dupes = @($entries | Group-Object FullName | Where-Object { $_.Count -gt 1 })
$caseConf = @($entries | Group-Object { $_.FullName.ToLowerInvariant() } | Where-Object { $_.Count -gt 1 })
$danger = @($entries | Where-Object { $_.FullName -match '^/|\.\./|^[A-Za-z]:|\\' })

$manLines = @(Get-Content -LiteralPath (Join-Path $Stage 'MANIFEST_SHA256.tsv') -Encoding UTF8 | Where-Object { $_ })
$manRows = @{}
foreach ($l in ($manLines | Select-Object -Skip 1)) { $c = $l -split "`t"; $manRows[$c[0]] = @{ bytes = [int64]$c[1]; sha = $c[2] } }

$mismatch = New-Object System.Collections.Generic.List[string]
foreach ($e in $entries) {
    if ($e.FullName -eq 'MANIFEST_SHA256.tsv') { continue }
    if (-not $manRows.ContainsKey($e.FullName)) { $mismatch.Add('清单缺少成员 ' + $e.FullName); continue }
    $tmp = Join-Path $env:TEMP ('sv-' + [guid]::NewGuid().ToString('n'))
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $tmp, $true)   # 解压会校验该成员 CRC
    $sha = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
    $len = (Get-Item -LiteralPath $tmp).Length
    Remove-Item -LiteralPath $tmp -Force
    if ($len -ne $manRows[$e.FullName].bytes) { $mismatch.Add('bytes 不符 ' + $e.FullName) }
    if ($sha -ne $manRows[$e.FullName].sha) { $mismatch.Add('sha256 不符 ' + $e.FullName) }
}
# 清单里多出的成员
foreach ($k in $manRows.Keys) {
    if (-not ($entries | Where-Object { $_.FullName -eq $k })) { $mismatch.Add('清单多出成员 ' + $k) }
}

# ---- 报告一致性：从 ZIP 实际读取 01_DELIVERY_REPORT.md，与选定原件（同规则脱敏后）逐字节比较
$reportEntry = $entries | Where-Object { $_.FullName -eq '01_DELIVERY_REPORT.md' } | Select-Object -First 1
$reportCheck = 'REJECT_MISSING_IN_PACKAGE'
$reportDetail = ''
if ($reportEntry) {
    $tmpA = Join-Path $env:TEMP ('rp-' + [guid]::NewGuid().ToString('n'))
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($reportEntry, $tmpA, $true)
    $aSha = (Get-FileHash -LiteralPath $tmpA -Algorithm SHA256).Hash.ToLowerInvariant()
    $aBytes = (Get-Item -LiteralPath $tmpA).Length
    Remove-Item -LiteralPath $tmpA -Force

    if (-not (Test-Path -LiteralPath $Selected)) { $reportCheck = 'REJECT_MISSING_SELECTED'; $reportDetail = $Selected }
    else {
        $tmpB = Join-Path $env:TEMP ('rm-' + [guid]::NewGuid().ToString('n'))
        $null = Mask-NetworkFile -SourcePath $Selected -DestinationPath $tmpB
        $bSha = (Get-FileHash -LiteralPath $tmpB -Algorithm SHA256).Hash.ToLowerInvariant()
        $bBytes = (Get-Item -LiteralPath $tmpB).Length
        Remove-Item -LiteralPath $tmpB -Force
        $reportCheck = if ($aSha -eq $bSha) { 'PASS' } else { 'REJECT_DIFFERENT' }
        $reportDetail = "包内 sha256=$aSha ($aBytes bytes) / 选定原件脱敏后 sha256=$bSha ($bBytes bytes)"
    }
}
$z.Dispose()

$item = Get-Item -LiteralPath $Zip
$zipSha = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ''
Write-Host ('ZIP      : ' + $Zip)
Write-Host ('bytes    : ' + $item.Length)
Write-Host ('sha256   : ' + $zipSha)
Write-Host ('成员数   : ' + $entries.Count + '   Manifest 记录数: ' + $manRows.Count)
Write-Host ('重复路径 : ' + $dupes.Count + '   大小写冲突: ' + $caseConf.Count + '   危险路径: ' + $danger.Count)
Write-Host ('成员核对不符: ' + $mismatch.Count)
Write-Host ('报告一致性: ' + $reportCheck + '  ' + $reportDetail)
$mismatch | Select-Object -First 5 | ForEach-Object { Write-Host ('  - ' + $_) }

if ($dupes.Count -gt 0 -or $caseConf.Count -gt 0 -or $danger.Count -gt 0 -or $mismatch.Count -gt 0 -or $reportCheck -ne 'PASS') { exit 2 }
