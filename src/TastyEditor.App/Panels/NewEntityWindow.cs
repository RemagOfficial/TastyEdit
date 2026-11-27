using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TastyEditor.Core.Levels;

namespace TastyEditor.App.Panels;

/// <summary>Modal "add entity" picker (S-D10).</summary>
///
/// <para>Spawning needs one decision from the author: the TYPE. Position comes from wherever they
/// clicked, and the record's bytes are not a choice at all — an entity body is read by the prefab's
/// own <c>readBinary</c>, so the only legal body for a type is a record the shipped game already has.
/// <see cref="EntitySpawn"/> copies one out of the install corpus; this window is the guard rail that
/// stops the author from picking a type that corpus cannot supply, because the engine would load the
/// level and silently drop the entity (see S-D9).</para>
///
/// <para>So the box is editable (a level's own types, and future modded types, are typed, not clicked)
/// but <see cref="EntityCatalogue"/> decides what is allowed: an unknown name greys <b>Add</b> and the
/// hint lists same-stem types instead. The rule lives in Core, and the hint quotes the same
/// <see cref="EntityCatalogue.Nearest"/> the refusal message uses, so the two cannot drift.</para>
public sealed class NewEntityWindow : Window
{
    private readonly EntityCatalogue _cat;
    private readonly ComboBox _type = new()
    {
        IsEditable = true,
        IsTextSearchEnabled = false,
        StaysOpenOnEdit = true,
        MaxDropDownHeight = 320,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly ComboBox _layer = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private TextBlock _lands = new();

    /// <summary>The landing line, restated whenever the level changes so it never lies about where
    /// the entity will appear (f4/f5 chain the position off the section's last record, so the
    /// absolute point is what the author cares about, not the delta that gets written).</summary>
    private void SayLands() => _lands.Text = $"Lands at ({_x:0.#}, {_y:0.#}) on Level {Layer + 1}.";
    private readonly Button _ok;
    private readonly double _x, _y;

    /// <summary>The accepted type name (trimmed), valid only after <c>DialogResult == true</c>.</summary>
    public string TypeName { get; private set; } = "";

    /// <summary>The level (the editor's own term for a layer) the entity is added to.</summary>
    public int Layer { get; private set; }

    public NewEntityWindow(EntityCatalogue? cat, int layerCount, int layer, double x, double y, string? prefill)
    {
        _cat = cat ?? EntityCatalogue.Empty;
        _x = x; _y = y;
        Layer = Math.Clamp(layer, 0, Math.Max(0, layerCount - 1));

        Title = "Add entity";
        Width = 470;
        Height = 330;      // fixed: the hint line re-wraps as the type changes, and a dialog that
                           // resizes under the typing caret reads as a glitch
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // A Window subclass does not pick up the implicit WindowStyle, so without this it keeps the
        // default white background while its TextBlock labels read the app-wide light ink -
        // light-on-white. Point the chrome at the theme brushes by key so it follows a live toggle.
        Theme.Ref(this, Window.BackgroundProperty, Theme.KWindow);
        Theme.Ref(this, Window.ForegroundProperty, Theme.KText);
        Theme.StyleCombo(_type);
        Theme.StyleCombo(_layer);

        var form = new StackPanel { Margin = new Thickness(12) };
        form.Children.Add(Heading("Entity type"));

        _type.Text = prefill ?? "";
        // An editable ComboBox has no TextChanged of its own: the caret lives in the TextBox part it
        // hosts, whose TextChangedEvent is a routed event, so handling it on the combo catches it.
        _type.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Refilter()));
        form.Children.Add(_type);
        form.Children.Add(Helper(_cat.IsEmpty
            ? "No shipped levels were found for this install, so any credible type name is allowed."
            : $"Type the entity's type name. {_cat.Count} types are defined by this game's shipped levels; "
              + "any other name will be dropped silently when the game loads the level."));

