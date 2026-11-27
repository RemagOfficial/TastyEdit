using System.Text.Json.Nodes;
using TastyEditor.Core.Streams;

namespace TastyEditor.Core.Levels;

/// <summary>Generic lossless codec for the ElementEngine raw-span levels (Forever
/// f4 / Final Bite f5). Every record and the whole opaque entity block are stored
/// verbatim, so re-emitting framing + raw spans reproduces the source byte-for-
/// byte (I1). The opaque entity block's end is found by a whole-file tiling oracle
/// (recursive, memoised) that trusts the deterministic deco+water sections after
/// it and, in strict mode, additionally requires the span to split into exactly
/// its authoritative record count (tb_ent_sem). 1:1 port of tb_f5_codec +
/// tb_f4_codec + their full_walk.</summary>
public sealed class ElementSpanCodec
{
    private const int CountCap = 500_000;
    private readonly ElementSchema _s;

    public ElementSpanCodec(ElementSchema s) => _s = s;

    public static readonly ElementSpanCodec FinalBite = new(FinalBiteSchema.Build());
    public static readonly ElementSpanCodec Forever = new(ForeverSchema.Build());

    public static ElementSpanCodec For(string game) => game switch
    {
        "f5" => FinalBite,
        "f4" => Forever,
        _ => throw new ArgumentException($"no packed-section reader is registered for game '{game}'"),
    };

    private void WalkRec(ByteCursor c, int si, int cnt)
    {
        var rec = _s.Recorders[si];
        for (int i = 0; i < cnt; i++) rec(c);
    }

    // --------------------------------------------------- EOF tiling oracle --

    private sealed class Walk
    {
        private readonly ElementSchema _s;
        private readonly byte[] _d;
        private readonly int _n, _lc, _h0;
        private readonly bool _strict, _front;
        private readonly Dictionary<(int, int, int), bool> _memo = new();
        private readonly Dictionary<(int, int, int), bool> _selfval = new();
        private readonly Dictionary<(int, int), (bool Ok, int FirstStart, int LastStart, int TrueFloor)> _anchor = new();
        private readonly Dictionary<(int, int), int> _measured = new();
        // TPE_DECODE_DIAG=1: narrate the strict walk's rejections. A level that falls through to
        // the lenient tiling still round-trips, so without this the only clue is a broken viewport.
        private readonly bool _diag;
        public readonly List<(int Start, int End, int Cnt)> Spans = new();

        // Diagnostics (only read when TB_DECODE_DIAG is set): how much work each
        // FullWalk pass actually does, to localise the super-linear blow-up.
        public long DiagScanIter, DiagTryDeco, DiagSplit, DiagParse;

        public Walk(ElementSchema s, byte[] d, int lc, int h0, bool strict, bool front)
        {
            _s = s; _d = d; _n = d.Length; _lc = lc; _h0 = h0; _strict = strict; _front = front;
            _diag = Environment.GetEnvironmentVariable("TPE_DECODE_DIAG") is not null;
        }

        private void WalkRec(ByteCursor c, int si, int cnt)
        {
            var rec = _s.Recorders[si];
            for (int i = 0; i < cnt; i++) rec(c);
        }

        private bool BlockSelfValid(int start, int cnt, int e)
        {
            var key = (start, cnt, e);
            if (!_selfval.TryGetValue(key, out var v))
            {
                DiagSplit++;
                var (heads, ok) = EntityFraming.SplitBlock(_d, start, cnt, e);
                // _front: record-exact means record-exact at BOTH ends. The loader reads `count`
                // records back to back straight out of the count field, so the payload's FIRST byte
                // must be a head. SplitBlock scans forward for the first head it can parse, so
                // without this a candidate landing inside the previous record still "validates" —
                // which is what cut a record's behaviour tail short (an Emitter body ran past the
                // span) and handed the following section's bytes to the wrong owner.
                v = ok && heads.Count > 0
                    && (!_front || heads[0].RecStart == start)
                    && heads[^1].RecStart < e;
                _selfval[key] = v;
            }
            return v;
        }

