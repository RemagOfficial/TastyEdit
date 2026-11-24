namespace TastyEditor.Core.Levels;

/// <summary>Which entity TYPE names a game can actually spawn (S-D9), and a real record to build a
/// new entity from (S-D10).</summary>
///
/// <para>An entity record's leading string is not free-form. <c>ElementEntityDefs::getDef</c> is an
/// exact <c>std::map&lt;string, shared_ptr&lt;ElementEntity&gt;&gt;::find</c> on it (f5 Android
/// <c>out_getdef2/b009_getDef_007f9f64.c</c>), and the entity loop of <c>Layer::readBinary</c>
/// (f5 <c>out_rb/b035_readBinary_00a5f85c.c</c>, ~00a60520) only clones a def, reads the record into
/// it and calls <c>addElement</c> inside the branch where that find HIT. On a miss it reads the
/// record through a fallback def — the literal <c>office_worker_base</c> — purely to keep the stream
/// cursor in step, and adds nothing. So a type the game has no definition for makes the entity
/// vanish in game while the file still parses and re-encodes perfectly: the bytes are fine, the
/// object is dead. That is a naming rule, not a framing rule, so it is policed here (the shell
/// consults it before applying an entity type) rather than inside the byte-level edit calls, which
/// keep proving only that the block still walks.</para>
///
/// <para>The definitions are compiled into the engine — no entitydefs script ships in any install
/// (only comics / levelmusic / scriptlevels / tips lua), and every name harvested below also
/// appears in the game binary's string table. The practical authoritative catalogue of spawnable
/// types is therefore the set of names the shipped levels use: those demonstrably load in game.
/// <see cref="Harvest"/> collects exactly that, and a whole game's corpus scans in well under a
/// second (measured: f5 268 levels 0.7 s, f4 328 0.4 s, tb 127 0.06 s — see the <c>entitytypes</c>
/// gate).</para>
///
/// <para>Because the same scan already has every record's bytes in hand, it also keeps ONE real
/// record per type (<see cref="EntityProto"/>) — which is what makes a brand-new entity possible
/// (S-D10): the record the game expects for a prefab is not something that can be invented, but a
/// shipped level contains dozens of correct ones. <see cref="EntitySpawn"/> copies such a record and
/// retargets its name and chained position, exactly as the old Python editors'
/// <c>make_entity_from_template</c> did.</para>
public sealed class EntityCatalogue
{
    private readonly Dictionary<string, EntityProto> _protos;

    internal EntityCatalogue(List<string> names, Dictionary<string, EntityProto> protos)
    {
        Names = names;
        _protos = protos;
    }

    /// <summary>A catalogue that knows nothing. Callers treat it as "no authority" (the guards below
    /// <c>Count &gt;= 8</c> stand down), so an unresolved asset root never blocks an edit.</summary>
    public static readonly EntityCatalogue Empty = new(new List<string>(), new Dictionary<string, EntityProto>(StringComparer.Ordinal));

    /// <summary>Every spawnable type name found in the corpus, ordinal-sorted (which groups a
    /// species' variants together).</summary>
    public List<string> Names { get; }

    public int Count => Names.Count;
    public bool IsEmpty => Names.Count == 0;

    /// <summary>True when this game can produce an entity for this type. Exact match: the engine's
    /// own lookup is an exact map find, so nothing looser is honest.</summary>
    public bool Knows(string? name) =>
        !string.IsNullOrEmpty(name) && Names.Contains(name!, StringComparer.Ordinal);

    /// <summary>A real shipped record for this type (name head + body), or null for a family that
    /// needs no byte template (TP2 entities are typed). First occurrence in the corpus wins.</summary>
    public EntityProto? ProtoFor(string name) =>
        _protos.TryGetValue(name ?? "", out var p) ? p : null;

    /// <summary>Types sharing the rejected name's stem, so a refusal can point at something usable
    /// instead of just saying no.</summary>
    public List<string> Nearest(string name, int take = 3)
    {
        string stem = name ?? "";
        int us = stem.IndexOf('_');
        if (us > 1) stem = stem[..us];
        var near = Names.Where(n => n.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase)).Take(take).ToList();
        return near.Count > 0 ? near : Names.Take(take).ToList();
    }
}

/// <summary>One shipped entity record, kept as the template a new entity is built from.
/// <see cref="Raw"/> carries the record for the raw-span families (Tasty Blue, Forever, Final Bite);
/// <see cref="SourceLevel"/> names the file it came from so the UI can say where the body came from.</summary>
public sealed record EntityProto(string Name, byte[] Raw, string SourceLevel);

