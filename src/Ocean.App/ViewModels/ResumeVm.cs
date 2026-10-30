using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ocean.App.Services;
using Ocean.Core;

namespace Ocean.App.ViewModels;

/// <summary>"Continue watching" card on the home page: the last video played and where it stopped.</summary>
public partial class ResumeVm : ObservableObject
{
    public ResumeVm(LastPlayed data) { Data = data; }

    public LastPlayed Data { get; private set; }

    [ObservableProperty] private bool _online = true;
    [ObservableProperty] private BitmapImage? _thumb;

    public bool HasThumb => Thumb is not null;
    partial void OnThumbChanged(BitmapImage? value) => OnPropertyChanged(nameof(HasThumb));
    partial void OnOnlineChanged(bool value) => OnPropertyChanged(nameof(Where));

    public bool Same(LastPlayed other) => other.PeerId == Data.PeerId && other.ItemId == Data.ItemId;

    public void Update(LastPlayed data)
    {
        Data = data;
        foreach (var p in new[] { nameof(Title), nameof(Where), nameof(Progress), nameof(ProgressText), nameof(ActionText), nameof(CanResumeMidway) })
            OnPropertyChanged(p);
    }

    /// <summary>Started but not finished, and far enough in to be worth resuming.</summary>
    public bool CanResumeMidway => !Data.Finished && Data.PositionSeconds >= 5;

    public string Title => Path.GetFileNameWithoutExtension(Data.Name) is { Length: > 0 } stem ? stem : Data.Name;

    public string Where
    {
        get
        {
            var folder = Core.Naming.FolderPath.Display(Data.Folder);
            var where = $"{Data.PeerName}  ·  {Data.ShareName}{(folder.Length > 0 ? "  ›  " + folder : "")}";
            return Online ? where : where + "  ·  offline";
        }
    }

    public double Progress => Data.Finished ? 1 : Data.DurationSeconds > 0 ? Math.Clamp(Data.PositionSeconds / Data.DurationSeconds, 0, 1) : 0;

    public string ProgressText
    {
        get
        {
            if (Data.Finished) return "Watched to the end";
            if (!CanResumeMidway) return "Just started";
            var at = T(Data.PositionSeconds);
            if (Data.DurationSeconds <= 0) return $"Stopped at {at}";
            return $"{at} of {T(Data.DurationSeconds)}  ·  {T(Math.Max(0, Data.DurationSeconds - Data.PositionSeconds))} left";
        }
    }

    public string ActionText => Data.Finished ? "Play again" : CanResumeMidway ? "Resume" : "Play";

    private static string T(double s) => Format.Duration(TimeSpan.FromSeconds(s));
}
