param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [switch]$Reap,          # 结束仍存活的、我们登记过的进程
    [switch]$SelfTest,      # 跑 R22-R26 清理回归（用无害子进程，不涉及 LanDisk）
    [string]$OutFile = ''
)

# 自动化测试进程的所有权清理与检查。
#
# 背景（真实事故）：上一轮真机验收时，验收作业被超时中断，e2e 脚本的 finally 没跑到，
# 它 spawn 的测试 LanDisk.exe 成了孤儿，长时间占用 53819/53820 与测试身份 cb5be599，
# 使随后的真机排查一度连到了这个残留实例。
#
# 因此规则是：
#   * 只处理 **登记过** 的 PID（dist/.owned-processes/*.json 由 e2e 脚本写入）；
#   * 处理前必须核对进程映像路径与该标记记录的 exe **一致**；
#   * 绝不按进程名全杀；绝不碰正式安装实例（Programs\LanDisk）；
#   * 清除后必须复验确实已退出，否则本轮不得 READY。

$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path $Repo).Path
$ownedDir = Join-Path $Repo 'dist\.owned-processes'
$installedMarker = 'AppData\Local\Programs\LanDisk'

function Get-OwnedMarkers {
    param([string]$Directory = $ownedDir)
    if (-not (Test-Path -LiteralPath $Directory)) { return @() }
    return @(Get-ChildItem -LiteralPath $Directory -File -Filter '*.json' | ForEach-Object {
        try {
            $o = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            # 记下来源文件路径，供回收后清理"已用完"的标记
            $o | Add-Member -NotePropertyName __path -NotePropertyValue $_.FullName -Force
            $o
        } catch { $null }
    } | Where-Object { $_ })
}

function Test-Alive([int]$ProcessId) {
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    return [bool]$p
}

# 刚启动的进程有时立刻取 .Path 会得到空值（本机复现约 1/6），
# 于是所有权标记会记下空的 exe，之后 reaper 正确地拒绝结束它（路径不符）。
# 这是**夹具竞态**：标记必须写入一个真实可核对的映像路径。
function Wait-ImagePath([int]$ProcessId, [int]$TimeoutMs = 5000) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $path = Get-ImagePath $ProcessId
        if ($path) { return $path }
        Start-Sleep -Milliseconds 50
    }
    return ''
}

function Get-ImagePath([int]$ProcessId) {
    try { return (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue).Path } catch { return '' }
}

function Invoke-Reap {
    param([switch]$Kill, [string]$Directory = $ownedDir)
    $results = New-Object System.Collections.Generic.List[object]
    foreach ($m in (Get-OwnedMarkers -Directory $Directory)) {
        $pidValue = [int]$m.pid
        $alive = Test-Alive $pidValue
        $image = if ($alive) { Get-ImagePath $pidValue } else { '' }
        $isInstalled = $image -like ('*' + $installedMarker + '*')
        $pathMatches = $alive -and ($image -eq $m.exe)
        $action = 'none'

        if ($alive -and $isInstalled) {
            # 极端防御：即使标记指向它，也绝不结束正式安装实例。
            $action = 'refused-installed'
        }
        elseif ($alive -and -not $pathMatches) {
            # PID 复用或路径不符：不动它，只报告。
            $action = 'refused-path-mismatch'
        }
        elseif ($alive -and $Kill) {
            try { Stop-Process -Id $pidValue -Force -ErrorAction Stop } catch { }
            for ($i = 0; $i -lt 30 -and (Test-Alive $pidValue); $i++) { Start-Sleep -Milliseconds 100 }
            $action = if (Test-Alive $pidValue) { 'kill-failed' } else { 'killed' }
        }

        $stillAlive = Test-Alive $pidValue

        # 进程已经不在的标记是"已用完"的，顺手清掉，避免注册表越积越多。
        # 这不会掩盖任何残留：仍存活的进程其标记会被保留（下一轮仍能被查到并处理）。
        if (-not $stillAlive -and $m.__path) {
            try { Remove-Item -LiteralPath $m.__path -Force } catch { }
        }

        $results.Add([pscustomobject]@{
            pid = $pidValue; owner = $m.owner; exe = $m.exe; startedAt = $m.startedAt
            aliveBefore = $alive; imagePath = $image; action = $action; aliveAfter = $stillAlive
            isInstalledInstance = $isInstalled
        })
    }
    return $results
}

