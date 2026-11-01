using System.Globalization;
using System.Text;
using Ocean.Core;
using Ocean.Core.Naming;
using Ocean.Core.Protocol;
using Ocean.Service.Catalog;
using Ocean.Service.Discovery;
using Ocean.Service.Peers;
using Ocean.Service.State;

namespace Ocean.Service.Social;

/// <summary>
/// Searches every video this PC knows about: the last known list of every node (online or not) and my own shares.
/// Words must all match (in any order, anywhere in the name, folder, share, tags or owner), accents and case don't
/// matter, "#tag" matches a tag only and "@name" the owner only. Best matches first: a word at the start of the
/// name beats one inside it, which beats one in the folder or the owner's name.
/// </summary>
public sealed class NetSearch
{
    private readonly PeerService _peers;
    private readonly PeerRegistry _registry;
    private readonly CatalogService _catalog;
    private readonly ConfigStore _config;

    public NetSearch(PeerService peers, PeerRegistry registry, CatalogService catalog, ConfigStore config)
    {
        _peers = peers; _registry = registry; _catalog = catalog; _config = config;
    }

    public SearchDto Search(string query, int limit)
    {
        var terms = Parse(query);
        var result = new SearchDto { Query = query.Trim() };
        if (terms.Count == 0) return result;

        var hits = new List<(NetItemDto Item, int Score)>();
        foreach (var (peerId, cat) in _peers.AllCatalogs())
        {
            var online = _registry.TryGetOnline(peerId, out _);
            var owner = DisplayNames.Of(cat.UserName, cat.NodeName);
            var ownerKey = Fold(owner + " " + cat.NodeName + " " + cat.Host);
            foreach (var share in cat.Shares)
                foreach (var item in share.Items)
                {
                    if (item.IsMine) continue; // my own video shared back by someone: it is listed under me
                    var score = ScoreOf(terms, item, share.Name, ownerKey);
                    if (score > 0)
                        hits.Add((new NetItemDto { PeerId = peerId, PeerName = owner, Online = online, ShareName = share.Name, Item = item }, score));
                }
        }
        var cfg = _config.Current;
        var me = DisplayNames.Of(cfg.UserName, cfg.NodeName);
        var meKey = Fold(me + " " + cfg.NodeName + " " + Environment.MachineName + " me mine my");
        foreach (var share in cfg.Shares)
            foreach (var item in _catalog.LocalItems(share.Id))
            {
                var score = ScoreOf(terms, item, share.Name, meKey);
                if (score > 0)
                    hits.Add((new NetItemDto { PeerName = me, Online = true, Mine = true, ShareName = share.Name, FullPath = item.FullPath, Item = item }, score));
            }

        var ordered = hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Item.Online).ThenByDescending(h => h.Item.Item.AddedAt).ToList();
        result.Total = ordered.Count;
        result.Nodes = ordered.GroupBy(h => h.Item.PeerId)
            .Select(g => new SearchNodeDto { PeerId = g.Key, PeerName = g.First().Item.PeerName, Online = g.First().Item.Online, Mine = g.First().Item.Mine, Count = g.Count() })
            .OrderByDescending(n => n.Count).ThenBy(n => n.PeerName, StringComparer.CurrentCultureIgnoreCase).ToList();
        result.Items = ordered.Take(limit).Select(h => h.Item).ToList();
        return result;
    }

    private sealed record Term(string Text, char Kind); // kind: ' ' any field, '#' tag, '@' owner

    private static List<Term> Parse(string query)
    {
        var terms = new List<Term>();
        foreach (var raw in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kind = raw[0] is '#' or '@' ? raw[0] : ' ';
            var text = Fold(kind == ' ' ? raw : raw[1..]);
            if (text.Length > 0) terms.Add(new Term(text, kind));
        }
        return terms;
    }

    /// <summary>0 when a term is missing; otherwise the sum of where each term was found.</summary>
    private static int ScoreOf(List<Term> terms, ItemDto item, string shareName, string ownerKey)
    {
        var name = Fold(item.Name); // the extension is a word too: "mp4" lists every MP4
        var nameWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var folder = Fold(item.Folder);
        var share = Fold(shareName);
        var tags = item.Tags is { Count: > 0 } ? item.Tags.Select(Fold).ToList() : new List<string>();
        var total = 0;
        foreach (var t in terms)
        {
            var s = 0;
            if (t.Kind == '#') s = tags.Any(x => x == t.Text) ? 6 : tags.Any(x => x.Contains(t.Text, StringComparison.Ordinal)) ? 4 : 0;
            else if (t.Kind == '@') s = ownerKey.Contains(t.Text, StringComparison.Ordinal) ? 4 : 0;
            else
            {
                if (name == t.Text) s = 12;
                else if (nameWords.Any(w => w == t.Text)) s = 8;
                else if (nameWords.Any(w => w.StartsWith(t.Text, StringComparison.Ordinal))) s = 6;
                else if (name.Contains(t.Text, StringComparison.Ordinal)) s = 4;
                if (s == 0 && tags.Any(x => x.Contains(t.Text, StringComparison.Ordinal))) s = 5;
                if (s == 0 && (folder.Contains(t.Text, StringComparison.Ordinal) || share.Contains(t.Text, StringComparison.Ordinal))) s = 2;
                if (s == 0 && ownerKey.Contains(t.Text, StringComparison.Ordinal)) s = 1;
            }
            if (s == 0) return 0;
            total += s;
        }
        return total;
    }

    /// <summary>Lower case, accents removed, punctuation turned into spaces: "Été_2024-(final).MP4" → "ete 2024 final mp4".</summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        var space = true;
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); space = false; }
            else if (!space) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }
}
