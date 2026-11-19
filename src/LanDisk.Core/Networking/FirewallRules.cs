namespace LanDisk.Core.Networking;

/// <summary>One inbound rule the product expects Windows to hold.</summary>
public sealed record FirewallRuleSpec(string Name, string Protocol, int LocalPort, string Purpose);

/// <summary>
/// A rule as Windows reports it, read back from <c>netsh advfirewall firewall show rule</c>.
///
/// <para>Every field the judgement needs is carried here. The earlier version carried only name, protocol,
/// port, program and enabled — so the check could not look at direction, action, profile or remote scope
/// even though it claimed the rules were "仅专用网络、仅本地子网". A field that is absent from the query
/// output stays empty, and an empty field is treated as <b>cannot confirm</b>, never as a pass.</para>
/// </summary>
public sealed record FirewallRuleState(
    string Name,
    string Protocol,
    string LocalPort,
    string Program,
    bool? Enabled,
    string Direction = "",
    string Action = "",
    string Profile = "",
    string RemoteAddress = "",
    string EnabledText = "")
{
    /// <summary>Whether every field the judgement reads was actually present in the output.</summary>
    public bool HasAllJudgedFields =>
        Name.Length > 0 && Protocol.Length > 0 && LocalPort.Length > 0 && Program.Length > 0 &&
        Direction.Length > 0 && Action.Length > 0 && Profile.Length > 0 && RemoteAddress.Length > 0 &&
        Enabled.HasValue;
}

/// <summary>What a check found, per rule.</summary>
public sealed record FirewallRuleVerdict(FirewallRuleSpec Expected, FirewallRuleState? Found, string Verdict)
{
    public bool Ok => Verdict == "ok";
    public bool Unknown => Verdict.StartsWith("unknown", StringComparison.Ordinal);

    /// <summary>How many same-named rules were inspected; more than one means Windows holds duplicates.</summary>
    public int Duplicates { get; init; }

    /// <summary>Every same-named rule, so a caller can show what was actually judged.</summary>
    public IReadOnlyList<FirewallRuleState> All { get; init; } = [];
}

/// <summary>
/// The whole picture: which rules are right, which are missing, which are wrong, which could not be read.
///
/// <para><b>What a pass does and does not mean.</b> "ok" means the rules <i>LanDisk looked at</i> match what
/// LanDisk expects. It is not a statement about the rest of the machine's firewall, and it says nothing about
/// whether a phone can actually reach this computer — that depends on the network, the router and the phone.
/// The summary says so rather than implying more.</para>
/// </summary>
public sealed record FirewallReport(IReadOnlyList<FirewallRuleVerdict> Rules)
{
    /// <summary>True when the query itself failed, so nothing could be judged.</summary>
    public bool QueryFailed { get; init; }

    /// <summary>Why the query failed, when it did.</summary>
    public string QueryFailureReason { get; init; } = string.Empty;

    public bool AllPresent =>
        !QueryFailed && Rules.Count > 0 && Rules.All(r => r.Ok);

    public IReadOnlyList<FirewallRuleVerdict> Problems => [.. Rules.Where(r => !r.Ok)];
    public IReadOnlyList<FirewallRuleVerdict> Unconfirmed => [.. Rules.Where(r => r.Unknown)];

    public string Summary
    {
        get
        {
            if (QueryFailed)
                return $"无法读取防火墙规则，未能确认（{QueryFailureReason}）";

            if (AllPresent)
                return $"已检查的 {Rules.Count} 条 LanDisk 入站规则符合要求（入站/允许/仅专用网络/仅本地子网/" +
                       "程序路径与端口一致）。这不代表整机防火墙或手机连通性已验证。";

            var missing = Rules.Count(r => r.Verdict == "missing");
            var unknown = Unconfirmed.Count;
            var wrong = Problems.Count - missing - unknown;

            var parts = new List<string>();
            if (missing > 0) parts.Add($"缺失 {missing} 条");
            if (wrong > 0) parts.Add($"不符合 {wrong} 条");
            if (unknown > 0) parts.Add($"无法确认 {unknown} 条");
            return "LanDisk 防火墙规则" + string.Join("、", parts) + "：" +
                   string.Join("；", Problems.Select(Describe));
        }
    }

    private static string Describe(FirewallRuleVerdict v)
    {
        string name = v.Expected.Purpose;
        if (v.Verdict == "missing") return name + "（未找到同名规则）";
        if (v.Verdict == "disabled") return name + "（规则已禁用）";
        if (v.Verdict.StartsWith("unknown", StringComparison.Ordinal))
            return name + "（无法确认：" + v.Verdict["unknown".Length..].TrimStart(':') + "）";
        return name + "（" + v.Verdict["mismatch".Length..].TrimStart(':') + "）";
    }
}

