using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace TastyEditor.Core.Levels;

/// <summary>Lossless codec for Tasty Planet 2 .bin levels — the C# port of
/// read_level.read_level (decode) + write_level.write_level (encode). Every field
/// is re-serialised from the parsed model; entity and decoration positions are
/// stored on disk as chained integer deltas in hundredths, so the writer replays
/// the reader's * 0.01 world conversion with round(* 100) to rebuild the deltas.
/// The span model is section counts only (kind "counts"); tp2 has no record-span
/// edit proof (it is covered by I1 + the model). Validated byte-exact (I1).</summary>
public sealed class Tp2Codec
{
    public static readonly Tp2Codec Instance = new();

    // ============================================================= decode ==

    public Tp2Level Decode(byte[] data)
    {
        var r = new Tp2Reader(data);
        var lv = new Tp2Level();
        lv.Dummy = r.I32();
        lv.TileTypeCount = r.I32();
        for (int i = 0; i < lv.TileTypeCount; i++) lv.TileTypes.Add(r.Str());
        lv.LayerCount = r.I32();

        for (int li = 0; li < lv.LayerCount; li++)
        {
            r.LayerIndex = li;
            var layer = new Tp2Layer();

            int wc = r.I32();
            for (int i = 0; i < wc; i++) layer.Walls.Add(ReadWall(r));

            int pc = r.I32();
            for (int i = 0; i < pc; i++) layer.Paths.Add(ReadPath(r));

            int ec = r.I32();
            int lx = 0, ly = 0, lp = 0;
            for (int i = 0; i < ec; i++) layer.Entities.Add(ReadEntity(r, ref lx, ref ly, ref lp));

            int dc = r.I32();
            lx = ly = lp = 0;
            for (int i = 0; i < dc; i++) layer.Decos.Add(ReadDeco(r, ref lx, ref ly, ref lp));

            lv.Layers.Add(layer);
        }
        return lv;
    }

