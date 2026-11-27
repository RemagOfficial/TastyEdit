namespace TastyEditor.Core.Levels;

/// <summary>The Tasty Planet 1 edit facade. Every user action is a localised write on the
/// level's verbatim text (through <see cref="Tp1Codec"/>'s span helpers), so a touched record
/// re-derives from its own bytes and nothing else moves. Scalars use <see cref="Tp1Codec.SetNum"/>
/// / <see cref="Tp1Codec.SetAttr"/>; whole-object moves and structural add / remove batch their
/// range edits or clone a same-file sibling so the new element matches the source's own
/// indentation and tag style.</summary>
public static class Tp1Edit
{
    // ---------------------------------------------------------------- move --

    /// <summary>Move the player-start <c>&lt;goo&gt;</c> by a world delta (its <c>x</c>/<c>y</c>).</summary>
    public static bool MoveGoo(Tp1Level lv, double dx, double dy)
        => Nudge(lv, lv.Goo, ("x", dx), ("y", dy));

    /// <summary>Move a <c>spot</c> / <c>shot</c> emitter by a world delta (<c>posx</c>/<c>posy</c>).
    /// A <c>side</c> emitter has no position (it spans an edge), so this is a no-op there.</summary>
    public static bool MoveEmitter(Tp1Node? emitter, double dx, double dy)
        => Nudge(emitter?.Owner, emitter, ("posx", dx), ("posy", dy));

    /// <summary>Move a whole <c>pathfollow</c> chain: translate every <c>&lt;pair&gt;</c> endpoint
    /// (<c>x1</c>/<c>y1</c>/<c>x2</c>/<c>y2</c>) by the delta. The Bézier handle offsets
    /// (<c>coef*</c>) are relative, so they stay. All pair spans are collected from one snapshot
    /// and written in a single batched replacement (one undo step).</summary>
    public static bool MovePath(Tp1Node? controller, double dx, double dy)
    {
        if (controller?.Owner is not { } lv) return false;
        var edits = new List<(int Start, int Len, string Repl)>();
        foreach (var pair in controller.Kids("pair"))
        {
            CollectNudge(pair, "x1", dx, edits);
            CollectNudge(pair, "y1", dy, edits);
            CollectNudge(pair, "x2", dx, edits);
            CollectNudge(pair, "y2", dy, edits);
        }
        if (edits.Count == 0) return false;
        Tp1Codec.ReplaceAll(lv, edits);
        return true;
    }

    // ------------------------------------------------------------ scalar ----

    /// <summary>Set one numeric attribute of a node (one localised write + reindex).</summary>
    public static void SetNum(Tp1Node node, string key, double value) => Tp1Codec.SetNum(node, key, value);

    /// <summary>Move one endpoint of a <c>&lt;pair&gt;</c> segment (a path vertex drag): <paramref
    /// name="ep"/> 0 is the start (<c>x1</c>/<c>y1</c>), 1 the end (<c>x2</c>/<c>y2</c>). Both writes
    /// land in a single batched edit so the record re-derives as one undo step. Reads data-space
    /// coordinates (Y-up); the caller handles the scene flip. <paramref name="pair"/> must be live.</summary>
    public static void SetPairPoint(Tp1Node pair, int ep, double x, double y)
    {
        var edits = new List<(int Start, int Len, string Repl)>();
        PushSetNum(pair, ep == 0 ? "x1" : "x2", x, edits);
        PushSetNum(pair, ep == 0 ? "y1" : "y2", y, edits);
        if (edits.Count > 0) Tp1Codec.ReplaceAll(pair.Owner, edits);
    }

