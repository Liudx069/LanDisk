namespace LanDisk.Core.Model;

/// <summary>Who performed an operation. A phone and the machine's own administrator are not the same actor.</summary>
public enum ActorSource
{
    /// <summary>A paired phone (or a browser session holding that phone's credential).</summary>
    Device,

    /// <summary>The Windows management surface on this machine: the native window or the loopback admin page.</summary>
    LocalAdmin,

    /// <summary>LanDisk itself — a scheduled or automatic action with no human behind it.</summary>
    System
}

/// <summary>
/// The actor behind a management operation.
///
/// <para><b>Why this exists.</b> The recycle service used to be handed a <see cref="DeviceDefinition"/>
/// even for work the local administrator did, and the native recycle page passed
/// <c>runtime.Devices.All().First()</c>. That meant restoring from the computer failed outright when no
/// phone had ever been paired, and when one had, the audit log credited that phone with something the
/// person at the keyboard did. The actor is therefore explicit: it is never persisted as a device, and
/// it always says which of the three kinds of actor it is.</para>
/// </summary>
public sealed class OperationActor
{
    public const string LocalAdminId = "local-admin";
    public const string LocalAdminName = "本机管理端";
    public const string SystemId = "system";
    public const string SystemName = "系统";

    public ActorSource Source { get; init; } = ActorSource.LocalAdmin;
    public string Id { get; init; } = LocalAdminId;
    public string Name { get; init; } = LocalAdminName;

    /// <summary>A phone that really is paired on this machine.</summary>
    public static OperationActor FromDevice(DeviceDefinition device) => new()
    {
        Source = ActorSource.Device,
        Id = device.Id,
        Name = device.Name
    };

    /// <summary>The Windows management surface. Not a device, never written to devices.json.</summary>
    public static OperationActor LocalAdmin { get; } = new();

    /// <summary>LanDisk acting on its own.</summary>
    public static OperationActor System { get; } = new()
    {
        Source = ActorSource.System,
        Id = SystemId,
        Name = SystemName
    };

    /// <summary>Lower-case token used in audit detail and in the recycle record.</summary>
    public string SourceToken => Source switch
    {
        ActorSource.Device => "device",
        ActorSource.System => "system",
        _ => "local-admin"
    };

    public bool IsDevice => Source == ActorSource.Device;
}
