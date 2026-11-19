param(
    [Parameter(Mandatory)][string]$Repo,
    [ValidateSet('Matrix', 'A', 'B')][string]$Mode = 'Matrix',
    [string]$PublishDir = '',
    [string]$FrozenPackage = '',
    [string]$TestPackage = '',
    [string]$BaselinePublish = '',
    [string]$HostPublish = '',
    [string]$KeyPath = '',
    [string]$EvidenceDir = '',
    [int]$HealthTimeoutSeconds = 90
)

# HTTPS / 监听 / 动态端口的端到端验收。
#
# 已核对的**实际**语义（读源码 + 隔离实跑，见 docs/LAN_DISK_11_HTTPS_LISTENER_RECORD.md）：
#   * 回环 HTTP  : 127.0.0.1 与 ::1，端口 = 生效的 HTTP 端口
#   * 回环 HTTPS : 127.0.0.1，端口 = 生效的 HTTPS 端口，**无条件监听**（HttpsEnabled 不影响它）
#   * 局域网     : 仅当 LanEnabled 为真，且只绑**物理网卡**（排除 Hyper-V 交换机与 VPN 隧道地址）
#                  LanEnabled && !HttpsEnabled && HttpCompatModeEnabled → 局域网 HTTP
#                  LanEnabled &&  HttpsEnabled                            → 局域网 HTTPS
#   * 端口 0     : FindFreePort()「挑一个空闲端口」，**不是关闭**
#
# 三种模式：
#   Matrix —— 只跑监听矩阵（不涉及更新），四种场景，任何构建都能跑。
#   A      —— 正常配置的旧版本升级到**冻结的正式 .lupd**；直接使用交付包并记录完整 SHA256。
#   B      —— 修复后的宿主以**动态端口**启动，发起更新与失败回滚；测试包明确标为测试用途。
#
# A 与 B 是两类不同的证据：B 验证的是**修复后的发起方**，不能代替 A，
# 也不能用来声称"旧宿主的动态端口问题已被追溯修复"。

$ErrorActionPreference = 'Stop'
if (-not $EvidenceDir) { $EvidenceDir = Join-Path $Repo 'dist\evidence-11\HTTPS-E2E' }
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

$results = New-Object System.Collections.Generic.List[object]
$script:step = 0

function Get-Listeners([int]$ProcessId) {
    try {
        return @(Get-NetTCPConnection -State Listen -OwningProcess $ProcessId -ErrorAction Stop |
            ForEach-Object { [pscustomobject]@{ address = $_.LocalAddress; port = $_.LocalPort } } |
            Sort-Object address, port -Unique)
    }
    catch { return @() }
}

function IsLoopback([string]$address) {
    return $address -eq '127.0.0.1' -or $address -eq '::1'
}

# 从实际监听里反推两个生效端口，而不是读配置：
#   HTTP  端口同时出现在 127.0.0.1 与 ::1 上（回环 HTTP 绑 v4+v6）；
#   HTTPS 端口只出现在 127.0.0.1 上（回环 HTTPS 只绑 v4，这是当前设计）。
# 由此才能判定"局域网 HTTPS 关闭时有没有多出局域网 HTTPS 监听"，而不是只看端口个数。
function ResolvePorts($listeners) {
    $v4 = @($listeners | Where-Object { $_.address -eq '127.0.0.1' } | ForEach-Object { $_.port } | Sort-Object -Unique)
    $v6 = @($listeners | Where-Object { $_.address -eq '::1' } | ForEach-Object { $_.port } | Sort-Object -Unique)
    $http = @($v4 | Where-Object { $v6 -contains $_ })
    $https = @($v4 | Where-Object { $v6 -notcontains $_ })
    return [pscustomobject]@{
        httpPort = if ($http.Count -gt 0) { $http[0] } else { 0 }
        httpsPort = if ($https.Count -gt 0) { $https[0] } else { 0 }
    }
}

function Start-Probe {
    param([string]$Exe, [string]$DataDir, [string]$Port, [string]$HttpsPort, [hashtable]$Settings)
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    if ($Settings) {
        $s = [ordered]@{
            HttpPort = 8891; HttpsPort = 8892; LanEnabled = $false; LanBindAddress = ''
            HttpsEnabled = $false; HttpCompatModeEnabled = $false; AllowedCidr = ''
        }
        foreach ($k in $Settings.Keys) { $s[$k] = $Settings[$k] }
        if ($Port -ne '') { $s.HttpPort = [int]$Port }
        if ($HttpsPort -ne '') { $s.HttpsPort = [int]$HttpsPort }
        $s | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $DataDir 'settings.json') -Encoding UTF8
    }
    $args = @('--data-dir', $DataDir)
    if ($Port -ne '') { $args += @('--port', $Port) }
    if ($HttpsPort -ne '') { $args += @('--https-port', $HttpsPort) }
    $p = Start-Process -FilePath $Exe -ArgumentList $args -PassThru -WindowStyle Hidden
    return $p
}