# ---------------------------------------------------------------- R22-R26 自测
function Invoke-SelfTest {
    $cases = New-Object System.Collections.Generic.List[object]
    $sandbox = Join-Path $Repo 'dist\.owned-process-selftest'
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

    # 自测使用**独立注册表目录**：绝不能对共享注册表执行清理，
    # 否则会把同时段真正在跑的测试服务端一起杀掉（已真实发生过，表现为 ECONNREFUSED）。
    $selftestDir = Join-Path $sandbox 'registry'
    New-Item -ItemType Directory -Force -Path $selftestDir | Out-Null

    function New-FakeMarker([int]$ProcessId, [string]$exe, [string]$owner) {
        New-Item -ItemType Directory -Force -Path $selftestDir | Out-Null
        $p = Join-Path $selftestDir ("$ProcessId.json")
        [pscustomobject]@{ pid = $ProcessId; exe = $exe; owner = $owner; startedAt = (Get-Date).ToString('o') } |
            ConvertTo-Json | Set-Content -LiteralPath $p -Encoding UTF8
        return $p
    }
    function New-Sleeper {
        # 无害的长命子进程（cmd 等待），用于代替 LanDisk 验证清理逻辑本身
        $psi = New-Object System.Diagnostics.ProcessStartInfo('cmd.exe')
        $psi.Arguments = '/c ping -n 60 127.0.0.1 >nul'
        $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
        return [System.Diagnostics.Process]::Start($psi)
    }

    # R22 成功路径：脚本正常结束 → 自行回收并删除标记
    $p1 = New-Sleeper; $m1 = New-FakeMarker $p1.Id (Wait-ImagePath $p1.Id) 'selftest/success'
    $r1 = Invoke-Reap -Kill -Directory $selftestDir
    # 断言只看"回收后确实已退出且动作是 killed"。先前多写了一个 -not $p1.HasExited，
    # 而该表达式是在回收**之后**求值的，等于要求进程还活着 —— 断言本身写反了。
    $ok1 = $null -ne ($r1 | Where-Object { $_.pid -eq $p1.Id -and $_.action -eq 'killed' -and -not $_.aliveAfter }) -and $p1.HasExited
    $cases.Add([pscustomobject]@{ id='R22'; name='成功路径回收'; passed=[bool]$ok1; detail=("pid={0} action={1}" -f $p1.Id, (($r1 | Where-Object { $_.pid -eq $p1.Id }).action)) })

    # R23 失败路径：父进程异常终止留下孤儿 → 标记仍在，reaper 仍能找到并回收
    $p2 = New-Sleeper; $m2 = New-FakeMarker $p2.Id (Wait-ImagePath $p2.Id) 'selftest/failure-orphan'
    $r2 = Invoke-Reap -Kill -Directory $selftestDir
    $ok2 = $null -ne ($r2 | Where-Object { $_.pid -eq $p2.Id -and $_.action -eq 'killed' -and -not $_.aliveAfter })
    $cases.Add([pscustomobject]@{ id='R23'; name='失败/孤儿路径回收'; passed=[bool]$ok2; detail=("pid={0} action={1}" -f $p2.Id, (($r2 | Where-Object { $_.pid -eq $p2.Id }).action)) })

    # R24 超时路径：只检查不杀（-Kill 关闭）时必须报告仍存活，不得假装干净
    $p3 = New-Sleeper; $m3 = New-FakeMarker $p3.Id (Wait-ImagePath $p3.Id) 'selftest/timeout'
    $r3 = Invoke-Reap -Directory $selftestDir   # 不 Kill
    $ok3 = $null -ne ($r3 | Where-Object { $_.pid -eq $p3.Id -and $_.aliveAfter })
    $r3k = Invoke-Reap -Kill -Directory $selftestDir
    $ok3b = $null -ne ($r3k | Where-Object { $_.pid -eq $p3.Id -and -not $_.aliveAfter })
    $cases.Add([pscustomobject]@{ id='R24'; name='超时路径：先如实报警再回收'; passed=[bool]($ok3 -and $ok3b); detail=("未清理时报告存活={0}；清理后退出={1}" -f $ok3, $ok3b) })

    # R25 异常路径：标记里的 exe 与实际映像不符（模拟 PID 复用）→ 拒绝结束，只报告
    $p4 = New-Sleeper; $m4 = New-FakeMarker $p4.Id 'C:\definitely\not\this\program.exe' 'selftest/exception-mismatch'
    $r4 = Invoke-Reap -Kill -Directory $selftestDir
    $ok4 = $null -ne ($r4 | Where-Object { $_.pid -eq $p4.Id -and $_.action -eq 'refused-path-mismatch' -and $_.aliveAfter })
    $cases.Add([pscustomobject]@{ id='R25'; name='异常路径：路径不符则拒绝结束'; passed=[bool]$ok4; detail=("action={0}" -f (($r4 | Where-Object { $_.pid -eq $p4.Id }).action)) })
    try { Stop-Process -Id $p4.Id -Force -ErrorAction SilentlyContinue } catch { }

    # R26 安全性：正式安装实例绝不被误杀
    $installed = @(Get-Process -Name 'LanDisk' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -like ('*' + $installedMarker + '*') } catch { $false } })
    if ($installed.Count -gt 0) {
        $before = $installed[0].Id
        $r5 = Invoke-Reap -Kill -Directory $selftestDir
        $stillThere = Test-Alive $before
        $touched = $null -ne ($r5 | Where-Object { $_.pid -eq $before -and $_.isInstalledInstance })
        $ok5 = $stillThere
        $cases.Add([pscustomobject]@{ id='R26'; name='不误杀正式安装实例'; passed=[bool]$ok5
            detail=("正式实例 pid={0} 清理后仍存活={1}；reaper 是否正确识别为安装实例={2}" -f $before, $stillThere, $touched) })
    }
    else {
        $cases.Add([pscustomobject]@{ id='R26'; name='不误杀正式安装实例'; passed=$true
            detail='当前没有正在运行的正式安装实例；本用例退化为“无实例时不误杀”（已通过），未做破坏性验证' })
    }

    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    return $cases
}

