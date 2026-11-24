namespace TastyEditor.Core.Levels;

/// <summary>Length-changing (<em>structural</em>) edits inside one entity record's behaviour tail: turning
/// a gated behaviour block on or off, renaming the engine name a block embeds, and resizing the
/// IndexedCellColours ramp. These are the only tail fields whose serialized width depends on their value,
/// which is why they cannot go through the in-place lane writer in
/// <see cref="EntityFraming"/> — they rebuild the tail instead.
///
/// <para><b>How a gate becomes a switch.</b> <see cref="TailSem.Layout"/> reports, besides the field names,
/// the exact byte extent of every block it read (<see cref="TailBlock"/>). Turning a block off therefore
/// means deleting precisely <c>[BodyOff, BodyEnd)</c> and writing 0 into its gate word; turning one on
/// means writing 1 into the gate and inserting the block's <em>default</em> body at <c>BodyOff</c> — the
/// bytes the engine itself would serialise for a fresh block (every scalar 0, every embedded string empty,
/// no optional trailing field), whose width <see cref="TailSem.DefaultBodySize"/> measures out of the same
/// grammar rather than a table typed next to it. Blocks before and after are copied byte-for-byte, so an
/// off block still costs exactly its 4-byte gate and the stream order the decompile fixes is preserved.</para>
///
/// <para><b>Nothing is written unless it reads back.</b> Every result is re-run through the decoder and
/// refused unless the walk covers the whole new tail with zero residual and the block ends up in exactly
/// the state asked for. The caller adds the second half of the proof — the record must still frame to the
/// same boundaries (<see cref="ElemEdit.ReshapeTail"/> re-splits its section block,
/// <see cref="TbEdit.ReshapeTail"/> re-measures it with the codec's own measurer) — so a structural edit
/// can never desynchronise the file it is committed to (I1: an untouched level still round-trips
/// byte-identically; I3: the edit stays inside the touched record).</para>
/// </summary>
public static class TailEdit
{
    /// <summary>Longest name the editor will write into a tail (path / defined area / point list /
    /// scriptable / linked-entity). Shipped levels stay under 40; the cap keeps a paste blunder from
    /// turning a record into kilobytes of text.</summary>
    public const int MaxNameLength = 256;

    /// <summary>Largest IndexedCellColours ramp the editor will write (each entry is four float32).</summary>
    public const int MaxColourEntries = 512;

    /// <summary>Whether a gate field currently reads as enabled, straight off the tail bytes.</summary>
    public static bool GateOn(byte[]? tail, TailField f)
        => f.Kind == TailKind.Gate && tail is not null
           && f.LaneOff >= 0 && f.LaneOff + 4 <= tail.Length
           && BitConverter.ToInt32(tail, f.LaneOff) != 0;

    /// <summary>Apply one structural tail edit and return the record's NEW tail bytes, or null when the
    /// edit is not applicable or cannot be proved — in every null case the caller changes nothing.
    /// <paramref name="value"/> is a <see cref="bool"/> for a <see cref="TailKind.Gate"/> field, a
    /// <see cref="string"/> for a <see cref="TailKind.Str"/> field, an <see cref="int"/> for a
    /// <see cref="TailKind.Count"/> field.</summary>
    public static byte[]? Reshape(byte[] tail, string? gameKey, TailField f, object? value)
        => Apply(tail, gameKey, f, value, out _);

    /// <summary>Why the same edit would be refused, or "" when it applies. The editor's status line uses
    /// this so a rejected change tells the user what the game's loader objects to instead of silently
    /// snapping the row back. It re-runs the (pure) rebuild; it writes nothing.</summary>
    public static string Explain(byte[] tail, string? gameKey, TailField f, object? value)
    {
        var ok = Apply(tail, gameKey, f, value, out string why);
        return ok is null ? why : "";
    }

    private static byte[]? Apply(byte[] tail, string? gameKey, TailField f, object? value, out string why)
    {
        switch (f.Kind)
        {
            case TailKind.Gate when value is bool on: return Toggle(tail, gameKey, f, on, out why);
            case TailKind.Str when value is string s: return Rename(tail, gameKey, f, s, out why);
            case TailKind.Count when value is int n: return Resize(tail, gameKey, f, n, out why);
            default: why = $"{f.Name} is not a switch, a name or a count, so it edits in place"; return null;
        }
    }

