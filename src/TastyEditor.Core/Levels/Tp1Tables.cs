using System.Globalization;
using System.IO;
using System.Text;

namespace TastyEditor.Core.Levels;

// Verbatim / span-preserving writer for the two Tasty Planet 1 asset tables the game reads at
// load: entitydefs.xml (the object types) and imagemap.xml (the sprite sheets and their cells).
// These are the files the base Python editor edits to make a custom entity, and they are plain
// XML (with the same quirks as the levels: unquoted values, mixed quoting), so they go through the
// same tokenizer as a level (Tp1Codec.ParseTree) and every change is a localised splice on the
// text buffer. An untouched table re-encodes byte-for-byte; creating, editing or removing one
// element moves no other byte, so a mod that adds a single custom type carries a diff of just that
// block. The building blocks (attribute batching, raw-element insertion, boundary-safe removal) are
// the ones the level editor already proves, reused here via Tp1Edit's internal helpers.

/// <summary>A circle in an entity's collision set. Coordinates are normalised the way the shipped
/// tables store them: centre offset from the sprite centre over the image size (so -0.5..0.5, Y
/// up), and the radius over the image height.</summary>
public sealed record Tp1Circle(double PosX, double PosY, double Radius);

/// <summary>A rotated rectangle in an entity's collision set, same normalisation as
/// <see cref="Tp1Circle"/>: centre offset over image size, extent over image width / height.</summary>
public sealed record Tp1Rect(double PosX, double PosY, double Width, double Height, double Angle = 0.0);

/// <summary>Everything the entity panel can set on one <c>&lt;entitydef&gt;</c>. Attributes not
/// named here (an animationdef, an id, etc.) are left exactly as the file had them, so editing a
/// stock type only changes what the user touched. Shapes are the collision set; an empty list
/// removes any existing <c>&lt;intersectionshapes&gt;</c> block.</summary>
public sealed class Tp1EntityDefEdit
{
    public string Name = "";
    public string ImageMap = "";
    public string? Cell;                       // set when the entity names a sub-cell of a shared sheet
    // The friendly name the game shows for this type. It is NOT an entitydef attribute — the engine
    // looks the def's name up in the string table (strings.xml) — so this is carried to the table
    // writer, never folded into the entitydef itself. Empty means "leave the table row as-is".
    public string Display = "";
    public double AspectRatio = 1.0;
    public double AreaCoverage = 1.0;
    // Tint (effectr/g/b/a): null means "leave whatever the file has" (or omit for a new def).
    public double? EffectR, EffectG, EffectB, EffectA;
    public readonly List<object> Shapes = new();   // Tp1Circle / Tp1Rect, in order
}

/// <summary>One <c>&lt;imagemap&gt;</c> entry: the sheet file (relative to graphics, no extension)
/// and either the whole image or one named cell. Pixels are absolute (as the shipped maps use).</summary>
public sealed class Tp1ImageMapEdit
{
    public string Name = "";
    public string Filename = "";               // "sub/base" with no extension, as the game stores it
    public int Width, Height;
    public bool FullImage;                     // true -> <fullimagecell />, no cell crop
    public string? CellName;                   // when false, the cell to add / update
    public int X1, Y1, X2, Y2;                 // cell rect in source pixels
}

/// <summary>The two table writers. Load a file to a <see cref="Tp1Level"/>, apply edits (each one a
/// localised splice), then save once. All methods are static and side-effect free beyond their
/// arguments; nothing here touches pixels or launches a game.</summary>
public static class Tp1Tables
{
    /// <summary>The table file names the game reads (kept here so the UI layer never hard-codes
    /// them; the base entity table file is spelled as the shipped install writes it). The third is the
    /// Excel-exported string table that turns an entity name into the friendly text shown in game.</summary>
    public const string EntityDefsFile = "entitydefs.xml";
    public const string ImageMapsFile = "imagemaps.xml";
    public const string StringsFile = "strings.xml";

    public static Tp1Level Load(string path) => Tp1Codec.ParseTree(File.ReadAllBytes(path));
    public static void Save(string path, Tp1Level doc) => File.WriteAllBytes(path, doc.Encode());

    // ------------------------------------------------------------ entitydefs --

