namespace TastyEditor.Core.Assets;

/// <summary>Thrown when <c>skeletons.bin</c> does not parse to the byte. The grammar is exact, so any
/// leftover or a bad string length means the file is not a Tasty Blue skeleton table (or a newer
/// variant), not a partial read worth salvaging.</summary>
public sealed class TbSkeletonException(string message) : Exception(message);

/// <summary>One keyframe row. A joint keyframe is <c>[time, angle, scale, tx, ty]</c> (proven: the
/// engine's reader <c>FUN_004be140</c> calls the float reader exactly five times and its constructor
/// defaults the third slot to 1.0); a shape keyframe carries fifteen more floats behind that, which the
/// rest pose does not use. Kept as raw floats so the row width stays the parser's concern.</summary>
public sealed record TbKeyframe(float[] V)
{
    public float Time => V.Length > 0 ? V[0] : 0f;
    public float Angle => V.Length > 1 ? V[1] : 0f;
    public float Scale => V.Length > 2 ? V[2] : 1f;
}

/// <summary>An animation track: one of the eleven fixed anim names and its keyframes. Most tracks in
/// the shipped file carry zero keys, which is why a static rest-pose composite is the exact picture for
/// most rigs.</summary>
public sealed record TbTrack(string Name, TbKeyframe[] Keys);

/// <summary>One art layer hung off a bone: an <c>imagemaps.xml</c> cell plus its local transform —
/// <c>A</c> the art's rotation in degrees, <c>B</c>/<c>C</c> its offset inside the bone frame, <c>N</c>
/// the draw order (0..11 across the file), and <c>S1</c>/<c>S2</c> the per-axis scale the engine bakes
/// into the link matrix (<c>ImageBoneLinkState::updateAnim</c> reads <c>ImageBoneLink+0x58/+0x5c</c>).
/// Final Bite stores them as two trailing f32 after the draw order; <c>S2 == -1</c> is the bilateral
/// mirror that puts a second copy of a single-drawn arm on the far side. The other dialects carry no
/// scale field, so they default to <c>(1, 1)</c>.</summary>
public sealed record TbShape(string Image, float A, float B, float C, float S1, float S2, int N, TbTrack[] Tracks);

/// <summary>A rig joint. <see cref="Mode"/> (the authoritative Final Bite <c>SkeletonBone+0x58</c> flag)
/// selects how <c>P1</c>/<c>P2</c> place the joint: 0 = polar (<c>P1</c> a rotation in degrees, <c>P2</c>
/// a distance along the rotated axis), nonzero = cartesian (an (x, y) offset). Where <c>Mode</c> is
/// <c>-1</c> (Blue / Forever, whose loaders do not expose the flag) the older <c>FlagB</c> reading — set
/// = cartesian, clear = polar lever — is the only hint, which is why the affine compositor is confined
/// to the dialect whose mode is proven.</summary>
public sealed class TbBone
{
    public required string Name { get; init; }
    public int FlagA { get; init; }
    public int FlagB { get; init; }
    public float P1 { get; init; }
    public float P2 { get; init; }

    /// <summary>The joint's placement mode, read from the <c>SkeletonBone+0x58</c> char (the 4th word of
    /// a Final Bite node record). <c>0</c> = polar: <c>P1</c> is a rotation in degrees and <c>P2</c> a
    /// distance along the rotated axis; nonzero = cartesian: <c>P1</c>/<c>P2</c> are an (x, y) offset in
    /// the parent frame. <c>-1</c> means the dialect does not carry it (Blue / Forever), so the caller
    /// must not trust a polar/cartesian split for those rigs.</summary>
    public int Mode { get; init; } = -1;

    public TbTrack[] Tracks { get; init; } = [];
    public TbShape[] Shapes { get; init; } = [];
    public TbBone[] Children { get; init; } = [];

    /// <summary>Depth-first self-then-children walk.</summary>
    public IEnumerable<TbBone> Walk()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var b in c.Walk())
                yield return b;
    }
}

