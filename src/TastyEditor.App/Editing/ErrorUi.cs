using System.IO;
using System.Text;
using System.Windows;

namespace TastyEditor.App.Editing;

/// <summary>Shared error presentation (roadmap slice 7 "error UX"). One dialog shape for every
/// failure in the shell: what was attempted, a plain-language cause when the exception type maps
/// to a known story (locked file, access denied, unknown format…), and the real message plus a
/// trimmed stack trace below a divider for bug reports. The unhandled-exception hooks in App use
/// the same helper so a crash looks identical to a handled failure.</summary>
public static class ErrorUi
{
    /// <summary>Show <paramref name="ex"/> with context. Returns nothing — every caller has
    /// already rolled its own status-bar line.</summary>
    public static void Show(Window owner, string title, string? context, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Hint(ex));
        if (!string.IsNullOrEmpty(context)) sb.AppendLine().AppendLine(context);
        sb.AppendLine().AppendLine(ex.Message);
        var trace = TrimStack(ex);
        if (trace.Length > 0) sb.AppendLine().AppendLine("── details ──").Append(trace);
        MessageBox.Show(owner, sb.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Log a crash to %LOCALAPPDATA%\TastyEdit\crash.log and show the dialog.</summary>
    public static void Crash(Window? owner, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "TastyEdit");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* logging must never be the thing that fails */ }
        Show(owner ?? new Window { WindowStyle = WindowStyle.None, Width = 0, Height = 0, Visibility = Visibility.Hidden },
            "Unexpected error", "The error was written to crash.log in the editor's AppData folder.", ex);
    }

    private static string Hint(Exception ex) => ex switch
    {
        UnauthorizedAccessException =>
            "Windows refused access. If the game is under Program Files, save levels to a " +
            "different folder or run the editor as administrator.",
        IOException io when io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase) =>
            "The file is open somewhere else (the game itself, or another editor window) — " +
            "close it and try again.",
        IOException =>
            "A file/folder problem: the path may be on a disconnected drive, or there is not " +
            "enough disk space.",
        System.Xml.XmlException =>
            "The XML is not well-formed — a tag is probably unbalanced by a hand edit.",
        FormatException =>
            "A value inside the file is not the kind of data the editor expected.",
        _ when IsUnknownFormat(ex) =>
            "This does not look like a level file of any supported game (TP2, Tasty Blue, " +
            "Forever, Final Bite). If it IS one, the file may be from a newer game update.",
        _ => "Something went wrong.",
    };

    private static bool IsUnknownFormat(Exception ex) =>
        (ex is InvalidOperationException && ex.Message.Contains("format", StringComparison.OrdinalIgnoreCase))
        || ex.Message.Contains("not a level", StringComparison.OrdinalIgnoreCase);

    private static string TrimStack(Exception ex)
    {
        var trace = ex.ToString();
        var lines = trace.Split('\n');
        // First line is the message again (skip it); keep at most 8 stack lines.
        var keep = lines.Skip(1).Take(8).ToArray();
        return keep.Length == 0 ? "" : string.Join("\n", keep).TrimEnd();
    }
}
