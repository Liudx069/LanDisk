namespace LanDisk.Core.Networking;

/// <summary>
/// One row of the network page's adapter list. <paramref name="判定"/> and <paramref name="说明"/> are
/// display text only; <paramref name="接受"/> is the decision.
/// </summary>
/// <param name="网卡">Adapter name as Windows reports it.</param>
/// <param name="地址">IPv4 address, or a dash when the adapter has none.</param>
/// <param name="判定">Display label. Never used to decide anything.</param>
/// <param name="说明">Why this adapter is or is not usable. Display only.</param>
/// <param name="接受">True when the product may use this adapter for phone access.</param>
public sealed record AdapterRow(string 网卡, string 地址, string 判定, string 说明, bool 接受);

/// <summary>
/// The rule that decides which adapters the network page presents as usable.
///
/// <para><b>Why this lives in the core and not in the page.</b> The page used to split the list with
/// <c>a.判定.StartsWith("使用")</c> — a decision read out of the <i>display text</i>. The presenter emits
/// 「用于手机访问」, which starts with 「用」 rather than 「使用」, so the accepted list was empty on every
/// machine: the network page said "没有可用物理网卡" while the same page recommended
/// <c>https://192.168.2.2:…</c>. Keeping the rule here means it can be exercised directly, and a test can
/// replace every label with unrelated text and prove the outcome does not move.</para>
/// </summary>
public static class AdapterRows
{
    public static (List<AdapterRow> Accepted, List<AdapterRow> Excluded) Split(IReadOnlyList<AdapterRow> rows)
        => ([.. rows.Where(r => r.接受)], [.. rows.Where(r => !r.接受)]);
}
