namespace TastyEditor.Core.Levels;

/// <summary>Byte-safe sprite-name reassignment for Tasty Blue decorations.
///
/// <para>Unlike TP2 (whose codec re-serialises every field from a typed model), Tasty Blue
/// carries each decoration as a verbatim raw span that <see cref="TbCodec.Encode"/> re-emits
/// untouched. A decoration's sprite, however, lives entirely in the fixed-shape record
/// <em>head</em> — <c>u8 type</c> followed by either an <c>int32</c> tileTypes index
/// (<c>type = 1</c>) or a length-prefixed inline string (<c>type = 2</c>) — and the rest of
/// the record (deltas, rotation, colour, dimensions, priority) is a length-stable tail we can
/// copy byte-for-byte. So repointing a deco is a bounded head rewrite: materialise the name
/// as a <c>type = 1</c> index into the level's <c>tileTypes</c> table (appending the name +
/// its parallel <c>TilesRaw</c> entry when absent, exactly mirroring <see cref="Tp2Edit"/>),
/// splice the new head onto the preserved tail, and let the encoder write the whole record
/// back verbatim. An untouched level still round-trips byte-identically (I1); a real edit
/// only touches the rewritten record (and the appended table entry), never any other bytes.</para>
///
/// <para>An <em>entity</em>'s sprite is its leading length-prefixed prefab name
/// (<c>&lt;int32 nameLen&gt;&lt;name + NUL&gt;</c> at the very start of <see cref="TbEntity.Raw"/>,
/// the field <see cref="TbCodec"/> reads back via <c>ReadRecordName</c>). That name is
/// variable-length, so <see cref="SetEntitySprite"/> rewrites the whole name field in place —
/// the encoder re-emits the record and re-derives its position offsets from the new length, and
/// each entity is a separate list element, so no other record shifts.</para>
///
/// <para>Run-length safety matches <see cref="Tp2Edit.SetDecoSprite"/>: a <c>type = 0</c>
/// deco inherits the previous resolved tile, so rewriting a deco would otherwise repaint the
/// follower that used to inherit through it. We materialise that immediate follower back to
/// the name it used to inherit, keeping the downstream chain's appearance unchanged.</para>
/// </summary>
public static class TbEdit
{
    /// <summary>Point a Tasty Blue decoration at <paramref name="name"/> the way the game
    /// loads a static tile: a <c>type = 1</c> head referencing an index into the level's
    /// <c>tileTypes</c> table, appending the name (and its parallel <c>TilesRaw</c> entry)
    /// when it is not already registered. Returns false (leaving the record untouched) for
    /// null inputs, a blank name, or a record whose head cannot be parsed.</summary>
    public static bool SetDecoSprite(TbLevel? lv, TbDeco? deco, string? name)
    {
        if (lv is null || deco is null || deco.Raw.Length < 1 || string.IsNullOrWhiteSpace(name)) return false;

        int tail = HeadLength(deco.Raw);
        if (tail < 0) return false;   // unparseable head: change nothing

        // Locate the record so we can protect the run-length chain that follows it.
        TbLayer? owner = null;
        int idx = -1;
        foreach (var l in lv.Layers)
        {
            int j = l.Decos.IndexOf(deco);
            if (j >= 0) { owner = l; idx = j; break; }
        }

        // The name this deco effectively rendered under BEFORE the edit (what a type-0
        // follower inherited through it). Decode resolved it into deco.Name.
        string? oldName = deco.Name;

        int cell = TileTypeIndex(lv, name);
        deco.Raw = BuildIndexedHead(cell, deco.Raw, tail);
        deco.Name = name;

        // Materialise the immediate type-0 follower back to the name it used to inherit.
        if (owner is not null && idx >= 0 && idx + 1 < owner.Decos.Count
            && !string.IsNullOrEmpty(oldName) && oldName != name)
        {
            var next = owner.Decos[idx + 1];
            if (next.Raw.Length >= 1 && next.Raw[0] == 0x00 && HeadLength(next.Raw) == 1)
            {
                int ncell = TileTypeIndex(lv, oldName!);
                next.Raw = BuildIndexedHead(ncell, next.Raw, 1);
                next.Name = oldName;
            }
        }
        return true;
    }

