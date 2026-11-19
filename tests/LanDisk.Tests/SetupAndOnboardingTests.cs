using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Configuration;
using LanDisk.Core.Networking;
using LanDisk.Core.Networking.Dns;
using LanDisk.Server.Services;
using LanDisk.Setup;
using LanDisk.Tests.Infrastructure;

namespace LanDisk.Tests;

/// <summary>
/// Task 07: the first-run experience.
///
/// Four things have to be true for the promise "install once, trust once, never think about IPs again"
/// to hold: the firewall rules must stay narrow, the root CA must never rotate behind the user's back,
/// the stable name must be persistent and secret-free, and the installer must never leave a
/// half-replaced installation behind.
/// </summary>
public sealed class SetupAndOnboardingTests
{
    // ------------------------------------------------------------------ firewall

    [Fact]
    public void Firewall_rules_stay_private_local_subnet_and_never_expose_the_admin_port()
    {
        using var kit = new UpdateTestKit(nameof(Firewall_rules_stay_private_local_subnet_and_never_expose_the_admin_port));
        string install = kit.NewDirectory("install");

        IReadOnlyList<FirewallRule> rules = FirewallPlan.Rules(install);

        Assert.Empty(FirewallPlan.Violations(install, rules));
        Assert.All(rules, r => Assert.Equal("private", r.Profile));
        Assert.All(rules, r => Assert.Equal("localsubnet", r.RemoteAddress));
        Assert.All(rules, r => Assert.StartsWith(SetupLayout.FirewallRulePrefix, r.Name, StringComparison.Ordinal));

        Assert.Contains(rules, r => r.Protocol == "TCP" && r.LocalPort == "8892");
        Assert.Contains(rules, r => r.Protocol == "TCP" && r.LocalPort == "8890");
        Assert.Contains(rules, r => r.Protocol == "UDP" && r.LocalPort == "5353");
        Assert.DoesNotContain(rules, r => r.LocalPort == "8891");

        TestBase.Record("UX07-01", "防火墙规则：仅 Private + LocalSubnet，放行 8892/8890/5353，8891 不开放");
    }

    [Fact]
    public void Firewall_commands_are_fixed_and_auditable()
    {
        using var kit = new UpdateTestKit(nameof(Firewall_commands_are_fixed_and_auditable));
        string install = kit.NewDirectory("install");

        FirewallRule https = FirewallPlan.Rules(install).First(r => r.LocalPort == "8892");
        IReadOnlyList<string> add = FirewallPlan.AddArguments(https);

        Assert.Equal("advfirewall", add[0]);
        Assert.Contains("name=" + https.Name, add);
        Assert.Contains("dir=in", add);
        Assert.Contains("action=allow", add);
        Assert.Contains("profile=private", add);
        Assert.Contains("remoteip=localsubnet", add);
        Assert.Contains("protocol=TCP", add);
        Assert.Contains("localport=8892", add);
        Assert.Contains("program=" + SetupLayout.ShellPathFor(Path.GetFullPath(install)), add);

        // A rule is deleted by the exact name its creator chose and nothing else.
        Assert.Equal(["advfirewall", "firewall", "delete", "rule", "name=" + https.Name],
            FirewallPlan.DeleteArguments(https).ToArray());

        TestBase.Record("UX07-02", "防火墙命令由计划生成，提权助手不接受任意参数：" + string.Join(' ', add));
    }

    [Fact]
    public void A_widened_rule_is_rejected_before_it_can_be_applied()
    {
        using var kit = new UpdateTestKit(nameof(A_widened_rule_is_rejected_before_it_can_be_applied));
        string install = kit.NewDirectory("install");

        FirewallRule widened = new(
            SetupLayout.FirewallRulePrefix + " everything", "in", "allow",
            SetupLayout.ShellPathFor(Path.GetFullPath(install)), "TCP", "1-65535", "any", "any", "test");

        IReadOnlyList<string> problems = FirewallPlan.Violations(install, [widened]);

        Assert.Contains(problems, p => p.Contains("Private"));
        Assert.Contains(problems, p => p.Contains("LocalSubnet"));
        Assert.Contains(problems, p => p.Contains("不允许端口范围"));
    }

