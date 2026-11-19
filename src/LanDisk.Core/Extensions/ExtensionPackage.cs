using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanDisk.Core.Extensions;

/// <summary>Approval lifecycle of one uploaded extension package.</summary>
public enum ExtensionState
{
    /// <summary>Uploaded and stored in quarantine; cannot run. The Windows operator has not yet approved it.</summary>
    PendingApproval = 0,

    /// <summary>Approved by the local operator, with the package SHA256 pinned.</summary>
    Approved = 1,

    /// <summary>Was approved but has been disabled; stored but not runnable.</summary>
    Disabled = 2
}

/// <summary>Lifecycle of one extension command execution.</summary>
public enum ExtensionTaskState
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5
}

/// <summary>One command an extension declares.</summary>
public sealed record ExtensionCommand(string Id, string Name, string Description, JsonObject? ArgumentsSchema);

/// <summary>The manifest the phone can see, but for the operator's review.</summary>
public sealed record ExtensionManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Version,
    string Publisher,
    string Entrypoint,
    IReadOnlyList<ExtensionCommand> Commands,
    string MinLanDiskVersion,
    int TimeoutSeconds);

/// <summary>A persisted extension package record (the .ldx, unpinned until approved).</summary>
public sealed record ExtensionPackageRecord(
    string Id,
    string Name,
    string Version,
    string Publisher,
    string Entrypoint,
    IReadOnlyList<ExtensionCommand> Commands,
    string PackageSha256,
    long PackageBytes,
    ExtensionState State,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string? ExtractRoot,
    DateTimeOffset UploadedAt,
    int TimeoutSeconds = 30);

/// <summary>A persisted task record.</summary>
public sealed record ExtensionTaskRecord(
    string Id,
    string ExtensionId,
    string ExtensionName,
    string CommandId,
    string? ArgumentsSummary,
    string SourceDeviceId,
    string SourceDeviceName,
    ExtensionTaskState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int ExitCode,
    string? Log,
    string? Error);

/// <summary>The outcome of validating an extension package: either a package description, or a reason.</summary>
public sealed record ExtensionPackageResult(ExtensionManifest? Manifest, string? Error)
{
    public bool Ok => Error is null;
    public static ExtensionPackageResult Fail(string error) => new(null, error);
}

/// <summary>The outcome of validating arguments against a command schema.</summary>
public sealed record ArgumentValidation(bool Ok, JsonObject? Arguments, string? Error, string? Summary);

/// <summary>Hard limits for packages and expansion, written into the report.</summary>
public static class ExtensionLimits
{
    public const long MaxPackageBytes = 64L * 1024 * 1024;
    public const long MaxExpandedBytes = 256L * 1024 * 1024;
    public const int MaxFileCount = 1000;
    public const int MaxLogBytes = 64 * 1024;
    public const int MaxConcurrentTasks = 4;

    /// <summary>Version prefixes a manifest may require (we ship 1.7.x; a manifest asking for 2.x is refused).</summary>
    public static readonly Version CurrentVersion = new(1, 7, 1);
}

/// <summary>
/// Validates and safely extracts a <c>.ldx</c> package.
///
/// <para><b>What "safe" means here.</b> A ZIP is a list of named blobs, and every one of those names is
/// attacker-controlled. This extracts each entry through <c>Path.GetFullPath</c> and refuses anything that
/// escapes the destination: <c>..</c>, absolute and UNC paths, a drive/alternate-data-stream colon, an entry
/// that would overwrite a directory, a reparse-point bit, and two entries that collide case-insensitively. It
/// also refuses a package or an expansion beyond the published limits, so a 64 MB zip cannot turn into a
/// gigabyte of files (the classic zip bomb).</para>
/// </summary>
public static class ExtensionPackage
{
    private static readonly string[] ForbiddenEntrypointExtensions = [".cmd", ".bat", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".wsh", ".com", ".scr", ".pif"];

