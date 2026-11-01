using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ocean.App.Services;
using Ocean.Core.Protocol;

namespace Ocean.App.ViewModels;

/// <summary>
/// The search page: videos from every node (last known lists, online or not) and my own shares, best matches
/// first, with node chips to narrow the results. The service does the matching (see NetSearch there).
/// </summary>
public partial class SearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, NetItemVm> _byKey = new(StringComparer.Ordinal);
    private List<NetItemVm> _all = new();
    private CancellationTokenSource? _cts;
    private int _refreshPending;

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
        Nodes.Add(AllChip);
        SelectedNode = AllChip;
    }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private List<NetItemVm> _results = new();
    /// <summary>"All" plus one chip per node with matches.</summary>
    public ObservableCollection<SearchNodeVm> Nodes { get; } = new();
    public SearchNodeVm AllChip { get; } = new(null, "All nodes", true, false);
    [ObservableProperty] private SearchNodeVm? _selectedNode;
    partial void OnSelectedNodeChanged(SearchNodeVm? oldValue, SearchNodeVm? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        Apply();
    }

    [RelayCommand] private void SelectNode(SearchNodeVm? chip) => SelectedNode = chip ?? AllChip;

    /// <summary>Runs the query (a newer call cancels an older one still in flight).</summary>
    public async Task RunAsync(string query)
    {
        Query = query;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(query)) { Clear(); return; }
        IsLoading = true;
        try
        {
            var dto = await _main.Api.GetAsync<SearchDto>("/api/search?q=" + Uri.EscapeDataString(query.Trim()), cts.Token);
            if (cts.IsCancellationRequested || dto is null) return;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<NetItemVm>(dto.Items.Count);
            foreach (var d in dto.Items)
            {
                var key = NetItemVm.KeyOf(d);
                keep.Add(key);
                if (_byKey.TryGetValue(key, out var vm)) vm.Update(d);
                else { vm = new NetItemVm(d, _main); _byKey[key] = vm; }
                list.Add(vm);
            }
            foreach (var gone in _byKey.Keys.Where(k => !keep.Contains(k)).ToList()) _byKey.Remove(gone);
            _all = list;

            // node chips (kept in place so a selected one stays selected)
            var selectedId = SelectedNode?.PeerId;
            var chips = new List<SearchNodeVm> { AllChip };
            foreach (var n in dto.Nodes)
            {
                var chip = Nodes.FirstOrDefault(c => c.PeerId == n.PeerId) ?? new SearchNodeVm(n.PeerId, n.Mine ? "My shares" : n.PeerName, n.Online, n.Mine);
                chip.Count = n.Count; chip.Online = n.Online;
                chips.Add(chip);
            }
            AllChip.Count = dto.Total;
            for (var i = Nodes.Count - 1; i >= 0; i--) if (!chips.Contains(Nodes[i])) Nodes.RemoveAt(i);
            foreach (var c in chips) if (!Nodes.Contains(c)) Nodes.Add(c);
            for (var i = 0; i < chips.Count; i++) { var idx = Nodes.IndexOf(chips[i]); if (idx != i) Nodes.Move(idx, i); }
            var sel = chips.FirstOrDefault(c => c.PeerId == selectedId) ?? AllChip;
            if (!ReferenceEquals(sel, SelectedNode)) SelectedNode = sel; else Apply();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Search", $"'{query}' failed", ex); }
        finally { if (ReferenceEquals(cts, _cts)) IsLoading = false; }
    }

    private void Apply()
    {
        var id = SelectedNode?.PeerId;
        var list = id is null ? _all : _all.Where(r => r.Dto.PeerId == id).ToList();
        Results = list;
        var total = id is null ? AllChip.Count : SelectedNode!.Count;
        Summary = Query.Length == 0 ? "" :
            total == 0 ? $"Nothing found for “{Query.Trim()}”" :
            $"{total} video{(total == 1 ? "" : "s")} for “{Query.Trim()}”" + (id is null && Nodes.Count > 2 ? $" on {Nodes.Count - 1} nodes" : "") + (total > list.Count ? $"  ·  showing the best {list.Count}" : "");
        IsEmpty = list.Count == 0 && !IsLoading;
        EmptyText = !IsEmpty ? "" : "No video matches every word. Fewer or shorter words find more; #word looks in tags only, @name in the owner's name.";
    }

    private void Clear()
    {
        _all = new(); _byKey.Clear();
        for (var i = Nodes.Count - 1; i >= 1; i--) Nodes.RemoveAt(i);
        AllChip.Count = 0;
        SelectedNode = AllChip;
        IsLoading = false;
        Apply();
    }

    /// <summary>Lists changed (a node came back, someone voted): the same query again, a moment later.</summary>
    public void ScheduleRefresh()
    {
        if (Query.Length == 0 || Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0) return;
        _ = Task.Delay(2000).ContinueWith(_ => System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Volatile.Write(ref _refreshPending, 0);
            return RunAsync(Query);
        }), TaskScheduler.Default);
    }

    /// <summary>A vote cast from the player on a video in the results.</summary>
    public void ApplyRating(string itemId, MyRatingDto r)
    {
        foreach (var vm in _byKey.Values) if (vm.Item.Id == itemId) vm.Item.ApplyRating(r);
    }
}

/// <summary>A node chip above the search results (PeerId null = all nodes, "" = my own shares).</summary>
public partial class SearchNodeVm : ObservableObject
{
    public SearchNodeVm(string? peerId, string label, bool online, bool mine) { PeerId = peerId; Label = label; Online = online; Mine = mine; }
    public string? PeerId { get; }
    public string Label { get; }
    public bool Mine { get; }
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _online;
    [ObservableProperty] private bool _isSelected;
}
