using Wpf.Ui.Controls;

namespace LanDisk.App;

/// <summary>One entry in the left navigation rail: the title, the page type it builds, its icon and the
/// group it belongs to.</summary>
internal sealed record PageDescriptor(string Title, Type PageType, SymbolRegular Icon, string Group)
{
    public NativePage Create() => (NativePage)Activator.CreateInstance(PageType)!;
}

/// <summary>
/// The single source of truth for which pages the product has.
///
/// <para><b>Why this exists.</b> The navigation rail and the UI capture used to keep their own, separate
/// hand-written lists of page titles. They drifted: the rail was reorganised (共享 → 文件与共享, a new
/// 运营看板 page, HTTPS / 安全 → HTTPS 与安全) while the capture still had the old names, so the capture
/// silently rendered the wrong pages — or, worse, fell through to the previous page and still wrote a PASS
/// row. One list cannot drift; the rail, the tray and the capture all read from here, and a page that is
/// added to the rail but not captured makes the capture fail (see <see cref="UiCapture"/>).</para>
/// </summary>
internal static class PageCatalog
{
    public static IReadOnlyList<PageDescriptor> All { get; } =
    [
        new("概览", typeof(OverviewPage), SymbolRegular.Home24, "概览"),
        new("运营看板", typeof(OperationsPage), SymbolRegular.DataTrending24, "概览"),

        new("文件与共享", typeof(SharesPage), SymbolRegular.Folder24, "文件与共享"),
        new("回收站", typeof(RecyclePage), SymbolRegular.Delete24, "文件与共享"),

        new("设备", typeof(DevicesPage), SymbolRegular.Phone24, "连接设备"),
        new("连接 iPhone", typeof(IphonePage), SymbolRegular.QrCode24, "连接设备"),

        new("网络", typeof(NetworkPage), SymbolRegular.Globe24, "网络与安全"),
        new("HTTPS 与安全", typeof(HttpsPage), SymbolRegular.ShieldLock24, "网络与安全"),

        new("系统更新", typeof(UpdatePage), SymbolRegular.ArrowDownload24, "系统"),
        new("自检", typeof(SelfCheckPage), SymbolRegular.HeartPulse24, "系统"),
        new("日志", typeof(LogsPage), SymbolRegular.TextBulletListSquare24, "系统"),

        // 扩展：安装与批准（扩展程序）以及执行记录与日志（任务与命令）。扩展代码从不在本进程里加载。
        new("扩展程序", typeof(ExtensionsPage), SymbolRegular.PuzzlePiece24, "扩展"),
        new("任务与命令", typeof(TasksPage), SymbolRegular.TaskListSquareLtr24, "扩展"),

        new("设置", typeof(SettingsPage), SymbolRegular.Settings24, "设置")
    ];

    /// <summary>Looks a page up by its exact title (the titles are what the rail and the tray use).</summary>
    public static PageDescriptor? Find(string? title)
        => All.FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.Ordinal));

    /// <summary>The pages in display order, grouped. Used by the rail.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<PageDescriptor> Pages)> Groups
        => All.GroupBy(p => p.Group).Select(g => (g.Key, (IReadOnlyList<PageDescriptor>)g.ToList())).ToList();
}
