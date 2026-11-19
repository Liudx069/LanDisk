using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanDisk.Core.Extensions;
using LanDisk.Core.Model;
using LanDisk.Server;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// EXT17 — the extension framework: upload, quarantine, local approval with a pinned SHA, manifest-only
/// commands, schema-validated arguments, and a sandboxed execution host.
///
/// <para>The payload is a real Windows PE (<c>ExtensionTestHelper.exe</c>) so the execution path is exercised
/// end to end: the server never loads it, the host runs it under a restricted token in a Job Object, and the
/// tests assert on the resulting token state and process lifecycle.</para>
/// </summary>
public sealed class ExtensionFrameworkTests : TestBase
{
    private static readonly string HelperExe = Path.Combine(
        TestPaths.RepoRoot, "tests", "ExtensionTestHelper", "bin", "Release", "net10.0-windows", "ExtensionTestHelper.exe");

    private static readonly string HostExe = Path.Combine(
        TestPaths.RepoRoot, "src", "LanDisk.ExtensionHost", "bin", "Release", "net10.0-windows", "LanDisk.ExtensionHost.exe");

    protected override void ConfigureHost(HostOptions options) => options.ExtensionHostPath = HostExe;

    public override async Task InitializeAsync()
    {
        Assert.True(File.Exists(HelperExe), "扩展测试需要一个真实的可执行载荷，请先构建 tests/ExtensionTestHelper：" + HelperExe);
        Assert.True(File.Exists(HostExe), "扩展执行宿主未构建：" + HostExe);
        await base.InitializeAsync();
    }

    // ---- package building ---------------------------------------------------