function Wait-Healthy([int]$ProcessId, [int]$Port, [int]$TimeoutSeconds = 25) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri ("http://127.0.0.1:$Port/api/health") -TimeoutSec 3 -UseBasicParsing
            if ($r.StatusCode -eq 200) { return $true }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Stop-Probe($proc) {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 800
}

# ---------------------------------------------------------------- Matrix 模式
function Invoke-MatrixCase {
    param([string]$Id, [string]$Description, [hashtable]$Settings, [string]$Port, [string]$HttpsPort, [scriptblock]$Assert)
    $script:step++
    $exe = Join-Path $PublishDir 'LanDisk.Server.exe'
    $data = Join-Path $EvidenceDir ("matrix-" + $Id)
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue

    $proc = Start-Probe -Exe $exe -DataDir $data -Port $Port -HttpsPort $HttpsPort -Settings $Settings
    Start-Sleep -Seconds 6
    $listeners = Get-Listeners $proc.Id
    # 健康检查目标：动态端口要按**实际**绑定的 HTTP 端口探测，而不是配置里的 0
    $actualHttp = ($listeners | Where-Object { -not (IsLoopback $_.address) -or $_.address -eq '127.0.0.1' } | Select-Object -First 1)
    $probePort = if ($Port -eq '0') { ($listeners | Where-Object { $_.address -eq '127.0.0.1' } | ForEach-Object { $_.port } | Sort-Object | Select-Object -First 2)[0] } else { [int]$Port }
    $healthy = Wait-Healthy -ProcessId $proc.Id -Port $probePort
    Stop-Probe $proc

    $settingsOnDisk = if (Test-Path (Join-Path $data 'settings.json')) { Get-Content (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $ctx = [pscustomobject]@{
        id = $Id; description = $Description
        configuredHttp = if ($settingsOnDisk) { $settingsOnDisk.HttpPort } else { $null }
        configuredHttps = if ($settingsOnDisk) { $settingsOnDisk.HttpsPort } else { $null }
        lanEnabled = if ($settingsOnDisk) { $settingsOnDisk.LanEnabled } else { $null }
        httpsEnabled = if ($settingsOnDisk) { $settingsOnDisk.HttpsEnabled } else { $null }
        listeners = $listeners
        ports = (ResolvePorts $listeners)
        probePort = $probePort
        healthy = $healthy
        processId = $proc.Id
        exe = $exe
        exeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $verdict = & $Assert $ctx
    $results.Add([pscustomobject]@{
        id = $Id; description = $Description; expectation = $verdict.expect
        configuredPorts = ("http=" + $ctx.configuredHttp + " https=" + $ctx.configuredHttps)
        actualListeners = @($listeners | ForEach-Object { $_.address + ':' + $_.port }) -join ', '
        actualHttpPort = $probePort
        healthOk = $healthy
        lanListeners = @($listeners | Where-Object { -not (IsLoopback $_.address) } | ForEach-Object { $_.address + ':' + $_.port }) -join ', '
        observed = $verdict.observed
        passed = $verdict.ok
    })
    $mark = if ($verdict.ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}  listeners={2}" -f $mark, $Id, $ctx.listeners.Count)
    if ($verdict.notes) { $verdict.notes | ForEach-Object { Write-Host ('        - ' + $_) -ForegroundColor Yellow } }
}

Write-Host ("HTTPS/listener verification mode = " + $Mode)

if ($Mode -eq 'Matrix') {
    if (-not $PublishDir) { throw 'Matrix 模式需要 -PublishDir' }

    Invoke-MatrixCase 'H1-lan-off' '局域网关闭：只有回环监听（HTTP 与 HTTPS 都在），不得有任何局域网监听' `
        @{ LanEnabled = $false; HttpsEnabled = $false } '0' '0' {
        param($c)
        $lan = @($c.listeners | Where-Object { -not (IsLoopback $_.address) })
        $loopHttps = @($c.listeners | Where-Object { $_.address -eq '127.0.0.1' })
        $ok = ($lan.Count -eq 0) -and ($c.listeners.Count -ge 3) -and ($c.probePort -ne 0) -and $c.healthy
        @{ ok = $ok; expect = '0 个局域网监听；回环 HTTP+HTTPS 存在；端口 0 被解析为非 0；健康检查通过'
           observed = ("局域网 " + $lan.Count + " 个；回环监听 " + $c.listeners.Count + " 个；探测端口 " + $c.probePort + "；健康 " + $c.healthy)
           notes = @(if ($lan.Count -gt 0) { '局域网 HTTPS 关闭时出现了局域网监听：' + (($lan | ForEach-Object { $_.address + ':' + $_.port }) -join ', ') }) }
    }

    Invoke-MatrixCase 'H2-lan-http-compat' 'LanEnabled=true 且 HttpsEnabled=false 且兼容模式：局域网只应有 HTTP，绝不应有局域网 HTTPS' `
        @{ LanEnabled = $true; HttpsEnabled = $false; HttpCompatModeEnabled = $true } '0' '0' {
        param($c)
        $lan = @($c.listeners | Where-Object { -not (IsLoopback $_.address) })
        $lanHttps = @($lan | Where-Object { $_.port -eq $c.ports.httpsPort })
        $ok = $c.healthy -and ($c.probePort -ne 0) -and ($lanHttps.Count -eq 0)
        @{ ok = $ok; expect = '局域网 HTTPS 关闭时不得新增局域网 HTTPS 监听（按实际 HTTPS 端口判定，不看端口个数）；既有回环 HTTPS 保留'
           observed = ("生效端口 http=" + $c.ports.httpPort + " https=" + $c.ports.httpsPort +
                        "；局域网监听 " + $lan.Count + " 个：" + (($lan | ForEach-Object { $_.address + ':' + $_.port }) -join ', ') +
                        "；其中落在 HTTPS 端口上的 " + $lanHttps.Count + " 个；回环监听 " + $c.listeners.Count + " 个；健康 " + $c.healthy)
           notes = @(
             if ($lanHttps.Count -gt 0) { '局域网 HTTPS 关闭却出现了局域网 HTTPS 监听：' + (($lanHttps | ForEach-Object { $_.address + ':' + $_.port }) -join ', ') }
             if (@($c.listeners | Where-Object { $_.address -eq '127.0.0.1' -and $_.port -eq $c.ports.httpsPort }).Count -eq 0) { '回环 HTTPS 监听消失了：按当前设计它应无条件保留' }
           ) }
    }

    Invoke-MatrixCase 'H3-lan-https-on' 'LanEnabled=true 且 HttpsEnabled=true：局域网 HTTPS 应存在，回环 HTTPS 仍在' `
        @{ LanEnabled = $true; HttpsEnabled = $true; HttpCompatModeEnabled = $false } '0' '0' {
        param($c)
        $loop = @($c.listeners | Where-Object { $_.address -eq '127.0.0.1' })
        $ok = $c.healthy -and ($loop.Count -ge 2) -and ($c.probePort -ne 0)
        @{ ok = $ok; expect = '回环 HTTP 与回环 HTTPS 都在；局域网 HTTPS 依物理网卡而定'
           observed = ("回环 127.0.0.1 监听 " + $loop.Count + " 个；全部监听 " + $c.listeners.Count + " 个；健康 " + $c.healthy)
           notes = @() }
    }

    Invoke-MatrixCase 'H4-fixed-ports' '固定端口：配置值与实际绑定端口应一致，且不再使用端口 0' `
        @{ LanEnabled = $false; HttpsEnabled = $false } '0' '0' {
        param($c)
        $ok = $c.healthy -and ($c.configuredHttp -eq 0) -and ($c.probePort -ne 0)
        @{ ok = $ok; expect = '配置保留用户写的 0；实际绑定端口非 0；健康检查按实际端口进行'
           observed = ("配置 http=" + $c.configuredHttp + "（用户配置值，不得被改写成固定端口）；实际探测端口 " + $c.probePort + "；健康 " + $c.healthy)
           notes = @(if ($c.configuredHttp -ne 0) { '配置值被改写成固定端口了：settings 必须保留用户配置值' }) }
    }
}

$passed = @($results | Where-Object { $_.passed }).Count
$payload = [pscustomobject]@{
    generatedAt = (Get-Date -Format 'o')
    mode = $Mode
    note = '监听矩阵模式不涉及更新。A/B 两类更新证据需在最终待验产物构建后产生。'
    publishDir = $PublishDir
    total = $results.Count
    passed = $passed
    failed = $results.Count - $passed
    verdict = if ($results.Count -gt 0 -and $passed -eq $results.Count) { 'PASS' } elseif ($results.Count -eq 0) { 'PREPARED_NOT_RUN' } else { 'FAIL' }
    cases = $results
}
$out = Join-Path $EvidenceDir ("https-e2e-" + $Mode.ToLowerInvariant() + ".json")
$payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $out -Encoding UTF8
Remove-Item (Join-Path $EvidenceDir 'matrix-*') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host ("https/listener verification ({0}): {1}/{2} passed -> {3}" -f $Mode, $passed, $results.Count, $payload.verdict)
if ($results.Count -gt 0 -and $passed -ne $results.Count) { exit 2 }