    /// <summary>S5-6: point a Tasty Blue entity at a different prefab <paramref name="name"/>
    /// by rewriting the leading length-prefixed name field of <see cref="TbEntity.Raw"/> in
    /// place. Unlike a decoration (fixed-shape head + stable tail), an entity's name is a
    /// variable-length <c>&lt;int32 nameLen&gt;&lt;name + NUL&gt;</c> at offset 0, so the whole field
    /// is replaced and the record's length changes by the name delta. That is safe here: the
    /// encoder re-emits each entity's <c>Raw</c> verbatim and re-derives its position offsets from
    /// <c>Raw[0]</c> (via <c>PatchEntityPositions</c>), and every entity is a separate list element,
    /// so no other record's bytes move. Returns false (leaving the record untouched) when the name
    /// is blank / non-ASCII or would not fit the record. An untouched level still round-trips
    /// byte-identically (I1).</summary>
    public static bool SetEntitySprite(TbLevel? lv, TbEntity? ent, string? name)
    {
        if (lv is null || ent is null) return false;
        var raw = ent.Raw;
        if (raw.Length < 8 || string.IsNullOrEmpty(name)) return false;
        if (!IsPrintableAscii(name!)) return false;

        int oldLen = BitConverter.ToInt32(raw, 0);
        if (oldLen < 0 || 4L + oldLen > raw.Length) return false;   // uncredible name field

        var nb = Latin1(name!);
        int newLen = nb.Length + 1;   // NUL-terminated, like the original prefab name
        int tailOff = 4 + oldLen;
        int tailLen = raw.Length - tailOff;
        if (tailLen < 1) return false;   // keep the instance + body after the name

        var nv = new byte[4 + newLen + tailLen];
        BitConverter.GetBytes(newLen).CopyTo(nv, 0);
        Buffer.BlockCopy(nb, 0, nv, 4, nb.Length);
        nv[4 + nb.Length] = 0x00;
        Buffer.BlockCopy(raw, tailOff, nv, 4 + newLen, tailLen);

        ent.Raw = nv;
        ent.Name = name;
        return true;
    }

    // ============================================================ deco move / rotate ==
    //
    // A decoration's position is carried as a pair of chained int32 deltas (centiunits, /100)
    // sitting immediately after the record head, followed by an int32 centidegree rotation
    // (see TbCodec.ReadDeco: _dx, _dy, size.raw). The absolute world position is the running
    // sum of the deltas down the layer, so:
    //   * moving deco[i] by a world delta changes delta[i] (+Δ) — which would otherwise also
    //     shift every LATER deco's absolute — so we compensate delta[i+1] (-Δ) to pin the rest
    //     of the chain. The last deco in the layer has no successor and needs none.
    //   * rotation is a per-record absolute (size.raw); writing it touches only that record.
    // All four fields are fixed-width int32 at a known offset, so both edits are length-
    // preserving in-place splices: the encoder re-emits the record verbatim (TbCodec writes
    // d.Raw untouched), an untouched level still round-trips byte-identically (I1), and a real
    // edit localises to the touched record(s) (I3) — exactly the primitive EditOps already uses
    // for decoration paste (delta re-solve) and delete (fold into successor).

