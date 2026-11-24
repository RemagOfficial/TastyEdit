namespace TastyEditor.Core.Levels;

/// <summary>The kind of a named behaviour-tail field, which drives both how its value is decoded and
/// how (whether) it is editable in the inspector. Recovered from the engine decompiles — every field
/// below is a real serialized value read by a named stream primitive, not a raw-byte guess.</summary>
public enum TailKind
{
    /// <summary>A block-presence gate (or a conditional-count gate): an int32 that must read 0 or 1.
    /// Editing it is a STRUCTURAL write: turning a block on inserts its default body straight after the
    /// gate, turning it off removes exactly the bytes that block occupies, so the record changes length
    /// (see <see cref="TailEdit"/>).</summary>
    Gate,
    /// <summary>A plain int32 scalar (editable in place).</summary>
    Int,
    /// <summary>An 8-byte little-endian float64 (editable in place).</summary>
    F64,
    /// <summary>A 4-byte little-endian float32 (editable in place; used by the cell-colour ramp).</summary>
    F32,
    /// <summary>A data boolean serialized as int32 (editable; the engine reads it via readBoolean).</summary>
    Bool,
    /// <summary>A length-prefixed string. Structural: a name's serialized width IS its text length, so
    /// renaming rewrites the prefix and resizes the record by the difference.</summary>
    Str,
    /// <summary>A variable-length array count (the IndexedCellColours ramp). Structural: editing it adds
    /// or removes the 16-byte colour entries that follow.</summary>
    Count,
}

/// <summary>One named field decoded from an entity's behaviour tail. <see cref="LaneOff"/> is the
/// offset from the tail start where the value lives, so an edit is a length-preserving, span-clamped
/// in-place write at exactly the byte the engine reads — the name is a view over the record, never a
/// re-serialisation of it.</summary>
public readonly record struct TailField(string Group, string Name, TailKind Kind, int LaneOff)
{
    /// <summary>Serialized width of the value this field names. Every writable kind is a 4-byte word
    /// except <see cref="TailKind.F64"/>; strings are variable-width and never written.</summary>
    public int Width => Kind == TailKind.F64 ? 8 : 4;

    /// <summary>Whether this field can be edited in place without changing the record length.</summary>
    public bool Editable => Kind is TailKind.Int or TailKind.F64 or TailKind.F32 or TailKind.Bool;

    /// <summary>Whether this field is edited by RESHAPE — a write whose byte width depends on its
    /// value, so it goes through <see cref="TailEdit"/> instead of a lane write. Every tail kind is
    /// editable one way or the other; nothing in a level file is view-only.</summary>
    public bool Resizing => Kind is TailKind.Gate or TailKind.Str or TailKind.Count;

    /// <summary>Editable <em>and</em> wholly inside the record's own byte frame. A field the tail walk
    /// recovered past the frame belongs to the neighbour the framing splitter cut early on, so it is
    /// shown by name but must not be written (the span-clamped lane write would refuse it anyway).</summary>
    public bool EditableWithin(int frameLen) => Editable && LaneOff >= 0 && LaneOff + Width <= frameLen;

    /// <summary>A reshape candidate: structural, and its own word lies inside the decoded frame. The
    /// walk over the record's complete tail re-checks this, so the frame test only keeps the
    /// look-ahead-recovered fields of a mis-framed record out of the editor.</summary>
    public bool ReshapeableWithin(int frameLen)
        => Resizing && LaneOff >= 0 && LaneOff + Width <= frameLen;
}

/// <summary>The byte range one flag-gated behaviour block occupies in a decoded tail: the offset of its
/// int32 presence gate, and the body the gate covers (empty when the block is off). This is what turns
/// a gate from a displayed number into an editable switch — turning a block on or off is exactly
/// inserting or removing <see cref="BodyLen"/> bytes at <see cref="BodyOff"/>, and the extents come from
/// the same walk that named the fields, so they cannot disagree with the grammar.</summary>
public readonly record struct TailBlock(string Group, string GateName, int GateOff, int BodyOff, int BodyLen)
{
    /// <summary>One past the block's last body byte (the gate word is not part of the body).</summary>
    public int BodyEnd => BodyOff + BodyLen;
}

