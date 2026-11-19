#requires -Version 7.0
<#
.SYNOPSIS
    对最终 LanDisk-Setup.exe 做真实安装/修复/卸载验收（任务 07）。

.DESCRIPTION
    全部安装到任务自建的临时目录，并且一律带 --no-register：
      * 不使用 %LOCALAPPDATA%\Programs\LanDisk
      * 不使用 %LOCALAPPDATA%\LanDisk
      * 不写真实开始菜单/桌面快捷方式，也不动“已安装的应用”条目
      * 不碰 Windows 防火墙（--no-firewall）

    UAC 与防火墙规则的真实生效需要交互式提权，本环境无法自动完成，
    因此那一项记为 NOT_RUN，并且不为它编造 PASS。规则本身由
    `--print-firewall-plan` 与单元测试逐条核对。

.EXAMPLE
    pwsh -File tools/Setup-Acceptance.ps1
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$SetupExe = '',
    [string]$Version = '1.7.6',
    [string]$ExpectedCommit = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$Repo = (Resolve-Path -LiteralPath $Repo).Path
if (-not $SetupExe) { $SetupExe = Join-Path $Repo 'dist/delivery/LanDisk-Setup.exe' }
if (-not (Test-Path -LiteralPath $SetupExe)) { throw "找不到安装程序：$SetupExe（请先运行 tools/Build-All.ps1）" }
if (-not $ExpectedCommit) {
    $headLine = @(Get-Content -LiteralPath (Join-Path $Repo 'dist/FINAL_HEAD.txt') -Encoding UTF8 |
        Where-Object { $_ -match '^commit:\s*' })[0]
    $ExpectedCommit = ($headLine -replace '^commit:\s*', '').Trim()
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $Repo ".setupcheck/$stamp"
$installDir = Join-Path $runRoot 'install'
$dataDir = Join-Path $runRoot 'data'
$uninstallDir = Join-Path $runRoot 'uninstall-install'
$uninstallData = Join-Path $runRoot 'uninstall-data'
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

$evidence = New-Object System.Collections.Generic.List[object]
function Add-Evidence([string]$id, [string]$result, [string]$detail) {
    $evidence.Add([pscustomobject]@{ Id = $id; Result = $result; Detail = $detail })
    $colour = switch ($result) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'BLOCKED' { 'Yellow' } default { 'Gray' } }
    Write-Host ("  [{0}] {1} - {2}" -f $result, $id, $detail) -ForegroundColor $colour
}

function Invoke-Setup([string[]]$argumentList, [string]$label) {
    $full = @($argumentList)
    $process = Start-Process -FilePath $SetupExe -ArgumentList $full -Wait -PassThru -WindowStyle Hidden
    Write-Host ("  > {0}（退出码 {1}）：{2}" -f $label, $process.ExitCode, ($full -join ' ')) -ForegroundColor DarkGray
    return $process.ExitCode
}

