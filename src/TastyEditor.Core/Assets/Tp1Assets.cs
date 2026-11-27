using System.Globalization;
using System.IO;
using TastyEditor.Core.Levels;

namespace TastyEditor.Core.Assets;

/// <summary>Where a Tasty Planet 1 install keeps its art. Unlike the four binary families (a
/// single opaque <c>built/graphics</c> or TP2's <c>graphics</c>), TP1 ships four loose table XMLs
/// (<c>entitydefs.xml</c> / <c>imagemaps.xml</c> / <c>tiledefs.xml</c> / <c>animationdefs.xml</c>)
/// and the PNGs scattered under <c>assets/graphics</c> plus its <c>newgraphics</c> /
/// <c>oldgraphics</c> / <c>EA</c> sub-folders — none of the tables are valid XML (unquoted values,
/// mixed quoting), so <see cref="Tp1Codec.ParseTree"/> tokenises them leniently. Levels live under
/// <c>assets/levels</c>; this walks up from a level to the shared <c>assets</c> segment.</summary>
public sealed class Tp1GraphicsRoot
{
    public required string GraphicsDir { get; init; }
    public required string AssetsDir { get; init; }

    /// <summary>A usable TP1 art tree: the graphics dir exists and carries the entity table.</summary>
    public bool IsValid =>
        Directory.Exists(GraphicsDir) &&
        (File.Exists(Path.Combine(GraphicsDir, "entitydefs.xml")) ||
         File.Exists(Path.Combine(GraphicsDir, "imagemaps.xml")));