    /// <summary>Create or update one <c>&lt;entitydef&gt;</c> in an entitydefs document. An existing
    /// def is edited in place (only its named attributes and collision block change); a missing one
    /// is appended as a new element matching the file's own style. Returns true when the document
    /// changed.</summary>
    public static bool UpsertEntityDef(Tp1Level doc, Tp1EntityDefEdit e)
    {
        var root = doc.Root;
        var existing = FindNamed(root, "entitydef", e.Name);
        if (existing is null)
        {
            Tp1Edit.InsertRaw(root, EntityDefFragment(e));
            return true;
        }

        // Batch the attribute writes so the whole scalar edit is one reindex; a node handle is only
        // valid until the next splice, so the def's path is captured first and re-resolved for the
        // collision rebuild that follows.
        var edits = new List<(int Start, int Len, string Repl)>();
        Tp1Edit.PushSet(existing, "imagemap", e.ImageMap, edits);
        if (e.Cell is { Length: > 0 }) Tp1Edit.PushSet(existing, "cell", e.Cell, edits);
        Tp1Edit.PushSetNum(existing, "aspectratio", e.AspectRatio, edits);
        Tp1Edit.PushSetNum(existing, "areacoverage", e.AreaCoverage, edits);
        if (e.EffectR is double er) Tp1Edit.PushSetNum(existing, "effectr", er, edits);
        if (e.EffectG is double eg) Tp1Edit.PushSetNum(existing, "effectg", eg, edits);
        if (e.EffectB is double eb) Tp1Edit.PushSetNum(existing, "effectb", eb, edits);
        if (e.EffectA is double ea) Tp1Edit.PushSetNum(existing, "effecta", ea, edits);

        int[] path = Tp1Level.PathOf(existing);
        if (edits.Count > 0) Tp1Codec.ReplaceAll(doc, edits);
        var node = doc.NodeAt(path);
        if (node is null) return edits.Count > 0;

        var oldShapes = node.Child("intersectionshapes");
        if (oldShapes is not null) { Tp1Edit.Remove(oldShapes); node = doc.NodeAt(path); }
        if (node is not null && e.Shapes.Count > 0) Tp1Edit.InsertRaw(node, ShapesFragment(e.Shapes));
        return true;
    }

    /// <summary>Remove the named <c>&lt;entitydef&gt;</c> (with its leading whitespace gap). True when
    /// a def of that name was present and taken out.</summary>
    public static bool RemoveEntityDef(Tp1Level doc, string name) => RemoveChild(doc.Root, "entitydef", name);

    /// <summary>Read a def back out of a document for the panel to prefill. Null when absent.</summary>
    public static Tp1EntityDefEdit? ReadEntityDef(Tp1Level doc, string name)
    {
        var n = FindNamed(doc.Root, "entitydef", name);
        if (n is null) return null;
        var e = new Tp1EntityDefEdit
        {
            Name = n.Get("name") ?? name,
            ImageMap = n.Get("imagemap") ?? "",
            Cell = n.Get("cell") ?? n.Get("imagemapcell"),
            AspectRatio = n.Num("aspectratio") ?? 1.0,
            AreaCoverage = n.Num("areacoverage") ?? 1.0,
            EffectR = n.Num("effectr"),
            EffectG = n.Num("effectg"),
            EffectB = n.Num("effectb"),
            EffectA = n.Num("effecta"),
        };
        var shapes = n.Child("intersectionshapes");
        if (shapes is not null)
            foreach (var s in shapes.Children)
            {
                double px = s.Num("posx") ?? 0, py = s.Num("posy") ?? 0;
                if (s.Tag == "circle") e.Shapes.Add(new Tp1Circle(px, py, s.Num("radius") ?? 0));
                else if (s.Tag is "rect" or "rotrect")
                    e.Shapes.Add(new Tp1Rect(px, py, s.Num("width") ?? 0, s.Num("height") ?? 0, s.Num("angle") ?? 0));
            }
        return e;
    }

    // ------------------------------------------------------------- imagemaps --

    /// <summary>Create or extend one <c>&lt;imagemap&gt;</c>: a new sheet (with a full-image marker or
    /// a single cell), or an added cell inside an existing sheet. Returns true when it changed.</summary>
    public static bool UpsertImageMap(Tp1Level doc, Tp1ImageMapEdit m)
    {
        var root = doc.Root;
        var existing = FindNamed(root, "imagemap", m.Name);
        if (existing is null)
        {
            string inner = m.FullImage || string.IsNullOrEmpty(m.CellName)
                ? "<fullimagecell />"
                : CellFragment(m);
            Tp1Edit.InsertRaw(root,
                $"<imagemap name=\"{Xml(m.Name)}\" filename=\"{Xml(m.Filename)}\" alwaysneeded=\"true\" " +
                $"width=\"{m.Width}\" height=\"{m.Height}\">{inner}</imagemap>");
            return true;
        }

        // Existing sheet: append a cell (or a full-image marker) unless one of that name is already
        // there, leaving the map's other cells untouched.
        if (!m.FullImage && m.CellName is { Length: > 0 })
        {
            if (CellExists(existing, m.CellName)) return false;
            Tp1Edit.InsertRaw(existing, CellFragment(m));
            return true;
        }
        if (m.FullImage && existing.Child("fullimagecell") is null)
        {
            Tp1Edit.InsertRaw(existing, "<fullimagecell />");
            return true;
        }
        return false;
    }

