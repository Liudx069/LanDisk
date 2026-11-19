using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LanDisk.Update;

namespace LanDisk.KeyTool;

/// <summary>
/// Release-side helper for the LanDisk update system.
///
/// It exists so that the private signing key never has to touch a build of the product, and so that
/// PowerShell scripts do not have to re-implement package signing. Commands:
///
///   new-key &lt;private.pem&gt;            生成新的 ECDSA P-256 发布签名私钥（仓库外）
///   pubkey  &lt;private.pem&gt;            由私钥导出公钥，输出可嵌入源码的 Base64 与 KeyId
///   pack    --payload &lt;dir&gt; ...      用私钥签名并打包出 .lupd
///   inspect &lt;package.lupd&gt;           只读查看清单（不校验签名）
///   verify  &lt;package.lupd&gt; &lt;pub.pem&gt; 完整校验签名与每个文件的哈希
/// </summary>
internal static class Program
{

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Usage(); return 1; }

            return args[0].ToLowerInvariant() switch
            {
                "new-key" => NewKey(args),
                "pubkey" => PublicKey(args),
                "pack" => Pack(args),
                "inspect" => Inspect(args),
                "verify" => Verify(args),
                "-h" or "--help" or "help" => Usage(),
                _ => Fail($"未知命令：{args[0]}")
            };
        }
        catch (UpdatePackageException ex)
        {
            Console.Error.WriteLine($"[{ex.Code}] {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            LanDisk 更新包工具

              landisk-keytool new-key <private.pem>
              landisk-keytool pubkey  <private.pem>
              landisk-keytool pack    --payload <dir> --out <file.lupd> --key <private.pem>
                                      --version <x.y.z> --commit <sha> [--min-upgrade <x.y.z>]
                                      [--build-id <text>] [--notes <text> | --notes-file <path>]
                                      [--entry LanDisk.exe] [--arch win-x64]
              landisk-keytool inspect <file.lupd>
              landisk-keytool verify  <file.lupd> <public.pem>
            """);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    // ------------------------------------------------------------------ key management

    private static int NewKey(string[] args)
    {
        if (args.Length < 2) return Fail("用法：new-key <private.pem>");
        string path = Path.GetFullPath(args[1]);

        if (File.Exists(path))
            return Fail($"拒绝覆盖已存在的私钥：{path}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(path, pem, new UTF8Encoding(false));

        // Best effort: keep the private key readable only by the current user.
        TryRestrictAcl(path);

        Console.WriteLine("已生成发布签名私钥（请离线保存，切勿放入仓库）：");
        Console.WriteLine("  " + path);
        Console.WriteLine();
        PrintPublic(key);
        return 0;
    }

    private static int PublicKey(string[] args)
    {
        if (args.Length < 2) return Fail("用法：pubkey <private.pem>");
        using ECDsa key = UpdateSigning.LoadPrivateKey(Path.GetFullPath(args[1]));
        PrintPublic(key);
        return 0;
    }

    private static void PrintPublic(ECDsa key)
    {
        string base64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string keyId = UpdateSigning.KeyId(key);

        Console.WriteLine("公钥（可公开，嵌入 src/LanDisk.Update/ReleasePublicKey.cs）：");
        Console.WriteLine("  KeyId: " + keyId);
        Console.WriteLine("  SubjectPublicKeyInfo (Base64):");
        Console.WriteLine("  " + base64);
        Console.WriteLine();
        Console.WriteLine("C# 片段：");
        Console.WriteLine($"    public const string KeyId = \"{keyId}\";");
    }

    private static void TryRestrictAcl(string path)
    {
        try
        {
            string user = Environment.UserName;
            var psi = new ProcessStartInfo("icacls")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/inheritance:r");
            psi.ArgumentList.Add("/grant:r");
            psi.ArgumentList.Add($"{user}:(R,W)");

            using Process? process = Process.Start(psi);
            process?.WaitForExit(10_000);
        }
        catch
        {
            // Not fatal: the key still works, it is just not ACL-restricted.
        }
    }

    // ------------------------------------------------------------------ packing

    private static int Pack(string[] args)
    {
        string? payloadDir = Opt(args, "--payload");
        string? outPath = Opt(args, "--out");
        string? keyPath = Opt(args, "--key");
        string? version = Opt(args, "--version");
        string minUpgrade = Opt(args, "--min-upgrade") ?? Opt(args, "--min-from") ?? "1.0.0";
        string commit = Opt(args, "--commit") ?? string.Empty;
        string buildId = Opt(args, "--build-id") ?? string.Empty;
        string? notes = Opt(args, "--notes");
        string? notesFile = Opt(args, "--notes-file");
        string entry = Opt(args, "--entry") ?? "LanDisk.exe";
        string architecture = Opt(args, "--arch") ?? "win-x64";

        if (payloadDir is null || outPath is null || keyPath is null || version is null)
            return Fail("用法：pack --payload <dir> --out <file.lupd> --key <private.pem> --version <x.y.z>");

        payloadDir = Path.GetFullPath(payloadDir);
        outPath = Path.GetFullPath(outPath);

        if (!Directory.Exists(payloadDir)) return Fail($"payload 目录不存在：{payloadDir}");
        if (!File.Exists(keyPath)) return Fail($"私钥不存在：{keyPath}");
        if (!Version.TryParse(version, out _)) return Fail($"版本号无法解析：{version}");
        if (!Version.TryParse(minUpgrade, out _)) return Fail($"最低可升级版本无法解析：{minUpgrade}");

        if (string.IsNullOrWhiteSpace(commit))
            return Fail("缺少 --commit：更新包必须声明它所对应的构建提交号，否则无法核对候选身份。");

        if (notesFile is not null && File.Exists(notesFile)) notes = File.ReadAllText(notesFile);
        notes ??= string.Empty;

        string[] files = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories);
        if (files.Length == 0) return Fail("payload 目录为空。");

        string payloadFull = payloadDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var entries = new List<(string Relative, string FullPath, long Bytes, string Sha)>();
        int skippedManifests = 0;
        foreach (string full in files)
        {
            string relative = full[payloadFull.Length..].Replace('\\', '/');
            if (!UpdatePath.IsSafeRelativePath(relative))
                return Fail($"发布目录中存在不适合放进更新包的路径：{relative}");

            // Only the install manifest is installer-generated metadata and therefore reserved.
            // build-info.json is a normal, signed payload file: the installed build identity has to
            // be replaced by an update, otherwise the directory would keep claiming the old version.
            if (string.Equals(relative, InstallManifest.FileName, StringComparison.OrdinalIgnoreCase))
            {
                skippedManifests++;
                continue;
            }

            var info = new FileInfo(full);
            entries.Add((relative, full, info.Length, UpdatePackage.HashFile(full)));
        }

        if (entries.Count == 0) return Fail("payload 目录中只有安装清单，没有可发布的程序文件。");
        if (skippedManifests > 0)
            Console.WriteLine($"已排除 {skippedManifests} 个 {InstallManifest.FileName}（由安装程序自身生成）。");

        entries.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));

        using ECDsa key = UpdateSigning.LoadPrivateKey(keyPath);
        string keyId = UpdateSigning.KeyId(key);

        var declaredFiles = entries
            .Select(e => new UpdateFileEntry { Path = e.Relative, Bytes = e.Bytes, Sha256 = e.Sha })
            .ToList();

        // Every user-visible or security-relevant value goes into the signed manifest, including the
        // signer identity and the release notes (by hash), so nothing the UI shows can be swapped
        // while the signature still verifies.
        var manifest = new UpdateManifest
        {
            SchemaVersion = UpdateManifest.CurrentSchemaVersion,
            Product = UpdateManifest.ProductName,
            Version = version,
            MinimumUpgradeVersion = minUpgrade,
            Architecture = architecture,
            BuildCommit = commit,
            BuildId = buildId,
            PublishedAt = DateTimeOffset.Now,
            EntryPoint = entry,
            KeyId = keyId,
            ReleaseNotesEntry = UpdatePackage.ReleaseNotesEntryName,
            ReleaseNotesSha256 = UpdateSigning.ComputeReleaseNotesHash(notes),
            ReleaseNotesBytes = System.Text.Encoding.UTF8.GetByteCount(notes),
            PayloadDigest = UpdateSigning.ComputePayloadDigest(declaredFiles),
            Files = declaredFiles
        };

        UpdatePackage.ValidateManifestShape(manifest);

        byte[] signature = UpdateSigning.Sign(manifest, key);

        var signatureBlock = new UpdateSignature
        {
            Algorithm = UpdateSigning.AlgorithmName,
            Value = Convert.ToBase64String(signature)
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        if (File.Exists(outPath)) File.Delete(outPath);

        using (var stream = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteTextEntry(archive, UpdatePackage.ManifestEntryName, UpdatePackage.SerializeManifest(manifest));
            WriteTextEntry(archive, UpdatePackage.SignatureEntryName, UpdatePackage.SerializeSignature(signatureBlock));
            WriteTextEntry(archive, UpdatePackage.ReleaseNotesEntryName, notes);

            foreach ((string relative, string full, _, _) in entries)
            {
                ZipArchiveEntry entryHandle = archive.CreateEntry(UpdatePath.PayloadPrefix + relative, CompressionLevel.Optimal);
                using Stream target = entryHandle.Open();
                using FileStream source = File.OpenRead(full);
                source.CopyTo(target);
            }
        }

        string packageHash = UpdatePackage.HashFile(outPath);
        var packageInfo = new FileInfo(outPath);

        Console.WriteLine("更新包已生成并签名：");
        Console.WriteLine("  文件:       " + outPath);
        Console.WriteLine("  格式版本:   " + manifest.SchemaVersion);
        Console.WriteLine("  版本:       " + manifest.Version + "（最低可升级 " + manifest.MinimumUpgradeVersion + "）");
        Console.WriteLine("  构建提交:   " + manifest.BuildCommit);
        Console.WriteLine("  签名者:     " + manifest.KeyId);
        Console.WriteLine("  更新说明:   " + manifest.ReleaseNotesBytes + " 字节 / " + manifest.ReleaseNotesSha256);
        Console.WriteLine("  payloadDigest: " + manifest.PayloadDigest);
        Console.WriteLine("  文件数:     " + manifest.Files.Count);
        Console.WriteLine("  原始大小:   " + manifest.Files.Sum(f => f.Bytes) + " 字节");
        Console.WriteLine("  包大小:     " + packageInfo.Length + " 字节");
        Console.WriteLine("  SHA256:     " + packageHash);
        return 0;
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    // ------------------------------------------------------------------ read-only inspection

    private static int Inspect(string[] args)
    {
        if (args.Length < 2) return Fail("用法：inspect <file.lupd>");
        string packagePath = Path.GetFullPath(args[1]);
        (UpdateManifest manifest, UpdateSignature signature) = UpdatePackage.ReadManifest(packagePath);

        Console.WriteLine($"产品:         {manifest.Product}");
        Console.WriteLine($"格式版本:     {manifest.SchemaVersion}");
        Console.WriteLine($"版本:         {manifest.Version}");
        Console.WriteLine($"最低可升级:   {manifest.MinimumUpgradeVersion}");
        Console.WriteLine($"架构:         {manifest.Architecture}");
        Console.WriteLine($"构建提交:     {manifest.BuildCommit}");
        Console.WriteLine($"构建标识:     {manifest.BuildId}");
        Console.WriteLine($"发布时间:     {manifest.PublishedAt:O}");
        Console.WriteLine($"入口:         {manifest.EntryPoint}");
        Console.WriteLine($"文件数:       {manifest.Files.Count}");
        Console.WriteLine($"payloadDigest:{manifest.PayloadDigest}");
        Console.WriteLine($"签名算法:     {signature.Algorithm}");
        Console.WriteLine($"签名者(keyId):{manifest.KeyId}");
        Console.WriteLine($"更新说明:     {manifest.ReleaseNotesBytes} 字节 sha256={manifest.ReleaseNotesSha256}");

        string notes;
        using (FileStream stream = File.OpenRead(packagePath))
        {
            notes = UpdatePackage.ReadReleaseNotes(stream, manifest);
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            Console.WriteLine("更新说明内容:");
            foreach (string line in notes.Split('\n')) Console.WriteLine("  " + line.TrimEnd());
        }
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 3) return Fail("用法：verify <file.lupd> <public.pem>");
        string packagePath = Path.GetFullPath(args[1]);
        string publicKeyPath = Path.GetFullPath(args[2]);
        string staging = Path.Combine(Path.GetTempPath(), "landisk-keytool-verify-" + Guid.NewGuid().ToString("n")[..8]);

        try
        {
            using ECDsa publicKey = UpdateSigning.LoadPublicKey(publicKeyPath);
            VerifiedUpdatePackage package = UpdatePackage.Extract(packagePath, staging, currentVersion: null, publicKey);

            Console.WriteLine("校验通过。");
            Console.WriteLine($"  格式版本:     {package.Manifest.SchemaVersion}");
            Console.WriteLine($"  版本:         {package.Manifest.Version}");
            Console.WriteLine($"  构建提交:     {package.Manifest.BuildCommit}");
            Console.WriteLine($"  签名者:       {package.KeyId}");
            Console.WriteLine($"  payloadDigest:{package.Manifest.PayloadDigest}");
            Console.WriteLine($"  文件数:       {package.Manifest.Files.Count}");
            Console.WriteLine($"  总大小:       {package.TotalBytes} 字节");
            Console.WriteLine($"  SHA256:       {package.PackageSha256}");
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private static string? Opt(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }
}