    /// <summary>Write all four endpoints of a freshly added <c>&lt;pair&gt;</c> in one batched edit so
    /// the new segment connects the previous end to a chosen point (used when the path lane adds a
    /// segment). A null coordinate leaves that number as the clone brought it.</summary>
    public static void SetPairCoords(Tp1Node pair,
                                     double? x1, double? y1, double? x2, double? y2)
    {
        var edits = new List<(int Start, int Len, string Repl)>();
        if (x1 is double a) PushSetNum(pair, "x1", a, edits);
        if (y1 is double b) PushSetNum(pair, "y1", b, edits);
        if (x2 is double c) PushSetNum(pair, "x2", c, edits);
        if (y2 is double d) PushSetNum(pair, "y2", d, edits);
        if (edits.Count > 0) Tp1Codec.ReplaceAll(pair.Owner, edits);
    }

    /// <summary>Set one text attribute of a node (one localised write + reindex).</summary>
    public static void SetAttr(Tp1Node node, string key, string value) => Tp1Codec.SetAttr(node, key, value);

    /// <summary>Set a boolean attribute, preserving the source's spelling (<c>true</c>/<c>false</c>
    /// are the only values the engine reads).</summary>
    public static void SetBool(Tp1Node node, string key, bool value)
        => Tp1Codec.SetAttr(node, key, value ? "true" : "false");

    /// <summary>Paint a background <c>&lt;tile&gt;</c> with a tiledef sprite name.</summary>
    public static void PaintTile(Tp1Node tile, string name) => Tp1Codec.SetAttr(tile, "name", name);

    // -------------------------------------------------------- structure ----

    /// <summary>Append a new <c>&lt;pair&gt;</c> to a <c>pathfollow</c> controller, continuing from
    /// the last segment (or cloning the controller's own style). Returns the fresh node.</summary>
    public static Tp1Node? AddPair(Tp1Node controller)
    {
        var pairs = controller.Children.FindAll(c => c.Tag == "pair");
        if (pairs.Count > 0)
            return CloneChild(controller, pairs[^1]);

        // No existing pair: synthesise one off the controller's indent, with a zero-length segment.
        return InsertRaw(controller, "<pair x1=\"0.0\" y1=\"0.0\" coefx1=\"0.0\" coefy1=\"0.0\" " +
                                     "x2=\"0.0\" y2=\"0.0\" coefx2=\"0.0\" coefy2=\"0.0\" />");
    }

    /// <summary>Append a new <c>&lt;affect&gt;</c> to a controller, cloning an existing one when
    /// present (so key order / style matches) or building a default. Returns the fresh node.</summary>
    public static Tp1Node? AddAffect(Tp1Node controller)
    {
        var existing = controller.Children.Find(c => c.Tag == "affect");
        if (existing is not null) return CloneChild(controller, existing);
        return InsertRaw(controller, "<affect entitydef=\"\" />");
    }

    /// <summary>Append a new <c>&lt;dontaffect&gt;</c>, cloning a sibling of the same tag when present.</summary>
    public static Tp1Node? AddDontAffect(Tp1Node controller)
    {
        var existing = controller.Children.Find(c => c.Tag == "dontaffect");
        if (existing is not null) return CloneChild(controller, existing);
        var affect = controller.Children.Find(c => c.Tag == "affect");
        if (affect is not null) return CloneRenamed(controller, affect, "dontaffect");
        return InsertRaw(controller, "<dontaffect entitydef=\"\" />");
    }

    /// <summary>Add a scheduled <c>&lt;emit&gt;</c> to an emitter's <c>&lt;scheduledemit&gt;</c> block,
    /// creating the block if the emitter has none. Returns the fresh emit node.</summary>
    public static Tp1Node? AddEmit(Tp1Node emitter)
    {
        var sched = emitter.Child("scheduledemit");
        if (sched is null)
            return InsertRaw(emitter, "<scheduledemit><emit time=\"0.0\" /></scheduledemit>")?
                   .Child("emit");
        var last = sched.Children.Find(c => c.Tag == "emit");
        return last is not null ? CloneChild(sched, last)
                                : InsertRaw(sched, "<emit time=\"0.0\" />");
    }

