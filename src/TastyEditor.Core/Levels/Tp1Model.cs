using System.Globalization;
using System.Text;

namespace TastyEditor.Core.Levels;

// Tasty Planet 1 levels are plain-text XML, but the shipped files are NOT uniformly
// formatted: no XML declaration on most, one on a few, mixed tab / space indent, a
// literal misspelled attribute (conditon) and per-number textual styles that differ
// between records. A canonical re-serializer therefore cannot reproduce a file
// byte-for-byte, so this model is verbatim / span-preserving: every element records
// the exact byte offsets it occupies in the source text, and an untouched model
// re-emits the original bytes exactly (invariant I1). An edit replaces only the
// touched substring inside that text, so no other byte moves (I3).
//
// The text is held in a Latin1 string: encoding 0..255 round-trips byte-for-byte
// through a single char, so char offsets are byte offsets and re-encoding is lossless
// for any file even though every shipped level happens to be pure ASCII.

/// <summary>One XML element in the verbatim tree. Every field is an absolute offset
/// into <see cref="Tp1Level.Text"/> (char == byte) except <see cref="Tag"/>, which is a
/// cached copy of the name. Nothing is normalised: <see cref="Attrs"/> keeps the source
/// key case, quoting and order, and unknown elements are represented here too so the
/// codec never drops content (unlike the lossy base editor).</summary>
public sealed class Tp1Node
{
    /// <summary>Offset of the element's opening <c>&lt;</c>.</summary>
    public int Start;

    /// <summary>Offset / length of the tag name (the run right after <c>&lt;</c>).</summary>
    public int NameStart;
    public int NameLen;

    /// <summary>Tag name as read (source case, never lower-cased).</summary>
    public string Tag = "";

    /// <summary>True for a <c>&lt;empty /&gt;</c> element that has no children or end tag.</summary>
    public bool SelfClosing;

    /// <summary>Offset just past the opening tag's <c>&gt;</c> (or <c>/&gt;</c>) — the start
    /// of the element's content region. For a self-closing element this equals <see cref="End"/>.</summary>
    public int ContentStart;

    /// <summary>Offset just past the element's closing <c>&gt;</c> — one byte beyond the
    /// element's own span. The verbatim bytes from <see cref="Start"/> to <see cref="End"/>
    /// reproduce the whole element plus nothing between siblings (whitespace lives outside
    /// both nodes and is carried verbatim in the level text).</summary>
    public int End;

    /// <summary>Attributes in source order, each with the exact span of its value text.</summary>
    public readonly List<Tp1Attr> Attrs = new();

    /// <summary>Child elements in source order (inter-element text / whitespace is not a node;
    /// it stays in the level text and is preserved across splices).</summary>
    public readonly List<Tp1Node> Children = new();

    /// <summary>Owning level (supplies the mutable text buffer so an edit can splice in place).</summary>
    public Tp1Level Owner = null!;

    /// <summary>Enclosing element, or null for the root. Set during parse so a splice can find
    /// the node's position among its siblings (for cloning its leading indentation).</summary>
    public Tp1Node? Parent;

    /// <summary>Byte span of the element in the source text.</summary>
    public int Length => End - Start;

    /// <summary>The element's verbatim source text.</summary>
    public string RawText => Owner.Text.Substring(Start, End - Start);

    /// <summary>Attribute with the given key (case-sensitive, source spelling) or null.</summary>
    public Tp1Attr? Attr(string key)
    {
        for (int i = 0; i < Attrs.Count; i++)
            if (string.Equals(Attrs[i].Key, key, StringComparison.Ordinal)) return Attrs[i];
        return null;
    }

    /// <summary>True when the element carries <paramref name="key"/> (source spelling).</summary>
    public bool Has(string key) => Attr(key) is not null;

    /// <summary>Raw value text of an attribute, or null when absent. Quotes are not part of it.</summary>
    public string? Get(string key) => Attr(key)?.Value;

