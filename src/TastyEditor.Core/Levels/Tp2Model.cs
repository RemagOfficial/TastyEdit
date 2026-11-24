namespace TastyEditor.Core.Levels;

/// <summary>In-memory model of a Tasty Planet 2 .bin level — the C# mirror of the
/// dict read_level.read_level produces and write_level.write_level consumes. Each
/// layer holds FOUR sections in order: Wall, Path, Entity, Decoration. The header
/// framing (dummy, tile-type strings, layer count) plus per-section counts form the
/// span model; the writer re-serialises every field (with the delta-chained entity
/// and decoration positions) to reproduce the source bytes (I1).</summary>
public sealed class Tp2Shape
{
    public string Name = "";
    public bool IsCircle;
    public double Cx, Cy, Radius;
    public double[][] Vertices = Array.Empty<double[]>();
}

public sealed class Tp2Wall
{
    public double PosX, PosY, Width, Length;
    public string? WallTypeName;
    public int HasShapesFlag;
    public List<Tp2Shape> Shapes = new();
    public int Reserved;
    public int WallId;
}

public sealed class Tp2Path
{
    public string? PathName;
    public double PosX, PosY;
    public double ExtentXGuess, ExtentYGuess;
    public int PathFlag;
    public double[][] SplinePoints = Array.Empty<double[]>();   // each: 6 doubles
    public int InternalIdGuess;
}

public sealed class Tp2Entity
{
    public string? Type;
    public double[] Position = new double[2];   // world = accumulated raw delta * 0.01
    public int Field158, Field15c;
    public int Vec0, Vec1;
    public int Field250, Rotation;
    public int HasBox;
    public int BoxEnabled;
    public int[] BoxBounds = new int[4];
    public byte[] ColorRgba = new byte[4];
    public double Mass;
    public int Priority;                        // accumulated
    public int HasMoveDirection;
    public double[] Move = Array.Empty<double>();   // v0,v1,flag0,v2..v7 (8 doubles + int)
    public int MoveFlag0;
    public int HasPathFollow;
    public double Pv0, Pv1, Pv2, Pv3, Pv4;
    public int PFlag0, PFlag1, PMode;
    public string? PathName;
    public int HasEmitter;
    public double[] EmitV = Array.Empty<double>();  // 11 doubles when present
    public int EmitReserved, EmitEndMarker;
}

public sealed class Tp2Deco
{
    public int Type;
    public string? String;
    public int Cell;
    public bool HasString;                      // type == 0x02
    public bool HasCell;                        // type == 0x01
    public double[] Position = new double[2];
    public int SizeRaw;
    public int ExtraFlag;
    // The optional extra block's six int32, in the engine's order: readBoolean consumes a WHOLE
    // int32 (it reads an int, advances one int and tests == 1), so ElementDecoration::readBinary
    // walks this block as FlipX, FlipY, then four readInt (object +0x40/+0x41/+0x44../+0x50) - 24
    // bytes, exactly what the old 2-bool + 4-int + int + short guess consumed, but at the real
    // boundaries. The words are kept raw so an untouched record replays byte-for-byte.
    public int FlipWordX, FlipWordY;              // setFlipX / setFlipY (the game tests == 1)
    public bool FlipX, FlipY;
    public int[] ExtraInts = Array.Empty<int>();  // the four words after the flags
    public byte[] ColorRgba = new byte[4];
    public int[] DimsRaw = new int[2];
    public int PriorityTotal;                   // accumulated
}

public sealed class Tp2Layer
{
    public List<Tp2Wall> Walls = new();
    public List<Tp2Path> Paths = new();
    public List<Tp2Entity> Entities = new();
    public List<Tp2Deco> Decos = new();
}

public sealed class Tp2Level
{
    public int Dummy;
    public int TileTypeCount;
    public List<string?> TileTypes = new();
    public int LayerCount;
    public List<Tp2Layer> Layers = new();
}
