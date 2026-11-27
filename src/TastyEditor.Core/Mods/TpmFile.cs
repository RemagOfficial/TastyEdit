using System.IO.Compression;

namespace TastyEditor.Core.Mods;

/// <summary>
/// Reading and writing the <c>.tpm</c> container — a plain zip that holds a <c>metadata.json</c>
/// at its root and the mod's custom files under an <c>assets/</c> prefix, mirroring the target
/// game's own assets folder. The editor installs it by overlaying those files on a vanilla copy.
/// </summary>
/// <remarks>
/// Every read path is zip-slip hardened: an entry whose resolved target escapes the assets root
/// (e.g. <c>../../windows/x</c>) is rejected rather than written. Entries carry forward-slash
/// separators per the zip spec; they are normalised to the platform separator on extract.
/// </remarks>
public static class TpmFile
{
    public const string Extension = ".tpm";
    public const string MetaEntry = "metadata.json";
    public const string PayloadPrefix = "assets/";

    public static bool IsTpm(string path) =>
        Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse the archive's <c>metadata.json</c>. Returns null when the entry is absent or
    /// unreadable; the caller decides whether an invalid (but parseable) manifest is fatal.</summary>
    public static ModMetadata? ReadMetadata(string tpmPath)
    {
        using var zip = ZipFile.OpenRead(tpmPath);
        return ReadMeta(zip);
    }

    private static ModMetadata? ReadMeta(ZipArchive zip)
    {
        var e = zip.GetEntry(MetaEntry);
        if (e is null) return null;
        try
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ModMetadata.FromBytes(ms.ToArray());
        }
        catch { return null; }
    }

    /// <summary>
    /// Structural validation for an import: the file opens, has metadata, the manifest is valid
    /// (name + known game), and it carries at least one payload file under <c>assets/</c>. Returns
    /// a one-line human reason on failure. Does <em>not</em> open the embedded levels.
    /// </summary>
    public static (bool Ok, ModMetadata? Meta, string? Error) Validate(string tpmPath)
    {
        if (!File.Exists(tpmPath)) return (false, null, "The mod file was not found.");
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(tpmPath); }
        catch { return (false, null, "This is not a readable mod file."); }

        using (zip)
        {
            var meta = ReadMeta(zip);
            if (meta is null) return (false, null, "Missing metadata.json — not a TastyEdit mod.");
            if (!meta.IsValid())
            {
                string why = string.IsNullOrWhiteSpace(meta.Name)
                    ? "The mod has no name."
                    : "The mod targets an unknown game.";
                return (false, meta, why);
            }
            if (!zip.Entries.Any(e => IsPayload(e.FullName)))
                return (false, meta, "The mod contains no assets.");
            return (true, meta, null);
        }
    }

    private static bool IsPayload(string entryName)
    {
        string n = entryName.Replace('\\', '/');
        return n.StartsWith(PayloadPrefix, StringComparison.OrdinalIgnoreCase) && !n.EndsWith("/");
    }

    /// <summary>The assets-rooted relative paths a mod carries, without extracting anything — used
    /// for an install preview (new vs replace) before any bytes are written.</summary>
    public static IReadOnlyList<string> ListPayload(string tpmPath)
    {
        var rel = new List<string>();
        using var zip = ZipFile.OpenRead(tpmPath);
        foreach (var e in zip.Entries)
        {
            if (!IsPayload(e.FullName)) continue;
            string r = e.FullName.Replace('\\', '/')[PayloadPrefix.Length..];
            if (r.Length > 0) rel.Add(r);
        }
        return rel;
    }

    /// <summary>
    /// Overlay the archive's <c>assets/*</c> files onto <paramref name="targetAssetsDir"/>, creating
    /// directories as needed. A zip-slip entry aborts the extract (leaving what was written for the
    /// caller to roll back). Returns the relative paths (assets-rooted) actually written.
    /// </summary>
    public static IReadOnlyList<string> ExtractAssets(string tpmPath, string targetAssetsDir)
    {
        var written = new List<string>();
        using var zip = ZipFile.OpenRead(tpmPath);
        var rootFull = Path.GetFullPath(targetAssetsDir);
        foreach (var e in zip.Entries)
        {
            if (!IsPayload(e.FullName)) continue;
            string rel = e.FullName.Replace('\\', '/')[PayloadPrefix.Length..];
            if (rel.Length == 0) continue;

            // Zip-slip guard: the resolved target must stay inside the assets root.
            string dest = Path.GetFullPath(Path.Combine(rootFull, rel));
            if (!dest.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(dest, rootFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"mod entry escapes the assets folder: {e.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
            written.Add(rel);
        }
        return written;
    }

    /// <summary>
    /// Author a <c>.tpm</c> from an assets folder. With <paramref name="deltaFrom"/> supplied (the
    /// vanilla assets folder) only files that are new or byte-different are packed; otherwise the
    /// whole tree is included. The manifest is written as <c>metadata.json</c> at the zip root.
    /// </summary>
    public static int Create(string tpmPath, string sourceAssetsDir, ModMetadata meta,
                             string? deltaFrom = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tpmPath))!);
        if (File.Exists(tpmPath)) File.Delete(tpmPath);

        int packed = 0;
        using var zip = ZipFile.Open(tpmPath, ZipArchiveMode.Create);
        using (var me = zip.CreateEntry(MetaEntry).Open())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(meta.ToJson());
            me.Write(bytes, 0, bytes.Length);
        }

        var srcFull = Path.GetFullPath(sourceAssetsDir);
        string? vanFull = deltaFrom is null ? null : Path.GetFullPath(deltaFrom);
        foreach (var file in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcFull, file).Replace('\\', '/');
            // Editor bookkeeping markers are never shipped as mod assets.
            if (Path.GetFileName(file) == ModIdentity.FileName) continue;
            if (vanFull is not null && UnchangedFromVanilla(file, Path.Combine(vanFull, rel.Replace('/', Path.DirectorySeparatorChar))))
                continue;

            var entry = zip.CreateEntry(PayloadPrefix + rel, CompressionLevel.Optimal);
            using var es = entry.Open();
            using var fs = File.OpenRead(file);
            fs.CopyTo(es);
            packed++;
        }
        return packed;
    }

    private static bool UnchangedFromVanilla(string modFile, string vanillaFile)
    {
        if (!File.Exists(vanillaFile)) return false;                     // added → keep
        var a = new FileInfo(modFile); var b = new FileInfo(vanillaFile);
        if (a.Length != b.Length) return false;                          // quick size reject
        return BytesEqual(modFile, vanillaFile);
    }

    private static bool BytesEqual(string p, string q)
    {
        using var s1 = File.OpenRead(p);
        using var s2 = File.OpenRead(q);
        var b1 = new byte[8192]; var b2 = new byte[8192];
        while (true)
        {
            int n1 = s1.Read(b1, 0, b1.Length);
            int n2 = s2.Read(b2, 0, b2.Length);
            if (n1 != n2) return false;
            if (n1 == 0) return true;
            for (int i = 0; i < n1; i++) if (b1[i] != b2[i]) return false;
        }
    }
}
