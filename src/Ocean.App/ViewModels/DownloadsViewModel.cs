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
    }

    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Running;
    public bool IsDone => Status == DownloadStatus.Done;
    public bool IsFailed => Status == DownloadStatus.Failed;
    public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Canceled;
    public double Percent => Size > 0 ? Math.Clamp(Received * 100.0 / Size, 0, 100) : 0;
    public string Glyph => Status switch { DownloadStatus.Done => "", DownloadStatus.Failed => "", DownloadStatus.Canceled => "", DownloadStatus.Running => "", _ => "" };
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => Error is null ? "Queued" : Error,
        DownloadStatus.Running => $"{Format.Bytes(Received)} of {Format.Bytes(Size)}{(SpeedBps > 0 ? "  ·  " + Format.Speed(SpeedBps) + "  ·  " + Eta() : "")}",
        DownloadStatus.Done => $"Done  ·  {Format.Bytes(Size)}",
        DownloadStatus.Failed => "Failed: " + (Error ?? "unknown error"),
        DownloadStatus.Canceled => "Canceled",
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

public partial class DownloadsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<DownloadVm> Jobs { get; } = new();
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
        }
        catch (Exception ex) { _main.Error("Could not load downloads", ex); }
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
