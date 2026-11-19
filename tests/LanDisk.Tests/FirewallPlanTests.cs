using LanDisk.Core.Networking;
using LanDisk.Setup;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// FIREWALL12 — the inbound rules the product asks Windows for, and the self-check that reads them back.
///
/// <para><b>Why this is its own family.</b> The firewall was the one item in the Owner's list with no gate of
/// its own: the report could only cite installer and identity checks, which say nothing about ports, profiles
/// or which executable a rule points at. Everything asserted here is pure data — the rule plan and the parser
/// of <c>netsh</c>'s output — so "TCP 8892, private, local subnet, bound to LanDisk.exe, never 8891" is
/// checkable without touching a firewall.</para>
/// </summary>
public sealed class FirewallPlanTests
{
    [Fact]
    public void The_three_required_rules_are_exactly_what_the_installer_creates()
    {
        IReadOnlyList<FirewallRule> rules = FirewallPlan.Rules(@"C:\Program Files\LanDisk");

        Assert.Equal(3, rules.Count);
        Assert.Equal([("TCP", 8892), ("TCP", 8890), ("UDP", 5353)],
            rules.Select(r => (r.Protocol, int.Parse(r.LocalPort))).ToList());

        Assert.All(rules, rule =>
        {
            Assert.Equal("in", rule.Direction);
            Assert.Equal("allow", rule.Action);
            // Private profile + local subnet only: a rule that also covers Public would expose the product on
            // every café and hotel network the machine ever joins.
            Assert.Equal(FirewallPlan.PrivateProfile, rule.Profile);
            Assert.Equal(FirewallPlan.LocalSubnet, rule.RemoteAddress);
            // Bound to the stable installed executable, not to a port alone and not to a build output path.
            Assert.Equal(Path.Combine(@"C:\Program Files\LanDisk", "LanDisk.exe"), rule.Program);
        });

        TestBase.Record("FIREWALL12-01",
            "安装器只请求三条入站规则：TCP 8892（HTTPS）、TCP 8890（首次连接向导）、UDP 5353（mDNS）；全部 inbound/allow/Private profile/LocalSubnet，且都绑定稳定安装路径下的 LanDisk.exe（不是端口通配、也不是构建输出路径）");
    }

    [Fact]
    public void The_management_port_never_gets_an_inbound_rule()
    {
        IReadOnlyList<FirewallRule> rules = FirewallPlan.Rules(@"C:\Program Files\LanDisk");

        Assert.Contains(8891, FirewallRules.NeverInbound);
        foreach (FirewallRule rule in rules)
            Assert.NotEqual("8891", rule.LocalPort);

        // And the generated argument list cannot smuggle it in either.
        foreach (FirewallRule rule in rules)
            Assert.DoesNotContain(FirewallPlan.AddArguments(rule), a => a.Contains("8891", StringComparison.Ordinal));

        TestBase.Record("FIREWALL12-02",
            "8891（仅本机管理端）永远不会有入站规则：三条规则的 localport 都不是 8891，生成的 netsh 参数里也搜不到 8891；FirewallRules.NeverInbound 把这条写成可断言的事实");
    }

    [Fact]
    public void The_netsh_arguments_carry_the_exact_rule()
    {
        FirewallRule https = FirewallPlan.Rules(@"C:\Program Files\LanDisk")[0];
        IReadOnlyList<string> add = FirewallPlan.AddArguments(https);

        Assert.Contains("name=" + https.Name, add);
        Assert.Contains("dir=in", add);
        Assert.Contains("action=allow", add);
        Assert.Contains("protocol=TCP", add);
        Assert.Contains("localport=8892", add);
        Assert.Contains("profile=private", add);
        Assert.Contains("remoteip=localsubnet", add);
        Assert.Contains("enable=yes", add);
        Assert.Contains(add, a => a.StartsWith("program=", StringComparison.Ordinal) && a.EndsWith("LanDisk.exe", StringComparison.Ordinal));

        // Deleting is by exact name only, so LanDisk can never remove a rule it did not create.
        FirewallRule mdns = FirewallPlan.Rules(@"C:\Program Files\LanDisk")[2];
        Assert.Equal(["advfirewall", "firewall", "delete", "rule", "name=" + mdns.Name], FirewallPlan.DeleteArguments(mdns));

        TestBase.Record("FIREWALL12-03",
            "生成的 netsh 参数逐项正确（name/dir=in/action=allow/protocol/localport/profile=private/remoteip=localsubnet/enable=yes/program=…LanDisk.exe）；删除只按精确规则名，LanDisk 删不掉不是自己建的规则");
    }