    /// <summary>Translate a Tasty Blue decoration by a world-space delta, keeping every other
    /// deco in its chain fixed. Mutates the chained <c>_dx</c>/<c>_dy</c> int32s in place and
    /// compensates the successor. Returns false (changing nothing) when the record has no owning
    /// layer, an unparseable head, or is too short to hold the delta/rotation fields.</summary>
    public static bool MoveDeco(TbDeco? deco, double dxWorld, double dyWorld)
    {
        if (deco?.Owner is null || deco.Raw.Length < 1) return false;
        int i = deco.Owner.Decos.IndexOf(deco);
        if (i < 0) return false;
        int hl = HeadLength(deco.Raw);
        if (hl < 0 || hl + 12 > deco.Raw.Length) return false;   // needs dx,dy,rot int32s

        int dcx = (int)Math.Round(dxWorld * 100);
        int dcy = (int)Math.Round(dyWorld * 100);
        if (dcx == 0 && dcy == 0) return true;   // below the format's resolution: a no-op

        WriteI32(deco.Raw, hl, ReadI32(deco.Raw, hl) + dcx);
        WriteI32(deco.Raw, hl + 4, ReadI32(deco.Raw, hl + 4) + dcy);
        deco.PosX += dcx * 0.01;
        deco.PosY += dcy * 0.01;

        // Compensation: the successor's absolute is (… + delta[i] + delta[i+1]); subtracting Δ
        // from delta[i+1] leaves it and every later deco exactly where they were.
        if (i + 1 < deco.Owner.Decos.Count)
        {
            var nx = deco.Owner.Decos[i + 1];
            int h2 = HeadLength(nx.Raw);
            if (h2 >= 0 && h2 + 8 <= nx.Raw.Length)
            {
                WriteI32(nx.Raw, h2, ReadI32(nx.Raw, h2) - dcx);
                WriteI32(nx.Raw, h2 + 4, ReadI32(nx.Raw, h2 + 4) - dcy);
            }
        }
        return true;
    }

    /// <summary>Move a decoration to an absolute world position (the inspector's X / Y path);
    /// expressed as the delta from its current chained position and routed through
    /// <see cref="MoveDeco"/>.</summary>
    public static bool SetDecoPos(TbDeco? deco, double wx, double wy)
        => deco is not null && MoveDeco(deco, wx - deco.PosX, wy - deco.PosY);

    /// <summary>Set a decoration's absolute rotation in degrees. The stored <c>size.raw</c> is
    /// centidegrees with the sign the renderer negates (<c>RotationDeg = -Rotation / 100</c>), so
    /// the raw int is <c>round(-deg * 100)</c>. Length-preserving int32 write; touches only this
    /// record. Returns false on an unparseable / too-short head.</summary>
    public static bool SetDecoRotationDeg(TbDeco? deco, double deg)
    {
        if (deco?.Raw == null || deco.Raw.Length < 1) return false;
        int hl = HeadLength(deco.Raw);
        if (hl < 0 || hl + 12 > deco.Raw.Length) return false;
        int centi = (int)Math.Round(-deg * 100);
        WriteI32(deco.Raw, hl + 8, centi);
        deco.Rotation = centi;
        return true;
    }

    private static int ReadI32(byte[] b, int off) => BitConverter.ToInt32(b, off);
    private static void WriteI32(byte[] b, int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);

    // ============================================================ entity rotate ==
    //
    // A Tasty Blue ENTITY already moves through the typed path (SceneProjector shifts
    // e.PosX / e.PosY and TbCodec.PatchEntityPositions re-derives the chained int32 deltas
    // from those absolutes on save, in whole world units). Its ROTATION, however, is a
    // fixed-width int32 centidegree field at <c>xy_off + EntityFraming.Rot</c> inside the
    // verbatim <see cref="TbEntity.Raw"/> span that no encoder touches — so editing it is an
    // in-place, length-preserving splice, the same primitive as the decoration rotate above.
    // Sign matches the renderer (<c>RotationDeg = -e.Rotation / 100</c>): raw = round(-deg*100).

