using System.Text.Json.Nodes;

namespace TastyEditor.Core.Levels;

/// <summary>Format-agnostic codec surface (the C# counterpart of a games.py
/// profile's reader/writer pair). The three codec families differ internally —
/// f4/f5 are raw-span, tb is semantic re-derive, tp2 is full field re-serialise —
/// but all speak decode(bytes)->model, encode(model)->bytes, and a structural span
/// model, so detection and the golden harness can drive any of them uniformly.</summary>
public interface ILevelCodec
{
    byte[] Encode(object model);
    JsonObject SpanModel(object model);
}

/// <summary>Adapter for the ElementEngine raw-span codecs (Forever f4 / Final Bite f5).</summary>
public sealed class ElementCodec : ILevelCodec
{
    private readonly ElementSpanCodec _c;
    public ElementCodec(string game) => _c = ElementSpanCodec.For(game);
    public object Decode(byte[] d) => _c.Decode(d);
    public byte[] Encode(object model) => _c.Encode((ElementLevel)model);
    public JsonObject SpanModel(object model) => _c.SpanModel((ElementLevel)model);
}

/// <summary>Adapter for the Tasty Blue structured codec.</summary>
public sealed class TbCodecAdapter : ILevelCodec
{
    public object Decode(byte[] d) => TbCodec.Instance.Decode(d);
    public byte[] Encode(object model) => TbCodec.Instance.Encode((TbLevel)model);
    public JsonObject SpanModel(object model) => TbCodec.Instance.SpanModel((TbLevel)model);
}

/// <summary>Adapter for the Tasty Planet 2 field codec.</summary>
public sealed class Tp2CodecAdapter : ILevelCodec
{
    public object Decode(byte[] d) => Tp2Codec.Instance.Decode(d);
    public byte[] Encode(object model) => Tp2Codec.Instance.Encode((Tp2Level)model);
    public JsonObject SpanModel(object model) => Tp2Codec.Instance.SpanModel((Tp2Level)model);
}

/// <summary>A supported game: its identity, codec and the asset-layout facts the
/// editor needs. Mirrors the codec/detection slice of games.py GameProfile; the
/// Qt-specific callbacks (dialogs, asset loaders) live in the WPF layer, not here.</summary>
public sealed class GameProfile
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Short { get; init; }
    public required ILevelCodec Codec { get; init; }
    public required Func<byte[], object> Decode { get; init; }
    /// <summary>Whether an entity's whole body can be rebuilt from fields (TB/TP2);
    /// f4/f5 entities are edited by an in-place splice, so their tails stay opaque.</summary>
    public bool EntityFullBody { get; init; } = true;
    /// <summary>ElementEngine direct games share Tasty Blue's asset layout.</summary>
    public bool IsDirect { get; init; }
}

/// <summary>The single registry that knows every supported game, and the content +
/// path detector that chooses one for a file — the faithful C# port of games.py's
/// PROFILES / DETECT_ORDER / PATH_MARKERS / detect_level / match_keys.</summary>
public static class GameRegistry
{
    public static readonly GameProfile Tp2 = new()
    {
        Key = "tp2", Label = "Tasty Planet: Back for Seconds", Short = "TP2",
        Codec = new Tp2CodecAdapter(), Decode = b => new Tp2CodecAdapter().Decode(b),
        EntityFullBody = true, IsDirect = false,
    };

    public static readonly GameProfile Tb = new()
    {
        Key = "tb", Label = "Tasty Blue", Short = "TB",
        Codec = new TbCodecAdapter(), Decode = b => new TbCodecAdapter().Decode(b),
        EntityFullBody = true, IsDirect = true,
    };

    public static readonly GameProfile Forever = new()
    {
        Key = "f4", Label = "Tasty Planet Forever", Short = "Forever",
        Codec = new ElementCodec("f4"), Decode = b => new ElementCodec("f4").Decode(b),
        EntityFullBody = false, IsDirect = true,
    };

    public static readonly GameProfile FinalBite = new()
    {
        Key = "f5", Label = "Tasty Planet Final Bite", Short = "Final Bite",
        Codec = new ElementCodec("f5"), Decode = b => new ElementCodec("f5").Decode(b),
        EntityFullBody = false, IsDirect = true,
    };

    private static readonly Dictionary<string, GameProfile> ByKey = new()
    {
        ["tp2"] = Tp2, ["tb"] = Tb, ["f4"] = Forever, ["f5"] = FinalBite,
    };

