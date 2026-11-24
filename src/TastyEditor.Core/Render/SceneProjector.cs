using TastyEditor.Core.Levels;
using TastyEditor.Core.Streams;

namespace TastyEditor.Core.Render;

/// <summary>Projects a decoded model into world-space display primitives. Read-only
/// over the model, so it can never disturb the byte-exact save path. Mirrors the
/// Python editor's item geometry: a wall/area polygon is the shape's normalised
/// vertices scaled by the record's <c>width</c> and translated to <c>(pos_x, pos_y)</c>
/// (tp2editor.PolygonItem.rebuild), paths are their spline points, entities a marker
/// at their world position. tb/tp2 are already semantic; f4/f5 carry raw spans so a
/// display-only reader re-walks the fields the schema recorders describe.</summary>
public static class SceneProjector
{
    public static SceneGeometry Project(object model)
    {
        var g = model switch
        {
            TbLevel tb => ProjectTb(tb),
            Tp2Level tp => ProjectTp2(tp),
            ElementLevel el => ProjectElement(el),
            _ => new SceneGeometry(),
        };
        // The cross list stays in RECORD order (the gates and the inspector index it as "the n-th
        // record"); the viewport draws SceneGeometry.PaintOrder(), the zValue sort documented there.
        g.AssignIds();
        return g;
    }

    /// <summary>Draw order of one decoration, from the reference editor's QGraphicsItem zValue
    /// (<c>tbeditor.DecoItem</c> / <c>tp2editor</c>: <c>layer*1000 + priority*0.001</c>). The priority
    /// is the record's CHAINED absolute value, and the 0.001 keeps it strictly below the next layer's
    /// base (observed range ~ -700..2 731), so Qt orders layer first, then priority.</summary>
    private static double ZDeco(int layer, int priority) => layer * 1000.0 + priority * 0.001;

    /// <summary>The same formula for an entity, offset by 1000 so that inside one layer every entity
    /// draws OVER every decoration in that layer - the oracle's <c>1000 + layer*1000 + p*0.001</c>.
    /// That is how the shipped game hides TP2's dirt seams, so it has to be reproduced.</summary>
    private static double ZEntity(int layer, int priority) => 1000.0 + layer * 1000.0 + priority * 0.001;

    /// <summary>TP2 entities are positioned on a coarser grid than decos: the codec stores
    /// <c>Position</c> in hundredths (decode <c>lx*0.01</c>, encode <c>Round(Position*100)</c>)
    /// and the viewport draws them at world <c>Position * Tp2EntityK</c> (matches the <c>EntityK</c>
    /// in <c>ProjectTp2</c>). Every position edit must go through this factor so the inspector read
    /// and a drag stay consistent with what is drawn (a raw world delta here overshot 100×).</summary>
    private const double Tp2EntityK = 100.0;

    /// <summary>The current world position of a movable <see cref="SceneCross.Source"/>,
    /// or null when the source is not a typed position the inspector may edit. The
    /// inspector reads through this so it never touches the model directly — the only
    /// editable lanes are ones the codec re-derives losslessly on save.</summary>
    public static PointD? GetPos(object? source) => source switch
    {
        TbEntity e => new PointD(e.PosX, e.PosY),
        Tp2Entity e => new PointD(e.Position[0] * Tp2EntityK, e.Position[1] * Tp2EntityK),
        Tp2Deco d => new PointD(d.Position[0], d.Position[1]),
        TbDeco d => new PointD(d.PosX, d.PosY),
        DecoEditHandle h => ElemPosPt(h),
        EntityEditHandle eh => ElemEntPt(eh),
        _ => null,
    };

    private static PointD ElemPosPt(DecoEditHandle h)
    {
        var a = ElemEdit.DecoAbsolute(h);
        return new PointD(a.X, a.Y);
    }

    private static PointD ElemEntPt(EntityEditHandle eh)
    {
        var a = ElemEdit.EntityAbsolute(eh);
        return new PointD(a.X, a.Y);
    }

    /// <summary>Set a movable source's absolute world position (paired with
    /// <see cref="GetPos"/> for the inspector's X / Y fields). Typed entities / TP2 decos write
    /// their typed field (the codec re-derives bytes); Tasty Blue and ElementEngine decorations
    /// splice their chained int32 deltas in place (TbEdit / ElemEdit).</summary>
    public static bool TrySetPos(object? source, double x, double y)
    {
        switch (source)
        {
            case TbEntity e: e.PosX = x; e.PosY = y; return true;
            case Tp2Entity e: e.Position[0] = x / Tp2EntityK; e.Position[1] = y / Tp2EntityK; return true;
            case Tp2Deco d: d.Position[0] = x; d.Position[1] = y; return true;
            case TbDeco d: return TbEdit.SetDecoPos(d, x, y);
            case DecoEditHandle h: return ElemEdit.SetDecoPos(h, x, y);
            case EntityEditHandle eh: return ElemEdit.SetEntityPos(eh, x, y);
            default: return false;
        }
    }

    /// <summary>True when a projected marker's <c>Source</c> is a record whose world position can
    /// be dragged / typed. Typed entities and TP2 decos are re-derived by the codec on encode;
    /// Tasty Blue decos (<see cref="TbDeco"/>) and ElementEngine decos (<see cref="DecoEditHandle"/>)
    /// are edited by an in-place int32 splice of their chained deltas (TbEdit / ElemEdit), and an
    /// ElementEngine <em>entity</em> (<see cref="EntityEditHandle"/>) is spliced inside its section's
    /// opaque block the same way (ElemEdit.MoveEntity).</summary>
    public static bool IsMovable(object? source) =>
        source is TbEntity or Tp2Entity or Tp2Deco or TbDeco or DecoEditHandle or EntityEditHandle;

    // ------------------------------------------------- polygon (wall/area) editing --
    // These records are re-serialised from their fields on encode, so a vertex edit
    // (count preserved) re-emits in place. A shape's vertices are stored normalised;
    // world = pos + v * width, matching AddShapePolys / the Python rebuild.

    private static bool TryGetPoly(object? src, int si, out double px, out double py, out double w, out double[][]? verts)
    {
        verts = null; px = py = 0; w = 1;
        switch (src)
        {
            case TbWall wl when si >= 0 && si < wl.Shapes.Count && !wl.Shapes[si].IsCircle:
                px = wl.PosX; py = wl.PosY; w = wl.Width; verts = wl.Shapes[si].Vertices; break;
            case TbDefinedArea ar when si >= 0 && si < ar.Shapes.Count && !ar.Shapes[si].IsCircle:
                px = ar.PosX; py = ar.PosY; w = ar.Width; verts = ar.Shapes[si].Vertices; break;
            case Tp2Wall wl when si >= 0 && si < wl.Shapes.Count && !wl.Shapes[si].IsCircle:
                px = wl.PosX; py = wl.PosY; w = wl.Width; verts = wl.Shapes[si].Vertices; break;
            default: return false;
        }
        if (verts is null || verts.Length < 2) return false;
        if (w == 0) w = 1;
        return true;
    }

    /// <summary>True when <paramref name="src"/> is a field-editable polygon record and
    /// shape <paramref name="si"/> is a polygon (not a circle) with at least two vertices,
    /// or <paramref name="src"/> is an axis-aligned water box (edited as a 4-corner rect),
    /// or an element (f4/f5) raw-span wall/area with a well-formed first polygon.</summary>
    public static bool IsEditablePoly(object? src, int si) =>
        src is TbWater || (src is ElemShapeEdit ee && ee.Ok)
        || TryGetPoly(src, si, out _, out _, out _, out _);

    /// <summary>A record's vertex count for shape <paramref name="si"/> (4 for a water box),
    /// or -1 if not editable.</summary>
    public static int PolyVertexCount(object? src, int si) =>
        src is TbWater ? 4
        : src is ElemShapeEdit ee ? (ee.Ok ? ee.VertCount : -1)
        : TryGetPoly(src, si, out _, out _, out _, out var v) ? v!.Length : -1;

    /// <summary>The live world-space points of an editable polygon or water rect, read from
    /// the model (so it reflects any prior edits). Empty if the record is not editable.</summary>
    public static List<PointD> PolyWorldPoints(object? src, int si)
    {
        if (src is TbWater wt) return WaterCorners(wt);
        if (src is ElemShapeEdit ee) return ElemWorldPoints(ee);
        var list = new List<PointD>();
        if (!TryGetPoly(src, si, out var px, out var py, out var w, out var verts)) return list;
        foreach (var v in verts!) list.Add(new PointD(px + v[0] * w, py + v[1] * w));
        return list;
    }

    /// <summary>Move vertex <paramref name="vi"/> of an editable polygon to a world point; for
    /// a water box, <paramref name="vi"/> is a corner and dragging it resizes the rect with
    /// the opposite corner held fixed.</summary>
    public static bool SetPolyVertexWorld(object? src, int si, int vi, PointD world)
    {
        if (src is TbWater wt)
        {
            if (vi < 0 || vi > 3) return false;
            var cs = WaterCorners(wt);
            var anchor = cs[(vi + 2) % 4];
            double minX = Math.Min(world.X, anchor.X), maxX = Math.Max(world.X, anchor.X);
            double minY = Math.Min(world.Y, anchor.Y), maxY = Math.Max(world.Y, anchor.Y);
            WaterSetRect(wt, (minX + maxX) / 2, (minY + maxY) / 2, maxX - minX, maxY - minY);
            return true;
        }
        if (src is ElemShapeEdit ee) return ElemSetVertex(ee, vi, world);
        if (!TryGetPoly(src, si, out var px, out var py, out var w, out var verts)) return false;
        if (vi < 0 || vi >= verts!.Length) return false;
        verts[vi][0] = (world.X - px) / w;
        verts[vi][1] = (world.Y - py) / w;
        return true;
    }

