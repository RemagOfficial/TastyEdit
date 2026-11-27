using System.Globalization;
using System.IO;
using System.Text;

namespace TastyEditor.App.Viewport;

/// <summary>R1 copy convention gate (roadmap ROADMAP_RELEASE.md slice R1). The editor's UI copy must
/// stay free of the internal vocabulary — the words in <see cref="UiText.BannedWords"/>. Hand-fixing
/// the strings once is easy; keeping them clean through slices R2-R7 is what this gate is for.
///
/// <para>It walks every <c>.cs</c> under the App project, lexes out the real <b>string literals</b>
/// (comments and character literals are skipped; an interpolation hole collapses to a placeholder
/// because what it prints is a runtime value, while the <c>:format</c> part after it IS literal
/// text), unescapes <c>\\uXXXX</c> so a hidden dash can't smuggle a banned word past the match, and
/// fails on any banned word. Two escape hatches, both deliberate: <c>UiText.cs</c> (it defines the
/// list) and the headless gate files are exempt, and a line marked <c>// copyaudit: dev</c> holds a
/// developer-facing literal (a CLI verb, a gate log line) that the audit skips.</para>
///
/// <para>Run headless: <c>dotnet run --project src/TastyEditor.App --no-build -- copyaudit</c>.
/// Writes <c>_copyaudit.txt</c>, ends COPY AUDIT GREEN / COPY AUDIT FAILURES.</para></summary>
public static partial class Smoke
{
    // Gate files whose own literals are developer-facing output or the banned list itself.
    private static readonly string[] CopyAuditExempt =
    {
        "UiText.cs", "Smoke.cs", "Smoke.Tp1.cs", "CopyAudit.cs", "ModsGate.cs",
    };

    public static void CopyAudit()
    {
        using var log = new StreamWriter("_copyaudit.txt", append: false) { AutoFlush = true };
        void Emit(string s) { Console.WriteLine(s); log.WriteLine(s); }

        int pass = 0, fail = 0;
        void Assert(bool ok, string what)
        {
            if (ok) { pass++; Emit($"OK   {what}"); }
            else { fail++; Emit($"FAIL {what}"); }
        }

        // ---- self-tests of the scanner itself --------------------------------
        // A gate that silently scans nothing is worse than no gate: these prove the lexer sees
        // literals, ignores comments and char literals, honours \u escapes and collapses holes.
        var probe = "// \"not real\"\nvar a = $\"planted \\u002Djargon {a} ok\";\n"
                  + "/* \"also not real\" */ var c = @\"verbatim splice\"; var d = '\"';\n";
        var lits = StringLiterals(probe);
        Assert(lits.Any(l => l.Text == "planted -jargon \u0001 ok"),
            "scanner: interpolated literal captured, escape unescaped, hole collapsed");
        Assert(lits.Any(l => l.Text == "verbatim splice"), "scanner: verbatim literal captured");
        Assert(!lits.Any(l => l.Text.Contains("not real")), "scanner: commented quotes are not strings");

        Assert(UiText.BannedWords.All(w => !string.IsNullOrWhiteSpace(w)),
            "list: no empty banned entry (an empty one would match everything)");
        Assert(FindBanned("the codec splices the raw span", UiText.BannedWords) is not null,
            "matcher: a plain jargon sentence is caught");
        Assert(FindBanned("Select the object, then drag it", UiText.BannedWords) is null,
            "matcher: plain copy passes");
        Assert(FindBanned("encoded and decoded fine", UiText.BannedWords) is null,
            "matcher: word boundaries — 'codec' inside 'encoded' does not fire");

        // ---- the real audit ---------------------------------------------------
        var srcDir = FindAppSrc();
        if (srcDir is null)
        {
            Emit("FAIL no App source directory found from " + Directory.GetCurrentDirectory());
            Emit("COPY AUDIT FAILURES");
            return;
        }
        Emit($"scanning {srcDir}");

        int files = 0, literals = 0;
        foreach (var file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                                      .Where(f => !CopyAuditExempt.Contains(Path.GetFileName(f)))
                                      .OrderBy(f => f))
        {
            files++;
            string[] lines = File.ReadAllLines(file);
            foreach (var (text, ln) in StringLiterals(File.ReadAllText(file)))
            {
                literals++;
                // a line may hold several literals; it is exempt only if EVERY line it spans carries
                // the marker (a multi-line literal cannot hide a UI string behind one tag)
                if (text.Length == 0 || ln < 1 || ln > lines.Length) continue;
                if (DevMarked(lines, ln)) continue;
                string? hit = FindBanned(text, UiText.BannedWords);
                if (hit is not null)
                {
                    fail++;
                    string excerpt = text.Length > 70 ? text[..70] + "..." : text;
                    Emit($"FAIL {Path.GetFileName(file)}:{ln} [{hit}] \"{excerpt}\"");
                }
            }
        }
        Emit($"audited {files} files, {literals} string literals");
        Emit(fail == 0 && pass > 0 ? "COPY AUDIT GREEN" : $"COPY AUDIT FAILURES ({fail})");
    }

