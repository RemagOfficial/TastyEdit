using System.Globalization;
using System.Xml.Linq;

namespace TastyEditor.Core.Assets;

/// <summary>One sprite sub-image inside an atlas: its crop rectangle plus the
/// original (pre-packing) size and the offset to paste the crop back at. Coordinates
/// arrive as integers for Tasty Blue but as float strings ("2.0") for Forever / Final
/// Bite, so every numeric attribute is read through <c>float</c> then truncated — the
/// exact tolerance <c>tbassets.Cell</c> relies on.
///
/// <para><see cref="ScaleFromRaw"/> is Final Bite's <c>scalefromraw</c>: the cell frame is the raw
/// authoring artwork shrunk by that factor (<c>192 = 3000 * 0.064</c>), so one RAW art pixel is
/// <c>ScaleFromRaw</c> atlas pixels. The engine's blit multiplies the quad by <c>1/scalefromraw</c>, which
/// is what puts a rig's joint/link numbers in RAW units — the scale a rig compositor must divide the ink
/// rectangle by. Tasty Blue / Forever cells carry no such attribute and read 1.</para></summary>
public sealed record Cell(
    string Name, string Atlas, int X1, int Y1, int X2, int Y2,
    int CropX, int CropY, int OrigW, int OrigH, double ScaleFromRaw = 1.0)
{
    public int W => X2 - X1;
    public int H => Y2 - Y1;

    internal static Cell Parse(string atlas, XElement e)
    {
        int Gi(string k)
        {
            var v = (string?)e.Attribute(k);
            return v is null || v.Length == 0 ? 0 : (int)float.Parse(v, CultureInfo.InvariantCulture);
        }
        // Kept as a float, not truncated: it is a ratio, and 0.064 vs 0.06 is a 6% geometry error.
        double Sf(string k)
        {
            var v = (string?)e.Attribute(k);
            return v is null || v.Length == 0 ? 1.0 : float.Parse(v, CultureInfo.InvariantCulture);
        }
        return new Cell((string?)e.Attribute("name") ?? "", atlas,
            Gi("x1"), Gi("y1"), Gi("x2"), Gi("y2"),
            Gi("cropoffsetx"), Gi("cropoffsety"), Gi("origwidth"), Gi("origheight"), Sf("scalefromraw"));
    }
}

/// <summary>A single animation frame: which cell it shows and how it is transformed
/// (offset / angle / scale / opacity / flips / duration). Mirrors <c>tbassets.Frame</c>.</summary>
public sealed record Frame(
    string Cell, double OffX, double OffY, double Angle, double Scale,
    double Opacity, bool FlipX, bool FlipY, double Time);

/// <summary>An animation definition: an ordered frame list with a pivot and looping.
/// Mirrors <c>tbassets.Anim</c>.</summary>
public sealed class Anim
{
    public required string Name { get; init; }
    public double Cx { get; init; } = 0.5;
    public double Cy { get; init; } = 0.5;
    public bool Looping { get; init; }
    public int Priority { get; init; }        // animationdef draw order (higher = on top); TP2 skeletal assembly
    public List<Frame> Frames { get; init; } = new();

    /// <summary>The animdef's own <c>extentx1/x2, extenty1/y2</c>: the rectangle this part sweeps on
    /// screen, in entity-origin pixels (x right, y down). Every shipped TP2 animationdef carries all
    /// four, and the union over a character's stand+walk defs reproduces its entitydef
    /// natwidth/natheight to within 1-3% (measured: ala 390x66 vs nat 390x68, tyrannosaurus 394x103 vs
    /// 394x106, roman_man 210x156 vs 210x156, frog 394x181 vs 394x182, cow 246x126 vs 246x130) - it is
    /// the engine's declared sprite box, so an assembled character should be measured by it and not by
    /// the art rects I can re-derive. False when the def omits any of the four.</summary>
    public bool HasExtents { get; init; }
    public double ExtX1, ExtX2, ExtY1, ExtY2;

