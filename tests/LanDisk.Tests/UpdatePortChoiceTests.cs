using LanDisk.Core.State;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// UPDATEPORT12 — the port an update restarts the host on.
///
/// <para>Found by running the update acceptance on non-default ports for the first time. The updater chose the
/// value stored in <c>settings.json</c> over the port the running host handed it, so a host started with
/// <c>--port 18891</c> came back on 8891; its own health check then looked at 18891, got a refusal, and rolled
/// the update back. An installation on non-default ports could never complete an update at all — and the
/// previous round's acceptance only ever used the default ports, which is why it went unnoticed.</para>
/// </summary>
public sealed class UpdatePortChoiceTests
{
    [Fact]
    public void The_live_port_wins_over_the_stored_one()
    {
        // The defect, stated as a test: live 18891, stored default 8891 -> 18891.
        Assert.Equal(18891, UpdatePortChoice.Resolve(0, liveFromHost: 18891, storedInSettings: 8891));
        Assert.True(UpdatePortChoice.MovedAwayFromStored(liveFromHost: 18891, storedInSettings: 8891));

        TestBase.Record("UPDATEPORT12-01",
            "还原缺陷场景：宿主实时端口 18891、settings.json 里是默认 8891 → 解析结果为 18891（修复前会得到 8891，服务被搬到错误端口，更新随即自检失败并回滚）");
    }

    [Fact]
    public void An_explicit_override_wins_over_everything()
    {
        Assert.Equal(19999, UpdatePortChoice.Resolve(19999, liveFromHost: 18891, storedInSettings: 8891));
        Assert.Equal(19999, UpdatePortChoice.Resolve(19999, liveFromHost: 0, storedInSettings: 0));

        TestBase.Record("UPDATEPORT12-02",
            "显式覆盖（内部重试等路径传入的端口）优先于实时值与存储值：优先级为 显式覆盖 > 宿主实时值 > settings.json");
    }

    [Fact]
    public void The_stored_value_is_only_a_fallback()
    {
        // Nothing live was handed over: an older caller that does not pass --http-port still works.
        Assert.Equal(8891, UpdatePortChoice.Resolve(0, liveFromHost: 0, storedInSettings: 8891));
        // Nothing anywhere: the caller decides what to do with 0 rather than binding an arbitrary port.
        Assert.Equal(0, UpdatePortChoice.Resolve(0, liveFromHost: 0, storedInSettings: 0));
        // A negative or nonsense stored value is treated as absent.
        Assert.Equal(0, UpdatePortChoice.Resolve(0, liveFromHost: 0, storedInSettings: -1));

        TestBase.Record("UPDATEPORT12-03",
            "存储值只在没有任何实时值时兜底（旧调用方不传 --http-port 仍可用）；两处都没有时返回 0，而不是绑定一个任意端口；负数按缺省处理");
    }

    [Fact]
    public void Https_absent_from_the_host_falls_back_without_inventing_one()
    {
        // The host passes 0 when it has no HTTPS listener; the stored value is then legitimate.
        Assert.Equal(8892, UpdatePortChoice.Resolve(0, liveFromHost: 0, storedInSettings: 8892));
        // And when neither exists, the caller sees 0 and skips --https-port, so no listener is invented.
        Assert.Equal(0, UpdatePortChoice.Resolve(0, liveFromHost: 0, storedInSettings: 0));

        TestBase.Record("UPDATEPORT12-04",
            "HTTPS：宿主没有 HTTPS 监听时传 0，此时回落到存储值 8892 是正确行为；两处都没有则得到 0，调用方据此不加 --https-port，不会凭空造出一个监听端口");
    }
/// <summary>
    /// The structural half of the gate: it is not enough for the precedence to be correct if a call site can
    /// bypass it. There are exactly two places that restart the host — the forward path and the rollback path —
    /// and both must route the port through <see cref="UpdatePortChoice.Resolve"/> rather than computing one
    /// from the stored settings and passing it as an explicit override, which outranks everything.
    ///
    /// <para>This is written because the rollback path had exactly that bug and the unit-level precedence test
    /// could not see it: the function was right, the second caller did not use it.</para>
    /// </summary>
    [Fact]
    public void Both_restart_paths_go_through_the_choice()
    {
        string path = Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Updater/Program.cs");
        string[] lines = File.ReadAllLines(path);

        var offEnds = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.Contains("ReadConfiguredPort(", StringComparison.Ordinal)
                && !line.Contains("ReadConfiguredHttpsPort(", StringComparison.Ordinal)) continue;
            if (line.Contains("private static int ReadConfigured", StringComparison.Ordinal)) continue;   // the definitions

