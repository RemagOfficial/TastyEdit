using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using TastyEditor.Core.Levels;

namespace TastyEditor.App.Panels;

/// <summary>The slice-6 multilevel / goals editor: a modal window over a game's
/// <c>levels/multilevels/*.xml</c>. It lists the ordered stages (name, zoom, position, trigger
/// area) with add / remove / reorder / pick-a-.bin, and edits the level's goal attributes (time
/// limit, victory type, gold / silver / bronze times, …) on the root. Load and Save go through
/// the Core <see cref="MultiLevel"/> port, whose save∘load idempotency the headless check proves;
/// because a multilevel is engine-parsed XML (not a byte-exact .bin), the only file invariant here
/// is that we write valid XML that reloads unchanged. Nothing here touches a loaded level's byte
/// path — this window owns its own document.</summary>
public sealed class MultiLevelWindow : Window
{
    private readonly ListView _list = new();
    private readonly TextBox _pathBox = new() { IsReadOnly = true };
    private readonly ComboBox _files = new() { Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private MultiLevel? _ml;
    private string? _path;

    // Auto-discovery of the multilevel files that reference the level open in the main editor, so
    // the user picks from a dropdown instead of browsing to the right .xml by hand. The stage-name
    // key is the loaded .bin without its extension; the search dir is <levelsDir>/multilevels.
    private readonly string? _stageName;
    private readonly string? _multilevelsDir;
    private readonly Dictionary<string, MultiLevel> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingList;

    public MultiLevelWindow(string? stageName, string? multilevelsDir)
    {
        _stageName = string.IsNullOrWhiteSpace(stageName) ? null : stageName;
        _multilevelsDir = string.IsNullOrWhiteSpace(multilevelsDir) || !System.IO.Directory.Exists(multilevelsDir)
            ? null : multilevelsDir;
        Title = "Multilevel / Goals editor";
        Width = 960;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var goalPanel = new StackPanel();
        var goals = new ScrollViewer
        {
            Content = goalPanel,
            Width = 270,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 8, 0),
        };
        BuildGoals(goalPanel);

        _list.SelectionMode = SelectionMode.Single;
        var gv = new GridView();
        AddCol(gv, "Stage", "Name", 160);
        AddCol(gv, "Zoom", "Zoom", 130);
        AddCol(gv, "Pos X", "PosX", 80);
        AddCol(gv, "Pos Y", "PosY", 80);
        AddCol(gv, "Trigger", "TriggerArea", 70);
        _list.View = gv;

        var ops = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        ops.Children.Add(Button("Add", (_, _) => AddStage()));
        ops.Children.Add(Button("Remove", (_, _) => RemoveStage()));
        ops.Children.Add(Button("Up", (_, _) => Move(-1)));
        ops.Children.Add(Button("Down", (_, _) => Move(1)));
        ops.Children.Add(Button("Pick .bin…", (_, _) => PickBin()));

        var fileRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        var open = Button("Open other\u2026", (_, _) => OpenFile());
        var save = Button("Save", (_, _) => SaveFile());
        var saveAs = Button("Save As\u2026", (_, _) => SaveAs());
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(saveAs, Dock.Right);
        fileRow.Children.Add(open);
        fileRow.Children.Add(saveAs);
        fileRow.Children.Add(save);
        fileRow.Children.Add(_files);
        _files.SelectionChanged += (_, _) => OnMultilevelSelected();
        BuildFileList();

        var right = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(fileRow, Dock.Top);
        DockPanel.SetDock(ops, Dock.Bottom);
        right.Children.Add(fileRow);
        right.Children.Add(ops);
        right.Children.Add(_list);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(goals, 0);
        Grid.SetColumn(right, 1);
        split.Children.Add(goals);
        split.Children.Add(right);

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var close = Button("Close", (_, _) => Close());
        Grid.SetRow(split, 0);
        Grid.SetRow(close, 1);
        root.Children.Add(split);
        root.Children.Add(close);
        Content = root;
    }

    // ---------------------------------------------------------- goals ------

    private readonly Dictionary<string, TextBox> _goalBoxes = new(StringComparer.Ordinal);

