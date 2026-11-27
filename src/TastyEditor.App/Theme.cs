using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TastyEditor.App;

/// <summary>R3 theme layer: the editor's palette, spacing/typography scale and control styles,
/// built entirely in code (the app has no XAML build task — <see cref="App"/> explains why).
///
/// <para>One place decides what "surface", "border", "accent" and "text" mean, so the panels stop
/// each hard-coding their own greys. Two palettes ship — dark (the default; the viewport canvas is
/// near-black) and light — and switching is live: <see cref="Apply"/> rebuilds the merged
/// <see cref="ResourceDictionary"/> with fresh brushes and styles, which makes every implicitly-
/// styled control re-resolve, while the chrome surfaces the shell builds by hand are pointed at the
/// brushes by resource key (see <see cref="Ref"/>) so they follow too.</para>
///
/// <para>Scope is deliberate: the widgets that theme cleanly in code (window, buttons, text boxes,
/// tabs, the tool bar, tooltips, the menu bar, check boxes, separators) are re-styled; the combo box
/// drop-down and the menu's submenu popup keep their functional platform chrome rather than risk a
/// hand-built template rendering an invisible menu. The viewport canvas keeps its own dark background
/// in both themes on purpose — it is the level being edited, not application chrome.</para></summary>
internal static class Theme
{
    // ---- resource keys (the live-toggle contract for hand-built chrome surfaces) ----
    public const string KWindow = "Tasty.Window";
    public const string KSurface = "Tasty.Surface";
    public const string KSurfaceAlt = "Tasty.SurfaceAlt";
    public const string KBorder = "Tasty.Border";
    public const string KBorderStrong = "Tasty.BorderStrong";
    public const string KText = "Tasty.Text";
    public const string KTextMuted = "Tasty.TextMuted";
    public const string KAccent = "Tasty.Accent";
    public const string KAccentSoft = "Tasty.AccentSoft";
    public const string KField = "Tasty.Field";
    public const string KHover = "Tasty.Hover";
    /// <summary>Keyed style for a flat icon-only tool-bar button (the implicit Button style is the
    /// padded, bordered dialog button — too heavy for a compact toolbar).</summary>
    public const string KToolButton = "Tasty.ToolButton";

    // ---- spacing / typography / corner scale ----
    public const double Corner = 5;         // rounded card + control corner radius, device-independent px
    public const double Gap = 8;            // standard gap between grouped controls
    public const double GapSmall = 4;       // tight gap inside a row
    public const double Pad = 10;           // card inner padding
    public const double FontBody = 12.5;
    public const double FontSection = 12.5; // group-card header
    public const double FontTitle = 15;     // start-screen headings

    /// <summary>True while the dark palette is active (the default).</summary>
    public static bool IsDark { get; private set; } = true;

    private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // Live brush getters for one-off construction (start screen, viewport). These bake the current
    // value; hand-built chrome that must follow a runtime toggle uses Ref(...) instead.
    public static SolidColorBrush WindowBrush() => B(Pal.Window);
    public static SolidColorBrush SurfaceBrush() => B(Pal.Surface);
    public static SolidColorBrush SurfaceAltBrush() => B(Pal.SurfaceAlt);
    public static SolidColorBrush BorderBrush() => B(Pal.Border);
    public static SolidColorBrush TextBrush() => B(Pal.Text);
    public static SolidColorBrush TextMutedBrush() => B(Pal.TextMuted);
    public static SolidColorBrush AccentBrush() => B(Pal.Accent);

    private static Palette Pal = DarkPal;

    private readonly struct Palette
    {
        public readonly Color Window, Surface, SurfaceAlt, Border, BorderStrong, Text, TextMuted,
                              Accent, AccentSoft, Field, Hover;
        public Palette(Color w, Color s, Color sa, Color bd, Color bs, Color tx, Color tm,
                       Color ac, Color acs, Color fd, Color hv)
        {
            Window = w; Surface = s; SurfaceAlt = sa; Border = bd; BorderStrong = bs;
            Text = tx; TextMuted = tm; Accent = ac; AccentSoft = acs; Field = fd; Hover = hv;
        }
    }

