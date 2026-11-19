using System.Drawing;
using System.Windows.Forms;

namespace LanDisk.Setup;

/// <summary>
/// The uninstall window.
///
/// <para>Two things are deliberate here. Keeping the user's settings, paired phones and certificates is
/// the default — losing them is a checkbox a person has to tick on purpose. And the checkbox says what
/// it will not do: the folders that were shared with the phone live outside LanDisk's data directory
/// and are never part of any deletion path in this program.</para>
/// </summary>
public sealed class UninstallForm : Form
{
    private readonly SetupOptions _options;
    private readonly ListBox _progress = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly CheckBox _removeData = new()
    {
        Text = "同时删除 LanDisk 设置、配对和证书",
        AutoSize = true,
        Checked = false
    };

    public UninstallForm(SetupOptions options)
    {
        _options = options;

        Text = "卸载 LanDisk";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 440);
        MinimumSize = new Size(560, 420);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;
        MaximizeBox = false;

        var title = new Label
        {
            Text = "卸载 LanDisk",
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(26, 22)
        };

        var what = new Label
        {
            Text = "将执行：\n" +
                   "  · 退出正在运行的 LanDisk\n" +
                   "  · 删除程序文件（" + options.InstallDir + "）\n" +
                   "  · 删除 LanDisk 自己创建的 Windows 防火墙规则\n" +
                   "  · 删除开始菜单与桌面快捷方式、开机启动项、卸载入口\n\n" +
                   "已配对的 iPhone、共享设置、收藏与更新记录都保存在 " + options.DataDir + "，默认予以保留：\n" +
                   "重新安装后不需要重新配对。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Location = new Point(29, 70)
        };

        _removeData.Location = new Point(32, 250);
        _removeData.ForeColor = Color.FromArgb(150, 30, 30);

        var warning = new Label
        {
            Text = "该选项只删除 LanDisk 自己的数据目录，绝不会删除任何共享硬盘上的真实文件（例如 E:\\BF）。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Location = new Point(52, 276),
            ForeColor = Color.FromArgb(100, 100, 100)
        };

        var uninstall = new Button
        {
            Text = "卸载",
            AutoSize = true,
            Padding = new Padding(18, 9, 18, 9),
            Location = new Point(340, 360)
        };
        uninstall.Click += async (_, _) => await RunAsync().ConfigureAwait(true);

        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            Padding = new Padding(14, 9, 14, 9),
            Location = new Point(250, 360)
        };
        cancel.Click += (_, _) => Close();

        Controls.AddRange([title, what, _removeData, warning, uninstall, cancel]);
    }

    private async Task RunAsync()
    {
        _options.RemoveUserData = _removeData.Checked;

        foreach (Control control in Controls) control.Enabled = false;
        Controls.Add(_progress);

        var engine = new SetupEngine(_options, Append);
        SetupOutcome outcome = await engine.UninstallAsync().ConfigureAwait(true);

        MessageBox.Show(this, outcome.Summary + Environment.NewLine + Environment.NewLine +
                              string.Join(Environment.NewLine, outcome.Details),
            "LanDisk", MessageBoxButtons.OK, MessageBoxIcon.Information);

        Close();
    }

    private void Append(string message)
    {
        if (_progress.InvokeRequired)
        {
            _progress.BeginInvoke(() => _progress.Items.Add(message));
            return;
        }

        _progress.Items.Add(message);
    }
}