        // Locate the cnt-th entity head of a block ONCE (scanning with limit=EOF), so the
        // strict self-valid test becomes O(1) for almost every candidate block end e. The
        // set of the first cnt heads is independent of e while e stays past them: for
        // e <= lastStart fewer than cnt heads fit (false); for e >= trueFloor the same cnt
        // heads all parse within e (true). Only the head's own parse span between them
        // (<= 16+nameLen+instLen <= ~144 bytes) is ambiguous and still calls the exact test.
        // This preserves the oracle's accept/reject decision byte-for-byte.
        private (bool Ok, int FirstStart, int LastStart, int TrueFloor) HeadAnchor(int start, int cnt)
        {
            if (_anchor.TryGetValue((start, cnt), out var cached)) return cached;
            var (heads, ok) = EntityFraming.SplitBlock(_d, start, cnt, _n);
            var r = ok && heads.Count == cnt
                ? (true, heads[0].RecStart, heads[^1].RecStart, heads[^1].XyOff + 8)
                : (false, 0, 0, 0);
            _anchor[(start, cnt)] = r;
            return r;
        }

        // The block end the reading rules give (see EntityFraming.MeasureBlock), or -1 when some record in
        // the run is not fully explained by the grammar. Cached per (start, count): it does not depend on the
        // candidate end being tested, and without the cache the measure would rerun for every tiling attempt.
        private int MeasuredEnd(int start, int cnt)
        {
            if (!_measured.TryGetValue((start, cnt), out int e))
            {
                e = EntityFraming.MeasureBlock(_d, start, cnt, _s.Game, out int me) ? me : -1;
                _measured[(start, cnt)] = e;
            }
            return e;
        }

        private int? TryDecoWaterAt(int p)
        {
            DiagTryDeco++;
            var c = new ByteCursor(_d, p);
            try
            {
                int dn = c.I32();
                if (dn < 0 || dn > CountCap) return null;
                // Fit guard: a deco record is ≥ ~30 bytes (type+dx+dy+size+flag+colour+
                // dims+prio), so dn records cannot claim more than the remaining bytes. A
                // bogus offset inside an entity block often reads a large "count" (a real
                // rotation/size field, ~10k-40k) and would otherwise walk thousands of
                // records to the array end before failing — this rejects it in O(1). A
                // genuine deco boundary always satisfies the bound, so results are identical.
                if ((long)dn * 16 > _n - c.P) return null;
                WalkRec(c, _s.Sections - 2, dn);   // DECO
                int wn = c.I32();
                if (wn < 0 || wn > CountCap) return null;
                if ((long)wn * 8 > _n - c.P) return null;   // same bound on the water section
                WalkRec(c, _s.Sections - 1, wn);   // WATER
            }
            catch (StopException) { return null; }
            return c.P;
        }

