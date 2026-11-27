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
/// Bite): loads imagemaps / animationdefs / entityintersections / skeletons for one
/// <see cref="AssetsRoot"/> and resolves a level entity's spawn name either to a single
/// <see cref="ResolvedSprite"/> (the <c>tbassets</c> cascade: species key → alias → animation
/// frame → direct cell → <c>name1</c> → stem → fuzzy) or, when the entity is one the engine
/// assembles from parts, to a <see cref="TbRigPlan"/> read out of <c>skeletons.bin</c> — the
/// only rig source these titles have, since their animationdefs.xml is an empty stub. When the
/// install ships the game binary, it also carries <c>EngineArt</c>: the entity → art table that is
/// compiled into the executable instead of any data file. It never touches pixels or WPF, so it is
/// fully testable headless.</summary>
public sealed partial class SpriteResolver
{
    private readonly AssetsRoot _root;
    private readonly Dictionary<string, Cell> _cells;
    private readonly Dictionary<string, Anim> _anims;
    private readonly Dictionary<string, double> _natdiam;
    // entityintersections.xml natwidth / natheight / coverage, keyed by def name / catalog base /
    // species key (AssetTables.ReadNatBox). The ElementEngine sizes an entity so the ink you SEE
    // equals the record's area (main.py physics_scale = sqrt(area / (spriteW*spriteH*areacoverage))),
    // so the viewport needs this to divide a sparse rig's mass back out -- exactly as TP2 does.
    private readonly Dictionary<string, (double W, double H, double Cov)> _natbox;
    // editabledirect/skeletons.bin: rigName -> root joint. Null (not empty) when the install ships no
    // skeleton table, so every entity stays on the single-cell path exactly as before.
    private readonly Dictionary<string, TbBone>? _rigs;

    // norm(cell name) -> first cell name with that spelling (setdefault order preserved).
    private readonly Dictionary<string, string> _cellNorm = new();
    // Final Bite bakes each prop/creature's lighting variants straight into the atlas cell names
    // (<c>building_1_dip1..4</c>, <c>colosseum_dip1..4</c>) while the level's TYPE and the skeletons name
    // the bare art (<c>building_1</c>, <c>colosseum</c>). Map that bare spelling to one variant so the
    // single-cell tiers can find it; empty for the installs that have no <c>_dipN</c> cells.
    private readonly Dictionary<string, string> _dipBase = new();

    private SpriteResolver(AssetsRoot root,
        Dictionary<string, Cell> cells, Dictionary<string, Anim> anims, Dictionary<string, double> natdiam,
        Dictionary<string, (double W, double H, double Cov)> natbox,
        Dictionary<string, TbBone>? rigs)
    {
        _root = root; _cells = cells; _anims = anims; _natdiam = natdiam; _natbox = natbox; _rigs = rigs;
        foreach (var cn in cells.Keys)
        {
            _cellNorm.TryAdd(EntityName.Norm(cn), cn);
            int d = cn.LastIndexOf("_dip", StringComparison.Ordinal);
            if (d <= 0 || d + 4 >= cn.Length || !char.IsDigit(cn[d + 4])) continue;   // <base>_dipN only
            string baseName = cn[..d];
            // Keep the lowest-numbered lighting variant that actually exists (some arts start at dip2).
            if (!_dipBase.TryGetValue(baseName, out var cur)
                || int.Parse(cn.AsSpan(d + 4)) < int.Parse(cur.AsSpan(d + 4)))
                _dipBase[baseName] = cn;
        }
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
        var natbox = AssetTables.ReadNatBox(Path.Combine(root.EditableDir, "entityintersections.xml"));
        var (rigs, rigDialect, rigError) = ReadSkeletons(Path.Combine(root.EditableDir, "skeletons.bin"));
        // Rig NAMES even when the rig grammar refuses the file: Forever / Final Bite ship a skeletons.bin
        // variant Blue's loaders do not describe, but their name records are anchor-readable, and the
        // engine-art scan below needs only the names to recognise a creature registered as a rig.
        var rigNames = rigs is not null ? rigs.Keys : (IReadOnlyCollection<string>)
            TbSkeletons.ScanRigNames(File.ReadAllBytes(Path.Combine(root.EditableDir, "skeletons.bin")));
        // The compiled-in entity -> art table, which lives in the game binary beside the assets folder
        // rather than inside it (see SpriteResolver.TbEngine). Every install carries it in its own exe —
        // Tasty Blue / Forever as x86 absolute literal pushes, Final Bite as x64 RIP-relative leas — so
        // the scan walks the directory's executables instead of one title's name. Absent or unreadable
        // returns null and costs nothing.
        var engineArt = ReadEngineArtForDir(
            Path.GetFullPath(Path.Combine(root.Root, "..")), cells.Keys, rigNames);
        return new SpriteResolver(root, cells, anims, natdiam, natbox, rigs)
        { RigError = rigError, EngineArt = engineArt, RigsAreFinalBite = rigDialect == TbSkeletons.Dialect.FinalBite };
    }