    /// <summary>Set a Tasty Blue entity's absolute rotation in degrees by splicing the
    /// centidegree int32 in its <c>Raw</c> body. Also mirrors the value into
    /// <see cref="TbEntity.Rotation"/> so the projection reads it without re-decoding.
    /// Returns false (changing nothing) when the record head or the rotation field is out of
    /// range. An untouched level still round-trips byte-identically (I1); the edit localises
    /// to the four rotation bytes (I3).</summary>
    public static bool SetEntityRotationDeg(TbEntity? ent, double deg)
    {
        if (ent?.Raw == null) return false;
        int xo = EntityXyOff(ent.Raw);
        if (xo < 0) return false;
        int ro = xo + EntityFraming.Rot;
        if (ro + 4 > ent.Raw.Length) return false;
        int centi = (int)Math.Round(-deg * 100);
        WriteI32(ent.Raw, ro, centi);
        ent.Rotation = centi;
        return true;
    }

    /// <summary>Offset of an entity record's chained <c>x</c> int32 within <c>Raw</c> (the
    /// <c>xy_off</c> the decoder and <see cref="EntityFraming.ReadBody"/> key off). Mirrors
    /// <c>TbCodec.PositionOffsets</c>: <c>[i32 nameLen][name][i32 instLen][inst][i32 x]</c>.
    /// -1 when the head is not credible.</summary>
    private static int EntityXyOff(byte[] raw)
    {
        if (raw.Length < 8) return -1;
        int nameLen = ReadI32(raw, 0);
        int body = 4 + nameLen;
        if (body + 4 > raw.Length) return -1;
        int n = ReadI32(raw, body);
        if (n < 1 || n > 64) return -1;
        int xo = body + 4 + n;
        if (xo + 8 > raw.Length) return -1;
        return xo;
    }

    // ==================================================== entity / deco field parity ==
    //
    // The classic editor also lets a Tasty Blue entity's mass / priority / colour and a
    // decoration's size / colour be edited. Every one is a fixed-width field inside the record
    // that TbCodec re-emits verbatim (d.Raw / e.Raw), so each edit is a length-preserving in-place
    // splice — the same primitive as the entity rotate above — keeping an untouched level
    // byte-identical (I1) and localising a real edit to the touched field's bytes (I3). The
    // variable-length behaviour blocks (move / path / emitter / persist) are deliberately NOT
    // exposed here: like the Python inspector (entity_full_body) they live in a per-prefab tail
    // we preserve verbatim, so those controls stay TP2-only.

    private const double DimBase = 16000.0;   // deco dimensions.raw -> scale 1.0 (tbeditor.DIM_BASE)

    private static void WriteF64(byte[] b, int off, double v) => BitConverter.GetBytes(v).CopyTo(b, off);

    /// <summary>Offsets of an entity body's colour / mass / priority fields (ext=0 for tb), or
    /// false when the record body isn't a layout we can address (matches the oracle's _no_body).</summary>
    private static bool EntityBody(TbEntity? ent, out int colorOff, out int massOff, out int prioOff)
    {
        colorOff = massOff = prioOff = -1;
        if (ent?.Raw == null) return false;
        int xo = EntityXyOff(ent.Raw);
        if (xo < 0) return false;
        return EntityFraming.BodyOffsets(ent.Raw, xo, EntityFraming.ExtTb, ent.Raw.Length, out colorOff, out massOff, out prioOff);
    }

    public static double? EntityMass(TbEntity? ent)
        => EntityBody(ent, out _, out int mo, out _) ? BitConverter.ToDouble(ent!.Raw, mo) : (double?)null;

    public static int? EntityPriority(TbEntity? ent)
        => EntityBody(ent, out _, out _, out int po) ? ReadI32(ent!.Raw, po) : (int?)null;

    public static byte[]? EntityColor(TbEntity? ent)
    {
        if (!EntityBody(ent, out int co, out _, out _)) return null;
        var r = ent!.Raw;
        return new[] { r[co], r[co + 1], r[co + 2], r[co + 3] };
    }

