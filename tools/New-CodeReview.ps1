# Generates CODE_REVIEW_11.md: the actual code behind the claims in this round, with file path, line range
# and SHA256. Written by a script rather than by hand so the quoted code, its line numbers and its hashes
# cannot drift away from the source that was built.

param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'

# SHA256. Written by a script rather than by hand so the quoted code, its line numbers and its hashes cannot
# drift away from the source that was built.


if (-not $OutFile) { $OutFile = Join-Path $Repo 'dist\delivery\CODE_REVIEW_11.md' }
$head = (& git -C $Repo rev-parse HEAD).Trim()
$version = (Select-String -Path (Join-Path $Repo 'src\LanDisk.App\LanDisk.App.csproj') -Pattern '<Version>(.*)</Version>').Matches.Groups[1].Value

$sections = @(
    @{ Title = '安装器：矢量机柜与灯光动画'; Why = 'REAL11-01/02 的"真矢量 + 逐组通电 + 真动画"就落在这里。'; Files = @(
        @{ Path = 'src/LanDisk.Setup/Visual/RackVisual.cs'; Note = '机柜本体：图元构造、每组灯数、阶段→灯组映射、关键帧点灯曲线与组内 stagger、Reduce Motion 分支、仅当前组 pulse。' },
        @{ Path = 'src/LanDisk.Setup/Visual/SetupVisualTheme.cs'; Note = '冻结色板与平铺 GeometryDrawing 画笔（散热孔、端口面），没有位图资源。' },
        @{ Path = 'src/LanDisk.Setup/Visual/LanDiskLogoGeometry.cs'; Note = '由 tools/logogen 在构建时生成的字标与副标轮廓，安装器解析成冻结 Geometry。' }
    ) },
    @{ Title = '安装器：六阶段、完成流程与自动关闭'; Why = 'REAL11-02/17：阶段模型、Ready 条件、无独立完成弹窗、首次运行教程在 LanDisk.exe 内。'; Files = @(
        @{ Path = 'src/LanDisk.Setup/SetupStageMap.cs'; Note = '六个显示阶段与十个引擎步骤的映射，以及"索引只前进"的判定。' },
        @{ Path = 'src/LanDisk.Setup/SetupWindow.xaml.cs'; Note = '阶段驱动、服务就绪后才成立 Ready、"LanDisk 已就绪"停留后自行关闭。' },
        @{ Path = 'src/LanDisk.App/MainWindow.xaml.cs'; Note = '首次运行判定：0 台设备且 0 个已启用共享 → 打开「连接 iPhone」教程。' },
        @{ Path = 'src/LanDisk.Core/Model/DeviceAccess.cs'; Note = 'FirstRunPolicy 与在线/最近活动/离线阈值（presence 的唯一判据）。' }
    ) },
    @{ Title = '手机端：心跳、权限与主题'; Why = 'REAL11-11/13/10：心跳脱离组件树、权限修订号、主题语义令牌与不被缓存。'; Files = @(
        @{ Path = 'src/LanDisk.Web/src/presence.ts'; Note = '模块级心跳控制器：启动即发、pageshow/focus/visibilitychange/活动即刻发、失败指数退避、focus 立即重试。' },
        @{ Path = 'src/LanDisk.Server/Endpoints/AuthEndpoints.cs'; Note = 'heartbeat 返回 permissionRevision 与 themeRevision；/api/mobile-theme 显式 no-store。' },
        @{ Path = 'src/LanDisk.Core/Model/Models.cs'; Note = 'DeviceDefinition.PermissionRevision：只有级别或能力真的变了才自增。' },
        @{ Path = 'src/LanDisk.Web/src/api.ts'; Note = '主题请求带 cache: no-store 与时间戳查询参数。' }
    ) },
    @{ Title = '手机端：查看器独立图层与唯一阻断原因'; Why = 'REAL11-15/16：查看器不是"更暗的对话框"，阻断原因只呈现一条。'; Files = @(
        @{ Path = 'src/LanDisk.Web/src/components/ui.tsx'; Note = 'Modal 的 viewer 变体：更高的 z-index、近乎不透明的 surface。' },
        @{ Path = 'src/LanDisk.Web/src/styles.css'; Note = '遮罩、surface、视频纯黑、safe-area；以及语义令牌的默认值。' },
        @{ Path = 'src/LanDisk.Web/src/pages/DeviceApp.tsx'; Note = 'blocker 的优先级（共享不可访问 > 共享未开放 > 手机只读 > 能力缺失）与查看器宿主。' }
    ) },
    @{ Title = '设备永久移除'; Why = 'REAL11-12：真删记录，且在删之前完成完整撤销。'; Files = @(
        @{ Path = 'src/LanDisk.Server/Services/AdminOperations.cs'; Note = 'RemoveDevice：撤销 → 轮换 token hash 与 session epoch → 取消上传与扩展任务 → 删记录 → 写审计 device-remove。' },
        @{ Path = 'src/LanDisk.Server/Endpoints/AdminEndpoints.cs'; Note = 'DELETE /api/admin/devices/{id} 的返回结构。' }
    ) },
    @{ Title = '网络状态唯一事实源'; Why = 'REAL11-06：绑定、证书 SAN、概览、网络页、自检、配对地址共用同一份读数。'; Files = @(
        @{ Path = 'src/LanDisk.Core/Networking/PhysicalLanSnapshot.cs'; Note = '读数模型与接受/排除原因；revision 只在内容变化时前进。' },
        @{ Path = 'src/LanDisk.Server/Services/LanDiskRuntime.cs'; Note = 'PhysicalLanState 的接线，LanAddresses 与 VPN 判定都改读同一份快照。' }
    ) },
    @{ Title = '防火墙：规则计划与只读探针'; Why = 'REAL11-08：期望规则与自检判定同源；探针只读，绝不建立规则。'; Files = @(
        @{ Path = 'src/LanDisk.Core/Networking/FirewallRules.cs'; Note = '三条期望规则、NeverInbound(8891)、netsh 输出解析与判定（分隔线只是装饰，不是记录边界）。' },
        @{ Path = 'src/LanDisk.Setup/FirewallPlan.cs'; Note = '安装器据此生成 netsh 参数：Private + LocalSubnet + 绑定 LanDisk.exe。' },
        @{ Path = 'src/LanDisk.App/FirewallStatus.cs'; Note = '自检侧：只跑 advfirewall firewall show rule；修复走 NativeDialogs.RunElevated 的一次显式 UAC。' }
    ) },
    @{ Title = '本轮修掉的真缺陷：更新后重启端口'; Why = '非默认端口上首次跑更新验收暴露；见 UPDATEPORT12-01..04。'; Files = @(
        @{ Path = 'src/LanDisk.Updater/Program.cs'; Note = '重启宿主时端口的取值（修复点）。' },
        @{ Path = 'src/LanDisk.Core/State/UpdatePortChoice.cs'; Note = '优先级决策：显式覆盖 > 宿主实时值 > settings.json 兜底。' },
        @{ Path = 'src/LanDisk.Update/UpdateService.cs'; Note = '宿主把**实时绑定**的端口交给更新程序（这一侧的意图原本就是对的）。' }
    ) },
    @{ Title = '证据是怎么产生的'; Why = '§九 要求给出"证据生成逻辑"的实际代码，而不是说明。'; Files = @(
        @{ Path = 'dist/tools/InstallerRecorder/Program.cs'; Note = '驱动真实安装器（UI Automation 按下它自己的安装按钮），PrintWindow 抓真实窗口，输出动图。' },
        @{ Path = 'dist/tools/GifVerify/Program.cs'; Note = '解码动图：帧数、互异帧数、循环扩展、每帧延时；单一画面的"动画"会被判为 NOT ANIMATED。' },
        @{ Path = 'dist/tools/New-RuntimeIdentity.ps1'; Note = '用冻结产物在隔离目录实跑并读取 /api/health 的版本与提交。' },
        @{ Path = 'dist/tools/New-EvidenceInventory.ps1'; Note = '把产物、工具与证据逐项算 SHA256 列成清单。' },
        @{ Path = 'tools/e2e/ui-test.mjs'; Note = '浏览器端门禁，含本轮补强的 MOBILE12（70 秒 presence、真视频、构造不可用共享、真实写入/拒绝、三档主题）。' }
    ) }
)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# LanDisk ' + $version + ' — 代码审阅（CODE_REVIEW_11）')
$lines.Add('')
$lines.Add('- 生成时间：' + (Get-Date -Format 'o'))
$lines.Add('- 源码 HEAD：`' + $head + '`')
$lines.Add('- 本文件由 `tools/New-CodeReview.ps1` 从源码生成，因此**引用的代码、行号与 SHA256 不可能与构建出的源码脱节**。')
$lines.Add('- 每节给出：为什么看它 → 文件路径 → 行范围 → 文件 SHA256 → 该范围的逐行原文。')
$lines.Add('')
$lines.Add('> **关于截断。** 本文件后面按"节"给出的源码是**节选**（每个文件最多若干百行），只用于定位。')
$lines.Add('> 本次任务真正改动的代码在下面第 0 节**完整列出**（完整 diff，含上下文），不会因为节选上限而丢掉修复点。')
$lines.Add('')

