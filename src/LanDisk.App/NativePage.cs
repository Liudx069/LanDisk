using System.Windows;
using System.Windows.Controls;
using LanDisk.Server.Services;

namespace LanDisk.App;

/// <summary>
/// Base for the native pages.
///
/// <para>A page builds its visual tree once and refreshes the data on demand, so navigating back to a
/// page is instant and the data is never older than the last refresh. Every page reads through the
/// window's runtime and acts through <see cref="AdminOperations"/> or the service that owns the
/// operation — the UI never writes a state file itself.</para>
/// </summary>
internal abstract class NativePage
{
    protected static LanDiskRuntime Runtime
        => App.Instance.Host?.Runtime ?? throw new InvalidOperationException("服务尚未就绪。");

    protected StackPanel Root { get; } = new();

    /// <summary>Runs on the UI thread; build the tree and return it.</summary>
    public abstract UIElement Build();

    /// <summary>Runs whenever the page is shown or the user asks for a refresh.</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>
    /// Called when this page becomes the visible one. A page that has to keep watching the server (the
    /// iPhone wizard waiting for the phone to trust the certificate) starts its timer here rather than
    /// running in the background forever.
    /// </summary>
    public virtual void OnShown() { }

    /// <summary>Called when another page replaces this one.</summary>
    public virtual void OnHidden() { }

    protected void Show(string? error = null, string? notice = null)
    {
        // A page that failed to read is told so in place, rather than silently showing stale values.
        if (error is not null) Root.Children.Insert(0, Ui.Notice(error, Wpf.Ui.Controls.InfoBarSeverity.Error));
        if (notice is not null) Root.Children.Insert(0, Ui.Notice(notice, Wpf.Ui.Controls.InfoBarSeverity.Success));
    }

    /// <summary>Wraps a refresh so a failure is reported instead of crashing the window.</summary>
    protected static async Task Guarded(Action<string> report, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            report(ex.Message);
        }
    }
}