    /// <summary>Translate a whole wall/area/water record by a world delta. For a wall/area
    /// this shifts its position (every owned shape follows); for a water box it shifts only
    /// the centre (min/max are local half-extents, so two doubles change).</summary>
    public static bool MoveRecord(object? src, double dx, double dy)
    {
        switch (src)
        {
            case TbWall wl: wl.PosX += dx; wl.PosY += dy; return true;
            case TbDefinedArea ar: ar.PosX += dx; ar.PosY += dy; return true;
            case Tp2Wall wl: wl.PosX += dx; wl.PosY += dy; return true;
            case TbWater wt: wt.D[5] += dx; wt.D[6] += dy; return true;
            case ElemShapeEdit ee when ee.Ok:
                WriteF64(ee.Rec, ee.PosXOff, ee.PosX + dx);
                WriteF64(ee.Rec, ee.PosYOff, ee.PosY + dy);
                return true;
            default: return false;
        }
    }

    /// <summary>An editable record's byte span in the file, if the model carries it
    /// (tb walls / areas / water do; tp2 does not). Used to assert an edit stayed inside one
    /// record.</summary>
    public static (long Off, long Len) RecordSpan(object? src) => src switch
    {
        TbWall wl when wl.Off >= 0 => (wl.Off, wl.Raw.Length),
        TbDefinedArea ar when ar.Off >= 0 => (ar.Off, ar.Raw.Length),
        TbWater wt when wt.Off >= 0 => (wt.Off, wt.Raw.Length),
        ElemShapeEdit ee when ee.Ok => (ElemFileOffset(ee), ee.Rec.Length),
        _ => (-1, 0),
    };

    /// <summary>Insert a new vertex on the shape edge nearest <paramref name="world"/>, at the
    /// cursor — but only when that edge is within <paramref name="maxEdgeDistance"/> (world
    /// units) of the cursor, so the click must land on an edge. Returns false and changes
    /// NOTHING when the nearest edge is farther than the tolerance (no partial mutation).
    /// Grows the vertex count by one; the codec re-serialises the whole shape from fields, so
    /// the lengthening edit stays lossless everywhere else. <paramref name="insertedAt"/> is
    /// the index the caller can immediately drag.</summary>
    public static bool TryAddPolyVertex(object? src, int si, PointD world, double maxEdgeDistance, out int insertedAt)
    {
        insertedAt = -1;
        if (src is TbWater) return false;   // axis-aligned box: no free vertices to add
        if (src is ElemShapeEdit ee) return ElemAddVertex(ee, world, maxEdgeDistance, out insertedAt);
        if (!TryGetPoly(src, si, out var px, out var py, out var w, out var verts)) return false;
        int n = verts!.Length;
        if (n < 2) return false;

        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var a = new PointD(px + verts[i][0] * w, py + verts[i][1] * w);
            int j = (i + 1) % n;
            var b = new PointD(px + verts[j][0] * w, py + verts[j][1] * w);
            double d = DistPointSeg(world, a, b);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (bestD > maxEdgeDistance) return false;

        var nv = new double[n + 1][];
        for (int i = 0; i <= best; i++) nv[i] = verts[i];
        nv[best + 1] = new double[] { (world.X - px) / w, (world.Y - py) / w };
        for (int i = best + 2; i <= n; i++) nv[i] = verts[i - 1];

        if (!SetPolyVerts(src, si, nv)) return false;
        insertedAt = best + 1;
        return true;
    }

    private static bool SetPolyVerts(object? src, int si, double[][] verts)
    {
        switch (src)
        {
            case TbWall wl when si >= 0 && si < wl.Shapes.Count: wl.Shapes[si].Vertices = verts; return true;
            case TbDefinedArea ar when si >= 0 && si < ar.Shapes.Count: ar.Shapes[si].Vertices = verts; return true;
            case Tp2Wall wl when si >= 0 && si < wl.Shapes.Count: wl.Shapes[si].Vertices = verts; return true;
            default: return false;
        }
    }

    private static double DistPointSeg(PointD p, PointD a, PointD b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double wx = p.X - a.X, wy = p.Y - a.Y;
        double len2 = vx * vx + vy * vy;
        double t = len2 == 0 ? 0 : (wx * vx + wy * vy) / len2;
        t = Math.Clamp(t, 0, 1);
        double cx = p.X - (a.X + t * vx), cy = p.Y - (a.Y + t * vy);
        return Math.Sqrt(cx * cx + cy * cy);
    }

    // ------------------------------- element (f4/f5) raw-span polygon editing --
    // The element model carries no typed vertices, so edits patch doubles directly into
    // the live record byte[] held in Section.Records (which the codec re-emits verbatim).
    // A vertex move preserves the count and so the record length; an add splices 16 bytes
    // and bumps the polygon's own count i32 (records are walk-delimited, not length-
    // prefixed), which grows the file but stays lossless.

    private static void WriteF64(byte[] b, int off, double v) => BitConverter.GetBytes(v).CopyTo(b, off);

    private static List<PointD> ElemWorldPoints(ElemShapeEdit ee)
    {
        var list = new List<PointD>();
        if (!ee.Ok) return list;
        double px = ee.PosX, py = ee.PosY, w = ee.Width;
        for (int i = 0; i < ee.VertCount; i++)
        {
            double vx = BitConverter.ToDouble(ee.Rec, ee.VertsOff + i * 16);
            double vy = BitConverter.ToDouble(ee.Rec, ee.VertsOff + i * 16 + 8);
            list.Add(new PointD(px + vx * w, py + vy * w));
        }
        return list;
    }

    private static bool ElemSetVertex(ElemShapeEdit ee, int vi, PointD world)
    {
        if (!ee.Ok || vi < 0 || vi >= ee.VertCount) return false;
        double px = ee.PosX, py = ee.PosY, w = ee.Width;
        int off = ee.VertsOff + vi * 16;
        WriteF64(ee.Rec, off, (world.X - px) / w);
        WriteF64(ee.Rec, off + 8, (world.Y - py) / w);
        return true;
    }

    private static long ElemFileOffset(ElemShapeEdit ee)
    {
        long off = ee.Sec.CountOff + 4L;
        for (int i = 0; i < ee.RecIndex; i++) off += ee.Sec.Records[i].Length;
        return off;
    }

    private static bool ElemAddVertex(ElemShapeEdit ee, PointD world, double maxEdgeDistance, out int insertedAt)
    {
        insertedAt = -1;
        if (!ee.Ok) return false;
        var pts = ElemWorldPoints(ee);
        int n = pts.Count;
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double d = DistPointSeg(world, pts[i], pts[(i + 1) % n]);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (bestD > maxEdgeDistance) return false;   // off-edge: change nothing

        double px = ee.PosX, py = ee.PosY, w = ee.Width;
        int insertByte = ee.VertsOff + (best + 1) * 16;
        var old = ee.Rec;
        var nv = new byte[old.Length + 16];
        Buffer.BlockCopy(old, 0, nv, 0, insertByte);
        Buffer.BlockCopy(old, insertByte, nv, insertByte + 16, old.Length - insertByte);
        WriteF64(nv, insertByte, (world.X - px) / w);
        WriteF64(nv, insertByte + 8, (world.Y - py) / w);
        int cnt = BitConverter.ToInt32(nv, ee.VertsOff - 4);
        WriteI32(nv, ee.VertsOff - 4, cnt + 1);

        ee.Sec.Records[ee.RecIndex] = nv;
        ee.Rec = nv;
        ee.VertCount = n + 1;
        insertedAt = best + 1;
        return true;
    }

    private static void WriteI32(byte[] b, int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);

    /// <summary>Apply a world-space delta to a movable <see cref="SceneCross.Source"/>.
    /// The viewport mutates the model live; the lossless encoder re-derives the exact
    /// bytes on save. Returns false (and changes nothing) for non-movable sources.</summary>
    public static bool TryMoveEntity(object? source, double dx, double dy)
    {
        switch (source)
        {
            case TbEntity e: e.PosX += dx; e.PosY += dy; return true;
            case Tp2Entity e: e.Position[0] += dx / Tp2EntityK; e.Position[1] += dy / Tp2EntityK; return true;
            case Tp2Deco d: d.Position[0] += dx; d.Position[1] += dy; return true;
            case TbDeco d: return TbEdit.MoveDeco(d, dx, dy);
            case DecoEditHandle h: return ElemEdit.MoveDeco(h, dx, dy);
            case EntityEditHandle eh: return ElemEdit.MoveEntity(eh, dx, dy);
            default: return false;
        }
    }

    // ------------------------------------------------------------- rotation --
    //
    // Decorations and (since the entity slice) entities are rotatable. The stored field is an
    // int32 centidegree value whose sign the renderer negates, so the world degrees surfaced to
    // the UI are <c>-raw / 100</c> (matching <see cref="SceneCross.RotationDeg"/>). TP2 entities
    // re-serialise their typed <c>Rotation</c> on encode, while a Tasty Blue entity's rotation and
    // an ElementEngine entity's live in a verbatim raw span / opaque block that only an in-place
    // int32 splice can change (TbEdit / ElemEdit).

    /// <summary>True when a marker's <c>Source</c> has an editable rotation: a decoration
    /// (Tasty Blue / ElementEngine / TP2) or an entity (Tasty Blue / TP2 / ElementEngine).
    /// A TP2 decoration stores its turn in the <c>size.raw</c> centidegree field, exactly as
    /// the projector draws it (<c>RotationDeg = -SizeRaw/100</c>).</summary>
    public static bool IsRotatable(object? source) =>
        source is TbDeco or DecoEditHandle or TbEntity or Tp2Entity or Tp2Deco or EntityEditHandle;

    /// <summary>The record's current rotation in world degrees, or null when the source has
    /// no editable rotation.</summary>
    public static double? GetRotationDeg(object? source) => source switch
    {
        TbDeco d => -d.Rotation / 100.0,
        DecoEditHandle h => ElemEdit.DecoRotationDeg(h),
        TbEntity e => -e.Rotation / 100.0,
        Tp2Entity e => -e.Rotation / 100.0,
        Tp2Deco d => -d.SizeRaw / 100.0,
        EntityEditHandle eh => ElemEdit.EntityRotationDeg(eh),
        _ => null,
    };