/// <summary>Reader for <c>editabledirect/skeletons.bin</c>, the entity rig table the three BlitzMax
/// Tasty Planet games (Blue / Forever / Final Bite) ship instead of animationdefs.xml — that file is an
/// empty stub for these titles, so the rigs are the only source of a multi-part entity's shape.
///
/// The grammar came out of <c>tastyblue.exe</c>'s own loaders (<c>FUN_00666a70</c> node,
/// <c>FUN_005fd0e0</c> shape, <c>FUN_004bebe0</c> anim list, <c>FUN_004be140</c> keyframe) and is
/// confirmed by consuming all 423,891 bytes of Tasty Blue's file with nothing left over and 451/451
/// shape images resolving to real <c>imagemaps.xml</c> cells:
///
///     file     = u32 count, count x ( str rigName + node )
///     node     = str name, u32 flagA, u32 flagB, f32 p1, f32 p2,
///                anims(5), u32 shapeCount, shapeCount x shape,
///                u32 childCount, childCount x node
///     shape    = str image, f32 a, f32 b, f32 c, u32 n, anims(20)
///     anims(w) = u32 count, count x ( str animName, u32 kfCount, kfCount x w*f32 )
///     str      = u32 len, len bytes INCLUDING the terminating NUL
///
/// Nothing is aligned to four bytes — a string of any length starts anywhere, so records sit at odd
/// offsets and the stream has to be walked, never indexed at a fixed stride.
///
/// The animation vocabulary is fixed at eleven names (idle, walk, swim, fly, mouth_open_super_wide,
/// mouth_open_wide, mouth_open_med_wide, mouth_open_normal, mouth_open_small, attack, custom1).</summary>
public static class TbSkeletons
{
    public const int NodeKf = 5;      // floats per joint keyframe (FUN_004be140 reads exactly 5)
    public const int ShapeKf = 20;    // floats per shape keyframe (only width 20 consumes the file exactly)

    // Forever / Final Bite keyframe records are wider: the joint keyframe (f4 FUN_004bb280 /
    // f5 FUN_1400d55e0) is 5 f32 + 2 bool + 1 u32 = 32 bytes = 8 words, and the shape keyframe
    // (f4 FUN_004cd070 / f5 FUN_1400e5ed0) is 3 f32 + 4-byte colour + 4 x vec4 + bool + u32 = 88
    // bytes = 22 words. Reading them as plain words consumes the stream exactly; the renderer only
    // needs each shape's image name and draw order, so the decoded keyframe values go unused.
    public const int F45NodeKf = 8;    // 32-byte joint keyframe, read as 8 words
    public const int F45ShapeKf = 22;  // 88-byte shape keyframe, read as 22 words

    /// <summary>Which of the three skeletons.bin record layouts a file uses. <see cref="Read"/> picks the
    /// one that consumes the whole stream, so the caller does not have to know the title.</summary>
    public enum Dialect { Blue, Forever, FinalBite }

    // The fixed animation vocabulary the Forever / Final Bite node anim list matches against (f4
    // FUN_004bbba0, table FUN_004bb8e0): a node track's keyframes are only read when its name is one of
    // these twelve; shape tracks read their body unconditionally.
    public static readonly HashSet<string> AnimVocab = new(StringComparer.Ordinal)
    {
        "idle", "walk", "run", "swim", "fly", "mouth_open_super_wide", "mouth_open_wide",
        "mouth_open_med_wide", "mouth_open_normal", "mouth_open_small", "attack", "custom1",
    };

    public static readonly string[] AnimNames =
    [
        "idle", "walk", "swim", "fly", "mouth_open_super_wide", "mouth_open_wide",
        "mouth_open_med_wide", "mouth_open_normal", "mouth_open_small", "attack", "custom1",
    ];

    /// <summary>Parse the whole table into <c>rigName -> root bone</c>, auto-detecting which of the three
    /// record layouts the file uses. Blue's grammar drifts on the Forever / Final Bite variants (a wider
    /// node header, richer shape header, a per-rig trailer and an extra <c>-1</c> word per f5 rig), so
    /// every layout is tried and the first that consumes the file to the byte wins. Throws only when none
    /// of them matches, i.e. the data is not a Tasty Planet skeleton table at all.</summary>
    public static Dictionary<string, TbBone> Read(byte[] data)
        => Read(data, out _);

    /// <summary>As <see cref="Read(byte[])"/> but also reports which layout won, so a caller can confine
    /// dialect-specific geometry (the affine rest-pose, which trusts the Final Bite joint <c>Mode</c> and
    /// per-shape scale) to the file whose record layout actually carries those fields.</summary>
    public static Dictionary<string, TbBone> Read(byte[] data, out Dialect won)
    {
        foreach (var d in new[] { Dialect.Blue, Dialect.Forever, Dialect.FinalBite })
        {
            try
            {
                var rigs = Read(data, d);
                if (rigs.Count > 0) { won = d; return rigs; }
            }
            catch (TbSkeletonException) { /* layout mismatch: try the next dialect */ }
        }
        won = default;
        throw new TbSkeletonException("no skeleton layout (Blue / Forever / Final Bite) consumed the file");
    }

