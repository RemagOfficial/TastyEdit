using TastyEditor.Core.Streams;

namespace TastyEditor.Core.Levels;

/// <summary>Record-body walkers for the ElementEngine sections whose stream shape
/// is identical across Forever and Final Bite (wall/snow, defined-area, path,
/// pointlist) plus the shared ShapeDefs reader. These advance the cursor by
/// exactly the bytes each record occupies so the codec can slice raw spans; they
/// never interpret values. Ported 1:1 from tb_f5_read.</summary>
public static class ElementShapes
{
    private const int CountCap = 4_000_000;

    public static void ReadColour(ByteCursor c) { c.U8(); c.U8(); c.U8(); c.U8(); }

    /// <summary>Whether a ShapeDefs type name denotes the circle shape. Dispatch is on the TYPE,
    /// not on the spelling the level happened to save: the shipped data writes shape names in lower
    /// case (`poly`, and `circle` in Forever's mars1_9 / mars1_10) while the decompile names the
    /// class `Circle`. Comparing case-sensitively sent those two levels down the polygon branch,
    /// which read a vertex count out of the circle's radius double (0, so an empty shape) and then
    /// lost the 24 bytes the circle actually occupies — the rest of the layer fell out of step, the
    /// level could not be framed, and it drew no entities at all.</summary>
    public static bool IsCircleName(string? name) =>
        name is not null && name.Equals("Circle", StringComparison.OrdinalIgnoreCase);

    private static void ReadShapeBody(ByteCursor c, string name)
    {
        if (IsCircleName(name))
        {
            c.F64(); c.F64(); c.F64();
        }
        else
        {
            int m = c.I32();
            if (m < 0 || m > CountCap)
                throw new StopException($"bad poly verts {m} @{c.P}");
            for (int i = 0; i < m; i++) { c.F64(); c.F64(); }
        }
    }

    /// <summary>ShapeDefs::readBinary(file, bool builtin).</summary>
    public static void ReadShapes(ByteCursor c)
    {
        int n = c.I32();
        if (n < 0 || n > CountCap)
            throw new StopException($"bad shape count {n} @{c.P}");
        for (int i = 0; i < n; i++)
        {
            string name = c.String();
            ReadShapeBody(c, name);
        }
        if (c.Bool4() != 0)
        {
            string name = c.String();
            ReadShapeBody(c, name);
        }
    }

    /// <summary>ElementWall / ElementSnow (identical stream shape).</summary>
    public static void RecWall(ByteCursor c)
    {
        c.F64(); c.F64(); c.F64(); c.F64();
        c.String();
        if (c.U8() == 0) ReadShapes(c);
        c.I32();
    }

    /// <summary>ElementDefinedArea.</summary>
    public static void RecArea(ByteCursor c)
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        if (c.U8() == 0) ReadShapes(c);
        c.I32();
    }

    /// <summary>ElementPath. flag==1 -> embedded DGUI::Path spline (n * 6 doubles).</summary>
    public static void RecPath(ByteCursor c)
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        if (c.U8() == 1)
        {
            int n = c.I32();
            if (n < 0 || n > CountCap)
                throw new StopException($"bad path pts {n} @{c.P}");
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 6; k++) c.F64();
        }
        c.I32();
    }

    /// <summary>ElementPointList (== Forever "Sec4").</summary>
    public static void RecPointList(ByteCursor c)
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        int n = c.I32();
        if (n < 0 || n > CountCap)
            throw new StopException($"bad pointlist n {n} @{c.P}");
        for (int i = 0; i < n; i++) { c.F64(); c.F64(); }
        c.I32();
    }
}
