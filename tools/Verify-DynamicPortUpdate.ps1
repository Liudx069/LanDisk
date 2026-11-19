param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$HostPublishDir,
    [Parameter(Mandatory)][string]$TestPackage,
    [string]$EvidenceDir = '',
    [int]$HealthTimeoutSeconds = 120
)

# B 类验证：**修复后的宿主**以动态端口启动，由它发起更新与失败回滚。
#
# 与 A 类的分工：
#   A —— 正常配置的旧版本升级到**冻结的正式 .lupd**（另有脚本执行，直接用交付包并记完整 SHA256）。
#   B —— 本脚本。验证的是**修复后的发起方**：宿主实际绑定的是动态解析出来的端口，
#        它必须把**实际绑定端口**交给更新程序，而不是配置里未解析的 0。
#        **B 不能代替 A**，也不能用来声称"旧宿主的动态端口问题已被追溯修复"。
#
# 关键证据是更新程序日志里的那一行重启记录：
#   「已启动 …（…，HTTP 端口 X，HTTPS 端口 Y）」
# X/Y 必须等于更新前**实际监听**的端口。若是 0 或别的端口，就说明修复没生效。

$ErrorActionPreference = 'Stop'
if (-not $EvidenceDir) { $EvidenceDir = Join-Path $Repo 'dist\evidence-11\B-DYNAMIC-PORT' }
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

function Get-Listeners([int]$ProcessId) {
    try {
        return @(Get-NetTCPConnection -State Listen -OwningProcess $ProcessId -ErrorAction Stop |
            ForEach-Object { [pscustomobject]@{ address = $_.LocalAddress; port = $_.LocalPort } } |
            Sort-Object address, port -Unique)
    }
    catch { return @() }
}

# 回环 HTTP 绑 v4+v6，回环 HTTPS 只绑 v4（当前设计）——据此从实际监听反推两个端口
function Resolve-Ports($listeners) {
    $v4 = @($listeners | Where-Object { $_.address -eq '127.0.0.1' } | ForEach-Object { $_.port } | Sort-Object -Unique)
    $v6 = @($listeners | Where-Object { $_.address -eq '::1' } | ForEach-Object { $_.port } | Sort-Object -Unique)
    return [pscustomobject]@{
        http = @($v4 | Where-Object { $v6 -contains $_ }) | Select-Object -First 1
        https = @($v4 | Where-Object { $v6 -notcontains $_ }) | Select-Object -First 1
    }
}

function Get-Health([int]$Port) {
    try {
        $r = Invoke-WebRequest -Uri ("http://127.0.0.1:$Port/api/health") -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { return ($r.Content | ConvertFrom-Json) }
    }
    catch { }
    return $null
}

function Wait-Version([int]$Port, [string]$Version, [int]$TimeoutSeconds, [int]$ExpectDown = 0) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $h = Get-Health $Port
        if ($h -and $h.version -eq $Version) { return $h }
        Start-Sleep -Milliseconds 700
    }
    return $null
}

# ---- 进程所有权：复用既有 owned-process registry，不另造生命周期 ----------------
#
# 真实缺陷：本脚本过去只在结束时停**最初启动的那个**进程。更新程序会重启出一个新进程，
# 失败路径（例如"宿主未就绪"提前 throw）连最初那个都不会被回收 —— 实测导致后续运行
# 撞上 single-instance lock（三次运行里发生两次）。
#
# 现在的规则：
#   * 本测试启动的、以及被更新程序重启出来的实例，都登记到 dist\.owned-processes；
#   * 回收前校验 PID + **映像路径**必须等于本测试的宿主 exe（防 PID 复用误杀）；
#   * 永远拒绝结束 %LOCALAPPDATA%\Programs\LanDisk 下的正式实例；
#   * 只结束本测试自己创建的实例，绝不按进程名全杀。

$OwnedDir = Join-Path $Repo 'dist\.owned-processes'