    /// <summary>Parse with one specific layout. Throws when the data is not fully consumed, so a returned
    /// dictionary means the grammar matched exactly.</summary>
    public static Dictionary<string, TbBone> Read(byte[] data, Dialect dialect)
    {
        var p = new Parser(new Reader(data), dialect);
        var rigs = new Dictionary<string, TbBone>();
        int skeletonCount = p.r.Count("skeletonCount");        // read once: the bound is a stream read here
        for (int i = 0; i < skeletonCount; i++)
        {
            string name = p.r.Str();
            if (dialect == Dialect.FinalBite) p.r.U32();       // f5 inserts one 0xFFFFFFFF word after the rig name
            rigs[name] = p.Bone(0);
            if (dialect != Dialect.Blue) { p.r.U32(); p.r.U32(); }  // rig trailer (bool + u32)
        }
        if (p.r.Pos != data.Length)
            throw new TbSkeletonException($"grammar drift ({dialect}): consumed {p.r.Pos} of {data.Length} bytes");
        return rigs;
    }

    /// <summary>The rig an entity's art name belongs to. Level entity names are <c>&lt;rig&gt;_&lt;set&gt;</c>
    /// (<c>attackhelicopter_air</c>, <c>barracuda_fish</c>) and rig names are the art stem, so the longest
    /// rig name the query starts with wins — that keeps <c>barnacle2</c> from being stolen by the shorter
    /// <c>barnacle</c>. Matching is case-insensitive because rig names carry the pre-alias spelling
    /// (<c>attackhelicopter</c>) while the cell/anim cascade uses the underscored one.</summary>
    public static string? FindRig(IReadOnlyDictionary<string, TbBone> rigs, string entityName)
    {
        if (string.IsNullOrEmpty(entityName)) return null;
        string low = entityName.ToLowerInvariant();
        string? best = null;
        foreach (var name in rigs.Keys)
        {
            string n = name.ToLowerInvariant();
            if (low != n && !low.StartsWith(n + "_", StringComparison.Ordinal)) continue;
            if (best is null || n.Length > best.Length) best = name;
        }
        if (best is not null) return best;

        // Final Bite concatenates the theme straight onto the rig with no separator, so its level types
        // are <catfish><atlantis1>_<n> (catfishatlantis1_7) where the underscore boundary above never
        // fires. Fall back to the longest rig that is a bare prefix of the name; only reached after the
        // boundary scan misses, so Tasty Blue / Forever (whose names carry the underscore) are untouched,
        // and the longest match keeps a short rig from stealing a longer creature that merely begins
        // with the same letters.
        foreach (var name in rigs.Keys)
        {
            string n = name.ToLowerInvariant();
            if (low.StartsWith(n, StringComparison.Ordinal) && (best is null || n.Length > best.Length))
                best = name;
        }
        return best;
    }