        if (layerCount > 1)
        {
            form.Children.Add(Heading("Level"));
            for (int i = 0; i < layerCount; i++) _layer.Items.Add($"Level {i + 1}");
            _layer.SelectedIndex = Layer;
            _layer.SelectionChanged += (_, _) =>
            {
                Layer = Math.Max(0, _layer.SelectedIndex);
                SayLands();
                UpdateState();
            };
            form.Children.Add(_layer);
        }
        _lands = Helper("");
        SayLands();
        form.Children.Add(_lands);
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
        cancel.Click += (_, _) => Close();
        btns.Children.Add(_ok);
        btns.Children.Add(cancel);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btns, Dock.Bottom);
        root.Children.Add(btns);
        root.Children.Add(form);
        Content = root;

        Refilter();
        Loaded += (_, _) => { _type.Focus(); Keyboard.Focus(_type); };
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

    /// <summary>Refill the dropdown with the types matching what has been typed (a few dozen at most —
    /// a whole install's catalogue is ~1 000 names, which no one scrolls), then re-decide the OK
    /// button. Filtering is substring, so "chair" finds <c>armchair_dip1</c>.</summary>
    private void Refilter()
    {
        string t = _type.Text.Trim();
        IEnumerable<string> q = _cat.Names;
        if (t.Length >= 2) q = q.Where(n => n.Contains(t, StringComparison.OrdinalIgnoreCase));
        else if (t.Length == 1) q = q.Where(n => n.StartsWith(t, StringComparison.OrdinalIgnoreCase));
        _type.ItemsSource = q.Take(80).ToList();
        UpdateState();
    }

    private void UpdateState()
    {
        string name = _type.Text.Trim();
        bool knows = _cat.Knows(name);
        bool typed = _cat.TypedRecords;         // TP2: curated names, no underscore rule
        bool credible = EntitySpawn.NameOk(name, typed);
        bool authority = _cat.Count >= 8;              // a thin corpus proves too little to refuse

        if (name.Length == 0)
        {
            Say("Pick or type the entity type to add.", false);
            _ok.IsEnabled = false;
            return;
        }
        if (!credible)
        {
            Say(typed
                ? "A type name must be 1-63 plain characters \u2014 this game names its entities like \u201clava\u201d or \u201ctree_oak\u201d."
                : "A type name must be 4-63 plain characters and contain an underscore \u2014 the game's "
                  + "levels only ever use names like that.", false);
            _ok.IsEnabled = false;
            return;
        }
        if (authority && !knows)
        {
            Say($"\u201c{name}\u201d has no entity definition in this game, so it would be written and then "
                + $"dropped on load. Try: {string.Join(", ", _cat.Nearest(name))}", true);
            _ok.IsEnabled = false;
            return;
        }
        if (knows && _cat.ProtoFor(name)?.Raw is not null)
        {
            Say($"Ready. {name} starts as a copy of one that already ships in this game "
                + $"(from {_cat.ProtoFor(name)!.SourceLevel}); every field stays editable afterwards.", false);
            _ok.IsEnabled = true;
            return;
        }
        if (knows)
        {
            // TP2: typed records, so no byte template exists and none is needed.
            Say($"Ready. {name} is added with the field defaults this level's own entities carry.", false);
            _ok.IsEnabled = true;
            return;
        }
        Say($"No definition list for this install: \u201c{name}\u201d will be written as typed. If the game "
            + "has no such entity, it will simply not appear in it.", false);
        _ok.IsEnabled = true;
    }

    private void Say(string text, bool bad)
    {
        _hint.Text = text;
        _hint.Foreground = bad ? Brushes.Firebrick : Brushes.DarkGreen;
    }

    private void Accept()
    {
        string name = _type.Text.Trim();
        if (!EntitySpawn.NameOk(name, _cat.TypedRecords) || (_cat.Count >= 8 && !_cat.Knows(name))) { UpdateState(); return; }
        TypeName = name;
        DialogResult = true;
        Close();
    }
}