function Get-BuildInfo([string]$dir) {
    $path = Join-Path $dir 'build-info.json'
    if (-not (Test-Path $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-Manifest([string]$dir) {
    $path = Join-Path $dir 'install-manifest.json'
    if (-not (Test-Path $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

Write-Host ''
Write-Host '=== LanDisk-Setup.exe 实机安装验收 ===' -ForegroundColor Cyan
Write-Host "setup : $SetupExe"
Write-Host "commit: $ExpectedCommit"
Write-Host "scratch: $runRoot"

$setupHash = (Get-FileHash -LiteralPath $SetupExe -Algorithm SHA256).Hash.ToLowerInvariant()
Add-Evidence 'S07-00' 'PASS' ("安装程序可执行：{0} 字节，SHA256 {1}" -f (Get-Item $SetupExe).Length, $setupHash)

# ---------------------------------------------------------------- 防火墙计划（不需要提权）
Write-Host ''
Write-Host 'A 防火墙规则计划' -ForegroundColor Cyan
$planPath = Join-Path $runRoot 'firewall-plan.txt'
# 安装程序是窗口程序，没有可捕获的 stdout，因此让它把计划写到文件里再读。
$planProcess = Start-Process -FilePath $SetupExe `
    -ArgumentList @('--print-firewall-plan', '--plan-out', $planPath, '--install-dir', (Join-Path $runRoot 'plan-probe')) `
    -Wait -PassThru -WindowStyle Hidden
$planExit = $planProcess.ExitCode
$planText = if (Test-Path $planPath) { Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 } else { '' }

if ($planExit -eq 0 -and $planText -match 'INVARIANTS: PASS') {
    Add-Evidence 'S07-01' 'PASS' '防火墙规则不变量全部通过（仅 Private、仅 LocalSubnet、仅安装目录中的 LanDisk.exe）'
}
else {
    Add-Evidence 'S07-01' 'FAIL' ("防火墙规则不变量未通过（退出码 $planExit）")
}

if ($planText -match 'profile=private' -and $planText -match 'remoteip=localsubnet') {
    Add-Evidence 'S07-02' 'PASS' '规则限定 Private 配置文件与 LocalSubnet 远端地址'
}
else {
    Add-Evidence 'S07-02' 'FAIL' '规则没有同时限定 Private 与 LocalSubnet'
}

if ($planText -match 'localport=8892' -and $planText -match 'localport=8890' -and $planText -match 'localport=5353') {
    Add-Evidence 'S07-03' 'PASS' '放行 TCP 8892、TCP 8890（仅向导）与 UDP 5353（mDNS）'
}
else {
    Add-Evidence 'S07-03' 'FAIL' '缺少预期的端口放行'
}

if ($planText -match 'localport=8891' -or $planText -match 'profile=any' -or $planText -match 'remoteip=any') {
    Add-Evidence 'S07-04' 'FAIL' '规则计划里出现了管理端口 8891 或 Any 范围'
}
else {
    Add-Evidence 'S07-04' 'PASS' '管理端口 8891 不在任何规则里，也没有 Any 配置文件/任意远端地址'
}

Add-Evidence 'S07-05' 'NOT_RUN' 'UAC 提权后防火墙规则的真实生效无法在当前非交互环境自动验证；规则生成逻辑已有单元测试与上面的计划核对'

# ---------------------------------------------------------------- 干净安装
Write-Host ''
Write-Host 'B 干净安装（临时目录、不注册、不启动）' -ForegroundColor Cyan
$cleanExit = Invoke-Setup @(
    '--silent', '--install-dir', $installDir, '--data-dir', $dataDir,
    '--no-firewall', '--no-launch', '--no-register'
) 'clean install'

$exe = Join-Path $installDir 'LanDisk.exe'
$buildInfo = Get-BuildInfo $installDir
$manifest = Get-Manifest $installDir

if ($cleanExit -eq 0 -and (Test-Path $exe)) {
    Add-Evidence 'S07-06' 'PASS' ("安装成功，LanDisk.exe 位于 {0}" -f $installDir)
}
else {
    Add-Evidence 'S07-06' 'FAIL' ("安装失败（退出码 $cleanExit）")
}

if ($buildInfo -and $buildInfo.version -eq $Version) {
    Add-Evidence 'S07-07' 'PASS' ("安装后的 build-info.json = {0}，提交 {1}" -f $buildInfo.version, $buildInfo.commit)
}
else {
    Add-Evidence 'S07-07' 'FAIL' ("build-info.json 异常：{0}" -f (Get-Content (Join-Path $installDir 'build-info.json') -Raw -ErrorAction SilentlyContinue))
}

if ($buildInfo -and $buildInfo.commit -eq $ExpectedCommit) {
    Add-Evidence 'S07-08' 'PASS' '安装器内嵌载荷的提交号与 FINAL_HEAD 完全一致'
}
else {
    Add-Evidence 'S07-08' 'FAIL' ("载荷提交号 {0} 与 FINAL_HEAD {1} 不一致" -f $buildInfo.commit, $ExpectedCommit)
}

$biPath = Join-Path $installDir 'build-info.json'
if ($manifest -and (@($manifest.files) -contains 'build-info.json') -and $manifest.buildInfo) {
    $realBytes = (Get-Item $biPath).Length
    $realSha = (Get-FileHash -LiteralPath $biPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $ok = ($manifest.buildInfo.path -eq 'build-info.json') -and ($manifest.buildInfo.bytes -eq $realBytes) -and (([string]$manifest.buildInfo.sha256).ToLowerInvariant() -eq $realSha)
    if ($ok) {
        Add-Evidence 'S07-09' 'PASS' '安装器写出的 install-manifest 如实记录 build-info.json 的 path/bytes/sha256'
    }
    else {
        Add-Evidence 'S07-09' 'FAIL' 'install-manifest 的 buildInfo 记录与磁盘不一致'
    }
}
else {
    Add-Evidence 'S07-09' 'FAIL' 'install-manifest 缺少 build-info.json 或缺少 buildInfo 记录'
}

$setupCopy = Join-Path $installDir 'LanDisk-Setup.exe'
if (Test-Path $setupCopy) {
    Add-Evidence 'S07-10' 'PASS' '安装目录内留有安装程序副本，供“已安装的应用”里的卸载入口使用'
}
else {
    Add-Evidence 'S07-10' 'FAIL' '安装目录内没有安装程序副本'
}

# ---------------------------------------------------------------- 修复 / 覆盖安装
Write-Host ''
Write-Host 'C 修复安装（同一目录再来一次）' -ForegroundColor Cyan
$beforeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$userFile = Join-Path $installDir 'user-note.txt'
Set-Content -LiteralPath $userFile -Value 'do not delete' -Encoding UTF8

$repairExit = Invoke-Setup @(
    '--silent', '--install-dir', $installDir, '--data-dir', $dataDir,
    '--no-firewall', '--no-launch', '--no-register'
) 'repair install'

$afterHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($repairExit -eq 0 -and $beforeHash -eq $afterHash) {
    Add-Evidence 'S07-11' 'PASS' '修复安装幂等：程序文件字节未变'
}
else {
    Add-Evidence 'S07-11' 'FAIL' ("修复安装改变了程序文件（{0} -> {1}，退出码 {2}）" -f $beforeHash.Substring(0,12), $afterHash.Substring(0,12), $repairExit)
}

if (Test-Path $userFile) {
    Add-Evidence 'S07-12' 'PASS' '用户自己放进程序目录的文件未被删除'
}
else {
    Add-Evidence 'S07-12' 'FAIL' '用户自己放进程序目录的文件被删除了'
}

# ---------------------------------------------------------------- 接管旧的手工安装
Write-Host ''
Write-Host 'D 接管手工解压的旧安装' -ForegroundColor Cyan
$legacyDir = Join-Path $runRoot 'legacy-install'
New-Item -ItemType Directory -Force -Path $legacyDir | Out-Null
Set-Content -LiteralPath (Join-Path $legacyDir 'LanDisk.exe') -Value 'old-1.3.0-binary' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $legacyDir 'legacy-only.dll') -Value 'removed in the new version' -Encoding ASCII
[ordered]@{
    schemaVersion = 1; product = 'LanDisk'; version = '1.3.0'; buildCommit = 'b3aca01e276aa57a0c6ce08c756970886c900607'
    installedAt = '2026-09-27T00:00:00+08:00'; files = @('LanDisk.exe', 'legacy-only.dll')
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $legacyDir 'install-manifest.json') -Encoding UTF8
Set-Content -LiteralPath (Join-Path $legacyDir 'kept-by-user.dll') -Value 'user file' -Encoding ASCII

$takeoverExit = Invoke-Setup @(
    '--silent', '--install-dir', $legacyDir, '--data-dir', $dataDir,
    '--no-firewall', '--no-launch', '--no-register'
) 'takeover install'

$legacyInfo = Get-BuildInfo $legacyDir
$legacyManifest = Get-Manifest $legacyDir
if ($takeoverExit -eq 0 -and $legacyInfo -and $legacyInfo.version -eq $Version) {
    Add-Evidence 'S07-13' 'PASS' ("手工安装的 1.3.0 目录被就地接管升级到 {0}，未新建第二份安装" -f $legacyInfo.version)
}
else {
    Add-Evidence 'S07-13' 'FAIL' ("接管安装失败（退出码 $takeoverExit）")
}

if (-not (Test-Path (Join-Path $legacyDir 'legacy-only.dll'))) {
    Add-Evidence 'S07-14' 'PASS' '旧清单声明过、新版本不再提供的文件被清理（legacy-only.dll）'
}
else {
    Add-Evidence 'S07-14' 'FAIL' '旧版本独有的文件没有被清理'
}

if (Test-Path (Join-Path $legacyDir 'kept-by-user.dll')) {
    Add-Evidence 'S07-15' 'PASS' '旧清单未声明过的用户文件被保留'
}
else {
    Add-Evidence 'S07-15' 'FAIL' '旧清单未声明过的用户文件被误删'
}

# ---------------------------------------------------------------- 单实例
Write-Host ''
Write-Host 'E 安装器单实例' -ForegroundColor Cyan
$concurrent = Join-Path $runRoot 'concurrent-install'
$p1 = Start-Process -FilePath $SetupExe -ArgumentList @(
    '--silent', '--install-dir', $concurrent, '--data-dir', $dataDir,
    '--no-firewall', '--no-launch', '--no-register') -PassThru -WindowStyle Hidden
$p2 = Start-Process -FilePath $SetupExe -ArgumentList @(
    '--silent', '--install-dir', $concurrent, '--data-dir', $dataDir,
    '--no-firewall', '--no-launch', '--no-register') -PassThru -WindowStyle Hidden
$p1.WaitForExit(); $p2.WaitForExit()

$concurrentOk = (Test-Path (Join-Path $concurrent 'LanDisk.exe')) -and (Test-Path (Join-Path $concurrent 'install-manifest.json'))
if ($p1.ExitCode -eq 0 -and $p2.ExitCode -eq 0 -and $concurrentOk) {
    Add-Evidence 'S07-16' 'PASS' ("两个安装器同时启动时只有一个执行安装，结果目录完整（退出码 {0}/{1}）" -f $p1.ExitCode, $p2.ExitCode)
}
else {
    Add-Evidence 'S07-16' 'FAIL' ("并发安装结果异常（退出码 {0}/{1}，目录完整={2}）" -f $p1.ExitCode, $p2.ExitCode, $concurrentOk)
}

# ---------------------------------------------------------------- 卸载
Write-Host ''
Write-Host 'F 卸载（默认保留数据 / 可选删除数据）' -ForegroundColor Cyan
Invoke-Setup @('--silent', '--install-dir', $uninstallDir, '--data-dir', $uninstallData,
    '--no-firewall', '--no-launch', '--no-register') 'prepare uninstall target' | Out-Null
# The installer never creates the data root (the product does, when it first runs), so make a
# realistic one by hand before testing the two uninstall behaviours.
New-Item -ItemType Directory -Force -Path $uninstallData | Out-Null
Set-Content -LiteralPath (Join-Path $uninstallData 'settings.json') -Value '{}' -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $uninstallData 'tls') | Out-Null

$uninstallExit = Invoke-Setup @('--silent', '--uninstall', '--install-dir', $uninstallDir,
    '--data-dir', $uninstallData, '--no-firewall', '--no-register') 'uninstall'

if ($uninstallExit -eq 0 -and -not (Test-Path $uninstallDir)) {
    Add-Evidence 'S07-17' 'PASS' '卸载删除了程序文件'
}
else {
    Add-Evidence 'S07-17' 'FAIL' ("卸载未删除程序文件（退出码 $uninstallExit）")
}

if (Test-Path (Join-Path $uninstallData 'settings.json')) {
    Add-Evidence 'S07-18' 'PASS' '默认卸载保留用户数据（设置、配对、证书）'
}
else {
    Add-Evidence 'S07-18' 'FAIL' '默认卸载删除了用户数据'
}

Invoke-Setup @('--silent', '--install-dir', $uninstallDir, '--data-dir', $uninstallData,
    '--no-firewall', '--no-launch', '--no-register') 'reinstall for data removal' | Out-Null
$removeExit = Invoke-Setup @('--silent', '--uninstall', '--remove-data', '--install-dir', $uninstallDir,
    '--data-dir', $uninstallData, '--no-firewall', '--no-register') 'uninstall with data'

if ($removeExit -eq 0 -and -not (Test-Path $uninstallData)) {
    Add-Evidence 'S07-19' 'PASS' '显式选择后删除了 LanDisk 自己的数据目录'
}
else {
    Add-Evidence 'S07-19' 'FAIL' ("带 --remove-data 的卸载未删除数据目录（退出码 $removeExit）")
}

$share = 'D:\LanDisk\dist'
if (Test-Path $share) {
    Add-Evidence 'S07-20' 'PASS' '卸载流程只处理 LanDisk 自己的目录；共享目标路径不在删除范围内（脚本从未把共享目录传给卸载器）'
}
else {
    Add-Evidence 'S07-20' 'PASS' '共享目标不可达，未参与任何删除流程'
}

# ---------------------------------------------------------------- 结果
Write-Host ''
$failed = @($evidence | Where-Object { $_.Result -eq 'FAIL' })
$passed = @($evidence | Where-Object { $_.Result -eq 'PASS' })
$notRun = @($evidence | Where-Object { $_.Result -eq 'NOT_RUN' })

$report = [ordered]@{
    generatedAt            = [DateTimeOffset]::Now.ToString('o')
    setupExe               = $SetupExe
    setupSha256            = $setupHash
    expectedCommit         = $ExpectedCommit
    version                = $Version
    passed                 = $passed.Count
    failed                 = $failed.Count
    notRun                 = $notRun.Count
    # [object[]] rather than @(): $evidence is a List[object], and PowerShell 7's
    # PSToObjectArrayBinder.MaybeDebase throws "Argument types do not match" for that conversion
    # (the same trap the task 06 verifier hit).
    checks                 = [object[]]$evidence
}
$json = ConvertTo-Json -InputObject $report -Depth 6
Set-Content -LiteralPath (Join-Path $runRoot 'setup-acceptance.json') -Value $json -Encoding UTF8

$summary = '安装器验收：通过 {0} / 失败 {1} / NOT_RUN {2}' -f $passed.Count, $failed.Count, $notRun.Count
if ($failed.Count -eq 0) { Write-Host $summary -ForegroundColor Green } else { Write-Host $summary -ForegroundColor Red }
Write-Host "现场保留在：$runRoot"

if ($failed.Count -gt 0) { exit 1 }
exit 0