/// <summary>The complete NAMED view over the behaviour tail of an entity record (Tasty Blue /
/// Forever / Final Bite). Every byte after the priority int32 belongs to a real, named engine field —
/// there is no un-modelled raw region, so the inspector never shows a hex lane.
///
/// The layout is read straight off each game's own <c>ElementEntity::readBinary</c>. After priority the
/// record carries a run of int32-gated sub-blocks, each read by its own <c>readBinary</c> whose primitive
/// call order gives the exact field order and types:
///
/// <b>Final Bite (f5)</b> — <c>out_rb/b024</c> @<c>007e0204</c>, SEVEN gates:
///   1 ContVarMoveDirection (b009) : f64, f64, bool, f64×6
///   2 ContVarPathFollow    (b010) : f64, f64, bool, f64, f64, str, bool, bool, int, f64, int, int
///   3 ContVarPointList     (b011) : f64, f64, bool, f64, f64, str, int, int, f64, f64, int, f64
///   4 ContVarRandomArea    (b012) : f64, f64, bool, f64, f64, f64, str
///   5 EntityEmitter        (b030) : f64×12, int, int, bool, str, f64, bool, [if bool] f64
///   6 EntityLink           (b024) : str (read inline, no sub-object)
///   7 IndexedCellColours   (b034) : int n, then n × (f32, f32, f32, f32)
///
/// <b>Forever (f4)</b> — <c>outrd_f4/r00_15_FUN_00561b60</c> @<c>00561b60</c>, SIX gates. Same gate
/// order and the same bodies for Movement / Point List / Cell Colours, but three real differences, each
/// read off f4's own code rather than assumed:
///   • Path Follow (FUN_004e6290) STOPS at the 10th field — no trailing int, int (f5 has @0x50/@0x54).
///   • Random Area  (FUN_004e7630) has five doubles, not six — no <c>param E</c> before the name.
///   • Emitter      (FUN_0060d480) is f64×12, int, int, f64 — it never calls readBoolean or readString,
///     so there is no <c>flag 1</c>, no <c>spawn prefab</c> name and no <c>has extra</c> conditional.
///   • There is NO Entity Link block at all: gate 6 constructs a 0x10 object whose constructor
///     (<c>out_f4vt/ctor_00643c30.c</c>) stores <c>IndexedCellColours::vftable</c>, so Cell Colours is
///     the SIXTH gate, promoted from f5's seventh.
///
/// <b>Tasty Blue (tb)</b> — <c>out_tbrb/a001_FUN_005578c0</c> @<c>005578c0</c>, found through the PE's own
/// RTTI (<c>.?AVElementEntity@@</c> → COL → vftable <c>0x006f6048</c>, slot <c>+0x8c</c>, the same slot the
/// Forever build uses). It has only FOUR gates and returns straight after the last one: Movement,
/// Path Follow, Random Area, Emitter. There is no Point List, no Entity Link and no IndexedCellColours in
/// this engine build at all — reading an f5/f4 grammar over a tb tail was naming those bytes under the
/// wrong block, which is what produced the tb residuals.
///
/// <b>Where the names come from.</b> Every field label below is a string the engine itself carries, never
/// a name we invented, and each is joined to its stream slot by the <em>destination object offset</em>
/// that both readers write: <c>readBinary</c> fixes the order, the game's own attribute reader fixes the
/// names. <c>out_f4attr</c> / <c>out_f4pl</c> hold f4's per-block attribute readers (FUN_004e5a40,
/// FUN_004e6390, FUN_004e6cc0, FUN_004e7700, FUN_0060d540) and its <c>ElementEntity::readXml</c>
/// (FUN_00562180) for the gate names; <c>out_tbxml</c> holds tb's (FUN_004e70a0, FUN_004e79d0,
/// FUN_004e8130, FUN_005c1370) and <c>out_tbrb/a003</c> its gate names. Final Bite is the exception:
/// on ARM64 the compiler builds short attribute names from immediates inside a stack SSO buffer, so
/// <c>readXML</c> has no quoted literal to grep — <c>out_f5attr</c> keeps those bodies and the names were
/// rebuilt byte by byte from the stores into each buffer, then cross-checked against the long names that
/// <em>are</em> in libmain.so's rodata (s_directionvariation, s_startingpartitions, s_indexedcellcolours …).
/// Only two blocks have no engine name anywhere in any build — the Entity Link string and the
/// IndexedCellColours float4 array — and both are labelled from their owning class instead, as noted
/// where they are read.
///
/// Primitives (f5 <c>out_prim</c>, f4 <c>0x4f10d0</c>/<c>0x4f1100</c>/<c>0x4f1130</c>/<c>0x4f1160</c>/
/// <c>0x4f1190</c>, tb <c>0x4ef350</c>/<c>0x4ef380</c>/<c>0x4ef3e0</c>/<c>0x4ef410</c>/<c>0x4ef4a0</c>, all
/// of them thin wrappers over one <c>read(dst, size, mode)</c> — tb's is <c>0x4ef1e0</c>): f64 = 8 B,
/// int = 4 B, float = 4 B, bool = readBoolean = 4 B (true iff 1), string =
/// int32 length + that many raw bytes. The walk is SELF-VALIDATING: a gate must read 0 or 1, an
/// in-block bool must read 0/1, and a string length / colour count must fit the tail; on any violation
/// it truncates the partial block and stops, so a record from a grammar we do not model can never show
/// misleading names. Nothing here re-serialises — it is a read view over the verbatim tail, so it can
/// never desynchronise the record framing (I1/I3). Writes live in <see cref="TailEdit"/>, which drives
/// this same grammar (in measure mode for a block's default body width) so an edit can only ever produce
/// bytes the reader above reads back cleanly.</summary>
public static class TailSem
{
    /// <summary>Which shipped <c>ElementEntity::readBinary</c> produced a tail. The three games share
    /// the gate-then-block shape but differ in the block LIST and in two block bodies, which is exactly
    /// why one walk cannot cover all of them — see the class doc.</summary>
    public enum Game { F5, F4, TB }

