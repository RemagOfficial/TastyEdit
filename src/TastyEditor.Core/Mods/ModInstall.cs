namespace TastyEditor.Core.Mods;

/// <summary>One assets folder on disk, as the mod picker sees it: the live <c>assets</c> slot, the
/// pristine <c>assets-vanilla</c> backup, or a stored mod copy <c>assets-&lt;slug&gt;</c>.</summary>
public sealed record InstalledMod(string FolderName, string Name, string Game, string Version,
                                  bool IsActive, bool IsVanilla);

/// <summary>What an install will do, counted before anything is written.</summary>
public sealed record InstallPreview(int New, int Replace);

/// <summary>What an install did. <see cref="FolderName"/> is the merged mod folder (before any
/// activation renamed it into the live <c>assets</c> slot).</summary>
public sealed record InstallReport(string FolderName, int Added, int Replaced, bool Activated);

/// <summary>
/// The install/switch/revert engine, operating on a single game's <b>install root</b> — the folder
/// that holds the game's <c>assets</c> directory (for every supported game that is
/// <c>&lt;GameInstall.InstallDir&gt;</c>, whose <c>assets</c> child the game actually reads).
/// </summary>
/// <remarks>
/// <para>The whole model rests on one fact: the game only ever loads a folder literally named
/// <c>assets</c>. So "installing" a mod builds a complete merged copy of the vanilla assets with
/// the mod's files overlaid on top, and "switching" (or reverting) is just a folder rename. Nothing
/// is deleted except a mod folder being reinstalled over itself; the pristine vanilla copy is made
/// once and kept forever as the merge base and the revert target.</para>
/// <para>Every multi-step rename is done with rollback: if the second half of a swap fails, the
/// first rename is undone so the game is never left without an <c>assets</c> folder. Renames retry
/// briefly to ride out a file lock (the game or an atlas handle holding a folder).</para>
/// </remarks>
public static class ModInstall
{
    public const string VanillaFolder = "assets-vanilla";
    private const string ActiveFolder = "assets";
    private const string Prefix = "assets-";

    private const int RenameRetries = 6;
    private const int RenameDelayMs = 150;

    public static string AssetsPath(string installRoot) => Path.Combine(installRoot, ActiveFolder);

