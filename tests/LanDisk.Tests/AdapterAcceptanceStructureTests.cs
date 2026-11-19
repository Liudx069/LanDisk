using LanDisk.Core.Networking;

namespace LanDisk.Tests;

/// <summary>
/// 网络页"哪些网卡可用"必须来自结构化字段，不能来自显示文案（原 WLAN12-03 门禁，随 WiFi 功能移除改为本类断言）。
///
/// <para><b>真机缺陷 A。</b> 页面曾经用 <c>a.判定.StartsWith("使用")</c> 反推数据，而 Presenter 实际产出的
/// 文案是「用于手机访问」（以「用」开头，不是「使用」）。于是 accepted 永远为空：网络页一直显示
/// "没有可用物理网卡"，而同一页的「推荐访问地址」却给出 <c>https://192.168.2.2:…</c> —— 页面自相矛盾，
/// 也正是 Owner 现场看到的那一对。</para>
///
/// <para>这里断言的是**行为**，不是源码文本：把标签文字整批换掉，结果必须完全不变。</para>
/// </summary>
public sealed class AdapterAcceptanceStructureTests
{
    private static AdapterRow Row(string name, string address, string label, bool accepted)
        => new(name, address, label, "夹具说明", accepted);

    /// <summary>等价现场：Ethernet 192.168.2.2 可用；Hyper-V 172.24.0.1 与 loopback 被排除。</summary>
    private static List<AdapterRow> FieldFixture(string acceptedLabel, string excludedLabel) =>
    [
        Row("Ethernet", "192.168.2.2", acceptedLabel, true),
        Row("vEthernet (Default Switch)", "172.24.0.1", excludedLabel, false),
        Row("Loopback", "127.0.0.1", excludedLabel, false),
    ];

    [Fact]
    public void The_physical_ethernet_is_accepted_and_virtual_adapters_are_not()
    {
        (List<AdapterRow> accepted, List<AdapterRow> excluded) =
            AdapterRows.Split(FieldFixture("用于手机访问", "已排除"));

        Assert.Single(accepted);
        Assert.Equal("192.168.2.2", accepted[0].地址);
        Assert.Equal(2, excluded.Count);
        Assert.DoesNotContain(excluded, a => a.地址 == "192.168.2.2");
    }

    /// <summary>
    /// 核心防回归：把显示文案换成**完全不同**的文字，判定结果必须一模一样。
    /// 旧实现（StartsWith("使用")）在这一步会直接崩掉 —— 它本来就永远匹配不上。
    /// </summary>
    [Theory]
    [InlineData("用于手机访问", "已排除")]              // Presenter 当前文案
    [InlineData("USE FOR PHONE", "SKIPPED")]          // 纯英文
    [InlineData("可用", "不可用")]                     // 换一组中文
    [InlineData("", "")]                              // 完全空白
    [InlineData("zzz", "yyy")]                        // 无关字符串
    public void Changing_the_display_text_never_changes_the_decision(string acceptedLabel, string excludedLabel)
    {
        (List<AdapterRow> accepted, List<AdapterRow> excluded) =
            AdapterRows.Split(FieldFixture(acceptedLabel, excludedLabel));

        Assert.Single(accepted);
        Assert.Equal("192.168.2.2", accepted[0].地址);
        Assert.Equal(2, excluded.Count);
    }

    /// <summary>
    /// 直接钉住旧缺陷的形态：即便文案恰好以「使用」开头，判定也不得再依赖它；
    /// 反之文案是「用于手机访问」时也必须被接受。
    /// </summary>
    [Fact]
    public void The_old_prefix_would_have_failed_and_now_does_not()
    {
        // 旧规则：StartsWith("使用")
        Assert.False("用于手机访问".StartsWith("使用", StringComparison.Ordinal));

        (List<AdapterRow> accepted, _) = AdapterRows.Split(FieldFixture("用于手机访问", "已排除"));
        Assert.Single(accepted);   // 旧实现这里是 0，因此页面显示"没有可用物理网卡"
    }

    /// <summary>空集合不得抛异常，也不得产生任何"可用"结论。</summary>
    [Fact]
    public void An_empty_adapter_list_yields_nothing_accepted()
    {
        (List<AdapterRow> accepted, List<AdapterRow> excluded) = AdapterRows.Split([]);
        Assert.Empty(accepted);
        Assert.Empty(excluded);
    }
}