    public static Game ForGame(string? key) => key switch
    {
        "f4" => Game.F4,
        "tb" => Game.TB,
        _ => Game.F5,
    };

    /// <summary>Decode the flag-gated behaviour blocks of <paramref name="tail"/> into named fields,
    /// using <paramref name="gameKey"/>'s grammar. Returns the fields (offsets relative to the tail
    /// start) and the number of tail bytes the walk consumed. For a correctly framed record the walk
    /// consumes the whole tail, so <c>Consumed == tail.Length</c> and every byte is a named field.</summary>
    public static (List<TailField> Fields, int Consumed) Decode(byte[] tail, string? gameKey = null)
        => new Walk(tail, ForGame(gameKey)).Run();

    /// <summary>The same walk, and also the byte extent of every block it read. The extents are what make
    /// a presence gate editable: enabling or disabling a behaviour block is an insert / remove of exactly
    /// <see cref="TailBlock.BodyLen"/> bytes at <see cref="TailBlock.BodyOff"/>, and reading them off this
    /// pass means a structural write follows the grammar instead of a hand-typed size table.</summary>
    public static (List<TailField> Fields, int Consumed, List<TailBlock> Blocks) Layout(byte[] tail, string? gameKey = null)
        => Layout(tail, 0, tail?.Length ?? 0, gameKey);

    /// <summary>The walk over a WINDOW of a bigger buffer — <paramref name="len"/> bytes starting at
    /// <paramref name="off"/> — without copying the window out first. This is what lets the entity framing
    /// measure one record inside the section's shared block (<see cref="EntityFraming.MeasureBlock"/>),
    /// where copying a tail per record would run the decode many times over. Field offsets stay relative to
    /// the window start, exactly as if the window had been its own array.</summary>
    public static (List<TailField> Fields, int Consumed, List<TailBlock> Blocks) Layout(byte[] buf, int off, int len,
                                                                                        string? gameKey = null)
    {
        var w = new Walk(buf, off, len, ForGame(gameKey));
        var (f, c) = w.Run();
        return (f, c, w.Blocks);
    }

    /// <summary>One pass over a window of a buffer: how many bytes the grammar read, and whether it read the
    /// whole gate list. "Complete" is what makes a record end claimable — a gate that does not read 0/1
    /// means the stream has desynchronised (or the record carries a block this build does not have), so the
    /// bytes after the stop belong to nobody and <see cref="EntityFraming.MeasureRecord"/> refuses to guess.
    /// The framing measure needs both together, and this is the single-pass form (the decode walks each
    /// record once rather than twice).</summary>
    public static (int Consumed, bool Complete) Measure(byte[] buf, int off, int len, string? gameKey)
    {
        var w = new Walk(buf, off, len, ForGame(gameKey));
        var (_, c) = w.Run();
        return (c, w.Clean);
    }