    private void BuildGoals(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "Goals / win conditions",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var k in MultiLevel.MlAttrs)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
            var lbl = new TextBlock
            {
                Text = k,
                Width = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = k,
            };
            DockPanel.SetDock(lbl, Dock.Left);
            var box = new TextBox();
            _goalBoxes[k] = box;
            row.Children.Add(lbl);
            row.Children.Add(box);
            panel.Children.Add(row);
        }
    }

    // ------------------------------------------------------ stage rows -----

    private sealed class Row : INotifyPropertyChanged
    {
        public string Key = "";
        public MultiStage? Stage;
        public string Name { get => _name; set { _name = value; On(nameof(Name)); } } string _name = "";
        public string Zoom { get => _zoom; set { _zoom = value; On(nameof(Zoom)); } } string _zoom = "";
        public string PosX { get => _posx; set { _posx = value; On(nameof(PosX)); } } string _posx = "";
        public string PosY { get => _posy; set { _posy = value; On(nameof(PosY)); } } string _posy = "";
        public string TriggerArea { get => _trig; set { _trig = value; On(nameof(TriggerArea)); } } string _trig = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new(p));
    }

    private readonly ObservableCollection<Row> _rows = new();

    // -------------------------------------------------------- operations ---

    // An editable grid cell: a TwoWay-bound TextBox whose edits land straight on the Row (and
    // through the Row's stage reference onto the model). DisplayMemberBinding is read-only, so a
    // cell template is the minimal way to get in-place editing in a ListView/GridView.
    private static void AddCol(GridView gv, string header, string binding, int width) =>
        gv.Columns.Add(new GridViewColumn
        {
            Header = header,
            Width = width,
            CellTemplate = CellTemplate(binding),
        });

    private static DataTemplate CellTemplate(string path)
    {
        var fef = new FrameworkElementFactory(typeof(TextBox));
        fef.SetBinding(TextBox.TextProperty, new Binding(path)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        return new DataTemplate { VisualTree = fef };
    }

    private void AddStage()
    {
        if (_ml is null) return;
        var st = _ml.AddStage($"stage{_ml.Stages.Count + 1}");
        Commit();
        var r = RowFor(st);
        _rows.Add(r);
        _list.SelectedItem = r;
    }

    private void RemoveStage()
    {
        if (_ml is null || _list.SelectedItem is not Row r) return;
        if (r.Stage is not null) _ml.RemoveStage(r.Stage);
        _rows.Remove(r);
        RebuildFromRows();
    }

    private void Move(int delta)
    {
        if (_ml is null || _list.SelectedItem is not Row r) return;
        int i = _rows.IndexOf(r);
        int j = i + delta;
        if (j < 0 || j >= _rows.Count) return;
        _rows.RemoveAt(i);
        _rows.Insert(j, r);
        _list.SelectedIndex = j;
        RebuildFromRows();
    }

    private void PickBin()
    {
        if (_ml is null || _list.SelectedItem is not Row r || r.Stage is null) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Level files (*.bin)|*.bin|All files (*.*)|*.*",
            Title = "Pick stage level",
            InitialDirectory = _path is not null ? System.IO.Path.GetDirectoryName(_path) : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        // The engine references a stage by file name without its extension.
        r.Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        r.Stage.Name = r.Name;
    }

    // ---------------------------------------------------------- io ---------

    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Multilevel XML (*.xml)|*.xml|All files (*.*)|*.*",
            Title = "Open multilevel XML",
            InitialDirectory = _multilevelsDir,
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadFile(dlg.FileName, addToList: true);
    }

    /// <summary>Scan <see cref="_multilevelsDir"/> for multilevel XMLs that reference the current
    /// level as a stage, populate the dropdown (filename shown; the full path in the tooltip), and
    /// auto-select the first. Files are cached so re-selecting one is free and idempotent.</summary>
    private void BuildFileList()
    {
        _loadingList = true;
        _files.Items.Clear();
        _byFile.Clear();
        if (_multilevelsDir is not null && _stageName is not null)
        {
            foreach (var f in System.IO.Directory.GetFiles(_multilevelsDir, "*.xml").OrderBy(x => x))
            {
                try
                {
                    var ml = MultiLevel.Load(f);
                    if (!ReferencesStage(ml, _stageName)) continue;
                    _byFile[f] = ml;
                    var name = System.IO.Path.GetFileName(f);
                    var item = new ComboBoxItem { Content = name, Tag = f, ToolTip = f };
                    _files.Items.Add(item);
                }
                catch { /* a stray / unreadable xml in the folder is simply not offered */ }
            }
        }
        if (_files.Items.Count == 0)
            _files.Items.Add(new ComboBoxItem
            {
                Content = _stageName is null
                    ? "Open a level first, or use “Open other…”"
                    : $"No multilevel references “{_stageName}” — use “Open other…”",
                Tag = null,
                IsEnabled = false,
            });
        _loadingList = false;
        _files.SelectedIndex = 0;
    }

    /// <summary>A multilevel references the level when any stage's <c>name</c> equals the loaded
    /// <c>.bin</c> stem (the engine keys stages by file name without extension).</summary>
    private static bool ReferencesStage(MultiLevel ml, string stageName) =>
        ml.Stages.Any(s => string.Equals(s.Get("name"), stageName, StringComparison.OrdinalIgnoreCase));

    private void OnMultilevelSelected()
    {
        if (_loadingList) return;
        if (_files.SelectedItem is ComboBoxItem { Tag: string path })
            LoadFile(path, addToList: false);
    }

    /// <summary>Load a multilevel into the editor from the cache (dropdown picks) or fresh from
    /// disk (manual open), pointing the model at <paramref name="path"/> so Save writes back.</summary>
    private void LoadFile(string path, bool addToList)
    {
        try
        {
            // Always read fresh from disk (so Save writes back exactly what is on disk + our edits);
            // _byFile only records which files reference the current level for the filter.
            var ml = MultiLevel.Load(path);
            _byFile[path] = ml;
            _path = path;
            _pathBox.Text = path;
            _ml = ml;
            Populate();
            if (addToList && _stageName is not null && ReferencesStage(ml, _stageName))
                AddToFilteredList(path, ml, select: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open multilevel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Insert a manually-opened (referencing) file into the dropdown without rebuilding the list
    // (which would lose the current selection). Keeps filename order stable by re-sorting items.
    private void AddToFilteredList(string path, MultiLevel ml, bool select)
    {
        if (_byFile.ContainsKey(path)) return;
        _byFile[path] = ml;
        _loadingList = true;
        var name = System.IO.Path.GetFileName(path);
        _files.Items.Add(new ComboBoxItem { Content = name, Tag = path, ToolTip = path });
        _loadingList = false;
        if (select) _files.SelectedItem = _files.Items.Cast<ComboBoxItem>().First(i => (string?)i.Tag == path);
    }

    private void SaveFile()
    {
        if (_ml is null || _path is null) { MessageBox.Show(this, "Nothing loaded.", "Save"); return; }
        Commit();
        RebuildFromRows();
        try { _ml.Save(_path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save"); }
    }

    private void SaveAs()
    {
        if (_ml is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Multilevel XML (*.xml)|*.xml",
            Title = "Save multilevel XML",
            FileName = _path is not null ? System.IO.Path.GetFileName(_path) : "multilevel.xml",
        };
        if (dlg.ShowDialog(this) != true) return;
        Commit();
        RebuildFromRows();
        try { _ml.Save(dlg.FileName); _path = dlg.FileName; _pathBox.Text = dlg.FileName; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save"); }
    }

    // ------------------------------------------------------- sync UI -------

    private void Populate()
    {
        if (_ml is null) return;
        _rows.Clear();
        foreach (var st in _ml.Stages) _rows.Add(RowFor(st));
        _list.ItemsSource = _rows;
        foreach (var (k, box) in _goalBoxes)
            box.Text = _ml.GetGoal(k) ?? "";
    }

    private Row RowFor(MultiStage st) => new()
    {
        Key = st.Get("name") ?? "",
        Stage = st,
        Name = st.Get("name") ?? "",
        Zoom = Fmt(st.Zoom),
        PosX = Fmt(st.PosX),
        PosY = Fmt(st.PosY),
        TriggerArea = st.Get("triggerarea") ?? "",
    };

    private static string Fmt(double? v) =>
        v is double d ? d.ToString("R", CultureInfo.InvariantCulture) : "";

    /// <summary>Push the goal text boxes back into the model (blank → drop the attribute).</summary>
    private void Commit()
    {
        if (_ml is null) return;
        foreach (var (k, box) in _goalBoxes)
            _ml.SetGoal(k, string.IsNullOrEmpty(box.Text) ? null : box.Text);
    }

    /// <summary>Rebuild the model's ordered stage list from the current rows, so reorder / remove
    /// (which edit <see cref="_rows"/>) and cell edits (which mutate each <see cref="Row"/>'s
    /// stage) both land in one place.</summary>
    private void RebuildFromRows()
    {
        if (_ml is null) return;
        _ml.Stages.Clear();
        foreach (var r in _rows)
        {
            if (r.Stage is null) continue;
            ApplyRow(r);
            _ml.Stages.Add(r.Stage);
        }
    }

    private void ApplyRow(Row r)
    {
        var st = r.Stage!;
        st.Set("name", r.Name);
        if (TryD(r.Zoom, out double z)) st.Zoom = z;
        st.SetPos(TryD(r.PosX, out double x) ? x : null, TryD(r.PosY, out double y) ? y : null);
        st.SetTriggerArea(TryI(r.TriggerArea, out int t) ? t : null);
    }

    private static bool TryD(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static bool TryI(string s, out int v) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static Button Button(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += onClick;
        return b;
    }
}
