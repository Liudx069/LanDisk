using LanDisk.Core.Networking;
using LanDisk.Setup;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// FIREWALL13 — the judgement chain: <c>netsh</c> output → parse → compare → the row the shell shows.
///
/// <para><b>Why this family exists.</b> The self-check used to compare only name, enabled, protocol and port,
/// yet its summary claimed "仅专用网络、仅本地子网". A rule could be outbound, a block rule, open to the
/// Public profile, reachable from any address, or point at an executable that no longer exists, and the
/// check still said "已放行". These tests pin every field the installer actually sets, and pin the three-way
/// result so "cannot confirm" can never render as a pass.</para>
///
/// <para>Everything here goes through the real functions the product calls: <see cref="FirewallRules.Parse"/>,
/// <see cref="FirewallRules.Evaluate"/> and <see cref="FirewallRules.Rows"/>. Nothing is asserted
/// against a helper that only the test uses.</para>
/// </summary>
public sealed class FirewallJudgementTests
{
    private const string InstallDir = @"C:\Program Files\LanDisk";
    private static readonly string Shell = Path.Combine(InstallDir, "LanDisk.exe");

    /// <summary>Renders one rule the way <c>netsh advfirewall firewall show rule</c> prints it.</summary>
    private static string Block(
        string name = "LanDisk HTTPS (Private)",
        string enabled = "Yes",
        string direction = "In",
        string profiles = "Private",
        string remoteIp = "LocalSubnet",
        string protocol = "TCP",
        string localPort = "8892",
        string program = "",
        string action = "Allow",
        bool omit = false,
        bool omitRemote = false,
        bool omitProgram = false)
    {
        if (program.Length == 0) program = Shell;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Rule Name:                            " + name);
        sb.AppendLine("----------------------------------------------------------------------");
        if (!omit) sb.AppendLine("Enabled:                              " + enabled);
        sb.AppendLine("Direction:                            " + direction);
        sb.AppendLine("Profiles:                             " + profiles);
        sb.AppendLine("Grouping:");
        sb.AppendLine("LocalIP:                              Any");
        if (!omitRemote) sb.AppendLine("RemoteIP:                             " + remoteIp);
        sb.AppendLine("Protocol:                             " + protocol);
        sb.AppendLine("LocalPort:                            " + localPort);
        sb.AppendLine("RemotePort:                           Any");
        sb.AppendLine("Edge traversal:                       No");
        if (!omitProgram) sb.AppendLine("Program:                              " + program);
        sb.AppendLine("Action:                               " + action);
        return sb.ToString();
    }

    /// <summary>One conforming rule for each of the three expected names.</summary>
    private static string AllThree() =>
        Block() +
        Block(name: "LanDisk 首次连接向导 (Private)", localPort: "8890") +
        Block(name: "LanDisk mDNS (Private)", protocol: "UDP", localPort: "5353");

    private static FirewallReport Judge(string listing) =>
        FirewallRules.Evaluate(FirewallRules.Parse(listing), 8892, 8890, Shell);

    // ---------------------------------------------------------------- 正确规则通过

    [Fact]
    public void Conforming_rules_pass()
    {
        FirewallReport report = Judge(AllThree());
        Assert.True(report.AllPresent, report.Summary);
        Assert.Empty(report.Problems);
        Assert.False(report.QueryFailed);
    }

    /// <summary>
    /// The installer's own plan must satisfy the self-check: render what the installer would create, feed it
    /// back, and require a pass. If the two ever drift, this fails instead of the Owner seeing a false alarm.
    /// </summary>
    [Fact]
    public void What_the_installer_creates_is_what_the_self_check_accepts()
    {
        string listing = string.Empty;
        foreach (FirewallRule rule in FirewallPlan.Rules(InstallDir))
        {
            listing += Block(
                name: rule.Name,
                direction: rule.Direction == "in" ? "In" : rule.Direction,
                profiles: rule.Profile == "private" ? "Private" : rule.Profile,
                remoteIp: rule.RemoteAddress == "localsubnet" ? "LocalSubnet" : rule.RemoteAddress,
                protocol: rule.Protocol,
                localPort: rule.LocalPort,
                program: rule.Program,
                action: rule.Action == "allow" ? "Allow" : rule.Action);
        }

        FirewallReport report = Judge(listing);
        Assert.True(report.AllPresent, report.Summary);
    }