    private static List<Tp2Shape> ReadShapes(Tp2Reader r)
    {
        var shapes = new List<Tp2Shape>();
        int n = r.I32();
        for (int i = 0; i < n; i++)
        {
            string? name = r.Str();
            var sh = new Tp2Shape { Name = name ?? "" };
            if (sh.Name == "Circle")
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

    private static Tp2Wall ReadWall(Tp2Reader r)
    {
        var w = new Tp2Wall
        {
            PosX = r.F64(), PosY = r.F64(), Width = r.F64(), Length = r.F64(),
            WallTypeName = r.Str(),
            HasShapesFlag = r.U8(),
        };
        if (w.HasShapesFlag == 0) w.Shapes = ReadShapes(r);
        w.Reserved = r.I32();
        w.WallId = r.I32();
        return w;
    }

    private static Tp2Path ReadPath(Tp2Reader r)
    {
        var p = new Tp2Path
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

    private static Tp2Entity ReadEntity(Tp2Reader r, ref int lx, ref int ly, ref int lp)
    {
        var e = new Tp2Entity { Type = r.Str() };
        int dx = r.I32(), dy = r.I32();
        lx += dx; ly += dy;
        e.Position = new[] { lx * 0.01, ly * 0.01 };
        e.Field158 = r.I32(); e.Field15c = r.I32();
        e.Vec0 = r.I32(); e.Vec1 = r.I32();
        e.Field250 = r.I32(); e.Rotation = r.I32();
        e.HasBox = r.U8();
        if (e.HasBox != 0)
        {
            e.BoxEnabled = r.I32();
            e.BoxBounds = new[] { r.I32(), r.I32(), r.I32(), r.I32() };
        }
        e.ColorRgba = r.RawBytes(4);
        e.Mass = r.F64();
        lp += r.I32();
        e.Priority = lp;
        e.HasMoveDirection = r.I32();
        if (e.HasMoveDirection == 1)
        {
            double v0 = r.F64(), v1 = r.F64();
            e.MoveFlag0 = r.I32();
            double[] rest = new double[6];
            for (int i = 0; i < 6; i++) rest[i] = r.F64();
            e.Move = new double[8];
            e.Move[0] = v0; e.Move[1] = v1;
            for (int i = 0; i < 6; i++) e.Move[2 + i] = rest[i];
        }
        e.HasPathFollow = r.I32();
        if (e.HasPathFollow == 1)
        {
            e.Pv0 = r.F64(); e.Pv1 = r.F64();
            e.PFlag0 = r.I32();
            e.Pv2 = r.F64(); e.Pv3 = r.F64();
            e.PathName = r.Str();
            e.PFlag1 = r.I32(); e.PMode = r.I32();
            e.Pv4 = r.F64();
        }
        e.HasEmitter = r.I32();
        if (e.HasEmitter == 1)
        {
            e.EmitV = new double[11];
            for (int i = 0; i < 11; i++) e.EmitV[i] = r.F64();
            if (r.LayerIndex >= 3)
            {
                e.EmitReserved = r.I32();
                e.EmitEndMarker = r.I32();
            }
        }
        return e;
    }

    private static Tp2Deco ReadDeco(Tp2Reader r, ref int lx, ref int ly, ref int lp)
    {
        var d = new Tp2Deco { Type = r.U8() };
        if (d.Type == 0x02) { d.HasString = true; d.String = r.Str(); }
        else if (d.Type == 0x01) { d.HasCell = true; d.Cell = r.I32(); }
        int dx = r.I32(), dy = r.I32();
        lx += dx; ly += dy;
        d.Position = new[] { lx * 0.01, ly * 0.01 };
        d.SizeRaw = r.I32();
        d.ExtraFlag = r.U8();
        if (d.ExtraFlag != 0)
        {
            // Engine word boundaries (ElementDecoration::readBinary): two readBoolean int32 then four
            // readInt, 24 bytes total - the same span the old 2-bool/4-int/int/short split consumed.
            d.FlipWordX = r.I32();
            d.FlipWordY = r.I32();
            d.FlipX = d.FlipWordX == 1;
            d.FlipY = d.FlipWordY == 1;
            d.ExtraInts = new[] { r.I32(), r.I32(), r.I32(), r.I32() };
        }
        d.ColorRgba = r.RawBytes(4);
        d.DimsRaw = new[] { r.I32(), r.I32() };
        lp += r.I32();
        d.PriorityTotal = lp;
        return d;
    }

    // ============================================================= encode ==

    public byte[] Encode(Tp2Level lv)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Wi(int v) => w.Write(unchecked((uint)v));
        void Wd(double v) => w.Write(v);
        void Ws(string? s)
        {
            if (s is null) { Wi(0); return; }
            var b = Encoding.ASCII.GetBytes(s);
            Wi(b.Length + 1);
            w.Write(b);
            w.Write((byte)0);
        }
        void WB(byte v) => w.Write(v);
        void WriteShapes(List<Tp2Shape> shapes)
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
        Wi(lv.TileTypeCount);
        foreach (var t in lv.TileTypes) Ws(t);
        Wi(lv.LayerCount);

        for (int li = 0; li < lv.Layers.Count; li++)
        {
            var layer = lv.Layers[li];

            Wi(layer.Walls.Count);
            foreach (var v in layer.Walls)
            {
                Wd(v.PosX); Wd(v.PosY); Wd(v.Width); Wd(v.Length);
                Ws(v.WallTypeName);
                WB((byte)(v.HasShapesFlag & 0xFF));
                if (v.HasShapesFlag == 0) WriteShapes(v.Shapes);
                Wi(v.Reserved);
                Wi(v.WallId);
            }

            Wi(layer.Paths.Count);
            foreach (var p in layer.Paths)
            {
                Ws(p.PathName);
                Wd(p.PosX); Wd(p.PosY); Wd(p.ExtentXGuess); Wd(p.ExtentYGuess);
                WB((byte)(p.PathFlag & 0xFF));
                if (p.PathFlag == 1)
                {
                    Wi(p.SplinePoints.Length);
                    foreach (var pt in p.SplinePoints)
                        for (int i = 0; i < 6; i++) Wd(pt[i]);
                }
                Wi(p.InternalIdGuess);
            }

            Wi(layer.Entities.Count);
            int lx = 0, ly = 0, lp = 0;
            foreach (var e in layer.Entities)
            {
                Ws(e.Type);
                int cx = (int)Round(e.Position[0] * 100);
                int cy = (int)Round(e.Position[1] * 100);
                Wi(cx - lx); Wi(cy - ly);
                lx = cx; ly = cy;
                Wi(e.Field158); Wi(e.Field15c);
                Wi(e.Vec0); Wi(e.Vec1);
                Wi(e.Field250); Wi(e.Rotation);
                WB((byte)(e.HasBox & 0xFF));
                if (e.HasBox != 0)
                {
                    Wi(e.BoxEnabled);
                    Wi(e.BoxBounds[0]); Wi(e.BoxBounds[1]); Wi(e.BoxBounds[2]); Wi(e.BoxBounds[3]);
                }
                for (int i = 0; i < 4; i++) WB(e.ColorRgba[i]);
                Wd(e.Mass);
                Wi(e.Priority - lp); lp = e.Priority;
                Wi(e.HasMoveDirection);
                if (e.HasMoveDirection == 1)
                {
                    Wd(e.Move[0]); Wd(e.Move[1]);
                    Wi(e.MoveFlag0);
                    for (int i = 2; i < 8; i++) Wd(e.Move[i]);
                }
                Wi(e.HasPathFollow);
                if (e.HasPathFollow == 1)
                {
                    Wd(e.Pv0); Wd(e.Pv1);
                    Wi(e.PFlag0);
                    Wd(e.Pv2); Wd(e.Pv3);
                    Ws(e.PathName);
                    Wi(e.PFlag1); Wi(e.PMode);
                    Wd(e.Pv4);
                }
                Wi(e.HasEmitter);
                if (e.HasEmitter == 1)
                {
                    for (int i = 0; i < 11; i++) Wd(e.EmitV[i]);
                    if (li >= 3) { Wi(e.EmitReserved); Wi(e.EmitEndMarker); }
                }
            }

            Wi(layer.Decos.Count);
            lx = ly = lp = 0;
            foreach (var d in layer.Decos)
            {
                WB((byte)(d.Type & 0xFF));
                if (d.Type == 0x02) Ws(d.String ?? "");
                else if (d.Type == 0x01) Wi(d.Cell);
                int cx = (int)Round(d.Position[0] * 100);
                int cy = (int)Round(d.Position[1] * 100);
                Wi(cx - lx); Wi(cy - ly);
                lx = cx; ly = cy;
                Wi(d.SizeRaw);
                WB((byte)(d.ExtraFlag & 0xFF));
                if (d.ExtraFlag != 0)
                {
                    Wi(d.FlipWordX); Wi(d.FlipWordY);
                    for (int i = 0; i < 4; i++) Wi(d.ExtraInts[i]);
                }
                for (int i = 0; i < 4; i++) WB(d.ColorRgba[i]);
                Wi(d.DimsRaw[0]); Wi(d.DimsRaw[1]);
                Wi(d.PriorityTotal - lp); lp = d.PriorityTotal;
            }
        }

        w.Flush();
        return ms.ToArray();
    }

    // Python round(): round-half-to-even on the true binary double.
    private static double Round(double v) => Math.Round(v, MidpointRounding.ToEven);

    // --------------------------------------------------------- span model --

    public JsonObject SpanModel(Tp2Level lv)
    {
        var layers = new JsonArray();
        foreach (var layer in lv.Layers)
        {
            var secs = new JsonArray
            {
                Sec(0, layer.Walls.Count), Sec(1, layer.Paths.Count),
                Sec(2, layer.Entities.Count), Sec(3, layer.Decos.Count),
            };
            layers.Add(secs);
        }
        return new JsonObject
        {
            ["kind"] = "counts",
            ["dummy"] = lv.Dummy,
            ["tile_count"] = lv.TileTypeCount,
            ["layer_count"] = lv.LayerCount,
            ["layers"] = layers,
        };

        static JsonObject Sec(int si, int count) =>
            new() { ["sec"] = si, ["count"] = count };
    }
}

/// <summary>Sequential reader over a TP2 .bin (read_level.BinaryReader semantics).
/// read_string decodes ascii and strips trailing NULs; length 0 => null.</summary>
internal sealed class Tp2Reader
{
    private readonly byte[] _d;
    public int P;
    public int LayerIndex;

    public Tp2Reader(byte[] d, int p = 0) { _d = d; P = p; }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || P + n > _d.Length) throw new InvalidOperationException($"tp2 eof read {n} @{P}");
        var s = _d.AsSpan(P, n);
        P += n;
        return s;
    }

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public int U8() => Take(1)[0];
    public int Short() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public byte[] RawBytes(int n) => Take(n).ToArray();

    public string? Str()
    {
        int len = I32();
        if (len == 0) return null;
        var raw = Take(len);
        int end = raw.Length;
        while (end > 0 && raw[end - 1] == 0) end--;
        var sb = new StringBuilder(end);
        for (int i = 0; i < end; i++) sb.Append((char)raw[i]);
        return sb.ToString();
    }
}
