using LanDisk.Server;

try
{
    // The command-line server is a shipped entry point too (it is what the acceptance scripts and
    // real-machine smoke tests drive), so it publishes the stable name as well.
    WebApplication app = LanDiskHost.Build(args, new LanDisk.Server.HostOptions { EnableMdns = true });
    await app.RunAsync();
    return 0;
}
catch (IOException ex) when (ex.Message.Contains("address", StringComparison.OrdinalIgnoreCase)
                             || ex.InnerException is System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("启动失败：监听端口或地址被占用。");
    Console.Error.WriteLine("  " + ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("LanDisk 会同时监听一个 HTTP 端口（默认 8891）和一个回环 HTTPS 端口（默认 8892）。");
    Console.Error.WriteLine("任一端口被占用都会导致启动失败。可以：");
    Console.Error.WriteLine("  1) 在管理页面的“网络”里更换端口后重启；或用命令行临时指定：");
    Console.Error.WriteLine("     LanDisk.Server.exe --port 18991 --https-port 18992");
    Console.Error.WriteLine("  2) 找到并关闭占用端口的程序：");
    Console.Error.WriteLine("     netstat -ano | findstr :8891");
    Console.Error.WriteLine("     netstat -ano | findstr :8892");
    Console.Error.WriteLine();
    Console.Error.WriteLine("提示：同一台电脑只需要运行一个 LanDisk 实例。");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("LanDisk 启动失败：" + ex.GetType().Name);
    Console.Error.WriteLine("  " + ex.Message);
    if (ex.InnerException is not null)
        Console.Error.WriteLine("  内部原因: " + ex.InnerException.Message);
    Console.Error.WriteLine();
    return 1;
}
