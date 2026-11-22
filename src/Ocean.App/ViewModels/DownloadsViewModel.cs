using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

public partial class DownloadVm : ObservableObject
{
    public string Id { get; }
    public string PeerName { get; }
    public string Name { get; }
    public string RelPath { get; }
    public DateTimeOffset CreatedAt { get; }
    [ObservableProperty] private long _size;
    [ObservableProperty] private long _received;
    [ObservableProperty] private DownloadStatus _status;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private double _speedBps;

    public DownloadVm(DownloadJobDto d, string? peerName = null)
    {
        Id = d.Id; PeerName = peerName ?? d.PeerName; Name = d.Name; RelPath = d.RelPath; CreatedAt = d.CreatedAt;
        Update(d);
    }

    public void Update(DownloadJobDto d)
    {
        Size = d.Size; Received = d.Received; Status = d.Status; Error = d.Error; Destination = d.Destination; SpeedBps = d.SpeedBps;
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDone)); OnPropertyChanged(nameof(IsFailed)); OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(ShowBar)); OnPropertyChanged(nameof(Glyph));
    }

    /// <summary>Not over yet (it can still be canceled): waiting, coming, or paused.</summary>
    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Running or DownloadStatus.Paused;
    public bool IsPaused => Status == DownloadStatus.Paused;
    public bool CanPause => Status is DownloadStatus.Queued or DownloadStatus.Running;
    /// <summary>The bar shows while it comes, and where it stopped while paused.</summary>
    public bool ShowBar => Status == DownloadStatus.Running || (Status == DownloadStatus.Paused && Received > 0);
    public bool IsDone => Status == DownloadStatus.Done;
    public bool IsFailed => Status == DownloadStatus.Failed;
    public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Canceled;
    public double Percent => Size > 0 ? Math.Clamp(Received * 100.0 / Size, 0, 100) : 0;
    public string Glyph => Status switch { DownloadStatus.Done => "", DownloadStatus.Failed => "", DownloadStatus.Canceled => "", DownloadStatus.Running => "", DownloadStatus.Paused => "", _ => "" };
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => Error is null ? "Queued" : Error,
        DownloadStatus.Running => $"{Format.Bytes(Received)} of {Format.Bytes(Size)}{(SpeedBps > 0 ? "  ·  " + Format.Speed(SpeedBps) + "  ·  " + Eta() : "")}",
        DownloadStatus.Done => $"Done  ·  {Format.Bytes(Size)}",
        DownloadStatus.Failed => "Failed: " + (Error ?? "unknown error"),
        DownloadStatus.Canceled => "Canceled",
        DownloadStatus.Paused => Received > 0 && Size > 0 ? $"Paused at {Percent:0}%  ·  {Format.Bytes(Received)} of {Format.Bytes(Size)}" : "Paused",
        _ => "",
    };
    public string ProgressText => Status == DownloadStatus.Running ? $"{Percent:0}%" : "";
    private string Eta()
    {
        if (SpeedBps <= 0 || Size <= Received) return "";
        var s = (Size - Received) / SpeedBps;
        return s < 60 ? $"{s:0}s left" : s < 3600 ? $"{s / 60:0} min left" : $"{s / 3600:0.#} h left";
    }
}

/// <summary>A download another PC is making of one of my files, as I send it.</summary>
public partial class UploadVm : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string ShareName { get; }
    [ObservableProperty] private string _who = "";
    [ObservableProperty] private long _size;
    [ObservableProperty] private long _sent;
    [ObservableProperty] private double _speedBps;
    [ObservableProperty] private bool _fullSpeed;
    [ObservableProperty] private bool _active;

    public UploadVm(UploadDto d, string who) { Id = d.Id; Name = d.Name; ShareName = d.ShareName; Update(d, who); }

    public void Update(UploadDto d, string who)
    {
        Who = who; Size = d.Size; Sent = d.Sent; SpeedBps = d.SpeedBps; FullSpeed = d.FullSpeed; Active = d.Active;
        OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(FullSpeedTip));
    }

    public double Percent => Size > 0 ? Math.Min(100, Sent * 100.0 / Size) : 0;
    /// <summary>"143 MB of 381 MB  ·  11 MB/s  ·  22s left"; between requests: where it stopped.</summary>
    public string ProgressText
    {
        get
        {
            var of = Size > 0 ? $"{Format.Bytes(Sent)} of {Format.Bytes(Size)}" : Format.Bytes(Sent);
            if (!Active) return Sent >= Size && Size > 0 ? $"Sent  ·  {Format.Bytes(Size)}" : $"{of}  ·  stopped (paused or waiting on their side)";
            if (SpeedBps <= 0) return of;
            var left = Size > Sent ? (Size - Sent) / SpeedBps : 0;
            var eta = left < 1 ? "" : left < 60 ? $"  ·  {left:0}s left" : left < 3600 ? $"  ·  {left / 60:0} min left" : $"  ·  {left / 3600:0.#} h left";
            return $"{of}  ·  {Format.Speed(SpeedBps)}{eta}";
        }
    }
    public string FullSpeedTip => FullSpeed
        ? "Full speed is on: sent as fast as the network goes. Click to send it at the normal pace again."
        : "Full speed: send this one as fast as the network goes (past your upload limit, not slowed down for people watching).";
}

