using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.App.Services;
using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>The windows of screen sharing (the view side provides them).</summary>
public interface IScreenUi
{
    /// <summary>Lets the user pick what to share: a monitor, a window or a part of a monitor. Null = cancelled.</summary>
    Task<ScreenPick?> PickAsync(string heading, string detail);
    void ShowPrompt(ScreenPromptVm prompt);
    void ClosePrompt(string askId);
    void ShowViewer(ScreenViewVm view);
    void CloseViewer();
    /// <summary>The "you are sharing your screen" bar, kept on top of everything while a session runs.</summary>
    void ShowBar();
    void HideBar();
}

/// <summary>A question waiting for the user: someone wants to see this PC's screen, or to show theirs.</summary>
public sealed partial class ScreenPromptVm : ObservableObject
{
    public ScreenPromptVm(ScreenPromptDto dto) { Dto = dto; Tick(); }
    public ScreenPromptDto Dto { get; }
    public bool IsShow => Dto.Kind == ScreenAskDto.Show;
    public bool IsOffer => Dto.Kind == ScreenAskDto.Offer;
    public string Who => Dto.PeerName;
    public string Headline => IsShow ? $"{Dto.PeerName} asks to see your screen" : $"{Dto.PeerName} wants to show you their screen";
    public string Detail => IsShow
        ? "If you allow it, you choose what they see: a whole screen, one window or a part. You can stop at any time."
        : IsOffer ? "If you say yes, they choose what to show and it opens here. You only watch; nothing of your PC is shown."
        : string.IsNullOrWhiteSpace(Dto.Title) ? "You only watch; nothing of your PC is shown." : $"“{Dto.Title}”. You only watch; nothing of your PC is shown.";
    public string AllowLabel => IsShow ? "Allow…" : "Watch";
    [ObservableProperty] private string _timeLeft = "";
    [ObservableProperty] private bool _busy;
    public void Tick()
    {
        var s = (int)Math.Max(0, (Dto.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        TimeLeft = s > 60 ? "" : $"{s} s";
    }
}

/// <summary>The screen being watched (one at a time).</summary>
public sealed partial class ScreenViewVm : ObservableObject
{
    public ScreenViewVm(ScreenViewDto dto) { PeerId = dto.PeerId; SessionId = dto.SessionId; Title = dto.Title; HostName = dto.HostName; Width = dto.Width; Height = dto.Height; Fps = dto.Fps; Apply(dto); }
    public string PeerId { get; }
    public string SessionId { get; }
    public string Title { get; }
    public string HostName { get; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string WindowTitle => $"{HostName}'s screen  ·  {Title}";
    [ObservableProperty] private bool _ended;
    [ObservableProperty] private string _mode = "connecting";
    [ObservableProperty] private int _viewers;
    /// <summary>A picture is on screen (set by the window).</summary>
    [ObservableProperty] private bool _hasPicture;
    public string StatusText => Ended ? $"{HostName} stopped sharing" : !HasPicture ? "Connecting…" : Mode == "direct" ? "Direct connection" : "Live";
    public string StatusTip => Ended ? "" : Mode == "direct"
        ? "The picture comes over the direct connection to that PC (UDP packets don't arrive well here). It is just as complete, at most a little later."
        : "The picture comes by UDP packets, with the lowest delay.";
    public string ViewersText => Viewers <= 1 ? "" : $"{Viewers} watching";
    partial void OnEndedChanged(bool value) => Refresh();
    partial void OnModeChanged(string value) => Refresh();
    partial void OnHasPictureChanged(bool value) => Refresh();
    partial void OnViewersChanged(int value) => OnPropertyChanged(nameof(ViewersText));
    private void Refresh() { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusTip)); }
    public void Apply(ScreenViewDto dto) { Mode = dto.Mode; Viewers = dto.Viewers; }
}

/// <summary>
/// Screen sharing in the app: sharing my screen (with everyone or with invited PCs), asking to see someone's,
/// answering such questions, and watching. The service does the network side; this captures (ScreenEncoder),
/// shows (the viewer window with ScreenDecoder) and asks the user.
/// </summary>
public sealed partial class ScreenViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly MainViewModel _main;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, ScreenPromptVm> _prompts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _promptClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private ScreenEncoder? _encoder;
    private bool _stopping;
    /// <summary>Between asking the service to start and having the capture ready: the service's own word about the new session arrives in between.</summary>
    private bool _starting;

    public IScreenUi Ui { get; set; } = null!;

    public ScreenViewModel(ApiClient api, MainViewModel main)
    {
        _api = api; _main = main;
        _promptClock.Tick += (_, _) => { foreach (var p in _prompts.Values) p.Tick(); if (_prompts.Count == 0) _promptClock.Stop(); };
    }

