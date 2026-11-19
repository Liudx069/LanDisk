using System.Diagnostics;
using System.Security.Principal;

namespace LanDisk.Setup;

/// <summary>
/// The only part of the installer that runs with administrator rights, and the reason it is a separate
/// code path rather than a flag on the main one.
///
/// <para><see cref="FirewallHelper"/> is reached by re-launching this same executable with
/// <c>--elevated-firewall-add</c> or <c>--elevated-firewall-remove</c>. Those two modes rebuild the
/// rule list themselves from <see cref="FirewallPlan"/> and the canonical install directory; they
/// accept no rule text, no path and no command from the caller. So even though Windows hands the
/// process full administrator rights, the set of things it can do is fixed at two, and both are
/// auditable by reading the plan.</para>
///
/// <para>Nothing else in the product ever asks for elevation. Installing into
/// <c>%LOCALAPPDATA%</c>, creating shortcuts, registering the uninstall entry and updating in place all
/// work as the ordinary user.</para>
/// </summary>
public static class FirewallHelper
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Launches this executable elevated for one fixed action. Returns the exit code, or null on refusal.</summary>
    public static int? RunElevated(string executablePath, bool add)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas", // the UAC prompt
            Arguments = add ? "--elevated-firewall-add" : "--elevated-firewall-remove"
        };

        try
        {
            using Process? process = Process.Start(info);
            if (process is null) return null;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user declined the UAC prompt. That is a legitimate answer, not an error: the product
            // still works on this machine, phones on the LAN just cannot reach it until a rule exists.
            return null;
        }
    }

    /// <summary>Creates exactly the rules in the plan. Returns the number applied.</summary>
    public static int AddRules(string installDir, Action<string>? log = null)
    {
        IReadOnlyList<FirewallRule> rules = FirewallPlan.Rules(installDir);

        IReadOnlyList<string> problems = FirewallPlan.Violations(installDir, rules);
        if (problems.Count > 0)
        {
            foreach (string problem in problems) log?.Invoke("拒绝应用防火墙规则：" + problem);
            return 0;
        }

        int applied = 0;
        foreach (FirewallRule rule in rules)
        {
            // Idempotent by construction: delete-then-add, so a repair install cannot accumulate
            // duplicate rules with the same name.
            RunNetsh(FirewallPlan.DeleteArguments(rule), log);
            if (RunNetsh(FirewallPlan.AddArguments(rule), log) == 0)
            {
                applied++;
                log?.Invoke("已添加防火墙规则：" + rule.Name);
            }
        }

        return applied;
    }

    /// <summary>Removes only the rules this installer creates.</summary>
    public static int RemoveRules(string installDir, Action<string>? log = null)
    {
        int removed = 0;
        foreach (FirewallRule rule in FirewallPlan.Rules(installDir))
        {
            if (RunNetsh(FirewallPlan.DeleteArguments(rule), log) == 0)
            {
                removed++;
                log?.Invoke("已删除防火墙规则：" + rule.Name);
            }
        }

        return removed;
    }

    /// <summary>
    /// Names of LanDisk-related rules currently present, so the wizard can tell the user about rules a
    /// hand-extracted 1.3.0 install created (those point at LanDisk.Server.exe and are not ours).
    /// </summary>
    public static IReadOnlyList<string> ExistingRuleNames(string? installDir = null)
    {
        var names = new List<string>();
        string output = Capture("advfirewall", ["firewall", "show", "rule", "name=all"]);

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase)) continue;

            string name = trimmed["Rule Name:".Length..].Trim();
            if (name.Contains("LanDisk", StringComparison.OrdinalIgnoreCase)) names.Add(name);
        }

        _ = installDir;
        return names;
    }

    private static int RunNetsh(IReadOnlyList<string> arguments, Action<string>? log)
    {
        try
        {
            var info = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            using Process? process = Process.Start(info);
            if (process is null) return -1;

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                log?.Invoke($"netsh 返回 {process.ExitCode}：{detail.Trim()}");
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            log?.Invoke("运行 netsh 失败：" + ex.Message);
            return -1;
        }
    }

    private static string Capture(string file, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            using Process? process = Process.Start(info);
            if (process is null) return string.Empty;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
