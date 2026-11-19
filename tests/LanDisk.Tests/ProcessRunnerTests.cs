using System.Diagnostics;
using LanDisk.Core.Networking;

namespace LanDisk.Tests;

/// <summary>
/// FIREWALL14 — the bounded process runner the firewall self-check actually uses, and the tri-state
/// <c>Enabled</c> field.
///
/// <para><b>Why the runner needs its own tests.</b> The previous implementation read stdout, then stderr, then
/// waited for exit. Neither read had a timeout, so a child that never closed its output stream left the caller
/// blocked for ever and the 15-second wait was never reached; and reading the streams in sequence can deadlock
/// once the stderr pipe buffer fills. Those are properties of the <i>execution</i> code, so they are tested
/// against real child processes — not against a helper that only the test uses.</para>
/// </summary>
public sealed class ProcessRunnerTests
{
    /// <summary>Short budget for the cases that are supposed to end quickly.</summary>
    private const int Quick = 15000;

    private static ProcessRunResult RunCmd(string commandLine, int timeoutMs = Quick) =>
        BoundedProcessRunner.Run("cmd.exe", ["/c", commandLine], timeoutMs);

    [Fact]
    public void A_normal_command_completes_and_returns_its_output()
    {
        ProcessRunResult r = RunCmd("echo hello-runner");

        Assert.True(r.Completed, r.Failure);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("hello-runner", r.StdOut);
    }

    [Fact]
    public void A_non_zero_exit_is_reported_as_not_completed()
    {
        ProcessRunResult r = RunCmd("exit /b 7");

        Assert.False(r.Completed);
        Assert.Equal(7, r.ExitCode);
        Assert.Contains("退出码 7", r.Failure);
    }

    /// <summary>
    /// A process that never exits must be cut off at the deadline — not waited on for ever. This is the case
    /// the old ordering could not survive: <c>ReadToEnd</c> blocked first and the timeout never ran.
    /// </summary>
    [Fact]
    public void A_process_that_never_exits_is_cut_off_at_the_deadline()
    {
        var sw = Stopwatch.StartNew();
        ProcessRunResult r = BoundedProcessRunner.Run("cmd.exe", ["/c", "ping -n 60 127.0.0.1 >nul"], 2500);
        sw.Stop();

        Assert.False(r.Completed);
        Assert.Contains("超时", r.Failure);
        // 必须真的在时限附近返回，而不是等子进程自己结束（60 次 ping 约一分钟）
        Assert.True(sw.ElapsedMilliseconds < 20000, "超时后必须在有界时间内返回，实际 " + sw.ElapsedMilliseconds + " ms");
    }

    /// <summary>
    /// A process that stays alive without ever closing its output stream: the read tasks do not finish, so the
    /// combined wait must expire. This is exactly the "输出流不关闭" case.
    /// </summary>
    [Fact]
    public void A_stream_that_never_closes_still_times_out()
    {
        var sw = Stopwatch.StartNew();
        // 不回显任何内容且长期存活：stdout 不会关闭
        ProcessRunResult r = BoundedProcessRunner.Run("cmd.exe", ["/c", "ping -n 60 127.0.0.1"], 2500);
        sw.Stop();

        Assert.False(r.Completed);
        Assert.True(sw.ElapsedMilliseconds < 20000, "实际 " + sw.ElapsedMilliseconds + " ms");
    }

    /// <summary>Large stdout must not deadlock, and must be returned whole.</summary>
    [Fact]
    public void Large_stdout_is_consumed_without_deadlock()
    {
        // 约 40000 行 × 约 40 字节 ≈ 1.6 MB，远大于管道缓冲
        ProcessRunResult r = RunCmd("for /L %i in (1,1,40000) do @echo 0123456789012345678901234567890123456789", 60000);

        Assert.True(r.Completed, r.Failure);
        Assert.True(r.StdOut.Length > 1_000_000, "实际长度 " + r.StdOut.Length);
    }

    /// <summary>
    /// Large <b>stderr</b> while stdout stays quiet is the deadlock case: with sequential reads the child
    /// blocks on the full stderr pipe while we block on stdout.
    /// </summary>
    [Fact]
    public void Large_stderr_is_consumed_without_deadlock()
    {
        // 这一条 stdout 故意为空（内容全走 stderr），所以关掉"空输出算失败"——
        // 那个默认值是给 netsh 用的：它必须返回一份清单，空清单不能当作结果。
        ProcessRunResult r = BoundedProcessRunner.Run(
            "cmd.exe",
            ["/c", "for /L %i in (1,1,40000) do @echo 0123456789012345678901234567890123456789 1>&2"],
            60000,
            emptyOutputIsFailure: false);

        Assert.True(r.Completed, r.Failure);
        Assert.True(r.StdErr.Length > 1_000_000, "实际长度 " + r.StdErr.Length);
    }

    /// <summary>Interleaved output on both streams at once must also finish.</summary>
    [Fact]
    public void Both_streams_at_once_do_not_deadlock()
    {
        ProcessRunResult r = RunCmd(
            "for /L %i in (1,1,20000) do @(echo out-0123456789012345678901234567890123456789 & echo err-0123456789012345678901234567890123456789 1>&2)",
            60000);

        Assert.True(r.Completed, r.Failure);
        Assert.True(r.StdOut.Length > 100_000);
        Assert.True(r.StdErr.Length > 100_000);
    }