/// <summary>
/// The firewall rules LanDisk expects, and the pure logic that decides whether Windows agrees.
///
/// <para><b>Why this moved into the shared assembly.</b> The installer knew the rules and the self-check did
/// not: it printed "需要放行 TCP 8892 与 UDP 5353" unconditionally, so a machine whose rules had never been
/// created showed the same "needs confirmation" line forever. The Owner read that as an error and had no way
/// to act on it. The expectation and the check now live together, and the self-check can say which rule is
/// actually missing and offer to repair it.</para>
///
/// <para><b>What the judgement now covers.</b> The installer generates rules with a fixed shape — inbound,
/// allow, bound to the exact program path, a specific protocol and port, Private profile only, LocalSubnet
/// remote only. A check that only compares name, enabled, protocol and port will happily call a rule "ok"
/// while it is outbound, a block rule, open to the Public profile, reachable from any address, or pointing at
/// a stale executable that protects nothing. Every one of those fields is therefore compared, and
/// <b>all</b> same-named rules must conform: one correct rule must not cover for a second, wider one.</para>
///
/// <para><b>What is deliberately absent.</b> There is no rule for 8891. The loopback management page is a
/// local-only surface; a firewall rule for it would publish it to the LAN and undo the separation the rest of
/// the product is built around. <see cref="NeverInbound"/> exists so a test can assert that explicitly rather
/// than relying on nobody adding one.</para>
/// </summary>
public static class FirewallRules
{
    public const int DefaultHttpsPort = 8892;
    public const int DefaultBootstrapPort = 8890;
    public const int MdnsPort = 5353;

    /// <summary>The only profile a LanDisk inbound rule may be scoped to.</summary>
    public const string RequiredProfile = "private";

    /// <summary>The only remote scope a LanDisk inbound rule may use.</summary>
    public const string RequiredRemoteAddress = "localsubnet";

    /// <summary>Ports that must never carry an inbound rule.</summary>
    public static IReadOnlyList<int> NeverInbound { get; } = [8891];

    public static IReadOnlyList<FirewallRuleSpec> Expected(int httpsPort = DefaultHttpsPort, int bootstrapPort = DefaultBootstrapPort)
        =>
        [
            new("LanDisk HTTPS (Private)", "TCP", httpsPort, "手机端 HTTPS 访问"),
            new("LanDisk 首次连接向导 (Private)", "TCP", bootstrapPort, "首次连接向导"),
            new("LanDisk mDNS (Private)", "UDP", MdnsPort, "稳定局域网名（mDNS）")
        ];

    /// <summary>
    /// Reads the output of <c>netsh advfirewall firewall show rule name=all</c> (or a single-name query) into
    /// rule states. Pure, so the parsing of a Windows text format is pinned by tests instead of by hope.
    /// </summary>
    public static IReadOnlyList<FirewallRuleState> Parse(string output)
    {
        var states = new List<FirewallRuleState>();
        if (string.IsNullOrWhiteSpace(output)) return states;

        string name = string.Empty;
        string protocol = string.Empty;
        string localPort = string.Empty;
        string program = string.Empty;
        string direction = string.Empty;
        string action = string.Empty;
        string profile = string.Empty;
        string remoteAddress = string.Empty;
        // null means "the output did not state it, or stated something we do not recognise".
        // It must never be folded into false: "禁用" and "没读到" are different findings.
        bool? enabled = null;
        string enabledText = string.Empty;

        void Flush()
        {
            if (name.Length == 0) return;
            states.Add(new FirewallRuleState(name, protocol.ToLowerInvariant(), localPort, program,
                enabled, direction.ToLowerInvariant(), action.ToLowerInvariant(),
                profile.ToLowerInvariant(), remoteAddress.ToLowerInvariant(), enabledText));
            name = protocol = localPort = program = direction = action = profile = remoteAddress = string.Empty;
            enabled = null;
            enabledText = string.Empty;
        }

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();

            // The dashed separator sits *below* the "Rule Name" header, so flushing on it would close the
            // record before its own attributes were read — which is how a real netsh listing ended up parsed
            // as nameless rules with empty ports. It is decoration, not a boundary.
            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Length == 0 || line.StartsWith("Ok.", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            switch (key)
            {
                // A second "Rule Name" ends the previous record, whatever separated them.
                case "rule name":
                    Flush();
                    name = value;
                    break;
                case "protocol": protocol = value; break;
                case "localport": localPort = value; break;
                case "program": program = value; break;
                case "direction": direction = value; break;
                case "action": action = value; break;
                // Windows prints "Profiles" in `show rule` output; accept the singular too.
                case "profiles":
                case "profile": profile = value; break;
                case "remoteip": remoteAddress = value; break;
                case "enabled":
                    enabledText = value;
                    // Recognise only what we can actually interpret. Windows prints Yes/No in English output;
                    // anything else is left unknown rather than being read as "off".
                    if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("是", StringComparison.OrdinalIgnoreCase)) { enabled = true; }
                    else if (value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                             value.Equals("否", StringComparison.OrdinalIgnoreCase)) { enabled = false; }
                    else { enabled = null; }
                    break;
            }
        }