    /// <summary>Whether this folder looks like a game install we can manage (has an <c>assets</c>
    /// dir, or a previously-managed <c>assets-*</c> sibling that could be re-activated).</summary>
    public static bool IsManageable(string installRoot)
    {
        if (Directory.Exists(AssetsPath(installRoot))) return true;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(installRoot))
                if (Path.GetFileName(d).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>List every assets folder on disk, the active one first.</summary>
    public static List<InstalledMod> ScanInstalled(string installRoot)
    {
        var list = new List<InstalledMod>();
        if (!Directory.Exists(installRoot)) return list;

        foreach (var dir in Directory.EnumerateDirectories(installRoot))
        {
            string folder = Path.GetFileName(dir);
            bool active = folder.Equals(ActiveFolder, StringComparison.OrdinalIgnoreCase);
            bool isVanillaDir = folder.Equals(VanillaFolder, StringComparison.OrdinalIgnoreCase);
            if (!active && !folder.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var id = ModIdentity.Read(dir);
            string name = id?.Name ?? ModIdentity.DisplayFor(dir, folder);
            bool vanilla = id?.IsVanilla == true || isVanillaDir;
            if (active && name.Length == 0)
                name = vanilla ? ModIdentity.VanillaName : "active";

            list.Add(new InstalledMod(folder, name, id?.Game ?? "", id?.Version ?? "", active, vanilla));
        }

        list.Sort((a, b) =>
        {
            if (a.IsActive != b.IsActive) return a.IsActive ? -1 : 1;
            if (a.IsVanilla != b.IsVanilla) return a.IsVanilla ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// <summary>The name the currently active assets folder represents (its identity), or empty.</summary>
    public static string ActiveName(string installRoot)
    {
        var dir = AssetsPath(installRoot);
        if (!Directory.Exists(dir)) return "";
        var id = ModIdentity.Read(dir);
        return id?.Name ?? "";
    }

    /// <summary>
    /// Make (or adopt) the pristine vanilla copy that every install merges onto and revert returns
    /// to. Idempotent: if <c>assets-vanilla</c> already exists it is returned untouched. Requires a
    /// live <c>assets</c> folder the first time.
    /// </summary>
    public static string EnsureVanilla(string installRoot, string gameKey)
    {
        var van = Path.Combine(installRoot, VanillaFolder);
        if (Directory.Exists(van)) return van;

        var assets = AssetsPath(installRoot);
        if (!Directory.Exists(assets))
            throw new ModInstallException("There is no 'assets' folder here to back up.");

        CopyTree(assets, van);
        new ModIdentity { Name = ModIdentity.VanillaName, Game = gameKey, Kind = ModIdentity.KindVanilla }
            .Write(van);
        return van;
    }

    /// <summary>Count new vs replaced payload files against vanilla, without writing anything.</summary>
    public static InstallPreview PreviewInstall(string installRoot, string tpmPath, string gameKey)
    {
        var van = Path.Combine(installRoot, VanillaFolder);
        if (!Directory.Exists(van))
        {
            // No vanilla yet: everything the mod carries is "new" relative to a not-yet-taken base
            // (the live assets is the de-facto base for the count).
            van = AssetsPath(installRoot);
        }
        int n = 0, r = 0;
        foreach (var rel in TpmFile.ListPayload(tpmPath))
        {
            var target = Path.Combine(van, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target)) r++; else n++;
        }
        return new InstallPreview(n, r);
    }

    /// <summary>
    /// Merge a <c>.tpm</c> over a fresh copy of vanilla into <c>assets-&lt;slug&gt;</c>, then (unless
    /// <paramref name="activate"/> is false) make it the live assets. Returns the count and folder.
    /// Throws <see cref="ModInstallException"/> on a game mismatch, a missing/locked vanilla, or a
    /// zip-slip entry (which rolls the half-built folder back).
    /// </summary>
    public static InstallReport InstallFromTpm(string installRoot, string tpmPath, string gameKey,
                                               bool activate = true)
    {
        var (ok, meta, err) = TpmFile.Validate(tpmPath);
        if (!ok || meta is null)
            throw new ModInstallException(err ?? "The mod file is not valid.");
        if (!string.Equals(meta.Game, gameKey, StringComparison.OrdinalIgnoreCase))
            throw new ModInstallException(
                $"This mod is for a different game ({meta.Game}) than the folder selected ({gameKey}).");

        // Activating renames the live assets folder, which Windows refuses while the game is
        // running (it holds assets open). Probe that rename feasibility up front — before any
        // vanilla backup or merge copy — so a running game yields one clear refusal instead of a
        // half-installed folder tree.
        if (activate) PreflightRename(AssetsPath(installRoot));

        // The pristine backup every install merges onto. On the very first install into a base
        // game the live 'assets' IS the untouched original, so rename it into the backup slot
        // instead of copying it: that leaves nothing for Activate to park afterwards, so the
        // install no longer ends up with a second identical copy of vanilla on disk (the old
        // copy-then-park path doubled it). When not activating, or when the live folder is
        // already a managed mod, fall back to the non-destructive copy.
        var van = Path.Combine(installRoot, VanillaFolder);
        if (!Directory.Exists(van))
        {
            var live = AssetsPath(installRoot);
            if (activate && Directory.Exists(live) && ModIdentity.Read(live) is null)
            {
                RobustRename(live, van);
                new ModIdentity { Name = ModIdentity.VanillaName, Game = gameKey, Kind = ModIdentity.KindVanilla }
                    .Write(van);
            }
            else
                van = EnsureVanilla(installRoot, gameKey);
        }

        string slug = UniqueSlug(installRoot, meta.Name);
        var target = Path.Combine(installRoot, Prefix + slug);

        // A leftover folder from an interrupted install of the same slug is rebuilt, not merged.
        if (Directory.Exists(target)) DeleteTree(target);

        bool act = false;
        bool finished = false;   // the merged folder is complete; losing it would waste the whole copy
        try
        {
            CopyTree(van, target);
            var written = TpmFile.ExtractAssets(tpmPath, target);
            int replaced = written.Count(rel =>
                File.Exists(Path.Combine(van, rel.Replace('/', Path.DirectorySeparatorChar))));
            int added = written.Count - replaced;

            new ModIdentity { Name = meta.Name, Game = meta.Game, Version = meta.Version, Kind = ModIdentity.KindMod }
                .Write(target);
            finished = true;

            if (activate)
            {
                try { Activate(installRoot, Prefix + slug); act = true; }
                catch (Exception) when (!Directory.Exists(target))
                {
                    // Activate renamed the merged folder into the live slot and then failed mid
                    // rollback — the mod is on disk under 'assets', never rebuild it here.
                    throw;
                }
                catch (Exception ex)
                {
                    // The merge itself succeeded; only the switch failed (almost always a file lock
                    // the preflight missed). Keep the finished mod folder so nothing is lost — it is
                    // one click from playable — and report the real reason.
                    throw new ModInstallException(
                        $"\u201c{meta.Name}\u201d was installed but could not be switched on: {ex.Message} " +
                        "Close the game and press Play for it in the mod list.");
                }
            }
            return new InstallReport(Path.GetFileName(target), added, replaced, act);
        }
        catch
        {
            // Only a half-built merge is discarded; a finished folder that merely failed to
            // activate was already turned into a ModInstallException above (with the mod kept).
            if (!finished && Directory.Exists(target)) DeleteTree(target);   // never leave a half-merged folder
            throw;
        }
    }

    /// <summary>
    /// Make <paramref name="folderName"/> (e.g. <c>assets-my mod</c> or <c>assets-vanilla</c>) the
    /// live <c>assets</c>: rename whatever is active out to its own persisted name, then rename the
    /// target in. If the second rename fails the first is undone. No-op when the target is already
    /// active.
    /// </summary>
    public static void Activate(string installRoot, string folderName)
    {
        var target = Path.Combine(installRoot, folderName);
        if (!Directory.Exists(target))
            throw new ModInstallException($"The mod folder '{folderName}' is not present.");
        var assets = AssetsPath(installRoot);
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(assets), StringComparison.OrdinalIgnoreCase))
            return;   // already live

        // The live folder is about to be renamed away; a running game holds it open and Windows
        // would fail the move halfway through the swap.
        PreflightRename(assets);

        // Park the outgoing active folder under its identity-derived name so it can be swapped back.
        string parked = null!;
        bool hadActive = Directory.Exists(assets);
        if (hadActive)
        {
            string curName = ActiveName(installRoot);
            parked = Prefix + (curName.Length == 0 ? "active-" + NowStamp() : ModIdentity.Slug(curName));
            // `parked` is a folder *name*, so the occupancy test has to resolve it inside the install
            // root — Directory.Exists on the bare name asks the process's own working directory and
            // almost always answers "free". It then renames the live folder onto an occupied slot and
            // the swap dies with "Cannot create 'assets-<name>' because ... already exists" (seen when
            // re-installing a mod while an older parked copy of that same mod is on disk).
            if (Directory.Exists(Path.Combine(installRoot, parked)) ||
                string.Equals(parked, folderName, StringComparison.OrdinalIgnoreCase))
                parked += "-" + NowStamp();
            RobustRename(assets, Path.Combine(installRoot, parked));
        }

        try
        {
            RobustRename(target, assets);
        }
        catch
        {
            // Roll the outgoing rename back so the game is never left without an assets folder.
            if (hadActive)
            {
                try { RobustRename(Path.Combine(installRoot, parked), assets); } catch { }
            }
            throw;
        }
    }

    public static void RevertVanilla(string installRoot)
    {
        var van = Path.Combine(installRoot, VanillaFolder);
        if (!Directory.Exists(van))
            throw new ModInstallException("There is no vanilla backup to revert to.");
        Activate(installRoot, VanillaFolder);
    }

    // ------------------------------------------------------------- mirroring --

    /// <summary>The assets folder (named <c>assets</c> or <c>assets-*</c>) a file lives under,
    /// or null when the path is not inside a managed game's assets tree. The <b>innermost</b>
    /// match wins: a path that happens to nest assets folders (a mod dev folder's own
    /// <c>assets</c> holding a copied game tree) mirrors relative to the one actually containing
    /// it, so a rel path never comes out starting with <c>assets\</c> and double-nesting.</summary>
    public static string? ManagedAssetsFor(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            while (!string.IsNullOrEmpty(dir))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals(ActiveFolder, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    return dir;   // walking bottom-up, the first hit is the innermost
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------- delete --

    /// <summary>Remove an installed mod's merged folder. Refuses the live <c>assets</c> (switch or
    /// revert first) and the pristine <c>assets-vanilla</c> backup, so deleting never strands the
    /// game or destroys the only original copy.</summary>
    public static void Delete(string installRoot, string folderName)
    {
        if (folderName.Equals(ActiveFolder, StringComparison.OrdinalIgnoreCase))
            throw new ModInstallException("Play a different mod (or revert to vanilla) before deleting this one.");
        if (folderName.Equals(VanillaFolder, StringComparison.OrdinalIgnoreCase))
            throw new ModInstallException("The original assets backup cannot be deleted.");
        var path = Path.Combine(installRoot, folderName);
        if (!Directory.Exists(path))
            throw new ModInstallException("That mod folder is not present.");
        DeleteTree(path);
    }

    // ------------------------------------------------------------- helpers ----

    private static string UniqueSlug(string installRoot, string name)
    {
        string baseSlug = ModIdentity.Slug(name);
        string slug = baseSlug;
        int i = 1;
        while (Directory.Exists(Path.Combine(installRoot, Prefix + slug)))
            slug = baseSlug + "-" + (++i);
        return slug;
    }

    private static string NowStamp() =>
        ((long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds).ToString();

    /// <summary>
    /// Ask the OS whether <paramref name="dir/>" can be renamed right now, without renaming it.
    /// Windows refuses to rename a directory any process holds open (a running game keeps its
    /// assets folder locked), and this surfaces that as one plain sentence before an operation has
    /// written anything. A folder that does not exist yet trivially passes.
    /// </summary>
    private static void PreflightRename(string dir)
    {
        if (!Directory.Exists(dir)) return;
        // Opening the directory itself with delete-access sharing would be the exact test, but it
        // needs P/Invoke; instead try the real thing on a scratch child, which fails with the same
        // error the later rename would hit while leaving nothing behind.
        string probe = Path.Combine(dir, ".tpm-rename-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "");
            Directory.Move(dir, dir + "~probe");   // would throw if the lock blocks the rename
            Directory.Move(dir + "~probe", dir);
            File.Delete(probe);
        }
        catch
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            throw new ModInstallException(
                "The game's assets folder is locked \u2014 is the game (or the editor) still running? "
                + "Close it and try again.");
        }
    }

    private static void RobustRename(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (IOException) when (attempt < RenameRetries) { Thread.Sleep(RenameDelayMs); }
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void DeleteTree(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { Thread.Sleep(RenameDelayMs); try { Directory.Delete(dir, true); } catch { } }
    }
}

/// <summary>Raised for a refused mod operation (bad file, game mismatch, locked/absent folder) so
/// the UI can show one plain sentence instead of a raw IO trace.</summary>
public sealed class ModInstallException : Exception
{
    public ModInstallException(string message) : base(message) { }
}
