using Microsoft.Win32;

namespace TastyEditor.App.Editing;

/// <summary>Per-user .bin association (roadmap slice 7). Writes only under HKCU\Software\Classes
/// — the per-user view that overrides machine defaults — so no admin rights are needed and the
/// registry keys we touch are entirely ours (a dedicated ProgId plus the user's Classes\.bin
/// default). We deliberately do NOT write the Explorer\UserChoice key: Windows validates it with
/// a hash and treats foreign writes as tampering. Removing restores whatever the machine default
/// (usually "no association") was. Game launchers never rely on a .bin association, and every
/// level-open path in this editor goes through the app itself, so this cannot touch game files.</summary>
public static class FileAssociation
{
    // S-D10 renamed the editor to TastyEdit, which renamed the executable too, so the association is
    // re-registered under a ProgId that matches the product. The old branch is only ever read from
    // (Unregister cleans it up): leaving a stale HKCU\Software\Classes\TastyLevelEditor.bin behind,
    // pointing at an exe that no longer exists, is exactly the kind of registry litter a rename
    // should not produce.
    private const string ProgId = "TastyEdit.bin";
    private const string LegacyProgId = "TastyLevelEditor.bin";
    private const string BinKey = @"Software\Classes\.bin";
    private const string ProgKey = @"Software\Classes\" + ProgId;
    private const string LegacyProgKey = @"Software\Classes\" + LegacyProgId;

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(BinKey);
        return k?.GetValue(null) as string == ProgId;
    }

    /// <summary>True when this user's .bin preference would not launch a working editor. See
    /// <see cref="StaleReason"/> for what specifically is wrong. Detection only: this class never
    /// writes without being asked.</summary>
    public static bool IsStale() => StaleReason() is not null;

    /// <summary>Why the handler needs moving, in one short phrase, or null when it does not. Three
    /// real states get here: a pre-rename build owns <c>.bin</c> (S-D10 renamed the product, and with it
    /// the exe, so Explorer resolves the old ProgId), our ProgId's <c>shell\open\command</c> names an exe
    /// that has been moved or deleted, or the ProgId branch itself is gone while <c>.bin</c> still names
    /// it (which is what a half-completed unregister leaves behind). The dialog shows the reason: a
    /// prompt that repeats with no explanation is indistinguishable from a broken prompt.</summary>
    public static string? StaleReason()
    {
        try
        {
            using var bin = Registry.CurrentUser.OpenSubKey(BinKey);
            var owner = bin?.GetValue(null) as string;
            if (owner == LegacyProgId) return "an earlier build of this editor still owns it";
            if (owner != ProgId) return null;                        // some other app: not ours to nag about
            using var prog = Registry.CurrentUser.OpenSubKey(ProgKey);
            if (prog is null) return "its program entry is missing, so Windows has nothing to launch";
            if (!TargetExists(ProgKey)) return "it points at a program file that is no longer there";
            return null;
        }
        catch { return null; }         // an unreadable key is not a reason to nag
    }

    /// <summary>Does the exe named by this ProgId's open command still exist?</summary>
    private static bool TargetExists(string key)
    {
        using var cmd = Registry.CurrentUser.OpenSubKey(key + @"\shell\open\command");
        if (cmd?.GetValue(null) is not string raw) return false;
        return System.IO.File.Exists(ExeOf(raw));
    }

    /// <summary>The executable inside an open-command string, which is <c>"exe" "%1"</c> (the quoted
    /// first token) or a bare <c>exe %1</c>. Note the closing quote is the one that follows the path,
    /// NOT the last quote in the string - the placeholder is quoted too, so LastIndexOf('"') swallows
    /// <c>" "%1</c> into the path and no such file can ever exist.</summary>
    internal static string ExeOf(string raw)
    {
        raw = raw.Trim();
        if (raw.Length > 0 && raw[0] == '"')
        {
            int close = raw.IndexOf('"', 1);
            return close > 1 ? raw.Substring(1, close - 1) : raw.Substring(1);
        }
        // Unquoted: an exe always ends in .exe before the arguments start.
        int e = raw.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return e >= 0 ? raw.Substring(0, e + 4) : raw.Split(' ')[0];
    }

    /// <summary>Point the current user's .bin files at this exe and open level files with it.</summary>
    public static void Register(string exePath)
    {
        using (var prog = Registry.CurrentUser.CreateSubKey(ProgKey))
        {
            prog.SetValue(null, "TastyEdit level", RegistryValueKind.String);
            using (var icon = prog.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exePath}\",0", RegistryValueKind.String);
            using (var cmd = prog.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, $"\"{exePath}\" \"%1\"", RegistryValueKind.String);
        }
        using (var bin = Registry.CurrentUser.CreateSubKey(BinKey))
            bin.SetValue(null, ProgId, RegistryValueKind.String);
        // Migrate rather than stack: a registration made before the rename points at an executable
        // that no longer exists, and an orphan ProgId branch under HKCU is invisible clutter whose only
        // effect is to make "which .bin handler is real?" unanswerable later.
        Registry.CurrentUser.DeleteSubKeyTree(LegacyProgKey, throwOnMissingSubKey: false);
        // Tell running shells so Explorer picks it up without a logoff.
        SHChangeNotify(0x08000000 /* AssocChanged */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Undo <see cref="Register"/>: delete our per-user keys (the .bin default and the
    /// ProgId branch we created, plus the pre-rename one). Nothing machine-wide is ever removed.</summary>
    public static void Unregister()
    {
        using (var bin = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
        {
            var current = bin?.GetValue(null) as string;
            if (current == ProgId || current == LegacyProgId)
                bin!.DeleteSubKey(".bin", throwOnMissingSubKey: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(ProgKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(LegacyProgKey, throwOnMissingSubKey: false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);
}
