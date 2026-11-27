using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TastyEditor.Core.Assets;
using TastyEditor.Core.Levels;

namespace TastyEditor.App.Panels;

/// <summary>The Tasty Planet 1 entity authoring panel (R7). A TP1 object type lives in the game's
/// entity table: it names a sprite (an image map and optional cell), a collision set (circles /
/// rectangles), a colour tint and a couple of sizing numbers. This window lets the author create a
/// brand-new type from an image file they pick, edit any existing type, or remove one — the C# twin of
/// the base editor's entity creator. Everything is written back through <see cref="Tp1Tables"/>, so a
/// stock table keeps its original bytes apart from the one entry edited. Opening is gated to TP1 only.</summary>
///
/// <para>The collision editor works like a picture editor: pick a shape tool and drag straight on the
/// picture to create a circle or a box, then switch to the arrow to click a shape and drag it or its
/// corner handles to move / resize it, or hit the eraser to remove one. The middle mouse button (or the
/// hand tool) pans the picture and the wheel zooms, so a shape can be checked closely. The whole set can
/// be cleared at once. Collision shapes are held in image-pixel space for editing and converted to the
/// game's normalised form only on save.</para>
///
/// <para>Deleting a type that shipped with the game prompts once via the owner (with a "stop warning me"
/// box it remembers); editing any type, and deleting one added this session, happens silently.</para>
public sealed class Tp1EntityWindow : Window
{
    /// <summary>The image work behind a new / retyped entity: a file to place in the game's art tree and
    /// the sheet entry that points at it. Null when the author only changed collision / tint / sizing on
    /// a type that already has art.</summary>
    public sealed class ArtJob
    {
        public string SourceFile = "";             // the file the author picked
        public readonly List<string> DestDirs = new();  // graphics sub-folders to copy into
        public string FilenameRel = "";            // what goes into the sheet's filename (sub/base)
        public string MapName = "";                // the image map entry name
        public int W, H;                           // source pixel size
        public bool FullImage;                     // whole sheet vs a cropped cell
        public string? CellName;                   // the cell name (= entity name) when cropped
        public int X1, Y1, X2, Y2;                 // cell rect, source pixels
    }

    private readonly Tp1SpriteResolver? _resolver;
    private readonly Tp1GraphicsRoot _root;
    private readonly Func<string, Tp1EntityDefEdit?> _load;
    private readonly Func<Tp1EntityDefEdit, ArtJob?, bool> _apply;
    private readonly Func<string, bool> _remove;
    private readonly Func<string, bool> _isOriginal;

