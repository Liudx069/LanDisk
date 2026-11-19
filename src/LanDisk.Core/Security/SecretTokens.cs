using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LanDisk.Core.Security;

/// <summary>Cryptographic helpers. No hand-rolled ciphers, only the platform RNG and hashes.</summary>
public static class SecretTokens
{
    public const int TokenBytes = 32; // 256 bits

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string NewShortCode()
    {
        // 6 digits, drawn from the cryptographic RNG (not Random).
        int value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// A short, URL-safe identifier for a single-purpose, short-lived session (currently the
    /// first-connection wizard). 128 bits: unguessable even though it travels over plain HTTP,
    /// because "it is only a bootstrap page" must not be an excuse for a guessable handle.
    /// </summary>
    public static string NewShortId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static string Hash(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }

    public static bool FixedTimeEquals(string a, string b)
    {
        byte[] ba = Encoding.UTF8.GetBytes(a);
        byte[] bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewPassword(int bytes = 24) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string CertificateFingerprint(byte[] certificateDer)
        => string.Join(':', SHA256.HashData(certificateDer).Select(b => b.ToString("X2")));
}

/// <summary>
/// Fixed window rate limiter used for pairing and other unauthenticated endpoints.
/// Deliberately simple: the goal is to make online guessing of a 6 digit code hopeless,
/// not to implement traffic shaping.
/// </summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private readonly int _limit;
    private readonly TimeSpan _window;

    public RateLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _window = window;
    }

    private sealed class Window
    {
        public DateTimeOffset Start;
        public int Count;
        public DateTimeOffset? BlockedUntil;
        public int Violations;
    }

    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        DateTimeOffset now = DateTimeOffset.Now;
        Window window = _windows.GetOrAdd(key, _ => new Window { Start = now });

        lock (window)
        {
            if (window.BlockedUntil is { } blocked && blocked > now)
            {
                retryAfter = blocked - now;
                return false;
            }

            if (now - window.Start > _window)
            {
                window.Start = now;
                window.Count = 0;
                window.BlockedUntil = null;
                window.Violations = 0;
            }

            window.Count++;
            if (window.Count > _limit)
            {
                window.Violations++;
                TimeSpan penalty = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(window.Violations, 6))));
                window.BlockedUntil = now + penalty;
                retryAfter = penalty;
                return false;
            }

            return true;
        }
    }

    public void Reset(string key) => _windows.TryRemove(key, out _);

    public void Prune()
    {
        DateTimeOffset cutoff = DateTimeOffset.Now - _window - TimeSpan.FromMinutes(10);
        foreach (var pair in _windows)
        {
            if (pair.Value.Start < cutoff && (pair.Value.BlockedUntil is null || pair.Value.BlockedUntil < DateTimeOffset.Now))
                _windows.TryRemove(pair.Key, out _);
        }
    }
}