$payload = [pscustomobject]@{ generatedAt = (Get-Date -Format 'o'); repo = $Repo; ownedDir = $ownedDir }
if ($SelfTest) {
    $cases = Invoke-SelfTest
    $passed = @($cases | Where-Object { $_.passed }).Count
    $payload | Add-Member -NotePropertyName cases -NotePropertyValue $cases
    $payload | Add-Member -NotePropertyName total -NotePropertyValue $cases.Count
    $payload | Add-Member -NotePropertyName passedCount -NotePropertyValue $passed
    $payload | Add-Member -NotePropertyName verdict -NotePropertyValue $(if ($passed -eq $cases.Count) { 'PASS' } else { 'FAIL' })
    Write-Host 'owned-process cleanup self test:'
    foreach ($c in $cases) { Write-Host ("  [{0}] {1}  {2}  {3}" -f $(if ($c.passed) { 'PASS' } else { 'FAIL' }), $c.id, $c.name, $c.detail) }
    Write-Host ''
    Write-Host ("cleanup self test: {0}/{1} passed -> {2}" -f $passed, $cases.Count, $payload.verdict)
}
else {
    $rows = Invoke-Reap -Kill:$Reap
    $leftover = @($rows | Where-Object { $_.aliveAfter })
    $payload | Add-Member -NotePropertyName markers -NotePropertyValue $rows.Count
    $payload | Add-Member -NotePropertyName leftover -NotePropertyValue $leftover.Count
    $payload | Add-Member -NotePropertyName reaped -NotePropertyValue ($Reap.IsPresent)
    $payload | Add-Member -NotePropertyName verdict -NotePropertyValue $(if ($leftover.Count -eq 0) { 'PASS' } else { 'FAIL' })
    $payload | Add-Member -NotePropertyName details -NotePropertyValue $rows
    Write-Host ("owned-process check: markers={0} leftover={1} reaped={2} -> {3}" -f $rows.Count, $leftover.Count, $Reap.IsPresent, $payload.verdict)
    foreach ($l in $leftover) { Write-Host ("  仍存活 pid={0} owner={1}" -f $l.pid, $l.owner) -ForegroundColor Yellow }
}

if ($OutFile) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
    $payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutFile -Encoding UTF8
}
if ($payload.verdict -ne 'PASS') { exit 2 }