    /// <summary>Set a record's absolute rotation in world degrees (splices or writes the int32
    /// centidegree field). False for a source with no editable rotation.</summary>
    public static bool SetRotationDeg(object? source, double deg) => source switch
    {
        TbDeco d => TbEdit.SetDecoRotationDeg(d, deg),
        DecoEditHandle h => ElemEdit.SetDecoRotationDeg(h, deg),
        TbEntity e => TbEdit.SetEntityRotationDeg(e, deg),
        Tp2Entity e => Tp2SetRot(e, deg),
        Tp2Deco d => Tp2SetDecoRot(d, deg),
        EntityEditHandle eh => ElemEdit.SetEntityRotationDeg(eh, deg),
        _ => false,
    };

    // A TP2 entity's rotation is a typed int32 the codec re-emits, so the edit is a field write.
    private static bool Tp2SetRot(Tp2Entity e, double deg)
    {
        e.Rotation = (int)Math.Round(-deg * 100);
        return true;
    }

    // A TP2 decoration's turn lives in its size.raw centidegree field (the same int32 the
    // projector reads for RotationDeg); the codec re-serialises it, so this is a lossless write.
    private static bool Tp2SetDecoRot(Tp2Deco d, double deg)
    {
        d.SizeRaw = (int)Math.Round(-deg * 100);
        return true;
    }

    /// <summary>Add <paramref name="deltaDeg"/> world degrees to a decoration's current rotation.</summary>
    public static bool NudgeRotationDeg(object? source, double deltaDeg) =>
        GetRotationDeg(source) is double cur && SetRotationDeg(source, cur + deltaDeg);

    // ------------------------------------------------------------------- flip --
    //
    // The two ElementDecoration::setFlipX / setFlipY words of a decoration record's optional block.
    // They are the record's facing, and the shipped editor's own Flip command (LevelEditor::
    // luaFlipDecorationX) is just a byte toggle of them — no angle change, no re-layout. Every
    // decoration family carries them: Tasty Blue and the ElementEngine games in a raw span that
    // needs an in-place splice (and a 24-byte block materialised when a record never had one),
    // TP2 in the typed model its codec re-serialises. Entities have no such field, so an entity
    // source reports not flippable and the inspector row stays hidden.

    /// <summary>True when the marker's <c>Source</c> is a decoration whose facing can be edited.</summary>
    public static bool IsFlippable(object? source) => source is TbDeco or DecoEditHandle or Tp2Deco;

    /// <summary>The record's current flip flags (false, false for anything without the field).</summary>
    public static (bool X, bool Y) GetFlip(object? source) => source switch
    {
        TbDeco d => (d.FlipX, d.FlipY),
        DecoEditHandle h => ElemEdit.DecoFlip(h),
        Tp2Deco d => (d.FlipX, d.FlipY),
        _ => (false, false),
    };

    /// <summary>Write a decoration's flip flags. <paramref name="resized"/> is true when the record
    /// changed byte length (the optional block was created or dropped): in an Forever / Final Bite
    /// decoration section that shifts every later record's offset, so the caller must re-project and
    /// re-select — for a Tasty Blue / TP2 record nothing else moves. False changes no bytes.</summary>
    public static bool TrySetFlip(object? source, bool fx, bool fy, out bool resized)
    {
        switch (source)
        {
            case TbDeco d: return TbEdit.SetDecoFlip(d, fx, fy, out resized);
            case DecoEditHandle h: return ElemEdit.SetDecoFlip(h, fx, fy, out resized);
            case Tp2Deco t: return Tp2Edit.SetDecoFlip(t, fx, fy, out resized);
            default: resized = false; return false;
        }
    }

    // ------------------------------------------------ TP2 field parity (D7) --
    //
    // tp2editor's Inspector edits the typed entity / decoration fields the codec re-serialises.
    // Every one here is either a fixed-width field or a presence-gated block that
    // Tp2Codec.Encode rebuilds from the model, so an edit is length-stable and an untouched
    // level still round-trips byte-exactly (I1). These accessors are the inspector's only
    // bridge to the model — it never touches Tp2Entity / Tp2Deco directly.

    private const double Tp2DimBase = 16000.0;   // deco dimensions.raw -> scale 1.0 (tp2editor.DIM_BASE)

    /// <summary>Read one numeric / boolean TP2 field (bool surfaces as 0 / 1). Null when the
    /// source has no such field.</summary>
    public static double? Tp2ScalarGet(object? s, string key) => s switch
    {
        Tp2Entity e => key switch
        {
            "mass" => e.Mass,
            "prio" => e.Priority,
            "persist" => e.Field158 != 0 ? 1 : 0,
            "emit_on" => e.HasEmitter != 0 ? 1 : 0,
            "emit_count" => e.EmitEndMarker,
            "emit_min" => e.EmitV is { Length: >= 10 } ? e.EmitV[9] : 0,
            "emit_max" => e.EmitV is { Length: >= 11 } ? e.EmitV[10] : 0,
            _ => null,
        },
        Tp2Deco d => key switch
        {
            "dsize" => (d.DimsRaw is { Length: >= 1 } && d.DimsRaw[0] != 0 ? d.DimsRaw[0] : (int)Tp2DimBase) / Tp2DimBase,
            "dprio" => d.PriorityTotal,
            _ => null,
        },
        _ => null,
    };

    /// <summary>Write one TP2 field. The block-presence key <c>emit_on</c> allocates / clears the
    /// emitter with the codec's default layout so Encode stays well-formed. False on an unknown
    /// key or a source that isn't a TP2 record.</summary>
    public static bool Tp2ScalarSet(object? s, string key, double v)
    {
        switch (s)
        {
            case Tp2Entity e:
                switch (key)
                {
                    case "mass": e.Mass = v; return true;
                    case "prio": e.Priority = (int)Math.Round(v); return true;
                    case "persist": e.Field158 = v != 0 ? 1 : 0; return true;
                    case "emit_on": SetEmitter(e, v != 0); return true;
                    case "emit_count": e.EmitEndMarker = (int)Math.Round(v); return true;
                    case "emit_min": e.EmitV = EnsureEmit(e.EmitV); e.EmitV[9] = v; return true;
                    case "emit_max": e.EmitV = EnsureEmit(e.EmitV); e.EmitV[10] = v; return true;
                }
                return false;
            case Tp2Deco d:
                switch (key)
                {
                    case "dsize": return SetDecoScale(d, v);
                    case "dprio": d.PriorityTotal = (int)Math.Round(v); return true;
                }
                return false;
            default: return false;
        }
    }

    private static double[] EnsureEmit(double[] v) => v is { Length: >= 11 } ? v : SeedEmit();

    private static double[] SeedEmit()
    {
        var v = new double[11];
        v[0] = 1.0; v[1] = 1.0; v[9] = 5.0; v[10] = 10.0;   // GENERIC_EMITTER
        return v;
    }

    /// <summary>Toggle an entity's spawner. On, seed the generic emitter block when none exists
    /// (tp2editor.make_emitter). Off, drop the flag and payload. The codec writes reserved /
    /// end_marker only on layers >= 3, exactly as shipped data does, so the edit stays faithful.</summary>
    private static void SetEmitter(Tp2Entity e, bool on)
    {
        if (on)
        {
            e.HasEmitter = 1;
            if (e.EmitV is not { Length: >= 11 }) e.EmitV = SeedEmit();
            if (e.EmitEndMarker == 0) e.EmitEndMarker = 5;
        }
        else
        {
            e.HasEmitter = 0;
            e.EmitV = Array.Empty<double>();
            e.EmitReserved = 0; e.EmitEndMarker = 0;
        }
    }

    /// <summary>Uniform decoration size scale: dims[0] = round(scale*DIM_BASE), dims[1] scaled by
    /// the original aspect so the tile keeps its shape (tp2editor._edit deco branch).</summary>
    private static bool SetDecoScale(Tp2Deco d, double scale)
    {
        if (d.DimsRaw is not { Length: >= 2 }) d.DimsRaw = new int[2];
        int ox = d.DimsRaw[0] != 0 ? d.DimsRaw[0] : (int)Tp2DimBase;
        int oy = d.DimsRaw[1] != 0 ? d.DimsRaw[1] : (int)Tp2DimBase;
        double aspect = (double)oy / ox;
        int nx = (int)Math.Round(scale * Tp2DimBase);
        if (nx == 0) nx = 1;
        d.DimsRaw[0] = nx;
        d.DimsRaw[1] = (int)Math.Round(nx * aspect);
        return true;
    }

    /// <summary>The record's current colour (RGBA) for the inspector swatch, or null.</summary>
    public static byte[]? Tp2ColorGet(object? s) => s switch
    {
        Tp2Entity e => e.ColorRgba is { Length: >= 4 } ? e.ColorRgba : null,
        Tp2Deco d => d.ColorRgba is { Length: >= 4 } ? d.ColorRgba : null,
        _ => null,
    };

    /// <summary>Write a TP2 entity / decoration colour (all four channels; the codec re-emits
    /// the 4 raw bytes verbatim).</summary>
    public static bool Tp2ColorSet(object? s, byte r, byte g, byte b, byte a)
    {
        var c = new[] { r, g, b, a };
        switch (s)
        {
            case Tp2Entity e: e.ColorRgba = c; return true;
            case Tp2Deco d: d.ColorRgba = c; return true;
            default: return false;
        }
    }

    /// <summary>The path name an entity currently follows, or null when it has no follow block.</summary>
    public static string? Tp2PathGet(object? s) =>
        s is Tp2Entity e && e.HasPathFollow == 1 ? e.PathName : null;

