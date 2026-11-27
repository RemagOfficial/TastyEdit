using System.IO;
using TastyEditor.Core.Assets;
using TastyEditor.Core.Levels;

namespace TastyEditor.App.Viewport;

// R7 gate: the verbatim / span-preserving contract over the two shared Tasty Planet 1 asset tables
// the entity panel writes. Nothing here is saved to the game: each check loads a table, mutates it in
// memory and re-encodes, proving (I1) an untouched table is byte-identical, a created + removed element
// restores the original bytes exactly (a clean splice), an edit re-reads stable, and a shipped def's
// values and collision shapes parse back. Developer log output, so it names the files it reads.

public static partial class Smoke
{
    public static void Tp1TablesCheck()
    {
        using var log = new StreamWriter("_tp1tablescheck.txt", append: false) { AutoFlush = true };
        void Emit(string line) { Console.WriteLine(line); log.WriteLine(line); }
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; } else { fail++; Emit("  FAIL " + what); } }

        var root = Tp1GraphicsRoot.ForInstall();
        string? ent = root?.FindTable(Tp1Tables.EntityDefsFile);
        string? map = root?.FindTable(Tp1Tables.ImageMapsFile);
        string? str = root?.FindTable(Tp1Tables.StringsFile);
        if (ent is null)
        { Emit("SKIP tp1tables: " + GameInstall.MissingHint("tp1")); Emit("TP1 TABLES SKIPPED"); return; }
        Emit("entity table: " + ent);
        if (map is not null) Emit("image table:  " + map);

        // ------------------------------------------------------------ entity table --
        var data = File.ReadAllBytes(ent);
        var doc = Tp1Tables.Load(ent);
        Check(doc.Encode().AsSpan().SequenceEqual(data), "entity: I1 not byte-identical");
        Check(Tp1Codec.ParseTree(doc.Encode()).Encode().AsSpan().SequenceEqual(data),
            "entity: re-read not stable");

        var names = new List<string>();
        foreach (var n in doc.Root.Kids("entitydef"))
            if (!string.IsNullOrEmpty(n.Get("name"))) names.Add(n.Get("name")!);
        Check(names.Count > 0, "entity: no defs found to exercise");

        // Add a brand-new def (with a collision shape), prove the count grows and it re-reads stable,
        // then remove it and prove the table is byte-identical to the original again.
        string uniq = "zeditprobe_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var added = new Tp1EntityDefEdit { Name = uniq, ImageMap = "none", AspectRatio = 1, AreaCoverage = 1 };
        added.EffectR = 0.5; added.EffectG = 0.25; added.EffectB = 0.75; added.EffectA = 1;
        added.Shapes.Add(new Tp1Circle(0, 0, 0.5));
        added.Shapes.Add(new Tp1Rect(0.1, -0.2, 0.3, 0.4, 0));

        int before = doc.Root.Kids("entitydef").Count();
        bool ins = Tp1Tables.UpsertEntityDef(doc, added);
        var afterAdd = doc.Encode();
        Check(ins, "entity: Upsert(new) returned false");
        Check(doc.Root.Kids("entitydef").Count() == before + 1, "entity: add did not grow the def count by one");
        Check(Tp1Codec.ParseTree(afterAdd).Encode().AsSpan().SequenceEqual(afterAdd),
            "entity: after-add not re-read stable");

        // Read the new def back out and confirm the values + both shapes survived the change.
        var back = Tp1Tables.ReadEntityDef(doc, uniq);
        Check(back is not null, "entity: added def not readable back");
        Check(back is { AspectRatio: 1 } && back.Shapes.Count == 2,
            "entity: added def read-back mismatch");
        Check(back!.EffectR is double br && Math.Abs(br - 0.5) < 1e-6, "entity: added tint not read back");

        Check(Tp1Tables.RemoveEntityDef(doc, uniq), "entity: Remove(new) returned false");
        Check(doc.Encode().AsSpan().SequenceEqual(data),
            "entity: add + remove did not restore the original bytes");

        // Editing a shipped def's scalar attributes is confined + stable (no byte-revert claim: a def
        // with a legacy-formatted collision block legitimately reflows when its shapes are rewritten).
        if (names.Count > 0)
        {
            var tdoc = Tp1Tables.Load(ent);
            string real = names[0];
            var orig = Tp1Tables.ReadEntityDef(tdoc, real);
            Check(orig is not null, "entity: read of a shipped def returned null");
            if (orig is not null)
            {
                orig.AreaCoverage += 0.5;
                Check(Tp1Tables.UpsertEntityDef(tdoc, orig), "entity: edit of a shipped def returned false");
                var edited = tdoc.Encode();
                Check(Tp1Codec.ParseTree(edited).Encode().AsSpan().SequenceEqual(edited),
                    "entity: shipped-def edit not re-read stable");
                Check(tdoc.Root.Kids("entitydef").Count() == names.Count,
                    "entity: shipped-def edit changed the def count");
                var reread = Tp1Tables.ReadEntityDef(tdoc, real);
                Check(reread is not null && Math.Abs(reread.AreaCoverage - orig.AreaCoverage) < 1e-6,
                    "entity: shipped-def edit did not persist");
            }
        }

        // ------------------------------------------------------------- def tint --
        // The viewport paints a TP1 entity in its entitydef's effectr/g/b/a (an emitter with no
        // per-record colour otherwise renders its raw white art). The renderer reads that through
        // Resolver.EntityTint, so prove the def->tint table is populated: greygoo must resolve to its
        // shipped 0.5 gray (≈128), and at least one shipped def must carry a non-null (non-white) tint.
        var gres = Tp1SpriteResolver.TryLoad(root);
        if (gres is not null)
        {
            var goo = gres.EntityTint("greygoo");
            Check(goo is { Length: >= 3 } && goo[0] is >= 100 and <= 160
                  && Math.Abs(goo[0] - goo[1]) < 8 && Math.Abs(goo[1] - goo[2]) < 8,
                "tint: greygoo did not resolve to its declared gray");
            int tintedDefs = 0;
            foreach (var dn in names) if (gres.EntityTint(dn) is not null) tintedDefs++;
            Check(tintedDefs > 0, "tint: no shipped entitydef resolved a non-white effect tint");
            Emit($"  def-tint: greygoo=[{(goo is null ? "-" : string.Join(",", goo))}] tintedDefs={tintedDefs}/{names.Count}");
        }
        else Emit("  note: sprite resolver not loadable; def-tint checks skipped");

        // -------------------------------------------------------------- image table --
        if (map is not null)
        {
            var mdata = File.ReadAllBytes(map);
            var mdoc = Tp1Tables.Load(map);
            Check(mdoc.Encode().AsSpan().SequenceEqual(mdata), "image: I1 not byte-identical");
            string muniq = "zeditmap_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            int mbefore = mdoc.Root.Kids("imagemap").Count();
            Check(Tp1Tables.UpsertImageMap(mdoc, new Tp1ImageMapEdit
            { Name = muniq, Filename = "none", Width = 8, Height = 8, FullImage = true }),
                "image: Upsert(new) returned false");
            Check(mdoc.Root.Kids("imagemap").Count() == mbefore + 1, "image: add did not grow the map count");
            var afterMap = mdoc.Encode();
            Check(Tp1Codec.ParseTree(afterMap).Encode().AsSpan().SequenceEqual(afterMap),
                "image: after-add not re-read stable");
            Check(Tp1Tables.RemoveImageMap(mdoc, muniq) && mdoc.Encode().AsSpan().SequenceEqual(mdata),
                "image: add + remove did not restore the original bytes");
        }
        else Emit("  note: image table not found; image-table checks skipped");

        // ------------------------------------------------------------- string table --
        // The friendly-name table (an Excel-spreadsheet XML export) is edited by the same verbatim
        // contract: an untouched copy re-encodes byte-for-byte, a created row reads back and keeps the
        // file re-read stable, and removing it restores the original bytes exactly.
        if (str is not null)
        {
            var sdata = File.ReadAllBytes(str);
            var sdoc = Tp1Tables.Load(str);
            Check(sdoc.Encode().AsSpan().SequenceEqual(sdata), "string: I1 not byte-identical");
            Check(Tp1Codec.ParseTree(sdoc.Encode()).Encode().AsSpan().SequenceEqual(sdata),
                "string: not re-read stable");

            // A shipped key should read back a non-empty display (proves the row locator + reader).
            var sdoc2 = Tp1Tables.Load(str);
            string? amoeba = Tp1Tables.ReadString(sdoc2, "amoeba1");
            Check(!string.IsNullOrEmpty(amoeba), "string: shipped key 'amoeba1' did not read back");

            string suniq = "zeditstr_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Check(Tp1Tables.UpsertString(sdoc, suniq, "Probe Display"), "string: Upsert(new) returned false");
            var afterStr = sdoc.Encode();
            Check(Tp1Codec.ParseTree(afterStr).Encode().AsSpan().SequenceEqual(afterStr),
                "string: after-add not re-read stable");
            Check(Tp1Tables.ReadString(sdoc, suniq) == "Probe Display",
                "string: added row not readable back");
            // Re-writing the same value is a no-op (returns false, bytes unchanged).
            Check(!Tp1Tables.UpsertString(sdoc, suniq, "Probe Display"),
                "string: re-upsert of same display reported a change");
            Check(Tp1Tables.RemoveString(sdoc, suniq) && sdoc.Encode().AsSpan().SequenceEqual(sdata),
                "string: add + remove did not restore the original bytes");
        }
        else Emit("  note: string table not found; string-table checks skipped");

        Emit("");
        Emit($"TP1 TABLES: {pass} pass, {fail} fail");
        Emit(fail == 0 ? "TP1 TABLES GREEN" : "TP1 TABLES FAILURES");
    }
}
