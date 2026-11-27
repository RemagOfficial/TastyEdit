using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using TastyEditor.Core.Streams;

namespace TastyEditor.Core.Levels;

/// <summary>Lossless structured codec for Tasty Blue .bin levels — the C# port of
/// tb_read_level.read_level (decode) + tb_encode.encode_level (encode). Structural
/// records (wall / defined-area / path / water) are re-derived field-by-field, so a
/// GUI edit repacks automatically; entity records are re-emitted from their raw span
/// after the chained-delta position chain is rewritten from each entity's absolute
/// world position (the exact inverse of the read-side accumulation, so an untouched
/// level round-trips byte-for-byte); decorations and tiles are verbatim raw.
/// Proven against the Phase-C golden corpus (I1 + span model + I3).</summary>
public sealed class TbCodec
{
    public static readonly TbCodec Instance = new();

    // =========================================================== body walk ==
    // Port of tb_body._walk_body / measure_record: the tagged entity body whose
    // walk end == record length. Any short read aborts (null = not structured).

    /// <summary>Return the true byte length of one entity record (== walk end), or
    /// -1 when the record is not a clean structured body (tb_body.measure_record).</summary>
    public static int MeasureRecord(byte[] rec)
    {
        if (rec.Length < 8) return -1;
        int typeLen = TbReader.I32At(rec, 0);
        int body = 4 + typeLen;
        if (body <= 0 || body >= rec.Length) return -1;
        try
        {
            var c = new TbReader(rec, body);
            byte[] inst = c.RawBytes(c.I32());      // instance name (len-prefixed)
            c.I32(); c.I32();                        // x, y
            c.I32(); c.I32();                        // field_158, field_15c
            c.I32(); c.I32();                        // vec[2]
            c.I32(); c.I32();                        // field_250, rotation
            if (c.U8() != 0) for (int i = 0; i < 5; i++) c.I32();  // box
            c.RawBytes(4);                           // color rgba
            c.F64();                                 // mass
            c.I32();                                 // prio_delta
            if (c.I32() == 1)                        // move
            {
                c.F64(); c.F64(); c.I32();
                for (int i = 0; i < 6; i++) c.F64();
            }
            if (c.I32() == 1)                        // path
            {
                c.F64(); c.F64(); c.I32(); c.F64(); c.F64();
                c.RawBytes(c.I32());                 // name
                c.I32(); c.I32(); c.F64();
            }
            if (c.I32() == 1)                        // area
            {
                c.F64(); c.F64(); c.I32(); c.F64(); c.F64();
                c.RawBytes(c.I32());                 // name
            }
            if (c.I32() == 1)                        // emitter
            {
                for (int i = 0; i < 11; i++) c.F64();
                c.I32(); c.I32();
            }
            // instance-name credibility guard (rejects swallowed wall/path tables)
            int il = inst.Length;
            while (il > 0 && inst[il - 1] == 0) il--;
            if (il > 40) return -1;
            for (int i = 0; i < il; i++) if (inst[i] < 32 || inst[i] >= 127) return -1;
            return c.P;
        }
        catch (StopException) { return -1; }
    }

    /// <summary>tb_body.position_offsets: absolute record offsets of the x/y int32s,
    /// accounting for the length-prefixed instance name. (-1,-1) when not credible.</summary>
    private static (int X, int Y) PositionOffsets(byte[] rec, int body)
    {
        if (body + 4 > rec.Length) return (-1, -1);
        int n = TbReader.I32At(rec, body);
        if (n < 1 || n > 64) return (-1, -1);
        int x = body + 4 + n;
        if (x + 8 > rec.Length) return (-1, -1);
        return (x, x + 4);
    }

    /// <summary>The prefab/spawn name carried in an entity record head: the
    /// length-prefixed NUL string at rec[4 .. 4+nameLen]. Trailing NULs are trimmed.
    /// Returns null when the bytes aren't a credible name.</summary>
    private static string? ReadRecordName(byte[] rec, int nameLen)
    {
        if (nameLen < 1 || 4 + nameLen > rec.Length) return null;
        int n = nameLen;
        while (n > 0 && rec[4 + n - 1] == 0) n--;
        if (n <= 0) return null;
        return System.Text.Encoding.ASCII.GetString(rec, 4, n);
    }

    // ============================================================= decode ==

