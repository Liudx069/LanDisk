using System.Security.Cryptography;
using System.Text;

namespace LanDisk.Update;

public sealed class UpdateSignatureException : Exception
{
    public UpdateSignatureException(string message) : base(message) { }

    public UpdateSignatureException(string code, string message) : base(message) => Code = code;

    /// <summary>Stable machine readable reason, surfaced to the admin UI untranslated.</summary>
    public string Code { get; } = "SIGNATURE_INVALID";
}

/// <summary>
/// ECDSA P-256 signing and verification for update packages.
///
/// Only the public key is compiled into the product. The private key lives outside the repository
/// on the release machine and is never written into a build, a package or a bundle; the packaging
/// tool refuses to run without it and the privacy scan looks for it explicitly.
/// </summary>
public static class UpdateSigning
{
    public const string AlgorithmName = "ECDSA-P256-SHA256";

    /// <summary>
    /// Canonical, deterministic JSON rendering of the signed manifest.
    ///
    /// It is a **real JSON document** with a fixed schema, written by hand so the byte layout cannot
    /// drift with a serializer version:
    ///
    /// <list type="bullet">
    /// <item>fields appear in a fixed order defined here;</item>
    /// <item>no insignificant whitespace, and every string is escaped so the output is pure ASCII
    /// (so the bytes cannot depend on an encoder's Unicode handling);</item>
    /// <item><c>files</c> is sorted by path with ordinal comparison.</item>
    /// </list>
    ///
    /// Because the input is the parsed object rather than the file text, reformatting
    /// <c>update-manifest.json</c> — re-indenting it, reordering its keys, escaping non-ASCII
    /// differently — yields the same bytes. Changing any <em>value</em> yields different bytes.
    /// </summary>
    public static byte[] ComputeSignedPayload(UpdateManifest manifest)
    {
        var writer = new CanonicalJsonWriter();

        writer.BeginObject();
        writer.Property("schemaVersion", manifest.SchemaVersion);
        writer.Property("product", manifest.Product);
        writer.Property("version", manifest.Version);
        writer.Property("minimumUpgradeVersion", manifest.MinimumUpgradeVersion);
        writer.Property("architecture", manifest.Architecture);
        writer.Property("buildCommit", manifest.BuildCommit);
        writer.Property("buildId", manifest.BuildId);
        writer.Property("publishedAt", FormatTimestamp(manifest.PublishedAt));
        writer.Property("entryPoint", manifest.EntryPoint);
        writer.Property("keyId", manifest.KeyId);
        writer.Property("releaseNotesEntry", manifest.ReleaseNotesEntry);
        writer.Property("releaseNotesSha256", manifest.ReleaseNotesSha256);
        writer.Property("releaseNotesBytes", manifest.ReleaseNotesBytes);
        writer.Property("payloadDigest", manifest.PayloadDigest);

        writer.PropertyName("files");
        writer.BeginArray();
        foreach (UpdateFileEntry file in manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            writer.BeginObject();
            writer.Property("path", file.Path);
            writer.Property("bytes", file.Bytes);
            writer.Property("sha256", file.Sha256);
            writer.EndObject();
        }
        writer.EndArray();

        writer.EndObject();

        return writer.ToUtf8();
    }