    /// <summary>Rig NAMES out of a <c>skeletons.bin</c> the full grammar refuses — the fallback for the
    /// Forever / Final Bite variants, whose record layout carries fields Blue's loaders do not
    /// (an <c>f32/int</c> pair per node, an extra <c>-1</c> field per rig), so <see cref="Read"/> drops
    /// those files whole.
    ///
    /// Every rig's root joint is literally named <c>root</c>, and in all three titles the rig's own name
    /// sits immediately before it: <c>[u32 len]name\0 [u32 5]root\0</c>, with Final Bite inserting one
    /// extra <c>0xFFFFFFFF</c> word between the two strings. Anchoring on that pattern and validating the
    /// back-found length field recovered 160/160 declared rigs from Forever and 173/174 from Final Bite —
    /// the names alone are what the engine-art scan needs to recognise a record that registers its
    /// creature as a rig; they never claim shape data, so no half-parsed geometry can reach the viewport.</summary>
    public static HashSet<string> ScanRigNames(byte[] b)
    {
        var outp = new HashSet<string>(StringComparer.Ordinal);
        // scan form: [u32 nameLen] name\0 [u32 5] "root\0"   (Final Bite: an extra -1 word after name\0)
        for (int a = 8; a + 9 <= b.Length; a++)
        {
            if (b[a] != 5 || b[a + 1] != 0 || b[a + 2] != 0 || b[a + 3] != 0) continue;   // root string len
            if (b[a + 4] != (byte)'r' || b[a + 5] != (byte)'o' || b[a + 6] != (byte)'o'
                || b[a + 7] != (byte)'t' || b[a + 8] != 0)
                continue;
            int nul = a - 1;                                   // the byte just before the root length
            // Final Bite inserts a 0xFFFFFFFF word between name\0 and the root length, so the four
            // bytes here are that word and the name's NUL sits four earlier; step back over it.
            if (nul >= 4 && b[nul] == 0xff && b[nul - 1] == 0xff
                && b[nul - 2] == 0xff && b[nul - 3] == 0xff)
                nul -= 4;                                      // now nul is the name's terminating NUL
            if (nul < 5 || b[nul] != 0) continue;
            int s = nul;
            while (s > 0 && NameByte(b[s - 1])) s--;
            int len = nul - s;
            if (len is < 1 or > 64 || s < 4 || !char.IsLetter((char)b[s])) continue;   // names begin with a letter
            if (BitConverter.ToUInt32(b, s - 4) != (uint)len + 1) continue;
            outp.Add(System.Text.Encoding.ASCII.GetString(b, s, len));
        }
        return outp;

        // letters / digits / _ / - : the spelling alphabet every rig name obeys
        static bool NameByte(byte c) =>
            c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
              or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-';
    }

    // ---- record readers ----

    /// <summary>Carries the active layout next to the byte cursor so the node / shape / anim readers can
    /// branch on it without threading a parameter through every call.</summary>
    private sealed class Parser(Reader r, Dialect d)
    {
        public readonly Reader r = r;
        public readonly Dialect d = d;

        // node keyframe record width (words) and shape keyframe width, per layout
        int NodeKfW => d == Dialect.Blue ? NodeKf : F45NodeKf;
        int ShapeKfW => d == Dialect.Blue ? ShapeKf : F45ShapeKf;

        public TbBone Bone(int depth)
        {
            if (depth > 256) throw new TbSkeletonException("node tree too deep");
            string name = r.Str();
            // Blue: 2 ints. Forever: 3 ints. Final Bite: 4 ints (an extra u32 stored as both int and f32).
            int flagA = r.Int(), flagB = r.Int();
            int flagC = d == Dialect.Blue ? 0 : r.Int();
            int mode = -1;
            if (d == Dialect.FinalBite) mode = r.Int();   // SkeletonBone+0x58: 0 = polar, else cartesian
            float p1 = r.F32(), p2 = r.F32();
            var tracks = NodeTracks();
            var shapes = new TbShape[r.Count("shapeCount")];
            for (int i = 0; i < shapes.Length; i++) shapes[i] = Shape();
            var kids = new TbBone[r.Count("childCount")];
            for (int i = 0; i < kids.Length; i++) kids[i] = Bone(depth + 1);
            return new TbBone
            {
                Name = name, FlagA = flagA, FlagB = flagB | (flagC << 1), P1 = p1, P2 = p2, Mode = mode,
                Tracks = tracks, Shapes = shapes, Children = kids,
            };
        }

        // Node anim list (f4 FUN_004bbcf0 / f5 FUN_1400d6160): each track reads its name, and the
        // Forever / Final Bite reader only consumes keyframes when the name is in the fixed vocabulary.
        // Blue's reader (FUN_004bebe0) has no such guard, so its tracks are read unconditionally.
        private TbTrack[] NodeTracks()
        {
            int n = r.Count("animCount");
            var list = new List<TbTrack>(n);
            for (int i = 0; i < n; i++)
            {
                string name = r.Str();
                bool readBody = d == Dialect.Blue || AnimVocab.Contains(name);
                list.Add(Track(name, readBody, NodeKfW));
            }
            return list.ToArray();
        }

