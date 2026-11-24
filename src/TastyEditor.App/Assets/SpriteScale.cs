namespace TastyEditor.App.Assets;

/// <summary>Ports the Python entity render-scale function (tbeditor) so an entity sprite lands at
/// the game's own relative size. The sprite's natural pixel size is multiplied by this so the
/// rendered area tracks the record's mass. Decorations are NOT scaled here: their record carries
/// an explicit world box (<c>dimensions.raw</c>, centiunits) which
/// <see cref="Viewport.ViewportHost"/> stretches the sprite to fill.</summary>
public static class SpriteScale
{
    /// <summary>Area-based entity scale (rendered area = mass) with an elongation boost
    /// shaped like a hump: compact sprites use the plain area model; the boost ramps to
    /// BMAX at the caliper's aspect (~3.2) then decays so very thin sprites don't run away.
    /// Mirrors <c>tbeditor.entity_render_scale</c> exactly (A_LO/A_PEAK/BMAX/DECAY).</summary>
    public static double EntityRender(double mass, double w, double h)
    {
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        double asp = Math.Max(w, h) / Math.Min(w, h);
        const double aLo = 2.1, aPeak = 3.2, bmax = 1.8, decay = 0.38;
        double boost = asp <= aLo ? 1.0
            : asp <= aPeak ? 1.0 + (asp - aLo) / (aPeak - aLo) * (bmax - 1.0)
            : Math.Max(1.0, bmax - decay * (asp - aPeak));
        return Math.Sqrt(Math.Max(mass, 1.0) / (w * h)) * boost;
    }
}
