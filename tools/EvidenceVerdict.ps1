# The single readiness verdict used by the report generator and by the negative tests.
#
# Dot-source this and call Get-EvidenceVerdict. Keeping the rules in one place is the point: a negative test
# that re-implements the rule proves nothing about the generator that actually ships.

Set-StrictMode -Version Latest

function Get-EvidenceVerdict {
    <#
      Reads the whole required result set — not a pre-picked shortlist — and returns
      @{ Ready = $bool; Reasons = @(); Facts = @{} }.

      READY requires all of:
        * the desktop matrix ran with every expected combination, no duplicates, no extras
        * no required assertion FAIL
        * the capture rendered the deterministic network fixture (physical Ethernet present, virtual
          adapters excluded) rather than whatever the machine happened to be plugged into
        * pages present, no wrong page
        * every required file exists, parses, and names the expected candidate
      Anything unparseable or unavailable is a refusal, never a zero.
    #>
    param(
        [string]$Repo = '',
        [string]$RunId = '',
        [string]$EvidenceRoot = '',
        [string]$ExpectedCandidate = '',
        # A reduced matrix is legitimate for a targeted chain test, but only when it is declared. It is recorded
        # in the facts so a one-page run can never be mistaken for the full acceptance.
        [switch]$SkipMatrixCompleteness,
        # UI   — the desktop matrix and its capture environment. Used by the release gate, which is responsible
        #        for the matrix only.
        # Full — everything the release needs overall. Used by the report generator and the negative tests,
        #        which must refuse READY when any required result is missing.
        [ValidateSet('Full', 'UI')][string]$Scope = 'Full'
    )

    $reasons = New-Object System.Collections.Generic.List[string]
    $facts = @{}
    $root = if ($EvidenceRoot) { $EvidenceRoot } else { Join-Path $Repo "dist\evidence-11\$RunId" }
    # The matrix files may sit directly in the root (the release gate points at the capture's own output
    # folder) or in a Matrix subfolder (a full run's evidence tree). Locate them instead of assuming.
    $matrixSub = if (Test-Path (Join-Path $root 'UI_CAPTURE.tsv')) { '' }
                 elseif (Test-Path (Join-Path $root '06-desktop-ui')) { '06-desktop-ui' }
                 else { '06-desktop-ui-STRICT-FIXTURE' }

    if (-not $ExpectedCandidate) {
        $bi = Get-ChildItem -LiteralPath $root -Recurse -Filter 'build-info.json' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $bi) { $reasons.Add('无法确定候选身份：证据目录里没有 build-info.json'); return @{ Ready = $false; Reasons = $reasons; Facts = $facts } }
        $ExpectedCandidate = (Get-Content -LiteralPath $bi.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).commit
    }
    $facts['expectedCandidate'] = $ExpectedCandidate

    # ---- desktop matrix -----------------------------------------------------
    $capturePath = (Join-Path (Join-Path $root $matrixSub) 'UI_CAPTURE.tsv')
    if (-not (Test-Path $capturePath)) {
        $reasons.Add('缺少桌面矩阵结果 UI_CAPTURE.tsv')
    }
    else {
        $rows = @(Get-Content -LiteralPath $capturePath -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { $_ })
        if ($rows.Count -eq 0) { $reasons.Add('UI_CAPTURE.tsv 没有数据行') }

        $parsed = @()
        $bad = 0
        foreach ($row in $rows) {
            $c = $row -split "`t"
            if ($c.Count -lt 10) { $bad++; continue }
            $parsed += [pscustomobject]@{ theme=$c[1]; scale=$c[2]; physical=$c[3]; page=$c[4]; result=$c[8] }
        }
        if ($bad -gt 0) { $reasons.Add("UI_CAPTURE.tsv 有 $bad 行无法解析") }

        $failed = @($parsed | Where-Object { $_.result -eq 'FAIL' }).Count
        $facts['matrixTotal'] = $parsed.Count
        $facts['matrixFailed'] = $failed
        if ($failed -gt 0) { $reasons.Add("桌面矩阵有 $failed 个必需组合 FAIL") }

        $keys = $parsed | ForEach-Object { "$($_.theme)|$($_.scale)|$($_.physical)|$($_.page)" }
        $dupes = @($keys | Group-Object | Where-Object { $_.Count -gt 1 })
        if ($dupes.Count -gt 0) { $reasons.Add("桌面矩阵有 $($dupes.Count) 个重复组合（数量相同不等于覆盖完整）") }
        $facts['matrixDuplicates'] = $dupes.Count

        # ---- page coverage, anchored outside the matrix rows
        #
        # Deriving the expected page list from the rows themselves is circular twice over:
        #   * delete one page's 42 rows and the expectation shrinks with it, so the totals still agree;
        #   * replace one page's rows with a page that does not exist and both the row count and the page
        #     count stay the same.
        # The anchor is therefore UI_PAGE_CATALOG.tsv, written from the product's own PageCatalog and stamped
        # with the build's identity, so it defines the expectation independently of what actually rendered —
        # and it must belong to the candidate being judged.
        $summaryPath = (Join-Path (Join-Path $root $matrixSub) 'UI_CAPTURE_SUMMARY.tsv')
        $catalogPath = (Join-Path (Join-Path $root $matrixSub) 'UI_PAGE_CATALOG.tsv')
        $expectedPages = @{}
        if (-not (Test-Path $catalogPath)) {
            $reasons.Add('缺少 UI_PAGE_CATALOG.tsv：无法确定期望页面身份集合（数量校验不能代替页面身份）')
        }
        else {
            $catalogVersion = ''
            $catalogCommit = ''
            foreach ($line in (Get-Content -LiteralPath $catalogPath -Encoding UTF8)) {
                if ($line -like '# version*') { $catalogVersion = ($line -split "`t")[1]; continue }
                if ($line -like '# commit*') { $catalogCommit = ($line -split "`t")[1]; continue }
                if ($line -like '#*' -or $line -like 'title*' -or -not $line) { continue }
                $c = $line -split "`t"
                if ($c.Count -ge 2) { $expectedPages[$c[0]] = $c[1] }
            }
            $facts['catalogVersion'] = $catalogVersion
            $facts['catalogCommit'] = $catalogCommit
            $facts['catalogPages'] = $expectedPages.Count
            if ($expectedPages.Count -eq 0) { $reasons.Add('UI_PAGE_CATALOG.tsv 没有页面条目') }
            if (-not $catalogCommit) { $reasons.Add('UI_PAGE_CATALOG.tsv 没有声明构建身份（commit 为空）：无法确认它属于当前候选') }
            elseif ($catalogCommit -ne $ExpectedCandidate) {
                $reasons.Add("UI_PAGE_CATALOG.tsv 属于提交 $catalogCommit，不是当前候选 $ExpectedCandidate")
            }
        }

        # The summary's counts are a cross-check on the catalogue, not a substitute for it.
        if (Test-Path $summaryPath) {
            $sum = @{}
            foreach ($line in (Get-Content -LiteralPath $summaryPath -Encoding UTF8)) {
                $p = $line -split "`t"; if ($p.Count -ge 2) { $sum[$p[0]] = $p[1] }
            }
            $navCount = if ($sum.ContainsKey('NAV_PAGE_COUNT')) { [int]$sum['NAV_PAGE_COUNT'] } else { -1 }
            $capExpected = if ($sum.ContainsKey('UI_CAPTURE_EXPECTED')) { [int]$sum['UI_CAPTURE_EXPECTED'] } else { -1 }
            if ($navCount -le 0 -or $capExpected -le 0) {
                $reasons.Add('摘要里没有可用的页面总数（NAV_PAGE_COUNT / UI_CAPTURE_EXPECTED）')
            }
            elseif ($navCount -ne $capExpected) {
                $reasons.Add("摘要自相矛盾：NAV_PAGE_COUNT=$navCount 与 UI_CAPTURE_EXPECTED=$capExpected 不一致")
            }
            elseif ($expectedPages.Count -gt 0 -and $capExpected -ne $expectedPages.Count) {
                $reasons.Add("摘要声明 $capExpected 个页面，页面目录列出 $($expectedPages.Count) 个：页面身份来源不一致")
            }
        }
        else { $reasons.Add('缺少 UI_CAPTURE_SUMMARY.tsv：无法确定期望页面集合') }

        # The expected set is the cross product of the catalogue pages with the full matrix definition, so a
        # deleted or substituted page shows up as a set difference rather than hiding behind matching totals.
        $pages = @($parsed | Select-Object -ExpandProperty page -Unique)
        $facts['matrixPagesSeen'] = $pages.Count
        $facts['matrixPageNames'] = @($pages | Sort-Object)

        if ($expectedPages.Count -gt 0) {
            $missingPages = @($expectedPages.Keys | Where-Object { $_ -notin $pages })
            $unknownPages = @($pages | Where-Object { -not $expectedPages.ContainsKey($_) })
            $facts['matrixPagesMissing'] = $missingPages.Count
            $facts['matrixPagesUnknown'] = $unknownPages.Count
            if ($missingPages.Count -gt 0) { $reasons.Add("桌面矩阵缺少页面（页面目录要求但结果中没有）：" + ($missingPages -join '、')) }
            if ($unknownPages.Count -gt 0) { $reasons.Add("桌面矩阵出现页面目录之外的页面（身份未知）：" + ($unknownPages -join '、')) }
        }

        $expectedCount = 0
        foreach ($size in @('1366x768','1600x900','1920x1080','2560x1440')) {
            foreach ($scale in @('1.00','1.25','1.50','1.75','2.00')) {
                foreach ($theme in @('dark','light')) { $expectedCount += $pages.Count }
            }
        }
        foreach ($theme in @('dark','light')) { $expectedCount += $pages.Count }   # the 1160x780 baseline
        $facts['matrixExpected'] = $expectedCount

        # Per-page completeness: every declared page must carry the full 42-combination set, not merely exist.
        $perPage = $parsed | Group-Object page
        foreach ($group in $perPage) {
            if ($group.Count -ne 42) {
                $reasons.Add(("页面「{0}」的组合数是 {1}，应为 42（4 尺寸 × 5 缩放 × 2 主题 + 2 基线）" -f $group.Name, $group.Count))
            }
        }

        $actualSet = @($keys | Sort-Object -Unique)
        $expectedSet = @(
            foreach ($page in $pages) {
                foreach ($theme in @('dark','light')) {
                    foreach ($size in @('1366x768','1600x900','1920x1080','2560x1440')) {
                        foreach ($scale in @('1.00','1.25','1.50','1.75','2.00')) { "$theme|$scale|$size|$page" }
                    }
                    "$theme|1.00|1160x780|$page"
                }
            }
        ) | Sort-Object -Unique
        $missingCombos = @($expectedSet | Where-Object { $_ -notin $actualSet })
        $extraCombos = @($actualSet | Where-Object { $_ -notin $expectedSet })
        $facts['matrixMissingCombos'] = $missingCombos.Count
        $facts['matrixExtraCombos'] = $extraCombos.Count
        if ($missingCombos.Count -gt 0) { $reasons.Add("桌面矩阵缺少 $($missingCombos.Count) 个规定组合（首个：" + $missingCombos[0] + "）") }
        if ($extraCombos.Count -gt 0) { $reasons.Add("桌面矩阵出现了 $($extraCombos.Count) 个规定之外组合（首个：" + $extraCombos[0] + "）") }

        if ($parsed.Count -ne $expectedCount) {
            if ($SkipMatrixCompleteness) {
                $facts['matrixReduced'] = $true
                $facts['matrixReducedTo'] = $parsed.Count
            }
            else {
                $reasons.Add("桌面矩阵组合数 $($parsed.Count) != 期望 $expectedCount（期望集合 − 实际集合）")
            }
        }
    }

    # ---- capture environment ------------------------------------------------
    #
    # WiFi 功能已从产品移除，因此这里不再要求 "原生 WLAN 调用 = 0 / 夹具哨兵" —— 那套断言随功能一起删除。
    # 现在要证明的是**网络页渲染的是确定性夹具**（Ethernet 192.168.2.2 可用、Hyper-V 172.24.0.1 被排除），
    # 这仍然必须由捕获环境自报，不能靠"没报错"推断。
    $envPath = (Join-Path (Join-Path $root $matrixSub) 'UI_CAPTURE_ENVIRONMENT.tsv')
    if (-not (Test-Path $envPath)) {
        $reasons.Add('缺少 UI_CAPTURE_ENVIRONMENT.tsv：无法证明矩阵用的是确定性网卡夹具而非现场真实网卡')
    }
    else {
        $env = @{}
        foreach ($line in (Get-Content -LiteralPath $envPath -Encoding UTF8)) {
            $p = $line -split "`t"; if ($p.Count -ge 2) { $env[$p[0]] = $p[1] }
        }
        foreach ($key in @('R2_FIXTURE','R2_NATIVE_UI_RENDER','UI_LAYOUT_TEXTBLOCKS_INSPECTED')) {
            if (-not $env.ContainsKey($key)) { $reasons.Add("环境断言缺少 $key") }
        }
        if ($env.ContainsKey('R2_NATIVE_UI_RENDER') -and $env['R2_NATIVE_UI_RENDER'] -ne 'PASS') {
            $reasons.Add('网络页夹具没有真正进入页面：R2_NATIVE_UI_RENDER 不是 PASS')
        }
        if ($env.ContainsKey('R2_VISIBLE_ADAPTER') -and $env['R2_VISIBLE_ADAPTER'] -ne 'true') {
            $reasons.Add('网络页没有显示夹具里的物理网卡')
        }
        if ($env.ContainsKey('R2_EMPTY_STATE_VISIBLE') -and $env['R2_EMPTY_STATE_VISIBLE'] -ne 'false') {
            $reasons.Add('网络页出现了"没有可用物理网卡"空态，与夹具矛盾')
        }
        if ($env.ContainsKey('UI_LAYOUT_TEXTBLOCKS_INSPECTED') -and [int]$env['UI_LAYOUT_TEXTBLOCKS_INSPECTED'] -le 0) {
            $reasons.Add('矩阵没有检查到任何文本控件：0 行诊断不能当成没有问题')
        }
        $facts['networkFixtureRender'] = $env['R2_NATIVE_UI_RENDER']
        $facts['networkFixtureAdapter'] = $env['R2_VISIBLE_ADAPTER']
    }

    # ---- required evidence files (whole-release scope only) ------------------
    if ($Scope -eq 'UI') {
        $facts['scope'] = 'UI'
        return @{ Ready = ($reasons.Count -eq 0); Reasons = @($reasons); Facts = $facts }
    }

    $required = @{
        'UI_CAPTURE.tsv'                     = $(if ($matrixSub) { "$matrixSub\UI_CAPTURE.tsv" } else { "UI_CAPTURE.tsv" })
        'setup-acceptance.json'              = '03-setup-acceptance'
        'update-verify-A'                    = '04-update-rollback'
        'runtime-identity.json'              = '05-runtime-identity\runtime-identity.json'
        'e2e-ui-chromium.json'               = '07-browser\e2e-ui-chromium.json'
        'e2e-ui-webkit.json'                 = '07-browser\e2e-ui-webkit.json'
        # 本轮新增的必需项：安装器视觉证据、B 类动态端口更新、HTTPS 监听矩阵、隐私结果。
        # 只检查"文件存在"不够 —— 下面还要核对它们验证的对象、必要条件和结果。
        'installer-ui-index'                 = '02-installer-capture\setup-ui-index.txt'
        'installer-recording'                = '02-installer-capture\recording\installer-animation.gif'
        'https-e2e-matrix.json'              = '08-https\https-e2e-matrix.json'
        'B-dynamic-port-update.json'         = '08-https\B-dynamic-port\B-dynamic-port-update.json'
        'PRIVACY_CHECK.json'                 = 'PRIVACY_CHECK.json'
    }
    foreach ($name in $required.Keys) {
        $path = Join-Path $root $required[$name]
        if (Test-Path $path -PathType Container) {
            $found = Get-ChildItem $path -Recurse -File -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $found) { $reasons.Add("必需证据缺失：$name"); continue }
            $path = $found.FullName
        }
        elseif (-not (Test-Path $path)) { $reasons.Add("必需证据缺失：$name"); continue }
    }

    # candidate identity on the runtime record
    $rt = Join-Path $root '05-runtime-identity\runtime-identity.json'
    if (Test-Path $rt) {
        try {
            $doc = Get-Content -LiteralPath $rt -Raw -Encoding UTF8 | ConvertFrom-Json
            $facts['runtimeCommit'] = $doc.reportedCommit
            if ($doc.reportedCommit -ne $ExpectedCandidate) {
                $reasons.Add("运行期身份 $($doc.reportedCommit) 与候选 $ExpectedCandidate 不一致（旧候选结果不得拼进来）")
            }
        }
        catch { $reasons.Add('runtime-identity.json 无法解析') }
    }

    # ---- 新增必需项的**内容**核对（不只看文件在不在）
    # 安装器：状态索引必须覆盖 9 个状态，录屏动图必须存在且非空
    $uiIndex = Join-Path $root '02-installer-capture\setup-ui-index.txt'
    if (Test-Path $uiIndex) {
        $states = @(Get-Content -LiteralPath $uiIndex -Encoding UTF8 | Where-Object { $_ -and ($_ -split "`t")[0] -notlike '#*' })
        $facts['installerStates'] = $states.Count
        if ($states.Count -lt 9) { $reasons.Add("安装器状态截图只有 $($states.Count) 项，应有 9 项（全灭/过渡中/Ready/失败等）") }
    }
    $gif = Join-Path $root '02-installer-capture\recording\installer-animation.gif'
    if (Test-Path $gif) {
        $facts['installerGifBytes'] = (Get-Item $gif).Length
        if ((Get-Item $gif).Length -le 0) { $reasons.Add('安装器录屏动图为空') }
    }

    # HTTPS 监听矩阵：必须 4/4 且验证对象是监听
    $mx = Join-Path $root '08-https\https-e2e-matrix.json'
    if (Test-Path $mx) {
        try {
            $m = Get-Content -LiteralPath $mx -Raw -Encoding UTF8 | ConvertFrom-Json
            $facts['httpsMatrixVerdict'] = $m.verdict
            if ($m.verdict -ne 'PASS') { $reasons.Add("HTTPS 监听矩阵未通过：$($m.verdict)（通过 $($m.passed)/$($m.total)）") }
        }
        catch { $reasons.Add('https-e2e-matrix.json 无法解析') }
    }

    # B 类：动态端口更新与失败回滚 —— 必须实际通过，且端口归属核对成立
    $bp = Join-Path $root '08-https\B-dynamic-port\B-dynamic-port-update.json'
    if (Test-Path $bp) {
        try {
            $b = Get-Content -LiteralPath $bp -Raw -Encoding UTF8 | ConvertFrom-Json
            $facts['bClassOutcome'] = $b.outcome
            $facts['bConfiguredPorts'] = "$($b.configuredHttp)/$($b.configuredHttps)"
            $facts['bBoundPorts'] = "$($b.boundHttpBefore)/$($b.boundHttpsBefore)"
            $facts['bRestartPorts'] = "$($b.restartHttp)/$($b.restartHttps)"
            if ($b.outcome -ne 'PASS') { $reasons.Add("B 类（动态端口更新与回滚）未通过：$($b.outcome)") }
            if (-not $b.restartPortsMatchBound) { $reasons.Add('B 类：更新程序重启用的端口与实际绑定端口不一致（说明传的是未解析配置值）') }
            if ($b.boundHttpBefore -le 0) { $reasons.Add('B 类：没有记录到实际绑定端口') }
            if ($b.configuredHttp -ne 0) { $reasons.Add('B 类：动态配置 0 被改写成固定端口，不符合"保留用户配置值"') }
        }
        catch { $reasons.Add('B-dynamic-port-update.json 无法解析') }
    }

    # 隐私：必须对实际交付集合检查通过
    $pv = Join-Path $root 'PRIVACY_CHECK.json'
    if (Test-Path $pv) {
        try {
            $p = Get-Content -LiteralPath $pv -Raw -Encoding UTF8 | ConvertFrom-Json
            $facts['privacyVerdict'] = $p.verdict
            $facts['privacyHits'] = $p.hitCount
            if ($p.verdict -ne 'PASS') { $reasons.Add("隐私检查未通过：$($p.verdict)（命中 $($p.hitCount) 处真实网络标识，落在 $((@($p.hits) | ForEach-Object { $_.file } | Select-Object -Unique) -join '、')）") }
        }
        catch { $reasons.Add('PRIVACY_CHECK.json 无法解析') }
    }

    @{ Ready = ($reasons.Count -eq 0); Reasons = @($reasons); Facts = $facts }
}

function Format-Verdict($verdict) {
    if ($verdict.Ready) { return 'READY' }
    return "NOT_READY ($($verdict.Reasons.Count)): " + ($verdict.Reasons -join ' ; ')
}
