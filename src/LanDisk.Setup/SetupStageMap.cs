namespace LanDisk.Setup;

/// <summary>
/// Maps the engine's fine-grained steps onto the console's six nodes, as a pure function.
///
/// <para><b>Why a separate, testable table.</b> The installer used to map steps to nodes inline, and a
/// reordering of the engine turned the node sequence into 0 → 2 → 1 — a node lighting up, then one before it
/// lighting up, which reads as a broken progress picture. A pure mapping, plus a monotonic assertion over the
/// engine's real step order (see the SETUP17 gates), makes that impossible to reintroduce silently.</para>
///
/// <para>Every <see cref="SetupStep"/> must have an explicit entry here; a future step that nobody mapped is
/// a test failure, not a silent "same as the previous step".</para>
/// </summary>
public static class SetupStageMap
{
    /// <summary>The six user-facing stages, in order.</summary>
    public static readonly (string Number, string Title, string Detail)[] Stages =
    [
        ("01", "验证安装包", "校验内置载荷与文件清单"),
        ("02", "停止旧版本 / 准备程序", "结束运行中的实例，写入程序文件"),
        ("03", "安装程序并保留用户数据", "程序就位，设备 / 共享 / 证书原样保留"),
        ("04", "配置本地服务", "快捷方式与卸载注册"),
        ("05", "配置 HTTPS 与防火墙", "仅专用网络的防火墙规则"),
        ("06", "启动并确认服务 Ready", "等待服务真正开始响应")
    ];

    public static int StageCount => Stages.Length;

    /// <summary>The node index (0-based) a given step belongs to.</summary>
    public static int StageIndex(SetupStep step) => step switch
    {
        SetupStep.Inspect => 0,
        SetupStep.VerifyPayload => 0,

        SetupStep.StopRunning => 1,
        SetupStep.InstallFiles => 1,

        SetupStep.PreserveUserData => 2,

        SetupStep.Shortcuts => 3,
        SetupStep.Register => 3,

        SetupStep.Firewall => 4,

        SetupStep.Launch => 5,
        SetupStep.Ready => 5,

        _ => throw new InvalidOperationException($"SetupStep.{step} 没有映射到控制台阶段（SetupStageMap 不完整）")
    };

    /// <summary>
    /// True when the engine's declared step order maps to a non-decreasing node sequence — the invariant the
    /// progress picture depends on. The engine's order is the order in which it invokes <c>_step</c>; if that
    /// ever produces a descending index, the SETUP17-01 gate fails.
    /// </summary>
    public static bool IsMonotonic(IEnumerable<SetupStep> steps)
    {
        int previous = -1;
        foreach (SetupStep step in steps)
        {
            int index = StageIndex(step);
            if (index < previous) return false;
            previous = index;
        }
        return true;
    }

    /// <summary>
    /// The exact step order the engine emits, declared once so both the engine and the tests can rely on it.
    /// Keeping it here — next to the mapping — means the two cannot be edited separately.
    /// </summary>
    public static IReadOnlyList<SetupStep> EngineStepOrder { get; } =
    [
        SetupStep.Inspect,
        SetupStep.VerifyPayload,
        SetupStep.StopRunning,
        SetupStep.InstallFiles,
        SetupStep.PreserveUserData,
        SetupStep.Shortcuts,
        SetupStep.Register,
        SetupStep.Firewall,
        SetupStep.Launch,
        SetupStep.Ready
    ];
}

/// <summary>
/// The node-strip state machine, as a pure class so "a node only lights when its work finished", "an error
/// stops every later node" and "indices never go backwards" are testable without a window.
/// </summary>
public sealed class SetupProgressState
{
    public int Completed { get; private set; } = -1;
    public int Active { get; private set; }
    public int ErrorStage { get; private set; } = -1;

    /// <summary>Marks a stage finished. Ignored once an error has been recorded at or before it.</summary>
    public void Complete(int stage)
    {
        if (ErrorStage >= 0 && stage >= ErrorStage) return;
        Completed = Math.Max(Completed, stage);
        Active = Math.Min(SetupStageMap.StageCount - 1, stage + 1);
    }

    public void SetActive(int stage) => Active = Math.Clamp(stage, 0, SetupStageMap.StageCount - 1);

    /// <summary>Records the failing stage: it renders as an error and no later node may light.</summary>
    public void SetError(int stage)
    {
        ErrorStage = Math.Clamp(stage, 0, SetupStageMap.StageCount - 1);
        Completed = Math.Min(Completed, ErrorStage - 1);
    }

    /// <summary>True when this node shows as finished (green): its work completed and it precedes any error.</summary>
    public bool IsLit(int stage) => stage <= Completed && (ErrorStage < 0 || stage < ErrorStage);

    /// <summary>True when this node shows as failed (red).</summary>
    public bool IsError(int stage) => ErrorStage >= 0 && stage == ErrorStage;
}
