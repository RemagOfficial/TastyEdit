using System.IO;
using TastyEditor.Core.Levels;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

/// <summary>R2 gate (ROADMAP_RELEASE §3): prove the exact operations the viewport's transform
/// handles commit — the rotate knob (the record's turn field), the uniform corner grip (a
/// decoration's size knob, an entity's mass), the free-axis grip while Shift is held (the two
/// dimension fields every decoration record stores), and the right-click vertex removal (a shape
/// keeps at least three corners; a water box's corners are not free vertices). Each write must
/// stay byte-localised to the touched record (I3), persist through a fresh read, keep the level
/// re-encode-stable, and — for the fixed-width writes — restore the exact loaded bytes when
/// reverted (I1). Sweeps one stock level per game; never writes to disk.</summary>
public static partial class Smoke
{
    public static void HandlesCheck()
    {
        using var log = new StreamWriter("_handles.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0, games = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Emit("  OK   " + what); } else { fail++; Emit("  FAIL " + what); } }

        foreach (var (game, dir) in Defaults)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Emit($"SKIP {game}: no levels folder on this machine"); continue; }
            var level = PickDecoAndEntityLevel(dir);
            if (level is null) { Emit($"SKIP {game}: no sample level with both a deco and an entity"); continue; }
            games++;
            Emit($"{game}: {Path.GetFileName(level)}");
            try
            {
                RotateProof(game, level, SceneKind.Deco, Emit, Check);
                RotateProof(game, level, SceneKind.Entity, Emit, Check);
                UniformSizeProof(game, level, Emit, Check);
                FreeAxesProof(game, level, Emit, Check);
                EntityMassProof(game, level, Emit, Check);
                VertexRemoveProof(game, dir, Emit, Check);
                WaterRefusalProof(game, dir, Emit, Check);
            }
            catch (Exception ex) { fail++; Emit($"  FAIL {game}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Emit("");
        Emit($"HANDLES: {pass} pass, {fail} fail ({games}/4 games sampled)");
        Emit(fail == 0 && games == Games.Length ? "HANDLES GREEN" : "HANDLES FAILURES");
    }

    // ---------------------------------------------------------- level pick --

    // One bounded walk (60 files) per game: the first level that projects both a field-editable
    // decoration and an entity, so the four handle proofs share one sample.
    private static string? PickDecoAndEntityLevel(string dir)
    {
        foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f).Take(60))
        {
            try
            {
                var doc = LevelLoader.Load(lvl);
                bool deco = false, ent = false;
                foreach (var c in doc.Scene.Crosses)
                {
                    if (c.Kind == SceneKind.Deco && SceneProjector.FieldGet(c.Source, "dsize") is not null) deco = true;
                    if (c.Kind == SceneKind.Entity && SceneProjector.FieldGet(c.Source, "mass") is not null) ent = true;
                }
                if (deco && ent) return lvl;
            }
            catch { /* a level that fails to load just isn't the sample */ }
        }
        return null;
    }