        public bool Parse(int o, int li, int si)
        {
            if (li == _lc) return o == _n;
            DiagParse++;
            var key = (o, li, si);
            if (_memo.TryGetValue(key, out var cached)) return cached;

            int nsi = si + 1, nli = li;
            if (nsi == _s.Sections) { nsi = 0; nli = li + 1; }

            bool res = false;
            if (si != _s.Entity)
            {
                var c = new ByteCursor(_d, o);
                try
                {
                    int cnt = c.I32();
                    if (cnt >= 0 && cnt <= CountCap)
                    {
                        WalkRec(c, si, cnt);
                        res = Parse(c.P, nli, nsi);
                        if (_diag && !res)
                            Console.Error.WriteLine($"L{li} sec{si} @{o} cnt={cnt}: walked to {c.P}, "
                                + "rest of the file did not close");
                    }
                    else if (_diag)
                        Console.Error.WriteLine($"L{li} sec{si} @{o}: count {cnt} out of range");
                }
                catch (StopException)
                {
                    res = false;
                    // `cnt` died with the try block; the cursor position says how far the grammar got,
                    // which is the part that identifies the section that is not what it looks like.
                    if (_diag)
                        Console.Error.WriteLine($"L{li} sec{si} @{o}: record grammar overran the "
                            + $"file at {c.P} of {_n}");
                }
            }
            else
            {
                var c = new ByteCursor(_d, o);
                int cnt;
                try { cnt = c.I32(); } catch (StopException) { cnt = -1; }
                if (cnt == 0) res = Parse(c.P, nli, nsi);
                else if (cnt > 0 && cnt <= CountCap)
                {
                    int start = c.P;
                    bool canStrict = true;
                    (int FirstStart, int LastStart, int TrueFloor) anchor = default;
                    if (_strict)
                    {
                        var (aok, fs, ls, tf) = HeadAnchor(start, cnt);
                        canStrict = aok; anchor = (fs, ls, tf);
                        // The front head is the same for every candidate end, so a block whose first
                        // byte is not a head has no record-exact end at all — give up on the span
                        // instead of scanning the whole file rejecting each candidate.
                        if (canStrict && _front && anchor.FirstStart != start) canStrict = false;
                        if (_diag && !aok)
                        {
                            var (dh, dok) = EntityFraming.SplitBlock(_d, start, cnt, _n);
                            Console.Error.WriteLine($"entity@{start} cnt={cnt}: head scan to EOF "
                                + $"ok={dok} heads={dh.Count} (needs {cnt}) -> no strict framing here");
                        }
                        else if (_diag && canStrict && _front && anchor.FirstStart != start)
                            Console.Error.WriteLine($"entity@{start} cnt={cnt}: first head is @{anchor.FirstStart} "
                                + $"(+{anchor.FirstStart - start}), not the block start -> front pass declines");
                        else if (_diag && canStrict)
                            Console.Error.WriteLine($"entity@{start} cnt={cnt}: anchored ok, lastHead={anchor.LastStart} "
                                + $"floor={anchor.TrueFloor} measured={MeasuredEnd(start, cnt)}");
                    }
                    if (canStrict)
                    {
                        // Prefer the end the LOADING CODE itself arrives at: `count` entities read back to back
                        // off one cursor, each record self-delimiting (EntityFraming.MeasureBlock). The scan
                        // below is a credibility search, and a search can be led astray by bytes that merely
                        // look like a head — which is what happened after a length-changing tail edit, when the
                        // block it had to re-find was no longer the one the edit produced. Where the grammar
                        // explains every record, that measured end is tried first; the scan still covers every
                        // level whose records carry bytes the decompile does not name.
                        int me = MeasuredEnd(start, cnt);
                        if (me > 0 && me > anchor.LastStart && me < _n && TryDecoWaterAt(me) is not null)
                        {
                            Spans.Add((start, me, cnt));
                            if (Parse(me, nli, nsi)) res = true;
                            else Spans.RemoveAt(Spans.Count - 1);
                        }
                        for (int e = start; !res && e < _n; e++)
                        {
                            DiagScanIter++;
                            if (_strict)
                            {
                                if (e <= anchor.LastStart) continue;
                                if (e < anchor.TrueFloor && !BlockSelfValid(start, cnt, e)) continue;
                            }
                            if (TryDecoWaterAt(e) is null) continue;
                            Spans.Add((start, e, cnt));
                            if (Parse(e, nli, nsi)) { res = true; break; }
                            Spans.RemoveAt(Spans.Count - 1);
                        }
                    }
                    else if (_strict)
                    {
                        // The search found no anchor at all. That is not the same as the block having no
                        // record-exact end: the search's credibility rule needs a name that looks like a
                        // prefab, and Forever ships plain words (`h2o`, `bedrock`, `airplanepath1`), so the
                        // scan can walk a whole entity block without ever firing. The loading rules need no
                        // scan — they start at a known offset and read `count` records back to back — so
                        // where they arrive is tried here, and it has to be believed by the rest of the file:
                        // the deco/water section must parse at that end and the walk must close on it.
                        // Only reached when the search failed, so no level that already framed
                        // record-exactly gets a different answer.
                        int me = MeasuredEnd(start, cnt);
                        if (_diag)
                            Console.Error.WriteLine($"entity@{start} cnt={cnt}: unanchored, measured end "
                                + $"{(me > 0 ? me.ToString() : "unknown")} of {_n}"
                                + $"  deco/water@{me}={(me > 0 && TryDecoWaterAt(me) is not null)}");
                        if (me > start && me < _n && TryDecoWaterAt(me) is not null)
                        {
                            Spans.Add((start, me, cnt));
                            if (Parse(me, nli, nsi)) res = true;
                            else Spans.RemoveAt(Spans.Count - 1);
                        }
                    }
                }
            }
            _memo[key] = res;
            if (_diag && !res && si == _s.Entity && _strict)
                Console.Error.WriteLine($"entity@{o}: strict walk found no end that closes the rest of the file");
            return res;
        }
    }

