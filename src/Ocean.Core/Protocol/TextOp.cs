namespace Ocean.Core.Protocol;

/// <summary>
/// A change to a text that several people type in at once (the board's text page): it walks the whole text from
/// the start, keeping, inserting and deleting (operational transformation, as ot.js does it). Two changes made at
/// the same time to the same text can be <see cref="Transform">transformed</see> against each other so that both,
/// applied in either order, give the same text; that is how everybody's typing ends up the same everywhere, with
/// one PC (the session's host) putting the changes in order.
/// On the wire: a list of parts, "r5" (keep 5), "d3" (delete 3), "i…" (insert what follows the i).
/// </summary>
public sealed class TextOp
{
    /// <summary>One part: keep (N &gt; 0), delete (N &lt; 0) or insert (S, N 0).</summary>
    public readonly record struct Part(int N, string? S)
    {
        public bool IsKeep => S is null && N > 0;
        public bool IsDelete => S is null && N < 0;
        public bool IsInsert => S is not null;
    }

    private readonly List<Part> _parts = new();
    public IReadOnlyList<Part> Parts => _parts;
    /// <summary>How long the text must be before.</summary>
    public int BaseLength { get; private set; }
    /// <summary>How long it is after.</summary>
    public int TargetLength { get; private set; }

    public bool IsNoop => _parts.Count == 0 || (_parts.Count == 1 && _parts[0].IsKeep);

    public TextOp Keep(int n)
    {
        if (n <= 0) return this;
        BaseLength += n; TargetLength += n;
        if (_parts.Count > 0 && _parts[^1].IsKeep) _parts[^1] = new Part(_parts[^1].N + n, null);
        else _parts.Add(new Part(n, null));
        return this;
    }

    public TextOp Insert(string s)
    {
        if (string.IsNullOrEmpty(s)) return this;
        TargetLength += s.Length;
        if (_parts.Count > 0 && _parts[^1].IsInsert) _parts[^1] = new Part(0, _parts[^1].S + s);
        else if (_parts.Count > 0 && _parts[^1].IsDelete)
        {
            // an insert always comes before a delete at the same place (so equal changes look the same)
            if (_parts.Count > 1 && _parts[^2].IsInsert) _parts[^2] = new Part(0, _parts[^2].S + s);
            else { var d = _parts[^1]; _parts[^1] = new Part(0, s); _parts.Add(d); }
        }
        else _parts.Add(new Part(0, s));
        return this;
    }

    public TextOp Delete(int n)
    {
        if (n <= 0) return this;
        BaseLength += n;
        if (_parts.Count > 0 && _parts[^1].IsDelete) _parts[^1] = new Part(_parts[^1].N - n, null);
        else _parts.Add(new Part(-n, null));
        return this;
    }

    /// <summary>One change in one place: in a text <paramref name="length"/> long, at <paramref name="at"/>, <paramref name="delete"/> characters go and <paramref name="insert"/> comes.</summary>
    public static TextOp Splice(int length, int at, int delete, string insert)
        => new TextOp().Keep(at).Delete(delete).Insert(insert).Keep(length - at - delete);

    /// <summary>The change that turns <paramref name="before"/> into <paramref name="after"/> (one place: what they don't share at both ends).</summary>
    public static TextOp Diff(string before, string after)
    {
        int start = 0, max = Math.Min(before.Length, after.Length);
        while (start < max && before[start] == after[start]) start++;
        int end = 0;
        while (end < max - start && before[before.Length - 1 - end] == after[after.Length - 1 - end]) end++;
        return Splice(before.Length, start, before.Length - start - end, after.Substring(start, after.Length - start - end));
    }

    public string Apply(string text)
    {
        if (text.Length != BaseLength) throw new InvalidOperationException($"the change is for a text {BaseLength} long, not {text.Length}");
        var sb = new System.Text.StringBuilder(TargetLength);
        var i = 0;
        foreach (var p in _parts)
        {
            if (p.IsKeep) { sb.Append(text, i, p.N); i += p.N; }
            else if (p.IsInsert) sb.Append(p.S);
            else i -= p.N;
        }
        return sb.ToString();
    }

