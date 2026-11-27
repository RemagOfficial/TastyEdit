using System.IO;
using System.Text;
using TastyEditor.App.Assets;
using TastyEditor.Core.Assets;
using TastyEditor.Core.Levels;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

/// <summary>Headless verification of the open pipeline (detect → decode → project)
/// without a window, so the projection can be checked on build machines / by an agent
/// that can't see the GUI. `smoke [path...]` prints one line per level; with no paths it
/// sweeps a representative stock level of each game. This is display-only — it never
/// touches the byte-exact save path.</summary>
public static partial class Smoke
{
    // The stock level folders every gate sweeps. Resolved from the machine's own Steam libraries
    // (or TPE_LEVELS_<GAME>) instead of a fixed path, so the gates run on any install; an
    // unresolved family keeps an empty directory, which every gate loop skips, and says so.
    private static readonly string[] Games = { "f5", "f4", "tb", "tp2" };
    private static readonly (string Game, string Dir)[] Defaults = ResolveDefaults();

    private static (string Game, string Dir)[] ResolveDefaults()
    {
        var resolved = new (string Game, string Dir)[Games.Length];
        var missing = new List<string>();
        for (int i = 0; i < Games.Length; i++)
        {
            var dir = GameInstall.LevelsDir(Games[i]);
            resolved[i] = (Games[i], dir ?? "");
            if (dir is null) missing.Add(GameInstall.MissingHint(Games[i]));
        }
        foreach (var note in missing) Console.WriteLine("NOTE " + note);
        return resolved;
    }

    private static string LevelsDirFor(string game) => Defaults.First(d => d.Game == game).Dir;

    /// <summary>Joins onto a resolved levels folder, tolerating the empty string that stands for
    /// "this game isn't installed here" (the caller's Directory.Exists guard then skips it).</summary>
    private static string Sub(string dir, string child) =>
        dir.Length == 0 ? "" : Path.Combine(dir, child);

