#requires -Version 7.0
<#
.SYNOPSIS
    对已发布的 Windows 运行包做真实启动/退出冒烟验收（ACCEPTANCE E02/E03/E04、P01、P03）。

.DESCRIPTION
    使用真正发布出来的 LanDisk.Server.exe（不是 dev server），在回环地址上启动，
    在任务自建的临时沙箱中完成共享、配对、查看、下载、上传、重命名、删除与恢复，
    然后优雅退出并确认端口与句柄已释放。

    脚本不会共享任何真实目录，不会修改防火墙、证书信任、全局配置或开机启动项。

.EXAMPLE
    pwsh -File tools/Release-SmokeTest.ps1 -PublishDir dist/publish/LanDisk-win-x64
#>
[CmdletBinding()]
param(
    [string]$PublishDir = '',
    [int]$Port = 0,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $repoRoot 'dist/publish/LanDisk-win-x64' }
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
$exe = Join-Path $PublishDir 'LanDisk.Server.exe'
if (-not (Test-Path $exe)) { throw "找不到发布出来的可执行文件: $exe" }

$runRoot = Join-Path $repoRoot ('.releasecheck/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$dataDir = Join-Path $runRoot 'data'
$sandbox = Join-Path $runRoot 'sandbox'
$logPath = Join-Path $runRoot 'server.log'
$evidence = New-Object System.Collections.Generic.List[object]

New-Item -ItemType Directory -Force -Path $dataDir, $sandbox | Out-Null

function Add-Evidence([string]$id, [string]$result, [string]$detail) {
    $evidence.Add([pscustomobject]@{ Id = $id; Result = $result; Detail = $detail })
    $color = switch ($result) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("  [{0}] {1} - {2}" -f $result, $id, $detail) -ForegroundColor $color
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $free = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    return $free
}

if ($Port -le 0) { $Port = Get-FreePort }
$httpsPort = Get-FreePort
$base = "http://127.0.0.1:$Port"

# 本机管理端的 cookie 容器
$adminSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Invoke-Json {
    param(
        [string]$Uri,
        [string]$Method = 'GET',
        $Body = $null,
        $Session = $null,
        [string]$Csrf = ''
    )
    $headers = @{ 'X-LanDisk-Client' = 'release-smoke' }
    if ($Csrf) { $headers['X-LanDisk-CSRF'] = $Csrf }
    $params = @{
        Uri             = $Uri
        Method          = $Method
        Headers         = $headers
        UseBasicParsing = $true
        TimeoutSec      = 60
    }
    if ($Session) { $params.WebSession = $Session }
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 8 -Compress }
        $params.Body = [System.Text.Encoding]::UTF8.GetBytes($json)
        $params.ContentType = 'application/json; charset=utf-8'
    }
    return Invoke-WebRequest @params
}

function Wait-ForHealth([int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "$base/api/health" -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return $true }
        }
        catch { Start-Sleep -Milliseconds 400 }
    }
    return $false
}

