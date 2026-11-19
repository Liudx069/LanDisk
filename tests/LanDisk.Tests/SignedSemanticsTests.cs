using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanDisk.Tests.Infrastructure;
using LanDisk.Update;

namespace LanDisk.Tests;

/// <summary>
/// H04-01: the signature must cover the whole release semantics.
///
/// The previous schema signed a hand-rolled rendering that omitted the signer identity and the
/// release notes text, so those two could be replaced losslessly while the signature still verified.
/// These tests pin the new behaviour field by field: every protected value must be unchangeable
/// without breaking the signature, while reformatting the JSON must remain harmless.
/// </summary>
public sealed class SignedSemanticsTests
{
    /// <summary>
    /// Signs a manifest, then rewrites the manifest JSON so one field differs from what was signed.
    /// The signature block is left untouched, which is exactly the tamper shape under test.
    /// </summary>
    private static string TamperField(UpdateTestKit kit, string name, PackageSpec spec, Action<JsonObject> mutate)
    {
        // Build a genuine, correctly signed package first so we know the signature is valid.
        string reference = kit.BuildPackage("reference-" + name, spec);

        JsonObject manifest;
        using (var archive = System.IO.Compression.ZipFile.OpenRead(reference))
        {
            System.IO.Compression.ZipArchiveEntry entry = archive.GetEntry(UpdatePackage.ManifestEntryName)!;
            using var reader = new StreamReader(entry.Open());
            manifest = (JsonObject)JsonNode.Parse(reader.ReadToEnd())!;
        }

        mutate(manifest);

        // Re-sign the *edited* manifest would defeat the test, so the original signature is copied
        // across verbatim from the reference package.
        UpdateSignature signature;
        using (var archive = System.IO.Compression.ZipFile.OpenRead(reference))
        {
            System.IO.Compression.ZipArchiveEntry entry = archive.GetEntry(UpdatePackage.SignatureEntryName)!;
            using var reader = new StreamReader(entry.Open());
            signature = UpdatePackage.ParseSignatureJson(reader.ReadToEnd());
        }

        var tampered = new PackageSpec
        {
            Version = spec.Version,
            BuildCommit = spec.BuildCommit,
            ReleaseNotes = spec.ReleaseNotes,
            ManifestJsonOverride = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            SignatureOverride = signature.Value
        };
        foreach ((string path, string content) in spec.Files) tampered.With(path, content);

        return kit.BuildPackage("tampered-" + name, tampered);
    }

    [Fact]
    public void The_manifest_declares_every_protected_field()
    {
        using var kit = new UpdateTestKit(nameof(The_manifest_declares_every_protected_field));
        string package = kit.BuildPackage("fields", new PackageSpec
        {
            Version = "1.3.0",
            BuildCommit = "abcdef1234567890",
            BuildId = "Release/win-x64",
            ReleaseNotes = "修复了若干问题。"
        });

        UpdateManifest manifest = UpdatePackage.ReadManifest(package).Manifest;

        Assert.Equal(UpdateManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal("LanDisk", manifest.Product);
        Assert.Equal("1.3.0", manifest.Version);
        Assert.Equal("1.0.0", manifest.MinimumUpgradeVersion);
        Assert.Equal("win-x64", manifest.Architecture);
        Assert.Equal("abcdef1234567890", manifest.BuildCommit);
        Assert.Equal("Release/win-x64", manifest.BuildId);
        Assert.Equal(kit.KeyId, manifest.KeyId);
        Assert.Equal(UpdatePackage.ReleaseNotesEntryName, manifest.ReleaseNotesEntry);
        Assert.Equal(UpdateSigning.ComputeReleaseNotesHash("修复了若干问题。"), manifest.ReleaseNotesSha256);
        Assert.Equal(Encoding.UTF8.GetByteCount("修复了若干问题。"), manifest.ReleaseNotesBytes);
        Assert.Equal(UpdateSigning.ComputePayloadDigest(manifest.Files), manifest.PayloadDigest);
        Assert.Equal("LanDisk.exe", manifest.EntryPoint);
        Assert.NotEmpty(manifest.Files);
    }

    [Fact]
    public void Changing_the_release_notes_invalidates_the_signature()
    {
        using var kit = new UpdateTestKit(nameof(Changing_the_release_notes_invalidates_the_signature));
        var spec = new PackageSpec { Version = "1.3.0", ReleaseNotes = "只修复了一个小问题。" };

        string tampered = TamperField(kit, "notes", spec, manifest =>
        {
            // The text the user reads is swapped for something alarming; the signature is untouched.
            manifest["releaseNotesSha256"] = UpdateSigning.ComputeReleaseNotesHash("此更新会删除你的全部文件。");
            manifest["releaseNotesBytes"] = Encoding.UTF8.GetByteCount("此更新会删除你的全部文件。");
        });

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(tampered));
        Assert.Equal("SIGNATURE_INVALID", error.Code);

        TestBase.Record("H04-01", "修改 releaseNotes 的哈希 → 签名校验失败（SIGNATURE_INVALID）");
    }

