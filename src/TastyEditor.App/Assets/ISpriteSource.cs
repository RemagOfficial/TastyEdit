using System.Windows.Media.Imaging;

namespace TastyEditor.App.Assets;

/// <summary>What the viewport and the asset browser need from a game's sprite pipeline,
/// independent of engine. Two implementations: <see cref="SpriteSource"/> (ElementEngine —
/// Tasty Blue / Forever / Final Bite) and <see cref="Tp2SpriteSource"/> (Back for Seconds),
/// which additionally tints by the record colour and composites multi-cell recipes. All
/// returned bitmaps are frozen and safe to draw from the render thread; a false return means
/// "no sprite" so the caller falls back to a cross marker.</summary>
public interface ISpriteSource
{
    /// <summary>Resolve <paramref name="rawName"/> (frame <paramref name="frame"/>) to a
    /// bitmap. <paramref name="tint"/> is the record's RGBA colour: both engines multiply the
    /// sprite's RGB by it (TP2's planets, candy and giants as much as ElementEngine's tinted
    /// coral and props), and the colour's alpha byte is a whole-sprite fade. Never throws.</summary>
    bool TryGet(string rawName, int frame, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity);

    /// <summary>Resolve a sprite for a DECORATION. Most engines draw decos exactly like
    /// entities, so the default forwards to <see cref="TryGet"/>. TP2's background tiles ship
    /// with a feathered mask whose soft edges let the dark viewport bleed through where tile
    /// corners meet (the reported "holes"); the shipped game shows seamless dirt, so TP2
    /// hardens the feather here (partial alpha -&gt; opaque, fully transparent surround kept),
    /// while entities keep their soft alpha.</summary>
    bool TryGetDeco(string rawName, int frame, byte[]? tint, out BitmapSource image,
        out int w, out int h, out double pivotX, out double pivotY, out double opacity)
        => TryGet(rawName, frame, tint, out image, out w, out h, out pivotX, out pivotY, out opacity);

    // ---- read-only asset-browser surface ----
    IReadOnlyCollection<string> CellNames { get; }
    IReadOnlyCollection<string> AnimNames { get; }
    int CellCount { get; }
    int AnimCount { get; }
    bool HasAnimations { get; }
    bool TryGetCellThumb(string cellName, out BitmapSource image);
}
