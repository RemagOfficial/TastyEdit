using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TastyEditor.Core.Assets;

/// <summary>Where a Tasty Planet 2 ("Back for Seconds") install keeps its art. Unlike the
/// ElementEngine games (a single <c>assets/built/graphics</c> + <c>assets/editabledirect</c>),
/// TP2 puts the atlas tables and images in <c>assets/graphics</c>
/// (<c>imagemaps.xml</c> + <c>&lt;atlas&gt;.jpg</c>/<c>&lt;atlas&gt;mask.png</c>) while the
/// animation/intersection/string XMLs sit in the parent <c>assets</c> folder. The editor
/// resolves each XML by searching a candidate list (graphics dir, then the assets root,
/// then the repo/cwd); <see cref="FindTable"/> mirrors that order so a partial install still
/// loads whatever is present. Levels live under <c>assets/levels</c>.</summary>
public sealed class Tp2AssetsRoot
{
    public required string GraphicsDir { get; init; }
    public required string AssetsDir { get; init; }

    /// <summary>Usable TP2 art tree: the graphics dir exists and carries <c>imagemaps.xml</c>.</summary>
    public bool IsValid =>
        Directory.Exists(GraphicsDir) && File.Exists(Path.Combine(GraphicsDir, "imagemaps.xml"));

    /// <summary>First existing <paramref name="fileName"/> among the graphics dir, the assets
    /// root and the repo/cwd — the same search <c>tp2editor._load_assets</c> performs.</summary>
    public string FindTable(string fileName)
    {
        foreach (var d in new[] { GraphicsDir, AssetsDir, Directory.GetCurrentDirectory() })
        {
            var p = Path.Combine(d, fileName);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(GraphicsDir, fileName);   // absent: caller's File.Exists check skips it
    }

    /// <summary>Resolve the TP2 asset root from a level path (walk up to the <c>assets</c>
    /// segment; graphics is its <c>graphics</c> child). <paramref name="overrideRoot"/> points
    /// straight at a graphics folder when supplied (a config key).</summary>
    public static Tp2AssetsRoot? ForLevel(string? levelPath, string? overrideRoot = null)
    {
        if (overrideRoot is not null && Directory.Exists(overrideRoot))
            return Build(overrideRoot);

        if (string.IsNullOrWhiteSpace(levelPath)) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(levelPath));
        string? assets = null;
        for (var d = new DirectoryInfo(dir ?? "."); d is not null; d = d.Parent)
            if (string.Equals(d.Name, "assets", StringComparison.OrdinalIgnoreCase)) { assets = d.FullName; break; }
        if (assets is null) return null;
        return new Tp2AssetsRoot
        {
            GraphicsDir = Path.Combine(assets, "graphics"),
            AssetsDir = assets,
        };
    }

    private static Tp2AssetsRoot Build(string graphicsDir) => new()
    {
        GraphicsDir = graphicsDir,
        AssetsDir = Directory.GetParent(graphicsDir)?.FullName ?? graphicsDir,
    };
}

/// <summary>One compositing instruction in a <see cref="Tp2SpritePlan"/>. A
/// <see cref="Tp2OpKind.Layer"/> pastes a resolved atlas cell (bottom-first) onto the shared
/// canvas, optionally multiplying that layer by its own baked <see cref="Tint"/> first (the
/// egypt house tarps). A <see cref="Tp2OpKind.Tint"/> multiplies the whole canvas built so far
/// by a colour, keeping alpha — the entity/deco recolour of grapes/planets/candy and the
/// star composite.</summary>
public enum Tp2OpKind { Layer, Tint }

public sealed class Tp2PlanOp
{
    public Tp2OpKind Kind;
    public ResolvedSprite Cell;                       // Layer geometry
    public string CellName = "";                       // Layer cell name (for the oracle signature)
    public (byte R, byte G, byte B)? Tint;            // Layer per-part tint OR Tint op colour
}