    // ------------------------------------------------------------------ gate ----

    private static byte[]? Toggle(byte[] tail, string? gameKey, TailField f, bool on, out string why)
    {
        why = "";
        if (!TryLayout(tail, gameKey, out _, out var blocks))
        { why = "the loading code does not read this record's tail as named behaviour"; return null; }
        int i = IndexOfGate(blocks, f);
        if (i < 0) { why = "this gate is not a behaviour block the loading code reads"; return null; }
        TailBlock b = blocks[i];
        if (GateOn(tail, f) == on)
        { why = on ? "the block is already enabled" : "the block is already disabled"; return null; }

        if (!on)
        {
            var off = Splice(tail, b.BodyOff, b.BodyLen, Array.Empty<byte>());
            if (off is null) { why = "the block's bytes fall outside this record's tail"; return null; }
            BitConverter.GetBytes(0).CopyTo(off, b.GateOff);
            if (Prove(off, gameKey, b.Group, false, out string pw)) return off;
            why = "switching it off would leave bytes the game cannot read (" + pw + ")";
            return null;
        }

        int size = TailSem.DefaultBodySize(gameKey, b.Group);
        if (size <= 0) { why = $"{b.Group} is not part of this game's loading code"; return null; }
        var ins = Splice(tail, b.BodyOff, 0, new byte[size]);
        if (ins is null) { why = $"there is no room in this record for {b.Group}'s default body"; return null; }
        BitConverter.GetBytes(1).CopyTo(ins, b.GateOff);
        if (Prove(ins, gameKey, b.Group, true, out string pw2)) return ins;
        why = $"switching it on would leave bytes the game cannot read ({pw2})";
        return null;
    }

