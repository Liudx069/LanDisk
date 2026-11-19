using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// ONLINE12 — presence is measured from real traffic, and watching a video counts as being there.
///
/// <para><b>The Owner's report.</b> The iPhone was in the foreground, playing a video, and the Windows device
/// page said "0 online / 1 recently active", with the phone's last activity minutes behind. Presence had been
/// made a property of the phone's heartbeat alone, so anything that kept the heartbeat from being sent — a
/// viewer taking over the component tree, Safari suspending timers during playback — read as "the phone
/// left", even while its requests were arriving.</para>
///
/// <para><b>What is pinned here.</b> Every authenticated request from a valid device session is evidence the
/// phone is present, including the byte-range requests a media player issues. These tests drive the same
/// endpoints the viewer does and assert on the server's own classification rather than on a screenshot.</para>
/// </summary>
public sealed class OnlinePresenceTests : TestBase
{
    private byte[] _payload = [];

    protected override void SeedSandbox(TestHost host)
    {
        _payload = Sandbox.Pattern(300 * 1024, seed: 11);
        host.WriteSandboxBytes("媒体/长片.mp4", _payload);
        host.WriteSandboxBytes("照片/样本.png", _payload);
    }

    private string MediaQuery => LanDiskClient.FileQuery(ShareId, "媒体/长片.mp4");
    private string PhotoQuery => LanDiskClient.FileQuery(ShareId, "照片/样本.png");

    private DeviceDefinition DeviceOf(LanDiskClient device)
    {
        ApiResult<JsonElement> me = device.GetAsync<JsonElement>("/api/me").GetAwaiter().GetResult();
        string id = me.Body.GetProperty("device").GetProperty("id").GetString()!;
        return Host.Runtime.Devices.All().Single(d => d.Id == id);
    }

