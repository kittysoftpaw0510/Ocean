using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Discovery;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Screen;

/// <summary>
/// The questions around screen sharing, both ways. Nothing is ever shown or watched without a click at the PC
/// concerned:
/// - "show me your screen" (kind show): the asked PC's user allows or declines; on allow they pick what to share
///   (or let the asker into what they share already), and the asker is told which session to join;
/// - "may I show you my screen?" (kind offer, the mirror of show): the user watches or declines; on yes, the
///   sender picks what to share and then tells this PC which session to watch (<c>/p/screen/ready</c>);
/// - "look at my screen" (kind watch, sent by <see cref="ScreenHost"/> for every invited PC): the user watches or
///   declines.
/// A question needs the app (someone to ask): without it the answer is "away". It waits 45 s (2 min while the
/// user picks what to share), one at a time per PC, and a PC can be muted (kept in screen-muted.json).
/// </summary>
public sealed class ScreenAsks : IDisposable
{
    private static readonly TimeSpan PickTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(10);

    private readonly ScreenHost _host;
    private readonly PeerService _peers;
    private readonly PeerClient _client;
    private readonly PeerRegistry _registry;
    private readonly EventHub _events;
    private readonly ILogger<ScreenAsks> _log;
    private readonly ConcurrentDictionary<string, ScreenPromptDto> _prompts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (ScreenRequestDto Dto, DateTimeOffset At)> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _declinedAt = new(StringComparer.Ordinal);
    /// <summary>Offers this PC's user said yes to: waiting for the other PC to pick and share.</summary>
    private readonly ConcurrentDictionary<string, (string PeerId, string PeerName, DateTimeOffset At)> _awaiting = new(StringComparer.Ordinal);
    /// <summary>My offers the other PC said yes to: waiting for this PC's user to pick and share.</summary>
    private readonly ConcurrentDictionary<string, (string PeerId, string PeerName, DateTimeOffset At)> _offered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _muted = new(StringComparer.Ordinal);
    private readonly string _mutedPath = Path.Combine(OceanDefaults.DataDir, "screen-muted.json");
    private readonly Timer _tick;

    public ScreenAsks(ScreenHost host, PeerService peers, PeerClient client, PeerRegistry registry, EventHub events, ILogger<ScreenAsks> log)
    {
        _host = host; _peers = peers; _client = client; _registry = registry; _events = events; _log = log;
        try { if (File.Exists(_mutedPath)) foreach (var id in Json.Deserialize<List<string>>(File.ReadAllText(_mutedPath)) ?? new()) _muted.Add(id); }
        catch (Exception ex) { _log.LogWarning("screen-muted.json can't be read: {Msg}", ex.Message); }
        _tick = new Timer(_ => Tick(), null, 1000, 1000);
    }

    // ------------------------------------------------------------------ questions to this PC's user

    /// <summary>A PC asks. Returns null when its user is being asked now, else why not (away, muted, busy).</summary>
    public string? Incoming(string peerId, string peerName, ScreenAskDto ask)
    {
        if (string.IsNullOrWhiteSpace(ask.AskId) || ask.AskId.Length > 64) return "busy";
        lock (_muted) if (_muted.Contains(peerId)) return "muted";
        if (_events.SubscriberCount == 0) return "away"; // Ocean's window and tray are closed: nobody to ask
        if (_prompts.ContainsKey(ask.AskId)) return null; // sent twice
        if (_prompts.Values.Any(p => p.PeerId == peerId && p.Kind == ask.Kind)) return "busy";
        if (_prompts.Count >= 5) return "busy";
        if (_declinedAt.TryGetValue(peerId, out var at) && DateTimeOffset.UtcNow - at < AskAgainAfter) return "busy";
        var kind = ask.Kind is ScreenAskDto.Watch or ScreenAskDto.Offer ? ask.Kind : ScreenAskDto.Show;
        if (kind == ScreenAskDto.Watch && string.IsNullOrEmpty(ask.SessionId)) return "busy";
        var now = DateTimeOffset.UtcNow;
        var prompt = new ScreenPromptDto
        {
            AskId = ask.AskId, Kind = kind, PeerId = peerId, PeerName = peerName, SessionId = ask.SessionId,
            Title = ask.Title is { Length: > 120 } t ? t[..120] : ask.Title, At = now, ExpiresAt = now + ScreenHost.AskTimeout,
        };
        _prompts[ask.AskId] = prompt;
        _log.LogInformation("screen sharing: {Name} asks {What}", peerName, kind == ScreenAskDto.Show ? "to see this PC's screen" : kind == ScreenAskDto.Offer ? "whether they may show their screen" : "this PC to look at their screen");
        _events.Publish("screenPrompt", prompt);
        return null;
    }