    public TbLevel Decode(byte[] data)
    {
        var r = new TbReader(data);
        var lv = new TbLevel();
        lv.Dummy = r.I32();
        int tc = r.I32();
        for (int i = 0; i < tc; i++)
        {
            int off = r.P;
            int len = r.I32();
            byte[] payload = r.RawBytes(len);
            lv.TilesRaw.Add(data.AsSpan(off, 4 + len).ToArray());
            lv.TileTypes.Add(DecodeNulString(payload));   // tileTypes[i]['value'] (sprite cell name)
        }
        lv.LayerCount = r.I32();

        for (int li = 0; li < lv.LayerCount; li++)
        {
            var layer = new TbLayer();
            int wc = r.I32();
            for (int i = 0; i < wc; i++) { int o = r.P; var rec = ReadWall(r); rec.Raw = data.AsSpan(o, r.P - o).ToArray(); rec.Off = o; layer.Walls.Add(rec); }
            int ac = r.I32();
            for (int i = 0; i < ac; i++) { int o = r.P; var rec = ReadArea(r); rec.Raw = data.AsSpan(o, r.P - o).ToArray(); rec.Off = o; layer.Areas.Add(rec); }
            int pc = r.I32();
            for (int i = 0; i < pc; i++) { int o = r.P; var rec = ReadPath(r); rec.Raw = data.AsSpan(o, r.P - o).ToArray(); rec.Off = o; layer.Paths.Add(rec); }
            ReadEntities(r, data, layer.Entities);
            int dc = r.I32();
            double dlx = 0, dly = 0;
            int dlp = 0;                 // finalize_decos: lp += _prio_delta, absolute per layer
            string? lastDecoName = null;   // run-length: a type-0 deco inherits the previous tile
            for (int i = 0; i < dc; i++)
            {
                int o = r.P;
                var (ddx, ddy, dtype, dinline, dcell, drot, dw, dh, dcolor, dflipX, dflipY, dprio) = ReadDeco(r);
                dlx += ddx; dly += ddy;   // finalize_decos: chained centiunit deltas
                dlp += dprio;             // ...and the same chaining for the draw priority
                // Resolve the sprite name exactly like tbeditor.resolve_deco_names:
                //   type 2 = inline string, type 1 = tileTypes[cell], type 0 = inherit previous.
                string? dname = dtype switch
                {
                    0x02 => dinline,
                    0x01 => (dcell >= 0 && dcell < lv.TileTypes.Count) ? lv.TileTypes[dcell] : null,
                    _ => lastDecoName,
                };
                if (dname is not null) lastDecoName = dname;
                layer.Decos.Add(new TbDeco
                {
                    Raw = data.AsSpan(o, r.P - o).ToArray(),
                    Name = dname,
                    Rotation = drot,
                    DimW = dw,
                    DimH = dh,
                    ColorRgba = dcolor,
                    PosX = dlx * 0.01,
                    PosY = dly * 0.01,
                    Priority = dlp,
                    FlipX = dflipX,
                    FlipY = dflipY,
                    Owner = layer,
                });
            }
            int qx = r.I32();
            for (int i = 0; i < qx; i++) { int o = r.P; var rec = ReadWater(r); rec.Raw = data.AsSpan(o, r.P - o).ToArray(); rec.Off = o; layer.Waters.Add(rec); }
            lv.Layers.Add(layer);
        }

        lv.Tail = r.P < data.Length ? data.AsSpan(r.P).ToArray() : Array.Empty<byte>();
        return lv;
    }

    private static List<TbShape> ReadShapes(TbReader r)
    {
        var shapes = new List<TbShape>();
        int n = r.I32();
        for (int i = 0; i < n; i++)
        {
            string? name = r.Str();
            var sh = new TbShape { Name = name ?? "" };
            if (ElementShapes.IsCircleName(sh.Name))
            {
                sh.IsCircle = true;
                sh.Cx = r.F64(); sh.Cy = r.F64(); sh.Radius = r.F64();
            }
            else
            {
                int vc = r.I32();
                sh.Vertices = new double[vc][];
                for (int v = 0; v < vc; v++) sh.Vertices[v] = new[] { r.F64(), r.F64() };
            }
            shapes.Add(sh);
        }
        return shapes;
    }

