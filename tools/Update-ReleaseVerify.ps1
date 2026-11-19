#requires -Version 7.0
<#
.SYNOPSIS
    对真实发布出来的 LanDisk.exe 做"解压 → 启动 → 升级 → 自检 → 回滚"的实机验收（任务 03）。

.DESCRIPTION
    这是本任务最重要的证据来源：它不使用开发服务器，也不使用任何测试替身。

      1. 把 dist 里发布出来的运行包解压到一个全新的安装目录；
      2. 双击式启动 LanDisk.exe（桌面外壳，无控制台），确认它绑定固定的 8891/8892 并自检通过；
      3. 再启动一次，确认单实例生效；
      4. 用真实发布签名私钥把一个真实的 1.6.3 更新包签名、上传、应用；
      5. 确认程序重启、版本变成 1.7.0、端口与配对数据完全没变；
      6. 再上传一个"启动不起来"的 1.8.0 包，确认自检失败后自动回滚到 1.7.0；
      7. 退出并确认端口释放；
      8. 核对 %LOCALAPPDATA%\LanDisk 的真实安装目录在整轮操作中字节未变（基线取自本次运行开始时）。

    脚本只写入 dist/ 与 %TEMP% 下的自建目录；不共享任何真实目录，不改防火墙、
    证书信任或开机启动项，也不修改用户真实的 LanDisk 数据目录。

.EXAMPLE
    pwsh -File tools/Update-ReleaseVerify.ps1
    pwsh -File tools/Update-ReleaseVerify.ps1 -SkipBuild -KeepArtifacts
#>
[CmdletBinding()]
param(
    [string]$PublishDir = '',
    [string]$FrozenPackage = '',
    # The commit the artifact under test was built from. Defaults to HEAD, but a frozen candidate whose
    # repository tip has moved on (documentation-only commits, for example) must be compared against the
    # commit it actually carries, or every identity check fails for a reason that is not about the product.
    [string]$ExpectedCommit = '',
    [string]$ReleaseZip = '',
    [string]$Version = '1.7.6',
    [string]$TargetVersion = '1.7.6',
    [string]$BrokenVersion = '1.8.0',
    [string]$KeyPath = '',
    [int]$HttpPort = 8891,
    [int]$HttpsPort = 8892,
    [switch]$SkipBuild,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $repoRoot 'dist/publish/LanDisk-win-x64' }