    public static bool SetEntityMass(TbEntity? ent, double mass)
    {
        if (!EntityBody(ent, out _, out int mo, out _)) return false;
        WriteF64(ent!.Raw, mo, mass);
        ent.Mass = mass;
        return true;
    }

    public static bool SetEntityPriority(TbEntity? ent, int prio)
    {
        if (!EntityBody(ent, out _, out _, out int po)) return false;
        WriteI32(ent!.Raw, po, prio);
        ent.Priority = prio;
        return true;
    }

    public static bool SetEntityColor(TbEntity? ent, byte r0, byte g0, byte b0, byte a0)
    {
        if (!EntityBody(ent, out int co, out _, out _)) return false;
        var raw = ent!.Raw;
        raw[co] = r0; raw[co + 1] = g0; raw[co + 2] = b0; raw[co + 3] = a0;
        ent.ColorRgba = new[] { r0, g0, b0, a0 };
        return true;
    }

    /// <summary>Resolve the raw behaviour tail of a Tasty Blue entity for the lane editor: the
    /// underlying record buffer (<c>ent.Raw</c>, which TbCodec re-emits verbatim), the absolute
    /// offset of the first tail byte (<c>prioOff + 4</c>) and the tail length (to the record end).
    /// False when the body layout isn't addressable. Lane writes go through
    /// <see cref="EntityFraming.WriteLaneInt"/> / <see cref="EntityFraming.WriteLaneF64"/> clamped to
    /// <c>baseAbs .. baseAbs + len</c>, so they splice in place without resizing the record (I1/I3).</summary>
    public static bool EntityTailSpan(TbEntity? ent, out byte[] buf, out int baseAbs, out int len)
    {
        buf = Array.Empty<byte>(); baseAbs = len = -1;
        if (!EntityBody(ent, out _, out _, out int po)) return false;
        buf = ent!.Raw;
        baseAbs = EntityFraming.TailStart(po);
        len = buf.Length - baseAbs;
        return len >= 0;
    }

    /// <summary>Structural (length-changing) edit of a Tasty Blue entity's behaviour tail: turning a gated
    /// behaviour block on or off, or renaming the path / defined-area name it embeds (see
    /// <see cref="TailEdit"/>). This resizes the record, which is safe for the same reason
    /// <see cref="SetEntitySprite"/> is: <c>ent.Raw</c> is the lossless carrier, every entity is its own
    /// list element and <c>TbCodec</c> re-derives each record's length by parsing it, so no other record's
    /// bytes move. Guarded by the codec's own measurer — the candidate is only committed when
    /// <see cref="TbCodec.MeasureRecord"/> walks to exactly its end, i.e. the resized tail still parses
    /// clean by the same block-by-block rules <c>readBinary</c> follows (I1 holds, I3 localises). False
    /// leaves the record untouched.</summary>
    public static bool ReshapeTail(TbEntity? ent, TailField f, object? value) => ReshapeTail(ent, f, value, out _);

    /// <summary>The same write reporting why it was refused ("" on success), for the editor's status line.</summary>
    public static bool ReshapeTail(TbEntity? ent, TailField f, object? value, out string why)
    {
        why = "";
        if (!EntityTailSpan(ent, out var raw, out int baseAbs, out int len))
        { why = "this entity's record cannot be addressed"; return false; }
        if (len < 4) { why = "this record has no behaviour tail to rebuild"; return false; }

        var tail = new byte[len];
        Buffer.BlockCopy(raw, baseAbs, tail, 0, len);
        var next = TailEdit.Reshape(tail, "tb", f, value);
        if (next is null) { why = TailEdit.Explain(tail, "tb", f, value); return false; }

        var cand = new byte[raw.Length - len + next.Length];
        Buffer.BlockCopy(raw, 0, cand, 0, baseAbs);
        Buffer.BlockCopy(next, 0, cand, baseAbs, next.Length);
        Buffer.BlockCopy(raw, baseAbs + len, cand, baseAbs + next.Length, raw.Length - baseAbs - len);

        // The record must still parse to its own end with the codec's own measurer.
        if (TbCodec.MeasureRecord(cand) != cand.Length)
        { why = "the new length would make the game read this record as a different size"; return false; }
        ent!.Raw = cand;
        return true;
    }

