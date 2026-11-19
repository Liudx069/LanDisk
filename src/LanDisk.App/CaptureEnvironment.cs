using System.IO;
using System.Text.Json;
using LanDisk.Core.Configuration;
using LanDisk.Core.Networking;

namespace LanDisk.App;

/// <summary>
/// Gives an evidence run its own data root and its own ports.
///
/// <para><b>Why this exists.</b> The UI capture used to start the shell's server with the user's own data
/// root and the fixed ports 8891/8892. On a machine where the Owner actually has LanDisk open — the normal
/// case — the ports were taken, the server refused to start, and the capture wrote nothing while appearing to
/// succeed. Worse, it was one bad line away from writing into real user state.</para>
///
/// <para><b>What it does instead.</b> A scratch directory under the system temp folder, a
/// <c>settings.json</c> with two ports that are free right now, and LAN off: the capture cannot collide with
/// the running product, cannot be affected by it, and cannot read or write the Owner's devices, shares,
/// certificates, theme or recycle bin.</para>
/// </summary>
internal static class CaptureEnvironment
{
    /// <summary>Rewrites the command line so the capture runs against a scratch root, and prepares that root.</summary>
    public static string[] Prepare(string[] args, out string dataRoot)
    {
        dataRoot = Path.Combine(Path.GetTempPath(), "landisk-ui-capture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataRoot);

        int http = FreePort(8900);
        int https = FreePort(http + 1);

        var settings = new AppSettings
        {
            HttpPort = http,
            HttpsPort = https,
            LanEnabled = false,
            HttpsEnabled = false,
            // The capture only needs the pages to render; discovery and TLS are irrelevant to it and would
            // only add a reason for the run to fail on a machine with an unusual network.
            AllowedCidr = string.Empty
        };
        File.WriteAllText(
            Path.Combine(dataRoot, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

        SeedHostileContent(dataRoot);

        List<string> rewritten = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) { i++; continue; }
            if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal)) continue;
            rewritten.Add(args[i]);
        }
        rewritten.Add("--data-dir");
        rewritten.Add(dataRoot);
        return [.. rewritten];
    }

    private static int FreePort(int start)
    {
        for (int port = start; port < start + 400; port++)
        {
            if (PortPreflight.IsPortFree(port)) return port;
        }
        throw new InvalidOperationException("找不到空闲端口用于截图运行");
    }

    /// <summary>
    /// Puts the awkward-but-real content into the scratch root.
    ///
    /// <para><b>Why.</b> An empty data root captures an empty extensions page, and the Owner's report was about
    /// that page *with content*: a long package name, a long publisher and a command list that runs off the
    /// side. A capture of an empty page proves nothing about any of it, so the longest plausible values are
    /// seeded here and every page in the matrix is rendered against them.</para>
    /// </summary>
    private static void SeedHostileContent(string dataRoot)
    {
        var commands = new[]
        {
            new
            {
                id = "collect-diagnostics-with-a-deliberately-long-identifier",
                name = "收集诊断信息（名称同样刻意很长，用来验证溢出与省略）",
                description = "导出最近的日志、自检结论与网络读数；这一行说明同样开得很长，"
                              + "用来确认表格不会把整行撑破，也不会把动作按钮挤到屏幕外面去。",
                argumentsSchema = (object?)null
            },
            new
            {
                id = "greet",
                name = "打招呼",
                description = "回显一个名字。",
                argumentsSchema = (object?)new { type = "object", properties = new { echo = new { type = "string" } } }
            }
        };

        // Declared as object[] on purpose: the two entries carry different command arrays, and letting the
        // compiler infer a single anonymous type here is what CS0826 is complaining about.
        var packages = new object[]
        {
            new
            {
                id = "com.example.landisk.diagnostics.enterprise.edition.preview",
                name = "LanDisk 企业版诊断与合规报告扩展（预览版，名称刻意很长用于验证溢出）",
                version = "1.2.3-preview.4+build.20250101",
                publisher = "某家名称同样非常长的第三方发布者有限责任公司（Example Diagnostics and Compliance GmbH）",
                entrypoint = "bin/diagnostics.exe",
                commands,
                packageSha256 = new string('a', 64),
                packageBytes = 123456L,
                // The tab the Owner was looking at: an uploaded package waiting for approval, with a long name.
                // The store reads enums as numbers (there is no JsonStringEnumConverter), so these are the
                // values the product itself writes: 0 = 待批准, 1 = 已批准.
                state = 0,
                approvedBy = (string?)null,
                approvedAt = (DateTimeOffset?)null,
                extractRoot = (string?)null,
                uploadedAt = DateTimeOffset.Now,
                timeoutSeconds = 30
            },
            new
            {
                id = "com.example.landisk.greet",
                // The default tab is 已安装, so the long values have to be here as well: seeding only the
                // pending one would leave the tab the capture opens showing short values and proving nothing.
                name = "示例问候扩展（名称刻意写长，用来验证表格与动作列的溢出处理）",
                version = "1.0.0",
                publisher = "某家名称同样非常长的第三方发布者有限责任公司（Example Diagnostics and Compliance GmbH）",
                entrypoint = "bin/greet.exe",
                commands = new object[] { commands[1] },
                packageSha256 = new string('b', 64),
                packageBytes = 2048L,
                state = 1,
                approvedBy = "本机管理员",
                approvedAt = (DateTimeOffset?)DateTimeOffset.Now,
                extractRoot = (string?)null,
                uploadedAt = DateTimeOffset.Now,
                timeoutSeconds = 30
            }
        };

        File.WriteAllText(
            Path.Combine(dataRoot, "extensions.json"),
            JsonSerializer.Serialize(packages, new JsonSerializerOptions { WriteIndented = true }));
    }
}