    // Default body size per (game, block), measured once by running that game's own grammar with every
    // gate forced on and every value reading as its serialized zero.
    private static readonly Dictionary<Game, Dictionary<string, int>> _bodySizes = new();
    private static readonly object _sizeLock = new();

    /// <summary>Byte width of the body a block gets when its gate is turned on from nothing: every scalar
    /// written as 0, every embedded string empty, no optional trailing field. Measured by re-running
    /// <b>the same grammar the reader uses</b> (a <see cref="Walk"/> in measure mode), never by a constant
    /// typed next to it, so a field added to the decode is automatically sized. -1 for a block this game
    /// does not have.</summary>
    public static int DefaultBodySize(string? gameKey, string group)
    {
        Game g = ForGame(gameKey);
        lock (_sizeLock)
        {
            if (!_bodySizes.TryGetValue(g, out var map))
            {
                map = new Dictionary<string, int>();
                var w = new Walk(Array.Empty<byte>(), g, measure: true);
                w.Run();
                foreach (var b in w.Blocks) map[b.Group] = b.BodyLen;
                _bodySizes[g] = map;
            }
            return map.TryGetValue(group, out int n) ? n : -1;
        }
    }

    /// <summary>Read the current value of a decoded field out of the (copied) tail bytes, for display:
    /// a <see cref="double"/> for <see cref="TailKind.F64"/> / <see cref="TailKind.F32"/>, an
    /// <see cref="int"/> for <see cref="TailKind.Int"/> / <see cref="TailKind.Bool"/> /
    /// <see cref="TailKind.Gate"/> / <see cref="TailKind.Count"/>, or a <see cref="string"/> for
    /// <see cref="TailKind.Str"/> (NUL / non-printable terminated). Never throws; returns null when the
    /// offset is out of range.</summary>
    public static object? Value(byte[] tail, TailField f)
    {
        if (tail is null) return null;
        switch (f.Kind)
        {
            case TailKind.F64:
                return f.LaneOff + 8 <= tail.Length ? BitConverter.ToDouble(tail, f.LaneOff) : (double?)null;
            case TailKind.F32:
                return f.LaneOff + 4 <= tail.Length ? (double)BitConverter.ToSingle(tail, f.LaneOff) : (double?)null;
            case TailKind.Int:
            case TailKind.Bool:
            case TailKind.Gate:
            case TailKind.Count:
                return f.LaneOff + 4 <= tail.Length ? BitConverter.ToInt32(tail, f.LaneOff) : (int?)null;
            case TailKind.Str:
            default:
                if (f.LaneOff < 0 || f.LaneOff >= tail.Length) return "";
                int e = f.LaneOff;
                while (e < tail.Length && tail[e] != 0 && tail[e] >= 32 && tail[e] < 127) e++;
                return System.Text.Encoding.Latin1.GetString(tail, f.LaneOff, e - f.LaneOff);
        }
    }

    // ---------------------------------------------------------------------- the walk --

    /// <summary>A single decode pass. The field helpers all take their group from <see cref="_grp"/>
    /// (set by <see cref="Gated"/>) so each game's grammar below reads as nothing but the block list
    /// and its field sequences, which is what makes the f4 / f5 / tb differences auditable against the
    /// decompile. In <paramref name="measure"/> mode the same code path is run with no bytes at all —
    /// every gate reads as on, every other value as zero, every string empty — which is how
    /// <see cref="DefaultBodySize"/> gets a block's fresh-body width out of the grammar itself.</summary>
    private sealed class Walk
    {
        private readonly byte[] _t;
        private readonly int _b0;    // absolute index in _t of the tail's first byte (0 for a standalone tail)
        private readonly int _n;
        private readonly bool _measure;
        private readonly List<TailField> _f = new();
        private readonly List<TailBlock> _b = new();
        private readonly Game _game;
        private int _p;
        private int _lastGate;    // the value the gate just read, so Gated needn't re-read it
        private int _lastBool;    // ditto for the bool that gates Emitter's trailing double
        private bool _clean;      // the walk read the grammar's LAST gate, so the tail ended where it did
        private string _grp = "";

