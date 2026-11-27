using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TastyEditor.Core.Levels;

/// <summary>Verbatim / span-preserving codec for Tasty Planet 1 XML levels. Decoding
/// tokenises the source text into a <see cref="Tp1Node"/> tree that records every
/// element's and attribute's exact byte span; encoding returns those same bytes, so an
/// untouched level reproduces the file <b>byte-for-byte</b> (I1). Edits never re-serialise
/// the document — they patch only the touched value's substring (or splice a cloned
/// element) inside the text buffer, so every other byte keeps its position (I3) and the
/// source's quirks (mixed indent, the <c>conditon</c> misspelling, per-number styles) are
/// carried verbatim.</summary>
public sealed class Tp1Codec
{
    public static readonly Tp1Codec Instance = new();

    // ------------------------------------------------------------- decode --

    /// <summary>Parse the bytes and reject anything that is not a Tasty Planet 1 level:
    /// the root must be a <c>&lt;level&gt;</c> element carrying tp1 hallmarks. This keeps
    /// <c>medaltimes.xml</c> (root <c>&lt;medaltimes&gt;</c>), campaign
    /// <c>&lt;multilevel&gt;</c> shells and the four binary families out of tp1 detection.</summary>
    public Tp1Level Decode(byte[] data)
    {
        var lv = new Tp1Level
        {
            Original = data,
            Text = System.Text.Encoding.Latin1.GetString(data),
        };
        lv.Reindex();
        if (lv.Root is null || !string.Equals(lv.Root.Tag, "level", StringComparison.Ordinal))
            throw new InvalidOperationException("not a Tasty Planet 1 level (root is not <level>)");
        if (!HasTp1Hallmark(lv.Root))
            throw new InvalidOperationException("not a Tasty Planet 1 level (no tp1 hallmarks)");
        return lv;
    }

    // A real tp1 level root carries a distinguishing attribute and one of its object
    // children. medaltimes <level> rows have a name + medal times but no goo/emitter/
    // tilelayers child and no victorysize/unitmultiplier/levelnum, so they are rejected.
    private static bool HasTp1Hallmark(Tp1Node root)
    {
        bool attr = root.Has("victorysize") || root.Has("unitmultiplier") || root.Has("levelnum");
        bool kid = root.Child("goo") is not null || root.Child("emitter") is not null
                                            || root.Child("tilelayers") is not null;
        return attr || kid;
    }

    // ------------------------------------------------------------- encode --

    public byte[] Encode(Tp1Level lv) => lv.Encode();

    /// <summary>Tokenise arbitrary Tasty Planet XML (the <c>assets/graphics/*.xml</c> tables) into
    /// the same verbatim tree, WITHOUT the <c>&lt;level&gt;</c> hallmark check. The def files are
    /// not valid XML (unquoted values like <c>cell=0</c>, mixed quoting), so they cannot go through
    /// <c>XDocument</c>; this reuses the level tokenizer to read them leniently. Read-only.</summary>
    public static Tp1Level ParseTree(byte[] data)
    {
        var lv = new Tp1Level { Original = data, Text = System.Text.Encoding.Latin1.GetString(data) };
        lv.Reindex();
        return lv;
    }

    /// <summary>Structural fingerprint for the golden harness: a tag histogram plus total
    /// element / attribute counts, derived from the verbatim tree (order-insensitive, so
    /// it is stable across an untouched re-encode).</summary>
    public JsonObject SpanModel(Tp1Level lv)
    {
        var tags = new JsonObject();
        int elems = 0, attrs = 0;
        void Walk(Tp1Node n)
        {
            elems++;
            attrs += n.Attrs.Count;
            tags[n.Tag] = (tags[n.Tag]?.GetValue<int>() ?? 0) + 1;
            foreach (var c in n.Children) Walk(c);
        }
        if (lv.Root is not null) Walk(lv.Root);
        return new JsonObject
        {
            ["kind"] = "xml",
            ["elements"] = elems,
            ["attributes"] = attrs,
            ["tags"] = tags,
        };
    }

    // =========================================================== tokenizer ==

