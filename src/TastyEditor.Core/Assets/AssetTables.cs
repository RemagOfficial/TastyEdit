using System.Globalization;
using System.Xml.Linq;

namespace TastyEditor.Core.Assets;

/// <summary>One sprite sub-image inside an atlas: its crop rectangle plus the
/// original (pre-packing) size and the offset to paste the crop back at. Coordinates
/// arrive as integers for Tasty Blue but as float strings ("2.0") for Forever / Final
/// Bite, so every numeric attribute is read through <c>float</c> then truncated — the
/// exact tolerance <c>tbassets.Cell</c> relies on.</summary>
public sealed record Cell(
    string Name, string Atlas, int X1, int Y1, int X2, int Y2,
    int CropX, int CropY, int OrigW, int OrigH)
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
        return new Cell((string?)e.Attribute("name") ?? "", atlas,
            Gi("x1"), Gi("y1"), Gi("x2"), Gi("y2"),
            Gi("cropoffsetx"), Gi("cropoffsety"), Gi("origwidth"), Gi("origheight"));
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
    public List<Frame> Frames { get; init; } = new();

    internal static Anim Parse(XElement e)
    {
        double Gf(XElement f, string k, double d) =>
            float.TryParse((string?)f.Attribute(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

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

        return new Anim
        {
            Name = (string?)e.Attribute("name") ?? "",
            Cx = Gf(e, "centerx", 0.5),
            Cy = Gf(e, "centery", 0.5),
            Looping = (string?)e.Attribute("looping") == "true",
            Frames = frames,
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
}
