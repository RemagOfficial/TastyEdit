using TastyEditor.Core.Render;

namespace TastyEditor.Core.Levels;

/// <summary>Address of one ElementEngine (Forever / Final Bite) decoration record: the
/// level, the framed <see cref="Section"/> that holds it, and its index within that
/// section's <c>Records</c>. The raw-span codec re-emits each record verbatim, so editing
/// <c>Sec.Records[RecIndex]</c> in place is the lossless edit primitive — this mirrors the
/// <c>Sec</c> + <c>RecIndex</c> pairing <see cref="Render.ElemShapeEdit"/> already uses for
/// polygon-vertex edits. Produced by <see cref="Render.SceneProjector"/> as a decoration
/// marker's <c>Source</c> so the shell can route a sprite reassignment back to the record.</summary>
public readonly record struct DecoEditHandle(ElementLevel Lv, Section Sec, int RecIndex);

/// <summary>Address of one ElementEngine (Forever / Final Bite) <em>entity</em> record inside
/// its section's opaque block. The entity section is a single <see cref="Section.Block"/> whose
/// per-record boundaries the framing oracle re-derives by walking <see cref="EntityFraming"/>;
/// a name edit is length-changing and shifts every later record's byte offset, so this handle
/// carries the record <em>index</em> (not a byte offset) — <see cref="ElemEdit.SetEntitySprite"/>
/// re-walks the block to the current byte position on each edit. Produced by
/// <see cref="Render.SceneProjector"/> as an entity marker's <c>Source</c>.</summary>
public readonly record struct EntityEditHandle(ElementLevel Lv, Section Sec, int RecIndex);

/// <summary>Byte-safe sprite-name reassignment for Forever / Final Bite decorations.
///
/// <para>These ElementEngine levels are lossless raw spans: <c>ElementSpanCodec.Encode</c>
/// rewrites the header framing and every section's records byte-for-byte. A decoration's
/// sprite lives in the fixed-shape record <em>head</em> — <c>u8 type</c> then either an
/// <c>int32</c> tileTypes index (<c>type = 1</c>) or a length-prefixed inline string
/// (<c>type = 2</c>) — while the tail (deltas, rotation, flags, colour, and for Final Bite
/// its one extra post-colour int32, then dimensions + priority) is a stable block we copy
/// verbatim. Repointing a deco is therefore a bounded head rewrite exactly as in
/// <see cref="TbEdit"/>: materialise the name as a <c>type = 1</c> index into the level's
/// <c>tileTypes</c> table (appending the name + its parallel <c>TilesRaw</c> entry when
/// absent) and splice it onto the preserved tail. Because only the head is rewritten, the
/// Final Bite-only extra int32 that sits in the tail is carried along untouched.</para>
///
/// <para>Decorations and (since S5-6) entities. An element <em>entity</em>'s name is a
/// variable-length string inside the section's opaque block; <see cref="SetEntitySprite"/>
/// rewrites it and re-validates that the block still walks to its exact record count before
/// committing (see that method). Run-length safety
/// matches <see cref="Tp2Edit"/>/<see cref="TbEdit"/>: an immediate <c>type = 0</c> follower
/// in the same section that used to inherit through the edited record is materialised to the
/// name it inherited, so the downstream chain keeps its appearance.</para>
/// </summary>
public static class ElemEdit
{
    /// <summary>Point the decoration at <paramref name="name"/> via a <c>type = 1</c> index
    /// into the level's <c>tileTypes</c> table (appending the name + its <c>TilesRaw</c>
    /// entry when absent). Returns false (leaving the record untouched) for a blank name or
    /// an unparseable record head.</summary>
    public static bool SetDecoSprite(DecoEditHandle h, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return false;

        var rec = h.Sec.Records[h.RecIndex];
        int tail = HeadLength(rec);
        if (tail < 0) return false;   // unparseable head: change nothing

        // The name this record effectively rendered under BEFORE the edit (what an immediate
        // type-0 follower inherits through it), resolved by walking the section's chain.
        string? oldName = ResolveNameAt(h, h.RecIndex);

        int cell = TileTypeIndex(h.Lv, name);
        h.Sec.Records[h.RecIndex] = BuildIndexedHead(cell, rec, tail);

        // Materialise the immediate type-0 follower back to the name it used to inherit.
        if (h.RecIndex + 1 < h.Sec.Records.Count
            && !string.IsNullOrEmpty(oldName) && oldName != name)
        {
            var next = h.Sec.Records[h.RecIndex + 1];
            if (next.Length >= 1 && next[0] == 0x00 && HeadLength(next) == 1)
            {
                int ncell = TileTypeIndex(h.Lv, oldName!);
                h.Sec.Records[h.RecIndex + 1] = BuildIndexedHead(ncell, next, 1);
            }
        }
        return true;
    }