    private static TbWall ReadWall(TbReader r)
    {
        var w = new TbWall
        {
            PosX = r.F64(), PosY = r.F64(), Width = r.F64(), Length = r.F64(),
            WallTypeName = r.Str(),
            HasShapesFlag = r.U8(),
        };
        if (w.HasShapesFlag == 0) { w.Shapes = ReadShapes(r); w.ShapesTailFlag = r.I32(); }
        w.WallId = r.I32();
        return w;
    }

    private static TbDefinedArea ReadArea(TbReader r)
    {
        var a = new TbDefinedArea
        {
            AreaName = r.Str(),
            PosX = r.F64(), PosY = r.F64(), Width = r.F64(), Length = r.F64(),
            HasShapesFlag = r.U8(),
        };
        if (a.HasShapesFlag == 0) { a.Shapes = ReadShapes(r); a.ShapesTailFlag = r.I32(); }
        a.AreaId = r.I32();
        return a;
    }

    private static TbPath ReadPath(TbReader r)
    {
        var p = new TbPath
        {
            PathName = r.Str(),
            PosX = r.F64(), PosY = r.F64(),
            ExtentXGuess = r.F64(), ExtentYGuess = r.F64(),
            PathFlag = r.U8(),
        };
        if (p.PathFlag == 1)
        {
            int n = r.I32();
            p.SplinePoints = new double[n][];
            for (int i = 0; i < n; i++)
            {
                var v = new double[6];
                for (int k = 0; k < 6; k++) v[k] = r.F64();
                p.SplinePoints[i] = v;
            }
        }
        p.InternalIdGuess = r.I32();
        return p;
    }

    private static (int Dx, int Dy, int Type, string? InlineName, int Cell, int Rot, int DimW, int DimH, byte[] Color,
                    bool FlipX, bool FlipY, int PrioDelta) ReadDeco(TbReader r)
    {
        int type = r.U8();
        string? inlineName = null; int cell = -1;
        if (type == 0x02) inlineName = r.Str();
        else if (type == 0x01) cell = r.I32();   // index into tileTypes (resolved by the caller)
        int dx = r.I32();
        int dy = r.I32();                 // _dx, _dy
        int rot = r.I32();                // size.raw = centidegree rotation
        int extraFlag = r.U8();
        bool flipX = false, flipY = false;
        if (extraFlag != 0)
        {
            // ElementDecoration::readBinary walks the same 24 bytes the Python reader consumes as
            // "2 bools + 4 ints + int + short", but with the engine's boundaries: DGUI::BinaryFile::
            // readBoolean consumes a WHOLE int32 (it reads an int and advances the cursor by one int,
            // returning `value == 1`) and stores it into this+0x40 / this+0x41, then four readInt.
            // Those two words are setFlipX / setFlipY, so the flags are block+0 and block+4 - the
            // Python 2-byte slice was a layout guess, not the game's read.
            flipX = r.I32() == 1;
            flipY = r.I32() == 1;
            r.I32(); r.I32(); r.I32(); r.I32();   // extra_ints (0x44/0x48/0x4c/0x50)
        }
        byte[] color = r.RawBytes(4);     // color_rgba (display + editable tint)
        int dimW = r.I32();
        int dimH = r.I32();               // dimensions.raw
        int prio = r.I32();               // _prio_delta (chained by the caller)
        return (dx, dy, type, inlineName, cell, rot, dimW, dimH, color, flipX, flipY, prio);
    }

    /// <summary>latin-1 decode of a length-prefixed string payload with trailing NULs
    /// stripped (tb_read_level.read_string), for the tileTypes sprite names.</summary>
    private static string DecodeNulString(byte[] raw)
    {
        int end = raw.Length;
        while (end > 0 && raw[end - 1] == 0) end--;
        var sb = new StringBuilder(end);
        for (int i = 0; i < end; i++) sb.Append((char)raw[i]);
        return sb.ToString();
    }

    private static TbWater ReadWater(TbReader r)
    {
        var w = new TbWater { Name = r.Str() };
        int[] I = w.I; double[] D = w.D;
        I[0] = r.I32(); I[1] = r.I32();       // toggle_a, toggle_b
        D[0] = r.F64(); D[1] = r.F64();       // scalar0, scalar1
        I[2] = r.I32();                       // toggle_c
        D[2] = r.F64();                       // scalar2
        I[3] = r.I32(); I[4] = r.I32(); I[5] = r.I32();  // toggle_d/e/f
        D[3] = r.F64(); D[4] = r.F64();       // scalar3, scalar4
        I[6] = r.I32(); I[7] = r.I32();       // toggle_g, toggle_h
        I[8] = r.I32();                       // int0
        I[9] = r.I32(); I[10] = r.I32(); I[11] = r.I32(); // flag_bit0/1/2
        D[5] = r.F64(); D[6] = r.F64();       // center_x, center_y
        D[7] = r.F64(); D[8] = r.F64();       // size_w, size_h
        D[9] = r.F64(); D[10] = r.F64();      // min_x, min_y
        D[11] = r.F64(); D[12] = r.F64();     // max_x, max_y
        w.WaterId = r.I32();
        return w;
    }