    // ---------------------------------------------------------------- 逐字段不通过

    [Theory]
    // 程序路径：指向已经不存在的可执行文件，规则保护不了任何东西
    [InlineData("program", @"C:\Program Files\LanDisk\old\LanDisk.exe")]
    // 方向：出站规则不构成入站放行
    [InlineData("direction", "Out")]
    // 动作：拦截规则
    [InlineData("action", "Block")]
    // 配置文件：任何更宽的配置文件都不接受
    [InlineData("profiles", "Public")]
    [InlineData("profiles", "Domain,Private,Public")]
    [InlineData("profiles", "Any")]
    // 远端范围：Any 意味着整张网络都能到达
    [InlineData("remoteIp", "Any")]
    // 端口与协议
    [InlineData("localPort", "8893")]
    [InlineData("protocol", "UDP")]
    // 禁用
    [InlineData("enabled", "No")]
    public void Any_single_field_that_disagrees_fails(string field, string value)
    {
        string listing = field switch
        {
            "program" => Block(program: value),
            "direction" => Block(direction: value),
            "action" => Block(action: value),
            "profiles" => Block(profiles: value),
            "remoteIp" => Block(remoteIp: value),
            "localPort" => Block(localPort: value),
            "protocol" => Block(protocol: value),
            "enabled" => Block(enabled: value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        FirewallReport report = Judge(listing);
        Assert.False(report.AllPresent, $"{field}={value} 不应通过");
        FirewallRuleVerdict https = report.Rules.First(r => r.Expected.LocalPort == 8892);
        Assert.False(https.Ok);
    }

    /// <summary>
    /// A same-named rule that is too wide must not be hidden by another same-named rule that is fine.
    /// The earlier implementation used <c>FirstOrDefault</c> by name, so whichever rule came first decided
    /// the verdict — one correct rule could cover for a second, wider one.
    /// </summary>
    [Fact]
    public void A_wider_duplicate_cannot_hide_behind_a_conforming_one()
    {
        string good = Block();
        string wide = Block(profiles: "Any", remoteIp: "Any");

        // 正常规则在前、过宽规则在后
        Assert.False(Judge(good + wide).AllPresent, "过宽的同名规则不应被前面的正常规则掩盖");

        // 顺序颠倒也必须失败（否则说明判定依赖遍历顺序）
        Assert.False(Judge(wide + good).AllPresent, "顺序颠倒后仍必须失败");

        FirewallRuleVerdict https = Judge(good + wide).Rules.First(r => r.Expected.LocalPort == 8892);
        Assert.Equal(2, https.Duplicates);
        Assert.Contains("配置文件", https.Verdict);
    }

    /// <summary>Fields absent from the output mean "cannot confirm" — never a pass.</summary>
    [Fact]
    public void An_unparsable_listing_is_not_a_pass()
    {
        Assert.False(Judge("这是一段无法解析的输出，没有任何规则字段").AllPresent);
        Assert.False(Judge(string.Empty).AllPresent);

        // 缺少远端范围字段：不能因为没读到就当作符合。
        // 用显式开关而不是字符串替换——替换会因行尾或空格数不同而静默失效，测试就成了假的。
        FirewallRuleState fields = FirewallRules.Parse(Block(omitRemote: true)).First();
        Assert.Equal(string.Empty, fields.RemoteAddress);
        Assert.False(fields.HasAllJudgedFields);

        FirewallRuleVerdict https = FirewallRules
            .Evaluate([fields], 8892, 8890, Shell)
            .Rules.First(r => r.Expected.LocalPort == 8892);
        Assert.False(https.Ok);
        Assert.True(https.Unknown, "缺字段必须是 unknown，而不是 ok 或 mismatch");
        Assert.Contains("远端范围", https.Verdict);
    }

    [Fact]
    public void Missing_rules_are_reported_as_missing_not_as_disabled_or_unknown()
    {
        FirewallReport report = Judge(Block());
        Assert.Equal(2, report.Rules.Count(r => r.Verdict == "missing"));
        Assert.Equal(1, report.Rules.Count(r => r.Verdict == "ok"));
    }

    /// <summary>
    /// A failed query must not look like "the rules are missing": the two lead to opposite actions, and
    /// re-adding rules cannot fix a machine whose firewall query does not run.
    /// </summary>
    [Fact]
    public void A_failed_query_is_cannot_confirm_not_missing()
    {
        FirewallReport report = FirewallRules.QueryFailure("netsh 查询失败：Win32Exception", 8892, 8890);

        Assert.True(report.QueryFailed);
        Assert.False(report.AllPresent);
        Assert.DoesNotContain(report.Rules, r => r.Verdict == "missing");
        Assert.All(report.Rules, r => Assert.True(r.Unknown));
        Assert.Contains("无法读取", report.Summary);
        Assert.Contains("netsh 查询失败", report.Summary);
    }

    /// <summary>
    /// A query failure must not be judged as if the caller had supplied an empty rule list — that is exactly
    /// the conflation the caller used to make by turning an exception into <c>string.Empty</c>.
    /// </summary>
    [Fact]
    public void An_empty_rule_list_and_a_failed_query_are_different_findings()
    {
        FirewallReport empty = FirewallRules.Evaluate([], 8892, 8890, Shell);
        FirewallReport failed = FirewallRules.QueryFailure("超时", 8892, 8890);

        Assert.Contains(empty.Rules, r => r.Verdict == "missing");
        Assert.DoesNotContain(failed.Rules, r => r.Verdict == "missing");
        Assert.NotEqual(empty.Summary, failed.Summary);
    }

    // ---------------------------------------------------------------- 界面结果链

    [Fact]
    public void The_shown_state_distinguishes_pass_repair_and_cannot_confirm()
    {
        Assert.Equal("通过", FirewallRules.Rows(Judge(AllThree()))[0].State);
        Assert.Equal("需修复", FirewallRules.Rows(Judge(Block()))[0].State);
        Assert.Equal("无法确认", FirewallRules.Rows(FirewallRules.QueryFailure("超时", 8892, 8890))[0].State);
    }

    /// <summary>The pass wording must not claim more than was checked.</summary>
    [Fact]
    public void The_pass_wording_does_not_overclaim()
    {
        string summary = Judge(AllThree()).Summary;
        Assert.Contains("已检查", summary);
        Assert.Contains("不代表整机防火墙或手机连通性已验证", summary);
        Assert.DoesNotContain("已放行", summary);
    }

    /// <summary>A failing row must carry the actual finding, not a restatement of the requirement.</summary>
    [Fact]
    public void A_failing_row_carries_the_actual_finding()
    {
        IReadOnlyList<(string Name, string State, string Detail)> rows =
            FirewallRules.Rows(Judge(Block(profiles: "Public")));

        (string Name, string State, string Detail) row = rows.First(r => r.Name.Contains("手机端 HTTPS"));
        Assert.Equal("规则与本版本不一致", row.State);
        Assert.Contains("配置文件", row.Detail);
        Assert.Contains("public", row.Detail);
    }

    [Fact]
    public void A_duplicate_is_stated_in_the_shown_detail()
    {
        IReadOnlyList<(string Name, string State, string Detail)> rows =
            FirewallRules.Rows(Judge(Block() + Block(profiles: "Any")));

        Assert.Contains(rows, r => r.Detail.Contains("同名规则共 2 条"));
    }

    // ---------------------------------------------------------------- 管理端口

    /// <summary>8891 stays loopback-only; no listing may make an inbound rule for it acceptable.</summary>
    [Fact]
    public void The_management_port_is_never_an_expected_inbound_rule()
    {
        Assert.DoesNotContain(FirewallRules.Expected(8892, 8890), s => s.LocalPort == 8891);
        Assert.Contains(8891, FirewallRules.NeverInbound);
        Assert.DoesNotContain(FirewallPlan.Rules(InstallDir), r => r.LocalPort == "8891");
    }
}