/// <summary>A resolved, tinted TP2 sprite expressed as an ordered pixel recipe (mirrors
/// <c>tp2assets.build_sprite</c>'s control flow) plus the frame pivot and a final alpha scale.
/// Core produces these headlessly; the WPF layer executes them against decoded atlases.
/// The App draws a cross when a name yields no plan.</summary>
public sealed class Tp2SpritePlan
{
    public readonly List<Tp2PlanOp> Ops = new();
    public (double X, double Y) Pivot = (0.5, 0.5);
    public int AlphaScale = 255;                       // round(tintAlpha * frameOpacity), applied last
}

/// <summary>UI-free port of <c>tp2assets.Assets</c>: loads TP2's imagemaps / animationdefs and
/// resolves a level entity/deco name to a <see cref="Tp2SpritePlan"/> through the exact
/// cascade (explicit aliases → egypt house recipe → multi-cell composites → skeletal
/// stand/walk body → animation frame → direct cell → candy base → <c>name1</c> → fuzzy with
/// theme/prefix strips, number-word mapping, stem synonyms, suffix trimming and a
/// digit-guarded difflib close-match). It never touches pixels, so it is testable headless and
/// cross-checkable against the Python oracle.</summary>
public sealed class Tp2SpriteResolver
{
    private readonly Tp2AssetsRoot _root;
    private readonly Dictionary<string, Cell> _cells;
    private readonly Dictionary<string, Anim> _anims;

    // norm(cell name) -> first cell spelling with that normalised form (setdefault order).
    private readonly Dictionary<string, string> _cellNorm = new();
    private readonly List<string> _cellKeys;