    /// <summary>Assign / clear a path-follow block. On, seed the generic follow params
    /// (tp2editor.GENERIC_PATH_FOLLOW) and, when the named path exists in this layer, respawn
    /// the entity at its first spline anchor (world = anchor, stored in hundredths like the
    /// codec). Off, drop the flag and name. The codec re-emits the whole block from these fields,
    /// so a 0->1 toggle stays byte-lossless.</summary>
    public static bool Tp2PathSet(object? s, Tp2Level? tp, int layer, string? name)
    {
        if (s is not Tp2Entity e) return false;
        if (string.IsNullOrEmpty(name))
        {
            e.HasPathFollow = 0; e.PathName = null; return true;
        }
        e.HasPathFollow = 1;
        e.Pv0 = 0.55; e.Pv1 = 0.55; e.PFlag0 = 0;
        e.Pv2 = 0.5; e.Pv3 = 2.0; e.PFlag1 = 0; e.PMode = 1; e.Pv4 = 1.0;
        e.PathName = name;
        if (tp is not null && layer >= 0 && layer < tp.Layers.Count)
        {
            Tp2Path? found = null;
            foreach (var pp in tp.Layers[layer].Paths)
                if (pp.PathName == name) { found = pp; break; }
            if (found is not null)
            {
                double[]? start = found.SplinePoints is { Length: > 0 } sp ? sp[0] : null;
                if (start is null) start = new[] { found.PosX, found.PosY };
                if (start.Length >= 2)
                {
                    e.Position[0] = start[0] / Tp2EntityK;
                    e.Position[1] = start[1] / Tp2EntityK;
                }
            }
        }
        return true;
    }

    // ------------------------------------------- unified record-field parity (all games) --
    //
    // One inspector facade over the four families. TP2 routes to the typed accessors above; the
    // raw-span games (Tasty Blue, Forever, Final Bite) route to the fixed-width byte patches in
    // TbEdit / ElemEdit. Every key maps to a length-preserving edit, so the same five-joint lane
    // (model source -> accessor -> inspector row -> MainWindow apply -> coalesced commit) drives
    // all four games. Keys: "mass", "prio" (entity); "dsize" (deco); plus TP2-only "persist",
    // "emit_*", "dprio". Colour is a separate RGBA channel set.

    public static bool IsFieldEntity(object? s) => s is Tp2Entity or TbEntity or EntityEditHandle;
    public static bool IsFieldDeco(object? s) => s is Tp2Deco or TbDeco or DecoEditHandle;
    public static bool IsFieldSource(object? s) => IsFieldEntity(s) || IsFieldDeco(s);

    /// <summary>The path-follow / emitter / persist behaviour blocks are only rewritable on TP2's
    /// typed re-serialiser; the raw-span families carry them in a per-prefab variable tail (the
    /// oracle's <c>entity_full_body</c> gate), so the inspector hides those rows for them.</summary>
    public static bool SupportsBehaviour(object? s) => s is Tp2Entity;
    /// <summary>Per-decoration priority is only surfaced on TP2 (its codec stores a typed
    /// PriorityTotal); the other families carry a delta the oracle does not expose.</summary>
    public static bool SupportsDecoPriority(object? s) => s is Tp2Deco;

    public static double? FieldGet(object? s, string key) => s switch
    {
        Tp2Entity or Tp2Deco => Tp2ScalarGet(s, key),
        TbEntity e => key switch { "mass" => TbEdit.EntityMass(e), "prio" => TbEdit.EntityPriority(e), _ => null },
        TbDeco d => key == "dsize" ? TbEdit.DecoSize(d) : null,
        EntityEditHandle eh => key switch { "mass" => ElemEdit.EntityMass(eh), "prio" => ElemEdit.EntityPriority(eh), _ => null },
        DecoEditHandle dh => key == "dsize" ? ElemEdit.DecoSize(dh) : null,
        _ => null,
    };

    public static bool FieldSet(object? s, string key, double v) => s switch
    {
        Tp2Entity or Tp2Deco => Tp2ScalarSet(s, key, v),
        TbEntity e => key switch { "mass" => TbEdit.SetEntityMass(e, v), "prio" => TbEdit.SetEntityPriority(e, (int)Math.Round(v)), _ => false },
        TbDeco d => key == "dsize" && TbEdit.SetDecoSize(d, v),
        EntityEditHandle eh => key switch { "mass" => ElemEdit.SetEntityMass(eh, v), "prio" => ElemEdit.SetEntityPriority(eh, (int)Math.Round(v)), _ => false },
        DecoEditHandle dh => key == "dsize" && ElemEdit.SetDecoSize(dh, v),
        _ => false,
    };

    public static byte[]? FieldColorGet(object? s) => s switch
    {
        Tp2Entity or Tp2Deco => Tp2ColorGet(s),
        TbEntity e => TbEdit.EntityColor(e),
        TbDeco d => TbEdit.DecoColor(d),
        EntityEditHandle eh => ElemEdit.EntityColor(eh),
        DecoEditHandle dh => ElemEdit.DecoColor(dh),
        _ => null,
    };

    public static bool FieldColorSet(object? s, byte r, byte g, byte b, byte a) => s switch
    {
        Tp2Entity or Tp2Deco => Tp2ColorSet(s, r, g, b, a),
        TbEntity e => TbEdit.SetEntityColor(e, r, g, b, a),
        TbDeco d => TbEdit.SetDecoColor(d, r, g, b, a),
        EntityEditHandle eh => ElemEdit.SetEntityColor(eh, r, g, b, a),
        DecoEditHandle dh => ElemEdit.SetDecoColor(dh, r, g, b, a),
        _ => false,
    };

    /// <summary>The record's raw <c>dimensions.raw[0]</c> int (deco sprite scale), used to refresh
    /// a decoration cross's render input after a size edit — <c>null</c> for non-deco sources.</summary>
    public static int? FieldDecoDim0(object? s) => s switch
    {
        Tp2Deco d => d.DimsRaw is { Length: >= 1 } && d.DimsRaw[0] != 0 ? d.DimsRaw[0] : (int)Tp2DimBase,
        TbDeco d => (int?)d.DimW,
        DecoEditHandle dh => ElemEdit.DecoSize(dh) is double sc ? (int)Math.Round(sc * Tp2DimBase) : null,
        _ => null,
    };

    // --------------------------------------- named behaviour-tail view (all games) ---
    //
    // Everything an entity record carries after the priority int32 is a real, named engine value: each
    // shipped build's own <c>ElementEntity::readBinary</c> was decompiled and turned into the per-game
    // grammar in <see cref="TailSem"/>, so the inspector shows named fields and never a raw byte lane.
    // Values commit two ways. A fixed-width scalar goes through the length-preserving lane write, clamped
    // to the record's own byte span, so it can never resize the record or bleed into the next one. A
    // STRUCTURAL field — a block's presence gate, an embedded engine name, the cell-colour ramp count —
    // changes the record's length by definition, so it rebuilds the tail and is only committed after the
    // framing oracle re-accepts it (see <see cref="TailEdit"/> and each family's ReshapeTail). TP2 has
    // typed named behaviour instead and needs none of this.

    /// <summary>True for the entity families whose behaviour tail is field-editable.</summary>
    public static bool IsTailEditable(object? s) => s is TbEntity or EntityEditHandle;

    // Resolve (buffer, absolute tail base, tail length) across the raw families. Shared by the
    // read (FieldTailBytes) and write (FieldLaneSet*) paths so bounds stay identical.
    private static bool TryTailSpan(object? s, out byte[] buf, out int baseAbs, out int len)
    {
        switch (s)
        {
            case TbEntity te: return TbEdit.EntityTailSpan(te, out buf, out baseAbs, out len);
            case EntityEditHandle eh: return ElemEdit.EntityTailSpan(eh, out buf, out baseAbs, out len);
            default: buf = Array.Empty<byte>(); baseAbs = len = -1; return false;
        }
    }

    /// <summary>A copy of an entity record's behaviour-tail bytes, or <c>null</c> for a source without
    /// one (TP2, decorations, unaddressable bodies).</summary>
    public static byte[]? FieldTailBytes(object? s)
    {
        if (!TryTailSpan(s, out var buf, out int b, out int len)) return null;
        var t = new byte[len];
        Array.Copy(buf, b, t, 0, len);
        return t;
    }

    /// <summary>How far past a record's own byte span the tail walk may read while proving the framing,
    /// looking for the next credible record head. A path / area name is at most 64 bytes and the widest
    /// block body is a few hundred, so 1 KiB covers any single mis-cut and nothing realistic beyond it.</summary>
    public const int TailLookAhead = 1024;

    /// <summary>The named view of an entity's behaviour tail: every byte after the priority int32,
    /// decoded as the <see cref="TailField"/>s that <em>that game's own</em> <c>ElementEntity::readBinary</c>
    /// reads (the per-game grammar lives in <see cref="TailSem"/>). Returns the fields, how many tail
    /// bytes the walk consumed, the tail bytes it was run over and the writable frame
    /// (<see cref="FrameLen"/>). <c>null</c> for a source with no entity tail (TP2, decorations). There
    /// is no raw fallback view — a byte is either a named engine field or the walk reports the gap so it
    /// can be chased down in the decompile.</summary>
    public static (List<TailField> Fields, int Consumed, byte[] Tail, int FrameLen)? FieldTailNamed(object? s)
    {
        if (!TryTailSpan(s, out var buf, out int b, out int frame)) return null;
        if (frame < 4) return null;
        string g = TailGame(s);

        var limited = new byte[frame];
        Array.Copy(buf, b, limited, 0, frame);
        var (fields, consumed) = TailSem.Decode(limited, g);
        if (consumed >= frame) return (fields, consumed, limited, frame);

        // The walk and the framing splitter can disagree, and the splitter can be wrong in EITHER
        // direction: its head test (the Python oracle's head_at) matches any
        // <i32 len><name_with_underscore\0><i32 0-or-1> run, which is exactly what a Path Follow /
        // Random Area block's `pathname` / `definedareaname` is.
        //
        //   • frame too SHORT — the walk runs out of bytes mid-block. Give it look-ahead; if the wider
        //     walk finishes and closes on a credible head, this record's tail really is that long.
        //   • frame too LONG — the walk finishes a full seven-gate grammar early and the bytes after it
        //     are the NEXT record, with a head sitting exactly where the grammar stopped.
        //
        // Both are accepted only on that head proof (or on running out of block), never on trust; any
        // other stop keeps the residual visible as an open question instead of papering over it with a
        // hex lane. The writable frame stays capped at what the splitter reserved, so a field recovered
        // past it is shown by name but refused by the span-clamped writer (I1/I3).
        int win = Math.Min(buf.Length - b, frame + TailLookAhead);
        List<TailField> wf = fields;
        int wc = consumed;
        if (win > frame)
        {
            var wide = new byte[win];
            Array.Copy(buf, b, wide, 0, win);
            (wf, wc) = TailSem.Decode(wide, g);
        }
        foreach (int cand in new[] { wc, consumed })
        {
            if (cand <= 0 || cand > win || cand == frame) continue;
            if (b + cand < buf.Length && EntityFraming.HeadAt(buf, b + cand, buf.Length) is null) continue;
            var trim = new byte[cand];
            Array.Copy(buf, b, trim, 0, cand);
            return (cand == wc ? wf : fields, cand, trim, Math.Min(cand, frame));
        }
        return (fields, consumed, limited, frame);
    }

