using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace TastyEditor.Core.Levels;

/// <summary>Which multilevel schema a file uses. The two engine families share the
/// <c>&lt;multilevel&gt;</c> root and the goal attributes but encode a stage's zoom
/// differently, so the layout is picked from the format, not guessed:</summary>
public enum MultiLevelFormat
{
    /// <summary>Back for Seconds: a single <c>meterperpix</c> float written in 3-digit
    /// scientific notation (<c>1.000000e-003</c>); stages have no <c>gootostartresume</c>.</summary>
    Tp2,

    /// <summary>Tasty Blue: zoom split into a <c>mantissa</c> (6dp) + integer <c>exponent</c>
    /// pair; stages additionally carry <c>gootostartresume</c>.</summary>
    Tb,
}

/// <summary>One ordered stage entry in a multilevel: a reference to a <c>.bin</c>/<c>.xml</c>
/// scene (<see cref="Name"/>) plus that scene's placement in the shared world (zoom, position,
/// the area at which the game advances, and the misc goal/trigger flags). It keeps the raw
/// attribute strings so an unedited stage round-trips byte-for-byte; typed accessors
/// (<see cref="Zoom"/>, <see cref="PosX"/>, …) parse on demand for the dialog UI.</summary>
public sealed class MultiStage
{
    /// <summary>Attribute names in canonical (schema) order; only keys present are emitted.</summary>
    public List<string> Order { get; } = new();