    [Fact]
    public void Release_notes_text_that_does_not_match_the_signed_hash_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(Release_notes_text_that_does_not_match_the_signed_hash_is_refused));

        // The manifest is signed over the original notes, but the zip carries different text.
        var spec = new PackageSpec { Version = "1.3.0", ReleaseNotes = "原始更新说明。" }
            .Also(s => s.ReleaseNotesOverride = "被替换过的更新说明。");

        string package = kit.BuildPackage("notes-swap", spec);

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("RELEASE_NOTES_MISMATCH", error.Code);
    }

    [Fact]
    public void A_package_without_the_declared_release_notes_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_package_without_the_declared_release_notes_is_refused));
        var spec = new PackageSpec { Version = "1.3.0" };
        spec.OmittedReleaseNotesEntries.Add(UpdatePackage.ReleaseNotesEntryName);

        string package = kit.BuildPackage("notes-missing", spec);

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("RELEASE_NOTES_MISSING", error.Code);
    }

    [Fact]
    public void Changing_the_key_id_invalidates_the_signature()
    {
        using var kit = new UpdateTestKit(nameof(Changing_the_key_id_invalidates_the_signature));
        var spec = new PackageSpec { Version = "1.3.0" };

        string tampered = TamperField(kit, "keyid", spec, manifest =>
        {
            // Relabelling the signer is the classic "looks like it came from someone else" edit.
            manifest["keyId"] = "0000000000000000";
        });

        // The key id is checked first because it is what the UI shows as "signed by"; relabelling the
        // signer is therefore reported as an unknown signer rather than as a crypto failure. Either
        // way the package is refused, and the id is inside the signed payload so the signature would
        // also have failed.
        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(tampered));
        Assert.Equal("UNKNOWN_KEY_ID", error.Code);

        TestBase.Record("H04-01", "修改 keyId → 拒绝安装（UNKNOWN_KEY_ID）");
    }

    [Fact]
    public void An_unknown_key_id_is_refused_with_a_specific_reason()
    {
        using var kit = new UpdateTestKit(nameof(An_unknown_key_id_is_refused_with_a_specific_reason));

        // Correctly signed, but by a key this build does not trust: the label names the stranger.
        using var stranger = new UpdateTestKit(nameof(An_unknown_key_id_is_refused_with_a_specific_reason) + "-stranger");
        string package = stranger.BuildPackage("stranger", new PackageSpec { Version = "1.3.0" });

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(package));
        Assert.Equal("UNKNOWN_KEY_ID", error.Code);
        Assert.Contains(stranger.KeyId, error.Message);
    }

    [Fact]
    public void Changing_the_build_commit_invalidates_the_signature()
    {
        using var kit = new UpdateTestKit(nameof(Changing_the_build_commit_invalidates_the_signature));
        var spec = new PackageSpec { Version = "1.3.0", BuildCommit = "1111111111111111" };

        string tampered = TamperField(kit, "commit", spec, manifest => manifest["buildCommit"] = "2222222222222222");

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(tampered));
        Assert.Equal("SIGNATURE_INVALID", error.Code);

        TestBase.Record("H04-01", "修改 buildCommit → 签名校验失败");
    }

    [Fact]
    public void Changing_the_version_invalidates_the_signature()
    {
        using var kit = new UpdateTestKit(nameof(Changing_the_version_invalidates_the_signature));
        var spec = new PackageSpec { Version = "1.3.0" };

        string tampered = TamperField(kit, "version", spec, manifest => manifest["version"] = "9.9.9");

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(tampered));
        Assert.Equal("SIGNATURE_INVALID", error.Code);
    }

    [Fact]
    public void Changing_a_payload_file_hash_invalidates_the_signature()
    {
        using var kit = new UpdateTestKit(nameof(Changing_a_payload_file_hash_invalidates_the_signature));
        var spec = new PackageSpec { Version = "1.3.0" };

        string tampered = TamperField(kit, "filehash", spec, manifest =>
        {
            var files = (JsonArray)manifest["files"]!;
            ((JsonObject)files[0]!)["sha256"] = new string('a', 64);
        });

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(tampered));
        Assert.Equal("SIGNATURE_INVALID", error.Code);
    }

    [Fact]
    public void Changing_the_minimum_upgrade_version_invalidates_the_signature()
    {
        using var kit = new UpdateTestKit(nameof(Changing_the_minimum_upgrade_version_invalidates_the_signature));
        var spec = new PackageSpec { Version = "1.3.0", MinimumUpgradeVersion = "1.0.0" };

        string tampered = TamperField(kit, "minupgrade", spec, manifest => manifest["minimumUpgradeVersion"] = "0.0.1");

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(tampered));
        Assert.Equal("SIGNATURE_INVALID", error.Code);
    }

    [Fact]
    public void Changing_the_architecture_invalidates_the_signature()
    {
        using var kit = new UpdateTestKit(nameof(Changing_the_architecture_invalidates_the_signature));
        var spec = new PackageSpec { Version = "1.3.0" };

        string tampered = TamperField(kit, "arch", spec, manifest => manifest["architecture"] = "win-x86");

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(tampered));
        Assert.Equal("SIGNATURE_INVALID", error.Code);
    }

    [Fact]
    public void Changing_the_product_invalidates_the_signature()
    {
        using var kit = new UpdateTestKit(nameof(Changing_the_product_invalidates_the_signature));
        var spec = new PackageSpec { Version = "1.3.0" };

        string tampered = TamperField(kit, "product", spec, manifest => manifest["product"] = "OtherApp");

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(tampered));
        Assert.Equal("SIGNATURE_INVALID", error.Code);
    }

    [Fact]
    public void A_payload_digest_that_does_not_match_the_file_list_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_payload_digest_that_does_not_match_the_file_list_is_refused));
        var spec = new PackageSpec { Version = "1.3.0" }
            .Also(s => s.PayloadDigestOverride = new string('b', 64));

        string package = kit.BuildPackage("digest-mismatch", spec);

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("PAYLOAD_DIGEST_MISMATCH", error.Code);
    }

    // ------------------------------------------------------------------ canonicalisation

    [Fact]
    public void Reformatting_the_manifest_json_does_not_change_what_was_signed()
    {
        using var kit = new UpdateTestKit(nameof(Reformatting_the_manifest_json_does_not_change_what_was_signed));

        var spec = new PackageSpec
        {
            Version = "1.3.0",
            BuildCommit = "abcdef1",
            ReleaseNotes = "带中文与符号的说明：<>&\"'\\n换行。"
        };

        // Build the correctly signed package, then rebuild the manifest entry with different
        // formatting and key order while keeping the same values and the same signature.
        string reference = kit.BuildPackage("canonical-reference", spec);

        JsonObject manifest;
        UpdateSignature signature;
        using (var archive = System.IO.Compression.ZipFile.OpenRead(reference))
        {
            using (var reader = new StreamReader(archive.GetEntry(UpdatePackage.ManifestEntryName)!.Open()))
                manifest = (JsonObject)JsonNode.Parse(reader.ReadToEnd())!;
            using (var reader = new StreamReader(archive.GetEntry(UpdatePackage.SignatureEntryName)!.Open()))
                signature = UpdatePackage.ParseSignatureJson(reader.ReadToEnd());
        }

        // Reverse key order and re-indent with tabs; values are untouched.
        var reordered = new JsonObject();
        foreach (string key in manifest.Select(pair => pair.Key).Reverse().ToList())
            reordered[key] = manifest[key]!.DeepClone();

        var reformatted = new PackageSpec
        {
            Version = spec.Version,
            BuildCommit = spec.BuildCommit,
            ReleaseNotes = spec.ReleaseNotes,
            ManifestJsonOverride = reordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("  ", "\t"),
            SignatureOverride = signature.Value
        };
        foreach ((string path, string content) in spec.Files) reformatted.With(path, content);

        string package = kit.BuildPackage("canonical-reformatted", reformatted);

        // The two packages really do carry different manifest bytes on disk (that is the point of the
        // test); only the signed *semantics* are identical.
        string referenceManifest;
        string reformattedManifest;
        using (var archive = System.IO.Compression.ZipFile.OpenRead(reference))
        using (var reader = new StreamReader(archive.GetEntry(UpdatePackage.ManifestEntryName)!.Open()))
            referenceManifest = reader.ReadToEnd();
        using (var archive = System.IO.Compression.ZipFile.OpenRead(package))
        using (var reader = new StreamReader(archive.GetEntry(UpdatePackage.ManifestEntryName)!.Open()))
            reformattedManifest = reader.ReadToEnd();

        Assert.NotEqual(referenceManifest, reformattedManifest);

        // Still verifies: whitespace and key order are not part of the signed semantics.
        VerifiedUpdatePackage verified = kit.Extract(package, "1.2.0");
        Assert.True(verified.IsVerified);
        Assert.Equal("1.3.0", verified.Manifest.Version);
        Assert.Equal(spec.ReleaseNotes, verified.ReleaseNotes);

        TestBase.Record("H04-01", "仅重排键顺序 / 重新缩进 manifest JSON → 仍按设计通过校验");
    }

    [Fact]
    public void The_canonical_payload_is_valid_json_and_stable_across_calls()
    {
        using var kit = new UpdateTestKit(nameof(The_canonical_payload_is_valid_json_and_stable_across_calls));
        string package = kit.BuildPackage("canonical-form", new PackageSpec
        {
            Version = "1.3.0",
            ReleaseNotes = "包含 emoji 😀 与中文的说明。"
        });

        UpdateManifest manifest = UpdatePackage.ReadManifest(package).Manifest;

        byte[] first = UpdateSigning.ComputeSignedPayload(manifest);
        byte[] second = UpdateSigning.ComputeSignedPayload(manifest);
        Assert.Equal(first, second);

        // The canonical form is a real JSON document, and it is pure ASCII so its bytes cannot
        // depend on how an encoder would have chosen to emit non-ASCII text.
        string canonical = Encoding.UTF8.GetString(first);
        Assert.All(canonical, c => Assert.InRange((int)c, 0x20, 0x7E));

        using JsonDocument document = JsonDocument.Parse(canonical);
        Assert.Equal("LanDisk", document.RootElement.GetProperty("product").GetString());
        Assert.Equal("1.3.0", document.RootElement.GetProperty("version").GetString());
        Assert.Equal(kit.KeyId, document.RootElement.GetProperty("keyId").GetString());
        Assert.True(document.RootElement.GetProperty("files").GetArrayLength() >= 1);

        // The canonical form must include every protected field by name, so a reviewer can see the
        // coverage without reading the writer.
        foreach (string field in new[]
                 {
                     "schemaVersion", "product", "version", "minimumUpgradeVersion", "architecture",
                     "buildCommit", "buildId", "publishedAt", "entryPoint", "keyId",
                     "releaseNotesEntry", "releaseNotesSha256", "releaseNotesBytes", "payloadDigest", "files"
                 })
        {
            Assert.True(document.RootElement.TryGetProperty(field, out _), $"canonical payload 缺少字段 {field}");
        }
    }

    [Fact]
    public void The_file_list_is_sorted_so_ordering_cannot_change_the_signature()
    {
        var forward = new List<UpdateFileEntry>
        {
            new() { Path = "a.exe", Bytes = 1, Sha256 = new string('a', 64) },
            new() { Path = "b.dll", Bytes = 2, Sha256 = new string('b', 64) }
        };
        var reversed = new List<UpdateFileEntry> { forward[1], forward[0] };

        Assert.Equal(UpdateSigning.ComputePayloadDigest(forward), UpdateSigning.ComputePayloadDigest(reversed));

        UpdateManifest Build(List<UpdateFileEntry> files) => new()
        {
            Version = "1.3.0",
            BuildCommit = "abcdef1",
            KeyId = "ac3ff1673b23518a",
            ReleaseNotesSha256 = new string('c', 64),
            ReleaseNotesBytes = 10,
            PayloadDigest = UpdateSigning.ComputePayloadDigest(files),
            PublishedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Files = files
        };

        Assert.Equal(
            Convert.ToHexString(UpdateSigning.ComputeSignedPayload(Build(forward))),
            Convert.ToHexString(UpdateSigning.ComputeSignedPayload(Build(reversed))));
    }

    // ------------------------------------------------------------------ legacy schema

    [Fact]
    public void A_schema_1_package_is_refused_with_an_explanation_about_its_signature()
    {
        using var kit = new UpdateTestKit(nameof(A_schema_1_package_is_refused_with_an_explanation_about_its_signature));
        string package = kit.BuildPackage("legacy", new PackageSpec
        {
            Version = "1.3.0",
            SchemaVersion = UpdateManifest.LegacySchemaVersion
        });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));

        // The old format did not cover keyId or the release notes, so it cannot be "upgraded" into
        // trust: it is refused, and the reason says why rather than showing a generic mismatch.
        Assert.Equal("LEGACY_SCHEMA_UNSUPPORTED", error.Code);
        Assert.Contains("签名者标识", error.Message);
        Assert.Contains("更新说明", error.Message);
    }

    [Fact]
    public void An_unknown_future_schema_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(An_unknown_future_schema_is_refused));
        string package = kit.BuildPackage("future", new PackageSpec { Version = "1.3.0", SchemaVersion = 99 });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("SCHEMA_UNSUPPORTED", error.Code);
    }

    [Fact]
    public void A_manifest_without_a_build_commit_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_manifest_without_a_build_commit_is_refused));
        string package = kit.BuildPackage("no-commit", new PackageSpec { Version = "1.3.0", BuildCommit = "" });

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("BUILD_COMMIT_MISSING", error.Code);
    }

    [Fact]
    public void A_manifest_without_a_key_id_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_manifest_without_a_key_id_is_refused));
        var spec = new PackageSpec { Version = "1.3.0" };
        spec.KeyIdOverride = "";

        string package = kit.BuildPackage("no-keyid", spec);

        // The signer identity is part of the signed semantics, so its absence is a signature-level
        // refusal rather than a cosmetic format problem.
        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(package));
        Assert.Equal("KEY_ID_MISSING", error.Code);
    }

    [Fact]
    public void A_release_notes_hash_that_is_not_a_sha256_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_release_notes_hash_that_is_not_a_sha256_is_refused));
        var spec = new PackageSpec { Version = "1.3.0" };
        spec.ReleaseNotesShaOverride = "not-a-hash";

        string package = kit.BuildPackage("bad-notes-hash", spec);

        UpdatePackageException error = Assert.Throws<UpdatePackageException>(() => kit.Extract(package));
        Assert.Equal("RELEASE_NOTES_HASH_MISSING", error.Code);
    }

    [Fact]
    public void A_staged_package_still_verifies_the_notes_and_key_id_without_the_archive()
    {
        using var kit = new UpdateTestKit(nameof(A_staged_package_still_verifies_the_notes_and_key_id_without_the_archive));
        string package = kit.BuildPackage("staged", new PackageSpec
        {
            Version = "1.3.0",
            ReleaseNotes = "暂存后再校验的说明。"
        });

        VerifiedUpdatePackage staged = kit.Extract(package, "1.2.0");
        VerifiedUpdatePackage reloaded = UpdatePackage.LoadStaged(staged.StagingRoot, kit.Key, "1.2.0");

        Assert.Equal("暂存后再校验的说明。", reloaded.ReleaseNotes);
        Assert.Equal(kit.KeyId, reloaded.Manifest.KeyId);

        // Editing the staged notes must be caught too: the updater is a separate process and cannot
        // assume the staging directory was not touched in between.
        File.WriteAllText(Path.Combine(staged.StagingRoot, UpdatePackage.StagedReleaseNotesName), "被改过的说明。");
        UpdatePackageException error = Assert.Throws<UpdatePackageException>(
            () => UpdatePackage.LoadStaged(staged.StagingRoot, kit.Key, "1.2.0"));
        Assert.Equal("RELEASE_NOTES_MISMATCH", error.Code);
    }

    [Fact]
    public void A_signed_manifest_from_a_different_key_is_still_refused_even_when_well_formed()
    {
        using var kit = new UpdateTestKit(nameof(A_signed_manifest_from_a_different_key_is_still_refused_even_when_well_formed));
        using var other = new UpdateTestKit(nameof(A_signed_manifest_from_a_different_key_is_still_refused_even_when_well_formed) + "-other");

        // Signed by `other`, so the manifest carries other's keyId: refused as an unknown key id
        // rather than as a cryptographic mismatch, which is the clearer diagnostic.
        string package = other.BuildPackage("other-signed", new PackageSpec { Version = "1.3.0" });

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(package));
        Assert.Equal("UNKNOWN_KEY_ID", error.Code);
    }

    [Fact]
    public void A_signature_over_a_manifest_the_signer_never_saw_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(A_signature_over_a_manifest_the_signer_never_saw_is_refused));

        // Correct key id, correct signature bytes, but the signature belongs to a different manifest.
        var spec = new PackageSpec { Version = "1.3.0" };
        string signedA = kit.BuildPackage("sig-a", spec);
        string signedB = kit.BuildPackage("sig-b", new PackageSpec { Version = "1.4.0" });

        string signatureB;
        using (var archive = System.IO.Compression.ZipFile.OpenRead(signedB))
        {
            using var reader = new StreamReader(archive.GetEntry(UpdatePackage.SignatureEntryName)!.Open());
            signatureB = UpdatePackage.ParseSignatureJson(reader.ReadToEnd()).Value;
        }

        string manifestA;
        using (var archive = System.IO.Compression.ZipFile.OpenRead(signedA))
        {
            using var reader = new StreamReader(archive.GetEntry(UpdatePackage.ManifestEntryName)!.Open());
            manifestA = reader.ReadToEnd();
        }

        var mixed = new PackageSpec
        {
            Version = "1.3.0",
            ManifestJsonOverride = manifestA,
            SignatureOverride = signatureB
        };
        string package = kit.BuildPackage("mixed-signature", mixed);

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(package));
        Assert.Equal("SIGNATURE_INVALID", error.Code);
    }

    [Fact]
    public void Cng_backed_keys_are_not_disposed_by_verification()
    {
        // Regression guard for a real bug found in task 03: the service disposed a key it did not
        // own, producing an ObjectDisposedException on the next use.
        using var kit = new UpdateTestKit(nameof(Cng_backed_keys_are_not_disposed_by_verification));
        string package = kit.BuildPackage("reuse-key", new PackageSpec { Version = "1.3.0" });

        _ = kit.Extract(package, "1.2.0", "one");
        _ = kit.Extract(package, "1.2.0", "two");

        Assert.Equal(kit.KeyId, UpdateSigning.KeyId(kit.Key));
    }

    [Fact]
    public void A_signature_that_is_not_base64_is_refused_with_a_clear_code()
    {
        using var kit = new UpdateTestKit(nameof(A_signature_that_is_not_base64_is_refused_with_a_clear_code));
        string package = kit.BuildPackage("bad-base64", new PackageSpec
        {
            Version = "1.3.0",
            SignatureOverride = "!!!not base64!!!"
        });

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(package));
        Assert.Equal("SIGNATURE_MALFORMED", error.Code);
    }

    [Fact]
    public void An_unsupported_signature_algorithm_is_refused()
    {
        using var kit = new UpdateTestKit(nameof(An_unsupported_signature_algorithm_is_refused));
        string package = kit.BuildPackage("bad-algo", new PackageSpec
        {
            Version = "1.3.0",
            SignatureAlgorithm = "RSA-SHA1"
        });

        UpdateSignatureException error = Assert.Throws<UpdateSignatureException>(() => kit.Extract(package));
        Assert.Equal("ALGORITHM_UNSUPPORTED", error.Code);
    }

    [Fact]
    public void The_public_key_is_the_only_trust_anchor_and_self_checks()
    {
        Assert.True(ReleasePublicKey.SelfCheck(out string keyId), "内置发布公钥必须可用。");
        Assert.Equal(ReleasePublicKey.KeyId, keyId);

        using ECDsa embedded = ReleasePublicKey.Create();
        Assert.Equal(256, embedded.KeySize);

        // A fresh key must not be able to sign anything this build accepts.
        using var kit = new UpdateTestKit(nameof(The_public_key_is_the_only_trust_anchor_and_self_checks));
        string package = kit.BuildPackage("foreign", new PackageSpec { Version = "9.9.9" });

        Assert.Throws<UpdateSignatureException>(() => UpdatePackage.Extract(
            package, Path.Combine(kit.Root, "staging-embedded"), "1.0.0", embedded));
    }
}
