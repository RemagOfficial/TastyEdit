using TastyEditor.Core.Streams;

namespace TastyEditor.Core.Levels;

/// <summary>One record head inside an entity block: absolute start, prefab name,
/// the chained int delta x/y, and the offset of the first coordinate int (xy_off).
/// Mirrors the tuple tb_ent_sem.head_at returns.</summary>
public readonly record struct EntityHead(int RecStart, string Name, int X, int Y, int XyOff);

/// <summary>Port of tb_ent_sem's solved entity-record HEAD scanner. The entity
/// section of Forever/Final Bite is one opaque block (prefab bodies are read by a
/// virtual Load, so lengths vary), but the record HEAD is shared and fixed:
/// <c>&lt;i32 nameLen&gt;&lt;prefab name NUL&gt;&lt;i32 instLen&gt;&lt;inst&gt;&lt;i32 dx&gt;&lt;i32 dy&gt;</c>.
/// split_block walks exactly <c>count</c> heads inside a candidate span, which is
/// the authoritative section count — this is the oracle that lets the EOF tiler
/// pick a record-exact entity-block end.</summary>
public static class EntityFraming
{
    public const int MinRec = 50;          // minimum bytes between two record heads
    public const int MaxXy = 10_000_000;   // |delta| bound on the chained position ints

    /// <summary>Parse a credible record head at absolute <paramref name="p"/>.
    /// Returns null (never throws) when the bytes there are not a valid head,
    /// exactly like the Python head_at which returns None.
    /// <paramref name="anyName"/> relaxes the name to "printable, NUL-terminated" for offsets the
    /// file itself names — a record start the loading cursor is sitting on. Those names are facts,
    /// not guesses, and Forever's real prefabs include <c>h2o</c>, <c>bedrock</c>, <c>airplanepath1</c>:
    /// short and underscore-free, which the search rule below would reject, costing a whole level its
    /// entity block. In the search (anyName false) the rule stays, because there a name is the only
    /// thing distinguishing a head from arbitrary bytes.</summary>
    public static EntityHead? HeadAt(byte[] d, int p, int endHard, bool anyName = false)
    {
        if (p + 4 > endHard) return null;
        int n = ByteCursor.ReadI32At(d, p);
        if (n < (anyName ? 2 : 5) || n > 64) return null;

        int sStart = p + 4;
        if (sStart + n > d.Length) return null;         // len(s) < n
        if (d[sStart + n - 1] != 0) return null;         // must end with NUL

        // core = s[:-1]; require '_' present and every byte printable ASCII (32..126)
        bool hasUnderscore = false;
        for (int i = 0; i < n - 1; i++)
        {
            byte c = d[sStart + i];
            if (c == (byte)'_') hasUnderscore = true;
            if (c < 32 || c > 126) return null;
        }
        if (!anyName && !hasUnderscore) return null;
        string core = System.Text.Encoding.ASCII.GetString(d, sStart, n - 1);

        int q = p + 4 + n;
        if (q + 4 > endHard) return null;
        int m = ByteCursor.ReadI32At(d, q);
        if (m < 0 || m > 64) return null;

        int instStart = q + 4;
        if (instStart + m > d.Length) return null;       // len(inst) < m
        // any(c<32||c>126 for c in inst.rstrip(b'\x00'))
        int instLen = m;
        while (instLen > 0 && d[instStart + instLen - 1] == 0) instLen--;
        for (int i = 0; i < instLen; i++)
        {
            byte c = d[instStart + i];
            if (c < 32 || c > 126) return null;
        }

        int r = q + 4 + m;
        if (r + 8 > endHard) return null;
        int x = ByteCursor.ReadI32At(d, r);
        int y = ByteCursor.ReadI32At(d, r + 4);
        if (Math.Abs(x) > MaxXy || Math.Abs(y) > MaxXy) return null;

        return new EntityHead(p, core, x, y, r);
    }