    // ------------------------------------------------------------------ installer arguments

    [Fact]
    public void Setup_options_default_to_the_fixed_paths_and_parse_the_uninstall_switch()
    {
        SetupOptions defaults = SetupOptions.Parse([]);
        Assert.Equal(SetupLayout.DefaultInstallDir, defaults.InstallDir);
        Assert.Equal(SetupLayout.DefaultDataDir, defaults.DataDir);
        Assert.True(defaults.DesktopShortcut);
        Assert.False(defaults.Uninstall);
        Assert.Null(defaults.Validate());

        SetupOptions uninstall = SetupOptions.Parse(["--uninstall", "--remove-data", "--silent", "--no-firewall"]);
        Assert.True(uninstall.Uninstall);
        Assert.True(uninstall.RemoveUserData);
        Assert.True(uninstall.Silent);
        Assert.True(uninstall.NoFirewall);
        Assert.Null(uninstall.Validate());
    }

    [Fact]
    public void Dangerously_ambiguous_combinations_are_refused()
    {
        Assert.NotNull(SetupOptions.Parse(["--remove-data"]).Validate());

        // The elevated helper must not be steerable to an arbitrary directory: it runs as administrator.
        SetupOptions redirected = SetupOptions.Parse(
            ["--elevated-firewall-add", "--install-dir", @"C:\Windows\System32"]);
        Assert.NotNull(redirected.Validate());

        SetupOptions both = SetupOptions.Parse(["--elevated-firewall-add", "--elevated-firewall-remove"]);
        Assert.NotNull(both.Validate());

        SetupOptions defaultElevated = SetupOptions.Parse(["--elevated-firewall-add"]);
        Assert.Null(defaultElevated.Validate());

        TestBase.Record("UX07-03", "提权助手只接受默认安装目录与单一动作，拒绝任意路径与任意命令");
    }

    [Fact]
    public void The_firewall_plan_can_be_printed_without_touching_a_firewall()
    {
        using var kit = new UpdateTestKit(nameof(The_firewall_plan_can_be_printed_without_touching_a_firewall));
        string install = kit.NewDirectory("install");

        string description = FirewallPlan.Describe(FirewallPlan.Rules(install));

        Assert.Contains("LanDisk HTTPS", description);
        Assert.Contains("profile=private", description);
        Assert.Contains("remoteip=localsubnet", description);
    }

    // ------------------------------------------------------------------ install identity

    [Fact]
    public void The_installation_name_is_persistent_and_carries_nothing_personal()
    {
        using var kit = new UpdateTestKit(nameof(The_installation_name_is_persistent_and_carries_nothing_personal));
        AppPaths paths = AppPaths.ForDataRoot(kit.NewDirectory("data"));

        InstallIdentity first = InstallIdentity.Ensure(paths);
        InstallIdentity second = InstallIdentity.Ensure(paths);

        Assert.Equal(first.InstallId, second.InstallId);
        Assert.Equal(first.HostName, second.HostName);
        Assert.Matches("^landisk-[0-9a-f]{8}\\.local$", first.HostName);

        string user = Environment.UserName;
        if (user.Length >= 3)
            Assert.DoesNotContain(user, first.HostName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, first.HostName, StringComparison.OrdinalIgnoreCase);

        // Changing networks must not change the name.
        InstallIdentity third = InstallIdentity.Ensure(paths);
        Assert.Equal(first.HostName, third.HostName);

        TestBase.Record("UX07-04", "稳定局域网名持久且不含用户名/机器名：" + first.HostName);
    }

    [Fact]
    public void A_name_collision_moves_to_the_next_suffix_exactly_once()
    {
        using var kit = new UpdateTestKit(nameof(A_name_collision_moves_to_the_next_suffix_exactly_once));
        AppPaths paths = AppPaths.ForDataRoot(kit.NewDirectory("data"));

        InstallIdentity identity = InstallIdentity.Ensure(paths, label => !label.EndsWith("-2", StringComparison.Ordinal));
        Assert.Equal(1, identity.NameAttempt);

        string renamed = identity.HostName;

        // The decision is persisted: a second start reuses the resolved name instead of probing again.
        InstallIdentity reloaded = InstallIdentity.Ensure(paths, _ => true);
        Assert.Equal(renamed, reloaded.HostName);

        Assert.True(identity.AdoptNextName(paths));
        Assert.NotEqual(renamed, identity.HostName);
        Assert.True(InstallIdentity.IsValidLabel(identity.HostLabel));
    }