    private static void ReadEntities(TbReader r, byte[] data, List<TbEntity> outList)
    {
        int count = r.I32();
        double ax = 0, ay = 0;
        for (int i = 0; i < count; i++)
        {
            int off = r.P;
            int span = MeasureRecord(data.AsSpan(off).ToArray());
            if (span < 0) throw new InvalidOperationException($"undecodable entity record @{off}");
            byte[] raw = data.AsSpan(off, span).ToArray();
            var ent = new TbEntity { Raw = raw };
            int nameLen = TbReader.I32At(raw, 0);
            int body = 4 + nameLen;
            var (xo, yo) = PositionOffsets(raw, body);
            if (raw.Length >= 16 && xo >= 0)
            {
                ax += TbReader.I32At(raw, xo);
                ay += TbReader.I32At(raw, yo);
                ent.PosX = ax; ent.PosY = ay;
            }
            // Read-only display reads (sprite name + body fields); Raw stays the
            // lossless carrier, so the byte-faithful save path is untouched.
            ent.Name = ReadRecordName(raw, nameLen);
            var disp = EntityFraming.ReadBody(raw, xo, EntityFraming.ExtTb, raw.Length);
            ent.Flip = disp.Flip;
            if (disp.HasBody)
            {
                ent.Rotation = disp.Rotation;
                ent.Mass = disp.Mass;
                ent.Priority = disp.Priority;
                ent.ColorRgba = disp.ColorRgba;
            }
            r.P = off + span;
            outList.Add(ent);
        }
    }

    // ============================================================= encode ==

    public byte[] Encode(TbLevel lv)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Wi(int v) => w.Write(unchecked((uint)v));
        void Wd(double v) => w.Write(v);
        void Ws(string? s)
        {
            if (s is null) { Wi(0); return; }
            var b = Latin1(s);
            Wi(b.Length + 1);
            w.Write(b);
            w.Write((byte)0);
        }
        void WriteShapes(List<TbShape> shapes)
        {
            Wi(shapes.Count);
            foreach (var sh in shapes)
            {
                Ws(sh.Name);
                if (sh.IsCircle) { Wd(sh.Cx); Wd(sh.Cy); Wd(sh.Radius); }
                else
                {
                    Wi(sh.Vertices.Length);
                    foreach (var v in sh.Vertices) { Wd(v[0]); Wd(v[1]); }
                }
            }
        }

        Wi(lv.Dummy);
        Wi(lv.TilesRaw.Count);
        foreach (var t in lv.TilesRaw) w.Write(t);
        Wi(lv.LayerCount);

