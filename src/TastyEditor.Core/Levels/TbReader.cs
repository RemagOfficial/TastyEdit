using System.Buffers.Binary;
using System.Text;
using TastyEditor.Core.Streams;

namespace TastyEditor.Core.Levels;

/// <summary>Bounds-checked sequential reader over a whole .bin, mirroring
/// read_level.BinaryReader / tb_read_level.TbReader primitives. A read past the
/// end throws <see cref="StopException"/> which the entity body-walker treats as
/// "not a structured record" (Python catches struct.error/IndexError/ValueError).</summary>
internal sealed class TbReader
{
    private readonly byte[] _d;
    public int P;

    public TbReader(byte[] d, int p = 0) { _d = d; P = p; }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || P + n > _d.Length) throw new StopException($"tb eof read {n} @{P}");
        var s = _d.AsSpan(P, n);
        P += n;
        return s;
    }

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public int U8() => Take(1)[0];
    public int Short() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public byte[] RawBytes(int n) => Take(n).ToArray();

    /// <summary>int32 len + len bytes (NUL inside the length). 0-length => null.
    /// Value decoded latin-1 with trailing NULs stripped (tb_read_level.read_string).</summary>
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

    // Absolute-offset helpers used by the entity measure/delta path.
    public static int I32At(byte[] d, int at) =>
        (at < 0 || at + 4 > d.Length) ? throw new StopException("i32 oob") :
        BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at, 4));
}
