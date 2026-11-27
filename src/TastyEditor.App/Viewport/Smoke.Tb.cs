using System.IO;
using TastyEditor.App.Assets;
using TastyEditor.Core.Assets;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

/// <summary>Headless visual + coverage proof for the Tasty Blue skeleton-sprite slice. Nothing here
/// launches the game: it drives the same resolver and pixel path the viewport uses and writes PNGs an
/// agent can look at.</summary>
public static partial class Smoke
{
    /// <summary>Dev-only verification of the TB rig compositing. Three reports in one run:
    /// (1) the skeleton table parsed and how many rigs / shapes it carries,
    /// (2) entity-name coverage over the install's stock levels through the real
    ///     <see cref="SpriteSource.TryGet"/> tiers (direct cell / rig composite / engine art / stem / fuzzy / none),
    /// (3) a labelled contact sheet of one composite per rig, <c>_tb_rig_sheet.png</c>, plus
    ///     <c>_tb_rig_&lt;name&gt;.png</c> per rig, so the placement and layer order can be inspected
    ///     against the Python oracle sheet. Read-only; touches no level bytes.</summary>
    public static void TbSkelDump()
    {
        using var log = new StreamWriter("_tbskel.txt", append: false) { AutoFlush = true };
        void Emit(string s) { Console.WriteLine(s); log.WriteLine(s); }

        var (game, dir) = Defaults.First(d => d.Game == "tb");
        var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
        var root = AssetsRoot.ForLevel(levels[0]);
        var res = root is null ? null : SpriteResolver.TryLoad(root);
        if (res is null) { Emit("FAIL: resolver null"); return; }
        if (res.Rigs is null) { Emit($"FAIL: skeletons.bin did not parse ({res.RigError})"); return; }

        int bones = 0, shapes = 0;
        foreach (var skeleton in res.Rigs.Values)
            foreach (var bn in skeleton.Walk())
            { bones++; shapes += bn.Shapes.Length; }
        Emit($"tb skeletons: rigs={res.RigCount} bones={bones} shapes={shapes} cells={res.CellCount} anims={res.AnimCount}");
        Emit($"tb engine art: {res.EngineArtCount} entries");
        DumpEngineArt(res);

        // (2) every distinct entity TYPE on the stock levels, through the render entry point.
        var names = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var lvl in levels)
        {
            List<SceneCross> ms;
            try { ms = LevelLoader.Load(lvl).Scene.Crosses
                .Where(c => c.Kind == SceneKind.Entity && c.SpriteName is { Length: > 0 }).ToList(); }
            catch { continue; }
            foreach (var c in ms) names[c.SpriteName!] = names.GetValueOrDefault(c.SpriteName!) + 1;
        }

