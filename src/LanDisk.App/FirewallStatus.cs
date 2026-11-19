using System.Diagnostics;
using System.IO;
using LanDisk.Core.Networking;

namespace LanDisk.App;

/// <summary>
/// Reads the real Windows Firewall state for the rules LanDisk needs, and repairs them.
///
/// <para><b>What this replaces.</b> The self-check printed "需要放行 TCP 8892 与 UDP 5353（专用网络、本地子网）"
/// as a constant. It never asked Windows anything, so a machine whose rules had never been created — the
/// Owner's, after a 1.7.1 install — showed the same "needs confirming" line for ever, with no way to act on
/// it. A check that cannot fail is not a check.</para>
///
/// <para><b>Reading is unprivileged, writing is not.</b> <c>show rule</c> needs no elevation, so the check
/// itself is silent and instant. Creating a rule does, so repair re-launches the installer with
/// <c>--elevated-firewall-add</c> through <see cref="NativeDialogs.RunElevated"/>: one explicit UAC prompt for
/// the fixed argument list the elevated helper accepts, and nothing else. No browser is opened, and the app
/// never asks for elevation just to look.</para>
///
/// <para><b>Scope.</b> The only <c>netsh</c> verb reachable from here is the read-only
/// <c>advfirewall firewall show rule</c> query. Adding and deleting rules stays in the installer, which is why
/// the shell-invariant gates can still assert that nothing else in the product talks to <c>netsh</c> and that
/// no routing, DNS or VPN configuration is ever written.</para>
/// </summary>
internal static class FirewallStatus
{
    /// <summary>Total budget for one read-only query: reading both streams <i>and</i> the process ending.</summary>
    private const int QueryTimeoutMilliseconds = 15000;

    /// <summary>The installer that knows how to add the rules; it lives next to the shell.</summary>
    internal static string? InstallerPath
    {
        get
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "LanDisk-Setup.exe");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    /// <summary>
    /// Runs the read-only query and evaluates it. Never throws.
    ///
    /// <para>A query that failed, timed out or exited non-zero is reported as <b>cannot confirm</b>, not as
    /// "rules are missing". Those two findings look identical in the UI today and lead to opposite actions:
    /// re-adding rules cannot fix a machine whose firewall query does not work.</para>
    /// </summary>
    internal static FirewallReport Check(int httpsPort, int bootstrapPort)
    {
        string expectedProgram = Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "LanDisk.exe");

        string? output = RunNetsh(["advfirewall", "firewall", "show", "rule", "name=all"], out string failure);
        if (output is null)
        {
            return FirewallRules.QueryFailure(failure, httpsPort, bootstrapPort);
        }

        return FirewallRules.Evaluate(FirewallRules.Parse(output), httpsPort, bootstrapPort, expectedProgram);
    }

    /// <summary>
    /// Asks Windows to create the rules through one UAC prompt. Returns true when the helper reported success;
    /// the caller re-runs <see cref="Check"/> afterwards rather than assuming the rules now exist.
    /// </summary>
    internal static (bool Ok, string Message) Repair()
    {
        string? installer = InstallerPath;
        if (installer is null)
            return (false, "没有找到 LanDisk-Setup.exe，无法修复防火墙规则。请重新运行安装程序。");

        // Elevation goes through the shell's one documented shell-execute helper, so the invariant "the shell
        // hands a string to the shell from exactly one place" stays true and stays checkable.
        bool ok = NativeDialogs.RunElevated(
            installer,
            ["--elevated-firewall-add", "--install-dir", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)],
            out string message);

        return ok
            ? (true, "防火墙规则已更新，正在重新自检。")
            : (false, message);
    }

    /// <summary>
    /// The read-only query. Only the <c>show rule</c> verb is ever run from the product shell.
    ///
    /// Returns <c>null</c> (with a reason) when the query could not be trusted: the process did not start,
    /// did not exit within the timeout, or exited non-zero. A partial read from a timed-out process is not a
    /// listing and must never be parsed as one.
    /// </summary>
    private static string? RunNetsh(IReadOnlyList<string> arguments, out string failure)
    {
        failure = string.Empty;
        // 执行与超时全部交给 Core 的 BoundedProcessRunner：它并行消费两个流，用**一个**总时限覆盖
        // "读完输出 + 进程退出"，超时只终止自己创建的那个进程，清理等待也有界。
        // 这里保留的只是"把结果翻译成防火墙自检要的结论"。
        ProcessRunResult run = BoundedProcessRunner.Run("netsh", arguments, QueryTimeoutMilliseconds);
        if (!run.Completed)
        {
            failure = run.Failure;
            return null;
        }

        return run.StdOut;
    }
}