    /// <summary>Remove an element (and the whitespace gap that introduced it) from its parent.</summary>
    public static bool Remove(Tp1Node node)
    {
        var parent = node.Parent;
        if (parent is null) return false;
        int gap = GapStart(parent, node);
        node.Owner.Replace(gap, node.End - gap, "");
        return true;
    }

    /// <summary>Insert a brand-new <c>&lt;emitter&gt;</c> cloned from the last emitter of the same
    /// <paramref name="type"/> in this level (provenance: the file's own style), then re-point its
    /// entitydef and (for spot/shot) give it a position. Returns the fresh node, or null when the
    /// level has no emitter of that type to use as a template.</summary>
    public static Tp1Node? NewEmitter(Tp1Level lv, string type, string entitydef, double x = 0, double y = 0)
    {
        // Clone the last emitter so the new record sits at the end of the level and matches the
        // file's own style, then rewrite its identifying attributes in a single batched edit
        // (one reindex) — a node handle is only valid until the next replace, so every attribute
        // span is read from the one snapshot the clone was parsed into.
        var template = lv.Emitters.LastOrDefault();
        if (template is null) return null;
        var created = CloneChild(lv.Root, template);
        if (created is null) return null;

        var edits = new List<(int Start, int Len, string Repl)>();
        PushSet(created, "type", type, edits);
        PushSet(created, "entitydef", entitydef, edits);
        if (type is "spot" or "shot")
        {
            PushSetNum(created, "posx", x, edits);
            PushSetNum(created, "posy", y, edits);
        }
        int[] path = Tp1Level.PathOf(created);
        if (edits.Count > 0) Tp1Codec.ReplaceAll(lv, edits);
        return lv.NodeAt(path);
    }

    /// <summary>Insert a new <c>&lt;goo&gt;</c> player-start, cloned from the level's existing one
    /// (matching its style) when present, else synthesised as a minimal element before
    /// <c>&lt;/level&gt;</c>. Returns the fresh node. A level normally has one goo; this supports the
    /// New-object flow and is fully undoable (a single localised splice).</summary>
    public static Tp1Node? NewGoo(Tp1Level lv, double x = 0, double y = 0)
    {
        var template = lv.Goo;
        if (template is not null)
        {
            var clone = CloneChild(lv.Root, template);
            if (clone is null) return null;
            var edits = new List<(int Start, int Len, string Repl)>();
            PushSetNum(clone, "x", x, edits);
            PushSetNum(clone, "y", y, edits);
            int[] p = Tp1Level.PathOf(clone);
            if (edits.Count > 0) Tp1Codec.ReplaceAll(lv, edits);
            return lv.NodeAt(p);
        }
        return InsertRaw(lv.Root, $"<goo x=\"{Tp1Codec.FormatNumber(null, x)}\" y=\"{Tp1Codec.FormatNumber(null, y)}\" area=\"1\" />");
    }

    /// <summary>Erase a background <c>&lt;tile&gt;</c> back to the game's blank sentinel
    /// (<c>name="null"</c>), which the projector skips. A value-only edit (one localised write).</summary>
    public static void EraseTile(Tp1Node tile) => PaintTile(tile, "null");

    /// <summary>Add a new background <c>&lt;tilelayer&gt;</c>. When the level has no
    /// <c>&lt;tilelayers&gt;</c> container it is created first; when a layer already exists the last one
    /// is cloned (so the fresh grid matches the file's own cell style), else a minimal single-blank-cell
    /// layer is synthesised. Returns the new layer node.</summary>
    public static Tp1Node? AddTileLayer(Tp1Level lv)
    {
        var container = lv.Tilelayers ?? InsertRaw(lv.Root, "<tilelayers />");
        if (container is null) return null;
        var layers = new List<Tp1Node>();
        foreach (var l in container.Kids("tilelayer")) layers.Add(l);
        if (layers.Count > 0) return CloneChild(container, layers[^1]);
        return InsertRaw(container,
            "<tilelayer tileswide=\"1\" tileshigh=\"1\" tilewidth=\"256\" tileheight=\"256\">" +
            "<tile name=\"null\" /></tilelayer>");
    }

