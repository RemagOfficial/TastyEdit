using System.IO;
using TastyEditor.Core.Levels;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Viewport;

// R5 gate: the Tasty Planet 1 contract. The tp1 codec is verbatim / span-preserving, so
// every shipped XML must re-encode byte-for-byte (I1), every edit must confine its change to
// the touched element (I3), and reverting must restore the exact original bytes. Detection is
// checked too: all real levels accepted, medaltimes.xml and a <multilevel> shell rejected.

public static partial class Smoke
{
    public static void Tp1Check()
    {
        using var log = new StreamWriter("_tp1check.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; } else { fail++; Emit("  FAIL " + what); } }

        var dir = GameInstall.LevelsDir("tp1");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        { Emit("SKIP tp1: " + GameInstall.MissingHint("tp1")); Emit("TP1 CHECK SKIPPED"); return; }

        var codec = Tp1Codec.Instance;
        var all = Directory.GetFiles(dir, "*.xml").OrderBy(f => f).ToArray();
        var levels = all.Where(f => !string.Equals(Path.GetFileName(f), "medaltimes.xml",
                                                   StringComparison.OrdinalIgnoreCase)).ToArray();
        Emit($"tp1 levels dir: {dir}  ({levels.Length} levels, {all.Length} xml total)");

        // ---- I1 / stability / detection over every real level ----
        foreach (var path in levels)
        {
            var name = Path.GetFileName(path);
            try
            {
                var data = File.ReadAllBytes(path);
                var lv = codec.Decode(data);
                var re = lv.Encode();
                Check(re.AsSpan().SequenceEqual(data), $"{name}: I1 not byte-identical");
                var re2 = codec.Decode(re).Encode();
                Check(re2.AsSpan().SequenceEqual(data), $"{name}: re-decode not stable");
                var (prof, _) = GameRegistry.DetectAndDecode(path, data, out _);
                Check(prof.Key == "tp1", $"{name}: detected as {prof.Key}, not tp1");
            }
            catch (Exception ex) { fail++; Emit($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---- detection rejections ----
        var medal = Path.Combine(dir, "medaltimes.xml");
        if (File.Exists(medal))
            Check(Throws(codec, File.ReadAllBytes(medal)), "medaltimes.xml accepted by tp1 codec");
        // A campaign multilevel shell must not read as a tp1 level either.
        foreach (var ml in FindMultilevel(dir))
            Check(Throws(codec, File.ReadAllBytes(ml)), $"{Path.GetFileName(ml)}: multilevel read as tp1");

        // ---- edit classes over every level that supports them ----
        bool gooDone = false, emitDone = false, setDone = false, affectDone = false,
             pairDone = false, tileDone = false, newDone = false, layerDone = false, rotDone = false;
        foreach (var path in levels)
        {
            var name = Path.GetFileName(path);
            byte[] data;
            try { data = File.ReadAllBytes(path); if (codec.Decode(data) is null) continue; }
            catch (Exception ex) { fail++; Emit($"  FAIL {name}: reload {ex.Message}"); continue; }

            // Run every edit class on every level that supports it (not just the first), so the
            // whole corpus exercises the I3 / revert contract.
            var lv0 = codec.Decode(data);
            if (lv0.Goo is not null) { gooDone = true; EditMoveGoo(name, data, codec, Emit, Check); }
            if (FirstPlaceable(lv0) is not null)
            {
                emitDone = true; EditMoveEmitter(name, data, codec, Emit, Check);
                setDone = true; EditSetAttr(name, data, codec, Emit, Check);
            }
            if (FirstControllerWithAffect(lv0) is not null) { affectDone = true; EditAffect(name, data, codec, Emit, Check); }
            if (FirstPathfollow(lv0) is not null) { pairDone = true; EditPair(name, data, codec, Emit, Check); }
            if (FirstTile(lv0) is not null) { tileDone = true; EditTile(name, data, codec, Emit, Check); }
            if (lv0.Tilelayers is not null && lv0.TileLayerCount >= 1)
            { layerDone = true; EditTileLayer(name, data, codec, Emit, Check); }
            if (lv0.Emitters.Any()) { newDone = true; EditNewEmitter(name, data, codec, Emit, Check); }
            // R7: a rotatable object must project already turned, so the first render matches the
            // inspector's read of the record (an emitter's <angle> / a tile's <angle>) with no manual set.
            if (HasAngle(lv0)) { rotDone = true; CheckRotationLoad(name, data, codec, Emit, Check); }
        }

        Emit("");
        Emit($"TP1 CHECK: {pass} pass, {fail} fail (edits: goo{gooDone} emit{emitDone} set{setDone} " +
             $"affect{affectDone} pair{pairDone} tile{tileDone} new{newDone} layer{layerDone} rot{rotDone})");
        bool classesRan = gooDone && emitDone && setDone && affectDone && pairDone && tileDone && newDone && layerDone;
        Emit(fail == 0 && classesRan ? "TP1 CHECK GREEN" : "TP1 CHECK FAILURES");
    }

    // ------------------------------------------------- edit-class helpers ---

    private static void EditMoveGoo(string lvl, byte[] data, Tp1Codec codec,
                                    Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var g = lv.Goo!;
        int s = g.Start, e = g.End;
        if (!Tp1Edit.MoveGoo(lv, 50, -30)) { check(false, $"{lvl}: move goo did nothing"); return; }
        var outB = lv.Encode();
        check(Localised(data, outB, s, e), $"{lvl}: goo move not localised");
        check(Stable(codec, outB), $"{lvl}: goo move not re-decode stable");
        Tp1Edit.MoveGoo(lv, -50, 30);   // MoveGoo re-reads lv.Goo itself
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: goo move revert not exact");
    }

    // ---- rotation-at-load (R7 render parity) ----

    // Any record the viewport can turn? A tile or an emitter that carries an <angle> attribute.
    private static bool HasAngle(Tp1Level lv)
    {
        if (lv.Emitters.Any(e => e.Has("angle"))) return true;
        if (lv.Tilelayers is { } tl)
            foreach (var layer in tl.Kids("tilelayer"))
                foreach (var tile in layer.Kids("tile"))
                    if (tile.Has("angle")) return true;
        return false;
    }

    // Every rotatable cross must already be turned when the level projects, so the first render matches
    // what the inspector reads from the record — the bug this guards is an object loading unrotated
    // (RotationDeg 0) while its <angle> is present, only turning after a manual inspector set.
    private static void CheckRotationLoad(string lvl, byte[] data, Tp1Codec codec,
                                         Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var g = SceneProjector.Project(lv);
        int rot = 0;
        foreach (var cr in g.Crosses)
        {
            if (cr.Source is null) continue;
            if (SceneProjector.GetRotationDeg(cr.Source) is not double src) continue;   // not rotatable
            rot++;
            check(Math.Abs(cr.RotationDeg - src) < 1e-6,
                $"{lvl}: rotation loaded as {cr.RotationDeg:0.##} but record reads {src:0.##} (not applied on load)");
        }
        if (rot > 0) emit($"  rotation-at-load x{rot} @ {lvl}");
    }

    private static void EditMoveEmitter(string lvl, byte[] data, Tp1Codec codec,
                                        Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var e = FirstPlaceable(lv)!;
        int[] path = Tp1Level.PathOf(e);
        int s = e.Start, en = e.End;
        if (!Tp1Edit.MoveEmitter(e, 40, 20)) { check(false, $"{lvl}: move emitter did nothing"); return; }
        var outB = lv.Encode();
        check(Localised(data, outB, s, en), $"{lvl}: emitter move not localised");
        check(Stable(codec, outB), $"{lvl}: emitter move not stable");
        Tp1Edit.MoveEmitter(lv.NodeAt(path), -40, -20);
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: emitter move revert not exact");
    }

    private static void EditSetAttr(string lvl, byte[] data, Tp1Codec codec,
                                    Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var e = FirstPlaceable(lv)!;
        var a = e.Attrs.FirstOrDefault(x => e.Num(x.Key) is not null);
        if (a is null) { check(false, $"{lvl}: emitter has no numeric field to set"); return; }
        string key = a.Key, orig = a.Value;
        int[] path = Tp1Level.PathOf(e);
        int s = e.Start, en = e.End;
        double nv = (e.Num(key) ?? 0) + 1.5;
        Tp1Edit.SetNum(e, key, nv);
        var outB = lv.Encode();
        emit($"  set {key} @ {lvl}");
        check(!outB.AsSpan().SequenceEqual(data), $"{lvl}: set {key} changed nothing");
        check(Localised(data, outB, s, en), $"{lvl}: set {key} not localised");
        check(Stable(codec, outB), $"{lvl}: set {key} not stable");
        Tp1Edit.SetAttr(lv.NodeAt(path)!, key, orig);
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: set {key} revert not exact");
    }

    private static void EditAffect(string lvl, byte[] data, Tp1Codec codec,
                                   Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var ctrl = FirstControllerWithAffect(lv)!;
        int s = ctrl.Start, en = ctrl.End;
        var added = Tp1Edit.AddAffect(ctrl);   // valid handle: no edit runs between add and remove
        check(added is not null, $"{lvl}: add affect returned null");
        var afterAdd = lv.Encode();
        emit($"  add/remove affect @ {lvl}");
        check(Localised(data, afterAdd, s, en), $"{lvl}: add affect not localised");
        check(Stable(codec, afterAdd), $"{lvl}: add affect not stable");
        check(added is not null && Tp1Edit.Remove(added), $"{lvl}: remove affect failed");
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: affect add/remove revert not exact");
    }

    private static void EditPair(string lvl, byte[] data, Tp1Codec codec,
                                 Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var ctrl = FirstPathfollow(lv)!;
        int[] path = Tp1Level.PathOf(ctrl);
        int s = ctrl.Start, en = ctrl.End;

        // Drag (move the whole path) then revert — a value-only edit, so the path is stable.
        if (!Tp1Edit.MovePath(ctrl, 25, 25)) { check(false, $"{lvl}: path drag did nothing"); return; }
        var dragged = lv.Encode();
        emit($"  path drag @ {lvl}");
        check(Localised(data, dragged, s, en), $"{lvl}: path drag not localised");
        check(Stable(codec, dragged), $"{lvl}: path drag not stable");
        Tp1Edit.MovePath(lv.NodeAt(path), -25, -25);
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: path drag revert not exact");

        // Add a pair then remove the very node that was added.
        var c2 = FirstPathfollow(lv)!;
        var added = Tp1Edit.AddPair(c2);
        check(added is not null, $"{lvl}: add pair returned null");
        var afterAdd = lv.Encode();
        emit($"  add/remove pair @ {lvl}");
        check(Localised(data, afterAdd, c2.Start, c2.End), $"{lvl}: add pair not localised");
        check(Stable(codec, afterAdd), $"{lvl}: add pair not stable");
        check(added is not null && Tp1Edit.Remove(added), $"{lvl}: remove pair failed");
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: pair add/remove revert not exact");
    }

    private static void EditTile(string lvl, byte[] data, Tp1Codec codec,
                                 Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var t = FirstTile(lv)!;
        var a = t.Attr("name");
        if (a is null) { check(false, $"{lvl}: tile has no name"); return; }
        string orig = a.Value;
        string setTo = orig == "null" ? "microback" : "null";
        int[] path = Tp1Level.PathOf(t);
        int s = t.Start, en = t.End;
        Tp1Edit.PaintTile(t, setTo);
        var outB = lv.Encode();
        emit($"  paint tile @ {lvl}");
        check(!outB.AsSpan().SequenceEqual(data), $"{lvl}: paint changed nothing");
        check(Localised(data, outB, s, en), $"{lvl}: paint not localised");
        check(Stable(codec, outB), $"{lvl}: paint not stable");
        Tp1Edit.PaintTile(lv.NodeAt(path)!, orig);
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: paint revert not exact");
    }

    // Structural tile-layer proofs: add then remove a layer (revert exact), the delete guard refusing
    // to drop the last layer, and a 2D resize that grows then shrinks back byte-for-byte.
    private static void EditTileLayer(string lvl, byte[] data, Tp1Codec codec,
                                      Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var container = lv.Tilelayers;
        if (container is null || lv.TileLayerCount < 1) { check(false, $"{lvl}: no tilelayers to test"); return; }
        int cs = container.Start, ce = container.End;

        // ---- add a layer then remove the one just added: change confined to <tilelayers>, revert exact ----
        var added = Tp1Edit.AddTileLayer(lv);
        check(added is not null, $"{lvl}: add layer returned null");
        var afterAdd = lv.Encode();
        emit($"  add/remove layer @ {lvl}");
        check(!afterAdd.AsSpan().SequenceEqual(data), $"{lvl}: add layer changed nothing");
        check(Localised(data, afterAdd, cs, ce), $"{lvl}: add layer not localised to <tilelayers>");
        check(Stable(codec, afterAdd), $"{lvl}: add layer not stable");
        var addedLive = lv.Tilelayers!.Kids("tilelayer").LastOrDefault();
        check(addedLive is not null && Tp1Edit.RemoveTileLayer(lv, addedLive), $"{lvl}: remove added layer failed");
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: add/remove layer revert not exact");

        // ---- delete guard: drive a fresh decode down to one layer, then the delete must refuse ----
        var lvG = codec.Decode(data);
        while (lvG.TileLayerCount > 1)
            Tp1Edit.RemoveTileLayer(lvG, lvG.Tilelayers!.Kids("tilelayer").Last());
        var only = lvG.Tilelayers!.Kids("tilelayer").First();
        bool refused = !Tp1Edit.RemoveTileLayer(lvG, only) && lvG.TileLayerCount == 1;
        check(refused, $"{lvl}: delete guard let the last layer go");

        // ---- resize grow (w+2,h+2) localised + stable, then resize back to (w,h) byte-exact ----
        var lv2 = codec.Decode(data);
        var layer = lv2.Tilelayers!.Kids("tilelayer").First();
        int w = layer.Int("tileswide") ?? 0, h = layer.Int("tileshigh") ?? 0;
        if (w < 1 || h < 1) { check(false, $"{lvl}: tile layer has no grid size"); return; }
        int lp = layer.Start, le = layer.End;
        int[] lpath = Tp1Level.PathOf(layer);
        check(Tp1Edit.ResizeTileLayer(lv2, layer, w + 2, h + 2), $"{lvl}: resize grow failed");
        var grown = lv2.Encode();
        emit($"  resize layer @ {lvl}");
        check(!grown.AsSpan().SequenceEqual(data), $"{lvl}: resize grow changed nothing");
        check(Localised(data, grown, lp, le), $"{lvl}: resize grow not localised to the layer");
        check(Stable(codec, grown), $"{lvl}: resize grow not stable");
        var back = lv2.NodeAt(lpath);   // the layer's sibling index is unchanged by a resize
        check(back is not null && Tp1Edit.ResizeTileLayer(lv2, back, w, h), $"{lvl}: resize shrink-back failed");
        check(lv2.Encode().AsSpan().SequenceEqual(data), $"{lvl}: resize grow/shrink-back revert not exact");
    }

    private static void EditNewEmitter(string lvl, byte[] data, Tp1Codec codec,
                                       Action<string> emit, Action<bool, string> check)
    {
        var lv = codec.Decode(data);
        var proto = lv.Emitters.LastOrDefault();
        if (proto is null) { check(false, $"{lvl}: no emitter to clone"); return; }
        string type = proto.Get("type") ?? "side";
        string def = proto.Get("entitydef") ?? "";
        int at = proto.End;   // the clone is spliced in right after the last emitter
        var created = Tp1Edit.NewEmitter(lv, type, def, 12, 34);
        emit($"  new emitter @ {lvl}");
        check(created is not null, $"{lvl}: New emitter returned null");
        var afterNew = lv.Encode();
        check(!afterNew.AsSpan().SequenceEqual(data), $"{lvl}: New emitter changed nothing");
        check(Localised(data, afterNew, at, data.Length), $"{lvl}: New emitter touched earlier bytes");
        check(Stable(codec, afterNew), $"{lvl}: New emitter not stable");
        check(created is not null && Tp1Edit.Remove(created), $"{lvl}: delete new emitter failed");
        check(lv.Encode().AsSpan().SequenceEqual(data), $"{lvl}: New emitter/delete revert not exact");
    }

    // ----------------------------------------------------- small utilities --

    private static bool Throws(Tp1Codec codec, byte[] data)
    { try { codec.Decode(data); return false; } catch (Exception) { return true; } }

    private static bool Stable(Tp1Codec codec, byte[] bytes)
    { try { return codec.Decode(bytes).Encode().AsSpan().SequenceEqual(bytes); } catch (Exception) { return false; } }

    // An element edit must leave the bytes before its original span and the bytes after it
    // (shifted by the net length delta) untouched — everything that moved maps into [s,e].
    private static bool Localised(byte[] a, byte[] b, int s, int e)
    {
        int delta = b.Length - a.Length;
        if (s < 0 || e > a.Length || s > e) return false;
        for (int i = 0; i < s; i++) if (a[i] != b[i]) return false;
        for (int i = e; i < a.Length; i++) if (a[i] != b[i + delta]) return false;
        return true;
    }

    private static Tp1Node? FirstPlaceable(Tp1Level lv) =>
        lv.Emitters.FirstOrDefault(e =>
            (e.Get("type") == "spot" || e.Get("type") == "shot") && e.Has("posx"));

    private static Tp1Node? FirstControllerWithAffect(Tp1Level lv)
    {
        // Only a controller that already carries an <affect> gives a same-file sibling to clone,
        // which is what the add+remove contract exercises (a clean splice round-trip). Controllers
        // without one aren't a template case, so the gate skips them rather than proving the
        // self-closing-expansion path (that stays an editor-only behaviour, not a byte round-trip).
        foreach (var e in lv.Emitters)
            foreach (var c in e.Children)
                if (c.Tag == "controller" && c.Children.Any(a => a.Tag == "affect")) return c;
        return null;
    }

    private static Tp1Node? FirstPathfollow(Tp1Level lv)
    {
        foreach (var e in lv.Emitters)
            foreach (var c in e.Children)
                if (c.Tag == "controller" && c.Get("type") == "pathfollow" &&
                    c.Children.Any(p => p.Tag == "pair")) return c;
        return null;
    }

    private static Tp1Node? FirstTile(Tp1Level lv)
    {
        var layers = lv.Root.Child("tilelayers");
        if (layers is null) return null;
        foreach (var layer in layers.Children)
            foreach (var tile in layer.Children)
                if (tile.Tag == "tile" && tile.Has("name")) return tile;
        return null;
    }

    // Any campaign multilevel (root <multilevel>) reachable under the install, if present.
    private static IEnumerable<string> FindMultilevel(string levelsDir)
    {
        var ml = Path.Combine(Path.GetDirectoryName(levelsDir) ?? levelsDir, "multilevels");
        if (!Directory.Exists(ml)) ml = Path.Combine(levelsDir, "multilevels");
        if (!Directory.Exists(ml)) return Array.Empty<string>();
        return Directory.GetFiles(ml, "*.xml").Take(1);
    }
}
