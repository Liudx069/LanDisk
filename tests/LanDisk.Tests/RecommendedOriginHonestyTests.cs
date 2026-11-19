namespace LanDisk.Tests;

/// <summary>
/// NET18 — 推荐的 HTTPS 地址必须与**实际监听**一致，而不是与配置一致。
///
/// <para><b>真机缺陷 B。</b> 现场 LAN 与 HTTPS 都关着、实际监听只有 loopback，页面却仍然推荐
/// <c>https://192.168.2.2:53820</c>：那个地址上没有任何监听，手机必然连不上。原实现只看网卡读数，
/// 后来只看设置，而设置与监听之间还隔着一次重启 —— 两者都会说出一个连不上的地址。</para>
///
/// <para>规则：只有 BoundUrls 里存在**非 loopback 的 https** 监听时才允许推荐物理地址；否则退回稳定
/// 名称（HTTPS 已启用时）或明确"关闭"（HTTPS 未启用时）。</para>
/// </summary>
public sealed class RecommendedOriginHonestyTests : TestBase
{
    private (bool Lan, bool Https) Configure(bool lan, bool https)
    {
        (bool Lan, bool Https) before = (Host.Runtime.Settings.LanEnabled, Host.Runtime.Settings.HttpsEnabled);
        Host.Runtime.Settings.LanEnabled = lan;
        Host.Runtime.Settings.HttpsEnabled = https;
        return before;
    }

    private void Restore((bool Lan, bool Https) before)
    {
        Host.Runtime.Settings.LanEnabled = before.Lan;
        Host.Runtime.Settings.HttpsEnabled = before.Https;
    }

    private static bool IsPhysicalAddress(string? origin)
        => origin is not null && System.Text.RegularExpressions.Regex.IsMatch(origin, @"^https://\d+\.\d+\.\d+\.\d+:\d+$");

    private bool ActuallyServingLanHttps()
        => Host.Runtime.BoundUrls.Any(u =>
            u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !u.Contains("127.0.0.1", StringComparison.Ordinal) &&
            !u.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
            !u.Contains("[::]", StringComparison.Ordinal));

    /// <summary>HTTPS 关闭时不得推荐任何"可访问地址"。</summary>
    [Fact]
    public async Task When_https_is_off_nothing_reachable_is_recommended()
    {
        (bool, bool) before = Configure(lan: true, https: false);
        try
        {
            string? origin = Host.Runtime.Admin.RecommendedHttpsOrigin();
            Assert.Equal("off", Host.Runtime.Admin.RecommendedHostKind);
            Assert.Null(origin);
            Assert.False(IsPhysicalAddress(origin));
        }
        finally { Restore(before); }

        await Task.CompletedTask;
        Record("NET18-01", "HTTPS 关闭时不推荐任何地址（kind=off，origin=null）");
    }

    /// <summary>局域网访问关闭时同样不得推荐物理地址。</summary>
    [Fact]
    public async Task When_lan_is_off_no_physical_address_is_recommended()
    {
        (bool, bool) before = Configure(lan: false, https: true);
        try
        {
            string? origin = Host.Runtime.Admin.RecommendedHttpsOrigin();
            Assert.NotEqual("physical", Host.Runtime.Admin.RecommendedHostKind);
            Assert.False(IsPhysicalAddress(origin));
        }
        finally { Restore(before); }

        await Task.CompletedTask;
        Record("NET18-02", "局域网访问关闭时不推荐物理 HTTPS 地址");
    }

    /// <summary>
    /// 关键反例：配置说"开"，但本进程只绑了 loopback（等价于刚改完设置还没重启）。
    /// 此时**不得**推荐物理地址 —— 这正是真机上"推荐了却连不上"的成因。
    /// </summary>
    [Fact]
    public async Task Configured_but_not_actually_listening_never_yields_a_physical_address()
    {
        (bool, bool) before = Configure(lan: true, https: true);
        try
        {
            bool serving = ActuallyServingLanHttps();
            string? origin = Host.Runtime.Admin.RecommendedHttpsOrigin();
            string kind = Host.Runtime.Admin.RecommendedHostKind;

            if (!serving)
            {
                Assert.NotEqual("physical", kind);
                Assert.False(IsPhysicalAddress(origin),
                    "配置已开启但本进程没有非 loopback 的 https 监听，此时不得推荐物理地址（实际推荐：" + origin + "）");
            }
            else
            {
                // 真正在监听时，才允许（且应当）给出物理地址
                Assert.Equal("physical", kind);
                Assert.True(IsPhysicalAddress(origin));
            }
        }
        finally { Restore(before); }

        await Task.CompletedTask;
        Record("NET18-03", "配置为开但未真正监听时，绝不推荐物理地址（真机缺陷 B 的防回归）");
    }
}