    /// <summary>The asking PC took its question back (or its session ended, or it won't share after an accepted offer).</summary>
    public void Withdrawn(string peerId, string askId)
    {
        if (_awaiting.TryGetValue(askId, out var w) && w.PeerId == peerId && _awaiting.TryRemove(askId, out _))
        {
            _events.Publish("screenReady", new ScreenReadyDto { AskId = askId, PeerId = peerId, PeerName = w.PeerName, Reason = "cancelled" });
            return;
        }
        if (!_prompts.TryGetValue(askId, out var p) || p.PeerId != peerId || !_prompts.TryRemove(askId, out _)) return;
        Gone(p, "withdrawn");
    }

    /// <summary>An offer this PC's user said yes to: the other PC shares now (or not after all).</summary>
    public void Ready(string peerId, ScreenReadyDto ready)
    {
        if (!_awaiting.TryGetValue(ready.AskId, out var w) || w.PeerId != peerId || !_awaiting.TryRemove(ready.AskId, out _)) return;
        ready.PeerId = peerId; ready.PeerName = w.PeerName;
        _log.LogInformation("screen sharing: {Name} {What}", w.PeerName, ready.SessionId is null ? "doesn't share after all" : "shares their screen now");
        _events.Publish("screenReady", ready);
    }

    private void Gone(ScreenPromptDto p, string why)
    {
        _events.Publish("screenPrompt", new ScreenPromptDto
        {
            AskId = p.AskId, Kind = p.Kind, PeerId = p.PeerId, PeerName = p.PeerName, SessionId = p.SessionId, Title = p.Title, At = p.At, ExpiresAt = p.ExpiresAt, Gone = why,
        });
    }

    public List<ScreenPromptDto> Prompts() => _prompts.Values.OrderBy(p => p.At).ToList();

    /// <summary>
    /// The user's answer. "ok": done. "pick": allowed to see my screen but nothing is shared yet: the app lets the
    /// user pick and starts the session with this ask id (or answers again with allow = false). "wait": yes to an
    /// offer; the other PC picks now and the screen opens when it shares (event screenReady). "gone": too late.
    /// </summary>
    public string Answer(string askId, ScreenPromptAnswerDto answer)
    {
        if (!_prompts.TryGetValue(askId, out var p)) return "gone";
        if (answer.Mute) Mute(p.PeerId, true);
        if (!answer.Allow)
        {
            _prompts.TryRemove(askId, out _);
            _declinedAt[p.PeerId] = DateTimeOffset.UtcNow;
            Send(p.PeerId, "/p/screen/answer", new ScreenAnswerDto { AskId = askId, Allow = false, Reason = answer.Mute ? "muted" : "declined" });
            _log.LogInformation("screen sharing: declined {Name}", p.PeerName);
            return "ok";
        }
        if (p.Kind == ScreenAskDto.Offer)
        {
            _prompts.TryRemove(askId, out _);
            _awaiting[askId] = (p.PeerId, p.PeerName, DateTimeOffset.UtcNow);
            Send(p.PeerId, "/p/screen/answer", new ScreenAnswerDto { AskId = askId, Allow = true });
            _log.LogInformation("screen sharing: will watch {Name}'s screen once they share it", p.PeerName);
            return "wait";
        }
        if (p.Kind == ScreenAskDto.Watch)
        {
            _prompts.TryRemove(askId, out _);
            Send(p.PeerId, "/p/screen/answer", new ScreenAnswerDto { AskId = askId, Allow = true });
            return "ok";
        }
        // kind show: into what I share already, or the user picks now
        if (_host.Active) return Granted(askId) ? "ok" : "gone";
        p.ExpiresAt = DateTimeOffset.UtcNow + PickTime;
        return "pick";
    }

