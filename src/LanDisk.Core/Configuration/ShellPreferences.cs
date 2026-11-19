using System.Text.Json;

namespace LanDisk.Core.Configuration;

/// <summary>
/// Small per-user preferences owned by the desktop shell itself.
///
/// Kept in its own file under the data root rather than in the device-keyed web preferences, and it
/// is part of the update system's managed state, so an upgrade or rollback preserves it.
/// </summary>
public sealed class ShellPreferences
{
    public const string FileName = "shell.json";

    /// <summary>Set once the user has acknowledged the "still running in the tray" notice.</summary>
    public bool TrayNoticeAcknowledged { get; set; }

    /// <summary>Set when the user ticked "不再提示" on that notice.</summary>
    public bool TrayNoticeSuppressed { get; set; }

    /// <summary>
    /// The appearance the user chose: "system", "light" or "dark". Stored by name rather than as an
    /// enum so an older build reading a newer file does not fail to deserialize; unknown values fall
    /// back to <see cref="DefaultTheme"/>.
    /// </summary>
    public string Theme { get; set; } = DefaultTheme;

    public const string DefaultTheme = "system";

    /// <summary>True when the stored value is one this build understands.</summary>
    public bool HasKnownTheme => Theme is "system" or "light" or "dark";

    /// <summary>
    /// The named theme profile the desktop shell uses (see <see cref="ThemeCatalog.Windows"/>), e.g.
    /// <c>follow-system</c>, <c>obsidian</c>, <c>graphite</c>, <c>midnight</c>, <c>arctic</c>, <c>custom</c>.
    ///
    /// <para>Separate from <see cref="Theme"/> so that upgrading cannot lose the older choice: a file
    /// written before 1.7.0 has no profile id, and <see cref="Theme"/> still resolves it to "follow
    /// system" exactly as before.</para>
    /// </summary>
    public string ThemeProfile { get; set; } = "follow-system";

    /// <summary>A custom profile saved by the user (roles as hex strings). Null until one is saved.</summary>
    public ThemeProfile? CustomTheme { get; set; }

    /// <summary>True when <see cref="ThemeProfile"/> names a built-in profile of this build.</summary>
    public bool HasKnownThemeProfile => ThemeCatalog.Find(ThemeProfile) is not null
                                        || string.Equals(ThemeProfile, "custom", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, FileName);

    public static ShellPreferences Load(string dataRoot)
    {
        try
        {
            string path = PathFor(dataRoot);
            if (!File.Exists(path)) return new ShellPreferences();
            return JsonSerializer.Deserialize<ShellPreferences>(File.ReadAllText(path), Json) ?? new ShellPreferences();
        }
        catch (JsonException)
        {
            // A damaged preference file must not stop the shell from starting.
            return new ShellPreferences();
        }
        catch (IOException)
        {
            return new ShellPreferences();
        }
    }

    public void Save(string dataRoot)
    {
        try
        {
            string path = PathFor(dataRoot);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the preference is harmless; failing the shutdown is not.
        }
    }
}