    /// <summary>S5-6: point an ElementEngine (Forever / Final Bite) entity at a different
    /// prefab <paramref name="name"/>. The entity section is one opaque block whose record
    /// boundaries the framing oracle re-derives by walking <see cref="EntityFraming.HeadAt"/>;
    /// a name is a variable-length <c>&lt;int32 nameLen&gt;&lt;name + NUL&gt;</c> field at each record's
    /// head, so rewriting it shifts every later record inside the block.
    ///
    /// <para>This is why the edit is guarded: the new name must still form a <em>credible</em>
    /// head (printable ASCII, <c>5..64</c> bytes, containing <c>'_'</c> — the exact constraints
    /// <see cref="EntityFraming.HeadAt"/> enforces), and after splicing we re-walk the candidate
    /// block and require it still splits to the section's authoritative record count with the
    /// edited head now reading <paramref name="name"/>. If either check fails the block is left
    /// byte-for-byte untouched and the call returns false, so a bad reassign can never corrupt
    /// the framing or be persisted. On success the whole edited level still re-decodes to itself
    /// (the stability gate); only the touched record's name changes.</para></summary>
    public static bool SetEntitySprite(EntityEditHandle h, string? name)
    {
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0) return false;
        if (string.IsNullOrEmpty(name)) return false;
        if (name!.Length < 5 || name.Length > 64) return false;   // HeadAt nameLen credibility
        if (name.IndexOf('_') < 0) return false;                   // HeadAt requires an underscore
        foreach (char ch in name) if (ch < 32 || ch > 126) return false;

        var block = h.Sec.Block;
        if (block.Length == 0) return false;

        // Locate the target record's current byte start by re-walking the block to its count.
        var (heads, ok) = EntityFraming.SplitRecords(block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok || heads.Count != h.Sec.Count) return false;
        if (h.RecIndex < 0 || h.RecIndex >= heads.Count) return false;

        int recStart = heads[h.RecIndex].RecStart;
        int oldLen = BitConverter.ToInt32(block, recStart);
        if (oldLen < 5 || oldLen > 64) return false;
        int nameFieldEnd = recStart + 4 + oldLen;                   // covers int32 + name + NUL
        if (nameFieldEnd > block.Length) return false;

        // Build the replacement name field: [int32 newLen][name][0x00].
        var nb = Latin1(name!);
        int newLen = nb.Length + 1;
        var field = new byte[4 + newLen];
        BitConverter.GetBytes(newLen).CopyTo(field, 0);
        Buffer.BlockCopy(nb, 0, field, 4, nb.Length);
        field[^1] = 0x00;

        // Splice: [0, recStart) + new field + [nameFieldEnd, end). Instance + body follow.
        int tailLen = block.Length - nameFieldEnd;
        var candidate = new byte[recStart + field.Length + tailLen];
        Buffer.BlockCopy(block, 0, candidate, 0, recStart);
        Buffer.BlockCopy(field, 0, candidate, recStart, field.Length);
        Buffer.BlockCopy(block, nameFieldEnd, candidate, recStart + field.Length, tailLen);

        // Hard safety gate, and it is the loading code's own read order that decides it. ElementEntity
        // pulls `count` records straight off one cursor, so after a name of a different length every later
        // record starts exactly `delta` bytes further on: verify THOSE offsets, each still reading its own
        // prefab name and chained position. Re-scanning with SplitBlock instead lets a heuristic overrule
        // the format — a new name whose bytes look like a credible head would re-tile the block and get a
        // perfectly good edit refused (the game never scans, so it would have read it fine).
        int delta = candidate.Length - block.Length;
        if (FrameCheck(candidate, heads, h.RecIndex, delta, name) is not null) return false;
        // Where the grammar explains every record, it is the stricter judge: the renamed run must still
        // measure back to back to the exact end of the block.
        int m0 = MeasuredEnd(block, h.Sec.Count, h.Lv.Game);
        if (m0 == block.Length && MeasuredEnd(candidate, h.Sec.Count, h.Lv.Game) != candidate.Length) return false;