        foreach (var layer in lv.Layers)
        {
            Wi(layer.Walls.Count);
            foreach (var v in layer.Walls)
            {
                Wd(v.PosX); Wd(v.PosY); Wd(v.Width); Wd(v.Length);
                Ws(v.WallTypeName);
                w.Write((byte)(v.HasShapesFlag & 0xFF));
                if (v.HasShapesFlag == 0) { WriteShapes(v.Shapes); Wi(v.ShapesTailFlag); }
                Wi(v.WallId);
            }

            Wi(layer.Areas.Count);
            foreach (var a in layer.Areas)
            {
                Ws(a.AreaName);
                Wd(a.PosX); Wd(a.PosY); Wd(a.Width); Wd(a.Length);
                w.Write((byte)(a.HasShapesFlag & 0xFF));
                if (a.HasShapesFlag == 0) { WriteShapes(a.Shapes); Wi(a.ShapesTailFlag); }
                Wi(a.AreaId);
            }

            Wi(layer.Paths.Count);
            foreach (var p in layer.Paths)
            {
                Ws(p.PathName);
                Wd(p.PosX); Wd(p.PosY); Wd(p.ExtentXGuess); Wd(p.ExtentYGuess);
                w.Write((byte)(p.PathFlag & 0xFF));
                if (p.PathFlag == 1)
                {
                    Wi(p.SplinePoints.Length);
                    foreach (var pt in p.SplinePoints)
                        for (int i = 0; i < 6; i++) Wd(pt[i]);
                }
                Wi(p.InternalIdGuess);
            }

            PatchEntityPositions(layer.Entities);
            Wi(layer.Entities.Count);
            foreach (var ent in layer.Entities) w.Write(ent.Raw);

            Wi(layer.Decos.Count);
            foreach (var d in layer.Decos) w.Write(d.Raw);

            Wi(layer.Waters.Count);
            foreach (var q in layer.Waters)
            {
                int[] I = q.I; double[] D = q.D;
                Ws(q.Name);
                Wi(I[0]); Wi(I[1]);
                Wd(D[0]); Wd(D[1]);
                Wi(I[2]);
                Wd(D[2]);
                Wi(I[3]); Wi(I[4]); Wi(I[5]);
                Wd(D[3]); Wd(D[4]);
                Wi(I[6]); Wi(I[7]);
                Wi(I[8]);
                Wi(I[9]); Wi(I[10]); Wi(I[11]);
                Wd(D[5]); Wd(D[6]);
                Wd(D[7]); Wd(D[8]);
                Wd(D[9]); Wd(D[10]);
                Wd(D[11]); Wd(D[12]);
                Wi(q.WaterId);
            }
        }

        w.Write(lv.Tail);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Latin1(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)(s[i] & 0xFF);
        return b;
    }

    /// <summary>tb_encode.patch_block_entity_positions: rewrite the chained deltas
    /// from each entity's absolute position, in place, so an untouched level writes
    /// back the exact bytes it read.</summary>
    private static void PatchEntityPositions(List<TbEntity> ents)
    {
        double ax = 0, ay = 0;
        foreach (var ent in ents)
        {
            var raw = ent.Raw;
            if (raw.Length < 16) continue;
            int body = 4 + TbReader.I32At(raw, 0);
            var (xo, yo) = PositionOffsets(raw, body);
            if (xo < 0) continue;
            long wx = (long)Math.Round(ent.PosX);
            long wy = (long)Math.Round(ent.PosY);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(xo, 4), unchecked((int)(wx - (long)ax)));
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(yo, 4), unchecked((int)(wy - (long)ay)));
            ax = wx; ay = wy;
        }
    }

    // --------------------------------------------------------- span model --

    public JsonObject SpanModel(TbLevel lv)
    {
        var layers = new JsonArray();
        foreach (var layer in lv.Layers)
        {
            var secs = new JsonArray
            {
                Sec(0, layer.Walls.Count, i => layer.Walls[i].Raw.Length),
                Sec(1, layer.Areas.Count, i => layer.Areas[i].Raw.Length),
                Sec(2, layer.Paths.Count, i => layer.Paths[i].Raw.Length),
                Sec(3, layer.Entities.Count, i => layer.Entities[i].Raw.Length),
                Sec(4, layer.Decos.Count, i => layer.Decos[i].Raw.Length),
                Sec(5, layer.Waters.Count, i => layer.Waters[i].Raw.Length),
            };
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

        static JsonObject Sec(int si, int count, Func<int, int> len)
        {
            var rl = new JsonArray();
            for (int i = 0; i < count; i++) rl.Add(len(i));
            return new JsonObject { ["sec"] = si, ["count"] = count, ["rec_lengths"] = rl };
        }
    }

    // ------------------------------------------------------- edit proof I3 --

    /// <summary>level_codec._edit_from for tb: bump the first wall (>=16 bytes) by
    /// pos_x+1 and confirm the re-encoded file differs only inside that wall's span.</summary>
    public JsonObject? EditProof(TbLevel lv, byte[] orig)
    {
        for (int li = 0; li < lv.Layers.Count; li++)
            for (int wi = 0; wi < lv.Layers[li].Walls.Count; wi++)
            {
                var wall = lv.Layers[li].Walls[wi];
                if (wall.Raw.Length < 16) continue;
                int off = wall.Off;
                int len = wall.Raw.Length;
                wall.PosX += 1.0;
                var outBytes = Encode(lv);
                return DiffReport(orig, outBytes, off, off + len,
                    $"tb L{li} wall#{wi} pos_x+1");
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
