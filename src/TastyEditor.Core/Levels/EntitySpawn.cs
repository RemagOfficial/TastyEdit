namespace TastyEditor.Core.Levels;

/// <summary>Spawn a brand-new entity record (S-D10).</summary>
///
/// <para>An entity record is <em>not</em> a fixed struct: <c>ElementEntity::readBinary</c> is a virtual
/// call (vtable slot <c>+0x138</c>), so the bytes a prefab expects after its name are the shape that
/// prefab's own definition reads — which is why a new entity cannot be invented from a template of
/// <em>no</em> type. It can, however, be copied from a record the shipped game itself ships:
/// <see cref="EntityCatalogue.ProtoFor"/> holds one real record per type name harvested from the
/// install's levels, and copying it is precisely what the old Python editors did
/// (<c>tb_entity.make_entity_from_template</c> / <c>spawn_entity</c>).</para>
///
/// <para>So a spawn is two writes plus one gate: rebuild the record head with the requested prefab
/// name (<c>&lt;i32 nameLen&gt;&lt;name NUL&gt;&lt;i32 instLen&gt;&lt;inst&gt;&lt;i32 dx&gt;&lt;i32 dy&gt;</c>,
/// the head <see cref="EntityFraming.HeadAt"/> parses for every family), retarget the chained position
/// delta so the new record lands where the author clicked, then append it and require the section to
/// still read back to back to <c>count + 1</c> records — the loader's own walk, not a heuristic scan.
/// TP2 needs none of that: its entities are typed and the codec re-serialises every field, so a new
/// one is built from field defaults the way <c>tp2editor.spawn_entity</c> did, including the
/// majority-vote <c>field_158</c> that governs cross-stage persistence.</para>
public static class EntitySpawn
{
    /// <summary>Append a new entity of <paramref name="type"/> to one layer of a decoded level.
    /// <paramref name="x"/> / <paramref name="y"/> are the world position the projector uses (level
    /// units for tb / f4 / f5, and TP2's own world units — the /100 factor is applied here).
    /// On refusal the model is untouched and <paramref name="detail"/> says why in one line; on
    /// success it names the record the body was copied from, so the UI can be honest about provenance.</summary>
    public static EditResult Add(object? model, int layer, double x, double y, string? type,
                                 EntityCatalogue? catalogue, out string? detail)
    {
        detail = null;
        if (model is null) { detail = "no level loaded"; return EditResult.Failed; }
        string name = (type ?? "").Trim();
        if (!CredibleName(name))
        {
            detail = "an entity type must be 4-63 printable ASCII characters containing an underscore "
                   + "(that is what the loader's record head accepts)";
            return EditResult.Failed;
        }

        // S-D9 rule, enforced here as well as in the shell so a headless gate can prove it: a type the
        // game has no definition for produces a record the loader walks past and never adds.
        if (catalogue is not null && catalogue.Count >= 8 && !catalogue.Knows(name))
        {
            detail = $"this game has no entity definition for \u201c{name}\u201d \u2014 the level would load "
                   + $"without it. Try: {string.Join(", ", catalogue.Nearest(name))}";
            return EditResult.Failed;
        }

        switch (model)
        {
            case TbLevel tb: return TbAdd(tb, layer, x, y, name, catalogue, out detail);
            case ElementLevel el: return ElemAdd(el, layer, x, y, name, catalogue, out detail);
            case Tp2Level tp: return Tp2Add(tp, layer, x, y, name, out detail);
            default:
                detail = "unsupported level model";
                return EditResult.NotSupported;
        }
    }

    // ------------------------------------------------------- tasty blue ---

    private static EditResult TbAdd(TbLevel tb, int layer, double x, double y, string name,
                                    EntityCatalogue? cat, out string? detail)
    {
        detail = null;
        if (tb.Layers.Count == 0) { detail = "level has no layers"; return EditResult.Failed; }
        int li = Math.Clamp(layer, 0, tb.Layers.Count - 1);
        if (Template(name, cat, tb) is not byte[] tpl)
        {
            detail = $"no shipped {name} record to copy (this game's entity body is read by the prefab "
                   + "itself, so a new one needs a real record of its own type)";
            return EditResult.Failed;
        }
        if (BuildRecord(tpl, name) is not byte[] rec)
        {
            detail = "the template record does not frame as an entity head; refusing to write it";
            return EditResult.Failed;
        }

        var e = new TbEntity { Raw = rec, PosX = x, PosY = y, Name = name };
        var h = EntityFraming.HeadAt(rec, 0, rec.Length);
        if (h is not null)
        {
            var disp = EntityFraming.ReadBody(rec, h.Value.XyOff, EntityFraming.ExtTb, rec.Length);
            if (disp.HasBody) { e.Rotation = disp.Rotation; e.Mass = disp.Mass; e.Priority = disp.Priority; e.ColorRgba = disp.ColorRgba; }
        }
        // TbCodec.PatchEntityPositions re-derives every record's chained delta from its absolute
        // PosX/PosY at encode time, so appending needs no delta bookkeeping here.
        tb.Layers[li].Entities.Add(e);
        detail = TemplateNote(tpl, cat, name);
        return EditResult.Done;
    }