    /// <summary>Find exactly <paramref name="count"/> sequential record heads in
    /// d[start:limit]. Returns the heads and true, or (empty, false) when the
    /// block cannot be split to its count (the caller keeps lenient spans).</summary>
    public static (List<EntityHead> Heads, bool Ok) SplitBlock(byte[] d, int start, int count, int limit)
    {
        var heads = new List<EntityHead>(count);
        int p = start;
        while (heads.Count < count)
        {
            EntityHead? found = null;
            int q = p;
            while (q < limit)
            {
                found = HeadAt(d, q, limit);
                if (found.HasValue) break;
                q += 1;
            }
            if (found is null) return (new List<EntityHead>(), false);
            heads.Add(found.Value);
            p = found.Value.RecStart + MinRec;
        }
        return (heads, true);
    }

    // The f4/f5 entity body shares Tasty Blue's fixed-width run up through the
    // centidegree rotation, then inserts TWO extra int32 before the has_box byte.
    // base_offsets (tb_ent_sem) locates colour/mass/priority from there; the only
    // per-game difference is that extension (0 for tb, 8 for f4/f5).
    //
    // The two extra int32 sit BEFORE the rotation word (proven against shipped bytes:
    // f4/f5 levels carry their per-record centidegree turn at xy_off+36, while xy_off+28
    // is a pad that is always 0). So the rotation is the int32 immediately before has_box
    // at <c>xy_off + Rot + ext</c> — which is +28 for Tasty Blue (ext=0) and +36 for f4/f5.
    public const int Rot = 28;           // tb rotation int32 offset from the x int
    public const int ExtF4F5 = 8;        // the two extra int32 f4/f5 insert
    public const int ExtTb = 0;

    /// <summary>Read the fixed-width display fields of one entity record body. Mirrors
    /// <c>tb_ent_sem.base_offsets</c>: rotation at <c>xyOff+28</c>, then
    /// <c>has_box</c> at <c>xyOff+32+ext</c>, an optional 5-int32 box, then colour(4) /
    /// mass(f64) / priority(i32). Returns defaults with <c>HasBody=false</c> (never
    /// throws) when the shape isn't recognised, exactly like the Python returning None.</summary>
    public static EntDisplay ReadBody(byte[] d, int xyOff, int ext, int recEnd)
    {
        // Rotation is the int32 just before has_box (<c>xy_off + Rot + ext</c>): +28 on Tasty Blue,
        // +36 on f4/f5, whose two extra int32 precede the turn. has_box below is unchanged (ro + 4).
        int ro = xyOff + Rot + ext;
        int rot = ro + 4 <= d.Length ? ByteCursor.ReadI32At(d, ro) : 0;
        int hb = ro + 4;
        if (hb >= d.Length || hb >= recEnd)
            return new EntDisplay(rot, DefaultColor(), 1.0, 0, false, false);
        byte hasBox = d[hb];
        if (hasBox > 1)
            return new EntDisplay(rot, DefaultColor(), 1.0, 0, false, false);
        // The optional block after the presence byte is [flip][clip x/y/w/h]. drawBase reads the FIRST
        // field through BinaryFile::readBoolean (which returns true only for the byte value 1) as the
        // per-instance flip flag:  spriteAngle = flag ? -recordAngle : recordAngle;  setFlip(false, flag).
        // So this single byte — not any move/path heuristic — decides an entity's facing mirror.
        int p = hb + 1;
        bool flip = hasBox != 0 && p < d.Length && d[p] == 1;
        if (hasBox != 0) p += 20;                        // box = 5 x int32
        if (p + 4 + 8 + 4 > recEnd || p + 16 > d.Length)
            return new EntDisplay(rot, DefaultColor(), 1.0, 0, false, false);
        var col = new byte[] { d[p], d[p + 1], d[p + 2], d[p + 3] };
        double mass = BitConverter.ToDouble(d, p + 4);
        int prio = ByteCursor.ReadI32At(d, p + 12);
        return new EntDisplay(rot, col, mass, prio, true, flip);
    }