    /// <summary>Remove the named <c>&lt;imagemap&gt;</c> block.</summary>
    public static bool RemoveImageMap(Tp1Level doc, string name) => RemoveChild(doc.Root, "imagemap", name);

    // ---------------------------------------------------------------- strings --
    //
    // The friendly in-game name of an object type is NOT an entitydef attribute — the engine looks the
    // def's name up in assets/strings.xml, an Excel-spreadsheet XML export whose rows are
    //   <Row><Cell ss:StyleID="s21"><Data ss:Type="String">KEY</Data></Cell>
    //        <Cell ss:StyleID="s21"><Data ss:Type="String">DISPLAY</Data></Cell></Row>
    // The verbatim tokenizer parses these elements (the text between <Data> tags is carried untouched as
    // inter-element data), but it does not model text content, so a row is located and edited on the raw
    // buffer. A created row is appended after the table's last <Row> and a removed one takes its leading
    // newline + indent with it, so add + remove returns the file to its original bytes (I1 / I3).

    /// <summary>Read the friendly name the string table gives <paramref name="key"/>, or null when it
    /// has no row. Used to prefill the panel's shown-name field when editing an existing type.</summary>
    public static string? ReadString(Tp1Level doc, string key)
    {
        var loc = FindStringRow(doc.Text, key);
        if (loc is null) return null;
        var (rowStart, rowEnd, _) = loc.Value;
        return ReadDisplayInRow(doc.Text, rowStart, rowEnd);
    }

