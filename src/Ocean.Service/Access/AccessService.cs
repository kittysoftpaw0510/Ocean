using System.Collections.Concurrent;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.Discovery;
using Ocean.Service.State;

namespace Ocean.Service.Access;

public sealed class GrantRecord
{
    public string Id { get; set; } = "";
    public string LockId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string ClientUser { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsValid => ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow;
}

internal sealed class PendingRequest
{
    public string RequestId { get; init; } = "";
    public string LockId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientName { get; init; } = "";
    public string ClientUser { get; init; } = "";
    public string ClientHost { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Decision { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public GrantRecord? Grant { get; set; }
}

/// <summary>
/// Owner-side authorization: password locks, "ask me" locks (pending requests the UI decides), and the
/// grants issued as a result. Grants are opaque random tokens looked up server-side, so revocation is immediate.
/// </summary>
public sealed class AccessService
{
    private static readonly TimeSpan OnceDuration = TimeSpan.FromHours(8);
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DecidedRetention = TimeSpan.FromMinutes(10);

    private readonly CatalogService _catalog;
    private readonly PeerRegistry _peers;
    private readonly EventHub _events;
    private readonly ILogger<AccessService> _log;
    private readonly string _path;
    private readonly object _lock = new();
    private List<GrantRecord> _grants = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();

    public AccessService(CatalogService catalog, PeerRegistry peers, EventHub events, ILogger<AccessService> log)
    {
        _catalog = catalog; _peers = peers; _events = events; _log = log;
        _path = Path.Combine(OceanDefaults.DataDir, "grants.json");
        try
        {
            if (File.Exists(_path))
                _grants = Json.Deserialize<List<GrantRecord>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) { _log.LogWarning(ex, "grants load failed"); }
        _catalog.LockChanged += lockId => RevokeLock(lockId);
    }

    private void Persist()
    {
        lock (_lock)
        {
            try { AtomicFile.Write(_path, Json.Serialize(_grants.Where(g => g.IsValid).ToList())); }
            catch (Exception ex) { _log.LogWarning(ex, "grants persist failed"); }
        }
    }

    /// <summary>Builds the per-request grant checker from the tokens the client presented.</summary>
    public Func<string, bool> Checker(string clientId, IReadOnlyDictionary<string, string> presented)
    {
        List<GrantRecord> mine;
        lock (_lock) mine = _grants.Where(g => g.ClientId == clientId && g.IsValid).ToList();
        return lockId =>
        {
            if (!presented.TryGetValue(lockId, out var token) || string.IsNullOrEmpty(token)) return false;
            return mine.Any(g => g.LockId == lockId && g.Token == token);
        };
    }

    public AccessResponseDto Request(string clientId, string clientName, string clientUser, string clientHost, AccessRequestDto req)
    {
        var resolved = _catalog.ResolveLock(req.LockId);
        if (resolved is null) return new AccessResponseDto { Status = AccessStatus.NotFound };
        var (mode, hash, salt, label, _, _) = resolved.Value;

        // Already granted? Hand back the existing token (keeps "always" grants stable).
        var existing = FindGrant(req.LockId, clientId);
        if (existing is not null && mode != AccessMode.Password)
            return Granted(existing);

        switch (mode)
        {
            case AccessMode.Public:
                return Granted(Issue(req.LockId, clientId, clientName, clientUser, null));

            case AccessMode.Password:
                if (string.IsNullOrEmpty(req.Password))
                    return new AccessResponseDto { Status = AccessStatus.WrongPassword, Message = "Password required" };
                if (!Passwords.Verify(req.Password, hash, salt))
                {
                    _log.LogInformation("wrong password for '{Label}' from {Client}", label, clientName);
                    return new AccessResponseDto { Status = AccessStatus.WrongPassword, Message = "Wrong password" };
                }
                return Granted(existing ?? Issue(req.LockId, clientId, clientName, clientUser, null));

            case AccessMode.Ask:
            default:
                var pending = _pending.Values.FirstOrDefault(p => p.LockId == req.LockId && p.ClientId == clientId && p.Decision is null);
                if (pending is null)
                {
                    pending = new PendingRequest
                    {
                        RequestId = Passwords.ShortId(8), LockId = req.LockId, ClientId = clientId,
                        ClientName = clientName, ClientUser = clientUser, ClientHost = clientHost,
                    };
                    _pending[pending.RequestId] = pending;
                    _log.LogInformation("access request for '{Label}' from {Client}", label, clientName);
                    _events.Publish("access", ToDto(pending));
                }
                return new AccessResponseDto { Status = AccessStatus.Pending, RequestId = pending.RequestId };
        }
    }

    public AccessResponseDto Poll(string requestId, string clientId)
    {
        if (!_pending.TryGetValue(requestId, out var p) || p.ClientId != clientId)
            return new AccessResponseDto { Status = AccessStatus.NotFound };
        if (p.Decision is null)
        {
            if (DateTimeOffset.UtcNow - p.CreatedAt > PendingTimeout)
            {
                p.Decision = "deny"; p.DecidedAt = DateTimeOffset.UtcNow;
                _events.Publish("accessDecided", new { requestId, decision = "expired" });
                return new AccessResponseDto { Status = AccessStatus.Denied, Message = "No answer from the owner" };
            }
            return new AccessResponseDto { Status = AccessStatus.Pending, RequestId = requestId };
        }
        return p.Grant is not null ? Granted(p.Grant) : new AccessResponseDto { Status = AccessStatus.Denied, Message = "The owner declined" };
    }

    public bool Decide(string requestId, string decision)
    {
        if (!_pending.TryGetValue(requestId, out var p) || p.Decision is not null) return false;
        decision = decision.ToLowerInvariant();
        p.Decision = decision;
        p.DecidedAt = DateTimeOffset.UtcNow;
        if (decision is "once" or "always")
            p.Grant = Issue(p.LockId, p.ClientId, p.ClientName, p.ClientUser, decision == "once" ? DateTimeOffset.UtcNow + OnceDuration : null);
        _log.LogInformation("access {Decision} for request {Id} ({Client})", decision, requestId, p.ClientName);
        _events.Publish("accessDecided", new { requestId, decision });
        return true;
    }

    public IEnumerable<PendingAccessDto> Pending()
    {
        Cleanup();
        return _pending.Values.Where(p => p.Decision is null).OrderBy(p => p.CreatedAt).Select(ToDto).ToList();
    }

    public IEnumerable<GrantDto> Grants()
    {
        lock (_lock)
        {
            return _grants.Where(g => g.IsValid).Select(g =>
            {
                var r = _catalog.ResolveLock(g.LockId);
                // the name that PC uses now; grants from older versions didn't record one
                var user = _peers.TryGet(g.ClientId, out var peer) && !string.IsNullOrWhiteSpace(peer.User) ? peer.User : g.ClientUser;
                return new GrantDto
                {
                    Id = g.Id, LockId = g.LockId, ClientId = g.ClientId, ClientName = g.ClientName, ClientUser = user,
                    LockLabel = r?.Label ?? g.LockId, ExpiresAt = g.ExpiresAt, CreatedAt = g.CreatedAt,
                };
            }).OrderByDescending(g => g.CreatedAt).ToList();
        }
    }

    public bool Revoke(string grantId)
    {
        bool removed;
        lock (_lock) removed = _grants.RemoveAll(g => g.Id == grantId) > 0;
        if (removed) Persist();
        return removed;
    }

    public void RevokeLock(string lockId)
    {
        int removed;
        lock (_lock) removed = _grants.RemoveAll(g => g.LockId == lockId);
        if (removed > 0) { Persist(); _log.LogInformation("revoked {Count} grants for {Lock}", removed, lockId); }
    }

    private GrantRecord? FindGrant(string lockId, string clientId)
    {
        lock (_lock) return _grants.FirstOrDefault(g => g.LockId == lockId && g.ClientId == clientId && g.IsValid);
    }

    private GrantRecord Issue(string lockId, string clientId, string clientName, string clientUser, DateTimeOffset? expires)
    {
        var g = new GrantRecord
        {
            Id = Passwords.ShortId(6), LockId = lockId, ClientId = clientId, ClientName = clientName, ClientUser = clientUser,
            Token = Passwords.RandomToken(), ExpiresAt = expires, CreatedAt = DateTimeOffset.UtcNow,
        };
        lock (_lock)
        {
            _grants.RemoveAll(x => x.LockId == lockId && x.ClientId == clientId);
            _grants.Add(g);
        }
        Persist();
        _events.Publish("grants");
        return g;
    }

    private static AccessResponseDto Granted(GrantRecord g)
        => new() { Status = AccessStatus.Granted, Token = g.Token, ExpiresAt = g.ExpiresAt };

    private PendingAccessDto ToDto(PendingRequest p)
    {
        var r = _catalog.ResolveLock(p.LockId);
        _peers.TryGet(p.ClientId, out var peer);
        return new PendingAccessDto
        {
            RequestId = p.RequestId, LockId = p.LockId,
            LockLabel = r?.Label ?? p.LockId, ShareName = r?.ShareName ?? "", ItemName = r?.ItemName,
            ClientId = p.ClientId,
            ClientName = string.IsNullOrEmpty(p.ClientName) ? peer?.Name ?? NodeIdentity.ShortId(p.ClientId) : p.ClientName,
            ClientUser = string.IsNullOrEmpty(p.ClientUser) ? peer?.User ?? "" : p.ClientUser,
            ClientHost = string.IsNullOrEmpty(p.ClientHost) ? peer?.Host ?? "" : p.ClientHost,
            CreatedAt = p.CreatedAt,
        };
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var p in _pending.Values.ToList())
        {
            if (p.Decision is not null && now - p.DecidedAt > DecidedRetention) _pending.TryRemove(p.RequestId, out _);
            else if (p.Decision is null && now - p.CreatedAt > PendingTimeout + DecidedRetention) _pending.TryRemove(p.RequestId, out _);
        }
    }
}