        Flush();
        return states;
    }

    /// <summary>
    /// A report that says "the query failed", as distinct from "the rules are missing".
    ///
    /// <para>These are not the same finding and must not look the same. Collapsing a failed or timed-out
    /// query into an empty rule list made every failure render as "缺少或不符合", which invites the wrong
    /// repair — deleting and re-adding rules cannot fix a machine where the query itself does not work.</para>
    /// </summary>
    public static FirewallReport QueryFailure(string reason, int httpsPort = DefaultHttpsPort, int bootstrapPort = DefaultBootstrapPort)
        => new([.. Expected(httpsPort, bootstrapPort)
                .Select(spec => new FirewallRuleVerdict(spec, null, "unknown:查询失败"))])
        {
            QueryFailed = true,
            QueryFailureReason = reason
        };

    /// <summary>
    /// Compares what Windows reports with what the product expects.
    ///
    /// <para>Each expected rule is matched against <b>every</b> same-named rule Windows holds. All of them
    /// must conform: taking the first match would let a duplicate, wider rule sit next to a correct one and
    /// still show green. A rule whose judged fields were not present in the output is <c>unknown</c>, not
    /// <c>ok</c>.</para>
    /// </summary>
    /// <param name="expectedProgram">
    /// The exact executable the rules must point at (the shell inside the install directory). When it is not
    /// known the program check cannot be made, so the report says "cannot confirm" rather than passing.
    /// </param>
    public static FirewallReport Evaluate(
        IEnumerable<FirewallRuleState> present,
        int httpsPort = DefaultHttpsPort,
        int bootstrapPort = DefaultBootstrapPort,
        string expectedProgram = "")
    {
        List<FirewallRuleState> states = [.. present];
        var verdicts = new List<FirewallRuleVerdict>();

        foreach (FirewallRuleSpec spec in Expected(httpsPort, bootstrapPort))
        {
            List<FirewallRuleState> matches =
                [.. states.Where(s => string.Equals(s.Name, spec.Name, StringComparison.OrdinalIgnoreCase))];

            if (matches.Count == 0)
            {
                verdicts.Add(new FirewallRuleVerdict(spec, null, "missing"));
                continue;
            }

            // Judge every same-named rule; the first problem wins, and duplicates are reported.
            string problem = string.Empty;
            FirewallRuleState? guilty = null;
            foreach (FirewallRuleState candidate in matches)
            {
                problem = Judge(candidate, spec, expectedProgram);
                if (problem.Length > 0) { guilty = candidate; break; }
            }

            verdicts.Add(new FirewallRuleVerdict(spec, guilty ?? matches[0], problem.Length == 0 ? "ok" : problem)
            {
                Duplicates = matches.Count,
                All = matches
            });
        }

        return new FirewallReport(verdicts);
    }

    /// <summary>Returns an empty string when the rule conforms, otherwise the verdict token.</summary>
    private static string Judge(FirewallRuleState found, FirewallRuleSpec spec, string expectedProgram)
    {
        if (!found.HasAllJudgedFields)
        {
            var absent = new List<string>();
            if (found.Program.Length == 0) absent.Add("程序");
            if (found.Direction.Length == 0) absent.Add("方向");
            if (found.Action.Length == 0) absent.Add("动作");
            if (found.Profile.Length == 0) absent.Add("配置文件");
            if (found.RemoteAddress.Length == 0) absent.Add("远端范围");
            if (found.LocalPort.Length == 0) absent.Add("端口");
            if (found.Protocol.Length == 0) absent.Add("协议");
            if (absent.Count > 0) return "unknown:查询结果缺少字段（" + string.Join("、", absent) + "）";

            // Enabled 单独说明：它可能是"字段缺失"，也可能是"读到了但无法识别"。
            return found.EnabledText.Length == 0
                ? "unknown:查询结果缺少 Enabled 字段"
                : "unknown:Enabled 字段无法识别（实际 \"" + found.EnabledText + "\"）";
        }

        // 到这里 Enabled 一定有明确值；false 才是"明确禁用"。
        if (found.Enabled == false) return "disabled";

        // Direction: must be inbound, and must not also be listed as outbound.
        if (!found.Direction.Contains("in", StringComparison.Ordinal) ||
            found.Direction.Contains("out", StringComparison.Ordinal))
            return "mismatch:方向不是入站（实际 " + found.Direction + "）";

        // Action: allow, and not block.
        if (!found.Action.Contains("allow", StringComparison.Ordinal))
            return "mismatch:动作不是允许（实际 " + found.Action + "）";

        // Profile: Private only. "Any"/"Domain,Private,Public" means the rule is wider than intended.
        if (!found.Profile.Contains(RequiredProfile, StringComparison.Ordinal))
            return "mismatch:配置文件不含 Private（实际 " + found.Profile + "）";
        if (found.Profile.Contains("public", StringComparison.Ordinal) ||
            found.Profile.Contains("domain", StringComparison.Ordinal) ||
            found.Profile.Contains("any", StringComparison.Ordinal))
            return "mismatch:配置文件超出了专用网络（实际 " + found.Profile + "）";

        // Remote scope: LocalSubnet only.
        if (!found.RemoteAddress.Replace(" ", string.Empty).Contains(RequiredRemoteAddress, StringComparison.Ordinal))
            return "mismatch:远端范围不是本地子网（实际 " + found.RemoteAddress + "）";

        // Program: the exact executable in the install directory.
        if (expectedProgram.Length == 0)
            return "unknown:调用方没有提供期望程序路径";
        string want = NormalizePath(expectedProgram);
        string got = NormalizePath(found.Program);
        if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase))
            return "mismatch:程序路径不符（实际 " + found.Program + "）";

        if (!found.Protocol.Contains(spec.Protocol.ToLowerInvariant(), StringComparison.Ordinal))
            return "mismatch:协议不符（实际 " + found.Protocol + "，期望 " + spec.Protocol + "）";

        if (!string.Equals(found.LocalPort, spec.LocalPort.ToString(), StringComparison.Ordinal))
            return "mismatch:端口不符（实际 " + found.LocalPort + "，期望 " + spec.LocalPort + "）";

        return string.Empty;
    }

    private static string NormalizePath(string path) =>
        path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// Turns a report into the rows the shell shows. Pure text mapping with no UI dependency, so the
    /// three-way state (通过 / 需修复 / 无法确认) and the wording are pinned by tests rather than by reading
    /// the presenter.
    ///
    /// <para>A failed query is <b>not</b> "需修复": re-adding rules cannot fix a machine whose firewall query
    /// does not work, and telling the Owner to repair something that was never measured is worse than saying
    /// "无法确认". Each failing row carries the actual finding instead of restating the requirement as though
    /// it had been verified.</para>
    /// </summary>
    public static IReadOnlyList<(string Name, string State, string Detail)> Rows(FirewallReport report)
    {
        var rows = new List<(string, string, string)>();

        string state = report.QueryFailed ? "无法确认" : report.AllPresent ? "通过" : "需修复";
        rows.Add(("防火墙", state, report.Summary));

        foreach (FirewallRuleVerdict verdict in report.Problems)
        {
            string name = "  · " + verdict.Expected.Purpose;
            string what;
            if (verdict.Verdict == "missing") what = "缺少规则";
            else if (verdict.Verdict == "disabled") what = "规则被禁用";
            else if (verdict.Verdict.StartsWith("unknown", StringComparison.Ordinal)) what = "无法确认";
            else what = "规则与本版本不一致";

            string detail;
            if (verdict.Verdict.StartsWith("mismatch", StringComparison.Ordinal))
                detail = verdict.Verdict["mismatch".Length..].TrimStart(':');
            else if (verdict.Verdict.StartsWith("unknown", StringComparison.Ordinal))
                detail = verdict.Verdict["unknown".Length..].TrimStart(':');
            else
                detail = $"应为 {verdict.Expected.Protocol} {verdict.Expected.LocalPort}，" +
                         "入站/允许/仅专用网络/仅本地子网，且指向安装目录中的 LanDisk.exe";

            if (verdict.Duplicates > 1) detail += $"（同名规则共 {verdict.Duplicates} 条，逐条都必须符合）";
            rows.Add((name, what, detail));
        }

        return rows;
    }
}
