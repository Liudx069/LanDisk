namespace LanDisk.App;

/// <summary>Command line the shell understands; everything else is forwarded to the web host.</summary>
internal sealed class ShellOptions
{
    public bool StartMinimized { get; init; }
    public bool FromUpdate { get; init; }

    /// <summary>
    /// Page to open on start, from <c>--page "连接 iPhone"</c>.
    ///
    /// <para>The installer uses this so its "连接 iPhone" button can bring up the product's own wizard in the
    /// native window. Before this existed, that button opened a browser on the engineering administration
    /// page, which is how a normal install ended up in /admin.</para>
    /// </summary>
    public string? Page { get; init; }

    public static ShellOptions Parse(string[] args)
    {
        bool fromUpdate = args.Any(a => a.Equals("--from-update", StringComparison.OrdinalIgnoreCase));
        bool startMinimized = fromUpdate
                              || args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase))
                              || args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                              || args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        string? page = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--page", StringComparison.OrdinalIgnoreCase))
            {
                page = args[i + 1];
                break;
            }
        }

        return new ShellOptions { StartMinimized = startMinimized, FromUpdate = fromUpdate, Page = page };
    }
}
