using LanDisk.Setup.Visual;

namespace LanDisk.Tests;

/// <summary>
/// ANIM13 — what the rack animation actually does when stages are applied.
///
/// <para><b>Why not just count lit LEDs.</b> <c>LitLedCount</c> says how many LEDs *should* be on; it says
/// nothing about whether an already-finished band was replayed, or whether the band that just completed ever
/// showed its ignition. Both of those were real: the settle pulse was a second <c>BeginAnimation</c> on the
/// same property, which replaces the first — so the completing band never rose — and a repeated stage
/// notification re-ran the rise for bands that were already lit, dropping them to the base value (0) first.</para>
///
/// <para>WPF objects have thread affinity, so the rack is built and exercised on an STA thread and only plain
/// values are asserted outside it.</para>
/// </summary>
public sealed class RackAnimationTests
{
    /// <summary>Builds the rack on an STA thread, runs the body, and rethrows whatever it threw.</summary>
    private static void OnRack(Action<RackVisual> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(new RackVisual()); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    // ---------------------------------------------------------------- 动画本身的形状

    [Fact]
    public void The_ignition_curve_is_present()
    {
        IReadOnlyList<(double Ms, double Opacity)> schedule = RackVisual.RiseSchedule(90, 0, 4, pulseWhenDone: false);

        Assert.Equal(0.25, schedule[0].Opacity, 3);
        Assert.Equal(0.70, schedule[1].Opacity, 3);
        Assert.Equal(1.00, schedule[2].Opacity, 3);
        // 单调点亮：时间递增
        Assert.True(schedule[0].Ms < schedule[1].Ms && schedule[1].Ms < schedule[2].Ms);
    }

    /// <summary>
    /// The settle pulse must live in the <b>same</b> schedule as the ignition. When it was a separate
    /// animation the completing band sat dark for the whole delay and then jumped to full.
    /// </summary>
    [Fact]
    public void The_settle_pulse_is_part_of_the_same_schedule_as_the_ignition()
    {
        IReadOnlyList<(double Ms, double Opacity)> schedule = RackVisual.RiseSchedule(90, 0, 4, pulseWhenDone: true);

        Assert.True(schedule.Count >= 6);
        // 点亮先完成……
        Assert.Equal(1.00, schedule[2].Opacity, 3);
        // ……脉冲在点亮结束之后，且确实回落再回到 1.0
        Assert.True(schedule[3].Ms > schedule[2].Ms, "脉冲必须排在点亮之后");
        Assert.Equal(1.00, schedule[3].Opacity, 3);
        Assert.Equal(0.78, schedule[4].Opacity, 3);
        Assert.Equal(1.00, schedule[5].Opacity, 3);
        Assert.True(schedule[5].Ms > schedule[4].Ms);
    }

    [Fact]
    public void A_band_that_did_not_just_finish_gets_no_pulse()
    {
        IReadOnlyList<(double Ms, double Opacity)> schedule = RackVisual.RiseSchedule(90, 0, 4, pulseWhenDone: false);
        Assert.Equal(3, schedule.Count);
        Assert.DoesNotContain(schedule, k => Math.Abs(k.Opacity - 0.78) < 0.001);
    }

    // ---------------------------------------------------------------- 阶段幂等性

    [Fact]
    public void Each_band_lights_exactly_once_across_the_stage_sequence()
    {
        OnRack(rack =>
        {
            for (int stage = 1; stage <= 6; stage++) rack.ApplyStage(stage, animate: true);

            for (int group = 0; group < RackVisual.GroupCount; group++)
            {
                Assert.Equal(1, rack.AnimationStarts(group));
            }
        });
    }

    /// <summary>
    /// A repeated notification for the same stage must be a no-op for bands that are already lit:
    /// they must not re-darken and must not replay their ignition.
    /// </summary>
    [Fact]
    public void Repeating_the_same_stage_does_not_replay_finished_bands()
    {
        OnRack(rack =>
        {
            rack.ApplyStage(3, animate: true);
            int[] after = [.. Enumerable.Range(0, RackVisual.GroupCount).Select(rack.AnimationStarts)];

            rack.ApplyStage(3, animate: true);
            rack.ApplyStage(3, animate: true);
            rack.ApplyStage(2, animate: true);   // 回退通知同样不得重播

            for (int group = 0; group < RackVisual.GroupCount; group++)
            {
                Assert.Equal(after[group], rack.AnimationStarts(group));
            }
        });
    }

    [Fact]
    public void Moving_to_the_next_stage_leaves_lit_bands_alone()
    {
        OnRack(rack =>
        {
            rack.ApplyStage(2, animate: true);
            int group0 = rack.AnimationStarts(0);

            rack.ApplyStage(3, animate: true);
            rack.ApplyStage(4, animate: true);

            Assert.Equal(group0, rack.AnimationStarts(0));    // 第一组只点亮过一次
            Assert.Equal(1, rack.AnimationStarts(1));
            Assert.Equal(1, rack.AnimationStarts(2));
        });
    }

    // ---------------------------------------------------------------- 失败与 Reduce Motion

    /// <summary>Failure lights the status strip and must not power on bands that had not been reached.</summary>
    [Fact]
    public void Failure_holds_what_is_lit_and_does_not_light_later_bands()
    {
        OnRack(rack =>
        {
            rack.ApplyStage(3, animate: true);
            int group2 = rack.AnimationStarts(2);
            int lit = rack.LitLedCount;
            rack.ApplyError(3);

            Assert.Equal(group2, rack.AnimationStarts(2));    // 后续组没有被点亮
            Assert.Equal(0, rack.AnimationStarts(3));
            // 失败保持已亮的部分，不会把已经点亮的组也清掉
            Assert.True(rack.LitLedCount >= lit, "失败不应让已点亮的灯变暗");
        });
    }

    [Fact]
    public void Reduce_motion_still_advances_the_rack()
    {
        OnRack(rack =>
        {
            rack.ReduceMotion = true;
            for (int stage = 1; stage <= 6; stage++) rack.ApplyStage(stage, animate: true);

            for (int group = 0; group < RackVisual.GroupCount; group++)
            {
                Assert.Equal(1, rack.AnimationStarts(group));
            }
        });
    }

    /// <summary>Nothing ever powers back off: the picture is monotonic in the stage number.</summary>
    [Fact]
    public void Lit_count_is_monotonic_in_the_stage()
    {
        OnRack(rack =>
        {
            int previous = -1;
            for (int stage = 1; stage <= 6; stage++)
            {
                rack.ApplyStage(stage, animate: false);
                Assert.True(rack.LitLedCount >= previous, $"stage {stage} 的亮灯数比上一阶段少");
                previous = rack.LitLedCount;
            }
            Assert.True(previous > 0);
        });
    }
}