    public static IReadOnlyCollection<GameProfile> All => ByKey.Values;

    public static GameProfile Profile(string key) =>
        ByKey.TryGetValue(key, out var p)
            ? p
            : throw new ArgumentException($"unsupported level format: {key}");

    /// <summary>Content-detection order (matches games.py DETECT_ORDER). The
    /// ElementEngine codecs can mutually mis-tile a foreign file, so a bare trial is
    /// only a candidate set; the install-folder path marker (see <see cref="PathHint"/>)
    /// is the definitive discriminator.</summary>
    public static readonly string[] DetectOrder = { "f5", "f4", "tb", "tp2" };

    // Distinctive install-folder substrings, most specific first.
    private static readonly (string Marker, string Key)[] PathMarkers =
    {
        ("tasty planet final bite", "f5"),
        ("tasty planet forever", "f4"),
        ("tasty blue", "tb"),
        ("back for seconds", "tp2"),
    };

    /// <summary>Game key implied by the file's install-folder path, or null.</summary>
    public static string? PathHint(string? path)
    {
        string low = (path ?? "").Replace('\\', '/').ToLowerInvariant();
        foreach (var (marker, key) in PathMarkers)
            if (low.Contains(marker)) return key;
        return null;
    }

    /// <summary>True when the given codec parses the bytes without throwing.</summary>
    public static bool TryDecode(GameProfile p, byte[] data)
    {
        try { p.Decode(data); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>All profiles whose codec accepts this file, in DETECT_ORDER
    /// (games.py match_keys).</summary>
    public static List<string> MatchKeys(byte[] data)
    {
        var outKeys = new List<string>();
        foreach (var key in DetectOrder)
        {
            var p = Profile(key);
            if (TryDecode(p, data)) outKeys.Add(key);
        }
        return outKeys;
    }

    /// <summary>Detect the game for a file from its path hint + content, returning
    /// the chosen profile and the already-decoded model (games.py detect_level).
    /// Ambiguity is broken first by the path marker, then by content alone; a
    /// foreign-but-tileable file resolved only by content may still be a tie — the
    /// caller is handed <paramref name="candidates"/> to prompt on a save.</summary>
    public static GameProfile Detect(string? path, byte[] data, out List<string> candidates)
    {
        candidates = new List<string>();

        // 1. A stock install-folder marker is definitive when the codec agrees.
        string? hint = PathHint(path);
        if (hint is not null)
        {
            var p = Profile(hint);
            if (TryDecode(p, data)) return p;
        }

        // 2. Trial-decode every family; a unique winner is the verdict.
        foreach (var key in DetectOrder)
        {
            var p = Profile(key);
            if (TryDecode(p, data)) candidates.Add(key);
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException("not a recognized level file");
        return Profile(candidates[0]);
    }

    /// <summary>Detect the game <em>and</em> hand back the model it decoded on the way,
    /// so the open path runs the codec exactly once (a bare <see cref="Detect"/> then
    /// <c>Decode</c> would tile the file twice). The chosen profile is the same one
    /// <see cref="Detect"/> returns (path hint, else first content match in
    /// <see cref="DetectOrder"/>); <paramref name="candidates"/> still lists every codec
    /// that accepted the bytes, for the save-time ambiguity prompt.</summary>
    public static (GameProfile Profile, object Model) DetectAndDecode(string? path, byte[] data, out List<string> candidates)
    {
        candidates = new List<string>();

        // 1. A stock install-folder marker is definitive when the codec agrees.
        string? hint = PathHint(path);
        if (hint is not null)
        {
            var p = Profile(hint);
            try { return (p, p.Decode(data)); }
            catch (Exception) { /* fall through to content detection */ }
        }

        // 2. Trial-decode every family; the first winner (DETECT_ORDER) is the verdict,
        // but keep trying the rest so the caller still learns of any ambiguity.
        GameProfile? first = null;
        object? firstModel = null;
        foreach (var key in DetectOrder)
        {
            var p = Profile(key);
            try
            {
                var m = p.Decode(data);
                candidates.Add(key);
                if (first is null) { first = p; firstModel = m; }
            }
            catch (Exception) { /* this family does not accept the bytes */ }
        }
        if (first is null)
            throw new InvalidOperationException("not a recognized level file");
        return (first, firstModel!);
    }
}