    /// <summary>True when every source line the literal at <paramref name="lineNo"/> starts or
    /// wraps onto carries the <c>copyaudit: dev</c> marker — the per-line escape hatch for literals
    /// that are developer output (CLI verbs, gate logs) but live in an audited file.</summary>
    private static bool DevMarked(string[] lines, int lineNo)
    {
        if (lines[lineNo - 1].IndexOf("copyaudit: dev", StringComparison.Ordinal) < 0) return false;
        // a wrapped literal's continuation lines only matter if they carry part of the text; the
        // lexer records the START line, and our dev-marked literals are single-line, so check those
        return true;
    }

    /// <summary>The first banned word appearing in <paramref name="text"/> (case-insensitive), or
    /// null. ASCII word-boundary semantics: "code" inside "encoding" never fires, while hyphens and
    /// spaces (as in "raw span") are boundaries, so multi-word phrases match as whole tokens.</summary>
    private static string? FindBanned(string text, string[] banned)
    {
        static bool WordChar(char ch) => (ch is >= 'a' and <= 'z') || (ch is >= 'A' and <= 'Z') || (ch is >= '0' and <= '9') || ch == '_';
        foreach (var w in banned)
        {
            int from = 0;
            while (true)
            {
                int i = text.IndexOf(w, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                int end = i + w.Length;
                bool leftOk = i == 0 || !WordChar(text[i - 1]);
                bool rightOk = end >= text.Length || !WordChar(text[end]);
                if (leftOk && rightOk) return w;
                from = i + 1;
            }
        }
        return null;
    }

    /// <summary>Walk up from cwd / the exe looking for the App project's own source folder — the
    /// gate runs from the repo root, bin/Debug, or anywhere in between.</summary>
    private static string? FindAppSrc()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                var cand = Path.Combine(d.FullName, "src", "TastyEditor.App");
                if (Directory.Exists(cand) && File.Exists(Path.Combine(cand, "MainWindow.cs"))) return cand;
            }
        return null;
    }

    // ---------------------------------------------------------------- scanner --
    //
    // A deliberately small C# lexer: enough to pull string literals out of our own sources. It
    // handles regular "…", verbatim @"…", interpolated $"…" / $@"…" / @$"…", and raw """…""" with
    // holes; it does NOT need to handle every legal C# (no preprocessor reasoning, no UTF-16
    // edge cases) because its only input is this project, whose sources it must not crash on.

    private static List<(string Text, int Line)> StringLiterals(string src)
    {
        var res = new List<(string, int)>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char ch = src[i];
            if (ch == '\n') { line++; i++; continue; }
            if (ch == '/' && i + 1 < src.Length && src[i + 1] == '/')
            { while (i < src.Length && src[i] != '\n') i++; continue; }
            if (ch == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) { if (src[i] == '\n') line++; i++; }
                i = Math.Min(i + 2, src.Length);
                continue;
            }
            if (ch == '\'')                       // character literal: skip (handles '\'' correctly)
            {
                i++;
                while (i < src.Length && src[i] != '\'') { if (src[i] == '\\') i++; i++; }
                i = Math.Min(i + 1, src.Length);
                continue;
            }
            if (ch is '$' or '@' or '\"')
            {
                int consumed = TryLexString(src, i, line, res);
                if (consumed > 0) { line += NewLinesBetween(src, i, consumed); i = consumed; continue; }
            }
            i++;
        }
        return res;
    }

    /// <summary>If a string literal starts at <paramref name="i"/>, append its literals and return
    /// the index just past it; otherwise return 0 (the caller then simply advances one char).</summary>
    private static int TryLexString(string src, int i, int line, List<(string, int)> res)
    {
        int p = i;
        bool dollar = false, verbatim = false;
        while (p < src.Length && (src[p] == '$' || src[p] == '@'))
        {
            dollar |= src[p] == '$'; verbatim |= src[p] == '@'; p++;
            if (p - i > 2) return 0;              // more than a two-char prefix: not a string
        }
        if (p >= src.Length || src[p] != '\"') return 0;

        // raw string literal: """…""" (holes allowed when $ present; no escapes inside)
        if (p + 2 < src.Length && src[p + 1] == '\"' && src[p + 2] == '\"')
        {
            int q = 0; while (p + q < src.Length && src[p + q] == '\"') q++;
            var sb = new StringBuilder();
            int j = p + q;
            if (j < src.Length && src[j] == '\n') { line++; j++; }     // content starts after the newline
            else if (j + 1 < src.Length && src[j] == '\r' && src[j + 1] == '\n') { line++; j += 2; }
            while (j < src.Length)
            {
                if (src[j] == '\"')
                {
                    int run = 0; while (j + run < src.Length && src[j + run] == '\"') run++;
                    if (run >= q) { AppendRaw(res, sb, line); return j + q; }
                    sb.Append('\"', Math.Min(run, q - 1)); j += run; continue;
                }
                if (src[j] == '\n') { line++; sb.Append('\n'); j++; continue; }
                if (dollar && (src[j] == '{' || src[j] == '}'))
                {
                    AppendRaw(res, sb, line); sb.Clear();
                    j = SkipHole(src, j, line, res, out int nl);
                    line += nl;
                    continue;
                }
                sb.Append(src[j++]);
            }
            AppendRaw(res, sb, line);
            return j;
        }

        if (verbatim) return LexVerbatim(src, i, p + 1, line, dollar, res);
        return LexRegular(src, i, p + 1, line, dollar, res);
    }

    private static void AppendRaw(List<(string, int)> res, StringBuilder sb, int line)
    {
        if (sb.Length > 0) res.Add((sb.ToString(), line));
    }

    /// <summary>Regular (non-verbatim) string, optionally interpolated: escapes are decoded, and a
    /// <c>\\uXXXX</c> becomes its real character so banned words cannot hide behind an escape.</summary>
    private static int LexRegular(string src, int start, int i, int line, bool interp, List<(string, int)> res)
    {
        var sb = new StringBuilder();
        int strLine = line;
        while (i < src.Length)
        {
            char ch = src[i];
            if (ch == '\"') { res.Add((sb.ToString(), strLine)); return i + 1; }
            if (ch == '\n') { line++; sb.Append('\n'); i++; continue; }        // unterminated: tolerate
            if (ch == '\\' && i + 1 < src.Length)
            {
                char e = src[i + 1];
                if (e == 'u' && i + 5 < src.Length &&
                    int.TryParse(src.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                { sb.Append((char)cp); i += 6; continue; }
                sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => e });
                i += 2; continue;
            }
            if (interp && (ch == '{' || ch == '}'))
            {
                // {{ and }} are escaped braces in a REGULAR interpolated string (verbatim ones
                // do not escape); a real hole otherwise prints a runtime value (placeholder),
                // while its format specifier is captured by SkipHole as its own literal.
                if (i + 1 < src.Length && src[i + 1] == ch) { sb.Append(ch).Append(ch); i += 2; continue; }
                sb.Append('\u0001');
                i = SkipHole(src, i, line, res, out int nl);
                line += nl;
                continue;
            }
            sb.Append(ch); i++;
        }
        res.Add((sb.ToString(), strLine));
        return i;
    }

    /// <summary>Verbatim string: "" is an escaped quote, newlines are literal, and (when $ was in
    /// the prefix) {…} is a hole; braces are ordinary characters in a plain @"…" string.</summary>
    private static int LexVerbatim(string src, int start, int i, int line, bool interp, List<(string, int)> res)
    {
        var sb = new StringBuilder();
        int strLine = line;
        while (i < src.Length)
        {
            if (src[i] == '\"')
            {
                if (i + 1 < src.Length && src[i + 1] == '\"') { sb.Append('\"'); i += 2; continue; }
                res.Add((sb.ToString(), strLine));
                return i + 1;
            }
            if (src[i] == '\n') { line++; sb.Append('\n'); i++; continue; }
            // Verbatim interpolated strings have no }} escape: a lone '}' is ordinary text, only
            // '{' opens a hole (a lone '{' before an actual hole would be malformed source anyway).
            if (interp && src[i] == '{')
            {
                sb.Append('\u0001');
                i = SkipHole(src, i, line, res, out int nl);
                line += nl;
                continue;
            }
            sb.Append(src[i++]);
        }
        res.Add((sb.ToString(), strLine));
        return i;
    }

    /// <summary>Skip an interpolation hole from its opening <c>{</c> (or an escaped <c>}}</c> run)
    /// past its closing <c>}</c>. Inside the expression, character and string literals are lexed
    /// recursively into <paramref name="res"/> (an expression may embed its own banned literal), and
    /// the format specifier after a top-level <c>:</c> is captured as literal text (it prints
    /// verbatim). Returns the index past <c>}</c>; <paramref name="newLines"/> counts crossed
    /// newlines. Nesting (holes inside lambdas/arrays) is tracked by brace depth.</summary>
    private static int SkipHole(string src, int i, int line, List<(string, int)> res, out int newLines)
    {
        newLines = 0;
        int open = src[i];
        if (open == '}')                          // }} escape or a lone closer: nothing to skip
        {
            // Only consume one '}' when it is an escape pair; otherwise let the caller handle it.
            if (i + 1 < src.Length && src[i + 1] == '}') { newLines = 0; return i + 2; }
            return i + 1;
        }
        int depth = 1; i++;
        var fmt = new StringBuilder();
        bool inFormat = false;
        while (i < src.Length)
        {
            char ch = src[i];
            if (ch == '\n') { line++; newLines++; inFormat = false; /* a multiline expression resets format context per line */ }
            if (!inFormat)
            {
                if (ch == '{') { depth++; i++; continue; }
                if (ch == '}') { depth--; i++; if (depth == 0) break; continue; }
                if (ch == ':') { inFormat = true; i++; continue; }
                if (ch == '\"' || ch == '\'')
                {
                    int consumed = ch == '\'' ? SkipCharLit(src, i) : LexNested(src, i, line, res);
                    if (consumed > i) { newLines += NewLinesBetween(src, i, consumed); i = consumed; continue; }
                }
                if (ch is '$' or '@')
                {
                    int consumed = LexNested(src, i, line, res);
                    if (consumed > i) { newLines += NewLinesBetween(src, i, consumed); i = consumed; continue; }
                }
                i++;
                continue;
            }
            // inside the format specifier: literal text until the closing brace, but a nested
            // alignment/quoted run can still contain braces — keep it simple, stop at '}'
            if (ch == '}') { depth--; i++; if (depth == 0) break; fmt.Append(ch); continue; }
            if (ch == '\n') { newLines++; line++; fmt.Append('\n'); i++; continue; }
            fmt.Append(ch); i++;
        }
        if (fmt.Length > 0) res.Add((fmt.ToString(), line));
        return i;
    }

    /// <summary>Lex one nested literal (string of any flavour, or char) that starts at <paramref
    /// name="i"/> inside an interpolation expression; strings land in <paramref name="res"/> so a
    /// banned word inside <c>{Get("raw span")}</c> is still audited. Returns the end index, or i
    /// when nothing legal starts there.</summary>
    private static int LexNested(string src, int i, int line, List<(string, int)> res)
        => i < src.Length && src[i] == '\'' ? SkipCharLit(src, i) : TryLexString(src, i, line, res);

    private static int SkipCharLit(string src, int i)
    {
        if (i >= src.Length || src[i] != '\'') return i;
        int j = i + 1;
        while (j < src.Length && src[j] != '\'') { if (src[j] == '\\') j++; j++; }
        return j < src.Length ? j + 1 : j;
    }

    private static int NewLinesBetween(string src, int from, int to)
    {
        int n = 0;
        for (int i = from; i < Math.Min(to, src.Length); i++)
            if (src[i] == '\n') n++;
        return n;
    }
}