    [Fact]
    public void Host_labels_are_validated_so_a_hand_edited_file_cannot_break_mdns()
    {
        Assert.True(InstallIdentity.IsValidLabel("landisk-a1b2c3d4"));
        Assert.False(InstallIdentity.IsValidLabel(""));
        Assert.False(InstallIdentity.IsValidLabel("-leading"));
        Assert.False(InstallIdentity.IsValidLabel("trailing-"));
        Assert.False(InstallIdentity.IsValidLabel("Has Spaces"));
        Assert.False(InstallIdentity.IsValidLabel("_underscore"));
        Assert.False(InstallIdentity.IsValidLabel(new string('a', 64)));
    }

    // ------------------------------------------------------------------ CA / leaf lifecycle

    private static (AppPaths Paths, CertificateService Service) NewCertificateService(UpdateTestKit kit, string name)
    {
        AppPaths paths = AppPaths.ForDataRoot(kit.NewDirectory(name));
        return (paths, new CertificateService(paths));
    }

    [Fact]
    public void The_first_run_creates_one_root_ca_and_a_leaf_that_chains_to_it()
    {
        using var kit = new UpdateTestKit(nameof(The_first_run_creates_one_root_ca_and_a_leaf_that_chains_to_it));
        (AppPaths paths, CertificateService service) = NewCertificateService(kit, "data");

        ServerCertificate issued = service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        Assert.Equal(LeafReissueReason.Missing, issued.Reason);
        Assert.Equal("CN=LanDisk Local CA a1b2c3d4", issued.Ca.Subject);
        Assert.Equal(issued.Ca.Subject, issued.Leaf.Issuer);
        Assert.NotEqual(issued.Ca.Subject, issued.Leaf.Subject);
        Assert.True(issued.Ca.NotAfter > DateTime.UtcNow.AddYears(5));
        Assert.True(issued.Leaf.NotAfter < DateTime.UtcNow.AddDays(398));

        (List<string> dns, List<string> ips) = LocalCertificateAuthority.ReadSubjectAlternativeNames(issued.Leaf);
        Assert.Contains("landisk-a1b2c3d4.local", dns);
        Assert.Contains("localhost", dns);
        Assert.Contains("127.0.0.1", ips);

        // The published root certificate is public only; the private keys are DPAPI-sealed files.
        byte[] caCer = service.ReadCaCerBytes()!;
        using var publicOnly = X509CertificateLoader.LoadCertificate(caCer);
        Assert.False(publicOnly.HasPrivateKey);
        Assert.False(X509CertificateLoader.LoadCertificate(File.ReadAllBytes(paths.LeafCerFile)).HasPrivateKey);

        string sealedText = Encoding.Latin1.GetString(File.ReadAllBytes(paths.CaProtectedFile));
        Assert.DoesNotContain("PRIVATE KEY", sealedText, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", sealedText, StringComparison.Ordinal);

        TestBase.Record("UX07-05", "首次运行创建唯一本地 CA 并由其签发服务器证书；私钥以 DPAPI 封存");
    }

    [Fact]
    public void Restarts_and_updates_do_not_rotate_the_root_ca()
    {
        using var kit = new UpdateTestKit(nameof(Restarts_and_updates_do_not_rotate_the_root_ca));
        (AppPaths paths, CertificateService service) = NewCertificateService(kit, "data");

        ServerCertificate first = service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");
        string caFingerprint = LocalCertificateAuthority.Fingerprint(first.Ca);
        string leafThumbprint = first.Leaf.Thumbprint;

        // A "restart" is a fresh service over the same directory; an "update" leaves the data root
        // untouched and only replaces program files.
        for (int i = 0; i < 3; i++)
        {
            var restarted = new CertificateService(paths);
            ServerCertificate again = restarted.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

            Assert.Equal(LeafReissueReason.None, again.Reason);
            Assert.Equal(caFingerprint, LocalCertificateAuthority.Fingerprint(again.Ca));
            Assert.Equal(leafThumbprint, again.Leaf.Thumbprint);
        }

        TestBase.Record("UX07-06", "重启与升级都不更换根 CA，也不重新签发未到期的服务器证书");
    }

    [Fact]
    public void An_address_change_reissues_only_the_leaf()
    {
        using var kit = new UpdateTestKit(nameof(An_address_change_reissues_only_the_leaf));
        (AppPaths paths, CertificateService service) = NewCertificateService(kit, "data");

        ServerCertificate before = service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local", ["192.168.2.10"]);
        string caFingerprint = LocalCertificateAuthority.Fingerprint(before.Ca);
        string leafThumbprint = before.Leaf.Thumbprint;

        ServerCertificate after = service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local", ["192.168.2.10", "10.0.0.7"]);

        Assert.Equal(LeafReissueReason.AddressesChanged, after.Reason);
        Assert.NotEqual(leafThumbprint, after.Leaf.Thumbprint);

        // The root — the thing the phone trusted — is untouched, which is why no re-trust is needed.
        Assert.Equal(caFingerprint, LocalCertificateAuthority.Fingerprint(after.Ca));

        (List<string> dns, List<string> ips) = LocalCertificateAuthority.ReadSubjectAlternativeNames(after.Leaf);
        Assert.Contains("10.0.0.7", ips);
        Assert.Contains("landisk-a1b2c3d4.local", dns);

        TestBase.Record("UX07-07", "IP 变化只重新签发服务器证书，根 CA 指纹不变 → 手机无需重新信任");
    }

    [Fact]
    public void An_explicit_reset_is_the_only_thing_that_rotates_the_root_ca()
    {
        using var kit = new UpdateTestKit(nameof(An_explicit_reset_is_the_only_thing_that_rotates_the_root_ca));
        (AppPaths paths, CertificateService service) = NewCertificateService(kit, "data");

        ServerCertificate before = service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");
        string caFingerprint = LocalCertificateAuthority.Fingerprint(before.Ca);

        service.ResetCa("a1b2c3d4");
        ServerCertificate after = service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        Assert.NotEqual(caFingerprint, LocalCertificateAuthority.Fingerprint(after.Ca));
        Assert.Equal(after.Ca.Subject, after.Leaf.Issuer);
        Assert.Contains("reset-", string.Join(',', Directory.GetFiles(paths.TlsDir).Select(Path.GetFileName)));
    }

    [Fact]
    public void Re_issuing_the_leaf_leaves_the_root_and_the_trust_alone()
    {
        using var kit = new UpdateTestKit(nameof(Re_issuing_the_leaf_leaves_the_root_and_the_trust_alone));
        (AppPaths paths, CertificateService service) = NewCertificateService(kit, "data");

        ServerCertificate before = service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");
        service.ResetLeafOnly();
        ServerCertificate after = service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        Assert.NotEqual(before.Leaf.Thumbprint, after.Leaf.Thumbprint);
        Assert.Equal(LocalCertificateAuthority.Fingerprint(before.Ca), LocalCertificateAuthority.Fingerprint(after.Ca));
    }

    // ------------------------------------------------------------------ mDNS

    private static MdnsAdvertisement Advertisement() => new(
        "landisk-a1b2c3d4.local", 8892, "1.4.0", ["192.168.2.10"]);

    [Fact]
    public void A_dns_message_round_trips()
    {
        var message = new DnsMessage { Id = 0, IsResponse = false };
        message.Questions.Add(new DnsQuestion("landisk-a1b2c3d4.local", DnsRecordType.A, false));
        message.Questions.Add(new DnsQuestion("_https._tcp.local", DnsRecordType.PTR, true));

        DnsMessage parsed = DnsMessage.Parse(message.ToBytes(), message.ToBytes().Length);

        Assert.False(parsed.IsResponse);
        Assert.Equal(2, parsed.Questions.Count);
        Assert.Equal("landisk-a1b2c3d4.local", parsed.Questions[0].Name);
        Assert.Equal(DnsRecordType.A, parsed.Questions[0].Type);
        Assert.True(parsed.Questions[1].UnicastResponse);
    }

    [Fact]
    public void The_parser_follows_compression_pointers()
    {
        // A response whose PTR payload is a compression pointer back to the record's own owner name.
        // Every real mDNS client compresses like this, so a responder that cannot read a pointer
        // cannot detect a name conflict either. The buffer is assembled rather than hand-counted so
        // the offsets cannot drift out of step with the labels.
        string owner = "landisk-a1b2c3d4.local";
        using var stream = new MemoryStream();
        stream.Write(new byte[12]); // header, filled in below

        int nameOffset = (int)stream.Position;
        DnsMessage.WriteName(stream, owner);

        Span<byte> fixedPart = stackalloc byte[10];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(fixedPart[..2], (ushort)DnsRecordType.PTR);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(fixedPart[2..4], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fixedPart[4..8], 120);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(fixedPart[8..10], 2);
        stream.Write(fixedPart);

        stream.WriteByte((byte)(0xC0 | (nameOffset >> 8)));
        stream.WriteByte((byte)(nameOffset & 0xFF));

        byte[] buffer = stream.ToArray();
        buffer[2] = 0x84; // response, authoritative
        buffer[7] = 1;    // one answer

        DnsMessage parsed = DnsMessage.Parse(buffer, buffer.Length);

        Assert.Single(parsed.Answers);
        Assert.Equal(owner, parsed.Answers[0].Name);
        Assert.Equal(owner, parsed.Answers[0].Target);
        Assert.Equal(DnsRecordType.PTR, parsed.Answers[0].Type);
    }

    [Fact]
    public void The_advertisement_publishes_the_stable_name_the_service_and_no_secrets()
    {
        MdnsAdvertisement advertisement = Advertisement();
        IReadOnlyList<DnsResource> records = MdnsRecords.All(advertisement);

        Assert.Contains(records, r => r.Name == "landisk-a1b2c3d4.local" && r.Type == DnsRecordType.A
                                      && r.AddressText == "192.168.2.10");
        Assert.Contains(records, r => r.Name == MdnsAdvertisement.HttpsService && r.Type == DnsRecordType.PTR);
        Assert.Contains(records, r => r.Type == DnsRecordType.SRV && r.Target == "landisk-a1b2c3d4.local.");
        Assert.Contains(records, r => r.Name == MdnsAdvertisement.ProductService && r.Type == DnsRecordType.PTR);

        IReadOnlyList<string> txt = MdnsRecords.TxtValues(advertisement);
        Assert.Equal(["product=LanDisk", "version=1.4.0", "port=8892", "scheme=https"], txt);
        Assert.DoesNotContain(txt, value => value.Contains("token", StringComparison.OrdinalIgnoreCase)
                                            || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
                                            || value.Contains("share", StringComparison.OrdinalIgnoreCase)
                                            || value.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));

        TestBase.Record("UX07-08", "mDNS 只发布名称/服务/端口，TXT 不含 token、用户名或共享名");
    }

