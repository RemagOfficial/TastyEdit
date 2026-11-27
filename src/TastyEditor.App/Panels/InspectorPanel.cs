using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TastyEditor.Core.Levels;
using TastyEditor.Core.Render;

namespace TastyEditor.App.Panels;

/// <summary>The inspector dock for the current viewport selection. It never touches the
/// model itself: it reads the selection through <see cref="SceneProjector"/> and pushes
/// edits back via the <see cref="ApplyPos"/> / <see cref="RequestNext"/> callbacks the
/// shell wires to the viewport. In slice 3 the only editable lanes are the world X / Y of
/// a typed entity/deco (the byte-faithful re-derivable ones); everything else is shown
/// read-only, and raw-span items (f4/f5) report as not yet editable.</summary>
public sealed class InspectorPanel : StackPanel
{
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _x = new();
    private readonly TextBox _y = new();
    private readonly Button _apply = new() { Content = "Apply position", Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _next = new() { Content = "Select next object", Margin = new Thickness(0, 10, 0, 0), ToolTip = "Jump to the next object you can move, centring the view on it." };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextMutedBrush(), Margin = new Thickness(0, 6, 0, 0) };

    // Decoration rotation (D6). A rotatable marker (Tasty Blue / ElementEngine deco) gets a
    // degrees field + its own apply; every other selection hides the whole rotation row.
    private readonly Grid _rotRow = new();
    private readonly TextBox _rot = new();
    private readonly Button _applyRot = new() { Content = "Apply rotation", Margin = new Thickness(0, 2, 0, 0) };
    private SceneCross? _rotBound;

