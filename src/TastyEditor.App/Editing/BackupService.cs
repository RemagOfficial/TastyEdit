using System.IO;
using System.Linq;

namespace TastyEditor.App.Editing;

/// <summary>Side copies of a level file taken just before the editor overwrites it in place, so a
/// bad save is always recoverable even though the editor has no undo past a save. Backups land next
/// to the file as <c>level.bin.&lt;timestamp&gt;.bak</c> (a plain <c>level.bin.bak</c> is refreshed to
/// the newest copy as well, so the well-known name always points at the last one). Only the most
/// recent <see cref="Keep"/> timestamped copies are retained; older ones are pruned. Every operation
/// is best-effort — a backup that cannot be written must never block or fail the save itself.</summary>
public static class BackupService
{
    /// <summary>How many timestamped side copies to keep per file.</summary>
    public const int Keep = 5;

    /// <summary>Copy <paramref name="destPath"/> aside before it is overwritten. Returns the backup
    /// path written, or null when there was nothing to back up (no existing file) or the copy failed.
    /// Never throws: a backup problem degrades to "no backup", it does not stop a save.</summary>
    public static string? BeforeWrite(string destPath)
    {
        try
        {
            if (!File.Exists(destPath)) return null;
            var dir = Path.GetDirectoryName(destPath);
            if (string.IsNullOrEmpty(dir)) return null;
            string name = Path.GetFileName(destPath);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backup = Path.Combine(dir, $"{name}.{stamp}.bak");
            File.Copy(destPath, backup, overwrite: true);

            // Keep the plain ".bak" name pointed at the newest copy for people who look for it there.
            try { File.Copy(destPath, Path.Combine(dir, $"{name}.bak"), overwrite: true); } catch { }

            Prune(dir, name);
            return backup;
        }
        catch { return null; }
    }

    // Trim the per-file timestamped backups down to the newest Keep. The plain ".bak" alias is never
    // matched here (no timestamp segment), so it survives independently.
    private static void Prune(string dir, string name)
    {
        try
        {
            var stale = Directory.GetFiles(dir, $"{name}.*.bak")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(Keep);
            foreach (var f in stale) { try { f.Delete(); } catch { } }
        }
        catch { }
    }
}