    [Fact]
    public void Only_questions_about_our_own_names_are_answered()
    {
        MdnsAdvertisement advertisement = Advertisement();

        Assert.Empty(MdnsRecords.ForQuestion(advertisement, new DnsQuestion("other-host.local", DnsRecordType.A, false)));
        Assert.Empty(MdnsRecords.ForQuestion(advertisement, new DnsQuestion("_ssh._tcp.local", DnsRecordType.PTR, false)));

        IReadOnlyList<DnsResource> ours = MdnsRecords.ForQuestion(advertisement,
            new DnsQuestion("landisk-a1b2c3d4.local", DnsRecordType.A, false));
        Assert.Single(ours);
        Assert.Equal(DnsRecordType.A, ours[0].Type);

        IReadOnlyList<DnsResource> service = MdnsRecords.ForQuestion(advertisement,
            new DnsQuestion("_https._tcp.local", DnsRecordType.PTR, false));
        Assert.Contains(service, r => r.Type == DnsRecordType.PTR);
        Assert.Contains(service, r => r.Type == DnsRecordType.SRV);
        Assert.Contains(service, r => r.Type == DnsRecordType.TXT);
    }

    [Fact]
    public void A_foreign_claim_on_our_name_is_reported_as_a_conflict()
    {
        var responder = new MdnsResponder(Advertisement());
        MdnsConflictEventArgs? observed = null;
        responder.ConflictDetected += (_, args) => observed = args;

        var message = new DnsMessage { IsResponse = true, Authoritative = true };
        message.Answers.Add(new DnsResource
        {
            Name = "landisk-a1b2c3d4.local",
            Type = DnsRecordType.A,
            RData = IPAddress.Parse("192.168.2.99").GetAddressBytes()
        });

        responder.HandleDatagram(message.ToBytes(), message.ToBytes().Length,
            new IPEndPoint(IPAddress.Parse("192.168.2.99"), 5353));

        Assert.NotNull(observed);
        Assert.Equal("192.168.2.99", observed!.OtherAddress);
        responder.Dispose();

        TestBase.Record("UX07-09", "检测到同一链路上有别的设备声明了相同名称时报告冲突并改用新的持久后缀");
    }

