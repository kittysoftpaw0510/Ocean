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
/// A question needs the app (someone to ask): without it the answer is "away". "Show" and "offer" stay until the
/// user answers (at most 10 min; the asker sees a waiting window with Cancel until then), and go at once when the
/// asker takes them back, its Ocean is closed or its PC goes offline. "Watch" waits 45 s. One at a time per PC and
/// kind; a PC can be muted (kept in screen-muted.json). When a "show" is allowed, the asker hears so at once
/// (<c>/p/screen/progress</c>, "picking"), not only when the screen is shared after the picking.
/// </summary>
public sealed class ScreenAsks : IDisposable
{
    private static readonly TimeSpan PickTime = TimeSpan.FromMinutes(2);
    /// <summary>A "show" or "offer" question stays until it is answered: this long at most.</summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(10);
    /// <summary>My own question: the other PC answers "timeout" first; this only covers a PC that never answers.</summary>
    private static readonly TimeSpan RequestTimeout = PromptTimeout + TimeSpan.FromSeconds(30);
    /// <summary>The other PC counted as offline this long: the question between us is over (the registry has waited already).</summary>
    private static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(2);
    /// <summary>My app gone this long (closed, crashed): my open questions are taken back.</summary>
    private static readonly TimeSpan NoAppAfter = TimeSpan.FromSeconds(8);
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
    /// <summary>Questions to or from this PC whose other PC is offline: since when.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _askerGone = new(StringComparer.Ordinal);
    private DateTimeOffset? _noAppSince;
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
            Title = ask.Title is { Length: > 120 } t ? t[..120] : ask.Title, At = now, ExpiresAt = now + (kind == ScreenAskDto.Watch ? ScreenHost.AskTimeout : PromptTimeout),
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
        // kind show: into what I share already, or the user picks now (the asker hears at once that it is allowed)
        if (_host.Active) return Granted(askId) ? "ok" : "gone";
        p.ExpiresAt = DateTimeOffset.UtcNow + PickTime;
        Send(p.PeerId, "/p/screen/progress", new ScreenAnswerDto { AskId = askId, Allow = true, Reason = "picking" });
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
        // a way that fails for a moment (through another PC above all: two connections, a bridge just busy) is tried
        // again for up to 25 s; asking twice is harmless (the same question id is one question at the other PC)
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (attempt > 0 && !_registry.TryGetOnline(peerId, out peer)) { dto.State = "unreachable"; dto.Message = $"{name} went offline."; return dto; }
                var req = _client.Build(peer, HttpMethod.Post, "/p/screen/ask");
                req.Content = JsonContent.Create(new ScreenAskDto { AskId = dto.AskId, Kind = kind }, options: Json.Options);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp((until - DateTime.UtcNow).TotalSeconds, 3, 12)));
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                if (resp.IsSuccessStatusCode) break;
                var err = resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed ? null : await ReadError(resp, cts.Token);
                // the way's own failure (a bridge: 502/503/504), not the other PC's answer: again, while there is time
                if (err?.Error is not ("away" or "muted" or "busy" or "unsupported") && (int)resp.StatusCode >= 500 && DateTime.UtcNow < until)
                    throw new HttpRequestException($"the way answered {(int)resp.StatusCode}");
                dto.State = resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed ? "old" : err?.Error is "away" or "muted" or "busy" ? err.Error : "unreachable";
                dto.Message = err?.Error == "unsupported" && peer.Bridged
                    ? $"{name} is on another network and reached only through another PC, whose Ocean is too old to pass screen sharing on. Update Ocean on that PC (it is the one shown under {name}'s name)."
                    : Explain(dto.State, name);
                return dto;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= until)
                {
                    _log.LogInformation("screen sharing: {Name} can't be asked: {Msg}", name, ex.Message);
                    dto.State = "unreachable"; dto.Message = $"{name} can't be reached right now.";
                    return dto;
                }
                _log.LogInformation("screen sharing: asking {Name} failed ({Msg}); again", name, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(4, 1 + attempt)), ct); } catch (OperationCanceledException) { throw; }
            }
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

    /// <summary>
    /// Word on my "show me your screen" question before its answer: allowed, they pick what to show now ("picking").
    /// The question stays open (the answer with the session follows); the app closes its waiting window.
    /// </summary>
    public void Progress(string peerId, ScreenAnswerDto progress)
    {
        if (progress.Reason != "picking" || !_requests.TryGetValue(progress.AskId, out var r) || r.Dto.PeerId != peerId) return;
        _requests[progress.AskId] = (r.Dto, DateTimeOffset.UtcNow); // (the picking may take a while)
        _log.LogInformation("screen sharing: {Name} allowed and picks what to show", r.Dto.PeerName);
        _events.Publish("screenRequest", new ScreenRequestDto { AskId = r.Dto.AskId, Kind = r.Dto.Kind, PeerId = peerId, PeerName = r.Dto.PeerName, State = "picking" });
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
            // a question whose asker is out of reach (switched off, left the network): nobody waits for the answer
            foreach (var p in _prompts.Values)
            {
                if (_registry.TryGetOnline(p.PeerId, out _)) { _askerGone.TryRemove(p.AskId, out _); continue; }
                var since = _askerGone.GetOrAdd(p.AskId, now);
                if (now - since > GoneAfter && _prompts.TryRemove(p.AskId, out _)) { _askerGone.TryRemove(p.AskId, out _); Gone(p, "offline"); }
            }
            // my own question to a PC that went offline: nobody will answer it
            foreach (var (id, r) in _requests)
            {
                if (_registry.TryGetOnline(r.Dto.PeerId, out _)) { _askerGone.TryRemove(id, out _); continue; }
                var since = _askerGone.GetOrAdd(id, now);
                if (now - since <= GoneAfter || !_requests.TryRemove(id, out _)) continue;
                _askerGone.TryRemove(id, out _);
                r.Dto.State = "unreachable"; r.Dto.Message = $"{r.Dto.PeerName} went offline.";
                _log.LogInformation("screen sharing: {Name} went offline; the request is over", r.Dto.PeerName);
                _events.Publish("screenRequest", r.Dto);
            }
            foreach (var id in _askerGone.Keys) if (!_prompts.ContainsKey(id) && !_requests.ContainsKey(id)) _askerGone.TryRemove(id, out _);
            // my app went away (closed, crashed): my open questions are taken back, so nobody is left looking at them
            if (_events.SubscriberCount > 0) _noAppSince = null;
            else if (_noAppSince is null) _noAppSince = now;
            else if (now - _noAppSince > NoAppAfter && (!_requests.IsEmpty || !_offered.IsEmpty))
            {
                _log.LogInformation("screen sharing: Ocean was closed; open requests taken back");
                foreach (var id in _requests.Keys.Concat(_offered.Keys).ToList()) CancelRequest(id);
            }
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

    private static async Task<ErrorDto?> ReadError(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadFromJsonAsync<ErrorDto>(Json.Options, ct); }
        catch { return null; } // (a bridge's own failure may carry no body)
    }

    /// <summary>Waits between the tries of a word to another PC: soon at first, then less often; about a minute in all.</summary>
    private static readonly TimeSpan[] SendWaits = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15) };

    /// <summary>
    /// A word to another PC (an answer, "picking", "ready", a cancel): until it arrives, for about a minute. Each
    /// carries its question's id, so one that arrives twice changes nothing. A PC that is offline for a moment (its
    /// way through another PC being renewed) is waited for; a way that fails (a bridge's 5xx) is tried again.
    /// Without this, a lost answer left the asker's waiting window up for ten minutes.
    /// </summary>
    private void Send(string peerId, string path, object body)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                string why;
                try
                {
                    if (_registry.TryGetOnline(peerId, out var peer))
                    {
                        var req = _client.Build(peer, HttpMethod.Post, path);
                        req.Content = JsonContent.Create(body, body.GetType(), options: Json.Options);
                        using var cts = new CancellationTokenSource(10_000);
                        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        if ((int)resp.StatusCode < 500) return; // there (also "not known there": nothing more to do)
                        why = $"answered {(int)resp.StatusCode}";
                    }
                    else why = "offline";
                }
                catch (Exception ex) { why = ex.Message; }
                if (attempt >= SendWaits.Length)
                {
                    _log.LogInformation("screen sharing: {Path} didn't reach {Peer} ({Why})", path, _peers.NameOf(peerId), why);
                    return;
                }
                _log.LogDebug("screen sharing: {Path} to {Peer}: {Why}; again", path, _peers.NameOf(peerId), why);
                await Task.Delay(SendWaits[attempt]);
            }
        });
    }

    public void Dispose() => _tick.Dispose();
}