/// <summary>Harvest side of <see cref="EntityCatalogue"/>.</summary>
public static class EntityDefs
{
    /// <summary>Scan the <c>*.bin</c> levels in <paramref name="levelsDir"/> and return the type names
    /// they use plus one real record for each. A level that fails to decode contributes nothing;
    /// nothing here throws. <paramref name="maxFiles"/> caps the scan for callers that want a cheap
    /// sample (the editor always harvests the whole install — it is fast).</summary>
    public static EntityCatalogue Harvest(string levelsDir, Func<byte[], object> decode, int maxFiles = 0)
    {
        var into = new SortedSet<string>(StringComparer.Ordinal);
        var protos = new Dictionary<string, EntityProto>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(levelsDir) || !Directory.Exists(levelsDir))
            return new EntityCatalogue(new List<string>(), protos);

        var files = Directory.GetFiles(levelsDir, "*.bin");
        Array.Sort(files, StringComparer.Ordinal);
        if (maxFiles > 0) files = files.Take(maxFiles).ToArray();

        foreach (var f in files)
        {
            object? model;
            try { model = decode(File.ReadAllBytes(f)); }
            catch { continue; }
            if (model is null) continue;

            string stem = Path.GetFileName(f);
            switch (model)
            {
                case TbLevel tb:
                    foreach (var l in tb.Layers)
                        foreach (var e in l.Entities)
                        {
                            if (string.IsNullOrEmpty(e.Name) || e.Raw is null) continue;
                            into.Add(e.Name!);
                            if (!protos.ContainsKey(e.Name!))
                                protos[e.Name!] = new EntityProto(e.Name!, (byte[])e.Raw.Clone(), stem);
                        }
                    break;

                case ElementLevel el:
                    foreach (var layer in el.Layers)
                        foreach (var sec in layer)
                        {
                            if (!sec.Opaque || sec.Count <= 0 || sec.Block.Length == 0) continue;
                            var (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
                            if (!ok || heads.Count != sec.Count) continue;   // unwalkable: no names to claim
                            for (int i = 0; i < heads.Count; i++)
                            {
                                string n = heads[i].Name;
                                if (string.IsNullOrEmpty(n)) continue;
                                into.Add(n);
                                if (protos.ContainsKey(n)) continue;
                                int start = heads[i].RecStart;
                                int end = i + 1 < heads.Count ? heads[i + 1].RecStart : sec.Block.Length;
                                if (end <= start) continue;
                                var raw = new byte[end - start];
                                Buffer.BlockCopy(sec.Block, start, raw, 0, raw.Length);
                                protos[n] = new EntityProto(n, raw, stem);
                            }
                        }
                    break;

                case Tp2Level tp:
                    // TP2 entities are typed and re-serialised by the codec, so a new one is built from
                    // field defaults (Tp2Spawn) rather than from a byte template. Names only.
                    foreach (var l in tp.Layers)
                        foreach (var e in l.Entities)
                            if (!string.IsNullOrEmpty(e.Type)) into.Add(e.Type!);
                    break;
            }
        }
        return new EntityCatalogue(into.ToList(), protos);
    }

    /// <summary>Every entity type name a decoded model carries, in record order. Tasty Blue and
    /// TP2 keep per-record entity lists; Forever / Final Bite keep one opaque block per entity
    /// section, whose names come from the same read-order walk the projector uses.</summary>
    public static IEnumerable<string> Names(object model)
    {
        switch (model)
        {
            case Tp2Level tp:
                foreach (var l in tp.Layers)
                    foreach (var e in l.Entities)
                        if (!string.IsNullOrEmpty(e.Type)) yield return e.Type!;
                break;

            case TbLevel tb:
                foreach (var l in tb.Layers)
                    foreach (var e in l.Entities)
                        if (!string.IsNullOrEmpty(e.Name)) yield return e.Name!;
                break;

            case ElementLevel el:
                foreach (var layer in el.Layers)
                    foreach (var sec in layer)
                    {
                        if (!sec.Opaque || sec.Count <= 0 || sec.Block.Length == 0) continue;
                        var (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
                        if (!ok || heads.Count != sec.Count) continue;   // unwalkable block: no names to claim
                        foreach (var h in heads)
                            if (!string.IsNullOrEmpty(h.Name)) yield return h.Name!;
                    }
                break;
        }
    }
}