        public Walk(byte[] tail, Game game, bool measure = false)
            : this(tail, 0, tail?.Length ?? 0, game, measure) { }

        public Walk(byte[] buf, int off, int len, Game game, bool measure = false)
        {
            _game = game;
            _measure = measure;
            _t = buf ?? Array.Empty<byte>();
            _b0 = measure ? 0 : off;
            // A measuring pass has no bytes to run out of: it exists to learn widths, so every
            // capacity check passes and no value is ever read out of _t.
            _n = measure ? int.MaxValue / 16 : len;
        }

        /// <summary>The blocks this pass read, in stream order (measure mode: one per block in the
        /// grammar, each with its default body width).</summary>
        public List<TailBlock> Blocks => _b;

        public (List<TailField>, int) Run() => _game switch
        {
            Game.F4 => Forever(),
            Game.TB => Blue(),
            _ => FinalBite(),
        };

        /// <summary>Set when the pass read the grammar's last gate, i.e. the tail it was given ended exactly
        /// where the stream says the record does.</summary>
        public bool Clean => _clean;

        // Each helper returns false (leaving `_p` at the field start) if the value would not fit or, for
        // a gate / bool, is not 0/1 — the caller then truncates the partial block and stops.
        private bool Gate(string label)
        {
            if (_p + 4 > _n) return false;
            int v = _measure ? 1 : BitConverter.ToInt32(_t, _b0 + _p);
            if (v is not (0 or 1)) return false;
            _lastGate = v;
            _f.Add(new TailField(_grp, label, TailKind.Gate, _p));
            _p += 4;
            return true;
        }
        private bool F64(string label)
        {
            if (_p + 8 > _n) return false;
            _f.Add(new TailField(_grp, label, TailKind.F64, _p));
            _p += 8;
            return true;
        }
        private bool F32(string label)
        {
            if (_p + 4 > _n) return false;
            _f.Add(new TailField(_grp, label, TailKind.F32, _p));
            _p += 4;
            return true;
        }
        private bool I32(string label)
        {
            if (_p + 4 > _n) return false;
            _f.Add(new TailField(_grp, label, TailKind.Int, _p));
            _p += 4;
            return true;
        }
        private bool Bool(string label)
        {
            if (_p + 4 > _n) return false;
            int v = _measure ? 0 : BitConverter.ToInt32(_t, _b0 + _p);
            if (v is not (0 or 1)) return false;
            _lastBool = v;
            _f.Add(new TailField(_grp, label, TailKind.Bool, _p));
            _p += 4;
            return true;
        }
        private bool Str(string label)
        {
            if (_p + 4 > _n) return false;
            int ln = _measure ? 0 : BitConverter.ToInt32(_t, _b0 + _p);
            if (ln < 0 || _p + 4 + ln > _n) return false;   // string data must fit inside the tail
            _p += 4;
            _f.Add(new TailField(_grp, label, TailKind.Str, _p));
            _p += ln;
            return true;
        }
        // Commit a gated block: remember where it began, run its readers, and on any failure roll the
        // field list + cursor back to the block start (so a half-read block never mis-frames names).
        private bool Block(int start, Func<bool> read)
        {
            int mark = _f.Count;
            if (read()) return true;
            _f.RemoveRange(mark, _f.Count - mark);
            _p = start;
            return false;
        }
        // A gate + optional body: read the gate (always), then the body only when the gate is set.
        // Records the block's extent on success — an absent block is recorded with an empty body, which
        // is exactly what a later "turn it on" write has to fill in.
        private bool Gated(string group, string gateLabel, Func<bool> body)
        {
            _grp = group;
            int gateOff = _p;
            if (!Gate(gateLabel)) return false;
            // The gate field belongs to the block, so a rolled-back body keeps it (it really is there).
            int mark = _f.Count;
            int bmark = _b.Count;
            if (_lastGate == 0 && !_measure)
            {
                // Absent block: still record it, with an empty body sitting right after the gate. That is
                // the byte extent a later "turn it on" write inserts into, and it lets the re-proof see
                // the off state as a real block instead of a hole in the walk.
                _b.Add(new TailBlock(group, gateLabel, gateOff, _p, 0));
                return true;
            }
            int s = _p;
            if (!Block(s, body))
            {
                _f.RemoveRange(mark, _f.Count - mark);
                _b.RemoveRange(bmark, _b.Count - bmark);
                return false;
            }
            _b.Add(new TailBlock(group, gateLabel, gateOff, s, _p - s));
            return true;
        }
        // IndexedCellColours::readBinary — identical in f5 (b034) and f4 (FUN_00643d20): an int32 count,
        // then count × four float32 (the ramp entries pushed onto a vector). The engine gives this block
        // no per-entry attribute names at all (no "cellcolour"/"colourcount" string exists in any of the
        // three builds) — it is a raw float4 array on the class IndexedCellColours, so the field labels
        // name the component each serialized float actually is.
        private bool CellColours()
        {
            if (_p + 4 > _n) return false;
            int count = _measure ? 0 : BitConverter.ToInt32(_t, _b0 + _p);
            if (count < 0 || count > _n / 16) return false;   // each colour is 4 f32 = 16 B
            _f.Add(new TailField(_grp, "colour count", TailKind.Count, _p));
            _p += 4;
            for (int c = 0; c < count; c++)
                if (!F32($"colour {c} R") || !F32($"colour {c} G")
                    || !F32($"colour {c} B") || !F32($"colour {c} A"))
                    return false;
            return true;
        }
        // ContVarMoveDirection — b009 (f5), FUN_004e59d0 (f4) and FUN_004e7030 (tb) all read the same
        // nine slots at +0x00..+0x40, and each game's own XML attribute reader (f4 FUN_004e5a40,
        // tb FUN_004e70a0) writes those exact offsets under these exact names, so the names are shared.
        private bool MovementBody() =>
            F64("priority") && F64("firstpriority") && Bool("first")
            && F64("maxspeed") && F64("accel") && F64("direction")
            && F64("directionvariation") && F64("changedirectionminperiod")
            && F64("changedirectionmaxperiod");
        // ContVarRandomArea — priority / firstpriority / first / maxspeed / accel then the area name;
        // identical in f4 (FUN_004e7630, whose XML reader FUN_004e7700 writes +0x00..+0x28 with these
        // names) and tb (FUN_004e8060 / FUN_004e8130, out_tbxml a005). Final Bite reads one extra
        // double, "rpre", in between — see RandomAreaBodyF5.
        private bool RandomAreaBody() =>
            F64("priority") && F64("firstpriority") && Bool("first")
            && F64("maxspeed") && F64("accel") && Str("definedareaname");
        // ContVarPointList — b011 (f5) and FUN_004e6bb0 (f4) agree field for field, and each game's own
        // attribute reader joins them by destination offset: f4's FUN_004e6cc0 and f5's
        // ContVarPointList::readXML (out_f5attr/003) both name +0x00 priority ... +0x60
        // considerdoneafterstucktime in exactly the readBinary call order.
        private bool PointListBody() =>
            F64("priority") && F64("firstpriority") && Bool("first")
            && F64("maxspeed") && F64("accel") && Str("pointlistname")
            && I32("type") && I32("doatend") && F64("stoptimeateachpoint")
            && F64("randomprioritizecloserelements") && I32("sequentialstartingpoint")
            && F64("considerdoneafterstucktime");

