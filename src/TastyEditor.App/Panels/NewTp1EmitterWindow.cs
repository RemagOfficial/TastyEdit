using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TastyEditor.App.Panels;

/// <summary>Modal "new spawner" picker for Tasty Planet 1 (R5).</summary>
///
/// <para>A TP1 spawner is an <c>&lt;emitter&gt;</c>: it makes objects of one type appear, either at a
/// spot, fired as a shot, or along an edge (a side emitter has no position). Two things are the
/// author's choice — the emitter's <b>kind</b> and the <b>object type</b> it releases. Every other
/// setting comes from a spawner the level already has (the record is cloned from it), and stays
/// editable in the inspector afterwards, so nothing here invents bytes.</para>
///
/// <para>The object type is policed against the game's own definitions: a type the game has never
/// defined is read from the file and then dropped by the loader, so the spawner would sit in the
/// level doing nothing. When a definition list is loaded, only a name on it can be added; the hint
/// offers close names otherwise. With no list the field is free text (the author may be modding).</para>
public sealed class NewTp1EmitterWindow : Window
{
    private readonly IReadOnlyCollection<string>? _names;
    private readonly ComboBox _type = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _def = new()
    {
        IsEditable = true,
        IsTextSearchEnabled = false,
        StaysOpenOnEdit = true,
        MaxDropDownHeight = 320,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _ok;
    private readonly double _x, _y;
    private bool _suppress;

    /// <summary>The emitter kind chosen: <c>shot</c> / <c>side</c> / <c>spot</c>.</summary>
    public string EmitterType { get; private set; } = "spot";
    /// <summary>The object type the spawner releases (a name from the game's definitions).</summary>
    public string EntityDef { get; private set; } = "";

    public NewTp1EmitterWindow(IReadOnlyCollection<string>? names, double x, double y, string? prefill)
    {
        _names = names;
        _x = x; _y = y;

        Title = "New spawner";
        Width = 470;
        Height = 320;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Ref(this, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(this, Window.ForegroundProperty, Theme.KText);
        Theme.StyleCombo(_type);
        Theme.StyleCombo(_def);

        var form = new StackPanel { Margin = new Thickness(12) };

        form.Children.Add(Heading("What kind of spawner"));
        foreach (var t in new[] { "spot", "shot", "side" }) _type.Items.Add(t);
        _type.SelectedIndex = 0;
        _type.SelectionChanged += (_, _) => { EmitterType = (string)_type.SelectedItem; UpdateState(); };
        form.Children.Add(_type);
        form.Children.Add(Helper("A spot sits in one place and spawns there; a shot fires across the level; "
            + "a side releases along an edge (it has no position to place)."));

        form.Children.Add(Heading("Object it releases"));
        _def.Text = prefill ?? "";
        _def.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => { if (!_suppress) Refilter(); }));
        form.Children.Add(_def);
        form.Children.Add(Helper(_names is { Count: > 0 }
            ? $"Only the types this game defines can be released — a name it has never used would be "
              + $"dropped when the level loads. {_names.Count} types are defined."
            : "No definition list was found for this install, so any type name is allowed."));

        _hint.Text = $"Spawns at ({_x:0.#}, {_y:0.#}).";
        _hint.Foreground = Brushes.DarkGreen;
        form.Children.Add(_hint);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 4, 12, 12),
        };
        _ok = new Button { Content = "Add", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        _ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        btns.Children.Add(_ok);
        btns.Children.Add(cancel);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btns, Dock.Bottom);
        root.Children.Add(btns);
        root.Children.Add(form);
        Content = root;

        Refilter();
        Loaded += (_, _) => { _def.Focus(); Keyboard.Focus(_def); };
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 2),
    };

    private static TextBlock Helper(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Opacity = 0.72,
        Margin = new Thickness(0, 4, 0, 0),
    };

    /// <summary>Refill the type dropdown with the defined names matching what has been typed (bounded,
    /// so a few hundred names never lag the caret), then re-decide the Add button.</summary>
    private void Refilter()
    {
        if (_names is null) { UpdateState(); return; }
        _suppress = true;
        string t = _def.Text.Trim();
        IEnumerable<string> q = _names;
        if (t.Length >= 2) q = q.Where(n => n.Contains(t, StringComparison.OrdinalIgnoreCase));
        else if (t.Length == 1) q = q.Where(n => n.StartsWith(t, StringComparison.OrdinalIgnoreCase));
        _def.ItemsSource = q.Take(80).ToList();
        _suppress = false;
        UpdateState();
    }

    private void UpdateState()
    {
        EmitterType = _type.SelectedItem as string ?? "spot";
        string name = _def.Text.Trim();
        if (name.Length == 0)
        {
            Say("Pick the object type this spawner releases.", false);
            _ok.IsEnabled = false;
            return;
        }
        bool known = _names?.Contains(name, StringComparer.Ordinal) ?? true;
        if (!known)
        {
            Say($"This game has no \u201c{name}\u201d type, so it would be released and then dropped on load. "
                + $"Try: {Closest(name)}", true);
            _ok.IsEnabled = false;
            return;
        }
        Say($"Ready. A {EmitterType} spawner for {name} is added as a copy of one already in this level; "
            + "every setting stays editable afterwards.", false);
        _ok.IsEnabled = true;
    }

    private string Closest(string name) => _names is null
        ? "(none listed)"
        : string.Join(", ", _names
            .Where(n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                     || n.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(6));

    private void Say(string text, bool bad)
    {
        _hint.Text = text;
        _hint.Foreground = bad ? Brushes.Firebrick : Brushes.DarkGreen;
    }

    private void Accept()
    {
        string name = _def.Text.Trim();
        if (name.Length == 0 || !(_names?.Contains(name, StringComparer.Ordinal) ?? true)) { UpdateState(); return; }
        EntityDef = name;
        DialogResult = true;
        Close();
    }
}