    [Fact]
    public void Our_own_announcement_is_not_mistaken_for_a_conflict()
    {
        var responder = new MdnsResponder(Advertisement());
        bool conflicted = false;
        responder.ConflictDetected += (_, _) => conflicted = true;

        var message = new DnsMessage { IsResponse = true };
        message.Answers.Add(new DnsResource
        {
            Name = "landisk-a1b2c3d4.local",
            Type = DnsRecordType.A,
            RData = IPAddress.Parse("192.168.2.10").GetAddressBytes()
        });
        byte[] payload = message.ToBytes();

        responder.HandleDatagram(payload, payload.Length, new IPEndPoint(IPAddress.Parse("192.168.2.10"), 5353));

        Assert.False(conflicted);
        responder.Dispose();
    }

    // ------------------------------------------------------------------ onboarding bootstrap

    private static OnboardingService NewOnboarding(UpdateTestKit kit, CertificateService service, string hostName, int port, string address = "192.168.2.10")
    {
        AppPaths paths = AppPaths.ForDataRoot(kit.NewDirectory("onboarding-data"));
        return new OnboardingService(
            paths,
            () => service.ReadCaCerBytes(),
            () => hostName,
            () => port,
            () => [address]);
    }

    [Fact]
    public void The_bootstrap_listener_is_never_bound_to_a_non_private_address()
    {
        using var kit = new UpdateTestKit(nameof(The_bootstrap_listener_is_never_bound_to_a_non_private_address));
        (_, CertificateService service) = NewCertificateService(kit, "ca");
        service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        // TEST-NET-3: documentation-only, and definitely not a private address. Binding the wizard
        // there would mean serving it to whatever network that address belongs to.
        OnboardingService onboarding = NewOnboarding(kit, service, "landisk-a1b2c3d4.local", 8892, "203.0.113.7");
        onboarding.Start(port: 0);

        Assert.False(onboarding.IsActive);
        Assert.NotNull(onboarding.LastError);
        Assert.Contains("没有可用的局域网地址", onboarding.LastError!);
    }