    // -------------------------------------------------- forever / final bite ---

    private static EditResult ElemAdd(ElementLevel el, int layer, double x, double y, string name,
                                      EntityCatalogue? cat, out string? detail)
    {
        detail = null;
        if (el.Layers.Count == 0) { detail = "level has no layers"; return EditResult.Failed; }
        int li = Math.Clamp(layer, 0, el.Layers.Count - 1);
        var secs = el.Layers[li];
        Section? sec = null;
        foreach (var s in secs) if (s.Opaque) { sec = s; break; }   // the entity section is the opaque one
        if (sec is null) { detail = "this level has no entity section"; return EditResult.Failed; }

        if (Template(name, cat, el) is not byte[] tpl)
        {
            detail = $"no shipped {name} record to copy (an f4/f5 entity body is prefab-sized, so a new "
                   + "one has to start from a real record of that type)";
            return EditResult.Failed;
        }
        if (BuildRecord(tpl, name) is not byte[] rec)
        {
            detail = "the template record does not frame as an entity head; refusing to write it";
            return EditResult.Failed;
        }

        // The block is one cursor: count records back to back, so the new record's x/y are a delta
        // from the current tail, exactly as a pasted record's are (EditOps.ElemPasteEntity).
        var (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
        if (!ok || heads.Count != sec.Count)
        {
            detail = "this section does not currently read back to its count; refusing to append";
            return EditResult.Failed;
        }
        var th = EntityFraming.HeadAt(rec, 0, rec.Length);
        if (th is null) { detail = "the rebuilt head does not read back as a record"; return EditResult.Failed; }

        int tailX = 0, tailY = 0;
        foreach (var h in heads) { tailX += h.X; tailY += h.Y; }
        int dx = (int)Math.Round(x) - tailX;
        int dy = (int)Math.Round(y) - tailY;
        if (Math.Abs(dx) > EntityFraming.MaxXy || Math.Abs(dy) > EntityFraming.MaxXy)
        {
            detail = "that position is too far from the last entity for the chained delta to fit";
            return EditResult.Failed;
        }
        int xy = th.Value.XyOff;
        if (xy + 8 > rec.Length) { detail = "the template has no coordinate pair to retarget"; return EditResult.Failed; }
        BitConverter.GetBytes(dx).CopyTo(rec, xy);
        BitConverter.GetBytes(dy).CopyTo(rec, xy + 4);

        var nb = new byte[sec.Block.Length + rec.Length];
        Buffer.BlockCopy(sec.Block, 0, nb, 0, sec.Block.Length);
        Buffer.BlockCopy(rec, 0, nb, sec.Block.Length, rec.Length);

        // Gate: the candidate block must still walk to back to back to count + 1 records, the new one
        // reading its own name at the end. The count field itself is re-emitted by ElementSpanCodec.
        var (heads2, ok2) = EntityFraming.SplitRecords(nb, 0, sec.Count + 1, el.Game);
        if (!ok2 || heads2.Count != sec.Count + 1 || heads2[^1].Name != name)
        {
            detail = "the level's entity block would not read back with the new record; nothing was written";
            return EditResult.Failed;
        }
        sec.Block = nb;
        sec.Count++;
        detail = TemplateNote(tpl, cat, name);
        return EditResult.Done;
    }

    // ---------------------------------------------------------------- tp2 ---

    /// <summary>TP2 entities are typed, and <c>Tp2Codec</c> re-serialises every field, so a spawn needs
    /// no byte template — it needs the defaults a shipped entity carries. Mirrors
    /// <c>tp2editor.spawn_entity</c>: white, no box / move / path / emitter, priority 1000, and
    /// <c>field_158</c> / <c>field_15c</c> taken by majority vote from the level's own entities (a
    /// hardcoded 0 there is what made spawned objects fail to persist across a multilevel).</summary>
    private static EditResult Tp2Add(Tp2Level tp, int layer, double x, double y, string name, out string? detail)
    {
        detail = null;
        if (tp.Layers.Count == 0) { detail = "level has no layers"; return EditResult.Failed; }
        int li = Math.Clamp(layer, 0, tp.Layers.Count - 1);
        var (f158, f15c) = PersistenceFlags(tp);
        tp.Layers[li].Entities.Add(new Tp2Entity
        {
            Type = name,
            Position = new[] { x / 100.0, y / 100.0 },   // world = raw * EntityK(100)
            Field158 = f158,
            Field15c = f15c,
            ColorRgba = new byte[] { 255, 255, 255, 255 },
            Mass = 300.0,
            Priority = 1000,
        });
        detail = $"typed record, flags {f158}/{f15c} from this level's own entities"
               + (f158 != 0 ? " (persists across stages)" : "");
        return EditResult.Done;
    }

    private static (int F158, int F15c) PersistenceFlags(Tp2Level tp)
    {
        var c158 = new Dictionary<int, int>();
        var c15c = new Dictionary<int, int>();
        foreach (var l in tp.Layers)
            foreach (var e in l.Entities)
            {
                c158[e.Field158] = c158.GetValueOrDefault(e.Field158) + 1;
                c15c[e.Field15c] = c15c.GetValueOrDefault(e.Field15c) + 1;
            }
        return (MostCommon(c158, 1), MostCommon(c15c, 0));
    }

    private static int MostCommon(Dictionary<int, int> votes, int fallback)
    {
        int best = fallback, n = 0;
        foreach (var (k, v) in votes) if (v > n) { best = k; n = v; }
        return best;
    }

    // ------------------------------------------------------------ shared ---

    /// <summary>The record to copy: the install corpus first (a shipped entity of this exact type),
    /// then any record of this type already inside the level (so a thin folder still spawns), then a
    /// same-species record as a last resort — which is honest only because the tail is editable
    /// afterwards, so it is reported in <see cref="Add"/>'s detail line rather than silently used.</summary>
    private static byte[]? Template(string name, EntityCatalogue? cat, object model)
    {
        if (cat?.ProtoFor(name)?.Raw is byte[] c && c.Length > 0) return c;
        if (InLevelRecord(model, name) is byte[] m) return m;
        if (cat is not null)
        {
            string stem = name;
            int us = stem.IndexOf('_');
            if (us > 1) stem = stem[..us];
            foreach (var near in cat.Names)
                if (near.StartsWith(stem + "_", StringComparison.Ordinal) && cat.ProtoFor(near)?.Raw is byte[] q && q.Length > 0)
                    return q;
        }
        return null;
    }

    private static byte[]? InLevelRecord(object model, string name) => model switch
    {
        TbLevel tb => tb.Layers.SelectMany(l => l.Entities)
                        .FirstOrDefault(e => e.Name == name && e.Raw.Length > 16)?.Raw,
        ElementLevel el => ElementRecord(el, name),
        _ => null,
    };

    private static byte[]? ElementRecord(ElementLevel el, string name)
    {
        foreach (var layer in el.Layers)
            foreach (var sec in layer)
            {
                if (!sec.Opaque || sec.Count <= 0 || sec.Block.Length == 0) continue;
                var (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
                if (!ok || heads.Count != sec.Count) continue;
                for (int i = 0; i < heads.Count; i++)
                {
                    if (heads[i].Name != name) continue;
                    int start = heads[i].RecStart;
                    int end = i + 1 < heads.Count ? heads[i + 1].RecStart : sec.Block.Length;
                    if (end <= start) continue;
                    var raw = new byte[end - start];
                    Buffer.BlockCopy(sec.Block, start, raw, 0, raw.Length);
                    return raw;
                }
            }
        return null;
    }

    private static string TemplateNote(byte[] template, EntityCatalogue? cat, string name)
    {
        string? from = cat?.ProtoFor(name)?.SourceLevel;
        return from is null
            ? "record copied from a matching entity in this level"
            : $"record copied from shipped level {from}";
    }

    /// <summary>Rebuild a record with a different prefab name: the head's name field is length-prefixed
    /// and NUL-terminated, so everything after it (instance name, coordinates, the prefab-sized body)
    /// shifts by the difference. Port of <c>tb_entity.clone_entity_record</c>'s head rewrite.</summary>
    private static byte[]? BuildRecord(byte[] tpl, string name)
    {
        if (tpl.Length < 8) return null;
        int oldLen = BitConverter.ToInt32(tpl, 0);
        if (oldLen < 5 || oldLen > 64 || 4 + oldLen >= tpl.Length) return null;
        var nb = Latin1(name);
        int newLen = nb.Length + 1;
        if (newLen < 5 || newLen > 64) return null;

        int bodyLen = tpl.Length - (4 + oldLen);
        var rec = new byte[4 + newLen + bodyLen];
        BitConverter.GetBytes(newLen).CopyTo(rec, 0);
        Buffer.BlockCopy(nb, 0, rec, 4, nb.Length);      // rec[4 + newLen - 1] stays the NUL
        Buffer.BlockCopy(tpl, 4 + oldLen, rec, 4 + newLen, bodyLen);
        return EntityFraming.HeadAt(rec, 0, rec.Length)?.Name == name ? rec : null;
    }

    /// <summary>The constraints <see cref="EntityFraming.HeadAt"/> imposes on a name field: printable
    /// ASCII, an underscore, and 5..64 bytes including the terminator.</summary>
    public static bool CredibleName(string name)
    {
        if (name.Length < 4 || name.Length > 63) return false;
        if (name.IndexOf('_') < 0) return false;
        foreach (char c in name) if (c < 32 || c > 126) return false;
        return true;
    }

    private static byte[] Latin1(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)(s[i] & 0xFF);
        return b;
    }
}