    /// <summary>Colour / dimension offsets inside a decoration record (after the fixed head and
    /// the flag-gated 24-byte extra block), mirroring <see cref="TbCodec.ReadDeco"/>. False when
    /// the head is unparseable or the record is too short to hold colour + both dims.</summary>
    private static bool DecoFields(byte[] raw, out int colorOff, out int dimWOff, out int dimHOff)
    {
        colorOff = dimWOff = dimHOff = -1;
        int hl = HeadLength(raw);
        if (hl < 0 || hl + 13 > raw.Length) return false;
        int extra = raw[hl + 12];
        int c = hl + 13 + (extra != 0 ? 24 : 0);
        if (c + 12 > raw.Length) return false;   // colour(4) + dimW(4) + dimH(4)
        colorOff = c; dimWOff = c + 4; dimHOff = c + 8;
        return true;
    }

    /// <summary>Current uniform size scale of a decoration (<c>dimW / DIM_BASE</c>), or null.</summary>
    public static double? DecoSize(TbDeco? deco)
        => deco?.Raw != null && DecoFields(deco.Raw, out _, out int wo, out _)
            ? (double)ReadI32(deco.Raw, wo) / DimBase : (double?)null;

    public static byte[]? DecoColor(TbDeco? deco)
    {
        if (deco?.Raw == null || !DecoFields(deco.Raw, out int co, out _, out _)) return null;
        var r = deco.Raw;
        return new[] { r[co], r[co + 1], r[co + 2], r[co + 3] };
    }

    /// <summary>Uniform decoration size scale preserving the current aspect (tbeditor._edit deco):
    /// <c>dimW = round(scale*DIM_BASE)</c>, <c>dimH = round(dimW*aspect)</c>. Mirrors DimW/DimH.</summary>
    public static bool SetDecoSize(TbDeco? deco, double scale)
    {
        if (deco?.Raw == null || !DecoFields(deco.Raw, out _, out int wo, out int ho)) return false;
        int ox = ReadI32(deco.Raw, wo);
        int oy = ReadI32(deco.Raw, ho);
        if (ox == 0) ox = (int)DimBase;
        double aspect = (double)oy / ox;
        int nx = (int)Math.Round(scale * DimBase);
        if (nx == 0) nx = 1;
        int ny = (int)Math.Round(nx * aspect);
        WriteI32(deco.Raw, wo, nx);
        WriteI32(deco.Raw, ho, ny);
        deco.DimW = nx; deco.DimH = ny;
        return true;
    }

    /// <summary>The record's two dimension ints (centiunits of world space, the same pair
    /// <see cref="DecoSize"/> derives its single knob from), or null when unparseable.</summary>
    public static (int W, int H)? DecoDimsRaw(TbDeco? deco)
        => deco?.Raw != null && DecoFields(deco.Raw, out _, out int wo, out int ho)
            ? (ReadI32(deco.Raw, wo), ReadI32(deco.Raw, ho)) : null;

    /// <summary>Free per-axis decoration resize (the viewport's Shift-drag grip): writes both
    /// dimension ints independently, one per axis — the record genuinely stores two fields, so
    /// unlike <see cref="SetDecoSize"/> this changes the sprite's aspect. Each axis is clamped to
    /// a minimum of 1 so an inverted drag can never zero or flip a dimension. Mirrors the
    /// DimW/DimH sync <see cref="SetDecoSize"/> does.</summary>
    public static bool SetDecoAxes(TbDeco? deco, double scaleW, double scaleH)
    {
        if (deco?.Raw == null || !DecoFields(deco.Raw, out _, out int wo, out int ho)) return false;
        int nx = Math.Max(1, (int)Math.Round(scaleW * DimBase));
        int ny = Math.Max(1, (int)Math.Round(scaleH * DimBase));
        WriteI32(deco.Raw, wo, nx);
        WriteI32(deco.Raw, ho, ny);
        deco.DimW = nx; deco.DimH = ny;
        return true;
    }