        var ss = new SpriteSource(res);
        var white = new byte[] { 255, 255, 255, 255 };
        int direct = 0, rig = 0, engine = 0, stem = 0, none = 0;
        int recsDirect = 0, recsRig = 0, recsEngine = 0, recsStem = 0, recsNone = 0;
        var perRig = new Dictionary<string, string>(StringComparer.Ordinal);
        var misses = new List<string>();
        var engineUsed = new List<string>();
        var engineVsDirect = new List<string>();
        var rigOverCell = new List<string>();
        var resolve = new List<string> { "name,records,tier,art,w,h,ink" };
        foreach (var (name, recs) in names)
        {
            // Same tier order SpriteSource.TryBuildNamed paints with: a multi-part rig outranks the cell
            // tables, because its cells are pieces of one sheet rather than the whole creature, and the
            // engine's own registration answer outranks a cell that merely spells like the TYPE.
            bool hasRig = res.TryResolveRig(name, out var plan);
            bool rigFirst = hasRig && plan!.Parts.Count > 1;
            string engineArt = string.Empty;
            bool hasEngine = !rigFirst
                && res.TryGetEngineArt(name, out engineArt) && engineArt != name
                && (res.TryResolveRig(engineArt, out _) || res.TryResolveDirectCell(engineArt, 0, out _)
                    || res.TryResolveFallbackCell(engineArt, 0, out _));
            bool hasDirect = !rigFirst && !hasEngine && res.TryResolveDirectCell(name, 0, out _);
            bool hasFallback = !hasDirect && !rigFirst && !hasEngine && res.TryResolveFallbackCell(name, 0, out _);
            if (rigFirst)
            {
                rig++; recsRig += recs; perRig.TryAdd(plan!.Rig, name);
                if (res.TryResolveDirectCell(name, 0, out _)) rigOverCell.Add(name);
            }
            else if (hasEngine)
            {
                engine++; recsEngine += recs;
                if (engineUsed.Count < 40) engineUsed.Add($"{name}->{engineArt}");
            }
            else if (hasDirect) { direct++; recsDirect += recs; }
            else if (hasFallback) { stem++; recsStem += recs; }
            else { none++; recsNone += recs; if (misses.Count < 40) misses.Add(name); }

            // the picture the viewport would actually paint
            if (!ss.TryGet(name, 0, white, out var built, out int w, out int h, out _, out _, out _))
                Emit($"  BUILD FAIL {name} (direct={hasDirect} rig={hasRig} engine={hasEngine} fallback={hasFallback})");
            else if (rigFirst && w != plan!.W)
                Emit($"  SIZE DRIFT {name}: built {w}x{h} plan {plan.W}x{plan.H}");

            // One row per TYPE with the pixels it produces, so a tier change can be diffed as an actual
            // image difference rather than a coverage number. `ink` is a cheap checksum over BGRA.
            string tier = rigFirst ? "rig" : hasEngine ? "engine" : hasDirect ? "direct" : hasFallback ? "stem" : "none";
            string artOf = rigFirst ? plan!.Rig : hasEngine ? engineArt : "";
            resolve.Add($"{name},{recs},{tier},{artOf},{w},{h},{Ink(built)}");

            // Where the executable's own answer and a coincidental cell-name match would paint different
            // pixels. The engine table wins in SpriteSource, so this is the list of deliberate overrides.
            if (!rigFirst && res.TryResolveDirectCell(name, 0, out var byCell)
                && res.TryGetEngineArt(name, out var engArt) && res.TryResolveDirectCell(engArt, 0, out var byEngine)
                && byCell != byEngine)
                engineVsDirect.Add($"{name}->{engArt}");
        }
        int total = names.Count, totalRecs = names.Values.Sum();
        Emit($"coverage: {total} distinct names / {totalRecs} records");
        Emit($"  rig composite {rig,3} names {recsRig,6} records");
        Emit($"  engine art    {engine,3} names {recsEngine,6} records: {string.Join(", ", engineUsed)}");
        Emit($"  direct cell  {direct,4} names {recsDirect,6} records");
        Emit($"  stem/fuzzy   {stem,4} names {recsStem,6} records");
        Emit($"  no art       {none,4} names {recsNone,6} records: {string.Join(", ", misses)}");
        Emit($"  entity marks drawing art: {recsDirect + recsRig + recsEngine + recsStem}/{totalRecs}");
        Emit($"  rigs that outrank a cell by name: {rigOverCell.Count}"
             + (rigOverCell.Count == 0 ? "" : ": " + string.Join(", ", rigOverCell.Take(20))));
        Emit($"  engine art that overrides a direct cell: {engineVsDirect.Count}"
             + (engineVsDirect.Count == 0 ? "" : ": " + string.Join(", ", engineVsDirect.Take(30))));
        File.WriteAllLines("_tb_resolve_cs.csv", resolve);

        // (3) one composite per rig, so the layering can be eyeballed against the Python sheet.
        var items = new List<(string Cap, System.Windows.Media.Imaging.BitmapSource Img)>();
        foreach (var rigName in res.Rigs.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            // Drive it through a real level name when one exists; otherwise the bare rig name still
            // finds the rig through the prefix match.
            string probe = perRig.TryGetValue(rigName, out var real) ? real : rigName + "_x";
            if (!res.TryResolveRig(probe, out var plan)) { Emit($"  {rigName}: no plan"); continue; }
            if (!ss.TryGet(probe, 0, white, out var img, out _, out _, out _, out _, out _))
            { Emit($"  {probe}: NO SPRITE (plan {plan.W}x{plan.H}, {plan.Parts.Count} parts)"); continue; }

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            using (var fs = File.Create($"_tb_rig_{rigName}.png")) enc.Save(fs);
            Emit($"  {rigName,-20} {probe,-30} canvas={plan.W}x{plan.H} parts={plan.Parts.Count} " +
                 $"pivot=({plan.PivotX:0.###},{plan.PivotY:0.###}) order=[{string.Join(",", plan.Parts.Select(p => p.N))}]");
            items.Add(($"{rigName}  {plan.Parts.Count}p  {plan.W}x{plan.H}", img));
        }

