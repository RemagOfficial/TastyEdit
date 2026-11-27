using TastyEditor.Core.Render;

namespace TastyEditor.Core.Levels;

/// <summary>Outcome of a scene edit operation so the shell can report it precisely.
/// <see cref="Done"/> mutated the model in place; <see cref="NothingSelected"/> had no
/// target; <see cref="NotSupported"/> names a kind/family whose byte-faithful re-encode
/// path this build deliberately does not offer (capability-gated, model untouched);
/// <see cref="Failed"/> means a guarded edit was refused after a safety check (model
/// untouched). Only <see cref="Done"/> should mark the document dirty.</summary>
public enum EditResult { Done, NothingSelected, NotSupported, Failed }

/// <summary>A deep snapshot of one selected record, taken by <see cref="EditOps.Copy"/> and
/// replayed by <see cref="EditOps.Paste"/>. It is model-independent (owns a cloned typed record
/// or cloned raw bytes) so a copy survives re-projection and can be pasted into another layer of
/// the same family. Position re-derivation on paste re-anchors the copy against the target
/// section's chain, so no stale absolute leaks across a paste.</summary>
public sealed class EditClipboard
{
    internal string Family = "";         // tp2 / tb / f4 / f5
    public string Kind = "";             // wall / area / water / entity / deco
    public int Layer;                    // source layer index (clamped on paste)
    internal object? Payload;            // cloned typed record or cloned raw byte[]
    internal string? Name;               // resolved sprite name (deco materialise on paste)
    internal int SectionIndex = -1;      // element framed/entity section (index within a layer)
    internal double AbsX, AbsY;          // source absolute world position (chain anchor)
    internal bool HasAbs;
    internal int RecXyLocal = -1;        // f4/f5 entity: x/y int offset within the record span
}

/// <summary>Scene-graph object mutations — duplicate / delete / copy / paste — for every
/// supported level family, each routed through the exact re-encode contract that family's codec
/// offers (the same contract the sprite-reassignment edits in <see cref="TbEdit"/>,
/// <see cref="ElemEdit"/> and <see cref="Tp2Edit"/> lean on).
///
/// <para>The invariants are unchanged: an untouched level still round-trips byte-identically
/// (I1), an edit stays localised (I3), and here "localised" additionally means a raw-span
/// section's record <b>count</b> moves in lockstep with its record list, which the encoders
/// assume (<c>ElementSpanCodec.Encode</c> writes the stored <see cref="Section.Count"/>, not the
/// list length; the typed codecs write list lengths). The families differ in how a new record is
/// carried:</para>
///
/// <list type="bullet">
/// <item><description><b>TP2</b> is fully typed and its codec re-serialises every field,
/// including the delta-chained entity/deco positions, so a duplicate is a deep clone with its
/// absolute position nudged and, where the record owns an id, a fresh id. Delete is a list
/// removal.</description></item>
/// <item><description><b>Tasty Blue</b> re-serialises walls / areas / water from typed fields and
/// re-derives entity positions from their absolute chained value (<c>PatchEntityPositions</c>), so
/// those clone cleanly. Decorations are verbatim raw spans whose positions chain down the section,
/// so a duplicate is <em>appended</em> (leaving every earlier record — and its resolved position —
/// untouched) with its delta re-solved for the target, and a delete folds the removed record's
/// delta into its successor so the rest of the chain keeps its place.</description></item>
/// <item><description><b>Forever / Final Bite</b> framed sections hold raw record bytes re-emitted
/// verbatim; a wall/area position is an absolute double inside its record, so a duplicate is a byte
/// clone with the position spliced and the section count bumped. Decorations chain like Tasty Blue.
/// Entities live in ONE opaque block whose boundaries the framing oracle re-derives, so an entity
/// duplicate/delete is block surgery validated by a re-walk that must still split to the new count
/// — otherwise the block is left byte-for-byte untouched.</description></item>
/// </list>
///
/// <para>Kinds without a proven path (TP2 / Tasty Blue paths, which the reference editor also
/// does not copy; f4/f5 walls that fell back to a centre marker) return
/// <see cref="NotSupported"/> rather than guess.</para></summary>
public static class EditOps
{
    // A duplicate is nudged this many WORLD units so the copy is visibly offset from its
    // source (tp2editor.duplicate_selection / paste_clipboard use 40 scene units). Public so the
    // shell can report the offset in a status line.
    public const double DupOffset = 40.0;

    // A TP2 entity's stored position is 1/ENTITY_K of its world position; a deco (raw families)
    // stores centiunits (world * 100). Mirrors SceneProjector's projection scalars.
    private const double Tp2EntityK = 100.0;
    private const double Centi = 100.0;

    // ------------------------------------------------------------- public --

    public static EditResult Duplicate(object? model, object? source)
    {
        var clip = Copy(model, source);
        return clip is null ? EditResult.NothingSelected : Paste(model, clip);
    }

    public static EditResult Delete(object? model, object? source) => model switch
    {
        Tp2Level tp => Tp2Delete(tp, source),
        TbLevel tb => TbDelete(tb, source),
        ElementLevel el => ElemDelete(el, source),
        _ => EditResult.NotSupported,
    };

    public static EditClipboard? Copy(object? model, object? source) => model switch
    {
        Tp2Level tp => Tp2Copy(tp, source),
        TbLevel tb => TbCopy(tb, source),
        ElementLevel el => ElemCopy(el, source),
        _ => null,
    };

    public static EditResult Paste(object? model, EditClipboard? clip)
    {
        if (clip is null || model is null) return EditResult.NothingSelected;
        if (FamilyOf(model) != clip.Family) return EditResult.NotSupported;
        return clip.Family switch
        {
            "tp2" => Tp2Paste((Tp2Level)model, clip),
            "tb" => TbPaste((TbLevel)model, clip),
            "f4" or "f5" => ElemPaste((ElementLevel)model, clip),
            _ => EditResult.NotSupported,
        };
    }

    /// <summary>Paste a clipboard into an explicit layer (or the copy's own layer when
    /// <paramref name="targetLayer"/> is null). The position re-anchor still resolves against the
    /// clipboard's ORIGINAL chain, so the copy keeps its shape; only the owning layer changes.</summary>
    public static EditResult Paste(object? model, EditClipboard? clip, int? targetLayer)
    {
        if (clip is null || model is null) return EditResult.NothingSelected;
        if (targetLayer is int tl) clip.Layer = tl;
        return Paste(model, clip);
    }

    /// <summary>Relocate a selected record to another level: deep-copy it, paste the clone into
    /// <paramref name="targetLayer"/>, then delete the original. Both halves run through the same
    /// byte-faithful append / re-fold paths a duplicate and a delete already use, so a move keeps
    /// I1/I3 across every family (a chained deco/entity in the new layer re-anchors to that
    /// layer's tail, and removing the source folds its delta forward). Returns NotSupported for a
    /// kind the family cannot copy, or Failed if the paste landed but the delete did not.</summary>
    public static EditResult MoveToLayer(object? model, object? source, int targetLayer)
    {
        var clip = Copy(model, source);
        if (clip is null) return EditResult.NothingSelected;
        if (clip.Layer == targetLayer) return EditResult.Done;   // already there — nothing to do
        var pasted = Paste(model, clip, targetLayer);
        if (pasted != EditResult.Done) return pasted;
        return Delete(model, source);
    }

    private static string FamilyOf(object model) => model switch
    {
        Tp2Level => "tp2",
        TbLevel => "tb",
        ElementLevel el => el.SectionsPerLayer >= 8 ? "f5" : "f4",
        _ => "",
    };

    // ============================================================== CREATE ===
    //
    // The create half of R4's "create-default-then-edit": a New command appends one
    // minimal, in-scale record so the author immediately has something to grab and
    // reshape with the existing handles / inspector. Every family uses the append
    // contract its paste op already proves (I1: untouched bytes stay byte-identical;
    // I3: the addition is one whole record inside its own span). The typed families
    // (tb / tp2) build a record from field defaults the codecs re-serialise; the raw-
    // span families (f4 / f5) have no field writer, so a new record is cloned from a
    // real shipped record of the same kind and re-anchored — we never invent bytes, and
    // a kind with no shipped template (or no storage at all) reports NotSupported.