    public (bool Ok, List<(int Start, int End, int Cnt)> Spans) FullWalk(byte[] d, bool strict = false,
                                                                        bool front = false)
    {
        int h0, lc;
        try
        {
            var c = new ByteCursor(d);
            c.I32();
            int tc = c.I32();
            if (tc < 0 || tc > 100_000) throw new StopException("bad tile count");
            for (int i = 0; i < tc; i++) c.String();
            lc = c.I32();
            if (lc < 0 || lc > 1000) throw new StopException("bad layer count");
            h0 = c.P;
        }
        catch (StopException) { return (false, new()); }

        var walk = new Walk(_s, d, lc, h0, strict, front);
        bool ok = walk.Parse(h0, 0, 0);
        LastDiag = (walk.DiagScanIter, walk.DiagTryDeco, walk.DiagSplit, walk.DiagParse);
        return (ok, walk.Spans);
    }

    /// <summary>Diagnostics from the most recent <see cref="FullWalk"/>: scan iterations,
    /// deco/water attempts, SplitBlock calls, Parse calls. Zero-cost counters kept for
    /// future performance work on the tiling oracle.</summary>
    public (long Scan, long TryDeco, long Split, long Parse) LastDiag { get; private set; }

    // ------------------------------------------------------------- decode --

    public ElementLevel Decode(byte[] d)
    {
        List<(int Start, int End, int Cnt)>? spans = null;
        // Three passes, best framing first: record-exact at both ends, then the count-exact strict
        // pass, then the lenient first-valid pick. A level the strict-front pass cannot tile (a block
        // whose first byte genuinely is not a head) keeps the framing it had instead of losing it.
        //
        // Which pass won is recorded on the model, because the three answers are not equally
        // trustworthy and I1 cannot tell them apart: every one of them re-emits the file byte-for-
        // byte, since the opaque block absorbs whatever the walk sliced. The lenient pass in
        // particular takes the first tiling that closes, which can land a section header in the
        // middle of a record. `FramingAuthority` is what the corpus gate reads to say whether a
        // level was actually framed or merely closed.
        var authority = FramingAuthority.RecordExactBothEnds;
        // Why a better pass lost is the whole story of a mis-framed level, and it is otherwise
        // invisible: a lenient fallback still round-trips byte-for-byte, so the only symptom is a
        // viewport full of nonsense. TPE_DECODE_DIAG=1 puts the pass-by-pass reason on stderr.
        bool diag = Environment.GetEnvironmentVariable("TPE_DECODE_DIAG") is not null;
        foreach (var (strict, front) in new[] { (true, true), (true, false) })
            try
            {
                var (ok, sp) = FullWalk(d, strict, front);
                if (ok) { spans = sp; authority = front ? FramingAuthority.RecordExactBothEnds
                                                         : FramingAuthority.RecordExactCount; break; }
            }
            catch (Exception ex)
            {
                if (diag)
                    Console.Error.WriteLine($"walk(strict:{strict},front:{front}) THREW "
                                            + $"{ex.GetType().Name}: {ex.Message}");
            }

        if (spans is null)
        {
            if (diag)
                Console.Error.WriteLine($"both strict passes declined; taking the lenient tiling "
                                        + $"of {d.Length} bytes");
            var (ok, lenient) = FullWalk(d, false);
            if (!ok) throw new InvalidOperationException("level does not tile to EOF");
            spans = lenient;
            authority = FramingAuthority.Lenient;
        }

        var entEnds = new Dictionary<int, int>();
        foreach (var (start, end, _) in spans) entEnds[start] = end;

        var lv = new ElementLevel { Game = _s.Game, SectionsPerLayer = _s.Sections,
                                    FramingAuthority = authority };
        var c = new ByteCursor(d);
        lv.Dummy = c.I32();
        int tc = c.I32();
        for (int i = 0; i < tc; i++)
        {
            int n = c.I32();
            var raw = d.AsSpan(c.P, n).ToArray();
            lv.TilesRaw.Add((n, raw));
            // Decode the tile's sprite-cell name (read_string: strip trailing NULs).
            int ne = raw.Length;
            while (ne > 0 && raw[ne - 1] == 0) ne--;
            lv.TileTypes.Add(System.Text.Encoding.Latin1.GetString(raw, 0, ne));
            c.P += n;
        }
        lv.LayerCount = c.I32();
        if (lv.LayerCount < 0) throw new InvalidOperationException("bad layer count");

        for (int li = 0; li < lv.LayerCount; li++)
        {
            var layer = new List<Section>(_s.Sections);
            for (int si = 0; si < _s.Sections; si++)
            {
                int countOff = c.P;
                int count = c.I32();
                if (si == _s.Entity)
                {
                    if (count == 0) { layer.Add(Section.OpaqueBlock(0, countOff, c.P, Array.Empty<byte>())); continue; }
                    int start = c.P;
                    if (!entEnds.TryGetValue(start, out int end))
                        throw new InvalidOperationException($"no entity block end @{start}");
                    var block = d.AsSpan(start, end - start).ToArray();
                    layer.Add(Section.OpaqueBlock(count, countOff, end, block));
                    c.P = end;
                }
                else
                {
                    var recs = new List<byte[]>(count);
                    for (int k = 0; k < count; k++)
                    {
                        int before = c.P;
                        _s.Recorders[si](c);
                        recs.Add(d.AsSpan(before, c.P - before).ToArray());
                    }
                    layer.Add(Section.Framed(count, countOff, c.P, recs));
                }
            }
            lv.Layers.Add(layer);
        }

        if (c.P != d.Length)
            throw new InvalidOperationException($"walk stopped at {c.P}, file is {d.Length}");
        lv.Trailing = Array.Empty<byte>();
        return lv;
    }

