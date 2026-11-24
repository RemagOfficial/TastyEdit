namespace TastyEditor.Core.Assets;

/// <summary>Everything the image layer needs to render one sprite: which atlas file to
/// open, the crop rectangle inside it, the original canvas size and paste offset to
/// reconstruct the pre-packed frame, plus the pivot and frame opacity. Core resolves this
/// from the XML tables; the WPF layer decodes the atlas and crops/composites it.</summary>
public readonly record struct ResolvedSprite(
    string Atlas, int X1, int Y1, int X2, int Y2,
    int CropX, int CropY, int OrigW, int OrigH,
    double PivotX, double PivotY, double Opacity);

/// <summary>UI-free asset façade for the ElementEngine games (Tasty Blue, Forever, Final
/// Bite): loads imagemaps / animationdefs / entityintersections for one
/// <see cref="AssetsRoot"/> and resolves a level entity's spawn name to a
/// <see cref="ResolvedSprite"/> using the exact <c>tbassets</c> cascade
/// (species key → alias → animation frame → direct cell → <c>name1</c> → fuzzy). It never
/// touches pixels or WPF, so it is fully testable headless.</summary>
public sealed class SpriteResolver
{
    private readonly AssetsRoot _root;
    private readonly Dictionary<string, Cell> _cells;
    private readonly Dictionary<string, Anim> _anims;
    private readonly Dictionary<string, double> _natdiam;

    // norm(cell name) -> first cell name with that spelling (setdefault order preserved).
    private readonly Dictionary<string, string> _cellNorm = new();

    private SpriteResolver(AssetsRoot root,
        Dictionary<string, Cell> cells, Dictionary<string, Anim> anims, Dictionary<string, double> natdiam)
    {
        _root = root; _cells = cells; _anims = anims; _natdiam = natdiam;
        foreach (var cn in cells.Keys)
            _cellNorm.TryAdd(EntityName.Norm(cn), cn);
    }

    /// <summary>Load every table under <paramref name="root"/>. Returns null when the
    /// asset tree isn't a usable ElementEngine install (missing graphics / imagemaps), so
    /// callers can fall back to plain markers. Never throws on individual absent files.</summary>
    public static SpriteResolver? TryLoad(AssetsRoot root)
    {
        if (root is null || !root.IsValid) return null;
        var cells = AssetTables.ReadImageMaps(Path.Combine(root.GraphicsDir, "imagemaps.xml"));
        var anims = AssetTables.ReadAnimations(Path.Combine(root.EditableDir, "animationdefs.xml"));
        var natdiam = AssetTables.ReadNatDiam(Path.Combine(root.EditableDir, "entityintersections.xml"));
        return new SpriteResolver(root, cells, anims, natdiam);
    }

    public AssetsRoot Root => _root;
    public IReadOnlyCollection<string> CellNames => _cells.Keys;
    public IReadOnlyCollection<string> AnimNames => _anims.Keys;
    public int CellCount => _cells.Count;
    public int AnimCount => _anims.Count;

    public bool TryGetCell(string name) => _cells.ContainsKey(name);

    public double NatDiam(string name, double fallback = 300.0) =>
        _natdiam.TryGetValue(name, out var v) ? v : fallback;

    /// <summary>World units per atlas pixel for a given physical area, from the entity's
    /// natural diameter — the <c>tbassets.world_per_pixel</c> circle model.</summary>
    public double WorldPerPixel(string name, double area)
    {
        double nd = NatDiam(name);
        double r = Math.Sqrt(Math.Max(area, 1.0) / Math.PI);
        return (2 * r) / nd;
    }

    public int FrameCount(string name)
    {
        var a = ResolveAnim(name);
        return a?.Frames.Count > 0 ? a.Frames.Count : 1;
    }

    public (double X, double Y) Pivot(string name)
    {
        var a = ResolveAnim(name);
        return a is null ? (0.5, 0.5) : (a.Cx, a.Cy);
    }

    /// <summary>Resolve a raw or catalog entity name to a sprite for frame
    /// <paramref name="frameI"/>. Applies <see cref="EntityName.SpeciesKey"/> then the
    /// tbassets cell cascade. Returns false (change nothing) when no cell matches — the
    /// caller draws its placeholder.</summary>
    public bool TryResolve(string rawName, int frameI, out ResolvedSprite sprite)
    {
        sprite = default;
        if (string.IsNullOrEmpty(rawName)) return false;
        string key = EntityName.SpeciesKey(rawName);
        Cell? cell = BaseCell(key, frameI);
        if (cell is null) return false;

        var (px, py) = Pivot(key);
        double opacity = FrameOpacity(key, frameI);
        sprite = new ResolvedSprite(cell.Atlas, cell.X1, cell.Y1, cell.X2, cell.Y2,
            cell.CropX, cell.CropY, cell.OrigW, cell.OrigH, px, py, opacity);
        return true;
    }

    /// <summary>Resolve an already-known cell by its exact / fuzzy spelling (used by the
    /// later asset browser, which lists cell names rather than species keys).</summary>
    public bool TryResolveCellName(string cellName, out ResolvedSprite sprite)
    {
        sprite = default;
        Cell? cell = _cells.TryGetValue(cellName, out var c) ? c : FuzzyCell(cellName);
        if (cell is null) return false;
        sprite = new ResolvedSprite(cell.Atlas, cell.X1, cell.Y1, cell.X2, cell.Y2,
            cell.CropX, cell.CropY, cell.OrigW, cell.OrigH, 0.5, 0.5, 1.0);
        return true;
    }

    private double FrameOpacity(string name, int frameI)
    {
        var a = ResolveAnim(name);
        if (a is null || a.Frames.Count == 0) return 1.0;
        return a.Frames[frameI % a.Frames.Count].Opacity;
    }

    private Anim? ResolveAnim(string name)
    {
        if (_anims.TryGetValue(name, out var a)) return a;
        if (_anims.TryGetValue(name + "_1", out a)) return a;
        string pref = name + "_";
        foreach (var k in _anims.Keys)
            if (k.StartsWith(pref, StringComparison.Ordinal)) return _anims[k];
        return null;
    }

    private Cell? BaseCell(string name, int frameI)
    {
        if (EntityName.Aliases.TryGetValue(name, out var al)) name = al;

        var a = ResolveAnim(name);
        if (a is { Frames.Count: > 0 })
            return _cells.TryGetValue(a.Frames[frameI % a.Frames.Count].Cell, out var fc) ? fc : null;

        if (_cells.TryGetValue(name, out var c)) return c;
        if (_cells.ContainsKey(name + "1")) return _cells[name + "1"];
        return FuzzyCell(name);
    }

    private Cell? FuzzyCell(string name)
    {
        var bases = new List<string> { name };
        if (EntityName.Aliases.TryGetValue(name, out var al) && al != name) bases.Add(al);

        foreach (var b in bases)
        {
            if (_cells.TryGetValue(b, out var c)) return c;
            if (_cellNorm.TryGetValue(EntityName.Norm(b), out var hit) && _cells.TryGetValue(hit, out var hc)) return hc;
            if (_cells.TryGetValue(b + "1", out var c1)) return c1;
        }
        var best = EntityName.BestClose(EntityName.Norm(name), _cellNorm.Keys, 0.86);
        if (best is not null && _cellNorm.TryGetValue(best, out var full)) return _cells[full];
        return null;
    }
}