    // Dark: calm slate surfaces sitting just above the viewport's rgb(28,30,36), one cool accent.
    // The first cut kept every level within ~10 units of the last, so buttons and fields read as
    // flat text on the card (visual gate). Steps are now real: raised controls clearly lighter than
    // the card, card clearly lighter than the window, fields dark with a strong outline.
    private static readonly Palette DarkPal = new(
        w: Color.FromRgb(20, 22, 27),    // window
        s: Color.FromRgb(37, 41, 50),    // panel surface / cards
        sa: Color.FromRgb(58, 63, 74),   // raised chrome: button fills, toolbar/status bands
        bd: Color.FromRgb(80, 86, 98),   // hairline border (clearly visible on both surfaces)
        bs: Color.FromRgb(104, 111, 124),// emphasized border (field outlines)
        tx: Color.FromRgb(228, 231, 237),
        tm: Color.FromRgb(160, 166, 178),
        ac: Color.FromRgb(96, 165, 250),
        acs: Color.FromArgb(60, 96, 165, 250),
        fd: Color.FromRgb(26, 29, 35),   // fields: darker than the card, plus the strong outline
        hv: Color.FromRgb(70, 77, 91));

    // Light: warm-neutral paper with the same accent hue, so a switch is not a different app.
    // Contrast is tuned up deliberately: the first cut used near-white borders and a mid-grey muted
    // text, which read as grey-on-white with no outline anywhere (the visual gate caught it).
    private static readonly Palette LightPal = new(
        w: Color.FromRgb(243, 244, 247),
        s: Color.FromRgb(255, 255, 255),
        sa: Color.FromRgb(228, 231, 237),  // raised chrome + button fill: clearly a chip on paper
        bd: Color.FromRgb(176, 182, 191),  // hairline border (visible against both white and paper)
        bs: Color.FromRgb(140, 147, 158),  // emphasized border (field outlines)
        tx: Color.FromRgb(28, 31, 38),
        tm: Color.FromRgb(84, 90, 101),    // muted text still clears 4.5:1 on white
        ac: Color.FromRgb(37, 99, 235),
        acs: Color.FromArgb(50, 37, 99, 235),
        fd: Color.FromRgb(252, 252, 254),
        hv: Color.FromRgb(222, 227, 236));

    /// <summary>Point one dependency property of a hand-built element at a theme brush by key, so it
    /// follows a runtime toggle. <see cref="Apply"/> must have run so the key exists.</summary>
    public static void Ref(FrameworkElement e, DependencyProperty p, string key)
        => e.SetResourceReference(p, key);

    private static ResourceDictionary? _applied;

    /// <summary>Install (or re-install, on a theme switch) the palette + control styles into the
    /// application's resources. Safe to call repeatedly; the previous dictionary is replaced.</summary>
    public static void Apply(Application app, bool dark)
    {
        IsDark = dark;
        Pal = dark ? DarkPal : LightPal;

        var rd = new ResourceDictionary
        {
            [KWindow] = B(Pal.Window),
            [KSurface] = B(Pal.Surface),
            [KSurfaceAlt] = B(Pal.SurfaceAlt),
            [KBorder] = B(Pal.Border),
            [KBorderStrong] = B(Pal.BorderStrong),
            [KText] = B(Pal.Text),
            [KTextMuted] = B(Pal.TextMuted),
            [KAccent] = B(Pal.Accent),
            [KAccentSoft] = B(Pal.AccentSoft),
            [KField] = B(Pal.Field),
            [KHover] = B(Pal.Hover),
        };

        foreach (var s in BuildStyles()) rd[s.TargetType] = s;
        rd[KToolButton] = ToolButtonStyle();

        if (_applied is not null) app.Resources.MergedDictionaries.Remove(_applied);
        app.Resources.MergedDictionaries.Add(rd);
        _applied = rd;
    }

    // ---- helpers ----
    private static Style S<T>() => new(typeof(T));
    private static void Set(Style s, DependencyProperty p, object v) => s.Setters.Add(new Setter(p, v));
    private static FrameworkElementFactory El(Type t) => new(t);
    private static FrameworkElementFactory Named(FrameworkElementFactory f, string n) { f.Name = n; return f; }