    /// <summary>The change that undoes this one (<paramref name="text"/>: the text before it).</summary>
    public TextOp Invert(string text)
    {
        var inv = new TextOp();
        var i = 0;
        foreach (var p in _parts)
        {
            if (p.IsKeep) { inv.Keep(p.N); i += p.N; }
            else if (p.IsInsert) inv.Delete(p.S!.Length);
            else { inv.Insert(text.Substring(i, -p.N)); i -= p.N; }
        }
        return inv;
    }

    /// <summary>This change, then <paramref name="next"/>, as one.</summary>
    public TextOp Compose(TextOp next)
    {
        if (TargetLength != next.BaseLength) throw new InvalidOperationException("the second change doesn't follow the first");
        var r = new TextOp();
        var a = _parts; var b = next._parts;
        int ia = 0, ib = 0;
        Part? x = ia < a.Count ? a[ia++] : null, y = ib < b.Count ? b[ib++] : null;
        while (x is not null || y is not null)
        {
            if (x is { IsDelete: true } xd) { r.Delete(-xd.N); x = ia < a.Count ? a[ia++] : null; continue; }
            if (y is { IsInsert: true } yi) { r.Insert(yi.S!); y = ib < b.Count ? b[ib++] : null; continue; }
            if (x is null || y is null) throw new InvalidOperationException("the changes don't fit together");
            var p = x.Value; var q = y.Value;
            if (p.IsKeep && q.IsKeep)
            {
                if (p.N > q.N) { r.Keep(q.N); x = new Part(p.N - q.N, null); y = ib < b.Count ? b[ib++] : null; }
                else if (p.N == q.N) { r.Keep(p.N); x = ia < a.Count ? a[ia++] : null; y = ib < b.Count ? b[ib++] : null; }
                else { r.Keep(p.N); y = new Part(q.N - p.N, null); x = ia < a.Count ? a[ia++] : null; }
            }
            else if (p.IsInsert && q.IsDelete)
            {
                var len = p.S!.Length;
                if (len > -q.N) { x = new Part(0, p.S[-q.N..]); y = ib < b.Count ? b[ib++] : null; }
                else if (len == -q.N) { x = ia < a.Count ? a[ia++] : null; y = ib < b.Count ? b[ib++] : null; }
                else { y = new Part(q.N + len, null); x = ia < a.Count ? a[ia++] : null; }
            }
            else if (p.IsInsert && q.IsKeep)
            {
                var len = p.S!.Length;
                if (len > q.N) { r.Insert(p.S[..q.N]); x = new Part(0, p.S[q.N..]); y = ib < b.Count ? b[ib++] : null; }
                else if (len == q.N) { r.Insert(p.S); x = ia < a.Count ? a[ia++] : null; y = ib < b.Count ? b[ib++] : null; }
                else { r.Insert(p.S); y = new Part(q.N - len, null); x = ia < a.Count ? a[ia++] : null; }
            }
            else if (p.IsKeep && q.IsDelete)
            {
                if (p.N > -q.N) { r.Delete(-q.N); x = new Part(p.N + q.N, null); y = ib < b.Count ? b[ib++] : null; }
                else if (p.N == -q.N) { r.Delete(-q.N); x = ia < a.Count ? a[ia++] : null; y = ib < b.Count ? b[ib++] : null; }
                else { r.Delete(p.N); y = new Part(q.N + p.N, null); x = ia < a.Count ? a[ia++] : null; }
            }
            else throw new InvalidOperationException("the changes don't fit together");
        }
        return r;
    }