        h.Sec.Block = candidate;
        return true;
    }

    // Byte end the reading rules arrive at for a whole entity block (records back to back), or -1 when some
    // record carries bytes this game's ElementEntity::readBinary does not name. See EntityFraming.MeasureBlock.
    private static int MeasuredEnd(byte[] blk, int count, string? gameKey)
        => EntityFraming.MeasureBlock(blk, 0, count, gameKey, out int e) ? e : -1;

    /// <summary>The framing gate every length-changing entity write shares, phrased the way the game reads:
    /// records come back to back off one cursor, so each one after the edited record must still be found at
    /// exactly its old offset plus <paramref name="delta"/>, carrying its own prefab name and its chained
    /// position (<paramref name="editedName"/> replaces the edited record's). Null when the candidate frames,
    /// otherwise the reason it does not, for the status line. This deliberately does NOT re-run the head
    /// <em>search</em>: a search is a guess about where heads are, and a record whose bytes happen to look
    /// like a head would then overrule the read order the loader actually follows.</summary>
    private static string? FrameCheck(byte[] cand, List<EntityHead> heads, int edited, int delta, string? editedName)
    {
        for (int i = 0; i < heads.Count; i++)
        {
            int at = heads[i].RecStart + (i > edited ? delta : 0);
            var hi = EntityFraming.HeadAt(cand, at, cand.Length);
            string want = i == edited && editedName is not null ? editedName : heads[i].Name;
            if (hi is null) return $"the new length leaves no record where record {i + 1} should be";
            if (hi.Value.Name != want) return $"the game would now read record {i + 1} as \u201c{hi.Value.Name}\u201d";
            if (hi.Value.X != heads[i].X || hi.Value.Y != heads[i].Y)
                return $"the game would now read record \u201c{want}\u201d somewhere else";
        }
        return null;
    }

    // ============================================================ deco move / rotate ==
    //
    // Element (Forever / Final Bite) decorations share Tasty Blue's record shape: a fixed
    // head then chained <c>int32 _dx, _dy</c> (centiunits, /100) and an absolute <c>int32
    // size.raw</c> rotation. ElementSpanCodec.Encode re-emits each record verbatim, so these
    // are length-preserving in-place int32 splices on <c>Sec.Records[i]</c> — move compensates
    // the successor record in the SAME section (the chain is scoped per decoration section, as
    // SceneProjector.ProjectDecoSection walks it), rotation writes only the touched record.

    /// <summary>Translate an ElementEngine decoration by a world delta, compensating the next
    /// record in its section so later decorations keep their place. False (nothing changed) when
    /// the handle is out of range or a record head is unparseable / too short.</summary>
    public static bool MoveDeco(DecoEditHandle h, double dxWorld, double dyWorld)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return false;
        var rec = h.Sec.Records[h.RecIndex];
        int hl = HeadLength(rec);
        if (hl < 0 || hl + 12 > rec.Length) return false;

        int dcx = (int)Math.Round(dxWorld * 100);
        int dcy = (int)Math.Round(dyWorld * 100);
        if (dcx == 0 && dcy == 0) return true;

        WriteI32(rec, hl, ReadI32(rec, hl) + dcx);
        WriteI32(rec, hl + 4, ReadI32(rec, hl + 4) + dcy);

        if (h.RecIndex + 1 < h.Sec.Records.Count)
        {
            var nx = h.Sec.Records[h.RecIndex + 1];
            int h2 = HeadLength(nx);
            if (h2 >= 0 && h2 + 8 <= nx.Length)
            {
                WriteI32(nx, h2, ReadI32(nx, h2) - dcx);
                WriteI32(nx, h2 + 4, ReadI32(nx, h2 + 4) - dcy);
            }
        }
        return true;
    }

    /// <summary>Move an ElementEngine decoration to an absolute world position (inspector X / Y),
    /// expressed as the delta from its chain-resolved current position.</summary>
    public static bool SetDecoPos(DecoEditHandle h, double wx, double wy)
    {
        var (ax, ay) = DecoAbsolute(h);
        return MoveDeco(h, wx - ax, wy - ay);
    }

    /// <summary>The decoration's absolute world position, walking the <c>_dx</c>/<c>_dy</c> chain
    /// down its section exactly like <see cref="Render.SceneProjector"/>'s ProjectDecoSection
    /// (centiunits summed, /100). Stops early at an unparseable record, matching the projector's
    /// bail so the value agrees with what the viewport drew.</summary>
    public static (double X, double Y) DecoAbsolute(DecoEditHandle h)
    {
        double x = 0, y = 0;
        if (h.Sec is null) return (0, 0);
        var recs = h.Sec.Records;
        for (int i = 0; i <= h.RecIndex && i < recs.Count; i++)
        {
            int hl = HeadLength(recs[i]);
            if (hl < 0 || hl + 8 > recs[i].Length) break;
            x += ReadI32(recs[i], hl);
            y += ReadI32(recs[i], hl + 4);
        }
        return (x * 0.01, y * 0.01);
    }

    /// <summary>The decoration's current rotation in degrees (<c>-size.raw / 100</c>, matching
    /// the projector's <c>RotationDeg</c>), or 0 when the record cannot be read.</summary>
    public static double DecoRotationDeg(DecoEditHandle h)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return 0;
        var rec = h.Sec.Records[h.RecIndex];
        int hl = HeadLength(rec);
        if (hl < 0 || hl + 12 > rec.Length) return 0;
        return -ReadI32(rec, hl + 8) / 100.0;
    }

    /// <summary>Set an ElementEngine decoration's absolute rotation in degrees (int32
    /// <c>size.raw = round(-deg * 100)</c>). Length-preserving; touches only this record.</summary>
    public static bool SetDecoRotationDeg(DecoEditHandle h, double deg)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return false;
        var rec = h.Sec.Records[h.RecIndex];
        int hl = HeadLength(rec);
        if (hl < 0 || hl + 12 > rec.Length) return false;
        WriteI32(rec, hl + 8, (int)Math.Round(-deg * 100));
        return true;
    }

    private static int ReadI32(byte[] b, int off) => BitConverter.ToInt32(b, off);
    private static void WriteI32(byte[] b, int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);

    // ============================================================ deco flip (S-D13) ==
    //
    // Same record layout the projector reads (SceneProjector.TryReadElementDeco): the gate byte at
    // head+12 selects whether the 24-byte block at head+13 exists, and its first two int32 are
    // ElementDecoration::setFlipX / setFlipY (readBoolean consumes a whole word and tests == 1).
    // ElementDecoration::writeBinary is the writer's rule: gate 0 only when both flags AND the four
    // clip ints are all default, else gate 1 + writeBoolean, writeBoolean, four writeInt. A flip
    // edit therefore legitimately resizes the record — which for these families matters because an
    // Forever / Final Bite decoration section is ONE opaque block, so the caller re-projects.

    /// <summary>The record's current flip flags, read straight from its bytes (false when there is
    /// no block or the head cannot be parsed).</summary>
    public static (bool X, bool Y) DecoFlip(DecoEditHandle h)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return (false, false);
        var rec = h.Sec.Records[h.RecIndex];
        int hl = HeadLength(rec);
        if (hl < 0 || hl + 13 > rec.Length || rec[hl + 12] == 0) return (false, false);
        int bo = hl + 13;
        if (bo + 24 > rec.Length) return (false, false);
        return (ReadI32(rec, bo) == 1, ReadI32(rec, bo + 4) == 1);
    }

    /// <summary>Set an ElementEngine decoration's flip flags, creating the optional block when the
    /// record has none and dropping it again when the last non-default word returns to zero (the
    /// engine's own writeBinary rule). <paramref name="resized"/> reports whether the record's byte
    /// length changed — the caller must then re-project, because every later record in the section
    /// block shifts. False leaves the record untouched.</summary>
    public static bool SetDecoFlip(DecoEditHandle h, bool fx, bool fy, out bool resized)
    {
        resized = false;
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return false;
        var rec = h.Sec.Records[h.RecIndex];
        int hl = HeadLength(rec);
        if (hl < 0 || hl + 13 > rec.Length) return false;
        int bo = hl + 13;
        bool has = rec[hl + 12] != 0;
        if (has && bo + 24 > rec.Length) return false;      // truncated block: refuse

        if (has)
        {
            int clip = ReadI32(rec, bo + 8) | ReadI32(rec, bo + 12) | ReadI32(rec, bo + 16) | ReadI32(rec, bo + 20);
            WriteI32(rec, bo, fx ? 1 : 0);
            WriteI32(rec, bo + 4, fy ? 1 : 0);
            if (clip == 0 && !fx && !fy && rec[hl + 12] == 1)
            {
                var nv = new byte[rec.Length - 24];
                Buffer.BlockCopy(rec, 0, nv, 0, bo);
                Buffer.BlockCopy(rec, bo + 24, nv, bo, rec.Length - bo - 24);
                nv[hl + 12] = 0;
                h.Sec.Records[h.RecIndex] = nv;
                resized = true;
            }
            return true;
        }

        if (!fx && !fy) return true;                        // nothing to store

        var nb = new byte[rec.Length + 24];
        Buffer.BlockCopy(rec, 0, nb, 0, bo);
        BitConverter.GetBytes(fx ? 1 : 0).CopyTo(nb, bo);
        BitConverter.GetBytes(fy ? 1 : 0).CopyTo(nb, bo + 4);
        Buffer.BlockCopy(rec, bo, nb, bo + 24, rec.Length - bo);
        nb[hl + 12] = 1;
        h.Sec.Records[h.RecIndex] = nb;
        resized = true;
        return true;
    }

    public static bool SetDecoFlip(DecoEditHandle h, bool fx, bool fy)
        => SetDecoFlip(h, fx, fy, out _);

    // ============================================================ entity move / rotate ==
    //
    // An ElementEngine (Forever / Final Bite) entity section is ONE opaque block; the framing
    // oracle re-derives its record boundaries by walking EntityFraming, and the encoder writes
    // the block back verbatim (no position re-derivation like tb's PatchEntityPositions). So the
    // entity edits mirror the decoration ones above but splice <c>Sec.Block</c> at a walked
    // head's <c>xy_off</c>:
    //   * positions chain down the block in WHOLE world units (<c>ax += head.X</c>), so a move
    //     adds the rounded delta to this record's dx/dy and subtracts it from the NEXT record to
    //     pin every later entity (the projector's ProjectEntitySection runs the same sum);
    //   * the int32 centidegree rotation sits at <c>xy_off + EntityFraming.Rot</c> (absolute, no
    //     cascade) — writing it touches only the four bytes of the one record.
    // Both are length-preserving in-place splices (I1 holds, I3 localises), and each re-walks the
    // block to the current offsets so it stays correct after a name reassign (SetEntitySprite).

    /// <summary>The entity's absolute world position, summing the chained <c>X</c>/<c>Y</c>
    /// record deltas down its block exactly like the projector's ProjectEntitySection (whole
    /// world units). (0,0) when the block cannot be walked to its count.</summary>
    public static (double X, double Y) EntityAbsolute(EntityEditHandle h)
    {
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0) return (0, 0);
        var (heads, ok) = EntityFraming.SplitRecords(h.Sec.Block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok) return (0, 0);
        double x = 0, y = 0;
        for (int i = 0; i <= h.RecIndex && i < heads.Count; i++) { x += heads[i].X; y += heads[i].Y; }
        return (x, y);
    }

    /// <summary>Translate an ElementEngine entity by a world delta, compensating the next record
    /// in its block so later entities keep their place. False (nothing changed) when the block
    /// will not walk to its count or the record / successor offsets are out of range.</summary>
    public static bool MoveEntity(EntityEditHandle h, double dxWorld, double dyWorld)
    {
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0) return false;
        var (heads, ok) = EntityFraming.SplitRecords(h.Sec.Block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok || heads.Count != h.Sec.Count) return false;
        if (h.RecIndex < 0 || h.RecIndex >= heads.Count) return false;

        int dcx = (int)Math.Round(dxWorld);
        int dcy = (int)Math.Round(dyWorld);
        if (dcx == 0 && dcy == 0) return true;   // sub-unit: below the format's resolution

        var block = h.Sec.Block;
        int xo = heads[h.RecIndex].XyOff;
        if (xo + 8 > block.Length) return false;
        WriteI32(block, xo, ReadI32(block, xo) + dcx);
        WriteI32(block, xo + 4, ReadI32(block, xo + 4) + dcy);

        if (h.RecIndex + 1 < heads.Count)
        {
            int nx = heads[h.RecIndex + 1].XyOff;
            if (nx + 8 <= block.Length)
            {
                WriteI32(block, nx, ReadI32(block, nx) - dcx);
                WriteI32(block, nx + 4, ReadI32(block, nx + 4) - dcy);
            }
        }
        return true;
    }

    /// <summary>Move an ElementEngine entity to an absolute world position (inspector X / Y),
    /// expressed as the delta from its chain-resolved current position.</summary>
    public static bool SetEntityPos(EntityEditHandle h, double wx, double wy)
    {
        var (ax, ay) = EntityAbsolute(h);
        return MoveEntity(h, wx - ax, wy - ay);
    }

    /// <summary>The entity's current rotation in degrees (<c>-raw / 100</c>, matching the
    /// projector's <c>RotationDeg</c>), or 0 when the block / field cannot be read.</summary>
    public static double EntityRotationDeg(EntityEditHandle h)
    {
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0) return 0;
        var (heads, ok) = EntityFraming.SplitRecords(h.Sec.Block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok || h.RecIndex < 0 || h.RecIndex >= heads.Count) return 0;
        // f4/f5 insert the two extra int32 BEFORE the turn, so the rotation lives at xy_off + Rot + ExtF4F5.
        int ro = heads[h.RecIndex].XyOff + EntityFraming.Rot + EntityFraming.ExtF4F5;
        if (ro + 4 > h.Sec.Block.Length) return 0;
        return -ReadI32(h.Sec.Block, ro) / 100.0;
    }

    /// <summary>The record's per-instance flip flag (the first word of the optional box block, the value
    /// ElementEntity::drawBase uses to negate the authored angle and mirror the sprite) — the same flag the
    /// projector needs to reproduce the game's facing. Frames the record and reads it off its
    /// <see cref="EntityFraming.EntDisplay"/>, exactly like the load path.</summary>
    public static bool EntityFlip(EntityEditHandle h)
    {
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0) return false;
        var (heads, ok) = EntityFraming.SplitRecords(h.Sec.Block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok || h.RecIndex < 0 || h.RecIndex >= heads.Count) return false;
        int recEnd = h.RecIndex + 1 < heads.Count ? heads[h.RecIndex + 1].RecStart : h.Sec.Block.Length;
        var disp = EntityFraming.ReadBody(h.Sec.Block, heads[h.RecIndex].XyOff, EntityFraming.ExtF4F5, recEnd);
        return disp.Flip;
    }

    /// <summary>Set an ElementEngine entity's absolute rotation in degrees (int32 centidegree
    /// <c>= round(-deg * 100)</c> at <c>xy_off + EntityFraming.Rot</c>). Length-preserving;
    /// touches only this record.</summary>
    public static bool SetEntityRotationDeg(EntityEditHandle h, double deg)
    {
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0) return false;
        var (heads, ok) = EntityFraming.SplitRecords(h.Sec.Block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok || heads.Count != h.Sec.Count) return false;
        if (h.RecIndex < 0 || h.RecIndex >= heads.Count) return false;
        // Match the read path: f4/f5 rotation is at xy_off + Rot + ExtF4F5 (see EntityFraming.ReadBody).
        int ro = heads[h.RecIndex].XyOff + EntityFraming.Rot + EntityFraming.ExtF4F5;
        if (ro + 4 > h.Sec.Block.Length) return false;
        WriteI32(h.Sec.Block, ro, (int)Math.Round(-deg * 100));
        return true;
    }

    // ==================================================== entity / deco field parity ==
    //
    // Forever / Final Bite entities and decorations get the same editable mass / priority /
    // colour (entity) and size / colour (deco) as the other families. Every field is fixed-width
    // inside the lossless raw carrier (the entity section's opaque block, or a deco record's
    // bytes) that ElementSpanCodec re-emits verbatim, so each edit is a length-preserving in-place
    // splice — the entity ones re-walking the block to the record's current head first (a prior
    // name reassign shifts offsets). The variable per-prefab tails (path / emitter / persist) stay
    // untouched, exactly as the Python inspector's entity_full_body gate leaves them.

    private const double DimBase = 16000.0;   // deco dimensions.raw -> scale 1.0 (tbeditor.DIM_BASE)

    private static void WriteF64(byte[] b, int off, double v) => BitConverter.GetBytes(v).CopyTo(b, off);

    private static bool IsF5(ElementLevel lv) => lv != null && lv.SectionsPerLayer >= 8;

    // Resolve one entity record's colour / mass / priority offsets by walking its section block
    // to the record head (ext=8: the two extra int32 f4/f5 insert before has_box). False when the
    // block won't walk to its count or the record body isn't addressable.
    private static bool EntityFields(EntityEditHandle h, out int colorOff, out int massOff, out int prioOff)
    {
        colorOff = massOff = prioOff = -1;
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0) return false;
        var block = h.Sec.Block;
        var (heads, ok) = EntityFraming.SplitRecords(block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok || heads.Count != h.Sec.Count) return false;
        if (h.RecIndex < 0 || h.RecIndex >= heads.Count) return false;
        int recEnd = h.RecIndex + 1 < heads.Count ? heads[h.RecIndex + 1].RecStart : block.Length;
        return EntityFraming.BodyOffsets(block, heads[h.RecIndex].XyOff, EntityFraming.ExtF4F5, recEnd,
                                         out colorOff, out massOff, out prioOff);
    }

    public static double? EntityMass(EntityEditHandle h)
        => EntityFields(h, out _, out int mo, out _) ? BitConverter.ToDouble(h.Sec.Block, mo) : (double?)null;

    public static int? EntityPriority(EntityEditHandle h)
        => EntityFields(h, out _, out _, out int po) ? ReadI32(h.Sec.Block, po) : (int?)null;

    public static byte[]? EntityColor(EntityEditHandle h)
    {
        if (!EntityFields(h, out int co, out _, out _)) return null;
        var b = h.Sec.Block;
        return new[] { b[co], b[co + 1], b[co + 2], b[co + 3] };
    }

    public static bool SetEntityMass(EntityEditHandle h, double mass)
    {
        if (!EntityFields(h, out _, out int mo, out _)) return false;
        WriteF64(h.Sec.Block, mo, mass);
        return true;
    }

    public static bool SetEntityPriority(EntityEditHandle h, int prio)
    {
        if (!EntityFields(h, out _, out _, out int po)) return false;
        WriteI32(h.Sec.Block, po, prio);
        return true;
    }

    public static bool SetEntityColor(EntityEditHandle h, byte r0, byte g0, byte b0, byte a0)
    {
        if (!EntityFields(h, out int co, out _, out _)) return false;
        var blk = h.Sec.Block;
        blk[co] = r0; blk[co + 1] = g0; blk[co + 2] = b0; blk[co + 3] = a0;
        return true;
    }

    /// <summary>Resolve the raw behaviour tail of a Forever / Final Bite entity record for the lane
    /// editor: the shared section block (which <c>ElementSpanCodec</c> re-emits verbatim), the
    /// absolute offset of the first tail byte (<c>prioOff + 4</c>) and the tail length up to THIS
    /// record's end (the next head, or block end for the last). The walk re-splits the block so a
    /// prior name reassign is accounted for. Lane writes are clamped to <c>baseAbs .. baseAbs + len</c>
    /// by <see cref="EntityFraming.WriteLaneInt"/> / <see cref="EntityFraming.WriteLaneF64"/>, so
    /// they can never spill into the following record (I1 holds; the edit localises, I3).</summary>
    public static bool EntityTailSpan(EntityEditHandle h, out byte[] buf, out int baseAbs, out int len)
    {
        buf = Array.Empty<byte>(); baseAbs = len = -1;
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0) return false;
        var block = h.Sec.Block;
        var (heads, ok) = EntityFraming.SplitRecords(block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok || heads.Count != h.Sec.Count) return false;
        if (h.RecIndex < 0 || h.RecIndex >= heads.Count) return false;
        int recEnd = h.RecIndex + 1 < heads.Count ? heads[h.RecIndex + 1].RecStart : block.Length;
        if (!EntityFraming.BodyOffsets(block, heads[h.RecIndex].XyOff, EntityFraming.ExtF4F5, recEnd,
                                       out _, out _, out int po)) return false;
        buf = block;
        baseAbs = EntityFraming.TailStart(po);
        len = recEnd - baseAbs;
        return len >= 0;
    }

    /// <summary>Structural (length-changing) edit of one entity record's behaviour tail: turning a gated
    /// behaviour block on or off, renaming the engine name it embeds, or resizing its IndexedCellColours
    /// ramp (see <see cref="TailEdit"/>). Unlike the lane writes above this resizes the record, so it is
    /// guarded twice over — <see cref="TailEdit.Reshape"/> refuses a tail that no longer decodes to
    /// itself, and the spliced block is only committed when every other record still sits exactly where the
    /// loading code will look for it (its old offset plus the size change) and still reads its own prefab
    /// name and chained position, the whole block measuring back to back to its end. An edited level is
    /// EXPECTED to be a different byte length from the stock one — the format carries no offset table, so
    /// growing a record is normal — what must not change is any other record. On failure the block is
    /// byte-for-byte untouched. The section block is re-emitted verbatim by <c>ElementSpanCodec</c> and its
    /// end is re-derived by the same read-order measure on load, so a resized record needs no other fix-up
    /// (I1 holds for an untouched level; the edit localises to the touched record, I3).</summary>
    public static bool ReshapeTail(EntityEditHandle h, TailField f, object? value) => ReshapeTail(h, f, value, out _);

    /// <summary>The same write reporting the reason it was refused ("" on success), so the editor can tell
    /// the user what the game's loader objects to rather than just snapping the row back.</summary>
    public static bool ReshapeTail(EntityEditHandle h, TailField f, object? value, out string why)
    {
        why = "";
        if (h.Sec is null || !h.Sec.Opaque || h.Sec.Count <= 0)
        { why = "this section's records are not individually addressable"; return false; }
        var block = h.Sec.Block;
        var (heads, ok) = EntityFraming.SplitRecords(block, 0, h.Sec.Count, h.Lv.Game);
        if (!ok || heads.Count != h.Sec.Count) { why = "the game's own record framing does not read this section cleanly"; return false; }
        if (h.RecIndex < 0 || h.RecIndex >= heads.Count) { why = "this record is no longer in its section"; return false; }
        if (!TailOf(block, heads, h.RecIndex, EntityFraming.ExtF4F5, out int baseAbs, out int len))
        { why = "this record's behaviour tail is too short to rebuild"; return false; }

        var tail = new byte[len];
        Buffer.BlockCopy(block, baseAbs, tail, 0, len);
        var next = TailEdit.Reshape(tail, h.Lv.Game, f, value);
        if (next is null) { why = TailEdit.Explain(tail, h.Lv.Game, f, value); return false; }

        // Splice the reshaped tail into the shared section block: [0, tail) + new tail + [tail end, ...).
        int rest = block.Length - baseAbs - len;
        var cand = new byte[block.Length - len + next.Length];
        Buffer.BlockCopy(block, 0, cand, 0, baseAbs);
        Buffer.BlockCopy(next, 0, cand, baseAbs, next.Length);
        Buffer.BlockCopy(block, baseAbs + len, cand, baseAbs + next.Length, rest);

        // Framing gate, on the candidate: the records the loader reads back to back, at their shifted
        // offsets, each still its own name and position (see FrameCheck for why this beats a re-scan).
        int delta = next.Length - len;
        if (FrameCheck(cand, heads, h.RecIndex, delta, null) is string bad)
        { why = bad; return false; }

        // Where the grammar explains every record it is the stricter judge: the resized run has to measure
        // back to back to exactly the end of the block, which is also the end the codec re-finds on load.
        int m0 = MeasuredEnd(block, h.Sec.Count, h.Lv.Game);
        if (m0 == block.Length && MeasuredEnd(cand, h.Sec.Count, h.Lv.Game) != cand.Length)
        { why = "after the change the records here would no longer read back to back to the end of the block"; return false; }

        // And the edited record's tail must still be fully explained, read straight out of the candidate.
        int nl = len + delta;
        var (cons, complete) = TailSem.Measure(cand, baseAbs, nl, h.Lv.Game);
        if (!complete || cons != nl)
        { why = $"after the change {nl - cons} byte(s) of this record would never be read"; return false; }

        h.Sec.Block = cand;
        return true;
    }

    // Absolute tail base + length of record `idx`, given an already-split head list. False when the
    // record body isn't addressable or its tail is too short to hold even one gate.
    private static bool TailOf(byte[] blk, List<EntityHead> hs, int idx, int ext,
                               out int baseAbs, out int len)
    {
        baseAbs = len = -1;
        int recEnd = idx + 1 < hs.Count ? hs[idx + 1].RecStart : blk.Length;
        if (!EntityFraming.BodyOffsets(blk, hs[idx].XyOff, ext, recEnd, out _, out _, out int po)) return false;
        baseAbs = EntityFraming.TailStart(po);
        len = recEnd - baseAbs;
        return len >= 4;
    }

    // Colour + dimension offsets inside an element decoration record: same head / extra-flag
    // walk as TryReadElementDeco, with the Final Bite-only int32 sitting between colour and
    // dimensions. dim0 = dimensions.raw[0] (uniform scale), dim1 = dimensions.raw[1].
    private static bool DecoFields(byte[] rec, bool isF5, out int colorOff, out int dimWOff, out int dimHOff)
    {
        colorOff = dimWOff = dimHOff = -1;
        int hl = HeadLength(rec);
        if (hl < 0 || hl + 13 > rec.Length) return false;
        int extra = rec[hl + 12];
        int c = hl + 13 + (extra != 0 ? 24 : 0);
        if (c + 4 > rec.Length) return false;                 // colour fits
        int d0 = c + 4 + (isF5 ? 4 : 0);                       // fb_extra sits after colour
        if (d0 + 8 > rec.Length) return false;                 // dim0 + dim1 int32s fit
        colorOff = c; dimWOff = d0; dimHOff = d0 + 4;
        return true;
    }

    public static double? DecoSize(DecoEditHandle h)
        => h.Sec is not null && h.RecIndex >= 0 && h.RecIndex < h.Sec.Records.Count
           && DecoFields(h.Sec.Records[h.RecIndex], IsF5(h.Lv), out _, out int wo, out _)
            ? (double)ReadI32(h.Sec.Records[h.RecIndex], wo) / DimBase : (double?)null;

    /// <summary>Both <c>dimensions.raw</c> int32s of a decoration record. They are the world box the
    /// sprite is stretched to, in the record's own centiunits (world = raw/100) - the renderer needs
    /// the pair, where <see cref="DecoSize"/> only exposes the editable single knob. Null when the
    /// record is too short to hold both.</summary>
    public static (int W, int H)? DecoDims(DecoEditHandle h)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return null;
        var rec = h.Sec.Records[h.RecIndex];
        if (!DecoFields(rec, IsF5(h.Lv), out _, out int wo, out int ho)) return null;
        return (ReadI32(rec, wo), ReadI32(rec, ho));
    }

    public static byte[]? DecoColor(DecoEditHandle h)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return null;
        var rec = h.Sec.Records[h.RecIndex];
        if (!DecoFields(rec, IsF5(h.Lv), out int co, out _, out _)) return null;
        return new[] { rec[co], rec[co + 1], rec[co + 2], rec[co + 3] };
    }

    public static bool SetDecoSize(DecoEditHandle h, double scale)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return false;
        var rec = h.Sec.Records[h.RecIndex];
        if (!DecoFields(rec, IsF5(h.Lv), out _, out int wo, out int ho)) return false;
        int ox = ReadI32(rec, wo);
        int oy = ReadI32(rec, ho);
        if (ox == 0) ox = (int)DimBase;
        double aspect = (double)oy / ox;
        int nx = (int)Math.Round(scale * DimBase);
        if (nx == 0) nx = 1;
        int ny = (int)Math.Round(nx * aspect);
        WriteI32(rec, wo, nx);
        WriteI32(rec, ho, ny);
        return true;
    }

    /// <summary>Free per-axis decoration resize (the viewport's Shift-drag grip): writes both
    /// <c>dimensions.raw</c> ints independently, one per axis — the record stores two fields, so
    /// unlike <see cref="SetDecoSize"/> this changes the sprite's aspect. Each axis is clamped to
    /// a minimum of 1 so an inverted drag can never zero or flip a dimension.</summary>
    public static bool SetDecoAxes(DecoEditHandle h, double scaleW, double scaleH)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return false;
        var rec = h.Sec.Records[h.RecIndex];
        if (!DecoFields(rec, IsF5(h.Lv), out _, out int wo, out int ho)) return false;
        WriteI32(rec, wo, Math.Max(1, (int)Math.Round(scaleW * DimBase)));
        WriteI32(rec, ho, Math.Max(1, (int)Math.Round(scaleH * DimBase)));
        return true;
    }

    public static bool SetDecoColor(DecoEditHandle h, byte r0, byte g0, byte b0, byte a0)
    {
        if (h.Sec is null || h.RecIndex < 0 || h.RecIndex >= h.Sec.Records.Count) return false;
        var rec = h.Sec.Records[h.RecIndex];
        if (!DecoFields(rec, IsF5(h.Lv), out int co, out _, out _)) return false;
        rec[co] = r0; rec[co + 1] = g0; rec[co + 2] = b0; rec[co + 3] = a0;
        return true;
    }

    /// <summary>Byte length of a decoration record's head (everything before the chained
    /// <c>dx</c> int32). Identical layout to Tasty Blue: 1 (type-0), 5 (type-1 cell) or
    /// <c>1 + 4 + nameLen</c> (type-2 inline). -1 when uncredible.</summary>
    private static int HeadLength(byte[] rec)
    {
        if (rec.Length < 1) return -1;
        int type = rec[0];
        if (type == 0x00) return 1;
        if (type == 0x01) return rec.Length >= 5 ? 5 : -1;
        if (type == 0x02)
        {
            if (rec.Length < 5) return -1;
            int n = BitConverter.ToInt32(rec, 1);
            if (n < 0 || 5L + n > rec.Length) return -1;
            return 5 + n;
        }
        return -1;
    }

    /// <summary>Rewrite a record as <c>[0x01][int32 cell]</c> + the untouched tail from
    /// <paramref name="tailOffset"/>. The tail (including Final Bite's extra int32) is
    /// copied byte-for-byte, so the edit never disturbs fields after the head.</summary>
    private static byte[] BuildIndexedHead(int cell, byte[] rec, int tailOffset)
    {
        int tailLen = rec.Length - tailOffset;
        var nv = new byte[5 + tailLen];
        nv[0] = 0x01;
        BitConverter.GetBytes(cell).CopyTo(nv, 1);
        Buffer.BlockCopy(rec, tailOffset, nv, 5, tailLen);
        return nv;
    }

    /// <summary>Resolve the effective sprite name of the record at <paramref name="upTo"/> by
    /// walking the section's run-length chain, exactly like
    /// <see cref="Render.SceneProjector"/>'s <c>ProjectDecoSection</c>: type 2 = inline string,
    /// type 1 = tileTypes[cell], type 0 = inherit the previous resolved name.</summary>
    private static string? ResolveNameAt(DecoEditHandle h, int upTo)
    {
        string? last = null;
        var tiles = h.Lv.TileTypes;
        for (int i = 0; i <= upTo && i < h.Sec.Records.Count; i++)
        {
            var rec = h.Sec.Records[i];
            if (rec.Length < 1) continue;
            string? nm = null;
            switch (rec[0])
            {
                case 0x02:
                    if (rec.Length >= 5)
                    {
                        int n = BitConverter.ToInt32(rec, 1);
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
                        int cell = BitConverter.ToInt32(rec, 1);
                        if (cell >= 0 && cell < tiles.Count) nm = tiles[cell];
                    }
                    break;
                default:
                    nm = last;   // type 0 = inherit previous
                    break;
            }
            if (nm is not null) last = nm;
        }
        return last;
    }

    /// <summary>Index of <paramref name="name"/> in the level's <c>tileTypes</c> table,
    /// appending it and a parallel <c>TilesRaw</c> entry when absent. Unlike Tasty Blue (whose
    /// raw entries carry their own int32 length), the element codec stores the length
    /// separately — <c>(Len, Raw)</c> where <c>Raw</c> excludes the prefix and the encoder
    /// writes <c>Wi(Len); w.Write(Raw)</c> — so the payload here is just the NUL-terminated
    /// latin-1 name bytes.</summary>
    private static int TileTypeIndex(ElementLevel lv, string name)
    {
        for (int i = 0; i < lv.TileTypes.Count; i++)
            if (lv.TileTypes[i] == name) return i;
        var payload = BuildElemTilePayload(name);
        lv.TileTypes.Add(name);
        lv.TilesRaw.Add((payload.Length, payload));
        return lv.TileTypes.Count - 1;
    }

    private static byte[] BuildElemTilePayload(string name)
    {
        var nb = Latin1(name);
        var payload = new byte[nb.Length + 1];
        Buffer.BlockCopy(nb, 0, payload, 0, nb.Length);
        payload[^1] = 0x00;
        return payload;
    }

    private static byte[] Latin1(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)(s[i] & 0xFF);
        return b;
    }
}
