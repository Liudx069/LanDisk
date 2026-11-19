using System.Text.RegularExpressions;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 08: three claims the delivery report makes about the native Windows shell and about the
/// network layer, turned into assertions instead of prose.
///
///   * <c>NORMAL_FLOW_OPENS_BROWSER = NO</c> — today supported on one side by the candidate identity
///     checks (CAND-16/17/18 read the shipped PE/CLI metadata and prove the shell is native WPF with
///     no WebView), and on the other side by this file: the shell source has exactly two shell-execute
///     call sites, only one of them can be a URL, and it is the labelled recovery entry behind an
///     explicit confirmation.
///   * <c>VPN_ROUTING_UNTOUCHED = YES</c> — no product source writes a route, a VPN connection or an
///     adapter/DNS configuration, and the only <c>netsh</c> invocations in the whole product are the
///     Setup firewall helper's two verbs (<c>advfirewall firewall add rule</c> /
///     <c>... delete rule</c>).
///
/// These are source-level gates: they cannot prove at runtime that no process opened a browser, but
/// they can prove that no code path exists which could. That is the part a reviewer can check.
/// </summary>
public sealed class ShellInvariantTests
{
    private static readonly string[] ForbiddenNetworkWrites =
    [
        "route add",
        "route delete",
        "New-NetRoute",
        "Remove-NetRoute",
        "Set-NetIPInterface",
        "Set-NetAdapter",
        "Set-DnsClientServerAddress",
        "Set-NetConnectionProfile",
        "Set-VpnConnection",
        "Add-VpnConnection",
        "rasdial",
        "EnableStatic",
        "ManagementClass",
        "ManagementObjectSearcher"
    ];

