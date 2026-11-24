namespace TastyEditor.Core.Levels;

/// <summary>Byte-safe sprite-name reassignment for Tasty Planet 2 levels.
///
/// <para>TP2 is the one family whose codec re-serialises <em>every</em> field from the
/// typed model (the entity's prefab <c>Type</c> string and a decoration's tile index are
/// both written back verbatim by <see cref="Tp2Codec.Encode"/>, and that whole
/// decode→encode path is proven byte-exact by the Phase-C golden corpus). So pointing an
/// entity or a decoration at a different sprite is a plain model-field write: re-encoding
/// reproduces the untouched bytes exactly (I1) and the edited file itself re-decodes to
/// itself (the in-app stability gate).</para>
///
/// <para>Only TP2 is offered here. In Tasty Blue and the ElementEngine games (Forever /
/// Final Bite) entities and decorations are carried as verbatim raw spans whose only
/// proven edit is a fixed-width, length-preserving in-place splice (position, colour,
/// mass); a variable-length name has no such splice, so reassigning it would need the
/// full-record re-encode path that is deferred to S5-6. Those callers must not route a
/// name edit through this class.</para>
///
/// <para>The decoration logic mirrors <c>tp2editor._deco_set_sprite</c> /
/// <c>_tiletype_index</c> exactly: a static tile is referenced by <c>type = 1</c> plus an
/// index into the level's <c>tileTypes</c> table, registering the name there (and bumping
/// <c>tileTypeCount</c>) when it is not already present. The editor never emits a type-2
/// inline-string tile, because those only resolve for theme-loaded animated sprites.</para>
/// </summary>
public static class Tp2Edit
{
    /// <summary>Repoint an entity at a different prefab/sprite name. The name is written
    /// straight back by the encoder, so this is lossless for every other record. Returns
    /// false (leaving the record untouched) for a null entity or a blank name.</summary>
    public static bool SetEntitySprite(Tp2Entity? entity, string? name)
    {
        if (entity is null || string.IsNullOrWhiteSpace(name)) return false;
        entity.Type = name;
        return true;
    }

    /// <summary>Point a decoration at <paramref name="name"/> the way the game loads static
    /// tiles: <c>type = 1</c> referencing an index into the level's <c>tileTypes</c> table,
    /// appending the name (and refreshing <c>tileTypeCount</c>) when it is not already
    /// registered. Returns false (leaving the record untouched) for null inputs or a blank
    /// name.</summary>
    /// <remarks>TP2 decorations are run-length named: a record with <c>type = 0</c> means
    /// "same sprite as the previous decoration". Repointing a deco therefore also repaints
    /// every <c>type 0</c> follower that used to inherit through it — which is why swapping a
    /// single background tile can make one sprite bloom into many in-game (and open apparent
    /// holes). To change exactly one deco we materialise the immediate follower back to the
    /// name it used to inherit; once that record is explicit the whole downstream chain keeps
    /// its original appearance unchanged.</remarks>
    public static bool SetDecoSprite(Tp2Level? level, Tp2Deco? deco, string? name)
    {
        if (level is null || deco is null || string.IsNullOrWhiteSpace(name)) return false;

        // Locate the record so we can protect the run-length chain that follows it.
        Tp2Layer? owner = null;
        int idx = -1;
        foreach (var l in level.Layers)
        {
            int j = l.Decos.IndexOf(deco);
            if (j >= 0) { owner = l; idx = j; break; }
        }

        // The name this deco effectively rendered under BEFORE the edit (what its type-0
        // followers were inheriting).
        string? oldName = owner is null ? null : ResolveDecoNameAt(owner, idx, level.TileTypes);

        deco.Type = 1;
        deco.HasCell = true;
        deco.Cell = TileTypeIndex(level, name);
        deco.HasString = false;
        deco.String = null;

        if (owner is not null && idx >= 0 && idx + 1 < owner.Decos.Count
            && owner.Decos[idx + 1].Type == 0
            && !string.IsNullOrEmpty(oldName) && oldName != name)
        {
            var next = owner.Decos[idx + 1];
            next.Type = 1;
            next.HasCell = true;
            next.Cell = TileTypeIndex(level, oldName!);
            next.HasString = false;
            next.String = null;
        }
        return true;
    }