    /// <summary>
    /// Two changes made to the same text at the same time: (a', b') such that a then b' and b then a' give the same
    /// text. Where both insert at the same place, <paramref name="a"/>'s text comes first.
    /// </summary>
    public static (TextOp A, TextOp B) Transform(TextOp a, TextOp b)
    {
        if (a.BaseLength != b.BaseLength) throw new InvalidOperationException("the changes aren't for the same text");
        var a2 = new TextOp(); var b2 = new TextOp();
        var pa = a._parts; var pb = b._parts;
        int ia = 0, ib = 0;
        Part? x = ia < pa.Count ? pa[ia++] : null, y = ib < pb.Count ? pb[ib++] : null;
        while (x is not null || y is not null)
        {
            if (x is { IsInsert: true } xi) { a2.Insert(xi.S!); b2.Keep(xi.S!.Length); x = ia < pa.Count ? pa[ia++] : null; continue; }
            if (y is { IsInsert: true } yi) { a2.Keep(yi.S!.Length); b2.Insert(yi.S!); y = ib < pb.Count ? pb[ib++] : null; continue; }
            if (x is null || y is null) throw new InvalidOperationException("the changes don't fit together");
            var p = x.Value; var q = y.Value;
            int min;
            if (p.IsKeep && q.IsKeep)
            {
                if (p.N > q.N) { min = q.N; x = new Part(p.N - q.N, null); y = ib < pb.Count ? pb[ib++] : null; }
                else if (p.N == q.N) { min = q.N; x = ia < pa.Count ? pa[ia++] : null; y = ib < pb.Count ? pb[ib++] : null; }
                else { min = p.N; y = new Part(q.N - p.N, null); x = ia < pa.Count ? pa[ia++] : null; }
                a2.Keep(min); b2.Keep(min);
            }
            else if (p.IsDelete && q.IsDelete)
            {
                if (-p.N > -q.N) { x = new Part(p.N - q.N, null); y = ib < pb.Count ? pb[ib++] : null; }
                else if (p.N == q.N) { x = ia < pa.Count ? pa[ia++] : null; y = ib < pb.Count ? pb[ib++] : null; }
                else { y = new Part(q.N - p.N, null); x = ia < pa.Count ? pa[ia++] : null; }
            }
            else if (p.IsDelete && q.IsKeep)
            {
                if (-p.N > q.N) { min = q.N; x = new Part(p.N + q.N, null); y = ib < pb.Count ? pb[ib++] : null; }
                else if (-p.N == q.N) { min = q.N; x = ia < pa.Count ? pa[ia++] : null; y = ib < pb.Count ? pb[ib++] : null; }
                else { min = -p.N; y = new Part(q.N + p.N, null); x = ia < pa.Count ? pa[ia++] : null; }
                a2.Delete(min);
            }
            else if (p.IsKeep && q.IsDelete)
            {
                if (p.N > -q.N) { min = -q.N; x = new Part(p.N + q.N, null); y = ib < pb.Count ? pb[ib++] : null; }
                else if (p.N == -q.N) { min = p.N; x = ia < pa.Count ? pa[ia++] : null; y = ib < pb.Count ? pb[ib++] : null; }
                else { min = p.N; y = new Part(q.N + p.N, null); x = ia < pa.Count ? pa[ia++] : null; }
                b2.Delete(min);
            }
            else throw new InvalidOperationException("the changes don't fit together");
        }
        return (a2, b2);
    }

    /// <summary>Where a place in the text (a caret) is after this change.</summary>
    public int TransformIndex(int index)
    {
        var at = index; var moved = index;
        foreach (var p in _parts)
        {
            if (p.IsKeep) at -= p.N;
            else if (p.IsInsert) moved += p.S!.Length;
            else { moved -= Math.Min(at, -p.N); at += p.N; }
            if (at < 0) break;
        }
        return moved;
    }

    public List<string> ToWire() => _parts.Select(p => p.IsInsert ? "i" + p.S : p.IsKeep ? "r" + p.N : "d" + -p.N).ToList();

    /// <summary>From the wire; null when it isn't a change (or a too large one).</summary>
    public static TextOp? FromWire(IReadOnlyList<string>? wire, int maxParts = 20_000)
    {
        if (wire is null || wire.Count > maxParts) return null;
        var op = new TextOp();
        foreach (var w in wire)
        {
            if (string.IsNullOrEmpty(w)) return null;
            switch (w[0])
            {
                case 'i': if (w.Length < 2) return null; op.Insert(w[1..]); break;
                case 'r': if (!int.TryParse(w.AsSpan(1), out var r) || r <= 0) return null; op.Keep(r); break;
                case 'd': if (!int.TryParse(w.AsSpan(1), out var d) || d <= 0) return null; op.Delete(d); break;
                default: return null;
            }
        }
        return op;
    }
}
