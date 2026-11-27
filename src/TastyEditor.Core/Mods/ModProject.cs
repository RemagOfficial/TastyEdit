namespace TastyEditor.Core.Mods;

/// <summary>
/// A mod under authoring: a plain development folder OUTSIDE any game install that holds exactly
/// what the mod ships — a <c>metadata.json</c> at its root and every custom file under an
/// <c>assets/</c> child. The editor saves edited levels straight into it (mirroring the path the
/// level has inside the game's own assets), an "add asset" copy drops any other file in, and
/// publish zips the whole folder as a <c>.tpm</c>.
/// </summary>
/// <remarks>
/// Creation is deliberately separate from loading: nothing here reads, diffs or compares against
/// a vanilla install. Whatever sits in the folder is what the mod contains — a file kept because
/// it was edited, added or dropped in by hand. A published mod is consumed by the existing
/// <see cref="ModInstall"/> pipeline like any imported <c>.tpm</c>.
/// </remarks>
public sealed class ModProject
{
    public string Root { get; }
    public ModMetadata Meta { get; }
    public string AssetsDir => Path.Combine(Root, "assets");
    public string MetaPath => Path.Combine(Root, "metadata.json");

    private ModProject(string root, ModMetadata meta) { Root = root; Meta = meta; }

    /// <summary>Create a new dev folder and its manifest. When the folder already holds a mod
    /// project and <paramref name="overwrite"/> is false, throws <see cref="ModInstallException"/>
    /// so an old project is never silently clobbered.</summary>
    public static ModProject Create(string root, ModMetadata meta, bool overwrite = false)
    {
        if (meta is null) throw new ArgumentNullException(nameof(meta));
        if (string.IsNullOrWhiteSpace(meta.Name))
            throw new ModInstallException("The mod needs a name before its folder can be made.");
        if (!Levels.GameRegistry.KnownKeys.Contains(meta.Game))
            throw new ModInstallException("Pick which game this mod is for.");

        bool isProject = File.Exists(Path.Combine(root, "metadata.json"))
                      || Directory.Exists(Path.Combine(root, "assets"));
        if (isProject && !overwrite)
            throw new ModInstallException($"There is already a mod project in '{Path.GetFileName(root)}'. "
                                        + "Pick another folder name or replace it.");

        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "metadata.json"), meta.ToJson());
        return new ModProject(root, meta);
    }

    /// <summary>Re-open an existing dev folder (e.g. one created in an earlier session). Returns
    /// null when the folder is not a mod project or its manifest cannot be read.</summary>
    public static ModProject? TryOpen(string root)
    {
        try
        {
            string metaPath = Path.Combine(root, "metadata.json");
            if (!File.Exists(metaPath)) return null;
            var meta = ModMetadata.FromJson(File.ReadAllText(metaPath));
            Directory.CreateDirectory(Path.Combine(root, "assets"));   // a project always has a payload dir
            return new ModProject(root, meta);
        }
        catch { return null; }
    }

    /// <summary>Where a level file belongs inside this mod: the game-relative path mirrored under
    /// <c>assets/</c> (a saved level from any managed assets folder lands on the path the game
    /// reads). The mirror is anchored on the innermost assets folder that actually contains the
    /// file, so a path already inside the mod resolves back to itself and a nested assets tree can
    /// never double the <c>assets</c> segment. Files outside any managed tree land in
    /// <c>assets/levels/</c>, which is where authored levels ship for every game.</summary>
    public string TargetFor(string levelPath)
    {
        string src = Path.GetFullPath(levelPath);
        string? assets = ModInstall.ManagedAssetsFor(levelPath);
        if (assets is not null)
        {
            // Mirror against the assets folder that actually contains the file, so a path that
            // nests assets folders can never produce a doubled "assets\assets" segment.
            string prefix = Path.GetFullPath(assets) + Path.DirectorySeparatorChar;
            if (src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(AssetsDir, src[prefix.Length..]);
        }
        return Path.Combine(AssetsDir, "levels", Path.GetFileName(levelPath));
    }

    /// <summary>Copy any file into the mod. With no explicit <paramref name="relPath"/> a file
    /// living under a managed assets tree keeps its game-relative path; anything else is stored
    /// under <c>assets/<filename></c>. Returns the stored assets-relative path
    /// (forward slashes), or null when the source does not exist.</summary>
    public string? AddAssetFile(string sourceFile, string? relPath = null)
    {
        if (!File.Exists(sourceFile)) return null;

        string rel = relPath ?? "";
        if (rel.Length == 0)
        {
            string src = Path.GetFullPath(sourceFile);
            string? assets = ModInstall.ManagedAssetsFor(sourceFile);
            string prefix = assets is null ? "" : Path.GetFullPath(assets) + Path.DirectorySeparatorChar;
            rel = prefix.Length > 0 && src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? src[prefix.Length..]
                : Path.GetFileName(sourceFile);
        }
        rel = rel.Replace('\\', '/');
        if (rel.Length == 0 || rel.StartsWith(".."))
            throw new ModInstallException("That file cannot go inside the mod folder.");

        string dest = Path.Combine(AssetsDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sourceFile, dest, overwrite: true);
        return rel;
    }

    /// <summary>Remove one file from the dev folder by its assets-relative path (the form
    /// <see cref="ListContents"/> returns).</summary>
    public void RemoveAsset(string relPath)
    {
        string rel = relPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(AssetsDir, rel));
        if (!full.StartsWith(Path.GetFullPath(AssetsDir) + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase))
            throw new ModInstallException("That path is outside the mod folder.");
        if (File.Exists(full)) File.Delete(full);
    }

    /// <summary>Every file the mod currently holds, assets-relative with forward slashes — the
    /// exact payload a publish will ship.</summary>
    public List<string> ListContents() =>
        Directory.Exists(AssetsDir)
            ? Directory.EnumerateFiles(AssetsDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(AssetsDir, f).Replace('\\', '/'))
                .ToList()
            : new List<string>();

    /// <summary>Publish the dev folder as a <c>.tpm</c>: every file in it, packed whole with no
    /// comparison to any install's original files. Returns the number of files shipped.</summary>
    public int Publish(string tpmOutPath)
    {
        // A file saved at assets\assets\… (an old mirroring bug, or a whole game assets folder
        // dropped into the dev folder by hand) would ship as assets/assets/… and install to the
        // wrong place, so refuse with a plain instruction instead of shipping it silently.
        var nested = ListContents().FirstOrDefault(r =>
            r.StartsWith("assets/", StringComparison.OrdinalIgnoreCase));
        if (nested is not null)
            throw new ModInstallException($"The mod folder contains a nested 'assets' file ({nested}). "
                                        + "Open the mod folder and delete the inner assets\\assets folder, "
                                        + "then save the level again.");

        // Keep the on-disk manifest authoritative so a hand edit to metadata.json survives.
        var onDisk = ModMetadata.FromJson(File.ReadAllText(MetaPath));
        File.WriteAllText(MetaPath, onDisk.ToJson());
        return TpmFile.Create(tpmOutPath, AssetsDir, onDisk);
    }
}