    /// <summary>Create or update the string-table row that names <paramref name="key"/> as
    /// <paramref name="display"/>. A missing row is appended; an existing one rewrites only its display
    /// cell. Returns true when the document changed (false when the row already read exactly so).</summary>
    public static bool UpsertString(Tp1Level doc, string key, string display)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (string.IsNullOrEmpty(display)) display = key;
        string nl = DetectNewLine(doc.Text);
        var loc = FindStringRow(doc.Text, key);
        if (loc is { } r)
        {
            if (string.Equals(ReadDisplayInRow(doc.Text, r.rowStart, r.rowEnd), display, StringComparison.Ordinal))
                return false;
            doc.Replace(r.rowStart, r.rowEnd - r.rowStart, RowBody(key, display, nl));
            return true;
        }
        int table = doc.Text.IndexOf("</Table>", StringComparison.Ordinal);
        if (table < 0) return false;
        int last = doc.Text.LastIndexOf("</Row>", table, StringComparison.Ordinal);
        if (last < 0) return false;
        last += "</Row>".Length;
        doc.Replace(last, 0, nl + "   " + RowBody(key, display, nl));
        return true;
    }

    /// <summary>Remove the string-table row for <paramref name="key"/> (with the newline + indent that
    /// introduced it). True when a row was present and taken out.</summary>
    public static bool RemoveString(Tp1Level doc, string key)
    {
        var loc = FindStringRow(doc.Text, key);
        if (loc is not { } r) return false;
        int start = r.rowStart;
        while (start > 0 && doc.Text[start - 1] == ' ') start--;         // the row's leading indent
        if (start > 0 && doc.Text[start - 1] == '\n') start--;           // the newline before it
        if (start > 0 && doc.Text[start - 1] == '\r') start--;           // a CRLF's carriage return
        doc.Replace(start, r.rowEnd - start, "");
        return true;
    }

    // A single <Row>...</Row> block starting exactly at "<Row>" and ending just past "</Row>", built
    // with the shipped table's own indentation (4 spaces on the cells, 3 before the close tag).
    private static string RowBody(string key, string display, string nl) =>
        "<Row>" + nl +
        "    <Cell ss:StyleID=\"s21\"><Data ss:Type=\"String\">" + Xml(key) + "</Data></Cell>" + nl +
        "    <Cell ss:StyleID=\"s21\"><Data ss:Type=\"String\">" + Xml(display) + "</Data></Cell>" + nl +
        "   </Row>";

    // Locate the row whose FIRST data cell is exactly <paramref name="key"/>. Returns the byte range of
    // the <Row>..</Row> block plus the offset of that key cell's opening tag (so the caller can tell the
    // key cell from a display cell that happens to read the same). Null when there is no such row.
    private static (int rowStart, int rowEnd, int keyCell)? FindStringRow(string text, string key)
    {
        string needle = ">" + Xml(key) + "</Data>";
        int from = 0;
        while (true)
        {
            int k = text.IndexOf(needle, from, StringComparison.Ordinal);
            if (k < 0) return null;
            int cellOpen = text.LastIndexOf("<Data", k, StringComparison.Ordinal);
            int rowOpen = text.LastIndexOf("<Row>", k, StringComparison.Ordinal);
            int closeBefore = rowOpen < 0 ? -1
                : text.IndexOf("</Data>", rowOpen, Math.Max(0, k - rowOpen), StringComparison.Ordinal);
            // It is the key cell only when this <Data> is the first one closed in its row: an earlier
            // </Data> between the <Row> and here would mean we matched a display cell, not the name.
            if (cellOpen >= 0 && rowOpen >= 0 && (closeBefore < 0 || closeBefore >= k))
            {
                int rEnd = text.IndexOf("</Row>", k, StringComparison.Ordinal);
                if (rEnd < 0) return null;
                return (rowOpen, rEnd + "</Row>".Length, cellOpen);
            }
            from = k + needle.Length;
        }
    }

    // The display text of a row: the characters inside the SECOND <Data>..</Data> of the block. Empty
    // when the row is malformed (fewer than two data cells).
    private static string ReadDisplayInRow(string text, int rowStart, int rowEnd)
    {
        int first = text.IndexOf("</Data>", rowStart, rowEnd - rowStart, StringComparison.Ordinal);
        if (first < 0) return "";
        int open = text.IndexOf("<Data", first, rowEnd - first, StringComparison.Ordinal);
        if (open < 0) return "";
        int gt = text.IndexOf('>', open, rowEnd - open);
        if (gt < 0) return "";
        int close = text.IndexOf("</Data>", gt + 1, rowEnd - gt - 1, StringComparison.Ordinal);
        if (close < 0) return "";
        return text.Substring(gt + 1, close - gt - 1);
    }

    private static string DetectNewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    // --------------------------------------------------------------- helpers --

    private static string EntityDefFragment(Tp1EntityDefEdit e)
    {
        var sb = new StringBuilder();
        sb.Append("<entitydef name=\"").Append(Xml(e.Name))
          .Append("\" imagemap=\"").Append(Xml(e.ImageMap)).Append('"');
        if (e.Cell is { Length: > 0 }) sb.Append(" cell=\"").Append(Xml(e.Cell)).Append('"');
        sb.Append(" aspectratio=\"").Append(N(e.AspectRatio))
          .Append("\" areacoverage=\"").Append(N(e.AreaCoverage)).Append('"');
        if (e.EffectR is double er) sb.Append(" effectr=\"").Append(N(er)).Append('"');
        if (e.EffectG is double eg) sb.Append(" effectg=\"").Append(N(eg)).Append('"');
        if (e.EffectB is double eb) sb.Append(" effectb=\"").Append(N(eb)).Append('"');
        if (e.EffectA is double ea) sb.Append(" effecta=\"").Append(N(ea)).Append('"');
        if (e.Shapes.Count == 0) { sb.Append(" />"); return sb.ToString(); }
        sb.Append('>').Append(ShapesFragment(e.Shapes)).Append("</entitydef>");
        return sb.ToString();
    }

    private static string ShapesFragment(List<object> shapes)
    {
        var sb = new StringBuilder("<intersectionshapes>");
        foreach (var s in shapes)
        {
            if (s is Tp1Circle c)
                sb.Append("<circle posx=\"").Append(N6(c.PosX)).Append("\" posy=\"").Append(N6(c.PosY))
                  .Append("\" radius=\"").Append(N6(c.Radius)).Append("\" />");
            else if (s is Tp1Rect r)
                sb.Append("<rotrect posx=\"").Append(N6(r.PosX)).Append("\" posy=\"").Append(N6(r.PosY))
                  .Append("\" width=\"").Append(N6(r.Width)).Append("\" height=\"").Append(N6(r.Height))
                  .Append("\" angle=\"").Append(N6(r.Angle)).Append("\" />");
        }
        return sb.Append("</intersectionshapes>").ToString();
    }

    private static string CellFragment(Tp1ImageMapEdit m) =>
        $"<cell name=\"{Xml(m.CellName!)}\" x1=\"{m.X1}\" y1=\"{m.Y1}\" x2=\"{m.X2}\" y2=\"{m.Y2}\" />";

    private static bool CellExists(Tp1Node map, string cellName)
    {
        foreach (var c in map.Kids("cell"))
            if (string.Equals(c.Get("name"), cellName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Tp1Node? FindNamed(Tp1Node root, string tag, string name)
    {
        foreach (var c in root.Kids(tag))
            if (string.Equals(c.Get("name") ?? c.Get("id"), name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    private static bool RemoveChild(Tp1Node root, string tag, string name)
    {
        var n = FindNamed(root, tag, name);
        return n is not null && Tp1Edit.Remove(n);
    }

    // Escape the characters that would end an attribute value or a tag; the tables are plain names
    // so this is a safety net, matching the codec's own Esc behaviour for quoted values.
    private static string Xml(string? v) => (v ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    // Six fraction digits, exactly as the base editor rounds a normalised collision value.
    private static string N6(double v) => v.ToString("F6", CultureInfo.InvariantCulture);
}