    public static void Run(string[]? paths)
    {
        // A WinExe is detached from the launching console, so Console output can be
        // discarded; mirror every line into a file so a headless check is readable.
        using var log = new StreamWriter("_smoke.txt", append: false);
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        if (paths is { Length: > 0 })
        {
            foreach (var p in paths) Report(p, Emit);
            return;
        }
        foreach (var (_, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"{dir}: MISSING DIR"); continue; }
            var first = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).FirstOrDefault();
            if (first is not null) Report(first, Emit);
        }
    }

    private static void Report(string path, Action<string> emit)
    {
        try
        {
            var doc = LevelLoader.Load(path);
            var s = doc.Scene;
            var b = s.Bounds;
            string bound = b.IsEmpty ? "empty" : $"{b.Left:F0},{b.Top:F0}..{b.Right:F0},{b.Bottom:F0}";
            emit(
                $"{doc.Profile.Short,-10} {Path.GetFileName(path),-28} " +
                $"poly={s.Polys.Count,-4} line={s.Lines.Count,-4} cross={s.Crosses.Count,-4} " +
                $"kinds=[{Kinds(s)}] bounds=({bound})");
        }
        catch (Exception ex)
        {
            emit($"ERROR {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Headless proof of the slice-3 save path. For each level: (a) an untouched
    /// encode must be byte-identical to disk and re-decode stable (a live I1 in the exact
    /// code the GUI Save uses), then (b) a +1 x nudge of the first movable entity must
    /// change only a few bytes with the size preserved and the edited file still re-decoding
    /// to itself (the in-app echo of the golden I3 edit-localisation proof). Writes only to
    /// a temp file, never the source.</summary>
    public static void SaveCheck(string[]? paths)
    {
        using var log = new StreamWriter("_savecheck.txt", append: false);
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        var targets = new List<string>();
        if (paths is { Length: > 0 }) targets.AddRange(paths);
        else foreach (var (_, dir) in Defaults)
            if (Directory.Exists(dir))
            { var f = Directory.GetFiles(dir, "*.bin").OrderBy(x => x).FirstOrDefault(); if (f is not null) targets.Add(f); }

        string tmp = Path.Combine(Path.GetTempPath(), "tpe_savecheck.bin");
        int pass = 0, fail = 0;
        try
        {
            foreach (var path in targets)
            {
                try
                {
                    var doc = LevelLoader.Load(path);
                    var rep0 = doc.SaveTo(tmp);
                    bool i1 = rep0 is { Changed: false, Stable: true };
                    Emit($"{(i1 ? "OK  " : "FAIL")} {doc.Profile.Short,-10} {Path.GetFileName(path),-26} " +
                         $"untouched: {rep0.Summarize()}");

                    var cr = doc.Scene.Crosses.FirstOrDefault(c => SceneProjector.IsMovable(c.Source));
                    if (cr is null) { Emit($"     (no movable entity — raw-span game; untouched pass only)"); Count(i1); continue; }

                    var before = SceneProjector.GetPos(cr.Source)!.Value;
                    SceneProjector.TrySetPos(cr.Source, before.X + 1, before.Y);
                    var rep1 = doc.SaveTo(tmp);
                    bool i3 = rep1 is { SizePreserved: true, Stable: true } && rep1.BytesChanged is > 0 and <= 16;
                    Emit($"{(i3 ? "OK  " : "FAIL")} {doc.Profile.Short,-10} {Path.GetFileName(path),-26} " +
                         $"+1 entity-x: {rep1.Summarize()}");
                    Count(i1 && i3);
                }
                catch (Exception ex) { fail++; Emit($"FAIL {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}"); }

                void Count(bool ok) { if (ok) pass++; else fail++; }
            }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }

        Emit("");
        Emit($"SAVE CHECK: {pass} pass, {fail} fail");
        Emit(fail == 0 ? "SAVE PATH GREEN" : "SAVE PATH FAILURES");
    }

    /// <summary>Headless proof of the slice-D4.4 polygon (wall/area) edit path. For each
    /// level that owns a field-editable polygon: (a) an untouched encode is byte-identical
    /// to disk; (b) nudging one existing vertex (count preserved) changes only a handful of
    /// bytes, keeps the size, re-decodes to itself, and — where the record carries a byte
    /// span (tb) — stays wholly inside that one record; (c) moving the whole shape's position
    /// does the same. Mirrors the Python _test_area_gui localisation assertion. Never writes.</summary>
    public static void PolyCheck(string[]? paths)
    {
        using var log = new StreamWriter("_polycheck.txt", append: false);
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        var targets = new List<string>();
        if (paths is { Length: > 0 }) targets.AddRange(paths);
        else foreach (var (_, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) continue;
            // One representative stock level per game (a level with no editable polygon
            // simply skips). Polygon editing is proven headlessly against the repo's own
            // levels\*.bin samples, passed explicitly — a full-corpus hunt is not cheap.
            var f = Directory.GetFiles(dir, "*.bin").OrderBy(x => x).FirstOrDefault();
            if (f is not null) targets.Add(f);
        }

        int pass = 0, fail = 0, skipped = 0;
        foreach (var path in targets)
        {
            try
            {
                var doc = LevelLoader.Load(path);
                var poly = doc.Scene.Polys.FirstOrDefault(p => SceneProjector.IsEditablePoly(p.Source, p.ShapeIndex));
                if (poly is null) { skipped++; continue; }

                byte[] baseBytes = doc.Encode();
                bool untouched = baseBytes.AsSpan().SequenceEqual(doc.Original);

                // (b) reshape: move one existing vertex to a new world point.
                var p0 = SceneProjector.PolyWorldPoints(poly.Source, poly.ShapeIndex);
                SceneProjector.SetPolyVertexWorld(poly.Source, poly.ShapeIndex, 0, new PointD(p0[0].X + 120, p0[0].Y - 90));
                var vert = Check(doc, baseBytes, "vertex");

                // (c) move: fresh load (same file → same first editable record), translate
                // the whole record's position.
                var doc2 = LevelLoader.Load(path);
                var poly2 = doc2.Scene.Polys.First(p => SceneProjector.IsEditablePoly(p.Source, p.ShapeIndex));
                var base2 = doc2.Encode();
                SceneProjector.MoveRecord(poly2.Source, 50, -25);
                var move = Check(doc2, base2, "move   ");

                bool ok = untouched && vert.Ok && move.Ok;
                Emit($"{(ok ? "OK  " : "FAIL")} {doc.Profile.Short,-4} {Path.GetFileName(path),-26} {doc.Scene.Polys.Count(p => SceneProjector.IsEditablePoly(p.Source, p.ShapeIndex))} editable poly | " +
                     $"untouched={untouched} | {vert.Msg} | {move.Msg}");
                if (ok) pass++; else fail++;
            }
            catch (Exception ex) { fail++; Emit($"FAIL {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"POLY CHECK: {pass} pass, {fail} fail ({skipped} levels with no editable polygon skipped)");
        Emit(fail == 0 ? "POLY EDIT GREEN" : "POLY EDIT FAILURES");
    }

    private static (bool Ok, string Msg) Check(LevelDoc doc, byte[] baseline, string what)
    {
        byte[] edited = doc.Encode();
        var rep = SaveReport.Compare(baseline, edited);
        var re = doc.Profile.Codec.Encode(doc.Profile.Decode(edited));
        bool stable = re.Length == edited.Length && re.AsSpan().SequenceEqual(edited);
        var (off, len) = SceneProjector.RecordSpan(FindEditedSpanSource(doc, what));
        bool within = off < 0 || (rep.FirstDiff >= off && rep.LastDiff < off + len);
        bool ok = rep.SizePreserved && rep.BytesChanged is > 0 and <= 40 && stable && within;
        string span = off < 0 ? "span n/a" : $"span[{off}..{off + len}) {(within ? "held" : "LEAKED")}";
        return (ok, $"{what.Trim()}: {rep.BytesChanged}b @{rep.FirstDiff}..{rep.LastDiff}, size {(rep.SizePreserved ? "same" : "CHANGED")}, {span}, stable={stable}");
    }

    // The record whose span we check is the first editable poly's source (all edits target it).
    private static object? FindEditedSpanSource(LevelDoc doc, string _) =>
        doc.Scene.Polys.FirstOrDefault(p => SceneProjector.IsEditablePoly(p.Source, p.ShapeIndex))?.Source;

    /// <summary>Headless proof of the water-rectangle edit path. For each level that owns a
    /// water box: untouched encode is byte-identical; moving the centre and dragging a corner
    /// to resize each change only that record's bytes (size preserved, re-decode stable, diff
    /// inside the water record's span). Never writes.</summary>
    public static void WaterCheck(string[]? paths)
    {
        using var log = new StreamWriter("_watercheck.txt", append: false);
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        var targets = new List<string>();
        if (paths is { Length: > 0 }) targets.AddRange(paths);
        else foreach (var (_, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) continue;
            var f = Directory.GetFiles(dir, "*.bin").OrderBy(x => x).FirstOrDefault();
            if (f is not null) targets.Add(f);
        }

        int pass = 0, fail = 0, skipped = 0;
        foreach (var path in targets)
        {
            try
            {
                var doc = LevelLoader.Load(path);
                var wtPoly = doc.Scene.Polys.FirstOrDefault(p => p.Source is TastyEditor.Core.Levels.TbWater);
                if (wtPoly?.Source is not TastyEditor.Core.Levels.TbWater wt) { skipped++; continue; }

                byte[] baseBytes = doc.Encode();
                bool untouched = baseBytes.AsSpan().SequenceEqual(doc.Original);
                var span = SceneProjector.RecordSpan(wt);

                // move: shift the centre (should touch only 2 doubles).
                bool mv = SceneProjector.MoveRecord(wt, 50, -25);
                var rMove = Verify(doc, baseBytes, span, 24);

                // resize: fresh load, drag corner 0 to a new world point (up to 8 doubles).
                var doc2 = LevelLoader.Load(path);
                var wt2 = doc2.Scene.Polys.First(p => p.Source is TastyEditor.Core.Levels.TbWater).Source!;
                var cs = SceneProjector.PolyWorldPoints(wt2, -1);
                bool rz = SceneProjector.SetPolyVertexWorld(wt2, -1, 0, new PointD(cs[0].X + 200, cs[0].Y + 150));
                var rResize = Verify(doc2, doc2.Original, span, 80);

                // named field edits (the water field panel's write path): set a scalar double,
                // the water id int and a toggle. All in-place (count/size fixed) -> few bytes.
                var doc3 = LevelLoader.Load(path);
                var wt3 = (TastyEditor.Core.Levels.TbWater)doc3.Scene.Polys.First(p => p.Source is TastyEditor.Core.Levels.TbWater).Source!;
                var span3 = SceneProjector.RecordSpan(wt3);
                WaterFields.Set(wt3, "scalar0", 12.5);
                WaterFields.Set(wt3, "water_id", 7);
                WaterFields.Set(wt3, "toggle_a", 1);
                var rFields = Verify(doc3, doc3.Original, span3, 28);

                bool ok = untouched && mv && rz && rMove.Ok && rResize.Ok && rFields.Ok;
                Emit($"{(ok ? "OK  " : "FAIL")} {doc.Profile.Short,-4} {Path.GetFileName(path),-26} water rect | " +
                     $"untouched={untouched} | {rMove.Msg} | {rResize.Msg} | fields: {rFields.Msg}");
                if (ok) pass++; else fail++;
            }
            catch (Exception ex) { fail++; Emit($"FAIL {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"WATER CHECK: {pass} pass, {fail} fail ({skipped} levels with no water skipped)");
        Emit(fail == 0 ? "WATER EDIT GREEN" : "WATER EDIT FAILURES");
    }

    private static (bool Ok, string Msg) Verify(LevelDoc doc, byte[] baseline, (long Off, long Len) span, int maxBytes)
    {
        byte[] edited = doc.Encode();
        var rep = SaveReport.Compare(baseline, edited);
        var re = doc.Profile.Codec.Encode(doc.Profile.Decode(edited));
        bool stable = re.Length == edited.Length && re.AsSpan().SequenceEqual(edited);
        bool within = span.Off < 0 || (rep.FirstDiff >= span.Off && rep.LastDiff < span.Off + span.Len);
        bool ok = rep.SizePreserved && rep.BytesChanged > 0 && rep.BytesChanged <= maxBytes && stable && within;
        string spanS = span.Off < 0 ? "span n/a" : $"span[{span.Off}..{span.Off + span.Len}) {(within ? "held" : "LEAKED")}";
        return (ok, $"{rep.BytesChanged}b @{rep.FirstDiff}..{rep.LastDiff}, size {(rep.SizePreserved ? "same" : "CHANGED")}, {spanS}, stable={stable}");
    }

    /// <summary>Representative catalog sprite names (alias targets / real cell names)
    /// shared across the ElementEngine games, used to exercise resolution + atlas decode
    /// headlessly without needing entity records.</summary>
    private static readonly string[] SampleSpecies =
    {
        "great_white_shark", "white_tip_shark", "orca", "gold_fish", "clown_fish",
        "aircraft_carrier", "attack_helicopter", "phyto_plankton", "zoo_plankton",
        "jumbo_squid", "personal_submarine", "underwater_car", "brain_coral_1",
        "sunken_pirates_ship",
    };

    /// <summary>Headless proof of the S5-1 asset loader: for each ElementEngine install,
    /// derive the asset root from a stock level path, load the tables, resolve sample
    /// species to cells, and (via the WPF decode layer) probe an atlas image to prove the
    /// alpha composite ran. Tasty Blue ships jpg+mask pairs, so it must also show both
    /// transparent and opaque atlas pixels. Display-only; never touches a level file.</summary>
    public static void AssetsCheck()
    {
        using var log = new StreamWriter("_assets.txt", append: false);
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        int pass = 0, fail = 0, skipped = 0;
        foreach (var (game, dir) in Defaults)
        {
            if (game == "tp2") continue;                 // TP2 assets are a different format (S5-7)
            if (!Directory.Exists(dir)) { skipped++; Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).FirstOrDefault();
            if (level is null) { skipped++; Emit($"SKIP {game}: empty levels dir"); continue; }

            try
            {
                var root = AssetsRoot.ForLevel(level);
                var res = root is null ? null : SpriteResolver.TryLoad(root);
                if (res is null) { fail++; Emit($"FAIL {game,-3} {Path.GetFileName(level),-24} resolver not loaded (invalid assets tree)"); continue; }

                var ss = new SpriteSource(res);
                int resolved = 0;
                foreach (var n in SampleSpecies)
                    if (res.TryResolve(n, 0, out _)) resolved++;

                // Game-agnostic core gate: decode this install's own atlases through the
                // cell table (species names differ per game, the shipped cells do not).
                // One probe per distinct atlas, capped so a headless run stays quick.
                bool decodedAtlas = false, maskedAtlas = false;
                string firstAtlas = "";
                var seen = new HashSet<string>();
                foreach (var cn in res.CellNames)
                {
                    if (!res.TryResolveCellName(cn, out var s)) continue;
                    if (!seen.Add(s.Atlas)) continue;
                    if (seen.Count > 16) break;
                    var pr = ss.ProbeAtlas(s.Atlas);
                    if (pr is null) continue;
                    decodedAtlas = true;
                    if (pr.Value.Clear > 0 && pr.Value.Opaque > 0) maskedAtlas = true;
                    if (firstAtlas.Length == 0) firstAtlas = $"{s.Atlas} {pr.Value.W}x{pr.Value.H} op={pr.Value.Opaque} clear={pr.Value.Clear} part={pr.Value.Partial}";
                }

                // Every sprite atlas ships with transparent surround (tb jpg+mask, f4/f5 RGBA
                // png), so a masked atlas is the proof the alpha composite actually ran.
                bool ok = res.CellCount > 0 && decodedAtlas && maskedAtlas;
                Emit($"{(ok ? "OK  " : "FAIL")} {game,-3} cells={res.CellCount,-5} anims={res.AnimCount,-4} species={resolved}/{SampleSpecies.Length} atlases={seen.Count} first=[{firstAtlas}] masked={maskedAtlas}");
                if (ok) pass++; else fail++;
            }
            catch (Exception ex) { fail++; Emit($"FAIL {game}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"ASSET CHECK: {pass} pass, {fail} fail ({skipped} skipped)");
        Emit(fail == 0 ? "ASSET LOAD GREEN" : "ASSET LOAD FAILURES");
    }

    /// <summary>Diagnostic: build specific deco/entity sprites through the real WPF decode
    /// path and histogram the returned bitmap's ALPHA, to prove whether a named sprite keeps
    /// its transparency. args = [gameShort, filterSubstring]. Sweeps a stock level's decos,
    /// builds any whose sprite name contains the filter, reports clear/opaque/part pixel
    /// counts of the built image. Display-only; never touches a level file.</summary>
    public static void AlphaProbe(string[] args)
    {
        using var log = new StreamWriter("_alphaprobe.txt", append: false);
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        string game = args.Length > 1 ? args[1] : "f5";
        string filter = args.Length > 2 ? args[2] : "stain";
        var dir = Defaults.First(d => d.Game == game).Dir;
        var level = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).FirstOrDefault();
        if (level is null) { Emit("no level"); return; }
        var root = AssetsRoot.ForLevel(level);
        var res = root is null ? null : SpriteResolver.TryLoad(root);
        if (res is null) { Emit("resolver not loaded"); return; }
        var ss = new SpriteSource(res);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var colorOf = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f))
        {
            if (found.Count >= 24) break;
            List<SceneCross> ms;
            try { ms = LevelLoader.Load(lvl).Scene.Crosses.Where(c => c.Kind == SceneKind.Deco).ToList(); }
            catch { continue; }
            foreach (var c in ms)
                if (c.SpriteName is { Length: > 0 } n && n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(n);
                    if (c.ColorRgba is { Length: >= 4 } cc && !colorOf.ContainsKey(n)) colorOf[n] = cc;
                }
        }
        var names = found.OrderBy(n => n).ToList();
        Emit($"{game} dir filter='{filter}' unique deco names matching={names.Count}");
        foreach (var n in names)
        {
            // resolve -> atlas + rect, then build via the real decode/crop path.
            bool okRes = res.TryResolve(n, 0, out var s);
            string cell = okRes ? $"{s.Atlas} rect=({s.X1},{s.Y1},{s.X2},{s.Y2}) orig={s.OrigW}x{s.OrigH} crop=({s.CropX},{s.CropY})" : "(unresolved)";
            if (!ss.TryGet(n, 0, null, out var img, out int w, out int h, out _, out _, out _))
            { Emit($"  {n,-22} NO SPRITE  {cell}"); continue; }
            var fmt = img.Format.ToString();
            int stride = w * 4;
            var px = new byte[stride * h];
            img.CopyPixels(px, stride, 0);
            int cl = 0, op = 0, pa = 0;
            for (int p = 3; p < px.Length; p += 4) { byte a = px[p]; if (a == 0) cl++; else if (a == 255) op++; else pa++; }
            var col = colorOf.TryGetValue(n, out var cv) ? string.Join(",", cv) : "none";
            Emit($"  {n,-22} {w}x{h} {fmt} clear={cl} opaque={op} part={pa} color=[{col}]  {cell}");
        }
        // Also probe the atlas file itself (raw decode, before crop) for alpha.
        foreach (var an in names.Select(n => res.TryResolve(n, 0, out var s2) ? s2.Atlas : null)
                 .Where(a => a is { Length: > 0 }).Distinct())
        {
            var pr = ss.ProbeAtlas(an!);
            Emit($"  ATLAS {an}: {(pr is null ? "null" : $"{pr.Value.W}x{pr.Value.H} op={pr.Value.Opaque} clear={pr.Value.Clear} part={pr.Value.Partial}")}");
        }
    }

    /// <summary>Headless proof of the S5-2 entity display fields. The committed
    /// levels/barrel1a.bin is cross-checked against barrel1a_data.json (the Python
    /// model's own field dump for the same file): five palmtree_barrel1 records covering
    /// has_box 0 and 1, signed centidegree rotations and increasing mass. Asserts the
    /// raw reads (name/rotation/mass/priority) match, that the projector carries them onto
    /// the scene crosses, and (structurally) that a Final Bite opaque entity block splits
    /// at its count and yields named crosses. Display-only; never writes.</summary>
    public static void EntFields()
    {
        using var log = new StreamWriter("_entfields.txt", append: false);
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        // Oracle: levels/barrel1a_data.json entities[], in file order.
        var oName = new[] { "palmtree_barrel1", "palmtree_barrel1", "palmtree_barrel1", "palmtree_barrel1", "palmtree_barrel1" };
        var oRot = new[] { 17165, -18437, -2415, -18780, 296 };
        var oMass = new[] { 20000.0, 26000.0, 33800.0, 43940.0, 57122.0 };
        var oPrio = new[] { 43, -1, -1, -1, -1 };
        var oPos = new (double X, double Y)[] { (-1068, -161), (-320, -216), (512, -231), (1288, -308), (1999, -346) };

        int pass = 0, fail = 0;
        void Count(bool ok) { if (ok) pass++; else fail++; }

        var tbPath = FindRepoLevel("barrel1a.bin");
        if (tbPath is null) { Emit("FAIL tb: levels/barrel1a.bin not found in repo"); fail++; }
        else
        {
            try
            {
                var lv = TbCodec.Instance.Decode(File.ReadAllBytes(tbPath));
                var ents = lv.Layers.SelectMany(l => l.Entities).ToList();
                // The oracle JSON dumps one layer (5 palmtree_barrel1); the file carries
                // more entities across its layers, so check the file covers them and that
                // the first five decode-order records match the oracle exactly.
                bool cnt = ents.Count >= oName.Length;
                Emit($"{(cnt ? "OK  " : "FAIL")} tb entity count = {ents.Count} (>= {oName.Length} oracle)");
                Count(cnt);
                for (int i = 0; i < Math.Min(ents.Count, oName.Length); i++)
                {
                    var e = ents[i];
                    bool ok = e.Name == oName[i] && e.Rotation == oRot[i]
                              && Math.Abs(e.Mass - oMass[i]) < 1e-6 && e.Priority == oPrio[i]
                              && e.ColorRgba is { Length: 4 } && e.ColorRgba.All(x => x == 255);
                    Emit($"{(ok ? "OK  " : "FAIL")} tb[{i}] {e.Name} rot={e.Rotation}(exp {oRot[i]}) mass={e.Mass}(exp {oMass[i]}) prio={e.Priority}(exp {oPrio[i]}) col=[{string.Join(",", e.ColorRgba)}] | pos=({e.PosX},{e.PosY}) json=({oPos[i].X},{oPos[i].Y})");
                    Count(ok);
                }

                // The projector must carry the same display fields onto the crosses the
                // viewport draws (rotation sign = -raw/100, matching tbeditor EntityItem).
                var doc = LevelLoader.Load(tbPath);
                var ec = doc.Scene.Crosses.Where(c => c.Kind == SceneKind.Entity).ToList();
                bool pj = ec.Count >= oName.Length
                          && ec.Take(oName.Length).All(c => c.SpriteName == "palmtree_barrel1")
                          && Math.Abs(ec[0].RotationDeg - (-17165 / 100.0)) < 1e-6
                          && Math.Abs(ec[0].Mass - 20000.0) < 1e-6
                          && ec[0].ColorRgba is { Length: 4 };
                Emit($"{(pj ? "OK  " : "FAIL")} tb projector: {ec.Count} entity crosses carry SpriteName/RotationDeg/Mass/ColorRgba");
                Count(pj);
            }
            catch (Exception ex) { fail++; Emit($"FAIL tb: {ex.GetType().Name}: {ex.Message}"); }
        }

        // f5 rotation: the entity block splits at its count and yields named crosses whose
        // RotationDeg must equal the int32 at xy_off + Rot + ExtF4F5 (the turn), NOT the pad at
        // xy_off + Rot (always 0). Scans the installed f5 levels for one with rotated entities and
        // proves the projector reads the relocated word (the S5 f4/f5 rotation bug).
        var f5dir = Defaults.First(d => d.Game == "f5").Dir;
        if (!Directory.Exists(f5dir)) { Emit("SKIP f5: levels dir not installed"); }
        else
        {
            try
            {
                int checkedFiles = 0, rotatedCrosses = 0, mismatch = 0, padMismatch = 0;
                string? proofFile = null;
                foreach (var lvl in Directory.GetFiles(f5dir, "*.bin").OrderBy(f => f))
                {
                    checkedFiles++;
                    var doc = LevelLoader.Load(lvl);
                    var ec = doc.Scene.Crosses.Where(c => c.Kind == SceneKind.Entity && c.Source is EntityEditHandle).ToList();
                    foreach (var c in ec)
                    {
                        var h = (EntityEditHandle)c.Source!;
                        var (heads, ok) = EntityFraming.SplitRecords(h.Sec.Block, 0, h.Sec.Count, h.Lv.Game);
                        if (!ok || h.RecIndex < 0 || h.RecIndex >= heads.Count) continue;
                        int xo = heads[h.RecIndex].XyOff;
                        int rotNew = xo + EntityFraming.Rot + EntityFraming.ExtF4F5 + 4 <= h.Sec.Block.Length
                            ? BitConverter.ToInt32(h.Sec.Block, xo + EntityFraming.Rot + EntityFraming.ExtF4F5) : 0;
                        int rotOld = xo + EntityFraming.Rot + 4 <= h.Sec.Block.Length
                            ? BitConverter.ToInt32(h.Sec.Block, xo + EntityFraming.Rot) : 0;
                        if (Math.Abs(c.RotationDeg - (-rotNew / 100.0)) > 1e-6) mismatch++;
                        if (rotNew != 0)
                        {
                            rotatedCrosses++;
                            if (rotOld != 0) padMismatch++;   // +28 should be the always-0 pad
                        }
                    }
                    if (rotatedCrosses > 0) { proofFile = Path.GetFileName(lvl); break; }
                }
                bool okf = rotatedCrosses > 0 && mismatch == 0 && padMismatch == 0;
                Emit($"{(okf ? "OK  " : "FAIL")} f5 rotation {proofFile ?? "(none rotated)",-24}: {rotatedCrosses} rotated crosses over {checkedFiles} file(s); projector/raw(+36) mismatches={mismatch}, pad(+28) non-zero={padMismatch}");
                Count(okf);
            }
            catch (Exception ex) { fail++; Emit($"FAIL f5: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"ENT FIELDS: {pass} pass, {fail} fail");
        Emit(fail == 0 ? "ENTITY FIELDS GREEN" : "ENTITY FIELDS FAILURES");
    }

    /// <summary>Diagnostic: print the decoded world-position scale of each record kind in a
    /// tb level (entity / deco / wall / path raw PosX/PosY) and the rendered cross vs
    /// polygon/line extents, to reveal any unit-scale mismatch that skews the camera fit.</summary>
    public static void PosCheck(string[]? paths)
    {
        using var log = new StreamWriter("_poscheck.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        var targets = new List<string>();
        if (paths is { Length: > 0 }) targets.AddRange(paths);
        else
        {
            var committed = FindRepoLevel("barrel1a.bin");
            if (committed is not null) targets.Add(committed);
            var d = Defaults.First(x => x.Game == "tb").Dir;
            if (Directory.Exists(d)) targets.Add(Directory.GetFiles(d, "*.bin").OrderBy(f => f).First());
        }
        foreach (var path in targets)
        {
            try
            {
                var doc = LevelLoader.Load(path);
                if (doc.Model is not TbLevel tb) { Emit($"{Path.GetFileName(path)}: not a tb level"); continue; }
                string J<T>(IEnumerable<T> xs, Func<T, string> f, int n) => string.Join("  ", xs.Take(n).Select(f));
                Emit(Path.GetFileName(path));
                Emit($"  entity posX: {J(tb.Layers.SelectMany(l => l.Entities), e => $"({e.PosX:0.##},{e.PosY:0.##})", 4)}");
                Emit($"  deco   posX: {J(tb.Layers.SelectMany(l => l.Decos), x => $"({x.PosX:0.##},{x.PosY:0.##})", 4)}");
                Emit($"  wall   posX: {J(tb.Layers.SelectMany(l => l.Walls), x => $"({x.PosX:0.##},{x.PosY:0.##})", 4)}");
                Emit($"  path   posX: {J(tb.Layers.SelectMany(l => l.Paths), x => $"({x.PosX:0.##},{x.PosY:0.##})", 4)}");
                var cr = doc.Scene.Crosses;
                if (cr.Count > 0)
                    Emit($"  CROSS bounds X[{cr.Min(c => c.At.X):0},{cr.Max(c => c.At.X):0}] Y[{cr.Min(c => c.At.Y):0},{cr.Max(c => c.At.Y):0}]");
                var pl = doc.Scene.Polys.SelectMany(s => s.Points).Concat(doc.Scene.Lines.SelectMany(s => s.Points)).ToList();
                if (pl.Count > 0)
                    Emit($"  POLY/LINE bounds X[{pl.Min(p => p.X):0},{pl.Max(p => p.X):0}] Y[{pl.Min(p => p.Y):0},{pl.Max(p => p.Y):0}]");
            }
            catch (Exception ex) { Emit($"FAIL {Path.GetFileName(path)}: {ex.Message}"); }
        }
    }

    /// <summary>Diagnostic: time each phase of the GUI open pipeline (OpenLevel) on one
    /// level to find what blocks the UI thread. Phases: decode+project, resolver load,
    /// sprite-source construct, then a forced atlas-decode of every named marker (the
    /// upper bound of what the first paint does when all sprites are on-screen).</summary>
    public static void LoadTime(string[]? paths)
    {
        using var log = new StreamWriter("_loadtime.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        var sw = new System.Diagnostics.Stopwatch();

        var targets = new List<string>();
        if (paths is { Length: > 0 }) targets.AddRange(paths);
        else
        {
            foreach (var (_, dir) in Defaults)
                if (Directory.Exists(dir))
                { var big = Directory.GetFiles(dir, "*.bin").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault(); if (big is not null) targets.Add(big); }
        }
        foreach (var path in targets)
        {
            Emit($"== {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024} KB)");
            try
            {
                T Time<T>(string label, Func<T> f)
                {
                    sw.Restart(); var r = f(); sw.Stop();
                    Emit($"  {label,-22} {sw.ElapsedMilliseconds,7} ms");
                    return r;
                }
                // Detect already runs the oracle (TryDecode), then Load decodes again — so
                // time the pieces to see how much is redundant re-decode vs the oracle itself.
                var bytes = File.ReadAllBytes(path);
                var prof = Time("Detect (trial)", () => GameRegistry.Detect(path, bytes, out _));
                var model = Time("one profile.Decode", () => prof.Decode(bytes));
                Emit($"    key={prof.Key}");
                var doc = Time("full LevelLoader.Load", () => LevelLoader.Load(path));
                int crosses = doc.Scene.Crosses.Count;
                Emit($"    (crosses={crosses}, polys={doc.Scene.Polys.Count})");
                var res = Time("SpriteResolver.TryLoad", () => { var root = AssetsRoot.ForLevel(path); return root is null ? null : SpriteResolver.TryLoad(root); });
                if (res is null) { Emit("  resolver null — no sprite path"); continue; }
                var ss = Time("new SpriteSource", () => new SpriteSource(res));
                _ = model;
                var marks = doc.Scene.Crosses.Where(c => c.SpriteName is { Length: > 0 }).ToList();
                var atlases = new HashSet<string>();
                Time("decode all named sprites", () =>
                {
                    foreach (var c in marks)
                        if (ss.TryGet(c.SpriteName!, 0, null, out _, out _, out _, out _, out _, out _))
                            atlases.Add(c.SpriteName!);
                    return 0;
                });
                Emit($"    (named markers={marks.Count}, distinct resolved cells={atlases.Count})");
            }
            catch (Exception ex) { Emit($"  FAIL: {ex.Message}"); }
        }
    }

    /// <summary>Locate a file under the repo's committed levels/ folder by walking up from
    /// the current directory and the executable base directory.</summary>
    private static string? FindRepoLevel(string fileName)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                var cand = Path.Combine(d.FullName, "levels", fileName);
                if (File.Exists(cand)) return cand;
            }
        return null;
    }

    /// <summary>Headless proof of the S5-3 render path. For each ElementEngine install it
    /// (a) drives the render entry point the viewport uses — SpriteSource.TryGet (resolve →
    /// WPF atlas decode) — over the install's own cells and requires a finite-sized bitmap
    /// with a game-accurate SpriteScale (proves decode + size math, no window), and (b)
    /// scans a sample of stock levels counting how many projected entity/deco marks
    /// resolve to a real sprite. Since the engine-art port all three installs resolve entity
    /// theme tokens through the scanned per-exe entity → art table (F45Probe reports the
    /// per-tier split); before it Forever / Final Bite entity heads carried a per-level theme
    /// token that is not a cell key and degraded to crosses. Display-only; never writes.</summary>
    public static void SpritesCheck()
    {
        using var log = new StreamWriter("_sprites.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        // Cap the atlas decodes: distinct atlases are multi-MB jpgs, so a small sample of
        // the install's own cells is enough to prove the render-input path without a stall.
        const int cellCap = 8, levelCap = 6;
        int pass = 0, fail = 0, skipped = 0;
        foreach (var (game, dir) in Defaults)
        {
            if (game == "tp2") continue;                 // TP2 assets are a different format (S5-7)
            if (!Directory.Exists(dir)) { skipped++; Emit($"SKIP {game}: no levels dir"); continue; }
            var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
            if (levels.Length == 0) { skipped++; Emit($"SKIP {game}: empty levels dir"); continue; }
            try
            {
                var root = AssetsRoot.ForLevel(levels[0]);
                var res = root is null ? null : SpriteResolver.TryLoad(root);
                if (res is null) { fail++; Emit($"FAIL {game,-3} resolver not loaded"); continue; }
                var ss = new SpriteSource(res);

                // (a) render entry point over the install's own cells -> finite bitmap.
                int rendered = 0; var seen = new HashSet<string>();
                foreach (var cn in res.CellNames)
                {
                    if (seen.Count >= cellCap) break;
                    if (!seen.Add(cn)) continue;
                    if (ss.TryGet(cn, 0, null, out _, out int w, out int h, out _, out _, out _)
                        && w > 0 && h > 0 && double.IsFinite(SpriteScale.EntityRender(1.0, w, h)))
                        rendered++;
                }

                // (b) projected entity/deco marks on real levels that resolve to a sprite.
                // Decos are the static single-cell case (must render); animated multi-part
                // entities are engine-composited and legitimately stay crosses.
                int decos = 0, decoHits = 0, ents = 0, entHits = 0;
                var decoSample = new List<string>();
                foreach (var lvl in levels.Take(levelCap))
                {
                    List<SceneCross> ms;
                    try { ms = LevelLoader.Load(lvl).Scene.Crosses
                        .Where(c => c.Kind is SceneKind.Entity or SceneKind.Deco && c.SpriteName is { Length: > 0 }).ToList(); }
                    catch { continue; }
                    foreach (var c in ms)
                    {
                        bool isDeco = c.Kind == SceneKind.Deco;
                        if (isDeco) decos++; else ents++;
                        if (ss.TryGet(c.SpriteName!, 0, null, out _, out int w, out int h, out _, out _, out _) && w > 0 && h > 0)
                        {
                            if (isDeco) { decoHits++; if (decoSample.Count < 6) decoSample.Add(c.SpriteName!); } else entHits++;
                        }
                    }
                }

                // Gate on the render input pipeline; deco resolution is the slice-5 GUI
                // deliverable (static tiles), reported so a regression is visible.
                bool ok = rendered > 0;
                Emit($"{(ok ? "OK  " : "FAIL")} {game,-3} cells-rendered={rendered}/{cellCap} atlases | " +
                     $"decos {decoHits}/{decos} resolved, entities {entHits}/{ents} (parts engine-composited) | " +
                     $"sample decos=[{string.Join(",", decoSample)}]");
                if (ok) pass++; else fail++;
            }
            catch (Exception ex) { fail++; Emit($"FAIL {game}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"SPRITE CHECK: {pass} pass, {fail} fail ({skipped} skipped)");
        Emit(fail == 0 ? "SPRITE RENDER GREEN" : "SPRITE RENDER FAILURES");
    }

    /// <summary>Headless proof of the S5-4 asset-browser data path: enumerate the install's
    /// cells/animations and build thumbnails through <see cref="SpriteSource.TryGetCellThumb"/>
    /// (the exact call the browser panel makes). Caps decodes so a headless run stays quick.
    /// Display-only; touches no level bytes.</summary>
    public static void BrowserCheck()
    {
        using var log = new StreamWriter("_browser.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        const int thumbCap = 24;
        int pass = 0, fail = 0, skipped = 0;
        foreach (var (game, dir) in Defaults)
        {
            if (game == "tp2") continue;                 // TP2 assets are a different format (S5-7)
            if (!Directory.Exists(dir)) { skipped++; Emit($"SKIP {game}: no levels dir"); continue; }
            var level = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).FirstOrDefault();
            if (level is null) { skipped++; Emit($"SKIP {game}: empty levels dir"); continue; }
            try
            {
                var root = AssetsRoot.ForLevel(level);
                var res = root is null ? null : SpriteResolver.TryLoad(root);
                if (res is null) { fail++; Emit($"FAIL {game,-3} resolver not loaded"); continue; }
                var ss = new SpriteSource(res);

                int made = 0, tried = 0;
                string sample = "";
                foreach (var cn in ss.CellNames)
                {
                    if (tried >= thumbCap) break;
                    tried++;
                    if (ss.TryGetCellThumb(cn, out var img) && img.PixelWidth > 0 && img.PixelHeight > 0)
                    {
                        made++;
                        if (sample.Length == 0) sample = $"{cn} {img.PixelWidth}x{img.PixelHeight}";
                    }
                }

                bool ok = ss.CellCount > 0 && made > 0;
                Emit($"{(ok ? "OK  " : "FAIL")} {game,-3} cells={ss.CellCount,-5} anims={ss.AnimCount,-4} " +
                     $"thumbs={made}/{tried} first=[{sample}]");
                if (ok) pass++; else fail++;
            }
            catch (Exception ex) { fail++; Emit($"FAIL {game}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"BROWSER CHECK: {pass} pass, {fail} fail ({skipped} skipped)");
        Emit(fail == 0 ? "ASSET BROWSER GREEN" : "ASSET BROWSER FAILURES");
    }

    /// <summary>Names chosen to exercise every branch of the TP2 cascade the resolver must
    /// reproduce (aliases, egypt house composites, the star composite, candy base+overlay, the
    /// grape glow overlay, skeletal stand-body, and the fuzzy theme/number-word/synonym
    /// re-spellings). Combined with the install's real cells + level entity names this gives a
    /// broad oracle cross-section.</summary>
    private static readonly string[] Tp2CascadeCases =
    {
        "redgiant", "supergiant", "hypergiant_moving", "star", "neptune_future1", "jupiter_future1",
        "saturn_future1", "mars_future1", "moon_future1", "green_grape", "purple_grape",
        "green_olive", "egypt_poor_house_eight", "egypt_rich_house_four", "galacticturtle",
        "fabricspace", "leaf", "stoneblock1", "scientist_young_lab1",
        "egypt_poor_house_1", "egypt_poor_house_2", "egypt_poor_house_3",
        "egypt_rich_house_1", "egypt_rich_house_4", "egypt_rich_house_6",
        "candy_red", "candy_green", "candy_blue", "creditchip", "solarpanel", "futurebuilding2",
        "fusioncell", "boulder1", "modern_sciyoung", "tree_oak", "asteroid1_future1",
        "richouse", "planet_gas", "grape_olive_1",
    };

    // All 61 TP2 skeletal characters (multi-part entities), enumerated by scanning animationdefs.xml
    // for `<base>_stand_*` / `<base>_walk_*` part groups. Each must assemble (>=2 part layers) instead
    // of showing one body fragment. Proves the R8 TP2 multi-part entity-sprite slice.
    private static readonly string[] Tp2SkeletalCases =
    {
        "ala", "alberta", "ant", "aphid", "bacteria", "ballista", "bambi", "butt", "cobra", "cory", "cow",
        "croc", "drag", "egypt_noble", "egypt_peasant", "egypt_soldier", "euo", "frog", "future_person",
        "grasshop", "hippo", "horse", "jack", "japan_ninja", "japan_samurai", "japanmonster", "larva",
        "lion", "maia", "mau", "modern_sciold", "modern_sciyoung", "moth", "nano", "nanobot", "noth",
        "ory", "pachyr", "para", "poss", "rat", "roman_glad", "roman_man", "roman_soldier", "roman_woman",
        "scar", "shrew", "simon", "snail", "steg", "struth", "styg", "styrac", "tank", "ter", "tricer",
        "troo", "turt", "tyran", "ufo", "worm",
    };

    // The names a level actually writes: full species + level set, abbreviated stem hidden in the
    // exe's entity-def table. These are the entities that drew bare Xs before the stem lookup, so
    // each must resolve through the alias/suffix trim to the same assembly as its base.
    private static readonly string[] Tp2SkeletalLevelNames =
    {
        "tyrannosaurus_dino1", "tyrannosaurus_dino2", "triceratops_dino1", "maiasaura_dino1",
        "maiasaura_dino2", "alamosaurus_dino1", "albertosaurus_dino1", "stegoceras_dino1",
        "stygimoloch_dino1", "styracosaurus_dino1", "oryctodromeus_dino1", "pachyrhinosaurus_dino1",
        "parasaurolophus_dino1", "corythosaurus_dino1", "bambiraptor_dino1", "nanotyrannus_dino1",
        "nothronychus_dino1", "struthiomimus_dino1", "troodon_dino1", "euoplocephalus_dino1",
        "termite_dino1", "grasshopper_dino1", "dragonfly_dino1", "butterfly1_dino1", "scarab_egypt1",
        "jackal", "possum", "turtle", "ant_dino1", "aphid_dino1", "larva_dino1", "snail_dino1",
        "croc_egypt1", "hippo_egypt1", "horse_egypt1", "mau_egypt1", "egypt_noble_egypt1",
        "egypt_peasant_egypt1", "egypt_soldier_egypt1", "egypt_soldier_egypt2", "modern_sciyoung_lab1",
        "roman_man_walk_arm_upper", "egypt_noble_walk_arm_upper",
        // Types with no art of their own, registered by the exe onto another entity's rig (ArtEntity):
        // they must assemble that rig rather than draw a cross.
        "agomphus_dino1", "agomphus_dino2", "alphadon_dino1", "cimolestes_dino1", "cimolestes_dino2",
        "deinosuchus_dino1", "deinosuchus_dino2", "watusi_egypt1", "watusi_egypt2",
        "locust_egypt1", "mau_kitten_egypt1", "ant_explosive1",
    };

    /// <summary>Headless TP2 asset-cascade dump (S5-7). Loads the Back for Seconds resolver
    /// and writes a resolution <c>name\tsignature</c> table (<c>_tp2_cs.txt</c>) for every
    /// atlas cell, curated cascade case and real level entity/deco name, under a neutral white
    /// tint. A companion Python run of the untouched <c>tp2assets.py</c> oracle recomputes the
    /// same signatures and diffs them: a match proves the ported C# cascade resolves each name
    /// to the identical cells / composite recipe / pivot / opacity. Read-only; touches no bytes.</summary>
    public static void Tp2AssetsCheck()
    {
        using var log = new StreamWriter("_tp2_assets.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        var (game, dir) = Defaults.First(d => d.Game == "tp2");
        if (!Directory.Exists(dir)) { Emit($"SKIP tp2: no levels dir {dir}"); Emit("TP2 ASSETS SKIPPED"); return; }
        var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
        if (levels.Length == 0) { Emit("SKIP tp2: empty levels dir"); Emit("TP2 ASSETS SKIPPED"); return; }
        try
        {
            var root = Tp2AssetsRoot.ForLevel(levels[0]);
            var res = root is null ? null : Tp2SpriteResolver.TryLoad(root);
            if (res is null) { Emit("FAIL tp2: resolver not loaded (invalid assets tree)"); Emit("TP2 ASSETS FAILURES"); return; }
            Emit($"tp2 graphics={root!.GraphicsDir}");
            Emit($"cells={res.CellCount} anims={res.AnimCount}");

            // Gather names: every cell, the curated cascade cases, and the entity/deco names
            // that actually appear in a sample of stock levels (run-length deco resolution).
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var cn in res.CellNames) names.Add(cn);
            foreach (var c in Tp2CascadeCases) names.Add(c);
            foreach (var c in Tp2SkeletalCases) names.Add(c);
            foreach (var c in Tp2SkeletalLevelNames) names.Add(c);

            // Every stock level, not a 40-level sample: the install ships 166, so an entity type that
            // only appears in an unsampled one (the maple leaves the user reported) drew as a cross
            // while this gate stayed green. Undecoded levels are counted, so a silent skip cannot hide
            // names again.
            int decoded = 0, undecoded = 0;
            foreach (var lvl in levels)
            {
                try
                {
                    if (LevelLoader.Load(lvl).Model is not Tp2Level tp) { undecoded++; continue; }
                    decoded++;
                    foreach (var layer in tp.Layers)
                    {
                        foreach (var e in layer.Entities)
                            if (!string.IsNullOrEmpty(e.Type)) names.Add(e.Type!);
                        foreach (var nm in ResolveTp2DecoNames(tp, layer))
                            if (!string.IsNullOrEmpty(nm)) names.Add(nm!);
                    }
                }
                catch { undecoded++; }   /* a level that fails to decode contributes no names */
            }
            Emit($"levels: decoded={decoded}/{levels.Length} undecoded={undecoded}");

            var white = new byte[] { 255, 255, 255, 255 };
            int resolved = 0;
            var unresolvedNames = new List<string>();
            using (var outw = new StreamWriter("_tp2_cs.txt", append: false))
                foreach (var n in names)
                {
                    var sig = res.Signature(n, white);
                    if (sig != "None") resolved++; else unresolvedNames.Add(n);
                    outw.Write(n); outw.Write('\t'); outw.WriteLine(sig);
                }

            Emit($"names={names.Count} resolved={resolved} unresolved={names.Count - resolved} (wrote _tp2_cs.txt)");

            // App pixel path (S5-7b): drive Tp2SpriteSource.TryGet over a sample of resolved
            // names (crop -> composite -> tint) and require real, non-empty bitmaps. A handful
            // of known multi-part / recoloured names (the star composite, a rocky/gas planet,
            // a grape with its glow overlay, candy with its overlay, a composited egypt house)
            // are rebuilt under a coloured tint to prove tinting runs without dropping the
            // sprite. A few cell thumbnails exercise the browser path.
            var ss = new Tp2SpriteSource(res);
            var colour = new byte[] { 180, 90, 200, 255 };
            int rendered = 0; string rsample = "";
            foreach (var n in names)
            {
                if (rendered >= 40) break;
                if (ss.TryGet(n, 0, white, out _, out int w, out int h, out _, out _, out _) && w > 0 && h > 0)
                {
                    rendered++;
                    if (rsample.Length == 0) rsample = $"{n} {w}x{h}";
                }
            }
            int tinted = 0;
            foreach (var n in new[] { "star", "mars_future1", "green_grape", "candy_red", "egypt_rich_house_1" })
                if (ss.TryGet(n, 0, colour, out _, out int tw, out int th, out _, out _, out _) && tw > 0 && th > 0)
                    tinted++;
            int thumbs = 0;
            foreach (var cn in res.CellNames)
            {
                if (thumbs >= 12) break;
                if (ss.TryGetCellThumb(cn, out var t) && t.PixelWidth > 0 && t.PixelHeight > 0) thumbs++;
            }
            Emit($"render: sprites={rendered} tinted={tinted}/5 thumbs={thumbs}/12 first=[{rsample}]");

            // R8 skeletal assembly: every curated character and every real level type name must
            // resolve to a multi-part plan (>=2 layers) and render a real bitmap. A single-body
            // fallback (<2 layers) is a failure.
            int skelPlans = 0, skelRendered = 0; var skelBad = new List<string>();
            // The assembled canvas must land on the engine's own natwidth/natheight for every case that
            // has a def. This is what proves the part-placement convention (a part mirrored about its
            // canvas centre instead of its pivot blew these boxes up 35% on average, ala by 79%).
            int natTot = 0, covTot = 0; var natBad = new List<string>();
            foreach (var c in Tp2SkeletalCases.Concat(Tp2SkeletalLevelNames))
            {
                var plan = res.BuildPlan(c, white, 0);
                if (plan is { IsSkeletal: true } && plan.Ops.Count >= 2) skelPlans++;
                else { skelBad.Add($"{c}(plan={(plan is null ? "null" : plan.IsSkeletal ? plan.Ops.Count + "L" : "single")})"); continue; }
                if (ss.TryGet(c, 0, white, out _, out int sw, out int sh, out _, out _, out _))
                {
                    if (sw <= 0 || sh <= 0) skelBad.Add($"{c}(render)");
                    else skelRendered++;
                }
                else skelBad.Add($"{c}(render)");
                if (plan is not null && res.NatBox(c) is (double nw, double nh, double cv))
                {
                    natTot++;
                    if (cv > 0 && cv < 1) covTot++;
                    double ew = Math.Abs(plan.CanvasW - nw) / nw, eh = Math.Abs(plan.CanvasH - nh) / nh;
                    if (ew > 0.25 || eh > 0.25)
                        natBad.Add($"{c} {plan.CanvasW}x{plan.CanvasH} vs nat {nw:0}x{nh:0} ({ew * 100:0}%/{eh * 100:0}%)");
                }
            }
            int skelTotal = Tp2SkeletalCases.Length + Tp2SkeletalLevelNames.Length;
            Emit($"skeletal: plans={skelPlans}/{skelTotal} rendered={skelRendered} bad=[{string.Join(" ", skelBad.Take(12))}]");
            Emit($"natbox: agree={natTot - natBad.Count}/{natTot} coverage<1={covTot}/{natTot} worst=[{string.Join(" ", natBad.Take(8))}]");

            // Every name the install's levels actually place must reach real art: an unresolved name is
            // an entity the viewport draws as a placeholder, which is the defect this gate exists to
            // catch. `resolved > 0` used to pass with 41 names missing, so the count is now load-bearing.
            bool ok = res.CellCount > 0 && resolved == names.Count && rendered > 0 && tinted >= 4
                      && thumbs > 0
                      && skelPlans == skelTotal && skelRendered == skelPlans && natBad.Count == 0;
            if (names.Count != resolved)
                foreach (var n in unresolvedNames.Take(40)) Emit($"  UNRESOLVED {n}");
            Emit($"{(ok ? "TP2 ASSETS GREEN" : "TP2 ASSETS FAILURES")} (cascade {resolved}/{names.Count}, render {rendered}, tinted {tinted}/5, thumbs {thumbs})");
        }
        catch (Exception ex) { Emit($"FAIL tp2: {ex.GetType().Name}: {ex.Message}"); Emit("TP2 ASSETS FAILURES"); }
    }

    /// <summary>Dev-only visual verification for the R8 TP2 skeletal assembly: renders skeletal
    /// characters through the real WPF pixel path, writes each built sprite to
    /// <c>_skel_&lt;name&gt;.png</c> and a labelled contact sheet to <c>_skel_sheet.png</c>, so an agent can
    /// inspect the assembled pixels (rotation sign, offsets, z-order, canvas vs the engine's own nat
    /// box) without a window. Read-only; touches no level bytes.</summary>
    public static void Tp2SkelDump()
    {
        using var log = new StreamWriter("_tp2skel.txt", append: false) { AutoFlush = true };
        void Emit(string s) { Console.WriteLine(s); log.WriteLine(s); }
        var (game, dir) = Defaults.First(d => d.Game == "tp2");
        var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
        var root = Tp2AssetsRoot.ForLevel(levels[0]);
        var res = root is null ? null : Tp2SpriteResolver.TryLoad(root);
        if (res is null) { Emit("FAIL: resolver null"); return; }
        var ss = new Tp2SpriteSource(res);
        var white = new byte[] { 255, 255, 255, 255 };
        var items = new List<(string Cap, System.Windows.Media.Imaging.BitmapSource Img)>();
        foreach (var n in new[] { "ala", "cobra", "cow", "rat", "modern_sciyoung", "lion", "future_person", "cory",
                                   "tyrannosaurus_dino1", "triceratops_dino1", "maiasaura_dino1",
                                   "styracosaurus_dino1", "egypt_soldier_egypt1", "termite_dino1",
                                   "roman_man", "ant", "watusi_egypt1", "agomphus_dino1", "locust_egypt1",
                                   "deinosuchus_dino1", "mau_kitten_egypt1", "goo_enemy_future1",
                                   // The names the widened corpus turned up: each was an X before the
                                   // alias / trim / pose-less-group work, so their pixels need eyes.
                                   "hoverguy_future1", "mouse_japan1", "ninja_japan1", "samurai_japan1",
                                   "future_tank_1", "scientist_old_lab2", "roman_glad_rome1", "lion_rome1",
                                   "rat_rome1", "hippo_baby1", "simon_labfarattack", "ballista_rome1",
                                   "hovercar_float1", "amoeba1_future1", "vacuum_future1", "uranus_future1",
                                   "shuttlecraft_fight1", "stoneblock1_pushable", "brain_watch1",
                                   "laserbolt_tank2", "future_person_blue_1", "roman_soldier_rome1",
                                   // The plain animal beside its armed variant: only the `_future` types
                                   // carry the Laser Ant / Laser Rat / Laser Simon gun.
                                   "ant_dino1", "ant_future1", "rat_future1", "simon_future1" })
        {
            if (!ss.TryGet(n, 0, white, out var img, out int w, out int h, out _, out _, out _))
            { Emit($"{n}: NO SPRITE"); continue; }
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            using var fs = File.Create($"_skel_{n}.png");
            enc.Save(fs);
            // The engine's own assembled box, so a caption can show canvas-vs-nat at a glance.
            var nat = res.NatBox(n);
            string pct(double got, double want) => (want > 0 ? (got - want) / want * 100 : 0).ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture);
            string box = nat is (double nw, double nh, double cv)
                ? $" nat={nw:0}x{nh:0} cov={cv:0.###} ({pct(w, nw)}%/{pct(h, nh)}%)"
                : " nat=-";
            Emit($"{n}: {w}x{h}{box}");
            items.Add(($"{n}  {w}x{h}" + (nat is (double nw2, double nh2, double cv2)
                ? $"  nat {nw2:0}x{nh2:0} cov {cv2:0.###}" : ""), img));
        }

        // Contact sheet: uniform-fit each sprite in a grey cell (so alpha is visible) with its caption.
        const double CW = 330, CH = 250, CAP = 30, GAP = 12;
        int cols = 4, rows = (items.Count + cols - 1) / cols;
        double W = cols * CW + (cols + 1) * GAP, H = rows * (CH + CAP) + (rows + 1) * GAP;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 46, 54)),
                             null, new System.Windows.Rect(0, 0, W, H));
            var typeface = new System.Windows.Media.Typeface("Segoe UI");
            var brush = System.Windows.Media.Brushes.White;
            for (int i = 0; i < items.Count; i++)
            {
                double x = GAP + (i % cols) * (CW + GAP), y = GAP + (i / cols) * (CH + CAP + GAP);
                var im = items[i].Img;
                double s = Math.Min((CW - 8) / im.PixelWidth, (CH - 8) / im.PixelHeight);
                double dw = im.PixelWidth * s, dh = im.PixelHeight * s;
                dc.DrawImage(im, new System.Windows.Rect(x + (CW - dw) / 2, y + (CH - dh) / 2, dw, dh));
                dc.DrawText(new System.Windows.Media.FormattedText(items[i].Cap, System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight, typeface, 12, brush, 1.0),
                    new System.Windows.Point(x + 4, y + CH + 4));
            }
        }
        var sheet = new System.Windows.Media.Imaging.RenderTargetBitmap((int)W, (int)H, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        sheet.Render(dv);
        var sheetEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        sheetEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sheet));
        using (var fs = File.Create("_skel_sheet.png")) sheetEnc.Save(fs);
        Emit($"sheet: {items.Count} tiles -> _skel_sheet.png");
        Emit("TP2 SKEL DUMP DONE");
    }

    /// <summary>Measurement dump for the TP2 render-scale law: for every entity TYPE in every stock
    /// level, the distinct <c>mass</c> values and the raw <c>BoxBounds</c> quads (centiunits, present only
    /// when the record carries a box). Joined offline against entityintersections.xml natwidth /
    /// natheight / coverage, this decides whether an entity's world footprint tracks its mass (and over
    /// which area) or is a constant pixels-per-world-unit. Read-only.</summary>
    public static void Tp2ScaleDump()
    {
        using var log = new StreamWriter("_tp2scale.txt", append: false) { AutoFlush = true };
        void Emit(string s) { log.WriteLine(s); }
        var (game, dir) = Defaults.First(d => d.Game == "tp2");
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var masses = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var boxes = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f))
        {
            Tp2Level? tp;
            try { tp = LevelLoader.Load(lvl).Model as Tp2Level; } catch { continue; }
            if (tp is null) continue;
            foreach (var l in tp.Layers)
                foreach (var e in l.Entities)
                {
                    if (string.IsNullOrEmpty(e.Type)) continue;
                    var k = e.Type!;
                    counts[k] = counts.GetValueOrDefault(k) + 1;
                    var ms = masses.TryGetValue(k, out var m0) ? m0 : new SortedSet<string>();
                    if (ms.Count < 8) ms.Add(e.Mass.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                    masses[k] = ms;
                    if (e.HasBox == 0) continue;
                    var bs = boxes.TryGetValue(k, out var b0) ? b0 : new SortedSet<string>();
                    if (bs.Count < 6) bs.Add(string.Join(",", e.BoxBounds));
                    boxes[k] = bs;
                }
        }
        foreach (var (name, n) in counts)
        {
            var ms = masses.TryGetValue(name, out var m) ? m : new SortedSet<string>();
            var bs = boxes.TryGetValue(name, out var b) ? b : new SortedSet<string>();
            Emit($"{name}\t{n}\t{string.Join("|", ms)}\t{string.Join(";", bs)}");
        }
        int ntypes = counts.Count;
        Console.WriteLine($"tp2scale: {ntypes} types -> _tp2scale.txt");
    }

    // type 0 inherits the previous tile; type 1 -> TileTypes[Cell]; type 2 -> inline String
    // (mirrors tp2editor.resolve_deco_names). Local to the check so the oracle dump does not
    // depend on the projector's TP2 field wiring landing first.
    private static IEnumerable<string?> ResolveTp2DecoNames(Tp2Level tp, Tp2Layer layer)
    {
        string? last = null;
        foreach (var d in layer.Decos)
        {
            string? nm = d.Type switch
            {
                2 => d.String,
                1 => (d.Cell >= 0 && d.Cell < tp.TileTypes.Count) ? tp.TileTypes[d.Cell] : null,
                _ => last,
            };
            if (nm is not null) last = nm;
            yield return nm;
        }
    }

    // Curated, cascade-resolvable TP2 sprite names (all exercised in the S5-7 oracle), used
    // as realistic reassignment targets for the S5-5 proof.
    private static readonly string[] Tp2SpriteTargets =
        { "star", "candy_red", "green_grape", "mars_future1", "egypt_rich_house_1" };

    private static string PickTp2Target(string current) =>
        Tp2SpriteTargets.FirstOrDefault(t => t != current) ?? "star";

    /// <summary>Headless proof of the S5-5 sprite-reassignment write path (TP2 only, the
    /// one family whose codec re-serialises entity prefab names and decoration tile indices
    /// from the typed model). For a handful of stock levels it reassigns one entity and one
    /// decoration through <see cref="Tp2Edit"/>, then for each proves three things: (1) the
    /// new name survives a decode of the encoded bytes and the record counts are unchanged;
    /// (2) the edited file is re-decode stable (encode(decode(x)) == x); (3) reverting the
    /// edit reproduces the original bytes EXACTLY — the same localisation/reversibility the
    /// golden I3 gate asserts, so no other record could have moved. Writes nothing to disk.</summary>
    public static void Tp2ReassignCheck()
    {
        using var log = new StreamWriter("_tp2_reassign.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }

        var (_, dir) = Defaults.First(d => d.Game == "tp2");
        if (!Directory.Exists(dir)) { Emit($"SKIP tp2: no levels dir {dir}"); Emit("TP2 REASSIGN SKIPPED"); return; }
        var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
        if (levels.Length == 0) { Emit("SKIP tp2: empty levels dir"); Emit("TP2 REASSIGN SKIPPED"); return; }

        int pass = 0, fail = 0, entProofs = 0, decoProofs = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK " + what); } else { fail++; Emit("  FAIL " + what); } }

        // Validity gate (the S5-5 crash fix): an entity may only be pointed at a name that
        // carries a real entity definition. A bare image-map sprite such as the bonus-level
        // button has no definition and crashes the game, so it must NOT be offered as an entity.
        Check(Tp2SpriteTargets.All(Tp2Entities.IsEntity), "every entity reassign target is a known entity");
        Check(Tp2Entities.IsEntity("star"), "star is a valid entity");
        Check(!Tp2Entities.IsEntity("bonuslevelsbutton"), "bonuslevelsbutton is NOT an entity (the crash case)");
        Check(Tp2Entities.All.Length > 250, $"curated entity catalogue populated ({Tp2Entities.All.Length} defs)");
        foreach (var path in levels)
        {
            if (entProofs >= 6 && decoProofs >= 6) break;
            try
            {
                var doc = LevelLoader.Load(path);
                if (doc.Model is not Tp2Level tp || doc.Profile.Key != "tp2") continue;
                byte[] orig = doc.Original;
                if (!doc.Encode().AsSpan().SequenceEqual(orig))
                { Emit($"FAIL {Path.GetFileName(path)}: baseline encode not byte-exact"); fail++; continue; }

                // ---- entity name reassign (first entity with a non-blank prefab) ----
                var (_, ent) = FirstEntity(tp);
                if (ent is not null && entProofs < 6)
                {
                    string? was = ent.Type;
                    string target = PickTp2Target(was ?? "");
                    Tp2Edit.SetEntitySprite(ent, target);
                    byte[] edited = doc.Encode();
                    var back = Tp2Codec.Instance.Decode(edited);
                    var (_, ent2) = FirstEntity(back);
                    bool persists = ent2?.Type == target;
                    bool counts = back.Layers.Count == tp.Layers.Count &&
                                  back.Layers.Sum(l => l.Entities.Count) == tp.Layers.Sum(l => l.Entities.Count) &&
                                  back.Layers.Sum(l => l.Decos.Count) == tp.Layers.Sum(l => l.Decos.Count);
                    var re = Tp2Codec.Instance.Encode(Tp2Codec.Instance.Decode(edited));
                    bool stable = re.AsSpan().SequenceEqual(edited);
                    ent.Type = was;                       // revert the field directly
                    bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
                    bool ok = persists && counts && stable && revert && target != was;
                    Emit($"{(ok ? "OK  " : "FAIL")} {Path.GetFileName(path),-24} entity '{was}'->'{target}' " +
                         $"persists={persists} counts={counts} stable={stable} revert={revert}");
                    if (ok) pass++; else fail++; entProofs++;
                }

                // ---- decoration sprite reassign ----
                // Prefer a deco whose immediate follower is a type-0 inheritor, so the
                // run-length guard is genuinely exercised (editing it must NOT repaint the
                // follower); fall back to the first deco when a level has no such chain.
                var (dli, deci, deco) = FirstDecoWithInheritor(tp);
                if (deco is not null && decoProofs < 6)
                {
                    string? wasName = Tp2DecoName(tp, dli, deci);
                    string target = PickTp2Target(wasName ?? "");
                    // snapshot what a revert must restore: the deco fields + any appended tile
                    (int type, string? str, int cell, bool hs, bool hc) = (deco.Type, deco.String, deco.Cell, deco.HasString, deco.HasCell);
                    int tilesBefore = tp.TileTypes.Count;
                    bool present = tp.TileTypes.Contains(target);

                    // Run-length guard: the follower (a type-0 inheritor) must keep the name
                    // it resolved to before the edit, and a revert must restore its fields too.
                    var decos = tp.Layers[dli].Decos;
                    Tp2Deco? next = deci + 1 < decos.Count ? decos[deci + 1] : null;
                    (int type, string? str, int cell, bool hs, bool hc) nsnap =
                        next is null ? default : (next.Type, next.String, next.Cell, next.HasString, next.HasCell);
                    string? nextWas = Tp2DecoName(tp, dli, deci + 1);

                    Tp2Edit.SetDecoSprite(tp, deco, target);
                    byte[] edited = doc.Encode();
                    var back = Tp2Codec.Instance.Decode(edited);
                    string? readName = Tp2DecoName(back, dli, deci);
                    bool persists = readName == target;
                    bool noCascade = next is null || Tp2DecoName(back, dli, deci + 1) == nextWas;
                    bool registered = back.TileTypes.Contains(target) &&
                                      back.TileTypeCount == back.TileTypes.Count;
                    var re = Tp2Codec.Instance.Encode(Tp2Codec.Instance.Decode(edited));
                    bool stable = re.AsSpan().SequenceEqual(edited);
                    // revert the deco record (+ any follower we materialised) and any tile we
                    // appended, then require the exact bytes back
                    deco.Type = type; deco.String = str; deco.Cell = cell; deco.HasString = hs; deco.HasCell = hc;
                    if (next is not null)
                    { next.Type = nsnap.type; next.String = nsnap.str; next.Cell = nsnap.cell; next.HasString = nsnap.hs; next.HasCell = nsnap.hc; }
                    if (!present && tp.TileTypes.Count > tilesBefore)
                    {
                        tp.TileTypes.RemoveAt(tp.TileTypes.Count - 1);
                        tp.TileTypeCount = tp.TileTypes.Count;
                    }
                    bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
                    bool ok = persists && noCascade && registered && stable && revert && target != wasName;
                    Emit($"{(ok ? "OK  " : "FAIL")} {Path.GetFileName(path),-24} deco   '{wasName}'->'{target}' " +
                         $"persists={persists} noCascade={noCascade} registered={registered} stable={stable} revert={revert}");
                    if (ok) pass++; else fail++; decoProofs++;
                }
            }
            catch (Exception ex) { fail++; Emit($"FAIL {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"TP2 REASSIGN: {pass} pass, {fail} fail (entity {entProofs}, deco {decoProofs})");
        Emit(fail == 0 && entProofs > 0 && decoProofs > 0 ? "TP2 REASSIGN GREEN" : "TP2 REASSIGN FAILURES");
    }

    // ===================================================== S-D13 flip edit ==
    //
    // The decoration facing write is the first deco edit that can legally CHANGE the record's byte
    // length in both directions: ElementDecoration::writeBinary emits the 24-byte block only when at
    // least one of the two flags / four clip ints is set, so flipping a record on materialises the
    // block and flipping it back off drops it again. "Revert produces the original bytes" is therefore
    // the whole proof — it shows the insert and the removal are exact inverses and that no other
    // record moved. It also guards the read side: the flags must come back through the codec's own
    // decode, not just the buffer we mutated.

    /// <summary>Headless proof of the decoration facing edit on all four families. For a few
    /// decorations of one stock level per game it asserts: the untouched level round-trips
    /// byte-exactly (I1); the write lands and the re-decoded projection reports the new flags for
    /// that decoration ONLY (I3); the edited file is itself lossless (re-decode → re-encode is
    /// byte-exact); the record grew or shrank by whole 24-byte blocks (or not at all); and restoring
    /// the original flags reproduces the ORIGINAL bytes. Never writes to disk.</summary>
    public static void FlipCheck()
    {
        using var log = new StreamWriter("_flip.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            string? picked = null;
            var cand = new List<int>();
            foreach (var lv in Directory.GetFiles(dir, "*.bin").OrderBy(f => f).Take(6))
            {
                List<SceneCross> ds;
                try { ds = LevelLoader.Load(lv).Scene.Crosses
                        .Where(c => c.Kind == SceneKind.Deco && SceneProjector.IsFlippable(c.Source)).ToList(); }
                catch { continue; }
                if (ds.Count < 2) continue;
                // take a mix: one already-flipped record (block present) plus plain ones (usually none)
                int first = ds.FindIndex(c => { var (a, b) = SceneProjector.GetFlip(c.Source); return a || b; });
                picked = lv;
                if (first >= 0) cand.Add(first);
                for (int i = 0; i < ds.Count && cand.Count < 4; i++)
                    if (!cand.Contains(i)) cand.Add(i);
                break;
            }
            if (picked is null) { Emit($"SKIP {game}: no flippable decorations"); continue; }
            Emit($"{game}: {Path.GetFileName(picked)}");
            foreach (int idx in cand)
            {
                try
                {
                    var (ok, note) = TestFlip(picked, idx);
                    if (ok) pass++; else fail++;
                    Emit($"  {(ok ? "OK  " : "FAIL")} deco #{idx} {note}");
                }
                catch (Exception ex) { fail++; Emit($"  FAIL {game} deco #{idx}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        Emit("");
        Emit($"FLIPS: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "FLIP EDIT GREEN" : "FLIP EDIT FAILURES");
    }

    // One decoration's flip proof on a freshly loaded doc, so each assertion starts from the
    // shipped bytes. `idx` indexes the level's flippable decorations in projection order.
    private static (bool Ok, string Note) TestFlip(string level, int idx)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        if (!doc.Encode().AsSpan().SequenceEqual(orig)) return (false, "baseline encode not byte-exact");

        var all0 = doc.Scene.Crosses.Where(c => c.Kind == SceneKind.Deco).ToList();
        var positions = all0.Select((c, i) => (c, i)).Where(t => SceneProjector.IsFlippable(t.c.Source)).ToList();
        if (idx >= positions.Count) return (false, "no such decoration");
        var (cr, pos) = positions[idx];
        var (fx0, fy0) = SceneProjector.GetFlip(cr.Source);
        // Snapshot every decoration's flags FIRST: a Tasty Blue / TP2 source is the live model
        // object the edit mutates in place, so reading it back after the write proves nothing.
        var snap = all0.Select(c => SceneProjector.GetFlip(c.Source)).ToList();

        // Move to the opposite state: both flags set, or both cleared when the record carried them.
        bool on = !(fx0 || fy0);
        var (nx, ny) = on ? (true, true) : (false, false);
        if (!SceneProjector.TrySetFlip(cr.Source, nx, ny, out bool resized)) return (false, "write refused");

        byte[] edited = doc.Encode();
        bool landed = !edited.AsSpan().SequenceEqual(orig);
        long delta = edited.LongLength - orig.LongLength;

        // Re-decode the written bytes and walk the decorations again: only the touched one changes.
        var back = doc.Profile.Decode(edited);
        var all2 = SceneProjector.Project(back).Crosses.Where(c => c.Kind == SceneKind.Deco).ToList();
        bool count = all2.Count == all0.Count;
        int differ = 0, at = -1;
        for (int i = 0; i < Math.Min(snap.Count, all2.Count); i++)
        {
            if (snap[i] != SceneProjector.GetFlip(all2[i].Source)) { differ++; at = i; }
        }
        var (gotX, gotY) = count ? SceneProjector.GetFlip(all2[pos].Source) : (false, false);
        bool persists = count && gotX == nx && gotY == ny;
        bool localized = differ == 1 && at == pos;
        bool stable = doc.Profile.Codec.Encode(back).AsSpan().SequenceEqual(edited);

        // Undo it by hand: the record must shrink/grow back and the file must be the shipped bytes.
        SceneProjector.TrySetFlip(cr.Source, fx0, fy0, out bool resized2);
        bool revert = doc.Encode().AsSpan().SequenceEqual(orig);

        bool blockOk = delta is 0 or 24 or -24 && resized == (delta != 0) && resized2 == (delta != 0);
        bool ok = landed && blockOk && persists && localized && stable && revert;
        return (ok, $"({fx0},{fy0})->({nx},{ny}) landed={landed} delta={delta} persists={persists} " +
                    $"localized={localized} stable={stable} revert={revert}");
    }

    /// <summary>Headless proof of the S5-5 decoration sprite reassign for Tasty Blue and the
    /// ElementEngine games (Forever / Final Bite). For one stock level of each game it checks
    /// both reassigning a decoration to a name ALREADY in the level's <c>tileTypes</c> table and
    /// to a brand-NEW cell name, asserting each time that: (a) the untouched level still
    /// round-trips byte-exactly (I1 unaffected by the new code); (b) after the edit the
    /// decoration re-decodes to the new sprite; (c) exactly one decoration name changed — no
    /// run-length cascade onto the followers that used to inherit through it; (d) a new name
    /// appends one <c>tileTypes</c> entry (an existing one does not); and (e) the edited file
    /// re-encodes to itself (the in-app stability gate). Operates on freshly loaded docs, so it
    /// never mutates shared state and never writes to disk.</summary>
    public static void DecoEditCheck()
    {
        using var log = new StreamWriter("_decoedit.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;

        foreach (var (game, dir) in Defaults.Where(d => d.Game is "tb" or "f4" or "f5"))
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).FirstOrDefault();
            if (level is null) { Emit($"SKIP {game}: empty levels dir"); continue; }
            Emit($"{game}: {Path.GetFileName(level)}");
            foreach (bool useExisting in new[] { true, false })
            {
                try
                {
                    var (ok, note) = TestDecoEdit(game, level, useExisting);
                    if (ok) pass++; else fail++;
                    Emit($"  {(ok ? "OK  " : "FAIL")} {(useExisting ? "existing-name" : "new-name    ")} {note}");
                }
                catch (Exception ex) { fail++; Emit($"  FAIL {game} {(useExisting ? "existing" : "new")}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        Emit("");
        Emit($"DECO EDIT: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "DECO EDIT GREEN" : "DECO EDIT FAILURES");
    }

    // One decoration reassign assertion for a given game/level. `useExisting` points the deco
    // at another name already in tileTypes; otherwise at a fresh cell name absent from the table.
    private static (bool Ok, string Note) TestDecoEdit(string game, string level, bool useExisting)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        if (!doc.Encode().AsSpan().SequenceEqual(orig))
            return (false, "baseline encode not byte-exact");

        // Ordered resolved sprite names of every decoration, before the edit.
        var beforeNames = DecoNames(doc.Model);

        // Pick a decoration to edit + the apply/target plumbing per family.
        string? wasName = null; int seq = -1; int tilesBefore = 0; Func<string, bool>? apply = null;
        int dseq = -1;
        switch (doc.Model)
        {
            case TbLevel lv:
                tilesBefore = lv.TileTypes.Count;
                foreach (var c in doc.Scene.Crosses)
                {
                    if (c.Kind != SceneKind.Deco) continue; dseq++;
                    if (c.Source is TbDeco d && d.Raw.Length >= 1 && d.Raw[0] == 0x01 && d.Name is { Length: > 0 })
                    { wasName = d.Name; seq = dseq; apply = name => TbEdit.SetDecoSprite(lv, d, name); break; }
                }
                break;
            case ElementLevel el:
                tilesBefore = el.TileTypes.Count;
                foreach (var c in doc.Scene.Crosses)
                {
                    if (c.Kind != SceneKind.Deco) continue; dseq++;
                    if (c.Source is DecoEditHandle h && c.SpriteName is { Length: > 0 })
                    { wasName = c.SpriteName; seq = dseq; apply = name => ElemEdit.SetDecoSprite(h, name); break; }
                }
                break;
            default:
                return (false, $"unexpected model for {game}");
        }
        if (apply is null || seq < 0)
            return (false, "no suitable decoration found to edit");

        var tiles = doc.Model is TbLevel lvt ? lvt.TileTypes : ((ElementLevel)doc.Model).TileTypes;
        string target = useExisting
            ? (tiles.FirstOrDefault(t => t is { Length: > 0 } && t != wasName) ?? "")
            : "zz_probe_new_cell";
        if (target.Length == 0) return (false, "no alternative existing tile name (n/a)");

        bool applied = apply(target);
        if (!applied) return (false, $"SetDecoSprite('{target}') refused");

        byte[] edited = doc.Encode();
        object back = doc.Profile.Decode(edited);
        var afterNames = DecoNames(back);

        bool lenOk = afterNames.Count == beforeNames.Count;
        bool persists = lenOk && seq < afterNames.Count && afterNames[seq] == target;
        int changed = 0;
        if (lenOk) for (int i = 0; i < beforeNames.Count; i++) if (beforeNames[i] != afterNames[i]) changed++;
        bool exactlyOne = lenOk && changed == 1;
        int tilesAfter = TileCount(back);
        bool tilesOk = useExisting ? tilesAfter == tilesBefore : tilesAfter == tilesBefore + 1;
        byte[] re = doc.Profile.Codec.Encode(doc.Profile.Decode(edited));
        bool stable = re.AsSpan().SequenceEqual(edited);

        bool ok = persists && exactlyOne && tilesOk && stable && target != wasName;
        string note = $"'{wasName}'->'{target}' persists={persists} onlyOneChanged={exactlyOne}(n={changed}) " +
                      $"tiles {tilesBefore}->{tilesAfter} ok={tilesOk} stable={stable}";
        return (ok, note);
    }

    // Ordered resolved sprite names of every decoration the projector would draw for a model.
    private static List<string?> DecoNames(object model) =>
        SceneProjector.Project(model).Crosses
            .Where(c => c.Kind == SceneKind.Deco).Select(c => c.SpriteName).ToList();

    private static int TileCount(object model) => model switch
    {
        TbLevel tb => tb.TileTypes.Count,
        ElementLevel el => el.TileTypes.Count,
        _ => -1,
    };

    /// <summary>D6 decoration move + rotate proof for Tasty Blue / Forever / Final Bite. Per
    /// family it loads one stock level that actually has a decoration and, driving the exact
    /// <see cref="SceneProjector"/> edit paths the viewport uses, asserts a move (a) changes only
    /// the moved deco's absolute position — every later record in the chained section keeps its
    /// place via the successor compensation, (b) leaves every rotation untouched, (c) touches a
    /// handful of bytes (localised, I3), (d) re-encodes to itself (the in-app stability gate), and
    /// (e) round-trips through the same HistoryService the UI undoes with. A separate load proves
    /// a rotation changes only one record's rotation. Writes nothing to disk.</summary>
    public static void DecoOpsCheck()
    {
        using var log = new StreamWriter("_decodops.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;

        foreach (var (game, dir) in Defaults.Where(d => d.Game is "tb" or "f4" or "f5"))
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = Directory.EnumerateFiles(dir, "*.bin").OrderBy(f => f).Take(80).FirstOrDefault(
                f => { try { return SceneProjector.Project(LevelLoader.Load(f).Model).Crosses.Any(c => c.Kind == SceneKind.Deco); }
                       catch { return false; } });
            if (level is null) { Emit($"SKIP {game}: no level with a decoration"); continue; }
            Emit($"{game}: {Path.GetFileName(level)}");

            try { var (ok, note) = TestDecoMove(level); if (ok) pass++; else fail++; Emit($"  {(ok ? "OK  " : "FAIL")} move:   {note}"); }
            catch (Exception ex) { fail++; Emit($"  FAIL {game} move: {ex.GetType().Name}: {ex.Message}"); }
            try { var (ok, note) = TestDecoRotate(level); if (ok) pass++; else fail++; Emit($"  {(ok ? "OK  " : "FAIL")} rotate: {note}"); }
            catch (Exception ex) { fail++; Emit($"  FAIL {game} rotate: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"DECO OPS: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "DECO OPS GREEN" : "DECO OPS FAILURES");
    }

    private static (bool Ok, string Note) TestDecoMove(string level)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        if (!doc.Encode().AsSpan().SequenceEqual(orig)) return (false, "baseline encode not byte-exact");
        var hist = new TastyEditor.App.Editing.HistoryService();
        hist.Seed(doc);

        var decos = doc.Scene.Crosses.Where(c => c.Kind == SceneKind.Deco).ToList();
        var before = DecoPts(doc.Model);
        const double mdx = 3, mdy = -2;
        int ti = -1; byte[] editedM = orig;
        for (int k = 0; k < decos.Count; k++)
        {
            if (!SceneProjector.IsMovable(decos[k].Source)) continue;
            if (!SceneProjector.TryMoveEntity(decos[k].Source, mdx, mdy)) continue;
            var e = doc.Encode();
            if (!e.AsSpan().SequenceEqual(orig)) { ti = k; editedM = e; break; }
        }
        if (ti < 0) return (false, "no decoration could be moved");
        hist.Commit(doc);

        int diffM = DiffBytes(orig, editedM);
        var afterM = DecoPts(doc.Profile.Decode(editedM));
        bool countOk = before.Count == afterM.Count;
        int posChanged = 0; bool rotKept = true;
        for (int k = 0; k < Math.Min(before.Count, afterM.Count); k++)
        {
            if (!(Near(before[k].X, afterM[k].X) && Near(before[k].Y, afterM[k].Y))) posChanged++;
            if (!Near(before[k].Rot, afterM[k].Rot)) rotKept = false;
        }
        bool targetMoved = ti < afterM.Count
            && Near(afterM[ti].X, before[ti].X + mdx, 0.05) && Near(afterM[ti].Y, before[ti].Y + mdy, 0.05);
        bool stableM = doc.Profile.Codec.Encode(doc.Profile.Decode(editedM)).AsSpan().SequenceEqual(editedM);

        var u = hist.Undo(); doc.ApplyRestoredModel(doc.Profile.Decode(u!));
        bool undoOk = doc.Encode().AsSpan().SequenceEqual(orig);
        var rd = hist.Redo(); doc.ApplyRestoredModel(doc.Profile.Decode(rd!));
        bool redoOk = doc.Encode().AsSpan().SequenceEqual(editedM);

        bool ok = countOk && posChanged == 1 && rotKept && targetMoved && stableM
                  && diffM > 0 && diffM <= 16 && undoOk && redoOk;
        return (ok, $"deco#{ti} moved(+{mdx},{mdy}) posChanged={posChanged} rotKept={rotKept} " +
                    $"bytes={diffM} stable={stableM} undo={undoOk} redo={redoOk}");
    }

    private static (bool Ok, string Note) TestDecoRotate(string level)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        if (!doc.Encode().AsSpan().SequenceEqual(orig)) return (false, "baseline encode not byte-exact");
        var before = DecoPts(doc.Profile.Decode(orig));

        var decos = doc.Scene.Crosses.Where(c => c.Kind == SceneKind.Deco).ToList();
        int ti = -1; double want = 0; byte[] editedR = orig;
        for (int k = 0; k < decos.Count; k++)
        {
            if (!SceneProjector.IsRotatable(decos[k].Source)) continue;
            double cur = SceneProjector.GetRotationDeg(decos[k].Source) ?? decos[k].RotationDeg;
            want = cur + 37;
            if (!SceneProjector.SetRotationDeg(decos[k].Source, want)) continue;
            var e = doc.Encode();
            if (!e.AsSpan().SequenceEqual(orig)) { ti = k; editedR = e; break; }
        }
        if (ti < 0) return (false, "no decoration could be rotated");

        int diffR = DiffBytes(orig, editedR);
        var afterR = DecoPts(doc.Profile.Decode(editedR));
        bool countOk = before.Count == afterR.Count;
        int rotChanged = 0; bool posKept = true;
        for (int k = 0; k < Math.Min(before.Count, afterR.Count); k++)
        {
            if (!Near(before[k].Rot, afterR[k].Rot)) rotChanged++;
            if (!(Near(before[k].X, afterR[k].X) && Near(before[k].Y, afterR[k].Y))) posKept = false;
        }
        bool targetRotated = ti < afterR.Count && Near(afterR[ti].Rot, want, 0.05);
        bool stableR = doc.Profile.Codec.Encode(doc.Profile.Decode(editedR)).AsSpan().SequenceEqual(editedR);

        bool ok = countOk && rotChanged == 1 && posKept && targetRotated && stableR && diffR <= 4;
        return (ok, $"deco#{ti} rot {before[ti].Rot:0.#}->{want:0.#} rotChanged={rotChanged} posKept={posKept} " +
                    $"bytes={diffR} stable={stableR}");
    }

    // Ordered (x, y, rotation-deg) of every decoration cross the projector draws for a model.
    private static List<(double X, double Y, double Rot)> DecoPts(object model) =>
        SceneProjector.Project(model).Crosses.Where(c => c.Kind == SceneKind.Deco)
            .Select(c => (c.At.X, c.At.Y, c.RotationDeg)).ToList();

    private static bool Near(double a, double b, double tol = 0.02) => Math.Abs(a - b) <= tol;

    // Number of differing byte positions; int.MaxValue when lengths differ (a move/rotate must
    // never resize the file, so a length change is treated as an unbounded diff and fails the bound).
    private static int DiffBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }

    /// <summary>Entity move + rotate proof for Tasty Blue / Forever / Final Bite (and TP2). The
    /// entities of the three raw-span families were the last records without an edit lane: a Tasty
    /// Blue entity already moved (typed position + encoder re-derivation) but never rotated, and a
    /// Forever / Final Bite entity sat in an opaque block that was neither. Driving the exact
    /// <see cref="SceneProjector"/> paths the viewport and inspector use, a move must change only
    /// the dragged entity's absolute position (later records stay put via the successor
    /// compensation / encoder re-derivation), leave every rotation untouched, touch a handful of
    /// bytes (I3) and re-encode to itself (the in-app stability gate); a rotate must change only
    /// one record's rotation, leaving positions fixed. The move round-trips through the same
    /// HistoryService the UI undoes with. Writes nothing to disk.</summary>
    public static void EntityOpsCheck()
    {
        using var log = new StreamWriter("_entops.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = FindEntityLevel(dir);
            if (level is null) { Emit($"SKIP {game}: no level with an editable entity"); continue; }
            Emit($"{game}: {Path.GetFileName(level)}");

            // Move: the three raw-span families splice their chained deltas, and TP2 moves its
            // typed Position (now scaled by EntityK to match the projected At). All four assert
            // the projected-coordinate delta equals the world delta applied.
            {
                try { var (ok, note) = TestEntityMove(level); if (ok) pass++; else fail++; Emit($"  {(ok ? "OK  " : "FAIL")} move:   {note}"); }
                catch (Exception ex) { fail++; Emit($"  FAIL {game} move: {ex.GetType().Name}: {ex.Message}"); }
            }
            try { var (ok, note) = TestEntityRotate(level); if (ok) pass++; else fail++; Emit($"  {(ok ? "OK  " : "FAIL")} rotate: {note}"); }
            catch (Exception ex) { fail++; Emit($"  FAIL {game} rotate: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"ENTITY OPS: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "ENTITY OPS GREEN" : "ENTITY OPS FAILURES");
    }

    private static (bool Ok, string Note) TestEntityMove(string level)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        if (!doc.Encode().AsSpan().SequenceEqual(orig)) return (false, "baseline encode not byte-exact");
        var hist = new TastyEditor.App.Editing.HistoryService();
        hist.Seed(doc);

        var ents = doc.Scene.Crosses.Where(c => c.Kind == SceneKind.Entity).ToList();
        var before = EntPts(doc.Model);
        const double mdx = 5, mdy = -3;
        int ti = -1; byte[] editedM = orig;
        for (int k = 0; k < ents.Count; k++)
        {
            if (!SceneProjector.IsMovable(ents[k].Source)) continue;
            if (!SceneProjector.TryMoveEntity(ents[k].Source, mdx, mdy)) continue;
            var e = doc.Encode();
            if (!e.AsSpan().SequenceEqual(orig)) { ti = k; editedM = e; break; }
        }
        if (ti < 0) return (false, "no entity could be moved");
        hist.Commit(doc);

        int diffM = DiffBytes(orig, editedM);
        var afterM = EntPts(doc.Profile.Decode(editedM));
        bool countOk = before.Count == afterM.Count;
        int posChanged = 0; bool rotKept = true;
        for (int k = 0; k < Math.Min(before.Count, afterM.Count); k++)
        {
            if (!(Near(before[k].X, afterM[k].X, 0.6) && Near(before[k].Y, afterM[k].Y, 0.6))) posChanged++;
            if (!Near(before[k].Rot, afterM[k].Rot)) rotKept = false;
        }
        bool targetMoved = ti < afterM.Count
            && Near(afterM[ti].X, before[ti].X + mdx, 0.6) && Near(afterM[ti].Y, before[ti].Y + mdy, 0.6);
        bool stableM = doc.Profile.Codec.Encode(doc.Profile.Decode(editedM)).AsSpan().SequenceEqual(editedM);

        var u = hist.Undo(); doc.ApplyRestoredModel(doc.Profile.Decode(u!));
        bool undoOk = doc.Encode().AsSpan().SequenceEqual(orig);
        var rd = hist.Redo(); doc.ApplyRestoredModel(doc.Profile.Decode(rd!));
        bool redoOk = doc.Encode().AsSpan().SequenceEqual(editedM);

        bool ok = countOk && posChanged == 1 && rotKept && targetMoved && stableM
                  && diffM > 0 && diffM <= 16 && undoOk && redoOk;
        return (ok, $"entity#{ti} moved(+{mdx},{mdy}) posChanged={posChanged} rotKept={rotKept} " +
                    $"bytes={diffM} stable={stableM} undo={undoOk} redo={redoOk}");
    }

    private static (bool Ok, string Note) TestEntityRotate(string level)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        if (!doc.Encode().AsSpan().SequenceEqual(orig)) return (false, "baseline encode not byte-exact");
        var before = EntPts(doc.Profile.Decode(orig));

        var ents = doc.Scene.Crosses.Where(c => c.Kind == SceneKind.Entity).ToList();
        int ti = -1; double want = 0; byte[] editedR = orig;
        for (int k = 0; k < ents.Count; k++)
        {
            if (!SceneProjector.IsRotatable(ents[k].Source)) continue;
            double cur = SceneProjector.GetRotationDeg(ents[k].Source) ?? ents[k].RotationDeg;
            want = cur + 37;
            if (!SceneProjector.SetRotationDeg(ents[k].Source, want)) continue;
            var e = doc.Encode();
            if (!e.AsSpan().SequenceEqual(orig)) { ti = k; editedR = e; break; }
        }
        if (ti < 0) return (false, "no entity could be rotated");

        int diffR = DiffBytes(orig, editedR);
        var afterR = EntPts(doc.Profile.Decode(editedR));
        bool countOk = before.Count == afterR.Count;
        int rotChanged = 0; bool posKept = true;
        for (int k = 0; k < Math.Min(before.Count, afterR.Count); k++)
        {
            if (!Near(before[k].Rot, afterR[k].Rot)) rotChanged++;
            if (!(Near(before[k].X, afterR[k].X, 0.6) && Near(before[k].Y, afterR[k].Y, 0.6))) posKept = false;
        }
        bool targetRotated = ti < afterR.Count && Near(afterR[ti].Rot, want, 0.05);
        bool stableR = doc.Profile.Codec.Encode(doc.Profile.Decode(editedR)).AsSpan().SequenceEqual(editedR);

        bool ok = countOk && rotChanged == 1 && posKept && targetRotated && stableR && diffR > 0 && diffR <= 4;
        return (ok, $"entity#{ti} rot {before[ti].Rot:0.#}->{want:0.#} rotChanged={rotChanged} posKept={posKept} " +
                    $"bytes={diffR} stable={stableR}");
    }

    // Ordered (x, y, rotation-deg) of every entity cross the projector draws for a model.
    private static List<(double X, double Y, double Rot)> EntPts(object model) =>
        SceneProjector.Project(model).Crosses.Where(c => c.Kind == SceneKind.Entity)
            .Select(c => (c.At.X, c.At.Y, c.RotationDeg)).ToList();

    // ---------------------------------------------- TP2 field parity (D7) --
    //
    // Proves every typed entity / decoration field the new inspector edits survives a
    // decode -> edit -> encode -> re-decode (the value persists), that the edited bytes
    // re-encode to themselves (the in-app stability gate), and that restoring the field
    // returns the EXACT original bytes — the write is localised to that field and never
    // disturbs the level framing (I1). The presence-gated emitter and path-follow blocks are
    // exercised on their 0->1 / 1->0 edges too, since Encode rebuilds them from the model.
    // Operates on freshly loaded docs; writes nothing to disk.
    public static void Tp2FieldsCheck()
    {
        using var log = new StreamWriter("_tp2fields.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK   " + what); } else { fail++; Emit("  FAIL " + what); } }

        var (_, dir) = Defaults.First(d => d.Game == "tp2");
        if (!Directory.Exists(dir)) { Emit("SKIP tp2: no levels dir " + dir); Emit("TP2 FIELDS SKIPPED"); return; }
        var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();

        bool entDone = false, decoDone = false, pathDone = false;
        foreach (var path in levels)
        {
            if (entDone && decoDone && pathDone) break;
            try
            {
                if (!entDone)
                {
                    var doc = LevelLoader.Load(path);
                    var (idx, src) = NthEnt(doc.Model, 0);
                    if (src is not null) { Emit($"entity fields @ {Path.GetFileName(path)}"); EntityProofs(doc, idx, (Tp2Entity)src, Emit, Check); entDone = true; }
                }
                if (!decoDone)
                {
                    var doc = LevelLoader.Load(path);
                    var (idx, src) = NthDeco(doc.Model, 0);
                    if (src is not null) { Emit($"deco fields @ {Path.GetFileName(path)}"); DecoProofs(doc, idx, (Tp2Deco)src, Emit, Check); decoDone = true; }
                }
                if (!pathDone)
                {
                    var doc = LevelLoader.Load(path);
                    var ents = SceneProjector.Project(doc.Model).Crosses
                        .Where(c => c.Kind == SceneKind.Entity && c.Source is Tp2Entity).ToList();
                    for (int k = 0; k < ents.Count; k++)
                    {
                        var nm = FirstPathName(doc.Model, k);
                        if (nm is null) continue;
                        Emit($"follow-path @ {Path.GetFileName(path)}");
                        PathProof(doc, k, (Tp2Entity)ents[k].Source!, nm, Emit, Check);
                        pathDone = true; break;
                    }
                }
            }
            catch (Exception ex) { fail++; Emit($"FAIL {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"TP2 FIELDS: {pass} pass, {fail} fail");
        Emit(fail == 0 && entDone && decoDone && pathDone ? "TP2 FIELDS GREEN" : "TP2 FIELDS FAILURES");
    }

    // ------------------------------------------ raw-span field parity (tb/f4/f5) --
    //
    // The three raw-span families carry every entity/deco as an opaque byte record the codec
    // re-emits verbatim; the inspector edits a handful of FIXED-WIDTH fields in place (entity
    // mass / priority / colour, decoration size / colour). This proves each, per family: a field
    // write changes only that record's bytes (localised, I3), survives a re-decode, keeps the
    // level re-encode-stable (the in-app save gate), and returns the EXACT original bytes when
    // reverted (I1). The behaviour blocks (path / emitter / persist, deco priority) are
    // deliberately NOT exercised: like the oracle's entity_full_body gate they live in a
    // per-prefab tail we preserve verbatim, so they stay TP2-only. Operates on freshly loaded
    // docs; writes nothing to disk.
    public static void FieldsCheck()
    {
        using var log = new StreamWriter("_fields.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK   " + what); } else { fail++; Emit("  FAIL " + what); } }

        int families = 0, covered = 0;
        foreach (var (game, dir) in Defaults)
        {
            if (game == "tp2") continue;               // tp2 has its own fully-typed proof
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = FindEntityLevel(dir);
            if (level is null) { Emit($"SKIP {game}: no level with an editable entity"); continue; }
            families++;
            Emit($"{game}: {Path.GetFileName(level)}");
            try
            {
                var doc = LevelLoader.Load(level);
                byte[] orig = doc.Original;
                Check(doc.Encode().AsSpan().SequenceEqual(orig), $"{game}: baseline encode byte-exact (I1)");

                int ei = -1; object? esrc = null;
                for (int k = 0; ; k++) { var s = NthFieldEnt(doc.Model, k); if (s is null) break; if (SceneProjector.FieldGet(s, "mass") is not null) { ei = k; esrc = s; break; } }
                int di = -1; object? dsrc = null;
                for (int k = 0; ; k++) { var s = NthFieldDeco(doc.Model, k); if (s is null) break; if (SceneProjector.FieldGet(s, "dsize") is not null) { di = k; dsrc = s; break; } }
                if (esrc is not null) EntityFieldProofs(doc, orig, ei, esrc, Emit, Check);
                else Emit("  (no addressable entity body in this level)");
                if (dsrc is not null) DecoFieldProofs(doc, orig, di, dsrc, Emit, Check);
                else Emit("  (no addressable deco in this level)");
                if (esrc is not null && dsrc is not null) covered++;
            }
            catch (Exception ex) { fail++; Emit($"  FAIL {game}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"FIELDS: {pass} pass, {fail} fail ({covered}/{families} families fully covered)");
        Emit(fail == 0 && families > 0 && covered == families ? "FIELDS GREEN" : "FIELDS FAILURES");
    }

    // Diagnostic sweep: run the named tail walk (TailSem) over EVERY entity tail in a stock level of
    // each family and classify how many fully consume the tail vs stop short, so we can tell whether a
    // gap is a systematic grammar hole or a per-record framing quirk.
    public static void TailSurvey()
    {
        foreach (var (game, dir) in Defaults)
        {
            if (game == "tp2") continue;
            if (!Directory.Exists(dir)) { Console.WriteLine($"SKIP {game}"); continue; }
            int whole = 0, short_ = 0, total = 0, shown = 0, atBlockEnd = 0;
            // Framing agreement (S-D8f): the loading code's own read order vs the credibility head scan.
            // `recovered` = blocks the scan could not walk to their count (they used to be dropped whole,
            // entities and all); `mismatch` = blocks both walk, to different boundaries; `drift` = blocks
            // whose measured end is not the span the tiler chose. All three must be explainable, and the
            // numbers are printed so a change in framing is visible rather than assumed.
            int entSecs = 0, recovered = 0, mismatch = 0, drift = 0;
            var oddFrames = new List<string>();     // the blocks where the two framings disagree, named
            var residLens = new Dictionary<int, int>();
            var lastGroups = new Dictionary<string, int>();
            var fullGroups = new Dictionary<string, int>();     // groups named in a FULLY consumed tail
            foreach (var lvl in Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories).Take(40))
            {
                LevelDoc doc; try { doc = LevelLoader.Load(lvl); } catch { continue; }
                if (doc.Model is ElementLevel e2)
                    for (int li = 0; li < e2.Layers.Count; li++)
                        for (int si = 0; si < e2.Layers[li].Count; si++)
                        {
                            var sec = e2.Layers[li][si];
                            if (!sec.Opaque || sec.Count <= 0 || sec.Block.Length == 0) continue;
                            entSecs++;
                            var (sh, sok) = EntityFraming.SplitBlock(sec.Block, 0, sec.Count, sec.Block.Length);
                            var (mh, mok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, e2.Game);
                            bool scanOk = sok && sh.Count == sec.Count, measOk = mok && mh.Count == sec.Count;
                            if (measOk && !scanOk) { recovered++; oddFrames.Add($"{Path.GetFileName(lvl)} L{li}[{si}] "
                                + $"scan found {(sok ? sh.Count : 0)}/{sec.Count}, read order found all {sec.Count}"); }
                            else if (measOk && scanOk)
                            {
                                int diff = 0;
                                for (int k = 0; k < sh.Count; k++) if (sh[k].RecStart != mh[k].RecStart) diff++;
                                if (diff > 0)
                                {
                                    mismatch++;
                                    // Name the two framings: the record names are what say which one the
                                    // loader is reading — a real prefab string means the walk was on a head,
                                    // a fragment means it started inside somebody else's bytes.
                                    string Names(List<EntityHead> hs) => string.Join(",",
                                        hs.Take(4).Select(h => h.Name));
                                    oddFrames.Add($"{Path.GetFileName(lvl)} L{li}[{si}] {diff}/{sec.Count} record starts "
                                        + $"differ (scan first {sh[0].RecStart}, read order first {mh[0].RecStart})\n"
                                        + $"         scan: {Names(sh)}\n         read order: {Names(mh)}");
                                }
                            }
                            if (measOk && EntityFraming.MeasureBlock(sec.Block, 0, sec.Count, e2.Game, out int me)
                                && me != sec.Block.Length) drift++;
                        }
                foreach (var c in SceneProjector.Project(doc.Model).Crosses)
                {
                    if (c.Kind != SceneKind.Entity || !SceneProjector.IsFieldEntity(c.Source)
                        || SceneProjector.SupportsBehaviour(c.Source) || !SceneProjector.IsTailEditable(c.Source)) continue;
                    var nm = SceneProjector.FieldTailNamed(c.Source);
                    if (nm is not { } t || t.Tail.Length < 4) continue;
                    total++;
                    if (t.Consumed == t.Tail.Length)
                    {
                        whole++;
                        foreach (var gp in t.Fields.Select(f => f.Group).Distinct())
                            fullGroups[gp] = fullGroups.GetValueOrDefault(gp) + 1;
                    }
                    else { short_++; int r = t.Tail.Length - t.Consumed; residLens[r] = residLens.GetValueOrDefault(r) + 1;
                        // A residual with no look-ahead left in the block is a section-boundary truncation,
                        // not a grammar hole: the record really does end where the block does.
                        if (SceneProjector.FieldTailAvailable(c.Source) <= t.Tail.Length) atBlockEnd++;
                        string g = t.Fields.Count > 0 ? t.Fields[^1].Group : "(none)"; lastGroups[g] = lastGroups.GetValueOrDefault(g) + 1;
                        if (shown < 4 && r >= 8 && r <= 200)
                        {
                            shown++;
                            int from = Math.Max(0, t.Consumed - 16);
                            int len = Math.Min(t.Tail.Length - from, 64);
                            Console.WriteLine($"    [{Path.GetFileName(lvl)}] last={g} stop@{t.Consumed}/{t.Tail.Length} @{from}..{from + len}: {BitConverter.ToString(t.Tail, from, len)}");
                            var sb2 = new System.Text.StringBuilder();
                            for (int o = from; o + 4 <= t.Tail.Length; o += 4) sb2.Append(BitConverter.ToInt32(t.Tail, o)).Append(' ');
                            Console.WriteLine($"       i32: {sb2}");
                        }
                    }
                }
            }
            Console.WriteLine($"{game}: {total} tails, whole={whole} residual={short_} (residuals at block end={atBlockEnd})");
            Console.WriteLine($"   entity blocks: {entSecs} walked by read order, {recovered} the head scan could not "
                + $"(recovered), {mismatch} framed differently, {drift} whose measured end is not the span's");
            foreach (string s in oddFrames.Take(6)) Console.WriteLine($"      ! {s}");
            Console.WriteLine($"   residual-length histogram: {string.Join(", ", residLens.OrderByDescending(k => k.Value).Select(k => $"{k.Key}B x{k.Value}"))}");
            Console.WriteLine($"   groups named in whole-record walks: {string.Join(", ", fullGroups.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}"))}");
            Console.WriteLine($"   last-group-of-residual: {string.Join(", ", lastGroups.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}"))}");
        }
    }

    // Dump ONE entity behaviour tail end-to-end: full hex + every named field the walk produced (with
    // its offset/type/value) + the bytes the walk stopped on, so the true record structure can be read
    // directly from a real record while chasing a grammar gap. This is a reverse-engineering probe, not
    // an editor surface — the inspector itself never shows raw bytes.
    public static void TailDump(string levelNameFragment, int wantLen)
    {
        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) continue;
            var lvl = Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(levelNameFragment, StringComparison.OrdinalIgnoreCase));
            if (lvl is null) continue;
            LevelDoc doc; try { doc = LevelLoader.Load(lvl); } catch { continue; }
            foreach (var c in SceneProjector.Project(doc.Model).Crosses)
            {
                if (c.Kind != SceneKind.Entity || !SceneProjector.IsTailEditable(c.Source)) continue;
                var nm = SceneProjector.FieldTailNamed(c.Source);
                if (nm is not { } t || t.Tail.Length < 8) continue;
                if (wantLen > 0 && t.Tail.Length != wantLen && t.FrameLen != wantLen) continue;
                Console.WriteLine($"{game} {Path.GetFileName(lvl)}  tail {t.Tail.Length}B  frame={t.FrameLen}  consumed={t.Consumed}  avail={SceneProjector.FieldTailAvailable(c.Source)}");
                Console.WriteLine($"  hex: {BitConverter.ToString(t.Tail)}");
                foreach (var f in t.Fields)
                    Console.WriteLine($"    +{f.LaneOff,3} [{f.Kind,-5}] {f.Group}/{f.Name} = {TailSem.Value(t.Tail, f)}{(f.LaneOff >= t.FrameLen ? "  (past frame, read-only)" : "")}");
                if (t.Consumed < t.Tail.Length)
                    Console.WriteLine($"    UNNAMED @{t.Consumed}: {BitConverter.ToString(t.Tail, t.Consumed, t.Tail.Length - t.Consumed)}");
                return;
            }
        }
        Console.WriteLine("no matching tail found");
    }

    // The want-th raw-span entity source (excludes TP2's typed entity, which the parity proof
    // skips because it has the behaviour blocks). Project enumerates crosses deterministically,
    // so the same index resolves on a freshly re-decoded model.
    private static object? NthFieldEnt(object model, int want)
    {
        int n = 0;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == SceneKind.Entity && SceneProjector.IsFieldEntity(c.Source)
                && !SceneProjector.SupportsBehaviour(c.Source) && n++ == want) return c.Source;
        return null;
    }

    private static object? NthFieldDeco(object model, int want)
    {
        int n = 0;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == SceneKind.Deco && SceneProjector.IsFieldDeco(c.Source)
                && !SceneProjector.SupportsDecoPriority(c.Source) && n++ == want) return c.Source;
        return null;
    }

    private static void EntityFieldProofs(LevelDoc doc, byte[] orig, int idx, object src, Action<string> Emit, Action<bool, string> Check)
    {
        Emit($"  entity#{idx}");
        ScalarProof(doc, orig, idx, src, "mass", (SceneProjector.FieldGet(src, "mass") ?? 1) + 3.5, "mass (size)", isEnt: true, cap: 8, Check);
        ScalarProof(doc, orig, idx, src, "prio", (SceneProjector.FieldGet(src, "prio") ?? 0) + 7, "priority", isEnt: true, cap: 4, Check);
        ColorProof(doc, orig, idx, src, isEnt: true, (0x3a, 0x5c, 0x71), Check);
        NamedTailProof(doc, orig, idx, src, Emit, Check);
    }

    // Prove the NAMED behaviour-tail view (TailSem / FieldTailNamed) that drives the inspector's
    // TP2-style rows. HARD guarantees: (a) the walk is self-validating — every presence gate and every
    // data bool it emits reads exactly 0 or 1; (b) the walk CONSUMES THE WHOLE TAIL (consumed == tail
    // length) — i.e. nothing is left unnamed, which is the entire point of the decompile-driven
    // rewrite; (c) a named edit (int / f64 / f32 / bool, routed by kind through FieldTailSet) localises
    // to <= field-width bytes and reverts to a byte-identical stock file (I1/I3).
    private static void NamedTailProof(LevelDoc doc, byte[] orig, int idx, object src, Action<string> Emit, Action<bool, string> Check)
    {
        if (!SceneProjector.IsTailEditable(src)) return;
        var named = SceneProjector.FieldTailNamed(src);
        if (named is not ({ } nm) || nm.Tail.Length < 4) { Emit("  (no tail to name)"); return; }
        var fields = nm.Fields; int consumed = nm.Consumed; byte[] tail = nm.Tail; int frame = nm.FrameLen;
        if (fields.Count == 0)
        {
            Emit($"  named: walk recognised 0 fields on this record ({tail.Length}B tail, nothing decoded)");
            Check(false, $"named: 0 fields decoded (consumed={consumed}/{tail.Length})");
            return;
        }

        // (a) every gate / data bool must read 0/1. (b) the walk must name the WHOLE tail.
        bool flagsClean = fields.Where(f => f.Kind is TailKind.Gate or TailKind.Bool)
                                .All(f => TailSem.Value(tail, f) is 0 or 1);
        bool wholeTail = consumed == tail.Length;
        var groups = fields.GroupBy(f => f.Group).Select(gr => gr.Key).ToList();
        Check(flagsClean && wholeTail,
            $"named walk covers tail: {fields.Count} fields, gates+bools 0/1={flagsClean}, consumed={consumed}/{tail.Length}B whole={wholeTail} [{string.Join("/", groups)}]");
        if (!wholeTail)
        {
            var sb = new System.Text.StringBuilder();
            for (int o = consumed; o + 4 <= tail.Length; o += 4) sb.Append(BitConverter.ToInt32(tail, o)).Append(' ');
            Emit($"    UNNAMED @{consumed}..{tail.Length} as i32: {sb}");
            Emit($"    UNNAMED hex: {BitConverter.ToString(tail, consumed, tail.Length - consumed)}");
        }

        // (c): edit the first writable value field inside the record's own frame (prefer f64, then
        // int / f32 / bool). A field the lookahead recovered past the frame is display-only.
        TailField? fldN = fields.Cast<TailField?>().FirstOrDefault(f => f!.Value.Kind == TailKind.F64 && f!.Value.EditableWithin(frame))
            ?? fields.Cast<TailField?>().FirstOrDefault(f => f!.Value.EditableWithin(frame));
        if (fldN is null) { Emit("  (no editable value field inside this record's frame)"); return; }
        TailField fld = fldN.Value;
        int w = fld.Width;
        if (fld.LaneOff + w > tail.Length) { Check(false, $"named field {fld.Name} offset {fld.LaneOff}+{w} past tail {tail.Length}"); return; }

        double origv = fld.Kind is TailKind.F64 or TailKind.F32 ? Convert.ToDouble(TailSem.Value(tail, fld)) : Convert.ToInt32(TailSem.Value(tail, fld));
        double newv = fld.Kind switch
        {
            TailKind.F64 => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(origv) ^ 1L),
            TailKind.F32 => origv + 1.0,
            _ => (Convert.ToInt32(origv) ^ 0x01020304),   // int / bool
        };
        bool wrote = SceneProjector.FieldTailSet(src, fld, newv);
        byte[] edited = doc.Encode();
        SceneProjector.FieldTailSet(src, fld, origv);   // restore exact original bytes
        int diff = DiffBytes(orig, edited);
        bool reverted = doc.Encode().AsSpan().SequenceEqual(orig);
        // decodedPersist / stable are best-effort: on Tasty Blue a named write can hit a word the codec
        // treats as structural and refuse a re-read (same caveat the raw lane note carries).
        bool persists = false, stable = false; bool structural = false;
        try
        {
            var re = NthFieldEnt(doc.Profile.Decode(edited), idx);
            var rt = re is not null ? SceneProjector.FieldTailNamed(re) : null;
            if (rt is { } r2 && r2.Fields.Any(x => x.Kind == fld.Kind && x.Name == fld.Name))
            {
                stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
                persists = diff > 0 && diff <= w;
            }
        }
        catch (Exception ex) { structural = true; Emit($"  note: named edit @{fld.LaneOff} hit a structural word on this family ({ex.GetType().Name}); localised + reversible"); }
        Check(wrote && diff > 0 && diff <= w && reverted,
            $"named edit {fld.Group}/{fld.Name}@{fld.LaneOff}({fld.Kind}): bytes={diff} localised={diff <= w} persists={(structural ? "n/a" : persists.ToString())} stable={stable} revert={reverted}");
    }

    // Diagnostic (S-D8f): what a length-changing tail edit does to the WHOLE-FILE framing. Prints every
    // section's count / end / block size before the edit and again after re-decoding the written bytes, so
    // a tiling surprise is visible rather than inferred. `tailframe [gameFragment]`.
    public static void TailFrame(string? frag)
    {
        foreach (var (game, dir) in Defaults)
        {
            if (game == "tp2" || !Directory.Exists(dir)) continue;
            if (frag is not null && !int.TryParse(frag, out _) && !game.Contains(frag)) continue;
            var level = FindEntityLevel(dir);
            if (level is null) continue;
            Console.WriteLine($"== {game}: {Path.GetFileName(level)}");
            var targets = PickTailTargets(level);
            if (targets.Count == 0) { Console.WriteLine("  no target"); continue; }
            int ei = targets[0];
            if (frag is not null && int.TryParse(frag, out int pick) && pick < targets.Count) ei = targets[pick];
            var fresh = FreshEntity(level, ei)!.Value;
            var (doc, src) = (fresh.doc, fresh.src);
            DumpSections(doc.Model, "before      ");
            if (SceneProjector.FieldTailNamed(src) is not { } nm) continue;
            var g = nm.Fields.FirstOrDefault(f => f.Kind == TailKind.Gate && f.ReshapeableWithin(nm.FrameLen)
                && !TailEdit.GateOn(nm.Tail, f));
            if (g.Kind != TailKind.Gate) { Console.WriteLine("  no off gate"); continue; }
            bool did = SceneProjector.FieldTailReshape(src, g, true);
            byte[] edited = doc.Encode();
            Console.WriteLine($"  enable {g.Group} on entity#{ei} -> {did}, file {doc.Original.Length}\u2192{edited.Length}B");
            try { DumpSections(doc.Profile.Decode(edited), "after re-read"); }
            catch (Exception ex) { Console.WriteLine($"  after re-read: {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine();
        }
    }

    private static void DumpSections(object model, string tag)
    {
        if (model is not ElementLevel el) { Console.WriteLine($"  {tag}: not an ElementLevel"); return; }
        for (int li = 0; li < el.Layers.Count; li++)
        {
            var secs = el.Layers[li];
            Console.Write($"  {tag} L{li}: ");
            for (int si = 0; si < secs.Count; si++)
                Console.Write($"[{si} n={secs[si].Count} end={secs[si].EndOff}"
                    + (secs[si].Opaque ? $" blk={secs[si].Block.Length}" : $" recs={secs[si].Records.Count}") + "] ");
            Console.WriteLine();
        }
    }

    // =============================== structural behaviour-tail proof (S-D8f) ===============
    //
    // A flag in a level file has to be an EDITABLE switch, not a number on screen. Toggling a behaviour
    // block changes the record's byte LENGTH, so this proves the whole guard chain on stock levels of
    // each family, driving exactly the calls the inspector makes:
    //   (a) the resize is the right size: enabling adds the block's measured default body, disabling
    //       removes precisely the bytes that block occupied. An edited file is EXPECTED to differ in
    //       length from the stock one (the format carries no offset table); byte-identity is only the
    //       unedited parser proof, so what is asserted here is that the touched record is the ONLY
    //       thing that moved - every other record's bytes come out identical;
    //   (b) the edited file RE-LOADS: the framing oracle re-derives every record, the gate reads back in
    //       the new state, and the named walk still covers the whole resized tail with no residual;
    //   (c) re-encoding the re-loaded file reproduces it byte for byte (so the edit is a fixed point of
    //       load -> save, which is what the in-app save gate asserts);
    //   (d) toggling back restores the stock file EXACTLY when the block had been blank, and otherwise
    //       comes back the same length with every other record untouched - switching a block on hands it
    //       the engine's default body, and the undo history (not the toggle) is what remembers its data.
    // The same four for renaming an embedded engine name and resizing the IndexedCellColours ramp.
    public static void TailStructCheck()
    {
        using var log = new StreamWriter("_tailedit.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK   " + what); } else { fail++; Emit("  FAIL " + what); } }

        int families = 0, covered = 0;
        foreach (var (game, dir) in Defaults)
        {
            if (game == "tp2") continue;                    // TP2 behaviour is typed; it has no tail
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = FindEntityLevel(dir);
            if (level is null) { Emit($"SKIP {game}: no level with an editable entity"); continue; }
            families++;
            Emit($"{game}: {Path.GetFileName(level)}");
            try
            {
                // Several records, chosen from the stock bytes, whose whole tail is named so every block
                // has a known byte extent - and chosen to differ, so all three structural writes get
                // exercised per family rather than riding on one lucky record. Each proof re-loads the
                // level itself, so no proof inherits the residue (or the offsets) of the one before it.
                var targets = PickTailTargets(level);
                if (targets.Count == 0) { Emit("  (no fully-named tail with an editable gate in this level)"); continue; }
                int gates = 0;
                foreach (int ei in targets)
                {
                    Emit($"  entity#{ei}");
                    var seed = FreshEntity(level, ei);
                    if (seed is null) { Emit("  (the record is not there on reload)"); continue; }
                    if (ei == targets[0])
                        Check(seed.Value.doc.Encode().AsSpan().SequenceEqual(seed.Value.doc.Original),
                            $"{game}: baseline encode byte-exact (I1)");
                    gates += GateProofs(level, ei, Emit, Check);
                    StringProof(level, ei, Emit, Check);
                    EnableRenameProof(level, ei, Emit, Check);
                    CountProof(level, ei, Emit, Check);
                }
                if (gates > 0) covered++;
            }
            catch (Exception ex) { fail++; Emit($"  FAIL {game}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"TAILEDIT: {pass} pass, {fail} fail ({covered}/{families} families fully covered)");
        Emit(fail == 0 && families > 0 && covered == families ? "TAIL EDIT GREEN" : "TAIL EDIT FAILURES");
    }

    // Entity records whose tail the grammar explains completely and that carry a gate inside their own
    // byte frame, ordered so the set covers a name to rename, a ramp to resize, a block to turn on and a
    // block to turn off (up to four of them).
    private static List<int> PickTailTargets(string path)
    {
        var doc = LevelLoader.Load(path);
        var cands = new List<(int I, bool Str, bool Count, bool On, bool Off)>();
        for (int k = 0; ; k++)
        {
            var s = NthFieldEnt(doc.Model, k);
            if (s is null) break;
            if (!SceneProjector.IsTailEditable(s) || SceneProjector.FieldTailNamed(s) is not { } t) continue;
            if (t.Consumed != t.Tail.Length) continue;
            var gs = t.Fields.Where(f => f.Kind == TailKind.Gate && f.ReshapeableWithin(t.FrameLen)).ToList();
            if (gs.Count == 0) continue;
            cands.Add((k,
                t.Fields.Any(f => f.Kind == TailKind.Str && f.ReshapeableWithin(t.FrameLen)),
                t.Fields.Any(f => f.Kind == TailKind.Count && f.ReshapeableWithin(t.FrameLen)),
                gs.Any(f => TailEdit.GateOn(t.Tail, f)),
                gs.Any(f => !TailEdit.GateOn(t.Tail, f))));
        }

        var picked = new List<int>();
        void Grab(Func<(int I, bool Str, bool Count, bool On, bool Off), bool> pred)
        {
            foreach (var c in cands)
                if (pred(c) && !picked.Contains(c.I)) { picked.Add(c.I); return; }
        }
        Grab(c => c.Str && c.On);        // rename an enabled block's engine name
        Grab(c => c.Count && c.On);      // reshape a live colour ramp
        Grab(c => c.On && !c.Off);       // switch a data-carrying block off
        Grab(c => c.Off && !c.On);       // switch a blank block on
        Grab(c => true);
        foreach (var c in cands) { if (picked.Count >= 4) break; if (!picked.Contains(c.I)) picked.Add(c.I); }
        return picked;
    }

    // A pristine load of the stock level plus the record under test, so each proof starts from the bytes
    // the game shipped (the edit helpers mutate the loaded model in place, like the editor does).
    private static (LevelDoc doc, object src)? FreshEntity(string path, int idx)
    {
        var d = LevelLoader.Load(path);
        var s = NthFieldEnt(d.Model, idx);
        return s is null || !SceneProjector.IsTailEditable(s) ? null : (d, s);
    }

    // Does the walk see this block as actually present (carrying a body) in these tail bytes?
    private static bool? BlockState(byte[] tail, string gk, string group)
    {
        foreach (var b in TailSem.Layout(tail, gk).Blocks)
            if (b.Group == group) return b.BodyLen > 0;
        return null;
    }

    private static bool AllZero(byte[] buf, int off, int len)
    {
        for (int i = 0; i < len; i++) if (buf[off + i] != 0) return false;
        return true;
    }

    // A fingerprint of everything the level projects to: every cross's kind / layer / label / id / spot,
    // plus the exact behaviour-tail bytes of the records that carry one.
    private static List<string> SceneSigs(object model)
    {
        var list = new List<string>();
        foreach (var c in SceneProjector.Project(model).Crosses)
        {
            string tail = SceneProjector.FieldTailNamed(c.Source) is { } t ? Convert.ToHexString(t.Tail) : "";
            list.Add($"{c.Kind}|{c.Layer}|{c.Label}|{c.Id}|{c.At.X:R}|{c.At.Y:R}|{tail}");
        }
        return list;
    }

    // The length-changing write touched exactly one record: the same set of crosses comes back and only
    // one of them (the record being edited) differs. This - not byte-identity - is what an EDIT must hold;
    // byte-identity is only the proof for an unedited file. `why` explains a failure, for the log.
    private static bool OnlyOneRecordMoved(List<string> before, LevelDoc doc, byte[] edited, out string why)
    {
        why = "";
        try
        {
            var after = SceneSigs(doc.Profile.Decode(edited));
            if (before.Count != after.Count) { why = $"cross count {before.Count}\u2192{after.Count}"; return false; }
            int diff = 0; string first = "";
            for (int k = 0; k < before.Count; k++)
                if (before[k] != after[k])
                {
                    diff++;
                    // Keep the first offender's two fingerprints (tails truncated) so a framing surprise is
                    // readable straight out of the log instead of being guessed at.
                    if (first.Length == 0)
                        first = $"{Cut(before[k])} <> {Cut(after[k])}";
                }
            if (diff <= 1) return true;
            why = $"{diff} records changed (first {first})";
            return false;
        }
        catch (Exception ex) { why = ex.GetType().Name; return false; }
    }

    private static string Cut(string s) => s.Length <= 96 ? s : s[..48] + "\u2026" + s[^44..];

    // Flip every presence gate of one record and flip it back, each pass from a fresh load of the stock
    // level. Returns how many gates were proven.
    private static int GateProofs(string path, int idx, Action<string> Emit, Action<bool, string> Check)
    {
        var seed = FreshEntity(path, idx);
        if (seed is null || SceneProjector.FieldTailNamed(seed.Value.src) is not { } n0) return 0;
        int total = n0.Fields.Count(f => f.Kind == TailKind.Gate && f.ReshapeableWithin(n0.FrameLen));
        int done = 0;
        for (int gi = 0; gi < total; gi++)
        {
            var fresh = FreshEntity(path, idx);
            if (fresh is null) { Check(false, $"gate #{gi}: the record is gone on reload"); continue; }
            var (doc, src) = fresh.Value;
            byte[] orig = doc.Original;
            if (SceneProjector.FieldTailNamed(src) is not { } nm) break;
            var gates = nm.Fields.Where(f => f.Kind == TailKind.Gate && f.ReshapeableWithin(nm.FrameLen)).ToList();
            if (gi >= gates.Count) break;                    // the walk listed fewer gates than counted
            var g = gates[gi];
            string gk = SceneProjector.TailGameKey(src);
            TailBlock blk = TailSem.Layout(nm.Tail, gk).Blocks.FirstOrDefault(b => b.GateOff == g.LaneOff && b.Group == g.Group);
            if (blk.GateOff != g.LaneOff || blk.Group != g.Group) continue;
            bool was = TailEdit.GateOn(nm.Tail, g);
            int want = was ? -blk.BodyLen : TailSem.DefaultBodySize(gk, g.Group);
            // Switching a block back on hands it the ENGINE'S OWN default body, not the data it happened
            // to be carrying (undo is what remembers that), so only a block that was already blank - or
            // was off to begin with - has to return the stock file byte for byte.
            bool plain = !was || AllZero(nm.Tail, blk.BodyOff, blk.BodyLen);
            List<string> sigs0 = SceneSigs(doc.Model);

            if (!SceneProjector.FieldTailReshape(src, g, !was))
            {
                Check(false, $"gate {g.Group}/{g.Name}: toggling {(was ? "off" : "on")} was refused \u2014 "
                    + SceneProjector.FieldTailWhy(src, g, !was));
                done++;
                continue;
            }
            byte[] edited = doc.Encode();
            int delta = edited.Length - orig.Length;
            var re = TryEntityAfter(doc, edited, idx);
            var r1 = re is not null ? SceneProjector.FieldTailNamed(re) : null;
            bool reads = r1 is { } a && BlockState(a.Tail, gk, g.Group) == !was;
            bool whole = r1 is { } b && b.Consumed == b.Tail.Length;
            bool stable = StableReEncode(doc, edited);
            bool alone = OnlyOneRecordMoved(sigs0, doc, edited, out string alonewhy);
            bool restored = SceneProjector.FieldTailReshape(src, g, was);
            byte[] rev = doc.Encode();
            var r2re = TryEntityAfter(doc, rev, idx);
            var r2 = r2re is not null ? SceneProjector.FieldTailNamed(r2re) : null;
            // A blank block must come home byte for byte. A block that held data comes back as the
            // engine's own default - a legal, fully-named record of its own (possibly shorter) length,
            // with no other record in the file disturbed.
            bool back = restored && (plain
                ? rev.AsSpan().SequenceEqual(orig)
                : r2 is { } c && c.Consumed == c.Tail.Length
                  && BlockState(c.Tail, gk, g.Group) == was && OnlyOneRecordMoved(sigs0, doc, rev, out _));
            int revGrow = rev.Length - orig.Length;
            string how = plain ? "byte-exact" : $"default-body ({revGrow}B)";
            Check(delta == want && reads && whole && stable && alone && back,
                $"gate {g.Group}/{g.Name} {(was ? "off" : "on")}: delta={delta}B want={want}B reads={reads} wholeTail={whole} stable={stable} local={alone}{(alone ? "" : " " + alonewhy)} revert={how}{(back ? "" : " FAILED")}");
            done++;
        }
        if (done == 0) Emit("  (no gate on this record was editable)");
        return done;
    }

    // Rename an embedded engine name (pathname / definedareaname / pointlistname / scriptable / link).
    // null = the record has no enabled block carrying a name (nothing to prove, not a failure).
    private static bool? StringProof(string path, int idx, Action<string> Emit, Action<bool, string> Check)
    {
        var seed = FreshEntity(path, idx);
        if (seed is null || SceneProjector.FieldTailNamed(seed.Value.src) is not { } n0) return null;
        var names = n0.Fields.Where(f => f.Kind == TailKind.Str && f.ReshapeableWithin(n0.FrameLen)).ToList();
        if (names.Count == 0) { Emit("  (no enabled block on this record carries a name)"); return null; }
        bool ok = true;

        foreach (var f in names)
        {
            var fresh = FreshEntity(path, idx);
            if (fresh is null) continue;
            var (doc, src) = fresh.Value;
            byte[] orig = doc.Original;
            if (SceneProjector.FieldTailNamed(src) is not { } nm) continue;
            var fld = nm.Fields.FirstOrDefault(x => x.Kind == TailKind.Str && x.Group == f.Group && x.Name == f.Name);
            if (fld.Kind != TailKind.Str) continue;
            List<string> sigs0 = SceneSigs(doc.Model);

            string was = TailSem.Value(nm.Tail, fld) as string ?? "";
            string now = was.Length > 48 ? "zz_path" : was + "_x";    // stay short and keep it credible
            if (!SceneProjector.FieldTailReshape(src, fld, now))
            {
                Check(false, $"rename {f.Group}/{f.Name}: refused (was \u201c{was}\u201d) \u2014 "
                    + SceneProjector.FieldTailWhy(src, fld, now));
                ok = false;
                continue;
            }

            byte[] edited = doc.Encode();
            int delta = edited.Length - orig.Length;
            var re = TryEntityAfter(doc, edited, idx);
            var r1 = re is not null ? SceneProjector.FieldTailNamed(re) : null;
            string? got = null;
            bool whole = r1 is { } a && a.Consumed == a.Tail.Length;
            if (r1 is { } b)
            {
                var mf = b.Fields.FirstOrDefault(x => x.Kind == TailKind.Str && x.Group == f.Group && x.Name == f.Name);
                if (mf.Kind == TailKind.Str) got = TailSem.Value(b.Tail, mf) as string;
            }
            bool reads = got == now;
            bool stable = StableReEncode(doc, edited);
            bool alone = OnlyOneRecordMoved(sigs0, doc, edited, out string alonewhy);
            bool back = SceneProjector.FieldTailReshape(src, fld, was) && doc.Encode().AsSpan().SequenceEqual(orig);
            Check(reads && whole && stable && alone && back,
                $"rename {f.Group}/{f.Name}: \u2192 \u201c{now}\u201d delta={delta}B reads={reads} ({got}) wholeTail={whole} stable={stable} local={alone}{(alone ? "" : " " + alonewhy)} revert={back}");
            ok &= back;
        }
        return ok;
    }

    // The flow a user actually follows: switch a behaviour block on, then type the engine name it wants.
    // A freshly enabled block carries an empty name, so this proves the two writes compose, and that
    // switching the block off again hands the stock bytes back exactly (it was blank to begin with).
    // null = no switch-off gate on this record opens a block that carries a name.
    private static bool? EnableRenameProof(string path, int idx, Action<string> Emit, Action<bool, string> Check)
    {
        var seed = FreshEntity(path, idx);
        if (seed is null || SceneProjector.FieldTailNamed(seed.Value.src) is not { } n0) return null;
        var offs = n0.Fields.Where(f => f.Kind == TailKind.Gate && f.ReshapeableWithin(n0.FrameLen)
            && !TailEdit.GateOn(n0.Tail, f)).ToList();
        bool tried = false;

        foreach (var g in offs)
        {
            var fresh = FreshEntity(path, idx);
            if (fresh is null) continue;
            var (doc, src) = fresh.Value;
            byte[] orig = doc.Original;
            if (SceneProjector.FieldTailNamed(src) is not { } nm) continue;
            var gf = nm.Fields.FirstOrDefault(f => f.Kind == TailKind.Gate && f.Group == g.Group);
            if (gf.Kind != TailKind.Gate) continue;
            List<string> sigs0 = SceneSigs(doc.Model);        // before the record grows
            tried = true;

            if (!SceneProjector.FieldTailReshape(src, gf, true)) continue;
            if (SceneProjector.FieldTailNamed(src) is not { } nm2) continue;   // the block now exists
            var sf = nm2.Fields.FirstOrDefault(f => f.Kind == TailKind.Str && f.Group == gf.Group
                                                   && f.ReshapeableWithin(nm2.FrameLen));
            if (sf.Kind != TailKind.Str) continue;            // this block carries no name to type

            if (!SceneProjector.FieldTailReshape(src, sf, "zz_probe"))
            {
                Check(false, $"enable+rename {gf.Group}/{sf.Name}: the empty name refused to be typed into \u2014 "
                    + SceneProjector.FieldTailWhy(src, sf, "zz_probe"));
                return false;
            }
            byte[] edited = doc.Encode();
            var re = TryEntityAfter(doc, edited, idx);
            var r1 = re is not null ? SceneProjector.FieldTailNamed(re) : null;
            string? got = null;
            bool whole = r1 is { } a && a.Consumed == a.Tail.Length;
            if (r1 is { } b)
            {
                var mf = b.Fields.FirstOrDefault(x => x.Kind == TailKind.Str && x.Group == gf.Group);
                if (mf.Kind == TailKind.Str) got = TailSem.Value(b.Tail, mf) as string;
            }
            bool stable = StableReEncode(doc, edited);
            bool alone = OnlyOneRecordMoved(sigs0, doc, edited, out string alonewhy2);
            bool back = SceneProjector.FieldTailReshape(src, gf, false) && doc.Encode().AsSpan().SequenceEqual(orig);
            Check(got == "zz_probe" && whole && stable && alone && back,
                $"enable+rename {gf.Group}/{sf.Name}: \u2192 \u201czz_probe\u201d reads={(got == "zz_probe")} ({got}) wholeTail={whole} stable={stable} local={alone}{(alone ? "" : " " + alonewhy2)} offAgain={back}");
            return back;
        }
        if (!tried) Emit("  (no switchable-off block on this record carries a name)");
        return null;
    }

    // Grow the IndexedCellColours ramp by one entry and back. null for a build with no such block (tb).
    private static bool? CountProof(string path, int idx, Action<string> Emit, Action<bool, string> Check)
    {
        var seed = FreshEntity(path, idx);
        if (seed is null || SceneProjector.FieldTailNamed(seed.Value.src) is not { } n0) return null;
        var c0 = n0.Fields.FirstOrDefault(f => f.Kind == TailKind.Count && f.ReshapeableWithin(n0.FrameLen));
        if (c0.Kind != TailKind.Count) { Emit("  (this build's record carries no variable-length ramp)"); return null; }

        var fresh = FreshEntity(path, idx);
        if (fresh is null) return null;
        var (doc, src) = fresh.Value;
        byte[] orig = doc.Original;
        if (SceneProjector.FieldTailNamed(src) is not { } nm) return null;
        var cf = nm.Fields.FirstOrDefault(f => f.Kind == TailKind.Count && f.Group == c0.Group);
        if (cf.Kind != TailKind.Count) return null;
        List<string> sigs0 = SceneSigs(doc.Model);

        int was = TailSem.Value(nm.Tail, cf) as int? ?? 0;
        if (!SceneProjector.FieldTailReshape(src, cf, was + 1))
        {
            Check(false, $"colour count {was} -> {was + 1}: refused \u2014 " + SceneProjector.FieldTailWhy(src, cf, was + 1));
            return false;
        }
        byte[] edited = doc.Encode();
        int delta = edited.Length - orig.Length;
        var re = TryEntityAfter(doc, edited, idx);
        var r1 = re is not null ? SceneProjector.FieldTailNamed(re) : null;
        int got = -1, entries = -1;
        bool whole = r1 is { } a && a.Consumed == a.Tail.Length;
        if (r1 is { } b)
        {
            var mf = b.Fields.FirstOrDefault(x => x.Kind == TailKind.Count && x.Group == cf.Group);
            if (mf.Kind == TailKind.Count) got = TailSem.Value(b.Tail, mf) as int? ?? -1;
            entries = b.Fields.Count(x => x.Group == cf.Group && x.Name.StartsWith("colour ") && x.Name.EndsWith(" R"));
        }
        bool stable = StableReEncode(doc, edited);
        bool alone = OnlyOneRecordMoved(sigs0, doc, edited, out string alonewhy3);
        bool back = SceneProjector.FieldTailReshape(src, cf, was) && doc.Encode().AsSpan().SequenceEqual(orig);
        Check(delta == 16 && got == was + 1 && entries == was + 1 && whole && stable && alone && back,
            $"colour count {was} -> {was + 1}: delta={delta}B reads={got} rows={entries} wholeTail={whole} stable={stable} local={alone}{(alone ? "" : " " + alonewhy3)} revert={back}");
        return back;
    }

    // Re-load an edited file and find the same entity record again, or null when the edit made the
    // level undecodable (which is itself the failure the proof is looking for).
    private static object? TryEntityAfter(LevelDoc doc, byte[] edited, int idx)
    {
        try { return NthFieldEnt(doc.Profile.Decode(edited), idx); } catch { return null; }
    }

    // The in-app save gate: loading what we just wrote and writing it again must be a fixed point.
    private static bool StableReEncode(LevelDoc doc, byte[] edited)
    {
        try { return doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited); }
        catch { return false; }
    }

    private static void DecoFieldProofs(LevelDoc doc, byte[] orig, int idx, object src, Action<string> Emit, Action<bool, string> Check)
    {
        Emit($"  deco#{idx}");
        ScalarProof(doc, orig, idx, src, "dsize", (SceneProjector.FieldGet(src, "dsize") ?? 1) * 1.5, "size \u00d7", isEnt: false, cap: 8, Check);
        ColorProof(doc, orig, idx, src, isEnt: false, (0x15, 0x2c, 0x4a), Check);
    }

    // Write a fixed-width numeric field, re-decode, read it back (persists), assert the change is
    // localised (bytes<=cap) and re-encode-stable, then restore the original value and prove the
    // file returns byte-identical to the stock level (I1/I3).
    private static void ScalarProof(LevelDoc doc, byte[] orig, int idx, object src, string key, double newv, string label, bool isEnt, int cap, Action<bool, string> Check)
    {
        double? was = SceneProjector.FieldGet(src, key);
        if (was is null) { return; }
        SceneProjector.FieldSet(src, key, newv);
        byte[] edited = doc.Encode();
        var re = isEnt ? NthFieldEnt(doc.Profile.Decode(edited), idx) : NthFieldDeco(doc.Profile.Decode(edited), idx);
        double? got = SceneProjector.FieldGet(re, key);
        bool persists = got is double g && Math.Abs(g - newv) < 0.02;
        bool differ = !edited.AsSpan().SequenceEqual(orig);
        int diff = DiffBytes(orig, edited);
        bool localised = diff > 0 && diff <= cap;
        bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
        SceneProjector.FieldSet(src, key, was.Value);
        bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
        Check(persists && differ && localised && stable && revert,
            $"{label}: differs={differ} bytes={diff} persists={got} stable={stable} revert={revert}");
    }

    // XOR-flip the rgb channels and invert alpha, so the edited colour is guaranteed distinct from
    // the original on every byte; the write must touch <=4 bytes and revert cleanly.
    private static void ColorProof(LevelDoc doc, byte[] orig, int idx, object src, bool isEnt, (byte, byte, byte) x, Action<bool, string> Check)
    {
        var wasc = (byte[])(SceneProjector.FieldColorGet(src) ?? new byte[] { 255, 255, 255, 255 }).Clone();
        var nw = new byte[] { (byte)(wasc[0] ^ x.Item1), (byte)(wasc[1] ^ x.Item2), (byte)(wasc[2] ^ x.Item3), (byte)(255 - wasc[3]) };
        SceneProjector.FieldColorSet(src, nw[0], nw[1], nw[2], nw[3]);
        byte[] edited = doc.Encode();
        var re = isEnt ? NthFieldEnt(doc.Profile.Decode(edited), idx) : NthFieldDeco(doc.Profile.Decode(edited), idx);
        var got = SceneProjector.FieldColorGet(re);
        bool persists = got is not null && got.SequenceEqual(nw);
        bool differ = !edited.AsSpan().SequenceEqual(orig);
        int diff = DiffBytes(orig, edited);
        bool localised = diff > 0 && diff <= 4;
        bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
        SceneProjector.FieldColorSet(src, wasc[0], wasc[1], wasc[2], wasc[3]);
        bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
        Check(persists && differ && localised && stable && revert,
            $"color: differs={differ} bytes={diff} persists={persists} stable={stable} revert={revert}");
    }

    private static (int Idx, object? Src) NthEnt(object model, int want)
    {
        int n = -1;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == SceneKind.Entity && c.Source is Tp2Entity) { if (n++ == want) return (want, c.Source); }
        return (-1, null);
    }

    private static (int Idx, object? Src) NthDeco(object model, int want)
    {
        int n = -1;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == SceneKind.Deco && c.Source is Tp2Deco) { if (n++ == want) return (want, c.Source); }
        return (-1, null);
    }

    private static Tp2Entity? NthEntSrc(object model, int want)
    {
        int n = -1;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == SceneKind.Entity && c.Source is Tp2Entity te) { if (n++ == want) return te; }
        return null;
    }

    private static Tp2Deco? NthDecoSrc(object model, int want)
    {
        int n = -1;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == SceneKind.Deco && c.Source is Tp2Deco td) { if (n++ == want) return td; }
        return null;
    }

    // The Tp2Layer index holding the flatIdx-th entity (projector enumerates layer by layer).
    private static int EntLayer(object model, int flatIdx)
    {
        if (model is not Tp2Level tp) return -1;
        int n = 0;
        for (int li = 0; li < tp.Layers.Count; li++)
            foreach (var _ in tp.Layers[li].Entities)
                if (n++ == flatIdx) return li;
        return -1;
    }

    private static string? FirstPathName(object model, int flatIdx)
    {
        if (model is not Tp2Level tp) return null;
        int li = EntLayer(model, flatIdx);
        if (li < 0) return null;
        foreach (var p in tp.Layers[li].Paths)
            if (!string.IsNullOrEmpty(p.PathName)) return p.PathName;
        return null;
    }

    private static void EntityProofs(LevelDoc doc, int idx, Tp2Entity e, Action<string> Emit, Action<bool, string> Check)
    {
        byte[] orig = doc.Original;
        Check(doc.Encode().AsSpan().SequenceEqual(orig), "baseline encode byte-exact (I1)");
        if (idx < 0) return;

        void Scalar(string key, double newv, string label)
        {
            double? was = SceneProjector.Tp2ScalarGet(e, key);
            if (was is null) return;
            SceneProjector.Tp2ScalarSet(e, key, newv);
            byte[] edited = doc.Encode();
            var re = NthEntSrc(doc.Profile.Decode(edited), idx);
            double? got = re is null ? null : SceneProjector.Tp2ScalarGet(re, key);
            bool persists = got is double g && Math.Abs(g - newv) < 0.01;
            bool differ = !edited.AsSpan().SequenceEqual(orig);
            bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
            SceneProjector.Tp2ScalarSet(e, key, was.Value);
            bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
            Check(persists && differ && stable && revert,
                $"{label}: differs={differ} persists={got} stable={stable} revert={revert}");
        }

        Scalar("mass", (SceneProjector.Tp2ScalarGet(e, "mass") ?? 1) + 3.5, "mass (size)");
        Scalar("prio", (SceneProjector.Tp2ScalarGet(e, "prio") ?? 0) + 7, "priority");
        Scalar("persist", (SceneProjector.Tp2ScalarGet(e, "persist") ?? 0) == 0 ? 1 : 0, "persists(field_158)");

        {
            var wasc = (byte[])(SceneProjector.Tp2ColorGet(e) ?? new byte[4]).Clone();
            var nw = new byte[] { (byte)(wasc[0] ^ 0x3a), (byte)(wasc[1] ^ 0x5c), (byte)(wasc[2] ^ 0x71), (byte)(255 - wasc[3]) };
            SceneProjector.Tp2ColorSet(e, nw[0], nw[1], nw[2], nw[3]);
            byte[] edited = doc.Encode();
            var got = SceneProjector.Tp2ColorGet(NthEntSrc(doc.Profile.Decode(edited), idx));
            bool persists = got is not null && got.SequenceEqual(nw);
            bool differ = !edited.AsSpan().SequenceEqual(orig);
            bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
            SceneProjector.Tp2ColorSet(e, wasc[0], wasc[1], wasc[2], wasc[3]);
            bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
            Check(persists && differ && stable && revert, $"color: differs={differ} persists={persists} stable={stable} revert={revert}");
        }

        // emitter toggle on its 0<->1 edge — Encode rebuilds (or drops) the whole block.
        {
            int oh = e.HasEmitter; var ov = (double[])(e.EmitV ?? Array.Empty<double>()).Clone();
            int ores = e.EmitReserved, oem = e.EmitEndMarker;
            double newOn = oh == 0 ? 1 : 0;
            SceneProjector.Tp2ScalarSet(e, "emit_on", newOn);
            byte[] edited = doc.Encode();
            double? got = SceneProjector.Tp2ScalarGet(NthEntSrc(doc.Profile.Decode(edited), idx), "emit_on");
            bool differs = !edited.AsSpan().SequenceEqual(orig);
            bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
            e.HasEmitter = oh; e.EmitV = ov; e.EmitReserved = ores; e.EmitEndMarker = oem;
            bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
            Check((got ?? -1) == newOn && differs && stable && revert,
                $"emitter toggle->{(int)newOn}: differs={differs} persists={(got ?? -1) == newOn} stable={stable} revert={revert}");
        }

        // path-follow assignment is proven in PathProof (run on a level that has paths).
    }

    // Assign an entity to a path on its own layer: the block (0->1) and the respawned chained
    // position must survive a re-decode + stable re-encode, and a full field restore returns the
    // exact original bytes.
    private static void PathProof(LevelDoc doc, int pidx, Tp2Entity pe, string pname, Action<string> Emit, Action<bool, string> Check)
    {
        byte[] orig = doc.Original;
        Check(doc.Encode().AsSpan().SequenceEqual(orig), "baseline encode byte-exact (I1) [path]");
        int ohf = pe.HasPathFollow; string? opath = pe.PathName;
        double op0 = pe.Pv0, op1 = pe.Pv1, op2 = pe.Pv2, op3 = pe.Pv3, op4 = pe.Pv4;
        int pf0 = pe.PFlag0, pf1 = pe.PFlag1, pm = pe.PMode;
        var opos = (double[])pe.Position.Clone();
        SceneProjector.Tp2PathSet(pe, doc.Model as Tp2Level, EntLayer(doc.Model, pidx), pname);
        byte[] edited = doc.Encode();
        string? got = SceneProjector.Tp2PathGet(NthEntSrc(doc.Profile.Decode(edited), pidx));
        bool persists = got == pname;
        bool differs = !edited.AsSpan().SequenceEqual(orig);
        bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
        pe.HasPathFollow = ohf; pe.PathName = opath;
        pe.Pv0 = op0; pe.Pv1 = op1; pe.Pv2 = op2; pe.Pv3 = op3; pe.Pv4 = op4;
        pe.PFlag0 = pf0; pe.PFlag1 = pf1; pe.PMode = pm; pe.Position = opos;
        bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
        Check(persists && differs && stable && revert,
            $"follow-path'{pname}' (ent#{pidx}): differs={differs} persists={persists} stable={stable} revert={revert}");
    }

    private static void DecoProofs(LevelDoc doc, int idx, Tp2Deco d, Action<string> Emit, Action<bool, string> Check)
    {
        byte[] orig = doc.Original;
        Check(doc.Encode().AsSpan().SequenceEqual(orig), "baseline encode byte-exact (I1) [deco]");
        if (idx < 0) return;

        void Scalar(string key, double newv, string label)
        {
            double? was = SceneProjector.Tp2ScalarGet(d, key);
            if (was is null) return;
            SceneProjector.Tp2ScalarSet(d, key, newv);
            byte[] edited = doc.Encode();
            double? got = SceneProjector.Tp2ScalarGet(NthDecoSrc(doc.Profile.Decode(edited), idx), key);
            bool persists = got is double g && Math.Abs(g - newv) < 0.02;
            bool differ = !edited.AsSpan().SequenceEqual(orig);
            bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
            SceneProjector.Tp2ScalarSet(d, key, was.Value);
            bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
            Check(persists && differ && stable && revert,
                $"{label}: differs={differ} persists={got} stable={stable} revert={revert}");
        }

        Scalar("dsize", (SceneProjector.Tp2ScalarGet(d, "dsize") ?? 1) * 1.5, "size\u00d7");
        Scalar("dprio", (SceneProjector.Tp2ScalarGet(d, "dprio") ?? 0) + 5, "priority");

        {
            var wasc = (byte[])(SceneProjector.Tp2ColorGet(d) ?? new byte[4]).Clone();
            var nw = new byte[] { (byte)(wasc[0] ^ 0x15), (byte)(wasc[1] ^ 0x2c), (byte)(wasc[2] ^ 0x4a), (byte)(255 - wasc[3]) };
            SceneProjector.Tp2ColorSet(d, nw[0], nw[1], nw[2], nw[3]);
            byte[] edited = doc.Encode();
            var got = SceneProjector.Tp2ColorGet(NthDecoSrc(doc.Profile.Decode(edited), idx));
            bool persists = got is not null && got.SequenceEqual(nw);
            bool differ = !edited.AsSpan().SequenceEqual(orig);
            bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
            SceneProjector.Tp2ColorSet(d, wasc[0], wasc[1], wasc[2], wasc[3]);
            bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
            Check(persists && differ && stable && revert, $"color: differs={differ} persists={persists} stable={stable} revert={revert}");
        }

        // decoration rotation lives in size.raw (now exposed via the shared rotatable API).
        {
            double was = SceneProjector.GetRotationDeg(d) ?? 0;
            double want = was + 42;
            SceneProjector.SetRotationDeg(d, want);
            byte[] edited = doc.Encode();
            double? got = SceneProjector.GetRotationDeg(NthDecoSrc(doc.Profile.Decode(edited), idx));
            bool persists = got is double g && Near(g, want, 0.05);
            bool differ = !edited.AsSpan().SequenceEqual(orig);
            bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(edited)).AsSpan().SequenceEqual(edited);
            SceneProjector.SetRotationDeg(d, was);
            bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
            Check(persists && differ && stable && revert,
                $"rotation {was:0.#}->{want:0.#}: differs={differ} persists={persists} stable={stable} revert={revert}");
        }
    }

    /// <summary>Headless proof of the S5-6 entity sprite reassign for Tasty Blue and the
    /// ElementEngine games (Forever / Final Bite). For one stock level of each it repoints the
    /// first entity at (a) another name already used by an entity in the level and (b) a fresh
    /// valid prefab name, asserting each time that: the untouched level still round-trips
    /// byte-exactly (I1); after the edit the entity re-decodes to the new name; exactly ONE
    /// entity name changed (no framing cascade onto the records that follow an f4/f5 block
    /// splice); and the edited file re-encodes to itself (the in-app stability gate). For the
    /// opaque-block games it also asserts an invalid (no-underscore) name is refused and leaves
    /// the bytes unchanged. Operates on freshly loaded docs; never writes to disk.</summary>
    public static void EntityEditCheck()
    {
        using var log = new StreamWriter("_entityedit.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;

        foreach (var (game, dir) in Defaults.Where(d => d.Game is "tb" or "f4" or "f5"))
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            // Pick the first stock level that actually carries an editable entity (many small
            // levels have none), rather than assuming the alphabetically-first file does.
            var level = FindEntityLevel(dir);
            if (level is null) { Emit($"SKIP {game}: no level with an editable entity"); continue; }
            Emit($"{game}: {Path.GetFileName(level)}");
            bool opaque = game is "f4" or "f5";
            foreach (var mode in opaque ? new[] { "existing", "new", "reject" } : new[] { "existing", "new" })
            {
                try
                {
                    var (ok, note) = TestEntityEdit(game, level, mode);
                    if (ok) pass++; else fail++;
                    Emit($"  {(ok ? "OK  " : "FAIL")} {mode,-9} {note}");
                }
                catch (Exception ex) { fail++; Emit($"  FAIL {game} {mode}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        Emit("");
        Emit($"ENTITY EDIT: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "ENTITY EDIT GREEN" : "ENTITY EDIT FAILURES");
    }

    // The first stock level in a game's dir (scan order, capped) whose projection exposes at
    // least one entity carrying a non-blank resolved name — the object TestEntityEdit repoints.
    private static string? FindEntityLevel(string dir)
    {
        foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f).Take(60))
        {
            try
            {
                var doc = LevelLoader.Load(lvl);
                foreach (var c in doc.Scene.Crosses)
                    if (c.Kind == SceneKind.Entity && !string.IsNullOrEmpty(c.SpriteName)) return lvl;
            }
            catch { /* a level that fails to decode just contributes nothing */ }
        }
        return null;
    }

    // One entity reassign assertion for a given game/level/mode. `existing` points the entity at
    // another name already used by an entity in the level; `new` at a fresh valid prefab name;
    // `reject` (opaque-block games only) requires an invalid name to be refused, bytes unchanged.
    private static (bool Ok, string Note) TestEntityEdit(string game, string level, string mode)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        if (!doc.Encode().AsSpan().SequenceEqual(orig))
            return (false, "baseline encode not byte-exact");

        var beforeNames = EntityNames(doc.Model);

        string? wasName = null; int seq = -1; Func<string, bool>? apply = null; int eseq = -1;
        switch (doc.Model)
        {
            case TbLevel lv:
                foreach (var c in doc.Scene.Crosses)
                {
                    if (c.Kind != SceneKind.Entity) continue; eseq++;
                    if (c.Source is TbEntity e && e.Name is { Length: > 0 })
                    { wasName = e.Name; seq = eseq; apply = name => TbEdit.SetEntitySprite(lv, e, name); break; }
                }
                break;
            case ElementLevel el:
                foreach (var c in doc.Scene.Crosses)
                {
                    if (c.Kind != SceneKind.Entity) continue; eseq++;
                    if (c.Source is EntityEditHandle h && c.SpriteName is { Length: > 0 })
                    { wasName = c.SpriteName; seq = eseq; apply = name => ElemEdit.SetEntitySprite(h, name); break; }
                }
                break;
            default:
                return (false, $"unexpected model for {game}");
        }
        if (apply is null || seq < 0)
            return (false, "no suitable entity found to edit");

        if (mode == "reject")
        {
            // An f4/f5 prefab name with no underscore is not a credible record head, so the
            // guarded splice must refuse it and leave every byte of the block untouched.
            bool refused = !apply("nounderscorehere");
            bool unchanged = doc.Encode().AsSpan().SequenceEqual(orig);
            bool ok = refused && unchanged;
            return (ok, $"invalid-name refused={refused} bytesUnchanged={unchanged}");
        }

        string target = mode == "new"
            ? "zz_probe_entity_cell"                       // fresh, valid (>=5 chars, has '_')
            : (beforeNames.FirstOrDefault(n => !string.IsNullOrEmpty(n) && n != wasName) ?? "");
        if (target.Length == 0) return (false, $"no alternative existing entity name (n/a) ({beforeNames.Count} entities)");

        bool applied = apply(target);
        if (!applied) return (false, $"SetEntitySprite('{target}') refused");

        byte[] edited = doc.Encode();
        object back = doc.Profile.Decode(edited);
        var afterNames = EntityNames(back);

        bool lenOk = afterNames.Count == beforeNames.Count;
        bool persists = lenOk && seq < afterNames.Count && afterNames[seq] == target;
        int changed = 0;
        if (lenOk) for (int i = 0; i < beforeNames.Count; i++) if (beforeNames[i] != afterNames[i]) changed++;
        bool exactlyOne = lenOk && changed == 1;
        byte[] re = doc.Profile.Codec.Encode(doc.Profile.Decode(edited));
        bool stable = re.AsSpan().SequenceEqual(edited);

        bool okAll = persists && lenOk && exactlyOne && stable && target != wasName;
        string note = $"'{wasName}'->'{target}' persists={persists} onlyOneChanged={exactlyOne}(n={changed}) stable={stable}";
        return (okAll, note);
    }

    // Ordered resolved prefab names of every entity the projector draws for a model.
    private static List<string?> EntityNames(object model) =>
        SceneProjector.Project(model).Crosses
            .Where(c => c.Kind == SceneKind.Entity).Select(c => c.SpriteName).ToList();

    // ---------------------------------------------------- S6 edit-ops ----

    /// <summary>Headless proof of the slice-6 mutation layer (duplicate / delete). For one
    /// stock level of each family and every kind it supports, it asserts that a duplicate (a)
    /// re-decodes stable, (b) grows that kind by exactly one and preserves every existing
    /// record's identity in order (no collateral), and that a delete removes exactly one and
    /// — critically for the chained raw-span families — keeps every later record's resolved
    /// position unchanged (proving the delta re-fold). Operates on fresh docs; never writes.</summary>
    public static void EditOpsCheck()
    {
        using var log = new StreamWriter("_editops.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir"); continue; }
            var level = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).FirstOrDefault();
            if (level is null) { Emit($"SKIP {game}: no levels"); continue; }
            Emit($"{game}: {Path.GetFileName(level)}");

            LevelDoc doc;
            try { doc = LevelLoader.Load(level); }
            catch (Exception ex) { fail++; Emit($"  FAIL load: {ex.GetType().Name}: {ex.Message}"); continue; }

            if (!doc.Encode().AsSpan().SequenceEqual(doc.Original))
            { fail++; Emit("  FAIL baseline encode not byte-exact (I1)"); continue; }

            foreach (var kind in new[] { SceneKind.Entity, SceneKind.Deco, SceneKind.Wall, SceneKind.Area, SceneKind.Water })
            {
                var (ok, note) = TestEditOp(doc, kind);
                if (ok == null) { Emit($"     (no {kind} — skipped)"); continue; }
                if (ok.Value) pass++; else fail++;
                Emit($"  {(ok.Value ? "OK  " : "FAIL")} {kind,-6} {note}");
            }
        }

        Emit("");
        Emit($"EDIT OPS: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "EDIT OPS GREEN" : "EDIT OPS FAILURES");
    }

    // One duplicate+delete integrity assertion for a given kind, or null when the level has
    // no selectable source of that kind (not applicable — not a failure).
    private static (bool? Ok, string Note) TestEditOp(LevelDoc fresh, SceneKind kind)
    {
        var src = FirstSourceOf(fresh.Scene, kind);
        if (src is null) return (null, "none");

        // --- duplicate ---
        var d1 = LevelLoader.Load(fresh.Path);
        var before = Fingerprint(d1.Model, kind);
        var dup = EditOps.Duplicate(d1.Model, FirstSourceOf(d1.Scene, kind));
        if (dup != EditResult.Done) return (false, $"duplicate={dup}");
        var afterDup = Fingerprint(d1.Model, kind);
        byte[] enc = d1.Encode();
        var back = d1.Profile.Decode(enc);
        var reBack = d1.Profile.Codec.Encode(d1.Profile.Decode(enc));
        bool stable = reBack.AsSpan().SequenceEqual(enc);
        bool grew = InsertedOne(before, afterDup);          // exactly one appended, others in order
        if (!stable || !grew) return (false, $"dup stable={stable} grew={grew} ({before.Count}->{afterDup.Count})");

        // --- delete (re-chain / collateral check) ---
        var d2 = LevelLoader.Load(fresh.Path);
        var beforeDel = Fingerprint(d2.Model, kind);
        int removeIdx = FirstSourceIndex(d2.Scene, kind);
        if (removeIdx < 0) return (false, "delete: no source");
        var del = EditOps.Delete(d2.Model, FirstSourceOf(d2.Scene, kind));
        if (del != EditResult.Done) return (false, $"delete={del}");
        var afterDel = Fingerprint(d2.Model, kind);
        byte[] enc2 = d2.Encode();
        var re2 = d2.Profile.Codec.Encode(d2.Profile.Decode(enc2));
        bool stable2 = re2.AsSpan().SequenceEqual(enc2);
        bool removed = RemovedOne(beforeDel, afterDel, removeIdx);
        if (!stable2 || !removed)
            return (false, $"del stable={stable2} clean={removed} ({beforeDel.Count}->{afterDel.Count})");

        return (true, $"dup+del stable grew removed (n={before.Count})");
    }

    // Model-independent fingerprint of one kind's projected records, in stable draw order:
    // a rounded position plus the sprite/label identity. Used to prove an op added/removed
    // exactly one record and left every other record's resolved position untouched.
    private static List<string> Fingerprint(object model, SceneKind kind)
    {
        var g = SceneProjector.Project(model);
        var list = new List<string>();
        void Add(double x, double y, string? n) => list.Add($"{Math.Round(x, 1)},{Math.Round(y, 1)}|{n}");
        if (kind == SceneKind.Wall || kind == SceneKind.Area || kind == SceneKind.Water)
            foreach (var p in g.Polys) if (p.Kind == kind && p.Points.Count > 0) Add(p.Points[0].X, p.Points[0].Y, p.Label);
        if (kind is SceneKind.Entity or SceneKind.Deco or SceneKind.Wall or SceneKind.Area)
            foreach (var c in g.Crosses) if (c.Kind == kind) Add(c.At.X, c.At.Y, c.SpriteName ?? c.Label);
        return list;
    }

    // True when `after` == `before` with exactly one record inserted (anywhere) and every
    // prior record preserved in order with an identical fingerprint — the shape of a correct
    // duplicate. A record whose resolved position drifted (collateral) breaks the subsequence
    // match, so this also guards against an append secretly shifting later records.
    private static bool InsertedOne(List<string> before, List<string> after)
    {
        if (after.Count != before.Count + 1) return false;
        int i = 0, j = 0;
        while (i < after.Count && j < before.Count)
            if (after[i] == before[j]) { i++; j++; }
            else i++;
        return j == before.Count;
    }

    // True when `after` == `before` with the element at `idx` removed and every other element
    // (including the records that FOLLOW a chained removal) otherwise identical — which only
    // holds if the deleted record's delta was correctly folded into its successor.
    private static bool RemovedOne(List<string> before, List<string> after, int idx)
    {
        if (after.Count != before.Count - 1) return false;
        int j = 0;
        for (int i = 0; i < before.Count; i++)
        {
            if (i == idx) continue;
            if (j >= after.Count || before[i] != after[j]) return false;
            j++;
        }
        return j == after.Count;
    }

    private static object? FirstSourceOf(SceneGeometry g, SceneKind kind)
    {
        foreach (var p in g.Polys) if (p.Kind == kind && p.Source is not null) return p.Source;
        foreach (var c in g.Crosses) if (c.Kind == kind && c.Source is not null) return c.Source;
        return null;
    }

    private static int FirstSourceIndex(SceneGeometry g, SceneKind kind)
    {
        int i = 0;
        foreach (var p in g.Polys) { if (p.Kind == kind) { if (p.Source is not null) return i; i++; } }
        foreach (var c in g.Crosses) { if (c.Kind == kind) { if (c.Source is not null) return i; i++; } }
        return -1;
    }

    // ------------------------------------------------- S6 multilevel xml ---

    /// <summary>Headless proof of the slice-6 multilevel port (Core MultiLevel ⇄ tp2multi.py /
    /// tbmulti.py). For the shipped levels/multilevels/*.xml of each family it asserts that a
    /// file parses to at least one stage, that save∘load is idempotent on the text (the engine
    /// reads our XML through its own parser, so — unlike a .bin — byte-equality to the original
    /// is not required, only that re-loading our own output re-encodes identically), that the
    /// stage count survives the round-trip, and that every stage's resolved zoom matches the
    /// freshly-parsed value. Operates read-only on stock files (writes only a temp). Never edits
    /// a level, so it cannot touch the I1 byte path.</summary>
    public static void MultiLevelCheck()
    {
        using var log = new StreamWriter("_multilevel.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;
        string tmp = Path.Combine(Path.GetTempPath(), "tpe_multilevel.xml");

        var dirs = new (string Game, string Dir)[]
        {
            ("tb", Sub(LevelsDirFor("tb"), "multilevels")),
            ("tp2", Sub(LevelsDirFor("tp2"), "multilevels")),
        };
        foreach (var (game, dir) in dirs)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no multilevels dir {dir}"); continue; }
            var files = Directory.GetFiles(dir, "*.xml").OrderBy(f => f).Take(40).ToList();
            Emit($"{game}: {dir}  ({files.Count} files)");
            foreach (var f in files)
            {
                try
                {
                    var ml = MultiLevel.Load(f);
                    string text = ml.ToXml();
                    File.WriteAllText(tmp, text);
                    var ml2 = MultiLevel.Load(tmp);
                    string text2 = ml2.ToXml();
                    bool idem = text == text2;
                    bool countOk = ml.Stages.Count == ml2.Stages.Count && ml.Stages.Count > 0;
                    bool zoomOk = true;
                    for (int i = 0; i < Math.Min(ml.Stages.Count, ml2.Stages.Count); i++)
                    {
                        var z1 = ml.Stages[i].Zoom;
                        var z2 = ml2.Stages[i].Zoom;
                        if ((z1 is null) != (z2 is null)) { zoomOk = false; break; }
                        if (z1 is double a && z2 is double b && Math.Abs(a - b) > Math.Abs(a) * 1e-4 + 1e-12) { zoomOk = false; break; }
                    }
                    bool ok = idem && countOk && zoomOk;
                    if (ok) pass++; else fail++;
                    Emit($"  {(ok ? "OK  " : "FAIL")} {Path.GetFileName(f),-30} stages={ml.Stages.Count} fmt={ml.Format} idempotent={idem} zoom={zoomOk}");
                }
                catch (Exception ex) { fail++; Emit($"  FAIL {Path.GetFileName(f)}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

        Emit("");
        Emit($"MULTILEVEL: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "MULTILEVEL GREEN" : "MULTILEVEL FAILURES");
    }

    // ----------------------------------------- S6 mutation integrity -------

    /// <summary>Two extra slice-6 guarantees beyond the per-kind dup/del check:
    /// (a) for the framed (f4/f5) families every section's authoritative <c>Count</c> stays in
    ///     lockstep with its record list after a duplicate and after a delete — the encoder writes
    ///     the stored Count, so a mismatch would corrupt the file even when re-decode looks stable;
    /// (b) a move-to-layer on a genuinely multi-layer level re-decodes stable and shifts exactly one
    ///     record from the source to the destination. Writes nothing to disk.</summary>
    public static void MoveOpsCheck()
    {
        using var log = new StreamWriter("_moveops.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}"); continue; }
            var level = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).FirstOrDefault();
            if (level is null) { Emit($"SKIP {game}: no levels"); continue; }

            // (a) Count lockstep for framed (non-opaque) sections, on the first f4/f5 level.
            if (game is "f4" or "f5")
            {
                var (ok, note) = TestCountLockstep(level);
                if (ok) pass++; else fail++;
                Emit($"  {(ok ? "OK  " : "FAIL")} {game} count-lockstep: {note}");
            }

            // (b) move-to-layer, needing a level with >= 2 layers and a movable source on layer 0.
            var (mok, mnote) = TestMoveToLayer(dir);
            if (mok is null) Emit($"     {game} move-to-layer: {mnote}");
            else { if (mok.Value) pass++; else fail++; Emit($"  {(mok.Value ? "OK  " : "FAIL")} {game} move-to-layer: {mnote}"); }
        }

        Emit("");
        Emit($"MOVE OPS: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "MOVE OPS GREEN" : "MOVE OPS FAILURES");
    }

    /// <summary>Slice-7 undo/redo proof. For one stock level per family, drives the same
    /// HistoryService the UI uses: seed → duplicate (Commit) → assert CanUndo/Undo restores the
    /// exact loaded bytes → Redo re-applies the edit → undo again back to baseline. This proves
    /// the snapshot round-trip is byte-exact (undo never silently corrupts) and that the history
    /// cursor / redo-branching behaves, without any window. Writes nothing to disk.</summary>
    public static void UndoCheck()
    {
        using var log = new StreamWriter("_undo.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}"); continue; }
            LevelDoc? picked = null; object? src = null;
            foreach (var f in Directory.GetFiles(dir, "*.bin").OrderBy(x => x).Take(60))
            {
                try
                {
                    var d = LevelLoader.Load(f);
                    var s = FirstSourceOf(d.Scene, SceneKind.Deco)
                            ?? FirstSourceOf(d.Scene, SceneKind.Entity)
                            ?? FirstSourceOf(d.Scene, SceneKind.Wall);
                    // Keep the SAME doc the source belongs to — a reloaded model is a different
                    // object graph, so a foreign source handle would resolve to NothingSelected.
                    if (s is not null && EditOps.Copy(d.Model, s) is not null) { picked = d; src = s; break; }
                }
                catch { }
            }
            if (picked is null) { Emit($"SKIP {game}: no undoable record found"); continue; }

            var doc = picked;
            var hist = new TastyEditor.App.Editing.HistoryService();
            hist.Seed(doc);
            byte[] baseline = doc.Original;
            int n0 = doc.Scene.Count;

            // 1) make one edit (duplicate) and commit it as an undo step.
            var r = EditOps.Duplicate(doc.Model, src);
            if (r != EditResult.Done) { fail++; Emit($"  FAIL {game}: duplicate={r}"); continue; }
            doc.Reproject();
            hist.Commit(doc);
            int n1 = doc.Scene.Count;
            byte[] edited = doc.Encode();

            bool canUndo = hist.CanUndo;
            // 2) undo → snapshot must decode + re-encode to the exact loaded bytes.
            var u = hist.Undo();
            doc.ApplyRestoredModel(doc.Profile.Decode(u!));
            bool backToBaseline = doc.Encode().AsSpan().SequenceEqual(baseline);
            bool dirtyCleared = !doc.Dirty;
            int nUndo = doc.Scene.Count;

            // 3) redo → the edit is re-applied byte-for-byte.
            var rd = hist.Redo();
            doc.ApplyRestoredModel(doc.Profile.Decode(rd!));
            bool redoRestores = doc.Encode().AsSpan().SequenceEqual(edited);
            int nRedo = doc.Scene.Count;

            // 4) undo once more → baseline again; now CanUndo is false at the oldest state.
            var u2 = hist.Undo();
            doc.ApplyRestoredModel(doc.Profile.Decode(u2!));
            bool undo2 = doc.Encode().AsSpan().SequenceEqual(baseline);
            bool atOldest = !hist.CanUndo;

            bool ok = canUndo && backToBaseline && dirtyCleared && nUndo == n0
                      && redoRestores && nRedo == n1 && undo2 && atOldest;
            if (ok) pass++; else fail++;
            Emit($"  {(ok ? "OK  " : "FAIL")} {game}: canUndo={canUndo} undo->baseline={backToBaseline} " +
                 $"dirty-clear={dirtyCleared} count {n0}->{n1}->{nUndo}->{nRedo} redo={redoRestores} " +
                 $"undo2={undo2} oldest={atOldest}");
        }

        Emit("");
        Emit($"UNDO: {pass} pass, {fail} fail");
        Emit(fail == 0 && pass > 0 ? "UNDO GREEN" : "UNDO FAILURES");
    }

    // Duplicate then delete the first framed record of each f4/f5 section, asserting Count ==
    // Records.Count is preserved both ways (and the edit re-decodes stable).
    private static (bool, string) TestCountLockstep(string level)
    {
        var doc = LevelLoader.Load(level);
        if (doc.Model is not ElementLevel el) return (false, "not an element level");
        var src = FirstSourceOf(doc.Scene, SceneKind.Deco)
                  ?? FirstSourceOf(doc.Scene, SceneKind.Entity)
                  ?? FirstSourceOf(doc.Scene, SceneKind.Wall);
        if (src is null) return (false, "no movable source");

        var dup = EditOps.Duplicate(doc.Model, src);
        if (dup != EditResult.Done) return (false, $"dup={dup}");
        string? s1 = CheckLockstep(el);
        bool stable1 = doc.Profile.Codec.Encode(doc.Profile.Decode(doc.Encode())).AsSpan().SequenceEqual(doc.Encode());
        if (s1 is not null || !stable1) return (false, $"after dup: {s1 ?? "ok"} stable={stable1}");

        var del = EditOps.Delete(doc.Model, src);
        if (del != EditResult.Done) return (false, $"del={del}");
        string? s2 = CheckLockstep(el);
        bool stable2 = doc.Profile.Codec.Encode(doc.Profile.Decode(doc.Encode())).AsSpan().SequenceEqual(doc.Encode());
        return (s2 is null && stable2, s2 ?? "ok");
    }

    // Null when every framed section's Count equals its record list length, else a description.
    private static string? CheckLockstep(ElementLevel el)
    {
        for (int li = 0; li < el.Layers.Count; li++)
            for (int si = 0; si < el.Layers[li].Count; si++)
            {
                var sec = el.Layers[li][si];
                if (!sec.Opaque && sec.Count != sec.Records.Count)
                    return $"layer {li} sec {si}: Count={sec.Count} != Records={sec.Records.Count}";
            }
        return null;
    }

    // The first level in a dir with >= 2 layers exposing a movable entity/deco on layer 0; moves
    // it to layer 1. Counts of that KIND are taken per layer before and after (the moved record's
    // identity is not preserved across a copy→paste→delete, so membership is by kind+layer, not by
    // object reference). Returns (null, reason) when no such level exists in the sample window.
    private static (bool?, string) TestMoveToLayer(string dir)
    {
        LevelDoc? doc = null; SceneCross? cross = null;
        foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f).Take(50))
        {
            try
            {
                var d = LevelLoader.Load(lvl);
                if (d.LayerCount < 2) continue;
                var c = d.Scene.Crosses.FirstOrDefault(x =>
                    x.Layer == 0 && x.Source is not null && (x.Kind == SceneKind.Entity || x.Kind == SceneKind.Deco));
                if (c is not null) { doc = d; cross = c; break; }
            }
            catch { }
        }
        if (doc is null || cross is null) return (null, "no multi-layer level with a layer-0 movable");

        var kind = cross.Kind;
        int src0 = KindLayerCount(doc, kind, 0);
        int dst0 = KindLayerCount(doc, kind, 1);
        var r = EditOps.MoveToLayer(doc.Model, cross.Source, 1);
        if (r != EditResult.Done) return (false, $"move={r}");
        var after = doc.Encode();
        bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(after)).AsSpan().SequenceEqual(after);
        int src1 = KindLayerCount(doc, kind, 0);
        int dst1 = KindLayerCount(doc, kind, 1);
        bool ok = stable && src1 == src0 - 1 && dst1 == dst0 + 1;
        return (ok, $"stable={stable} {kind} L0 {src0}->{src1}, L1 {dst0}->{dst1}");
    }

    // Count of projected crosses of a kind on a layer — a stable membership proxy that survives a
    // move (the record's identity changes, its kind+layer do not).
    private static int KindLayerCount(LevelDoc doc, SceneKind kind, int layer) =>
        SceneProjector.Project(doc.Model).Crosses.Count(c => c.Kind == kind && c.Layer == layer);

    // -------------------------------------------- R4 element creation ------

    /// <summary>Headless R4 proof of <see cref="TastyEditor.Core.Levels.EditOps.NewRecord"/> across
    /// all four families. For one stock level per game it asserts the untouched level is byte-exact
    /// (I1), then for every kind the family can create it appends a fresh default record centred on
    /// the level bounds and checks: that kind's projected count grows by exactly one (I3 — no
    /// collateral to any other record), the result re-encodes stable, f4/f5 section Counts stay in
    /// lockstep, and — for a kind the delete path supports — removing the new record restores the
    /// loaded bytes exactly. A kind a family has no storage for (TP2 area / water, f4/f5 water) or a
    /// raw-span kind with no shipped record to clone is an honest skip, never an invented byte. Writes
    /// nothing to disk.</summary>
    public static void CreateOpsCheck()
    {
        using var log = new StreamWriter("_createops.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, skip = 0, fail = 0;

        var kinds = new[] { SceneKind.Wall, SceneKind.Area, SceneKind.Water, SceneKind.Path, SceneKind.Deco };

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir"); continue; }
            LevelDoc? doc = null; string? level = null;
            foreach (var f in Directory.GetFiles(dir, "*.bin").OrderBy(x => x).Take(60))
            {
                try { var d = LevelLoader.Load(f); if (d.Encode().AsSpan().SequenceEqual(d.Original)) { doc = d; level = f; break; } }
                catch { }
            }
            if (doc is null) { fail++; Emit($"{game}: FAIL no byte-exact stock level"); continue; }
            Emit($"{game}: {Path.GetFileName(level!)}");

            foreach (var kind in kinds)
            {
                var (ok, note) = TestCreate(kind, doc.Original, doc);
                switch (ok)
                {
                    case true: pass++; Emit($"  OK   {kind,-6} {note}"); break;
                    case false: fail++; Emit($"  FAIL {kind,-6} {note}"); break;
                    default: skip++; Emit($"  (skip {kind,-6} {note})"); break;
                }
            }
        }

        Emit("");
        Emit($"CREATE OPS: {pass} pass, {fail} fail, {skip} skip");
        Emit(fail == 0 && pass > 0 ? "CREATE OPS GREEN" : "CREATE OPS FAILURES");
    }

    // (true) created + verified; (false) a real failure; (null) an honest not-supported skip.
    private static (bool?, string) TestCreate(SceneKind kind, byte[] original, LevelDoc fresh)
    {
        var doc = LevelLoader.Load(fresh.Path);
        var b = SceneProjector.Project(doc.Model).Bounds;
        var at = b.IsEmpty ? new PointD(400, 400) : new PointD((b.Left + b.Right) / 2, (b.Top + b.Bottom) / 2);
        int layerCount = LayerCount(doc.Model);
        int before = CountRecords(doc.Model, kind);

        // A raw-span family clones a shipped record, so a given layer may hold none of a kind
        // (honest NotSupported) while a later one does; try each layer before declaring a skip.
        // The typed families create on the first layer that accepts them.
        int usedLayer = -1; object? created = null;
        for (int li = 0; li < layerCount; li++)
        {
            var r = TastyEditor.Core.Levels.EditOps.NewRecord(doc.Model, kind, li, at, out created, null);
            if (r == TastyEditor.Core.Levels.EditResult.NotSupported) continue;
            if (r != TastyEditor.Core.Levels.EditResult.Done) return (false, $"NewRecord={r} @layer{li}");
            usedLayer = li;
            break;
        }
        if (usedLayer < 0) return (null, "family/level has no create path for this kind");

        int after = CountRecords(doc.Model, kind);
        if (after != before + 1) return (false, $"record count {before}->{after}, expected +1");

        byte[] enc = doc.Encode();
        bool stable = doc.Profile.Codec.Encode(doc.Profile.Decode(enc)).AsSpan().SequenceEqual(enc);
        if (!stable) return (false, "re-encode not stable");
        if (doc.Model is ElementLevel el)
        {
            string? bad = CheckLockstep(el);
            if (bad is not null) return (false, $"lockstep: {bad}");
        }

        // Strong I3 (only-diff) proof for kinds the delete path supports: remove the exact record
        // just created (NewRecord hands back its source / edit handle) and the file must return to
        // the loaded bytes. Path has no delete op, so it rests on count + stability + lockstep.
        bool delSupported = kind is SceneKind.Wall or SceneKind.Area or SceneKind.Water or SceneKind.Deco;
        if (delSupported)
        {
            if (created is null) return (false, "NewRecord returned no created source");
            var del = TastyEditor.Core.Levels.EditOps.Delete(doc.Model, created);
            if (del != TastyEditor.Core.Levels.EditResult.Done) return (false, $"delete-back={del}");
            if (!doc.Encode().AsSpan().SequenceEqual(original)) return (false, $"create+delete did not restore: {ByteDiff(original, doc.Encode())}");
        }
        return (true, $"+1 @({Math.Round(at.X)},{Math.Round(at.Y)}) L{usedLayer} stable{(delSupported ? " +byte-restore" : "")}");
    }

    private static int LayerCount(object model) => model switch
    {
        TbLevel tb => tb.Layers.Count,
        Tp2Level tp => tp.Layers.Count,
        ElementLevel el => el.Layers.Count,
        _ => 0,
    };

    // Structural record count of a kind across every layer — the honest backing-store proof that a
    // create appended exactly one record. Projection can hide a degenerate path (a spline that
    // draws no segment), so the byte-level record list, not the drawn primitives, is the invariant.
    private static int CountRecords(object model, SceneKind kind)
    {
        int n = 0;
        switch (model)
        {
            case TbLevel tb:
                foreach (var l in tb.Layers) n += kind switch
                {
                    SceneKind.Wall => l.Walls.Count, SceneKind.Area => l.Areas.Count,
                    SceneKind.Water => l.Waters.Count, SceneKind.Path => l.Paths.Count,
                    SceneKind.Deco => l.Decos.Count, SceneKind.Entity => l.Entities.Count, _ => 0,
                };
                break;
            case Tp2Level tp:
                foreach (var l in tp.Layers) n += kind switch
                {
                    SceneKind.Wall => l.Walls.Count, SceneKind.Path => l.Paths.Count,
                    SceneKind.Deco => l.Decos.Count, SceneKind.Entity => l.Entities.Count, _ => 0,
                };
                break;
            case ElementLevel el:
                string game = el.SectionsPerLayer >= 8 ? "f5" : "f4";
                int idx = ElemSectionIdx(game, kind);
                if (idx >= 0) foreach (var layer in el.Layers)
                    if (idx < layer.Count && !layer[idx].Opaque) n += layer[idx].Records.Count;
                break;
        }
        return n;
    }

    // Mirrors EditOps/SceneProjector's per-family element section layout (f5 wall0/area2/path3/
    // deco6; f4 wall0/area1/path2/deco5; no create storage for water / pointlist).
    private static int ElemSectionIdx(string game, SceneKind kind) => (game, kind) switch
    {
        ("f5", SceneKind.Wall) => 0, ("f5", SceneKind.Area) => 2, ("f5", SceneKind.Path) => 3, ("f5", SceneKind.Deco) => 6,
        ("f4", SceneKind.Wall) => 0, ("f4", SceneKind.Area) => 1, ("f4", SceneKind.Path) => 2, ("f4", SceneKind.Deco) => 5,
        _ => -1,
    };

    // Compact byte-diff summary for a failed create+delete-restore assertion.
    private static string ByteDiff(byte[] a, byte[] b)
    {
        var rep = SaveReport.Compare(a, b);
        return $"{rep.BytesChanged}b @{rep.FirstDiff}..{rep.LastDiff}, size {a.Length}->{b.Length}";
    }

    /// <summary>D7 (R4 stability checklist): bundle every headless gate behind one command for a
    /// CI-on-tag release run. Each gate already prints its own one-word GREEN / FAILURES verdict;
    /// this runs them in order, tees their live output straight through to the console, and turns any
    /// FAILURES (or a decode ERROR surfaced by the projection sweep) into a non-zero exit so the whole
    /// release checklist is one `allgates` invocation instead of eleven. It adds no assertions of its
    /// own — it is purely the aggregator. Returns the count of gates that did not come back green.</summary>
    public static int AllGates()
    {
        var gates = new (string Name, Action Run)[]
        {
            ("copyaudit", CopyAudit),
            ("smoke", () => Run(null)),
            ("savecheck", () => SaveCheck(null)),
            ("polycheck", () => PolyCheck(null)),
            ("watercheck", () => WaterCheck(null)),
            ("editops", EditOpsCheck),
            ("createops", CreateOpsCheck),
            ("pathops", PathOpsCheck),
            ("handles", HandlesCheck),
            ("fields", FieldsCheck),
            ("tp2fields", Tp2FieldsCheck),
            ("tp1check", Tp1Check),
            ("mods", ModsCheck),
            // R8: the sprite-identity invariant — no object may be drawn as another object's rig just
            // because their names share a prefix. It renders through the real SpriteSource, so it catches
            // the 1.0.0 fishbowl class of bug rather than re-reading the resolver's own flags.
            ("rigfall", RigFallProbe),
        };

        var real = Console.Out;
        var verdicts = new List<string>();
        int notGreen = 0;
        foreach (var (name, run) in gates)
        {
            var buf = new StringBuilder();
            real.WriteLine();
            real.WriteLine($"──────── {name} ────────");
            Console.SetOut(new TeeWriter(real, buf));
            try { run(); }
            catch (Exception ex)
            {
                Console.SetOut(real);
                buf.AppendLine($"gate threw {ex.GetType().Name}: {ex.Message}");
                real.WriteLine($"  {name} threw {ex.GetType().Name}: {ex.Message}");
            }
            finally { Console.SetOut(real); }

            string text = buf.ToString();
            // A gate fails only on its terminal verdict line ("... FAILURES") or a per-level decode
            // ERROR; a machine missing one game install reports SKIP, which is honest, not a failure.
            bool failed = text.Contains("FAILURES") || text.Contains("ERROR ");
            if (failed) notGreen++;
            verdicts.Add($"{(failed ? "FAIL" : "PASS")}  {name}");
        }

        real.WriteLine();
        real.WriteLine("===== ALL GATES =====");
        foreach (var v in verdicts) real.WriteLine("  " + v);
        real.WriteLine(notGreen == 0
            ? $"ALL GATES GREEN ({gates.Length})"
            : $"ALL GATES FAILURES ({notGreen}/{gates.Length} not green)");
        return notGreen;
    }

    // Forwards every write to the live console while also collecting it, so AllGates can read a
    // gate's verdict without hiding its output from whoever is watching the run.
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _live;
        private readonly StringBuilder _buf;
        public TeeWriter(TextWriter live, StringBuilder buf) { _live = live; _buf = buf; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { _live.Write(value); _buf.Append(value); }
        public override void Write(string? value) { _live.Write(value); _buf.Append(value); }
        public override void Write(char[] buffer, int index, int count) { _live.Write(buffer, index, count); _buf.Append(buffer, index, count); }
    }

    /// <summary>Headless R4 path-editing proof (SceneProjector path API). For one stock level per
    /// family that owns an editable path, from a fresh load each time: a whole-path move shifts every
    /// anchor by exactly the delta, an add grows the anchor list by one on a chosen segment, a remove
    /// shrinks it by one (never below two). Every op re-encodes stable; for the raw-span (f4/f5) family
    /// the edit stays inside the touched path record (untouched records byte-identical, I1/I3) and the
    /// section Count re-walks (lockstep). Never writes.</summary>
    public static void PathOpsCheck()
    {
        using var log = new StreamWriter("_pathops.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, skip = 0, fail = 0;

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir"); skip++; continue; }
            string? level = null;
            foreach (var f in Directory.GetFiles(dir, "*.bin").OrderBy(x => x).Take(120))
            {
                try
                {
                    var d = LevelLoader.Load(f);
                    if (!d.Encode().AsSpan().SequenceEqual(d.Original)) continue;
                    if (FirstEditablePath(d) is not null) { level = f; break; }
                }
                catch { }
            }
            if (level is null) { skip++; Emit($"{game}: (skip no editable stock path)"); continue; }
            Emit($"{game}: {Path.GetFileName(level)}");

            var tests = new (string Name, Func<string, (bool?, string)> Run)[]
            {
                ("move", TestPathMove), ("add", TestPathAdd), ("remove", TestPathRemove),
            };
            foreach (var (name, run) in tests)
            {
                var (ok, note) = run(level);
                if (ok == true) { pass++; Emit($"  OK   {name,-6} {note}"); }
                else if (ok == false) { fail++; Emit($"  FAIL {name,-6} {note}"); }
                else { skip++; Emit($"  (skip {name,-6} {note})"); }
            }
        }

        Emit("");
        Emit($"PATH OPS: {pass} pass, {fail} fail, {skip} skip");
        Emit(fail == 0 && pass > 0 ? "PATH OPS GREEN" : "PATH OPS FAILURES");
    }

    // The first projected path line whose source the editor can actually drive.
    private static object? FirstEditablePath(LevelDoc doc)
    {
        foreach (var line in doc.Scene.Lines)
            if (line.Kind == SceneKind.Path && SceneProjector.IsEditablePath(line.Source))
                return line.Source;
        return null;
    }

    private static (bool?, string) TestPathMove(string level)
    {
        var doc = LevelLoader.Load(level);
        var src = FirstEditablePath(doc);
        if (src is null) return (null, "no editable path");
        var beforePts = SceneProjector.PathWorldPoints(src);
        var snap = SnapshotElem(doc.Model);
        const double dx = 37, dy = 21;
        if (!SceneProjector.MoveRecord(src, dx, dy)) return (false, "MoveRecord refused");
        var afterPts = SceneProjector.PathWorldPoints(src);
        if (afterPts.Count != beforePts.Count) return (false, $"move changed anchor count {beforePts.Count}->{afterPts.Count}");
        for (int i = 0; i < afterPts.Count; i++)
            if (Math.Abs(afterPts[i].X - (beforePts[i].X + dx)) > 1e-6 || Math.Abs(afterPts[i].Y - (beforePts[i].Y + dy)) > 1e-6)
                return (false, $"move anchor {i} not shifted by exactly (+{dx},+{dy})");
        return PathFinish(doc, snap, $"moved {beforePts.Count} pts (+{dx},+{dy})");
    }

    private static (bool?, string) TestPathAdd(string level)
    {
        var doc = LevelLoader.Load(level);
        var src = FirstEditablePath(doc);
        if (src is null) return (null, "no editable path");
        var pts = SceneProjector.PathWorldPoints(src);
        if (pts.Count < 2) return (null, "path shorter than two anchors");
        int c0 = SceneProjector.PathPointCount(src);
        var snap = SnapshotElem(doc.Model);
        var mid = new PointD((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
        if (!SceneProjector.TryAddPathPoint(src, mid, double.MaxValue, out int at)) return (false, "TryAddPathPoint refused");
        if (at != 1) return (false, $"insertedAt={at}, expected 1");
        int c1 = SceneProjector.PathPointCount(src);
        if (c1 != c0 + 1) return (false, $"anchor count {c0}->{c1}, expected +1");
        var np = SceneProjector.PathWorldPoints(src);
        if (np.Count < 2 || Math.Abs(np[at].X - mid.X) > 1e-6 || Math.Abs(np[at].Y - mid.Y) > 1e-6)
            return (false, "inserted anchor not at the requested point");
        return PathFinish(doc, snap, $"add pt @{at} ->{c1}");
    }

    private static (bool?, string) TestPathRemove(string level)
    {
        var doc = LevelLoader.Load(level);
        var src = FirstEditablePath(doc);
        if (src is null) return (null, "no editable path");
        var pts = SceneProjector.PathWorldPoints(src);
        if (pts.Count <= 2) return (null, "path keeps its last two anchors (nothing to remove)");
        int c0 = SceneProjector.PathPointCount(src);
        var snap = SnapshotElem(doc.Model);
        if (!SceneProjector.TryRemovePathPoint(src, pts[0], double.MaxValue, out int rm)) return (false, "TryRemovePathPoint refused");
        int c1 = SceneProjector.PathPointCount(src);
        if (c1 != c0 - 1) return (false, $"anchor count {c0}->{c1}, expected -1");
        return PathFinish(doc, snap, $"remove pt @{rm} ->{c1}");
    }

    // Shared tail: re-encode stable, and for a raw-span family prove the edit stayed inside exactly
    // one record and the section still walks. Returns (false, note) on any violation.
    private static (bool, string) PathFinish(LevelDoc doc, Dictionary<Section, List<byte[]>>? snap, string note)
    {
        byte[] enc = doc.Encode();
        if (enc.AsSpan().SequenceEqual(doc.Original)) return (false, $"{note} | edit did not reach the saved bytes");
        if (!doc.Profile.Codec.Encode(doc.Profile.Decode(enc)).AsSpan().SequenceEqual(enc)) return (false, $"{note} | re-encode not stable");
        if (doc.Model is ElementLevel el)
        {
            string? bad = CheckLockstep(el);
            if (bad is not null) return (false, $"{note} | lockstep: {bad}");
            if (snap is not null && !ElemPathLocalized(el, snap, out int diffs))
                return (false, $"{note} | {diffs} records changed (expected exactly 1)");
            note += " +localised";
        }
        return (true, note);
    }

    // Deep copy of every non-opaque element record so an edit's blast radius can be measured.
    private static Dictionary<Section, List<byte[]>>? SnapshotElem(object model)
    {
        if (model is not ElementLevel el) return null;
        var d = new Dictionary<Section, List<byte[]>>();
        foreach (var layer in el.Layers)
            foreach (var sec in layer)
            {
                if (sec.Opaque) continue;
                var copies = new List<byte[]>(sec.Records.Count);
                foreach (var r in sec.Records) copies.Add((byte[])r.Clone());
                d[sec] = copies;
            }
        return d;
    }

    // Exactly one record's bytes may differ from the snapshot (the touched path record — same length
    // for a move, ±48 bytes for add / remove); every other record must be byte-identical.
    private static bool ElemPathLocalized(ElementLevel el, Dictionary<Section, List<byte[]>> snap, out int diffs)
    {
        diffs = 0;
        foreach (var layer in el.Layers)
            foreach (var sec in layer)
            {
                if (sec.Opaque || !snap.TryGetValue(sec, out var old)) continue;
                if (old.Count != sec.Records.Count) { diffs = -1; return false; }   // record count changed
                for (int i = 0; i < sec.Records.Count; i++)
                    if (!sec.Records[i].AsSpan().SequenceEqual(old[i])) diffs++;
            }
        return diffs == 1;
    }

    // First entity across all layers carrying a non-blank prefab name, as (layer, record).
    private static (int Layer, Tp2Entity? Ent) FirstEntity(Tp2Level tp)
    {
        for (int li = 0; li < tp.Layers.Count; li++)
            foreach (var e in tp.Layers[li].Entities)
                if (!string.IsNullOrWhiteSpace(e.Type)) return (li, e);
        return (-1, null);
    }

    // First decoration across all layers, as (layer, index-within-layer, record).
    private static (int Layer, int Index, Tp2Deco? Deco) FirstDeco(Tp2Level tp)
    {
        for (int li = 0; li < tp.Layers.Count; li++)
            if (tp.Layers[li].Decos.Count > 0)
                return (li, 0, tp.Layers[li].Decos[0]);
        return (-1, -1, null);
    }

    // Like FirstDeco but biased to a record followed by a type-0 inheritor, so the sprite
    // reassign exercises the run-length cascade guard.
    private static (int Layer, int Index, Tp2Deco? Deco) FirstDecoWithInheritor(Tp2Level tp)
    {
        for (int li = 0; li < tp.Layers.Count; li++)
        {
            var d = tp.Layers[li].Decos;
            for (int i = 0; i + 1 < d.Count; i++)
                if (d[i + 1].Type == 0 && Tp2DecoName(tp, li, i) is not null)
                    return (li, i, d[i]);
        }
        return FirstDeco(tp);
    }

    // The resolved sprite name of the deco at (layer, index) via the run-length tileTypes
    // chain (tp2editor.resolve_deco_names) — so a type-0 inherit deco is named correctly.
    private static string? Tp2DecoName(Tp2Level tp, int li, int di)
    {
        if (li < 0 || li >= tp.Layers.Count || di < 0 || di >= tp.Layers[li].Decos.Count) return null;
        var names = ResolveTp2DecoNames(tp, tp.Layers[li]).ToList();
        return di < names.Count ? names[di] : null;
    }

    /// <summary>S-D9 probe: what namespace does an entity TYPE live in? The loader looks the
    /// record's first string up in <c>ElementEntityDefs</c> with an exact <c>std::map&lt;string,
    /// shared_ptr&lt;ElementEntity&gt;&gt;::find</c> (<c>out_getdef2/b009_getDef_007f9f64.c</c>), and the
    /// caller (<c>out_rb/b035_readBinary_00a5f85c.c</c>, entity loop) only builds and <c>addElement</c>s
    /// when that find hits — a miss still consumes the record's bytes (so nothing else shifts) but the
    /// entity never enters the level. So the type is a DEF name, not a graphics cell name.
    ///
    /// This gate harvests the def names the shipped levels actually use (the proven-spawnable set),
    /// times a full-corpus scan, and reports how that set intersects the atlas cell list the entity
    /// dropdown currently offers — the number that shows whether the picker points at the right
    /// namespace. It also cross-checks every harvested name against the game binary's string table
    /// (defs are compiled in; there is no entitydefs script in the install). Read-only.</summary>
    public static void EntityTypes()
    {
        using var log = new StreamWriter("_entitytypes.txt", append: false);
        void Emit(string s) { Console.WriteLine(s); log.WriteLine(s); }

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game} (no {dir})"); continue; }
            if (game == "tp2") { Tp2EntityTypeAudit(dir, Emit); continue; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var names = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int levels = 0, recs = 0, failed = 0;
            foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f))
            {
                object? model;
                try { model = LevelLoader.Load(lvl).Model; }
                catch { failed++; continue; }
                levels++;
                switch (model)
                {
                    case TbLevel tb:
                        foreach (var l in tb.Layers) foreach (var e in l.Entities)
                        {
                            if (string.IsNullOrEmpty(e.Name)) continue;
                            recs++; names[e.Name] = names.GetValueOrDefault(e.Name) + 1;
                        }
                        break;
                    case ElementLevel el:
                        foreach (var layer in el.Layers) foreach (var sec in layer)
                        {
                            if (!sec.Opaque || sec.Count <= 0 || sec.Block.Length == 0) continue;
                            var (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
                            if (!ok || heads.Count != sec.Count) continue;
                            foreach (var h in heads)
                            {
                                if (string.IsNullOrEmpty(h.Name)) continue;
                                recs++; names[h.Name] = names.GetValueOrDefault(h.Name) + 1;
                            }
                        }
                        break;
                }
            }
            sw.Stop();

            Emit($"{game}: {levels} levels ({failed} undecodable) in {sw.ElapsedMilliseconds} ms, "
                + $"{recs} entity records, {names.Count} distinct TYPE names");
            Emit("   sample: " + string.Join(", ", names.Keys.Take(12)));

            // (a) the namespace the entity picker currently offers: graphics cell names.
            var first = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).FirstOrDefault();
            var aroot = first is null ? null : AssetsRoot.ForLevel(first);
            var ares = aroot is null ? null : SpriteResolver.TryLoad(aroot);
            if (ares is not null)
            {
                var cells = new HashSet<string>(ares.CellNames, StringComparer.Ordinal);
                int overlap = names.Keys.Count(cells.Contains);
                Emit($"   atlas cells={cells.Count}, of the {names.Count} entity TYPEs only {overlap} "
                    + $"are also cell names ({overlap * 100 / Math.Max(1, names.Count)}%)");
            }
            else Emit("   (no asset tree: cell-name overlap not measured)");

            // (b) every harvested name must exist as a string in the game binary — the defs are
            // compiled in, so a name missing from the binary could never have been spawned.
            var exe = FindGameExe(dir);
            if (exe is not null)
            {
                string blob = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(exe));
                int missing = names.Keys.Count(n => !blob.Contains(n, StringComparison.Ordinal));
                Emit($"   binary {Path.GetFileName(exe)}: {names.Count - missing}/{names.Count} TYPE names present, "
                    + $"{missing} missing"
                    + (missing > 0 ? " -> " + string.Join(", ", names.Keys.Where(n => !blob.Contains(n, StringComparison.Ordinal)).Take(6)) : ""));
            }
            else Emit("   (game exe not found next to the assets dir: binary cross-check skipped)");
        }
    }

    /// <summary>TP2 entity-type audit (R4-C). Unlike the raw-span families, TP2 entities are typed
    /// and the curated <see cref="Tp2Entities"/> table is the authority on what the engine will spawn
    /// on its own. <see cref="EntityDefs.Harvest"/> for a Tp2Level, however, adds EVERY <c>e.Type</c>
    /// string a shipped level carries — which includes sub-parts and attached "things" the level uses
    /// but the game cannot place as a fresh entity. This proves that gap exists (the corpus harvest is
    /// a strict superset of the curated set), which is exactly what the New-entity picker must police.
    /// Read-only.</summary>
    private static void Tp2EntityTypeAudit(string dir, Action<string> Emit)
    {
        var corpus = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int levels = 0, failed = 0;
        foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f))
        {
            object? model;
            try { model = LevelLoader.Load(lvl).Model; }
            catch { failed++; continue; }
            if (model is not Tp2Level tp) continue;
            levels++;
            foreach (var l in tp.Layers)
                foreach (var e in l.Entities)
                    if (!string.IsNullOrEmpty(e.Type)) corpus[e.Type!] = corpus.GetValueOrDefault(e.Type!) + 1;
        }
        var curated = new HashSet<string>(Tp2Entities.All.Select(d => d.Name), StringComparer.Ordinal);
        var leaked = corpus.Keys.Where(n => !curated.Contains(n)).ToList();
        Emit($"tp2: {levels} levels ({failed} undecodable), {corpus.Count} distinct corpus TYPE names, "
            + $"curated catalogue {curated.Count} names");
        Emit($"   corpus-not-in-curated (sub-parts / non-placeable things): {leaked.Count}"
            + (leaked.Count > 0 ? " e.g. " + string.Join(", ", leaked.Take(15)) : ""));
        var curatedUnused = curated.Where(n => !corpus.ContainsKey(n)).ToList();
        Emit($"   curated-not-in-corpus (valid but unused by stock levels): {curatedUnused.Count}"
            + (curatedUnused.Count > 0 ? " e.g. " + string.Join(", ", curatedUnused.Take(10)) : ""));
    }

    // S-D9 proof: the entity TYPE catalogue the shell now consults before applying a type must refuse
    // exactly the names the game would drop (ElementEntityDefs::getDef misses) and never refuse a name
    // a stock level already uses (or the editor would be refusing content it loaded itself). Runs the
    // same EntityDefs.Harvest the load path runs, then re-walks every level of the install.
    public static void EntityDefsCheck()
    {
        using var log = new StreamWriter("_entitydefs.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0, families = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK   " + what); } else { fail++; Emit("  FAIL " + what); } }

        foreach (var (game, dir) in Defaults)
        {
            if (game == "tp2") continue;               // tp2 keeps its curated Tp2Entities catalogue
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = FindEntityLevel(dir);
            if (level is null) { Emit($"SKIP {game}: no level with an editable entity"); continue; }
            families++;

            var decode = LevelLoader.Load(level).Profile.Decode;
            var cat = EntityDefs.Harvest(dir, decode);
            Emit($"{game}: catalogue of {cat.Count} TYPE names from {Directory.GetFiles(dir, "*.bin").Length} levels");
            Check(cat.Count >= 8, $"{game}: catalogue large enough to police ({cat.Count})");
            if (cat.Count == 0) continue;              // nothing to probe against (thin install)

            // 1. no stock entity may be refused: every type any shipped level carries is in the catalogue.
            int unseen = 0, ents = 0; string firstBad = "";
            foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f))
            {
                object model;
                try { model = decode(File.ReadAllBytes(lvl)); } catch { continue; }
                foreach (var n in EntityDefs.Names(model))
                {
                    ents++;
                    if (cat.Knows(n)) continue;
                    unseen++;
                    if (firstBad.Length == 0) firstBad = $"{Path.GetFileName(lvl)}: {n}";
                }
            }
            Check(unseen == 0, $"{game}: all {ents} entity records across the corpus resolve (unresolved={unseen}" +
                  (unseen > 0 ? $", first {firstBad}" : "") + ")");

            // 2. the bug class: an atlas cell name used as a type must be refused, since the engine's
            //    def map and the atlas cell list are different namespaces (see EntityTypes).
            var aroot = AssetsRoot.ForLevel(level);
            var ares = aroot is null ? null : SpriteResolver.TryLoad(aroot);
            if (ares is not null)
            {
                var bogus = ares.CellNames.Where(c => !cat.Knows(c)).Take(3).ToList();
                int refused = ares.CellNames.Count(c => !cat.Knows(c));
                Emit($"   atlas cells={ares.CellNames.Count}, refused as types={refused}"
                    + (bogus.Count > 0 ? " e.g. " + string.Join(", ", bogus) : ""));
                Check(refused * 20 >= ares.CellNames.Count,
                    $"{game}: cell-name types get refused ({refused}/{ares.CellNames.Count})");
            }
            else Emit("   (no asset tree: cell-name refusal not measured)");

            // 3. a fabricated name and a case-flipped real one must both be refused (getDef is an
            //    exact std::map find, so nothing looser than an ordinal match is honest).
            Check(!cat.Knows("zz_probe_entity_cell"), $"{game}: fabricated type refused");
            var probe = cat.Names[0];
            var flip = probe.Length > 3 && char.IsLetter(probe[0])
                ? $"{(char.IsUpper(probe[0]) ? char.ToLowerInvariant(probe[0]) : char.ToUpperInvariant(probe[0]))}{probe[1..]}" : null;
            if (flip is not null && !cat.Names.Contains(flip, StringComparer.Ordinal))
                Check(!cat.Knows(flip), $"{game}: case-flipped type refused (\u201c{flip}\u201d vs \u201c{probe}\u201d)");
            else Emit($"   (no clean case-flip probe off \u201c{probe}\u201d: skipped)");
            Check(!cat.Knows(probe + "_x"), $"{game}: suffixed type refused");
            Check(cat.Knows(probe), $"{game}: catalogue name accepted");
        }

        Emit("");
        Emit($"ENTITY DEFS: {pass} pass, {fail} fail ({families}/3 families covered)");
        Emit(fail == 0 && families == 3 ? "ENTITY DEFS GREEN" : "ENTITY DEFS FAILURES");
    }

    // S-D10 proof: adding an entity that was never in the file. The claim being tested is not "the
    // editor shows one more cross" but "the GAME would": the appended record has to be found by the
    // loader's own read-order walk (SplitRecords / MeasureRecord via the codec's decode), survive a
    // save and a reload, and land at the coordinates that were asked for. The last assertion is the
    // strict one: spawn then delete must give back the ORIGINAL FILE BYTE FOR BYTE, so an add can
    // never leave residue in a level the author reverts (I3 localisation taken to its limit).
    public static void EntityNewCheck()
    {
        using var log = new StreamWriter("_entitynew.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0, skipped = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK   " + what); } else { fail++; Emit("  FAIL " + what); } }

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = FindEntityLevel(dir);
            if (level is null) { Emit($"SKIP {game}: no level with an entity"); continue; }
            Emit($"{game}: {Path.GetFileName(level)}");

            foreach (var mode in new[] { "in-level", "corpus", "singleword", "unknown", "badname" })
            {
                var (ok, skip, note) = TestSpawn(dir, level, mode);
                if (skip) { skipped++; Emit($"  SKIP {game}/{mode}: {note}"); continue; }
                Check(ok, $"{game}/{mode}: {note}");
            }
            if (game == "tp2")
            {
                // R4-C: TP2 is policed from the curated Tp2Entities authority, so even a single-level
                // folder yields the full definition set (>= 8) instead of standing down. Prove it.
                var decode1 = LevelLoader.Load(level).Profile.Decode;
                var thin = EntityDefs.Harvest(dir, decode1, maxFiles: 1);
                bool authoritative = thin.Count >= 8 && thin.TypedRecords
                    && thin.Knows("lava") && thin.Knows("horse_egypt1") && thin.Knows("bunsen");
                bool refusesThings = !thin.Knows("bunsun") && !thin.Knows("Arabian Horse")
                    && !thin.Knows("zz_probe_entity_cell") && !thin.Knows("bonuslevelsbutton");
                Check(authoritative, $"tp2/thin-folder: curated authority survives 1 level (count={thin.Count}, typed={thin.TypedRecords})");
                Check(refusesThings, "tp2/thin-folder: non-placeable things refused (bunsun, Arabian Horse, probe, bonus button)");
            }
        }

        Emit("");
        Emit($"ENTITY NEW: {pass} pass, {fail} fail, {skipped} skipped");
        Emit(fail == 0 && pass >= 4 ? "ENTITY NEW GREEN" : "ENTITY NEW FAILURES");
    }

    /// <summary>One spawn case. <c>in-level</c> reuses a type the level already carries, <c>corpus</c>
    /// uses a type the install ships but this level does not (that is the case that needs a template
    /// record), <c>unknown</c> / <c>badname</c> must both be refused with the file untouched.</summary>
    private static (bool Ok, bool Skip, string Note) TestSpawn(string dir, string level, string mode)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        if (!doc.Encode().AsSpan().SequenceEqual(orig)) return (false, false, "baseline encode not byte-exact");

        var cat = EntityDefs.Harvest(dir, doc.Profile.Decode);
        var used = EntityDefs.Names(doc.Model).ToList();
        var distinct = used.Distinct(StringComparer.Ordinal).ToList();
        string type = mode switch
        {
            "in-level" => distinct.FirstOrDefault() ?? "",
            // A name the game ships that this level does not use: for tp2 / f4 / f5 it must have a
            // record to copy, so require one the harvest can actually supply.
            "corpus" => cat.Names.FirstOrDefault(n => !used.Contains(n, StringComparer.Ordinal)
                        && (doc.Model is Tp2Level || cat.ProtoFor(n)?.Raw is { Length: > 16 })) ?? "",
            // R4-C: a real TP2 entity with NO underscore (lava, sun, pyramid) - the curated authority
            // must let it spawn even though the raw-span underscore rule used to block it. Not offered
            // for the raw-span families (their names always carry an underscore), so they skip.
            "singleword" => doc.Model is Tp2Level
                ? cat.Names.FirstOrDefault(n => n.IndexOf('_') < 0 && !used.Contains(n, StringComparer.Ordinal)) ?? ""
                : "",
            "unknown" => "zz_probe_entity_cell",
            "badname" => "nounderscorehere",
            _ => "",
        };
        if (type.Length == 0) return (true, true, "no candidate name");
        bool expectOk = mode is "in-level" or "corpus" or "singleword";

        // Spawn next to the level's last entity, in its own layer, on integer coordinates: the
        // raw-span families store positions as int32 deltas, so a fractional target would only
        // prove the codec's rounding rather than the placement.
        var ents0 = doc.Scene.Crosses.Where(c => c.Kind == SceneKind.Entity).ToList();
        if (ents0.Count == 0) return (false, false, "projection has no entity to anchor on");
        int li = ents0[^1].Layer;
        int n0 = ents0.Count(e => e.Layer == li);
        double tx = Math.Round(ents0[^1].At.X) + 137, ty = Math.Round(ents0[^1].At.Y) + 211;

        var r = EntitySpawn.Add(doc.Model, li, tx, ty, type, cat, out string? detail);
        if (!expectOk)
        {
            bool refused = r != EditResult.Done && !string.IsNullOrEmpty(detail);
            bool untouched = doc.Encode().AsSpan().SequenceEqual(orig);
            return (refused && untouched, false, $"refused \u201c{type}\u201d={refused} bytesUntouched={untouched}"
                    + (refused ? $" ({detail})" : ""));
        }
        if (r != EditResult.Done)
            return (false, false, $"spawn \u201c{type}\u201d refused: {detail}");

        // 1. the model now carries one more entity on that layer, named and placed as asked.
        var ents1 = SceneProjector.Project(doc.Model).Crosses
            .Where(c => c.Kind == SceneKind.Entity && c.Layer == li).ToList();
        var nw = ents1.Count > 0 ? ents1[^1] : null;
        bool grew = ents1.Count == n0 + 1;
        bool named = nw?.SpriteName == type;
        bool placed = nw is not null && Math.Abs(nw.At.X - tx) <= 1 && Math.Abs(nw.At.Y - ty) <= 1;

        // 2. save, then read the saved bytes back: still there, and re-encoding is stable.
        byte[] edited = doc.Encode();
        object back = doc.Profile.Decode(edited);
        var ents2 = SceneProjector.Project(back).Crosses
            .Where(c => c.Kind == SceneKind.Entity && c.Layer == li).ToList();
        var rel = ents2.Count > 0 ? ents2[^1] : null;
        bool survives = ents2.Count == n0 + 1 && rel?.SpriteName == type
                        && Math.Abs(rel.At.X - tx) <= 1 && Math.Abs(rel.At.Y - ty) <= 1;
        byte[] re = doc.Profile.Codec.Encode(back);
        bool stable = re.AsSpan().SequenceEqual(edited);

        // 3. every pre-existing entity keeps its name (no collateral damage down the chain).
        var before = ents0.Where(e => e.Layer == li).Select(e => e.SpriteName).ToList();
        var after = ents1.Take(ents1.Count - 1).Select(e => e.SpriteName).ToList();
        bool noCollateral = before.SequenceEqual(after);

        // 4. spawn + delete == the original file, byte for byte. Fresh document: the delete handle
        //    (an EntityEditHandle for f4/f5) must point into the model being edited.
        var d2 = LevelLoader.Load(level);
        var r2 = EntitySpawn.Add(d2.Model, li, tx, ty, type, cat, out _);
        var live = SceneProjector.Project(d2.Model).Crosses
            .Where(c => c.Kind == SceneKind.Entity && c.Layer == li).ToList();
        var del = r2 == EditResult.Done && live.Count > 0
            ? EditOps.Delete(d2.Model, live[^1].Source) : EditResult.Failed;
        bool reversible = del == EditResult.Done && d2.Encode().AsSpan().SequenceEqual(orig);

        bool ok = grew && named && placed && survives && stable && noCollateral && reversible;
        string note = $"\u201c{type}\u201d grew={grew}({n0}->{ents1.Count}) named={named} placed={placed}"
                    + $" reload={survives} stable={stable} noCollateral={noCollateral} deleteRestoresBytes={reversible}"
                    + $" :: {detail}";
        return (ok, false, note);
    }

    // S-D10 fix proof for the inspector's "value snaps back after I type it" bug, plus the .bin
    // association parse that made the rename prompt fire on every launch.
    //
    // The rows are filled from <see cref="SceneProjector.FieldTailNamed"/>, which hands back its own
    // COPY of the record's tail bytes; the panel caches that copy. So the invariant the fix rests on is
    // not "the write works" (tailedit already proves that) but "a FRESH read shows the new number while
    // the cached copy still shows the old one" — which is precisely why re-filling from the cache after
    // a commit displays a revert even though the file has the edit. The panel now re-reads (SyncTail);
    // this gate pins the behaviour it depends on, and that restoring the value gives the original file
    // back byte for byte.
    public static void RefillCheck()
    {
        using var log = new StreamWriter("_refill.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0, skipped = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK   " + what); } else { fail++; Emit("  FAIL " + what); } }

        // ---- (1) the open-command parse IsStale() reads. The command is \"exe\" \"%1\" : cutting at the
        // LAST quote swallows `" "%1` into the path, so the file can never exist and the prompt never
        // stops. This is the exact string Register writes, so it must parse to the exe alone.
        string q = Path.Combine(AppContext.BaseDirectory, "TastyEdit.exe");
        Check(Editing.FileAssociation.ExeOf($"\"{q}\" \"%1\"") == q, "assoc: quoted exe ahead of a quoted %1");
        Check(Editing.FileAssociation.ExeOf($"{q} %1") == q, "assoc: unquoted exe + argument");
        Check(Editing.FileAssociation.ExeOf($"\"{q}\"") == q, "assoc: exe only, no placeholder");
        Check(Editing.FileAssociation.ExeOf($"\"{q}\" \"%L\"") == q, "assoc: never cuts at the last quote");
        // Read-only report of this machine's real state (no assertion: a gate must not depend on what
        // the developer's Explorer settings happen to be, but it should say what it sees).
        Emit($"  assoc now: registered={Editing.FileAssociation.IsRegistered()} "
             + $"stale={(Editing.FileAssociation.StaleReason() ?? "no")}");

        // ---- (2) fresh-read-after-write, per family, on the fields the inspector shows.
        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { Emit($"SKIP {game}: no levels dir {dir}"); continue; }
            var level = FindEntityLevel(dir);
            if (level is null) { Emit($"SKIP {game}: no level with an entity"); continue; }
            var doc = LevelLoader.Load(level);
            byte[] orig = doc.Original;
            var ents = SceneProjector.Project(doc.Model).Crosses
                .Where(c => c.Kind == SceneKind.Entity && c.Source is not null)
                .Select(c => c.Source!).ToList();
            if (ents.Count == 0) { skipped++; Emit($"SKIP {game}: no projected entity"); continue; }
            Emit($"{game}: {Path.GetFileName(level)}");

            if (doc.Model is Tp2Level)
            {
                // TP2 rows read the typed model directly, so the only way this lane could lie is if
                // FieldSet wrote somewhere FieldGet does not read.
                object src0 = ents[0];
                double m0 = SceneProjector.FieldGet(src0, "mass") ?? 0;
                bool ok = SceneProjector.FieldSet(src0, "mass", m0 + 3.25)
                          && Math.Abs((SceneProjector.FieldGet(src0, "mass") ?? 0) - (m0 + 3.25)) < 1e-6;
                SceneProjector.FieldSet(src0, "mass", m0);
                Check(ok && doc.Encode().AsSpan().SequenceEqual(orig),
                    $"tp2: mass read-back after write is fresh, restore is byte-exact (mass {m0:0.##})");
                continue;
            }

            // Not every record carries a behaviour block with a number in it (a plain static prop has an
            // empty tail), so look for one that does rather than asserting on whichever entity happens
            // to be first — otherwise a family silently drops out of the gate.
            object? src = null;
            (List<TailField> Fields, int Consumed, byte[] Tail, int FrameLen)? named = null;
            TailField? fldN = null;
            foreach (var cand in ents.Take(25))
            {
                if (!SceneProjector.IsTailEditable(cand)) continue;
                var nm = SceneProjector.FieldTailNamed(cand);
                if (nm is null) continue;
                var f = nm.Value.Fields.Cast<TailField?>().FirstOrDefault(x =>
                    x!.Value.Kind is TailKind.Int or TailKind.F64 or TailKind.F32 && x!.Value.EditableWithin(nm.Value.FrameLen));
                if (f is null) continue;
                src = cand; named = nm; fldN = f; break;
            }
            if (src is null || named is not ({ } nmv) || fldN is null)
            { skipped++; Emit($"  SKIP {game}: no entity with an editable value field in frame (scanned {Math.Min(ents.Count, 25)} of {ents.Count})"); continue; }
            TailField fld = fldN.Value;
            byte[] cached = nmv.Tail;                        // what the panel would keep holding
            double old = fld.Kind == TailKind.Int ? Convert.ToInt32(TailSem.Value(cached, fld))
                                                  : Convert.ToDouble(TailSem.Value(cached, fld));
            double want = fld.Kind == TailKind.Int ? (int)old ^ 0x00100001 : old + 1.5;
            if (!SceneProjector.FieldTailSet(src, fld, want)) { Check(false, $"{game}: write of {fld.Name} refused"); continue; }

            var again = SceneProjector.FieldTailNamed(src);
            double fresh = 0, stale = 0;
            bool reFound = false;
            if (again is { } ag)
            {
                TailField? rf = ag.Fields.Cast<TailField?>()
                    .FirstOrDefault(f => f!.Value.LaneOff == fld.LaneOff && f!.Value.Name == fld.Name);
                if (rf is not null)
                {
                    reFound = true;
                    TailField rfx = rf.Value;
                    fresh = fld.Kind == TailKind.Int ? Convert.ToInt32(TailSem.Value(ag.Tail, rfx))
                                                    : Convert.ToDouble(TailSem.Value(ag.Tail, rfx));
                }
                stale = fld.Kind == TailKind.Int ? Convert.ToInt32(TailSem.Value(cached, fld))
                                                 : Convert.ToDouble(TailSem.Value(cached, fld));
            }
            double target = fld.Kind == TailKind.F32 ? (float)want : want;
            bool readFresh = reFound && Math.Abs(fresh - target) <= 1e-6;
            bool copyWasStale = Math.Abs(stale - old) < 1e-9 && Math.Abs(fresh - stale) > 1e-9;

            SceneProjector.FieldTailSet(src, fld, old);      // put the record back
            bool restored = doc.Encode().AsSpan().SequenceEqual(orig);
            Check(readFresh, $"{game}: a re-read of \u201c{fld.Name}\u201d shows the written value "
                          + $"({old:0.####} \u2192 {fresh:0.####}) \u2014 this is what the panel now does after a commit");
            Check(copyWasStale, $"{game}: the copy taken before the write still reads {stale:0.####} "
                              + "(why re-filling from the cache looked like a revert)");
            Check(restored, $"{game}: undoing the write restores the file byte for byte");
        }

        Emit("");
        Emit($"REFILL: {pass} pass, {fail} fail, {skipped} skipped");
        Emit(fail == 0 && pass >= 7 ? "REFILL GREEN" : "REFILL FAILURES");
    }

    // S-D11 (deco render fidelity). A decoration's dimensions.raw IS the world box it occupies, in
    // the same centiunits as the record's own position (world = raw/100). The shipped data proves it:
    // Tasty Blue alone carries 4709 records whose dims are exactly origWidth*100 x origHeight*100
    // (cloud1: [18300,10900] over a 183x109 cell), its X and Y factors track the cell aspect on
    // 100% of records, and only 36 records sit on the Qt oracle's 16000 baseline - at values like
    // 15987/16010, i.e. coincidence rather than an authored default. Scaling the pixmap by
    // dims/16000 (the old rule) was therefore wrong by (cell pixels / 160) for every sprite: props
    // under 160 px drew too small, so parts the game shows touching had seams in the editor.
    // ElementEngine also multiplies a sprite's RGB by the record colour (tbeditor.sprite_pixmap ->
    // tbassets.build_sprite -> ImageChops.multiply, alpha kept), which the element bridge ignored.
    // This gate pins both against the bytes and the artwork. Read-only: it never writes a level.
    public static void DecoFitCheck()
    {
        using var log = new StreamWriter("_decofit.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0, skipped = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK   " + what); } else { fail++; Emit("  FAIL " + what); } };
        static bool Close(double? a, double? b)
            => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) < 1e-9;
        static (int W, int H)? RawDims(object? src) => src switch
        {
            TbDeco td => (td.DimW, td.DimH),
            DecoEditHandle dh => ElemEdit.DecoDims(dh),
            Tp2Deco d when d.DimsRaw is { Length: >= 2 } a => (a[0], a[1]),
            _ => null,
        };

        const int levelCap = 6;
        ISpriteSource? elementBridge = null;
        string? tintName = null;
        int anyFlip = 0;

        foreach (var (game, dir) in Defaults)
        {
            if (!Directory.Exists(dir)) { skipped++; Emit($"SKIP {game}: no levels dir"); continue; }
            var levels = Directory.GetFiles(dir, "*.bin").OrderBy(f => f).ToArray();
            if (levels.Length == 0) { skipped++; Emit($"SKIP {game}: empty levels dir"); continue; }
            ISpriteSource? ss = null;
            try
            {
                if (game == "tp2")
                {
                    var tr = Tp2AssetsRoot.ForLevel(levels[0]);
                    var tres = tr is null ? null : Tp2SpriteResolver.TryLoad(tr);
                    ss = tres is null ? null : new Tp2SpriteSource(tres);
                }
                else
                {
                    var ar = AssetsRoot.ForLevel(levels[0]);
                    var ares = ar is null ? null : SpriteResolver.TryLoad(ar);
                    ss = ares is null ? null : new SpriteSource(ares);
                    elementBridge ??= ss;
                }
            }
            catch { ss = null; }

            int decos = 0, boxOk = 0, natural = 0, coloured = 0, zeroDim = 0;
            int flips = 0, zOut = 0, coverBad = 0, zLevels = 0, flipMismatch = 0;
            int fr0 = 0, frn = 0, fboth = 0, fxonly = 0, fyonly = 0;
            var fSample = new List<string>();
            double ratioSum = 0; int ratioN = 0;
            foreach (var lvl in levels.Take(levelCap))
            {
                SceneGeometry scene;
                try { scene = LevelLoader.Load(lvl).Scene; }
                catch { continue; }
                // The list the viewport actually paints, not the record-order Crosses list.
                List<SceneCross> all = scene.PaintOrder().ToList();

                // Draw order (S-D12): the drawn list must be in the game's order per layer, and a
                // layer's entities must come AFTER its decorations (the oracle puts entities a fixed
                // 1000 above the decos of the same layer, which is how the game hides tile seams).
                zLevels++;
                foreach (var grp in all.Where(c => c.Kind is SceneKind.Deco or SceneKind.Entity).GroupBy(c => c.Layer))
                {
                    double prev = double.MinValue;
                    int lastDeco = -1, firstEnt = -1, i = 0;
                    foreach (var c in grp)
                    {
                        if (c.ZOrder < prev) zOut++;
                        prev = c.ZOrder;
                        if (c.Kind == SceneKind.Deco) lastDeco = i; else if (firstEnt < 0) firstEnt = i;
                        i++;
                    }
                    if (lastDeco >= 0 && firstEnt >= 0 && lastDeco > firstEnt) coverBad++;
                }

                var ms = all.Where(c => c.Kind == SceneKind.Deco).ToList();
                foreach (var cr in ms)
                {
                    decos++;
                    if (cr.FlipX || cr.FlipY)
                    {
                        flips++;
                        if (Math.Abs(cr.RotationDeg) < 0.001) fr0++; else frn++;
                        if (cr.FlipX && cr.FlipY) fboth++; else if (cr.FlipX) fxonly++; else fyonly++;
                        if (fSample.Count < 8)
                            fSample.Add($"{cr.SpriteName} rot={cr.RotationDeg:0.#} fx={cr.FlipX} fy={cr.FlipY}");
                    }
                    // A flip must be the record's own words, not a render-time coin flip: the same
                    // flag has to come back off the model the cross points at.
                    bool srcFlip = cr.Source switch
                    {
                        TbDeco td => td.FlipX == cr.FlipX && td.FlipY == cr.FlipY,
                        Tp2Deco td => td.FlipX == cr.FlipX && td.FlipY == cr.FlipY,
                        _ => true,
                    };
                    if (!srcFlip) flipMismatch++;
                    var raw = RawDims(cr.Source);
                    bool expectTp2 = game == "tp2";
                    bool ok = raw is null
                        ? cr.DecoWorldW is null && cr.DecoWorldH is null
                        : Close(cr.DecoWorldW, DimBox(raw.Value.W, expectTp2))
                          && Close(cr.DecoWorldH, DimBox(raw.Value.H, expectTp2));
                    if (ok) boxOk++;
                    if (raw is not null && raw.Value.W == 0) zeroDim++;
                    if (cr.ColorRgba is { Length: >= 3 } col
                        && (col[0] != 255 || col[1] != 255 || col[2] != 255)) coloured++;

                    if (ss is null || cr.SpriteName is not { Length: > 0 } nm) continue;
                    if (!ss.TryGetDeco(nm, 0, null, out _, out int pw, out int ph, out _, out _, out _)
                        || pw <= 0 || ph <= 0) continue;
                    tintName ??= game != "tp2" ? nm : null;
                    if (cr.DecoWorldW is double bw && cr.DecoWorldH is double bh)
                    {
                        // A natural-size record (dims == cell pixels * 100) must land on the sprite's
                        // own pixel box: that identity is what ties the world unit to the artwork.
                        if (Math.Abs(bw - pw) < 0.5 && Math.Abs(bh - ph) < 0.5) natural++;
                        // How far the old pixmap-scaling rule sat from the record's box.
                        if (raw is not null)
                        {
                            double old = pw * ((raw.Value.W == 0 ? 16000 : raw.Value.W) / 16000.0);
                            if (old > 0) { ratioSum += bw / old; ratioN++; }
                        }
                    }
                }
            }

            Check(decos > 0, $"{game}: {decos} decorations projected");
            Check(zOut == 0, $"{game}: markers are in the game's paint order per layer ({zOut} out of order in {zLevels} levels)");
            Check(coverBad == 0, $"{game}: a layer's entities all draw over its decorations ({coverBad} layers wrong)");
            Check(flipMismatch == 0, $"{game}: every flipped cross carries its record's own flags ({flipMismatch} mismatched)");
            anyFlip += flips;
            Emit($"  {game}: {flips}/{decos} decorations carry a flip flag "
                 + $"(x-only {fxonly}, y-only {fyonly}, both {fboth}; rotation 0: {fr0}, rotation != 0: {frn})");
            foreach (var s in fSample) Emit($"     flip sample: {s}");
            Check(boxOk == decos,
                $"{game}: every deco box equals its record dimensions ({boxOk}/{decos})");
            Emit($"  {game}: {coloured} records carry a non-white colour, {zeroDim} set no width; "
                 + $"{natural} draw a box exactly the sprite's pixel size; box/old-rule mean "
                 + (ratioN > 0 ? $"{ratioSum / ratioN:F2}x over {ratioN} sized decos" : "n/a"));
            if (game == "tp2") { Emit($"  {game}: TP2 keeps its validated dims/100 box (unchanged)"); }
            else Check(natural > 0,
                $"{game}: natural-size records draw the sprite's own pixel box ({natural} found)");
        }

        Check(anyFlip > 0, $"the flip flag is real in the shipped levels ({anyFlip} flipped decorations found)");

        // Tint arithmetic on a real sprite: build_sprite multiplies RGB by the record colour and
        // puts the ORIGINAL alpha back, so nothing about the sprite's shape may move.
        if (elementBridge is not null && tintName is not null)
        {
            bool g0 = elementBridge.TryGet(tintName, 0, null, out var img0, out int w0, out int h0, out _, out _, out _);
            bool gw = elementBridge.TryGet(tintName, 0, new byte[] { 255, 255, 255, 255 }, out var imgW, out _, out _, out _, out _, out _);
            bool gc = elementBridge.TryGet(tintName, 0, new byte[] { 128, 255, 255, 255 }, out var imgC, out int wc, out int hc, out _, out _, out _);
            Check(g0 && gw && gc, $"record colour does not break sprite resolution ({tintName})");
            // A successful resolve must hand back the bitmap as well as its size - the cold
            // (uncached) build path is the one a fresh record colour takes.
            Check(!g0 || img0 is not null, "untinted resolve returns a bitmap");
            Check(!gc || imgC is not null, "tinted resolve returns a bitmap");
            if (g0 && gw)
                Check(ReferenceEquals(img0, imgW), "a white colour is the identity multiply: the untinted bitmap is reused");
            if (g0 && gc && img0 is not null && imgC is not null)
            {
                Check(wc == w0 && hc == h0, "tinting never resizes the sprite");
                var px0 = Pixels(img0, w0, h0);
                var pxc = Pixels(imgC, wc, hc);
                int op = -1;
                for (int i = 3; i < px0.Length; i += 4) if (px0[i] == 255) { op = i - 3; break; }
                Check(op >= 0, "the sample sprite has an opaque pixel to measure");
                if (op >= 0)
                {
                    Check(Math.Abs(pxc[op] - px0[op]) <= 1, $"blue survives a (128,255,255) tint ({px0[op]} -> {pxc[op]})");
                    Check(Math.Abs(pxc[op + 1] - px0[op + 1]) <= 1, $"green survives ({px0[op + 1]} -> {pxc[op + 1]})");
                    Check(Math.Abs(pxc[op + 2] - px0[op + 2] * 128 / 255) <= 1,
                        $"red scales by the record's red ({px0[op + 2]} -> {pxc[op + 2]}, want {px0[op + 2] * 128 / 255})");
                }
                int aDiff = 0;
                for (int i = 3; i < px0.Length; i += 4) if (px0[i] != pxc[i]) aDiff++;
                Check(aDiff == 0, $"every pixel keeps its alpha after the multiply ({aDiff} changed)");
            }
        }
        else { skipped++; Emit("  SKIP tint arithmetic: no ElementEngine asset bridge here"); }

        Emit("");
        Emit($"DECOFIT: {pass} pass, {fail} fail, {skipped} skipped");
        Emit(fail == 0 && pass >= 10 ? "DECOFIT GREEN" : "DECOFIT FAILURES");

        static double? DimBox(int dimRaw, bool tp2) => tp2
            ? (dimRaw != 0 ? dimRaw : 16000) / 100.0            // TP2's documented 16000 default
            : (dimRaw != 0 ? (double?)dimRaw / 100.0 : null);   // else: no box, so the cell's own size
    }

    /// <summary>Raw BGRA pixels of a bitmap, for pixel-level assertions in the render gates.</summary>
    private static byte[] Pixels(System.Windows.Media.Imaging.BitmapSource img, int w, int h)
    {
        var px = new byte[w * h * 4];
        img.CopyPixels(px, w * 4, 0);
        return px;
    }

    // The game's own executable: the install root is the parent of the assets folder that holds the
    // levels dir, so walk up to it and take the largest .exe there (the def-name string table lives
    // in that binary — there is no entitydefs script in any of the installs).
    private static string? FindGameExe(string levelsDir)
    {
        var d = new DirectoryInfo(levelsDir);
        for (int i = 0; i < 5 && d is not null; i++, d = d.Parent)
        {
            var exe = d.GetFiles("*.exe").OrderByDescending(f => f.Length).FirstOrDefault();
            if (exe is not null) return exe.FullName;
        }
        return null;
    }

    private static string Kinds(SceneGeometry s)
    {
        var parts = new List<string>();
        foreach (SceneKind k in Enum.GetValues<SceneKind>())
        {
            int n = s.Polys.Count(p => p.Kind == k) + s.Lines.Count(p => p.Kind == k) + s.Crosses.Count(p => p.Kind == k);
            if (n > 0) parts.Add($"{k}:{n}");
        }
        return string.Join(" ", parts);
    }

    /// <summary>Per-section record dump for an element (f4/f5) level: counts plus the
    /// leading bytes of the first record in each framed section, hand-decoded as f64 /
    /// int32, to verify the GeoReader field order against the real bytes.</summary>
    public static void Dump(string path)
    {
        using var log = new StreamWriter("_smoke.txt", append: false);
        void Emit(string s) { Console.WriteLine(s); log.WriteLine(s); }
        try
        {
            var doc = LevelLoader.Load(path);
            Emit($"{doc.Profile.Short} {Path.GetFileName(path)}");
            if (doc.Model is not TastyEditor.Core.Levels.ElementLevel el) { Emit("not an element level"); return; }
            for (int li = 0; li < el.Layers.Count; li++)
            {
                var layer = el.Layers[li];
                for (int si = 0; si < layer.Count; si++)
                {
                    var sec = layer[si];
                    if (sec.Opaque) continue;
                    if (sec.Records.Count == 0) continue;
                    var r = sec.Records[0];
                    int show = Math.Min(r.Length, 48);
                    Emit($" L{li} sec{si} count={sec.Count} rec0len={r.Length} hex={Convert.ToHexString(r.AsSpan(0, show))}");
                    if (r.Length >= 32)
                        Emit($"      f64[0..3]={BitConverter.ToDouble(r, 0):.3f},{BitConverter.ToDouble(r, 8):.3f},{BitConverter.ToDouble(r, 16):.3f},{BitConverter.ToDouble(r, 24):.3f}");
                    if (r.Length >= 4)
                        Emit($"      i32[0]={BitConverter.ToInt32(r, 0)}");
                }
            }
        }
        catch (Exception ex) { Emit("ERROR " + ex); }
    }
}