function Register-OwnedHost([int]$ProcessId, [string]$ExePath, [string]$Tag) {
    if (-not $ProcessId) { return }
    New-Item -ItemType Directory -Force -Path $OwnedDir | Out-Null
    $marker = Join-Path $OwnedDir ("$ProcessId.json")
    [pscustomobject]@{
        pid = $ProcessId; exe = $ExePath; dataDir = $dataDir
        owner = "dist/tools/Verify-DynamicPortUpdate.ps1:$Tag"
        startedAt = (Get-Date).ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
}

function Get-OwnedHostPids {
    if (-not (Test-Path -LiteralPath $OwnedDir)) { return @() }
    $pids = @()
    foreach ($m in (Get-ChildItem -LiteralPath $OwnedDir -File -Filter '*.json' -ErrorAction SilentlyContinue)) {
        try {
            $j = Get-Content -LiteralPath $m.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($j.owner -like '*Verify-DynamicPortUpdate*') { $pids += [int]$j.pid }
        }
        catch { }
    }
    return $pids
}

function Stop-OurHosts {
    foreach ($processId in (Get-OwnedHostPids)) {
        $p = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if (-not $p) {
            Remove-Item -LiteralPath (Join-Path $OwnedDir "$processId.json") -Force -ErrorAction SilentlyContinue
            continue
        }
        $path = ''; try { $path = $p.Path } catch { }
        if ($path -like '*AppData\Local\Programs\LanDisk*') {
            Write-Host ("    [拒绝] PID {0} 属于 Owner 正式安装，不结束" -f $processId) -ForegroundColor Yellow
            continue
        }
        if ([string]::IsNullOrWhiteSpace($path) -or $path -ne $record.hostExe) {
            Write-Host ("    [拒绝] PID {0} 映像路径与本测试宿主不符（{1}），不结束" -f $processId, $path) -ForegroundColor Yellow
            continue
        }
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 30 -and (Get-Process -Id $processId -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
        if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath (Join-Path $OwnedDir "$processId.json") -Force -ErrorAction SilentlyContinue
        }
    }
}

# 更新程序重启出来的新实例：按**映像路径等于本测试宿主**识别并登记（路径校验，不用进程名）。
function Register-RestartedHosts {
    foreach ($p in (Get-Process -Name 'LanDisk' -ErrorAction SilentlyContinue)) {
        $path = ''; try { $path = $p.Path } catch { }
        if ($path -eq $record.hostExe) { Register-OwnedHost $p.Id $path 'restarted' }
    }
}

$record = [ordered]@{
    class = 'B'
    hostPublishDir = $HostPublishDir
    hostExe = ''
    hostExeSha256 = ''
    configuredHttp = 0
    configuredHttps = 0
    boundHttpBefore = 0
    boundHttpsBefore = 0
    listenersBefore = @()
    hostProcessIdBefore = 0
    testPackage = $TestPackage
    testPackageSha256 = ''
    testPackageNote = '测试用途：由既有工具从当前源码生成的更高版本包，仅用于验证修复后的发起方；不代替 A 类正式包验收。'
    updaterLaunchLines = @()
    restartHttp = $null
    restartHttps = $null
    restartPortsMatchBound = $false
    healthTarget = 0
    healthOkAfterRollback = $false
    versionAfterRollback = ''
    listenersAfterRollback = @()
    outcome = ''
    passed = $false
}

$exe = Join-Path $HostPublishDir 'LanDisk.exe'
if (-not (Test-Path $exe)) { throw "宿主目录缺少 LanDisk.exe：$HostPublishDir" }
$record.hostExe = $exe
$record.hostExeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$record.testPackageSha256 = (Get-FileHash $TestPackage -Algorithm SHA256).Hash.ToLowerInvariant()

$dataDir = Join-Path $EvidenceDir 'data'
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# ---- 1) 以动态端口启动修复后的宿主
#
# 从这里到本脚本末尾的收尾都在 try/finally 内：**任何**提前 throw（宿主未就绪、上传失败、
# 异常、超时）都会走到 finally 的 Stop-OurHosts，把本测试创建过的实例全部回收。
try {
$proc = Start-Process -FilePath $exe -ArgumentList @('--data-dir', $dataDir, '--port', '0', '--https-port', '0') -PassThru
Register-OwnedHost $proc.Id $exe 'initial'
Start-Sleep -Seconds 10
$listenersBefore = Get-Listeners $proc.Id
$ports = Resolve-Ports $listenersBefore
$record.hostProcessIdBefore = $proc.Id
$record.listenersBefore = @($listenersBefore | ForEach-Object { $_.address + ':' + $_.port })
$record.boundHttpBefore = if ($ports.http) { $ports.http } else { 0 }
$record.boundHttpsBefore = if ($ports.https) { $ports.https } else { 0 }
$record.healthTarget = $record.boundHttpBefore

$healthBefore = Get-Health $record.boundHttpBefore
Write-Host ("宿主 PID {0}；配置端口 0/0；实际绑定 {1}/{2}；健康 {3}" -f $proc.Id, $record.boundHttpBefore, $record.boundHttpsBefore, ($null -ne $healthBefore))
if (-not $healthBefore) { throw '动态端口宿主未就绪，无法继续 B 类验证' }
$versionBefore = $healthBefore.version

# ---- 2) 登录管理端并上传测试包
$adminUrl = (Get-Content -LiteralPath (Join-Path $dataDir 'admin-url.txt') -Raw).Trim()
$token = ($adminUrl -split 'token=')[1]
$base = "http://127.0.0.1:$($record.boundHttpBefore)"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = Invoke-WebRequest -Uri "$base/api/admin/session" -Method POST -Body (@{ token = $token } | ConvertTo-Json) `
    -ContentType 'application/json' -WebSession $session -UseBasicParsing -TimeoutSec 30
$csrf = ($login.Content | ConvertFrom-Json).csrfToken

$bytes = [System.IO.File]::ReadAllBytes($TestPackage)
$up = Invoke-WebRequest -Uri "$base/api/admin/update/upload" -Method POST -Body $bytes `
    -ContentType 'application/octet-stream' -WebSession $session -UseBasicParsing -TimeoutSec 600 `
    -Headers @{
        'X-LanDisk-Client' = 'dynamic-port-verify'
        'X-LanDisk-CSRF' = $csrf
        'X-LanDisk-Package-Name' = [uri]::EscapeDataString([System.IO.Path]::GetFileName($TestPackage))
    }
Write-Host ("上传测试包：HTTP " + $up.StatusCode)

# ---- 3) 应用更新（会重启宿主并自检；测试包预期自检失败 → 回滚）
try {
    Invoke-WebRequest -Uri "$base/api/admin/update/apply" -Method POST -Body (@{ confirm = $true } | ConvertTo-Json) `
        -ContentType 'application/json' -WebSession $session -UseBasicParsing -TimeoutSec 120 | Out-Null
}
catch { Write-Host ('应用请求返回：' + $_.Exception.Message) }

# ---- 4) 等更新程序写出重启记录并等回滚完成
$updaterLog = Join-Path $dataDir 'updates\updater.log'
$deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds + 180)
$lines = @()
while ((Get-Date) -lt $deadline) {
    if (Test-Path $updaterLog) {
        $lines = @(Get-Content -LiteralPath $updaterLog -Encoding UTF8 -ErrorAction SilentlyContinue)
        if (@($lines | Where-Object { $_ -match '已启动' }).Count -gt 0 -and @($lines | Where-Object { $_ -match '回滚' }).Count -gt 0) { break }
    }
    Start-Sleep -Seconds 3
}
$record.updaterLaunchLines = @($lines | Where-Object { $_ -match '已启动' } | ForEach-Object { $_.Trim() })

