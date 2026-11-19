namespace LanDisk.Setup;

public sealed record SetupOutcome(bool Ok, string Summary, List<string> Details)
{
    public static SetupOutcome Fail(string summary, IEnumerable<string> details)
        => new(false, summary, [.. details]);
}

/// <summary>The order of operations an install performs, separated from the window that shows it.</summary>
public enum SetupStep
{
    Inspect,

    /// <summary>The embedded payload has been verified (version, manifest, per-file hashes).</summary>
    VerifyPayload,

    StopRunning,

    /// <summary>The existing user data directory was found and is being left in place.</summary>
    PreserveUserData,

    InstallFiles,
    Shortcuts,
    Register,
    Firewall,
    Launch,
    Ready
}

/// <summary>
/// The install and uninstall sequences.
///
/// <para>Everything that decides something is here rather than in the form, so the same sequence can be
/// driven by the window, by <c>--silent</c>, and by the acceptance script — and so a test can run an
/// install into a scratch directory without a user interface at all.</para>
/// </summary>
public sealed class SetupEngine
{
    private readonly SetupOptions _options;
    private readonly Action<string>? _log;
    private readonly Action<SetupStep>? _step;

    public SetupEngine(SetupOptions options, Action<string>? log = null, Action<SetupStep>? step = null)
    {
        _options = options;
        _log = log;
        _step = step;
    }