    // Decoration facing (S-D13). The record's two flip words get their own row rather than a place
    // in the TP2-only field panel, because every decoration family carries them (Tasty Blue and the
    // ElementEngine games splice them in their raw span, TP2 re-serialises them from the model).
    // A checkbox commits on click: a toggle is a discrete edit, not a typing burst, so it needs no
    // Apply button and gets its own undo step.
    private readonly StackPanel _flipRow = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly CheckBox _flipX = new() { Content = "Flip X", Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _flipY = new() { Content = "Flip Y", VerticalAlignment = VerticalAlignment.Center };
    private SceneCross? _flipBound;

    private SceneCross? _bound;

    // Water field panel (slice 4). Built once per selected TbWater (never on every Show, so
    // OnEdited's re-Show cannot eat keystrokes); numeric edits commit on focus-loss/Enter.
    private readonly Border _waterBox = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        BorderThickness = new Thickness(1),
        BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 36, 44)),
        Padding = new Thickness(6),
        Visibility = Visibility.Collapsed,
    };
    private readonly Grid _waterGrid = new();
    private readonly Dictionary<string, (WaterFieldKind Kind, FrameworkElement Ctl)> _waterInputs = new();
    private TastyEditor.Core.Levels.TbWater? _boundWater;
    private bool _suppress;

    /// <summary>Raised after a water field is written to the model. The shell refreshes the
    /// viewport rect + marks the document dirty, but must NOT re-Show the inspector (that
    /// would rebuild the very control being typed into).</summary>
    public Action? WaterChanged;

    // Sprite reassignment panel (S5-5). Decorations are editable for every family: TP2 writes a
    // typed tile index, while Tasty Blue and the ElementEngine games rewrite a deco record's
    // fixed sprite head in place (TbEdit / ElemEdit). Entity reassignment is TP2-only — the other
    // families carry an entity's variable-length name inside an opaque raw span with no
    // length-stable splice (deferred to S5-6). The panel just edits a name string and raises
    // ApplySprite — the shell does the lossless model write for whichever family is loaded.
    private readonly Border _spriteBox = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        BorderThickness = new Thickness(1),
        BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(167, 139, 250)),
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 36, 44)),
        Padding = new Thickness(6),
        Visibility = Visibility.Collapsed,
    };
    // The atlas holds thousands of cell names; binding them all into the editable combo made
    // opening the dropdown lag badly (WPF realized every item). We cap the browse list and
    // filter as the user types; the box stays free-text so any name still applies (S5-5).
    private const int MaxDropdownItems = 250;
    private bool _suppressFilter;
    private readonly ComboBox _spriteCombo = new()
    {
        IsEditable = true,
        Margin = new Thickness(0, 2, 0, 0),
        MaxDropDownHeight = 320,
        IsTextSearchEnabled = false,
        // The dropdown list stays on the platform's always-light popup, so its rows are pinned to dark
        // ink via a local ItemContainerStyle (certain to apply, unlike the implicit ComboBox style).
        ItemContainerStyle = Theme.ComboItemInkStyle(),
    };
    private readonly Button _spriteApply = new() { Content = "Apply sprite", Margin = new Thickness(0, 6, 0, 0) };
    private SceneCross? _spriteBound;
    private List<string> _spriteCandidates = new();
    private List<Choice> _entityChoices = new();
    private bool _spriteIsEntity;
    // True for the raw-span families, whose entity catalogue is harvested from the shipped levels
    // (hundreds of names): the combo stays editable so the list can be searched, and the shell
    // refuses a typed name that is not in it. TP2 leaves this false — a short curated list, chosen
    // from only.
    private bool _entityFilterable;

    /// <summary>Set by the shell when the loaded level's entity types come from a harvested
    /// definition catalogue (tb / f4 / f5) rather than the built-in TP2 list.</summary>
    public bool EntityCatalogFilterable
    {
        get => _entityFilterable;
        set => _entityFilterable = value;
    }

    /// <summary>One entity entry in the picker; ToString drives the non-editable combo label.</summary>
    private sealed class Choice
    {
        public string Name = "";
        public string Label = "";
        public override string ToString() => Label;
    }

    /// <summary>Set by the shell when the loaded level's format supports sprite reassignment
    /// (every family, for both entities and decorations). When false the sprite panel never
    /// appears.</summary>
    public bool SpritesEditable;

    /// <summary>Raised with the chosen sprite name; the shell writes it into the model through
    /// the codec-proven path, updates the marker and marks the document dirty.</summary>
    public Action<string>? ApplySprite;

    /// <summary>Wire these to the viewport. ApplyPos pushes an absolute world position;
    /// RequestNext asks the viewport to advance the selection to the following entity.</summary>
    public Action<double, double>? ApplyPos;
    public Action? RequestNext;

    /// <summary>Raised with an absolute rotation (world degrees) for a rotatable decoration.
    /// The shell routes it to the viewport's rotation edit path (splices the record in place).</summary>
    public Action<double>? ApplyRotation;

    /// <summary>Raised with the new facing of the selected decoration. The shell writes the two
    /// ElementDecoration flip words (creating or dropping the record's optional block) and, when the
    /// record changed byte length in an opaque section block, re-projects and re-selects it.</summary>
    public Action<bool, bool>? ApplyFlip;

    // ---- TP2 field parity panel (D7) -------------------------------------
    // A typed entity / decoration gets the same editable fields the Python inspector exposes:
    // mass/size, priority, colour (RGBA), multilevel-persist, emitter (on + count + interval),
    // follow-path (entities) and decoration size-x + rotation. The panel is rebuilt only when the
    // bound record changes identity (so OnEdited's re-Show can't eat keystrokes); numeric edits
    // commit on focus-loss / Enter and forward a (key, value) to the shell, which writes the
    // model through SceneProjector's TP2 accessors and commits one undo step.
    private readonly Border _tp2Box = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromRgb(251, 146, 60)),
        Background = new SolidColorBrush(Color.FromRgb(32, 36, 44)),
        Padding = new Thickness(6),
        Visibility = Visibility.Collapsed,
    };
    private readonly Grid _tp2Grid = new();
    private readonly Dictionary<string, FrameworkElement> _tp2Ctl = new();
    private object? _boundTp2;
    private bool _tp2IsEntity;
    private bool _tp2Suppress;
    private TextBlock? _eatLabel;
    private Rectangle? _swatch;
    private TextBox _cr = new(), _cg = new(), _cb = new(), _ca = new();
    private CheckBox? _emitCheck;
    private ComboBox? _pathCombo;

    /// <summary>Raised with (key, numeric value) for a scalar / bool TP2 field; the shell maps
    /// the key to a <see cref="SceneProjector.Tp2ScalarSet"/> call.</summary>
    public Action<string, double>? ApplyTp2Scalar;
    /// <summary>Raised with the four colour channels; the shell writes the record colour.</summary>
    public Action<byte, byte, byte, byte>? ApplyTp2Color;
    /// <summary>Raised with a path name (or null to clear) when the follow-path combo changes.</summary>
    public Action<string?>? ApplyTp2Path;
    /// <summary>The shell supplies the follow-path candidate names for a selected element's layer.</summary>
    public Func<ISceneElem, string[]>? Tp2PathCandidates;
    /// <summary>Meter-per-pixel used to show the derived eat size (cm); a default when no
    /// multilevel is linked (the value only rescales the read-only display).</summary>
    public double MeterPerPixel = 1e-3;

    // ---- named behaviour-tail fields (the whole tail, editable, no raw view) ----
    //
    // Every byte an entity record carries after mass / priority is decoded by <see cref="TailSem"/>
    // into the field names that game's own <c>ElementEntity::readBinary</c> / XML readers use, so this
    // panel reads like TP2 and there is no raw hex table anywhere in the editor. Every one of those
    // fields is editable; only the write differs. A fixed-width scalar commits an in-place,
    // span-clamped write at the field's tail-relative offset. A behaviour block's presence gate is a
    // checkbox: toggling it inserts / removes that block's bytes through <see cref="TailEdit"/>, which
    // re-decodes the result and is itself gated on the record still framing to the same boundaries, so
    // enabling "Emitter" gives the entity a real emitter block and disabling one takes it away cleanly.
    // Embedded names (path / defined area / point list / scriptable / linked entity) and the cell-colour
    // ramp count resize the record the same guarded way. The only rows left read-only are the rare
    // fields a mis-framed record recovered past its own byte span, whose bytes belong to the neighbour.
    private readonly Border _namedBox = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromRgb(129, 199, 132)),
        Background = new SolidColorBrush(Color.FromRgb(32, 36, 44)),
        Padding = new Thickness(6),
        Visibility = Visibility.Collapsed,
    };
    private readonly StackPanel _namedRows = new();
    private readonly TextBlock _tailNote = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.IndianRed,
        FontSize = 10,
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly Dictionary<int, (TextBox Box, TailField Fld)> _namedCtl = new();
    private readonly Dictionary<int, CheckBox> _namedGate = new();
    private readonly Dictionary<int, TextBox> _namedStruct = new();
    private readonly Dictionary<int, TextBlock> _namedRo = new();
    private List<TailField> _namedFields = new();
    private byte[] _namedTail = Array.Empty<byte>();
    private int _tailFrame;
    private object? _boundTail;
    private bool _tailSuppress;

    /// <summary>Raised to write a decoded named tail field in place: <c>(field, value)</c>. The shell
    /// routes it through <see cref="SceneProjector.FieldTailSet"/> (which dispatches on the field kind
    /// and re-checks the span bounds) and commits one coalesced undo step.</summary>
    public Action<TailField, double>? ApplyTailField;

    /// <summary>Raised to turn one behaviour block on or off. The shell routes it through
    /// <see cref="SceneProjector.FieldTailReshape"/>, which rebuilds the record's tail (inserting the
    /// block's default body, or removing exactly the bytes it occupied) and commits it only after the
    /// level's framing oracle still accepts the record; the inspector view is then rebuilt, since the
    /// record changed size.</summary>
    public Action<TailField, bool>? ApplyTailGate;

    /// <summary>Raised to rename an engine name embedded in a behaviour block (path, defined area, point
    /// list, emitting scriptable, linked entity).</summary>
    public Action<TailField, string>? ApplyTailString;

    /// <summary>Raised to resize the IndexedCellColours ramp. Adding entries appends zero-filled
    /// colours, removing drops the last ones.</summary>
    public Action<TailField, int>? ApplyTailCount;

    /// <summary>Force the next <see cref="Show"/> to rebuild the behaviour rows: a structural edit
    /// changed which fields exist, so the cached row layout is stale even though the same record is
    /// still selected.</summary>
    public void InvalidateTail() => _boundTail = null;

    /// <summary>R3: reads whether a named inspector card starts collapsed, and reports a change so the
    /// shell persists it. With nothing wired, every card starts expanded.</summary>
    public Func<string, bool>? IsCollapsed;
    public Action<string, bool>? OnCollapse;

    // ---- Tasty Planet 1 object card --------------------------------------
    //
    // A tp1 level is a tree of plain-text XML elements whose only meaning is the attributes each
    // carries. Rather than hand-enumerate every object kind's fields (the shipped levels use a long
    // tail of attribute names, and the game reads them by the exact spelling the file uses), the card
    // reflects whatever attributes the selected element actually has and lets each be edited in place.
    // Scalar writes go through the span-localised edit so only that value's bytes move; the whole card
    // re-reads the element after a commit, so it can never drift from what the file really holds. For
    // a controller / emitter the element's own list children (what it affects, its timed emits) are
    // shown too, each editable, with add / remove. A handful of attributes are the game's own
    // vocabulary — an emitter/controller type, or which object an emitter makes — and those are offered
    // as a dropdown the shell validates so an edit can only pick a name the game knows.
    private readonly Border _tp1Box = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
        Background = new SolidColorBrush(Color.FromRgb(32, 36, 44)),
        Padding = new Thickness(6),
        Visibility = Visibility.Collapsed,
    };
    private readonly StackPanel _tp1Rows = new();
    private string _boundTp1Path = "";
    private bool _tp1Suppress;
    private Func<string, string[]?>? _tp1Names;   // the shell's list of object types / tile pictures

    /// <summary>Commit an absolute attribute edit on a tp1 element: <c>(handle, key, new text)</c>.
    /// The shell re-derives the number-vs-text form, writes only that value's bytes, re-projects and
    /// commits one undo step.</summary>
    public Action<TastyEditor.Core.Levels.Tp1Handle, string, string>? ApplyTp1Attr;

    /// <summary>Add one child of a tag to a tp1 element (a new affect / timed emit), cloned from the
    /// element's own style. The shell re-projects so the fresh element is editable immediately.</summary>
    public Action<TastyEditor.Core.Levels.Tp1Handle, string>? ApplyTp1Add;

    /// <summary>Remove one child element (addressed by its own handle) from a tp1 element.</summary>
    public Action<TastyEditor.Core.Levels.Tp1Handle>? ApplyTp1Remove;

    /// <summary>Supply the shell's known object-type names (emitter <c>entitydef</c>) and tile pictures
    /// so the card can offer them as a validated dropdown. Null hides the dropdown (plain free text).</summary>
    public void SetTp1Names(Func<string, string[]?>? names) => _tp1Names = names;

    /// <summary>Force the tp1 card to rebuild on the next Show. A scalar commit refreshes values
    /// in place (the path is unchanged), but an add / remove changes the element's own children
    /// while keeping its path, so the shell calls this to drop the row layout and re-read the tree.</summary>
    public void InvalidateTp1() => _boundTp1Path = "";

    public InspectorPanel()
    {
        Margin = new Thickness(8, 6, 8, 6);
        Children.Add(new TextBlock { Text = "Inspector", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        Children.Add(_info);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(grid, 0, "X", _x);
        AddRow(grid, 1, "Y", _y);
        Children.Add(grid);

        Children.Add(_apply);

        // Rotation row (hidden unless the selection is a rotatable decoration).
        _rotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _rotRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(_rotRow, 0, "Rot\u00b0", _rot);
        _rotRow.Margin = new Thickness(0, 8, 0, 0);
        _rotRow.Visibility = Visibility.Collapsed;
        Children.Add(_rotRow);
        _applyRot.Visibility = Visibility.Collapsed;
        Children.Add(_applyRot);

        // Facing row (hidden unless the selection is a decoration with the flip field).
        _flipRow.Children.Add(_flipX);
        _flipRow.Children.Add(_flipY);
        Children.Add(_flipRow);

        Children.Add(_next);
        Children.Add(_hint);

        BuildWaterPanel();
        Children.Add(_waterBox);

        BuildSpritePanel();
        // The entity picker is a NON-editable combo (a short curated list, chosen from only), and the
        // closed box draws its selected item through a ContentPresenter that ignores the control's own
        // Foreground - so the name sat there in the platform's default grey on light chrome. Setting
        // Foreground alone was never going to reach it; StyleCombo also installs the ItemTemplate whose
        // TextBlock carries a local dark ink, which the collapsed box mirrors into SelectionBoxItemTemplate.
        Theme.StyleCombo(_spriteCombo);
        Children.Add(_spriteBox);

        BuildTp2Panel();
        Children.Add(_tp2Box);

        BuildNamedPanel();
        Children.Add(_namedBox);

        BuildTp1Panel();
        Children.Add(_tp1Box);

        // R3: turn the four hardcoded group boxes into rounded theme cards with a click-to-collapse
        // header whose open/closed state the shell remembers.
        MakeCard(_waterBox, "water");
        MakeCard(_spriteBox, "sprite");
        MakeCard(_tp2Box, "tp2");
        MakeCard(_namedBox, "behaviour");
        MakeCard(_tp1Box, "tp1");

        _apply.Click += (_, _) => Apply();
        _applyRot.Click += (_, _) => ApplyRotationNow();
        _flipX.Checked += (_, _) => OnFlipEdit();
        _flipX.Unchecked += (_, _) => OnFlipEdit();
        _flipY.Checked += (_, _) => OnFlipEdit();
        _flipY.Unchecked += (_, _) => OnFlipEdit();
        _next.Click += (_, _) => RequestNext?.Invoke();
        _spriteApply.Click += (_, _) =>
        {
            // Entities pick from the definition list (TP2: strictly non-editable; Tasty Blue /
            // ElementEngine: a searchable catalogue the shell still validates, see EntityDefs).
            // Decorations are free sprites, so their combo stays plain free-text.
            string? nm = _spriteIsEntity
                ? (_spriteCombo.SelectedItem as Choice)?.Name ?? (_spriteCombo.Text ?? "").Trim()
                : (_spriteCombo.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(nm) && _spriteBound is not null) ApplySprite?.Invoke(nm!);
        };
        // Filter-as-you-type: over the atlas cells for a decoration, over the entity catalogue for a
        // Tasty Blue / ElementEngine entity (hundreds of names, so an unfiltered list is unusable).
        // An editable ComboBox hosts a TextBox whose TextChanged bubbles, so handle it as a routed
        // event (the base ComboBox exposes no TextChanged of its own).
        _spriteCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) =>
            {
                if (_suppressFilter) return;
                _spriteCombo.ItemsSource = _spriteIsEntity
                    ? (_entityFilterable ? FilteredEntityChoices(_spriteCombo.Text) : _entityChoices)
                    : FilteredCandidates(_spriteCombo.Text);
            }));
        Show(null);
    }

    /// <summary>R3: restyle one group box as a rounded theme card and give its header a collapse
    /// toggle. The head (the box's first child, a coloured title) becomes a clickable row with a
    /// chevron; everything below it moves into a host the header hides. The starting state comes from
    /// <see cref="IsCollapsed"/> and a toggle is reported through <see cref="OnCollapse"/>. Manage*
    /// methods still show/hide the whole card per selection, which is unaffected by this wrapping.</summary>
    private void MakeCard(Border box, string id)
    {
        box.CornerRadius = new CornerRadius(Theme.Corner);
        box.BorderThickness = new Thickness(1);
        Theme.Ref(box, Border.BackgroundProperty, Theme.KSurface);
        Theme.Ref(box, Border.BorderBrushProperty, Theme.KBorder);

        if (box.Child is not StackPanel outer || outer.Children.Count == 0) return;
        UIElement head = outer.Children[0];
        outer.Children.RemoveAt(0);

        var contentHost = new StackPanel();
        while (outer.Children.Count > 0)
        {
            var el = outer.Children[0];
            outer.Children.RemoveAt(0);
            contentHost.Children.Add(el);
        }

        var chevron = new TextBlock { Width = 14, VerticalAlignment = VerticalAlignment.Center, FontSize = 10 };
        Theme.Ref(chevron, TextBlock.ForegroundProperty, Theme.KTextMuted);
        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 2),
        };
        headerRow.Children.Add(chevron);
        headerRow.Children.Add(head);
        var headerHost = new Border { Child = headerRow, Background = Brushes.Transparent };

        bool collapsed = IsCollapsed?.Invoke(id) ?? false;
        contentHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        chevron.Text = collapsed ? "\u25B8" : "\u25BE";

        headerHost.MouseLeftButtonUp += (_, _) =>
        {
            bool wasOpen = contentHost.Visibility == Visibility.Visible;
            contentHost.Visibility = wasOpen ? Visibility.Collapsed : Visibility.Visible;
            chevron.Text = wasOpen ? "\u25B8" : "\u25BE";
            OnCollapse?.Invoke(id, wasOpen);
        };

        outer.Children.Add(headerHost);
        outer.Children.Add(contentHost);
    }

    private static void AddRow(Grid g, int row, string label, TextBox box)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var lb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2) };
        box.Margin = new Thickness(0, 2, 0, 2);
        Grid.SetRow(lb, row); Grid.SetColumn(lb, 0);
        Grid.SetRow(box, row); Grid.SetColumn(box, 1);
        g.Children.Add(lb);
        g.Children.Add(box);
    }

    /// <summary>Refresh the panel for the given selection (null clears it).</summary>
    public void Show(ISceneElem? sel)
    {
        _bound = null;
        if (sel is null)
        {
            _info.Text = "Nothing selected.";
            SetEditable(false);
            _x.Text = _y.Text = "";
            _hint.Text = "Click an object in the viewport, or use Select next object.";
            ManageWater(null);
            ManageSprite(null);
            ManageRotation(null);
            ManageFlip(null);
            ManageTp2(null);
            ManageTail(null);
            ManageTp1(null);
            return;
        }

        string label = string.IsNullOrEmpty(sel.Label) ? "" : $" “{sel.Label}”";
        _info.Text = $"{UiText.Kind(sel.Kind)}{label}   ·   Level {sel.Layer + 1}   ·   #{sel.Id}";
        ManageWater(sel);
        ManageSprite(sel);
        ManageRotation(sel);
        ManageFlip(sel);
        ManageTp2(sel);
        ManageTail(sel);
        ManageTp1(sel);

        if (sel is SceneCross cr && SceneProjector.GetPos(cr.Source) is PointD p)
        {
            _bound = cr;
            _x.Text = Fmt(p.X);
            _y.Text = Fmt(p.Y);
            SetEditable(true);
            _hint.Text = SceneProjector.IsRotatable(cr.Source)
                ? "Drag the object to move it, or type its X / Y here. To turn it, type an angle in Rot\u00b0 and press Apply rotation, or press [ and ] (hold Shift for 15\u00b0 steps). "
                    + (SceneProjector.IsFlippable(cr.Source)
                        ? "Flip X / Flip Y mirror the picture, the way the game places a flipped decoration. "
                        : "")
                    + UndoHint
                : "Drag the object in the viewport, or type its X / Y here and press Apply position.";
        }
        else
        {
            _x.Text = _y.Text = "";
            SetEditable(false);
            if (sel is ScenePoly sp && SceneProjector.IsEditablePoly(sp.Source, sp.ShapeIndex))
            {
                _hint.Text = sp.Kind == SceneKind.Water
                    ? "This is the water box. Drag a corner to resize it (the opposite corner stays put), or drag inside it to move the whole box."
                    : $"This shape has {SceneProjector.PolyVertexCount(sp.Source, sp.ShapeIndex)} corners. Drag a corner to reshape it, double-click an edge to add a corner there, or drag inside the outline to move the whole shape.";
            }
            else if (sel is SceneLine sl && SceneProjector.IsEditablePath(sl.Source))
            {
                _hint.Text = $"This path has {SceneProjector.PathPointCount(sl.Source)} points. Drag a point to bend it, double-click the line to add a point there, right-click a point to remove it, or drag along the line to move the whole path. " + UndoHint;
            }
            else
            {
                _hint.Text = sel is SceneCross
                    ? "This object's position is stored in a way the editor cannot safely change yet, so it can't be moved."
                    : "This shape can't be edited yet.";
            }
        }
    }

    private void Apply()
    {
        if (_bound is null) return;
        if (double.TryParse(_x.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(_y.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            ApplyPos?.Invoke(x, y);
        else
            _hint.Text = "X and Y must both be numbers.";
    }

    /// <summary>R1: the shared hint line for the geometry rows, in place of the old sentences that
    /// narrated how the write works.</summary>
    private const string UndoHint = "Every change can be undone with Ctrl+Z.";

    // ------------------------------------------------ rotation row (D6) ---

    /// <summary>Show the degrees field only for a rotatable decoration (a Tasty Blue or
    /// ElementEngine deco); collapse it for every other selection so the panel stays stable.</summary>
    private void ManageRotation(ISceneElem? sel)
    {
        _rotBound = null;
        if (sel is not SceneCross cr || !SceneProjector.IsRotatable(cr.Source))
        {
            _rotRow.Visibility = Visibility.Collapsed;
            _applyRot.Visibility = Visibility.Collapsed;
            _rot.Text = "";
            return;
        }
        _rotBound = cr;
        _rot.Text = SceneProjector.GetRotationDeg(cr.Source) is double d ? Fmt(d) : "";
        _rotRow.Visibility = Visibility.Visible;
        _applyRot.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------- decoration facing (S-D13) --

    /// <summary>Show the Flip X / Flip Y row only for a decoration that carries the field. The
    /// checkboxes are filled under <see cref="_suppress"/> so re-Show (which OnEdited triggers after
    /// a commit) cannot fire the handlers and re-apply the value it was just given.</summary>
    private void ManageFlip(ISceneElem? sel)
    {
        _flipBound = null;
        if (sel is not SceneCross cr || cr.Kind != SceneKind.Deco || !SceneProjector.IsFlippable(cr.Source))
        {
            _flipRow.Visibility = Visibility.Collapsed;
            _suppress = true;
            _flipX.IsChecked = _flipY.IsChecked = false;
            _suppress = false;
            return;
        }
        var (fx, fy) = SceneProjector.GetFlip(cr.Source);
        _suppress = true;
        _flipX.IsChecked = fx;
        _flipY.IsChecked = fy;
        _suppress = false;
        _flipBound = cr;
        _flipRow.Visibility = Visibility.Visible;
    }

    private void OnFlipEdit()
    {
        if (_suppress || _flipBound is null) return;
        ApplyFlip?.Invoke(_flipX.IsChecked == true, _flipY.IsChecked == true);
    }

    private void ApplyRotationNow()
    {
        if (_rotBound is null) return;
        if (double.TryParse(_rot.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg))
            ApplyRotation?.Invoke(deg);
        else
            _hint.Text = "Rotation must be a number of degrees.";
    }

    // ------------------------------------------------ water field panel ---

    private void BuildWaterPanel()
    {
        var head = new TextBlock { Text = "Water", FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.DeepSkyBlue, Margin = new Thickness(0, 0, 0, 2) };
        var note = new TextBlock
        {
            Text = "Position and size are what the game draws the water box from. The fields below it are settings the game's own files never name \u2014 hover one to see what is known about it. " + UndoHint,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.TextMutedBrush(),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 6),
        };
        _waterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _waterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var outer = new StackPanel();
        outer.Children.Add(head);
        outer.Children.Add(note);
        outer.Children.Add(_waterGrid);
        _waterBox.Child = outer;
    }

    private void ManageWater(ISceneElem? sel)
    {
        var wt = sel is ScenePoly sp && sp.Kind == SceneKind.Water ? sp.Source as TbWater : null;
        if (wt is null)
        {
            _boundWater = null;
            _waterBox.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ReferenceEquals(wt, _boundWater))   // rebuild only when a different water is picked
        {
            _boundWater = wt;
            RebuildWaterInputs();
        }
        _waterBox.Visibility = Visibility.Visible;
        FillWater(wt);
    }

    private void RebuildWaterInputs()
    {
        _suppress = true;
        _waterGrid.Children.Clear();
        _waterGrid.RowDefinitions.Clear();
        _waterInputs.Clear();
        for (int i = 0; i < WaterFields.All.Length; i++)
        {
            var spec = WaterFields.All[i];
            _waterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lb = new TextBlock { Text = spec.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 1), FontSize = 11 };
            FrameworkElement ctl;
            if (spec.Kind == WaterFieldKind.Bool)
            {
                var cb = new CheckBox { Margin = new Thickness(0, 2, 0, 2) };
                cb.Checked += (_, _) => OnWaterEdit(spec);
                cb.Unchecked += (_, _) => OnWaterEdit(spec);
                ctl = cb;
            }
            else
            {
                var tb = new TextBox { Margin = new Thickness(0, 1, 0, 1), FontSize = 11 };
                if (spec.Key == "name") tb.TextChanged += (_, _) => OnWaterEdit(spec);
                else { tb.LostFocus += (_, _) => OnWaterEdit(spec); tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) OnWaterEdit(spec); }; }
                ctl = tb;
            }
            // R1: the label alone cannot explain a field the game never names, so the meaning (or the
            // honest "not known") hangs on both the label and the input.
            if (spec.Hint.Length > 0) { lb.ToolTip = spec.Hint; ctl.ToolTip = spec.Hint; }
            Grid.SetRow(lb, i); Grid.SetColumn(lb, 0);
            Grid.SetRow(ctl, i); Grid.SetColumn(ctl, 1);
            _waterGrid.Children.Add(lb);
            _waterGrid.Children.Add(ctl);
            _waterInputs[spec.Key] = (spec.Kind, ctl);
        }
        _suppress = false;
    }

    private void FillWater(TbWater w)
    {
        _suppress = true;
        foreach (var spec in WaterFields.All)
        {
            var ctl = _waterInputs[spec.Key].Ctl;
            object val = WaterFields.Get(w, spec.Key);
            if (ctl is CheckBox cb) cb.IsChecked = ToInt(val) != 0;
            else if (ctl is TextBox tb) tb.Text = spec.Kind == WaterFieldKind.Double ? Fmt((double)val) : val.ToString();
        }
        _suppress = false;
    }

    private void OnWaterEdit(WaterFieldSpec spec)
    {
        if (_suppress || _boundWater is null) return;
        var ctl = _waterInputs[spec.Key].Ctl;
        object value;
        if (ctl is CheckBox cb) value = cb.IsChecked == true ? 1 : 0;
        else
        {
            string text = ((TextBox)ctl).Text;
            if (spec.Kind == WaterFieldKind.Int)
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) return;
                value = iv;
            }
            else if (spec.Key == "name") value = text;
            else
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)) return;
                value = dv;
            }
        }
        WaterFields.Set(_boundWater, spec.Key, value);
        WaterChanged?.Invoke();
    }

    private static int ToInt(object v) => v is int i ? i : Convert.ToInt32(v);

    // ------------------------------------------------ sprite picker (S5-5) ---

    private void BuildSpritePanel()
    {
        var head = new TextBlock { Text = "Sprite", FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.MediumPurple, Margin = new Thickness(0, 0, 0, 2) };
        var note = new TextBlock
        {
            Text = "Show this object as a different picture. An entity has to be a type the game actually has \u2014 Back for Seconds only offers those, and on the other games a name they have never used is refused, because the game would load the level and leave the entity out. "
                + "Decorations are plain pictures, so any sprite the game ships can be used.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.TextMutedBrush(),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var outer = new StackPanel();
        outer.Children.Add(head);
        outer.Children.Add(note);
        outer.Children.Add(_spriteCombo);
        outer.Children.Add(_spriteApply);
        _spriteBox.Child = outer;
    }

    /// <summary>Show the picker for an editable sprite marker: a TP2 entity/deco, or a Tasty
    /// Blue / ElementEngine entity or decoration (whose <c>Source</c> is the live record handle
    /// the shell can rewrite in place); otherwise collapse it so non-editable selections are
    /// unchanged. An entity is offered its game's <b>entity definition</b> names (never atlas cells:
    /// a type the game has no definition for is read from the file and then dropped, so the entity
    /// disappears in game — see <see cref="TastyEditor.Core.Levels.EntityDefs"/>). TP2 uses a fixed
    /// dropdown of the curated catalogue; the raw-span families use a searchable one the shell
    /// validates. Decorations stay plain free-text sprite pickers.</summary>
    private void ManageSprite(ISceneElem? sel)
    {
        _spriteBound = null;
        if (!SpritesEditable || sel is not SceneCross cr || !IsSpriteEditableSource(cr.Source))
        {
            _spriteBox.Visibility = Visibility.Collapsed;
            return;
        }
        _spriteBound = cr;
        if (cr.Source is TastyEditor.Core.Levels.Tp2Entity
            || cr.Source is TastyEditor.Core.Levels.TbEntity
            || cr.Source is TastyEditor.Core.Levels.EntityEditHandle)
        {
            _spriteIsEntity = true;
            string cur = cr.SpriteName ?? "";
            if (cur.Length > 0 && !_entityChoices.Any(c => c.Name == cur))
                _entityChoices.Insert(0, new Choice { Name = cur, Label = cur + "  (current)" });
            _suppressFilter = true;
            if (_entityFilterable)
            {
                // Hundreds of catalogue names: editable + filtered, with the current type shown.
                _spriteCombo.IsEditable = true;
                _spriteCombo.Text = cur;
                _spriteCombo.ItemsSource = FilteredEntityChoices("");
            }
            else
            {
                _spriteCombo.IsEditable = false;
                _spriteCombo.ItemsSource = _entityChoices;
                _spriteCombo.SelectedItem = _entityChoices.FirstOrDefault(c => c.Name == cur);
            }
            _suppressFilter = false;
        }
        else
        {
            _spriteIsEntity = false;
            _suppressFilter = true;
            _spriteCombo.IsEditable = true;
            _spriteCombo.Text = cr.SpriteName ?? "";
            _spriteCombo.ItemsSource = FilteredCandidates("");   // capped browse list
            _suppressFilter = false;
        }
        _spriteBox.Visibility = Visibility.Visible;
    }

    /// <summary>The same bounded browse/filter view over the entity definition catalogue, whose rows
    /// are <see cref="Choice"/>s (label shown, name applied).</summary>
    private List<Choice> FilteredEntityChoices(string? filter)
    {
        var f = (filter ?? "").Trim();
        if (f.Length == 0) return _entityChoices.Take(MaxDropdownItems).ToList();
        return _entityChoices
            .Where(c => c.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                     || c.Label.Contains(f, StringComparison.OrdinalIgnoreCase))
            .Take(MaxDropdownItems)
            .ToList();
    }

    /// <summary>A bounded, filter-as-you-type view of the atlas cell names for the decoration
    /// picker. With no filter it returns the first <see cref="MaxDropdownItems"/> names to browse;
    /// with text it returns up to that many case-insensitive substring matches. The combo is
    /// still free-text, so a name beyond the cap can be typed and applied unchanged.</summary>
    private IEnumerable<string> FilteredCandidates(string? filter)
    {
        var f = (filter ?? "").Trim();
        if (f.Length == 0)
            return _spriteCandidates.Take(MaxDropdownItems).ToList();
        return _spriteCandidates
            .Where(n => n is not null && n.Contains(f, StringComparison.OrdinalIgnoreCase))
            .Take(MaxDropdownItems)
            .ToList();
    }

    /// <summary>True when this marker's <c>Source</c> names a record the shell can re-point at a
    /// different sprite: a TP2 entity/deco, a Tasty Blue entity/deco, or an ElementEngine entity/
    /// deco handle. TP2 entities use the curated dropdown; every other source uses the free-text
    /// all-sprite list (raw-span names are validated for byte-credibility inside the edit call).</summary>
    private static bool IsSpriteEditableSource(object? src) =>
        src is TastyEditor.Core.Levels.Tp2Entity
        || src is TastyEditor.Core.Levels.Tp2Deco
        || src is TastyEditor.Core.Levels.TbEntity
        || src is TastyEditor.Core.Levels.TbDeco
        || src is TastyEditor.Core.Levels.EntityEditHandle
        || src is TastyEditor.Core.Levels.DecoEditHandle;

    /// <summary>Offer these names in the decoration picker dropdown (the loaded level's asset
    /// cell and animation names). The box stays editable, so a name outside the list can still
    /// be typed - correct for decorations, which are plain sprites. Called by the shell.</summary>
    public void SetSpriteCandidates(IEnumerable<string>? names)
    {
        _spriteCandidates = (names ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>Offer these entity definitions in the entity picker dropdown (for TP2 the curated
    /// entity catalogue plus any type already used in the open level; for Tasty Blue / Forever /
    /// Final Bite the types the shipped levels of that install use). The combo is non-editable,
    /// so an entity can only ever be set to a name that has a real definition. Called by the shell.</summary>
    public void SetEntityCandidates(IEnumerable<Tp2Entities.Def>? defs)
    {
        _entityChoices = (defs ?? Array.Empty<Tp2Entities.Def>())
            .Select(d => new Choice
            {
                Name = d.Name,
                Label = string.IsNullOrEmpty(d.Theme)
                    ? d.Display
                    : d.Display + "  \u00b7  " + d.Theme,
            })
            .ToList();
    }

    private void SetEditable(bool on)
    {
        _x.IsEnabled = _y.IsEnabled = _apply.IsEnabled = on;
    }

    // ------------------------------------------------ TP2 field panel (D7) ---

    private void BuildTp2Panel()
    {
        var head = new TextBlock { Text = "Properties", FontWeight = FontWeights.SemiBold, Foreground = Brushes.Orange, Margin = new Thickness(0, 0, 0, 2) };
        var note = new TextBlock
        {
            Text = "The same settings the classic editor showed. Position and rotation are above; everything here undoes with Ctrl+Z. On Tasty Blue, Forever and Final Bite the behaviour settings (spawning, path following, wandering, colour fades) live in the \u201cBehaviour\u201d group below \u2014 they are read out of each game's own loading code and shown by the game's own names.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.TextMutedBrush(),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 6),
        };
        _tp2Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tp2Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var outer = new StackPanel();
        outer.Children.Add(head);
        outer.Children.Add(note);
        outer.Children.Add(_tp2Grid);
        _tp2Box.Child = outer;
    }

    private void ManageTp2(ISceneElem? sel)
    {
        var src = (sel as SceneCross)?.Source;
        bool isEnt = SceneProjector.IsFieldEntity(src);
        bool isDeco = SceneProjector.IsFieldDeco(src);
        if (!isEnt && !isDeco)
        {
            _boundTp2 = null;
            _tp2Box.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ReferenceEquals(src, _boundTp2) || isEnt != _tp2IsEntity)
        {
            _boundTp2 = src;
            _tp2IsEntity = isEnt;
            RebuildTp2();
        }
        _tp2Box.Visibility = Visibility.Visible;
        FillTp2(src, sel!);
    }
    
    private void RebuildTp2()
    {
        _tp2Suppress = true;
        _tp2Grid.Children.Clear();
        _tp2Grid.RowDefinitions.Clear();
        _tp2Ctl.Clear();
        _eatLabel = null; _swatch = null; _emitCheck = null; _pathCombo = null;
        bool behav = SceneProjector.SupportsBehaviour(_boundTp2);
        int row = 0;
        if (_tp2IsEntity)
        {
            NumRow("mass", "mass (size)", ref row, isInt: false,
                "How big this object is. The goo can eat things lighter than it, and this also sets how large the object is drawn.");
            NumRow("prio", "priority", ref row, isInt: true);
            EatRow(ref row);
            // The behaviour blocks (persist / spawner / follow-path) are rewritable only on TP2's
            // typed codec; the raw-span families keep them in a per-prefab tail, so those rows
            // stay hidden for them (matching the classic editor's entity_full_body gate).
            if (behav)
            {
                BoolRow("persist", "persists", ref row);
                BoolRow("emit_on", "emits (spawner)", ref row,
                    "Makes this object produce other objects over and over. The two settings under it say how many, and how long it waits between bursts.");
                NumRow("emit_count", "\u21b3 count", ref row, isInt: true, "How many objects it produces in each burst.");
                NumRow("emit_min", "\u21b3 min s", ref row, isInt: false, "Shortest wait between bursts, in seconds.");
                NumRow("emit_max", "\u21b3 max s", ref row, isInt: false, "Longest wait between bursts, in seconds.");
            }
            ColorRow(ref row);
            if (behav) PathRow(ref row);
        }
        else
        {
            NumRow("dsize", "size \u00d7", ref row, isInt: false,
                "Scales the picture: 1 shows it at the sprite's own size, 2 makes it twice as big.");
            if (SceneProjector.SupportsDecoPriority(_boundTp2))
                NumRow("dprio", "priority", ref row, isInt: true);
            ColorRow(ref row);
        }
        _tp2Suppress = false;
    }

    private void Label(string text, int row, string? hint = null)
    {
        _tp2Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var lb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 1), FontSize = 11 };
        if (!string.IsNullOrEmpty(hint)) lb.ToolTip = hint;
        Grid.SetRow(lb, row); Grid.SetColumn(lb, 0);
        _tp2Grid.Children.Add(lb);
    }

    private void NumRow(string key, string label, ref int row, bool isInt, string? hint = null)
    {
        hint ??= UiText.FieldHint(label);
        Label(label, row, hint);
        var tb = new TextBox { Margin = new Thickness(0, 1, 0, 1), FontSize = 11 };
        if (!string.IsNullOrEmpty(hint)) tb.ToolTip = hint;
        tb.LostFocus += (_, _) => CommitNum(key, tb, isInt);
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitNum(key, tb, isInt); };
        Grid.SetRow(tb, row); Grid.SetColumn(tb, 1);
        _tp2Grid.Children.Add(tb);
        _tp2Ctl[key] = tb;
        row++;
    }

    private void BoolRow(string key, string label, ref int row, string? hint = null)
    {
        hint ??= UiText.FieldHint(label);
        Label(label, row, hint);
        var cb = new CheckBox { Margin = new Thickness(0, 3, 0, 3) };
        if (!string.IsNullOrEmpty(hint)) cb.ToolTip = hint;
        cb.Checked += (_, _) => OnToggle(key, true);
        cb.Unchecked += (_, _) => OnToggle(key, false);
        Grid.SetRow(cb, row); Grid.SetColumn(cb, 1);
        _tp2Grid.Children.Add(cb);
        _tp2Ctl[key] = cb;
        if (key == "emit_on") _emitCheck = cb;
        row++;
    }

    private void EatRow(ref int row)
    {
        Label("eat size (cm)", row,
            "How big this object ends up in metres, worked out from its mass and the campaign's scale. It follows from mass \u2014 change the mass, not this.");
        _eatLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 1), FontSize = 11, Foreground = Theme.TextMutedBrush() };
        Grid.SetRow(_eatLabel, row); Grid.SetColumn(_eatLabel, 1);
        _tp2Grid.Children.Add(_eatLabel);
        row++;
    }

    private void ColorRow(ref int row)
    {
        Label("color (r g b a)", row,
            "The colour the picture is tinted with, from 0 to 255 per channel. The last one is how see-through it is: 255 solid, 0 invisible.");
        var pan = new StackPanel { Orientation = Orientation.Horizontal };
        _swatch = new Rectangle { Width = 20, Height = 20, Margin = new Thickness(0, 0, 6, 0), Stroke = Theme.BorderBrush(), Fill = Brushes.White };
        // Allocate the four channel boxes fresh each rebuild: a WPF element can only have one
        // visual parent, so re-adding a previous build's TextBox to the new panel throws.
        _cr = NewChannel("cr"); _cg = NewChannel("cg"); _cb = NewChannel("cb"); _ca = NewChannel("ca");
        pan.Children.Add(_cr);
        pan.Children.Add(_cg);
        pan.Children.Add(_cb);
        pan.Children.Add(_ca);
        var ok = new Button { Content = "Set", Padding = new Thickness(6, 0, 6, 0) };
        ok.Click += (_, _) => CommitColor();
        pan.Children.Add(ok);
        var wrap = new StackPanel { Orientation = Orientation.Horizontal };
        wrap.Children.Add(_swatch);
        wrap.Children.Add(pan);
        Grid.SetRow(wrap, row); Grid.SetColumn(wrap, 1);
        _tp2Grid.Children.Add(wrap);
        row++;
    }

    private TextBox NewChannel(string key)
    {
        var tb = new TextBox { Width = 38, Margin = new Thickness(0, 1, 4, 1), FontSize = 11 };
        _tp2Ctl[key] = tb;
        return tb;
    }

    private void PathRow(ref int row)
    {
        Label("follow path", row);
        _pathCombo = new ComboBox { Margin = new Thickness(0, 1, 0, 1), FontSize = 11 };
        Theme.StyleCombo(_pathCombo);
        _pathCombo.SelectionChanged += (_, _) => OnPathChanged();
        Grid.SetRow(_pathCombo, row); Grid.SetColumn(_pathCombo, 1);
        _tp2Grid.Children.Add(_pathCombo);
        _tp2Ctl["path"] = _pathCombo;
        row++;
    }

    private void FillTp2(object? src, ISceneElem sel)
    {
        _tp2Suppress = true;
        if (_tp2IsEntity)
        {
            SetNum("mass", SceneProjector.FieldGet(src, "mass"), isInt: false);
            SetNum("prio", SceneProjector.FieldGet(src, "prio"), isInt: true);
            if (SceneProjector.SupportsBehaviour(src))
            {
                SetChk("persist", SceneProjector.FieldGet(src, "persist") != 0);
                SetChk("emit_on", SceneProjector.FieldGet(src, "emit_on") != 0);
                SetNum("emit_count", SceneProjector.FieldGet(src, "emit_count"), isInt: true);
                SetNum("emit_min", SceneProjector.FieldGet(src, "emit_min"), isInt: false);
                SetNum("emit_max", SceneProjector.FieldGet(src, "emit_max"), isInt: false);
                UpdateEmitEnabled();
                FillPath(src, sel);
            }
            UpdateEat(src);
        }
        else
        {
            SetNum("dsize", SceneProjector.FieldGet(src, "dsize"), isInt: false);
            SetNum("dprio", SceneProjector.FieldGet(src, "dprio"), isInt: true);
        }
        FillColor(src);
        _tp2Suppress = false;
    }

    private void SetNum(string key, double? v, bool isInt)
    {
        if (_tp2Ctl.TryGetValue(key, out var c) && c is TextBox tb)
            tb.Text = v is double d ? (isInt ? ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture) : Fmt(d)) : "";
    }

    private void SetChk(string key, bool on)
    {
        if (_tp2Ctl.TryGetValue(key, out var c) && c is CheckBox cb) cb.IsChecked = on;
    }

    private void UpdateEmitEnabled()
    {
        bool on = _emitCheck?.IsChecked == true;
        foreach (var k in new[] { "emit_count", "emit_min", "emit_max" })
            if (_tp2Ctl.TryGetValue(k, out var c) && c is TextBox tb) tb.IsEnabled = on;
    }

    private void UpdateEat(object? src)
    {
        if (_eatLabel is null) return;
        double mass = SceneProjector.FieldGet(src, "mass") ?? 0;
        double cm = 2 * Math.Sqrt(Math.Max(mass, 1.0) / Math.PI) * MeterPerPixel * 100;
        _eatLabel.Text = cm.ToString("0.00", CultureInfo.InvariantCulture);
        _eatLabel.ToolTip = $"Worked out from mass and the campaign's scale (Ø = 2·√(mass/π) · scale · 100, scale {MeterPerPixel:0.######}) — shown so you can judge how big the object will feel in the game.";
    }

    private void FillColor(object? src)
    {
        var c = SceneProjector.FieldColorGet(src);
        _cr.Text = (c is { Length: >= 1 } ? c[0] : (byte)255).ToString();
        _cg.Text = (c is { Length: >= 2 } ? c[1] : (byte)255).ToString();
        _cb.Text = (c is { Length: >= 3 } ? c[2] : (byte)255).ToString();
        _ca.Text = (c is { Length: >= 4 } ? c[3] : (byte)255).ToString();
        SetSwatch(c);
    }

    private void SetSwatch(byte[]? c)
    {
        if (_swatch is null) return;
        byte r = c is { Length: >= 1 } ? c[0] : (byte)255;
        byte g = c is { Length: >= 2 } ? c[1] : (byte)255;
        byte b = c is { Length: >= 3 } ? c[2] : (byte)255;
        byte a = c is { Length: >= 4 } ? c[3] : (byte)255;
        _swatch.Fill = new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    private void FillPath(object? src, ISceneElem sel)
    {
        if (_pathCombo is null) return;
        string? cur = SceneProjector.Tp2PathGet(src);
        _pathCombo.Items.Clear();
        _pathCombo.Items.Add("(none)");
        string[] cands = Tp2PathCandidates?.Invoke(sel) ?? Array.Empty<string>();
        foreach (var nm in cands) _pathCombo.Items.Add(nm);
        if (!string.IsNullOrEmpty(cur) && !cands.Contains(cur, StringComparer.Ordinal))
            _pathCombo.Items.Add(cur + "  (missing)");
        _pathCombo.SelectedIndex = cands.Length >= 0 ? IndexFor(cur, cands) : 0;
        _pathCombo.IsEnabled = cands.Length > 0 || !string.IsNullOrEmpty(cur);
        _pathCombo.ToolTip = cands.Length == 0 ? "This layer has no paths to follow." : "Assign this entity to a path: it snaps to the path and follows it.";
    }

    private static int IndexFor(string? cur, string[] cands)
    {
        if (string.IsNullOrEmpty(cur)) return 0;
        int i = Array.IndexOf(cands, cur);
        return i >= 0 ? i + 1 : 0;
    }

    private void CommitNum(string key, TextBox box, bool isInt)
    {
        if (_tp2Suppress) return;
        string text = box.Text.Trim();
        double val;
        if (isInt)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) return;
            val = iv;
        }
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)) return;
        else val = dv;
        ApplyTp2Scalar?.Invoke(key, val);
        if (key == "mass" && _boundTp2 is not null) UpdateEat(_boundTp2);
    }

    private void OnToggle(string key, bool on)
    {
        if (_tp2Suppress) return;
        ApplyTp2Scalar?.Invoke(key, on ? 1 : 0);
        if (key == "emit_on") UpdateEmitEnabled();
    }

    private void CommitColor()
    {
        if (_tp2Suppress) return;
        if (!TryChannel(_cr.Text, out byte r) || !TryChannel(_cg.Text, out byte g) ||
            !TryChannel(_cb.Text, out byte b) || !TryChannel(_ca.Text, out byte a)) return;
        ApplyTp2Color?.Invoke(r, g, b, a);
        SetSwatch(new[] { r, g, b, a });
    }

    private static bool TryChannel(string text, out byte value)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0 && v <= 255)
        {
            value = (byte)v;
            return true;
        }
        value = 0;
        return false;
    }

    private void OnPathChanged()
    {
        if (_tp2Suppress || _pathCombo is null) return;
        string? sel = _pathCombo.SelectedItem as string;
        ApplyTp2Path?.Invoke(sel is null || sel == "(none)" || sel.EndsWith("  (missing)") ? null : sel);
    }

    // ------------------------------------------------ named behaviour-tail view ---

    private void ManageTail(ISceneElem? sel)
    {
        var src = (sel as SceneCross)?.Source;
        if (src is null || !SceneProjector.IsTailEditable(src))
        {
            _boundTail = null;
            _namedBox.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ReferenceEquals(src, _boundTail))   // rebuild the row structure only on a new record
        {
            _boundTail = src;
            var named = SceneProjector.FieldTailNamed(src);
            _namedFields = named?.Fields ?? new List<TailField>();
            _namedTail = named?.Tail ?? Array.Empty<byte>();
            _tailFrame = named?.FrameLen ?? 0;
            int unconsumed = _namedTail.Length - (named?.Consumed ?? 0);
            RebuildNamed();
            // No raw fallback exists: unnamed bytes are reported, never rendered as a hex lane, so a
            // gap in the grammar is visible as a fact to chase down instead of data to hand-edit.
            _tailNote.Text = unconsumed > 0
                ? $"{unconsumed} value{(unconsumed == 1 ? "" : "s")} at the end of this object aren't understood yet in {SceneProjector.TailGameName(src)} \u2014 the editor leaves them exactly as they are while they are still being worked out from the game's code."
                : "";
            _tailNote.Visibility = unconsumed > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        _namedBox.Visibility = _namedFields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FillNamed();
    }

    private void BuildNamedPanel()
    {
        var head = new TextBlock { Text = "Behaviour", FontWeight = FontWeights.SemiBold, Foreground = Brushes.MediumSeaGreen, Margin = new Thickness(0, 0, 0, 2) };
        var note = new TextBlock
        {
            Text = "What this object does in the game beyond sitting there: moving, following a path, walking a list of points, wandering an area, spawning things, linking to another object, fading through colours. Each group has a switch \u2014 turning one off takes that behaviour away from the object, turning it on adds it back with the game's own starting values. Some fields carry the game's own internal names because the shipped files use them \u2014 hover any name to see what it does. A change the game would not read back correctly is refused instead of written, so an edit here can never leave a broken level.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.TextMutedBrush(),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var scroller = new ScrollViewer
        {
            Content = _namedRows,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var outer = new StackPanel();
        outer.Children.Add(head);
        outer.Children.Add(note);
        outer.Children.Add(scroller);
        outer.Children.Add(_tailNote);
        _namedBox.Child = outer;
    }

    private void RebuildNamed()
    {
        _tailSuppress = true;
        _namedRows.Children.Clear();
        _namedCtl.Clear();
        _namedGate.Clear();
        _namedStruct.Clear();
        _namedRo.Clear();

        string? lastGroup = null;
        foreach (var f in _namedFields)
        {
            if (f.Group != lastGroup)
            {
                lastGroup = f.Group;
                _namedRows.Children.Add(new TextBlock
                {
                    Text = f.Group,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Foreground = Brushes.MediumSeaGreen,
                    Margin = new Thickness(0, 4, 0, 1),
                    ToolTip = UiText.FieldHint(f.Group),
                });
            }

            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            // R1: several of these labels are the attribute names the game's own loading code reads,
            // which are not self-explanatory; the hint hangs the meaning on both halves of the row.
            string? fHint = UiText.FieldHint(f.Name);
            var lbl = new TextBlock { Text = f.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = Theme.TextMutedBrush(), ToolTip = fHint };
            Grid.SetColumn(lbl, 0);
            g.Children.Add(lbl);

            if (f.Kind == TailKind.Gate && f.ReshapeableWithin(_tailFrame))
            {
                // The block's on/off switch. Ticking it inserts the block, unticking removes it; the
                // rows underneath appear / disappear with it because the walk decides what exists.
                var cb = new CheckBox
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(4, 0, 0, 0),
                    Content = "enabled",
                    Foreground = Theme.TextMutedBrush(),
                    FontSize = 11,
                    ToolTip = fHint,
                };
                var fld = f;
                cb.Click += (_, _) =>
                {
                    if (_tailSuppress) return;
                    ApplyTailGate?.Invoke(fld, cb.IsChecked == true);
                };
                Grid.SetColumn(cb, 1);
                g.Children.Add(cb);
                _namedGate[f.LaneOff] = cb;
            }
            else if (f.Kind is TailKind.Str or TailKind.Count && f.ReshapeableWithin(_tailFrame))
            {
                // A rename or a ramp-count change: same guarded rebuild, different value type.
                var box = new TextBox { Margin = new Thickness(4, 0, 0, 0), FontSize = 11, ToolTip = fHint };
                var fld = f;
                box.LostFocus += (_, _) => CommitStruct(fld, box);
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitStruct(fld, box); };
                Grid.SetColumn(box, 1);
                g.Children.Add(box);
                _namedStruct[f.LaneOff] = box;
            }
            else if (f.EditableWithin(_tailFrame))
            {
                var box = new TextBox { Margin = new Thickness(4, 0, 0, 0), FontSize = 11, ToolTip = fHint };
                var fld = f;   // capture the struct for the handlers
                box.LostFocus += (_, _) => CommitNamed(fld, box);
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitNamed(fld, box); };
                Grid.SetColumn(box, 1);
                g.Children.Add(box);
                _namedCtl[f.LaneOff] = (box, f);
            }
            else
            {
                // Only a field the walk recovered past this record's own byte frame stays read-only:
                // those bytes belong to the neighbour the framing splitter cut early on, so writing
                // them would edit two records at once. Everything named inside the frame is editable.
                var ro = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    Foreground = Theme.TextMutedBrush(),
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(4, 0, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = "Shown only \u2014 these values belong to the object next door in the file, so changing them here would change two objects at once.",
                };
                Grid.SetColumn(ro, 1);
                g.Children.Add(ro);
                _namedRo[f.LaneOff] = ro;
            }
            _namedRows.Children.Add(g);
        }
        _tailSuppress = false;
    }

    /// <summary>Re-read the bound record's tail bytes from the model. A scalar write patches the record
    /// in place, but <see cref="SceneProjector.FieldTailNamed"/> returns its own <em>copy</em> of the
    /// bytes, so anything the view derives from the cached <see cref="_namedTail"/> goes stale the moment
    /// an edit lands: the box would snap back to the pre-edit number even though the file has the new
    /// one (and a later comparison would be made against bytes that no longer exist). The row LAYOUT
    /// stays as built — a value write never changes the grammar, and the length-changing edits go
    /// through <see cref="InvalidateTail"/> instead.</summary>
    private void SyncTail()
    {
        if (_boundTail is null) return;
        var named = SceneProjector.FieldTailNamed(_boundTail);
        if (named is not null) _namedTail = named.Value.Tail ?? Array.Empty<byte>();
    }

    private void FillNamed()
    {
        SyncTail();
        foreach (var f in _namedFields)
        {
            object? v = TailSem.Value(_namedTail, f);
            if (_namedGate.TryGetValue(f.LaneOff, out var cb))
            {
                cb.IsChecked = TailEdit.GateOn(_namedTail, f);
            }
            else if (_namedStruct.TryGetValue(f.LaneOff, out var sb))
            {
                sb.Text = f.Kind == TailKind.Str
                    ? v as string ?? ""
                    : v is int cv ? cv.ToString(CultureInfo.InvariantCulture) : "";
            }
            else if (_namedCtl.TryGetValue(f.LaneOff, out var ctl))
            {
                ctl.Box.Text = f.Kind is TailKind.Int or TailKind.Bool
                    ? (v is int iv ? iv.ToString(CultureInfo.InvariantCulture) : "")
                    : (v is double dv && IsPrintableF64(dv) ? dv.ToString("0.#####", CultureInfo.InvariantCulture) : "");
            }
            else if (_namedRo.TryGetValue(f.LaneOff, out var ro))
            {
                ro.Text = f.Kind switch
                {
                    TailKind.Str => v as string ?? "",
                    TailKind.F64 or TailKind.F32 => v is double dv && IsPrintableF64(dv) ? dv.ToString("0.#####", CultureInfo.InvariantCulture) : "",
                    _ => v is int iv ? iv.ToString(CultureInfo.InvariantCulture) : "",   // Gate / Count
                };
            }
        }
    }

    // A structural row commits a resize: the shell rebuilds the tail, proves it against the reader and
    // the framing oracle, then re-shows this panel (the field list changed with the record's length). A
    // count that doesn't parse, or a name with non-ASCII in it, just restores what is really there.
    private void CommitStruct(TailField f, TextBox box)
    {
        if (_tailSuppress || _boundTail is null) return;
        SyncTail();
        if (f.Kind == TailKind.Str)
        {
            string text = box.Text.Trim();
            if (text == (TailSem.Value(_namedTail, f) as string ?? "")) return;   // unchanged: no undo step
            ApplyTailString?.Invoke(f, text);
            return;
        }
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && n != (TailSem.Value(_namedTail, f) as int? ?? -1))
            ApplyTailCount?.Invoke(f, n);
        else
            FillNamed();
    }

    // A named edit is an in-place write at the field's tail-relative offset, routed by the field's kind
    // (int / f64 / f32 / bool) through the shell, which clamps it to the record's own span. A parsing
    // failure just restores the current value rather than committing garbage. The restore re-reads the
    // record (SyncTail, inside FillNamed), so a write that applied keeps showing what was typed and only
    // a write the span guard refused visibly snaps back.
    private void CommitNamed(TailField f, TextBox box)
    {
        if (_tailSuppress || _boundTail is null) return;
        SyncTail();
        double val;
        if (f.Kind is TailKind.Int or TailKind.Bool)
        {
            if (!TryParseLaneInt(box.Text, out int iv)) { FillNamed(); return; }
            val = iv;
        }
        else if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
        {
            FillNamed(); return;
        }
        else val = dv;
        ApplyTailField?.Invoke(f, val);
        FillNamed();
    }

    // A decoded tail double is only worth printing when it is a real, finite scalar.
    private static bool IsPrintableF64(double d) =>
        !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e15;

    // Mirror the oracle's int(txt, 0): plain decimal, or 0x-hex when prefixed.
    private static bool TryParseLaneInt(string text, out int value)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    // --------------------------------------- Tasty Planet 1 object card ----

    private void BuildTp1Panel()
    {
        var head = new TextBlock { Text = "This object", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250)), Margin = new Thickness(0, 0, 0, 2) };
        var note = new TextBlock
        {
            Text = "Every setting the file stores on this object, under the names the game itself uses \u2014 hover a name for what it does. A value the game lists by name comes up as a choice, so you can only pick one it recognises. Every change undoes with Ctrl+Z.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.TextMutedBrush(),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var scroller = new ScrollViewer
        {
            Content = _tp1Rows,
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var outer = new StackPanel();
        outer.Children.Add(head);
        outer.Children.Add(note);
        outer.Children.Add(scroller);
        _tp1Box.Child = outer;
    }

    private static TastyEditor.Core.Levels.Tp1Handle? Tp1SourceOf(ISceneElem? sel) => sel switch
    {
        SceneCross cr => cr.Source as TastyEditor.Core.Levels.Tp1Handle,
        SceneLine sl => sl.Source as TastyEditor.Core.Levels.Tp1Handle,
        _ => null,
    };

    /// <summary>Show the card for a selected tp1 element (a marker or a path). Rebuilt only when the
    /// selection moves to a different element, so a commit that re-shows the same element (the shell
    /// re-projects after every tp1 write) refreshes its values without tearing down the row layout
    /// mid-edit.</summary>
    private void ManageTp1(ISceneElem? sel)
    {
        var th = Tp1SourceOf(sel);
        var node = th?.Node;
        if (th is null || node is null)
        {
            _boundTp1Path = "";
            _tp1Box.Visibility = Visibility.Collapsed;
            return;
        }
        string path = string.Join(",", th.Path);
        if (path != _boundTp1Path)
        {
            _boundTp1Path = path;
            RebuildTp1(th, node);
        }
        _tp1Box.Visibility = Visibility.Visible;
    }

    // Tags a node may carry many of: they are summarised (and edited in the view / as their own
    // selection), never inlined here, because a dense level can hold hundreds.
    private static readonly HashSet<string> Tp1BulkTags = new(StringComparer.Ordinal) { "tile", "pair" };
    // Tags worth inlining as their own editable sub-group (a controller's target lists, a schedule's
    // emits) rather than just counting.
    private static readonly HashSet<string> Tp1ListTags = new(StringComparer.Ordinal) { "affect", "dontaffect", "emit" };

    private void RebuildTp1(TastyEditor.Core.Levels.Tp1Handle th, TastyEditor.Core.Levels.Tp1Node node)
    {
        _tp1Suppress = true;
        _tp1Rows.Children.Clear();
        Tp1Element(th, node, 0);
        _tp1Suppress = false;
    }

    /// <summary>Render one element's attributes, then (shallowly) its list children. Depth is capped
    /// so an emitter → controller → affect chain reads as a nested group without running away.</summary>
    private void Tp1Element(TastyEditor.Core.Levels.Tp1Handle th, TastyEditor.Core.Levels.Tp1Node node, int depth)
    {
        var title = new TextBlock
        {
            Text = Tp1Title(node.Tag),
            FontWeight = depth == 0 ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = depth == 0 ? 12 : 11,
            Foreground = depth == 0 ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) : Theme.TextMutedBrush(),
            Margin = new Thickness(0, depth == 0 ? 0 : 6, 0, 2),
        };
        _tp1Rows.Children.Add(title);

        if (node.Attrs.Count == 0)
            _tp1Rows.Children.Add(new TextBlock { Text = "(no settings)", FontSize = 10, Foreground = Theme.TextMutedBrush(), Margin = new Thickness(0, 0, 0, 2) });

        foreach (var a in node.Attrs)
            Tp1AttrRow(th, node, a);

        // Bulk children (tiles, path segments) get a one-line count, not a group per child.
        var bulk = node.Children.Where(c => Tp1BulkTags.Contains(c.Tag)).GroupBy(c => c.Tag)
                                .ToDictionary(g => g.Key, g => g.Count());
        foreach (var (tag, n) in bulk)
            _tp1Rows.Children.Add(new TextBlock
            {
                Text = tag == "tile" ? $"{n} tiles \u2014 click one in the view to edit it, or paint it from the sprite list."
                                     : $"{n} path segments \u2014 drag their points in the view, double-click the line to add one, right-click a point to remove it.",
                FontSize = 10, Foreground = Theme.TextMutedBrush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 3),
            });

        if (depth < 2)
        {
            foreach (var child in node.Children)
            {
                // A schedule is just a wrapper for its emits: descend through it without its own title.
                if (child.Tag == "scheduledemit")
                {
                    foreach (var e in child.Children.Where(c => c.Tag == "emit"))
                        Tp1Child(e, depth + 1);
                    continue;
                }
                if (Tp1ListTags.Contains(child.Tag)) Tp1Child(child, depth + 1);
                else if (child.Tag == "controller" || child.Tag == "scheduledemit" || child.Tag == "randomemit")
                    Tp1Element(new TastyEditor.Core.Levels.Tp1Handle(node.Owner, child), child, depth + 1);
            }
        }

        Tp1AddRow(node);
    }

    // One inlined list child (an affect / a timed emit): its attributes, plus a remove control.
    private void Tp1Child(TastyEditor.Core.Levels.Tp1Node child, int depth)
    {
        var th = new TastyEditor.Core.Levels.Tp1Handle(child.Owner, child);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        head.Children.Add(new TextBlock
        {
            Text = Tp1Title(child.Tag), FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextMutedBrush(), VerticalAlignment = VerticalAlignment.Center,
        });
        var rm = new Button
        {
            Content = "Remove", FontSize = 10, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Take this out of the level.",
        };
        var captured = th;
        rm.Click += (_, _) => { if (!_tp1Suppress) ApplyTp1Remove?.Invoke(captured); };
        head.Children.Add(rm);
        _tp1Rows.Children.Add(head);
        if (child.Attrs.Count == 0)
            _tp1Rows.Children.Add(new TextBlock { Text = "(no settings)", FontSize = 10, Foreground = Theme.TextMutedBrush(), Margin = new Thickness(0, 0, 0, 2) });
        foreach (var a in child.Attrs)
            Tp1AttrRow(th, child, a);
    }

    private void Tp1AttrRow(TastyEditor.Core.Levels.Tp1Handle th, TastyEditor.Core.Levels.Tp1Node node, TastyEditor.Core.Levels.Tp1Attr a)
    {
        string? hint = UiText.FieldHint(a.Key);
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        var lb = new TextBlock { Text = a.Key, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = Theme.TextMutedBrush(), ToolTip = hint };
        Grid.SetColumn(lb, 0); g.Children.Add(lb);

        FrameworkElement ctl;
        string[]? opts = Tp1Options(node, a.Key);
        if (opts is not null)
        {
            var combo = new ComboBox { IsEditable = true, FontSize = 11, Margin = new Thickness(4, 0, 0, 0), ToolTip = hint, ItemContainerStyle = Theme.ComboItemInkStyle() };
            Theme.StyleCombo(combo);
            foreach (var o in opts) combo.Items.Add(o);
            combo.Text = a.Value;
            var boxHandle = th; var key = a.Key;
            combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
            {
                if (_tp1Suppress) return;
                var t = combo.Text.Trim();
                if (t != a.Value) ApplyTp1Attr?.Invoke(boxHandle, key, t);
            }));
            ctl = combo;
        }
        else
        {
            var tb = new TextBox { Text = a.Value, FontSize = 11, Margin = new Thickness(4, 0, 0, 0), ToolTip = hint };
            var boxHandle = th; var key = a.Key; var orig = a.Value;
            void Commit() { if (_tp1Suppress) return; var t = tb.Text.Trim(); if (t != orig) ApplyTp1Attr?.Invoke(boxHandle, key, t); }
            tb.LostFocus += (_, _) => Commit();
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            ctl = tb;
        }
        Grid.SetColumn(ctl, 1); g.Children.Add(ctl);
        _tp1Rows.Children.Add(g);
    }

    // Add-a-child controls, offered only on the elements that actually own such a list.
    private void Tp1AddRow(TastyEditor.Core.Levels.Tp1Node node)
    {
        string tag = node.Tag;
        if (tag is not ("controller" or "emitter")) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var th = new TastyEditor.Core.Levels.Tp1Handle(node.Owner, node);
        void Btn(string label, string childTag, string tip)
        {
            var b = new Button { Content = label, FontSize = 10, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            var captured = th; var t = childTag;
            b.Click += (_, _) => { if (!_tp1Suppress) ApplyTp1Add?.Invoke(captured, t); };
            row.Children.Add(b);
        }
        if (tag == "controller")
        {
            Btn("Add target", "affect", "Let this behaviour act on one more kind of object.");
            Btn("Add excluded", "dontaffect", "Keep this behaviour away from one more kind of object.");
        }
        else if (tag == "emitter")
            Btn("Add timed emit", "emit", "Make the spawner release one more item at a set moment.");
        if (row.Children.Count > 0) _tp1Rows.Children.Add(row);
    }

    /// <summary>The game's own choices for a few named attributes, or null for a free-text field.
    /// An emitter / controller type and a target condition are fixed sets; an object type (entitydef)
    /// and a tile picture (name) come from the loaded definitions so only a real one can be picked.</summary>
    private string[]? Tp1Options(TastyEditor.Core.Levels.Tp1Node node, string key)
    {
        switch (key)
        {
            case "type" when node.Tag == "emitter": return new[] { "shot", "side", "spot" };
            case "type" when node.Tag == "controller":
                return new[] { "attack", "avoid", "evade", "leavelevel", "movedirection", "pathfollow", "stationary" };
            case "conditon":
            case "condition":
                return new[] { "all", "larger", "largerequal", "none", "smaller", "smallerequal" };
            case "entitydef":
                return _tp1Names?.Invoke("entity");
            case "name" when node.Tag == "tile":
                return _tp1Names?.Invoke("tile");
            default: return null;
        }
    }

    // The plain word for a tp1 element type, shown as the card's group title (never the file's tag).
    private static string Tp1Title(string tag) => tag switch
    {
        "level" => "Level settings",
        "goo" => "Player start",
        "emitter" => "Spawner",
        "controller" => "Behaviour",
        "affect" => "Acts on",
        "dontaffect" => "Never acts on",
        "scheduledemit" => "Timed releases",
        "emit" => "Release",
        "randomemit" => "Random releases",
        "tilelayers" => "Background layers",
        "tilelayer" => "Background layer",
        "tile" => "Background tile",
        "pair" => "Path segment",
        "left" or "right" or "top" or "bottom" => tag + " edge",
        "tip" => "Tip",
        "comic" => "Comic",
        "frame" => "Frame",
        "end" => "Ending",
        "path" => "Path",
        _ => tag,
    };
}
