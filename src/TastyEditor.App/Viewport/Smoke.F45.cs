using System.IO;
using TastyEditor.App.Assets;
using TastyEditor.Core.Assets;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

/// <summary>Headless coverage proof for the Forever / Final Bite engine-art port. Like
/// <see cref="TbSkelDump"/> it drives the exact tiers <see cref="SpriteSource.TryBuildNamed"/> paints
/// with, but over the two installs whose entity heads carry a per-level theme token that is not a cell
/// key: it reports the scanned EngineArt table size, the rig-name fallback count, and per-tier
/// entity/deco resolution over a sample of stock levels, so the X to sprite improvement is a number.
/// Read-only; touches no level bytes.</summary>
public static partial class Smoke
{
    public static void F45Probe()
    {
        using var log = new StreamWriter("_f45_probe.txt", append: false) { AutoFlush = true };
        void Emit(string s) { Console.WriteLine(s); log.WriteLine(s); }

        const int levelCap = 10;
        foreach (var game in new[] { "f4", "f5" })
        {
            var (g, dir) = Defaults.First(d => d.Game == game);
            if (!Directory.Exists(dir)) { Emit($"{game}: SKIP (no levels dir)"); continue; }
            var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
            if (levels.Length == 0) { Emit($"{game}: SKIP (no levels)"); continue; }

            var root = AssetsRoot.ForLevel(levels[0]);
            var res = root is null ? null : SpriteResolver.TryLoad(root);
            if (res is null) { Emit($"{game}: FAIL resolver null"); continue; }

            var skelPath = Path.Combine(res.Root.EditableDir, "skeletons.bin");
            int anchorRigs = File.Exists(skelPath) ? TbSkeletons.ScanRigNames(File.ReadAllBytes(skelPath)).Count : 0;
            Emit($"== {g} == cells={res.CellCount} rigs(parsed)={(res.Rigs?.Count ?? -1)} " +
                 $"rigs(anchor-scan)={anchorRigs} engineArt={res.EngineArtCount} rigError={res.RigError ?? "-"} dipBase={res.DipBaseCount}");

            var ss = new SpriteSource(res);
            var white = new byte[] { 255, 255, 255, 255 };
            foreach (var kind in new[] { SceneKind.Entity, SceneKind.Deco })
            {
                var names = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var lvl in levels.Take(levelCap))
                {
                    List<SceneCross> ms;
                    try { ms = LevelLoader.Load(lvl).Scene.Crosses
                        .Where(c => c.Kind == kind && c.SpriteName is { Length: > 0 }).ToList(); }
                    catch { continue; }
                    foreach (var c in ms) names[c.SpriteName!] = names.GetValueOrDefault(c.SpriteName!) + 1;
                }

                int rig = 0, engine = 0, direct = 0, stem = 0, none = 0;
                int recsRig = 0, recsEngine = 0, recsDirect = 0, recsStem = 0, recsNone = 0;
                var misses = new List<string>();
                var engineUsed = new List<string>();
                foreach (var (name, recs) in names)
                {
                    bool hasRig = res.TryResolveRig(name, out var plan);
                    bool rigFirst = hasRig && plan!.Parts.Count > 1;
                    string engineArt = string.Empty;
                    bool hasEngine = !rigFirst
                        && res.TryGetEngineArt(name, out engineArt) && engineArt != name
                        && (res.TryResolveRig(engineArt, out _) || res.TryResolveDirectCell(engineArt, 0, out _)
                            || res.TryResolveFallbackCell(engineArt, 0, out _));
                    bool hasDirect = !rigFirst && !hasEngine && res.TryResolveDirectCell(name, 0, out _);
                    bool hasFallback = !hasDirect && !rigFirst && !hasEngine && res.TryResolveFallbackCell(name, 0, out _);
                    // the picture the viewport would actually paint is the ground truth
                    bool built = ss.TryGet(name, 0, white, out _, out _, out _, out _, out _, out _);
                    if (rigFirst) { rig++; recsRig += recs; }
                    else if (hasEngine) { engine++; recsEngine += recs; if (engineUsed.Count < 15) engineUsed.Add($"{name}->{engineArt}"); }
                    else if (hasDirect) { direct++; recsDirect += recs; }
                    else if (hasFallback) { stem++; recsStem += recs; }
                    else { none++; recsNone += recs; if (misses.Count < 15) misses.Add(name); }
                    if (!built && (rigFirst || hasEngine || hasDirect || hasFallback))
                        Emit($"  BUILD FAIL {name} (rig={rigFirst} engine={hasEngine} direct={hasDirect} stem={hasFallback})");
                }
                int totalRecs = names.Values.Sum();
                int hit = recsRig + recsEngine + recsDirect + recsStem;
                Emit($"  {kind,-7}: {names.Count} names / {totalRecs} records -> art {hit}/{totalRecs} " +
                     $"({(totalRecs == 0 ? 0 : 100.0 * hit / totalRecs):F0}%)  " +
                     $"[rig {rig} engine {engine} direct {direct} stem {stem} none {none}]");
                if (engineUsed.Count > 0) Emit($"    engine used: {string.Join(", ", engineUsed)}");
                if (misses.Count > 0) Emit($"    still X: {string.Join(", ", misses)}");
            }
        }
        Emit("");
        Emit("F45 PROBE DONE");
    }

    /// <summary>Dev-only visual proof for the Final Bite affine rig compositor. Loads the f5 install and
    /// renders every multi-part rig whose plan took the affine path (a shape that rotates or mirrors, i.e.
    /// one the crop model used to collapse onto another) to <c>_f5_rig_&lt;name&gt;.png</c> and a labelled
    /// contact sheet <c>_f5_rig_sheet.png</c>. Bilateral limbs that share one sheet cell must now land on
    /// opposite sides rather than stack. Read-only; touches no level bytes.</summary>
    public static void F5RigDump(string? filter = null)
    {
        using var log = new StreamWriter("_f5rig.txt", append: false) { AutoFlush = true };
        void Emit(string s) { Console.WriteLine(s); log.WriteLine(s); }

        var (_, dir) = Defaults.First(d => d.Game == "f5");
        var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
        if (levels.Length == 0) { Emit("FAIL: no f5 levels to anchor the asset root"); return; }
        var root = AssetsRoot.ForLevel(levels[0]);
        var res = root is null ? null : SpriteResolver.TryLoad(root);
        if (res?.Rigs is null) { Emit($"FAIL: no f5 skeletons ({res?.RigError ?? "no root"})"); return; }
        if (!res.RigsAreFinalBite) { Emit("FAIL: f5 table did not parse as Final Bite dialect"); return; }

        var ss = new SpriteSource(res);
        var white = new byte[] { 255, 255, 255, 255 };
        var items = new List<(string Cap, System.Windows.Media.Imaging.BitmapSource Img)>();
        int affineRigs = 0, missing = 0;
        foreach (var rigName in res.Rigs.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!res.TryResolveRig(rigName + "_x", out var plan)) continue;
            // With a name filter, render whatever path that rig takes (crop or affine) for targeted
            // eyeballing. Without one, show the rigs the compositor actually moved: the colliding ones, i.e.
            // any rig with a rotated or mirrored part. Plain crop rigs keep the historical paste and are
            // exercised by tbskel / sprites.
            bool fanoutPath = plan.Fanout || plan.Parts.Any(p => !p.M.IsTranslateOnly);
            if (filter is null ? !fanoutPath : !rigName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ss.TryGet(rigName + "_x", 0, white, out var img, out _, out _, out _, out _, out _))
            { Emit($"  {rigName}: NO SPRITE (plan {plan.W}x{plan.H}, {plan.Parts.Count} parts)"); missing++; continue; }
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            using (var fs = File.Create($"_f5_rig_{rigName}.png")) enc.Save(fs);
            int nAffine = plan.Parts.Count(p => !p.M.IsTranslateOnly);
            Emit($"  {rigName,-24} canvas={plan.W}x{plan.H} parts={plan.Parts.Count} affine={nAffine} "
                + $"base={plan.BaseScale:0.###} out={img.PixelWidth}x{img.PixelHeight} "
                + $"sfr=[{string.Join(",", plan.Parts.Select(p => p.Sfr.ToString("0.###")).Distinct())}]");
            items.Add(($"{rigName} {plan.Parts.Count}p {plan.W}x{plan.H}", img));
            affineRigs++;
        }
        Emit($"\nf5 affine rigs rendered: {affineRigs}");

        const int CW = 160, CH = 160, CAP = 14, COLS = 8;
        int rowsN = (items.Count + COLS - 1) / COLS;
        double W = COLS * (CW + 4) + 4, H = rowsN * (CH + CAP + 4) + 4;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.DarkSlateGray, null, new System.Windows.Rect(0, 0, W, H));
            for (int i = 0; i < items.Count; i++)
            {
                int cx = 4 + (i % COLS) * (CW + 4), cy = 4 + (i / COLS) * (CH + CAP + 4);
                var sc = Math.Min((double)CW / items[i].Img.PixelWidth, (double)CH / items[i].Img.PixelHeight);
                double iw = items[i].Img.PixelWidth * sc, ih = items[i].Img.PixelHeight * sc;
                dc.DrawImage(items[i].Img, new System.Windows.Rect(cx + (CW - iw) / 2, cy + (CH - ih) / 2, iw, ih));
                var ft = new System.Windows.Media.FormattedText(items[i].Cap,
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight, new System.Windows.Media.Typeface("Segoe UI"),
                    10, System.Windows.Media.Brushes.White, 1.0);
                dc.DrawText(ft, new System.Windows.Point(cx, cy + CH));
            }
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)W, (int)H, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        var sheet = new System.Windows.Media.Imaging.PngBitmapEncoder();
        sheet.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create("_f5_rig_sheet.png")) sheet.Save(fs);
        Emit($"sheet: {items.Count} tiles -> _f5_rig_sheet.png");
        // With a name filter, success is simply that the requested rigs rendered. Without one, the check is
        // that every rig the affine path owns came out as a sprite: a rig that plans parts but paints none is
        // the compositor placing them outside the canvas it sized, which is how a creature ends up clipped to
        // a fragment of one limb.
        Emit(filter is not null
            ? (items.Count > 0 ? "F5 RIG GREEN" : "F5 RIG: filter matched no renderable rig")
            : (missing == 0
                ? $"F5 RIG GREEN (affine compositor live; {affineRigs} rigs assembled from the skeleton)"
                : $"F5 RIG FAIL: {missing} of {affineRigs + missing} rigs planned parts but painted none"));
    }
}