    // ------------------------------------------------------------------ my screen (host)

    /// <summary>What I share right now (null: nothing).</summary>
    [ObservableProperty] private MyScreenDto? _mine;
    public bool IsSharing => Mine is not null;
    public string SharingTitle => Mine is null ? "" : Mine.Everyone ? $"Sharing “{Mine.Title}” with everyone" : $"Sharing “{Mine.Title}”";
    public string SharingDetail
    {
        get
        {
            if (Mine is not { } m) return "";
            var watching = m.People.Where(p => p.State == "watching").Select(p => p.Name).ToList();
            var asked = m.People.Where(p => p.State is "asked" or "accepted").Select(p => p.Name).ToList();
            if (watching.Count > 0) return (watching.Count <= 3 ? string.Join(", ", watching) : $"{watching.Count} people") + (watching.Count == 1 ? " is watching" : " are watching");
            if (asked.Count > 0) return "Waiting for " + string.Join(", ", asked.Take(3)) + (asked.Count > 3 ? "…" : "");
            return m.Everyone ? "Nobody is watching yet (nothing is captured until someone does)" : "Nobody is watching";
        }
    }
    public string SharingPeopleTip => Mine is null ? "" : string.Join("\n", Mine.People.Select(p => $"{p.Name}: {StateText(p.State)}"));
    private static string StateText(string s) => s switch
    {
        "asked" => "asked, no answer yet", "accepted" => "is coming", "watching" => "watching", "declined" => "declined", "left" => "left",
        "timeout" => "didn't answer", "away" => "Ocean isn't open there", "old" => "older Ocean (no screen sharing)", "muted" => "doesn't take requests from you", "busy" => "busy", _ => "can't be reached",
    };
    partial void OnMineChanged(MyScreenDto? value)
    {
        OnPropertyChanged(nameof(IsSharing)); OnPropertyChanged(nameof(SharingTitle)); OnPropertyChanged(nameof(SharingDetail)); OnPropertyChanged(nameof(SharingPeopleTip));
    }

    private bool CanCapture()
    {
        if (ScreenTools.Ffmpeg is not null) return true;
        _main.Toast("Screen sharing isn't available", "ffmpeg.exe is missing next to Ocean. Installing Ocean again puts it back.", "error");
        return false;
    }

    [RelayCommand]
    private async Task ShareWithEveryone()
    {
        if (!CanCapture()) return;
        if (Mine is { } running)
        {
            if (running.Everyone) { _main.Toast("Already sharing", "Your screen is shared with everyone. Stop it with the bar at the top of your screen.", "info"); return; }
            if (!await _main.Dialogs.ConfirmAsync("Share with everyone instead?", $"You are sharing “{running.Title}” with invited PCs. That sharing stops, and you pick again what everyone may see.", "Stop and pick")) return;
            await StopAsync();
        }
        var pick = await Ui.PickAsync("Share your screen with everyone", "Everyone on your network can watch until you stop. Pick what they see.");
        if (pick is null) return;
        await StartAsync(pick, everyone: true, Array.Empty<string>(), null);
    }

    /// <summary>
    /// "Show them my screen": the mirror of asking to see theirs. They are asked first; nothing is picked, captured
    /// or shown before they say yes (<see cref="OfferAcceptedAsync"/>).
    /// </summary>
    [RelayCommand]
    private async Task ShowTo(PeerVm? peer)
    {
        if (peer is null || !CanCapture()) return;
        if (!peer.Online) { _main.Toast("Offline", $"{peer.Label} is offline right now.", "warn"); return; }
        if (Mine is { Everyone: true }) { _main.Toast("Already shared with everyone", $"{peer.Label} can watch your screen from their Ocean (it shows LIVE next to your name).", "info"); return; }
        try
        {
            var r = await _api.PostAsync<ScreenRequestDto>($"/api/screen/ask/{peer.Id}?kind={ScreenAskDto.Offer}", null);
            if (r is null) return;
            if (r.State == "waiting")
                _main.Toast("Asked", $"{peer.Label} is asked whether they want to see your screen. Once they say yes, you pick what they see.", "info");
            else _main.Toast("Couldn't ask", r.Message ?? $"{peer.Label} can't be asked right now.", "warn");
        }
        catch (Exception ex) { _main.Toast("Couldn't ask", ex.Message, "error"); }
    }