    [Fact]
    public void Empty_output_can_be_treated_as_a_failure_or_as_a_result()
    {
        Assert.False(RunCmd("exit /b 0").Completed);                                  // 默认：空输出算失败
        Assert.True(BoundedProcessRunner.Run("cmd.exe", ["/c", "exit /b 0"], Quick, emptyOutputIsFailure: false).Completed);
    }

    [Fact]
    public void A_missing_executable_is_reported_not_thrown()
    {
        ProcessRunResult r = BoundedProcessRunner.Run("definitely-not-a-real-program-xyz.exe", [], 2000);

        Assert.False(r.Completed);
        Assert.False(string.IsNullOrWhiteSpace(r.Failure));
    }
}

/// <summary>
/// FIREWALL15 — <c>Enabled</c> must be tri-state. A missing or unrecognised field is <b>cannot confirm</b>;
/// only an explicit "No" is "disabled".
/// </summary>
public sealed class FirewallEnabledStateTests
{
    private const string InstallDir = @"C:\Program Files\LanDisk";
    private static readonly string Shell = Path.Combine(InstallDir, "LanDisk.exe");

    private static string Listing(string? enabledLine, string extra = "")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Rule Name:                            LanDisk HTTPS (Private)");
        sb.AppendLine("----------------------------------------------------------------------");
        if (enabledLine is not null) sb.AppendLine("Enabled:                              " + enabledLine);
        sb.AppendLine("Direction:                            In");
        sb.AppendLine("Profiles:                             Private");
        sb.AppendLine("RemoteIP:                             LocalSubnet");
        sb.AppendLine("Protocol:                             TCP");
        sb.AppendLine("LocalPort:                            8892");
        sb.AppendLine("Program:                              " + Shell);
        sb.AppendLine("Action:                               Allow");
        sb.Append(extra);
        return sb.ToString();
    }

    private static FirewallRuleVerdict Https(string listing) =>
        FirewallRules.Evaluate(FirewallRules.Parse(listing), 8892, 8890, Shell)
            .Rules.First(r => r.Expected.LocalPort == 8892);

    [Fact]
    public void An_explicit_yes_is_enabled_and_passes()
    {
        FirewallRuleVerdict v = Https(Listing("Yes"));
        Assert.True(v.Ok, v.Verdict);
        Assert.Equal(true, FirewallRules.Parse(Listing("Yes")).First().Enabled);
    }

    [Fact]
    public void An_explicit_no_is_disabled()
    {
        FirewallRuleVerdict v = Https(Listing("No"));
        Assert.Equal("disabled", v.Verdict);
        Assert.Equal(false, FirewallRules.Parse(Listing("No")).First().Enabled);
    }

    /// <summary>A missing field must not be folded into "disabled" before being called UNKNOWN.</summary>
    [Fact]
    public void A_missing_enabled_field_is_unknown_not_disabled()
    {
        FirewallRuleState state = FirewallRules.Parse(Listing(null)).First();

        Assert.Null(state.Enabled);                 // 没有被折成 false
        Assert.False(state.HasAllJudgedFields);
        FirewallRuleVerdict v = Https(Listing(null));
        Assert.False(v.Ok);
        Assert.True(v.Unknown, "缺 Enabled 必须是 unknown，而不是 disabled");
        Assert.Contains("Enabled", v.Verdict);
    }

    [Fact]
    public void An_unrecognised_enabled_value_is_unknown_and_keeps_the_raw_text()
    {
        FirewallRuleState state = FirewallRules.Parse(Listing("Enabled?")).First();

        Assert.Null(state.Enabled);
        Assert.Equal("Enabled?", state.EnabledText);
        FirewallRuleVerdict v = Https(Listing("Enabled?"));
        Assert.False(v.Ok);
        Assert.True(v.Unknown);
        Assert.Contains("无法识别", v.Verdict);
        Assert.Contains("Enabled?", v.Verdict);
    }

    /// <summary>Localised output is understood where we can interpret it, and flagged where we cannot.</summary>
    [Fact]
    public void Localised_values_are_handled_explicitly()
    {
        Assert.Equal(true, FirewallRules.Parse(Listing("Yes")).First().Enabled);
        Assert.Equal(true, FirewallRules.Parse(Listing("yes")).First().Enabled);
        Assert.Equal(false, FirewallRules.Parse(Listing("No")).First().Enabled);
        Assert.Null(FirewallRules.Parse(Listing("Aktiv")).First().Enabled);
    }

    /// <summary>A second, disabled same-named rule cannot be hidden by a first, enabled one.</summary>
    [Fact]
    public void A_disabled_duplicate_cannot_hide_behind_an_enabled_one()
    {
        string two = Listing("Yes") + Listing("No");
        FirewallReport report = FirewallRules.Evaluate(FirewallRules.Parse(two), 8892, 8890, Shell);
        FirewallRuleVerdict v = report.Rules.First(r => r.Expected.LocalPort == 8892);

        Assert.False(v.Ok);
        Assert.Equal(2, v.Duplicates);
        Assert.Equal("disabled", v.Verdict);
    }
}
