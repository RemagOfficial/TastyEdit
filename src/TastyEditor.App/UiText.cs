using TastyEditor.Core.Render;

namespace TastyEditor.App;

/// <summary>R1 copy layer: the one place the editor's user-facing wording lives, so a phrase is
/// fixed once instead of in five panels, and so the internal vocabulary can never leak back into a
/// label by accident. The rules this file exists to enforce (recorded in ROADMAP_RELEASE.md R1):
///
/// <para><b>Plain nouns, sentence case.</b> A label names the thing, not the code behind it. The
/// internal vocabulary — <i>record, raw span, splice, codec, re-derive, re-serialise, frame,
/// oracle, opaque block, centidegree, meterperpix, payload, harvest</i> — belongs in code comments,
/// MODLOG and the gate logs, never in a string a user reads.</para>
///
/// <para><b>Consequence, not mechanism.</b> A warning says what happens in the game ("the entity
/// would not appear"), not how the loader achieves it ("readBinary drops unknown EntityDefs keys").</para>
///
/// <para><b>No type or enum names.</b> <see cref="SceneKind"/> and class names reach the screen only
/// through <see cref="Kind"/>, which turns them into the words the games themselves use.</para>
///
/// <para><b>Honest about the unknown.</b> Several behaviour fields are named after the attribute the
/// game's own loading code reads (<c>minsizemult</c>, <c>changedirectionmaxperiod</c>) because the
/// shipped data carries no label. Those stay — the game's name is the only true one — but they gain a
/// tooltip from <see cref="FieldHint"/>, and where what the field does has not been confirmed the
/// tooltip says so instead of guessing.</para>
///
/// <para>The <c>copyaudit</c> gate greps every string literal the UI can show for the banned words
/// below and fails on a hit, which is what keeps this from rotting as later slices add strings.</para>
/// </summary>
internal static class UiText
{
    /// <summary>Words that must not appear in a string the user can see. The audit gate matches these
    /// case-insensitively over the UI sources (the headless gate harness in Viewport/Smoke.cs is
    /// excluded — its logs are developer output by design).</summary>
    public static readonly string[] BannedWords =
    {
        "splice", "re-deriv", "re-seriali", "re-decode", "re-encode", "codec", "raw span",
        "raw-span", "byte span", "byte-safety", "centidegree", "meterperpix", "mantissa",
        "opaque", "oracle", "prefab", "harvest", "EntityDefs", "SceneKind", "record head",
        "tail byte", "un-namespaced", "projector", "re-project", "reseriali", "stride",
    };

    /// <summary>The user's word for a projected object kind. The enum names are internal (and
    /// <c>Deco</c> / <c>Area</c> are not words the games use on screen).</summary>
    public static string Kind(SceneKind k) => k switch
    {
        SceneKind.Wall => "Wall",
        SceneKind.Area => "Area",
        SceneKind.Path => "Path",
        SceneKind.Entity => "Entity",
        SceneKind.Deco => "Decoration",
        SceneKind.Water => "Water",
        SceneKind.PointList => "Point list",
        _ => "Object",
    };