    private readonly ListBox _types = new() { MinHeight = 120 };
    private readonly TextBox _name = new();
    private readonly TextBox _disp = new();
    private readonly TextBox _map = new();
    private readonly TextBox _asp = new() { Text = "1" };
    private readonly TextBox _cov = new() { Text = "1" };
    private readonly TextBox _r = new() { Text = "1" }, _g = new() { Text = "1" }, _b = new() { Text = "1" }, _a = new() { Text = "1" };
    private readonly ComboBox _dest = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _full = new() { Content = "Use the whole image as one sprite (no crop)", IsChecked = true };
    private readonly ShapeEditor _editor = new();
    private readonly TextBlock _zoomLbl = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.8 };

    private BitmapSource? _bmp;                      // current preview pixels (source or resolved sheet)
    private string _pickedFile = "";                 // author's new image, when one was chosen
    private bool _loadingSelection;

    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

    public Tp1EntityWindow(
        Tp1SpriteResolver? resolver,
        Tp1GraphicsRoot root,
        Func<string, Tp1EntityDefEdit?> load,
        Func<Tp1EntityDefEdit, ArtJob?, bool> apply,
        Func<string, bool> remove,
        Func<string, bool> isOriginal)
    {
        _resolver = resolver; _root = root; _load = load; _apply = apply; _remove = remove; _isOriginal = isOriginal;

        Title = "Entity types";
        Width = 980; Height = 700;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Ref(this, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(this, Window.ForegroundProperty, Theme.KText);
        Theme.StyleBox(_types);
        Theme.StyleCombo(_dest);
        foreach (var d in new[] { "New graphics", "Old graphics", "Both" }) _dest.Items.Add(d);
        _dest.SelectedIndex = 0;

        // ---- left: the list of defined types, with new / delete ----
        var listPanel = new DockPanel { LastChildFill = true };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        btnRow.Children.Add(Push("New type", OnNew));
        btnRow.Children.Add(Push("Delete", OnDelete));
        DockPanel.SetDock(btnRow, Dock.Bottom);
        listPanel.Children.Add(btnRow);
        listPanel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _types,
        });
        _types.SelectionChanged += (_, _) => { if (!_loadingSelection) LoadInto(_types.SelectedItem as string); };

        // ---- centre: tool palette + editor surface ----
        _editor.Note = (t, bad) => Say(t, bad);
        _editor.ShapesChanged = () => Say(_editor.Shapes.Count == 1
            ? "1 collision shape. Drag its handles to adjust, or the eraser to remove it."
            : $"{_editor.Shapes.Count} collision shapes.");
        _editor.ViewChanged = () => _zoomLbl.Text = (int)Math.Round(_editor.ZoomPercent * 100) + "%";
        _full.Checked += (_, _) => { _editor.ShowCrop = false; _editor.InvalidateVisual(); };

        var tools = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        tools.Children.Add(Tool("Edit", ToolMode.Edit));
        tools.Children.Add(Tool("Circle", ToolMode.Circle));
        tools.Children.Add(Tool("Box", ToolMode.Box));
        tools.Children.Add(Tool("Crop", ToolMode.Crop));
        tools.Children.Add(Tool("Hand", ToolMode.Pan));
        tools.Children.Add(Tool("Erase", ToolMode.Erase));

        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        zoomRow.Children.Add(Push("Fit", (_, _) => _editor.FitToView()));
        zoomRow.Children.Add(Push("100%", (_, _) => _editor.SetZoom(1.0)));
        zoomRow.Children.Add(Push("−", (_, _) => _editor.ZoomBy(1 / 1.25)));
        zoomRow.Children.Add(Push("+", (_, _) => _editor.ZoomBy(1.25)));
        zoomRow.Children.Add(_zoomLbl);
        var editRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 4) };
        editRow.Children.Add(Push("Delete shape", (_, _) => DeleteSelected()));
        editRow.Children.Add(Push("Clear shapes", (_, _) => { _editor.ClearShapes(); }));
        var topRow = new DockPanel { LastChildFill = false };
        topRow.Children.Add(zoomRow); topRow.Children.Add(editRow);

        var surface = new Border
        {
            Child = _editor,
            MinHeight = 340,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
        };
        Theme.Ref(surface, Border.BorderBrushProperty, Theme.KBorder);
        var canvasHost = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(topRow, Dock.Top);
        canvasHost.Children.Add(tools);
        canvasHost.Children.Add(topRow);
        canvasHost.Children.Add(surface);

        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        pickRow.Children.Add(Push("Choose image…", OnPickImage));
        var previewPanel = new StackPanel();
        previewPanel.Children.Add(pickRow);
        previewPanel.Children.Add(canvasHost);
        previewPanel.Children.Add(_full);

        // ---- right: fields ----
        var form = new StackPanel();
        form.Children.Add(Label("Type name")); form.Children.Add(_name);
        form.Children.Add(Label("Shown name (in game)")); form.Children.Add(_disp);
        form.Children.Add(Label("Image map name")); form.Children.Add(_map);
        form.Children.Add(Label("Where to save the image")); form.Children.Add(_dest);
        form.Children.Add(Label("Aspect ratio")); form.Children.Add(_asp);
        form.Children.Add(Label("Area coverage")); form.Children.Add(_cov);
        form.Children.Add(Label("Tint  red / green / blue / alpha (0..1)"));
        form.Children.Add(Row4(_r, _g, _b, _a));

        var save = Push("Save type", OnSave);
        save.IsDefault = true;
        var close = Push("Close", (_, _) => Close());
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(save); actions.Children.Add(close);

        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(form);
        right.Children.Add(actions);

        var center = new DockPanel { LastChildFill = true };
        var help = new TextBlock
        {
            Text = "Pick the picture, choose a shape tool and drag it on. Use the arrow to move / resize, " +
                   "the eraser or Delete shape to remove one. Wheel zooms, middle-drag pans.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Opacity = 0.8,
        };
        DockPanel.SetDock(help, Dock.Top);
        center.Children.Add(help);
        center.Children.Add(previewPanel);

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        var c0 = new Border { Child = listPanel }; Grid.SetColumn(c0, 0);
        var c2 = new Border { Child = center }; Grid.SetColumn(c2, 2);
        var c4 = new Border { Child = right }; Grid.SetColumn(c4, 4);
        cols.Children.Add(c0); cols.Children.Add(c2); cols.Children.Add(c4);

        var statusDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(cols, Dock.Top);
        statusDock.Children.Add(cols);
        statusDock.Children.Add(_status);

        var host = new Border { Child = statusDock, Padding = new Thickness(12) };
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = host };

        ReloadList(null);
    }

    // A tool-palette radio button: selecting it switches the editor's active tool.
    private RadioButton Tool(string text, ToolMode mode)
    {
        var rb = new RadioButton
        {
            Content = text,
            GroupName = "tool",
            Margin = new Thickness(0, 0, 10, 0),
            IsChecked = mode == ToolMode.Edit,
        };
        rb.Checked += (_, _) => _editor.Tool = mode;
        return rb;
    }

    // -------------------------------------------------------------- list --

    private void ReloadList(string? select)
    {
        _loadingSelection = true;
        _types.Items.Clear();
        IEnumerable<string> names = _resolver?.EntityNames ?? Enumerable.Empty<string>();
        foreach (var n in names.OrderBy(s => s, StringComparer.Ordinal)) _types.Items.Add(n);
        if (select is not null)
            for (int i = 0; i < _types.Items.Count; i++)
                if (string.Equals(_types.Items[i] as string, select, StringComparison.Ordinal)) { _types.SelectedIndex = i; break; }
        _loadingSelection = false;
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        ClearFields();
        _name.Focus();
        Say("Fill in a name and pick an image to create a new type.");
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        string? who = _types.SelectedItem as string;
        if (string.IsNullOrEmpty(who)) { Say("Choose a type to delete first.", true); return; }
        if (!_isOriginal(who) || OwnerConfirm(who))
        {
            if (_remove(who)) { ReloadList(null); ClearFields(); Say($"Removed {who}."); }
            else Say($"Could not remove {who}.", true);
        }
    }

    private void LoadInto(string? who)
    {
        if (string.IsNullOrEmpty(who)) return;
        var e = _load(who);
        if (e is null) { Say($"No definition found for {who}.", true); return; }
        ClearFields();
        _name.Text = e.Name; _map.Text = e.ImageMap;
        _disp.Text = ReadCurrentDisplay(e.Name) ?? e.Name;
        _asp.Text = Fmt(e.AspectRatio); _cov.Text = Fmt(e.AreaCoverage);
        _r.Text = Fmt(e.EffectR ?? 1); _g.Text = Fmt(e.EffectG ?? 1);
        _b.Text = Fmt(e.EffectB ?? 1); _a.Text = Fmt(e.EffectA ?? 1);
        _full.IsChecked = string.IsNullOrEmpty(e.Cell);
        _pickedFile = "";
        // Show the type's current sheet if it can be found on disk, then convert its collision shapes
        // from the game's normalised form into editable picture pixels.
        var file = ResolveSheet(e.ImageMap);
        if (file is not null) SetPreview(file); else { _bmp = null; _editor.SetImage(null); }
        LoadShapes(e.Shapes);
        _editor.InvalidateVisual();
        Say($"{who}: {_editor.Shapes.Count} collision shape(s). Drag to move / resize, then press Save type.");
    }

    // ----------------------------------------------------------- image --

    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Choose an image for this entity",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tga)|*.png;*.jpg;*.jpeg;*.bmp;*.tga|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != true) return;
        _pickedFile = ofd.FileName;
        _full.IsChecked = true;
        _editor.ClearShapes();
        SetPreview(ofd.FileName);
        Say("Image loaded. Choose a shape tool and drag it on the picture.");
    }

    private void SetPreview(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            _bmp = bmp;
            _editor.SetImage(bmp);
        }
        catch { _bmp = null; _editor.SetImage(null); Say("That image could not be read.", true); }
    }

    // Find a sheet PNG/JPG on disk from an image map filename (the last path segment, any extension).
    private string? ResolveSheet(string filenameRel)
    {
        if (string.IsNullOrEmpty(filenameRel)) return null;
        string baseName = Path.GetFileNameWithoutExtension(filenameRel.Replace('/', Path.DirectorySeparatorChar));
        if (baseName.Length == 0) return null;
        foreach (var f in SafeWalk(_root.GraphicsDir))
            if (string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase)
                && Array.IndexOf(ImageExts, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                return f;
        return null;
    }

    private static IEnumerable<string> SafeWalk(string dir)
    {
        var list = new List<string>();
        if (!Directory.Exists(dir)) return list;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                list.Add(f);
        }
        catch { }
        return list;
    }

    // The friendly name the game's string table currently gives this type (read straight from
    // strings.xml), or null when there is no table / row. Used only to prefill the shown-name field.
    private string? ReadCurrentDisplay(string name)
    {
        try
        {
            var sp = _root.FindTable(Tp1Tables.StringsFile);
            return sp is null ? null : Tp1Tables.ReadString(Tp1Tables.Load(sp), name);
        }
        catch { return null; }
    }

    // ---------------------------------------------------- shape <-> normalised --

    // The editor's pixel-space shapes converted to the game's normalised form (Y flipped, size over the
    // image extent) for writing. Empty when there is no picture to measure against.
    private List<object> NormFromShapes()
    {
        var list = new List<object>();
        if (_bmp is null) return list;
        double iw = _bmp.PixelWidth, ih = _bmp.PixelHeight;
        foreach (var s in _editor.Shapes)
        {
            if (s is WCircle c)
                list.Add(new Tp1Circle(R6((c.Cx - iw / 2) / iw), R6((ih / 2 - c.Cy) / ih), R6(c.R / ih)));
            else if (s is WRect r)
                list.Add(new Tp1Rect(R6((r.Cx - iw / 2) / iw), R6((ih / 2 - r.Cy) / ih), R6(r.W / iw), R6(r.H / ih), R6(r.Angle)));
        }
        return list;
    }

    // The game's normalised shapes converted into editable picture pixels (inverse of NormFromShapes).
    private void LoadShapes(IEnumerable<object> shapes)
    {
        _editor.Shapes.Clear();
        _editor.Selected = -1;
        if (_bmp is null) return;
        double iw = _bmp.PixelWidth, ih = _bmp.PixelHeight;
        foreach (var o in shapes)
        {
            if (o is Tp1Circle c)
                _editor.Shapes.Add(new WCircle { Cx = c.PosX * iw + iw / 2, Cy = ih / 2 - c.PosY * ih, R = c.Radius * ih });
            else if (o is Tp1Rect r)
                _editor.Shapes.Add(new WRect { Cx = r.PosX * iw + iw / 2, Cy = ih / 2 - r.PosY * ih, W = r.Width * iw, H = r.Height * ih, Angle = r.Angle });
        }
    }

    private void DeleteSelected()
    {
        if (_editor.DeleteSelected())
            Say($"{_editor.Shapes.Count} collision shape(s).");
        else
            Say("Click a shape first (with the Edit tool), then delete it.", true);
    }

    // ------------------------------------------------------------- save --

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string nm = _name.Text.Trim();
        if (nm.Length == 0) { Say("The type needs a name.", true); return; }
        string mapName = _map.Text.Trim();
        string disp = _disp.Text.Trim();
        if (disp.Length == 0) disp = nm;   // the shown name defaults to the type name so it is never blank in game

        // Editing a type (vanilla or not) is allowed directly; the owner backs up the table first.
        var edit = new Tp1EntityDefEdit
        {
            Name = nm,
            ImageMap = mapName,
            Display = disp,
            Cell = _full.IsChecked == true ? null : nm,
            AspectRatio = ParseD(_asp.Text, 1),
            AreaCoverage = ParseD(_cov.Text, 1),
            EffectR = ParseDOrNull(_r.Text),
            EffectG = ParseDOrNull(_g.Text),
            EffectB = ParseDOrNull(_b.Text),
            EffectA = ParseDOrNull(_a.Text),
        };
        foreach (var s in NormFromShapes()) edit.Shapes.Add(s);

        ArtJob? art = null;
        if (_pickedFile.Length > 0)
        {
            if (_bmp is null) { Say("The chosen image could not be measured.", true); return; }
            art = BuildArt(nm, mapName);
            if (art is null) return;
            if (edit.ImageMap.Length == 0) edit.ImageMap = art.MapName;
            edit.Cell = art.FullImage ? null : nm;
        }
        else if (edit.ImageMap.Length == 0)
        {
            Say("Give the type an image map name, or choose a new image.", true);
            return;
        }

        if (_apply(edit, art))
        {
            ReloadList(nm);
            Say($"Saved {nm}" + (art is not null ? " with its new image." : "."));
        }
        else Say("Could not save that type.", true);
    }

    private ArtJob? BuildArt(string entName, string mapName)
    {
        if (_bmp is null) return null;
        string baseName = Path.GetFileNameWithoutExtension(_pickedFile);
        // The game resolves an image map's filename by bare name across the whole graphics tree (every
        // shipped map is written that way, its PNG living in a sub-folder), so the entry must carry just
        // the file's own name — never a folder path, or the texture is not found and the level crashes.
        // Refuse up front if that name already belongs to another game image (it would shadow it).
        foreach (var f in SafeWalk(_root.GraphicsDir))
            if (string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFullPath(f), Path.GetFullPath(_pickedFile), StringComparison.OrdinalIgnoreCase))
            { Say($"An image named \u201c{baseName}\u201d already exists in the game. Rename your file and pick it again.", true); return null; }

        var dirs = new List<string>();
        string ng = Path.Combine(_root.GraphicsDir, "newgraphics");
        string og = Path.Combine(_root.GraphicsDir, "oldgraphics");
        int mode = _dest.SelectedIndex;   // 0 new, 1 old, 2 both
        if (mode is 0 or 2 && Directory.Exists(ng)) dirs.Add(ng);
        if (mode is 1 or 2 && Directory.Exists(og)) dirs.Add(og);
        if (dirs.Count == 0) dirs.Add(_root.GraphicsDir);

        bool full = _full.IsChecked == true;
        var a = new ArtJob
        {
            SourceFile = _pickedFile,
            FilenameRel = baseName,
            MapName = mapName.Length > 0 ? mapName : baseName,
            W = _bmp.PixelWidth,
            H = _bmp.PixelHeight,
            FullImage = full,
            CellName = full ? null : entName,
        };
        a.DestDirs.AddRange(dirs);
        if (!full)
        {
            Rect cr = _editor.Crop;
            if (_editor.ShowCrop && !cr.IsEmpty)
            {
                a.X1 = (int)Math.Round(cr.X); a.Y1 = (int)Math.Round(cr.Y);
                a.X2 = (int)Math.Round(cr.X + cr.Width); a.Y2 = (int)Math.Round(cr.Y + cr.Height);
            }
            else { a.X1 = 0; a.Y1 = 0; a.X2 = a.W; a.Y2 = a.H; }
        }
        return a;
    }

    // --------------------------------------------------------- warning --

    // Warn once per session before deleting a type that shipped with the game. The owner shows the
    // warning and remembers the "stop warning me" choice; with no owner set the delete proceeds.
    private bool OwnerConfirm(string who) => _confirm?.Invoke(who) ?? true;

    /// <summary>Set by the owner: returns true to proceed with deleting a shipped type. The owner
    /// shows the warning and remembers the "stop warning me" choice.</summary>
    public Func<string, bool>? _confirm;

    // ----------------------------------------------------------- util --

    private static Button Push(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        b.Click += onClick;
        return b;
    }

    private static TextBlock Label(string t) => new() { Text = t, Margin = new Thickness(0, 6, 0, 1), Opacity = 0.85 };

    private static StackPanel Row4(TextBox a, TextBox b, TextBox c, TextBox d)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var t in new[] { a, b, c, d }) { t.Width = 44; t.Margin = new Thickness(0, 0, 6, 0); p.Children.Add(t); }
        return p;
    }

    private void ClearFields()
    {
        _name.Clear(); _disp.Clear(); _map.Clear();
        _asp.Text = "1"; _cov.Text = "1";
        _r.Text = "1"; _g.Text = "1"; _b.Text = "1"; _a.Text = "1";
        _full.IsChecked = true;
        _editor.ClearShapes();
        _editor.Crop = Rect.Empty; _editor.ShowCrop = false;
        _pickedFile = ""; _bmp = null; _editor.SetImage(null);
    }

    private static double R6(double v) => Math.Round(v, 6);
    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private static double ParseD(string s, double d) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
    private static double? ParseDOrNull(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void Say(string text, bool bad = false) { _status.Text = text; _status.Foreground = bad ? Brushes.Firebrick : Brushes.DarkGreen; }
}