    internal static Anim Parse(XElement e)
    {
        double Gf(XElement f, string k, double d) =>
            double.TryParse((string?)f.Attribute(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        var frames = new List<Frame>();
        foreach (var f in e.Elements("frame"))
            frames.Add(new Frame(
                (string?)f.Attribute("cellname") ?? "",
                Gf(f, "offsetx", 0), Gf(f, "offsety", 0),
                Gf(f, "angle", 0), Gf(f, "scale", 1.0),
                Gf(f, "opacity", 1.0),
                (string?)f.Attribute("flipx") == "true",
                (string?)f.Attribute("flipy") == "true",
                Gf(f, "time", 1.0)));

        bool Has(string k) => (string?)e.Attribute(k) is { Length: > 0 };
        return new Anim
        {
            Name = (string?)e.Attribute("name") ?? "",
            Cx = Gf(e, "centerx", 0.5),
            Cy = Gf(e, "centery", 0.5),
            Looping = (string?)e.Attribute("looping") == "true",
            Priority = (int)Gf(e, "priority", 0),
            Frames = frames,
            HasExtents = Has("extentx1") && Has("extentx2") && Has("extenty1") && Has("extenty2"),
            ExtX1 = Gf(e, "extentx1", 0), ExtX2 = Gf(e, "extentx2", 0),
            ExtY1 = Gf(e, "extenty1", 0), ExtY2 = Gf(e, "extenty2", 0),
        };
    }
}

/// <summary>Readers for the three ElementEngine XML tables into plain dictionaries.
/// Split out from <see cref="SpriteResolver"/> so the parsing is independently testable.</summary>
internal static class AssetTables
{
    public static Dictionary<string, Cell> ReadImageMaps(string path)
    {
        var cells = new Dictionary<string, Cell>();
        if (!File.Exists(path)) return cells;
        var root = XDocument.Load(path).Root;
        if (root is null) return cells;
        foreach (var im in root.Elements("imagemap"))
        {
            string atlas = (string?)im.Attribute("name") ?? "";
            foreach (var c in im.Elements("cell"))
            {
                var cell = Cell.Parse(atlas, c);
                if (cell.Name.Length > 0) cells[cell.Name] = cell;
            }
        }
        return cells;
    }

    public static Dictionary<string, Anim> ReadAnimations(string path)
    {
        var anims = new Dictionary<string, Anim>();
        if (!File.Exists(path)) return anims;
        var root = XDocument.Load(path).Root;
        if (root is null) return anims;
        foreach (var a in root.Elements("animationdef"))
        {
            var anim = Anim.Parse(a);
            if (anim.Name.Length > 0) anims[anim.Name] = anim;
        }
        return anims;
    }

    /// <summary>Read <c>entitydef</c> natural diameters, keyed by the full def name,
    /// its catalog base (name minus <c>_base</c>) and its species key — the same three
    /// spellings <c>tb_entities._parse_entityintersections</c> registers.</summary>
    public static Dictionary<string, double> ReadNatDiam(string path)
    {
        var nd = new Dictionary<string, double>();
        if (!File.Exists(path)) return nd;
        var root = XDocument.Load(path).Root;
        if (root is null) return nd;
        foreach (var e in root.Descendants("entitydef"))
        {
            string full = (string?)e.Attribute("name") ?? "";
            if (full.Length == 0) continue;
            if (!double.TryParse((string?)e.Attribute("natdiam"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                continue;
            nd[full] = v;
            nd.TryAdd(EntityName.CatalogBase(full), v);
            nd.TryAdd(EntityName.SpeciesKey(full), v);
        }
        return nd;
    }

    /// <summary>Read <c>entitydef</c> natural art boxes (<c>natwidth</c> x <c>natheight</c>) and the
    /// opaque fraction of that box (<c>coverage</c>), keyed by the same three spellings as
    /// <see cref="ReadNatDiam"/>. This is the engine's own measure of an assembled sprite: for every
    /// def checked, <c>coverage</c> equals the opaque fraction of exactly this rectangle (leaf_maple
    /// 0.4956 against cell mapleleaf's 200x209 art, pebble1 0.7401, pyramid 1.0), so a skeletal
    /// assembly whose canvas does not land on it is placing parts where the game does not, and the
    /// render scale must divide by it to keep the visible material equal to the record's area.</summary>
    public static Dictionary<string, (double W, double H, double Cov)> ReadNatBox(string path)
    {
        var nb = new Dictionary<string, (double, double, double)>();
        if (!File.Exists(path)) return nb;
        var root = XDocument.Load(path).Root;
        if (root is null) return nb;
        foreach (var e in root.Descendants("entitydef"))
        {
            string full = (string?)e.Attribute("name") ?? "";
            if (full.Length == 0) continue;
            if (!double.TryParse((string?)e.Attribute("natwidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                || !double.TryParse((string?)e.Attribute("natheight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                continue;
            double cov = double.TryParse((string?)e.Attribute("coverage"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cv)
                && cv > 0 ? cv : 1.0;
            nb[full] = (w, h, cov);
            // A shared shorthand (bacteria1/2/3 -> "bacteria") is not a lookup the editor can honour:
            // picking whichever def registered first would size a 3-segment sprite off a 1-segment box.
            // Mark the collision and let callers fall back, rather than silently guessing a tier.
            Try(nb, EntityName.CatalogBase(full), (w, h, cov));
            Try(nb, EntityName.SpeciesKey(full), (w, h, cov));
        }
        return nb;
    }

    /// <summary>Register <paramref name="v"/> under <paramref name="key"/> unless another spelling is
    /// already there; a conflict becomes the (0,0,0) "ambiguous" marker so a lookup cannot quietly pick
    /// one tier of a numbered family.</summary>
    private static void Try(Dictionary<string, (double W, double H, double Cov)> nb, string key, (double, double, double) v)
    {
        if (key.Length == 0) return;
        if (nb.TryAdd(key, v)) return;
        if (nb[key] != (v.Item1, v.Item2, v.Item3)) nb[key] = (0, 0, 0);
    }
}