    /// <summary>
    /// SHA-256 over the canonical rendering of the payload file list. It gives the signed manifest a
    /// single value that summarises "which bytes, in which arrangement", which is convenient to log
    /// and compare without re-listing every file.
    /// </summary>
    public static string ComputePayloadDigest(IEnumerable<UpdateFileEntry> files)
    {
        var writer = new CanonicalJsonWriter();
        writer.BeginArray();
        foreach (UpdateFileEntry file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            writer.BeginObject();
            writer.Property("path", file.Path);
            writer.Property("bytes", file.Bytes);
            writer.Property("sha256", file.Sha256);
            writer.EndObject();
        }
        writer.EndArray();
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToUtf8()));
    }

    public static string ComputeReleaseNotesHash(string releaseNotes)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(releaseNotes)));

    /// <summary>ISO-8601 UTC with fixed precision, so the same instant always renders identically.</summary>
    public static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static string KeyId(ECDsa key)
    {
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        return Convert.ToHexStringLower(SHA256.HashData(spki))[..16];
    }

    public static byte[] Sign(UpdateManifest manifest, ECDsa privateKey)
    {
        byte[] payload = ComputeSignedPayload(manifest);
        return privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static ECDsa LoadPrivateKey(string pemPath)
    {
        string pem = File.ReadAllText(pemPath);
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    public static ECDsa LoadPublicKey(string pemPath)
    {
        string pem = File.ReadAllText(pemPath);
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    public static ECDsa LoadPublicKeyFromBase64(string subjectPublicKeyInfoBase64)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(subjectPublicKeyInfoBase64), out _);
        return key;
    }

    /// <summary>
    /// Verifies a detached signature against a public key, and requires the manifest's signed
    /// <c>keyId</c> to name that same key.
    ///
    /// The second half matters: the key id is what the admin page shows the user as "who signed
    /// this", so a package must not be able to carry a different signer label than the key that
    /// actually verified it. Because <c>keyId</c> is inside the signed payload, swapping the label
    /// changes the payload and the signature fails as well — the explicit check is the belt to that
    /// braces, and it produces a clearer error.
    /// </summary>
    public static void VerifyOrThrow(UpdateManifest manifest, UpdateSignature signature, ECDsa publicKey)
    {
        if (!string.Equals(signature.Algorithm, AlgorithmName, StringComparison.OrdinalIgnoreCase))
            throw new UpdateSignatureException("ALGORITHM_UNSUPPORTED", $"不支持的签名算法：{signature.Algorithm}");

        string trustedKeyId = KeyId(publicKey);

        if (string.IsNullOrWhiteSpace(manifest.KeyId))
            throw new UpdateSignatureException("KEY_ID_MISSING", "更新包清单没有声明签名者标识（keyId），拒绝安装。");

        if (!string.Equals(manifest.KeyId, trustedKeyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateSignatureException("UNKNOWN_KEY_ID",
                $"更新包声明的签名者 {manifest.KeyId} 与本程序内置的发布公钥 {trustedKeyId} 不一致，拒绝安装。");
        }

        byte[] payload = ComputeSignedPayload(manifest);
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(signature.Value);
        }
        catch (FormatException)
        {
            throw new UpdateSignatureException("SIGNATURE_MALFORMED", "签名不是合法的 Base64。");
        }

        bool ok;
        try
        {
            ok = publicKey.VerifyData(payload, raw, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException ex)
        {
            throw new UpdateSignatureException("SIGNATURE_INVALID", "签名校验失败：" + ex.Message);
        }

        if (!ok)
        {
            throw new UpdateSignatureException("SIGNATURE_INVALID",
                "更新包签名无效：清单内容与发布者签名不匹配。");
        }
    }

    /// <summary>
    /// Byte-exact canonical JSON writer.
    ///
    /// Written by hand rather than with a serializer so that the signed bytes are a property of this
    /// file and not of whichever serialization library version happens to be on the machine.
    /// </summary>
    private sealed class CanonicalJsonWriter
    {
        private readonly StringBuilder _builder = new();
        private bool _expectComma;

        public void BeginObject()
        {
            Separate();
            _builder.Append('{');
            _expectComma = false;
        }

        public void EndObject()
        {
            _builder.Append('}');
            _expectComma = true;
        }

        public void BeginArray()
        {
            Separate();
            _builder.Append('[');
            _expectComma = false;
        }

        public void EndArray()
        {
            _builder.Append(']');
            _expectComma = true;
        }

        public void PropertyName(string name)
        {
            Separate();
            WriteString(name);
            _builder.Append(':');
            _expectComma = false;
        }

        public void Property(string name, string value)
        {
            PropertyName(name);
            WriteString(value);
            _expectComma = true;
        }

        public void Property(string name, long value)
        {
            PropertyName(name);
            _builder.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _expectComma = true;
        }

        public byte[] ToUtf8() => Encoding.UTF8.GetBytes(_builder.ToString());

        private void Separate()
        {
            if (_expectComma) _builder.Append(',');
        }

        /// <summary>
        /// ASCII-only escaping: every code point outside 0x20..0x7E becomes <c>\uXXXX</c> (surrogate
        /// pairs for astral characters). The result is therefore byte-identical regardless of how a
        /// JSON encoder would have chosen to emit non-ASCII text.
        /// </summary>
        private void WriteString(string value)
        {
            _builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': _builder.Append("\\\""); break;
                    case '\\': _builder.Append("\\\\"); break;
                    case '\b': _builder.Append("\\b"); break;
                    case '\f': _builder.Append("\\f"); break;
                    case '\n': _builder.Append("\\n"); break;
                    case '\r': _builder.Append("\\r"); break;
                    case '\t': _builder.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E)
                            _builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        else
                            _builder.Append(c);
                        break;
                }
            }
            _builder.Append('"');
        }
    }
}