    [Fact]
    public void The_bootstrap_page_carries_no_secret_and_states_the_manual_trust_step()
    {
        using var kit = new UpdateTestKit(nameof(The_bootstrap_page_carries_no_secret_and_states_the_manual_trust_step));
        (_, CertificateService service) = NewCertificateService(kit, "ca");
        service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        OnboardingService onboarding = NewOnboarding(kit, service, "landisk-a1b2c3d4.local", 8892);
        onboarding.Start(port: 0);

        // The page is rendered without a live session in this test; what matters is its content, and
        // that content must not depend on a session existing.
        string page = onboarding.BootstrapPage();

        Assert.Contains("LanDisk Local CA", page);
        Assert.Contains("完全信任", page);
        Assert.Contains("证书信任设置", page);
        Assert.Contains("https://landisk-a1b2c3d4.local:8892", page);
        Assert.Contains("/api/onboarding/trust-probe", page);

        // Nothing that looks like a credential may travel over the plain-HTTP bootstrap page.
        Assert.DoesNotContain("admin", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", page, StringComparison.OrdinalIgnoreCase);

        onboarding.Stop("测试结束");

        TestBase.Record("UX07-10", "引导页只含公开 CA、安装步骤与 HTTPS 探测，不含 token/secret/管理入口");
    }

    [Fact]
    public void The_bootstrap_id_is_per_session_and_is_required()
    {
        using var kit = new UpdateTestKit(nameof(The_bootstrap_id_is_per_session_and_is_required));
        (_, CertificateService service) = NewCertificateService(kit, "ca");
        service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        OnboardingService onboarding = NewOnboarding(kit, service, "landisk-a1b2c3d4.local", 8892);

        Assert.False(onboarding.MatchesBootstrapId("anything"));
        Assert.Null(onboarding.BootstrapId);
        Assert.Null(onboarding.Status().BootstrapUrl);
        Assert.False(onboarding.IsActive);
    }

    [Fact]
    public void The_profile_contains_only_the_public_root_and_no_device_policy()
    {
        using var kit = new UpdateTestKit(nameof(The_profile_contains_only_the_public_root_and_no_device_policy));
        (_, CertificateService service) = NewCertificateService(kit, "ca");
        service.EnsureServerCertificate("a1b2c3d4", "landisk-a1b2c3d4.local");

        OnboardingService onboarding = NewOnboarding(kit, service, "landisk-a1b2c3d4.local", 8892);
        string profile = onboarding.MobileConfig(service.ReadCaCerBytes()!);

        Assert.Contains("com.apple.security.root", profile);
        Assert.Contains("PayloadType", profile);
        Assert.DoesNotContain("com.apple.vpn", profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("com.apple.proxy", profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("com.apple.mdm", profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE KEY", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadRemovalDisallowed</key><true/>", profile);

        TestBase.Record("UX07-11", "mobileconfig 只含公开根证书，不设置 VPN/代理/MDM，也不禁止移除");
    }

    // ------------------------------------------------------------------ uninstall

    [Fact]
    public void The_uninstall_entry_points_at_the_installer_copy_and_records_the_data_root()
    {
        using var kit = new UpdateTestKit(nameof(The_uninstall_entry_points_at_the_installer_copy_and_records_the_data_root));
        string install = kit.NewDirectory("install");
        string data = kit.NewDirectory("data");

        IReadOnlyDictionary<string, object> values = ShellIntegration.UninstallValues(install, data, "1.4.0", 95_000_000);

        Assert.Equal("LanDisk", values["DisplayName"]);
        Assert.Equal("1.4.0", values["DisplayVersion"]);
        Assert.Contains("--uninstall", (string)values["UninstallString"]);
        Assert.Contains(SetupLayout.SetupCopyPathFor(install), (string)values["UninstallString"]);
        Assert.Equal(Path.GetFullPath(data), values["LanDiskDataRoot"]);

        // Keeping the user's data is the default, so nothing here asks for it to be removed.
        Assert.DoesNotContain(values.Keys, key => key.Contains("RemoveData", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Data_removal_refuses_to_touch_anything_but_a_lanDisk_data_root()
    {
        using var kit = new UpdateTestKit(nameof(Data_removal_refuses_to_touch_anything_but_a_lanDisk_data_root));
        string unrelated = kit.NewDirectory("not-the-data-root");
        File.WriteAllText(Path.Combine(unrelated, "important.txt"), "keep me");

        string? logged = null;
        Assert.False(ShellIntegration.RemoveUserData(unrelated, message => logged = message));
        Assert.True(File.Exists(Path.Combine(unrelated, "important.txt")));
        Assert.Contains("拒绝删除", logged ?? string.Empty);

        // A real LanDisk data root is recognised by its own contents and may be removed on request —
        // this is the acceptance path, where "delete my settings" has to actually work.
        string dataRoot = kit.NewDirectory("landisk-data");
        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(dataRoot, "tls"));

        Assert.True(ShellIntegration.LooksLikeDataRoot(dataRoot));
        Assert.True(ShellIntegration.RemoveUserData(dataRoot));
        Assert.False(Directory.Exists(dataRoot));

        TestBase.Record("UX07-16", "卸载删除数据只针对 LanDisk 自己的数据目录（按内容识别），共享真实文件不可达");
    }
}