    // Explicit entity-name -> art-name aliases (verbatim from tp2assets.Aliases).
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["scientist_young_lab1"] = "modern_sciyoung",
        ["scientist_old_lab1"] = "modern_sciold",
        ["leaf"] = "cucumberleaf",
        ["stoneblock1"] = "stone1",
        ["galacticturtle"] = "turtles_all_the_way_down",
        ["fabricspace"] = "turtle_tile",
        ["redgiant"] = "star", ["supergiant"] = "star", ["hypergiant"] = "star", ["hypergiant_moving"] = "star",
        ["neptune_future1"] = "planet_gas", ["jupiter_future1"] = "planet_gas_2",
        ["saturn_future1"] = "planet_rings", ["mars_future1"] = "planet_rockey",
        ["venus_future1"] = "planet_rockey", ["mercury_future1"] = "planet_rockey", ["moon_future1"] = "planet_rockey",
        ["green_grape"] = "grape_olive_1", ["purple_grape"] = "grape_olive_1",
        ["green_olive"] = "grape_olive_1", ["purple_olive"] = "grape_olive_1",
        ["egypt_poor_house_eight"] = "poorhouse8", ["egypt_rich_house_four"] = "richouse4",
    };

    // Composite egypt houses: shared-canvas parts (garden under base under tinted tarp).
    private sealed record House(string? Base, string? Garden, string? Tarp, string TintHex);
    private static readonly Dictionary<string, House> HouseRecipe = new()
    {
        ["egypt_poor_house_1"] = new("poorhouse", null, "poorhousetarp", "#b5c5c5"),
        ["egypt_poor_house_2"] = new("poorhouse", null, "poorhousetarp", "#b9cba2"),
        ["egypt_poor_house_3"] = new("poorhouse", null, "poorhousetarp", "#cbc2b2"),
        ["egypt_rich_house_1"] = new("richouse", "richhousegarden1", "richousetarp", "#a8c681"),
        ["egypt_rich_house_2"] = new("richouse", "richhousegarden1", "richousetarp", "#d9cb7b"),
        ["egypt_rich_house_3"] = new("richouse", "richhousegarden1", "richousetarp", "#d07765"),
        ["egypt_rich_house_4"] = new("richouse", "richhousegarden2", "richousetarp", "#a8c681"),
        ["egypt_rich_house_5"] = new("richouse", "richhousegarden2", "richousetarp", "#d9cb7b"),
        ["egypt_rich_house_6"] = new("richouse", "richhousegarden2", "richousetarp", "#d07765"),
    };

    // base cell -> gloss/shine overlay composited on top after tinting.
    private static readonly Dictionary<string, string> Glow = new() { ["grape_olive_1"] = "grape_olive_2" };

    // multi-cell composites overlaid on a shared canvas, THEN recoloured by the entity RGBA.
    private static readonly Dictionary<string, string[]> Composites = new()
    {
        ["star"] = new[] { "star_bottom", "star_top" },
    };

    private static readonly string[] StripPrefixes = { "tree_", "plant_", "prop_", "deco_", "obj_" };
    private static readonly string[] ThemePrefixes =
        { "egypt_", "future_", "japan_", "roman_", "dino_", "modern_", "anti_", "croc_", "rat_", "lab_" };
    private static readonly Dictionary<string, string> NumWords = new()
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6",
        ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12",
    };
    private static readonly Dictionary<string, string> Synonyms = new() { ["boulder"] = "rock" };

    // Trailing theme / instance / state suffixes trimmed by _fuzzy_cell.
    private static readonly Regex ThemeSuffix = new(
        @"(_(?:future|egypt|dino|roman|japan|modern|anti|space)\d*|_moving|_disp)$", RegexOptions.Compiled);
    private static readonly Regex DigitSplit = new("(_)", RegexOptions.Compiled);
    private static readonly Regex TrailDigit = new(@"(\d+)$", RegexOptions.Compiled);

    private Tp2SpriteResolver(Tp2AssetsRoot root, Dictionary<string, Cell> cells, Dictionary<string, Anim> anims)
    {
        _root = root; _cells = cells; _anims = anims;
        foreach (var cn in cells.Keys) _cellNorm.TryAdd(EntityName.Norm(cn), cn);
        _cellKeys = _cellNorm.Keys.ToList();
    }

    /// <summary>Load the TP2 tables. Null when the tree isn't a usable install (no graphics /
    /// imagemaps), so the caller keeps drawing crosses. Never throws on absent optional files.</summary>
    public static Tp2SpriteResolver? TryLoad(Tp2AssetsRoot root)
    {
        if (root is null || !root.IsValid) return null;
        var cells = AssetTables.ReadImageMaps(Path.Combine(root.GraphicsDir, "imagemaps.xml"));
        var animPath = root.FindTable("animationdefs.xml");
        var anims = File.Exists(animPath) ? AssetTables.ReadAnimations(animPath) : new Dictionary<string, Anim>();
        return new Tp2SpriteResolver(root, cells, anims);
    }

    public Tp2AssetsRoot Root => _root;
    public IReadOnlyCollection<string> CellNames => _cells.Keys;
    public IReadOnlyCollection<string> AnimNames => _anims.Keys;
    public int CellCount => _cells.Count;
    public int AnimCount => _anims.Count;
    public bool HasAnimations => _anims.Count > 0;

    // ---- animation helpers (tp2assets.resolve_anim / frame_count / pivot / frame_transform) ----
    private Anim? ResolveAnim(string name)
    {
        if (_anims.TryGetValue(name, out var a)) return a;
        if (_anims.TryGetValue(name + "_1", out a)) return a;
        string pref = name + "_";
        foreach (var k in _anims.Keys)
            if (k.StartsWith(pref, StringComparison.Ordinal)) return _anims[k];
        return null;
    }

    public int FrameCount(string name) { var a = ResolveAnim(name); return a?.Frames.Count > 0 ? a.Frames.Count : 1; }

    public (double X, double Y) Pivot(string name)
    {
        var a = ResolveAnim(name);
        return a is null ? (0.5, 0.5) : (a.Cx, a.Cy);
    }

    private double FrameOpacity(string name, int frameI)
    {
        var a = ResolveAnim(name);
        if (a is null || a.Frames.Count == 0) return 1.0;
        return a.Frames[frameI % a.Frames.Count].Opacity;
    }

    // ---- name -> base cell cascade (tp2assets._base_cell / _fuzzy_cell) ----
    private Cell? BaseCell(string name, int frameI = 0)
    {
        name = Aliases.TryGetValue(name, out var al) ? al : name;

        if (_anims.ContainsKey(name + "_stand_body") || _anims.ContainsKey(name + "_walk_body"))
        {
            var body = _anims.TryGetValue(name + "_stand_body", out var sb) ? sb
                     : _anims.TryGetValue(name + "_walk_body", out var wb) ? wb : null;
            if (body is { Frames.Count: > 0 } && _cells.TryGetValue(body.Frames[0].Cell, out var bc))
                return bc;
        }

        var a = ResolveAnim(name);
        if (a is { Frames.Count: > 0 })
            return _cells.TryGetValue(a.Frames[frameI % a.Frames.Count].Cell, out var fc) ? fc : null;

        if (_cells.TryGetValue(name, out var c)) return c;
        if (name.StartsWith("candy_", StringComparison.Ordinal) && _cells.ContainsKey("candy")) return _cells["candy"];
        if (_cells.ContainsKey(name + "1")) return _cells[name + "1"];
        return FuzzyCell(name);
    }

    private Cell? FuzzyCell(string name)
    {
        var bases = new List<string> { name };
        foreach (var p in StripPrefixes.Concat(ThemePrefixes))
            if (name.StartsWith(p, StringComparison.Ordinal) && name.Length > p.Length)
                bases.Add(name[p.Length..]);

        var suf = ThemeSuffix.Replace(name, "");
        if (suf != name) bases.Add(suf);

        foreach (var b in bases.ToList())
            foreach (var (k, v) in Synonyms)
                if (b.Contains(k, StringComparison.Ordinal)) bases.Add(b.Replace(k, v));

        foreach (var b in bases)
            foreach (var cand in new[] { b, Numword(b) })
            {
                if (_cells.TryGetValue(cand, out var c)) return c;
                if (_cellNorm.TryGetValue(EntityName.Norm(cand), out var hit)) return _cells[hit];
            }

        foreach (var b in bases)
        {
            string q = EntityName.Norm(Numword(b));
            foreach (var m in EntityName.CloseMatches(q, _cellKeys, 4, 0.86))
                if (TrailDigits(m) == TrailDigits(q))
                    return _cells[_cellNorm[m]];
        }
        return null;
    }

    // number-words -> digits, keeping '_' separators (tp2assets._numword).
    private static string Numword(string name)
    {
        var parts = DigitSplit.Split(name);
        var sb = new StringBuilder(name.Length);
        foreach (var p in parts) sb.Append(NumWords.TryGetValue(p, out var d) ? d : p);
        return sb.ToString();
    }

    private static string TrailDigits(string s)
    {
        var m = TrailDigit.Match(s);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static ResolvedSprite Geo(Cell c) =>
        new(c.Atlas, c.X1, c.Y1, c.X2, c.Y2, c.CropX, c.CropY, c.OrigW, c.OrigH, 0.5, 0.5, 1.0);

    // ---- sprite building (tp2assets.build_sprite), as a pixel recipe ----
    /// <summary>Resolve <paramref name="name"/> (frame <paramref name="frameI"/>, tinted by the
    /// record's <paramref name="rgba"/>) to a compositing plan. Null when no cell matches — the
    /// caller draws its placeholder.</summary>
    public Tp2SpritePlan? BuildPlan(string name, byte[] rgba, int frameI = 0)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string canon = Aliases.TryGetValue(name, out var al) ? al : name;

        if (HouseRecipe.TryGetValue(canon, out var house))
        {
            var hp = new Tp2SpritePlan { Pivot = Pivot(name) };
            AddHouseParts(hp, house);
            if (hp.Ops.Count > 0) return hp;      // baked tints, no entity recolour, no opacity
        }

        if (Composites.TryGetValue(canon, out var parts))
        {
            var cp = new Tp2SpritePlan { Pivot = Pivot(name) };
            foreach (var p in parts)
                if (_cells.TryGetValue(p, out var pc)) cp.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Layer, Cell = Geo(pc), CellName = pc.Name });
            if (cp.Ops.Count > 0)
            {
                AddTintIfColourful(cp, rgba);
                return cp;
            }
        }

        var baseCell = BaseCell(name, frameI);
        if (baseCell is null) return null;

        var plan = new Tp2SpritePlan { Pivot = Pivot(name) };
        plan.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Layer, Cell = Geo(baseCell), CellName = baseCell.Name });
        AddTintIfColourful(plan, rgba);

        if (name.StartsWith("candy_", StringComparison.Ordinal) && ResolveAnim(name) is null
            && _cells.TryGetValue("candy_overlay", out var candyOv))
            plan.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Layer, Cell = Geo(candyOv), CellName = candyOv.Name });

        if (Glow.TryGetValue(baseCell.Name, out var glowName) && _cells.TryGetValue(glowName, out var glowCell))
            plan.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Layer, Cell = Geo(glowCell), CellName = glowCell.Name });

        byte a = rgba is { Length: 4 } ? rgba[3] : (byte)255;
        plan.AlphaScale = (int)Math.Round(a * FrameOpacity(name, frameI));
        return plan;
    }

    /// <summary>A browser thumbnail for an exact / fuzzy cell name: a single untinted layer (or
    /// null when the cell is absent). Mirrors <c>tp2assets.cell_image</c>.</summary>
    public bool TryCellPlan(string cellName, out Tp2SpritePlan plan)
    {
        plan = new Tp2SpritePlan();
        var cell = _cells.TryGetValue(cellName, out var c) ? c : FuzzyCell(cellName);
        if (cell is null) return false;
        plan.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Layer, Cell = Geo(cell), CellName = cell.Name });
        return true;
    }

    private void AddHouseParts(Tp2SpritePlan plan, House h)
    {
        // garden (back) -> base -> tarp tinted by the per-house colour, on a shared canvas.
        AddLayer(plan, h.Garden, null);
        AddLayer(plan, h.Base, null);
        AddLayer(plan, h.Tarp, ParseHexTint(h.TintHex));
    }

    private void AddLayer(Tp2SpritePlan plan, string? cellName, (byte, byte, byte)? tint)
    {
        if (cellName is null) return;
        if (_cells.TryGetValue(cellName, out var c))
            plan.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Layer, Cell = Geo(c), CellName = c.Name, Tint = tint });
    }

    private static void AddTintIfColourful(Tp2SpritePlan plan, byte[] rgba)
    {
        byte r = rgba is { Length: >= 3 } ? rgba[0] : (byte)255;
        byte g = rgba is { Length: >= 3 } ? rgba[1] : (byte)255;
        byte b = rgba is { Length: >= 3 } ? rgba[2] : (byte)255;
        if (r != 255 || g != 255 || b != 255)
            plan.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Tint, Tint = (r, g, b) });
    }

    private static (byte, byte, byte)? ParseHexTint(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#')
            return (Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16),
                    Convert.ToByte(hex.Substring(5, 2), 16));
        return null;
    }

    // ---- oracle cross-check signature ----
    /// <summary>A deterministic text digest of the plan <see cref="BuildPlan"/> would produce
    /// (layer cell names, tints, pivot, alpha). The headless TP2 check diffs this against the
    /// Python oracle's own digest for the same name, proving the ported cascade resolves to the
    /// identical cells/recipe — the fidelity risk lives in name resolution, not pixel math.</summary>
    public string Signature(string name, byte[] rgba, int frameI = 0)
    {
        var p = BuildPlan(name, rgba, frameI);
        if (p is null) return "None";
        var sb = new StringBuilder();
        foreach (var op in p.Ops)
        {
            if (sb.Length > 0) sb.Append(',');
            if (op.Kind == Tp2OpKind.Tint)
                sb.Append("T:").Append(Hex(op.Tint!.Value));
            else
            {
                sb.Append(op.CellName);
                if (op.Tint is { } lt) sb.Append('~').Append(Hex(lt));
            }
        }
        sb.Append(" A").Append(p.AlphaScale.ToString(CultureInfo.InvariantCulture));
        sb.Append(" P").Append(p.Pivot.X.ToString("0.###", CultureInfo.InvariantCulture))
          .Append(',').Append(p.Pivot.Y.ToString("0.###", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string Hex((byte R, byte G, byte B) t) => $"{t.R:x2}{t.G:x2}{t.B:x2}";
}
