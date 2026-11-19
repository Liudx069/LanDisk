using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanDisk.Core.Extensions;
using LanDisk.Core.Model;

namespace LanDisk.Server.Services;

/// <summary>
/// Stores, approves and runs extension packages.
///
/// <para><b>Security boundary.</b> The server never loads an extension binary. Uploaded packages stay in a
/// quarantine directory as <c>PendingApproval</c> until the local operator approves them; approval pins the
/// package SHA256, so any later change returns it to <c>PendingApproval</c>. Running a command spawns the
/// separate <c>LanDisk.ExtensionHost.exe</c>, which is the only process that executes extension code — under a
/// restricted token and a Job Object. This service only writes a task description and reads back the result.</para>
/// </summary>
public sealed class ExtensionService
{
    private readonly string _dataRoot;
    private readonly string _hostPath;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<ExtensionPackageRecord> _packages;
    private readonly List<ExtensionTaskRecord> _tasks;
    private readonly ConcurrentDictionary<string, Process> _running = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ExtensionService(string dataRoot, string? hostPath, Action<string>? log = null)
    {
        _dataRoot = dataRoot;
        // The host ships in its own folder next to the product so its runtime files cannot collide with ours.
        // Both layouts are accepted: a test can point at a build output, and a release finds extensions\.
        _hostPath = hostPath ?? FirstExisting(
            Path.Combine(AppContext.BaseDirectory, "extensions", "LanDisk.ExtensionHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "LanDisk.ExtensionHost.exe"));
        _log = log;
        _packages = Load<List<ExtensionPackageRecord>>(Path.Combine(dataRoot, "extensions.json"));
        _tasks = Load<List<ExtensionTaskRecord>>(Path.Combine(dataRoot, "extension-tasks.json"));
    }

    private static string FirstExisting(params string[] candidates)
        => candidates.FirstOrDefault(File.Exists) ?? candidates[0];

    public string PackagesDir => Path.Combine(_dataRoot, "extensions");
    public string QuarantineDir => Path.Combine(PackagesDir, "quarantine");
    public string ApprovedDir => Path.Combine(PackagesDir, "approved");
    public string TasksDir => Path.Combine(_dataRoot, "extension-tasks");

    public IReadOnlyList<ExtensionPackageRecord> Packages() { lock (_gate) return _packages.ToList(); }

    public IReadOnlyList<ExtensionTaskRecord> Tasks(int limit = 200)
    {
        lock (_gate) return _tasks.OrderByDescending(t => t.StartedAt).Take(limit).ToList();
    }

    /// <summary>Uploads and quarantines a package. Returns its record, or a failure.</summary>
    public OperationOutcome<ExtensionPackageRecord> Upload(byte[] bytes, string fileName)
    {
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.Length > ExtensionLimits.MaxPackageBytes)
            return OperationOutcome<ExtensionPackageRecord>.Fail("PACKAGE_TOO_LARGE", "扩展包超过大小上限。");

        string packagePath = Path.Combine(QuarantineDir, fileName);
        Directory.CreateDirectory(QuarantineDir);
        File.WriteAllBytes(packagePath, bytes);

        ExtensionPackageResult inspection = ExtensionPackage.Inspect(packagePath);
        if (!inspection.Ok)
        {
            try { File.Delete(packagePath); } catch { }
            return OperationOutcome<ExtensionPackageRecord>.Fail("PACKAGE_REJECTED", inspection.Error!);
        }

        ExtensionManifest manifest = inspection.Manifest!;
        string extractRoot = Path.Combine(QuarantineDir, manifest.Id);
        if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, true);
        Directory.CreateDirectory(extractRoot);
        string entrypoint;
        try
        {
            entrypoint = ExtensionPackage.Extract(packagePath, extractRoot);
        }
        catch (Exception ex)
        {
            return OperationOutcome<ExtensionPackageRecord>.Fail("EXTRACT_REJECTED", ex.Message);
        }

        // A re-upload of the same id replaces the quarantined record; an already-approved package of the same
        // id is *not* replaced — a changed package must go through approval again.
        var record = new ExtensionPackageRecord(
            manifest.Id, manifest.Name, manifest.Version, manifest.Publisher, entrypoint,
            manifest.Commands, sha, bytes.Length, ExtensionState.PendingApproval, null, null, extractRoot, DateTimeOffset.Now,
            manifest.TimeoutSeconds);

        lock (_gate)
        {
            _packages.RemoveAll(p => p.Id == manifest.Id && p.State != ExtensionState.Approved);
            _packages.Add(record);
            Save(_packages, "extensions.json");
        }

        return OperationOutcome<ExtensionPackageRecord>.Success(record);
    }