    /// <summary>The session the user started for a "show me your screen" question runs: its asker is let in and told.</summary>
    public bool Granted(string askId)
    {
        if (!_prompts.TryRemove(askId, out var p)) return false;
        var sid = _host.Admit(p.PeerId, p.PeerName);
        if (sid is null) { Send(p.PeerId, "/p/screen/answer", new ScreenAnswerDto { AskId = askId, Allow = false, Reason = "declined" }); return false; }
        _log.LogInformation("screen sharing: {Name} may see this PC's screen", p.PeerName);
        Send(p.PeerId, "/p/screen/answer", new ScreenAnswerDto { AskId = askId, Allow = true, SessionId = sid });
        return true;
    }

    /// <summary>Who an open "show me your screen" question is from (for the session's first invited PC).</summary>
    public ScreenPromptDto? Prompt(string askId) => _prompts.TryGetValue(askId, out var p) ? p : null;

    // ------------------------------------------------------------------ muted PCs

    public List<string> Muted() { lock (_muted) return _muted.ToList(); }

    public void Mute(string peerId, bool on)
    {
        lock (_muted)
        {
            if (on ? !_muted.Add(peerId) : !_muted.Remove(peerId)) return;
            try { File.WriteAllText(_mutedPath, Json.Serialize(_muted.ToList())); }
            catch (Exception ex) { _log.LogWarning("screen-muted.json can't be written: {Msg}", ex.Message); }
        }
        _log.LogInformation("screen sharing: requests from {Name} are {What}", _peers.NameOf(peerId), on ? "muted" : "allowed again");
    }

    // ------------------------------------------------------------------ my questions to other PCs ("show me your screen", "may I show you mine?")

    public async Task<ScreenRequestDto> RequestAsync(string peerId, string kind, CancellationToken ct)
    {
        kind = kind == ScreenAskDto.Offer ? ScreenAskDto.Offer : ScreenAskDto.Show;
        var name = _peers.NameOf(peerId);
        if (_requests.Values.FirstOrDefault(r => r.Dto.PeerId == peerId && r.Dto.Kind == kind && r.Dto.State == "waiting") is { Dto: not null } open) return open.Dto;
        var dto = new ScreenRequestDto { AskId = Guid.NewGuid().ToString("N"), Kind = kind, PeerId = peerId, PeerName = name };
        if (!_registry.TryGetOnline(peerId, out var peer)) { dto.State = "unreachable"; dto.Message = $"{name} is offline."; return dto; }
        try
        {
            var req = _client.Build(peer, HttpMethod.Post, "/p/screen/ask");
            req.Content = JsonContent.Create(new ScreenAskDto { AskId = dto.AskId, Kind = kind }, options: Json.Options);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(10_000);
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var err = resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed ? null : await resp.Content.ReadFromJsonAsync<ErrorDto>(Json.Options, cts.Token);
                dto.State = resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed ? "old" : err?.Error is "away" or "muted" or "busy" ? err.Error : "unreachable";
                dto.Message = Explain(dto.State, name);
                return dto;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            dto.State = "unreachable"; dto.Message = $"{name} can't be reached right now.";
            return dto;
        }
        _requests[dto.AskId] = (dto, DateTimeOffset.UtcNow);
        _log.LogInformation("screen sharing: asked {Name} {What}", name, kind == ScreenAskDto.Offer ? "whether they want to see this PC's screen" : "to show their screen");
        return dto;
    }

    public static string Explain(string state, string name) => state switch
    {
        "old" => $"{name}'s Ocean is older and has no screen sharing yet.",
        "away" => $"Ocean isn't open on {name}'s PC, so there is nobody to ask.",
        "muted" => $"{name} doesn't take screen requests from this PC.",
        "busy" => $"{name} is busy with another request; try again in a moment.",
        "declined" => $"{name} declined.",
        "timeout" => $"{name} didn't answer.",
        _ => $"{name} can't be reached right now.",
    };

    /// <summary>My question taken back, or my accepted offer given up (the user didn't pick anything to share).</summary>
    public void CancelRequest(string askId)
    {
        if (_offered.TryRemove(askId, out var o)) { Send(o.PeerId, "/p/screen/cancel", new ScreenAnswerDto { AskId = askId }); return; }
        if (!_requests.TryRemove(askId, out var r)) return;
        Send(r.Dto.PeerId, "/p/screen/cancel", new ScreenAnswerDto { AskId = askId });
    }