    /// <summary>Delete a background layer. A level always keeps at least one, so this refuses and
    /// returns false when only one layer remains; otherwise the layer (and its leading gap) is removed
    /// with the ordinary structural splice.</summary>
    public static bool RemoveTileLayer(Tp1Level lv, Tp1Node layer)
    {
        if (lv.TileLayerCount <= 1) return false;
        return Remove(layer);
    }

    /// <summary>Resize a layer's <c>tileswide</c>/<c>tileshigh</c> grid, 2D-aware and byte-reversible.
    /// Existing cells keep their verbatim source bytes; grown cells are inserted as a blank
    /// <c>&lt;tile name="null" /&gt;</c> (with a gap copied from the layer's own spacing) at the end of
    /// each row / as new trailing rows, and shrunk cells (with their leading gap) are spliced out.
    /// Because every change adds or removes whole cell substrings, resizing back to the original
    /// dimensions restores the exact original bytes. The attribute writes and all cell splices land in
    /// a single <see cref="Tp1Codec.ReplaceAll"/> so it is one undo step, localised to the layer.</summary>
    public static bool ResizeTileLayer(Tp1Level lv, Tp1Node layer, int newWide, int newHigh)
    {
        if (newWide < 1 || newHigh < 1) return false;
        var tiles = new List<Tp1Node>();
        foreach (var t in layer.Kids("tile")) tiles.Add(t);
        int oldW = layer.Int("tileswide") ?? 0;
        int oldH = layer.Int("tileshigh") ?? 0;
        if (oldW < 1 || oldH < 1) return false;
        // The game reads tileswide*tileshigh cells; some shipped layers carry a stray trailing tile,
        // so require the grid cells to be present but allow extras (which stay untouched, keeping the
        // grow / shrink-back revert byte-exact).
        if (tiles.Count < oldW * oldH) return false;
        if (newWide == oldW && newHigh == oldH) return true;

        const string blankCell = "<tile name=\"null\" />";
        // A blank cell's leading gap mirrors the spacing already used between the layer's own tiles.
        string cellGap = tiles.Count > 0
            ? lv.Text.Substring(GapStart(layer, tiles[^1]), tiles[^1].Start - GapStart(layer, tiles[^1]))
            : "\n" + ParentIndent(layer) + "\t";
        string BlankRun(int n)
        {
            var b = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) { b.Append(cellGap); b.Append(blankCell); }
            return b.ToString();
        }
        Tp1Node TileAt(int r, int c) => tiles[r * oldW + c];

        // Structural splices merged by start offset: an insert and a delete that meet on the same
        // boundary (a kept row's trailing insert beside a removed region) collapse into one replace,
        // so ReplaceAll never has to order two edits at one offset.
        var ops = new Dictionary<int, (int Len, System.Text.StringBuilder Ins)>();
        void Ins(int at, string text)
        {
            if (text.Length == 0) return;
            if (!ops.TryGetValue(at, out var e)) { e = (0, new System.Text.StringBuilder()); ops[at] = e; }
            e.Ins.Append(text);
        }
        void Del(int at, int len)
        {
            if (len <= 0) return;
            if (!ops.TryGetValue(at, out var e)) e = (0, new System.Text.StringBuilder());
            e.Len = len;
            ops[at] = e;   // (int Len, StringBuilder) is a value tuple: write the mutated copy back
        }

        int keepRows = Math.Min(oldH, newHigh);

        // Width (per kept row): grow appends blanks after the row's last tile, shrink drops the tail.
        for (int r = 0; r < keepRows; r++)
        {
            if (newWide > oldW)
                Ins(TileAt(r, oldW - 1).End, BlankRun(newWide - oldW));
            else if (newWide < oldW)
            {
                int p = GapStart(layer, TileAt(r, newWide));
                Del(p, TileAt(r, oldW - 1).End - p);
            }
        }

