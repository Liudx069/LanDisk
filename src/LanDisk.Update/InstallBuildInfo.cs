using System.Text.Json;

namespace LanDisk.Update;

/// <summary>
/// The build identity written into an installation directory as <c>build-info.json</c>.
///
/// It is a normal, signed payload file rather than installer metadata: an update has to replace it,
/// otherwise the directory would keep claiming the version it was first installed at. Because both
/// the running assembly and the update manifest also carry a version and a commit, the health check
/// can insist that all three agree — a disagreement means the update did not land as intended.
/// </summary>
public sealed class InstallBuildInfo
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = UpdateManifest.ProductName;
    public string Version { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public string BuiltAt { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public bool SelfContained { get; set; }
}

/// <summary>Reads and writes <c>build-info.json</c> in an installation directory.</summary>
public static class InstallBuildInfoFile
{
    public const string FileName = "build-info.json";

    private static readonly JsonSerializerOptions Json = new(UpdatePackage.SerializerOptions);

    public static string PathFor(string installRoot) => Path.Combine(UpdateLayout.Normalize(installRoot), FileName);

    public static InstallBuildInfo? TryRead(string installRoot)
    {
        try
        {
            string path = PathFor(installRoot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<InstallBuildInfo>(File.ReadAllText(path), Json);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Write(string installRoot, InstallBuildInfo info)
    {
        string path = PathFor(installRoot);
        Directory.CreateDirectory(UpdateLayout.Normalize(installRoot));
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(info, Json));
        File.Move(temp, path, overwrite: true);
    }
}