        // ---------------------------------------------- Final Bite (f5), seven gates --
        // The gate labels are block names the engine itself carries: libmain.so's rodata holds
        // NUL-terminated strings "movedirection", "pathfollow", "pointlist", "randomarea",
        // "entityemitter" and "indexedcellcolours" ("entitylink" is the only one absent, so that gate is
        // named from the EntityLink class symbol). Field names come from each class's readXML
        // (out_f5attr), joined to readBinary by destination offset.
        private (List<TailField>, int) FinalBite()
        {
            if (!Gated("Movement", "movedirection", MovementBody)) return Done();

            // ContVarPathFollow (b010 / readXML 0073cd90): +0x00 priority .. +0x48 aimahead, then the
            // two ints that only Final Bite has, +0x50 startingpartitions and +0x54 continuepartitions.
            if (!Gated("Path Follow", "pathfollow", () =>
                    F64("priority") && F64("firstpriority") && Bool("first")
                    && F64("maxspeed") && F64("accel") && Str("pathname")
                    && Bool("loop") && Bool("deleteatend") && I32("type")
                    && F64("aimahead") && I32("startingpartitions") && I32("continuepartitions")))
                return Done();

            if (!Gated("Point List", "pointlist", PointListBody)) return Done();

            // ContVarRandomArea (b012 / readXML 0073e694): f4's six fields plus the double "rpre" at
            // +0x28, which the Forever and Tasty Blue builds do not read.
            if (!Gated("Random Area", "randomarea", () =>
                    F64("priority") && F64("firstpriority") && Bool("first")
                    && F64("maxspeed") && F64("accel") && F64("rpre") && Str("definedareaname")))
                return Done();

            // EntityEmitter (b030 / readXML 009f4598).
            if (!Gated("Emitter", "entityemitter", EmitterBodyF5)) return Done();

            // EntityLink: gate 6 is inline in ElementEntity::readBinary and its body is one readString.
            // Neither the class nor the build carries an attribute name for it, so it is labelled by what
            // the reader stores: the name of the linked entity.
            if (!Gated("Entity Link", "entitylink", () => Str("linked entity name"))) return Done();

            if (!Gated("Indexed Cell Colours", "indexedcellcolours", CellColours)) return Done();
            return Done(true);      // read all seven gates: this tail is complete (see MeasureRecord)
        }

