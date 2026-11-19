namespace LanDisk.Core.Security;

/// <summary>
/// The one place a device name is cleaned.
///
/// <para>Lives in Core because both the HTTP endpoints and the native window accept a name for the same
/// device: when the two cleaned it differently, a name that was refused in the browser could be stored
/// from the desktop, and the same edit produced two different devices depending on where it was made.</para>
/// </summary>
public static class DeviceNames
{
    public const int MaxLength = 64;

    /// <summary>Trims, bounds the length and removes control characters. Null when nothing usable is left.</summary>
    public static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string trimmed = name.Trim();
        if (trimmed.Length > MaxLength) trimmed = trimmed[..MaxLength];
        return new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
    }

    /// <summary>
    /// A name derived from the User-Agent, used when the client did not supply one. Deliberately coarse:
    /// it is a label for a human, not an identification of the device.
    /// </summary>
    public static string Derive(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "未命名设备";
        string ua = userAgent;
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone";
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android 设备";
        if (ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "Mac";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows 设备";
        return "未命名设备";
    }
}