    // The rewritten tail must decode to itself, completely, with the block now in the state asked for.
    private static bool Prove(byte[] tail, string? gameKey, string group, bool want, out string why)
    {
        why = "";
        var (fields, consumed, blocks) = TailSem.Layout(tail, gameKey);
        if (fields.Count == 0 || consumed != tail.Length)
        { why = $"{tail.Length - consumed} byte(s) the loading code would not read"; return false; }
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].Group == group)
            {
                bool state = GateOn(tail, new TailField(group, blocks[i].GateName, TailKind.Gate, blocks[i].GateOff));
                bool body = blocks[i].BodyLen > 0;
                if (state == want && body == want) return true;
                why = $"it reads back {(state ? "enabled" : "disabled")} with {(body ? "a body" : "no body")}";
                return false;
            }
        why = "the walk stopped before this block";
        return false;
    }

    private static int IndexOfGate(List<TailBlock> blocks, TailField f)
    {
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].GateOff == f.LaneOff && blocks[i].Group == f.Group) return i;
        return -1;
    }

    // ----------------------------------------------------------------- string ---

    private static byte[]? Rename(byte[] tail, string? gameKey, TailField f, string text, out string why)
    {
        why = "";
        text ??= "";
        int lenOff = f.LaneOff - 4;      // a Str field is recorded just after its int32 prefix
        if (lenOff < 0 || f.LaneOff >= tail.Length) { why = "this name sits outside the record's tail"; return null; }
        int oldLen = BitConverter.ToInt32(tail, lenOff);
        if (oldLen < 0 || f.LaneOff + oldLen > tail.Length)
        { why = $"the stored name length ({oldLen}) runs past the end of the record"; return null; }
        if (text.Length > MaxNameLength) { why = $"a name longer than {MaxNameLength} characters is refused"; return null; }
        foreach (char ch in text) if (ch < 32 || ch > 126)
        { why = "the engine's own names are printable ASCII, so anything else is refused"; return null; }

        // Keep the build's own terminator convention: a NUL-terminated name stays NUL-terminated, an
        // unterminated one stays exactly as wide as its text (the reader stops on the length either way).
        bool nul = oldLen > 0 && tail[f.LaneOff + oldLen - 1] == 0x00;
        var name = Latin1(text);
        var payload = new byte[name.Length + (nul ? 1 : 0)];
        Buffer.BlockCopy(name, 0, payload, 0, name.Length);

        // Replace the whole field — <i32 length><bytes> — with the new <i32 length><bytes>, so the tail
        // grows or shrinks by the difference in text length and everything after it shifts as one.
        var insert = new byte[4 + payload.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, insert, 0, 4);
        Buffer.BlockCopy(payload, 0, insert, 4, payload.Length);
        var nv = Splice(tail, lenOff, 4 + oldLen, insert);
        if (nv is null) { why = "the renamed record does not fit its own tail"; return null; }
        var (fields, consumed, _) = TailSem.Layout(nv, gameKey);
        if (fields.Count == 0 || consumed != nv.Length)
        { why = $"the longer name leaves {nv.Length - consumed} byte(s) the loading code would not read"; return null; }
        return nv;
    }

    // ------------------------------------------------------------ colour ramp ---

    private static byte[]? Resize(byte[] tail, string? gameKey, TailField f, int count, out string why)
    {
        why = "";
        if (count < 0 || count > MaxColourEntries)
        { why = $"the ramp has to hold between 0 and {MaxColourEntries} entries"; return null; }
        if (!TryLayout(tail, gameKey, out _, out var blocks))
        { why = "the loading code does not read this record's tail as named behaviour"; return null; }
        int owner = IndexOfOwner(blocks, f);
        if (owner < 0) { why = "this count is not inside a behaviour block the loading code reads"; return null; }
        TailBlock b = blocks[owner];

        const int entry = 16;                       // IndexedCellColours pushes four float32 per entry
        int dataOff = f.LaneOff + 4;
        int have = b.BodyEnd - dataOff;             // bytes the old ramp occupied
        if (have < 0 || dataOff > tail.Length) { why = "the ramp runs past the end of the record"; return null; }
        int want = count * entry;

        var nv = Splice(tail, dataOff, have, new byte[want]);
        if (nv is null) { why = "the resized ramp does not fit the record"; return null; }
        Buffer.BlockCopy(tail, dataOff, nv, dataOff, Math.Min(have, want));   // keep the entries that fit
        BitConverter.GetBytes(count).CopyTo(nv, f.LaneOff);
        if (Prove(nv, gameKey, b.Group, true, out string pw)) return nv;
        why = "resizing the ramp would leave bytes the game cannot read (" + pw + ")";
        return null;
    }

    private static int IndexOfOwner(List<TailBlock> blocks, TailField f)
    {
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].Group == f.Group && f.LaneOff >= blocks[i].GateOff && f.LaneOff < blocks[i].BodyEnd)
                return i;
        return -1;
    }

    // ---------------------------------------------------------------- shared ----

    // Decode, and require the walk to have named the whole tail: a record the grammar only partly
    // explains is never reshaped, because the byte extent of its blocks is then unknown.
    private static bool TryLayout(byte[] tail, string? gameKey, out int consumed, out List<TailBlock> blocks)
    {
        var (fields, c, b) = TailSem.Layout(tail, gameKey);
        consumed = c;
        blocks = b;
        return tail.Length >= 4 && fields.Count > 0;
    }

    /// <summary>Copy <paramref name="src"/> with <paramref name="removeLen"/> bytes at
    /// <paramref name="off"/> replaced by <paramref name="insert"/>. Null on an out-of-range request.</summary>
    private static byte[]? Splice(byte[] src, int off, int removeLen, byte[] insert)
    {
        if (off < 0 || removeLen < 0 || off + removeLen > src.Length) return null;
        int rest = src.Length - off - removeLen;
        var nv = new byte[off + insert.Length + rest];
        Buffer.BlockCopy(src, 0, nv, 0, off);
        Buffer.BlockCopy(insert, 0, nv, off, insert.Length);
        Buffer.BlockCopy(src, off + removeLen, nv, off + insert.Length, rest);
        return nv;
    }

    private static byte[] Latin1(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)(s[i] & 0xFF);
        return b;
    }
}
