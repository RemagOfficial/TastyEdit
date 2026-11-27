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
    // Per-part transform, only meaningful for a skeletal assembly Layer (default = identity, so
    // the house/composite/single Layer paths draw exactly as before).
    public double OffX, OffY;                          // part-pivot position relative to the entity origin, px
    public double AngleDeg, Scale = 1.0;               // rotation (deg) and scale about the part pivot
    public bool FlipX, FlipY;
    public double PartCx = 0.5, PartCy = 0.5;          // part pivot within its own cell canvas (animdef centerx/centery)
    public double PartOpacity = 1.0;                   // per-frame opacity, applied as the part is laid down
    public int Priority;                               // animdef draw order (recorded for the oracle signature)
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
    // Skeletal assembly (multi-part character). When true the App composites the Layer parts onto
    // a shared <see cref="CanvasW"/>×<see cref="CanvasH"/> buffer, placing each part's pivot at
    // (<see cref="OriginX"/> + OffX, <see cref="OriginY"/> + OffY). Axis-aligned geometry, so the
    // oracle diff is deterministic (no trig).
    public bool IsSkeletal;
    public int CanvasW, CanvasH;
    public double OriginX, OriginY;
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
    // entityintersections.xml natwidth / natheight / coverage, keyed by def name / catalog base /
    // species key. The engine's own assembled sprite box and how much of it is ink.
    private readonly Dictionary<string, (double W, double H, double Cov)> _natbox;

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
        // Dino props whose art cell is spelled differently from the level's entity type. Proven by the
        // engine's own boxes: def twig 300x68 == cell stick1 300x68, def leaf_maple 200x208 == cell
        // mapleleaf 200x209; both <name>_disp strings sit next to those art names in the exe's pool.
        ["twig"] = "stick1",
        ["leaf_maple"] = "mapleleaf",
        // The future level's goo enemy: the engine registers it onto the animated `goo` cell strip
        // (goo1..goo8), which no name trimming connects it to.
        ["goo_enemy_future1"] = "goo",
        ["galacticturtle"] = "turtles_all_the_way_down",
        ["fabricspace"] = "turtle_tile",
        ["redgiant"] = "star", ["supergiant"] = "star", ["hypergiant"] = "star", ["hypergiant_moving"] = "star",
        ["neptune_future1"] = "planet_gas", ["jupiter_future1"] = "planet_gas_2",
        ["saturn_future1"] = "planet_rings", ["mars_future1"] = "planet_rockey",
        ["venus_future1"] = "planet_rockey", ["mercury_future1"] = "planet_rockey", ["moon_future1"] = "planet_rockey",
        ["green_grape"] = "grape_olive_1", ["purple_grape"] = "grape_olive_1",
        ["green_olive"] = "grape_olive_1", ["purple_olive"] = "grape_olive_1",
        ["egypt_poor_house_eight"] = "poorhouse8", ["egypt_rich_house_four"] = "richouse4",
        // Future-level props with no name link of their own. Each is proven by the entitydef's own
        // sprite box, which equals the cell's canvas (the measurement that pinned twig==stick1):
        // amoeba1_base 200x194 == cell amoeba, laserbolt_base 100x26 == laser_bolt, vacuum_base
        // 200x184 == cell `vaccum` (the atlas misspells it), shuttlecraft_base 200x200 == the only
        // shuttle art there is, shuttle_craft. The exe's registration pool lists them adjacently too
        // (`vacuum_base | vacuum_disp | vacuum | 97a384 | vaccum`). uranus has no art of its own: the
        // future set ships four planet cells and the other gas giant's is planet_gas, just as
        // mars/venus/mercury/moon all share planet_rockey. hovercar is the body/windows pair below.
        ["amoeba1"] = "amoeba", ["laserbolt"] = "laser_bolt", ["vacuum"] = "vaccum",
        ["shuttlecraft"] = "shuttle_craft", ["uranus"] = "planet_gas", ["hovercar"] = "hovercar_body",
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
        // Registered on one 230x128 canvas with its glass (exe: `hovercar_base | hovercar_disp |
        // explosion | hovercar_body | hovercar_windows`), so the body alone loses its windows. The
        // glass is NOT part of the tinted body (see CompositeOverlays) -- only the chassis recolors.
        ["hovercar_body"] = new[] { "hovercar_body" },
    };

    // Composite parts laid on the shared canvas AFTER the entity recolour, so they keep their own art
    // colour. A Tint op multiplies only the canvas built so far, so a Layer added after it escapes the
    // record tint -- which is exactly how the hovercar windows stay clear glass while its body recolors.
    private static readonly Dictionary<string, string[]> CompositeOverlays = new()
    {
        ["hovercar_body"] = new[] { "hovercar_windows" },
    };

    // Characters whose parts are animated but whose defs carry no _stand_/_walk_ pose token, so the
    // stem search can never find them. Proven by the box: hoverguy_base is 238x74 and the six
    // `hoverguy_*` defs' extent envelope is 237x73 -- a crouched rider (body rotated 87 deg) on a
    // 250x67 board. Listed bottom-first; the animdefs' own priority orders the draw anyway.
    private static readonly Dictionary<string, string[]> PartGroups = new()
    {
        ["hoverguy"] = new[] { "hoverguy_board", "hoverguy_foot_upper_left",
                               "hoverguy_foot_upper_right", "hoverguy_body",
                               "hoverguy_head", "hoverguy_hair" },
    };

    // Which placed type is the *armed variant* of a character, i.e. gets the accessory part that the
    // base animal does not draw. The engine keeps the variant as its own entitydef (`ant_laser_base`,
    // coverage 0.2272930 against the plain `ant_base` 0.2173381 -- the gun's ink is the difference) and
    // its own part defs, appended after the base rig's list in the same animdef block:
    // `... ant_walk_head | ant_laser_base | ant_laser_disp | ant_stand_laser | ant_walk_laser`, and
    // strings.xml calls it "Laser Ant" / "Laser Rat". Nothing in the type name says `laser`, so the
    // carriers come from the exe's master type table, which lists every laser carrier immediately
    // before its own projectile: `ant_future1 | laserbolt_ant1`, `rat_future1 | laserbolt_rat1`,
    // `rat_future2 | laserbolt_rat2`, `simon_future1 | laserbolt_simon1`, `simon_future2 |
    // laserbolt_simon2`, `simon_future3` (with `laserbolt_simon3`). The plain types have no bolt and are
    // placed by other levels: `ant_dino1/2` (dino1/dino2.bin), `ant_explosive1` (ant_explode1a.bin),
    // `rat_egypt1`, `rat_rome1`, `rat_lab1/2`, `simon_lab1`, `simon_labfarattack`.
    private static readonly Dictionary<string, string> TypeVariant = new()
    {
        ["ant_future1"] = "laser",
        ["rat_future1"] = "laser", ["rat_future2"] = "laser",
        ["simon_future1"] = "laser", ["simon_future2"] = "laser", ["simon_future3"] = "laser",
    };

    // Skeletal art stems whose animation group is spelled differently from the entity type, so no
    // amount of prefix/suffix trimming connects them. Read off the game's own entity-def table in
    // tastyplanet2.exe: each `<type>_base` block is followed by that character's part strings, all
    // sharing one `<stem>_stand_*` / `<stem>_walk_*` prefix (e.g. the alamosaurus block lists
    // ala_stand_body_lower ...; tyrannosaurus lists tyran_*; jackal lists jack_*). The exe is the
    // only place this mapping exists -- animationdefs.xml knows the stems, never the type names.
    private static readonly Dictionary<string, string> SkelAliases = new()
    {
        ["alamosaurus"] = "ala", ["albertosaurus"] = "alberta", ["bambiraptor"] = "bambi",
        ["euoplocephalus"] = "euo", ["corythosaurus"] = "cory", ["maiasaura"] = "maia",
        ["nanotyrannus"] = "nano", ["triceratops"] = "tricer", ["struthiomimus"] = "struth",
        ["parasaurolophus"] = "para", ["nothronychus"] = "noth", ["styracosaurus"] = "styrac",
        ["pachyrhinosaurus"] = "pachyr", ["oryctodromeus"] = "ory", ["stegoceras"] = "steg",
        ["troodon"] = "troo", ["tyrannosaurus"] = "tyran", ["stygimoloch"] = "styg",
        ["grasshopper"] = "grasshop", ["termite"] = "ter", ["scarab"] = "scar",
        ["jackal"] = "jack", ["turtle"] = "turt", ["possum"] = "poss", ["dragonfly"] = "drag",
        ["butterfly1"] = "butt", ["butterfly2"] = "moth", ["cat"] = "mau",
        ["bacteria1"] = "bacteria", ["bacteria2"] = "bacteria", ["bacteria3"] = "bacteria",
        // Same proof as the cell aliases: the entitydef's nat box equals the rig's extent envelope,
        // to the pixel -- ninja_base 228x150 == japan_ninja_* (228x150 over 26 defs), samurai_base
        // 236x164 == japan_samurai_* (236x163), scientist_old_base 310x202 == modern_sciold_*
        // (309x201), scientist_young_base 294x192 == modern_sciyoung_* (293x191), future_tank_base
        // 200x202 == tank_* (199x201). The japan mouse is the last: nothing at all is spelled
        // `mouse` in the tables (the only cell is `mousearrows`, which lives in the tutorial-tip
        // atlas), yet japanmouse1a.bin places `mouse_japan1` beside rice/teapot, the exe pairs the
        // type with `mouse_disp`, and the one creature rig in the japan atlases is japanmonster
        // (lower/upper body, tail, four paws, head -- a rat at three times the size).
        ["ninja"] = "japan_ninja", ["samurai"] = "japan_samurai",
        ["scientist_old"] = "modern_sciold", ["scientist_young"] = "modern_sciyoung",
        ["future_tank"] = "tank", ["mouse"] = "japanmonster",
    };

    // Which entity's art a placed type reuses, read off the exe's registration sequence: each entity is
    // registered by pushing `<art>_base` and then the level's `<type>` name, so a type with no animation
    // group and no entitydef of its own is drawn by the preceding entity's rig. Recovered by scanning
    // .text for push/mov imm32 literals in the registration range (0x559000-0x570000) and taking the
    // nearest preceding `<...>_base` for each type the shipped levels actually place; the known pairs
    // (alamosaurus_dino1 -> alamosaurus_base, tyrannosaurus_dino1 -> tyrannosaurus_base, grasshopper_dino1
    // -> grasshopper_base) reproduce the aliases already in SkelAliases, which is what validates the rest.
    // agomphus really is drawn by the turtle rig and watusi by the cow rig.
    private static readonly Dictionary<string, string> ArtEntity = new()
    {
        ["agomphus"] = "turtle", ["alphadon"] = "possum", ["cimolestes"] = "shrew",
        ["deinosuchus"] = "croc", ["locust"] = "grasshopper",
        ["mau_kitten"] = "cat", ["watusi"] = "cow", ["ant_explosive1"] = "ant",
    };

    // Inverse of SkelAliases (art stem -> entity type), built once and holding only stems that exactly
    // one alias shares. "bacteria" is spelled by three tiers (bacteria1/2/3), so asking the table for its
    // art box has no single answer; keeping it out makes NatBox return null instead of whichever tier
    // happened to register first, which would size a 3-segment sprite off a 1-segment box.
    private static readonly Dictionary<string, string> SkelAliasInverse = BuildAliasInverse();

    private static Dictionary<string, string> BuildAliasInverse()
    {
        var rev = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in SkelAliases)
        {
            if (!rev.TryGetValue(kv.Value, out var list)) rev[kv.Value] = list = new List<string>();
            list.Add(kv.Key);
        }
        var uniq = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in rev) if (kv.Value.Count == 1) uniq[kv.Key] = kv.Value[0];
        return uniq;
    }

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
    // Trailing level-set suffixes trimmed when finding a skeletal stem. Wider than ThemeSuffix: it
    // also drops `_lab<n>` / `_prehistoric<n>`, and it never trims `_disp` (a strings.xml key, not a
    // placed type). Used only by the skeletal lookup, so the single-cell cascade is untouched.
    private static readonly Regex SkelSuffix = new(
        @"_(?:future|egypt|dino|roman|japan|modern|anti|space|lab|prehistoric)\d*$", RegexOptions.Compiled);
    // A whole `_stand_*` / `_walk_*` pose tail, trimmed when a level writes one of the character's own
    // part names as its type (egypt_noble_walk_arm_upper) rather than the entity name.
    private static readonly Regex SkelPose = new(@"_(?:stand|walk)_.*$", RegexOptions.Compiled);
    private static readonly Regex DigitSplit = new("(_)", RegexOptions.Compiled);
    private static readonly Regex TrailDigit = new(@"(\d+)$", RegexOptions.Compiled);

    // A level decorates a type name with a set word and a tier digit (lion_rome1, hovercar_float1,
    // future_tank_2), while the art is spelled by the leading fields alone. These words name a set or a
    // variant rather than a character, so trimming may never stop on one -- otherwise future_tank_2
    // would walk back to the bare theme word `future` and borrow whatever that resolves to.
    private static readonly HashSet<string> SetWords = new(StringComparer.Ordinal)
    {
        "future", "egypt", "dino", "roman", "rome", "japan", "modern", "anti", "space", "lab",
        "prehistoric", "baby", "pushable", "stationary", "float", "fight", "watch", "tank",
        "old", "young", "blue", "green", "white", "greyalien", "base", "stand", "walk", "body",
        "head", "one", "two", "three", "four", "five", "six", "disp",
    };

    /// <summary>`a_b_c1` -> `a_b_c`, `a_b`, ... longest first, plus the name with a trailing tier digit
    /// dropped, so both `stoneblock1_pushable`->`stoneblock1` and `amoeba1_future1`->`amoeba1` reach
    /// their art. Mirrors <c>tp2assets._trim_chain</c>.</summary>
    private static List<string> TrimChain(string name)
    {
        var outList = new List<string>();
        var cur = name;
        while (true)
        {
            int i = cur.LastIndexOf('_');
            if (i <= 0) break;
            cur = cur[..i];
            if (!SetWords.Contains(cur)) outList.Add(cur);
        }
        var head = TrailDigit.Replace(name, "");
        if (head.Length > 0 && head != name && !SetWords.Contains(head)) outList.Add(head);
        return outList;
    }

    private Tp2SpriteResolver(Tp2AssetsRoot root, Dictionary<string, Cell> cells, Dictionary<string, Anim> anims,
        Dictionary<string, (double W, double H, double Cov)> natbox)
    {
        _root = root; _cells = cells; _anims = anims; _natbox = natbox;
        _variantBase = BuildVariantBases(natbox);
        foreach (var cn in cells.Keys) _cellNorm.TryAdd(EntityName.Norm(cn), cn);
        _cellKeys = _cellNorm.Keys.ToList();
    }

    // Which entitydef measures an art stem that has no def of its own. The alias table already records
    // the link from the other side (`scientist_young_lab1` draws with the stem `modern_sciyoung`), so the
    // scientist rigs are sized by the only defs that describe that art: scientist_young_base 294x192
    // cov 0.2518 and scientist_old_base 310x202 cov 0.2643. Without this they silently fell back to
    // coverage 1.0 and drew too large.
    private static readonly Dictionary<string, string> ArtDef = new(StringComparer.Ordinal)
    {
        ["modern_sciyoung"] = "scientist_young",
        ["modern_sciold"] = "scientist_old",
    };

    // `<name>_<variant>_base` def families whose members all declare the SAME box -- future_person's four
    // colour tiers are 226x172 cov 0.2096 each, and the placed type is just `future_person`. Read off the
    // table and accepted only on exact agreement, so a prefix that spans genuinely different entities
    // (scientist -> young 294x192 vs old 310x202) stays out.
    private readonly Dictionary<string, (double W, double H, double Cov)> _variantBase;

    private static Dictionary<string, (double W, double H, double Cov)> BuildVariantBases(
        Dictionary<string, (double W, double H, double Cov)> natbox)
    {
        var groups = new Dictionary<string, List<(double W, double H, double Cov)>>(StringComparer.Ordinal);
        foreach (var (key, box) in natbox)
        {
            if (!key.EndsWith("_base", StringComparison.Ordinal) || box.W <= 0 || box.H <= 0) continue;
            string stem = key.Substring(0, key.Length - 5);
            int cut = stem.LastIndexOf('_');
            if (cut <= 0 || stem.Substring(0, cut).IndexOf('_') < 0) continue;
            string prefix = stem.Substring(0, cut);
            if (!groups.TryGetValue(prefix, out var l)) groups[prefix] = l = new();
            l.Add(box);
        }
        var ok = new Dictionary<string, (double W, double H, double Cov)>(StringComparer.Ordinal);
        foreach (var (prefix, l) in groups)
            if (l.All(b => b.W == l[0].W && b.H == l[0].H && b.Cov == l[0].Cov)) ok[prefix] = l[0];
        return ok;
    }

    /// <summary>Load the TP2 tables. Null when the tree isn't a usable install (no graphics /
    /// imagemaps), so the caller keeps drawing crosses. Never throws on absent optional files.</summary>
    public static Tp2SpriteResolver? TryLoad(Tp2AssetsRoot root)
    {
        if (root is null || !root.IsValid) return null;
        var cells = AssetTables.ReadImageMaps(Path.Combine(root.GraphicsDir, "imagemaps.xml"));
        var animPath = root.FindTable("animationdefs.xml");
        var anims = File.Exists(animPath) ? AssetTables.ReadAnimations(animPath) : new Dictionary<string, Anim>();
        var entPath = root.FindTable("entityintersections.xml");
        var natbox = File.Exists(entPath) ? AssetTables.ReadNatBox(entPath) : new Dictionary<string, (double, double, double)>();
        return new Tp2SpriteResolver(root, cells, anims, natbox);
    }

    /// <summary>The engine's own assembled art box for an entity, or null when the def table has no
    /// entry. The keys the table actually uses are the def name and its catalog base, but a level
    /// places <c>tyrannosaurus_dino1</c> / <c>croc_egypt1</c>, so the lookup walks the same name
    /// spellings the art resolution does: the raw name, its species key, and those with the level-set
    /// suffix trimmed (<see cref="SkelSuffix"/>); then each through the inverse of
    /// <see cref="SkelAliases"/> (the stem <c>ala</c> belongs to <c>alamosaurus</c>) and through
    /// <see cref="ArtEntity"/> (a borrowed rig is measured by its owner's box).</summary>
    public (double W, double H, double Cov)? NatBox(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string species = EntityName.SpeciesKey(name);
        string trimmed = SkelSuffix.Replace(species, "");
        // An armed variant is measured by its own def, not the plain animal's: `ant_future1` is the
        // Laser Ant, whose shipped coverage counts the gun the dino ant's rig does not draw.
        if (TypeVariant.TryGetValue(name, out var varWord)
            && SkeletalStem(trimmed.Length > 0 ? trimmed : name) is string vlast
            && Hit(vlast + "_" + varWord, out var vbox)) return vbox;
        foreach (var key in new[] { name, species, trimmed })
        {
            if (Hit(key, out var nb)) return nb;
            if (SkelAliasInverse.TryGetValue(key, out var owner) && Hit(owner, out nb)) return nb;
            if (ArtEntity.TryGetValue(key, out var art) && Hit(art, out nb)) return nb;
            if (ArtDef.TryGetValue(key, out var def) && Hit(def, out nb)) return nb;
        }
        // The type was renamed to another entity's animation stem (alamosaurus -> ala): go back to the
        // stem's owner, which is the def that carries the box.
        if (SkelAliases.TryGetValue(trimmed, out var stem)
            && SkelAliasInverse.TryGetValue(stem, out var own) && Hit(own, out var box)) return box;
        // A decorated type name (lion_rome1, hovercar_float1, shuttlecraft_fight1, mouse_japan1) is
        // measured by the def of whatever name it resolves its ART to, so the same right-to-left trim
        // and the same alias tables have to find the box too. Without this the coverage silently falls
        // back to 1.0 and the viewport draws the entity at the wrong world size -- the divisor is only
        // as good as the lookup that reaches the def.
        foreach (var cand in TrimChain(trimmed).Concat(new[] { SkeletalStem(name) }))
        {
            if (cand is null) continue;
            if (Hit(cand, out var nb)) return nb;
            if (Aliases.TryGetValue(cand, out var art) && Hit(art, out nb)) return nb;
            if (SkelAliases.TryGetValue(cand, out var rig)
                && (Hit(rig, out nb)
                    || SkelAliasInverse.TryGetValue(rig, out var owner) && Hit(owner, out nb))) return nb;
        }
        // Last resort: the def exists only as a variant family (`<name>_<variant>_base`), all agreeing.
        foreach (var key in new[] { name, species, trimmed })
            if (_variantBase.TryGetValue(key, out var vb)) return vb;
        return null;
    }

    // (0,0,0) is the "several defs share this shorthand" marker from ReadNatBox: not an answer.
    private bool Hit(string key, out (double W, double H, double Cov) nb) =>
        _natbox.TryGetValue(key, out nb) && nb.W > 0 && nb.H > 0;

    /// <summary>The shipped <c>coverage</c> of an entity's own def: the opaque fraction of its
    /// natwidth x natheight box (1.0 when the entity has no def, so the scale falls back to the plain
    /// box area). Tasty Planet sizes a sprite so the material you SEE equals the record's area, which is
    /// why the table carries this at all -- the sibling rule is proven in Tasty Planet 1
    /// (<c>physics_scale = sqrt(minarea / (spriteW*spriteH*areacoverage))</c>), and a sparse rig (ant 0.217,
    /// roman_man 0.281) otherwise renders far smaller than the mass the level author gave it.</summary>
    public double EntityCoverage(string name) => NatBox(name) is (double, double, double cov) ? cov : 1.0;

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

        // then the same bases with their trailing set/tier fields dropped, longest first
        foreach (var b in bases.ToList())
            foreach (var t in TrimChain(b))
                if (!bases.Contains(t)) bases.Add(t);

        foreach (var b0 in bases)
        {
            // A trimmed name can still be spelled differently from its art (vacuum -> vaccum), so each
            // candidate goes through the alias table before it is looked up.
            var b = Aliases.TryGetValue(b0, out var alias) ? alias : b0;
            foreach (var cand in new[] { b, Numword(b) })
            {
                if (_cells.TryGetValue(cand, out var c)) return c;
                if (_cellNorm.TryGetValue(EntityName.Norm(cand), out var hit)) return _cells[hit];
            }
        }

        foreach (var b in bases)
        {
            string q = EntityName.Norm(Numword(Aliases.TryGetValue(b, out var al) ? al : b));
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

        var cpFromCanon = CompositePlan(name, canon, rgba);
        if (cpFromCanon is not null) return cpFromCanon;

        // Skeletal characters (ala, cobra, cow, rat, modern_sciyoung, ...): several `<canon>_stand_*`
        // / `_walk_*` animdef parts assembled onto a shared canvas. Runs before the single-cell
        // cascade, which otherwise drops every part but one.
        var skel = TryBuildSkeletal(canon, rgba, frameI);
        if (skel is not null) return skel;

        var baseCell = BaseCell(name, frameI);
        if (baseCell is null) return null;

        // A decorated placed type (hovercar_float1 / _future1 / _stationary1) carries no alias of its
        // own, so `canon` misses the Composites key and the check above is skipped. Its resolved base
        // cell, though, IS the composite head (hovercar_body), so re-key the composite on the base
        // cell here — otherwise the shared-canvas overlay parts (the windows) are silently dropped
        // and every hovercar in a shipped level renders as a body with no glass.
        var cpFromBase = CompositePlan(name, baseCell.Name, rgba);
        if (cpFromBase is not null) return cpFromBase;

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

    private sealed record SkeletalPart(Anim A, Frame F, Cell C);

    /// <summary>Build a shared-canvas composite plan when <paramref name="key"/> names a
    /// <see cref="Composites"/> head: overlay every part cell (bottom-first) and tint the result by
    /// the record colour. Returns null when the key is not a composite or none of its parts resolve,
    /// so the caller falls through to the single-cell cascade. Called with the canonical placed name
    /// first and, when that misses, with the resolved base cell name (see <see cref="BuildPlan"/>).</summary>
    private Tp2SpritePlan? CompositePlan(string name, string key, byte[] rgba)
    {
        if (!Composites.TryGetValue(key, out var parts)) return null;
        var cp = new Tp2SpritePlan { Pivot = Pivot(name) };
        // Tinted body parts first, then the whole-canvas entity recolour, then the un-tinted overlays.
        // A Tint op multiplies only the canvas accumulated so far, so a Layer laid after it keeps its
        // own art colour -- the hovercar glass stays clear while the chassis takes the record tint.
        foreach (var p in parts)
            if (_cells.TryGetValue(p, out var pc)) cp.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Layer, Cell = Geo(pc), CellName = pc.Name });
        AddTintIfColourful(cp, rgba);
        if (CompositeOverlays.TryGetValue(key, out var overlays))
            foreach (var p in overlays)
                if (_cells.TryGetValue(p, out var oc)) cp.Ops.Add(new Tp2PlanOp { Kind = Tp2OpKind.Layer, Cell = Geo(oc), CellName = oc.Name });
        // A composite must resolve at least one Layer; a lone Tint op is not a sprite.
        foreach (var op in cp.Ops) if (op.Kind == Tp2OpKind.Layer) return cp;
        return null;
    }

    /// <summary>The art stem a placed entity name uses for its skeletal parts, or null. A level never
    /// spells the stem: it writes the full species plus a level set (<c>tyrannosaurus_dino1</c>,
    /// <c>maiasaura_dino2</c>, <c>egypt_soldier_egypt1</c>) while animationdefs groups the parts under
    /// an abbreviated stem (<c>tyran</c>, <c>maia</c>). Candidates, in order: the name as-is; the same
    /// name with a trailing pose tail dropped (a few levels place a character's own part name,
    /// <c>egypt_noble_walk_arm_upper</c>); and each of those with the level-set suffix trimmed and
    /// passed through <see cref="SkelAliases"/>. A candidate is accepted only if it owns at least two
    /// parts, so an unrelated prefix hit falls through instead of hijacking the lookup.</summary>
    private string? SkeletalStem(string canon)
    {
        string asis = SkelPose.Replace(canon, "");
        foreach (var base0 in new[] { canon, asis })
        {
            string stem = SkelSuffix.Replace(base0, "");
            // The name as written, then with the level-set suffix dropped, then with its trailing
            // set/tier fields cut one at a time (lion_rome1 -> lion_rome -> lion, simon_labfarattack
            // -> simon, roman_glad_rome1 -> roman_glad).
            foreach (var cand in new[] { base0, stem }.Concat(TrimChain(stem)))
            {
                foreach (var s in new[] { cand, SkelAliases.TryGetValue(cand, out var a) ? a : cand })
                    if (CountParts(s) >= 2) return s;
                // A rider with no pose tokens: each part is one animdef (`hoverguy_board`,
                // `hoverguy_head`, ...) tied together only by their shared origin.
                if (PartGroups.ContainsKey(cand)) return cand;
                // Nothing of its own: the engine registered this type onto another entity's rig, so
                // borrow that entity's stem (watusi -> cow -> the cow stand/walk group).
                if (ArtEntity.TryGetValue(cand, out var art))
                {
                    string astr = SkelAliases.TryGetValue(art, out var x) ? x : art;
                    if (CountParts(astr) >= 2) return astr;
                }
            }
        }
        return null;
    }

    // A stem qualifies if it owns >=2 parts in either pose: bacteria / nanobot / ufo are walk-only,
    // so counting stand alone would reject them before TryBuildSkeletal ever reaches its walk fallback.
    private int CountParts(string stem) =>
        _anims.Keys.Count(k => k.StartsWith(stem + "_stand_", StringComparison.Ordinal)
                            || k.StartsWith(stem + "_walk_", StringComparison.Ordinal));

    /// <summary>Assemble a skeletal character (multi-part entity) into a plan, or null when
    /// <paramref name="canon"/> isn't one (fewer than two resolvable stand/walk parts). Mirrors
    /// <c>tp2assets.compose_skeletal</c>: the stand pose is authoritative, but any limb stand never
    /// defines is taken from the walk pose (roman_man's stand group is body/head/2 feet only, so it
    /// assembled armless and legless), parts are ordered by animdef priority, and the canvas is the
    /// animdefs' own <c>extentx/y</c> envelope unioned with the parts' transformed art rects. Rotation
    /// is carried on the op for the App blit and enters the bound maths as a plain cos/sin of the
    /// animdef's degree value — the same expression on both sides of the oracle, with an outward
    /// epsilon guard so a libm difference of an ULP cannot move a canvas edge a whole pixel.</summary>
    private Tp2SpritePlan? TryBuildSkeletal(string canon, byte[] rgba, int frameI)
    {
        var stem = SkeletalStem(canon);
        if (stem is null) return null;

        // A pose-less group (hoverguy) names its parts directly; there is no stand/walk split to merge.
        List<string> standKeys, walkKeys, keys;
        if (PartGroups.TryGetValue(stem, out var group))
        {
            standKeys = group.Where(k => _anims.ContainsKey(k)).ToList();
            walkKeys = new List<string>();
            keys = standKeys;
        }
        else
        {
            // An accessory part belongs to the character's armed variant, not to the character: the
            // dino ant must be drawn without the Laser Ant's gun. Which slots are accessories is read
            // off the def table (see VariantSlot), and which types carry them off TypeVariant.
            string? variant = TypeVariant.TryGetValue(canon, out var v) ? v : null;
            bool Owns(string k) { var s = VariantSlot(stem, k); return s is null || s == variant; }
            standKeys = _anims.Keys.Where(k => k.StartsWith(stem + "_stand_", StringComparison.Ordinal)
                                            && Owns(k)).ToList();
            walkKeys = _anims.Keys.Where(k => k.StartsWith(stem + "_walk_", StringComparison.Ordinal)
                                           && Owns(k)).ToList();
            keys = standKeys.Count >= 2 ? standKeys : walkKeys;
        }
        if (keys.Count < 2) return null;

        // Stand pose wins, but a limb it omits entirely belongs to the character: the shipped stand
        // groups for the humanoid/cow/frog rigs leave out whole chains (roman_man has no arm, hand,
        // leg or toga def under _stand_ at all, so its assembly rendered a torso on two feet).
        // A limb's identity is its def name minus the pose token and a trailing phase digit, so walk's
        // foot_left_1 / foot_left_2 collapse onto stand's foot_left and a complete rig (ala, croc, rat,
        // ant, cobra, lion, shrew, aphid, snail, simon, worm) gains nothing -- no double-drawn legs.
        if (standKeys.Count >= 2 && !PartGroups.ContainsKey(stem))
        {
            var have = new HashSet<string>(standKeys.Select(k => LimbSlot(k, stem, "stand")), StringComparer.Ordinal);
            foreach (var grp in walkKeys.GroupBy(k => LimbSlot(k, stem, "walk"))
                                          .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (have.Contains(grp.Key)) continue;
                // One def per limb: the first (name-ordered, so identical on both sides of the oracle)
                // whose frame cell actually resolves -- walk's _1/_2 twins are the same limb in two
                // swing phases and one of the pair is usually the placeholder cell 0null.
                foreach (var k in grp.OrderBy(x => x, StringComparer.Ordinal))
                    if (HasCell(k, frameI)) { keys.Add(k); have.Add(grp.Key); break; }
            }
        }

        var parts = new List<SkeletalPart>();
        foreach (var k in keys)
        {
            var an = _anims[k];
            if (an.Frames.Count == 0) continue;
            var fr = an.Frames[frameI % an.Frames.Count];
            if (_cells.TryGetValue(fr.Cell, out var c)) parts.Add(new SkeletalPart(an, fr, c));
        }
        if (parts.Count < 2) return null;

        parts = parts.OrderBy(p => p.A.Priority).ThenBy(p => p.A.Name, StringComparer.Ordinal).ToList();

        var plan = new Tp2SpritePlan { IsSkeletal = true };
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;

        // The engine sizes a character by its animdefs' own extent rectangles, and that envelope is what
        // entityintersections natwidth/natheight was measured from (union of a stem's stand+walk extents:
        // ala 390x66 vs nat 390x68, tyrannosaurus 394x103 vs 394x106, cow 246x126 vs 246x130, roman_man
        // 210x156 vs 210x156, frog 394x181 vs 394x182). Re-deriving the box from the art rects alone
        // under-measures a rig whose animated limbs only reach the envelope in their other phases, so the
        // sprite came out too small in world units (scale tracks the box area). Extents are absolute
        // entity-origin pixels with the origin at (0,0), so they anchor the canvas where the game anchors it.
        foreach (var k in standKeys.Concat(walkKeys))
        {
            var an = _anims[k];
            if (!an.HasExtents) continue;
            minX = Math.Min(minX, Math.Min(an.ExtX1, an.ExtX2)); maxX = Math.Max(maxX, Math.Max(an.ExtX1, an.ExtX2));
            minY = Math.Min(minY, Math.Min(an.ExtY1, an.ExtY2)); maxY = Math.Max(maxY, Math.Max(an.ExtY1, an.ExtY2));
        }

        // Then each part's OPAQUE ART rect, transformed by its own frame -- never its orig canvas. The
        // canvas carries the transparent trim margins the atlas packing removed (a cow's 11x126 horn
        // lives in a 270x147 canvas). Unioning it as well guarantees no ink is clipped even when a def
        // has no extents or the animation carries a scale/offset the envelope was not measured with.
        foreach (var p in parts)
        {
            plan.Ops.Add(new Tp2PlanOp
            {
                Kind = Tp2OpKind.Layer, Cell = Geo(p.C), CellName = p.C.Name,
                OffX = p.F.OffX, OffY = p.F.OffY, AngleDeg = p.F.Angle, Scale = p.F.Scale,
                FlipX = p.F.FlipX, FlipY = p.F.FlipY,
                PartCx = p.A.Cx, PartCy = p.A.Cy, PartOpacity = p.F.Opacity, Priority = p.A.Priority,
            });

            double sc = p.F.Scale <= 0 ? 1.0 : p.F.Scale;
            double pw = p.C.OrigW, ph = p.C.OrigH;
            double pcx = p.A.Cx * pw, pcy = p.A.Cy * ph;
            double ax0 = p.C.CropX, ax1 = p.C.CropX + (p.C.X2 - p.C.X1);
            double ay0 = p.C.CropY, ay1 = p.C.CropY + (p.C.Y2 - p.C.Y1);
            // Fitted against the engine's own per-part boxes, not guessed. Every animdef declares
            // extentx1/x2, extenty1/y2 = the rectangle that part really draws; for a single-frame def that
            // box IS the transformed art rect, so the transform can be solved for. Over the 220
            // single-frame parts of 31 characters, the centre error against the declared box is
            // median 1.1 / p90 1.8 / max 5.5 px here, versus median 1.2 / p90 29.5 / max 62.5 px for the
            // obvious reading (angle as authored, flip mirroring the rect about the pivot). Two things
            // differ: the rotation runs the other way (the engine's angle is counter-clockwise on screen,
            // i.e. clockwise in this y-down space), and a flip negates the ROTATED offset vector instead
            // of mirroring the source rect -- the part is laid down and then folded across the horizontal
            // line through its own offset, which is what moved the mirrored right-side limbs 30-60px off
            // the body. Same rule as BlitPart; it reproduces 213/220 declared boxes to within 3px.
            double rad = -p.F.Angle * Math.PI / 180.0, co = Math.Cos(rad), si = Math.Sin(rad);
            foreach (var (cx, cy) in new[] { (ax0, ay0), (ax1, ay0), (ax0, ay1), (ax1, ay1) })
            {
                double ux = (cx - pcx) * sc, uy = (cy - pcy) * sc;
                double rx = ux * co - uy * si, ry = ux * si + uy * co;
                if (p.F.FlipX) rx = -rx;
                if (p.F.FlipY) ry = -ry;
                double X = p.F.OffX + rx, Y = p.F.OffY + ry;
                if (X < minX) minX = X;
                if (X > maxX) maxX = X;
                if (Y < minY) minY = Y;
                if (Y > maxY) maxY = Y;
            }
        }

        // 2px of slack for the nearest-neighbour inverse map. The 1e-6 outward guard makes floor/ceil
        // agree with the Python oracle even where the two libms' cos() differ by an ULP: a difference
        // far below the guard cannot land the two sides on opposite sides of an integer.
        const double Pad = 2, Eps = 1e-6;
        int left = (int)Math.Floor(minX - Pad - Eps), top = (int)Math.Floor(minY - Pad - Eps);
        int right = (int)Math.Ceiling(maxX + Pad + Eps), bottom = (int)Math.Ceiling(maxY + Pad + Eps);
        plan.CanvasW = right - left;
        plan.CanvasH = bottom - top;
        plan.OriginX = -left;
        plan.OriginY = -top;
        plan.Pivot = (plan.CanvasW > 0 ? plan.OriginX / plan.CanvasW : 0.5,
                      plan.CanvasH > 0 ? plan.OriginY / plan.CanvasH : 0.5);

        AddTintIfColourful(plan, rgba);
        plan.AlphaScale = rgba is { Length: 4 } ? rgba[3] : (byte)255;
        return plan;
    }

    /// <summary>The accessory word a part key belongs to, or null when the part is simply a limb. A slot
    /// is an accessory when the shipped def table carries the character plus that word as its own
    /// entity (<c>ant_laser_base</c> next to <c>ant_base</c>), which is the engine saying "this is a
    /// different entity's rig, not another leg". Derived from the tables rather than written out: across
    /// all 1242 animdefs exactly three match, <c>ant</c>/<c>rat</c>/<c>simon</c> slot <c>laser</c>, so no
    /// other character loses a part. Mirrors <c>tp2assets._variant_slot</c>.</summary>
    private string? VariantSlot(string stem, string key)
    {
        string rest = key[(stem.Length + 1)..];                       // `stand_laser`, `walk_arm_lower_left_1`
        int i = rest.IndexOf('_');
        if (i <= 0 || i + 1 >= rest.Length) return null;
        string slot = PhaseTail.Replace(rest[(i + 1)..], "");
        return _natbox.ContainsKey(stem + "_" + slot + "_base") ? slot : null;
    }

    /// <summary>A part's identity within a stem: its animdef name with the pose token and a trailing
    /// swing-phase digit removed, so <c>roman_man_walk_arm_lower_left_2</c> and
    /// <c>roman_man_walk_arm_lower_left_1</c> are the same limb and <c>cow_stand_leg_left</c> is the same
    /// limb as <c>cow_walk_leg_left</c>. Mirrors <c>tp2assets._limb_slot</c>.</summary>
    private static string LimbSlot(string key, string stem, string pose) =>
        PhaseTail.Replace(key.Substring(stem.Length + 1 + pose.Length + 1), "");
    private static readonly System.Text.RegularExpressions.Regex PhaseTail =
        new("_\\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when the animdef's frame for <paramref name="frameI"/> names a cell that exists
    /// (placeholder parts carry <c>cellname="0null"</c>, which the engine reserves space for but never
    /// draws). Mirrors <c>tp2assets._has_cell</c>.</summary>
    private bool HasCell(string key, int frameI)
    {
        if (!_anims.TryGetValue(key, out var an) || an.Frames.Count == 0) return false;
        return _cells.ContainsKey(an.Frames[frameI % an.Frames.Count].Cell);
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
        if (p.IsSkeletal)
        {
            foreach (var op in p.Ops)
            {
                if (sb.Length > 0) sb.Append(',');
                if (op.Kind == Tp2OpKind.Tint)
                { sb.Append("T:").Append(Hex(op.Tint!.Value)); continue; }
                sb.Append(op.CellName).Append('@')
                  .Append(Num(op.OffX)).Append(',').Append(Num(op.OffY)).Append(',')
                  .Append(Num(op.AngleDeg)).Append(',').Append(Num(op.Scale)).Append(',')
                  .Append(op.Priority.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(" C").Append(p.CanvasW.ToString(CultureInfo.InvariantCulture))
              .Append(',').Append(p.CanvasH.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
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
        }
        sb.Append(" A").Append(p.AlphaScale.ToString(CultureInfo.InvariantCulture));
        sb.Append(" P").Append(Num(p.Pivot.X)).Append(',').Append(Num(p.Pivot.Y));
        return sb.ToString();
    }

    // Mirrors _tp2_sig.fmt: invariant up-to-3-decimals, trailing zeros/dot dropped, "-0" -> "0".
    // Formats the double DIRECTLY ("F3") instead of Math.Round(x,3): .NET Core and CPython both round
    // the value's exact binary expansion half-to-even when formatting, whereas Math.Round(double,int)
    // scales by 10^3 first, so a pivot like 199/400 (0.4975, stored just under the midpoint) came out
    // 0.498 here against 0.497 in Python.
    private static string Num(double x)
    {
        var s = x.ToString("F3", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return s is "" or "-0" ? "0" : s;
    }

    private static string Hex((byte R, byte G, byte B) t) => $"{t.R:x2}{t.G:x2}{t.B:x2}";
}
