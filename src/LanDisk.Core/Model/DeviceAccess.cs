namespace LanDisk.Core.Model;

/// <summary>
/// What a paired phone is allowed to do beyond plain file access.
///
/// <para><b>Why these are separate from <see cref="DeviceAccessLevel"/>.</b> File access ("can this phone
/// write into a share") and administrative reach ("can this phone install an extension, run an approved
/// command") are different decisions with very different consequences. Folding them into a single read-only /
/// read-write switch is what makes a product grant far more than the operator intended: "let the phone upload
/// photos" would silently also mean "let the phone run commands".
/// Every phone starts with <see cref="None"/> and stays there until the local operator grants each one.</para>
/// </summary>
[Flags]
public enum DeviceCapability
{
    None = 0,

    /// <summary>Write files into shares even when the device level still says read-only.</summary>
    FileWrite = 1 << 0,

    /// <summary>
    /// <b>Retired.</b> This bit used to grant "read WiFi state and change the machine's wireless connection".
    /// The WiFi feature has been removed from the product, so the bit no longer grants anything and is never
    /// offered or written again. The numeric value is kept only so <c>devices.json</c> written by an earlier
    /// version still deserializes; dropping it would renumber the flags below and silently reinterpret every
    /// existing device record.
    /// </summary>
    RetiredNetworkManage = 1 << 1,

    /// <summary>Upload an extension package for local approval.</summary>
    ExtensionInstall = 1 << 2,

    /// <summary>Run commands that an already approved extension declares.</summary>
    ExtensionExecute = 1 << 3,

    /// <summary>See machine-level diagnostics (operations dashboard details).</summary>
    SystemView = 1 << 4,
}

/// <summary>
/// The permission decision for one request, derived from the <i>current</i> device record every time.
///
/// <para>Nothing caches this. A session cookie proves identity (device id, session epoch, still valid); it
/// never carries authority. That is what makes "changed my mind" take effect on the phone's very next
/// request instead of at the next pairing — in both directions.</para>
/// </summary>
public readonly record struct DevicePermissions(
    bool WriteFiles,
    bool InstallExtensions,
    bool ExecuteExtensions,
    bool ViewSystem)
{
    public static readonly DevicePermissions Admin = new(true, true, true, true);

    public static DevicePermissions For(DeviceDefinition? device)
    {
        if (device is null) return default;
        DeviceCapability caps = device.Capabilities;
        return new DevicePermissions(
            // The legacy level still means "may write files": an installation that was set to read-write
            // before capabilities existed must keep working after the upgrade.
            WriteFiles: device.Level == DeviceAccessLevel.ReadWrite || caps.HasFlag(DeviceCapability.FileWrite),
            InstallExtensions: caps.HasFlag(DeviceCapability.ExtensionInstall),
            ExecuteExtensions: caps.HasFlag(DeviceCapability.ExtensionExecute),
            ViewSystem: caps.HasFlag(DeviceCapability.SystemView));
    }
}

/// <summary>How recently a paired phone was actually heard from.</summary>
public enum DevicePresence
{
    /// <summary>Never seen, or silent for longer than <see cref="DeviceActivity.RecentlyActiveWindow"/>.</summary>
    Offline = 0,

    /// <summary>Heard from within the last few minutes, but not right now.</summary>
    RecentlyActive = 1,

    /// <summary>Heard from within <see cref="DeviceActivity.OnlineWindow"/>.</summary>
    Online = 2,

    /// <summary>Revoked: it is not "offline", it is no longer a device of this installation.</summary>
    Revoked = 3,
}

/// <summary>
/// Presence thresholds in one place, so the policy is testable and the UI never invents its own numbers.
///
/// <para>Safari suspends timers in a backgrounded tab, so a phone that is awake but backgrounded stops
/// sending its 15 second heartbeat. The online window is therefore deliberately wider than the heartbeat
/// interval (three missed beats), and everything between that and five minutes is "recently active" rather
/// than a false "offline".</para>
/// </summary>
public static class DeviceActivity
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan RecentlyActiveWindow = TimeSpan.FromMinutes(5);

    public static DevicePresence Classify(DeviceDefinition device, DateTimeOffset now)
    {
        if (device.Revoked) return DevicePresence.Revoked;
        TimeSpan idle = now - device.LastSeenAt;
        if (idle <= OnlineWindow) return DevicePresence.Online;
        if (idle <= RecentlyActiveWindow) return DevicePresence.RecentlyActive;
        return DevicePresence.Offline;
    }

    /// <summary>The lowercase wire value the UI and the phone both read.</summary>
    public static string ToWire(DevicePresence presence) => presence switch
    {
        DevicePresence.Online => "online",
        DevicePresence.RecentlyActive => "recently-active",
        DevicePresence.Offline => "offline",
        _ => "revoked",
    };
}

/// <summary>
/// Decides whether the shell should open the first-connection tutorial.
///
/// <para>In <c>LanDisk.Core</c> rather than in the window because both directions matter and both are worth
/// pinning without a UI: a fresh install (no device ever paired, no share enabled) should be shown how to
/// connect a phone, and a machine that already has either must never see that tutorial again. The installer's
/// own completion window used to own this decision, which is why the guidance vanished as soon as it was
/// dismissed.</para>
/// </summary>
public static class FirstRunPolicy
{
    public static bool IsFirstRun(int deviceCount, int enabledShareCount)
        => deviceCount == 0 && enabledShareCount == 0;
}