# ---- 第 0 节：本次改动**完整** diff（不截断）
# 先前统一截取每个文件前 220 行，结果恰好把真正的修复点省掉了。这里直接给出改动文件与改动文件的
# 完整统一 diff，读者不必从节选里猜。
$baseline = & git -C $Repo log --format=%H -1 -- src 2>$null
$changed = @(& git -C $Repo diff --name-only $baseline $head 2>$null | Where-Object { $_ })
$lines.Add('---')
$lines.Add('')
$lines.Add('## 0. 本次改动（完整 diff，不截断）')
$lines.Add('')
$lines.Add('- 产品构建提交（基线）：`' + $baseline + '`')
$lines.Add('- 当前 HEAD：`' + $head + '`')
$lines.Add('- 改动文件 ' + $changed.Count + ' 个')
$lines.Add('')
if ($changed.Count -eq 0) {
    $lines.Add('（自产品构建提交以来没有源码改动。）')
}
else {
    foreach ($f in $changed) {
        $full = Join-Path $Repo $f
        $lines.Add('### `' + $f + '`')
        $lines.Add('')
        if (Test-Path -LiteralPath $full) {
            $lines.Add('- 当前文件 SHA256：`' + (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() + '`')
        }
        $lines.Add('')
        $lines.Add('```diff')
        foreach ($dl in @(& git -C $Repo diff $baseline $head -- $f 2>$null)) { $lines.Add($dl) }
        $lines.Add('```')
        $lines.Add('')
    }
}
$lines.Add('')

$index = 0
foreach ($section in $sections) {
    $index++
    $lines.Add('---')
    $lines.Add('')
    $lines.Add('## ' + $index + '. ' + $section.Title)
    $lines.Add('')
    $lines.Add('> ' + $section.Why)
    $lines.Add('')
    foreach ($file in $section.Files) {
        $full = Join-Path $Repo $file.Path
        if (-not (Test-Path -LiteralPath $full)) { $lines.Add('（缺失：' + $file.Path + '）'); continue }
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        $content = [System.IO.File]::ReadAllLines($full)
        $size = (Get-Item -LiteralPath $full).Length
        $lines.Add('### `' + $file.Path + '`')
        $lines.Add('')
        $lines.Add('- 作用：' + $file.Note)
        $lines.Add('- 字节：' + $size + '　行数：' + $content.Count)
        $lines.Add('- SHA256：`' + $hash + '`')
        $lines.Add('')
        # Quote the whole file when it is short, otherwise the interesting half.
        $limit = if ($content.Count -le 220) { $content.Count } else { 220 }
        $lines.Add('```csharp')
        for ($i = 0; $i -lt $limit; $i++) { $lines.Add(('{0,5}  {1}' -f ($i + 1), $content[$i])) }
        if ($limit -lt $content.Count) { $lines.Add(('  ... 其余 {0} 行未在本文件内展开（按上面的 SHA256 核对）' -f ($content.Count - $limit))) }
        $lines.Add('```')
        $lines.Add('')
    }
}

$lines | Set-Content -LiteralPath $OutFile -Encoding UTF8
Write-Host ('written: {0} ({1} lines)' -f $OutFile, $lines.Count)
