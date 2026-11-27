using TastyEditor.Core.Streams;

namespace TastyEditor.Core.Levels;

/// <summary>Per-game description of an ElementEngine raw-span level: how many
/// sections a layer has, which index is the opaque entity block, and the
/// record-body walkers that advance the cursor one record at a time. Forever and
/// Final Bite share the framing algorithm and differ only here (section count /
/// order, and the Snow section + deco/water/shape deltas), so both drive one
/// generic codec through a schema.</summary>
public sealed class ElementSchema
{
    public required string Game { get; init; }
    public required int Sections { get; init; }
    public required int Entity { get; init; }
    /// <summary>Indexable by section id; the entity slot is never invoked.</summary>
    public required Action<ByteCursor>[] Recorders { get; init; }
}

/// <summary>Final Bite (f5) section readers. Eight sections per layer
/// (Wall, Snow, DefinedArea, Path, PointList, Entity, Decoration, Water). FB
/// deltas vs Forever: a Snow clone of Wall; Decoration has one extra 4-byte bool
/// after RGBA; Water has one extra 4-byte float; ShapeDefs nested-extension reads
/// a full shape (string + geometry), not just a string.</summary>
public static class FinalBiteSchema
{
    private const int Cap = 4_000_000;

    private static void ReadShapes(ByteCursor c)
    {
        int n = c.I32();
        if (n < 0 || n > Cap) throw new StopException("bad shape count");
        for (int i = 0; i < n; i++)
        {
            string name = c.String();
            ShapeBody(c, name);
        }
        if (c.Bool4() != 0) { string name = c.String(); ShapeBody(c, name); }
    }

    private static void ShapeBody(ByteCursor c, string name)
    {
        if (ElementShapes.IsCircleName(name)) { c.F64(); c.F64(); c.F64(); }
        else { int m = c.I32(); if (m < 0 || m > Cap) throw new StopException("bad verts"); for (int i = 0; i < m; i++) { c.F64(); c.F64(); } }
    }

    private static void RecWall(ByteCursor c)
    {
        c.F64(); c.F64(); c.F64(); c.F64();
        c.String();
        if (c.U8() == 0) ReadShapes(c);
        c.I32();
    }

    private static void RecArea(ByteCursor c)
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        if (c.U8() == 0) ReadShapes(c);
        c.I32();
    }

    private static void RecPath(ByteCursor c)
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        if (c.U8() == 1)
        {
            int n = c.I32();
            if (n < 0 || n > Cap) throw new StopException("bad path pts");
            for (int i = 0; i < n; i++) for (int k = 0; k < 6; k++) c.F64();
        }
        c.I32();
    }

    private static void RecPointList(ByteCursor c)
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        int n = c.I32();
        if (n < 0 || n > Cap) throw new StopException("bad pointlist n");
        for (int i = 0; i < n; i++) { c.F64(); c.F64(); }
        c.I32();
    }

    private static void RecDeco(ByteCursor c)
    {
        int t = c.U8();
        if (t == 1) c.I32();
        else if (t == 2) c.String();
        c.I32(); c.I32(); c.I32();
        if (c.U8() != 0) { c.Bool4(); c.Bool4(); c.I32(); c.I32(); c.I32(); c.I32(); }
        ElementShapes.ReadColour(c);
        c.Bool4();               // FB extra
        c.I32(); c.I32(); c.I32();
    }

    private static void RecWater(ByteCursor c)
    {
        c.String();
        c.Bool4(); c.Bool4(); c.F64(); c.F64(); c.Bool4(); c.F64();
        c.Bool4(); c.Bool4(); c.Bool4(); c.F64(); c.F64();
        c.F32();                 // FB extra
        c.Bool4(); c.Bool4(); c.I32(); c.Bool4(); c.Bool4(); c.Bool4();
        for (int i = 0; i < 8; i++) c.F64();
        c.I32();
    }

    // order: Wall0 Snow1 Area2 Path3 PointList4 Entity5 Deco6 Water7
    public static ElementSchema Build() => new()
    {
        Game = "f5",
        Sections = 8,
        Entity = 5,
        Recorders = new Action<ByteCursor>[]
        {
            RecWall, RecWall, RecArea, RecPath, RecPointList, _ => { }, RecDeco, RecWater,
        },
    };
}

/// <summary>Forever (f4) section readers. Seven sections per layer
/// (Wall, DefinedArea, Path, Sec4, Entity, Decoration, Water). Differences vs FB:
/// no Snow section; ShapeDefs nested-extension reads only a string; Decoration has
/// no trailing extra bool after RGBA; Water has no extra float.</summary>
public static class ForeverSchema
{
    private static void ReadShapes(ByteCursor c)
    {
        int count = c.I32();
        for (int i = 0; i < count; i++)
        {
            string name = c.String();
            // Type dispatch, not spelling dispatch — see ElementShapes.IsCircleName for why the
            // case-sensitive version cost Forever's mars1_9 / mars1_10 their entity blocks.
            if (ElementShapes.IsCircleName(name)) { c.F64(); c.F64(); c.F64(); }
            else { int vc = c.I32(); for (int k = 0; k < vc; k++) { c.F64(); c.F64(); } }
        }
        if (c.Bool4() != 0) c.String();      // f4 nested-extension reads only a string
    }

    private static void RecWall(ByteCursor c)
    {
        c.F64(); c.F64(); c.F64(); c.F64();
        c.String();
        if (c.U8() == 0) ReadShapes(c);
        c.I32();
    }

    private static void RecArea(ByteCursor c)
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        if (c.U8() == 0) ReadShapes(c);
        c.I32();
    }

    private static void RecPath(ByteCursor c)
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        if (c.U8() == 1)
        {
            int n = c.I32();
            for (int i = 0; i < n; i++) for (int k = 0; k < 6; k++) c.F64();
        }
        c.I32();
    }

    private static void RecSec4(ByteCursor c)   // == PointList
    {
        c.String();
        c.F64(); c.F64(); c.F64(); c.F64();
        int n = c.I32();
        for (int i = 0; i < n; i++) { c.F64(); c.F64(); }
        c.I32();
    }

    private static void RecDeco(ByteCursor c)
    {
        int f = c.U8();
        if (f == 1) c.I32();
        else if (f == 2) c.String();
        c.I32(); c.I32();            // delta x, delta y
        c.I32();                     // scale
        if (c.U8() != 0) { c.Bool4(); c.Bool4(); c.I32(); c.I32(); c.I32(); c.I32(); }
        ElementShapes.ReadColour(c); // RGBA, 4 x 1 byte
        c.I32(); c.I32();            // two scaled ints
        c.I32();                     // id delta
    }

    private static void RecWater(ByteCursor c)
    {
        c.String();
        c.Bool4(); c.Bool4(); c.F64(); c.F64(); c.Bool4(); c.F64();
        c.Bool4(); c.Bool4(); c.Bool4(); c.F64(); c.F64();
        c.Bool4(); c.Bool4(); c.I32(); c.Bool4(); c.Bool4(); c.Bool4();
        for (int i = 0; i < 8; i++) c.F64();
        c.I32();
    }

    // order: Wall0 Area1 Path2 Sec4=3 Entity4 Deco5 Water6
    public static ElementSchema Build() => new()
    {
        Game = "f4",
        Sections = 7,
        Entity = 4,
        Recorders = new Action<ByteCursor>[]
        {
            RecWall, RecArea, RecPath, RecSec4, _ => { }, RecDeco, RecWater,
        },
    };
}
