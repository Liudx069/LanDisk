namespace LanDisk.Core.State;

/// <summary>
/// Decides which port the host must be restarted on after an update.
///
/// <para><b>Why this is a named decision and not an inline expression.</b> The updater used to choose the
/// stored <c>settings.json</c> value over the port the running host handed it, so a host that had been started
/// with <c>--port 18891</c> came back on 8891. The updater then health-checked the port it had been told about,
/// got a refusal, and rolled the update back — meaning an installation on non-default ports could never
/// complete an update at all. The precedence is therefore written down once, tested, and used at the call
/// site, instead of being an argument order that is easy to get backwards.</para>
///
/// <para><b>The order.</b> An explicit override from the caller wins; then the live value the running host
/// handed over; only then the value stored on disk. Stored settings describe how the product was last
/// <em>configured</em>; the live value describes how it is actually <em>serving</em>, and an update must put
/// the service back exactly where it was.</para>
/// </summary>
public static class UpdatePortChoice
{
    /// <summary>
    /// Resolves one port. A non-positive value means "not supplied", which is how both the command line and the
    /// settings file express absence, so the next source in the chain is used.
    /// </summary>
    public static int Resolve(int explicitOverride, int liveFromHost, int storedInSettings)
    {
        if (explicitOverride > 0) return explicitOverride;
        if (liveFromHost > 0) return liveFromHost;
        return storedInSettings > 0 ? storedInSettings : 0;
    }

    /// <summary>
    /// True when the resolved values differ from what was stored, i.e. when this update would have moved the
    /// service under the old precedence. Used by the acceptance run to make the difference visible.
    /// </summary>
    public static bool MovedAwayFromStored(int liveFromHost, int storedInSettings)
        => liveFromHost > 0 && storedInSettings > 0 && liveFromHost != storedInSettings;
}
