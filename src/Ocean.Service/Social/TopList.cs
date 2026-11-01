using Ocean.Core;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.Discovery;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Social;

/// <summary>
/// "Top on the network": the best videos of every PC this one knows (its last known catalog when offline) and its
/// own, ranked by the owners' scores (votes, fading with age, plus "watched to the end"), optionally for one tag.
/// Only videos someone liked or finished make the list; copies of my own videos shared back by others are left out.
/// </summary>
public sealed class TopList
{
    private readonly PeerService _peers;
    private readonly PeerRegistry _registry;
    private readonly CatalogService _catalog;
    private readonly ConfigStore _config;

    public TopList(PeerService peers, PeerRegistry registry, CatalogService catalog, ConfigStore config)
    {
        _peers = peers; _registry = registry; _catalog = catalog; _config = config;
    }

    public TopDto Build(string? tag, int limit)
    {
        var all = new List<NetItemDto>();
        foreach (var (peerId, cat) in _peers.AllCatalogs())
        {
            var online = _registry.TryGetOnline(peerId, out _);
            var name = DisplayNames.Of(cat.UserName, cat.NodeName);
            foreach (var share in cat.Shares)
                foreach (var item in share.Items)
                    if (!item.IsMine)
                        all.Add(new NetItemDto { PeerId = peerId, PeerName = name, Online = online, ShareName = share.Name, Item = item });
        }
        var cfg = _config.Current;
        foreach (var share in cfg.Shares)
            foreach (var item in _catalog.LocalItems(share.Id))
                all.Add(new NetItemDto { PeerName = DisplayNames.Of(cfg.UserName, cfg.NodeName), Online = true, Mine = true, ShareName = share.Name, FullPath = item.FullPath, Item = item });

        var dto = new TopDto
        {
            Tags = all.SelectMany(t => t.Item.Tags ?? new List<string>())
                .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new TagCountDto { Tag = g.First(), Count = g.Count() })
                .OrderByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.CurrentCultureIgnoreCase).Take(30).ToList(),
        };
        var ranked = all.Where(t => t.Item.Up > 0 || t.Item.Finished > 0);
        if (!string.IsNullOrWhiteSpace(tag))
            ranked = ranked.Where(t => t.Item.Tags?.Any(x => string.Equals(x, tag, StringComparison.CurrentCultureIgnoreCase)) == true);
        dto.Items = ranked.OrderByDescending(t => t.Item.Score).ThenByDescending(t => t.Item.Up).ThenByDescending(t => t.Item.AddedAt).Take(limit).ToList();
        return dto;
    }
}