    /// <summary>Diagnostic for the <c>taildump</c> probe: how many bytes the shared section block
    /// actually holds from the tail base on. Equal to the frame for the LAST record of a block (no
    /// look-ahead exists), greater otherwise. -1 when the source has no tail.</summary>
    public static int FieldTailAvailable(object? s)
        => TryTailSpan(s, out var buf, out int b, out _) ? buf.Length - b : -1;

    // Whose readBinary wrote this record: the ElementEngine builds carry their game key on the decoded
    // level, Tasty Blue is its own codec. Keeps the app from branching on game (all format knowledge
    // stays in Core) while letting the tail view follow the right decompile.
    private static string TailGame(object? s) => s switch
    {
        EntityEditHandle eh => eh.Lv.Game,
        TbEntity => "tb",
        _ => "f5",
    };

    /// <summary>Grammar key (<c>tb</c> / <c>f4</c> / <c>f5</c>) whose <c>readBinary</c> wrote this record,
    /// for callers that have to size a block (the <c>tailedit</c> proof). The app never hard-codes a game.
    /// </summary>
    public static string TailGameKey(object? s) => TailGame(s);

    /// <summary>Shipped-build name for the inspector's wording, so the app never has to know the
    /// internal grammar keys.</summary>
    public static string TailGameName(object? s) => TailGame(s) switch
    {
        "tb" => "Tasty Blue",
        "f4" => "Tasty Planet Forever",
        _ => "Final Bite",
    };

    /// <summary>Write an int32 lane at <paramref name="laneOff"/> (relative to the tail start) in
    /// place. Refused (false) for a non-tail source, or when the word would run past the tail span —
    /// so it can never resize the record or touch the next one (I1/I3).</summary>
    public static bool FieldLaneSetInt32(object? s, int laneOff, int v)
        => TryTailSpan(s, out var buf, out int b, out int len)
           && laneOff >= 0 && laneOff + 4 <= len
           && EntityFraming.WriteLaneInt(buf, b + laneOff, v);

    /// <summary>Write a float64 lane at <paramref name="laneOff"/> (relative to the tail start) in
    /// place. Refused (false) for a non-tail source, or when the 8 bytes would run past the tail span.</summary>
    public static bool FieldLaneSetF64(object? s, int laneOff, double v)
        => TryTailSpan(s, out var buf, out int b, out int len)
           && laneOff >= 0 && laneOff + 8 <= len
           && EntityFraming.WriteLaneF64(buf, b + laneOff, v);

    /// <summary>Write a float32 lane at <paramref name="laneOff"/> (relative to the tail start) in
    /// place. Refused (false) for a non-tail source, or when the 4 bytes would run past the tail span.</summary>
    public static bool FieldLaneSetF32(object? s, int laneOff, float v)
        => TryTailSpan(s, out var buf, out int b, out int len)
           && laneOff >= 0 && laneOff + 4 <= len
           && EntityFraming.WriteLaneF32(buf, b + laneOff, v);

    /// <summary>Write a decoded named tail field in place, dispatching on its <see cref="TailKind"/>.
    /// Only value kinds (int / f64 / f32 / bool) are writable; gates / strings / counts are structural
    /// and rejected (they would resize the record). Bounds are re-checked by the lane setter, so this
    /// can never spill past the tail span (I1/I3).</summary>
    public static bool FieldTailSet(object? s, TailField f, double v) => f.Kind switch
    {
        TailKind.Int or TailKind.Bool => FieldLaneSetInt32(s, f.LaneOff, (int)Math.Round(v)),
        TailKind.F64 => FieldLaneSetF64(s, f.LaneOff, v),
        TailKind.F32 => FieldLaneSetF32(s, f.LaneOff, (float)v),
        _ => false,
    };

    /// <summary>Apply a STRUCTURAL (length-changing) edit to one tail field of an entity record: a
    /// <see cref="bool"/> for a <see cref="TailKind.Gate"/> (turn that behaviour block on / off), a
    /// <see cref="string"/> for a <see cref="TailKind.Str"/> (rename the path / defined area / point list
    /// / scriptable / linked entity the block names), an <see cref="int"/> for a
    /// <see cref="TailKind.Count"/> (resize the IndexedCellColours ramp). The tail is rebuilt by
    /// <see cref="TailEdit"/>, then committed only if the family's own framing oracle still accepts the
    /// record — an f4/f5 section block must still split to its authoritative record count with every
    /// record keeping its name, a tb record must still measure to its end. False means nothing was
    /// written, so a refused edit cannot corrupt the file (I1/I3). After a true the caller must re-read
    /// the tail view, since the record changed size.</summary>
    public static bool FieldTailReshape(object? s, TailField f, object? value) => s switch
    {
        TbEntity te => TbEdit.ReshapeTail(te, f, value),
        EntityEditHandle eh => ElemEdit.ReshapeTail(eh, f, value),
        _ => false,
    };

    /// <summary>Why a structural tail edit was refused, in the game's own terms ("" when it applies) — for
    /// the inspector's status line, so a change that would corrupt the record says what the loading code
    /// objects to instead of silently snapping the row back. Call it AFTER <see cref="FieldTailReshape"/>
    /// returned false: it re-runs the same guarded write, which on that path cannot commit anything.</summary>
    public static string FieldTailWhy(object? s, TailField f, object? value) => s switch
    {
        TbEntity te => TbEdit.ReshapeTail(te, f, value, out string w) ? "" : w,
        EntityEditHandle eh => ElemEdit.ReshapeTail(eh, f, value, out string w) ? "" : w,
        _ => "this object has no behaviour tail",
    };

    /// <summary>Re-derive a cross's render inputs (sprite scale, deco world box, colour, fade) from
    /// its source after a field/colour edit, so the viewport updates in place without a full
    /// re-Show. Mirrors exactly what each family's Project* path sets at build time (TP2 and the
    /// tb entity carry no alpha fade; the raw-span decos and the ElementEngine records do). The
    /// edit lane reads the patched source back through the facade so it works for typed and
    /// byte-spliced families alike. Untouched for non-field sources.</summary>
    public static void RefreshRender(SceneCross cr)
    {
        switch (cr.Source)
        {
            case Tp2Entity e:
                cr.Mass = e.Mass;
                cr.ColorRgba = e.ColorRgba;
                break;
            case Tp2Deco d:
                int dx = d.DimsRaw.Length > 0 ? d.DimsRaw[0] : 0;
                int dy = d.DimsRaw.Length > 1 ? d.DimsRaw[1] : 0;
                cr.Mass = dx;
                cr.DecoWorldW = (dx != 0 ? dx : 16000) / 100.0;
                cr.DecoWorldH = (dy != 0 ? dy : 16000) / 100.0;
                cr.RotationDeg = -d.SizeRaw / 100.0;
                cr.ColorRgba = d.ColorRgba;
                break;
            case TbEntity te:
                cr.Mass = TbEdit.EntityMass(te) ?? te.Mass;
                cr.ColorRgba = te.ColorRgba;
                break;
            case TbDeco td:
                cr.Mass = td.DimW;
                // A deco's dimensions ARE its world box (centiunits), like TP2's - not a
                // fraction of the sprite's pixel size. See DecoWorld.
                cr.DecoWorldW = DecoWorld(td.DimW);
                cr.DecoWorldH = DecoWorld(td.DimH);
                cr.ColorRgba = td.ColorRgba;
                cr.Opacity = FadeOpacity(td.ColorRgba);
                break;
            case EntityEditHandle eh:
                cr.Mass = ElemEdit.EntityMass(eh) ?? cr.Mass;
                cr.ColorRgba = ElemEdit.EntityColor(eh) ?? cr.ColorRgba;
                cr.Opacity = FadeOpacity(cr.ColorRgba);
                break;
            case DecoEditHandle dh:
                if (ElemEdit.DecoDims(dh) is (int dwRaw, int dhRaw))
                {
                    cr.Mass = dwRaw;
                    cr.DecoWorldW = DecoWorld(dwRaw);
                    cr.DecoWorldH = DecoWorld(dhRaw);
                }
                cr.ColorRgba = ElemEdit.DecoColor(dh) ?? cr.ColorRgba;
                cr.Opacity = FadeOpacity(cr.ColorRgba);
                break;
        }
    }

    // ------------------------------------------------------------ tb -------

    // Water box field indices (see TbCodec.ReadWater): D5/D6 = centre, D7/D8 = size,
    // D9..D12 = local min/max half-extents (min = -size/2, max = +size/2).
    private static List<PointD> WaterCorners(TbWater wt)
    {
        double cx = wt.D[5], cy = wt.D[6], hw = wt.D[7] / 2, hh = wt.D[8] / 2;
        return new List<PointD>
        {
            new(cx - hw, cy - hh), new(cx + hw, cy - hh),
            new(cx + hw, cy + hh), new(cx - hw, cy + hh),
        };
    }

    private static void WaterSetRect(TbWater wt, double cx, double cy, double sw, double sh)
    {
        wt.D[5] = cx; wt.D[6] = cy; wt.D[7] = sw; wt.D[8] = sh;
        wt.D[9] = -sw / 2; wt.D[10] = -sh / 2;
        wt.D[11] = sw / 2; wt.D[12] = sh / 2;
    }