    // ------------------------------------------------------------- encode --

    public byte[] Encode(ElementLevel lv)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Wi(int v) => w.Write(unchecked((uint)v));

        Wi(lv.Dummy);
        Wi(lv.TilesRaw.Count);
        foreach (var (len, raw) in lv.TilesRaw) { Wi(len); w.Write(raw); }
        Wi(lv.LayerCount);
        foreach (var layer in lv.Layers)
            foreach (var sec in layer)
            {
                Wi(sec.Count);
                if (sec.Opaque) w.Write(sec.Block);
                else foreach (var r in sec.Records) w.Write(r);
            }
        w.Write(lv.Trailing);
        w.Flush();
        return ms.ToArray();
    }

    // --------------------------------------------------------- span model --

    public JsonObject SpanModel(ElementLevel lv)
    {
        var layers = new JsonArray();
        foreach (var layer in lv.Layers)
        {
            var secs = new JsonArray();
            for (int si = 0; si < layer.Count; si++)
            {
                var sec = layer[si];
                var o = new JsonObject { ["sec"] = si, ["count"] = sec.Count };
                if (sec.Opaque) o["opaque_len"] = sec.Block.Length;
                else
                {
                    var rl = new JsonArray();
                    foreach (var r in sec.Records) rl.Add(r.Length);
                    o["rec_lengths"] = rl;
                }
                secs.Add(o);
            }
            layers.Add(secs);
        }
        return new JsonObject
        {
            ["kind"] = "spans",
            ["dummy"] = lv.Dummy,
            ["tile_count"] = lv.TilesRaw.Count,
            ["layer_count"] = lv.LayerCount,
            ["layers"] = layers,
        };
    }

    // ------------------------------------------------------- edit proof I3 --

    public JsonObject? EditProof(ElementLevel lv, byte[] orig)
    {
        for (int li = 0; li < lv.Layers.Count; li++)
            for (int si = 0; si < lv.Layers[li].Count; si++)
            {
                var sec = lv.Layers[li][si];
                if (sec.Opaque || sec.Records.Count == 0 || sec.Records[0].Length < 8) continue;

                var original = sec.Records[0];
                var mutated = (byte[])original.Clone();
                mutated[mutated.Length / 2] ^= 0xFF;
                sec.Records[0] = mutated;

                var outBytes = Encode(lv);
                int start = sec.CountOff + 4;
                int end = start + original.Length;
                return DiffReport(orig, outBytes, start, end, $"{_s.Game} L{li} S{si} R0 byte-flip");
            }
        return null;
    }

    private static JsonObject DiffReport(byte[] orig, byte[] outB, int spanStart, int spanEnd, string label)
    {
        bool sameLen = orig.Length == outB.Length;
        int n = Math.Min(orig.Length, outB.Length);
        int diffs = 0, outside = 0;
        for (int i = 0; i < n; i++)
            if (orig[i] != outB[i]) { diffs++; if (!(spanStart <= i && i < spanEnd)) outside++; }
        return new JsonObject
        {
            ["edit"] = label,
            ["span"] = new JsonArray { spanStart, spanEnd },
            ["size_preserved"] = sameLen,
            ["n_bytes_changed"] = diffs,
            ["bytes_outside_span_changed"] = outside,
            ["localised"] = sameLen && diffs > 0 && outside == 0,
        };
    }
}