    /// <summary>They said yes to seeing my screen: into what I share already, or I pick now.</summary>
    private async Task OfferAcceptedAsync(ScreenRequestDto r)
    {
        try
        {
            if (Mine is not null)
            {
                await _api.PostAsync("/api/screen/offer/" + r.AskId + "/go");
                _main.Toast("Watching now", $"{r.PeerName} said yes and now sees what you are sharing.", "success");
                return;
            }
            var pick = CanCapture() ? await Ui.PickAsync($"Show your screen to {r.PeerName}", $"{r.PeerName} said yes. Pick what they see; they watch until you stop.") : null;
            if (pick is null || !await StartAsync(pick, everyone: false, Array.Empty<string>(), null, offerAskId: r.AskId))
            {
                try { await _api.DeleteAsync("/api/screen/ask/" + r.AskId); } catch { } // they stop waiting
            }
        }
        catch (ApiException ex) { _main.Toast("Too late", ex.Message, "info"); }
        catch (Exception ex) { _main.Toast("Screen sharing didn't start", ex.Message, "error"); }
    }

    /// <summary>An offer I said yes to: they share now (event "screenReady"), or not after all.</summary>
    public void ApplyReady(ScreenReadyDto ready)
    {
        if (ready.SessionId is { } sid) { _ = WatchAsync(ready.PeerId, sid); return; }
        _main.Toast("No screen", ready.Reason == "timeout" ? $"{ready.PeerName} didn't share their screen after all (nothing was picked in time)." : $"{ready.PeerName} didn't share their screen after all.", "info");
    }