    private static SceneGeometry ProjectTb(TbLevel tb)
    {
        var g = new SceneGeometry();
        for (int li = 0; li < tb.Layers.Count; li++)
        {
            var layer = tb.Layers[li];
            foreach (var w in layer.Walls)
            {
                if (w.HasShapesFlag == 0 && AddShapePolys(g, w.Shapes, w.PosX, w.PosY, w.Width, SceneKind.Wall, w.WallTypeName, li, w)) continue;
                g.Crosses.Add(new SceneCross { Kind = SceneKind.Wall, Label = w.WallTypeName, Layer = li, At = new PointD(w.PosX, w.PosY) });
            }
            foreach (var a in layer.Areas)
            {
                if (a.HasShapesFlag == 0 && AddShapePolys(g, a.Shapes, a.PosX, a.PosY, a.Width, SceneKind.Area, a.AreaName, li, a)) continue;
                g.Crosses.Add(new SceneCross { Kind = SceneKind.Area, Label = a.AreaName, Layer = li, At = new PointD(a.PosX, a.PosY) });
            }
            foreach (var p in layer.Paths)
                AddSpline(g, p.SplinePoints, SceneKind.Path, p.PathName, li);
            int elp = 0;   // _finalize_entities: entity priority chains down the section too
            foreach (var e in layer.Entities)
            {
                elp += e.Priority;
                g.Crosses.Add(new SceneCross
                {
                    Kind = SceneKind.Entity, Layer = li, At = new PointD(e.PosX, e.PosY), Source = e,
                    SpriteName = e.Name, Mass = e.Mass, RotationDeg = -e.Rotation / 100.0, ColorRgba = e.ColorRgba,
                    ZOrder = ZEntity(li, elp),
                });
            }
            foreach (var d in layer.Decos)
                g.Crosses.Add(new SceneCross
                {
                    Kind = SceneKind.Deco, Layer = li, At = new PointD(d.PosX, d.PosY), Source = d,
                    SpriteName = d.Name, Mass = d.DimW, RotationDeg = -d.Rotation / 100.0,
                    DecoWorldW = DecoWorld(d.DimW), DecoWorldH = DecoWorld(d.DimH),
                    ColorRgba = d.ColorRgba, Opacity = FadeOpacity(d.ColorRgba),
                    ZOrder = ZDeco(li, d.Priority), FlipX = d.FlipX, FlipY = d.FlipY,
                });
            foreach (var wt in layer.Waters)
            {
                // Water is an axis-aligned box (centre + half-extents), not a polygon: emit
                // it as a 4-corner rect whose Source is the TbWater, ShapeIndex -1 marks it
                // as a whole-rect (so the normalised wall/area path never claims it).
                var rect = new ScenePoly { Kind = SceneKind.Water, Label = wt.Name, Layer = li, Source = wt, ShapeIndex = -1 };
                rect.Points.AddRange(WaterCorners(wt));
                g.Polys.Add(rect);
            }
        }
        return g;
    }

    // ------------------------------------------------------------ tp2 ------

    private static SceneGeometry ProjectTp2(Tp2Level tp)
    {
        var g = new SceneGeometry();
        // The editor positions entities on a coarser grid than decorations/geometry: an
        // entity's dict position is multiplied by ENTITY_K (100) to reach world units, while
        // a deco's is used directly (DECO_K = 1). tp2editor.ENTITY_K / DECO_K.
        const double EntityK = 100.0, DecoK = 1.0;
        for (int li = 0; li < tp.Layers.Count; li++)
        {
            var layer = tp.Layers[li];
            foreach (var w in layer.Walls)
            {
                if (w.HasShapesFlag == 0 && AddShapePolys(g, w.Shapes, w.PosX, w.PosY, w.Width, SceneKind.Wall, w.WallTypeName, li, w)) continue;
                g.Crosses.Add(new SceneCross { Kind = SceneKind.Wall, Label = w.WallTypeName, Layer = li, At = new PointD(w.PosX, w.PosY) });
            }
            foreach (var p in layer.Paths)
                AddSpline(g, p.SplinePoints, SceneKind.Path, p.PathName, li);
            foreach (var e in layer.Entities)
                g.Crosses.Add(new SceneCross
                {
                    Kind = SceneKind.Entity, Label = e.Type, Layer = li, Source = e,
                    At = new PointD(e.Position[0] * EntityK, e.Position[1] * EntityK),
                    SpriteName = e.Type, Mass = e.Mass, RotationDeg = -e.Rotation / 100.0, ColorRgba = e.ColorRgba,
                    ZOrder = ZEntity(li, e.Priority),
                });
            // Decoration tiles: type 0 inherits the previous tile (run-length), type 1 is a cell
            // index into the level's tileTypes, type 2 carries an inline name (tp2editor.
            // resolve_deco_names). SIZE: the game fills the deco's explicit world rectangle
            // dims/100 x dims/100 (the oracle's uniform native-size scale of dims[0]/16000 is only
            // right for ~160px tiles, which is why the bigdirt background looked fine but a swapped
            // UI sprite did not). ROTATION: the 'size' field is a CENTIDEGREE value like entities
            // (-raw/100), NOT a fraction of a turn. Evidence: across dino1 the tile size.raw values
            // cluster on multiples of 9000 (8800/18000/26500/35400/36000/45100 -> 88/180/265/354/
            // 360/451 deg), so centidegrees keep every tile axis-aligned and the background tiles
            // butt seamlessly; a turn model would tilt them to arbitrary angles and open diagonal
            // seams between neighbours (the reported "holes"). The swapped bonus button's upside-down
            // look is intrinsic to that UI sprite, not a deco rotation (its size.raw ~ matches an
            // un-flipped dirt tile's), so it is deliberately not reproduced here.
            string? lastDeco = null;
            foreach (var d in layer.Decos)
            {
                string? nm = d.Type switch
                {
                    2 => d.String,
                    1 => (d.Cell >= 0 && d.Cell < tp.TileTypes.Count) ? tp.TileTypes[d.Cell] : null,
                    _ => lastDeco,
                };
                if (nm is not null) lastDeco = nm;
                int dimX = d.DimsRaw.Length > 0 ? d.DimsRaw[0] : 0;
                int dimY = d.DimsRaw.Length > 1 ? d.DimsRaw[1] : 0;
                g.Crosses.Add(new SceneCross
                {
                    Kind = SceneKind.Deco, Layer = li, Source = d,
                    At = new PointD(d.Position[0] * DecoK, d.Position[1] * DecoK),
                    SpriteName = nm, Mass = dimX,
                    DecoWorldW = (dimX != 0 ? dimX : 16000) / 100.0,
                    DecoWorldH = (dimY != 0 ? dimY : 16000) / 100.0,
                    RotationDeg = -d.SizeRaw / 100.0, ColorRgba = d.ColorRgba,
                    ZOrder = ZDeco(li, d.PriorityTotal), FlipX = d.FlipX, FlipY = d.FlipY,
                });
            }
        }
        return g;
    }

    private static bool AddShapePolys(SceneGeometry g, List<TbShape> shapes, double px, double py, double width, SceneKind kind, string? label, int layer, object owner)
    {
        if (shapes.Count == 0) return false;
        double w = width == 0 ? 1.0 : width;
        bool any = false;
        for (int i = 0; i < shapes.Count; i++)
        {
            var sh = shapes[i];
            if (sh.IsCircle || sh.Vertices.Length < 2) continue;
            var poly = new ScenePoly { Kind = kind, Label = label, Layer = layer, Source = owner, ShapeIndex = i };
            foreach (var v in sh.Vertices)
                poly.Points.Add(new PointD(px + v[0] * w, py + v[1] * w));
            g.Polys.Add(poly);
            any = true;
        }
        return any;
    }

    private static bool AddShapePolys(SceneGeometry g, List<Tp2Shape> shapes, double px, double py, double width, SceneKind kind, string? label, int layer, object owner)
    {
        if (shapes.Count == 0) return false;
        double w = width == 0 ? 1.0 : width;
        bool any = false;
        for (int i = 0; i < shapes.Count; i++)
        {
            var sh = shapes[i];
            if (sh.IsCircle || sh.Vertices.Length < 2) continue;
            var poly = new ScenePoly { Kind = kind, Label = label, Layer = layer, Source = owner, ShapeIndex = i };
            foreach (var v in sh.Vertices)
                poly.Points.Add(new PointD(px + v[0] * w, py + v[1] * w));
            g.Polys.Add(poly);
            any = true;
        }
        return any;
    }

    private static void AddSpline(SceneGeometry g, double[][] pts, SceneKind kind, string? label, int layer)
    {
        if (pts.Length < 2) return;
        var line = new SceneLine { Kind = kind, Label = label, Layer = layer };
        foreach (var sp in pts)
            if (sp.Length >= 2) line.Points.Add(new PointD(sp[0], sp[1]));
        if (line.Points.Count >= 2) g.Lines.Add(line);
    }

    // ------------------------------------------------- element (f4 / f5) ---

    // Section index per game (matches ElementSchemas recorder order).
    private static readonly Dictionary<string, (int Wall, int Snow, int Area, int Path, int PointList, int Entity, int Deco, int Water, int Sections)> ElemIdx = new()
    {
        ["f5"] = (0, 1, 2, 3, 4, 5, 6, 7, 8),
        ["f4"] = (0, -1, 1, 2, 3, 4, 5, 6, 7),
    };

    private static SceneGeometry ProjectElement(ElementLevel el)
    {
        var g = new SceneGeometry();
        string key = el.Layers.Count > 0 ? SchemaKey(el) : "";
        if (!ElemIdx.TryGetValue(key, out var ix))
            return g;
        bool isF5 = key == "f5";
        for (int li = 0; li < el.Layers.Count; li++)
        {
            var layer = el.Layers[li];
            ProjectWallSection(g, layer, ix.Wall, li);
            if (ix.Snow >= 0) ProjectWallSection(g, layer, ix.Snow, li);
            ProjectAreaSection(g, layer, ix.Area, li);
            ProjectPathSection(g, layer, ix.Path, li);
            ProjectPointListSection(g, layer, ix.PointList, li);
            ProjectEntitySection(g, el, layer, ix.Entity, li);
            ProjectDecoSection(g, el, layer, ix.Deco, li, isF5);
        }
        return g;
    }