    /// <summary>First existing <paramref name="fileName"/> under graphics, then the assets root and
    /// the process cwd (some installs keep the tables one level up).</summary>
    public string? FindTable(string fileName)
    {
        foreach (var d in new[] { GraphicsDir, AssetsDir, Directory.GetCurrentDirectory() })
        {
            var p = Path.Combine(d, fileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Resolve the art root from a level path (walk up to the <c>assets</c> segment;
    /// graphics is its <c>graphics</c> child). <paramref name="overrideRoot"/> points straight at a
    /// graphics folder when supplied.</summary>
    public static Tp1GraphicsRoot? ForLevel(string? levelPath, string? overrideRoot = null)
    {
        if (overrideRoot is not null && Directory.Exists(overrideRoot))
            return new Tp1GraphicsRoot
            {
                GraphicsDir = overrideRoot,
                AssetsDir = Directory.GetParent(overrideRoot)?.FullName ?? overrideRoot,
            };

        if (string.IsNullOrWhiteSpace(levelPath)) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(levelPath));
        string? assets = null;
        for (var d = new DirectoryInfo(dir ?? "."); d is not null; d = d.Parent)
            if (string.Equals(d.Name, "assets", StringComparison.OrdinalIgnoreCase)) { assets = d.FullName; break; }
        if (assets is null) return null;
        return new Tp1GraphicsRoot
        {
            GraphicsDir = Path.Combine(assets, "graphics"),
            AssetsDir = assets,
        };
    }

    /// <summary>The art root of the detected Tasty Planet install (its <c>assets\graphics</c>), when
    /// one is found on this machine. Used as a fallback so a level opened from a loose copy — the base
    /// editor's own <c>levels</c> folder, an extracted backup — still draws real sprites instead of
    /// crosses. Null when no install is present. Folder-presence detection only; nothing is launched
    /// or written.</summary>
    public static Tp1GraphicsRoot? ForInstall()
    {
        var inst = GameInstall.InstallDir("tp1");
        if (inst is null) return null;
        var gfx = Path.Combine(inst, "assets", "graphics");
        return Directory.Exists(gfx) ? ForLevel(null, gfx) : null;
    }
}

/// <summary>One image sub-rectangle of a TP1 atlas: the crop inside the PNG plus the original
/// (pre-pack) canvas size and paste offset, expressed through the shared
/// <see cref="ResolvedSprite"/> shape the WPF crop already understands. <c>Atlas</c> is the
/// <c>filename</c> the <c>imagemap</c> names (no extension).</summary>
public sealed record Tp1Cell(string Atlas, int X1, int Y1, int X2, int Y2,
    int CropX, int CropY, int OrigW, int OrigH);

/// <summary>UI-free port of the base editor's <c>asset_manager</c> + <c>parser</c> asset cascade.
/// Loads the four TP1 tables, indexes every <c>imagemap</c>'s cells (named <c>&lt;cell&gt;</c>,
/// <c>&lt;cellgrid&gt;</c> / <c>&lt;cellgridpadded&gt;</c> and <c>&lt;fullimagecell&gt;</c>), and
/// resolves a level entitydef / tiledef name to a <see cref="Tp1Cell"/> through the same route the
/// game's renderer takes: entity → its <c>animationdef</c>'s first frame (imagemap + cell) else its
/// direct <c>imagemap</c> / <c>cell</c>. It never touches pixels, so it is testable headless.</summary>
public sealed class Tp1SpriteResolver
{
    private sealed class MapInfo
    {
        public string Filename = "";
        public bool FullImage;
        public int Width, Height;
        public readonly List<Tp1Cell> ByIndex = new();
        public readonly Dictionary<string, Tp1Cell> ByName = new(StringComparer.Ordinal);
    }

    private sealed class AnimDef
    {
        public string Imagemap = "";
        public double Time;                 // animationdef total duration (0 => fall back to the weight sum)
        public bool Looping = true;
        public readonly List<(string? Map, string? Cell, string? CellName)> Frames = new();
        public readonly List<double> Weights = new();   // per-frame time weight, parallel to Frames
    }

    private readonly Tp1GraphicsRoot _root;
    private readonly Dictionary<string, MapInfo> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimDef> _anims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Map, string? Cell, string? Anim, double Aspect, double Coverage)> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Map, string? Cell, string? Anim)> _tiles = new(StringComparer.Ordinal);
    // The entitydef's <c>effectr/g/b/a</c> tint (greygoo ships 0.5,0.5,0.5 = the gray the game paints
    // the goo body with). The base editor's get_tk_image leaves tint at 1,1,1, so this is a TastyEdit
    // addition that makes the goo read as gray rather than the pure-white raw cell art.
    private readonly Dictionary<string, (double R, double G, double B, double A)> _effects = new(StringComparer.Ordinal);

    private Tp1SpriteResolver(Tp1GraphicsRoot root) => _root = root;

    public Tp1GraphicsRoot Root => _root;

    /// <summary>Load the TP1 tables from one art root. Null when the tree is not a usable install,
    /// so the caller keeps drawing crosses. Never throws on absent / optional files.</summary>
    public static Tp1SpriteResolver? TryLoad(Tp1GraphicsRoot? root)
    {
        if (root is null || !root.IsValid) return null;
        var r = new Tp1SpriteResolver(root);
        var imap = root.FindTable("imagemaps.xml");
        if (imap is not null) r.ReadImageMaps(imap);
        var apath = root.FindTable("animationdefs.xml");
        if (apath is not null) r.ReadAnimations(apath);
        var epath = root.FindTable("entitydefs.xml");
        if (epath is not null) r.ReadEntityDefs(epath);
        var tpath = root.FindTable("tiledefs.xml");
        if (tpath is not null) r.ReadTileDefs(tpath);
        return r._maps.Count > 0 || r._entities.Count > 0 ? r : null;
    }

    // ---- sprite-name surface (the inspector's dropdown + the asset browser) ----
    public IReadOnlyCollection<string> CellNames
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in _entities.Keys) set.Add(k);
            foreach (var k in _tiles.Keys) set.Add(k);
            foreach (var m in _maps.Values) foreach (var n in m.ByName.Keys) set.Add(n);
            return set;
        }
    }
    public IReadOnlyCollection<string> AnimNames => _anims.Keys;
    public int CellCount => _entities.Count + _tiles.Count;
    public int AnimCount => _anims.Count;
    public bool HasAnimations => _anims.Count > 0;

    /// <summary>The <c>entitydefs.xml</c> name set — the policing authority for a New emitter's
    /// entitydef and for reassigning an emitter's type (an unknown name crashes the game).</summary>
    public IReadOnlyCollection<string> EntityNames => _entities.Keys;

    /// <summary>The <c>tiledefs.xml</c> name set — the valid pictures for a background tile, offered
    /// in the inspector's tile-name dropdown (the paint list).</summary>
    public IReadOnlyCollection<string> TileNames => _tiles.Keys;

    // ---- the four table readers ----

    private static Tp1Level Load(string path) => Tp1Codec.ParseTree(File.ReadAllBytes(path));

    private void ReadImageMaps(string path)
    {
        foreach (var im in Load(path).Root.Kids("imagemap"))
        {
            string name = im.Get("name") ?? im.Get("id") ?? "";
            string file = im.Get("filename") ?? name;
            if (name.Length == 0 || file.Length == 0) continue;
            var m = new MapInfo
            {
                Filename = file,
                Width = (int)(im.Num("width") ?? 0),
                Height = (int)(im.Num("height") ?? 0),
            };
            foreach (var kid in im.Children)
            {
                switch (kid.Tag)
                {
                    case "fullimagecell":
                        m.FullImage = true;
                        break;
                    case "cell":
                    {
                        var c = ParseCell(file, kid, m.Width, m.Height);
                        var nm = kid.Get("name");
                        if (nm is { Length: > 0 }) m.ByName[nm] = c;
                        m.ByIndex.Add(c);
                        break;
                    }
                    case "cellgrid":
                        AddGrid(m, kid, spacing: 0);
                        break;
                    case "cellgridpadded":
                        AddGrid(m, kid, spacing: 1);
                        break;
                }
            }
            // A map with no cells / grid at all is the whole image.
            if (m.ByIndex.Count == 0 && m.ByName.Count == 0) m.FullImage = true;
            _maps[name] = m;
        }
    }

    private static Tp1Cell ParseCell(string atlas, Tp1Node e, int mapW, int mapH)
    {
        int Gi(string k, int d = 0) => (int)(e.Num(k) ?? d);
        int x1 = Gi("x1", Gi("x"));
        int y1 = Gi("y1", Gi("y"));
        int w = Gi("w"), h = Gi("h");
        int x2 = Gi("x2", x1 + w);
        int y2 = Gi("y2", y1 + h);
        if (x2 <= x1) { x2 = x1 + (w > 0 ? w : 256); }
        if (y2 <= y1) { y2 = y1 + (h > 0 ? h : 256); }
        return new Tp1Cell(atlas, x1, y1, x2, y2, Gi("cropoffsetx"), Gi("cropoffsety"),
            Gi("origwidth", x2 - x1), Gi("origheight", y2 - y1));
    }

    // Mirror asset_manager.process_map_grid_and_cells: a uniform grid of cells across the map.
    private static void AddGrid(MapInfo m, Tp1Node g, int spacing)
    {
        int gw = (int)(g.Num("width") ?? 0), gh = (int)(g.Num("height") ?? 0);
        if (gw <= 0 || gh <= 0 || m.Width <= 0 || m.Height <= 0) return;
        int cols, rows;
        if (spacing > 0)
        {
            cols = (m.Width + spacing) / (gw + spacing);
            rows = (m.Height + spacing) / (gh + spacing);
        }
        else { cols = m.Width / gw; rows = m.Height / gh; }
        if (cols <= 0 || rows <= 0) return;
        int count = (int)(g.Num("number") ?? 0);
        if (count <= 0 || count > cols * rows) count = cols * rows;
        for (int i = 0; i < count; i++)
        {
            int col = i % cols, row = i / cols;
            int x = spacing > 0 ? col * (gw + spacing) : col * gw;
            int y = spacing > 0 ? row * (gh + spacing) : row * gh;
            m.ByIndex.Add(new Tp1Cell(m.Filename, x, y, x + gw, y + gh, 0, 0, gw, gh));
        }
    }

    private void ReadAnimations(string path)
    {
        foreach (var a in Load(path).Root.Kids("animationdef"))
        {
            string name = a.Get("name") ?? a.Get("id") ?? "";
            if (name.Length == 0) continue;
            var anim = new AnimDef
            {
                Imagemap = a.Get("imagemap") ?? "",
                Time = a.Num("time") ?? 0.0,
                Looping = a.Get("looping") is not ("false" or "0") ,   // defaults true, as the base editor
            };
            foreach (var f in a.Kids("frame"))
            {
                anim.Frames.Add((f.Get("imagemap") ?? anim.Imagemap, f.Get("cell"), f.Get("cellname")));
                anim.Weights.Add(f.Num("time") ?? 1.0);
            }
            _anims[name] = anim;
        }
    }

    private void ReadEntityDefs(string path)
    {
        foreach (var e in Load(path).Root.Kids("entitydef"))
        {
            string name = e.Get("name") ?? e.Get("id") ?? "";
            if (name.Length == 0) continue;
            _entities[name] = (e.Get("imagemap"), e.Get("cell") ?? e.Get("imagemapcell"),
                e.Get("animationdef"),
                e.Num("aspectratio") ?? 1.0, e.Num("areacoverage") ?? 1.0);
            _effects[name] = (e.Num("effectr") ?? 1.0, e.Num("effectg") ?? 1.0,
                e.Num("effectb") ?? 1.0, e.Num("effecta") ?? 1.0);
        }
    }

    private void ReadTileDefs(string path)
    {
        foreach (var t in Load(path).Root.Kids("tiledef"))
        {
            string name = t.Get("name") ?? t.Get("id") ?? "";
            if (name.Length == 0) continue;
            _tiles[name] = (t.Get("imagemap"), t.Get("cell") ?? t.Get("imagemapcell"), t.Get("animationdef"));
        }
    }

    // ---- name -> cell resolution ----

    /// <summary>Resolve a level entity's spawn name (an <c>entitydefs.xml</c> name) to its first
    /// sprite. Null when no def / image matches — the caller draws a cross.</summary>
    public Tp1Cell? ResolveEntity(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_entities.TryGetValue(name, out var d)) return Follow(name, d.Map, d.Cell, d.Anim);
        return Follow(name, null, null, name);   // a raw animation / map / cell name also resolves
    }

    /// <summary>Resolve a background <c>&lt;tile name=…/&gt;</c> sprite name to its cell.</summary>
    public Tp1Cell? ResolveTile(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_tiles.TryGetValue(name, out var t)) return Follow(name, t.Map, t.Cell, t.Anim);
        return Follow(name, null, null, name);
    }

    private Tp1Cell? Follow(string name, string? map, string? cell, string? anim)
    {
        // Frame 0 of the animation, taken through the same new-mode redirect the base editor applies
        // ("goo" -> "newgoo", whose frames live as bare cellnames on newgoo0). GetAnimationFrameCell
        // already searches every map for a frame's cellname when the frame names no imagemap, so this
        // is what makes the goo body resolve to the 200px newgoo0 blob (matching the editor's face
        // layout) instead of the 256px old-graphics goo1 cell.
        if (anim is { Length: > 0 })
        {
            var c = GetAnimationFrameCell(anim, 0.0);
            if (c is not null) return c;
        }
        return Cell(map, cell);
    }

    private Tp1Cell? Cell(string? mapName, string? cellRef)
    {
        if (mapName is { Length: > 0 } && _maps.TryGetValue(mapName, out var m))
        {
            if (m.FullImage || string.IsNullOrEmpty(cellRef))
                return new Tp1Cell(m.Filename, 0, 0, Dim(m.Width), Dim(m.Height), 0, 0, Dim(m.Width), Dim(m.Height));
            if (cellRef is not null && m.ByName.TryGetValue(cellRef, out var byName)) return byName;
            if (int.TryParse(cellRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                && i >= 0 && i < m.ByIndex.Count) return m.ByIndex[i];
            if (m.ByIndex.Count > 0) return m.ByIndex[0];
            return new Tp1Cell(m.Filename, 0, 0, Dim(m.Width), Dim(m.Height), 0, 0, Dim(m.Width), Dim(m.Height));
        }
        return null;
    }

    private static int Dim(int v) => v > 0 ? v : 256;

    // Port of asset_manager.get_animation_frame: pick the frame current at `elapsed` by weight, then
    // resolve its cell. A frame may name an imagemap or rely on the anim's; when neither carries the
    // cell (the goo eyelid frames only give a bare cellname) the base editor searches every map, so
    // FindCellByName reproduces that. Mirrors the new-mode redirect ("eyelid" -> "neweyelid" when it
    // exists) the editor applies before lookup.
    public Tp1Cell? GetAnimationFrameCell(string animName, double elapsed)
    {
        if (string.IsNullOrEmpty(animName)) return null;
        if (!animName.StartsWith("new", StringComparison.Ordinal) && _anims.ContainsKey("new" + animName))
            animName = "new" + animName;
        if (!_anims.TryGetValue(animName, out var a) || a.Frames.Count == 0) return null;
        double totalW = 0; foreach (var wt in a.Weights) totalW += wt; if (totalW <= 0) totalW = 1;
        double totalDur = a.Time > 0 ? a.Time : totalW;
        double t = a.Looping ? PosMod(elapsed, totalDur) : Math.Min(elapsed, totalDur - 0.001);
        if (t < 0) t = 0;
        double target = t * totalW / totalDur;
        double acc = 0;
        for (int i = 0; i < a.Frames.Count; i++)
        {
            double dur = i < a.Weights.Count ? a.Weights[i] : 1.0;
            if (acc + dur >= target) return FrameCell(a, i);
            acc += dur;
        }
        return FrameCell(a, a.Frames.Count - 1);
    }

    private Tp1Cell? FrameCell(AnimDef a, int i)
    {
        var (map, cell, cellName) = a.Frames[i];
        string? reference = !string.IsNullOrEmpty(cell) ? cell : cellName;
        if (!string.IsNullOrEmpty(map) && _maps.ContainsKey(map))
        {
            var c = Cell(map, reference);
            if (c is not null) return c;
        }
        return !string.IsNullOrEmpty(reference) ? FindCellByName(reference!) : null;
    }

    private Tp1Cell? FindCellByName(string cellName)
    {
        foreach (var m in _maps.Values)
            if (m.ByName.TryGetValue(cellName, out var c)) return c;
        return null;
    }

    private static double PosMod(double x, double m) { if (m <= 0) return 0; double r = x % m; return r < 0 ? r + m : r; }

    /// <summary>Resolve one named cell inside a specific <c>imagemap</c> (the goo face parts live as
    /// named cells on the consolidated <c>newgoo0</c> sheet — <c>gooeyes</c> / <c>goopupils</c> — and
    /// are not reachable through an entity/tile name). Null when the map or cell is absent.</summary>
    public Tp1Cell? ResolveCell(string mapName, string cellName) =>
        !string.IsNullOrEmpty(mapName) && !string.IsNullOrEmpty(cellName)
        && _maps.TryGetValue(mapName, out var m) && m.ByName.TryGetValue(cellName, out var c) ? c : null;

    /// <summary>The entitydef's <c>areacoverage</c> (the fraction of its sprite box its mass fills —
    /// greygoo ships 0.49), used to scale the goo the way the base editor's physics_scale does.
    /// 1.0 when the name is unknown, so a missing def never blows up the divide.</summary>
    public double EntityCoverage(string name) =>
        !string.IsNullOrEmpty(name) && _entities.TryGetValue(name, out var d) && d.Coverage > 0 ? d.Coverage : 1.0;

    /// <summary>The entitydef's <c>effectr/g/b/a</c> as an RGBA tint buffer, or null when it is a no-op
    /// (all 1.0) or the name is unknown. Feeds the sprite source's per-pixel RGB multiply, so the goo
    /// body paints in its authored gray instead of the raw white cell art.</summary>
    public byte[]? EntityTint(string name)
    {
        if (string.IsNullOrEmpty(name) || !_effects.TryGetValue(name, out var e)) return null;
        static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
        byte r = B(e.R), g = B(e.G), b = B(e.B), a = B(e.A);
        return r == 255 && g == 255 && b == 255 && a == 255 ? null : new[] { r, g, b, a };
    }

    /// <summary>True when an entitydef name exists in <c>entitydefs.xml</c> — the New-emitter /
    /// entitydef-edit policing gate. The special "Player" goo is accepted too.</summary>
    public bool IsKnownEntity(string name) =>
        string.Equals(name, "Player", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrEmpty(name) && _entities.ContainsKey(name));

    /// <summary>Every valid entitydef name, for the New-emitter dropdown.</summary>
    public IEnumerable<string> AllEntityNames() => _entities.Keys.OrderBy(k => k, StringComparer.Ordinal);
}