    /// <summary>The local operator approves the *exact* package whose SHA is recorded.</summary>
    public OperationOutcome<ExtensionPackageRecord> Approve(string id, string expectedSha)
    {
        lock (_gate)
        {
            ExtensionPackageRecord? record = _packages.FirstOrDefault(p => p.Id == id);
            if (record is null) return OperationOutcome<ExtensionPackageRecord>.Fail("NOT_FOUND", "扩展不存在。");
            if (!string.Equals(record.PackageSha256, expectedSha, StringComparison.OrdinalIgnoreCase))
                return OperationOutcome<ExtensionPackageRecord>.Fail("SHA_MISMATCH", "包内容与批准时的 SHA 不一致，需要重新批准。");

            var approved = record with { State = ExtensionState.Approved, ApprovedBy = "本机管理端", ApprovedAt = DateTimeOffset.Now };
            _packages.RemoveAll(p => p.Id == id);
            _packages.Add(approved);
            Save(_packages, "extensions.json");
            return OperationOutcome<ExtensionPackageRecord>.Success(approved);
        }
    }

    public OperationOutcome<ExtensionPackageRecord> SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            ExtensionPackageRecord? record = _packages.FirstOrDefault(p => p.Id == id);
            if (record is null) return OperationOutcome<ExtensionPackageRecord>.Fail("NOT_FOUND", "扩展不存在。");