    /// <summary>Effective sprite name of the decoration at <paramref name="idx"/> after
    /// walking the layer's run-length chain (mirrors <see cref="Render.SceneProjector"/>:
    /// type 2 = inline string, type 1 = tileTypes[cell], type 0 = inherit previous).</summary>
    private static string? ResolveDecoNameAt(Tp2Layer layer, int idx, List<string?> tileTypes)
    {
        string? last = null;
        for (int i = 0; i <= idx && i < layer.Decos.Count; i++)
        {
            var d = layer.Decos[i];
            string? nm = d.Type switch
            {
                2 => d.String,
                1 => (d.Cell >= 0 && d.Cell < tileTypes.Count) ? tileTypes[d.Cell] : null,
                _ => last,
            };
            if (nm is not null) last = nm;
        }
        return last;
    }

    // ---------------------------------------------- deco flip (S-D13) ----
    //
    // TP2's codec re-serialises the decoration record from the typed model, so the flip is a field
    // write — with one thing the raw-span families don't have: the block's existence is itself a
    // model field (<c>ExtraFlag</c>), and Tp2Codec.Encode only emits the six words when it is
    // non-zero. ElementDecoration::writeBinary decides that flag as "any of the two flags or the
    // four clip ints is set", which is exactly what we reproduce here, so turning both flips off on
    // a record that has no clip shrinks it back to what the game itself would have written.

    /// <summary>Set a TP2 decoration's flip flags, materialising (or, when nothing is left set,
    /// dropping) the optional extra block the way the engine's writer does. <paramref name="resized"/>
    /// reports the record's byte length changing — harmless for TP2, whose encoder re-derives every
    /// field and whose records are read sequentially by per-section count.</summary>
    public static bool SetDecoFlip(Tp2Deco? deco, bool fx, bool fy, out bool resized)
    {
        resized = false;
        if (deco is null) return false;

        deco.FlipWordX = fx ? 1 : 0;      // writeBoolean emits 1 / 0; readBoolean tests == 1
        deco.FlipWordY = fy ? 1 : 0;
        deco.FlipX = fx; deco.FlipY = fy;

        bool clip = deco.ExtraInts is { Length: >= 4 } ei &&
                    (ei[0] != 0 || ei[1] != 0 || ei[2] != 0 || ei[3] != 0);
        if (fx || fy || clip)
        {
            if (deco.ExtraFlag == 0) { deco.ExtraFlag = 1; resized = true; }
            if (deco.ExtraInts is null || deco.ExtraInts.Length < 4)
            {
                var four = new int[4];
                if (deco.ExtraInts is not null) Array.Copy(deco.ExtraInts, four, deco.ExtraInts.Length);
                deco.ExtraInts = four;    // the encoder indexes all four clip words
            }
            return true;
        }
        if (deco.ExtraFlag == 1)          // writeBinary's collapse rule (only for its own value)
        {
            deco.ExtraFlag = 0;
            deco.ExtraInts = Array.Empty<int>();
            resized = true;
        }
        return true;
    }

    public static bool SetDecoFlip(Tp2Deco? deco, bool fx, bool fy)
        => SetDecoFlip(deco, fx, fy, out _);

    /// <summary>Index of <paramref name="name"/> in the level's tileTypes table, appending
    /// it (and keeping <c>TileTypeCount</c> in step, which the encoder writes as the table
    /// length) when absent. Mirrors <c>tp2editor._tiletype_index</c>.</summary>
    private static int TileTypeIndex(Tp2Level level, string name)
    {
        for (int i = 0; i < level.TileTypes.Count; i++)
            if (level.TileTypes[i] == name) return i;
        level.TileTypes.Add(name);
        level.TileTypeCount = level.TileTypes.Count;
        return level.TileTypes.Count - 1;
    }
}
