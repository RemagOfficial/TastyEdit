namespace TastyEditor.Core.Levels;

/// <summary>In-memory model of a Tasty Blue .bin level — the C# mirror of the
/// dict produced by tb_read_level.read_level, trimmed to exactly what the lossless
/// golden round-trip needs: structural records (wall / area / path / water) carry
/// both their re-serialisable fields and their original span; entities carry their
/// raw record + chained-delta world position; decorations and tiles are opaque raw.
/// Six interleaved sections per layer in fixed order:
/// Wall, DefinedArea, Path, Entity, Decoration, Water.</summary>
public sealed class TbShape
{
    public string Name = "";
    public bool IsCircle;
    public double Cx, Cy, Radius;
    public double[][] Vertices = Array.Empty<double[]>();
}

public sealed class TbWall
{
    public double PosX, PosY, Width, Length;
    public string? WallTypeName;
    public int HasShapesFlag;
    public List<TbShape> Shapes = new();
    public int ShapesTailFlag;
    public int WallId;
    public int Off;
    public byte[] Raw = Array.Empty<byte>();
}

public sealed class TbDefinedArea
{
    public string? AreaName;
    public double PosX, PosY, Width, Length;
    public int HasShapesFlag;
    public List<TbShape> Shapes = new();
    public int ShapesTailFlag;
    public int AreaId;
    public int Off;
    public byte[] Raw = Array.Empty<byte>();
}

public sealed class TbPath
{
    public string? PathName;
    public double PosX, PosY;
    public double ExtentXGuess, ExtentYGuess;
    public int PathFlag;
    public double[][] SplinePoints = Array.Empty<double[]>();   // each: 6 doubles
    public int InternalIdGuess;
    public int Off;
    public byte[] Raw = Array.Empty<byte>();
}

public sealed class TbWater
{
    public string? Name;
    public int[] I = new int[12];        // ordered int32 fields (see reader)
    public double[] D = new double[13];  // ordered double fields (see reader)
    public int WaterId;
    public int Off;
    public byte[] Raw = Array.Empty<byte>();
}

public sealed class TbEntity
{
    public byte[] Raw = Array.Empty<byte>();
    public double PosX, PosY;            // absolute chained world position

    // Read-only display fields parsed from the record body (rendering only; the Raw
    // span stays the lossless carrier and edits remain gated elsewhere).
    public string? Name;                 // prefab / spawn name (sprite key)
    public int Rotation;                 // raw centidegrees
    public bool Flip;                  // record's per-instance flip flag (drawBase box word): mirror + negate angle
    public double Mass = 1.0;
    public int Priority;
    public byte[] ColorRgba = new byte[] { 255, 255, 255, 255 };
}

public sealed class TbDeco
{
    public byte[] Raw = Array.Empty<byte>();

    // Read-only display fields parsed from the record (rendering only; the Raw span
    // stays the lossless carrier). Position is the delta-chained world position
    // (centiunits/100, see finalize_decos); Dims are the raw scaled dimensions whose
    // first entry drives the sprite scale (DIM_BASE=16000 -> 1.0).
    public double PosX, PosY;
    public string? Name;
    public int Rotation;             // size.raw = centidegrees (tbeditor DecoItem rotation)
    public int DimW, DimH;
    public byte[] ColorRgba = new byte[] { 255, 255, 255, 255 };   // record colour; A fades the sprite

    // Draw order + facing, from the same record. Priority is the delta-chained absolute value
    // (finalize_decos: lp += _prio_delta) that the oracle feeds setZValue(layer*1000 + p*0.001).
    // The extra block's first two words are 4-byte booleans - ElementDecoration::setFlipX/setFlipY -
    // which mirror the sprite in local space; both stay false when the record has no extra block.
    public int Priority;
    public bool FlipX, FlipY;

    // The owning layer, captured at decode. A decoration's position deltas are chained
    // down the layer (absolute = running sum), so moving one deco must compensate the
    // next record's delta to keep the rest of the chain fixed — that neighbour lives in
    // this list. Not part of the byte model (Encode never reads it); null only for a
    // hand-built record, in which case the move edit safely declines.
    public TbLayer? Owner;
}

public sealed class TbLayer
{
    public List<TbWall> Walls = new();
    public List<TbDefinedArea> Areas = new();
    public List<TbPath> Paths = new();
    public List<TbEntity> Entities = new();
    public List<TbDeco> Decos = new();
    public List<TbWater> Waters = new();
}

public sealed class TbLevel
{
    public int Dummy;
    public List<byte[]> TilesRaw = new();
    // Decoded sprite-name per tile type (parallel to TilesRaw), used to resolve a
    // type-1 decoration's cell index to the graphics cell name (tbeditor.resolve_deco_names).
    public List<string?> TileTypes = new();
    public int LayerCount;
    public List<TbLayer> Layers = new();
    public byte[] Tail = Array.Empty<byte>();
}