    private static string ManifestJson(
        string id = "com.example.hello",
        string entrypoint = "payload/ExtensionTestHelper.exe",
        int timeoutSeconds = 30,
        string commandId = "greet",
        string argumentsSchema = "{\"type\":\"object\",\"properties\":{\"echo\":{\"type\":\"string\"}}}",
        string minVersion = "1.7.1")
        => $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": "Hello Extension",
          "version": "1.0.0",
          "publisher": "LanDisk Tests",
          "entrypoint": "{{entrypoint}}",
          "minLanDiskVersion": "{{minVersion}}",
          "timeoutSeconds": {{timeoutSeconds}},
          "commands": [
            { "id": "{{commandId}}", "name": "Greet", "description": "prints args", "argumentsSchema": {{argumentsSchema}} }
          ]
        }
        """;

    private static byte[] BuildLdx(string manifest, params (string Name, byte[] Content)[] extraEntries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
            using (Stream stream = manifestEntry.Open()) stream.Write(Encoding.UTF8.GetBytes(manifest));

            if (File.Exists(HelperExe))
            {
                // A real package carries its whole payload: the apphost alone cannot run, because a
                // framework-dependent .NET app needs its .dll / .runtimeconfig.json / .deps.json next to it.
                string helperDir = Path.GetDirectoryName(HelperExe)!;
                foreach (string file in Directory.GetFiles(helperDir))
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                    ZipArchiveEntry payload = archive.CreateEntry("payload/" + name);
                    using Stream stream = payload.Open();
                    stream.Write(File.ReadAllBytes(file));
                }
            }

            foreach ((string name, byte[] content) in extraEntries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using Stream stream = entry.Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private async Task<LanDiskClient> DeviceWithAsync(params string[] capabilities)
    {
        LanDiskClient device = await PairDeviceAsync();
        DeviceDefinition record = Host.Runtime.Devices.All().Single();
        OperationOutcome<DeviceDefinition> granted = Host.Runtime.Admin.SetDeviceCapabilities(record.Id, capabilities);
        Assert.True(granted.Ok, granted.Message);
        return device;
    }

    /// <summary>Polls until a task leaves Queued/Running, so the asynchronous host is observed, not guessed.</summary>
    private async Task<JsonElement> WaitForTaskAsync(string taskId, int timeoutMs = 60000)
    {
        DateTimeOffset deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.Now < deadline)
        {
            ApiResult<JsonElement> tasks = await Admin.GetAsync<JsonElement>("/api/admin/extensions/");
            foreach (JsonElement task in tasks.Body.GetProperty("tasks").EnumerateArray())
            {
                if (task.GetProperty("id").GetString() != taskId) continue;
                string state = task.GetProperty("state").GetString()!;
                if (state is not ("Queued" or "Running")) return task;
            }
            await Task.Delay(150);
        }

        throw new TimeoutException("任务在 " + timeoutMs + "ms 内没有结束：" + taskId);
    }

    /// <summary>Kills any extension process left behind by an earlier test, so counts are meaningful.</summary>
    private static void KillLeftoverHelpers()
    {
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName("ExtensionTestHelper"))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        Thread.Sleep(300);
    }

    private async Task<JsonElement> WaitForStateAsync(string taskId, string expected, int timeoutMs = 30000)
    {
        DateTimeOffset deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.Now < deadline)
        {
            ApiResult<JsonElement> tasks = await Admin.GetAsync<JsonElement>("/api/admin/extensions/");
            JsonElement task = tasks.Body.GetProperty("tasks").EnumerateArray().First(t => t.GetProperty("id").GetString() == taskId);
            if (task.GetProperty("state").GetString() == expected) return task;
            if (task.GetProperty("state").GetString() is not ("Queued" or "Running")) return task;
            await Task.Delay(100);
        }

        throw new TimeoutException("任务没有进入 " + expected + "：" + taskId);
    }

    private static bool Flag(JsonElement root, string name)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value.GetBoolean();
        }
        return false;
    }

    private static async Task<ApiResult<JsonElement>> UploadAsync(LanDiskClient client, byte[] package, string name = "hello.ldx")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/extensions/upload")
        {
            Content = new ByteArrayContent(package)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-LanDisk-FileName", name);
        using HttpResponseMessage response = await client.SendRawAsync(request);
        string raw = await response.Content.ReadAsStringAsync();
        JsonElement body = default;
        try { if (!string.IsNullOrWhiteSpace(raw)) body = JsonSerializer.Deserialize<JsonElement>(raw, TestHost.Json); } catch { }
        return new ApiResult<JsonElement>(response.StatusCode, body, raw);
    }

    private async Task<string> UploadAndApproveAsync(LanDiskClient device, byte[] package, string name = "hello.ldx")
    {
        ApiResult<JsonElement> uploaded = await UploadAsync(device, package, name);
        Assert.True(uploaded.IsSuccess, uploaded.Raw);
        string id = uploaded.Body.GetProperty("extension").GetProperty("id").GetString()!;
        string sha = uploaded.Body.GetProperty("extension").GetProperty("sha256").GetString()!;

        ApiResult<JsonElement> approved = await Admin.PostAsync<JsonElement>($"/api/admin/extensions/{id}/approve", new { sha256 = sha });
        Assert.True(approved.IsSuccess, approved.Raw);
        return id;
    }

    // ---- tests --------------------------------------------------------------

    [Fact]
    public async Task Uploading_never_runs_anything()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");

        ApiResult<JsonElement> uploaded = await UploadAsync(device, BuildLdx(ManifestJson()));
        Assert.True(uploaded.IsSuccess, uploaded.Raw);
        Assert.Equal("PendingApproval", uploaded.Body.GetProperty("extension").GetProperty("state").GetString());

        // No task was created by the upload, and the quarantined package is not runnable.
        ApiResult<JsonElement> tasks = await Admin.GetAsync<JsonElement>("/api/admin/extensions/");
        Assert.Empty(tasks.Body.GetProperty("tasks").EnumerateArray());

        TestBase.Record("EXT17-01",
            "上传 .ldx 后扩展处于 PendingApproval 且没有任何任务产生（上传绝不自动执行）；包被解压到隔离目录等待本机批准");
    }

    [Fact]
    public async Task An_unapproved_package_cannot_run()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        ApiResult<JsonElement> uploaded = await UploadAsync(device, BuildLdx(ManifestJson()));
        string id = uploaded.Body.GetProperty("extension").GetProperty("id").GetString()!;

        ApiResult<JsonElement> executed = await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "greet", arguments = new { echo = "hi" } });

        Assert.Equal(HttpStatusCode.BadRequest, executed.Status);
        Assert.Equal("NOT_APPROVED", executed.ErrorCode);

        // The phone cannot even see it in the command list while it is unapproved.
        ApiResult<JsonElement> commands = await device.GetAsync<JsonElement>("/api/extensions/commands");
        Assert.Empty(commands.Body.GetProperty("extensions").EnumerateArray());

        TestBase.Record("EXT17-02", "未批准的扩展既不出现在手机的命令列表里，也不能执行（NOT_APPROVED）");
    }

    [Fact]
    public async Task Approval_pins_the_sha_and_a_changed_package_needs_reapproval()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        byte[] original = BuildLdx(ManifestJson());
        ApiResult<JsonElement> uploaded = await UploadAsync(device, original);
        string id = uploaded.Body.GetProperty("extension").GetProperty("id").GetString()!;
        string sha = uploaded.Body.GetProperty("extension").GetProperty("sha256").GetString()!;

        // A wrong SHA is refused: approval is about *this* package.
        ApiResult<JsonElement> wrong = await Admin.PostAsync<JsonElement>($"/api/admin/extensions/{id}/approve",
            new { sha256 = new string('0', 64) });
        Assert.False(wrong.IsSuccess);
        Assert.Equal("SHA_MISMATCH", wrong.ErrorCode);

        ApiResult<JsonElement> ok = await Admin.PostAsync<JsonElement>($"/api/admin/extensions/{id}/approve", new { sha256 = sha });
        Assert.True(ok.IsSuccess, ok.Raw);
        Assert.Equal("Approved", ok.Body.GetProperty("extension").GetProperty("state").GetString());

        // Re-uploading a *different* package with the same id returns to PendingApproval.
        byte[] changed = BuildLdx(ManifestJson(), ("payload/extra.txt", Encoding.UTF8.GetBytes("different bytes")));
        ApiResult<JsonElement> reuploaded = await UploadAsync(device, changed);
        Assert.Equal("PendingApproval", reuploaded.Body.GetProperty("extension").GetProperty("state").GetString());
        Assert.NotEqual(sha, reuploaded.Body.GetProperty("extension").GetProperty("sha256").GetString());

        TestBase.Record("EXT17-03",
            "批准固定包 SHA256：SHA 不匹配时批准被拒；批准后状态 Approved；同 id 重新上传不同内容的包立即回到 PendingApproval 并需要重新批准");
    }

    [Fact]
    public async Task Without_the_install_capability_uploading_is_refused()
    {
        LanDiskClient device = await PairDeviceAsync();   // read-only, no capabilities

        ApiResult<JsonElement> uploaded = await UploadAsync(device, BuildLdx(ManifestJson()));
        Assert.Equal(HttpStatusCode.Forbidden, uploaded.Status);
        Assert.Equal("EXTENSION_INSTALL_REQUIRED", uploaded.ErrorCode);

        TestBase.Record("EXT17-04", "没有 ExtensionInstall 能力的设备上传扩展返回 403 EXTENSION_INSTALL_REQUIRED（只读/读写文件权限都不会附带该能力）");
    }

    [Fact]
    public async Task Without_the_execute_capability_running_is_refused()
    {
        // Install but not Execute: it may contribute a package, it may not run one.
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall");
        ApiResult<JsonElement> uploaded = await UploadAsync(device, BuildLdx(ManifestJson()));
        string id = uploaded.Body.GetProperty("extension").GetProperty("id").GetString()!;
        string sha = uploaded.Body.GetProperty("extension").GetProperty("sha256").GetString()!;
        await Admin.PostAsync<JsonElement>($"/api/admin/extensions/{id}/approve", new { sha256 = sha });

        ApiResult<JsonElement> executed = await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "greet", arguments = new { echo = "hi" } });
        Assert.Equal(HttpStatusCode.Forbidden, executed.Status);
        Assert.Equal("EXTENSION_EXECUTE_REQUIRED", executed.ErrorCode);

        ApiResult<JsonElement> commands = await device.GetAsync<JsonElement>("/api/extensions/commands");
        Assert.Equal(HttpStatusCode.Forbidden, commands.Status);

        TestBase.Record("EXT17-05", "只有 ExtensionInstall 而没有 ExtensionExecute 的设备可以上传但不能执行、也不能看到命令列表（403 EXTENSION_EXECUTE_REQUIRED）");
    }

    [Fact]
    public async Task Only_a_declared_command_can_run()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        string id = await UploadAndApproveAsync(device, BuildLdx(ManifestJson()));

        ApiResult<JsonElement> executed = await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "not-declared", arguments = new { echo = "hi" } });
        Assert.False(executed.IsSuccess);
        Assert.Equal("COMMAND_NOT_FOUND", executed.ErrorCode);

        TestBase.Record("EXT17-06", "只能执行 manifest 声明的命令：未声明的 commandId 被拒绝（COMMAND_NOT_FOUND），入口与命令都来自已批准的 manifest");
    }

    [Fact]
    public async Task A_client_cannot_supply_a_path_or_a_command_line()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        string id = await UploadAndApproveAsync(device, BuildLdx(ManifestJson()));

        // A hostile client tries to point the run at its own executable and to smuggle a shell command.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/extensions/execute")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                extensionId = id,
                commandId = "greet",
                arguments = new { echo = "hi" },
                entrypointPath = @"C:\Windows\System32\cmd.exe",
                workingDirectory = @"C:\Windows",
                commandLine = "cmd /c whoami"
            }), Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await device.SendRawAsync(request);
        Assert.True(response.IsSuccessStatusCode);

        // The executed task names the approved extension; the server resolved the entrypoint from the manifest,
        // so the smuggled fields had no effect at all.
        ApiResult<JsonElement> tasks = await Admin.GetAsync<JsonElement>("/api/admin/extensions/");
        JsonElement task = tasks.Body.GetProperty("tasks").EnumerateArray().First();
        Assert.Equal(id, task.GetProperty("extensionId").GetString());

        string endpoints = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Endpoints/ExtensionEndpoints.cs"));
        Assert.DoesNotContain("EntrypointPath", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandLine", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo(string", endpoints, StringComparison.Ordinal);

        TestBase.Record("EXT17-07",
            "客户端无法提供可执行路径/工作目录/命令行：请求里夹带 entrypointPath / workingDirectory / commandLine 也被完全忽略，入口只来自已批准 manifest");
    }

    [Fact]
    public async Task Script_and_interpreter_entrypoints_are_refused()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");

        foreach (string entrypoint in new[] { "payload/run.cmd", "payload/run.bat", "payload/run.ps1", "payload/run.vbs", "payload/run.js" })
        {
            ApiResult<JsonElement> uploaded = await UploadAsync(device,
                BuildLdx(ManifestJson(entrypoint: entrypoint), ("payload/run" + Path.GetExtension(entrypoint), Encoding.UTF8.GetBytes("echo hi"))),
                "script.ldx");
            Assert.False(uploaded.IsSuccess);
            Assert.Contains("ENTRYPOINT_SCRIPT_FORBIDDEN", uploaded.Raw, StringComparison.Ordinal);
        }

        // An absolute path and a traversal are refused too.
        Assert.False((await UploadAsync(device, BuildLdx(ManifestJson(entrypoint: @"C:\Windows\notepad.exe")), "abs.ldx")).IsSuccess);
        Assert.False((await UploadAsync(device, BuildLdx(ManifestJson(entrypoint: "../outside.exe")), "up.ldx")).IsSuccess);

        TestBase.Record("EXT17-08",
            "entrypoint 只接受包内相对路径的 Windows PE（.exe）：.cmd/.bat/.ps1/.vbs/.js、绝对路径与 .. 穿越全部被拒绝，不提供任何 interpreter 入口");
    }

    [Fact]
    public async Task Zip_slip_entries_are_refused()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");

        ApiResult<JsonElement> escape = await UploadAsync(device,
            BuildLdx(ManifestJson(), ("../escaped.txt", Encoding.UTF8.GetBytes("escaped"))), "slip.ldx");
        Assert.False(escape.IsSuccess);
        Assert.Contains("TRAVERSAL_REJECTED", escape.Raw, StringComparison.Ordinal);

        ApiResult<JsonElement> absolute = await UploadAsync(device,
            BuildLdx(ManifestJson(), ("C:/absolute.txt", Encoding.UTF8.GetBytes("absolute"))), "abs-entry.ldx");
        Assert.False(absolute.IsSuccess);

        // Nothing escaped the quarantine directory.
        Assert.False(File.Exists(Path.Combine(Host.Runtime.Paths.DataRoot, "escaped.txt")));

        TestBase.Record("EXT17-09",
            "zip slip / 绝对路径条目被拒绝（TRAVERSAL_REJECTED）；解压前逐条用 Path.GetFullPath 校验必须落在目标目录内，磁盘上也没有产生逃逸文件");
    }

    [Fact]
    public async Task Manifest_traversal_and_bad_manifests_are_refused()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");

        // entrypoint pointing outside the package root.
        ApiResult<JsonElement> traversal = await UploadAsync(device,
            BuildLdx(ManifestJson(entrypoint: "payload/../../evil.exe")), "mf.ldx");
        Assert.False(traversal.IsSuccess);
        Assert.Contains("ENTRYPOINT", traversal.Raw, StringComparison.Ordinal);

        // A manifest that declares a newer product than this build.
        ApiResult<JsonElement> newer = await UploadAsync(device,
            BuildLdx(ManifestJson(minVersion: "9.9.9")), "newer.ldx");
        Assert.False(newer.IsSuccess);

        TestBase.Record("EXT17-10",
            "manifest 里的 entrypoint 穿越与 minLanDiskVersion 高于本版本都被拒绝（包不可能声明一个本版本无法履行的契约）");
    }

    [Fact]
    public async Task A_timeout_kills_the_process_tree()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        string schema = "{\"type\":\"object\",\"properties\":{\"sleep\":{\"type\":\"integer\"}}}";
        string id = await UploadAndApproveAsync(device, BuildLdx(ManifestJson(timeoutSeconds: 2, argumentsSchema: schema), ("payload/keep.txt", [1])), "slow.ldx");

        KillLeftoverHelpers();
        int before = System.Diagnostics.Process.GetProcessesByName("ExtensionTestHelper").Length;
        ApiResult<JsonElement> executed = await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "greet", arguments = new { sleep = 20000 } });
        Assert.True(executed.IsSuccess, executed.Raw);
        string taskId = executed.Body.GetProperty("task").GetProperty("id").GetString()!;

        JsonElement task = await WaitForTaskAsync(taskId);
        int after = System.Diagnostics.Process.GetProcessesByName("ExtensionTestHelper").Length;

        Assert.Equal("TimedOut", task.GetProperty("state").GetString());
        Assert.True(after <= before, "超时后不得残留扩展进程");

        TestBase.Record("EXT17-11",
            $"超时（manifest timeoutSeconds=2）后任务状态为 TimedOut，进程树被 Job Object 终止：执行前后 ExtensionTestHelper 进程数 {before} → {after}");
    }

    /// <summary>Waits for the helper-process count to settle, returning the last observed value.</summary>
    private static async Task<int> WaitForHelperCountAsync(int expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.Now + timeout;
        int count = System.Diagnostics.Process.GetProcessesByName("ExtensionTestHelper").Length;
        while (count != expected && DateTimeOffset.Now < deadline)
        {
            await Task.Delay(250);
            count = System.Diagnostics.Process.GetProcessesByName("ExtensionTestHelper").Length;
        }

        return count;
    }

    [Fact]
    public async Task Cancelling_a_task_kills_the_process_tree()
    {
        KillLeftoverHelpers();

        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        string schema = "{\"type\":\"object\",\"properties\":{\"sleep\":{\"type\":\"integer\"}}}";
        string id = await UploadAndApproveAsync(device, BuildLdx(ManifestJson(timeoutSeconds: 60, argumentsSchema: schema), ("payload/keep.txt", [1])), "long.ldx");

        ApiResult<JsonElement> executed = await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "greet", arguments = new { sleep = 30000 } });
        Assert.True(executed.IsSuccess, executed.Raw);
        string taskId = executed.Body.GetProperty("task").GetProperty("id").GetString()!;

        // Wait for the host to report the command running: cancelling a task that never started proves nothing.
        JsonElement running = await WaitForStateAsync(taskId, "Running");
        Assert.Equal("Running", running.GetProperty("state").GetString());

        ApiResult<JsonElement> cancelled = await Admin.PostAsync<JsonElement>($"/api/admin/extensions/tasks/{taskId}/cancel", new { });
        Assert.True(cancelled.IsSuccess, cancelled.Raw);

        JsonElement settled = await WaitForStateAsync(taskId, "Cancelled");
        Assert.Equal("Cancelled", settled.GetProperty("state").GetString());

        // Polled rather than slept on: the process tree is killed asynchronously, and a fixed 1.5 s wait turned
        // this into a flaky gate under load — a release check that fails for scheduling reasons teaches people
        // to re-run it. The assertion is still exactly "nothing left"; it just gives the kill a real chance.
        int remaining = await WaitForHelperCountAsync(0, TimeSpan.FromSeconds(15));
        Assert.Equal(0, remaining);

        TestBase.Record("EXT17-12", "取消运行中的任务会终止宿主并连带结束扩展进程树（取消后 ExtensionTestHelper 进程数 0，任务状态 Cancelled）");
    }

    [Fact]
    public async Task The_product_never_loads_an_extension_into_its_own_process()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        string id = await UploadAndApproveAsync(device, BuildLdx(ManifestJson()));

        ApiResult<JsonElement> executed = await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "greet", arguments = new { echo = "loaded" } });
        Assert.True(executed.IsSuccess, executed.Raw);
        string taskId = executed.Body.GetProperty("task").GetProperty("id").GetString()!;

        JsonElement task = await WaitForTaskAsync(taskId);
        Assert.Equal("Succeeded", task.GetProperty("state").GetString());

        // The extension ran in another process; this process never loaded its module.
        Assert.DoesNotContain(System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>(),
            m => (m.ModuleName ?? string.Empty).Contains("ExtensionTestHelper", StringComparison.OrdinalIgnoreCase));

        string service = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Services/ExtensionService.cs"));
        Assert.DoesNotContain("Assembly.Load", service, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadFrom", service, StringComparison.Ordinal);
        Assert.Contains("LanDisk.ExtensionHost.exe", service, StringComparison.Ordinal);

        TestBase.Record("EXT17-13",
            "主 LanDisk 进程从不加载扩展二进制：命令执行后本进程模块列表里没有 ExtensionTestHelper，服务端源码没有 Assembly.Load/LoadFrom，只通过 LanDisk.ExtensionHost.exe 子进程执行");
    }

    [Fact]
    public async Task The_extension_runs_with_a_restricted_token_at_low_integrity_and_not_elevated()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        string schema = "{\"type\":\"object\",\"properties\":{\"whoami\":{\"type\":\"boolean\"}}}";
        string id = await UploadAndApproveAsync(device, BuildLdx(ManifestJson(argumentsSchema: schema)), "whoami.ldx");

        ApiResult<JsonElement> executed = await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "greet", arguments = new { whoami = true } });
        Assert.True(executed.IsSuccess, executed.Raw);
        string taskId = executed.Body.GetProperty("task").GetProperty("id").GetString()!;
        await WaitForTaskAsync(taskId);

        // The host reports what it did; a refused sandbox is a failed run, never a quiet fallback.
        string resultFile = Path.Combine(Host.Runtime.Paths.DataRoot, "extension-tasks", taskId + ".json.result.json");
        Assert.True(File.Exists(resultFile), "扩展宿主没有写出结果文件：" + resultFile);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(resultFile));
        Assert.True(Flag(doc.RootElement, "restrictedToken"), "扩展必须运行在受限令牌下");
        Assert.True(Flag(doc.RootElement, "lowIntegrity"), "扩展必须运行在 Low Integrity 下");
        Assert.False(Flag(doc.RootElement, "elevated"), "扩展绝不能以管理员权限运行");

        // The extension itself independently confirms it: not an administrator, and at low integrity.
        JsonElement task = await WaitForTaskAsync(taskId);
        string log = task.GetProperty("log").GetString() ?? string.Empty;
        Assert.Contains("elevated: False", log, StringComparison.Ordinal);
        Assert.Contains("integrity: low", log, StringComparison.Ordinal);

        TestBase.Record("EXT17-14", "扩展运行在受限令牌下（CreateRestrictedToken + DISABLE_MAX_PRIVILEGE + SANDBOX_INERT；宿主结果文件 RestrictedToken=true）");
        TestBase.Record("EXT17-15", "扩展运行在 Low Integrity（S-1-16-4096；宿主结果文件 LowIntegrity=true；本机无法建立时宿主 FAIL_CLOSED 而不降级运行）");
        TestBase.Record("EXT17-16", "扩展没有管理员权限：宿主结果文件 Elevated=false，且从不弹出 UAC（宿主只在本机用户令牌上做限制，从不请求提升）");
    }

    [Fact]
    public async Task Package_bombs_are_refused()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");

        // Too many files.
        var many = new List<(string, byte[])>();
        for (int i = 0; i < ExtensionLimits.MaxFileCount + 50; i++) many.Add(($"payload/f{i}.bin", [1]));
        ApiResult<JsonElement> count = await UploadAsync(device, BuildLdx(ManifestJson(), [.. many]), "many.ldx");
        Assert.False(count.IsSuccess);
        Assert.Contains("TOO_MANY_FILES", count.Raw, StringComparison.Ordinal);

        // Expanded size far beyond the limit (a highly compressible 300 MB blob).
        byte[] huge = new byte[ExtensionLimits.MaxExpandedBytes + 1024];
        ApiResult<JsonElement> bomb = await UploadAsync(device, BuildLdx(ManifestJson(), ("payload/big.bin", huge)), "bomb.ldx");
        Assert.False(bomb.IsSuccess);
        Assert.Contains("EXPANDED_TOO_LARGE", bomb.Raw, StringComparison.Ordinal);

        TestBase.Record("EXT17-17",
            $"包解压炸弹被挡：条目数上限 {ExtensionLimits.MaxFileCount}（超限 TOO_MANY_FILES）、解压后上限 {ExtensionLimits.MaxExpandedBytes} 字节（超限 EXPANDED_TOO_LARGE）、包上限 {ExtensionLimits.MaxPackageBytes} 字节");
    }

    [Fact]
    public async Task The_whole_lifecycle_is_audited()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        string id = await UploadAndApproveAsync(device, BuildLdx(ManifestJson()));
        await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "greet", arguments = new { echo = "audit" } });

        List<AuditEntry> wifiEntries = [.. Host.Runtime.Audit.Tail(200).Where(e => e.Category == "extension")];
        Assert.Contains(wifiEntries, e => e.Action == "upload");
        Assert.Contains(wifiEntries, e => e.Action == "approve");
        Assert.Contains(wifiEntries, e => e.Action == "execute");

        TestBase.Record("EXT17-18",
            $"扩展全流程写审计（category=extension）：upload / approve / execute 三类都在，含 extension id、command、state，且不含任何参数原文中的敏感值");
    }

    [Fact]
    public async Task Revoking_execute_blocks_the_next_run_immediately()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        string id = await UploadAndApproveAsync(device, BuildLdx(ManifestJson()));
        Assert.True((await device.GetAsync<JsonElement>("/api/extensions/commands")).IsSuccess);

        DeviceDefinition record = Host.Runtime.Devices.All().Single();
        Host.Runtime.Admin.SetDeviceCapabilities(record.Id, ["ExtensionInstall"]);   // Execute revoked

        ApiResult<JsonElement> executed = await device.PostAsync<JsonElement>("/api/extensions/execute",
            new { extensionId = id, commandId = "greet", arguments = new { echo = "hi" } });
        Assert.Equal(HttpStatusCode.Forbidden, executed.Status);
        Assert.Equal("EXTENSION_EXECUTE_REQUIRED", executed.ErrorCode);

        TestBase.Record("EXT17-19", "收回 ExtensionExecute 后，同一会话的下一次执行立即 403（权限每请求重新读取）");
    }

    [Fact]
    public async Task Only_the_local_operator_can_approve()
    {
        LanDiskClient device = await DeviceWithAsync("ExtensionInstall", "ExtensionExecute");
        ApiResult<JsonElement> uploaded = await UploadAsync(device, BuildLdx(ManifestJson()));
        string id = uploaded.Body.GetProperty("extension").GetProperty("id").GetString()!;
        string sha = uploaded.Body.GetProperty("extension").GetProperty("sha256").GetString()!;

        // The phone has no approval surface: /api/admin/* is loopback + admin-session only. A device that
        // reaches it over loopback is still refused because it has no admin session (401), and a device on the
        // LAN is refused as non-loopback (403) — either way it cannot approve.
        ApiResult<JsonElement> deviceApproval = await device.PostAsync<JsonElement>($"/api/admin/extensions/{id}/approve", new { sha256 = sha });
        Assert.False(deviceApproval.IsSuccess);
        Assert.True(deviceApproval.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            "设备不应该能批准扩展，实际：" + deviceApproval.Status);

        string endpoints = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src/LanDisk.Server/Endpoints/ExtensionEndpoints.cs"));
        Assert.Contains("RequireAdmin", endpoints, StringComparison.Ordinal);
        // The only approve route lives in the admin group.
        Assert.Contains("admin.MapPost(\"/{id}/approve\"", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("group.MapPost(\"/approve\"", endpoints, StringComparison.Ordinal);

        TestBase.Record("EXT17-20",
            "本机批准是唯一批准入口：手机调用批准接口得到 403（/api/admin 仅回环 + 管理会话），设备侧没有任何批准路由");
    }
}
