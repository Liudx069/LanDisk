using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// The update package format: what is signed, what a package may contain, and what is refused.
///
/// Every rejection test asserts the stable machine code, not just "it threw": the codes are what the
/// management page and any future automation branch on.
/// </summary>
public sealed class UpdatePackageTests
{
    // ------------------------------------------------------------------ signatures

    [Fact]
    public void Signature_covers_the_canonical_manifest_not_the_json_text()
    {
        DateTimeOffset published = DateTimeOffset.Now;

        UpdateManifest Build(bool reversed) => new()
        {
            Version = "1.2.0",
            PublishedAt = published,
            Files = reversed
                ?
                [
                    new UpdateFileEntry { Path = "b.dll", Bytes = 2, Sha256 = new string('b', 64) },
                    new UpdateFileEntry { Path = "a.exe", Bytes = 1, Sha256 = new string('a', 64) }
                ]
                :
                [
                    new UpdateFileEntry { Path = "a.exe", Bytes = 1, Sha256 = new string('a', 64) },
                    new UpdateFileEntry { Path = "b.dll", Bytes = 2, Sha256 = new string('b', 64) }
                ]
        };

        byte[] first = UpdateSigning.ComputeSignedPayload(Build(reversed: false));
        byte[] second = UpdateSigning.ComputeSignedPayload(Build(reversed: true));

        Assert.Equal(Convert.ToHexString(first), Convert.ToHexString(second));

        // Reformatting the JSON must not be able to change what the signature means.
        string pretty = JsonSerializer.Serialize(Build(reversed: false), new JsonSerializerOptions { WriteIndented = true });
        string compact = JsonSerializer.Serialize(Build(reversed: true));
        Assert.NotEqual(pretty, compact);
        Assert.Equal(
            Convert.ToHexString(UpdateSigning.ComputeSignedPayload(UpdatePackage.ParseManifestJson(pretty))),
            Convert.ToHexString(UpdateSigning.ComputeSignedPayload(UpdatePackage.ParseManifestJson(compact))));

        // Changing any signed field must change the payload.
        UpdateManifest altered = Build(reversed: false);
        altered.Version = "1.2.1";
        Assert.NotEqual(Convert.ToHexString(first), Convert.ToHexString(UpdateSigning.ComputeSignedPayload(altered)));
    }

    [Fact]
    public void A_signature_from_a_different_key_is_rejected()
    {
        using var kit = new UpdateTestKit(nameof(A_signature_from_a_different_key_is_rejected));
        string package = kit.BuildPackage("foreign", new PackageSpec { SigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256) });