    public static bool SetDecoColor(TbDeco? deco, byte r0, byte g0, byte b0, byte a0)
    {
        if (deco?.Raw == null || !DecoFields(deco.Raw, out int co, out _, out _)) return false;
        var raw = deco.Raw;
        raw[co] = r0; raw[co + 1] = g0; raw[co + 2] = b0; raw[co + 3] = a0;
        deco.ColorRgba = new[] { r0, g0, b0, a0 };
        return true;
    }

    // ============================================================ deco flip (S-D13) ==
    //
    // ElementDecoration::writeBinary in the game's own code is the authority for the record's
    // optional 24-byte block. It emits the gate byte as 0 only when
    // flipX, flipY AND all four clip ints (object +0x44/0x48/0x4c/0x50) are default; otherwise it
    // writes gate 1 followed by writeBoolean(flipX), writeBoolean(flipY) and the four
    // writeInt verbatim. So a flip edit is allowed to change the record's length in BOTH
    // directions — materialising a block a record never had, and dropping one whose last
    // non-default word just went back to zero — and storing exactly those bytes keeps a level the
    // editor saved byte-comparable with one the shipped editor saved. Records whose gate byte is
    // something other than 1 keep their block (we never saw that value emitted by the game, so we
    // never invent its meaning either).

    /// <summary>Set a decoration's flip flags. Returns false (changing nothing) when the record
    /// head is unparseable or the existing block is truncated. Reports via <paramref name="resized"/>
    /// whether the record's byte length changed, so the caller knows to re-project (f4/f5 lanes).
    /// I1 holds: every other record is re-emitted verbatim; I3 holds: only this record's bytes move.</summary>
    public static bool SetDecoFlip(TbDeco? deco, bool fx, bool fy, out bool resized)
    {
        resized = false;
        if (deco?.Raw == null) return false;
        var raw = deco.Raw;
        int hl = HeadLength(raw);
        if (hl < 0 || hl + 13 > raw.Length) return false;
        int bo = hl + 13;                                  // block start: flipX word, flipY +4
        bool has = raw[hl + 12] != 0;
        if (has && bo + 24 > raw.Length) return false;      // truncated block: refuse

        if (has)
        {
            int clip = ReadI32(raw, bo + 8) | ReadI32(raw, bo + 12) | ReadI32(raw, bo + 16) | ReadI32(raw, bo + 20);
            WriteI32(raw, bo, fx ? 1 : 0);
            WriteI32(raw, bo + 4, fy ? 1 : 0);
            // writeBinary's own collapse rule: a gate byte of 1 with nothing left set is re-written
            // as no block at all.
            if (clip == 0 && !fx && !fy && raw[hl + 12] == 1)
            {
                var nv = new byte[raw.Length - 24];
                Buffer.BlockCopy(raw, 0, nv, 0, bo);
                Buffer.BlockCopy(raw, bo + 24, nv, bo, raw.Length - bo - 24);
                nv[hl + 12] = 0;
                deco.Raw = nv;
                resized = true;
            }
            deco.FlipX = fx; deco.FlipY = fy;
            return true;
        }

        if (!fx && !fy) { deco.FlipX = deco.FlipY = false; return true; }   // nothing to store

        // Materialise the block exactly as the engine would: flags then four zero clip ints.
        var nb = new byte[raw.Length + 24];
        Buffer.BlockCopy(raw, 0, nb, 0, bo);
        BitConverter.GetBytes(fx ? 1 : 0).CopyTo(nb, bo);
        BitConverter.GetBytes(fy ? 1 : 0).CopyTo(nb, bo + 4);
        Buffer.BlockCopy(raw, bo, nb, bo + 24, raw.Length - bo);
        nb[hl + 12] = 1;
        deco.Raw = nb;
        deco.FlipX = fx; deco.FlipY = fy;
        resized = true;
        return true;
    }