    /// <summary>Tokenise <paramref name="lv"/>.<see cref="Tp1Level.Text"/> into the element
    /// tree and return its root. Public so <see cref="Tp1Level.Reindex"/> can re-derive
    /// offsets after every buffer edit.</summary>
    public static Tp1Node Parse(Tp1Level lv)
    {
        var t = new Tokenizer(lv);
        return t.ParseDocument();
    }

    private sealed class Tokenizer
    {
        private readonly Tp1Level _lv;
        private readonly string _s;
        private int _i;

        public Tokenizer(Tp1Level lv) { _lv = lv; _s = lv.Text; }

        private bool Eof => _i >= _s.Length;
        private char C => _s[_i];

        public Tp1Node ParseDocument()
        {
            SkipMisc();
            if (Eof || C != '<' || PeekClose())
                throw new InvalidOperationException("no root element");
            return ParseElement();
        }

        private bool PeekClose() => _i + 1 < _s.Length && _s[_i + 1] == '/';

        /// <summary>Skip whitespace, comments, processing instructions (the XML declaration)
        /// and DOCTYPE between markup — none of it becomes a node, but every byte stays in
        /// the buffer untouched.</summary>
        private void SkipMisc()
        {
            while (!Eof)
            {
                if (char.IsWhiteSpace(C)) { _i++; continue; }
                if (C != '<') { _i++; continue; }        // stray character data between tags
                if (_i + 3 < _s.Length && _s.AsSpan(_i, 4).SequenceEqual("<!--"))
                {
                    int end = _s.IndexOf("-->", _i + 4, StringComparison.Ordinal);
                    _i = end < 0 ? _s.Length : end + 3;
                    continue;
                }
                if (_i + 1 < _s.Length && _s[_i + 1] == '?')
                {
                    int end = _s.IndexOf("?>", _i + 2, StringComparison.Ordinal);
                    _i = end < 0 ? _s.Length : end + 2;
                    continue;
                }
                if (_i + 1 < _s.Length && _s[_i + 1] == '!')
                {
                    int end = _s.IndexOf('>', _i + 2);
                    _i = end < 0 ? _s.Length : end + 1;
                    continue;
                }
                break;
            }
        }

        private static bool IsNameChar(char c) =>
            char.IsLetterOrDigit(c) || c is '.' or '_' or ':' or '-';

        private Tp1Node ParseElement()
        {
            var node = new Tp1Node { Owner = _lv, Start = _i };
            Expect('<');
            node.NameStart = _i;
            while (!Eof && IsNameChar(C)) _i++;
            node.NameLen = _i - node.NameStart;
            node.Tag = _s.Substring(node.NameStart, node.NameLen);
            if (node.NameLen == 0) throw new InvalidOperationException("empty tag name");

            // Attributes until '>' or '/>'.
            while (true)
            {
                SkipTagSpace();
                if (Eof) throw new InvalidOperationException("unterminated open tag");
                if (C == '/')
                {
                    Expect('/'); Expect('>');
                    node.SelfClosing = true;
                    node.ContentStart = _i;
                    node.End = _i;
                    return node;
                }
                if (C == '>') { Expect('>'); break; }
                ParseAttribute(node);
            }

            node.ContentStart = _i;

            // Children + the end tag.
            while (true)
            {
                SkipMisc();
                if (Eof) throw new InvalidOperationException("missing end tag for <" + node.Tag + ">");
                if (C == '<' && PeekClose())
                {
                    Expect('<'); Expect('/');
                    while (!Eof && IsNameChar(C)) _i++;   // end-tag name (not validated further)
                    SkipTagSpace();
                    Expect('>');
                    node.End = _i;
                    return node;
                }
                if (C == '<') { var ch = ParseElement(); ch.Parent = node; node.Children.Add(ch); continue; }
                throw new InvalidOperationException("unexpected content in <" + node.Tag + ">");
            }
        }