    /// <summary>One-line in-game meaning for the fields whose label is a engine attribute name the
    /// shipped data actually uses. Keys are matched case-insensitively against the field label, so a
    /// partial key covers the families that spell the same field slightly differently. Not a
    /// guarantee: where the behaviour was never confirmed, the hint says that instead.</summary>
    private static readonly (string Key, string Hint)[] FieldHints =
    {
        // Entity / decoration properties
        ("mass", "How big this object is in the food chain. The goo can eat things lighter than it; this also sets the size the object is drawn at."),
        ("priority", "Draw order. Objects with a higher priority are drawn on top of lower ones, and the game eats in this order."),
        ("firstpriority", "Used when several objects start together — which one goes first."),
        ("first", "Whether this object leads the group when several share the same movement."),
        ("persists", "Stays in the game when the level changes instead of respawning."),
        ("dsize", "Scales the picture: 1 is the sprite's own size."),
        // Movement block
        ("movedirection", "Turns this object into a mover: when on, the settings below say how it travels."),
        ("maxspeed", "Fastest it can go, in world units per second."),
        ("accel", "How quickly it reaches that speed."),
        ("aimahead", "How far ahead it looks when steering along its path."),
        ("direction", "Which way it travels, in degrees."),
        ("directionvariation", "How much that travel direction is allowed to vary at random."),
        ("changedirectionminperiod", "Shortest wait before it picks a new direction, in seconds."),
        ("changedirectionmaxperiod", "Longest wait before it picks a new direction, in seconds."),
        ("turnrate", "How fast it rotates while moving."),
        // Path follow block
        ("pathfollow", "When on, the object snaps to a path and travels along it. The settings below control the trip."),
        ("pathfol", "When on, the object snaps to a path and travels along it. The settings below control the trip."),
        ("pathname", "Which path on this level to follow. The path must exist on the same level or the object does not move."),
        ("loop", "Returns to the start and repeats when it reaches the end."),
        ("deleteatend", "Removes the object once it finishes the path."),
        ("type", "Still being identified from the game's code — how it selects between the modes the loader supports."),
        // Point list block
        ("pointlist", "When on, the object visits the listed points in order instead of following a path."),
        ("pointlistname", "Which of the level's point lists to walk. The list must exist on the same level."),
        ("doatend", "What happens when it reaches the last point."),
        ("stoptimeateachpoint", "How long it pauses at each point, in seconds."),
        ("sequentialstartingpoint", "Which point in the list it starts from."),
        ("randomprioritizecloserelements", "Still being identified from the game's code — the exact effect is not confirmed."),
        ("considerdoneafterstucktime", "How long it may be stuck before the game treats it as finished."),
        // Random area block
        ("randomarea", "When on, the object wanders inside one of the level's areas. The settings below say where and how fast."),
        ("randarea", "When on, the object wanders inside one of the level's areas. The settings below say where and how fast."),
        ("definedareaname", "Which of the level's defined areas it may wander in. The area must exist on the same level."),
        ("rpre", "Still being identified from the game's code — the exact effect is not confirmed."),
        // Emitter block
        ("entityemitter", "When on, this object is a spawner: it keeps producing other objects. The settings below control what comes out."),
        ("entemit", "When on, this object is a spawner: it keeps producing other objects. The settings below control what comes out."),
        ("minsizemult", "Smallest size a spawned object can appear at, relative to its own normal size."),
        ("maxsizemult", "Largest size a spawned object can appear at, relative to its own normal size."),
        ("emitwidth", "Width of the area the objects appear in."),
        ("emitheight", "Height of the area the objects appear in."),
        ("emitminperiod", "Shortest wait between two spawns, in seconds."),
        ("emitmaxperiod", "Longest wait between two spawns, in seconds."),
        ("maxentities", "How many it can have on screen at once; it stops spawning above this."),
        ("priorityrange", "How much the spawned objects' draw order is allowed to vary."),
        ("redrange", "How much the spawned objects' red shade is allowed to vary."),
        ("greenrange", "How much the spawned objects' green shade is allowed to vary."),
        ("bluerange", "How much the spawned objects' blue shade is allowed to vary."),
        ("opacityrange", "How much the spawned objects' see-through-ness is allowed to vary."),
        ("initialanglerange", "Random starting spin angle of each spawned object."),
        ("initialangularvelrange", "Random starting spin speed of each spawned object."),
        ("onlyemitunderplayerarea", "Only spawns while the player is inside the area this object is in."),
        ("autolink", "Still being identified from the game's code — the exact effect is not confirmed."),
        ("scriptablename", "Name of the level script that decides what this spawner produces."),
        ("entitylink", "Links this object to another one by name; the pair then move or get eaten together."),
        ("linked entity name", "The other object this one is linked to. It must be the exact name of an entity on the same level."),
        ("indexedcellcolours", "When on, the object fades through a list of colours instead of using one flat colour."),
        ("colour count", "How many colours are in the fade list. Raising it adds entries at the end; lowering it removes them."),
        ("colour", "One colour in the fade list, as a red / green / blue / alpha channel. A higher value is stronger."),
        // Tasty Planet 1 (the text level files keep the game's own attribute names)
        ("levelnum", "Which number this level is in the game's order."),
        ("victorysize", "How big the player has to grow to finish the level."),
        ("growthrate", "How fast the player gets bigger as it eats."),
        ("unitmultiplier", "Scales the whole level's sizes; the game's own units-per-picture-unit."),
        ("entitydef", "What kind of object this is \u2014 one of the types the game knows. Pick one from the list."),
        ("posx", "How far left or right this sits, in the level's own coordinates."),
        ("posy", "How high up or down this sits, in the level's own coordinates."),
        ("minarea", "The smallest size the player can be for this to happen."),
        ("maxarea", "The largest size the player can be for this to happen."),
        ("maxlive", "How many of these can be out at once; it stops making more above this."),
        ("damage", "How much this hurts the player when it touches them."),
        ("angle", "Which way it points, in degrees."),
        ("conditon", "Only does this when the player's size matches the comparison chosen here. (The game's file spells this one wrong; the spelling is kept so it still loads.)"),
        ("pathpos", "How far along its path this release happens, from 0 at the start to 1 at the end."),
        ("tileswide", "How many tiles across this background layer is."),
        ("tileshigh", "How many tiles tall this background layer is."),
        ("flipx", "Mirrors the picture left-to-right."),
        ("flipy", "Mirrors the picture top-to-bottom."),
    };

    /// <summary>The tooltip for a behaviour-field label, or null when there is nothing to add (which
    /// is the common case: most labels are already plain words). An exact match wins; otherwise the
    /// longest key contained in the label is used, so "emitmaxperiod" is described by its own entry
    /// and not by the short "count" / "loop" ones.</summary>
    public static string? FieldHint(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        string key = label.Trim().ToLowerInvariant();
        string? best = null;
        int bestLen = -1;
        foreach (var (k, hint) in FieldHints)
        {
            if (k == key) return hint;
            if (key.Length > k.Length && key.Contains(k) && k.Length > bestLen) { best = hint; bestLen = k.Length; }
        }
        return best;
    }

    /// <summary>Re-reads the level on save and reports what came back. The short form for the status
    /// bar; the byte-level detail stays in <see cref="TastyEditor.App.Viewport.SaveReport"/>'s fields
    /// and in the headless gate logs, which is where a developer reads it.</summary>
    public static string SavedLine(bool changed, bool stable, int bytesChanged)
    {
        if (!changed) return "Saved — the file already matched, nothing had to change.";
        string what = bytesChanged == 1 ? "1 byte changed" : $"{bytesChanged} bytes changed";
        return stable ? $"Saved — {what}." : $"Saved, but the file did not read back as expected — {what}. Please report this.";
    }
}
