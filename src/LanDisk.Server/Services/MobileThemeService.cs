using System.Text.Json;
using LanDisk.Core.Configuration;

namespace LanDisk.Server.Services;

/// <summary>The mobile theme choices, as data, so both the HTTP endpoint and the native window read the same
/// thing.</summary>
public sealed record MobileThemeChoices(
    string Current,
    int Revision,
    ThemeProfile? Custom,
    IReadOnlyList<ThemeProfile> Profiles);

/// <summary>The appearance the phones should use, decided on the Windows side.
///
/// <para><b>Why the server owns it.</b> The operator asked for "Windows can set the iPhone theme", which
/// means the phone must be able to pick it up without the operator touching the phone. It is therefore
/// stored next to the installation (not in a device-keyed preference), served over the authenticated API,
/// and versioned: a phone that knows revision N re-fetches when a heartbeat reports something newer.</para>
///
/// <para><b>What it is not.</b> Not CSS, not a script, not a stylesheet: a profile is a fixed set of colour
/// roles that the phone maps onto its own variables. Nothing the operator types can become executable
/// content in the phone's browser, and a profile that fails the contrast check is refused rather than
/// stored.</para>
/// </summary>
public sealed class MobileThemeService
{
    public const string FileName = "mobile-theme.json";

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private MobileThemeState _state;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public MobileThemeService(string dataRoot, Action<string>? log = null)
    {
        _path = Path.Combine(dataRoot, FileName);
        _log = log;
        _state = Load();
    }

    /// <summary>Bumped on every accepted change so a phone can tell whether its copy is stale.</summary>
    public int Revision { get { lock (_gate) return _state.Revision; } }

    /// <summary>The profile the phones should be using right now, resolved for an explicit appearance when
    /// the profile follows the device.</summary>
    public ThemeProfile Resolve(bool devicePrefersDark)
    {
        lock (_gate)
        {
            if (string.Equals(_state.ProfileId, "custom", StringComparison.OrdinalIgnoreCase) && _state.Custom is not null)
            {
                return _state.Custom.WithId("custom", _state.Custom.Name);
            }

            return ThemeCatalog.Resolve(_state.ProfileId, devicePrefersDark, ThemeSurface.Mobile);
        }
    }

    public string ProfileId { get { lock (_gate) return _state.ProfileId; } }

    /// <summary>
    /// True when the operator chose "follow the device": the phone then picks light or dark on its own, and
    /// the two resolved profiles below differ. Everything else is a decision the operator already made.
    /// </summary>
    public bool FollowsDevice
    {
        get
        {
            lock (_gate)
            {
                if (string.Equals(_state.ProfileId, "custom", StringComparison.OrdinalIgnoreCase)) return false;
                return ThemeCatalog.Find(_state.ProfileId, ThemeSurface.Mobile)?.IsSystem ?? false;
            }
        }
    }

    /// <summary>The selectable profiles for the Windows side to offer, with the current choice marked.</summary>
    public object Choices()
    {
        MobileThemeChoices choices = ChoicesModel();
        return new
        {
            current = choices.Current,
            revision = choices.Revision,
            custom = choices.Custom,
            profiles = choices.Profiles.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                appearance = p.Appearance,
                roles = p.Roles
            }).ToList()
        };
    }

    /// <summary>The same data as <see cref="Choices"/>, typed, for the native window to render directly.</summary>
    public MobileThemeChoices ChoicesModel()
    {
        lock (_gate)
        {
            List<ThemeProfile> profiles = [.. ThemeCatalog.Mobile];
            if (_state.Custom is not null) profiles.Add(_state.Custom.WithId("custom", _state.Custom.Name));
            return new MobileThemeChoices(_state.ProfileId, _state.Revision, _state.Custom, profiles);
        }
    }

    /// <summary>
    /// Stores a new choice. Refuses anything that is not a known profile or a valid custom profile.
    /// Returns the validation result so the caller can tell the operator exactly what was wrong.
    /// </summary>
    public (bool Ok, ThemeValidation Validation, string Message) Set(
        string? profileId,
        string? customAccent,
        string? customBackground,
        string? customSurface,
        string? customName)
    {
        string id = string.IsNullOrWhiteSpace(profileId) ? ThemeCatalog.DefaultId(ThemeSurface.Mobile) : profileId!.Trim();
        ThemeProfile? custom = null;
        ThemeProfile? candidate;

        if (string.Equals(id, "custom", StringComparison.OrdinalIgnoreCase))
        {
            custom = ThemeCatalog.Custom(customAccent, customBackground, customSurface, customName);
            candidate = custom;
        }
        else
        {
            candidate = ThemeCatalog.Find(id, ThemeSurface.Mobile);
            if (candidate is null)
            {
                return (false, new ThemeValidation(false, 0, string.Empty, ["未知主题：" + id]),
                    "未知主题：" + id);
            }
        }

        ThemeValidation validation = ThemeCatalog.Validate(candidate);
        if (!validation.Ok)
        {
            return (false, validation, "该配色不满足可读性要求，未保存：" + validation.Summary);
        }

        lock (_gate)
        {
            _state = new MobileThemeState
            {
                ProfileId = id,
                Custom = custom,
                Revision = _state.Revision + 1,
                UpdatedAt = DateTimeOffset.Now
            };
            Save(_state);
        }

        _log?.Invoke($"手机主题已更新为 {candidate.Name}（对比度最低 {validation.WorstBody:0.0}:1）");
        return (true, validation, candidate.Name);
    }

    private MobileThemeState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new MobileThemeState();
            MobileThemeState? state = JsonSerializer.Deserialize<MobileThemeState>(File.ReadAllText(_path), Json);
            if (state is null) return new MobileThemeState();
            // A file naming a theme this build does not know falls back to the default rather than
            // failing to start: an upgrade must never leave the phone unstyled.
            if (!string.Equals(state.ProfileId, "custom", StringComparison.OrdinalIgnoreCase)
                && ThemeCatalog.Find(state.ProfileId, ThemeSurface.Mobile) is null)
            {
                state.ProfileId = ThemeCatalog.DefaultId(ThemeSurface.Mobile);
            }
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new MobileThemeState();
        }
    }

    private void Save(MobileThemeState state)
    {
        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("保存手机主题失败：" + ex.Message);
        }
    }

    private sealed class MobileThemeState
    {
        public string ProfileId { get; set; } = ThemeCatalog.DefaultId(ThemeSurface.Mobile);
        public ThemeProfile? Custom { get; set; }
        public int Revision { get; set; } = 1;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    }
}