    /// <summary>Locate the fixed-width colour / mass / priority offsets of one entity body,
    /// mirroring <c>tb_ent_sem.base_offsets</c> (and the traversal in <see cref="ReadBody"/>).
    /// The three fields sit BEFORE the variable-length move / path / area / emitter tail, so once
    /// <c>has_box</c> is read their absolute offsets are fixed and independent of anything that
    /// follows — which is what lets a mass / colour / priority edit splice in place without changing
    /// the record length (I1 holds; the edit localises, I3). Returns false — leaving the bytes
    /// untouched — when the record is too short, <c>has_box</c> isn't 0/1, or a field would run past
    /// <paramref name="recEnd"/>.</summary>
    public static bool BodyOffsets(byte[] d, int xyOff, int ext, int recEnd,
                                   out int colorOff, out int massOff, out int prioOff)
    {
        colorOff = massOff = prioOff = -1;
        int hb = xyOff + Rot + 4 + ext;
        if (hb < 0 || hb >= d.Length || hb >= recEnd) return false;
        byte hasBox = d[hb];
        if (hasBox > 1) return false;
        int p = hb + 1 + (hasBox != 0 ? 20 : 0);      // box = 5 x int32
        if (p + 4 + 8 + 4 > recEnd || p + 16 > d.Length) return false;   // colour(4)+mass(8)+prio(4)
        colorOff = p; massOff = p + 4; prioOff = p + 12;
        return true;
    }

    private static byte[] DefaultColor() => new byte[] { 255, 255, 255, 255 };

    // ------------------------------------------- deterministic record measure --
    //
    // The head scanner above is a CREDIBILITY search — it is what lets the EOF tiler find an entity block at
    // all — but it is a heuristic: it walks forward for the next byte pattern that looks like a head. A
    // length-changing tail edit can move that pattern, which is why refusing or accepting an edit on the scan
    // alone is wrong in both directions. The game itself has no such search: ElementEntity::readBinary reads
    // `count` entities straight off one stream cursor, each record self-delimiting — head, the fixed-width
    // run through priority, then exactly the gated blocks its own flags name. Measuring that way therefore
    // says where the LOADER's cursor lands, which is the only framing that matters (see <see cref="TailSem"/>).

    /// <summary>One-past-the-last byte of the entity record starting at <paramref name="recStart"/>, measured
    /// the way the loading code reads it — head, colour/mass/priority, then the gated behaviour tail — or -1
    /// when the bytes there do not read back to back cleanly. "Cleanly" means the tail walk ran the game's
    /// whole gate list (<see cref="TailSem.Measure"/>): a gate that is not 0/1 means the record carries
    /// something the decompile does not describe, so no end can be claimed for it and the caller keeps the
    /// framing it derived elsewhere. <paramref name="hardEnd"/> bounds the record (the next section's first
    /// byte for the last record in a block).</summary>
    public static int MeasureRecord(byte[] d, int recStart, int hardEnd, string? gameKey)
    {
        if (d is null || recStart < 0 || hardEnd > d.Length || recStart >= hardEnd) return -1;
        var h = HeadAt(d, recStart, hardEnd, anyName: true);
        if (h is null) return -1;
        // The fixed-width run is read before the tail, and only its bounds depend on the record end, so
        // measuring with the block's own end as the bound is what the stream cursor sees.
        if (!BodyOffsets(d, h.Value.XyOff, ExtF4F5, hardEnd, out _, out _, out int po)) return -1;
        int ts = TailStart(po);
        if (ts > hardEnd) return -1;
        var (consumed, complete) = TailSem.Measure(d, ts, hardEnd - ts, gameKey);
        return complete ? ts + consumed : -1;
    }

    /// <summary>Measure <paramref name="count"/> records back to back from <paramref name="start"/> and return
    /// the block end the loader's cursor arrives at — the entity section's true byte end, from the reading
    /// rules rather than from a search. False (and nothing claimed) if any record in the run is unmeasurable
    /// or the walk overruns <paramref name="hardEnd"/>. Returns the end it reached even when that is short of
    /// <paramref name="hardEnd"/>: the caller compares it against the span it has.</summary>
    public static bool MeasureBlock(byte[] d, int start, int count, string? gameKey, out int end, int hardEnd = -1)
    {
        end = -1;
        if (d is null || count < 0 || start < 0 || start > d.Length) return false;
        if (hardEnd < 0) hardEnd = d.Length;
        if (hardEnd > d.Length || start > hardEnd) return false;
        int p = start;
        for (int i = 0; i < count; i++)
        {
            int next = MeasureRecord(d, p, hardEnd, gameKey);
            if (next < 0 || next <= p) return false;
            p = next;
        }
        end = p;
        return true;
    }