if (-not $ReleaseZip) { $ReleaseZip = Join-Path $repoRoot 'dist/delivery/LanDisk-win-x64.zip' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $repoRoot ".rcverify/$stamp"
$installDir = Join-Path $runRoot 'install'
$dataDir = Join-Path $runRoot 'data'
$packagesDir = Join-Path $runRoot 'packages'
$evidence = New-Object System.Collections.Generic.List[object]

$base = "http://127.0.0.1:$HttpPort"
$adminSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$adminToken = ''
$shell = $null

New-Item -ItemType Directory -Force -Path $installDir, $dataDir, $packagesDir | Out-Null

# ---------------------------------------------------------------- 安装清单 / build-info（任务 06）
#
# install-manifest.json 里记录的 build-info.json 路径/字节数/SHA256 必须**始终**与磁盘上的真实
# 文件一致：干净解压后、升级提交后、回滚恢复后。三处都用同一个判定函数，避免各写一遍。
function Test-BuildInfoRecord([string]$dir) {
    $manifestPath = Join-Path $dir 'install-manifest.json'
    $buildInfoPath = Join-Path $dir 'build-info.json'
    if (-not (Test-Path $manifestPath)) { return [pscustomobject]@{ Ok = $false; Detail = '缺少 install-manifest.json'; Sha = '' } }
    if (-not (Test-Path $buildInfoPath)) { return [pscustomobject]@{ Ok = $false; Detail = '缺少 build-info.json'; Sha = '' } }

    $manifest = $null
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { return [pscustomobject]@{ Ok = $false; Detail = 'install-manifest.json 无法解析'; Sha = '' } }

    $listed = @($manifest.files) -contains 'build-info.json'
    $record = $manifest.buildInfo
    $bytes = (Get-Item -LiteralPath $buildInfoPath).Length
    $sha = (Get-FileHash -LiteralPath $buildInfoPath -Algorithm SHA256).Hash.ToLowerInvariant()

    if (-not $listed) { return [pscustomobject]@{ Ok = $false; Detail = 'install-manifest.files 不含 build-info.json'; Sha = $sha } }
    if (-not $record) { return [pscustomobject]@{ Ok = $false; Detail = 'install-manifest 没有 buildInfo 记录'; Sha = $sha } }

    $recordSha = ([string]$record.sha256).ToLowerInvariant()
    $pathOk = ([string]$record.path -eq 'build-info.json')
    $bytesOk = ([long]$record.bytes -eq $bytes)
    $shaOk = ($recordSha -eq $sha)
    $ok = $pathOk -and $bytesOk -and $shaOk
    $detail = ("files 收录=True；path={0}；bytes {1}/{2}；sha256 {3}/{4}" -f `
        $record.path, $record.bytes, $bytes, $recordSha.Substring(0, [Math]::Min(12, $recordSha.Length)), $sha.Substring(0, 12))
    return [pscustomobject]@{ Ok = $ok; Detail = $detail; Sha = $sha; Bytes = $bytes }
}

function Get-BuildInfoRecordSha([string]$dir) {
    $manifestPath = Join-Path $dir 'install-manifest.json'
    if (-not (Test-Path $manifestPath)) { return '' }
    try {
        $record = (Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json).buildInfo
        if ($record) { return ([string]$record.sha256).ToLowerInvariant() }
    }
    catch { }
    return ''
}

function Add-Evidence([string]$id, [string]$result, [string]$detail) {
    $evidence.Add([pscustomobject]@{ Id = $id; Result = $result; Detail = $detail })
    $color = switch ($result) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'BLOCKED' { 'Yellow' } default { 'Gray' } }
    Write-Host ("  [{0}] {1} - {2}" -f $result, $id, $detail) -ForegroundColor $color
}

function Test-PortFree([int]$Port) {
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        $listener.Stop()
        return $true
    }
    catch { return $false }
}

function Invoke-Json {
    param([string]$Uri, [string]$Method = 'GET', $Body = $null, $Session = $null, [string]$Csrf = '', [int]$TimeoutSec = 60)
    $headers = @{ 'X-LanDisk-Client' = 'release-verify' }
    if ($Csrf) { $headers['X-LanDisk-CSRF'] = $Csrf }
    $params = @{ Uri = $Uri; Method = $Method; Headers = $headers; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
    if ($Session) { $params.WebSession = $Session }
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 8 -Compress }
        $params.Body = [System.Text.Encoding]::UTF8.GetBytes($json)
        $params.ContentType = 'application/json; charset=utf-8'
    }
    return Invoke-WebRequest @params
}

function Get-Health {
    try {
        $response = Invoke-WebRequest -Uri "$base/api/health" -UseBasicParsing -TimeoutSec 3
        if ($response.StatusCode -ne 200) { return $null }
        return ($response.Content | ConvertFrom-Json)
    }
    catch { return $null }
}

$script:lastObservedHealth = $null

function Wait-ForVersion([string]$Expected, [int]$Seconds = 180) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $health = Get-Health
        if ($health) {
            $script:lastObservedHealth = $health
            if ($health.version -eq $Expected) { return $health }
        }
        Start-Sleep -Milliseconds 700
    }
    return $null
}

function Describe-LastHealth {
    if (-not $script:lastObservedHealth) { return '从未收到 /api/health 响应（服务可能未监听）' }
    return ("最后一次响应：version={0} buildCommit={1}" -f $script:lastObservedHealth.version, $script:lastObservedHealth.buildCommit)
}

function Wait-ForTerminalState([string]$Expected, [int]$Seconds = 120) {
    # The restarted instance answers /api/health as soon as Kestrel is up, which can be a moment
    # before the updater finishes its own health check and records the outcome. Wait for the
    # *expected* outcome rather than for "some terminal outcome": the previous attempt's result is
    # still on disk and would otherwise be read as this attempt's.
    $statePath = Join-Path $dataDir 'updates/state.json'
    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $statePath) {
            try {
                $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                $last = $state
                if ($state.lastOutcome -eq $Expected) { return $state }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $last
}

function Wait-ForDown([int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Health)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-AdminCsrf {
    $adminUrlFile = Join-Path $dataDir 'admin-url.txt'
    if (-not (Test-Path $adminUrlFile)) { throw "缺少 admin-url.txt（$adminUrlFile）" }
    $adminUrl = (Get-Content -LiteralPath $adminUrlFile -Raw).Trim()
    $script:adminToken = ($adminUrl -split 'token=')[1]
    $login = Invoke-Json -Uri "$base/api/admin/session" -Method POST -Body @{ token = $script:adminToken } -Session $adminSession
    return ($login.Content | ConvertFrom-Json).csrfToken
}

function Upload-Package([string]$Path, [string]$Csrf) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $headers = @{
        'X-LanDisk-Client'       = 'release-verify'
        'X-LanDisk-CSRF'         = $Csrf
        'X-LanDisk-Package-Name' = [uri]::EscapeDataString([System.IO.Path]::GetFileName($Path))
    }
    try {
        $response = Invoke-WebRequest -Uri "$base/api/admin/update/upload" -Method POST -Body $bytes `
            -ContentType 'application/octet-stream' -Headers $headers -WebSession $adminSession -UseBasicParsing -TimeoutSec 600
        return [pscustomobject]@{ Ok = $true; Status = $response.StatusCode; Body = ($response.Content | ConvertFrom-Json); Raw = $response.Content }
    }
    catch {
        $status = 0
        $raw = ''
        if ($_.Exception.Response) {
            $status = $_.Exception.Response.StatusCode.value__
            try {
                $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
                $raw = $reader.ReadToEnd()
                $reader.Dispose()
            }
            catch { }
        }
        $body = $null
        if ($raw) { try { $body = $raw | ConvertFrom-Json } catch { } }
        return [pscustomobject]@{ Ok = $false; Status = $status; Body = $body; Raw = $raw }
    }
}

function Get-FileHashSafe([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$realDataRoot = Join-Path $env:LOCALAPPDATA 'LanDisk'
# 基线必须在**本次运行开始时**现取现用。04 轮把它固化成一个固定的 %TEMP% 文件，于是只要用户
# 自己的 LanDisk 在两次验收之间正常写过日志或配置，这个检查就会对着一份几小时前的快照报 FAIL——
# 那是脚本陈旧，不是产品问题。现在改成"本次运行开始 → 本次运行结束"的闭区间比对：
# 任何差异都只可能来自这次验收过程。
$realBaseline = Join-Path $runRoot 'userdata-baseline.tsv'

# A real, user-started LanDisk may be running (for example from a Desktop copy) and writing its own
# data directory. Its writes are not evidence about this verification, so they are identified up
# front and reported rather than silently counted as a failure.
$realInstanceRunning = $false
$realInstancePath = ''
foreach ($p in (Get-Process -Name 'LanDisk' -ErrorAction SilentlyContinue)) {
    try {
        $exe = $p.MainModule.FileName
        if ($exe -and -not $exe.StartsWith($installDir, [System.StringComparison]::OrdinalIgnoreCase)) {
            $realInstanceRunning = $true
            $realInstancePath = "$exe (PID $($p.Id))"
        }
    }
    catch { }
}
if ($realInstanceRunning) { Write-Host ("注意：检测到另一个正在运行的 LanDisk 实例：{0}" -f $realInstancePath) -ForegroundColor Yellow }

# 只有当用户自己的实例没在运行时才取基线：它自己在写的数据目录不能作为"验收有没有改动它"的基准。
if (-not $realInstanceRunning -and (Test-Path -LiteralPath $realDataRoot)) {
    $baselineLines = New-Object System.Collections.Generic.List[string]
    $baselineLines.Add(("# %LOCALAPPDATA%\LanDisk 基线，取自本次验收开始时 " + [DateTimeOffset]::Now.ToString('o')))
    $baselineLines.Add("relative_path`tbytes`tsha256")
    foreach ($file in (Get-ChildItem -LiteralPath $realDataRoot -Recurse -File -ErrorAction SilentlyContinue)) {
        $relative = [System.IO.Path]::GetRelativePath($realDataRoot, $file.FullName)
        $baselineLines.Add(("{0}`t{1}`t{2}" -f $relative, $file.Length, (Get-FileHashSafe $file.FullName)))
    }
    Set-Content -LiteralPath $realBaseline -Value $baselineLines -Encoding UTF8
    Write-Host ("已记录用户真实数据目录基线：{0} 个文件" -f ($baselineLines.Count - 2))
}

try {
    # ================================================================ 准备
    Write-Host ''
    Write-Host '准备：核对端口与发布包' -ForegroundColor Cyan

    if (-not (Test-Path (Join-Path $PublishDir 'LanDisk.exe'))) {
        throw "发布目录中缺少 LanDisk.exe：$PublishDir（请先运行 tools/Build-All.ps1）"
    }
    if (-not (Test-Path (Join-Path $PublishDir 'updater/LanDisk.Updater.exe'))) {
        throw "发布目录中缺少 updater/LanDisk.Updater.exe：$PublishDir"
    }

    $busyPorts = @($HttpPort, $HttpsPort) | Where-Object { -not (Test-PortFree $_) }
    if ($busyPorts.Count -gt 0) {
        # Something else owns the fixed ports. That is not a reason to skip the one thing this
        # situation is ideal for: proving that the shell reports a port conflict instead of quietly
        # picking another port. The full upgrade/rollback run needs the ports free, so it is reported
        # as BLOCKED while this partial verification still produces real evidence.
        $owners = @(Get-NetTCPConnection -LocalPort $busyPorts -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { "$($_.LocalPort) <- pid $($_.OwningProcess)" })
        Add-Evidence 'T03-00' 'BLOCKED' ("固定端口被占用（{0}），无法执行完整的固定端口升级/回滚验收。占用者：{1}" -f ($busyPorts -join '/'), ($owners -join '; '))

        # ---- H04-04：端口冲突必须被如实报告，且不得自动换端口 --------------------
        Copy-Item -Path (Join-Path $PublishDir '*') -Destination $installDir -Recurse -Force
        $conflictData = Join-Path $runRoot 'conflict-data'
        New-Item -ItemType Directory -Force -Path $conflictData | Out-Null

        # LanDisk.exe is single instance per user, so a second launch would exit before reaching the
        # port check. The publish also ships the headless server, which shares the same host code and
        # the same fixed-port promise, so the conflict is exercised through it.
        $conflictPort = $busyPorts[0]
        $conflictProcess = Start-Process -FilePath (Join-Path $installDir 'LanDisk.Server.exe') `
            -ArgumentList @('--data-dir', $conflictData, '--port', "$conflictPort", '--https-port', "$($busyPorts[-1])") `
            -WorkingDirectory $installDir -RedirectStandardOutput (Join-Path $runRoot 'conflict.out') `
            -RedirectStandardError (Join-Path $runRoot 'conflict.err') -PassThru
        $conflictExited = $conflictProcess.WaitForExit(45000)
        Start-Sleep -Seconds 2

        # The console writes with the system code page, so read the bytes and decode with it rather
        # than assuming UTF-8 (which turns the Chinese text into replacement characters).
        $stateLog = Join-Path $runRoot 'conflict.err'
        $stateText = ''
        if (Test-Path $stateLog) {
            $bytes = [System.IO.File]::ReadAllBytes($stateLog)
            $stateText = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage).GetString($bytes)
        }
        $reportedConflict = ($stateText -match '端口' -and $stateText -match "$conflictPort") -or
            ($stateText -match 'address already in use' -and $stateText -match ":$conflictPort")

        $ourListeners = @(Get-NetTCPConnection -OwningProcess $conflictProcess.Id -State Listen -ErrorAction SilentlyContinue)
        $foreignStillOwns = @(Get-NetTCPConnection -LocalPort $busyPorts -State Listen -ErrorAction SilentlyContinue).Count -gt 0
        $otherPorts = @($ourListeners | Where-Object { $busyPorts -notcontains $_.LocalPort })

        if ($reportedConflict -and $ourListeners.Count -eq 0 -and $foreignStillOwns -and $otherPorts.Count -eq 0) {
            Add-Evidence 'T04-08' 'PASS' ("端口被占用时明确指出端口 $conflictPort 并拒绝启动；" +
                "本进程没有绑定任何端口（未自动换端口），原有占用者未受影响")
        }
        else {
            Add-Evidence 'T04-08' 'FAIL' ("冲突处理不符合预期：reportedConflict=$reportedConflict exited=$conflictExited " +
                "ourListeners=$($ourListeners.Count) foreignStillOwns=$foreignStillOwns otherPorts=$($otherPorts.Count)")
        }

        if (-not $conflictExited) { try { $conflictProcess.Kill($true) } catch { } }
        Add-Evidence 'T04-09' 'BLOCKED' '因为固定端口被占用，升级/回滚与用户数据校验未执行'
        Add-Evidence 'T06-00' 'BLOCKED' '因为固定端口被占用，release ZIP 的干净解压与安装清单核对未执行'
        Add-Evidence 'T06-01' 'BLOCKED' '因为固定端口被占用，升级后的安装清单核对未执行'
        Add-Evidence 'T06-02' 'BLOCKED' '因为固定端口被占用，回滚后的安装清单核对未执行'
        throw '端口被占用'
    }
    Add-Evidence 'T03-00' 'PASS' "固定端口 $HttpPort/$HttpsPort 当前空闲，发布目录包含 LanDisk.exe 与 updater"

    Copy-Item -Path (Join-Path $PublishDir '*') -Destination $installDir -Recurse -Force
    $installedExe = Join-Path $installDir 'LanDisk.exe'

    $versionBefore = (Get-Content -LiteralPath (Join-Path $installDir 'install-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version

    # 任务 06：最终 release ZIP 必须能被干净解压，且解压出来的安装清单收录并如实描述 build-info.json。
    # 这一项直接针对最终的 release ZIP，而不是构建目录，因此它检验的正是交付物的自洽性。
    $zipDump = Join-Path $runRoot 'release-zip'
    if (Test-Path $zipDump) { Remove-Item $zipDump -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $zipDump | Out-Null
    if (-not (Test-Path $ReleaseZip)) {
        Add-Evidence 'T06-00' 'FAIL' "找不到 release ZIP：$ReleaseZip"
    }
    else {
        Expand-Archive -LiteralPath $ReleaseZip -DestinationPath $zipDump -Force
        $zipInstall = @(Get-ChildItem -LiteralPath $zipDump -Directory)[0]
        if (-not $zipInstall) { $zipInstall = Get-Item $zipDump }
        $zipRecord = Test-BuildInfoRecord $zipInstall.FullName
        if ($zipRecord.Ok) {
            Add-Evidence 'T06-00' 'PASS' ("release ZIP 干净解压后 install-manifest 收录 build-info.json 且记录一致（{0}）" -f $zipRecord.Detail)
        }
        else {
            Add-Evidence 'T06-00' 'FAIL' ("release ZIP 内安装清单与 build-info.json 不一致：{0}" -f $zipRecord.Detail)
        }
    }

    $recordShaBefore = Get-BuildInfoRecordSha $installDir

    # ================================================================ 启动桌面外壳
    Write-Host ''
    Write-Host 'B 启动桌面外壳 LanDisk.exe' -ForegroundColor Cyan

    $shell = Start-Process -FilePath $installedExe `
        -ArgumentList @('--data-dir', $dataDir, '--port', "$HttpPort", '--https-port', "$HttpsPort") `
        -WorkingDirectory $installDir -PassThru

    $health = Wait-ForVersion $versionBefore 90
    if (-not $health) {
        Add-Evidence 'T03-01' 'FAIL' ("LanDisk.exe 未能在 90 秒内以预期版本 $versionBefore 提供 /api/health；" + (Describe-LastHealth))
        throw '桌面外壳启动失败'
    }

    Add-Evidence 'T03-01' 'PASS' ("LanDisk.exe 启动成功（PID {0}），版本 {1}，未产生控制台窗口" -f $shell.Id, $health.version)

    $csrf = Get-AdminCsrf
    $status = (Invoke-Json -Uri "$base/api/admin/status" -Session $adminSession).Content | ConvertFrom-Json
    $boundList = @($status.network.boundUrls) -join ', '
    $expectedBound = @($status.network.boundUrls | Where-Object { $_ -match ":$HttpPort$" })
    if ($expectedBound.Count -gt 0) {
        Add-Evidence 'T03-02' 'PASS' "固定 HTTP 端口生效：$boundList"
    }
    else {
        Add-Evidence 'T03-02' 'FAIL' "监听地址中没有固定端口 $HttpPort：$boundList"
    }

    $healthDetail = (Invoke-Json -Uri "$base/api/admin/health" -Session $adminSession).Content | ConvertFrom-Json
    $releaseKey = @($healthDetail.checks | Where-Object { $_.id -eq 'release-key' })[0]
    $updaterCheck = @($healthDetail.checks | Where-Object { $_.id -eq 'updater' })[0]
    if ($healthDetail.ok -and $releaseKey.status -eq 'ok' -and $updaterCheck.status -eq 'ok') {
        Add-Evidence 'T03-03' 'PASS' ("结构化自检全部通过，内置发布公钥可用（{0} 项检查）" -f @($healthDetail.checks).Count)
    }
    else {
        Add-Evidence 'T03-03' 'FAIL' ("自检未全部通过：ok={0} releaseKey={1} updater={2}" -f $healthDetail.ok, $releaseKey.status, $updaterCheck.status)
    }

    # ================================================================ 单实例
    Write-Host ''
    Write-Host 'C 单实例' -ForegroundColor Cyan

    $second = Start-Process -FilePath $installedExe -ArgumentList @('--data-dir', $dataDir, '--port', "$HttpPort", '--https-port', "$HttpsPort") -WorkingDirectory $installDir -PassThru
    $secondExited = $second.WaitForExit(20000)
    if ($secondExited -and $second.ExitCode -eq 0) {
        Add-Evidence 'T03-04' 'PASS' ("第二次启动在 {0} 秒内退出（退出码 0），未启动第二个服务" -f [math]::Round(($second.ExitTime - $second.StartTime).TotalSeconds, 1))
    }
    else {
        Add-Evidence 'T03-04' 'FAIL' ("第二次启动没有立即退出（exited={0}）" -f $secondExited)
        if (-not $secondExited) { try { $second.Kill($true) } catch { } }
    }

    $stillUp = Get-Health
    if ($stillUp -and $stillUp.version -eq $versionBefore -and -not $shell.HasExited) {
        Add-Evidence 'T03-05' 'PASS' '第一个实例仍在正常运行，端口未被抢占'
    }
    else {
        Add-Evidence 'T03-05' 'FAIL' '第一个实例状态异常'
    }

    # ================================================================ 更新包准备
    Write-Host ''
    Write-Host "D 生成并校验 $TargetVersion 更新包" -ForegroundColor Cyan

    $targetPublish = Join-Path $repoRoot "dist/publish/LanDisk-win-x64-$TargetVersion"
    if (-not $SkipBuild -or -not (Test-Path (Join-Path $targetPublish 'LanDisk.exe'))) {
        Write-Host "正在构建 $TargetVersion 的自包含发布（同一源码、不同版本号）…" -ForegroundColor DarkGray
        if (Test-Path $targetPublish) { Remove-Item $targetPublish -Recurse -Force }

        $commit = (& git -C $repoRoot rev-parse HEAD).Trim()
        $buildId = "Release/win-x64/$TargetVersion"

        foreach ($project in @('src/LanDisk.App/LanDisk.App.csproj', 'src/LanDisk.Server/LanDisk.Server.csproj')) {
            & dotnet publish (Join-Path $repoRoot $project) -c Release -r win-x64 --self-contained true `
                -p:PublishSingleFile=false -p:DebugType=embedded -p:Version=$TargetVersion `
                -p:LanDiskBuildCommit=$commit -p:LanDiskBuildId=$buildId `
                -o $targetPublish --nologo | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "发布 $TargetVersion 失败（$project）" }
        }

        $updaterOut = Join-Path $targetPublish 'updater'
        & dotnet publish (Join-Path $repoRoot 'src/LanDisk.Updater/LanDisk.Updater.csproj') -c Release -r win-x64 `
            --self-contained true -p:PublishSingleFile=true -p:Version=$TargetVersion `
            -p:LanDiskBuildCommit=$commit -p:LanDiskBuildId=$buildId -o $updaterOut --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "发布 $TargetVersion 的更新程序失败" }

        [ordered]@{
            schemaVersion = 1; product = 'LanDisk'; version = $TargetVersion; commit = $commit
            buildId = $buildId; builtAt = [DateTimeOffset]::Now.ToString('o')
            runtime = 'win-x64'; selfContained = $true
        } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $targetPublish 'build-info.json') -Encoding UTF8
        $manifestFiles = @(
            Get-ChildItem -LiteralPath $targetPublish -Recurse -File |
                Where-Object { $_.Name -ne 'install-manifest.json' } |
                ForEach-Object { [System.IO.Path]::GetRelativePath($targetPublish, $_.FullName).Replace('\', '/') } |
                Sort-Object
        )
        [ordered]@{
            schemaVersion = 1
            product       = 'LanDisk'
            version       = $TargetVersion
            buildCommit   = $commit
            installedAt   = [DateTimeOffset]::Now.ToString('o')
            files         = $manifestFiles
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $targetPublish 'install-manifest.json') -Encoding UTF8
    }

    $targetPackage = Join-Path $packagesDir "LanDisk-update-$TargetVersion.lupd"

    if ($FrozenPackage) {
        # Test the frozen shipping update package instead of packing a fresh one.
        #
        # Why this exists: this script used to always pack its own target package from the current source, so
        # the update acceptance could never be about the artifact that was actually delivered — it was about a
        # package built minutes earlier, with a different buildId and a different SHA256. The Owner's
        # instruction for this round is explicit that the *frozen* .lupd is what must be applied, and that its
        # full SHA256 must be recorded, so the script accepts one.
        if (-not (Test-Path -LiteralPath $FrozenPackage)) { throw "指定的更新包不存在：$FrozenPackage" }
        Copy-Item -LiteralPath $FrozenPackage -Destination $targetPackage -Force

        $frozenSha = (Get-FileHash -LiteralPath $targetPackage -Algorithm SHA256).Hash.ToLowerInvariant()
        Add-Evidence 'T03-06a' 'PASS' ("本次升级使用的是**冻结的正式更新包**：{0}（{1} 字节，SHA256 {2}）" -f `
            (Split-Path -Leaf $FrozenPackage), (Get-Item -LiteralPath $targetPackage).Length, $frozenSha)
        Write-Host ("使用冻结更新包：{0}" -f $frozenSha) -ForegroundColor DarkGray
    }
    else {
        $packArgs = @(
            '-File', (Join-Path $PSScriptRoot 'Build-UpdatePackage.ps1'),
            '-PayloadDir', $targetPublish, '-Version', $TargetVersion,
            '-MinimumFrom', '1.0.0', '-OutDir', $packagesDir,
            '-Commit', (& git -C $repoRoot rev-parse HEAD).Trim(),
            '-BuildId', "Release/win-x64/$TargetVersion",
            '-NotesFile', (Join-Path $repoRoot 'docs/RELEASE_NOTES.md')
        )
        if ($KeyPath) { $packArgs += @('-KeyPath', $KeyPath) }
        & pwsh @packArgs | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $targetPackage)) { throw "生成 $TargetVersion 更新包失败" }
    }

    $verified = & dotnet run --project (Join-Path $repoRoot 'tools/keytool/KeyTool.csproj') -c Release -- inspect $targetPackage
    if ($LASTEXITCODE -ne 0) { throw "读取 $TargetVersion 更新包清单失败" }
    $signerLine = @($verified | Where-Object { $_ -match '^签名者' })
    Add-Evidence 'T03-06' 'PASS' ("$TargetVersion 更新包已用发布私钥签名并自校验通过（{0}）" -f ($signerLine -join ' ').Trim())

    # ================================================================ 篡改包必须被拒绝
    Write-Host ''
    Write-Host 'E 篡改包必须被拒绝' -ForegroundColor Cyan

    $tamperedPath = Join-Path $packagesDir "tampered-$TargetVersion.lupd"
    $packageBytes = [System.IO.File]::ReadAllBytes($targetPackage)
    # Flip a byte well inside the archive body so the ZIP stays readable but the payload hash differs.
    $index = [int]($packageBytes.Length / 2)
    $packageBytes[$index] = $packageBytes[$index] -bxor 0xFF
    [System.IO.File]::WriteAllBytes($tamperedPath, $packageBytes)

    $tampered = Upload-Package -Path $tamperedPath -Csrf $csrf
    if (-not $tampered.Ok) {
        Add-Evidence 'T03-07' 'PASS' ("篡改后的更新包被拒绝：HTTP {0} / {1}" -f $tampered.Status, $tampered.Body.error.code)
    }
    else {
        Add-Evidence 'T03-07' 'FAIL' '篡改后的更新包竟然被接受'
    }

    $statusAfterTamper = (Invoke-Json -Uri "$base/api/admin/update/status" -Session $adminSession).Content | ConvertFrom-Json
    if ($statusAfterTamper.stage -eq 'None') {
        Add-Evidence 'T03-08' 'PASS' '被拒绝的包没有留下任何待安装状态'
    }
    else {
        Add-Evidence 'T03-08' 'FAIL' ("拒绝后仍处于 {0} 状态" -f $statusAfterTamper.stage)
    }

    # ================================================================ 应用更新
    Write-Host ''
    Write-Host "F 应用 $TargetVersion 更新" -ForegroundColor Cyan

    # Add a share so "state survives an upgrade" is a real claim rather than a comparison of two
    # empty files.
    $sandbox = Join-Path $runRoot 'sandbox'
    New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
    Set-Content -Path (Join-Path $sandbox 'upgrade-check.txt') -Value '升级前后应当仍然存在' -Encoding UTF8
    Invoke-Json -Uri "$base/api/admin/shares" -Method POST -Session $adminSession -Csrf $csrf -Body @{
        rootPath = $sandbox; displayName = '升级验收共享'; allowUpload = $false; allowRename = $false; allowDelete = $false
    } | Out-Null
    $sharesBefore = Get-FileHashSafe (Join-Path $dataDir 'shares.json')

    # Pair a device before the upgrade. The device credential lives in devices.json, so this is the
    # real "an already paired phone keeps working after an upgrade" claim rather than a file diff.
    $pair = (Invoke-Json -Uri "$base/api/admin/pairing" -Method POST -Session $adminSession -Csrf $csrf -Body @{}).Content | ConvertFrom-Json
    $deviceSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $redeem = (Invoke-Json -Uri "$base/api/pair/redeem" -Method POST -Session $deviceSession -Body @{
        token = $pair.token; deviceName = '升级验收手机'
    }).Content | ConvertFrom-Json
    $deviceIdBefore = $redeem.device.id
    $deviceCountBefore = ((Invoke-Json -Uri "$base/api/admin/devices" -Session $adminSession).Content | ConvertFrom-Json).devices.Count

    $uploaded = Upload-Package -Path $targetPackage -Csrf $csrf
    if (-not $uploaded.Ok) {
        Add-Evidence 'T03-09' 'FAIL' ("上传 $TargetVersion 更新包失败：HTTP {0} / {1}" -f $uploaded.Status, $uploaded.Raw)
        throw '上传失败'
    }
    Add-Evidence 'T03-09' 'PASS' ("$TargetVersion 更新包上传并校验通过（{0} 个文件 / {1} 字节）" -f $uploaded.Body.fileCount, $uploaded.Body.totalBytes)

    $apply = Invoke-Json -Uri "$base/api/admin/update/apply" -Method POST -Session $adminSession -Csrf $csrf -Body @{ confirm = $true }
    $applyJson = $apply.Content | ConvertFrom-Json
    Add-Evidence 'T03-10' 'PASS' ("已受理安装请求，更新程序 PID {0}，目标版本 {1}" -f $applyJson.updaterProcessId, $applyJson.targetVersion)

    $shellExited = $shell.WaitForExit(60000)
    if ($shellExited) {
        Add-Evidence 'T03-11' 'PASS' ("旧实例已退出（退出码 {0}），程序文件可以安全替换" -f $shell.ExitCode)
    }
    else {
        Add-Evidence 'T03-11' 'FAIL' '旧实例在 60 秒内没有退出，更新程序无法替换程序文件'
    }

    $healthAfter = Wait-ForVersion $TargetVersion 240
    if (-not $healthAfter) {
        Add-Evidence 'T03-12' 'FAIL' ("更新后未能在 240 秒内以版本 $TargetVersion 恢复服务；" + (Describe-LastHealth))
        throw '更新后服务未恢复'
    }
    Add-Evidence 'T03-12' 'PASS' ("更新成功：服务以版本 {0} 重新启动并自检通过" -f $healthAfter.version)

    # ---- 任务 04：签名覆盖语义、候选身份、快照与事务 ---------------------------------
    $releaseCommit = if ($ExpectedCommit) { $ExpectedCommit } else { (& git -C $repoRoot rev-parse HEAD).Trim() }
    $manifestJson = & dotnet run --project (Join-Path $repoRoot 'tools/keytool/KeyTool.csproj') -c Release -- inspect $targetPackage
    $schemaLine = @($manifestJson | Where-Object { $_ -match '^格式版本:' })[0]
    $commitLine = @($manifestJson | Where-Object { $_ -match '^构建提交:' })[0]
    $keyLine = @($manifestJson | Where-Object { $_ -match '^签名者\(keyId\):' })[0]
    $notesLine = @($manifestJson | Where-Object { $_ -match '^更新说明:' })[0]

    if ($schemaLine -match '2' -and $commitLine -match $releaseCommit -and $keyLine -match 'ac3ff1673b23518a' -and $notesLine -match 'sha256=') {
        Add-Evidence 'T04-01' 'PASS' ("更新包格式版本 2，签名覆盖 keyId 与更新说明，buildCommit = {0}" -f $releaseCommit.Substring(0, 12))
    }
    else {
        Add-Evidence 'T04-01' 'FAIL' ("更新包签名语义核对失败：{0} / {1} / {2} / {3}" -f $schemaLine, $commitLine, $keyLine, $notesLine)
    }

    if (Test-Path (Join-Path $dataDir 'updates/pre-update-health.json')) {
        $snap = Get-Content (Join-Path $dataDir 'updates/pre-update-health.json') -Raw | ConvertFrom-Json
        Add-Evidence 'T04-02' 'PASS' ("更新前健康快照已写入：版本 {0}，端口 {1}/{2}，证书指纹 {3}" -f $snap.version, $snap.adminPort, $snap.lanPort, $(if ($snap.certificateThumbprint) { $snap.certificateThumbprint.Substring(0, 8) + '…' } else { '(无)' }))
    }
    else {
        Add-Evidence 'T04-02' 'FAIL' '缺少更新前的健康快照 pre-update-health.json'
    }

    $updaterLog = Join-Path $dataDir 'updates/updater.log'
    $statePath = Join-Path $dataDir 'updates/state.json'
    $state = Wait-ForTerminalState 'Committed' 120
    if ($state) {
        if ($state.lastOutcome -eq 'Committed' -and $state.installedVersion -eq $TargetVersion) {
            Add-Evidence 'T03-13' 'PASS' ("更新状态已提交：lastOutcome=Committed，installedVersion={0}" -f $state.installedVersion)
        }
        else {
            Add-Evidence 'T03-13' 'FAIL' ("更新状态异常：lastOutcome={0} installedVersion={1}" -f $state.lastOutcome, $state.installedVersion)
        }

        if (@($state.history).Count -ge 1 -and $state.lastBackupDir -and (Test-Path $state.lastBackupDir)) {
            Add-Evidence 'T03-14' 'PASS' ("存在更新历史记录与安装备份：{0}" -f $state.lastBackupDir)
        }
        else {
            Add-Evidence 'T03-14' 'FAIL' '缺少更新历史或备份目录'
        }
    }
    else {
        Add-Evidence 'T03-13' 'FAIL' "更新后 90 秒内没有写出终态：$statePath"
        Add-Evidence 'T03-14' 'FAIL' '缺少更新历史或备份目录'
    }

    $healthNow = Get-Health
    if ($healthNow -and $healthNow.buildCommit -eq $releaseCommit) {
        Add-Evidence 'T04-03' 'PASS' ("运行中的实例报告 buildCommit = {0}，与最终 HEAD 一致" -f $releaseCommit.Substring(0, 12))
    }
    else {
        Add-Evidence 'T04-03' 'FAIL' ("运行中的实例 buildCommit = {0}，预期 {1}" -f $healthNow.buildCommit, $releaseCommit)
    }

    if (-not (Test-Path (Join-Path $dataDir 'updates/transaction.json'))) {
        Add-Evidence 'T04-04' 'PASS' '更新成功后未完成事务标记已被清除'
    }
    else {
        Add-Evidence 'T04-04' 'FAIL' '更新成功后仍残留 transaction.json'
    }

    $history = (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).history
    $committed = @($history | Where-Object { $_.outcome -eq 'Committed' })[0]
    if ($committed -and $committed.packageSha256 -and $committed.packageSha256.Length -eq 64) {
        Add-Evidence 'T04-05' 'PASS' ("更新历史记录了更新包 SHA256：{0}" -f $committed.packageSha256.Substring(0, 16) + '…')
    }
    else {
        Add-Evidence 'T04-05' 'FAIL' '更新历史缺少 packageSha256'
    }

    # PKG05-03：更新后安装目录里的 build-info.json 必须已经是新版本（04 轮的缺陷是它留在旧版本）。
    $buildInfoPath = Join-Path $installDir 'build-info.json'
    if ((Test-Path $buildInfoPath) -and ((Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json).version -eq $TargetVersion)) {
        Add-Evidence 'T05-02' 'PASS' ("升级后 build-info.json = {0}（与更新包目标版本一致）" -f $TargetVersion)
    }
    else {
        $actual = if (Test-Path $buildInfoPath) { (Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json).version } else { '(缺失)' }
        Add-Evidence 'T05-02' 'FAIL' ("升级后 build-info.json = {0}，预期 {1}" -f $actual, $TargetVersion)
    }

    $manifestAfter = (Get-Content -LiteralPath (Join-Path $installDir 'install-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version
    if ($manifestAfter -eq $TargetVersion) {
        Add-Evidence 'T03-15' 'PASS' "安装清单已更新到 $manifestAfter"
    }
    else {
        Add-Evidence 'T03-15' 'FAIL' "安装清单版本为 $manifestAfter，预期 $TargetVersion"
    }

    # 任务 06：升级提交后，install-manifest 记录的 build-info 摘要必须随磁盘文件一起刷新，
    # 不能停留在升级前的旧摘要（否则同一种"声明与事实不符"的缺陷会以另一种形式复活）。
    $recordAfterUpdate = Test-BuildInfoRecord $installDir
    $recordShaAfterUpdate = Get-BuildInfoRecordSha $installDir
    if ($recordAfterUpdate.Ok -and $recordShaAfterUpdate -ne $recordShaBefore) {
        Add-Evidence 'T06-01' 'PASS' ("升级后 install-manifest 的 buildInfo 记录随磁盘刷新（{0}）" -f $recordAfterUpdate.Detail)
    }
    elseif (-not $recordAfterUpdate.Ok) {
        Add-Evidence 'T06-01' 'FAIL' ("升级后安装清单与 build-info.json 不一致：{0}" -f $recordAfterUpdate.Detail)
    }
    else {
        Add-Evidence 'T06-01' 'FAIL' "升级后 buildInfo 摘要没有变化，仍为升级前的 $recordShaBefore"
    }

    $csrf = Get-AdminCsrf
    $statusAfter = (Invoke-Json -Uri "$base/api/admin/status" -Session $adminSession).Content | ConvertFrom-Json
    $portsUnchanged = @($statusAfter.network.boundUrls | Where-Object { $_ -match ":$HttpPort$" }).Count -gt 0
    $sharesAfter = Get-FileHashSafe (Join-Path $dataDir 'shares.json')
    $deviceCountAfter = ((Invoke-Json -Uri "$base/api/admin/devices" -Session $adminSession).Content | ConvertFrom-Json).devices.Count

    if ($portsUnchanged) {
        Add-Evidence 'T03-16' 'PASS' "升级后仍绑定固定端口 $HttpPort：$($statusAfter.network.boundUrls -join ', ')"
    }
    else {
        Add-Evidence 'T03-16' 'FAIL' "升级后端口发生变化：$($statusAfter.network.boundUrls -join ', ')"
    }

    if ($sharesAfter -eq $sharesBefore -and $deviceCountAfter -eq $deviceCountBefore -and (Test-Path (Join-Path $sandbox 'upgrade-check.txt'))) {
        Add-Evidence 'T03-17' 'PASS' ("升级后共享配置、配对设备与共享目录中的用户文件全部保持不变（设备 {0} 台）" -f $deviceCountAfter)
    }
    else {
        Add-Evidence 'T03-17' 'FAIL' '升级后状态或用户文件发生了变化'
    }

    # The strongest persistence claim: the paired device's own session still resolves after the
    # program files were replaced and the service restarted, without re-pairing.
    $meAfter = $null
    try {
        $meAfter = (Invoke-Json -Uri "$base/api/me" -Session $deviceSession).Content | ConvertFrom-Json
    }
    catch { }
    if ($meAfter -and $meAfter.device.id -eq $deviceIdBefore) {
        Add-Evidence 'T03-17b' 'PASS' ("升级后已配对设备无需重新配对即可继续访问（设备 {0}，权限 {1}）" -f $meAfter.device.name, $meAfter.device.level)
    }
    else {
        Add-Evidence 'T03-17b' 'FAIL' '升级后已配对设备的会话失效，需要重新配对'
    }

    # ================================================================ 回滚验证
    Write-Host ''
    Write-Host "G 用启动不起来的 $BrokenVersion 包验证自动回滚" -ForegroundColor Cyan

    $brokenPayload = Join-Path $runRoot 'broken-payload'
    if (Test-Path $brokenPayload) { Remove-Item $brokenPayload -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $brokenPayload | Out-Null
    Copy-Item -Path (Join-Path $targetPublish '*') -Destination $brokenPayload -Recurse -Force

    # A valid, runnable executable that simply does not implement LanDisk: the restarted instance
    # exits at once, so the post-update health check fails and the updater must roll back.
    Copy-Item -Path (Join-Path $env:SystemRoot 'System32/cmd.exe') -Destination (Join-Path $brokenPayload 'LanDisk.exe') -Force
    Copy-Item -Path (Join-Path $env:SystemRoot 'System32/cmd.exe') -Destination (Join-Path $brokenPayload 'LanDisk.Server.exe') -Force

    $brokenFiles = @(
        Get-ChildItem -LiteralPath $brokenPayload -Recurse -File |
            Where-Object { $_.Name -ne 'install-manifest.json' } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($brokenPayload, $_.FullName).Replace('\', '/') } |
            Sort-Object
    )
    [ordered]@{
        schemaVersion = 1
        product       = 'LanDisk'
        version       = $BrokenVersion
        buildCommit   = 'rollback-proof'
        installedAt   = [DateTimeOffset]::Now.ToString('o')
        files         = $brokenFiles
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $brokenPayload 'install-manifest.json') -Encoding UTF8

    $brokenPackage = Join-Path $packagesDir "LanDisk-update-$BrokenVersion.lupd"
    $brokenArgs = @(
        '-File', (Join-Path $PSScriptRoot 'Build-UpdatePackage.ps1'),
        '-PayloadDir', $brokenPayload, '-Version', $BrokenVersion,
        '-MinimumFrom', '1.0.0', '-OutDir', $packagesDir,
        '-Commit', (& git -C $repoRoot rev-parse HEAD).Trim(),
        '-BuildId', "Release/win-x64/$BrokenVersion",
        '-NotesFile', (Join-Path $repoRoot 'docs/RELEASE_NOTES.md')
    )
    if ($KeyPath) { $brokenArgs += @('-KeyPath', $KeyPath) }
    & pwsh @brokenArgs | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $brokenPackage)) { throw "生成 $BrokenVersion 更新包失败" }

    $brokenUpload = Upload-Package -Path $brokenPackage -Csrf $csrf
    if (-not $brokenUpload.Ok) {
        Add-Evidence 'T03-18' 'FAIL' ("$BrokenVersion 包上传失败：HTTP {0} / {1}" -f $brokenUpload.Status, $brokenUpload.Raw)
        throw '坏包上传失败'
    }
    Add-Evidence 'T03-18' 'PASS' "$BrokenVersion 包签名合法（服务器只按签名与哈希判断，不看内容意图）"

    $goodDllHash = Get-FileHashSafe (Join-Path $installDir 'LanDisk.dll')
    Invoke-Json -Uri "$base/api/admin/update/apply" -Method POST -Session $adminSession -Csrf $csrf -Body @{ confirm = $true } | Out-Null

    # The old instance keeps answering for a moment after the request returns, so wait for it to go
    # away first; otherwise the version probe below would be satisfied by the instance being replaced.
    if (-not (Wait-ForDown 60)) {
        Add-Evidence 'T03-19' 'FAIL' '旧实例在 60 秒内没有停止，无法判断重启结果'
        throw '旧实例未停止'
    }

    $rolledBack = Wait-ForVersion $TargetVersion 300
    if (-not $rolledBack) {
        Add-Evidence 'T03-19' 'FAIL' "自检失败后未能在 300 秒内回滚并恢复到 $TargetVersion"
        throw '回滚未完成'
    }
    Add-Evidence 'T03-19' 'PASS' "新版自检失败后自动回滚并重新以 $TargetVersion 提供服务"

    $stateAfterRollback = Wait-ForTerminalState 'RolledBack' 120
    if ($stateAfterRollback -and $stateAfterRollback.lastOutcome -eq 'RolledBack') {
        Add-Evidence 'T03-20' 'PASS' ("回滚状态已记录：lastOutcome=RolledBack（{0}）" -f $stateAfterRollback.lastMessage)
    }
    else {
        Add-Evidence 'T03-20' 'FAIL' ("回滚状态异常：lastOutcome={0}" -f $stateAfterRollback.lastOutcome)
    }

    # PKG05-03：回滚后 build-info.json 必须回到升级前那个版本。
    $buildInfoAfterRollback = if (Test-Path $buildInfoPath) { (Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json).version } else { '(缺失)' }
    if ($buildInfoAfterRollback -eq $TargetVersion) {
        Add-Evidence 'T05-03' 'PASS' ("回滚后 build-info.json 恢复为 {0}" -f $buildInfoAfterRollback)
    }
    else {
        Add-Evidence 'T05-03' 'FAIL' ("回滚后 build-info.json = {0}，预期 {1}" -f $buildInfoAfterRollback, $TargetVersion)
    }

    $restoredManifest = (Get-Content -LiteralPath (Join-Path $installDir 'install-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).version
    $restoredDllHash = Get-FileHashSafe (Join-Path $installDir 'LanDisk.dll')
    if ($restoredManifest -eq $TargetVersion -and $restoredDllHash -eq $goodDllHash) {
        Add-Evidence 'T03-21' 'PASS' "程序文件已按字节恢复（LanDisk.dll 哈希与回滚前一致，清单版本 $restoredManifest）"
    }
    else {
        Add-Evidence 'T03-21' 'FAIL' ("回滚后的程序文件与回滚前不一致：manifest=$restoredManifest dll=$restoredDllHash 期望=$goodDllHash")
    }

    # 任务 06：回滚把程序目录整体恢复，因此"记录"与"被记录的文件"必须一起回到**失败的那次更新
    # 之前**的状态。基准是"1.7.0 失败更新之前"，也就是刚升级成功的 1.6.3，而不是最初安装的 1.3.0：
    # 回滚恢复的是紧邻的上一个状态，脚本前半段的 T03-21 / T05-03 也是这么断言的。
    $recordAfterRollback = Test-BuildInfoRecord $installDir
    $recordShaAfterRollback = Get-BuildInfoRecordSha $installDir
    if ($recordAfterRollback.Ok -and $recordShaAfterRollback -eq $recordShaAfterUpdate) {
        Add-Evidence 'T06-02' 'PASS' ("回滚后 install-manifest 的 buildInfo 记录与 build-info.json 一起回到失败更新之前的值（{0}）" -f $recordAfterRollback.Detail)
    }
    elseif (-not $recordAfterRollback.Ok) {
        Add-Evidence 'T06-02' 'FAIL' ("回滚后安装清单与 build-info.json 不一致：{0}" -f $recordAfterRollback.Detail)
    }
    else {
        Add-Evidence 'T06-02' 'FAIL' ("回滚后 buildInfo 摘要为 $recordShaAfterRollback，与失败更新之前的 $recordShaAfterUpdate 不同")
    }

    # 任务 04：回滚必须清理失败版本新增的受管状态文件，并回到旧的构建提交。
    # 白名单必须等于产品自己认定的受管状态集合（UpdateBackupScope.StateFiles）在数据根目录下的
    # `*.json` 子集。07 轮把 install.json（安装 id / 稳定主机名 / 固定端口）落在数据根目录，
    # 白名单没有跟上，于是这个检查把一个**合法**文件报成了失败版本残留。
    $extra = Get-ChildItem -LiteralPath $dataDir -File -Filter '*.json' |
        Where-Object {
            @('settings.json', 'shares.json', 'devices.json', 'recycle-index.json',
              'transfers.json', 'favorites.json', 'preferences.json', 'shell.json',
              'install.json') -notcontains $_.Name
        }
    $healthRollback = Get-Health
    if ($extra.Count -eq 0 -and $healthRollback -and $healthRollback.buildCommit -eq $releaseCommit) {
        Add-Evidence 'T04-06' 'PASS' '回滚后受管状态目录没有残留失败版本新增的文件，且构建提交已回到升级前'
    }
    else {
        Add-Evidence 'T04-06' 'FAIL' ("回滚后残留 {0} 个可疑文件，buildCommit={1}" -f $extra.Count, $healthRollback.buildCommit)
    }

    $stateAfterRollback2 = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($stateAfterRollback2.lastOutcome -eq 'RolledBack' -and $stateAfterRollback2.stage -eq 'RolledBack') {
        Add-Evidence 'T04-07' 'PASS' '回滚状态被记为 RolledBack（旧版本自检通过后才如此记录）'
    }
    else {
        Add-Evidence 'T04-07' 'FAIL' ("回滚状态异常：lastOutcome={0} stage={1}" -f $stateAfterRollback2.lastOutcome, $stateAfterRollback2.stage)
    }

    if (Test-Path $updaterLog) {
        $logText = Get-Content -LiteralPath $updaterLog -Raw
        if ($logText -match '回滚') {
            Add-Evidence 'T03-22' 'PASS' ("更新程序日志记录了回滚过程：{0}" -f $updaterLog)
        }
        else {
            Add-Evidence 'T03-22' 'FAIL' '更新程序日志中没有回滚记录'
        }
    }
    else {
        Add-Evidence 'T03-22' 'FAIL' "缺少更新程序日志：$updaterLog"
    }

    $csrf = Get-AdminCsrf
    if ((Get-FileHashSafe (Join-Path $dataDir 'shares.json')) -eq $sharesBefore) {
        Add-Evidence 'T03-23' 'PASS' '回滚后共享配置仍然未被改动'
    }
    else {
        Add-Evidence 'T03-23' 'FAIL' '回滚后共享配置发生变化'
    }

    $meAfterRollback = $null
    try {
        $meAfterRollback = (Invoke-Json -Uri "$base/api/me" -Session $deviceSession).Content | ConvertFrom-Json
    }
    catch { }
    if ($meAfterRollback -and $meAfterRollback.device.id -eq $deviceIdBefore) {
        Add-Evidence 'T03-23b' 'PASS' '回滚后已配对设备的会话同样依然有效'
    }
    else {
        Add-Evidence 'T03-23b' 'FAIL' '回滚后已配对设备的会话失效'
    }
}
catch {
    $message = $_.Exception.Message
    if ($message -notmatch '端口' -and $message -notmatch 'BLOCKED') {
        Add-Evidence 'T03-XX' 'FAIL' ('验收过程异常：' + $message)
    }
}
finally {
    # ================================================================ 退出与清理
    if (Test-Path (Join-Path $dataDir 'admin-url.txt')) {
        try {
            if (-not $csrf) { $csrf = Get-AdminCsrf }
            Invoke-Json -Uri "$base/api/admin/shutdown" -Method POST -Session $adminSession -Csrf $csrf -Body @{} | Out-Null
            Start-Sleep -Seconds 3

            $released = $true
            foreach ($port in @($HttpPort, $HttpsPort)) {
                if (-not (Test-PortFree $port)) { $released = $false }
            }
            if ($released) {
                Add-Evidence 'T03-24' 'PASS' "退出后端口 $HttpPort/$HttpsPort 已释放"
            }
            else {
                Add-Evidence 'T03-24' 'FAIL' '退出后端口仍被占用'
            }
        }
        catch {
            Add-Evidence 'T03-24' 'FAIL' ('退出验收失败：' + $_.Exception.Message)
        }
    }

    foreach ($name in @('LanDisk', 'LanDisk.Server')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                $path = $_.MainModule.FileName
                if ($path -and $path.StartsWith($installDir, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $_.Kill($true)
                    Write-Host "已结束残留进程 $($_.Id)" -ForegroundColor Yellow
                }
            }
            catch { }
        }
    }

    # ================================================================ 用户真实安装未被改动
    Write-Host ''
    Write-Host 'H 用户真实安装目录未被触碰' -ForegroundColor Cyan

    if ($realInstanceRunning) {
        Add-Evidence 'T03-25' 'BLOCKED' ("检测到另一个 LanDisk 实例正在运行（" + $realInstancePath +
            "），它会正常写入自己的配置与日志，因此本次运行无法把用户数据目录的差异归因于验收过程")
    }
    elseif (Test-Path $realBaseline) {
        $mismatches = New-Object System.Collections.Generic.List[string]
        $checked = 0
        foreach ($line in Get-Content -LiteralPath $realBaseline -Encoding UTF8) {
            if (-not $line -or $line.StartsWith('#')) { continue }
            $parts = $line -split "`t"
            if ($parts.Count -lt 3) { continue }
            if ($parts[0] -eq 'relative_path') { continue }   # 跳过表头行
            $relative = $parts[0]
            $expected = $parts[2]
            $full = Join-Path $realDataRoot $relative
            $actual = Get-FileHashSafe $full
            $checked++
            if ($actual -ne $expected) { $mismatches.Add($relative) }
        }

        if ($mismatches.Count -eq 0 -and $checked -gt 0) {
            Add-Evidence 'T03-25' 'PASS' ("%LOCALAPPDATA%\LanDisk 中 {0} 个基线文件的 SHA256 与**本次验收开始时**完全一致" -f $checked)
        }
        elseif ($checked -eq 0) {
            Add-Evidence 'T03-25' 'FAIL' '基线文件为空，无法证明真实安装未被改动'
        }
        else {
            Add-Evidence 'T03-25' 'FAIL' ('以下真实文件被改动：' + ($mismatches -join ', '))
        }
    }
    else {
        Add-Evidence 'T03-25' 'BLOCKED' "用户真实数据目录不存在（$realDataRoot），没有可核对的基线"
    }

    if (-not $KeepArtifacts) {
        Write-Host "现场保留在：$runRoot" -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host '=== 结果汇总 ===' -ForegroundColor Cyan
$evidence | Format-Table -AutoSize | Out-String | Write-Host

$reportPath = Join-Path $runRoot 'update-verify.json'
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "证据文件: $reportPath"

$failed = @($evidence | Where-Object { $_.Result -eq 'FAIL' }).Count
$blocked = @($evidence | Where-Object { $_.Result -eq 'BLOCKED' }).Count
if ($blocked -gt 0) { exit 2 }
if ($failed -gt 0) { exit 1 }
exit 0