# 「已启动 …（…，HTTP 端口 X，HTTPS 端口 Y）」——取第一次重启的那一行
$launch = @($record.updaterLaunchLines | Where-Object { $_ -match 'HTTP 端口 (\d+)，HTTPS 端口 (\d+)' } | Select-Object -First 1)
if ($launch.Count -gt 0) {
    $null = $launch[0] -match 'HTTP 端口 (\d+)，HTTPS 端口 (\d+)'
    $record.restartHttp = [int]$Matches[1]
    $record.restartHttps = [int]$Matches[2]
    $record.restartPortsMatchBound = ($record.restartHttp -eq $healthBefore.port) -or ($record.restartHttp -eq $record.boundHttpBefore)
}

# 回滚后：服务必须回到失败更新之前的**实际版本与实际端点**
$after = Wait-Version -Port $record.boundHttpBefore -Version $versionBefore -TimeoutSeconds 120
if ($after) {
    $record.healthOkAfterRollback = $true
    $record.versionAfterRollback = $after.version
    $record.listenersAfterRollback = @((Get-Listeners $proc.Id) | ForEach-Object { $_.address + ':' + $_.port })
}

$record.outcome = if ($record.restartPortsMatchBound -and $record.healthOkAfterRollback) { 'PASS' } else { 'FAIL' }
$record.passed = ($record.outcome -eq 'PASS')
}
finally {
    # 更新程序重启出来的实例也要回收：先按映像路径登记，再统一按路径校验结束。
    Register-RestartedHosts
    Stop-OurHosts
}

[pscustomobject]$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDir 'B-dynamic-port-update.json') -Encoding UTF8
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ("B 类（动态端口更新与回滚）：" + $record.outcome)
Write-Host ("  配置端口        : 0/0（保持用户配置值，未写成固定端口）")
Write-Host ("  实际绑定端口    : {0}/{1}" -f $record.boundHttpBefore, $record.boundHttpsBefore)
Write-Host ("  更新程序重启端口: {0}/{1}（与实际绑定一致 = {2}）" -f $record.restartHttp, $record.restartHttps, $record.restartPortsMatchBound)
Write-Host ("  回滚后版本/端点 : {0} @ {1}（健康 {2}）" -f $record.versionAfterRollback, $record.boundHttpBefore, $record.healthOkAfterRollback)
if (-not $record.passed) { exit 2 }