    [Fact]
    public void The_self_check_reads_back_what_windows_reports()
    {
        // A realistic `netsh advfirewall firewall show rule name=all` excerpt: one good rule, one pointing at
        // an old executable with the wrong port, and nothing else.
        // 该片段缺 RemoteIP/Action（真实输出总是有），判定器对缺字段必须说"无法确认"——
        // 这正是 FIREWALL13 的根本要求，所以这里也按真实输出补齐后再断言。
        const string output = """
Rule Name:                            LanDisk HTTPS (Private)
----------------------------------------------------------------------
Enabled:                              Yes
Direction:                            In
Profiles:                             Private
LocalIP:                              Any
RemoteIP:                             LocalSubnet
LocalPort:                            8892
Protocol:                             TCP
Program:                              C:\Program Files\LanDisk\LanDisk.exe
Action:                               Allow

Rule Name:                            LanDisk mDNS (Private)
----------------------------------------------------------------------
Enabled:                              Yes
Direction:                            In
Profiles:                             Private
LocalIP:                              Any
RemoteIP:                             LocalSubnet
LocalPort:                            5353
Protocol:                             UDP
Program:                              C:\Program Files\LanDisk\LanDisk.exe
Action:                               Allow

Ok.
""";

        IReadOnlyList<FirewallRuleState> parsed = FirewallRules.Parse(output);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("8892", parsed[0].LocalPort);
        Assert.Equal("tcp", parsed[0].Protocol);
        Assert.True(parsed[0].Enabled);
        Assert.Equal("localsubnet", parsed[0].RemoteAddress);
        Assert.Equal("in", parsed[0].Direction);
        Assert.Equal("allow", parsed[0].Action);
        Assert.Equal("private", parsed[0].Profile);

        FirewallReport report = FirewallRules.Evaluate(parsed, FirewallRules.DefaultHttpsPort, FirewallRules.DefaultBootstrapPort,
            @"C:\Program Files\LanDisk\LanDisk.exe");
        Assert.False(report.AllPresent);
        // The bootstrap rule is absent, and its absence is named rather than summarised.
        Assert.Contains(report.Problems, p => p.Verdict == "missing" && p.Expected.LocalPort == 8890);

        // A rule with the wrong port is a problem, not a pass: it protects nothing.
        IReadOnlyList<FirewallRuleState> wrongPort =
            [new FirewallRuleState("LanDisk HTTPS (Private)", "tcp", "9999", @"C:\Program Files\LanDisk\LanDisk.exe", true,
                "in", "allow", "private", "localsubnet")];
        Assert.Contains(FirewallRules.Evaluate(wrongPort, FirewallRules.DefaultHttpsPort, FirewallRules.DefaultBootstrapPort,
            @"C:\Program Files\LanDisk\LanDisk.exe").Problems, p => p.Verdict.StartsWith("mismatch", StringComparison.Ordinal));

        // Empty output means "Windows has no such rules", which must not read as success.
        Assert.False(FirewallRules.Evaluate([], FirewallRules.DefaultHttpsPort, FirewallRules.DefaultBootstrapPort,
            @"C:\Program Files\LanDisk\LanDisk.exe").AllPresent);

        TestBase.Record("FIREWALL12-04",
            "自检真的判定 Windows 报告的规则：解析 netsh 输出得到 2 条规则并读出方向/动作/配置文件/远端范围；缺 TCP 8890 被逐条指出为 missing；端口不符判为 mismatch 而不是通过；没有任何规则时判定为未通过（而不是默认成功）");
    }

    [Fact]
    public void All_three_present_and_correct_is_the_only_pass()
    {
        // 用安装器**实际计划**的形状构造，而不是只填 5 个字段：判定器现在要求方向/动作/配置文件/
        // 远端范围/程序路径都必须读得到且符合，缺任何一项都只能是"无法确认"，不是通过。
        IReadOnlyList<FirewallRuleState> present = FirewallPlan.Rules(@"C:\Program Files\LanDisk")
            .Select(r => new FirewallRuleState(r.Name, r.Protocol.ToLowerInvariant(), r.LocalPort, r.Program, true,
                r.Direction, r.Action, r.Profile, r.RemoteAddress))
            .ToList();

        FirewallReport report = FirewallRules.Evaluate(present, FirewallRules.DefaultHttpsPort, FirewallRules.DefaultBootstrapPort,
            @"C:\Program Files\LanDisk\LanDisk.exe");
        Assert.True(report.AllPresent, report.Summary);
        Assert.Empty(report.Problems);

        // Disabled is not present.
        List<FirewallRuleState> disabled = [.. present];
        disabled[1] = disabled[1] with { Enabled = false };
        Assert.Contains(FirewallRules.Evaluate(disabled, FirewallRules.DefaultHttpsPort, FirewallRules.DefaultBootstrapPort,
            @"C:\Program Files\LanDisk\LanDisk.exe").Problems, p => p.Verdict == "disabled");

        TestBase.Record("FIREWALL12-05",
            "只有三条规则全部存在、端口/协议/方向/动作/配置文件/远端范围/程序路径都正确且已启用时才通过；被禁用的规则判为 disabled（不算通过）——自检不会因为规则「存在」就报通过");
    }
}
