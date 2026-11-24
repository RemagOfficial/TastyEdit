namespace TastyEditor.Core.Levels;

/// <summary>One decoded section: the authoritative record count, the byte offset
/// of the count field and of the section end, and either a list of raw record
/// spans (framed sections) or one opaque blob (the entity block). This is the
/// lossless carrier the raw-span codecs re-emit verbatim: framing + raw bytes
/// reproduce the source exactly.</summary>
public sealed class Section
{
    public int Count;
    public int CountOff;
    public int EndOff;
    public bool Opaque;                 // true for the entity section
    public List<byte[]> Records = new(); // framed sections
    public byte[] Block = Array.Empty<byte>(); // opaque entity block

    public static Section Framed(int count, int countOff, int endOff, List<byte[]> recs) =>
        new() { Count = count, CountOff = countOff, EndOff = endOff, Opaque = false, Records = recs };

    public static Section OpaqueBlock(int count, int countOff, int endOff, byte[] block) =>
        new() { Count = count, CountOff = countOff, EndOff = endOff, Opaque = true, Block = block };
}

/// <summary>A decoded ElementEngine level stored as exact raw spans (the Forever /
/// Final Bite lossless model). Header framing + per-section counts + every
/// record's raw bytes; re-emitting them reproduces the source byte-for-byte.</summary>
public sealed class ElementLevel
{
    /// <summary>Which shipped build produced this file (<c>"f4"</c> Forever / <c>"f5"</c> Final Bite).
    /// Carried on the model because the two builds' <c>ElementEntity::readBinary</c> grammars differ
    /// (<see cref="TailSem"/>), so the tail view must know whose decompile to follow.</summary>
    public string Game = "f5";
    public int Dummy;
    public List<(int Len, byte[] Raw)> TilesRaw = new();
    // Decoded sprite-name per tile type (parallel to TilesRaw): a type-1 decoration
    // references the graphics cell by index into this table (resolve_deco_names).
    public List<string?> TileTypes = new();
    public int LayerCount;
    public List<List<Section>> Layers = new();
    public byte[] Trailing = Array.Empty<byte>();
    public int SectionsPerLayer;
}