    // nth decoration / entity source by projection order — the cross list keeps record order, so
    // the same index resolves the same record after a fresh read of the written bytes. Both
    // cover every family (the FieldsCheck helpers deliberately exclude TP2's typed records; the
    // handles do not, so neither do these).
    private static object? NthDecoAny(object model, int want)
    {
        int n = 0;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == SceneKind.Deco && SceneProjector.IsFieldDeco(c.Source) && n++ == want) return c.Source;
        return null;
    }

    private static object? NthEntAny(object model, int want)
    {
        int n = 0;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == SceneKind.Entity && SceneProjector.IsFieldEntity(c.Source) && n++ == want) return c.Source;
        return null;
    }

    private static object? NthRotatable(object model, SceneKind kind, int want)
    {
        int n = 0;
        foreach (var c in SceneProjector.Project(model).Crosses)
            if (c.Kind == kind && SceneProjector.IsRotatable(c.Source) && n++ == want) return c.Source;
        return null;
    }

    // ------------------------------------------------------------- rotate --

    // The knob's commit: an absolute turn through SetRotationDeg. A 37.5 degree step writes
    // exactly the record's own turn field, so at most 4 bytes move, and turning back to the
    // loaded angle must reproduce the loaded file byte for byte.
    private static void RotateProof(string game, string level, SceneKind kind, Action<string> Emit, Action<bool, string> Check)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        int idx = -1; object? src = null;
        for (int k = 0; ; k++)
        {
            var s = NthRotatable(doc.Model, kind, k);
            if (s is null) break;
            if (SceneProjector.GetRotationDeg(s) is not null) { idx = k; src = s; break; }
        }
        if (src is null) { Emit($"  ({game}: no turnable {kind.ToString().ToLowerInvariant()} in this sample)"); return; }

        double was = SceneProjector.GetRotationDeg(src)!.Value;
        double to = was + 37.5;
        bool wrote = SceneProjector.SetRotationDeg(src, to);
        byte[] edited = doc.Encode();
        double? got = SceneProjector.GetRotationDeg(NthRotatable(doc.Profile.Decode(edited), kind, idx));
        bool persists = got is double g && Math.Abs(g - to) < 0.02;
        int diff = DiffBytes(orig, edited);
        bool stable = StableReEncode(doc, edited);
        SceneProjector.SetRotationDeg(src, was);
        bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
        Check(wrote && persists && diff is > 0 and <= 8 && stable && revert,
            $"{game} {kind.ToString().ToLowerInvariant()}: turn {to - was:+#;-#;0}\u00b0 bytes={diff} persists={got} stable={stable} revert={revert}");
    }

    // -------------------------------------------------------- size, uniform --

    // The corner grip without Shift: the decoration's single size knob (aspect kept, the pair of
    // dimension ints moves together, so <= 8 bytes), and an entity's grip lands on mass (the one
    // field its drawn size comes from).
    private static void UniformSizeProof(string game, string level, Action<string> Emit, Action<bool, string> Check)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        int idx = -1; object? src = null;
        for (int k = 0; ; k++)
        {
            var s = NthDecoAny(doc.Model, k);
            if (s is null) break;
            if (SceneProjector.FieldGet(s, "dsize") is not null) { idx = k; src = s; break; }
        }
        if (src is null) { Emit($"  ({game}: no sizable deco in this sample)"); return; }

        double was = SceneProjector.FieldGet(src, "dsize")!.Value;
        double to = was * 1.25;
        bool wrote = SceneProjector.FieldSet(src, "dsize", to);
        byte[] edited = doc.Encode();
        double? got = SceneProjector.FieldGet(NthDecoAny(doc.Profile.Decode(edited), idx), "dsize");
        bool persists = got is double g && Math.Abs(g - to) < 0.02;
        int diff = DiffBytes(orig, edited);
        bool stable = StableReEncode(doc, edited);
        SceneProjector.FieldSet(src, "dsize", was);
        bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
        Check(wrote && persists && diff is > 0 and <= 8 && stable && revert,
            $"{game} deco uniform: x1.25 bytes={diff} persists={got} stable={stable} revert={revert}");
    }

    private static void EntityMassProof(string game, string level, Action<string> Emit, Action<bool, string> Check)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        int idx = -1; object? src = null;
        for (int k = 0; ; k++)
        {
            var s = NthEntAny(doc.Model, k);
            if (s is null) break;
            if (SceneProjector.FieldGet(s, "mass") is not null) { idx = k; src = s; break; }
        }
        if (src is null) { Emit($"  ({game}: no resizable entity in this sample)"); return; }

        double was = SceneProjector.FieldGet(src, "mass")!.Value;
        double to = was + 2.5;
        bool wrote = SceneProjector.FieldSet(src, "mass", to);
        byte[] edited = doc.Encode();
        double? got = SceneProjector.FieldGet(NthEntAny(doc.Profile.Decode(edited), idx), "mass");
        bool persists = got is double g && Math.Abs(g - to) < 0.02;
        int diff = DiffBytes(orig, edited);
        bool stable = StableReEncode(doc, edited);
        SceneProjector.FieldSet(src, "mass", was);
        bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
        Check(wrote && persists && diff is > 0 and <= 8 && stable && revert,
            $"{game} entity via mass: +2.5 bytes={diff} persists={got} stable={stable} revert={revert}");
    }

    // ----------------------------------------------------------- free axes --

    // The Shift-drag grip: both dimension fields move independently. This is exposed only because
    // every decoration record genuinely stores the pair; the write is still two fixed ints.
    private static void FreeAxesProof(string game, string level, Action<string> Emit, Action<bool, string> Check)
    {
        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        int idx = -1; object? src = null;
        for (int k = 0; ; k++)
        {
            var s = NthDecoAny(doc.Model, k);
            if (s is null) break;
            if (SceneProjector.FieldDecoAxes(s) is not null) { idx = k; src = s; break; }
        }
        if (src is null) { Emit($"  ({game}: no two-field deco size in this sample)"); return; }
        if (!SceneProjector.SupportsFreeAxes(src))
        {
            Check(false, $"{game}: facade hides free axes from a record that stores both fields");
            return;
        }

        var (w0, h0) = SceneProjector.FieldDecoAxes(src)!.Value;
        bool wrote = SceneProjector.FieldSetDecoAxes(src, w0 * 1.2, h0 * 0.85);
        byte[] edited = doc.Encode();
        var got = SceneProjector.FieldDecoAxes(NthDecoAny(doc.Profile.Decode(edited), idx));
        bool persists = got is (double gw, double gh)
            && Math.Abs(gw - w0 * 1.2) < 0.02 && Math.Abs(gh - h0 * 0.85) < 0.02
            && Math.Abs(gw - gh) > 0.01;   // the axes really moved apart (aspect changed)
        int diff = DiffBytes(orig, edited);
        bool stable = StableReEncode(doc, edited);
        SceneProjector.FieldSetDecoAxes(src, w0, h0);
        bool revert = doc.Encode().AsSpan().SequenceEqual(orig);
        Check(wrote && persists && diff is > 0 and <= 8 && stable && revert,
            $"{game} deco free axes: w x1.2 h x0.85 bytes={diff} persists=({got?.W.ToString("0.###")},{got?.H.ToString("0.###")}) stable={stable} revert={revert}");
    }

    // ------------------------------------------------------ vertex removal --

    // The right-click on a vertex handle. This write shortens the record by one corner's
    // coordinates (16 bytes), so byte-exact revert is not the assertion — I1 is proved by the
    // untouched encode, and I3 by the whole change sitting inside the record's own span (where
    // the format carries one) plus a fresh read that agrees the shape lost exactly that corner
    // and re-encodes stable.
    private static void VertexRemoveProof(string game, string dir, Action<string> Emit, Action<bool, string> Check)
    {
        string? level = null; int polyIdx = -1; int n0 = 0;
        foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f).Take(60))
        {
            try
            {
                var d = LevelLoader.Load(lvl);
                for (int i = 0; i < d.Scene.Polys.Count; i++)
                {
                    var p = d.Scene.Polys[i];
                    if (p.Source is TbWater || !SceneProjector.IsEditablePoly(p.Source, p.ShapeIndex)) continue;
                    int cnt = SceneProjector.PolyWorldPoints(p.Source, p.ShapeIndex).Count;
                    if (cnt > 3) { level = lvl; polyIdx = i; n0 = cnt; break; }
                }
            }
            catch { }
            if (level is not null) break;
        }
        if (level is null) { Emit($"  ({game}: no free-polygon with more than three corners in the sampled files)"); return; }

        var doc = LevelLoader.Load(level);
        byte[] orig = doc.Original;
        Check(doc.Encode().AsSpan().SequenceEqual(orig), $"{game}: sample {Path.GetFileName(level)} loads byte-identical (I1)");

        var poly = doc.Scene.Polys[polyIdx];
        var pts0 = SceneProjector.PolyWorldPoints(poly.Source, poly.ShapeIndex);
        var span = SceneProjector.RecordSpan(poly.Source);
        bool removed = SceneProjector.TryRemovePolyVertex(poly.Source, poly.ShapeIndex, pts0[0], 1e9, out int at);
        byte[] edited = doc.Encode();
        var rep = SaveReport.Compare(orig, edited);
        bool shrank = edited.Length == orig.Length - 16;
        // I3 for a length-changing edit reads differently than for a fixed-width one: nothing may
        // change before the record, and everything AFTER it must survive byte-for-byte exactly 16
        // bytes earlier (a clean shift, no other record rewritten). Where the format carries no
        // file offset the fresh-read agreement below carries the check instead.
        bool localised;
        string spanS;
        if (span.Off >= 0)
        {
            int recEnd = (int)(span.Off + span.Len);
            localised = recEnd <= orig.Length
                && edited.AsSpan(0, (int)span.Off).SequenceEqual(orig.AsSpan(0, (int)span.Off))
                && edited.AsSpan(recEnd - 16).SequenceEqual(orig.AsSpan(recEnd));
            spanS = localised ? "shift clean" : "NEIGHBOURS CHANGED";
        }
        else { localised = true; spanS = "span n/a"; }
        var back = SceneProjector.Project(doc.Profile.Decode(edited)).Polys[polyIdx];
        var pts1 = SceneProjector.PolyWorldPoints(back.Source, back.ShapeIndex);
        bool oneLess = pts1.Count == n0 - 1;
        bool stable = StableReEncode(doc, edited);
        Check(removed && at == 0 && shrank && localised && oneLess && stable,
            $"{game}: vertex removed ({n0}->{pts1.Count}) at {at}, {rep.BytesChanged}b @{rep.FirstDiff}, size {(shrank ? "-16 ok" : "WRONG")}, " +
            $"{spanS}, fresh read agrees={oneLess}, stable={stable}");

        // A triangle refuses: removing any corner would leave a line, which the games cannot draw.
        var triDoc = LevelLoader.Load(level);
        var tri = triDoc.Scene.Polys.FirstOrDefault(pp => pp.Source is not TbWater
            && SceneProjector.IsEditablePoly(pp.Source, pp.ShapeIndex)
            && SceneProjector.PolyWorldPoints(pp.Source, pp.ShapeIndex).Count == 3);
        if (tri is not null)
        {
            var tp = SceneProjector.PolyWorldPoints(tri.Source, tri.ShapeIndex);
            bool refused = !SceneProjector.TryRemovePolyVertex(tri.Source, tri.ShapeIndex, tp[0], 1e9, out _);
            Check(refused && triDoc.Encode().AsSpan().SequenceEqual(triDoc.Original),
                $"{game}: triangle keeps its three corners (refused, bytes untouched)");
        }
    }

    // A water box is an axis-aligned rectangle, not a free polygon: the same call the gesture
    // makes must refuse it outright and leave every byte alone.
    private static void WaterRefusalProof(string game, string dir, Action<string> Emit, Action<bool, string> Check)
    {
        foreach (var lvl in Directory.GetFiles(dir, "*.bin").OrderBy(f => f).Take(60))
        {
            LevelDoc doc;
            try { doc = LevelLoader.Load(lvl); } catch { continue; }
            var wtPoly = doc.Scene.Polys.FirstOrDefault(p => p.Source is TbWater);
            if (wtPoly?.Source is not TbWater wt) continue;
            var pts = SceneProjector.PolyWorldPoints(wt, wtPoly.ShapeIndex);
            bool refused = !SceneProjector.TryRemovePolyVertex(wt, wtPoly.ShapeIndex, pts[0], 1e9, out _);
            Check(refused && doc.Encode().AsSpan().SequenceEqual(doc.Original),
                $"{game}: water box refuses corner removal ({Path.GetFileName(lvl)}, bytes untouched)");
            return;
        }
        Emit($"  ({game}: no water box in the sampled files)");
    }
}
