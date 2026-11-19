using System.Net;
using System.Text.Json;
using LanDisk.Core.Model;
using LanDisk.Core.Security;
using LanDisk.Server.Services;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// COMPLETE17 — the completion screen must recommend the address that actually worked.
///
/// <para><b>The trap.</b> Confirming a pairing closes the wizard and clears the verified-host hint, because
/// that hint describes one connection attempt. The completion screen is rendered after all of that, so
/// reading the hint at render time would find nothing and fall back to the stable <c>.local</c> name — the
/// very address the phone just failed to reach when a VPN/TUN holds the default route. The recommendation is
/// therefore captured at the moment it is still known and carried on the request that produced it.</para>
/// </summary>
public sealed class PairingCompletionOriginTests : TestBase
{
    [Fact]
    public async Task The_completion_keeps_the_measured_address_after_the_hint_is_cleared()
    {
        // A phone that reached us at the physical address, not at the stable name.
        string physical = Host.Runtime.LanAddresses().FirstOrDefault() ?? "192.168.2.2";
        Host.Runtime.Certificates.EnsureServerCertificate(Host.Runtime.Identity.InstallId, Host.Runtime.Identity.HostName);

        OnboardingService onboarding = Host.Runtime.Onboarding;
        onboarding.Start(port: 0);
        Assert.True(onboarding.IsActive, onboarding.LastError);
        string bootstrapId = onboarding.BootstrapId!;

        Assert.True(Host.Runtime.Onboarding.RecordVerifiedHttpsHost(physical), "物理地址应当被记录为可达 host");
        Assert.Equal(physical, Host.Runtime.Onboarding.VerifiedHttpsHost);

        string token = await Admin.CreatePairingTokenAsync();
        PairingRequest request = Host.Runtime.Pairing.FindByToken(token)!;
        request.State = PairingState.AwaitingLocalConfirmation;
        request.DeviceName = "推荐地址手机";

        OperationOutcome<DeviceDefinition> confirmed = Host.Runtime.Admin.ConfirmPairing(request.Id);
        Assert.True(confirmed.Ok, confirmed.Message);

        // The hint is gone (that attempt is over) ...
        Assert.Null(Host.Runtime.Onboarding.VerifiedHttpsHost);

        // ... but the recommendation survives on the request, and names the measured address.
        AdminOperations.PairingCompletion completion = Host.Runtime.Admin.CompletionOf(request.Id);
        Assert.True(completion.Completed);
        Assert.Equal($"https://{physical}:{Host.Runtime.Settings.HttpsPort}", completion.RecommendedOrigin);
        Assert.NotEqual(Host.Runtime.Identity.HttpsOrigin(Host.Runtime.Settings.HttpsPort), completion.RecommendedOrigin);

        TestBase.Record("COMPLETE17-01",
            $"配对完成信息携带本次真正可达的推荐地址：VerifiedHttpsHost 在确认后被清空，但 CompletionOf().RecommendedOrigin 仍是 https://{physical}:{Host.Runtime.Settings.HttpsPort}（而不是打不开的稳定名称）");
    }

    [Fact]
    public async Task The_stable_name_stays_the_recommendation_when_it_is_what_worked()
    {
        OnboardingService onboarding = Host.Runtime.Onboarding;
        onboarding.Start(port: 0);
        Assert.True(onboarding.IsActive, onboarding.LastError);

        Assert.True(Host.Runtime.Onboarding.RecordVerifiedHttpsHost(Host.Runtime.Identity.HostName));

        string token = await Admin.CreatePairingTokenAsync();
        PairingRequest request = Host.Runtime.Pairing.FindByToken(token)!;
        request.State = PairingState.AwaitingLocalConfirmation;
        request.DeviceName = "稳定名称手机";
        Assert.True(Host.Runtime.Admin.ConfirmPairing(request.Id).Ok);

        AdminOperations.PairingCompletion completion = Host.Runtime.Admin.CompletionOf(request.Id);
        Assert.Equal(Host.Runtime.Identity.HttpsOrigin(Host.Runtime.Settings.HttpsPort), completion.RecommendedOrigin);

        onboarding.Stop("测试结束");
        TestBase.Record("COMPLETE17-02",
            "当稳定名称才是真正可达的地址时，完成页推荐的仍然是稳定名称（不会因为「优先物理 IP」而反过来推荐一个没被证明的地址）");
    }

    [Fact]
    public async Task A_fresh_pairing_without_any_measurement_still_offers_an_address()
    {
        // No probe happened (no hint): the recommendation must still be a usable HTTPS origin rather than
        // an empty field on the completion screen.
        //
        // 前提必须与真实部署一致：只有局域网 HTTPS **确实在服务**时，才有"可用的 HTTPS 地址"可言。
        // 1.7.7 起，LAN 或 HTTPS 关闭时不再推荐一个没有 listener 的地址（缺陷 B 的修复），
        // 因此这里显式打开两者；"关闭时不推荐"由 CloseoutGateTests 的正反例单独覆盖。
        bool lanBefore = Host.Runtime.Settings.LanEnabled;
        bool httpsBefore = Host.Runtime.Settings.HttpsEnabled;
        Host.Runtime.Settings.LanEnabled = true;
        Host.Runtime.Settings.HttpsEnabled = true;
        try
        {
            string token = await Admin.CreatePairingTokenAsync();
            PairingRequest request = Host.Runtime.Pairing.FindByToken(token)!;
            request.State = PairingState.AwaitingLocalConfirmation;
            request.DeviceName = "无探测手机";
            Assert.True(Host.Runtime.Admin.ConfirmPairing(request.Id).Ok);

            AdminOperations.PairingCompletion completion = Host.Runtime.Admin.CompletionOf(request.Id);
            Assert.NotNull(completion.RecommendedOrigin);
            Assert.StartsWith("https://", completion.RecommendedOrigin!, StringComparison.Ordinal);
            Assert.Contains($":{Host.Runtime.Settings.HttpsPort}", completion.RecommendedOrigin!);
        }
        finally
        {
            Host.Runtime.Settings.LanEnabled = lanBefore;
            Host.Runtime.Settings.HttpsEnabled = httpsBefore;
        }

        TestBase.Record("COMPLETE17-03",
            "没有可达性探测时完成页依然给出一个可用的 HTTPS 推荐地址（回退到物理 LAN 地址），不会出现空地址");
    }
}
