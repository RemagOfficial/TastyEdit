using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TastyEditor.App;

/// <summary>R3 icon set: a small family of hand-authored monochrome line glyphs on a 16×16 grid,
/// drawn as vector <see cref="Path"/> geometry (no bitmaps, no emoji, one consistent stroke so the
/// toolbar reads as a set). Each stroke is pointed at the theme's text brush by resource key, so an
/// icon follows a live light/dark switch the same way the rest of the chrome does.
///
/// <para>Kept deliberately spare — these are recognisable marks for the handful of actions the tool
/// bar exposes, not illustrations. <see cref="Make"/> returns a ready-to-use element sized in device
/// pixels; <see cref="Brush"/> is offered for panels that want the geometry directly.</para></summary>
internal static class Icons
{
    private const double Grid = 16;
    private const double Stroke = 1.3;

    // Path data for each glyph, drawn inside a 0..16 box. Line art (stroked, no fill) throughout.
    private static readonly Dictionary<string, string> Data = new()
    {
        ["open"]      = "M2,4 H6 L8,6 H14 V13 H2 Z",
        ["save"]      = "M4,3 H11 L13,5 V13 H3 V3 Z M6,3 V6 H11 V3 M6,10 H11 V13 H6 Z",
        ["new"]       = "M5,2 H10 L13,5 V14 H5 Z M10,2 V5 H13 M9,8 V12 M7,10 H11",
        ["undo"]      = "M6,3 L2,7 L6,11 M2,7 H9 A4,4 0 0 1 13,11 V12",
        ["redo"]      = "M10,3 L14,7 L10,11 M14,7 H7 A4,4 0 0 0 3,11 V12",
        ["fit"]       = "M2,6 V2 H6 M10,2 H14 V6 M14,10 V14 H10 M6,14 H2 V10",
        ["sprites"]   = "M2,3 H14 V13 H2 Z M4,11 L7,7 L9,9 L11,6 L13,9",
        ["onion"]     = "M8,2 L14,6 L8,10 L2,6 Z M2,9 L8,13 L14,9",
        ["multi"]     = "M3,3 H7 V7 H3 Z M9,3 H13 V7 H9 Z M3,9 H7 V13 H3 Z M9,9 H13 V13 H9 Z",
        // Background tilemap: a framed 3x2 grid of tile cells (reads as a paintable tile map, distinct
        // from "multi"'s four detached squares and "sprites"'s picture-in-frame).
        ["tilemap"]   = "M2,3 H14 V13 H2 Z M6.67,3 V13 M11.33,3 V13 M2,8 H14",
        // Level properties: the level's bounds rectangle with the player start inside it (reads as
        // "the level itself", not an element — distinct from tilemap's gridded frame).
        ["level"]     = "M2,3 H14 V13 H2 Z M8,6.4 A1.6,1.6 0 1 0 8,9.6 A1.6,1.6 0 1 0 8,6.4",
        ["settings"]  = "M8,5.2 A2.8,2.8 0 1 0 8,10.8 A2.8,2.8 0 1 0 8,5.2 M8,2 V3.6 M8,12.4 V14 " +
                        "M2,8 H3.6 M12.4,8 H14 M4.2,4.2 L5.4,5.4 M10.6,10.6 L11.8,11.8 " +
                        "M11.8,4.2 L10.6,5.4 M5.4,10.6 L4.2,11.8",
        ["delete"]    = "M4,5 H12 M6,5 V3 H10 V5 M5,5 L6,14 H10 L11,5",
        ["copy"]      = "M6,6 H13 V13 H6 Z M3,3 H10 M3,3 V10 H6",
        ["paste"]     = "M4,3 H12 V14 H4 Z M6,3 V2 H10 V3 M6,7 H10 M6,10 H10",
        ["dup"]       = "M5,5 H12 V13 H5 Z M3,3 H9 M3,3 V10 M9,8 V11 M7,9.5 H11",
        ["recent"]    = "M8,3 A5,5 0 1 0 12.5,6 M8,6 V8.5 L10,10 M8,3 V5",
        ["theme"]     = "M8,2 A6,6 0 1 0 8,14 A3,3 0 1 0 8,2 M8,2 V14",
        // R4 create glyphs (line art on the same 0..16 grid): a brick wall, a bounded area, a wave,
        // an anchored path, a decorative star — each reads as the object it drops into the level.
        ["newwall"]   = "M2,5 H14 M2,9 H14 M2,13 H14 M6,5 V9 M11,5 V9 M4,9 V13 M9,9 V13 M13,9 V13",
        ["newarea"]   = "M3,3 H13 V13 H3 Z M5,5 H11 V11 H5 Z",
        ["newwater"]  = "M2,6 C4,4 6,8 8,6 C10,4 12,8 14,6 M2,11 C4,9 6,13 8,11 C10,9 12,13 14,11",
        ["newpath"]   = "M4,13 C4,7 12,9 12,3 M3,13 A1.4,1.4 0 1 0 5,13 M11,3 A1.4,1.4 0 1 0 13,3",
        ["newdeco"]   = "M8,2 L9.6,6 L14,6.4 L10.6,9.3 L11.8,14 L8,11.4 L4.2,14 L5.4,9.3 L2,6.4 L6.4,6 Z",
    };

    /// <summary>Whether a glyph name is defined (lets callers fall back to a text label cleanly).</summary>
    public static bool Has(string name) => Data.ContainsKey(name);

    /// <summary>A themed line glyph as a <see cref="Path"/> in the given size (device-independent px),
    /// stroke following the theme text brush. Returns null for an unknown name.</summary>
    public static Path? Make(string name, double size = Grid)
    {
        if (!Data.TryGetValue(name, out var raw)) return null;
        var g = Geometry.Parse(raw);
        g.Freeze();
        var p = new Path
        {
            Data = g,
            StrokeThickness = Stroke * (size / Grid),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // Follow the theme live (the resource key exists once Theme.Apply has run).
        p.SetResourceReference(Shape.StrokeProperty, Theme.KText);
        return p;
    }

    /// <summary>A glyph wrapped for direct use as a button/label content, sized to <paramref name="size"/>.</summary>
    public static UIElement? Content(string name, double size = Grid) => Make(name, size);

    /// <summary>A filled geometry for a glyph, if a caller wants to paint it as an image source.</summary>
    public static Geometry? Brush(string name)
        => Data.TryGetValue(name, out var raw) ? Geometry.Parse(raw) : null;
}