    // Default proportions for a from-scratch shape, ported from the reference editor's
    // add_wall / add_water / add_path so a created object reads as a real volume.
    private const double NewWallW = 800.0, NewWallH = 140.0;   // wall / defined-area box
    private const double NewWaterW = 1200.0, NewWaterH = 400.0;
    private const double NewPathSpan = 300.0;                  // length of a new two-point path

    /// <summary>Append a brand-new default record of <paramref name="kind"/> to one layer,
    /// centred on the world point <paramref name="at"/> (the view centre or the cursor). An
    /// optional <paramref name="sprite"/> re-points a created decoration at a chosen name.
    /// Returns <see cref="EditResult.Done"/> (model mutated, dirty) or a reason code with the
    /// model untouched. Entity creation lives in <see cref="EntitySpawn.Add"/> (a different
    /// provenance contract), so <see cref="SceneKind.Entity"/> is deliberately not handled here.</summary>
    public static EditResult NewRecord(object? model, SceneKind kind, int layer, PointD at, string? sprite = null)
        => NewRecord(model, kind, layer, at, out _, sprite);

    /// <summary>Create a minimal default record of <paramref name="kind"/> on <paramref name="layer"/>
    /// anchored at world point <paramref name="at"/>; on <see cref="EditResult.Done"/> the newly added
    /// record (or, for a raw-span family, a delete-/select-able edit handle over it) is returned in
    /// <paramref name="created"/> so the shell can select it and a caller can prove the write was
    /// localised. <paramref name="sprite"/> names a deco template when the caller has one.</summary>
    public static EditResult NewRecord(object? model, SceneKind kind, int layer, PointD at, out object? created, string? sprite = null)
    {
        created = null;
        return model switch
        {
            TbLevel tb => TbCreate(tb, kind, layer, at, sprite, out created),
            Tp2Level tp => Tp2Create(tp, kind, layer, at, sprite, out created),
            ElementLevel el => ElemCreate(el, kind, layer, at, sprite, out created),
            _ => EditResult.NotSupported,
        };
    }

    // Normalised rectangle vertices (x spans ±0.5) matching the reference _rect_wall_verts;
    // the projector maps world = pos + vertex * width, so a unit-scaled shape lands in world size.
    private static double[][] RectWallVerts(double width, double length)
    {
        double hy = width != 0 ? 0.5 * length / width : 0.5;
        return new[]
        {
            new[] { -0.5, -hy }, new[] { 0.5, -hy }, new[] { 0.5, hy }, new[] { -0.5, hy },
        };
    }