    /// <summary>Raw attribute values, exactly as read (or as the UI wrote them).</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public string? Name
    {
        get => Get("name");
        set => Set("name", value);
    }

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        if (value is null) { Values.Remove(key); Order.Remove(key); return; }
        if (!Values.ContainsKey(key)) Order.Add(key);
        Values[key] = value;
    }

    // Typed helpers for the dialog; the owning MultiLevel supplies the format so a Tb zoom
    // reads its mantissa/exponent pair and a Tp2 zoom its single scientific-notation field.
    internal MultiLevel? Owner { get; set; }

    public double? Zoom
    {
        get => Owner?.ZoomOf(this);
        set { if (Owner is not null) Owner.SetZoomOf(this, value); }
    }

    public double? PosX => Doub(Get("posx"));
    public double? PosY => Doub(Get("posy"));
    public int? TriggerArea => Int(Get("triggerarea"));

    public void SetPos(double? x, double? y)
    {
        if (x is double dx) Set("posx", Owner?.FmtFloat("posx", dx) ?? dx.ToString("R", CultureInfo.InvariantCulture));
        if (y is double dy) Set("posy", Owner?.FmtFloat("posy", dy) ?? dy.ToString("R", CultureInfo.InvariantCulture));
    }

    public void SetTriggerArea(int? v) { if (v is int t) Set("triggerarea", t.ToString(CultureInfo.InvariantCulture)); }

    private static double? Doub(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static int? Int(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>A game "multilevel" file (<c>levels/multilevels/*.xml</c>): the ordered list of
/// stage-levels the engine chains through as the goo grows, plus the level's goal / win-condition
/// attributes on the root (<c>timelimit</c>, <c>goldtime</c>, <c>silvertime</c>, <c>bronzetime</c>,
/// <c>victorytype</c> …). This is the .NET port of the proven <c>tp2multi.py</c> / <c>tbmulti.py</c>
/// oracle — faithful parse, canonical attribute ordering and per-family float formatting.
///
/// <para>Unlike the <c>.bin</c> codec this is XML the engine reads through its own parser, so a
/// re-save need not be byte-identical to the shipped file; the guarantee here is that a file is
/// parsed without loss and that <b>save∘load is idempotent</b> (loading our own output re-encodes
/// to the same bytes), which the smoke check proves. Emitting the game's own multi-line layout
/// keeps diffs against stock files minimal.</para></summary>
public sealed class MultiLevel
{
    // Goal / win-condition attributes on the root, in schema order (identical for both families).
    public static readonly string[] MlAttrs =
    {
        "timelimit", "victorytype", "numspecialentities", "goldtime", "silvertime",
        "bronzetime", "smallfailstring", "tipscriptfunction", "comicstartfunction",
        "comicendfunction", "levelmusicscript",
    };

    private static readonly string[] Tp2LevelAttrs =
        ["name", "meterperpix", "posx", "posy", "triggerarea", "triggerspecial", "gootostart"];
    private static readonly string[] TbLevelAttrs =
        ["name", "mantissa", "exponent", "posx", "posy", "triggerarea",
         "triggerspecial", "gootostart", "gootostartresume"];

    // Attributes a per-family float formatter owns (scientific for tp2, 6dp for tb geometry).
    private static readonly HashSet<string> Tp2Floats = ["meterperpix", "posx", "posy"];
    private static readonly HashSet<string> TbFloats = ["mantissa", "posx", "posy"];

    public MultiLevelFormat Format { get; }
    public string? Path { get; private set; }

    /// <summary>Root goal attributes, keyed by name. Only keys present are emitted.</summary>
    public Dictionary<string, string> Attrs { get; } = new(StringComparer.Ordinal);

    /// <summary>The ordered stages; the game advances through them as the goo grows.</summary>
    public List<MultiStage> Stages { get; } = new();

    public MultiLevel(MultiLevelFormat format) { Format = format; }

    private string[] LevelAttrOrder => Format == MultiLevelFormat.Tb ? TbLevelAttrs : Tp2LevelAttrs;
    private HashSet<string> FloatAttrs => Format == MultiLevelFormat.Tb ? TbFloats : Tp2Floats;

    // ------------------------------------------------------------- io ------

    public static MultiLevel Load(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidDataException("multilevel: no root element");
        if (!string.Equals(root.Name.LocalName, "multilevel", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"multilevel: unexpected root <{root.Name.LocalName}>");

        // Detect the family from the stage fields themselves: a mantissa/exponent pair is Tasty
        // Blue, a meterperpix field is Back for Seconds (the caller need not know the game).
        var levels = root.Elements("level").ToList();
        bool tb = levels.Any(l => l.Attribute("mantissa") is not null)
                  || levels.Any(l => l.Attribute("exponent") is not null);
        var ml = new MultiLevel(tb ? MultiLevelFormat.Tb : MultiLevelFormat.Tp2) { Path = path };

        foreach (var k in MlAttrs)
        {
            var a = root.Attribute(k);
            if (a is not null) ml.Attrs[k] = a.Value;
        }
        // Preserve any extra root attributes the schema didn't list (defensive; rare in stock).
        foreach (var a in root.Attributes())
            if (a.Name.LocalName != "name" && !ml.Attrs.ContainsKey(a.Name.LocalName))
                ml.Attrs[a.Name.LocalName] = a.Value;

        foreach (var lv in levels) ml.Stages.Add(ml.ReadStage(lv));
        ml.NormaliseFloats();
        return ml;
    }

    private MultiStage ReadStage(XElement lv)
    {
        var st = new MultiStage { Owner = this };
        foreach (var k in LevelAttrOrder)
        {
            var a = lv.Attribute(k);
            if (a is not null) { st.Order.Add(k); st.Values[k] = a.Value; }
        }
        // Keep unknown stage attributes at the tail so nothing is silently dropped.
        foreach (var a in lv.Attributes())
            if (!st.Values.ContainsKey(a.Name.LocalName))
            { st.Order.Add(a.Name.LocalName); st.Values[a.Name.LocalName] = a.Value; }
        return st;
    }

    /// <summary>Serialise to the engine's multi-line layout (matching the stock <c>.xml</c> shape
    /// and the oracle's <c>to_xml</c>): one attribute per line with the schema's indentation,
    /// CRLF terminators, and a trailing newline. A level with no attributes emits an open/close
    /// pair rather than the oracle's self-close crash (which never fires on real files).</summary>
    public string ToXml()
    {
        var sb = new StringBuilder();
        void Nl() => sb.Append("\r\n");

        sb.Append("<multilevel");
        var rootKeys = PresentRootKeys();
        foreach (var k in rootKeys) { Nl(); sb.Append("    ").Append(Attr(k, Attrs[k])); }
        sb.Append('>');
        Nl();

        foreach (var st in Stages)
        {
            sb.Append("    <level");
            foreach (var k in st.Order)
            {
                if (!st.Values.ContainsKey(k)) continue;
                Nl(); sb.Append("        ").Append(Attr(k, st.Values[k]));
            }
            sb.Append('>'); Nl();
            sb.Append("    </level>"); Nl();
        }

        sb.Append("</multilevel>"); Nl();
        return sb.ToString();
    }

    private List<string> PresentRootKeys()
    {
        var ordered = MlAttrs.Where(Attrs.ContainsKey).ToList();
        foreach (var k in Attrs.Keys)
            if (!ordered.Contains(k)) ordered.Add(k);
        return ordered;
    }

    private string Attr(string key, string raw) => $"{key}=\"{Escape(raw)}\"";

    private static string Escape(string v) => v
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public void Save(string path)
    {
        File.WriteAllText(path, ToXml(), new UTF8Encoding(false));
        Path = path;
    }

    // ------------------------------------------------------ stage edits ----

    /// <summary>Append a new stage referencing <paramref name="name"/> with format-appropriate
    /// defaults (zoom 1e-3, at the origin, a 20000 trigger) in canonical attribute order.</summary>
    public MultiStage AddStage(string name)
    {
        var st = new MultiStage { Owner = this };
        foreach (var k in LevelAttrOrder) st.Order.Add(k);
        st.Set("name", name);
        if (Format == MultiLevelFormat.Tb)
        {
            st.Set("mantissa", "1.000000");
            st.Set("exponent", "-3");
        }
        else st.Set("meterperpix", Sci(1e-3));
        st.Set("posx", FmtFloat("posx", 0));
        st.Set("posy", FmtFloat("posy", 0));
        st.Set("triggerarea", "20000");
        st.Set("triggerspecial", "-1");
        st.Set("gootostart", "false");
        if (Format == MultiLevelFormat.Tb) st.Set("gootostartresume", "false");
        Stages.Add(st);
        return st;
    }

    public void RemoveStage(MultiStage st) => Stages.Remove(st);

    /// <summary>Move a stage one slot earlier / later in the chain (the game's progression order).</summary>
    public void MoveStage(MultiStage st, int delta)
    {
        int i = Stages.IndexOf(st);
        if (i < 0) return;
        int j = Math.Clamp(i + delta, 0, Stages.Count - 1);
        if (i == j) return;
        Stages.RemoveAt(i);
        Stages.Insert(j, st);
    }

    public string? GetGoal(string key) => Attrs.TryGetValue(key, out var v) ? v : null;

    public void SetGoal(string key, string? value)
    {
        if (value is null) Attrs.Remove(key);
        else Attrs[key] = value;
    }

    // --------------------------------------------------- zoom + floats -----

    /// <summary>Effective meters-per-pixel for a stage (tb: mantissa×10^exponent; tp2: the field).</summary>
    internal double? ZoomOf(MultiStage st)
    {
        if (Format == MultiLevelFormat.Tp2) return st.DoubPub("meterperpix");
        double m = double.TryParse(st.Get("mantissa"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mv) ? mv : 1.0;
        int e = int.TryParse(st.Get("exponent"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ev) ? ev : -3;
        return m * Math.Pow(10, e);
    }

    internal void SetZoomOf(MultiStage st, double? value)
    {
        if (value is not double v) return;
        if (Format == MultiLevelFormat.Tp2) { st.Set("meterperpix", Sci(v)); return; }
        if (v == 0) { st.Set("mantissa", "1.000000"); st.Set("exponent", "-3"); return; }
        int e = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        double m = v / Math.Pow(10, e);
        st.Set("mantissa", m.ToString("F6", CultureInfo.InvariantCulture));
        st.Set("exponent", e.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Format a float attribute the way this family's writer emits it, so a UI edit
    /// produces the same textual shape the stock files carry (idempotent on re-parse).</summary>
    internal string FmtFloat(string key, double v)
    {
        if (!FloatAttrs.Contains(key)) return v.ToString("R", CultureInfo.InvariantCulture);
        if (Format == MultiLevelFormat.Tp2) return Sci(v);
        return v.ToString("F6", CultureInfo.InvariantCulture);
    }

    // Game format: 1.200000e-003 — a 6dp mantissa and a signed, zero-padded 3-digit exponent.
    internal static string Sci(double v)
    {
        string s = v.ToString("E6", CultureInfo.InvariantCulture);   // e.g. 1.200000E-003
        int idx = s.IndexOfAny(['E', 'e']);
        if (idx < 0) return s;
        string mant = s[..idx];
        int exp = int.Parse(s[(idx + 1)..], CultureInfo.InvariantCulture);
        return $"{mant}e{(exp >= 0 ? "+" : "-")}{Math.Abs(exp):000}";
    }

    // Re-encode every float attribute through the writer format so loading a stock file whose
    // textual floats differ (e.g. a short "0" vs "0.000000") normalises to our idempotent form.
    // Called once after Load: without it a re-save could differ from a save∘load∘save on the
    // *text*, which is what the idempotency assertion checks.
    internal void NormaliseFloats()
    {
        foreach (var st in Stages)
            foreach (var k in st.Order.ToList())
            {
                if (!FloatAttrs.Contains(k) || !st.Values.TryGetValue(k, out var raw)) continue;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    st.Values[k] = FmtFloat(k, v);
            }
    }
}

internal static class MultiStageExt
{
    public static double? DoubPub(this MultiStage st, string key) =>
        st.Get(key) is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;
}