    /// <summary>Is this accepted offer still open (the other PC waits for my screen)?</summary>
    public bool OfferOpen(string askId) => _offered.ContainsKey(askId);

    /// <summary>An accepted offer is kept: the other PC is let into what I share now and told where to watch. Null: too late, or nothing shared.</summary>
    public string? OfferGo(string askId)
    {
        if (!_offered.TryRemove(askId, out var o)) return null;
        var sid = _host.Admit(o.PeerId, o.PeerName);
        if (sid is null) { Send(o.PeerId, "/p/screen/cancel", new ScreenAnswerDto { AskId = askId }); return null; }
        _log.LogInformation("screen sharing: {Name} said yes and watches now", o.PeerName);
        Send(o.PeerId, "/p/screen/ready", new ScreenReadyDto { AskId = askId, SessionId = sid });
        return sid;
    }

    /// <summary>An answer from another PC: to my own question, or to an invitation of my session.</summary>
    public void Answered(string peerId, ScreenAnswerDto answer)
    {
        if (_requests.TryGetValue(answer.AskId, out var r) && r.Dto.PeerId == peerId && _requests.TryRemove(answer.AskId, out _))
        {
            var dto = r.Dto;
            var yes = answer.Allow && (dto.Kind == ScreenAskDto.Offer || !string.IsNullOrEmpty(answer.SessionId));
            dto.State = yes ? "allowed" : answer.Reason is "timeout" or "away" or "muted" or "busy" ? answer.Reason : "declined";
            if (yes && dto.Kind == ScreenAskDto.Offer) _offered[dto.AskId] = (peerId, dto.PeerName, DateTimeOffset.UtcNow);
            dto.SessionId = answer.Allow ? answer.SessionId : null;
            dto.Message = dto.State == "allowed" ? null : Explain(dto.State, dto.PeerName);
            _log.LogInformation("screen sharing: {Name} answered: {State}", dto.PeerName, dto.State);
            _events.Publish("screenRequest", dto);
            return;
        }
        _host.Answered(peerId, answer);
    }

    // ------------------------------------------------------------------

    private void Tick()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var p in _prompts.Values)
            {
                if (now <= p.ExpiresAt || !_prompts.TryRemove(p.AskId, out _)) continue;
                Send(p.PeerId, "/p/screen/answer", new ScreenAnswerDto { AskId = p.AskId, Allow = false, Reason = "timeout" });
                Gone(p, "timeout");
            }
            // the app went away with questions open: nobody will answer them
            if (_events.SubscriberCount == 0)
                foreach (var p in _prompts.Values)
                    if (_prompts.TryRemove(p.AskId, out _)) Send(p.PeerId, "/p/screen/answer", new ScreenAnswerDto { AskId = p.AskId, Allow = false, Reason = "away" });
            // an offer said yes to, but its sender never shared (the picker left open, the app gone): over for both
            foreach (var (id, w) in _awaiting)
                if (now - w.At > PickTime + TimeSpan.FromSeconds(30) && _awaiting.TryRemove(id, out _))
                    _events.Publish("screenReady", new ScreenReadyDto { AskId = id, PeerId = w.PeerId, PeerName = w.PeerName, Reason = "timeout" });
            foreach (var (id, o) in _offered)
                if (now - o.At > PickTime && _offered.TryRemove(id, out _)) Send(o.PeerId, "/p/screen/cancel", new ScreenAnswerDto { AskId = id });
            foreach (var (id, r) in _requests)
            {
                if (now - r.At <= RequestTimeout || !_requests.TryRemove(id, out _)) continue;
                r.Dto.State = "timeout"; r.Dto.Message = Explain("timeout", r.Dto.PeerName);
                _events.Publish("screenRequest", r.Dto);
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "screen asks tick"); }
    }

    private void Send(string peerId, string path, object body)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (!_registry.TryGetOnline(peerId, out var peer)) return;
                    var req = _client.Build(peer, HttpMethod.Post, path);
                    req.Content = JsonContent.Create(body, body.GetType(), options: Json.Options);
                    using var cts = new CancellationTokenSource(8000);
                    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    return;
                }
                catch (Exception ex) { _log.LogDebug("screen sharing: {Path} to {Peer}: {Msg}", path, _peers.NameOf(peerId), ex.Message); }
                await Task.Delay(1500);
            }
        });
    }

    public void Dispose() => _tick.Dispose();
}
