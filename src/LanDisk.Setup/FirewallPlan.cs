using System.Text;

namespace LanDisk.Setup;

/// <summary>One Windows Firewall rule, described as data so it can be asserted without touching a firewall.</summary>
public sealed record FirewallRule(
    string Name,
    string Direction,
    string Action,
    string Program,
    string Protocol,
    string LocalPort,
    string Profile,
    string RemoteAddress,
    string Description);

/// <summary>
/// The complete set of firewall rules LanDisk will ever ask Windows for.
///
/// <para>Three rules, all inbound, all Private profile, all restricted to the local subnet, all bound
/// to the exact program path: HTTPS for phones, the first-connection wizard's bootstrap port, and the
/// multicast group mDNS needs. Nothing else — in particular the loopback management port 8891 is never
/// mentioned, because management is a local-only surface and exposing it to the LAN would undo the
/// separation the rest of the product is built around.</para>
///
/// <para>The commands are generated here rather than assembled at the call site so that the elevated
/// helper has nothing to decide: it receives a fixed argument list and refuses anything else. That is
/// what keeps "one UAC prompt" from turning into "an administrator-capable installer that runs
/// whatever it is told".</para>
/// </summary>
public static class FirewallPlan
{
    // These mirror LanDisk.Core's expectation rather than being a second copy: the self-check compares the
    // rules Windows reports against exactly these values, so a drift between the two would mean the installer
    // creates rules the check then rejects (or, worse, accepts a wider rule).
    public const string PrivateProfile = Core.Networking.FirewallRules.RequiredProfile;
    public const string LocalSubnet = Core.Networking.FirewallRules.RequiredRemoteAddress;

    public const string HttpsRuleName = SetupLayout.FirewallRulePrefix + " HTTPS (Private)";
    public const string BootstrapRuleName = SetupLayout.FirewallRulePrefix + " 首次连接向导 (Private)";
    public const string MdnsRuleName = SetupLayout.FirewallRulePrefix + " mDNS (Private)";

    public static IReadOnlyList<FirewallRule> Rules(string installDir, bool includeMdns = true)
    {
        // The expectation itself lives in LanDisk.Core (FirewallRules) so the installer and the self-check
        // cannot drift apart: the check used to print "需要放行 TCP 8892 与 UDP 5353" without ever looking at
        // Windows, so a machine with no rules showed the same line forever.
        string program = SetupLayout.ShellPathFor(Path.GetFullPath(installDir));
        var rules = new List<FirewallRule>();
        foreach (Core.Networking.FirewallRuleSpec spec in Core.Networking.FirewallRules.Expected(SetupLayout.HttpsPort, SetupLayout.BootstrapPort))
        {
            if (!includeMdns && spec.LocalPort == Core.Networking.FirewallRules.MdnsPort) continue;
            rules.Add(new FirewallRule(spec.Name, "in", "allow", program, spec.Protocol, spec.LocalPort.ToString(),
                PrivateProfile, LocalSubnet, "LanDisk " + spec.Purpose + "（仅专用网络、仅本地子网）"));
        }

        return rules;
    }

    /// <summary>The exact <c>netsh</c> arguments for creating one rule.</summary>
    public static IReadOnlyList<string> AddArguments(FirewallRule rule) =>
    [
        "advfirewall", "firewall", "add", "rule",
        "name=" + rule.Name,
        "dir=" + rule.Direction,
        "action=" + rule.Action,
        "program=" + rule.Program,
        "protocol=" + rule.Protocol,
        "localport=" + rule.LocalPort,
        "profile=" + rule.Profile,
        "remoteip=" + rule.RemoteAddress,
        "enable=yes",
        "description=" + rule.Description
    ];

    /// <summary>
    /// The exact <c>netsh</c> arguments for removing one rule.
    ///
    /// A rule is identified by the exact name its creator chose. LanDisk deletes only names it can
    /// prove it created, which is why older <c>LanDisk.Server.exe</c> rules from a hand-extracted
    /// 1.3.0 install are reported to the user instead of being removed behind their back.
    /// </summary>
    public static IReadOnlyList<string> DeleteArguments(FirewallRule rule) =>
    [
        "advfirewall", "firewall", "delete", "rule",
        "name=" + rule.Name
    ];

    public static string Describe(IEnumerable<FirewallRule> rules)
    {
        var sb = new StringBuilder();
        foreach (FirewallRule rule in rules)
        {
            sb.AppendLine($"{rule.Name}: dir={rule.Direction} action={rule.Action} " +
                          $"program={rule.Program} protocol={rule.Protocol} localport={rule.LocalPort} " +
                          $"profile={rule.Profile} remoteip={rule.RemoteAddress}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Invariants that must hold for every rule this product creates. Enforced by tests and by the
    /// elevated helper before it runs anything, so a future edit cannot quietly widen the exposure.
    /// </summary>
    public static IReadOnlyList<string> Violations(string installDir, IEnumerable<FirewallRule> rules)
    {
        var problems = new List<string>();
        string expectedProgram = SetupLayout.ShellPathFor(Path.GetFullPath(installDir));

        foreach (FirewallRule rule in rules)
        {
            if (!rule.Profile.Equals(PrivateProfile, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{rule.Name}: 只允许 Private 配置文件，实际 {rule.Profile}");
            if (!rule.RemoteAddress.Equals(LocalSubnet, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{rule.Name}: 只允许 LocalSubnet 远端，实际 {rule.RemoteAddress}");
            if (!rule.Program.Equals(expectedProgram, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{rule.Name}: 程序路径必须是安装目录中的 {SetupLayout.ShellExeName}，实际 {rule.Program}");
            if (rule.Direction != "in") problems.Add($"{rule.Name}: 只允许入站规则");
            if (rule.Action != "allow") problems.Add($"{rule.Name}: 只允许 allow 规则");
            if (rule.Protocol is not ("TCP" or "UDP")) problems.Add($"{rule.Name}: 协议只允许 TCP/UDP");
            if (rule.LocalPort == SetupLayout.AdminPort.ToString())
                problems.Add($"{rule.Name}: 管理端口 {SetupLayout.AdminPort} 绝不能对局域网开放");
            if (rule.LocalPort == "0" || rule.LocalPort.Contains('-')) problems.Add($"{rule.Name}: 不允许端口范围");
            if (!rule.Name.StartsWith(SetupLayout.FirewallRulePrefix, StringComparison.Ordinal))
                problems.Add($"{rule.Name}: 规则名必须以 {SetupLayout.FirewallRulePrefix} 开头，便于卸载时只删除自己的规则");
        }

        return problems;
    }
}