        // EntityEmitter::readBinary (b030), named by EntityEmitter::readXML (out_f5attr/026) at the same
        // object offsets in the same order. The first fourteen are shared with Forever; the last five are
        // Final Bite's own: autolink, the emitting-from-scriptable name, onlyemitunderplayerarea (which f4
        // also has, but reads last), the has- flag that gates the trailing double, and that double.
        private bool EmitterBodyF5()
        {
            if (!F64("minsizemult") || !F64("maxsizemult") || !F64("emitwidth") || !F64("emitheight")
                || !F64("redrange") || !F64("greenrange") || !F64("bluerange") || !F64("opacityrange")
                || !F64("initialangularvelrange") || !F64("initialanglerange")
                || !F64("emitminperiod") || !F64("emitmaxperiod")
                || !I32("priorityrange") || !I32("maxentities")
                || !Bool("autolink") || !Str("emittingfromscriptable")
                || !F64("onlyemitunderplayerarea") || !Bool("hasminplayerareatoemit"))
                return false;
            // EntityEmitter::readBinary line 70: the trailing f64 is present only when that bool is set.
            if (_lastBool != 0)
                if (!F64("minplayerareatoemit")) return false;
            return true;
        }

        // ------------------------------------------------------- Forever (f4), six gates --
        // The gate labels are the block attribute names f4's own <c>ElementEntity::readXml</c>
        // (out_f4attr/008_FUN_00562180) uses in exactly this order: priority, movedir, pathfol,
        // pointlist, randarea, entemit, indexedcellcolours. Field names come from each block's own
        // attribute reader, matched to readBinary by destination offset:
        //   Movement    FUN_004e59d0 <- FUN_004e5a40 (out_f4attr/001)  +0x00 priority .. +0x40 changedirectionmaxperiod
        //   Path Follow FUN_004e6290 <- FUN_004e6390 (out_f4attr/003)  +0x00 .. +0x48 aimahead
        //   Point List  FUN_004e6bb0 <- FUN_004e6cc0 (out_f4pl/a002)   +0x00 .. +0x60 considerdoneafterstucktime
        //   Random Area FUN_004e7630 <- FUN_004e7700 (out_f4attr/010)  +0x00 .. +0x28 definedareaname
        //   Emitter     FUN_0060d480 <- FUN_0060d540 (out_f4attr/006)  +0x38 .. +0x9c, then +0x30 last
        private (List<TailField>, int) Forever()
        {
            if (!Gated("Movement", "movedir", MovementBody)) return Done();

            // ContVarPathFollow (FUN_004e6290 / XML FUN_004e6390): +0x40 loop, +0x41 deleteatend,
            // +0x44 type, +0x48 aimahead. It stops there — no trailing ints (f5 has two).
            if (!Gated("Path Follow", "pathfol", () =>
                    F64("priority") && F64("firstpriority") && Bool("first")
                    && F64("maxspeed") && F64("accel") && Str("pathname")
                    && Bool("loop") && Bool("deleteatend") && I32("type") && F64("aimahead")))
                return Done();

            if (!Gated("Point List", "pointlist", PointListBody)) return Done();

            if (!Gated("Random Area", "randarea", RandomAreaBody)) return Done();

            // EntityEmitter (FUN_0060d480 / XML FUN_0060d540): twelve doubles at +0x38..+0x90, two
            // int32 at +0x98/+0x9c, then ONE more double at +0x30 — the stream really does read that
            // field last, which is why it sits at the end of the object and not at the front.
            if (!Gated("Emitter", "entemit", () =>
                    F64("minsizemult") && F64("maxsizemult") && F64("emitwidth") && F64("emitheight")
                    && F64("redrange") && F64("greenrange") && F64("bluerange") && F64("opacityrange")
                    && F64("initialangularvelrange") && F64("initialanglerange")
                    && F64("emitminperiod") && F64("emitmaxperiod")
                    && I32("priorityrange") && I32("maxentities")
                    && F64("onlyemitunderplayerarea")))
                return Done();

            // Gate 6 IS IndexedCellColours in f4 — no Entity Link block exists (ctor_00643c30.c, and
            // readXml's sixth attribute is literally "indexedcellcolours").
            if (!Gated("Indexed Cell Colours", "indexedcellcolours", CellColours)) return Done();
            return Done(true);      // read all six gates
        }