    /// <summary>What the wizard should say before anything happens, including an existing installation.</summary>
    public (bool ExistingInstall, string Version, string? Commit) Inspect()
    {
        string buildInfo = SetupLayout.BuildInfoPathFor(_options.InstallDir);
        if (!File.Exists(buildInfo)) return (false, string.Empty, null);

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(buildInfo));
            string version = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            string commit = document.RootElement.TryGetProperty("commit", out var c) ? c.GetString() ?? "" : "";
            return (true, version, commit);
        }
        catch
        {
            return (true, string.Empty, null);
        }
    }

    public async Task<SetupOutcome> InstallAsync()
    {
        var details = new List<string>();

        _step?.Invoke(SetupStep.Inspect);
        using PayloadPackage? payload = PayloadPackage.FromEmbedded();
        if (payload is null)
        {
            return SetupOutcome.Fail(
                "这个安装程序没有内置安装载荷，无法安装。",
                ["该可执行文件是在没有 release 载荷的情况下构建的（开发构建），请使用正式发布的 LanDisk-Setup.exe。"]);
        }

        _log?.Invoke($"安装载荷：LanDisk {payload.Info.Version}（提交 {PayloadPackage.Short(payload.Info.Commit)}，{payload.Info.FileCount} 个文件，{payload.Info.TotalBytes / (1024 * 1024)} MB）");
        if (!payload.Info.DigestVerified)
        {
            _log?.Invoke("提示：该安装程序没有记录载荷摘要（开发构建），跳过载荷整体校验；逐文件与清单校验仍然执行。");
        }

        // The payload in hand is the one that will be installed: this step is complete before anything on
        // disk is touched, which is exactly what the first node of the console reports.
        _step?.Invoke(SetupStep.VerifyPayload);

        (bool existing, string existingVersion, _) = Inspect();
        details.Add(existing
            ? $"检测到已有安装：{_options.InstallDir}（版本 {existingVersion}），本次执行修复/升级安装。"
            : $"将安装到：{_options.InstallDir}");
        details.Add($"用户数据目录保持不变：{_options.DataDir}");

        // 1. Stop anything running, using the product's own shutdown path first.
        _step?.Invoke(SetupStep.StopRunning);
        IReadOnlyList<System.Diagnostics.Process> remaining =
            await ProcessControl.StopAsync(_options.DataDir, _options.InstallDir, !_options.NoForceStop, _log).ConfigureAwait(false);
        if (remaining.Count > 0)
        {
            return SetupOutcome.Fail("LanDisk 仍在运行，安装已中止。",
                [.. details, "请手动退出 LanDisk（托盘图标 → 退出），然后重新运行安装程序。"]);
        }

        // 2. Put the program files in place: staged, verified, replace-with-backup.
        _step?.Invoke(SetupStep.InstallFiles);
        InstallResult install = payload.InstallTo(_options.InstallDir, Environment.ProcessPath, _log);
        details.Add(install.Message ?? string.Empty);
        if (!install.Ok)
        {
            details.AddRange(install.Failures);
            return SetupOutcome.Fail("安装程序文件失败，安装目录已恢复到安装前的状态。", details);
        }

        // 3. The program files are now in place and the data directory was never part of the payload. Report
        //    it *after* the files landed, so the console's nodes advance in the order the work actually
        //    happens — never "user data kept" before the program files were even staged (which is what made
        //    the node sequence go 0 → 2 → 1 and break the monotonic-invariant).
        _step?.Invoke(SetupStep.PreserveUserData);

        // 3. Shortcuts.
        _step?.Invoke(SetupStep.Shortcuts);
        if (_options.NoRegister)
        {
            details.Add("已跳过快捷方式与卸载注册（--no-register，仅用于自动化验收）。");
        }
        else
        {
            foreach (ShortcutPlan plan in ShellIntegration.ShortcutPlans(
                         _options.InstallDir, _options.DesktopShortcut, _options.StartMenuShortcut, _options.DataDir))
            {
                if (ShellIntegration.CreateShortcut(plan, _log)) details.Add("已创建快捷方式：" + plan.LinkPath);
            }

            // 4. Uninstall registration (per user, so no administrator is needed).
            _step?.Invoke(SetupStep.Register);
            ShellIntegration.RegisterUninstall(ShellIntegration.UninstallValues(
                _options.InstallDir, _options.DataDir, payload.Info.Version, payload.Info.TotalBytes));
            details.Add("已在“已安装的应用”中注册 LanDisk（仅当前用户）。");
        }

        // 5. Firewall: one UAC prompt, and only when the user asked for it.
        _step?.Invoke(SetupStep.Firewall);
        if (_options.NoFirewall)
        {
            details.Add("已跳过防火墙配置（--no-firewall）。");
        }
        else
        {
            string executable = Environment.ProcessPath ?? SetupLayout.SetupCopyPathFor(_options.InstallDir);
            int? exit = FirewallHelper.RunElevated(executable, add: true);
            details.Add(exit switch
            {
                0 => $"已添加最小防火墙规则（专用网络、仅本地子网）：TCP {SetupLayout.HttpsPort}、TCP {SetupLayout.BootstrapPort}、UDP 5353。管理端口 {SetupLayout.AdminPort} 未对外开放。",
                null => "未获得防火墙授权（UAC 被拒绝或不可用）。LanDisk 仍然可用，但同一局域网中的手机暂时无法连接；" +
                        "可以稍后以管理员身份运行本安装程序，或在 Windows 防火墙中手动放行。",
                _ => $"防火墙配置未成功（退出码 {exit}）。可以稍后重试；手机端访问需要该规则。"
            });
        }

        // 6. Start it and wait until it is genuinely serving.
        _step?.Invoke(SetupStep.Launch);
        if (_options.LaunchAfterInstall && !_options.NoLaunch)
        {
            if (ProcessControl.StartShell(_options.InstallDir, _options.DataDir, _log))
            {
                details.Add("已启动 LanDisk。");
                _step?.Invoke(SetupStep.Ready);
                bool ready = await ProcessControl.WaitForReadinessAsync(
                    SetupLayout.AdminPort, TimeSpan.FromSeconds(90), _log).ConfigureAwait(false);
                details.Add(ready
                    ? "LanDisk 已就绪，可以连接 iPhone。"
                    : "LanDisk 已启动但尚未在 90 秒内就绪；可稍后点击“连接 iPhone”重试。");
            }
            else
            {
                details.Add("启动 LanDisk 失败；可以从开始菜单手动启动。");
            }
        }

        return new SetupOutcome(true, $"LanDisk {payload.Info.Version} 已安装。", details);
    }

    /// <summary>
    /// Removes the installation. User data is kept unless the caller explicitly asked for it to go,
    /// and even then only LanDisk's own data directory is touched — never a folder the user shared.
    /// </summary>
    public async Task<SetupOutcome> UninstallAsync()
    {
        var details = new List<string>();

        _step?.Invoke(SetupStep.StopRunning);
        IReadOnlyList<System.Diagnostics.Process> remaining =
            await ProcessControl.StopAsync(_options.DataDir, _options.InstallDir, !_options.NoForceStop, _log).ConfigureAwait(false);
        if (remaining.Count > 0)
        {
            return SetupOutcome.Fail("LanDisk 仍在运行，卸载已中止。",
                [.. details, "请先退出 LanDisk（托盘图标 → 退出）。"]);
        }

        _step?.Invoke(SetupStep.Firewall);
        if (!_options.NoFirewall)
        {
            string executable = Environment.ProcessPath ?? SetupLayout.SetupCopyPathFor(_options.InstallDir);
            int? exit = FirewallHelper.RunElevated(executable, add: false);
            details.Add(exit switch
            {
                0 => "已删除 LanDisk 自己创建的防火墙规则（其它规则未改动）。",
                null => "未获得防火墙授权，LanDisk 创建的规则可能仍然存在。可在 Windows 防火墙中手动删除名称以 LanDisk 开头的规则。",
                _ => "删除防火墙规则未成功，可在 Windows 防火墙中手动删除。"
            });
        }

        _step?.Invoke(SetupStep.Register);
        if (_options.NoRegister)
        {
            details.Add("已跳过快捷方式与卸载注册的清理（--no-register，仅用于自动化验收）。");
        }
        else
        {
            ShellIntegration.UnregisterUninstall();
            ShellIntegration.RemoveAutostart();
            ShellIntegration.RemoveShortcuts(_options.InstallDir);
            details.Add("已删除快捷方式、开机启动项与“已安装的应用”条目。");
        }

        _step?.Invoke(SetupStep.InstallFiles);
        ShellIntegration.RemoveProgramFiles(_options.InstallDir, _log);
        details.Add("已删除程序文件：" + _options.InstallDir);

        if (_options.RemoveUserData)
        {
            bool removed = ShellIntegration.RemoveUserData(_options.DataDir, _log);
            details.Add(removed
                ? "已删除 LanDisk 的设置、配对与证书（" + _options.DataDir + "）。共享目录里的真实文件从未被触碰。"
                : "未能删除用户数据目录；可以手动删除 " + _options.DataDir + "。");
        }
        else
        {
            details.Add("已保留用户数据：" + _options.DataDir + "（设置、共享、配对与证书）。重新安装后会继续使用它们。");
        }

        return new SetupOutcome(true, "LanDisk 已卸载。", details);
    }
}
