param(
    [string]$RunId = '',
    [string]$ExpectedVersion = '',
    [string]$ExpectedCommit = ''
)

$ErrorActionPreference = 'Stop'


# Produces the runtime identity of the FROZEN release artifact.
#
# The shell (LanDisk.exe) cannot be used here: it takes a per-user single-instance mutex that the Owner's own
# running LanDisk already holds, and this round is not allowed to change product source or stop the Owner's
# app. The server executable inside the frozen ZIP has no such mutex, so the release is started directly from
# the extracted artifact and asked to identify itself. What that leaves out (tray, single-instance behaviour)
# is stated in the result rather than implied away.

$repo = Split-Path -Parent $PSScriptRoot
if (-not $ExpectedVersion) { $ExpectedVersion = (Select-String -Path (Join-Path $repo 'src\LanDisk.App\LanDisk.App.csproj') -Pattern '<Version>(.*)</Version>').Matches.Groups[1].Value }
if (-not $ExpectedCommit) { $ExpectedCommit = (& git -C $repo rev-parse HEAD).Trim() }
. (Join-Path $PSScriptRoot 'Resolve-EvidenceRunId.ps1')
$runId = if ($RunId) { $RunId } else { Resolve-EvidenceRunId -Repo $repo }
$releaseDir = Get-ChildItem (Join-Path $repo "dist\evidence-11\$runId") -Directory -Recurse -Filter 'LanDisk-win-x64' -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'LanDisk.Server.exe') } | Select-Object -First 1
if (-not $releaseDir) { throw "在证据目录里找不到解压好的冻结发布包（LanDisk-win-x64）" }
$server = Join-Path $releaseDir.FullName 'LanDisk.Server.exe'
# canonical 位置是 05-runtime-identity（与 EvidenceVerdict / Assemble-Bundle02 / Finalize-Delivery /
# Make-SmallBundle / New-VerifiedReports / Test-EvidenceAggregator 一致）。
# 这里曾写成 04-runtime-identity，导致运行期必然报"必需证据缺失：runtime-identity.json"。
$outDir = Join-Path $repo "dist\evidence-11\$runId\05-runtime-identity"
$dataDir = Join-Path $repo "dist\evidence-11\scratch-runtime\data"
New-Item -ItemType Directory -Force -Path $outDir, $dataDir | Out-Null

$port = 18861
$httpsPort = 18862
while (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { $port += 2 }
while (Get-NetTCPConnection -LocalPort $httpsPort -State Listen -ErrorAction SilentlyContinue) { $httpsPort += 2 }

$zipSha = (Get-FileHash (Join-Path $repo 'dist\delivery\LanDisk-win-x64.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
$built = Get-Content (Join-Path (Split-Path $server -Parent) 'build-info.json') -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ("starting the frozen release: version {0}, commit {1}" -f $built.version, $built.commit)

$proc = Start-Process -FilePath $server `
    -ArgumentList @('--data-dir', $dataDir, '--port', $port, '--https-port', $httpsPort) `
    -WorkingDirectory (Split-Path $server -Parent) -PassThru -WindowStyle Hidden

$health = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/health" -TimeoutSec 3
        break
    } catch { }
}

$result = [ordered]@{
    generatedAt          = (Get-Date -Format 'o')
    method               = "把交付 ZIP（LanDisk-win-x64.zip）干净解压后，直接以 LanDisk.Server.exe --data-dir <隔离目录> --port $port --https-port $httpsPort 启动并读取 /api/health。"
    whyNotTheShell       = 'LanDisk.exe 会取一个每用户单实例互斥量，Owner 自己的 LanDisk 正在持有它；本轮不允许改产品源码，也不允许关掉 Owner 的实例，因此改用同一产物的服务端可执行文件。托盘与单实例行为不在本次证据范围内。'
    releaseZipSha256     = $zipSha
    artifactBuildVersion = $built.version
    artifactBuildCommit  = $built.commit
    httpPort             = $port
    httpsPort            = $httpsPort
    started              = $proc.Id
    healthRaw            = $health
    reportedVersion      = $health.version
    reportedCommit       = $health.buildCommit
    versionMatches       = ($health.version -eq $expectedVersion)
    commitMatches        = ($health.buildCommit -eq $expectedCommit)
}

$identity = Join-Path $dataDir 'install.json'
if (Test-Path $identity) {
    $result.installJson = (Get-Content $identity -Raw -Encoding UTF8 | ConvertFrom-Json)
}
foreach ($f in @('tls\landisk-ca.cer', 'tls\landisk-leaf.cer')) {
    $p = Join-Path $dataDir $f
    if (Test-Path $p) { $result[$f.Replace('\', '_')] = (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$result.dataRootFiles = @(Get-ChildItem $dataDir -Recurse -File | ForEach-Object { $_.FullName.Substring($dataDir.Length + 1) })

try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }

$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outDir 'runtime-identity.json') -Encoding UTF8
Write-Host ''
Write-Host ("reported version : {0}  (matches {1} = {2})" -f $health.version, $expectedVersion, $result.versionMatches)
Write-Host ("reported commit  : {0}  (matches {1} = {2})" -f $health.buildCommit, $expectedCommit.Substring(0,7), $result.commitMatches)
Write-Host ("written          : {0}" -f (Join-Path $outDir 'runtime-identity.json'))