    public static bool SetDecoFlip(TbDeco? deco, bool fx, bool fy)
        => SetDecoFlip(deco, fx, fy, out _);

    private static bool IsPrintableAscii(string s)
    {
        foreach (char ch in s) if (ch < 32 || ch > 126) return false;
        return true;
    }

    /// <summary>Byte length of a decoration record's head (everything before the chained
    /// <c>dx</c> int32): 1 for a type-0 inherit, 5 for a type-1 int32 cell index, or
    /// <c>1 + 4 + nameLen</c> for a type-2 length-prefixed inline string. -1 when the head
    /// is not credible (unknown type byte, or a length that runs past the record), so the
    /// caller bails rather than corrupt the span.</summary>
    private static int HeadLength(byte[] raw)
    {
        if (raw.Length < 1) return -1;
        int type = raw[0];
        if (type == 0x00) return 1;
        if (type == 0x01) return raw.Length >= 5 ? 5 : -1;
        if (type == 0x02)
        {
            if (raw.Length < 5) return -1;
            int n = BitConverter.ToInt32(raw, 1);
            if (n < 0 || 5L + n > raw.Length) return -1;
            return 5 + n;
        }
        return -1;
    }

    /// <summary>Rewrite a record as <c>[0x01][int32 cell]</c> + the untouched tail from
    /// <paramref name="tailOffset"/> (the tail's length is preserved; only the head changes,
    /// so a type-0/1/2 → type-1 conversion grows or shrinks the record by whole fields, never
    /// scrambling the bytes after the head).</summary>
    private static byte[] BuildIndexedHead(int cell, byte[] raw, int tailOffset)
    {
        int tailLen = raw.Length - tailOffset;
        var nv = new byte[5 + tailLen];
        nv[0] = 0x01;
        BitConverter.GetBytes(cell).CopyTo(nv, 1);
        Buffer.BlockCopy(raw, tailOffset, nv, 5, tailLen);
        return nv;
    }

    /// <summary>Index of <paramref name="name"/> in the level's <c>tileTypes</c> table,
    /// appending it (and its parallel <c>TilesRaw</c> entry) when absent. Mirrors
    /// <c>tp2editor._tiletype_index</c>; here the raw-span codec keeps the two lists in
    /// lockstep so a new cell index the editor points a deco at resolves identically on
    /// re-decode.</summary>
    private static int TileTypeIndex(TbLevel lv, string name)
    {
        for (int i = 0; i < lv.TileTypes.Count; i++)
            if (lv.TileTypes[i] == name) return i;
        lv.TileTypes.Add(name);
        lv.TilesRaw.Add(BuildTbTileEntry(name));
        return lv.TileTypes.Count - 1;
    }

    /// <summary>A <c>tileTypes</c> raw entry as <see cref="TbCodec.Decode"/> captured it:
    /// the little-endian int32 length (byte count of the NUL-terminated payload) followed by
    /// the latin-1 name bytes and one terminating <c>0x00</c>. <see cref="TbCodec.Encode"/>
    /// re-emits these verbatim, so the appended entry reproduces on a re-decode.</summary>
    private static byte[] BuildTbTileEntry(string name)
    {
        var nb = Latin1(name);
        var entry = new byte[4 + nb.Length + 1];
        BitConverter.GetBytes(nb.Length + 1).CopyTo(entry, 0);
        Buffer.BlockCopy(nb, 0, entry, 4, nb.Length);
        entry[^1] = 0x00;
        return entry;
    }

    private static byte[] Latin1(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)(s[i] & 0xFF);
        return b;
    }
}
