namespace TastyEditor.Core.Assets;

/// <summary>Where a game's <c>assets</c> folder lives and the two sub-roots the
/// ElementEngine asset loader reads: <c>built/graphics</c> (imagemaps + atlas images)
/// and <c>editabledirect</c> (animationdefs + entityintersections). The level file path
/// names its own install, so the root is normally derived by walking up to the
/// <c>assets</c> directory; an explicit override (a config key) wins when supplied.</summary>
public sealed class AssetsRoot
{
    public required string Root { get; init; }
    public required string GraphicsDir { get; init; }
    public required string EditableDir { get; init; }

    /// <summary>True when this looks like a usable ElementEngine asset tree (the
    /// graphics dir exists and carries imagemaps.xml). TP2 and missing installs return
    /// false, so callers fall back to plain markers instead of erroring.</summary>
    public bool IsValid =>
        Directory.Exists(GraphicsDir) && File.Exists(Path.Combine(GraphicsDir, "imagemaps.xml"));

    /// <summary>Resolve the asset root for a level file. Walk the path's directories
    /// for the nearest ancestor literally named <c>assets</c> (the ElementEngine and
    /// TP2 installs both put levels under <c>.../assets/.../levels</c>). When no such
    /// segment exists, fall back to the level directory's parent. <paramref name="overrideRoot"/>
    /// (from a config key) short-circuits the walk when it contains a graphics dir.</summary>
    public static AssetsRoot? ForLevel(string? levelPath, string? overrideRoot = null)
    {
        if (overrideRoot is not null && Directory.Exists(overrideRoot))
            return Build(overrideRoot);

        if (string.IsNullOrWhiteSpace(levelPath)) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(levelPath));
        string? root = null;
        for (var d = new DirectoryInfo(dir ?? "."); d is not null; d = d.Parent)
        {
            if (string.Equals(d.Name, "assets", StringComparison.OrdinalIgnoreCase)) { root = d.FullName; break; }
        }
        root ??= Directory.GetParent(dir ?? "")?.FullName;
        return root is null ? null : Build(root);
    }

    private static AssetsRoot Build(string root) => new()
    {
        Root = root,
        GraphicsDir = Path.Combine(root, "built", "graphics"),
        EditableDir = Path.Combine(root, "editabledirect"),
    };
}
