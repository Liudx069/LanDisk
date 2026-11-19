using System.Net;
using System.Net.Sockets;

namespace LanDisk.Core.Networking;

/// <summary>A port this machine cannot bind, with the message the operator should see.</summary>
public sealed record PortConflict(int Port, string Message);

/// <summary>
/// Decides which ports LanDisk will try to bind, and whether it can.
///
/// This lives here rather than inside the Windows shell for two reasons. It is the only part of
/// start-up that must be testable without a desktop session, and the rule it enforces is a product
/// promise rather than a UI detail: **LanDisk never changes its ports to dodge a conflict**, because
/// the phone and the browser have the old address saved.
/// </summary>
public static class PortPreflight
{
    public const int DefaultHttpPort = 8891;
    public const int DefaultHttpsPort = 8892;

    /// <summary>True when a loopback listener can be created on the port right now.</summary>
    public static bool IsPortFree(int port)
    {
        if (port is <= 0 or > 65535) return false;

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// The ports the host will try to bind: the persisted settings, overridden by an explicit command
    /// line flag. Order is HTTP first, then HTTPS, and a single distinct value when they coincide.
    /// </summary>
    public static int[] ResolveConfiguredPorts(
        int settingsHttp,
        int settingsHttps,
        IReadOnlyList<string>? args = null)
    {
        int http = settingsHttp > 0 ? settingsHttp : DefaultHttpPort;
        int https = settingsHttps > 0 ? settingsHttps : DefaultHttpsPort;

        if (args is not null)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Count && int.TryParse(args[i + 1], out int parsedHttp) && parsedHttp > 0)
                    http = parsedHttp;
                if (args[i] == "--https-port" && i + 1 < args.Count && int.TryParse(args[i + 1], out int parsedHttps) && parsedHttps > 0)
                    https = parsedHttps;
            }
        }

        return http != https ? [http, https] : [http];
    }

    /// <summary>Returns the first port that cannot be bound, or null when all of them are available.</summary>
    public static PortConflict? FindConflict(IEnumerable<int> ports)
    {
        foreach (int port in ports)
        {
            if (IsPortFree(port)) continue;

            // The message names the port, says what LanDisk will not do, and tells the operator what
            // to do instead. "启动失败" on its own is not actionable.
            return new PortConflict(port,
                $"端口 {port} 已被其它程序占用，LanDisk 无法启动。\n\n"
                + "LanDisk 不会自动更换端口，因为手机或浏览器里保存的地址会因此失效。\n"
                + $"请关闭占用 {port} 的程序，或在管理页面的“网络”里显式改用其它端口后重启。");
        }

        return null;
    }
}
