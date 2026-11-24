using System.Buffers.Binary;
using System.Text;

namespace TastyEditor.Core.Streams;

/// <summary>Raised to abort a walk when a read runs off the end or a length is
/// absurd — the C# mirror of tb_f5_read.Stop. Caught by the tiling oracle.</summary>
public sealed class StopException : Exception
{
    public StopException(string message) : base(message) { }
}

/// <summary>A typed cursor over a whole-file buffer — the exact stream-primitive
/// semantics the ElementEngine BinaryFile uses (readInt/readFloat/readBoolean are
/// 4 bytes, readDouble 8, readUnsignedChar 1, readString = int32 len + len bytes
/// with the NUL inside the length). Ported 1:1 from tb_f5_read.Cur so walk
/// outcomes match the Python reference byte-for-byte.</summary>
public sealed class ByteCursor
{
    private readonly byte[] _d;
    public int P;

    public ByteCursor(byte[] d, int p = 0)
    {
        _d = d;
        P = p;
    }

    public int Length => _d.Length;
    public byte[] Buffer => _d;
    public bool Eof => P >= _d.Length;

    private ReadOnlySpan<byte> Take(int n)
    {
        // long arithmetic: a corrupt/speculative length can push P past int.MaxValue;
        // the oracle must see StopException (a probe miss), not an int-overflow crash
        // in AsSpan — matching Python's arbitrary-precision struct read that just EOFs.
        if (n < 0 || (long)P + n > _d.Length)
            throw new StopException($"eof read {n} @{P}");
        var s = _d.AsSpan(P, n);
        P += n;
        return s;
    }

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public int U8() => Take(1)[0];
    public int Bool4() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    /// <summary>int32 len + len bytes; the value is bytes up to the first NUL,
    /// decoded as Latin-1 (one char per byte), matching Python's
    /// <c>raw.split(b'\\x00',1)[0].decode('latin-1')</c>.</summary>
    public string String()
    {
        int n = I32();
        if (n < 0 || (long)P + n > _d.Length)
            throw new StopException($"bad strlen {n} @{P}");
        var raw = Take(n);
        int end = raw.IndexOf((byte)0);
        int len = end < 0 ? raw.Length : end;
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)raw[i]);
        return sb.ToString();
    }

    /// <summary>Peek an int32 at an absolute offset without moving the cursor
    /// (used by the entity head scanner). Throws StopException when OOB.</summary>
    public int PeekI32(int at)
    {
        if (at < 0 || at + 4 > _d.Length)
            throw new StopException($"peek oob @{at}");
        return BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(at, 4));
    }

    public static int ReadI32At(byte[] d, int at) =>
        BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at, 4));
}