    /// <summary>Split an entity block into its <paramref name="count"/> records the way the loading code
    /// reads them — one cursor, records back to back, each ended by its own gated tail — so a record's end
    /// is the next record's start rather than the next byte pattern that happens to look like a head. Falls
    /// back to the credibility <see cref="SplitBlock"/> scan when the grammar cannot explain every record
    /// (unnamed bytes), which is what keeps levels outside the decompile framing the way the tiling oracle
    /// found them. Use this wherever record boundaries are turned into edits or views; the scan is only
    /// sound for asking whether a span <em>could</em> be an entity block. Returns the heads and whether the
    /// block was walked to its count.</summary>
    public static (List<EntityHead> Heads, bool Ok) SplitRecords(byte[] d, int start, int count, string? gameKey)
    {
        if (d is null) return (new List<EntityHead>(), false);
        if (start < 0 || start > d.Length) return SplitBlock(d, start, count, d.Length);
        var heads = new List<EntityHead>(count);
        int p = start;
        for (int i = 0; i < count; i++)
        {
            var h = HeadAt(d, p, d.Length, anyName: true);
            int next = h is null ? -1 : MeasureRecord(d, p, d.Length, gameKey);
            if (next <= p) return SplitBlock(d, start, count, d.Length);
            heads.Add(h!.Value);
            p = next;
        }
        return (heads, true);
    }

    // --------------------------------------------- in-place tail value writes --
    //
    // The bytes after colour / mass / priority are a flag-gated run of behaviour blocks, and each
    // block's field list is now named from the game's own ElementEntity::readBinary (see TailSem).
    // A named field is written back through these primitives: IN PLACE, length-preserving and clamped
    // to the caller's tail span, so an edit can never move the record framing or spill into the next
    // record (I1 holds; the edit localises, I3). Ports of write_lane_int / write_lane_f64.

    /// <summary>Write a 4-byte int32 lane into <paramref name="buf"/> at absolute
    /// <paramref name="abs"/>. Length-preserving; refuses (returns false, leaves bytes untouched)
    /// when the word would run past the buffer. Port of <c>write_lane_int</c>.</summary>
    public static bool WriteLaneInt(byte[] buf, int abs, int value)
    {
        if (buf is null || abs < 0 || abs + 4 > buf.Length) return false;
        BitConverter.GetBytes(value).CopyTo(buf, abs);
        return true;
    }

    /// <summary>Write an 8-byte float64 lane into <paramref name="buf"/> at absolute
    /// <paramref name="abs"/>. Length-preserving; refuses when it would run past the buffer.
    /// Port of <c>write_lane_f64</c>.</summary>
    public static bool WriteLaneF64(byte[] buf, int abs, double value)
    {
        if (buf is null || abs < 0 || abs + 8 > buf.Length) return false;
        BitConverter.GetBytes(value).CopyTo(buf, abs);
        return true;
    }

    /// <summary>Write a 4-byte float32 lane into <paramref name="buf"/> at absolute
    /// <paramref name="abs"/>. Length-preserving; refuses when it would run past the buffer. Used by
    /// the named cell-colour ramp (IndexedCellColours stores each channel as a float32).</summary>
    public static bool WriteLaneF32(byte[] buf, int abs, float value)
    {
        if (buf is null || abs < 0 || abs + 4 > buf.Length) return false;
        BitConverter.GetBytes(value).CopyTo(buf, abs);
        return true;
    }

    /// <summary>The first byte of an entity record's behaviour tail: just after the priority int32
    /// (<c>prioOff + 4</c>). The tail runs to the record end.</summary>
    public static int TailStart(int prioOff) => prioOff + 4;
}

/// <summary>Display fields decoded from an entity record body (render-only). Mass and
/// rotation drive the sprite's size and angle; colour is a later tint input.
/// <see cref="Flip"/> is the record's own per-instance flip flag (the first word of the optional
/// box block, read by ElementEntity::drawBase): when set the game negates the authored angle and
/// mirrors the sprite, which is what makes a mover read upright facing the other way and what turns
/// a pinned prop (a barnacle) to the way it is placed. When the body shape is unrecognised
/// <see cref="HasBody"/> is false and the defaults apply.</summary>
public readonly record struct EntDisplay(int Rotation, byte[] ColorRgba, double Mass, int Priority, bool HasBody, bool Flip);