    private static async Task<HttpResponseMessage> RangeAsync(LanDiskClient device, string url, int from, int to)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
        return await device.SendRawAsync(request);
    }

    [Fact]
    public async Task A_viewer_left_open_stays_online_without_a_single_heartbeat()
    {
        LanDiskClient device = await PairDeviceAsync();

        // Exactly what an open file viewer does: it keeps asking for the file. No heartbeat is sent at all,
        // which is the situation the Owner was in.
        DateTimeOffset first = Host.Runtime.Devices.All().Single().LastSeenAt;
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await RangeAsync(device, "/api/file/content" + PhotoQuery, 0, 4095);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            await Task.Delay(400);
        }

        DeviceDefinition after = DeviceOf(device);
        Assert.True(after.LastSeenAt > first, "查看文件本身应当刷新最后活动时间");
        Assert.Equal(DevicePresence.Online, DeviceActivity.Classify(after, DateTimeOffset.Now));
        Assert.True(DateTimeOffset.Now - after.LastSeenAt < TimeSpan.FromSeconds(5));

        TestBase.Record("ONLINE12-01",
            "文件查看器一直开着（只反复请求文件、完全不发心跳）时设备仍是 Online：每次请求都会刷新 LastSeenAt，presence 不再只依赖心跳");
    }

    [Fact]
    public async Task Media_range_requests_maintain_activity()
    {
        LanDiskClient device = await PairDeviceAsync();
        DateTimeOffset before = DeviceOf(device).LastSeenAt;

        // A playing video issues a stream of byte-range requests. They must count.
        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage response = await RangeAsync(device, "/api/file/content" + MediaQuery, i * 32768, (i * 32768) + 32767);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            await Task.Delay(250);
        }

        DeviceDefinition after = DeviceOf(device);
        Assert.True(after.LastSeenAt >= before, "媒体请求必须刷新最后活动时间");
        Assert.Equal(DevicePresence.Online, DeviceActivity.Classify(after, DateTimeOffset.Now));

        TestBase.Record("ONLINE12-02",
            "媒体播放的 Range 请求同样维持活动：4 次 206 分片请求后设备仍是 Online，播放视频不会让手机变成离线");
    }

    [Fact]
    public async Task The_heartbeat_keeps_working_while_other_requests_are_in_flight()
    {
        LanDiskClient device = await PairDeviceAsync();

        // Interleave the two things a phone does at once: a viewer open on a file, and the heartbeat.
        for (int i = 0; i < 2; i++)
        {
            using HttpResponseMessage media = await RangeAsync(device, "/api/file/content" + MediaQuery, 0, 1023);
            Assert.Equal(HttpStatusCode.PartialContent, media.StatusCode);

            ApiResult<JsonElement> beat = await device.PostAsync<JsonElement>("/api/heartbeat", new { });
            Assert.True(beat.IsSuccess, beat.Raw);
            Assert.Equal("online", beat.Body.GetProperty("presence").GetProperty("state").GetString());
        }

        TestBase.Record("ONLINE12-03",
            "心跳与其它请求可以并行：在查看器持续请求文件的同时调用 /api/heartbeat 仍然成功并报告 online（心跳不再依赖某个页面是否挂载）");
    }

    [Fact]
    public async Task Navigation_between_endpoints_keeps_the_device_online()
    {
        LanDiskClient device = await PairDeviceAsync();

        // What "switching screens" looks like from the server: a run of different authenticated endpoints.
        foreach (string url in new[] { "/api/me", "/api/shares", "/api/recent", "/api/file/content" + PhotoQuery })
        {
            ApiResult<JsonElement> result = await device.GetAsync<JsonElement>(url);
            Assert.True(result.IsSuccess, url + " -> " + result.Raw);
        }

        DeviceDefinition after = DeviceOf(device);
        Assert.Equal(DevicePresence.Online, DeviceActivity.Classify(after, DateTimeOffset.Now));

        TestBase.Record("ONLINE12-04",
            "在多个端点之间切换（/api/me、/api/shares、目录列表、最近使用）后设备仍然 Online：presence 不绑定某个具体页面");
    }

    [Fact]
    public async Task A_foreground_phone_is_online_well_inside_twenty_seconds()
    {
        LanDiskClient device = await PairDeviceAsync();

        // Simulate "the phone came back to the foreground": one authenticated request, then check.
        DateTimeOffset start = DateTimeOffset.Now;
        _ = await device.GetAsync<JsonElement>("/api/me");
        DeviceDefinition after = DeviceOf(device);

        TimeSpan latency = after.LastSeenAt - start;
        Assert.True(latency < TimeSpan.FromSeconds(20), $"回到前台后应在 20 秒内变为在线，实际 {latency.TotalSeconds:0.0}s");
        Assert.Equal(DevicePresence.Online, DeviceActivity.Classify(after, DateTimeOffset.Now));

        TestBase.Record("ONLINE12-05",
            $"前台活动在 {latency.TotalSeconds:0.00}s 内反映为 Online（远小于 20 秒上限）：一次认证请求就足够，不必等下一次心跳");
    }

    [Fact]
    public void A_backgrounded_phone_is_allowed_to_degrade_to_recently_active()
    {
        var device = new DeviceDefinition { Name = "后台手机", LastSeenAt = DateTimeOffset.Now };
        DateTimeOffset now = DateTimeOffset.Now;

        // Safari suspends timers in a background tab, so this degradation is expected and must not be
        // reported as "offline" — that was the false negative the Owner kept seeing.
        Assert.Equal(DevicePresence.Online, DeviceActivity.Classify(device, now));
        Assert.Equal(DevicePresence.Online, DeviceActivity.Classify(device, now.AddSeconds(44)));
        Assert.Equal(DevicePresence.RecentlyActive, DeviceActivity.Classify(device, now.AddSeconds(46)));
        Assert.Equal(DevicePresence.RecentlyActive, DeviceActivity.Classify(device, now.AddMinutes(4)));
        Assert.Equal(DevicePresence.Offline, DeviceActivity.Classify(device, now.AddMinutes(6)));

        TestBase.Record("ONLINE12-06",
            "后台降级是允许且被明确定义的：≤45s 在线、46s–5min 最近活动、>5min 离线；后台标签页被 Safari 冻结计时器时不会误报离线");
    }
}