        private void ParseAttribute(Tp1Node node)
        {
            var a = new Tp1Attr { Owner = node, KeyStart = _i };
            while (!Eof && C != '=' && !char.IsWhiteSpace(C) && C != '>' && C != '/') _i++;
            a.KeyLen = _i - a.KeyStart;
            a.Key = _s.Substring(a.KeyStart, a.KeyLen);
            SkipTagSpace();
            Expect('=');
            SkipTagSpace();

            if (C == '"' || C == '\'')
            {
                a.Quote = C;
                _i++;
                a.ValStart = _i;
                int close = _s.IndexOf(a.Quote, _i);
                if (close < 0) throw new InvalidOperationException("unterminated attribute value");
                a.ValLen = close - a.ValStart;
                _i = close + 1;
            }
            else
            {
                a.Quote = (char)0;
                a.ValStart = _i;
                while (!Eof && C != '>' && C != '/' && !char.IsWhiteSpace(C)) _i++;
                a.ValLen = _i - a.ValStart;
            }

            if (a.KeyLen > 0) node.Attrs.Add(a);
        }

        private void SkipTagSpace()
        {
            while (!Eof && char.IsWhiteSpace(C)) _i++;
        }

        private void Expect(char c)
        {
            if (Eof || C != c) throw new InvalidOperationException($"expected '{c}' at {_i}");
            _i++;
        }
    }

    // ============================================================= edits ===
    //
    // Every edit is a range replacement on the level's text buffer. Multi-range edits
    // (a whole-path move patches each pair's anchors) are applied right-to-left so the
    // leftward offsets stay valid, then the tree is re-tokenised once. Bytes outside the
    // touched ranges never move (I3).

    /// <summary>Set a text attribute, replacing only its value substring and keeping the
    /// source's quoting style. When the attribute is absent it is appended to the open tag
    /// (quoted) so the write still stays inside the element's own span.</summary>
    public static void SetAttr(Tp1Node node, string key, string value)
    {
        var lv = node.Owner;
        var a = node.Attr(key);
        if (a is not null) { lv.Replace(a.ValStart, a.ValLen, Esc(a.Quote, value)); return; }

        // Absent: insert ` key="value"` just inside the open tag, before its '>' (or '/>').
        int at = node.ContentStart - 1;
        if (node.SelfClosing) at--;
        lv.Replace(at, 0, $" {key}=\"{Esc('"', value)}\"");
    }

    /// <summary>Set a numeric attribute, formatting the value to match the original token's
    /// style (same fraction digits / scientific form) so untouched numbers keep their text
    /// and an edited number reads naturally in place.</summary>
    public static void SetNum(Tp1Node node, string key, double value)
    {
        var a = node.Attr(key);
        SetAttr(node, key, FormatNumber(a?.Value, value));
    }

    /// <summary>Set an integer attribute (no fraction part).</summary>
    public static void SetInt(Tp1Node node, string key, int value)
        => SetAttr(node, key, value.ToString(CultureInfo.InvariantCulture));

    // A batched, right-to-left multi-range replacement (single reindex). Used by the
    // edit facade for whole-object moves that touch several attribute spans at once.
    internal static void ReplaceAll(Tp1Level lv, List<(int Start, int Len, string Repl)> edits)
    {
        edits.Sort((x, y) => y.Start.CompareTo(x.Start));
        var sb = new StringBuilder(lv.Text);
        foreach (var (start, len, repl) in edits)
            sb.Remove(start, len).Insert(start, repl);
        lv.Text = sb.ToString();
        lv.Dirty = true;
        lv.Reindex();
    }

    /// <summary>Format <paramref name="v"/> preserving the textual style of the original
    /// token <paramref name="orig"/> (present in the file). Falls back to round-trip "R".</summary>
    public static string FormatNumber(string? orig, double v)
    {
        if (string.IsNullOrEmpty(orig)) return v.ToString("R", CultureInfo.InvariantCulture);
        bool sci = orig.IndexOfAny(new[] { 'e', 'E' }) >= 0;
        if (sci) return v.ToString("R", CultureInfo.InvariantCulture);

        int dot = orig.IndexOf('.');
        if (dot < 0) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        int frac = orig.Length - dot - 1;
        if (frac > 15) frac = 15;
        return v.ToString("F" + frac, CultureInfo.InvariantCulture);
    }

    // Escape only the character that would terminate the value in its quoting style; the
    // shipped corpus is plain names/numbers so this is a safety net, not a reformat.
    internal static string Esc(char quote, string value)
    {
        if (quote == (char)0) return value;   // unquoted token written as-is
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace(quote.ToString(), quote == '"' ? "&quot;" : "&apos;");
    }
}