    public AssetsRoot Root => _root;
    public IReadOnlyCollection<string> CellNames => _cells.Keys;
    public IReadOnlyCollection<string> AnimNames => _anims.Keys;
    public int CellCount => _cells.Count;
    public int AnimCount => _anims.Count;

    public bool TryGetCell(string name) => _cells.ContainsKey(name);

    // Headless diagnostics for the _dip resolution (Smoke.F45Probe); read-only.
    public int DipBaseCount => _dipBase.Count;

    public double NatDiam(string name, double fallback = 300.0) =>
        _natdiam.TryGetValue(name, out var v) ? v : fallback;

    /// <summary>The engine's own assembled art box (natwidth x natheight) and its ink coverage for an
    /// entity, or null when the def table has no entry. A level places <c>kayak_lab1</c> while the def is
    /// <c>kayak_base</c>, so the lookup walks the same spellings the art resolution does: the raw name,
    /// its species key, its catalog base, and each through the <see cref="EntityName.Aliases"/> table.
    /// A (0,0,0) entry is ReadNatBox's "several defs share this shorthand" marker and counts as a miss.</summary>
    public (double W, double H, double Cov)? NatBox(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var key in new[] { name, EntityName.SpeciesKey(name), EntityName.CatalogBase(name) })
        {
            if (HitBox(key, out var nb)) return nb;
            if (EntityName.Aliases.TryGetValue(key, out var art) && HitBox(art, out nb)) return nb;
        }
        // A level places <art>_<setname> (seagull_crocodile1, swimmermale_dino1) with an arbitrary set
        // suffix that SpeciesKey does not recognise, so walk the underscore prefixes longest-first to the
        // owning art def -- the same stem logic the art cascade uses (StemCell), keeping an underscored
        // art (great_white_shark) ahead of any shorter prefix that happens to name an unrelated creature.
        for (int i = name.LastIndexOf('_'); i > 0; i = name.LastIndexOf('_', i - 1))
        {
            string stem = name[..i];
            if (HitBox(stem, out var nb)) return nb;
            if (HitBox(stem + "_base", out nb)) return nb;
            if (EntityName.Aliases.TryGetValue(stem, out var art) && HitBox(art, out nb)) return nb;
        }
        return null;
    }

    private bool HitBox(string key, out (double W, double H, double Cov) nb) =>
        _natbox.TryGetValue(key, out nb) && nb.W > 0 && nb.H > 0;

    /// <summary>The shipped <c>coverage</c> of an entity's own def: the opaque fraction of its natwidth x
    /// natheight box, or 1.0 when it has no def (so the render scale falls back to the plain box area).
    /// Tasty Blue sizes a sprite so the material you SEE equals the record's mass; a sparse rig (kayak
    /// 0.085, seagull 0.119) otherwise renders far smaller than the mass the level author gave it.</summary>
    public double EntityCoverage(string name) => NatBox(name) is (double, double, double cov) ? cov : 1.0;

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
        Cell? cell = BaseCell(key, frameI) ?? StemCell(key);
        if (cell is null) return false;

        var (px, py) = Pivot(key);
        double opacity = FrameOpacity(key, frameI);
        sprite = new ResolvedSprite(cell.Atlas, cell.X1, cell.Y1, cell.X2, cell.Y2,
            cell.CropX, cell.CropY, cell.OrigW, cell.OrigH, px, py, opacity);
        return true;
    }

    /// <summary>The direct half of the cascade — alias, animation frame, exact cell, <c>name1</c> —
    /// with no stem stripping and no fuzzy tail. The viewport tries this first, then a skeleton rig,
    /// then <see cref="TryResolveFallbackCell"/>, so an entity that already has its own art is never
    /// replaced by a composite.</summary>
    public bool TryResolveDirectCell(string rawName, int frameI, out ResolvedSprite sprite)
    {
        sprite = default;
        if (string.IsNullOrEmpty(rawName)) return false;
        string key = EntityName.SpeciesKey(rawName);
        Cell? cell = BaseCell(key, frameI);
        if (cell is null) return false;
        sprite = ToResolved(cell, key, frameI);
        return true;
    }

    /// <summary>The last-resort half of the cascade: the art stem (an entity named
    /// <c>anchor_static</c> paints cell <c>anchor</c>) and then the fuzzy spelling match.</summary>
    public bool TryResolveFallbackCell(string rawName, int frameI, out ResolvedSprite sprite)
    {
        sprite = default;
        if (string.IsNullOrEmpty(rawName)) return false;
        string key = EntityName.SpeciesKey(rawName);
        Cell? cell = StemCell(key) ?? FuzzyCell(key);
        if (cell is null) return false;
        sprite = ToResolved(cell, key, frameI);
        return true;
    }

    private ResolvedSprite ToResolved(Cell cell, string key, int frameI) => new(
        cell.Atlas, cell.X1, cell.Y1, cell.X2, cell.Y2,
        cell.CropX, cell.CropY, cell.OrigW, cell.OrigH,
        Pivot(key).X, Pivot(key).Y, FrameOpacity(key, frameI));

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
        return DipBaseCell(name);
    }

    /// <summary>Resolve a bare art spelling to the Final Bite atlas cell stored only under its
    /// <c>_dipN</c> lighting variants (<c>colosseum</c> -> <c>colosseum_dip1</c>). The art may sit behind a
    /// state suffix the level type carries (<c>colosseum_phys</c>, <c>building_1_static</c>), so walk the
    /// underscore prefixes longest-first — the same stem descent <see cref="NatBox"/> uses. Null everywhere
    /// the atlas has no such variants, so Tasty Blue / Forever / TP2 are unaffected.</summary>
    private Cell? DipBaseCell(string name)
    {
        if (_dipBase.Count == 0) return null;                 // no install without _dip cells pays this
        if (_dipBase.TryGetValue(name, out var self) && _cells.TryGetValue(self, out var sc)) return sc;
        for (int i = name.LastIndexOf('_'); i > 0; i = name.LastIndexOf('_', i - 1))
            if (_dipBase.TryGetValue(name[..i], out var hit) && _cells.TryGetValue(hit, out var hc)) return hc;
        // concatenated art+theme (coral_polypatlantis1_1): the art is not on an underscore boundary, so
        // take the longest dip base that is a bare prefix, mirroring the rig-name fallback in FindRig.
        string low = name.ToLowerInvariant();
        string? bestKey = null;
        foreach (var key in _dipBase.Keys)
            if (low.StartsWith(key, StringComparison.Ordinal) &&
                (bestKey is null || key.Length > bestKey.Length)) bestKey = key;
        if (bestKey is not null && _cells.TryGetValue(_dipBase[bestKey], out var bc)) return bc;
        // The art stem carries a qualifier after it (at_rock -> at_rock_entity_1 / at_rock_tile_1) AND the
        // level type may glue a theme/instance token onto it (<at_rock><atlantis1>_12), so the stem is an
        // underscore boundary but not itself a dip base key. Walk the underscore prefixes longest-first and
        // match each against a dip base that begins with "<prefix>_"; prefer the _entity variant (the sprite
        // a placed entity draws, over the background tile), then the longest. Require a 2+-token prefix so a
        // bare leading word cannot grab an unrelated "<word>_*" cell. Only runs on an otherwise-unresolved
        // name, so Tasty Blue / Forever / TP2 (no _dip cells) never reach here.
        string? bestQualified = null;
        for (int i = name.LastIndexOf('_'); i > 0; i = name.LastIndexOf('_', i - 1))
        {
            string cand = name[..i];
            if (cand.IndexOf('_') < 0) continue;                 // need at least <art>_<qualifier> to glue onto
            foreach (var key in _dipBase.Keys)
                if (key.StartsWith(cand + "_", StringComparison.Ordinal) && BetterQual(key, bestQualified))
                    bestQualified = key;
        }
        return bestQualified is not null && _cells.TryGetValue(_dipBase[bestQualified], out var qc) ? qc : null;
    }

    // Prefer an _entity dip base over a plain/tile one; within the same class, the longer key.
    private static bool BetterQual(string cand, string? cur) =>
        cur is null
        || (cand.Contains("_entity", StringComparison.Ordinal)
            && !cur.Contains("_entity", StringComparison.Ordinal))
        || (cand.Contains("_entity", StringComparison.Ordinal) == cur.Contains("_entity", StringComparison.Ordinal)
            && cand.Length > cur.Length);

    /// <summary>The art stem: an ElementEngine entity TYPE is <c>&lt;art&gt;_&lt;state&gt;</c>
    /// (<c>anchor_static</c>, <c>fighterjet_dolphin2a</c>, <c>aircraftcarrier_aircraftcarrier1</c>) while
    /// its cell is keyed by <c>&lt;art&gt;</c> alone. Tasty Blue's entity names are never cell names
    /// (measured: 0 of the 326 distinct TYPEs, and animationdefs.xml is an empty stub), so without this
    /// the whole prey population renders as crosses. Longest stem first, so a genuinely underscored cell
    /// keeps its specificity (<c>jump_hoop_base</c> must find <c>jump_hoop</c>, not <c>jump</c>).</summary>
    private Cell? StemCell(string name)
    {
        for (int i = name.LastIndexOf('_'); i > 0; i = name.LastIndexOf('_', i - 1))
            if (_cells.TryGetValue(name[..i], out var c)) return c;
        return DipBaseCell(name);
    }
}