    /// <summary>Parse a numeric attribute (invariant culture), or null when absent / unparsable.</summary>
    public double? Num(string key)
    {
        var v = Get(key);
        return v is not null &&
               double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Parse an integer attribute, or null when absent / unparsable.</summary>
    public int? Int(string key)
    {
        var v = Get(key);
        return v is not null &&
               int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    /// <summary>First direct child with this tag, or null.</summary>
    public Tp1Node? Child(string tag)
    {
        for (int i = 0; i < Children.Count; i++)
            if (string.Equals(Children[i].Tag, tag, StringComparison.Ordinal)) return Children[i];
        return null;
    }

    /// <summary>All direct children with this tag, in source order.</summary>
    public IEnumerable<Tp1Node> Kids(string tag)
    {
        foreach (var c in Children)
            if (string.Equals(c.Tag, tag, StringComparison.Ordinal)) yield return c;
    }
}

/// <summary>One attribute as read: the exact span of its value text plus the quoting style
/// so a rewrite can preserve <c>"quoted"</c> / <c>'quoted'</c> / <c>unquoted</c> and never
/// touch the surrounding bytes.</summary>
public sealed class Tp1Attr
{
    public Tp1Node Owner = null!;
    public int KeyStart;
    public int KeyLen;
    public string Key = "";

    /// <summary>Quote character (<c>&quot;</c> or <c>'</c>), or <c>(char)0</c> for an unquoted value.</summary>
    public char Quote;

    /// <summary>Offset / length of the value text (the characters between the quotes, or the
    /// bare token when unquoted). This is exactly the substring <see cref="Tp1Codec.SetAttr"/>
    /// replaces on a scalar edit (I3).</summary>
    public int ValStart;
    public int ValLen;

    /// <summary>The value text as stored (no surrounding quotes).</summary>
    public string Value => Owner.Owner.Text.Substring(ValStart, ValLen);
}

/// <summary>A decoded Tasty Planet 1 level: the original bytes, the working Latin1 text
/// (char == byte), and the verbatim element tree rooted at <c>&lt;level&gt;</c>. Re-encoding
/// an untouched model returns the original byte-for-byte; every edit goes through
/// <see cref="Replace"/> so only the touched range of <see cref="Text"/> ever changes.</summary>
public sealed class Tp1Level
{
    /// <summary>The bytes as loaded — returned verbatim by <see cref="Encode"/> while clean (I1).</summary>
    public byte[] Original = System.Array.Empty<byte>();

    /// <summary>Working text (Latin1 mirror of the file). The single source of truth for encode.</summary>
    public string Text = "";

    /// <summary>Root <c>&lt;level&gt;</c> element.</summary>
    public Tp1Node Root = null!;

    /// <summary>True once an edit has altered <see cref="Text"/>.</summary>
    public bool Dirty;

    /// <summary>Encode: the original bytes when untouched (guarantees I1 exactly), otherwise the
    /// current text re-emitted through the byte-preserving Latin1 map.</summary>
    public byte[] Encode() => Dirty ? Encoding.Latin1.GetBytes(Text) : Original;

    /// <summary>A node's index path from the root (each entry is the child's position in its
    /// parent). A value edit (move / set / paint) never changes the tree shape, so the path
    /// still resolves to the same element after a re-tokenise — the only safe way to hold a
    /// reference across an edit, since every <see cref="Tp1Node"/> is rebuilt on reindex.</summary>
    public static int[] PathOf(Tp1Node n)
    {
        var list = new List<int>();
        for (var cur = n; cur.Parent is { } p; cur = p) list.Add(p.Children.IndexOf(cur));
        list.Reverse();
        return list.ToArray();
    }

    /// <summary>Resolve a <see cref="PathOf"/> index path back to its node in the current tree.</summary>
    public Tp1Node? NodeAt(int[] path)
    {
        var cur = Root;
        foreach (var i in path)
        {
            if (cur is null || i < 0 || i >= cur.Children.Count) return null;
            cur = cur.Children[i];
        }
        return cur;
    }

    /// <summary>Re-tokenise <see cref="Text"/> into the element tree (called after load and after
    /// every structural splice so the recorded offsets always match the current buffer).</summary>
    public void Reindex() => Root = Tp1Codec.Parse(this);

    /// <summary>Replace <paramref name="len"/> bytes at <paramref name="start"/> with
    /// <paramref name="replacement"/>, mark the level dirty and re-derive the tree. The replacement
    /// is written straight into the buffer, so bytes before <paramref name="start"/> and after
    /// <paramref name="start"/>+<paramref name="len"/> are never disturbed (I3).</summary>
    public void Replace(int start, int len, string replacement)
    {
        Text = string.Concat(Text.AsSpan(0, start), replacement, Text.AsSpan(start + len));
        Dirty = true;
        Reindex();
    }

    // ------------------------------------------------------- typed views ----

    /// <summary>The <c>&lt;goo&gt;</c> player-start element, if present.</summary>
    public Tp1Node? Goo => Root.Child("goo");

    /// <summary>Every <c>&lt;emitter&gt;</c> under the root, in source order.</summary>
    public IEnumerable<Tp1Node> Emitters => Root.Kids("emitter");

    /// <summary>Every <c>&lt;tip&gt;</c> / <c>&lt;comic&gt;</c> / <c>&lt;end&gt;</c> path-block element.</summary>
    public IEnumerable<Tp1Node> PathBlocks =>
        Root.Children.Where(c => c.Tag is "tip" or "comic" or "end");

    /// <summary>The <c>&lt;tilelayers&gt;</c> container element that holds the background layers, if any.</summary>
    public Tp1Node? Tilelayers => Root.Child("tilelayers");

    /// <summary>Every <c>&lt;tilelayer&gt;</c> under the container, in source order (row-major tile grids).</summary>
    public IEnumerable<Tp1Node> TileLayers =>
        Tilelayers?.Kids("tilelayer") ?? Enumerable.Empty<Tp1Node>();

    /// <summary>How many background tile layers the level has. A level always keeps at least one, so
    /// the delete-layer edit refuses below this count.</summary>
    public int TileLayerCount => TileLayers.Count();
}

/// <summary>A stable reference to one element across the level's re-indexing. A Tasty Planet 1 edit
/// rebuilds the whole node tree, so a bare <see cref="Tp1Node"/> handle goes stale after the first
/// write — yet the viewport drags an object through many writes in one gesture without re-projecting.
/// This handle carries the element's index path instead, which is unchanged by a value edit and by
/// editing a node's own children, so <see cref="Node"/> always resolves to the live element. It is what
/// the projector stores as a tp1 marker's <c>Source</c> and every tp1 accessor re-resolves through.</summary>
public sealed class Tp1Handle
{
    public Tp1Level Lv = null!;
    public int[] Path = System.Array.Empty<int>();

    public Tp1Handle() { }
    public Tp1Handle(Tp1Level lv, Tp1Node node) { Lv = lv; Path = Tp1Level.PathOf(node); }

    /// <summary>The live element this path points at, or null if the tree no longer has it (a sibling
    /// removal can retire a path); callers treat that as a no-op rather than a crash.</summary>
    public Tp1Node? Node => Lv is null ? null : Lv.NodeAt(Path);

    /// <summary>The element's tag as it currently reads (empty once the path is gone).</summary>
    public string Tag => Node?.Tag ?? "";
}