public partial class DownloadsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<DownloadVm> Jobs { get; } = new();
    /// <summary>What others download from me right now (empty: the section doesn't show).</summary>
    public ObservableCollection<UploadVm> Sending { get; } = new();
    [ObservableProperty] private int _sendingCount;
    [ObservableProperty] private string _sendingSummary = "";
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _downloadDir = "";

    public DownloadsViewModel(MainViewModel main) { _main = main; }

    public async Task LoadAsync()
    {
        try
        {
            var list = await _main.Api.GetAsync<List<DownloadJobDto>>("/api/downloads") ?? new();
            Jobs.Clear();
            foreach (var d in list) Jobs.Add(new DownloadVm(d, PeerLabel(d)));
            Recount();
            SetSending(await _main.Api.GetAsync<List<UploadDto>>("/api/uploads") ?? new());
        }
        catch (Exception ex) { _main.Error("Could not load downloads", ex); }
    }

    /// <summary>The list of what I send, as the service has it now (rows kept in place, so nothing jumps).</summary>
    public void SetSending(List<UploadDto> list)
    {
        foreach (var gone in Sending.Where(s => list.All(d => d.Id != s.Id)).ToList()) Sending.Remove(gone);
        foreach (var d in list)
        {
            var who = _main.Peers.FirstOrDefault(p => p.Id == d.PeerId)?.Label
                      ?? (d.PeerUser.Length > 0 ? d.PeerUser : d.PeerName.Length > 0 ? d.PeerName : "another PC");
            var vm = Sending.FirstOrDefault(s => s.Id == d.Id);
            if (vm is null) Sending.Add(new UploadVm(d, who)); else vm.Update(d, who);
        }
        SendingCount = Sending.Count(s => s.Active);
        var speed = Sending.Where(s => s.Active).Sum(s => s.SpeedBps);
        SendingSummary = SendingCount == 0 ? "" : $"{SendingCount} sending{(speed > 0 ? "  ·  " + Format.Speed(speed) : "")}";
    }

    [RelayCommand]
    private async Task ToggleFullSpeed(UploadVm? up)
    {
        if (up is null) return;
        var on = !up.FullSpeed;
        up.FullSpeed = on; // (at once; the next update confirms it)
        try { await _main.Api.PostAsync($"/api/uploads/{up.Id}/fullspeed?on={(on ? "true" : "false")}"); }
        catch (Exception ex) { up.FullSpeed = !on; _main.Error("Could not change its speed", ex); }
    }

    // the name the sender uses now (older downloads recorded the PC's name)
    private string? PeerLabel(DownloadJobDto d) => _main.Peers.FirstOrDefault(p => p.Id == d.PeerId)?.Label;

    public void Upsert(DownloadJobDto d)
    {
        var vm = Jobs.FirstOrDefault(j => j.Id == d.Id);
        if (vm is null) Jobs.Insert(0, new DownloadVm(d, PeerLabel(d)));
        else vm.Update(d);
        Recount();
    }

    public void RemoveJob(string id)
    {
        var vm = Jobs.FirstOrDefault(j => j.Id == id);
        if (vm is not null) Jobs.Remove(vm);
        Recount();
    }

    private void Recount()
    {
        ActiveCount = Jobs.Count(j => j.IsActive);
        var running = Jobs.Where(j => j.Status == DownloadStatus.Running).ToList();
        var speed = running.Sum(j => j.SpeedBps);
        Summary = ActiveCount == 0 ? (Jobs.Count == 0 ? "" : $"{Jobs.Count(j => j.IsDone)} completed")
                                   : $"{ActiveCount} active{(speed > 0 ? "  ·  " + Format.Speed(speed) : "")}";
        IsEmpty = Jobs.Count == 0;
        _main.ActiveDownloads = ActiveCount;
    }

    [RelayCommand]
    private async Task Cancel(DownloadVm? job)
    {
        if (job is null) return;
        try { await _main.Api.PostAsync($"/api/downloads/{job.Id}/cancel"); } catch (Exception ex) { _main.Error("Could not cancel", ex); }
    }

    [RelayCommand]
    private async Task Pause(DownloadVm? job)
    {
        if (job is null) return;
        try { await _main.Api.PostAsync($"/api/downloads/{job.Id}/pause"); } catch (Exception ex) { _main.Error("Could not pause", ex); }
    }

    [RelayCommand]
    private async Task Resume(DownloadVm? job)
    {
        if (job is null) return;
        try { await _main.Api.PostAsync($"/api/downloads/{job.Id}/resume"); } catch (Exception ex) { _main.Error("Could not resume", ex); }
    }

    [RelayCommand]
    private async Task Retry(DownloadVm? job)
    {
        if (job is null) return;
        try { await _main.Api.PostAsync($"/api/downloads/{job.Id}/retry"); } catch (Exception ex) { _main.Error("Could not retry", ex); }
    }

    [RelayCommand]
    private async Task Remove(DownloadVm? job)
    {
        if (job is null) return;
        try { await _main.Api.DeleteAsync($"/api/downloads/{job.Id}"); RemoveJob(job.Id); } catch (Exception ex) { _main.Error("Could not remove", ex); }
    }

    [RelayCommand]
    private async Task ClearCompleted()
    {
        try { await _main.Api.PostAsync("/api/downloads/clear"); await LoadAsync(); } catch (Exception ex) { _main.Error("Could not clear", ex); }
    }

    [RelayCommand]
    private void Open(DownloadVm? job)
    {
        if (job is null || !job.IsDone) return;
        try { Process.Start(new ProcessStartInfo(job.Destination) { UseShellExecute = true }); }
        catch (Exception ex) { _main.Error("Could not open", ex); }
    }

    [RelayCommand]
    private void Reveal(DownloadVm? job)
    {
        if (job is null) return;
        try
        {
            if (File.Exists(job.Destination)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{job.Destination}\"") { UseShellExecute = true });
            else OpenDownloadsFolder();
        }
        catch { }
    }

    [RelayCommand]
    private void OpenDownloadsFolder()
    {
        try
        {
            if (string.IsNullOrEmpty(DownloadDir)) return;
            Directory.CreateDirectory(DownloadDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DownloadDir}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