        const int CW = 150, CH = 150, CAP = 14, COLS = 8;
        int rows = (items.Count + COLS - 1) / COLS;
        double W = COLS * (CW + 4) + 4, H = rows * (CH + CAP + 4) + 4;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 8, 12)),
                             null, new System.Windows.Rect(0, 0, W, H));
            var typeface = new System.Windows.Media.Typeface("Segoe UI");
            for (int i = 0; i < items.Count; i++)
            {
                double x = 4 + (i % COLS) * (CW + 4), y = 4 + (i / COLS) * (CH + CAP + 4);
                var im = items[i].Img;
                double s = Math.Min((CW - 6.0) / im.PixelWidth, (CH - 6.0) / im.PixelHeight);
                double dw = im.PixelWidth * s, dh = im.PixelHeight * s;
                dc.DrawImage(im, new System.Windows.Rect(x + (CW - dw) / 2, y + (CH - dh) / 2, dw, dh));
                dc.DrawText(new System.Windows.Media.FormattedText(items[i].Cap, System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight, typeface, 10, System.Windows.Media.Brushes.Khaki, 1.0),
                    new System.Windows.Point(x + 2, y + CH));
            }
        }
        var sheet = new System.Windows.Media.Imaging.RenderTargetBitmap((int)W, (int)H, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        sheet.Render(dv);
        var sheetEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        sheetEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sheet));
        using (var fs = File.Create("_tb_rig_sheet.png")) sheetEnc.Save(fs);
        Emit($"sheet: {items.Count} tiles -> _tb_rig_sheet.png");

        // (4) the names that used to draw nothing, plus the names whose art the table overrides, now that
        // the executable's own registration table is in.
        Emit("engine-tier renders:");
        foreach (var probe in TbFormerlyMissing.Concat(TbEngineOverrides))
        {
            string art = res.TryGetEngineArt(probe, out var a) ? a : "-";
            bool ok = ss.TryGet(probe, 0, white, out var img, out int w, out int h, out _, out _, out _);
            if (ok)
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                using var fs = File.Create($"_tb_art_{probe}.png");
                enc.Save(fs);
            }
            Emit($"  {probe,-28} art={art,-16} built={ok} {(ok ? $"{w}x{h} -> _tb_art_{probe}.png" : "")}");
        }

        Emit(none == 0 && recsNone == 0 ? "TB RIG GREEN" : "TB RIG: some names still have no art");
    }

    /// <summary>The eight entity TYPEs that rendered as crosses after the rig slice shipped. Each one has a
    /// level record, no image-map cell or rig under its own spelling, and no stem or fuzzy hit — they are
    /// what the executable's registration table exists to answer.</summary>
    private static readonly string[] TbFormerlyMissing =
    {
        "barnacle2_base", "barnacle4_base", "innercore1_base", "moon_base", "outercore1_base",
        "seaweed_normal", "torpedo_torpedo1", "woodencratesquare_normal",
    };

    /// <summary>The two TYPEs whose spelling is also a cell name, so the direct-cell tier used to answer
    /// with it and the engine's registration never got a say. <c>crust</c> and <c>crust2</c> are horizon
    /// bands; the records name <c>rock_middle1</c> / <c>rock_middle2</c>, the rock chunks these levels
    /// scatter about 100 of. Rendered so the override can be looked at rather than argued about.</summary>
    private static readonly string[] TbEngineOverrides = { "crust1_base", "crust2_base" };

    /// <summary>A cheap identity for a built sprite: every 16th BGRA byte folded into one sum. Two runs
    /// that agree on it painted the same pixels; a change means the sprite really moved.</summary>
    private static long Ink(System.Windows.Media.Imaging.BitmapSource img)
    {
        if (img.PixelWidth == 0 || img.PixelHeight == 0) return 0;
        int stride = img.PixelWidth * 4;
        var px = new byte[stride * img.PixelHeight];
        img.CopyPixels(px, stride, 0);
        long sum = 0;
        for (int i = 0; i < px.Length; i += 16) sum = (sum * 31 + px[i]) & 0x7FFFFFFFFFFFFFFF;
        return sum;
    }

    /// <summary>Write the scanned table so it can be diffed against the Python extractor's
    /// <c>tb_engine_art.json</c> — the two must agree name for name or one of them is reading the binary
    /// wrong. Skips silently when there is no table (no exe in the install).</summary>
    private static void DumpEngineArt(SpriteResolver res)
    {
        var tbl = res.EngineArt;
        if (tbl is not { Count: > 0 }) return;
        try
        {
            var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in tbl) ordered[k] = v;
            File.WriteAllText("_tb_engine_art_cs.json",
                System.Text.Json.JsonSerializer.Serialize(ordered, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (Exception ex) { Console.WriteLine($"  engine dump failed: {ex.GetType().Name}"); }
    }
}