            var updated = record with { State = enabled ? ExtensionState.Approved : ExtensionState.Disabled };
            _packages.RemoveAll(p => p.Id == id);
            _packages.Add(updated);
            Save(_packages, "extensions.json");
            return OperationOutcome<ExtensionPackageRecord>.Success(updated);
        }
    }

    /// <summary>
    /// Runs a command. Only the manifest-declared command and schema-validated arguments reach the host; the
    /// entrypoint comes from the approved manifest, never from the caller.
    /// </summary>
    public async Task<OperationOutcome<ExtensionTaskRecord>> ExecuteAsync(
        string extensionId, string commandId, JsonNode? arguments, DeviceDefinition? device, CancellationToken cancellation)
    {
        ExtensionPackageRecord? package;
        ExtensionCommand? command;
        lock (_gate)
        {
            package = _packages.FirstOrDefault(p => p.Id == extensionId && p.State == ExtensionState.Approved);
            command = package?.Commands.FirstOrDefault(c => c.Id == commandId);
        }

        if (package is null) return OperationOutcome<ExtensionTaskRecord>.Fail("NOT_APPROVED", "该扩展尚未批准或已禁用。");
        if (command is null) return OperationOutcome<ExtensionTaskRecord>.Fail("COMMAND_NOT_FOUND", "该扩展没有这个命令。");

        ArgumentValidation validation = ExtensionPackage.ValidateArguments(command!, arguments);
        if (!validation.Ok) return OperationOutcome<ExtensionTaskRecord>.Fail(validation.Error!, validation.Error!);

        if (!File.Exists(_hostPath))
            return OperationOutcome<ExtensionTaskRecord>.Fail("HOST_MISSING", "扩展执行宿主不可用。");

        var task = new ExtensionTaskRecord(
            Guid.NewGuid().ToString("N"), package.Id, package.Name, commandId, validation.Summary,
            device?.Id ?? "local-admin", device?.Name ?? "本机管理端", ExtensionTaskState.Queued, DateTimeOffset.Now, null, 0, null, null);

        lock (_gate)
        {
            _tasks.Add(task);
            Save(_tasks, "extension-tasks.json");
        }

        // The host runs asynchronously: the endpoint returns the queued task immediately and the caller polls
        // for the result. A cancel or a timeout must be possible *while* the command runs, which cannot happen
        // if the request blocks until the extension exits.
        // The host runs asynchronously: the endpoint returns the queued task immediately and the caller polls
        // for the result. A cancel or a timeout must be possible *while* the command runs, which cannot happen
        // if the request blocks until the extension exits.
        _ = Task.Run(() => RunHostAsync(task, package, command, validation.Arguments), CancellationToken.None);
        return OperationOutcome<ExtensionTaskRecord>.Success(task);
    }

    private void RunHostAsync(ExtensionTaskRecord task, ExtensionPackageRecord package, ExtensionCommand command, JsonObject? arguments)
    {
        Directory.CreateDirectory(TasksDir);
        string specFile = Path.Combine(TasksDir, task.Id + ".json");

        var spec = new
        {
            entrypoint = package.Entrypoint,
            timeoutSeconds = Math.Clamp(package.TimeoutSeconds, 1, 300),
            arguments = ArgumentsToArray(arguments)
        };
        File.WriteAllText(specFile, JsonSerializer.Serialize(spec, Json));

        MarkState(task, ExtensionTaskState.Running);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _hostPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--task");
            psi.ArgumentList.Add(specFile);

            using Process host = Process.Start(psi) ?? throw new InvalidOperationException("无法启动扩展宿主");
            _running[task.Id] = host;

            host.WaitForExit();

            _running.TryRemove(task.Id, out _);

            string resultFile = specFile + ".result.json";
            if (File.Exists(resultFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(resultFile));
                JsonElement root = doc.RootElement;
                // The host writes camelCase (JsonSerializerDefaults.Web); read case-insensitively so a naming
                // convention on either side cannot silently turn a successful run into "Failed".
                string state = GetString(root, "state") ?? "Failed";
                int exitCode = GetInt(root, "exitCode");
                string? stdout = Truncate(GetString(root, "stdout"));
                string? stderr = Truncate(GetString(root, "stderr"));
                string? error = GetString(root, "error");

                ExtensionTaskState parsed = state switch
                {
                    "Succeeded" => ExtensionTaskState.Succeeded,
                    "TimedOut" => ExtensionTaskState.TimedOut,
                    "Cancelled" => ExtensionTaskState.Cancelled,
                    _ => ExtensionTaskState.Failed
                };
                Finish(task, parsed, exitCode, stdout, stderr, error);
            }
            else
            {
                Finish(task, ExtensionTaskState.Failed, host.ExitCode, null, null, "宿主没有写出结果文件。");
            }
        }
        catch (Exception ex)
        {
            _running.TryRemove(task.Id, out _);
            Finish(task, ExtensionTaskState.Failed, -1, null, null, ex.GetType().Name);
        }
    }

    /// <summary>Cancels a running task by terminating its host; the job object kills the extension tree too.</summary>
    public OperationOutcome<bool> Cancel(string taskId)
    {
        // The state is moved to Cancelled *before* the host is killed: killing is asynchronous and the run
        // loop could otherwise record "the host exited without a result file" first, reporting a cancelled
        // task as failed. Finish() also refuses to relabel a cancelled task.
        bool marked;
        lock (_gate)
        {
            ExtensionTaskRecord? task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null) return OperationOutcome<bool>.Fail("NOT_FOUND", "任务不存在。");
            if (task.State is not (ExtensionTaskState.Running or ExtensionTaskState.Queued))
                return OperationOutcome<bool>.Fail("NOT_RUNNING", "任务不在运行中。");

            int index = _tasks.FindIndex(t => t.Id == taskId);
            _tasks[index] = _tasks[index] with
            {
                State = ExtensionTaskState.Cancelled,
                FinishedAt = DateTimeOffset.Now,
                Error = "任务已被取消。"
            };
            Save(_tasks, "extension-tasks.json");
            marked = true;
        }

        if (marked && _running.TryGetValue(taskId, out Process? host))
        {
            try { host.Kill(entireProcessTree: true); } catch { }
        }

        return OperationOutcome<bool>.Success(true);
    }

    /// <summary>Cancels every running task of a device that just lost ExtensionExecute.</summary>
    public int CancelForDevice(string deviceId, string reason)
    {
        int count = 0;
        foreach (ExtensionTaskRecord task in _tasks.Where(t => t.SourceDeviceId == deviceId && t.State is ExtensionTaskState.Running or ExtensionTaskState.Queued).ToList())
        {
            Cancel(task.Id);
            count++;
        }
        return count;
    }

    private static int TimeoutFrom(ExtensionPackageRecord package) => package.TimeoutSeconds;

    private static List<string> ArgumentsToArray(JsonObject? arguments)
    {
        if (arguments is null) return [];
        var list = new List<string>();
        foreach ((string key, JsonNode? value) in arguments)
        {
            list.Add("--" + key);
            list.Add(JsonPrimitiveText(value));
        }
        return list;
    }

    private static string JsonPrimitiveText(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out string? s) => s!,
        JsonValue value when value.TryGetValue<bool>(out bool b) => b ? "true" : "false",
        JsonValue value when value.TryGetValue<double>(out double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<int>(out int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => node?.ToJsonString() ?? string.Empty
    };

    private static string? Truncate(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : (text.Length <= ExtensionLimits.MaxLogBytes ? text : text[..ExtensionLimits.MaxLogBytes] + "…");

    /// <summary>Case-insensitive JSON property read, so the host's camelCase and this side cannot disagree.</summary>
    private static string? GetString(JsonElement root, string name)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }

    private static int GetInt(JsonElement root, string name)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out int value))
                return value;
        }
        return 0;
    }

    private void MarkState(ExtensionTaskRecord task, ExtensionTaskState state)
    {
        lock (_gate)
        {
            int index = _tasks.FindIndex(t => t.Id == task.Id);
            if (index >= 0) _tasks[index] = _tasks[index] with { State = state };
            Save(_tasks, "extension-tasks.json");
        }
    }

    private void Finish(ExtensionTaskRecord task, ExtensionTaskState state, int exitCode, string? stdout, string? stderr, string? error)
    {
        lock (_gate)
        {
            int index = _tasks.FindIndex(t => t.Id == task.Id);
            if (index >= 0)
            {
                // A deliberate cancellation wins: the host is killed, so it cannot write its result file, and
                // the "no result file" path must not relabel a cancelled task as failed.
                if (_tasks[index].State == ExtensionTaskState.Cancelled && state != ExtensionTaskState.Cancelled)
                {
                    Save(_tasks, "extension-tasks.json");
                    return;
                }

                _tasks[index] = _tasks[index] with
                {
                    State = state,
                    FinishedAt = DateTimeOffset.Now,
                    ExitCode = exitCode,
                    Log = stderr is null ? stdout : (stdout + "\n" + stderr),
                    Error = error
                };
            }
            Save(_tasks, "extension-tasks.json");
        }
    }

    private void Save<T>(T value, string fileName)
    {
        try
        {
            File.WriteAllText(Path.Combine(_dataRoot, fileName), JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex)
        {
            _log?.Invoke("保存扩展状态失败：" + ex.Message);
        }
    }

    private static T Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return Activator.CreateInstance<T>();
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? Activator.CreateInstance<T>();
        }
        catch
        {
            return Activator.CreateInstance<T>();
        }
    }
}