    // Inside a ControlTemplate, the Border must read the CONTROL's Background/BorderBrush from its
    // templated parent. A plain Binding walks the element's own (inherited, unrelated) DataContext
    // and silently yields null, which leaves the rest state transparent - only trigger repaints
    // showed, so the hover looked themed while the idle control vanished into the surface.
    // RelativeSource=TemplatedParent is the code-built equivalent of XAML's {TemplateBinding ...}.
    private static FrameworkElementFactory TBind(FrameworkElementFactory f, DependencyProperty p, string src)
    { f.SetBinding(p, new System.Windows.Data.Binding(src) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); return f; }

    private static Style[] BuildStyles() => new Style[]
    {
        WindowStyle(), TextBlockStyle(), LabelStyle(), ButtonStyle(), ToggleStyle(), TextBoxStyle(),
        ComboBoxStyle(), ComboBoxItemStyle(), CheckBoxStyle(), MenuStyle(), MenuItemStyle(),
        SeparatorStyle(),
        TabStyle(), TabItemStyle(), ToolBarStyle(), ToolBarTrayStyle(), GridSplitterStyle(), ToolTipStyle(),
    };

    private static Style WindowStyle()
    {
        var s = S<Window>();
        Set(s, Window.BackgroundProperty, B(Pal.Window));
        Set(s, Window.ForegroundProperty, B(Pal.Text));
        // NOTE: deliberately NO TextElement.Foreground here. It is an inheritable attached property
        // that leaks past control boundaries into platform-chrome popups - the native menu dropdowns
        // read their ink through that inheritance, so setting it light made black-on-white menu text
        // go light-on-white (the R3 regression). Controls that need theme ink (TextBlock/Label/...)
        // have their own implicit styles.
        Set(s, TextElement.FontSizeProperty, FontBody);
        Set(s, TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
        return s;
    }

    private static Style TextBlockStyle()
    {
        var s = S<TextBlock>();
        Set(s, TextBlock.ForegroundProperty, B(Pal.Text));
        Set(s, TextBlock.FontSizeProperty, FontBody);
        return s;
    }

    private static Style LabelStyle()
    {
        var s = S<Label>();
        Set(s, Label.ForegroundProperty, B(Pal.Text));
        Set(s, Label.FontSizeProperty, FontBody);
        Set(s, Label.PaddingProperty, new Thickness(0, 2, 0, 2));
        return s;
    }

    // A flat, rounded button: fill/border/text from the palette, a lighter hover, an accent press,
    // and a dimmed disabled state. Access keys work through the ContentPresenter.
    private static Style ButtonStyle()
    {
        var bd = Named(El(typeof(Border)), "bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(Corner));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.SnapsToDevicePixelsProperty, true);
        TBind(bd, Border.BackgroundProperty, "Background");
        TBind(bd, Border.BorderBrushProperty, "BorderBrush");
        bd.SetValue(Border.PaddingProperty, new Thickness(10, 5, 10, 5));

        var cp = El(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        bd.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        tpl.Triggers.Add(Repaint("bd", UIElement.IsMouseOverProperty, true, Pal.Hover, Pal.BorderStrong));
        tpl.Triggers.Add(Repaint("bd", Button.IsPressedProperty, true, Pal.AccentSoft, Pal.Accent));

        var s = S<Button>();
        Set(s, Control.BackgroundProperty, B(Pal.SurfaceAlt));
        Set(s, Control.BorderBrushProperty, B(Pal.Border));
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, Control.FontSizeProperty, FontBody);
        Set(s, FrameworkElement.CursorProperty, Cursors.Hand);
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }

    private static Style ToggleStyle()
    {
        var bd = Named(El(typeof(Border)), "bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(Corner));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        TBind(bd, Border.BackgroundProperty, "Background");
        TBind(bd, Border.BorderBrushProperty, "BorderBrush");
        bd.SetValue(Border.PaddingProperty, new Thickness(10, 5, 10, 5));

        var cp = El(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(ToggleButton)) { VisualTree = bd };
        tpl.Triggers.Add(Repaint("bd", ToggleButton.IsCheckedProperty, true, Pal.AccentSoft, Pal.Accent));
        tpl.Triggers.Add(Repaint("bd", UIElement.IsMouseOverProperty, true, Pal.Hover, Pal.BorderStrong));

        var s = S<ToggleButton>();
        Set(s, Control.BackgroundProperty, B(Pal.SurfaceAlt));
        Set(s, Control.BorderBrushProperty, B(Pal.Border));
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }

    // A template trigger that repaints the named border's background + border when a state holds.
    private static TriggerBase Repaint(string name, DependencyProperty prop, object value, Color bg, Color border)
    {
        var t = new Trigger { Property = prop, Value = value };
        t.Setters.Add(new Setter(Border.BackgroundProperty, B(bg), name));
        t.Setters.Add(new Setter(Border.BorderBrushProperty, B(border), name));
        return t;
    }

    // Rounded text field: a themed border around the required PART_ContentHost scroll viewer.
    private static Style TextBoxStyle()
    {
        var bd = Named(El(typeof(Border)), "bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        TBind(bd, Border.BackgroundProperty, "Background");
        TBind(bd, Border.BorderBrushProperty, "BorderBrush");
        bd.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));

        var host = Named(El(typeof(ScrollViewer)), "PART_ContentHost");
        host.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        bd.AppendChild(host);

        var tpl = new ControlTemplate(typeof(TextBox)) { VisualTree = bd };
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, B(Pal.Accent), "bd"));
        tpl.Triggers.Add(focus);

        var s = S<TextBox>();
        Set(s, Control.BackgroundProperty, B(Pal.Field));
        Set(s, Control.BorderBrushProperty, B(Pal.BorderStrong));
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, TextBoxBase.CaretBrushProperty, B(Pal.Text));
        Set(s, TextBoxBase.SelectionBrushProperty, B(Pal.AccentSoft));
        Set(s, Control.FontSizeProperty, FontBody);
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }

    /// <summary>The dropdown-row ink style, exposed so a specific ComboBox can be given it directly
    /// (a local ItemContainerStyle value) rather than relying on the implicit ComboBox style reaching
    /// the control - the sprite picker sets this on its own instance to be certain it lands.</summary>
    internal static Style ComboItemInkStyle() => ComboBoxItemStyle();

    /// <summary>Dark ink for ComboBox text that sits on the platform's always-light chrome: the
    /// dropdown rows and the collapsed editable box both render on a light surface in both themes,
    /// so light theme text would be invisible there - both use this instead.</summary>
    public static SolidColorBrush ComboInkBrush() => B(Color.FromRgb(24, 26, 32));

    /// <summary>Make one ComboBox readable in dark mode. An implicit ComboBoxItem style and the
    /// implicit ComboBox style's ItemContainerStyle setter were BOTH proven not to reach a generated
    /// popup container here - only a LOCAL ItemContainerStyle does - so every combo must be tagged
    /// explicitly. The dark ink foreground is for the collapsed box (always-light platform chrome);
    /// the row template carries its own ink for the popup. Idempotent; call right after construction.</summary>
    public static void StyleCombo(ComboBox c)
    {
        c.ItemContainerStyle = ComboBoxItemStyle();
        c.ItemTemplate = ComboSelectionTemplate();
        c.Foreground = ComboInkBrush();
    }

    // The closed, non-editable combo shows its selected item through a ContentPresenter that ignores
    // the control's own Foreground (the same ContentPresenter-generated-text precedence that defeated
    // the popup rows). The box chrome is always light, so the light theme ink reads as gray there. An
    // ItemTemplate whose TextBlock carries a LOCAL dark ink is the bulletproof lever; the ComboBox
    // mirrors it into SelectionBoxItemTemplate, so the collapsed box renders through it too.
    private static DataTemplate ComboSelectionTemplate()
    {
        var tb = El(typeof(TextBlock));
        tb.SetValue(TextBlock.ForegroundProperty, ComboInkBrush());
        tb.SetValue(TextBlock.FontSizeProperty, FontBody);
        tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        return new DataTemplate(typeof(string)) { VisualTree = tb };
    }

    // ComboBox: recolour the closed box. The drop-down popup panel stays platform-rendered (always
    // light), but its rows get an explicit ItemContainerStyle (see ComboBoxItemStyle) so the sprite
    // list is readable in dark mode. An implicit ComboBoxItem style was NOT reaching the popup's
    // generated containers, so the style is attached directly here - the guaranteed path.
    private static Style ComboBoxStyle()
    {
        var s = S<ComboBox>();
        Set(s, Control.BackgroundProperty, B(Pal.Field));
        Set(s, Control.BorderBrushProperty, B(Pal.BorderStrong));
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, Control.FontSizeProperty, FontBody);
        Set(s, Control.PaddingProperty, new Thickness(6, 3, 6, 3));
        Set(s, ItemsControl.ItemContainerStyleProperty, ComboBoxItemStyle());
        return s;
    }

    // The dropdown rows. The yellow-background probe proved this template IS applied to the popup
    // containers, yet setting TextElement.Foreground on the ContentPresenter did NOT recolour the
    // auto-generated string text (ContentPresenter's implicit text view ignores the inherited/attached
    // foreground here). The bulletproof lever is an explicit TextBlock with Foreground set as a LOCAL
    // value - the highest WPF precedence - bound to the item content. The popup panel stays platform
    // light, so the ink is pinned dark; the picked row flips to white on the accent fill.
    private static Style ComboBoxItemStyle()
    {
        var bd = Named(El(typeof(Border)), "bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(7, 4, 7, 4));
        var txt = Named(El(typeof(TextBlock)), "txt");
        txt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Content")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        txt.SetValue(TextBlock.ForegroundProperty, ComboInkBrush());
        txt.SetValue(TextBlock.FontSizeProperty, FontBody);
        txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        bd.AppendChild(txt);
        var tpl = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = bd };
        var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Border.BackgroundProperty, B(Pal.Accent), "bd"));
        sel.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White, "txt"));
        tpl.Triggers.Add(sel);

        var s = new Style(typeof(ComboBoxItem));
        Set(s, Control.FontSizeProperty, FontBody);
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }

    private static Style CheckBoxStyle()
    {
        var s = S<CheckBox>();
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, Control.FontSizeProperty, FontBody);
        return s;
    }

    /// <summary>Theme a ListView/GridView pair. The list surface follows the palette by key so it
    /// stops rendering on the platform's white; the column headers get a dark template through
    /// GridView.ColumnHeaderContainerStyle - the local hook that reliably reaches the auto-generated
    /// GridViewColumnHeader containers (an implicit style does not, exactly like the combo popup).
    /// The cells are TextBoxes and already pick up the dark field style.</summary>
    public static void StyleList(ListView lv, GridView gv)
    {
        Ref(lv, Control.BackgroundProperty, KSurface);
        Ref(lv, Control.ForegroundProperty, KText);
        lv.BorderBrush = BorderBrush();
        gv.ColumnHeaderContainerStyle = GridViewColumnHeaderStyle();
    }

    /// <summary>Theme a plain ListBox. Unlike the combo popup (platform-hosted, always light), the
    /// list surface is ours to paint, so it follows the palette (dark field, light ink) and never
    /// renders on the platform's white. The rows get a LOCAL-ink template for the same reason the
    /// combo rows do: an implicit TextBlock style outranks an inherited trigger ink, so the picked
    /// row would otherwise stay light-on-accent. Items are shown as their string content.</summary>
    public static void StyleBox(ListBox lb)
    {
        Ref(lb, Control.BackgroundProperty, KSurface);
        Ref(lb, Control.ForegroundProperty, KText);
        lb.BorderBrush = BorderBrush();
        lb.ItemContainerStyle = ListBoxItemStyle();
    }

    private static Style ListBoxItemStyle()
    {
        var bd = Named(El(typeof(Border)), "bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(7, 4, 7, 4));
        var txt = Named(El(typeof(TextBlock)), "txt");
        txt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Content")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        txt.SetValue(TextBlock.ForegroundProperty, B(Pal.Text));
        txt.SetValue(TextBlock.FontSizeProperty, FontBody);
        txt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        bd.AppendChild(txt);
        var tpl = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bd };
        var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Border.BackgroundProperty, B(Pal.Accent), "bd"));
        sel.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White, "txt"));
        tpl.Triggers.Add(sel);

        var s = new Style(typeof(ListBoxItem));
        Set(s, Control.FontSizeProperty, FontBody);
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }

    private static Style GridViewColumnHeaderStyle()
    {
        var bd = El(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, B(Pal.SurfaceAlt));
        bd.SetValue(Border.BorderBrushProperty, B(Pal.Border));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1));
        bd.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
        var cp = El(typeof(ContentPresenter));
        cp.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Header")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        var tpl = new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = bd };
        var s = new Style(typeof(GridViewColumnHeader));
        Set(s, Control.TemplateProperty, tpl);
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, Control.FontSizeProperty, FontBody);
        return s;
    }

    private static Style SeparatorStyle()
    {
        var s = S<Separator>();
        Set(s, Control.BackgroundProperty, B(Pal.Border));
        Set(s, FrameworkElement.HeightProperty, 1.0);
        Set(s, FrameworkElement.MarginProperty, new Thickness(4, 5, 4, 5));
        return s;
    }

    private static Style MenuStyle()
    {
        var s = S<Menu>();
        Set(s, Control.BackgroundProperty, B(Pal.Surface));
        // No Foreground here: it leaks down into the native submenu popups (see MenuItemStyle).
        return s;
    }

    // Pre-redesign contract, restored for the popups. Two defects came from the platform submenu
    // chrome: (1) the app-wide implicit TextBlock style paints Foreground = Pal.Text (light in the
    // dark theme) onto the native popup's generated TextBlocks, and a style on the inner element
    // outranks any MenuItem ink; (2) the platform submenu PANEL reserves a left icon/check column
    // with a vertical rule, which reads as "a checkbox slot for every row". Both are fixed by taking
    // over the templates: leaf rows carry a LOCAL black ink (SubmenuItemTemplate) and the popup
    // panel is re-hosted as a plain Border+ItemsPresenter with no icon column (TopLevelHeader /
    // SubmenuHeader templates). The top-level bar entries keep light ink because they sit on the
    // dark Menu surface. Popup open/close stays wired through IsSubmenuOpen so behaviour is intact.
    private static Style MenuItemStyle()
    {
        var s = S<MenuItem>();
        var top = new Trigger { Property = MenuItem.RoleProperty, Value = MenuItemRole.TopLevelHeader };
        top.Setters.Add(new Setter(Control.TemplateProperty, TopLevelHeaderTemplate()));
        s.Triggers.Add(top);
        var topItem = new Trigger { Property = MenuItem.RoleProperty, Value = MenuItemRole.TopLevelItem };
        topItem.Setters.Add(new Setter(Control.ForegroundProperty, B(Pal.Text)));
        s.Triggers.Add(topItem);
        var head = new Trigger { Property = MenuItem.RoleProperty, Value = MenuItemRole.SubmenuHeader };
        head.Setters.Add(new Setter(Control.TemplateProperty, SubmenuHeaderTemplate()));
        s.Triggers.Add(head);
        var leaf = new Trigger { Property = MenuItem.RoleProperty, Value = MenuItemRole.SubmenuItem };
        leaf.Setters.Add(new Setter(Control.TemplateProperty, SubmenuItemTemplate()));
        s.Triggers.Add(leaf);
        return s;
    }

    // The submenu popup body: a single native-looking panel (flat light fill + 1px border) that
    // hosts ONLY an ItemsPresenter - no icon column, so no vertical rule and no phantom check gutter.
    private static FrameworkElementFactory SubmenuPopup(PlacementMode place)
    {
        var pop = Named(El(typeof(Popup)), "pop");
        pop.SetBinding(Popup.IsOpenProperty, new System.Windows.Data.Binding("IsSubmenuOpen")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent, Mode = System.Windows.Data.BindingMode.TwoWay });
        pop.SetValue(Popup.PlacementProperty, place);
        pop.SetBinding(Popup.PlacementTargetProperty, new System.Windows.Data.Binding { ElementName = "hdr" });
        pop.SetValue(Popup.AllowsTransparencyProperty, false);

        var panel = Named(El(typeof(Border)), "panel");
        panel.SetValue(Border.BackgroundProperty, B(Color.FromRgb(240, 240, 240)));
        panel.SetValue(Border.BorderBrushProperty, B(Color.FromRgb(153, 153, 153)));
        panel.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        panel.SetValue(Border.PaddingProperty, new Thickness(2));
        panel.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);
        panel.AppendChild(El(typeof(ItemsPresenter)));

        pop.AppendChild(panel);
        return pop;
    }

    // Top-level bar entry (File/Edit/View): a header cell with light ink + a Bottom-placed popup.
    private static ControlTemplate TopLevelHeaderTemplate()
    {
        var root = El(typeof(Grid));
        var hdr = Named(El(typeof(Border)), "hdr");
        hdr.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        hdr.SetValue(Border.PaddingProperty, new Thickness(9, 4, 9, 4));
        var ht = Named(El(typeof(AccessText)), "ht");
        ht.SetBinding(AccessText.TextProperty, new System.Windows.Data.Binding("Header")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        ht.SetValue(TextBlock.ForegroundProperty, B(Pal.Text));
        ht.SetValue(TextBlock.FontSizeProperty, FontBody);
        ht.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        hdr.AppendChild(ht);
        root.AppendChild(hdr);
        root.AppendChild(SubmenuPopup(PlacementMode.Bottom));

        var tpl = new ControlTemplate(typeof(MenuItem)) { VisualTree = root };
        var hl = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hl.Setters.Add(new Setter(Border.BackgroundProperty, B(Pal.AccentSoft), "hdr"));
        tpl.Triggers.Add(hl);
        var open = new Trigger { Property = MenuItem.IsSubmenuOpenProperty, Value = true };
        open.Setters.Add(new Setter(Border.BackgroundProperty, B(Pal.AccentSoft), "hdr"));
        tpl.Triggers.Add(open);
        return tpl;
    }

    // A popup row that itself opens a nested submenu (Open recent): black ink + a right arrow, with a
    // Right-placed popup panel.
    private static ControlTemplate SubmenuHeaderTemplate()
    {
        var root = El(typeof(Grid));
        var hdr = Named(El(typeof(Border)), "hdr");
        hdr.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        hdr.SetValue(Border.PaddingProperty, new Thickness(9, 3, 11, 3));
        var dp = El(typeof(DockPanel));
        dp.SetValue(DockPanel.LastChildFillProperty, true);
        var arrow = El(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "\u25B6");
        arrow.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
        arrow.SetValue(TextBlock.FontSizeProperty, FontBody);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(18, 0, 0, 0));
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(DockPanel.DockProperty, Dock.Right);
        var ht = Named(El(typeof(AccessText)), "ht");
        ht.SetBinding(AccessText.TextProperty, new System.Windows.Data.Binding("Header")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        ht.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
        ht.SetValue(TextBlock.FontSizeProperty, FontBody);
        ht.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dp.AppendChild(arrow);
        dp.AppendChild(ht);
        hdr.AppendChild(dp);
        root.AppendChild(hdr);
        root.AppendChild(SubmenuPopup(PlacementMode.Right));

        var tpl = new ControlTemplate(typeof(MenuItem)) { VisualTree = root };
        var hl = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hl.Setters.Add(new Setter(Border.BackgroundProperty, B(Color.FromRgb(204, 232, 255)), "hdr"));
        tpl.Triggers.Add(hl);
        return tpl;
    }

    // A submenu leaf row that reads native: transparent fill (the platform panel shows through),
    // black header text via a local value, an inline tick only when checked (no reserved gutter),
    // a right-aligned shortcut, and the classic light-blue hover.
    private static ControlTemplate SubmenuItemTemplate()
    {
        var black = Brushes.Black;
        var bd = Named(El(typeof(Border)), "bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(9, 3, 11, 3));
        bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var dp = El(typeof(DockPanel));
        dp.SetValue(DockPanel.LastChildFillProperty, true);

        var chk = Named(El(typeof(TextBlock)), "chk");
        chk.SetValue(TextBlock.TextProperty, "\u2713 ");
        chk.SetValue(TextBlock.ForegroundProperty, black);
        chk.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        chk.SetValue(DockPanel.DockProperty, Dock.Left);

        var gest = Named(El(typeof(TextBlock)), "gest");
        gest.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("InputGestureText")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        gest.SetValue(TextBlock.ForegroundProperty, B(Color.FromRgb(90, 95, 105)));
        gest.SetValue(FrameworkElement.MarginProperty, new Thickness(18, 0, 0, 0));
        gest.SetValue(DockPanel.DockProperty, Dock.Right);

        var head = Named(El(typeof(AccessText)), "head");
        head.SetBinding(AccessText.TextProperty, new System.Windows.Data.Binding("Header")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        head.SetValue(TextBlock.ForegroundProperty, black); // LOCAL value beats the implicit TextBlock style
        head.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        dp.AppendChild(chk);
        dp.AppendChild(gest);
        dp.AppendChild(head);
        bd.AppendChild(dp);

        var tpl = new ControlTemplate(typeof(MenuItem)) { VisualTree = bd };
        var hl = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hl.Setters.Add(new Setter(Border.BackgroundProperty, B(Color.FromRgb(204, 232, 255)), "bd"));
        tpl.Triggers.Add(hl);
        var ck = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
        ck.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "chk"));
        tpl.Triggers.Add(ck);
        var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        dis.Setters.Add(new Setter(TextBlock.ForegroundProperty, B(Color.FromRgb(170, 174, 182)), "head"));
        tpl.Triggers.Add(dis);
        return tpl;
    }

    private static Style TabStyle()
    {
        var s = S<TabControl>();
        Set(s, Control.BackgroundProperty, B(Pal.Surface));
        Set(s, Control.BorderBrushProperty, B(Pal.Border));
        return s;
    }

    private static Style TabItemStyle()
    {
        var bd = Named(El(typeof(Border)), "bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(Corner, Corner, 0, 0));
        bd.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 5));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = El(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        // HeaderedContentControl: without ContentSource the presenter shows Content (null on a tab),
        // so the header rendered as an empty band and the "Level"/"Assets" labels vanished.
        cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        bd.AppendChild(cp);
        var tpl = new ControlTemplate(typeof(TabItem)) { VisualTree = bd };
        tpl.Triggers.Add(Repaint("bd", TabItem.IsSelectedProperty, true, Pal.SurfaceAlt, Pal.SurfaceAlt));
        tpl.Triggers.Add(Repaint("bd", UIElement.IsMouseOverProperty, true, Pal.Hover, Pal.Hover));

        var s = S<TabItem>();
        Set(s, Control.ForegroundProperty, B(Pal.TextMuted));
        Set(s, Control.FontSizeProperty, FontBody);
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }

    private static Style ToolBarTrayStyle()
    {
        var s = S<ToolBarTray>();
        Set(s, Control.BackgroundProperty, B(Pal.Surface));
        return s;
    }

    // A quiet tool bar: no grip, no overflow arrow (the actions all fit), just themed icon buttons.
    private static Style ToolBarStyle()
    {
        var root = El(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3));
        var items = El(typeof(ItemsPresenter));
        root.AppendChild(items);
        var tpl = new ControlTemplate(typeof(ToolBar)) { VisualTree = root };

        var s = S<ToolBar>();
        Set(s, Control.BackgroundProperty, B(Pal.Surface));
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, ToolBar.OverflowModeProperty, OverflowMode.Never);
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }

    private static Style GridSplitterStyle()
    {
        var s = S<GridSplitter>();
        Set(s, Control.BackgroundProperty, B(Pal.Border));
        Set(s, FrameworkElement.WidthProperty, 5.0);
        Set(s, FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        return s;
    }

    // Flat, quiet toolbar button: transparent until hovered, a soft square highlight, no border.
    private static Style ToolButtonStyle()
    {
        var bd = Named(El(typeof(Border)), "bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, new Thickness(6, 5, 6, 5));
        var cp = El(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        tpl.Triggers.Add(Repaint("bd", UIElement.IsMouseOverProperty, true, Pal.Hover, Pal.Hover));
        tpl.Triggers.Add(Repaint("bd", Button.IsPressedProperty, true, Pal.AccentSoft, Pal.AccentSoft));

        var s = S<Button>();
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }

    private static Style ToolTipStyle()
    {
        var root = El(typeof(Border));
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        root.SetValue(Border.BackgroundProperty, B(Pal.SurfaceAlt));
        root.SetValue(Border.BorderBrushProperty, B(Pal.Border));
        root.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        root.SetValue(Border.PaddingProperty, new Thickness(8, 5, 8, 5));
        var cp = El(typeof(ContentPresenter));
        root.AppendChild(cp);
        var tpl = new ControlTemplate(typeof(ToolTip)) { VisualTree = root };

        var s = S<ToolTip>();
        Set(s, Control.ForegroundProperty, B(Pal.Text));
        Set(s, Control.FontSizeProperty, 11.5);
        Set(s, Control.TemplateProperty, tpl);
        return s;
    }
}