    private static IReadOnlyList<string> SourceFiles(string relativeDirectory)
    {
        string root = Path.Combine(TestPaths.RepoRoot, relativeDirectory);
        Assert.True(Directory.Exists(root), $"找不到源码目录：{root}");
        IReadOnlyList<string> files = [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

        // A scan that reads no files passes for the wrong reason, and every gate below depends on this.
        Assert.True(files.Count > 5, $"扫描 {relativeDirectory} 只找到 {files.Count} 个源文件，门禁会退化成空断言");
        return files;
    }

    /// <summary>Which forbidden patterns a piece of source contains. Split out so it can itself be tested.</summary>
    private static List<string> ScanForbidden(string text)
    {
        var hits = new List<string>();
        foreach (string pattern in ForbiddenNetworkWrites)
        {
            if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase)) hits.Add(pattern);
        }
        return hits;
    }

    private static List<(string File, int Line, string Text)> Sites(IEnumerable<string> files, string needle)
    {
        var found = new List<(string, int, string)>();
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(needle, StringComparison.Ordinal)) found.Add((file, i + 1, lines[i].Trim()));
            }
        }
        return found;
    }

    /// <summary>The code around a site, so an assertion can talk about what guards it.</summary>
    private static string WindowAround(string file, int line, int before, int after)
    {
        string[] lines = File.ReadAllLines(file);
        int start = Math.Max(0, line - 1 - before);
        int end = Math.Min(lines.Length - 1, line - 1 + after);
        return string.Join('\n', lines[start..(end + 1)]);
    }

    [Fact]
    public void The_shell_opens_a_browser_from_exactly_one_guarded_place()
    {
        IReadOnlyList<string> files = SourceFiles("src/LanDisk.App");

        // UseShellExecute = true is the only way managed code hands a string to the shell, which is how a URL
        // reaches the default browser.
        //
        // Round 10 tightened this from "two sites" to "one": opening a folder used to be done inline in two
        // places, and since this gate cannot read intent, the honest fix was to move folder opening into one
        // documented helper that refuses anything URL-shaped. What is left is exactly one shell-execute call
        // site in the whole shell, and it is the labelled recovery page.
        List<(string File, int Line, string Text)> sites = Sites(files, "UseShellExecute = true");
        // Whole-line comments are dropped: the helper's doc comment quotes the literal while explaining the
        // rule, and a gate that counts its own explanation is a gate that fails for the wrong reason.
        List<(string File, int Line, string Text)> codeSitesOnly =
            [.. sites.Where(s => !s.Text.TrimStart().StartsWith("///", StringComparison.Ordinal)
                                 && !s.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))];
        List<(string File, int Line, string Text)> helperSites =
            [.. codeSitesOnly.Where(s => string.Equals(Path.GetFileName(s.File), "NativeDialogs.cs", StringComparison.OrdinalIgnoreCase))];
        List<(string File, int Line, string Text)> codeSites =
            [.. codeSitesOnly.Where(s => !string.Equals(Path.GetFileName(s.File), "NativeDialogs.cs", StringComparison.OrdinalIgnoreCase))];

        Assert.True(codeSites.Count == 1,
            "外壳里应当只有一个 shell-execute 调用点（打开 Web 管理恢复页），实际：" +
            string.Join("; ", codeSites.Select(s => $"{Path.GetFileName(s.File)}:{s.Line} {s.Text.Trim()}")));

        // Folder opening must go through the one helper, and that helper must refuse URL-shaped input — which
        // is what keeps "open the log folder" from becoming a second, unlabelled way to a browser.
        string dialogs = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App/NativeDialogs.cs"));
        Assert.Contains("public static void OpenFolder(", dialogs, StringComparison.Ordinal);
        Assert.Contains("Contains(\"://\"", dialogs, StringComparison.Ordinal);
        // 1.7.2 added a second *guarded* helper to this same file: running the installer once with `runas` so
        // the user can repair the firewall rules from the self-check. Elevation cannot be expressed without a
        // shell-execute call, and putting it in the caller would mean the "one place" invariant covered
        // nothing. Both helpers are therefore asserted here instead: each refuses URL-shaped input, one opens
        // a folder and the other requires a real file path and the runas verb.
        Assert.True(helperSites.Count == 2,
            "NativeDialogs 里应当有两个受控 shell-execute 调用点（打开目录、单次提权），实际：" +
            string.Join("; ", helperSites.Select(s => $"line {s.Line} {s.Text.Trim()}")));

        (string File, int Line, string Text) folderSite = helperSites.First(s =>
            WindowAround(s.File, s.Line, 12, 2).Contains("OpenFolder", StringComparison.Ordinal));
        (string File, int Line, string Text) elevationSite = helperSites.First(s =>
            WindowAround(s.File, s.Line, 30, 2).Contains("RunElevated", StringComparison.Ordinal));

        Assert.Contains("Contains(\"://\"", WindowAround(folderSite.File, folderSite.Line, 12, 2), StringComparison.Ordinal);
        Assert.Contains("Contains(\"://\"", WindowAround(elevationSite.File, elevationSite.Line, 30, 2), StringComparison.Ordinal);
        Assert.Contains("Verb = \"runas\"", WindowAround(elevationSite.File, elevationSite.Line, 30, 2), StringComparison.Ordinal);
        Assert.Contains("File.Exists", WindowAround(elevationSite.File, elevationSite.Line, 30, 2), StringComparison.Ordinal);

        IReadOnlyList<string> pageFiles = SourceFiles("src/LanDisk.App");
        Assert.Contains(pageFiles, f => File.ReadAllText(f).Contains("NativeDialogs.OpenFolder(Runtime.Paths.LogsDir)", StringComparison.Ordinal));

        // The browser entry must sit behind an explicit confirmation, and the window around it must
        // say what it is for.
        (string file, int line, string _) = codeSites[0];
        string around = WindowAround(file, line, 22, 2);
        Assert.Contains("NativeDialogs.Confirm(", around, StringComparison.Ordinal);
        Assert.Contains("故障恢复", around, StringComparison.Ordinal);
        Assert.Contains("浏览器", around, StringComparison.Ordinal);
        Assert.Contains("adminUrl", around, StringComparison.Ordinal);

        // The second shell-execute call site is the folder helper. It must be a folder-only path: no URL, and
        // the parameter it opens must be checked against "://" first (asserted above), which is what makes
        // "open the log folder" unable to become a second, unlabelled way to a browser.
        (string helperFile, int helperLine, string helperText) = helperSites[0];
        string helperWindow = WindowAround(helperFile, helperLine, 14, 1);
        Assert.Contains("OpenFolder", helperWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("http", helperWindow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adminUrl", helperWindow, StringComparison.Ordinal);
        Assert.True(helperText.Contains("ProcessStartInfo(path)", StringComparison.Ordinal),
            $"文件夹助手只应当用它校验过的 path：{Path.GetFileName(helperFile)}:{helperLine} {helperText}");
        Assert.True(codeSites.Count + helperSites.Count == 3,
            "外壳里应当只有三个 shell-execute 代码调用点：一个恢复页 URL、一个文件夹助手、一个单次提权助手；实际：" +
            string.Join("; ", codeSites.Select(s => $"{Path.GetFileName(s.File)}:{s.Line}")) + " | " +
            string.Join("; ", helperSites.Select(s => $"{Path.GetFileName(s.File)}:{s.Line}")));

        // And there must be no second mechanism: no embedded browser control, no shell URI helper.
        foreach (string forbidden in new[] { "WebBrowser", "WebView2", "LaunchUri", "ShellExecute(", "OpenBrowser" })
        {
            string[] hits = [.. files.Where(f => File.ReadAllText(f).Contains(forbidden, StringComparison.Ordinal))];
            Assert.True(hits.Length == 0,
                $"外壳里出现了额外的浏览器入口 “{forbidden}”：{string.Join(", ", hits.Select(Path.GetFileName))}");
        }

        TestBase.Record("UX08-10",
            $"外壳只有 {sites.Count} 个 shell-execute 调用点：1 个打开日志目录（非 URL），" +
            "1 个打开 Web 管理恢复页且必须经过确认对话框；没有任何内嵌浏览器控件");
    }

    [Fact]
    public void The_shell_project_is_wpf_only()
    {
        string project = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App/LanDisk.App.csproj");
        Assert.True(File.Exists(project), $"找不到 {project}");
        string text = File.ReadAllText(project);

        Assert.Contains("<UseWPF>true</UseWPF>", text, StringComparison.Ordinal);
        Assert.Contains("<UseWindowsForms>false</UseWindowsForms>", text, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>LanDisk</AssemblyName>", text, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0-windows</TargetFramework>", text, StringComparison.Ordinal);

        // The version is pinned; a floating version would make "which WPF-UI is in this candidate"
        // unanswerable from the candidate itself.
        Assert.Contains("Include=\"WPF-UI\" Version=\"4.3.0\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WPF-UI\" Version=\"*", text, StringComparison.Ordinal);

        TestBase.Record("UX08-11", "外壳工程：UseWPF=true / UseWindowsForms=false / AssemblyName=LanDisk / net10.0-windows，WPF-UI 版本固定为 4.3.0");
    }

    [Fact]
    public void No_product_source_writes_a_route_vpn_or_adapter_configuration()
    {
        // First prove the detector is not vacuous: it must flag a planted write and must not flag a
        // plain read. Without this, a typo in the pattern list would silently turn the gate green.
        Assert.Contains("route add", ScanForbidden("route add 0.0.0.0 mask 0.0.0.0 198.18.0.1 metric 1"));
        Assert.Contains("Set-NetIPInterface", ScanForbidden("Set-NetIPInterface -InterfaceAlias 'iKuuuVPN' -Dhcp Disabled"));
        Assert.Empty(ScanForbidden("var adapters = NetworkInterface.GetAllNetworkInterfaces();"));
        Assert.Empty(ScanForbidden("// Wintun / WireGuard / iKuuu tunnels are detected and excluded here"));

        IReadOnlyList<string> files = SourceFiles("src");
        var hits = new List<string>();

        foreach (string file in files)
        {
            foreach (string pattern in ScanForbidden(File.ReadAllText(file)))
            {
                hits.Add($"{Path.GetFileName(file)} → {pattern}");
            }
        }

        Assert.True(hits.Count == 0,
            "LanDisk 不允许写入路由 / VPN / 网卡配置，但出现了：" + string.Join("; ", hits));

        TestBase.Record("UX08-12",
            $"扫描 {files.Count} 个产品源文件：没有任何路由、VPN 连接或网卡/DNS 配置写入（{ForbiddenNetworkWrites.Length} 种模式全部未命中；探测器本身已用植入样本自检）");
    }

    [Fact]
    public void The_only_netsh_use_in_the_product_is_the_firewall_rule_helper()
    {
        IReadOnlyList<string> files = SourceFiles("src");
        // Documentation is dropped before the scan: these gates count *code*, and a doc comment that explains
        // which netsh verb is read-only is part of the explanation, not a second call site.
        List<(string File, int Line, string Text)> sites =
        [.. Sites(files, "netsh").Where(s => !s.Text.TrimStart().StartsWith("///", StringComparison.Ordinal)
                                             && !s.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))];

        Assert.NotEmpty(sites);

        // Scope: every netsh reference lives in the firewall code, never in the server, and never anywhere
        // that could reach routing, DNS or VPN. That is what makes "LanDisk never touches routing" checkable
        // rather than merely claimed.
        //
        // 1.7.2 added a second site, and it is deliberately the narrowest possible one: the self-check has to
        // be able to *read* whether the rules exist, because printing "需要放行 …" unconditionally meant a
        // machine with no rules looked identical to a healthy one. The addition is allowed only because it is
        // read-only and is asserted to be read-only below.
        foreach ((string file, int line, string text) in sites)
        {
            bool installer = file.Contains("LanDisk.Setup", StringComparison.Ordinal);
            bool readOnlyProbe = file.Replace('\\', '/').EndsWith("LanDisk.App/FirewallStatus.cs", StringComparison.Ordinal);
            Assert.True(installer || readOnlyProbe, $"netsh 出现在不允许的位置：{file}:{line} {text.Trim()}");
        }

        string probe = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App/FirewallStatus.cs"));
        Assert.Contains("advfirewall\", \"firewall\", \"show\", \"rule\"", probe, StringComparison.Ordinal);
        foreach (string writeVerb in new[] { "\"add\"", "\"delete\"", "\"set\"", "\"reset\"", "\"interface\"", "\"winhttp\"", "\"wlan\"" })
            Assert.DoesNotContain(writeVerb, probe, StringComparison.Ordinal);

        // And the only verbs it can run are the two firewall-rule operations. The argument lists come
        // from FirewallPlan, so pinning that type pins the whole vocabulary.
        string planFile = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Setup/FirewallPlan.cs");
        string plan = File.ReadAllText(planFile);
        Assert.Contains("\"advfirewall\", \"firewall\", \"add\", \"rule\"", plan, StringComparison.Ordinal);
        Assert.Contains("\"advfirewall\", \"firewall\", \"delete\", \"rule\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("advfirewall", plan.Replace("\"advfirewall\", \"firewall\", \"add\", \"rule\"", string.Empty)
            .Replace("\"advfirewall\", \"firewall\", \"delete\", \"rule\"", string.Empty), StringComparison.Ordinal);

        // The helper passes its arguments straight through, so no other netsh context can be smuggled in.
        string helper = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Setup/FirewallHelper.cs"));
        Assert.Contains("new ProcessStartInfo(\"netsh\")", helper, StringComparison.Ordinal);
        Assert.Contains("RunNetsh(FirewallPlan.AddArguments(rule), log)", helper, StringComparison.Ordinal);
        Assert.Contains("RunNetsh(FirewallPlan.DeleteArguments(rule), log)", helper, StringComparison.Ordinal);

        TestBase.Record("UX08-13",
            $"产品中 {sites.Count} 处 netsh 全部位于安装器；可用动词只有 advfirewall firewall add rule 与 delete rule");
    }

    [Fact]
    public void Each_round_08_evidence_id_belongs_to_exactly_one_test_file()
    {
        // The suite uses case ids as labels for a *group* of assertions (BATCH x12, SORT x9, ...), so
        // repetition inside one feature is normal and this test deliberately does not forbid it.
        // What is not normal is one id covering two different features: UX08-02 was briefly used by
        // both the root-route contract and the shell browser gate, which makes the evidence file
        // ambiguous for exactly the reviewer who is trying to look something up. Round-scoped ids
        // (UX07-*/UX08-*) therefore have to map to a single file.
        string root = Path.Combine(TestPaths.RepoRoot, "tests/LanDisk.Tests");
        var owners = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

            foreach (Match match in Regex.Matches(File.ReadAllText(file), "Record\\(\"(UX0[78]-[0-9A-Za-z]+)\""))
            {
                string id = match.Groups[1].Value;
                if (!owners.TryGetValue(id, out SortedSet<string>? set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    owners[id] = set;
                }
                set.Add(Path.GetFileName(file));
            }
        }

        Assert.True(owners.Count >= 20, $"只找到 {owners.Count} 个 UX07/UX08 证据编号，扫描可能没读到文件");

        List<string> ambiguous =
            [.. owners.Where(pair => pair.Value.Count > 1).Select(pair => $"{pair.Key} → {string.Join(", ", pair.Value)}")];

        Assert.True(ambiguous.Count == 0,
            "以下本轮证据编号同时出现在多个测试文件里，审核时无法唯一定位：" + string.Join("; ", ambiguous));

        TestBase.Record("UX08-14",
            $"{owners.Count} 个 UX07/UX08 证据编号各自只属于一个测试文件（本轮证据文件可唯一定位）");
    }
}
