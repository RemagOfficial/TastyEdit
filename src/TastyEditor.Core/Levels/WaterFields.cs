namespace TastyEditor.Core.Levels;

public enum WaterFieldKind { Text, Int, Double, Bool }

/// <summary>One named ElementWater field: a stream-order key, the widget kind the inspector
/// should use, and a display label. Ported from the Python editor's WATER_FIELDS spec.</summary>
public readonly record struct WaterFieldSpec(string Key, WaterFieldKind Kind, string Label);

/// <summary>The named fields of a Tasty Blue <see cref="TbWater"/> in record order, as a
/// static table shared with the UI (1:1 port of tp2editor.Inspector.WATER_FIELDS). Geometry
/// (centre / size) is engine-certain; the other fields keep the neutral, offset-stable names
/// the reader uses because the shipped data never labels them. Every access goes through the
/// water's raw <c>D</c>/<c>I</c> lanes so the codec's field re-serialisation stays the single
/// lossless write path. The lane mapping is fixed by <c>TbCodec.ReadWater</c>:
/// I0/I1 toggle_a/b, D0/D1 scalar0/1, I2 toggle_c, D2 scalar2, I3..5 toggle_d/e/f,
/// D3/D4 scalar3/4, I6/I7 toggle_g/h, I8 int0, I9..11 flag_bit0/1/2, D5/D6 centre,
/// D7/D8 size, D9..12 min/max (min/max are local half-extents, derived from size).</summary>
public static class WaterFields
{
    public static readonly WaterFieldSpec[] All =
    {
        new("name",      WaterFieldKind.Text,   "name"),
        new("water_id",  WaterFieldKind.Int,    "water id"),
        new("center_x",  WaterFieldKind.Double, "center X"),
        new("center_y",  WaterFieldKind.Double, "center Y"),
        new("size_w",    WaterFieldKind.Double, "size W"),
        new("size_h",    WaterFieldKind.Double, "size H"),
        new("scalar0",   WaterFieldKind.Double, "scalar0"),
        new("scalar1",   WaterFieldKind.Double, "scalar1"),
        new("scalar2",   WaterFieldKind.Double, "scalar2"),
        new("scalar3",   WaterFieldKind.Double, "scalar3"),
        new("scalar4",   WaterFieldKind.Double, "scalar4"),
        new("toggle_a",  WaterFieldKind.Bool,   "toggle_a"),
        new("toggle_b",  WaterFieldKind.Bool,   "toggle_b"),
        new("toggle_c",  WaterFieldKind.Bool,   "toggle_c"),
        new("toggle_d",  WaterFieldKind.Bool,   "toggle_d"),
        new("toggle_e",  WaterFieldKind.Bool,   "toggle_e"),
        new("toggle_f",  WaterFieldKind.Bool,   "toggle_f"),
        new("toggle_g",  WaterFieldKind.Bool,   "toggle_g"),
        new("toggle_h",  WaterFieldKind.Bool,   "toggle_h"),
        new("int0",      WaterFieldKind.Int,    "int0"),
        new("flag_bit0", WaterFieldKind.Bool,   "flag_bit0"),
        new("flag_bit1", WaterFieldKind.Bool,   "flag_bit1"),
        new("flag_bit2", WaterFieldKind.Bool,   "flag_bit2"),
    };

    public static object Get(TbWater w, string key) => key switch
    {
        "name" => w.Name ?? "",
        "water_id" => w.WaterId,
        "center_x" => w.D[5], "center_y" => w.D[6],
        "size_w" => w.D[7], "size_h" => w.D[8],
        "scalar0" => w.D[0], "scalar1" => w.D[1], "scalar2" => w.D[2], "scalar3" => w.D[3], "scalar4" => w.D[4],
        "toggle_a" => w.I[0], "toggle_b" => w.I[1], "toggle_c" => w.I[2],
        "toggle_d" => w.I[3], "toggle_e" => w.I[4], "toggle_f" => w.I[5],
        "toggle_g" => w.I[6], "toggle_h" => w.I[7],
        "int0" => w.I[8],
        "flag_bit0" => w.I[9], "flag_bit1" => w.I[10], "flag_bit2" => w.I[11],
        _ => 0,
    };

    /// <summary>Write one field. Editing a size lane re-derives the local min/max half-extents
    /// (D9..D12) so the box stays internally consistent, exactly as a corner-drag resize does.
    /// Booleans and flag bits are stored as int32 lanes (0 / non-zero).</summary>
    public static void Set(TbWater w, string key, object value)
    {
        switch (key)
        {
            case "name": w.Name = value as string ?? value.ToString() ?? ""; return;
            case "water_id": w.WaterId = ToInt(value); return;
            case "center_x": w.D[5] = ToDbl(value); return;
            case "center_y": w.D[6] = ToDbl(value); return;
            case "size_w": w.D[7] = ToDbl(value); Derive(w); return;
            case "size_h": w.D[8] = ToDbl(value); Derive(w); return;
            case "scalar0": w.D[0] = ToDbl(value); return;
            case "scalar1": w.D[1] = ToDbl(value); return;
            case "scalar2": w.D[2] = ToDbl(value); return;
            case "scalar3": w.D[3] = ToDbl(value); return;
            case "scalar4": w.D[4] = ToDbl(value); return;
            case "toggle_a": w.I[0] = ToInt(value); return;
            case "toggle_b": w.I[1] = ToInt(value); return;
            case "toggle_c": w.I[2] = ToInt(value); return;
            case "toggle_d": w.I[3] = ToInt(value); return;
            case "toggle_e": w.I[4] = ToInt(value); return;
            case "toggle_f": w.I[5] = ToInt(value); return;
            case "toggle_g": w.I[6] = ToInt(value); return;
            case "toggle_h": w.I[7] = ToInt(value); return;
            case "int0": w.I[8] = ToInt(value); return;
            case "flag_bit0": w.I[9] = ToInt(value); return;
            case "flag_bit1": w.I[10] = ToInt(value); return;
            case "flag_bit2": w.I[11] = ToInt(value); return;
        }
    }

    private static void Derive(TbWater w)
    {
        w.D[9] = -w.D[7] / 2; w.D[10] = -w.D[8] / 2;
        w.D[11] = w.D[7] / 2; w.D[12] = w.D[8] / 2;
    }

    private static int ToInt(object v) => v switch
    {
        bool b => b ? 1 : 0,
        int i => i,
        long l => unchecked((int)l),
        double d => (int)d,
        _ => Convert.ToInt32(v),
    };

    private static double ToDbl(object v) => v switch
    {
        double d => d,
        int i => i,
        long l => l,
        bool b => b ? 1 : 0,
        _ => Convert.ToDouble(v),
    };
}