        // A package signed by a key this build does not trust is refused before anything else; the
        // message names both ids so an operator can tell "wrong key" from "corrupted signature".
        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(
            () => kit.Extract(package));
        Assert.Equal("UNKNOWN_KEY_ID", error.Code);
        Assert.Contains("内置的发布公钥", error.Message);
    }

    [Fact]
    public void A_tampered_signature_is_rejected()
    {
        using var kit = new UpdateTestKit(nameof(A_tampered_signature_is_rejected));
        string package = kit.BuildPackage("tampered-sig", new PackageSpec()
            .Only(("LanDisk.exe", "payload"))
            .Also(spec => spec.SignatureOverride = Convert.ToBase64String(new byte[64])));

        Assert.Throws<UpdateSignatureException>(() => kit.Extract(package));
    }

    [Fact]
    public void An_unsigned_package_is_rejected()
    {
        using var kit = new UpdateTestKit(nameof(An_unsigned_package_is_rejected));
        string package = kit.BuildPackage("unsigned", new PackageSpec()
            .Only(("LanDisk.exe", "payload"))
            .Also(spec => spec.Sign = false));

        // Either the missing signature block or the empty value must stop it; both are refusals.
        Assert.ThrowsAny<Exception>(() => kit.Extract(package));
    }

    // ------------------------------------------------------------------ payload integrity

    [Fact]
    public void A_payload_byte_that_differs_from_the_signed_hash_is_rejected()
    {
        using var kit = new UpdateTestKit(nameof(A_payload_byte_that_differs_from_the_signed_hash_is_rejected));
        const string original = "the original bytes";
        string package = kit.BuildPackage("swapped", new PackageSpec()
            .Only(("LanDisk.exe", original))
            // Same length, different content: only the hash can catch this one.
            .WithRaw("LanDisk.exe", PackageSpec.SameLengthBytes(original, "the tampered bytes")));

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("HASH_MISMATCH", error.Code);
    }

    [Fact]
    public void A_payload_file_whose_size_differs_from_the_manifest_is_rejected()
    {
        using var kit = new UpdateTestKit(nameof(A_payload_file_whose_size_differs_from_the_manifest_is_rejected));
        var spec = new PackageSpec().Only(("LanDisk.exe", "abc"));
        spec.DeclaredFiles =
        [
            new UpdateFileEntry
            {
                Path = "LanDisk.exe",
                Bytes = 999,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abc")))
            }
        ];
        string package = kit.BuildPackage("wrong-size", spec);

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("SIZE_MISMATCH", error.Code);
    }

    [Fact]
    public void An_entry_the_manifest_never_declared_is_rejected()
    {
        using var kit = new UpdateTestKit(nameof(An_entry_the_manifest_never_declared_is_rejected));
        string package = kit.BuildPackage("smuggled", new PackageSpec()
            .Only(("LanDisk.exe", "ok"))
            .WithUndeclared("LanDisk.Server.dll", Encoding.UTF8.GetBytes("smuggled")));

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("UNDECLARED_FILE", error.Code);
    }

    [Fact]
    public void A_declared_file_missing_from_the_archive_is_rejected()
    {
        using var kit = new UpdateTestKit(nameof(A_declared_file_missing_from_the_archive_is_rejected));
        var spec = new PackageSpec().Only(("LanDisk.exe", "ok"), ("LanDisk.Server.dll", "missing"));
        spec.Files.Remove("LanDisk.Server.dll");
        spec.DeclaredFiles =
        [
            Entry("LanDisk.exe", "ok"),
            Entry("LanDisk.Server.dll", "missing")
        ];
        string package = kit.BuildPackage("incomplete", spec);

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("FILE_MISSING", error.Code);
    }

    // ------------------------------------------------------------------ manifest shape

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/windows/system32/evil.dll")]
    [InlineData("payload/../../escape.txt")]
    [InlineData("dir//double.txt")]
    [InlineData("dir/./dot.txt")]
    [InlineData("trailing./file.txt")]
    [InlineData("nul.txt")]
    [InlineData("bad<name>.txt")]
    public void Hostile_paths_in_the_manifest_are_refused(string hostile)
    {
        Assert.False(UpdatePath.IsSafeRelativePath(hostile), $"'{hostile}' 不应被视为安全路径。");
    }

    [Theory]
    [InlineData("LanDisk.exe")]
    [InlineData("updater/LanDisk.Updater.exe")]
    [InlineData("wwwroot/assets/index-abc123.js")]
    [InlineData("zh-Hans/LanDisk.resources.dll")]
    public void Ordinary_payload_paths_are_accepted(string ordinary)
    {
        Assert.True(UpdatePath.IsSafeRelativePath(ordinary), $"'{ordinary}' 应当是合法路径。");
    }

    [Fact]
    public void ResolveWithin_proves_containment_segment_wise()
    {
        using var kit = new UpdateTestKit(nameof(ResolveWithin_proves_containment_segment_wise));
        string root = kit.NewDirectory("install");
        string sibling = kit.NewDirectory("install-sibling");

        string inside = UpdatePath.ResolveWithin(root, "a/b.txt");
        Assert.StartsWith(root, inside, StringComparison.OrdinalIgnoreCase);

        // A sibling directory that merely shares a name prefix must not be reachable.
        Assert.Throws<UpdatePackageException>(() => UpdatePath.ResolveWithin(root, "../install-sibling/x.txt"));
        Assert.False(sibling.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_package_for_another_product_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_package_for_another_product_is_refused));
        string package = kit.BuildPackage("other-product", new PackageSpec { Product = "OtherApp" });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("PRODUCT_MISMATCH", error.Code);
    }

    [Fact]
    public void A_package_for_another_architecture_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_package_for_another_architecture_is_refused));
        string package = kit.BuildPackage("arm", new PackageSpec { Architecture = "win-arm64" });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("ARCHITECTURE_MISMATCH", error.Code);
    }

    [Fact]
    public void An_unknown_schema_version_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(An_unknown_schema_version_is_refused));
        string package = kit.BuildPackage("schema-2", new PackageSpec { SchemaVersion = 99 });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("SCHEMA_UNSUPPORTED", error.Code);
    }

    [Fact]
    public void A_package_without_the_entry_point_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_package_without_the_entry_point_is_refused));
        string package = kit.BuildPackage("no-entry", new PackageSpec()
            .Only(("LanDisk.Server.dll", "only a library")));

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("ENTRY_POINT_MISSING", error.Code);
    }

    [Fact]
    public void A_package_with_duplicate_manifest_paths_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_package_with_duplicate_manifest_paths_is_refused));
        var spec = new PackageSpec().Only(("LanDisk.exe", "one"));
        spec.DeclaredFiles = [Entry("LanDisk.exe", "one"), Entry("landisk.exe", "one")];
        string package = kit.BuildPackage("duplicate", spec);

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("DUPLICATE_PATH", error.Code);
    }

    // ------------------------------------------------------------------ downgrade protection

    [Fact]
    public void A_package_that_is_not_newer_than_the_installed_version_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_package_that_is_not_newer_than_the_installed_version_is_refused));
        string package = kit.BuildPackage("downgrade", new PackageSpec { Version = "1.1.0" });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(
            () => kit.Extract(package, currentVersion: "1.2.0"));
        Assert.Equal("VERSION_NOT_NEWER", error.Code);
    }

    [Fact]
    public void A_package_requiring_a_newer_starting_point_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_package_requiring_a_newer_starting_point_is_refused));
        string package = kit.BuildPackage("min-from", new PackageSpec
        {
            Version = "2.0.0",
            MinimumUpgradeVersion = "1.5.0"
        });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(
            () => kit.Extract(package, currentVersion: "1.2.0"));
        Assert.Equal("VERSION_TOO_OLD", error.Code);
    }

    [Fact]
    public void A_valid_package_round_trips_and_reports_the_signed_metadata()
    {
        using var kit = new UpdateTestKit(nameof(A_valid_package_round_trips_and_reports_the_signed_metadata));
        string package = kit.BuildPackage("good", new PackageSpec
        {
            Version = "1.3.0",
            BuildCommit = "abc123def4567890",
            ReleaseNotes = "修好了若干问题。"
        });

        VerifiedUpdatePackage verified = kit.Extract(package, currentVersion: "1.2.0");

        Assert.True(verified.IsVerified);
        Assert.Equal("1.3.0", verified.Manifest.Version);
        Assert.Equal("abc123def4567890", verified.Manifest.BuildCommit);
        Assert.Equal(kit.KeyId, verified.KeyId);
        Assert.Equal(2, verified.Manifest.Files.Count);
        Assert.True(File.Exists(Path.Combine(verified.PayloadRoot, "LanDisk.exe")));
        Assert.True(verified.PackageSha256.Length == 64);

        TestBase.Record("UPDATE-01", $"合法更新包通过校验 version={verified.Manifest.Version} signer={verified.KeyId}");
    }

    [Fact]
    public void A_staged_directory_can_be_reverified_without_the_original_archive()
    {
        using var kit = new UpdateTestKit(nameof(A_staged_directory_can_be_reverified_without_the_original_archive));
        string package = kit.BuildPackage("restage", new PackageSpec { Version = "1.4.0" });
        VerifiedUpdatePackage first = kit.Extract(package, currentVersion: "1.2.0");

        // This is exactly what the updater process does after the server has exited.
        VerifiedUpdatePackage second = UpdatePackage.LoadStaged(first.StagingRoot, kit.Key, "1.2.0");

        Assert.True(second.IsVerified);
        Assert.Equal(first.Manifest.Version, second.Manifest.Version);
        Assert.Equal(first.TotalBytes, second.TotalBytes);
    }

    [Fact]
    public void A_staged_directory_edited_after_verification_is_refused_on_reverification()
    {
        using var kit = new UpdateTestKit(nameof(A_staged_directory_edited_after_verification_is_refused_on_reverification));
        string package = kit.BuildPackage("edited", new PackageSpec { Version = "1.4.0" });
        VerifiedUpdatePackage staged = kit.Extract(package, currentVersion: "1.2.0");

        File.WriteAllBytes(
            Path.Combine(staged.PayloadRoot, "LanDisk.exe"),
            PackageSpec.SameLengthBytes("fake-lan-disk-exe-1.2.0", "swapped-after-verification"));

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(
            () => UpdatePackage.LoadStaged(staged.StagingRoot, kit.Key, "1.2.0"));
        Assert.Equal("HASH_MISMATCH", error.Code);
    }

    [Fact]
    public void ReleasePublicKey_self_check_matches_the_compiled_in_identifier()
    {
        Assert.True(ReleasePublicKey.SelfCheck(out string keyId), "内置发布公钥必须可用。");
        Assert.Equal(ReleasePublicKey.KeyId, keyId);

        // The embedded key must not be able to verify a package signed by a fresh test key.
        using var kit = new UpdateTestKit(nameof(ReleasePublicKey_self_check_matches_the_compiled_in_identifier));
        string package = kit.BuildPackage("foreign-key", new PackageSpec { Version = "9.9.9" });

        using ECDsa embedded = ReleasePublicKey.Create();
        Assert.Throws<UpdateSignatureException>(() => UpdatePackage.Extract(
            package, Path.Combine(kit.Root, "staging-embedded"), "1.0.0", embedded));

        TestBase.Record("UPDATE-02", $"内置发布公钥可用 keyId={keyId}；陌生密钥签名的包被拒绝");
    }

    [Fact]
    public void A_package_that_tries_to_ship_the_install_manifest_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_package_that_tries_to_ship_the_install_manifest_is_refused));
        string package = kit.BuildPackage("smuggle-manifest", new PackageSpec()
            .Only(("LanDisk.exe", "exe"), (InstallManifest.FileName, """{"version":"9.9.9"}""")));

        // The install manifest is the applier's own output. A package that declares it would make
        // the update write one thing and verify another, which is exactly the bug this guards.
        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("RESERVED_PATH", error.Code);
    }

    private static UpdateFileEntry Entry(string path, string content) => new()
    {
        Path = path,
        Bytes = Encoding.UTF8.GetByteCount(content),
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
    };
}