            // Accept only the fallback argument of UpdatePortChoice.Resolve, i.e. a line whose preceding
            // non-empty line belongs to a Resolve( call.
            string previous = i > 0 ? lines[i - 1] : string.Empty;
            bool insideResolve = line.Contains("UpdatePortChoice.Resolve(", StringComparison.Ordinal)
                                 || previous.Contains("UpdatePortChoice.Resolve(", StringComparison.Ordinal);
            // A second line of arguments can follow Resolve( two lines up; walk back a little.
            for (int back = 2; back <= 4 && !insideResolve && i - back >= 0; back++)
                insideResolve = lines[i - back].Contains("UpdatePortChoice.Resolve(", StringComparison.Ordinal);

            if (!insideResolve) offEnds.Add($"  line {i + 1}: {line.Trim()}");
        }

        Assert.Empty(offEnds);
        Assert.Equal(2, CountOccurrences(lines, "UpdatePortChoice.Resolve(") >= 2 ? 2 : 0);

        TestBase.Record("UPDATEPORT12-05",
            "更新程序里只有两条重启路径（正向应用后、回滚后），两处的端口都必须经 UpdatePortChoice.Resolve 取值；任何直接用 ReadConfiguredPort 的端口都会让存储值越过实时值，正向路径与回滚路径各出现过一次这个错误");
    }

    /// <summary>
    /// The update request must be told the ports the host is actually bound to, never the configured value.
    ///
    /// <para>A configured port of 0 means "pick a free port" — a supported mode, though the admin page refuses
    /// to save 0, so it is reachable only through the command line or a hand-edited settings file. The host
    /// resolves it and binds real ports while <c>Settings.HttpPort</c> stays 0. Passing that 0 on tells the
    /// updater to restore an endpoint that was never in use: the restarted instance resolves 0 to a
    /// different free port, and the health check waits on port 0. Verified against the frozen server started
    /// with <c>--port 0 --https-port 0</c>: listeners 127.0.0.1:60600, [::1]:60600, 127.0.0.1:60601, and no
    /// settings.json written.</para>
    /// </summary>
    [Fact]
    public void The_update_request_is_given_the_bound_ports_not_the_configured_ones()
    {
        string[] sites =
        [
            Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Endpoints/UpdateEndpoints.cs"),
            Path.Combine(TestPaths.RepoRoot, "src/LanDisk.App/Pages2.cs")
        ];

        var offenders = new List<string>();
        foreach (string site in sites)
        {
            Assert.True(File.Exists(site), site + " 不存在");
            string[] lines = File.ReadAllLines(site);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("RequestApply(", StringComparison.Ordinal)
                    && lines[i].Contains("Settings.HttpPort", StringComparison.Ordinal))
                    offenders.Add($"  {Path.GetFileName(site)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.Empty(offenders);
        TestBase.Record("UPDATEPORT12-06",
            "更新请求必须使用宿主实际绑定的端口（BoundHttpPort/BoundHttpsPort），不得使用配置值；配置值 0 的含义是「挑一个空闲端口」，传给更新程序会让重启后的实例落到另一个端口、健康检查却探测 0");
    }

    private static int CountOccurrences(string[] lines, string needle)
    {
        int count = 0;
        foreach (string line in lines)
            if (line.Contains(needle, StringComparison.Ordinal)) count++;
        return count;
    }
}