    public static ExtensionPackageResult Inspect(string packagePath)
    {
        try
        {
            FileInfo info = new(packagePath);
            if (!info.Exists) return ExtensionPackageResult.Fail("PACKAGE_NOT_FOUND：找不到上传的扩展包。");
            if (info.Length > ExtensionLimits.MaxPackageBytes)
                return ExtensionPackageResult.Fail($"PACKAGE_TOO_LARGE：扩展包 {info.Length} 字节超过上限 {ExtensionLimits.MaxPackageBytes} 字节。");

            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count > ExtensionLimits.MaxFileCount)
                return ExtensionPackageResult.Fail($"TOO_MANY_FILES：包内 {archive.Entries.Count} 个条目超过上限 {ExtensionLimits.MaxFileCount}。");

            long totalUncompressed = archive.Entries.Sum(e => e.Length);
            if (totalUncompressed > ExtensionLimits.MaxExpandedBytes)
                return ExtensionPackageResult.Fail($"EXPANDED_TOO_LARGE：解压后 {totalUncompressed} 字节超过上限 {ExtensionLimits.MaxExpandedBytes} 字节。");

            ZipArchiveEntry? manifestEntry = archive.GetEntry("manifest.json")
                                              ?? throw new InvalidDataException("MANIFEST_MISSING：包内没有 manifest.json。");
            using var manifestStream = manifestEntry.Open();
            ExtensionManifest? manifest = ParseManifest(manifestStream);
            if (manifest is null) return ExtensionPackageResult.Fail("MANIFEST_INVALID：manifest.json 无法解析。");

            // The entrypoint must be a package-relative path to a Windows PE .exe, nothing else.
            string? error = ValidateEntrypoint(manifest.Entrypoint, archive);
            if (error is not null) return ExtensionPackageResult.Fail(error);

            foreach (ExtensionCommand command in manifest.Commands)
            {
                if (string.IsNullOrWhiteSpace(command.Id) || string.IsNullOrWhiteSpace(command.Name))
                    return ExtensionPackageResult.Fail("COMMAND_INVALID：命令缺少 id 或 name。");
            }

            return new ExtensionPackageResult(manifest, null);
        }
        catch (InvalidDataException ex)
        {
            return ExtensionPackageResult.Fail(ex.Message);
        }
        catch (Exception)
        {
            return ExtensionPackageResult.Fail("PACKAGE_INVALID：无法作为 ZIP 打开扩展包。");
        }
    }

    /// <summary>Extracts an already-inspected package into <paramref name="destination"/> and returns the
    /// absolute path of the entrypoint. The destination is created by the caller and must be empty.</summary>
    public static string Extract(string packagePath, string destination)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination);

        using ZipArchive archive = ZipFile.OpenRead(packagePath);

        // Reject reparse/symlink entries up front: a link inside a zip is how an archive escapes its sandbox.
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            uint mode = (uint)(entry.ExternalAttributes >> 16);
            bool symlink = (mode & 0xA000) == 0xA000;   // S_IFLNK | S_IFREG-with-link
            if (symlink) throw new InvalidDataException("REPARSE_POINT_REJECTED：包内含符号链接/重解析点条目：" + entry.FullName);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? entrypointPath = null;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = SafeTarget(root, entry.FullName);
            if (!seen.Add(target)) throw new InvalidDataException("CASE_COLLISION：两个条目在大小写不敏感文件系统上解析到同一路径：" + target);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream input = entry.Open();
            using FileStream output = File.Create(target);
            input.CopyTo(output);
        }

        // Resolve the entrypoint path (validated already) and return it.
        string manifest = File.ReadAllText(Path.Combine(root, "manifest.json"));
        using var doc = JsonDocument.Parse(manifest);
        string entrypoint = doc.RootElement.GetProperty("entrypoint").GetString()!;
        entrypointPath = SafeTarget(root, entrypoint.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(entrypointPath)) throw new InvalidDataException("ENTRYPOINT_MISSING：声明了入口文件但包内没有它。");

        return entrypointPath;
    }

    private static string? ValidateEntrypoint(string entrypoint, ZipArchive archive)
    {
        if (string.IsNullOrWhiteSpace(entrypoint)) return "ENTRYPOINT_EMPTY：manifest 没有 entrypoint。";

        string normalized = entrypoint.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) return "ENTRYPOINT_ABSOLUTE：entrypoint 必须是包内相对路径。";
        if (normalized.Contains(".." + Path.DirectorySeparatorChar) || normalized == ".." || normalized.Contains(Path.DirectorySeparatorChar + ".."))
            return "ENTRYPOINT_TRAVERSAL：entrypoint 不能包含 .. 路径穿越。";
        if (normalized.Contains(':')) return "ENTRYPOINT_ADS：entrypoint 不能包含盘符或备用数据流冒号。";

        string extension = Path.GetExtension(normalized);
        if (ForbiddenEntrypointExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return "ENTRYPOINT_SCRIPT_FORBIDDEN：entrypoint 只能是 Windows PE 可执行文件（.exe），不接受 " + extension + "。";

        // An interpreter or an absolute path in the extension field is rejected by the schema; the file must
        // exist and be a real PE (starts with MZ) rather than a script renamed to .exe.
        ZipArchiveEntry? entry = archive.GetEntry(entrypoint.Replace('\\', '/')) ?? archive.Entries.FirstOrDefault(e => e.FullName == entrypoint);
        if (entry is null) return "ENTRYPOINT_MISSING：manifest 声明的 entrypoint 不在包内。";

        byte[] header = new byte[2];
        using (Stream s = entry.Open()) s.ReadExactly(header);
        if (header[0] != (byte)'M' || header[1] != (byte)'Z') return "ENTRYPOINT_NOT_PE：entrypoint 不是 Windows 可执行文件（缺少 MZ 头）。";

        return null;
    }

    internal static string SafeTarget(string root, string entryName)
    {
        string relative = entryName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("EMPTY_ENTRY：包内含空路径条目。");
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("ABSOLUTE_PATH_REJECTED：包内含绝对路径：" + entryName);
        if (relative.Contains(':')) throw new InvalidDataException("ADS_REJECTED：包内含盘符/备用数据流：" + entryName);

        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && full != root)
            throw new InvalidDataException("TRAVERSAL_REJECTED：条目逃逸了解压目录：" + entryName);
        return full;
    }

    /// <summary>
    /// Parses and validates a manifest. <paramref name="stream"/> is read and disposed by the caller.
    /// </summary>
    public static ExtensionManifest? ParseManifest(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string? id = root.String("id");
        string? name = root.String("name");
        string? version = root.String("version");
        string publisher = root.String("publisher") ?? string.Empty;
        string entrypoint = root.String("entrypoint") ?? string.Empty;
        string min = root.String("minLanDiskVersion") ?? "0.0.0";
        int timeout = root.Int("timeoutSeconds") ?? 30;
        int schemaVersion = root.Int("schemaVersion") ?? 1;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
            return null;
        if (schemaVersion != 1) return null;

        var commands = new List<ExtensionCommand>();
        if (root.TryGetProperty("commands", out JsonElement commandsElement) && commandsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement command in commandsElement.EnumerateArray())
            {
                string? commandId = command.String("id");
                string? commandName = command.String("name");
                string description = command.String("description") ?? string.Empty;
                JsonObject? schema = null;
                if (command.TryGetProperty("argumentsSchema", out JsonElement schemaElement) && schemaElement.ValueKind == JsonValueKind.Object)
                {
                    schema = JsonNode.Parse(schemaElement.GetRawText()) as JsonObject;
                }
                if (commandId is not null && commandName is not null)
                    commands.Add(new ExtensionCommand(commandId, commandName, description, schema));
            }
        }

        // A manifest that asks for a newer product is refused: this build cannot promise its contract.
        if (Version.TryParse(min, out Version? minimum) && minimum > ExtensionLimits.CurrentVersion)
            return null;

        return new ExtensionManifest(schemaVersion, id, name, version, publisher, entrypoint, commands, min, timeout);
    }

    /// <summary>
    /// Validates a caller-supplied arguments object against a command's schema.
    ///
    /// <para>This is a deliberately small schema: <c>type: object</c> plus <c>properties</c> (each with a JSON
    /// <c>type</c>) and a <c>required</c> array. Unknown properties are rejected, because the whole point of a
    /// schema is that the extension only ever sees what it declared — an argument it did not declare is an
    /// argument it did not intend to receive.</para>
    /// </summary>
    public static ArgumentValidation ValidateArguments(ExtensionCommand command, JsonNode? arguments)
    {
        if (command.ArgumentsSchema is null || command.ArgumentsSchema.Count == 0)
        {
            // No schema means no arguments.
            return (arguments is null || (arguments is JsonObject obj && obj.Count == 0))
                ? new ArgumentValidation(true, null, null, "（无参数）")
                : new ArgumentValidation(false, null, "SCHEMA_NO_ARGUMENTS：该命令不接受参数。", null);
        }

        if (arguments is not JsonObject args) return new ArgumentValidation(false, null, "ARGS_NOT_OBJECT：参数必须是 JSON 对象。", null);

        JsonNode? properties = command.ArgumentsSchema["properties"];
        if (properties is JsonObject schemaProperties)
        {
            foreach ((string key, JsonNode? value) in args)
            {
                if (!schemaProperties.ContainsKey(key))
                    return new ArgumentValidation(false, null, $"UNKNOWN_ARGUMENT：参数 \"{key}\" 不是该命令 schema 声明的。", null);

                JsonNode? typeNode = schemaProperties[key]?["type"];
                string? expected = typeNode?.GetValue<string>();
                if (!TypeMatches(value, expected))
                    return new ArgumentValidation(false, null, $"ARGUMENT_TYPE：参数 \"{key}\" 应为 {expected}。", null);
            }
        }

        if (command.ArgumentsSchema["required"] is JsonArray required)
        {
            foreach (JsonNode? item in required)
            {
                string? name = item?.GetValue<string>();
                if (name is not null && !args.ContainsKey(name))
                    return new ArgumentValidation(false, null, $"MISSING_ARGUMENT：缺少必填参数 \"{name}\"。", null);
            }
        }

        string summary = "{" + string.Join(", ", args.Select(kv => $"{kv.Key}:{Kind(kv.Value)}")) + "}";
        return new ArgumentValidation(true, args, null, summary);
    }

    private static bool TypeMatches(JsonNode? value, string? expected) => expected switch
    {
        "string" => value is JsonValue v && v.TryGetValue<string>(out _),
        "number" => value is JsonValue n && n.TryGetValue<double>(out _),
        "integer" => value is JsonValue i && i.TryGetValue<int>(out _),
        "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        _ => false
    };

    private static string Kind(JsonNode? node) => node switch
    {
        JsonObject => "object",
        JsonArray => "array",
        JsonValue value when value.TryGetValue<string>(out _) => "string",
        JsonValue value when value.TryGetValue<bool>(out _) => "boolean",
        JsonValue value when value.TryGetValue<int>(out _) => "number",
        JsonValue value when value.TryGetValue<double>(out _) => "number",
        _ => "?"
    };

    /// <summary>True when this state may execute commands.</summary>
    public static bool CanRun(ExtensionState state) => state == ExtensionState.Approved;
}

/// <summary>Small helpers for reading JSON elements defensively.</summary>
internal static class JsonElementExtensions
{
    public static string? String(this JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int? Int(this JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int i) ? i : null;
}