        private TbShape Shape()
        {
            string image = r.Str();
            if (d == Dialect.Forever)
            {
                r.Int();                          // @0x30
                float a = r.F32(), b = r.F32(), c = r.F32();
                int nn = r.Int();                 // @0x40 draw order
                if (r.Int() != 0) r.U32();        // hasColour -> 4-byte RGBA
                return new TbShape(image, a, b, c, 1f, 1f, nn, ShapeTracks());
            }
            if (d == Dialect.FinalBite)
            {
                r.Int();                          // @0x48
                r.Int(); r.Int(); r.Int();        // @0x4c,4d,4e flags
                float a = r.F32(), b = r.F32(), c = r.F32();   // angle A, offsets B, C
                int nn = r.Int();                 // draw order N
                float s1 = r.F32(), s2 = r.F32();  // per-axis scale (S2 == -1 is the bilateral mirror)
                if (r.Int() != 0) r.U32();        // hasColour -> 4-byte RGBA
                return new TbShape(image, a, b, c, s1, s2, nn, ShapeTracks());
            }
            // Blue
            float ba = r.F32(), bb = r.F32(), bc = r.F32();
            int bn = r.Int();
            return new TbShape(image, ba, bb, bc, 1f, 1f, bn, Tracks(r, ShapeKfW));
        }

        // Shape anim list (f4 FUN_004cd190 / f5 FUN_1400e6010): the body is read for EVERY track, no
        // vocabulary guard, which is what made the shape keyframe stride the earlier desync.
        private TbTrack[] ShapeTracks()
        {
            int n = r.Count("animCount");
            var list = new List<TbTrack>(n);
            for (int i = 0; i < n; i++)
            {
                string name = r.Str();
                list.Add(Track(name, true, ShapeKfW));
            }
            return list.ToArray();
        }

        private TbTrack Track(string name, bool readBody, int width)
        {
            if (!readBody) return new TbTrack(name, []);
            int k = r.Count("kfCount");
            var keys = new TbKeyframe[k];
            for (int j = 0; j < k; j++)
            {
                var row = new float[width];
                for (int f = 0; f < width; f++) row[f] = r.F32();
                keys[j] = new TbKeyframe(row);
            }
            return new TbTrack(name, keys);
        }
    }

    // Blue's original unconditional track reader, kept for the legacy path shape above.
    private static TbTrack[] Tracks(Reader r, int width)
    {
        var outp = new TbTrack[r.Count("animCount")];
        for (int i = 0; i < outp.Length; i++)
        {
            string name = r.Str();
            int n = r.Count("kfCount");
            var keys = new TbKeyframe[n];
            for (int k = 0; k < n; k++)
            {
                var row = new float[width];
                for (int f = 0; f < width; f++) row[f] = r.F32();
                keys[k] = new TbKeyframe(row);
            }
            outp[i] = new TbTrack(name, keys);
        }
        return outp;
    }

    /// <summary>Cursor over the byte stream. Count and string lengths are validated the way the Python
    /// reference does, so a mis-synced read fails loudly instead of inventing a giant allocation.</summary>
    private sealed class Reader(byte[] b)
    {
        private readonly byte[] _b = b;
        public int Pos { get; private set; }

        public uint U32()
        {
            if (Pos + 4 > _b.Length) throw new TbSkeletonException($"eof u32 @{Pos}");
            uint v = (uint)(_b[Pos] | (_b[Pos + 1] << 8) | (_b[Pos + 2] << 16) | (_b[Pos + 3] << 24));
            Pos += 4;
            return v;
        }

        public int Int() => (int)U32();

        public float F32()
        {
            if (Pos + 4 > _b.Length) throw new TbSkeletonException($"eof f32 @{Pos}");
            uint raw = (uint)(_b[Pos] | (_b[Pos + 1] << 8) | (_b[Pos + 2] << 16) | (_b[Pos + 3] << 24));
            Pos += 4;
            return BitConverter.Int32BitsToSingle((int)raw);
        }

        public string Str()
        {
            uint n = U32();
            if (n == 0) return "";
            if (n > 4096 || Pos + n > _b.Length || _b[Pos + n - 1] != 0)
                throw new TbSkeletonException($"bad string len={n} @{Pos - 4}");
            string s = System.Text.Encoding.ASCII.GetString(_b, Pos, (int)n - 1);
            Pos += (int)n;
            return s;
        }

        /// <summary>Read a record count. This ADVANCES the stream, so capture it in a local: putting it
        /// in a `for` condition re-reads (and re-consumes) the field on every pass and silently loses sync
        /// a few bytes in — which is exactly how the first C# port diverged from the Python reference.</summary>
        public int Count(string what)
        {
            uint n = U32();
            if (n > 4096) throw new TbSkeletonException($"{what}={n} @{Pos - 4}");
            return (int)n;
        }
    }
}