    // f4/f5 entities: the whole section is one opaque block whose per-record boundaries are prefab-dependent,
    // but the loading code reads them back to back off one cursor, so EntityFraming.SplitRecords walks the
    // block exactly that way (name + chained x/y + xy_off are shared, and each record's gated tail ends it)
    // and ReadBody(ext=8) reads rotation + colour/mass/priority like the Python render_only_dicts. Positions
    // chain down the block in whole world units. Source is an EntityEditHandle pointing at the record by
    // index, so a sprite reassignment (ElemEdit.SetEntitySprite) can splice the name inside the block; the
    // block resizes on such an edit, so the shell re-projects afterwards. Position stays non-movable
    // (TrySetPos/GetPos ignore this handle), matching the previous not-draggable behaviour.
    private static void ProjectEntitySection(SceneGeometry g, ElementLevel el, List<Section> layer, int idx, int li)
    {
        if (idx < 0 || idx >= layer.Count) return;
        var sec = layer[idx];
        if (!sec.Opaque || sec.Count <= 0 || sec.Block.Length == 0) return;
        var (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
        if (!ok || heads.Count != sec.Count) return;
        double ax = 0, ay = 0;
        int ap = 0;   // the record's priority word is a delta; the z uses the chained total
        for (int i = 0; i < heads.Count; i++)
        {
            var h = heads[i];
            int recEnd = i + 1 < heads.Count ? heads[i + 1].RecStart : sec.Block.Length;
            ax += h.X; ay += h.Y;
            var cr = new SceneCross
            {
                Kind = SceneKind.Entity, Layer = li, At = new PointD(ax, ay),
                SpriteName = h.Name, Source = new EntityEditHandle(el, sec, i),
            };
            var disp = EntityFraming.ReadBody(sec.Block, h.XyOff, EntityFraming.ExtF4F5, recEnd);
            cr.RotationDeg = -disp.Rotation / 100.0;
            if (disp.HasBody) { cr.Mass = disp.Mass; cr.ColorRgba = disp.ColorRgba; cr.Opacity = FadeOpacity(disp.ColorRgba); }
            ap += disp.Priority;
            cr.ZOrder = ZEntity(li, ap);
            g.Crosses.Add(cr);
        }
    }

    // f4/f5 decorations are framed records; parse_deco (f4, == tb shape) / parse_deco_f5
    // (+ one int32 after colour) then finalize_decos chains _dx/_dy down the section at
    // centiunits (/100). The rotation lives in size.raw and the sprite scale in
    // dimensions.raw[0]. A record that fails to parse aborts the section (bailing keeps
    // the chain consistent rather than misplacing every later sprite). Source is a
    // DecoEditHandle pointing at the live record, so a decoration can be repointed at a
    // different sprite cell in place (ElemEdit.SetDecoSprite).
    private static void ProjectDecoSection(SceneGeometry g, ElementLevel el, List<Section> layer, int idx, int li, bool isF5)
    {
        if (idx < 0 || idx >= layer.Count) return;
        var sec = layer[idx];
        if (sec.Opaque) return;
        var tiles = el.TileTypes;
        double lx = 0, ly = 0;
        int lp = 0;               // finalize_decos: lp += _prio_delta (chained per section)
        string? last = null;   // run-length: a type-0 deco inherits the previous tile (per layer)
        for (int ri = 0; ri < sec.Records.Count; ri++)
        {
            var rec = sec.Records[ri];
            if (!TryReadElementDeco(rec, isF5, out int dx, out int dy, out int type, out var inlineName, out int cell,
                    out int rot, out int dim0, out int dim1, out var color, out bool flipX, out bool flipY, out int prio))
                return;
            lx += dx; ly += dy;
            lp += prio;
            // Resolve the sprite name like tbeditor.resolve_deco_names:
            //   type 2 = inline string, type 1 = tileTypes[cell], type 0 = inherit previous.
            string? name = type switch
            {
                0x02 => inlineName,
                0x01 => (cell >= 0 && cell < tiles.Count) ? tiles[cell] : null,
                _ => last,
            };
            if (name is not null) last = name;
            g.Crosses.Add(new SceneCross
            {
                Kind = SceneKind.Deco, Layer = li, At = new PointD(lx * 0.01, ly * 0.01),
                Source = new DecoEditHandle(el, sec, ri),
                SpriteName = name, Mass = dim0, RotationDeg = -rot / 100.0,
                DecoWorldW = DecoWorld(dim0), DecoWorldH = DecoWorld(dim1),
                ColorRgba = color, Opacity = FadeOpacity(color),
                ZOrder = ZDeco(li, lp), FlipX = flipX, FlipY = flipY,
            });
        }
    }

    private static bool TryReadElementDeco(byte[] b, bool isF5, out int dx, out int dy, out int type, out string? inlineName, out int cell, out int rot, out int dim0, out int dim1, out byte[]? color, out bool flipX, out bool flipY, out int prio)
    {
        dx = dy = rot = dim0 = dim1 = cell = prio = 0; type = -1; inlineName = null; color = null;
        flipX = flipY = false;
        int o = 0;
        if (b.Length < 1) return false;
        type = b[o]; o += 1;
        if (type == 0x02)
        {
            if (o + 4 > b.Length) return false;
            int n = ByteCursor.ReadI32At(b, o); o += 4;
            if (n < 0 || o + n > b.Length) return false;
            int end = o;
            while (end < o + n && b[end] != 0) end++;
            inlineName = System.Text.Encoding.Latin1.GetString(b, o, end - o);
            o += n;
        }
        else if (type == 0x01)
        {
            if (o + 4 > b.Length) return false;
            cell = ByteCursor.ReadI32At(b, o); o += 4;   // index into tileTypes (resolved by caller)
        }
        if (o + 8 + 4 + 1 > b.Length) return false;
        dx = ByteCursor.ReadI32At(b, o);
        dy = ByteCursor.ReadI32At(b, o + 4); o += 8;
        rot = ByteCursor.ReadI32At(b, o); o += 4;     // size.raw = centidegrees
        int extra = b[o]; o += 1;
        if (extra != 0)
        {
            // The engine's boundaries inside the 24-byte block: two readBoolean int32 (readBoolean
            // consumes a whole word and tests == 1) then four readInt. Words 0/1 are the record's
            // ElementDecoration::setFlipX / setFlipY, i.e. the mirroring the game applies in local
            // space before rotation - the property that was missing from the render.
            if (o + 24 > b.Length) return false;
            flipX = ByteCursor.ReadI32At(b, o) == 1;
            flipY = ByteCursor.ReadI32At(b, o + 4) == 1;
            o += 24;
        }
        if (o + 4 > b.Length) return false;
        color = new[] { b[o], b[o + 1], b[o + 2], b[o + 3] };   // record colour (RGBA); A fades the sprite
        o += 4;                                        // color rgba
        if (isF5)
        {
            if (o + 4 > b.Length) return false;
            o += 4;                                   // fb_extra (Final Bite only)
        }
        if (o + 8 > b.Length) return false;
        dim0 = ByteCursor.ReadI32At(b, o);            // dimensions.raw[0] = world width (centiunits)
        dim1 = ByteCursor.ReadI32At(b, o + 4);        // dimensions.raw[1] = world height
        o += 8;
        if (o + 4 <= b.Length) prio = ByteCursor.ReadI32At(b, o);   // _prio_delta (chained by caller)
        return true;
    }

    /// <summary>A decoration's <c>dimensions.raw</c> entry IS the world box it occupies, in the
    /// same centiunits as the record's own position (world = raw/100). Evidence from the shipped
    /// levels: Tasty Blue has 4709 records whose dims are exactly <c>origWidth*100 x
    /// origHeight*100</c> (cloud1: dims [18300,10900], cell 183x109) and its X/Y factors track the
    /// cell aspect on ~100% of records, while only 36 sit at the Qt oracle's 16000 baseline - and
    /// those are values like 15987/16010, i.e. coincidence, not an authored default. So the sprite
    /// is stretched to dims/100 per axis (exactly how TP2 decos are drawn), NOT scaled by
    /// dims/16000 relative to its pixel size, which was off by origPx/160 per sprite (0.4x to 3.1x
    /// on natural-size records alone: small props drew too small, leaving seams between parts the
    /// game shows touching). A zero dimension set no box, so the sprite keeps its cell size -
    /// hence null rather than a guessed default.</summary>
    private static double? DecoWorld(int dimRaw) => dimRaw != 0 ? dimRaw / 100.0 : (double?)null;

    // ElementLevel doesn't carry its game key; infer from section count per layer.
    private static string SchemaKey(ElementLevel el) =>
        el.SectionsPerLayer >= 8 ? "f5" : "f4";

    // The ElementEngine game fades a whole sprite by the record colour's alpha byte
    // (build_sprite: eff = colorAlpha/255 * frameOpacity). Only emit an opacity when the
    // alpha is actually < 255 so ordinary opaque records/entities keep the default 1.0 path
    // and no PushOpacity is paid for them; null otherwise.
    private static double? FadeOpacity(byte[]? c) =>
        c is { Length: >= 4 } && c[3] != 255 ? c[3] / 255.0 : null;

    private static void ProjectWallSection(SceneGeometry g, List<Section> layer, int idx, int li)
    {
        if (idx < 0 || idx >= layer.Count) return;
        var sec = layer[idx];
        for (int ri = 0; ri < sec.Records.Count; ri++)
        {
            var rec = sec.Records[ri];
            var r = new GeoReader(rec);
            // Wall/Snow layout: f64 x,y,w,l; string; u8; [shapes]; i32.
            if (!r.TryWallOrArea(isWall: true, out _, out double px, out double py, out double width, out bool hasShapes, out var verts)) continue;
            if (!hasShapes && EmitVerts(g, verts, px, py, width, SceneKind.Wall, null, li, MakeEdit(sec, ri, rec, r))) continue;
            g.Crosses.Add(new SceneCross { Kind = SceneKind.Wall, Layer = li, At = new PointD(px, py) });
        }
    }

    private static void ProjectAreaSection(SceneGeometry g, List<Section> layer, int idx, int li)
    {
        if (idx < 0 || idx >= layer.Count) return;
        var sec = layer[idx];
        for (int ri = 0; ri < sec.Records.Count; ri++)
        {
            var rec = sec.Records[ri];
            var r = new GeoReader(rec);
            // Area layout: string; f64 x,y,w,l; u8; [shapes]; i32.
            if (!r.TryWallOrArea(isWall: false, out _, out double px, out double py, out double width, out bool hasShapes, out var verts)) continue;
            if (!hasShapes && EmitVerts(g, verts, px, py, width, SceneKind.Area, null, li, MakeEdit(sec, ri, rec, r))) continue;
            g.Crosses.Add(new SceneCross { Kind = SceneKind.Area, Layer = li, At = new PointD(px, py) });
        }
    }

    private static void ProjectPathSection(SceneGeometry g, List<Section> layer, int idx, int li)
    {
        if (idx < 0 || idx >= layer.Count) return;
        foreach (var rec in layer[idx].Records)
        {
            var r = new GeoReader(rec);
            if (r.TryPath(out var pts)) EmitSpline(g, pts, SceneKind.Path, li);
        }
    }

    private static void ProjectPointListSection(SceneGeometry g, List<Section> layer, int idx, int li)
    {
        if (idx < 0 || idx >= layer.Count) return;
        foreach (var rec in layer[idx].Records)
        {
            var r = new GeoReader(rec);
            if (r.TryPointList(out var pts)) EmitXY(g, pts, SceneKind.PointList, li);
        }
    }

    private static bool EmitVerts(SceneGeometry g, List<double[]>? verts, double px, double py, double width, SceneKind kind, string? label, int layer, ElemShapeEdit? edit = null)
    {
        if (verts is null || verts.Count < 2) return false;
        double w = width == 0 ? 1.0 : width;
        var poly = new ScenePoly { Kind = kind, Label = label, Layer = layer };
        if (edit is not null) { poly.Source = edit; poly.ShapeIndex = 0; }
        foreach (var v in verts) poly.Points.Add(new PointD(px + v[0] * w, py + v[1] * w));
        g.Polys.Add(poly);
        return true;
    }

    // Build an in-place edit handle for a raw-span element wall/area record, or null when
    // the walk did not land on a well-formed polygon (then the shape stays read-only).
    private static ElemShapeEdit? MakeEdit(Section sec, int ri, byte[] rec, GeoReader r)
    {
        if (r.PosXOff < 0 || r.VertsOff < 0 || r.VertCount < 2) return null;
        if (r.PosXOff + 24 > rec.Length) return null;                 // pos x,y,w doubles fit
        if (r.VertsOff + (long)r.VertCount * 16 > rec.Length) return null;
        return new ElemShapeEdit
        {
            Sec = sec, RecIndex = ri, Rec = rec,
            PosXOff = r.PosXOff, PosYOff = r.PosXOff + 8,
            VertsOff = r.VertsOff, VertCount = r.VertCount,
        };
    }

    private static void EmitSpline(SceneGeometry g, List<double[]> pts, SceneKind kind, int layer)
    {
        var line = new SceneLine { Kind = kind, Layer = layer };
        foreach (var sp in pts)
            if (sp.Length >= 2) line.Points.Add(new PointD(sp[0], sp[1]));
        if (line.Points.Count >= 2) g.Lines.Add(line);
    }

    private static void EmitXY(SceneGeometry g, List<double[]> pts, SceneKind kind, int layer)
    {
        foreach (var p in pts)
            if (p.Length >= 2) g.Crosses.Add(new SceneCross { Kind = kind, Layer = layer, At = new PointD(p[0], p[1]) });
    }

    /// <summary>Display-only bounds-checked reader over one element record. It walks
    /// the same field order as the schema recorders but captures the geometry floats.
    /// Any short/absurd read throws <see cref="StopException"/>; callers swallow it and
    /// fall back to a centre marker, so a misparse can never corrupt the model.</summary>
    private struct GeoReader
    {
        private readonly byte[] _d;
        private int _p;
        // Byte offsets the walk lands on, captured so an element wall/area record (a raw
        // span with no typed fields) can still be edited in place. -1 = not found.
        public int PosXOff = -1, VertsOff = -1, VertCount;
        public GeoReader(byte[] rec) { _d = rec; _p = 0; }

        private bool Has(int n) => (long)_p + n <= _d.Length && n >= 0;
        private int I32() { if (!Has(4)) throw new StopException("eof"); int v = BitConverter.ToInt32(_d, _p); _p += 4; return v; }
        private double F64() { if (!Has(8)) throw new StopException("eof"); double v = BitConverter.ToDouble(_d, _p); _p += 8; return v; }
        private int U8() { if (!Has(1)) throw new StopException("eof"); return _d[_p++]; }
        private void SkipString()
        {
            int n = I32();
            if (n < 0 || !Has(n)) throw new StopException("bad str");
            _p += n;
        }

        // Wall/Snow (isWall) leads with f64 x,y,w,l then the name string; Area leads
        // with the name string then the four doubles. Both then: u8 flag; [shapes]; i32.
        public bool TryWallOrArea(bool isWall, out string? name, out double px, out double py,
                                 out double width, out bool hasShapes, out List<double[]>? verts)
        {
            name = null; px = py = width = 0; hasShapes = true; verts = null;
            try
            {
                double a, b, c, d;
                if (isWall)
                {
                    PosXOff = _p;
                    a = F64(); b = F64(); c = F64(); d = F64();
                    name = ReadStringOrNull();
                }
                else
                {
                    name = ReadStringOrNull();
                    PosXOff = _p;
                    a = F64(); b = F64(); c = F64(); d = F64();
                }
                px = a; py = b; width = c;
                int flag = U8();
                hasShapes = flag != 0;
                if (flag == 0) verts = ReadShapeDefs();
                return true;
            }
            catch (StopException) { return false; }
        }

        private string? ReadStringOrNull()
        {
            try
            {
                int n = I32();
                if (n < 0 || !Has(n)) throw new StopException("bad str");
                int end = 0; while (end < n && _d[_p + end] != 0) end++;
                var s = System.Text.Encoding.Latin1.GetString(_d, _p, end);
                _p += n;
                return s;
            }
            catch (StopException) { return null; }
        }

        // ReadShapes: i32 count, then per shape (name + geometry), then bool4 ext.
        // We capture only the FIRST polygon's vertices (walls usually carry one).
        private List<double[]>? ReadShapeDefs()
        {
            int count = I32();
            if (count < 0 || count > 4_000_000) throw new StopException("bad shapes");
            List<double[]>? first = null;
            for (int i = 0; i < count; i++)
            {
                // Each shape: name string, then Circle = 3 f64, else i32 vertcount + n*(x,y).
                string? nm = ReadStringOrNull();
                if (nm == "Circle") { F64(); F64(); F64(); }
                else
                {
                    int n = I32();
                    if (n < 0 || n > 4_000_000) throw new StopException("bad verts");
                    int vOff = _p;
                    var list = new List<double[]>(n);
                    for (int k = 0; k < n; k++) { double x = F64(); double y = F64(); list.Add(new[] { x, y }); }
                    if (first is null) { first = list; VertsOff = vOff; VertCount = n; }
                }
            }
            return first;
        }

        // Path: string; f64×4; u8; if==1 { i32 n; n×6f64 }; i32.
        public bool TryPath(out List<double[]> pts)
        {
            pts = new();
            try
            {
                SkipString();
                F64(); F64(); F64(); F64();
                int flag = U8();
                if (flag == 1)
                {
                    int n = I32();
                    if (n < 0 || n > 4_000_000) throw new StopException("bad path n");
                    for (int i = 0; i < n; i++)
                    {
                        double x = F64(); double y = F64();
                        F64(); F64(); F64(); F64();
                        pts.Add(new[] { x, y });
                    }
                }
                return pts.Count >= 2;
            }
            catch (StopException) { pts = new(); return false; }
        }

        // PointList/Sec4: string; f64×4; i32 n; n×(f64,f64); i32.
        public bool TryPointList(out List<double[]> pts)
        {
            pts = new();
            try
            {
                SkipString();
                F64(); F64(); F64(); F64();
                int n = I32();
                if (n < 0 || n > 4_000_000) throw new StopException("bad pl n");
                for (int i = 0; i < n; i++)
                {
                    double x = F64(); double y = F64();
                    pts.Add(new[] { x, y });
                }
                return pts.Count >= 1;
            }
            catch (StopException) { pts = new(); return false; }
        }
    }
}

/// <summary>An editable handle onto the first polygon of a raw-span element (Forever /
/// Final Bite) wall or area record. Those codecs store each record as an opaque
/// <c>byte[]</c> in <see cref="Section.Records"/> that the encoder re-emits verbatim, so
/// editing means patching doubles in place at the byte offsets the display walk found
/// (<paramref name="VertsOff"/> is the first vertex's X; the polygon's own count <c>i32</c>
/// sits at <c>VertsOff-4</c>). It reads position/width live from the record so a move and a
/// reshape stay consistent across successive edits.</summary>
public sealed class ElemShapeEdit
{
    public Section Sec = default!;
    public int RecIndex;
    public byte[] Rec = default!;      // == Sec.Records[RecIndex]
    public int PosXOff, PosYOff;       // byte offsets of the pos x,y doubles within Rec
    public int VertsOff;               // byte offset of the first vertex X
    public int VertCount;

    public double PosX => BitConverter.ToDouble(Rec, PosXOff);
    public double PosY => BitConverter.ToDouble(Rec, PosYOff);
    public double Width { get { double w = BitConverter.ToDouble(Rec, PosXOff + 16); return w == 0 ? 1 : w; } }

    /// <summary>The captured offsets still describe a well-formed polygon inside the
    /// current record bytes (re-checked after a length-changing add).</summary>
    public bool Ok =>
        Rec is not null && VertCount >= 2
        && PosXOff + 24 <= Rec.Length
        && VertsOff + (long)VertCount * 16 <= Rec.Length;
}