        // Height: grow appends whole blank rows after the last kept row's tail anchor, shrink drops
        // the bottom rows as one contiguous run.
        if (newHigh > oldH)
            Ins(TileAt(oldH - 1, oldW - 1).End, BlankRun((newHigh - oldH) * newWide));
        else if (newHigh < oldH)
        {
            int p = GapStart(layer, TileAt(newHigh, 0));
            Del(p, TileAt(oldH - 1, oldW - 1).End - p);
        }

        var edits = new List<(int Start, int Len, string Repl)>();
        PushSetNum(layer, "tileswide", newWide, edits);
        PushSetNum(layer, "tileshigh", newHigh, edits);
        foreach (var (start, e) in ops)
            edits.Add((start, e.Len, e.Ins.ToString()));
        Tp1Codec.ReplaceAll(lv, edits);
        return true;
    }

    // Batched single-attribute set: replace the value span if present, else insert into the tag.
    // Internal so the table writer (Tp1Tables) batches attribute edits the same way.
    internal static void PushSet(Tp1Node node, string key, string value, List<(int Start, int Len, string Repl)> edits)
    {
        var a = node.Attr(key);
        if (a is not null) { if (a.Value != value) edits.Add((a.ValStart, a.ValLen, Tp1Codec.Esc(a.Quote, value))); return; }
        int at = node.ContentStart - (node.SelfClosing ? 2 : 1);
        edits.Add((at, 0, $" {key}=\"{Tp1Codec.Esc('"', value)}\""));
    }

    internal static void PushSetNum(Tp1Node node, string key, double value, List<(int Start, int Len, string Repl)> edits)
        => PushSet(node, key, Tp1Codec.FormatNumber(node.Get(key), value), edits);

    // --------------------------------------------------------- internals ---

    // Apply two numeric nudges (x by dx, y by dy) in one batched replacement so a move is a
    // single edit.
    private static bool Nudge(Tp1Level? lv, Tp1Node? node,
                              (string key, double delta) a, (string key, double delta) b)
    {
        if (lv is null || node is null) return false;
        var edits = new List<(int Start, int Len, string Repl)>();
        CollectNudge(node, a.key, a.delta, edits);
        CollectNudge(node, b.key, b.delta, edits);
        if (edits.Count == 0) return false;
        Tp1Codec.ReplaceAll(lv, edits);
        return true;
    }

    // Record a value-substring replacement for node[key] += delta (skips absent / unparsable).
    private static void CollectNudge(Tp1Node node, string key, double delta,
                                     List<(int Start, int Len, string Repl)> edits)
    {
        if (delta == 0) return;
        var a = node.Attr(key);
        if (a is null) return;
        var cur = node.Num(key);
        if (cur is null) return;
        var repl = Tp1Codec.FormatNumber(a.Value, cur.Value + delta);
        if (repl == a.Value) return;
        edits.Add((a.ValStart, a.ValLen, repl));
    }

    // Offset where a child's leading whitespace (indent + line break) begins.
    private static int GapStart(Tp1Node parent, Tp1Node child)
    {
        int idx = parent.Children.IndexOf(child);
        return idx <= 0 ? parent.ContentStart : parent.Children[idx - 1].End;
    }

    // Clone an existing child's verbatim block (with its leading gap) and insert it right after.
    // Replace reindexes the whole tree, so the returned handle is re-resolved by path: the clone
    // lands at (template path with its last index + 1), which is stable across the insertion.
    private static Tp1Node? CloneChild(Tp1Node parent, Tp1Node template)
    {
        int gap = GapStart(parent, template);
        string text = parent.Owner.Text.Substring(gap, template.End - gap);
        int[] path = NextSiblingPath(Tp1Level.PathOf(template));
        parent.Owner.Replace(template.End, 0, text);
        return parent.Owner.NodeAt(path);
    }

    // Clone a child but rename its tag on both ends (used to derive a dontaffect from an affect).
    private static Tp1Node? CloneRenamed(Tp1Node parent, Tp1Node template, string newTag)
    {
        int gap = GapStart(parent, template);
        string block = parent.Owner.Text.Substring(gap, template.End - gap);
        block = block.Replace("<" + template.Tag, "<" + newTag)
                     .Replace("</" + template.Tag, "</" + newTag);
        int[] path = NextSiblingPath(Tp1Level.PathOf(template));
        parent.Owner.Replace(template.End, 0, block);
        return parent.Owner.NodeAt(path);
    }

    // A sibling inserted directly after the node at `path` occupies the same parent with its own
    // child index incremented by one.
    private static int[] NextSiblingPath(int[] path)
    {
        var p = (int[])path.Clone();
        p[^1] += 1;
        return p;
    }

    // Insert a raw element fragment as the parent's last child (or first, when empty), matching
    // the indentation of the parent's existing children. A self-closing parent is first expanded
    // into <tag …> … </tag> so the child has somewhere to live. Returns the newly parsed node,
    // re-resolved against the tree Replace rebuilds. Internal so the table writer appends
    // <entitydef> / <imagemap> / <cell> elements with the same indentation-preserving splice.
    internal static Tp1Node? InsertRaw(Tp1Node parent, string fragment)
    {
        var lv = parent.Owner;
        string indent = ParentIndent(parent);
        string nl = DetectNewLine(lv.Text);
        string inner = nl + indent + "\t" + fragment;
        int[] pPath = Tp1Level.PathOf(parent);
        string tag = FirstTag(fragment);

        if (parent.SelfClosing)
        {
            // Swap the trailing "/>" (or "/ >") for ">" + child + "</tag>". Every byte written
            // lies inside the element's own [Start,End) span, so the edit stays localised (I3).
            int slash = lv.Text.LastIndexOf('/', parent.End - 1);
            if (slash < 0 || slash < parent.Start) return null;
            string close = nl + indent + "</" + parent.Tag + ">";
            lv.Replace(slash, parent.End - slash, ">" + inner + close);
        }
        else
        {
            int at = parent.Children.Count > 0 ? parent.Children[^1].End : parent.ContentStart;
            string ins = parent.Children.Count > 0 ? inner : inner + nl + indent;
            lv.Replace(at, 0, ins);
        }
        var p2 = lv.NodeAt(pPath);
        if (p2 is null) return null;
        return LastChildWithTag(p2, tag) ?? p2.Children.LastOrDefault();
    }

    private static string FirstTag(string fragment)
    {
        int lt = fragment.IndexOf('<');
        int i = lt + 1, s = i;
        while (i < fragment.Length && (char.IsLetterOrDigit(fragment[i]) || fragment[i] is '.' or '_' or ':'))
            i++;
        return fragment.Substring(s, i - s);
    }

    private static Tp1Node? LastChildWithTag(Tp1Node parent, string tag)
    {
        for (int i = parent.Children.Count - 1; i >= 0; i--)
            if (parent.Children[i].Tag == tag) return parent.Children[i];
        return null;
    }

    // The parent's own leading indentation, so a synthesised child lines up under it.
    internal static string ParentIndent(Tp1Node parent)
    {
        var t = parent.Owner.Text;
        int lineStart = t.LastIndexOfAny(new[] { '\n', '\r' }, parent.Start) + 1;
        int i = lineStart;
        while (i < parent.Start && (t[i] == ' ' || t[i] == '\t')) i++;
        return t.Substring(lineStart, i - lineStart);
    }

    private static string DetectNewLine(string text)
    {
        int i = text.IndexOf('\n');
        return i > 0 && text[i - 1] == '\r' ? "\r\n" : "\n";
    }
}