    /// <summary>Port of the reference editor's recompute_path: derive each spline point's smooth
    /// tangents from its neighbours (p1 = −p2 = ±a quarter of the neighbour gap) and the record's
    /// bbox centre / extent from the anchors, so a created or reshaped path has the shape of a
    /// shipped one the game follows smoothly. Each point is the six doubles
    /// [p0x, p0y, p1x, p1y, p2x, p2y]; only the p0 anchors are read — the handle pairs are written.
    /// Shared by creation and the runtime path editor.</summary>
    internal static void RecomputeSpline(double[][] sps, out double posX, out double posY, out double extX, out double extY)
    {
        posX = posY = extX = extY = 0;
        int n = sps.Length;
        if (n == 0) return;
        double minx = double.MaxValue, miny = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double[] cur = sps[i];
            double[] prev = i > 0 ? sps[i - 1] : cur;
            double[] nxt = i < n - 1 ? sps[i + 1] : cur;
            double tx = (nxt[0] - prev[0]) * 0.25, ty = (nxt[1] - prev[1]) * 0.25;
            cur[2] = -tx; cur[3] = -ty;    // p1 (in-handle)
            cur[4] = tx;  cur[5] = ty;     // p2 (out-handle)
            if (cur[0] < minx) minx = cur[0];
            if (cur[1] < miny) miny = cur[1];
        }
        double maxx = double.MinValue, maxy = double.MinValue;
        foreach (var sp in sps)
        {
            if (sp[0] > maxx) maxx = sp[0];
            if (sp[1] > maxy) maxy = sp[1];
        }
        posX = (minx + maxx) / 2; posY = (miny + maxy) / 2;
        extX = maxx - minx; extY = maxy - miny;
    }

    // A fresh two-point horizontal path centred on (cx, cy), tangents + bbox recomputed. Shared
    // by tb and tp2 creation (the record types differ only in their tail id / reserved word).
    private static (double[][] splines, double posX, double posY, double extX, double extY) NewSplinePoints(double cx, double cy)
    {
        double half = NewPathSpan / 2;
        var sps = new[] { new double[6], new double[6] };
        sps[0][0] = cx - half; sps[0][1] = cy;
        sps[1][0] = cx + half; sps[1][1] = cy;
        RecomputeSpline(sps, out double px, out double py, out double ex, out double ey);
        return (sps, px, py, ex, ey);
    }

    // --------------------------------------------------------- tasty blue ---

    private static EditResult TbCreate(TbLevel tb, SceneKind kind, int layer, PointD at, string? sprite, out object? created)
    {
        created = null;
        if (tb.Layers.Count == 0) return EditResult.Failed;
        int li = Math.Clamp(layer, 0, tb.Layers.Count - 1);
        var L = tb.Layers[li];
        switch (kind)
        {
            case SceneKind.Wall:
            {
                var w = new TbWall
                {
                    PosX = at.X, PosY = at.Y, Width = NewWallW, Length = NewWallH,
                    WallTypeName = "everything", HasShapesFlag = 0,
                    WallId = NextTbWallId(tb), Raw = Array.Empty<byte>(), Off = -1,
                };
                w.Shapes.Add(new TbShape { Name = "poly", Vertices = RectWallVerts(NewWallW, NewWallH) });
                L.Walls.Add(w);
                created = w;
                return EditResult.Done;
            }
            case SceneKind.Area:
            {
                // A defined area is a named trigger; inherit a real area's name when the level
                // has one (an unknown name is legal bytes but does nothing in game), else a plain
                // square. The codec re-serialises every field, so the fresh record is lossless.
                var src = tb.Layers.SelectMany(x => x.Areas).FirstOrDefault();
                var a = src is null
                    ? new TbDefinedArea { AreaName = "" }
                    : CloneTbArea(src);
                a.PosX = at.X; a.PosY = at.Y;
                a.Width = NewWallW; a.Length = NewWallH;
                a.HasShapesFlag = 0; a.ShapesTailFlag = 0;
                a.Shapes = new List<TbShape> { new TbShape { Name = "poly", Vertices = RectWallVerts(NewWallW, NewWallH) } };
                a.AreaId = NextTbAreaId(tb); a.Raw = Array.Empty<byte>(); a.Off = -1;
                L.Areas.Add(a);
                created = a;
                return EditResult.Done;
            }
            case SceneKind.Water:
            {
                var q = NewTbWater(tb, at.X, at.Y);
                L.Waters.Add(q);
                created = q;
                return EditResult.Done;
            }
            case SceneKind.Path:
            {
                var (sps, px, py, ex, ey) = NewSplinePoints(at.X, at.Y);
                var pth = new TbPath
                {
                    PathName = NextPathName(tb), PosX = px, PosY = py, ExtentXGuess = ex, ExtentYGuess = ey,
                    PathFlag = 1, SplinePoints = sps, InternalIdGuess = NextTbPathId(tb),
                    Raw = Array.Empty<byte>(), Off = -1,
                };
                L.Paths.Add(pth);
                created = pth;
                return EditResult.Done;
            }
            case SceneKind.Deco:
                return TbCreateDeco(tb, L, at, sprite, out created);
            default:
                return EditResult.NotSupported;
        }
    }

    // ElementWater constructor defaults recovered from the disassembly (mirrors tp2editor.
    // _new_water_record): copy a real volume's flags/scalars when one exists so a new box matches
    // the level's style, else the shipped defaults; geometry is then set from the call.
    private static TbWater NewTbWater(TbLevel tb, double cx, double cy)
    {
        var src = tb.Layers.SelectMany(l => l.Waters).FirstOrDefault();
        var q = src is not null ? CloneTbWater(src) : new TbWater { Name = "" };
        if (src is null)
        {
            q.I[0] = 1; q.I[1] = 1; q.I[2] = 1; q.I[3] = 1; q.I[4] = 1; q.I[5] = 1; q.I[6] = 1; q.I[7] = 1;
            q.D[0] = 50; q.D[1] = 50; q.D[2] = 50; q.D[3] = 1; q.D[4] = 1;
        }
        SetWaterRect(q, cx, cy, NewWaterW, NewWaterH);
        q.WaterId = NextTbWaterId(tb);
        q.Raw = Array.Empty<byte>(); q.Off = -1;
        return q;
    }

    // Mirror SceneProjector.WaterSetRect: centre + full size, with the local min/max half-extents.
    private static void SetWaterRect(TbWater q, double cx, double cy, double sw, double sh)
    {
        q.D[5] = cx; q.D[6] = cy; q.D[7] = sw; q.D[8] = sh;
        q.D[9] = -sw / 2; q.D[10] = -sh / 2; q.D[11] = sw / 2; q.D[12] = sh / 2;
    }

    private static EditResult TbCreateDeco(TbLevel tb, TbLayer layer, PointD at, string? sprite, out object? created)
    {
        created = null;
        var src = TbDecoTemplate(tb, sprite);
        if (src is null) return EditResult.NotSupported;   // never invent a decoration body
        double baseX = layer.Decos.Count > 0 ? layer.Decos[^1].PosX * Centi : 0;
        double baseY = layer.Decos.Count > 0 ? layer.Decos[^1].PosY * Centi : 0;
        long tx = (long)Math.Round(at.X * Centi), ty = (long)Math.Round(at.Y * Centi);

        var d = new TbDeco
        {
            Raw = (byte[])src.Raw.Clone(), Owner = layer,
            Rotation = src.Rotation, DimW = src.DimW, DimH = src.DimH,
            ColorRgba = (byte[])src.ColorRgba.Clone(), FlipX = src.FlipX, FlipY = src.FlipY,
        };
        layer.Decos.Add(d);
        // An explicit sprite re-points the record (and sets Name); otherwise it keeps the
        // template's look so the copy renders, and the author re-sprites it in the inspector.
        if (!string.IsNullOrEmpty(sprite)) TbEdit.SetDecoSprite(tb, d, sprite);
        else d.Name = src.Name;
        int hl = DecoHeadLen(d.Raw);
        if (hl < 0) { layer.Decos.RemoveAt(layer.Decos.Count - 1); return EditResult.Failed; }
        WriteI32(d.Raw, hl, (int)(tx - (long)baseX));
        WriteI32(d.Raw, hl + 4, (int)(ty - (long)baseY));
        d.PosX = tx / Centi; d.PosY = ty / Centi;
        created = d;
        return EditResult.Done;
    }

    private static TbDeco? TbDecoTemplate(TbLevel tb, string? sprite) => sprite is { Length: > 0 }
        ? tb.Layers.SelectMany(l => l.Decos).FirstOrDefault(d => d.Name == sprite && d.Raw.Length > 1)
          ?? tb.Layers.SelectMany(l => l.Decos).FirstOrDefault(d => d.Raw.Length > 1)
        : tb.Layers.SelectMany(l => l.Decos).FirstOrDefault(d => d.Raw.Length > 1);

    private static string NextPathName(TbLevel tb)
    {
        var taken = new HashSet<string>(tb.Layers.SelectMany(l => l.Paths).Select(p => p.PathName ?? ""), StringComparer.Ordinal);
        int k = 1; while (taken.Contains($"path{k}")) k++;
        return $"path{k}";
    }

    private static int NextTbPathId(TbLevel tb)
    {
        int m = 0; foreach (var l in tb.Layers) foreach (var p in l.Paths) if (p.InternalIdGuess > m) m = p.InternalIdGuess;
        return m == 0 ? 1000 : m + 1;
    }

    // ---------------------------------------------------------------- tp2 ---

    private static EditResult Tp2Create(Tp2Level tp, SceneKind kind, int layer, PointD at, string? sprite, out object? created)
    {
        created = null;
        if (tp.Layers.Count == 0) return EditResult.Failed;
        int li = Math.Clamp(layer, 0, tp.Layers.Count - 1);
        var L = tp.Layers[li];
        switch (kind)
        {
            case SceneKind.Wall:
            {
                var w = new Tp2Wall
                {
                    PosX = at.X, PosY = at.Y, Width = NewWallW, Length = NewWallH,
                    WallTypeName = "everything", HasShapesFlag = 0, Reserved = 0, WallId = NextWallId(tp),
                };
                w.Shapes.Add(new Tp2Shape { Name = "poly", Vertices = RectWallVerts(NewWallW, NewWallH) });
                L.Walls.Add(w);
                created = w;
                return EditResult.Done;
            }
            case SceneKind.Path:
            {
                var (sps, px, py, ex, ey) = NewSplinePoints(at.X, at.Y);
                var pth = new Tp2Path
                {
                    PathName = NextTp2PathName(tp), PosX = px, PosY = py, ExtentXGuess = ex, ExtentYGuess = ey,
                    PathFlag = 1, SplinePoints = sps, InternalIdGuess = NextTp2PathId(tp),
                };
                L.Paths.Add(pth);
                created = pth;
                return EditResult.Done;
            }
            case SceneKind.Deco:
            {
                // TP2 decorations are typed, so a create is a deep clone of a real deco nudged to
                // the point (the codec re-serialises every field; the sprite stays editable after).
                var src = Tp2DecoTemplate(tp, sprite);
                if (src is null) return EditResult.NotSupported;
                var d = CloneTp2Deco(src);
                d.Position = new[] { at.X, at.Y };
                L.Decos.Add(d);
                if (!string.IsNullOrEmpty(sprite)) Tp2Edit.SetDecoSprite(tp, d, sprite);
                created = d;
                return EditResult.Done;
            }
            // TP2 has no defined-area or water storage (Tp2Layer holds Walls/Paths/Entities/Decos only).
            default:
                return EditResult.NotSupported;
        }
    }

    private static Tp2Deco? Tp2DecoTemplate(Tp2Level tp, string? sprite) => sprite is { Length: > 0 }
        ? tp.Layers.SelectMany(l => l.Decos).FirstOrDefault(d => d.String == sprite)
          ?? tp.Layers.SelectMany(l => l.Decos).FirstOrDefault()
        : tp.Layers.SelectMany(l => l.Decos).FirstOrDefault();

    private static string NextTp2PathName(Tp2Level tp)
    {
        var taken = new HashSet<string>(tp.Layers.SelectMany(l => l.Paths).Select(p => p.PathName ?? ""), StringComparer.Ordinal);
        int k = 1; while (taken.Contains($"path{k}")) k++;
        return $"path{k}";
    }

    private static int NextTp2PathId(Tp2Level tp)
    {
        int m = 0; foreach (var l in tp.Layers) foreach (var p in l.Paths) if (p.InternalIdGuess > m) m = p.InternalIdGuess;
        return m == 0 ? 1000 : m + 1;
    }

    // ------------------------------------------------ forever / final bite ---

    // Section index per kind (matches SceneProjector.ElemIdx): f5 wall0 snow1 area2 path3
    // pointlist4 entity5 deco6 water7; f4 wall0 area1 path2 pointlist3 entity4 deco5 water6.
    private static int ElemSectionIndex(string game, SceneKind kind) => (game, kind) switch
    {
        ("f5", SceneKind.Wall) => 0,
        ("f5", SceneKind.Area) => 2,
        ("f5", SceneKind.Path) => 3,
        ("f5", SceneKind.Deco) => 6,
        ("f4", SceneKind.Wall) => 0,
        ("f4", SceneKind.Area) => 1,
        ("f4", SceneKind.Path) => 2,
        ("f4", SceneKind.Deco) => 5,
        _ => -1,
    };

    private static EditResult ElemCreate(ElementLevel el, SceneKind kind, int layer, PointD at, string? sprite, out object? created)
    {
        created = null;
        if (el.Layers.Count == 0) return EditResult.Failed;
        int li = Math.Clamp(layer, 0, el.Layers.Count - 1);
        var secs = el.Layers[li];
        string game = el.SectionsPerLayer >= 8 ? "f5" : "f4";
        int idx = ElemSectionIndex(game, kind);
        if (idx < 0 || idx >= secs.Count) return EditResult.NotSupported;
        var sec = secs[idx];
        if (sec.Opaque || sec.Records.Count == 0) return EditResult.NotSupported;   // no shipped record to copy

        switch (kind)
        {
            case SceneKind.Wall:
            case SceneKind.Area:
            {
                var rec = (byte[])sec.Records[^1].Clone();
                if (!TryPlaceShapePos(rec, kind == SceneKind.Wall, at.X, at.Y)) return EditResult.Failed;
                sec.Records.Add(rec);
                sec.Count++;
                created = SceneProjector.BuildShapeEdit(sec, sec.Records.Count - 1, kind == SceneKind.Wall);
                return EditResult.Done;
            }
            case SceneKind.Path:
            {
                var rec = (byte[])sec.Records[^1].Clone();
                if (!TryPlacePathPos(rec, at.X, at.Y)) return EditResult.Failed;
                sec.Records.Add(rec);
                sec.Count++;
                return EditResult.Done;
            }
            case SceneKind.Deco:
                return ElemCreateDeco(el, sec, at, sprite, out created);
            default:
                return EditResult.NotSupported;
        }
    }

    private static EditResult ElemCreateDeco(ElementLevel el, Section sec, PointD at, string? sprite, out object? created)
    {
        created = null;
        int n = sec.Records.Count;
        var (baseX, baseY) = DecoChainAt(sec.Records, n - 1);   // centiunits accumulated to the tail
        long tx = (long)Math.Round(at.X * Centi), ty = (long)Math.Round(at.Y * Centi);
        var clone = (byte[])sec.Records[^1].Clone();
        sec.Records.Add(clone);
        sec.Count++;
        int idx = sec.Records.Count - 1;
        if (!string.IsNullOrEmpty(sprite))
            ElemEdit.SetDecoSprite(new DecoEditHandle(el, sec, idx), sprite);
        if (DecoHeadLen(sec.Records[idx]) < 0)
        {
            sec.Records.RemoveAt(idx); sec.Count--; return EditResult.Failed;
        }
        int hl = DecoHeadLen(sec.Records[idx]);
        WriteI32(sec.Records[idx], hl, (int)(tx - (long)baseX));
        WriteI32(sec.Records[idx], hl + 4, (int)(ty - (long)baseY));
        created = new DecoEditHandle(el, sec, idx);
        return EditResult.Done;
    }

    // Wall/area record: place the leading x,y doubles at an absolute point (same head walk as
    // TryOffsetShapePos: area leads with a name string, wall with the four geometry doubles).
    private static bool TryPlaceShapePos(byte[] rec, bool isWall, double ax, double ay)
    {
        int p = 0;
        if (!isWall)
        {
            if (p + 4 > rec.Length) return false;
            int n = ReadI32(rec, p); p += 4;
            if (n < 0 || p + n > rec.Length) return false;
            p += n;
        }
        if (p + 16 > rec.Length) return false;
        BitConverter.GetBytes(ax).CopyTo(rec, p);
        BitConverter.GetBytes(ay).CopyTo(rec, p + 8);
        return true;
    }

    // Path record: <i32 nameLen><name><f64 posx><f64 posy><f64 extx><f64 exty><u8 flag>
    // [if flag==1: <i32 n><n × 6 f64>]<i32>. Move the whole spline so its nominal position lands
    // on (ax, ay) by shifting the position pair and every anchor p0 by the same delta; the tangent
    // handles are relative so they stay, and the record length is unchanged (the framed section
    // therefore stays walk-consistent). Mirrors SceneProjector.GeoReader.TryPath's walk.
    private static bool TryPlacePathPos(byte[] rec, double ax, double ay)
    {
        int p = 0;
        if (p + 4 > rec.Length) return false;
        int nameLen = ReadI32(rec, p); p += 4;
        if (nameLen < 0 || p + nameLen > rec.Length) return false;
        p += nameLen;
        if (p + 32 > rec.Length) return false;
        double x = BitConverter.ToDouble(rec, p), y = BitConverter.ToDouble(rec, p + 8);
        double dx = ax - x, dy = ay - y;
        AddF64(rec, p, dx); AddF64(rec, p + 8, dy);          // pos_x, pos_y
        p += 32;
        if (p + 1 > rec.Length) return false;
        int flag = rec[p]; p += 1;
        if (flag == 1)
        {
            if (p + 4 > rec.Length) return false;
            int cnt = ReadI32(rec, p); p += 4;
            if (cnt < 0 || cnt > 1_000_000) return false;
            for (int i = 0; i < cnt; i++)
            {
                if (p + 48 > rec.Length) return false;
                AddF64(rec, p, dx); AddF64(rec, p + 8, dy);   // anchor p0
                p += 48;
            }
        }
        return true;
    }

    private static void AddF64(byte[] b, int off, double d)
        => BitConverter.GetBytes(BitConverter.ToDouble(b, off) + d).CopyTo(b, off);

    // ============================================================== TP2 ====

    private static EditClipboard? Tp2Copy(Tp2Level tp, object? source)
    {
        switch (source)
        {
            case Tp2Wall w:
                return new EditClipboard { Family = "tp2", Kind = "wall", Layer = FindTp2Layer(tp, w), Payload = CloneTp2Wall(w) };
            case Tp2Entity e:
                return new EditClipboard { Family = "tp2", Kind = "entity", Layer = FindTp2Layer(tp, e), Payload = CloneTp2Entity(e) };
            case Tp2Deco d:
            {
                int li = FindTp2Layer(tp, d);
                return new EditClipboard { Family = "tp2", Kind = "deco", Layer = li, Payload = CloneTp2Deco(d), Name = li >= 0 ? Tp2DecoResolvedName(tp, li, d) : null };
            }
            default:
                return null;
        }
    }

    private static EditResult Tp2Paste(Tp2Level tp, EditClipboard clip)
    {
        if (tp.Layers.Count == 0) return EditResult.Failed;
        int li = Math.Clamp(clip.Layer, 0, tp.Layers.Count - 1);
        var layer = tp.Layers[li];
        switch (clip.Kind)
        {
            case "wall":
            {
                if (clip.Payload is not Tp2Wall src) return EditResult.Failed;
                var w = CloneTp2Wall(src);
                w.PosX += DupOffset; w.PosY += DupOffset; w.WallId = NextWallId(tp);
                layer.Walls.Add(w);
                return EditResult.Done;
            }
            case "entity":
            {
                if (clip.Payload is not Tp2Entity src) return EditResult.Failed;
                var e = CloneTp2Entity(src);
                e.Position[0] += DupOffset / Tp2EntityK; e.Position[1] += DupOffset / Tp2EntityK;
                layer.Entities.Add(e);
                return EditResult.Done;
            }
            case "deco":
            {
                if (clip.Payload is not Tp2Deco src) return EditResult.Failed;
                var d = CloneTp2Deco(src);
                d.Position[0] += DupOffset; d.Position[1] += DupOffset;
                layer.Decos.Add(d);
                // Materialise the resolved name (a type-0 clone appended last would inherit an
                // unrelated neighbour) so the copy looks like its source in its new position.
                if (!string.IsNullOrEmpty(clip.Name)) Tp2Edit.SetDecoSprite(tp, d, clip.Name);
                return EditResult.Done;
            }
            default:
                return EditResult.NotSupported;
        }
    }

    private static EditResult Tp2Delete(Tp2Level tp, object? source)
    {
        if (source is not (Tp2Wall or Tp2Entity or Tp2Deco)) return EditResult.NotSupported;
        int li = FindTp2Layer(tp, source!);
        if (li < 0) return EditResult.NothingSelected;
        var layer = tp.Layers[li];
        if (source is Tp2Wall w) { layer.Walls.Remove(w); return EditResult.Done; }
        if (source is Tp2Entity e) { layer.Entities.Remove(e); return EditResult.Done; }
        if (source is Tp2Deco d)
        {
            int i = layer.Decos.IndexOf(d);
            if (i < 0) return EditResult.NothingSelected;
            // A following type-0 deco inherits its name through this one: materialise it first.
            if (i + 1 < layer.Decos.Count && layer.Decos[i + 1].Type == 0)
            {
                string? nm = Tp2DecoResolvedName(tp, li, d);
                if (!string.IsNullOrEmpty(nm)) Tp2Edit.SetDecoSprite(tp, layer.Decos[i + 1], nm);
            }
            layer.Decos.RemoveAt(i);
            return EditResult.Done;
        }
        return EditResult.NotSupported;
    }

    private static string? Tp2DecoResolvedName(Tp2Level tp, int li, Tp2Deco d)
    {
        var layer = tp.Layers[li];
        int idx = layer.Decos.IndexOf(d);
        if (idx < 0) return null;
        string? last = null;
        for (int i = 0; i <= idx; i++)
        {
            var x = layer.Decos[i];
            string? nm = x.Type switch
            {
                2 => x.String,
                1 => (x.Cell >= 0 && x.Cell < tp.TileTypes.Count) ? tp.TileTypes[x.Cell] : null,
                _ => last,
            };
            if (nm is not null) last = nm;
        }
        return last;
    }

    private static int FindTp2Layer(Tp2Level tp, object rec)
    {
        for (int li = 0; li < tp.Layers.Count; li++)
        {
            var l = tp.Layers[li];
            if (l.Walls.Contains(rec) || l.Entities.Contains(rec) || l.Decos.Contains(rec)) return li;
        }
        return -1;
    }

    private static int NextWallId(Tp2Level tp)
    {
        int max = 0;
        foreach (var l in tp.Layers) foreach (var w in l.Walls) if (w.WallId > max) max = w.WallId;
        return max + 1;
    }

    private static Tp2Wall CloneTp2Wall(Tp2Wall w)
    {
        var n = new Tp2Wall
        {
            PosX = w.PosX, PosY = w.PosY, Width = w.Width, Length = w.Length,
            WallTypeName = w.WallTypeName, HasShapesFlag = w.HasShapesFlag,
            Reserved = w.Reserved, WallId = w.WallId,
        };
        foreach (var s in w.Shapes) n.Shapes.Add(CloneShape(s));
        return n;
    }

    private static Tp2Shape CloneShape(Tp2Shape s) => new()
    {
        Name = s.Name, IsCircle = s.IsCircle, Cx = s.Cx, Cy = s.Cy, Radius = s.Radius,
        Vertices = CloneVerts(s.Vertices),
    };

    private static double[][] CloneVerts(double[][] v)
    {
        var c = new double[v.Length][];
        for (int i = 0; i < v.Length; i++) c[i] = (double[])v[i].Clone();
        return c;
    }

    private static Tp2Entity CloneTp2Entity(Tp2Entity e) => new()
    {
        Type = e.Type,
        Position = (double[])e.Position.Clone(),
        Field158 = e.Field158, Field15c = e.Field15c, Vec0 = e.Vec0, Vec1 = e.Vec1,
        Field250 = e.Field250, Rotation = e.Rotation, HasBox = e.HasBox, BoxEnabled = e.BoxEnabled,
        BoxBounds = (int[])e.BoxBounds.Clone(), ColorRgba = (byte[])e.ColorRgba.Clone(),
        Mass = e.Mass, Priority = e.Priority, HasMoveDirection = e.HasMoveDirection,
        Move = (double[])e.Move.Clone(), MoveFlag0 = e.MoveFlag0, HasPathFollow = e.HasPathFollow,
        Pv0 = e.Pv0, Pv1 = e.Pv1, Pv2 = e.Pv2, Pv3 = e.Pv3, Pv4 = e.Pv4,
        PFlag0 = e.PFlag0, PFlag1 = e.PFlag1, PMode = e.PMode, PathName = e.PathName,
        HasEmitter = e.HasEmitter, EmitV = (double[])e.EmitV.Clone(),
        EmitReserved = e.EmitReserved, EmitEndMarker = e.EmitEndMarker,
    };

    private static Tp2Deco CloneTp2Deco(Tp2Deco d) => new()
    {
        Type = d.Type, String = d.String, Cell = d.Cell, HasString = d.HasString, HasCell = d.HasCell,
        Position = (double[])d.Position.Clone(), SizeRaw = d.SizeRaw, ExtraFlag = d.ExtraFlag,
        FlipWordX = d.FlipWordX, FlipWordY = d.FlipWordY, FlipX = d.FlipX, FlipY = d.FlipY,
        ExtraInts = (int[])d.ExtraInts.Clone(),
        ColorRgba = (byte[])d.ColorRgba.Clone(), DimsRaw = (int[])d.DimsRaw.Clone(),
        PriorityTotal = d.PriorityTotal,
    };

    // =========================================================== TB =======

    private static EditClipboard? TbCopy(TbLevel tb, object? source)
    {
        switch (source)
        {
            case TbWall w:
                return new EditClipboard { Family = "tb", Kind = "wall", Layer = FindTbLayer(tb, w), Payload = CloneTbWall(w) };
            case TbDefinedArea a:
                return new EditClipboard { Family = "tb", Kind = "area", Layer = FindTbLayer(tb, a), Payload = CloneTbArea(a) };
            case TbWater wt:
                return new EditClipboard { Family = "tb", Kind = "water", Layer = FindTbLayer(tb, wt), Payload = CloneTbWater(wt) };
            case TbEntity e:
                return new EditClipboard { Family = "tb", Kind = "entity", Layer = FindTbLayer(tb, e), Payload = (byte[])e.Raw.Clone(), AbsX = e.PosX, AbsY = e.PosY, HasAbs = true };
            case TbDeco d:
                return new EditClipboard { Family = "tb", Kind = "deco", Layer = FindTbLayer(tb, d), Payload = (byte[])d.Raw.Clone(), Name = d.Name, AbsX = d.PosX, AbsY = d.PosY, HasAbs = true };
            default:
                return null;
        }
    }

    private static EditResult TbPaste(TbLevel tb, EditClipboard clip)
    {
        if (tb.Layers.Count == 0) return EditResult.Failed;
        int li = Math.Clamp(clip.Layer, 0, tb.Layers.Count - 1);
        var layer = tb.Layers[li];
        switch (clip.Kind)
        {
            case "wall":
            {
                if (clip.Payload is not TbWall src) return EditResult.Failed;
                var w = CloneTbWall(src);
                w.PosX += DupOffset; w.PosY += DupOffset; w.WallId = NextTbWallId(tb);
                w.Raw = Array.Empty<byte>(); w.Off = -1;
                layer.Walls.Add(w);
                return EditResult.Done;
            }
            case "area":
            {
                if (clip.Payload is not TbDefinedArea src) return EditResult.Failed;
                var a = CloneTbArea(src);
                a.PosX += DupOffset; a.PosY += DupOffset; a.AreaId = NextTbAreaId(tb);
                a.Raw = Array.Empty<byte>(); a.Off = -1;
                layer.Areas.Add(a);
                return EditResult.Done;
            }
            case "water":
            {
                if (clip.Payload is not TbWater src) return EditResult.Failed;
                var q = CloneTbWater(src);
                q.D[5] += DupOffset; q.D[6] += DupOffset; q.WaterId = NextTbWaterId(tb);
                q.Raw = Array.Empty<byte>(); q.Off = -1;
                layer.Waters.Add(q);
                return EditResult.Done;
            }
            case "entity":
            {
                if (clip.Payload is not byte[] raw) return EditResult.Failed;
                double ax = clip.HasAbs ? clip.AbsX : (layer.Entities.Count > 0 ? layer.Entities[^1].PosX : 0);
                double ay = clip.HasAbs ? clip.AbsY : (layer.Entities.Count > 0 ? layer.Entities[^1].PosY : 0);
                var e = new TbEntity { Raw = (byte[])raw.Clone(), PosX = ax + DupOffset, PosY = ay + DupOffset };
                // Re-read the display fields off the cloned record (name / rotation / mass / facing) so
                // the paste projects immediately and, crucially, so a pasted entity keeps the record's own
                // flip flag instead of falling to the un-mirrored path.
                var eh = EntityFraming.HeadAt(e.Raw, 0, e.Raw.Length);
                if (eh is not null)
                {
                    e.Name = eh.Value.Name;
                    var edisp = EntityFraming.ReadBody(e.Raw, eh.Value.XyOff, EntityFraming.ExtTb, e.Raw.Length);
                    e.Flip = edisp.Flip;
                    if (edisp.HasBody) { e.Rotation = edisp.Rotation; e.Mass = edisp.Mass; e.Priority = edisp.Priority; e.ColorRgba = edisp.ColorRgba; }
                }
                // TbCodec.PatchEntityPositions re-derives each entity's chained delta from its
                // absolute PosX/PosY at encode time, so an appended clone lands correctly and
                // earlier entities keep their absolute positions (their PosX/PosY are unchanged).
                layer.Entities.Add(e);
                return EditResult.Done;
            }
            case "deco":
            {
                if (clip.Payload is not byte[] raw) return EditResult.Failed;
                return TbPasteDeco(tb, layer, (byte[])raw.Clone(), clip);
            }
            default:
                return EditResult.NotSupported;
        }
    }

    private static EditResult TbPasteDeco(TbLevel tb, TbLayer layer, byte[] cloneRaw, EditClipboard clip)
    {
        // Base of the append = accumulated centiunits of the current tail (empty layer => 0).
        double baseX = layer.Decos.Count > 0 ? layer.Decos[^1].PosX * Centi : 0;
        double baseY = layer.Decos.Count > 0 ? layer.Decos[^1].PosY * Centi : 0;
        double targetX = (clip.HasAbs ? clip.AbsX : (baseX / Centi)) * Centi + DupOffset * Centi;
        double targetY = (clip.HasAbs ? clip.AbsY : (baseY / Centi)) * Centi + DupOffset * Centi;

        var d = new TbDeco { Raw = cloneRaw, Owner = layer };
        layer.Decos.Add(d);

        // Materialise the sprite name so a trailing clone inherits the source's look, not the
        // new last neighbour's; then rewrite the (now type-1, 5-byte) head's chained delta.
        if (!string.IsNullOrEmpty(clip.Name)) TbEdit.SetDecoSprite(tb, d, clip.Name);
        int hl = DecoHeadLen(d.Raw);
        if (hl < 0) return EditResult.Failed;
        WriteI32(d.Raw, hl, (int)Math.Round(targetX - baseX));
        WriteI32(d.Raw, hl + 4, (int)Math.Round(targetY - baseY));
        d.PosX = (baseX + (targetX - baseX)) / Centi;
        d.PosY = (baseY + (targetY - baseY)) / Centi;
        d.Name = clip.Name;
        return EditResult.Done;
    }

    private static EditResult TbDelete(TbLevel tb, object? source)
    {
        if (source is not (TbWall or TbDefinedArea or TbWater or TbEntity or TbDeco))
            return EditResult.NotSupported;
        int li = FindTbLayer(tb, source);
        if (li < 0) return EditResult.NothingSelected;
        var layer = tb.Layers[li];
        if (source is TbWall w) { layer.Walls.Remove(w); return EditResult.Done; }
        if (source is TbDefinedArea a) { layer.Areas.Remove(a); return EditResult.Done; }
        if (source is TbWater q) { layer.Waters.Remove(q); return EditResult.Done; }
        if (source is TbEntity e) { layer.Entities.Remove(e); return EditResult.Done; }
        if (source is TbDeco d)
        {
            int i = layer.Decos.IndexOf(d);
            if (i < 0) return EditResult.NothingSelected;
            if (i + 1 < layer.Decos.Count)
            {
                var next = layer.Decos[i + 1];
                // A following type-0 deco inherits its name through the removed one: bake it first.
                if (next.Raw.Length >= 1 && next.Raw[0] == 0x00 && DecoHeadLen(next.Raw) == 1
                    && !string.IsNullOrEmpty(d.Name))
                    TbEdit.SetDecoSprite(tb, next, d.Name);

                // Fold the removed record's delta into its successor so later positions are kept:
                // absolute[i+1] = absolute[i] + delta[i+1]; removing i drops absolute[i], so
                // delta'[i+1] = delta[i] + delta[i+1] restores it.
                int hlRm = DecoHeadLen(d.Raw);
                int hlNx = DecoHeadLen(next.Raw);
                if (hlRm < 0 || hlNx < 0) return EditResult.Failed;
                int nrx = ReadI32(next.Raw, hlNx) + ReadI32(d.Raw, hlRm);
                int nry = ReadI32(next.Raw, hlNx + 4) + ReadI32(d.Raw, hlRm + 4);
                WriteI32(next.Raw, hlNx, nrx);
                WriteI32(next.Raw, hlNx + 4, nry);
            }
            layer.Decos.RemoveAt(i);
            return EditResult.Done;
        }
        return EditResult.NotSupported;
    }

    private static int FindTbLayer(TbLevel tb, object rec)
    {
        for (int li = 0; li < tb.Layers.Count; li++)
        {
            var l = tb.Layers[li];
            if (l.Walls.Contains(rec) || l.Areas.Contains(rec) || l.Waters.Contains(rec)
                || l.Entities.Contains(rec) || l.Decos.Contains(rec)) return li;
        }
        return -1;
    }

    private static int NextTbWallId(TbLevel tb) { int m = 0; foreach (var l in tb.Layers) foreach (var w in l.Walls) if (w.WallId > m) m = w.WallId; return m + 1; }
    private static int NextTbAreaId(TbLevel tb) { int m = 0; foreach (var l in tb.Layers) foreach (var a in l.Areas) if (a.AreaId > m) m = a.AreaId; return m + 1; }
    private static int NextTbWaterId(TbLevel tb) { int m = 0; foreach (var l in tb.Layers) foreach (var q in l.Waters) if (q.WaterId > m) m = q.WaterId; return m + 1; }

    private static TbWall CloneTbWall(TbWall w)
    {
        var n = new TbWall { PosX = w.PosX, PosY = w.PosY, Width = w.Width, Length = w.Length, WallTypeName = w.WallTypeName, HasShapesFlag = w.HasShapesFlag, ShapesTailFlag = w.ShapesTailFlag, WallId = w.WallId };
        foreach (var s in w.Shapes) n.Shapes.Add(CloneTbShape(s));
        return n;
    }

    private static TbDefinedArea CloneTbArea(TbDefinedArea a)
    {
        var n = new TbDefinedArea { AreaName = a.AreaName, PosX = a.PosX, PosY = a.PosY, Width = a.Width, Length = a.Length, HasShapesFlag = a.HasShapesFlag, ShapesTailFlag = a.ShapesTailFlag, AreaId = a.AreaId };
        foreach (var s in a.Shapes) n.Shapes.Add(CloneTbShape(s));
        return n;
    }

    private static TbShape CloneTbShape(TbShape s) => new()
    {
        Name = s.Name, IsCircle = s.IsCircle, Cx = s.Cx, Cy = s.Cy, Radius = s.Radius,
        Vertices = CloneVerts(s.Vertices),
    };

    private static TbWater CloneTbWater(TbWater q) => new()
    {
        Name = q.Name, I = (int[])q.I.Clone(), D = (double[])q.D.Clone(), WaterId = q.WaterId,
    };

    // ==================================================== ELEMENT (f4/f5) ==

    private static EditClipboard? ElemCopy(ElementLevel el, object? source)
    {
        string fam = el.SectionsPerLayer >= 8 ? "f5" : "f4";
        switch (source)
        {
            case ElemShapeEdit ee when ee.Ok:
            {
                var (li, si) = FindSection(el, ee.Sec);
                if (li < 0) return null;
                return new EditClipboard { Family = fam, Kind = KindOfWallArea(si), Layer = li, SectionIndex = si, Payload = (byte[])ee.Rec.Clone() };
            }
            case DecoEditHandle dh:
            {
                if (dh.RecIndex < 0 || dh.RecIndex >= dh.Sec.Records.Count) return null;
                var (li, si) = FindSection(el, dh.Sec);
                if (li < 0) return null;
                var (ax, ay) = DecoChainAt(dh.Sec.Records, dh.RecIndex);   // centiunits at the source
                return new EditClipboard { Family = fam, Kind = "deco", Layer = li, SectionIndex = si, Payload = (byte[])dh.Sec.Records[dh.RecIndex].Clone(), AbsX = ax / Centi, AbsY = ay / Centi, HasAbs = true };
            }
            case EntityEditHandle eh:
            {
                var (li, si) = FindSection(el, eh.Sec);
                if (li < 0 || !eh.Sec.Opaque) return null;
                var (heads, ok) = EntityFraming.SplitRecords(eh.Sec.Block, 0, eh.Sec.Count, el.Game);
                if (!ok || eh.RecIndex < 0 || eh.RecIndex >= heads.Count) return null;
                int start = heads[eh.RecIndex].RecStart;
                int end = eh.RecIndex + 1 < heads.Count ? heads[eh.RecIndex + 1].RecStart : eh.Sec.Block.Length;
                var span = new byte[end - start];
                Buffer.BlockCopy(eh.Sec.Block, start, span, 0, span.Length);
                var a = AbsOf(heads, eh.RecIndex);
                return new EditClipboard { Family = fam, Kind = "entity", Layer = li, SectionIndex = si, Payload = span, AbsX = a.X, AbsY = a.Y, HasAbs = true, RecXyLocal = heads[eh.RecIndex].XyOff - heads[eh.RecIndex].RecStart };
            }
            default:
                return null;
        }
    }

    private static EditResult ElemPaste(ElementLevel el, EditClipboard clip)
    {
        if (el.Layers.Count == 0) return EditResult.Failed;
        int li = Math.Clamp(clip.Layer, 0, el.Layers.Count - 1);
        if (clip.SectionIndex < 0 || clip.SectionIndex >= el.Layers[li].Count) return EditResult.Failed;
        var sec = el.Layers[li][clip.SectionIndex];

        switch (clip.Kind)
        {
            case "wall":
            case "area":
            {
                if (clip.Payload is not byte[] raw || sec.Opaque) return EditResult.Failed;
                // Wall/area positions are absolute doubles inside the record: clone, splice the
                // two leading geometry doubles past their own offsets, append, bump the count.
                var rec = (byte[])raw.Clone();
                if (!TryOffsetShapePos(rec, clip.Kind == "wall", DupOffset, DupOffset)) return EditResult.Failed;
                sec.Records.Add(rec);
                sec.Count++;
                return EditResult.Done;
            }
            case "deco":
            {
                if (clip.Payload is not byte[] raw || sec.Opaque) return EditResult.Failed;
                return ElemPasteDeco(el, sec, (byte[])raw.Clone(), clip);
            }
            case "entity":
            {
                if (clip.Payload is not byte[] span || !sec.Opaque) return EditResult.Failed;
                return ElemPasteEntity(el, sec, (byte[])span.Clone(), clip);
            }
            default:
                return EditResult.NotSupported;
        }
    }

    private static EditResult ElemPasteDeco(ElementLevel el, Section sec, byte[] clone, EditClipboard clip)
    {
        // Chain base for an appended record = accumulated centiunits of the current tail.
        int n = sec.Records.Count;
        var (baseX, baseY) = DecoChainAt(sec.Records, n - 1);
        // Anchor the copy 40 world units from where its source resolved (falls back to the tail
        // when the copy carries no absolute, e.g. a cross-layer paste with an unresolved source).
        double srcX = clip.HasAbs ? clip.AbsX * Centi : baseX;
        double srcY = clip.HasAbs ? clip.AbsY * Centi : baseY;
        double targetX = srcX + DupOffset * Centi;
        double targetY = srcY + DupOffset * Centi;

        sec.Records.Add(clone);
        sec.Count++;
        int idx = sec.Records.Count - 1;

        // Materialise a type-1 head for the appended deco (avoids inheriting a stale neighbour).
        string? resolvedName = ResolveElemDecoName(el, sec, idx - 1 < 0 ? 0 : idx - 1);
        if (!string.IsNullOrEmpty(resolvedName))
            ElemEdit.SetDecoSprite(new DecoEditHandle(el, sec, idx), resolvedName);

        int hl = DecoHeadLen(sec.Records[idx]);
        if (hl < 0) return EditResult.Failed;
        WriteI32(sec.Records[idx], hl, (int)Math.Round(targetX - baseX));
        WriteI32(sec.Records[idx], hl + 4, (int)Math.Round(targetY - baseY));
        return EditResult.Done;
    }

    private static EditResult ElemPasteEntity(ElementLevel el, Section sec, byte[] span, EditClipboard clip)
    {
        var (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
        if (!ok || heads.Count != sec.Count) return EditResult.Failed;

        int xyLocal = clip.RecXyLocal;
        if (xyLocal < 0 && heads.Count > 0) xyLocal = heads[^1].XyOff - heads[^1].RecStart;
        if (xyLocal < 0 || xyLocal + 8 > span.Length) return EditResult.Failed;

        var tail = AbsOf(heads, heads.Count - 1);
        int dx = (int)Math.Round(clip.AbsX + DupOffset) - tail.X;
        int dy = (int)Math.Round(clip.AbsY + DupOffset) - tail.Y;
        WriteI32(span, xyLocal, dx);
        WriteI32(span, xyLocal + 4, dy);

        var nb = new byte[sec.Block.Length + span.Length];
        Buffer.BlockCopy(sec.Block, 0, nb, 0, sec.Block.Length);
        Buffer.BlockCopy(span, 0, nb, sec.Block.Length, span.Length);

        // Hard safety gate: the candidate block must still read back to back to Count+1 records (the
        // loading code's own walk; see EntityFraming.SplitRecords), else reject and leave it untouched.
        var (heads2, ok2) = EntityFraming.SplitRecords(nb, 0, sec.Count + 1, el.Game);
        if (!ok2 || heads2.Count != sec.Count + 1) return EditResult.Failed;

        sec.Block = nb;
        sec.Count++;
        return EditResult.Done;
    }

    private static EditResult ElemDelete(ElementLevel el, object? source)
    {
        switch (source)
        {
            case ElemShapeEdit ee when ee.Ok:
            {
                int i = ee.Sec.Records.IndexOf(ee.Rec);
                if (i < 0) return EditResult.NothingSelected;
                ee.Sec.Records.RemoveAt(i);
                ee.Sec.Count--;
                return EditResult.Done;
            }
            case DecoEditHandle dh:
            {
                if (dh.Sec.Opaque || dh.RecIndex < 0 || dh.RecIndex >= dh.Sec.Records.Count) return EditResult.NothingSelected;
                var recs = dh.Sec.Records;
                int i = dh.RecIndex;
                if (i + 1 < recs.Count)
                {
                    var next = recs[i + 1];
                    if (next.Length >= 1 && next[0] == 0x00 && DecoHeadLen(next) == 1)
                    {
                        string? nm = ResolveElemDecoName(el, dh.Sec, i);
                        if (!string.IsNullOrEmpty(nm)) ElemEdit.SetDecoSprite(new DecoEditHandle(el, dh.Sec, i + 1), nm);
                    }
                    int hlRm = DecoHeadLen(recs[i]);
                    int hlNx = DecoHeadLen(recs[i + 1]);
                    if (hlRm < 0 || hlNx < 0) return EditResult.Failed;
                    WriteI32(recs[i + 1], hlNx, ReadI32(recs[i + 1], hlNx) + ReadI32(recs[i], hlRm));
                    WriteI32(recs[i + 1], hlNx + 4, ReadI32(recs[i + 1], hlNx + 4) + ReadI32(recs[i], hlRm + 4));
                }
                recs.RemoveAt(i);
                dh.Sec.Count--;
                return EditResult.Done;
            }
            case EntityEditHandle eh:
                return ElemDeleteEntity(el, eh);
            default:
                return EditResult.NotSupported;
        }
    }

    private static EditResult ElemDeleteEntity(ElementLevel el, EntityEditHandle eh)
    {
        var sec = eh.Sec;
        if (!sec.Opaque || eh.RecIndex < 0) return EditResult.NothingSelected;
        var (heads, ok) = EntityFraming.SplitBlock(sec.Block, 0, sec.Count, sec.Block.Length);
        if (!ok || heads.Count != sec.Count || eh.RecIndex >= heads.Count) return EditResult.Failed;
        if (sec.Count == 0) return EditResult.NothingSelected;

        int start = heads[eh.RecIndex].RecStart;
        int end = eh.RecIndex + 1 < heads.Count ? heads[eh.RecIndex + 1].RecStart : sec.Block.Length;
        if (eh.RecIndex + 1 < heads.Count)
        {
            // Fold the removed record's delta into the next record's head so later absolute
            // positions are preserved (entities chain by whole world-unit ints).
            var nx = heads[eh.RecIndex + 1];
            int rx = ReadI32(sec.Block, heads[eh.RecIndex].XyOff);
            int ry = ReadI32(sec.Block, heads[eh.RecIndex].XyOff + 4);
            WriteI32(sec.Block, nx.XyOff, ReadI32(sec.Block, nx.XyOff) + rx);
            WriteI32(sec.Block, nx.XyOff + 4, ReadI32(sec.Block, nx.XyOff + 4) + ry);
            // Re-walk after the in-place delta change (the heads list is still valid byte-wise).
            (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
            if (!ok || heads.Count != sec.Count) return EditResult.Failed;
            start = heads[eh.RecIndex].RecStart;
            end = eh.RecIndex + 1 < heads.Count ? heads[eh.RecIndex + 1].RecStart : sec.Block.Length;
        }

        var nb = new byte[sec.Block.Length - (end - start)];
        Buffer.BlockCopy(sec.Block, 0, nb, 0, start);
        Buffer.BlockCopy(sec.Block, end, nb, start, sec.Block.Length - end);

        var (heads2, ok2) = EntityFraming.SplitRecords(nb, 0, sec.Count - 1, el.Game);
        if (!ok2 || heads2.Count != sec.Count - 1) return EditResult.Failed;

        sec.Block = nb;
        sec.Count--;
        return EditResult.Done;
    }

    // --------------------------------------------- element byte helpers --

    // Wall/area record head: f4 leads with the four doubles (x,y,w,l); f5 too (Wall/Snow), while
    // Area leads with a name string then the doubles. Re-derive the x/y double offsets the same
    // way SceneProjector.GeoReader does, then add the offset to both.
    private static bool TryOffsetShapePos(byte[] rec, bool isWall, double dx, double dy)
    {
        int p = 0;
        if (!isWall)
        {
            // area: name string first
            if (p + 4 > rec.Length) return false;
            int n = ReadI32(rec, p); p += 4;
            if (n < 0 || p + n > rec.Length) return false;
            p += n;
        }
        // now four doubles: x, y, w, l
        if (p + 16 > rec.Length) return false;
        double x = BitConverter.ToDouble(rec, p) + dx;
        double y = BitConverter.ToDouble(rec, p + 8) + dy;
        BitConverter.GetBytes(x).CopyTo(rec, p);
        BitConverter.GetBytes(y).CopyTo(rec, p + 8);
        return true;
    }

    // Accumulated deco-chain centiunits after processing records[0..upto] (inclusive).
    private static (double X, double Y) DecoChainAt(List<byte[]> recs, int upto)
    {
        double x = 0, y = 0;
        for (int i = 0; i <= upto && i < recs.Count; i++)
        {
            int hl = DecoHeadLen(recs[i]);
            if (hl < 0 || hl + 8 > recs[i].Length) return (x, y);
            x += ReadI32(recs[i], hl);
            y += ReadI32(recs[i], hl + 4);
        }
        return (x, y);
    }

    private static string? ResolveElemDecoName(ElementLevel el, Section sec, int upTo)
    {
        string? last = null;
        var tiles = el.TileTypes;
        for (int i = 0; i <= upTo && i < sec.Records.Count; i++)
        {
            var rec = sec.Records[i];
            if (rec.Length < 1) continue;
            string? nm = null;
            switch (rec[0])
            {
                case 0x02:
                    if (rec.Length >= 5)
                    {
                        int n = ReadI32(rec, 1);
                        if (n >= 0 && 5L + n <= rec.Length)
                        {
                            int end = 5;
                            while (end < 5 + n && rec[end] != 0) end++;
                            nm = System.Text.Encoding.Latin1.GetString(rec, 5, end - 5);
                        }
                    }
                    break;
                case 0x01:
                    if (rec.Length >= 5)
                    {
                        int cell = ReadI32(rec, 1);
                        if (cell >= 0 && cell < tiles.Count) nm = tiles[cell];
                    }
                    break;
                default:
                    nm = last;
                    break;
            }
            if (nm is not null) last = nm;
        }
        return last;
    }

    // Decoration record head length (identical layout in tb and f4/f5): 1 (type 0),
    // 5 (type 1 int32 cell) or 1+4+nameLen (type 2 inline). -1 when not credible.
    private static int DecoHeadLen(byte[] rec)
    {
        if (rec.Length < 1) return -1;
        int type = rec[0];
        if (type == 0x00) return 1;
        if (type == 0x01) return rec.Length >= 5 ? 5 : -1;
        if (type == 0x02)
        {
            if (rec.Length < 5) return -1;
            int n = ReadI32(rec, 1);
            if (n < 0 || 5L + n > rec.Length) return -1;
            return 5 + n;
        }
        return -1;
    }

    private static (int li, int si) FindSection(ElementLevel el, Section sec)
    {
        for (int li = 0; li < el.Layers.Count; li++)
        {
            var layer = el.Layers[li];
            for (int si = 0; si < layer.Count; si++)
                if (ReferenceEquals(layer[si], sec)) return (li, si);
        }
        return (-1, -1);
    }

    // Wall/area section kinds per schema index (SceneProjector.ElemIdx ordering).
    private static string KindOfWallArea(int si) => si switch
    {
        0 => "wall",       // f4 wall=0 / f5 wall=0 (snow=1 shares the wall layout)
        1 => "wall",       // f5 snow section is a wall-layout record
        2 => "area",       // f5 area=2
        _ => "area",
    };

    private static PointDInt AbsOf(List<EntityHead> heads, int i)
    {
        int x = 0, y = 0;
        for (int k = 0; k <= i && k < heads.Count; k++) { x += heads[k].X; y += heads[k].Y; }
        return new PointDInt(x, y);
    }

    private readonly record struct PointDInt(int X, int Y);

    private static int ReadI32(byte[] b, int off) => BitConverter.ToInt32(b, off);
    private static void WriteI32(byte[] b, int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
}
