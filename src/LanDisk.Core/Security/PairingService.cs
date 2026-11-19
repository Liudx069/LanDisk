using System.Collections.Concurrent;

namespace LanDisk.Core.Security;

public enum PairingState
{
    Pending,
    AwaitingLocalConfirmation,
    Redeemed,
    Expired,
    Cancelled
}

public sealed class PairingRequest
{
    public string Id { get; init; } = SecretTokens.NewId();

    /// <summary>256-bit one time bootstrap token carried in the QR code / link.</summary>
    public string Token { get; init; } = SecretTokens.NewToken();

    /// <summary>Six digit fallback code that always requires explicit local confirmation.</summary>
    public string ShortCode { get; init; } = SecretTokens.NewShortCode();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.Now.AddMinutes(2);
    public PairingState State { get; set; } = PairingState.Pending;
    public string? DeviceName { get; set; }
    public string? DeviceUserAgent { get; set; }
    public string? RemoteAddress { get; set; }
    public string? IssuedDeviceId { get; set; }
    public bool LocalConfirmationGranted { get; set; }

    /// <summary>
    /// The HTTPS origin that actually worked for this phone, captured when the operator confirmed it.
    ///
    /// <para>Lives on the request on purpose: the wizard's connectivity hint is cleared the moment pairing
    /// succeeds (it described that attempt and nothing else), while the completion screen is rendered a
    /// fraction of a second later and must still be able to say "use this address" instead of pointing the
    /// user back at the name that just failed. Display information for one flow — never a credential, never
    /// persisted.</para>
    /// </summary>
    public string? RecommendedOrigin { get; set; }

    /// <summary>Secret handed to the phone when it submits the 6 digit code, so that only that
    /// phone can claim the session after the operator confirms locally.</summary>
    public string? ClaimSecret { get; set; }

    public bool IsExpired => DateTimeOffset.Now >= ExpiresAt;
}

/// <summary>
/// Issues and redeems short lived, single use pairing requests.
/// A request is created by the local administrator; the 256-bit token is delivered
/// out of band through the loopback management page (QR code / manual link).
/// The 6 digit code path additionally requires an explicit click by the local operator.
/// </summary>
public sealed class PairingService
{
    private readonly ConcurrentDictionary<string, PairingRequest> _byId = new();
    private readonly ConcurrentDictionary<string, string> _byToken = new();
    private readonly ConcurrentDictionary<string, string> _byShortCode = new();
    private readonly RateLimiter _tokenLimiter = new(limit: 8, window: TimeSpan.FromMinutes(5));
    private readonly RateLimiter _codeLimiter = new(limit: 6, window: TimeSpan.FromMinutes(5));

    public PairingRequest Create()
    {
        Prune();
        var request = new PairingRequest();
        _byId[request.Id] = request;
        _byToken[request.Token] = request.Id;
        _byShortCode[request.ShortCode] = request.Id;
        return request;
    }

    public PairingRequest? Find(string id)
    {
        if (!_byId.TryGetValue(id, out PairingRequest? request)) return null;
        if (request.IsExpired && request.State is PairingState.Pending or PairingState.AwaitingLocalConfirmation)
            request.State = PairingState.Expired;
        return request;
    }

    public PairingRequest? FindByToken(string token)
    {
        if (!_byToken.TryGetValue(token, out string? id)) return null;
        return Find(id);
    }

    public PairingRequest? FindByShortCode(string code)
    {
        if (!_byShortCode.TryGetValue(code, out string? id)) return null;
        return Find(id);
    }

    public bool TryAcquireTokenAttempt(string remoteKey, out TimeSpan retryAfter)
        => _tokenLimiter.TryAcquire("token:" + remoteKey, out retryAfter);

    public bool TryAcquireCodeAttempt(string remoteKey, out TimeSpan retryAfter)
        => _codeLimiter.TryAcquire("code:" + remoteKey, out retryAfter);

    public void Cancel(string id)
    {
        if (_byId.TryGetValue(id, out PairingRequest? request))
        {
            request.State = PairingState.Cancelled;
            Forget(request);
        }
    }

    public void Complete(PairingRequest request, string deviceId)
    {
        request.State = PairingState.Redeemed;
        request.IssuedDeviceId = deviceId;
        Forget(request);
    }

    private void Forget(PairingRequest request)
    {
        _byToken.TryRemove(request.Token, out _);
        _byShortCode.TryRemove(request.ShortCode, out _);
    }

    public void Prune()
    {
        DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-10);
        foreach (var pair in _byId.ToList())
        {
            if (pair.Value.CreatedAt < cutoff)
            {
                _byId.TryRemove(pair.Key, out _);
                Forget(pair.Value);
            }
        }
    }

    public IReadOnlyList<PairingRequest> Active() => _byId.Values
        .Where(r => r.State is PairingState.Pending or PairingState.AwaitingLocalConfirmation)
        .OrderByDescending(r => r.CreatedAt)
        .ToList();
}

public sealed class AdminSession
{
    public string Id { get; init; } = SecretTokens.NewId();
    public string TokenHash { get; init; } = string.Empty;
    public string CsrfToken { get; init; } = SecretTokens.NewToken();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.Now.AddHours(12);
    public string? RemoteAddress { get; init; }
    public bool IsExpired => DateTimeOffset.Now >= ExpiresAt;
}

/// <summary>
/// In-memory admin sessions. Bootstrap is a 256-bit token written to the data directory
/// (readable only by the account that runs LanDisk) and printed to the console at startup.
/// Restarting the process invalidates every admin session by design.
/// </summary>
public sealed class AdminSessionStore
{
    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new();

    public AdminSession Create(string? remoteAddress)
    {
        var session = new AdminSession
        {
            TokenHash = SecretTokens.Hash(SecretTokens.NewToken()),
            RemoteAddress = remoteAddress
        };
        return session;
    }

    public AdminSession Issue(string? remoteAddress, out string rawToken)
    {
        rawToken = SecretTokens.NewToken();
        var session = new AdminSession
        {
            TokenHash = SecretTokens.Hash(rawToken),
            RemoteAddress = remoteAddress
        };
        _sessions[session.TokenHash] = session;
        return session;
    }

    public AdminSession? Validate(string? rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;
        string hash = SecretTokens.Hash(rawToken);
        if (!_sessions.TryGetValue(hash, out AdminSession? session)) return null;
        if (session.IsExpired)
        {
            _sessions.TryRemove(hash, out _);
            return null;
        }
        session.LastSeenAt = DateTimeOffset.Now;
        return session;
    }

    public void RevokeAll() => _sessions.Clear();
}