        // ---------------------------------------------------------- Tasty Blue (tb) --
        // Four gates only (out_tbrb/a001_FUN_005578c0.c, and the game's own ElementEntity::readXml
        // out_tbrb/a003_FUN_00557dc0 reads exactly "movedir", "pathfol", "randarea", "entemit"): after
        // the Emitter block the function just restores the exception list and returns, so there is
        // nothing else to read. Field names are the game's own XML attribute names, taken from each
        // block's attribute reader, whose destination offsets line up one-for-one with the readBinary
        // call order:
        //   Movement    FUN_004e7030  <- FUN_004e70a0 (out_tbxml/a001)  +0x00 priority .. +0x40 changedirectionmaxperiod
        //   Path Follow FUN_004e78e0  <- FUN_004e79d0 (out_tbxml/a003)  +0x00 .. +0x48 aimahead
        //   Random Area FUN_004e8060  <- FUN_004e8130 (out_tbxml/a005)  +0x00 .. +0x28 definedareaname
        //   Emitter     FUN_005c12d0  <- FUN_005c1370 (out_tbxml/a008)  +0x30 minsizemult .. +0x8c maxentities
        // All three shipped builds carry the same attribute strings, so the names are the engine's, not ours.
        private (List<TailField>, int) Blue()
        {
            if (!Gated("Movement", "movedir", MovementBody)) return Done();
            if (!Gated("Path Follow", "pathfol", PathFollowBodyTb)) return Done();
            if (!Gated("Random Area", "randarea", RandomAreaBody)) return Done();
            if (!Gated("Emitter", "entemit", EmitterBodyTb)) return Done();
            return Done(true);      // read all four gates
        }

        // ContVarPathFollow (tb FUN_004e78e0): five scalars, the path name, then loop / type / aimahead.
        // The bool at +0x40 and the int32 at +0x44 are the `loop` and `type` attributes; the trailing
        // double is `aimahead`. tb has neither f4's `deleteatend` byte at +0x41 nor f5's extra ints.
        private bool PathFollowBodyTb() =>
            F64("priority") && F64("firstpriority") && Bool("first")
            && F64("maxspeed") && F64("accel") && Str("pathname")
            && Bool("loop") && I32("type") && F64("aimahead");

        // EntityEmitter (tb FUN_005c12d0 / XML FUN_005c1370): eleven doubles then two int32, filling
        // +0x30..+0x8c of the 0x90 emitter. tb's emitter stops short of f4's — no `initialanglerange`
        // and no trailing `onlyemitunderplayerarea`, and the string `onlyemitunderplayerarea` does not
        // exist in tastyblue.exe at all.
        private bool EmitterBodyTb() =>
            F64("minsizemult") && F64("maxsizemult") && F64("emitwidth") && F64("emitheight")
            && F64("redrange") && F64("greenrange") && F64("bluerange") && F64("opacityrange")
            && F64("initialangularvelrange") && F64("emitminperiod") && F64("emitmaxperiod")
            && I32("priorityrange") && I32("maxentities");

        private (List<TailField>, int) Done(bool clean = false) { _clean = clean; return (_f, _p); }
    }
}