    private async Task<bool> StartAsync(ScreenPick pick, bool everyone, IReadOnlyList<string> invite, string? askId, string? offerAskId = null)
    {
        _starting = true;
        try
        {
            var (w, h) = pick.SendSize();
            var st = await _api.PostAsync<MyScreenDto>("/api/screen/start", new ScreenStartDto
            {
                Title = pick.Title, Width = w, Height = h, Fps = pick.Fps, Everyone = everyone, Invite = invite.ToList(), AskId = askId, OfferAskId = offerAskId,
            });
            if (st is null || st.Error is not null || !st.Active) { _main.Toast("Screen sharing didn't start", st?.Error ?? "The service didn't start it.", "warn"); return false; }
            _encoder?.Dispose();
            var enc = new ScreenEncoder(_api, st.SessionId!, pick);
            enc.Stopped += why => _ui.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_encoder, enc)) return;
                AppLog.Warn("Screen", "sharing stopped: " + why);
                _main.Toast("Screen sharing stopped", char.ToUpper(why[0]) + why[1..] + ".", "warn");
                _ = StopAsync();
            });
            _encoder = enc;
            AppLog.Info("Screen", $"sharing {pick.Kind} “{pick.Title}” {w}x{h} at {pick.Fps} fps with {(everyone ? "everyone" : "invited PCs")}");
            _starting = false;
            // the newest word from the service (somebody may be watching already)
            ApplyMine(await _api.GetAsync<MyScreenDto>("/api/screen") ?? st);
            return true;
        }
        catch (Exception ex) { _main.Toast("Screen sharing didn't start", ex.Message, "error"); return false; }
        finally { _starting = false; }
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        _stopping = true;
        try { await _api.PostAsync("/api/screen/stop"); }
        catch (Exception ex) { AppLog.Warn("Screen", "stop", ex); }
        ApplyMine(new MyScreenDto());
        _stopping = false;
    }

    /// <summary>My session as the service sees it (event "myScreen", and after connecting).</summary>
    public void ApplyMine(MyScreenDto st)
    {
        if (st.Active)
        {
            if (_starting) return; // the session being started: taken up once its capture is ready
            if (_encoder is null)
            {
                // a session without a capture: the app was started again meanwhile; what was picked is gone with the old app
                _ = _api.PostAsync("/api/screen/stop");
                return;
            }
            Mine = st;
            _encoder.Wanted = st.Wanted;
            Ui.ShowBar();
            return;
        }
        var was = Mine;
        _encoder?.Dispose(); _encoder = null;
        Mine = null;
        Ui.HideBar();
        if (was is not null && !_stopping && st.Ended is { } why && why != "stopped")
            _main.Toast("Screen sharing ended", why == "nobody watches" ? "Nobody accepted, so nothing was shared." : char.ToUpper(why[0]) + why[1..] + ".", "info");
    }

    /// <summary>An invited PC answered (event "screenInvite").</summary>
    public void ApplyInvite(string peerName, string state)
    {
        if (state == "accepted") return; // the bar shows who watches
        _main.Toast(state == "declined" ? "Declined" : "Not watching", $"{peerName}: {StateText(state)}.", "info");
    }

    /// <summary>The app is closing: nothing may go on being captured.</summary>
    public void Shutdown()
    {
        _encoder?.Dispose(); _encoder = null;
        // (on other threads: this one is busy closing the app and must not be waited for)
        if (Mine is not null) { try { Task.Run(() => _api.PostAsync("/api/screen/stop")).Wait(1500); } catch { } }
        if (View is not null) { try { Task.Run(() => _api.PostAsync("/api/screen/view/leave")).Wait(1500); } catch { } }
    }

    // ------------------------------------------------------------------ asking to see someone's screen

    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);

    [RelayCommand]
    private async Task Ask(PeerVm? peer)
    {
        if (peer is null) return;
        if (peer.Screen is { } live) { await WatchAsync(peer.Id, live.SessionId); return; }
        if (!peer.Online) { _main.Toast("Offline", $"{peer.Label} is offline right now.", "warn"); return; }
        if (ScreenTools.Ffmpeg is null) { CanCapture(); return; }
        try
        {
            var r = await _api.PostAsync<ScreenRequestDto>($"/api/screen/ask/{peer.Id}", null);
            if (r is null) return;
            if (r.State == "waiting")
            {
                _waiting.Add(r.AskId);
                _main.Toast("Asked", $"{peer.Label} is asked to show their screen. It opens here as soon as they allow it.", "info");
            }
            else _main.Toast("Couldn't ask", r.Message ?? $"{peer.Label} can't be asked right now.", "warn");
        }
        catch (Exception ex) { _main.Toast("Couldn't ask", ex.Message, "error"); }
    }

    /// <summary>The answer to my question (event "screenRequest").</summary>
    public void ApplyRequest(ScreenRequestDto r)
    {
        _waiting.Remove(r.AskId);
        if (r.Kind == ScreenAskDto.Offer)
        {
            if (r.State == "allowed") { AppLog.Info("Screen", $"{r.PeerName} wants to see this PC's screen"); _ = OfferAcceptedAsync(r); }
            else _main.Toast(r.State == "declined" ? "Declined" : "Not watching", r.Message ?? $"{r.PeerName} doesn't want to see your screen now.", "info");
            return;
        }
        if (r.State == "allowed" && r.SessionId is { } sid) { AppLog.Info("Screen", $"{r.PeerName} allowed their screen"); _ = WatchAsync(r.PeerId, sid); }
        else _main.Toast(r.State == "declined" ? "Declined" : "No screen", r.Message ?? $"{r.PeerName} doesn't show their screen.", "info");
    }

    // ------------------------------------------------------------------ questions to me

    /// <summary>A question for me, or the end of one (event "screenPrompt").</summary>
    public void ApplyPrompt(ScreenPromptDto dto)
    {
        if (dto.Gone is { } gone)
        {
            if (!_prompts.Remove(dto.AskId)) return;
            Ui.ClosePrompt(dto.AskId);
            _main.Toast(gone == "withdrawn" ? "Request taken back" : "Request timed out",
                dto.Kind == ScreenAskDto.Show ? $"{dto.PeerName} no longer asks to see your screen." : $"{dto.PeerName}'s invitation to watch their screen is over.", "info");
            return;
        }
        if (_prompts.ContainsKey(dto.AskId)) return;
        var vm = new ScreenPromptVm(dto);
        _prompts[dto.AskId] = vm;
        _promptClock.Start();
        AppLog.Info("Screen", vm.Headline);
        Ui.ShowPrompt(vm);
        _main.Notify(vm.Headline, "Answer in the small window at the bottom right of your screen.", null);
    }

    public async Task AnswerAsync(ScreenPromptVm prompt, bool allow, bool mute = false)
    {
        if (prompt.Busy) return;
        prompt.Busy = true;
        var dto = prompt.Dto;
        _prompts.Remove(dto.AskId);
        Ui.ClosePrompt(dto.AskId);
        try
        {
            var res = await _api.PostAsync<PromptResult>($"/api/screen/prompts/{dto.AskId}", new ScreenPromptAnswerDto { Allow = allow, Mute = mute });
            if (mute)
            {
                if (_main.Peers.FirstOrDefault(p => p.Id == dto.PeerId) is { } muted) muted.ScreenMuted = true;
                _main.Toast("Muted", $"Screen requests from {dto.PeerName} are not shown any more. Right-click them in the list to allow them again.", "info");
            }
            if (!allow) return;
            if (res?.Result == "gone") { _main.Toast("Too late", $"{dto.PeerName}'s request is over.", "info"); return; }
            if (prompt.IsOffer) { _main.Toast("Waiting", $"{dto.PeerName} is choosing what to show you. It opens here by itself.", "info"); return; }
            if (!prompt.IsShow) { if (dto.SessionId is { } sid) await WatchAsync(dto.PeerId, sid); return; }
            if (res?.Result == "ok") { _main.Toast("Allowed", $"{dto.PeerName} now watches what you are sharing.", "success"); return; }
            // nothing is shared yet: pick what they see
            if (!CanCapture()) { await DeclineQuietly(dto.AskId); return; }
            var pick = await Ui.PickAsync($"Show your screen to {dto.PeerName}", $"{dto.PeerName} sees what you pick here, until you stop.");
            if (pick is null || !await StartAsync(pick, everyone: false, Array.Empty<string>(), dto.AskId)) await DeclineQuietly(dto.AskId);
        }
        catch (Exception ex) { _main.Toast("Couldn't answer", ex.Message, "error"); }
    }

    private async Task DeclineQuietly(string askId)
    {
        try { await _api.PostAsync<PromptResult>($"/api/screen/prompts/{askId}", new ScreenPromptAnswerDto { Allow = false }); } catch { }
    }

    private sealed class PromptResult { public string Result { get; set; } = ""; }

    [RelayCommand]
    private async Task Unmute(PeerVm? peer)
    {
        if (peer is null) return;
        try { await _api.DeleteAsync($"/api/screen/muted/{peer.Id}"); peer.ScreenMuted = false; _main.Toast("Allowed again", $"{peer.Label} can ask about screens again.", "success"); }
        catch (Exception ex) { _main.Toast("Couldn't change that", ex.Message, "error"); }
    }

    // ------------------------------------------------------------------ watching

    /// <summary>The screen I'm watching (null: none).</summary>
    [ObservableProperty] private ScreenViewVm? _view;
    public ApiClient Api => _api;

    [RelayCommand]
    private Task Watch(PeerVm? peer) => peer?.Screen is { } s ? WatchAsync(peer.Id, s.SessionId) : Task.CompletedTask;

    public async Task WatchAsync(string peerId, string sessionId)
    {
        if (ScreenTools.Ffmpeg is null) { CanCapture(); return; }
        if (View is { Ended: false } open && open.SessionId == sessionId) { Ui.ShowViewer(open); return; }
        try
        {
            if (View is not null) { Ui.CloseViewer(); View = null; }
            var dto = await _api.PostAsync<ScreenViewDto>($"/api/peers/{peerId}/screen/{sessionId}/join", null);
            if (dto is null || !dto.Active) { _main.Toast("Can't watch", "That screen is no longer shared.", "warn"); return; }
            View = new ScreenViewVm(dto);
            AppLog.Info("Screen", $"watching {dto.HostName}'s screen ({dto.Width}x{dto.Height})");
            Ui.ShowViewer(View);
        }
        catch (ApiException ex) { _main.Toast("Can't watch", ex.Message, "warn"); }
        catch (Exception ex) { _main.Toast("Can't watch", ex.Message, "error"); }
    }

    /// <summary>What I watch, as the service sees it (event "screenView").</summary>
    public void ApplyView(ScreenViewDto dto)
    {
        if (View is not { } v) return;
        if (dto.Active && dto.SessionId == v.SessionId) { v.Apply(dto); return; }
        if (dto.Ended || !dto.Active) v.Ended = true; // the window stays with the last picture until the user closes it
    }

    /// <summary>The viewer window closed.</summary>
    public async Task LeaveAsync(ScreenViewVm view)
    {
        if (!ReferenceEquals(View, view)) return;
        View = null;
        if (view.Ended) return;
        try { await _api.PostAsync("/api/screen/view/leave"); } catch (Exception ex) { AppLog.Warn("Screen", "leave", ex); }
    }

    // ------------------------------------------------------------------ after (re)connecting to the service

    public async Task SyncAsync()
    {
        try
        {
            if (await _api.GetAsync<MyScreenDto>("/api/screen") is { } mine) ApplyMine(mine);
            foreach (var p in await _api.GetAsync<List<ScreenPromptDto>>("/api/screen/prompts") ?? new()) ApplyPrompt(p);
            var muted = (await _api.GetAsync<List<string>>("/api/screen/muted") ?? new()).ToHashSet(StringComparer.Ordinal);
            foreach (var p in _main.Peers) p.ScreenMuted = muted.Contains(p.Id);
            if (View is { Ended: false } v && await _api.GetAsync<ScreenViewDto>("/api/screen/view") is { } view && (!view.Active || view.SessionId != v.SessionId)) v.Ended = true;
        }
        catch (Exception ex) { AppLog.Warn("Screen", "sync", ex); }
    }
}