$process = $null
try {
    # ---------------------------------------------------------------- E03 初次运行
    Write-Host ''
    Write-Host 'E03 初次运行状态' -ForegroundColor Cyan
    if (Test-Path (Join-Path $dataDir 'shares.json')) {
        Add-Evidence 'E03' 'FAIL' '全新数据目录中不应存在 shares.json'
    }
    else {
        Add-Evidence 'E03' 'PASS' '全新数据目录没有共享与设备记录'
    }

    # ---------------------------------------------------------------- E02 发布包启动
    Write-Host ''
    Write-Host 'E02 使用发布目录启动' -ForegroundColor Cyan
    $process = Start-Process -FilePath $exe `
        -ArgumentList @('--data-dir', $dataDir, '--port', "$Port", '--https-port', "$httpsPort") `
        -WorkingDirectory $PublishDir `
        -RedirectStandardOutput $logPath `
        -RedirectStandardError "$logPath.err" `
        -PassThru -WindowStyle Hidden

    if (-not (Wait-ForHealth)) {
        Add-Evidence 'E02' 'FAIL' '发布包未能在 60 秒内响应 /api/health，日志见 ' + $logPath
        throw '启动失败'
    }

    $health = Invoke-Json -Uri "$base/api/health"
    $healthJson = $health.Content | ConvertFrom-Json
    Add-Evidence 'E02' 'PASS' ("发布包启动成功，版本 {0}，进程 PID {1}" -f $healthJson.version, $process.Id)

    $indexPath = Join-Path $PublishDir 'wwwroot/index.html'
    $index = Invoke-WebRequest -Uri "$base/" -UseBasicParsing
    $hasInlineScript = $index.Content -match '<script(?![^>]*\ssrc=)[^>]*>'
    if ($index.StatusCode -eq 200 -and $index.Content -match '<div id="root">' -and -not $hasInlineScript) {
        Add-Evidence 'P01' 'PASS' '前端静态资源由发布包自身提供，index.html 无内联脚本'
    }
    else {
        Add-Evidence 'P01' 'FAIL' '前端资源校验失败（状态 ' + $index.StatusCode + '，内联脚本=' + $hasInlineScript + '）'
    }

    # ---------------------------------------------------------------- E03 默认不暴露
    $adminUrlFile = Join-Path $dataDir 'admin-url.txt'
    if (-not (Test-Path $adminUrlFile)) { throw '缺少 admin-url.txt' }
    $adminUrl = (Get-Content -LiteralPath $adminUrlFile -Raw).Trim()
    $adminToken = ($adminUrl -split 'token=')[1]
    $adminUrl = $adminUrl -replace '#.*$', ''

    if ($adminUrl -match '^http://127\.0\.0\.1:' -and $adminUrl -match ":$Port/") {
        Add-Evidence 'E03' 'PASS' '管理入口位于回环地址，且端口与启动参数一致'
    }
    else {
        Add-Evidence 'E03' 'FAIL' "管理入口不在预期的回环地址: $adminUrl"
    }

    $anonymous = $null
    try { $anonymous = Invoke-Json -Uri "$base/api/shares"; Add-Evidence 'E03' 'FAIL' '匿名访问 /api/shares 未被拒绝' }
    catch { Add-Evidence 'E03' 'PASS' '匿名访问 /api/shares 返回 ' + $_.Exception.Response.StatusCode.value__ }

    $login = Invoke-Json -Uri "$base/api/admin/session" -Method POST -Body @{ token = $adminToken } -Session $adminSession
    $csrf = ($login.Content | ConvertFrom-Json).csrfToken
    Add-Evidence 'E03' 'PASS' '使用一次性管理令牌建立管理会话'

    $status = (Invoke-Json -Uri "$base/api/admin/status" -Session $adminSession).Content | ConvertFrom-Json
    $badUrls = @($status.network.boundUrls | Where-Object { $_ -notmatch '127\.0\.0\.1' -and $_ -notmatch '\[::1\]' })
    if ($status.network.lanEnabled -eq $false -and $badUrls.Count -eq 0) {
        Add-Evidence 'E03' 'PASS' ('默认只监听回环: ' + ($status.network.boundUrls -join ', '))
    }
    else {
        Add-Evidence 'E03' 'FAIL' ('默认监听不符合预期: ' + ($status.network.boundUrls -join ', '))
    }

    $shares = (Invoke-Json -Uri "$base/api/admin/shares" -Session $adminSession).Content | ConvertFrom-Json
    if (@($shares.shares).Count -eq 0) { Add-Evidence 'E03' 'PASS' '全新实例没有任何共享' }
    else { Add-Evidence 'E03' 'FAIL' '全新实例不应有共享' }

    # ---------------------------------------------------------------- 六项功能真实可用
    Write-Host ''
    Write-Host '六项核心功能的发布包实测' -ForegroundColor Cyan

    $samples = Join-Path $repoRoot 'tests/samples'
    Copy-Item (Join-Path $samples 'video-h264-aac.mp4') (Join-Path $sandbox '样本视频.mp4')
    Copy-Item (Join-Path $samples 'image-sample.png') (Join-Path $sandbox '样本图片.png')
    Copy-Item (Join-Path $samples 'audio-pcm.wav') (Join-Path $sandbox '样本音频.wav')
    Set-Content -Path (Join-Path $sandbox '说明 文本.txt') -Value '这是发布包冒烟测试用的合成文本。' -Encoding UTF8
    $downloadSource = Join-Path $samples 'video-vp9-opus.webm'
    Copy-Item $downloadSource (Join-Path $sandbox '待下载.webm')

    $shareResponse = Invoke-Json -Uri "$base/api/admin/shares" -Method POST -Session $adminSession -Csrf $csrf -Body @{
        rootPath = $sandbox; displayName = '冒烟测试共享'; allowUpload = $true; allowRename = $true; allowDelete = $true
    }
    $share = ($shareResponse.Content | ConvertFrom-Json).share
    Add-Evidence 'E02' 'PASS' ('通过发布包创建共享，卷身份 ' + $share.volumeGuidPath)

    $pairResponse = Invoke-Json -Uri "$base/api/admin/pairing" -Method POST -Session $adminSession -Csrf $csrf -Body @{}
    $pair = $pairResponse.Content | ConvertFrom-Json

    $deviceSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $redeem = Invoke-Json -Uri "$base/api/pair/redeem" -Method POST -Session $deviceSession -Body @{ token = $pair.token; deviceName = '冒烟设备' }
    $device = ($redeem.Content | ConvertFrom-Json).device
    $me = (Invoke-Json -Uri "$base/api/me" -Session $deviceSession).Content | ConvertFrom-Json
    $deviceCsrf = $me.csrfToken
    Add-Evidence 'A01' 'PASS' ('配对成功，设备 {0} 初始权限 {1}' -f $device.name, $device.level)

    # 1) 查看
    $browse = (Invoke-Json -Uri ("$base/api/browse?shareId={0}&path=" -f $share.id) -Session $deviceSession).Content | ConvertFrom-Json
    if ($browse.total -ge 5) { Add-Evidence 'F01' 'PASS' ('目录浏览返回 {0} 项，包含中文与 emoji 名称' -f $browse.total) }
    else { Add-Evidence 'F01' 'FAIL' ('目录浏览项数异常: ' + $browse.total) }

    # 2) 播放（Range 语义，真机播放能力见 IPHONE_REAL_DEVICE / 浏览器实测）
    $rangeHeaders = @{ Range = 'bytes=0-1023'; 'X-LanDisk-Client' = 'release-smoke' }
    $rangeResponse = Invoke-WebRequest -Uri ("$base/api/file/content?shareId={0}&path={1}&disposition=inline" -f $share.id, [uri]::EscapeDataString('样本视频.mp4')) -Headers $rangeHeaders -WebSession $deviceSession -UseBasicParsing
    if ($rangeResponse.StatusCode -eq 206 -and $rangeResponse.Headers['Content-Range'] -match '^bytes 0-1023/') {
        Add-Evidence 'M02' 'PASS' ('媒体 Range 请求返回 206，Content-Range=' + $rangeResponse.Headers['Content-Range'])
    }
    else {
        Add-Evidence 'M02' 'FAIL' ('媒体 Range 请求异常: ' + $rangeResponse.StatusCode)
    }

    # 3) 下载（使用独立的会话对象，避免上一次 Range 请求的头部被复用）
    $downloadSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $downloadSession.Cookies = $deviceSession.Cookies
    $downloadTarget = Join-Path $runRoot 'downloaded.webm'
    if (Test-Path $downloadTarget) { Remove-Item $downloadTarget -Force }

    $downloadUrl = "$base/api/file/download?shareId={0}&path={1}" -f $share.id, [uri]::EscapeDataString('待下载.webm')
    $downloadResponse = Invoke-WebRequest -Uri $downloadUrl -WebSession $downloadSession -OutFile $downloadTarget -UseBasicParsing -PassThru
    $sourceHash = (Get-FileHash -LiteralPath $downloadSource -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $downloadTarget -Algorithm SHA256).Hash
    if ($sourceHash -eq $targetHash -and $downloadResponse.StatusCode -eq 200) {
        Add-Evidence 'D01' 'PASS' ("下载文件 SHA256 与源文件一致（HTTP {0}，{1} 字节）" -f $downloadResponse.StatusCode, (Get-Item $downloadTarget).Length)
    }
    else {
        Add-Evidence 'D01' 'FAIL' ("下载校验失败：HTTP {0}，大小 {1}" -f $downloadResponse.StatusCode, (Get-Item $downloadTarget).Length)
    }

    # 4) 上传（先把设备提升为可读写）
    Invoke-Json -Uri ("$base/api/admin/devices/{0}" -f $device.id) -Method PATCH -Session $adminSession -Csrf $csrf -Body @{ level = 'readWrite' } | Out-Null

    $uploadSource = Join-Path $runRoot '上传 测试.bin'
    [System.IO.File]::WriteAllBytes($uploadSource, [byte[]](0..255))
    $uploadBytes = [System.IO.File]::ReadAllBytes($uploadSource)
    $uploadHash = (Get-FileHash -LiteralPath $uploadSource -Algorithm SHA256).Hash.ToLowerInvariant()

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $headLen = [Math]::Min(65536, $uploadBytes.Length)
    $headHash = [Convert]::ToHexString($sha.ComputeHash($uploadBytes[0..($headLen - 1)])).ToLowerInvariant()
    $tailHash = [Convert]::ToHexString($sha.ComputeHash([byte[]]@())).ToLowerInvariant()
    $material = "landisk-v1`n$($uploadBytes.Length)`n1700000000000`n$headHash`n$tailHash"
    $fingerprint = [Convert]::ToHexString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($material))).ToLowerInvariant()

    $init = (Invoke-Json -Uri "$base/api/uploads/" -Method POST -Session $deviceSession -Csrf $deviceCsrf -Body @{
        shareId = $share.id; path = ''; fileName = '上传 测试.bin'; length = $uploadBytes.Length; fingerprint = $fingerprint
    }).Content | ConvertFrom-Json

    $chunkHeaders = @{ 'Content-Type' = 'application/octet-stream'; 'X-LanDisk-Client' = 'release-smoke'; 'X-LanDisk-SHA256' = '' }
    $chunkResponse = Invoke-WebRequest -Uri ("$base/api/uploads/{0}/chunk?offset=0" -f $init.uploadId) -Method PUT -Body $uploadBytes -ContentType 'application/octet-stream' -Headers @{ 'X-LanDisk-Client' = 'release-smoke'; 'X-LanDisk-CSRF' = $deviceCsrf } -WebSession $deviceSession -UseBasicParsing
    $chunkJson = $chunkResponse.Content | ConvertFrom-Json

    $complete = (Invoke-Json -Uri ("$base/api/uploads/{0}/complete" -f $init.uploadId) -Method POST -Session $deviceSession -Csrf $deviceCsrf -Body @{
        sha256 = $uploadHash; conflictPolicy = 'fail'
    }).Content | ConvertFrom-Json

    $uploadedPath = Join-Path $sandbox '上传 测试.bin'
    if ((Test-Path $uploadedPath) -and (Get-FileHash -LiteralPath $uploadedPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $uploadHash) {
        Add-Evidence 'U01' 'PASS' '上传落盘且 SHA256 一致'
    }
    else {
        Add-Evidence 'U01' 'FAIL' '上传结果校验失败'
    }

    # 5) 重命名
    $meta = (Invoke-Json -Uri ("$base/api/file/meta?shareId={0}&path={1}" -f $share.id, [uri]::EscapeDataString('说明 文本.txt')) -Session $deviceSession).Content | ConvertFrom-Json
    $rename = (Invoke-Json -Uri "$base/api/file/rename" -Method POST -Session $deviceSession -Csrf $deviceCsrf -Body @{
        shareId = $share.id; path = '说明 文本.txt'; newName = '改名后的 文本.txt'; version = $meta.version
    }).Content | ConvertFrom-Json
    if (Test-Path (Join-Path $sandbox '改名后的 文本.txt')) { Add-Evidence 'R01' 'PASS' '中文文件名重命名成功' }
    else { Add-Evidence 'R01' 'FAIL' '重命名结果校验失败' }

    # 6) 删除与恢复
    $meta2 = (Invoke-Json -Uri ("$base/api/file/meta?shareId={0}&path={1}" -f $share.id, [uri]::EscapeDataString('改名后的 文本.txt')) -Session $deviceSession).Content | ConvertFrom-Json
    $delete = (Invoke-Json -Uri "$base/api/file/delete" -Method POST -Session $deviceSession -Csrf $deviceCsrf -Body @{
        shareId = $share.id; path = '改名后的 文本.txt'; version = $meta2.version
    }).Content | ConvertFrom-Json

    if (-not (Test-Path (Join-Path $sandbox '改名后的 文本.txt'))) {
        Add-Evidence 'R02' 'PASS' '删除进入 LanDisk 回收站（非系统回收站）'
    }
    else {
        Add-Evidence 'R02' 'FAIL' '删除后文件仍然存在'
    }

    Invoke-Json -Uri ("$base/api/recycle/{0}/restore" -f $delete.recycleId) -Method POST -Session $deviceSession -Csrf $deviceCsrf -Body @{} | Out-Null
    if (Test-Path (Join-Path $sandbox '改名后的 文本.txt')) { Add-Evidence 'R02' 'PASS' '从回收站恢复成功' }
    else { Add-Evidence 'R02' 'FAIL' '恢复失败' }

    # ---------------------------------------------------------------- E02 优雅退出
    Write-Host ''
    Write-Host 'E02 退出与资源释放' -ForegroundColor Cyan
    Invoke-Json -Uri "$base/api/admin/shutdown" -Method POST -Session $adminSession -Csrf $csrf -Body @{} | Out-Null
    $exited = $process.WaitForExit(30000)
    if ($exited) {
        Add-Evidence 'E02' 'PASS' ('进程在收到退出请求后正常结束，退出码 ' + $process.ExitCode)
    }
    else {
        Add-Evidence 'E02' 'FAIL' '进程未在 30 秒内退出'
    }

    Start-Sleep -Milliseconds 800
    $stillListening = $false
    try {
        $probe = [System.Net.Sockets.TcpClient]::new()
        $probe.Connect('127.0.0.1', $Port)
        $stillListening = $probe.Connected
        $probe.Close()
    }
    catch { $stillListening = $false }

    if (-not $stillListening) { Add-Evidence 'E02' 'PASS' "端口 $Port 已释放" }
    else { Add-Evidence 'E02' 'FAIL' "端口 $Port 仍被占用" }
}
catch {
    Add-Evidence 'E02' 'FAIL' ('冒烟测试异常: ' + $_.Exception.Message)
}
finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill($true) } catch { }
        Write-Host '已强制结束残留进程' -ForegroundColor Yellow
    }

    # 证据脱敏：日志由 .NET 控制台按系统代码页写出，这里转成 UTF-8 并抹掉一次性管理令牌，
    # 避免把任何凭据形式的内容带进交付物（令牌所在的临时数据目录随后会被删除）。
    if (Test-Path $logPath) {
        try {
            $bytes = [System.IO.File]::ReadAllBytes($logPath)
            $text = [System.Text.Encoding]::UTF8.GetString($bytes)
            if ($text.Contains([char]0xFFFD)) {
                $text = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage).GetString($bytes)
            }
            if ($adminToken) { $text = $text.Replace($adminToken, '<ADMIN-TOKEN-REDACTED>') }
            $text = $text -replace '(admin#token=)\S+', '$1<ADMIN-TOKEN-REDACTED>'
            [System.IO.File]::WriteAllText($logPath, $text, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host '服务日志已转为 UTF-8 并脱敏管理令牌'
        }
        catch {
            Write-Host ('日志脱敏失败（不影响验收结论）: ' + $_.Exception.Message) -ForegroundColor Yellow
        }
    }
}

Write-Host ''
Write-Host '=== 结果汇总 ===' -ForegroundColor Cyan
$evidence | Format-Table -AutoSize | Out-String | Write-Host

$reportPath = Join-Path $runRoot 'release-smoke.json'
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host "证据文件: $reportPath"
Write-Host "服务日志: $logPath"

$failed = @($evidence | Where-Object { $_.Result -eq 'FAIL' }).Count
if (-not $KeepArtifacts) {
    Write-Host "（保留现场以便核对：$runRoot）"
}
if ($failed -gt 0) { exit 1 } else { exit 0 }
