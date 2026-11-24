using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TastyEditor.App.Assets;

namespace TastyEditor.App.Panels;

/// <summary>S5-4 asset browser: a filterable thumbnail grid over the loaded install's
/// sprite cells (and animations), reusing the exact decode/crop path the viewport draws
/// with so a thumbnail is what the game actually shows. It is strictly read-only — it
/// only reads assets and never touches the level model or the save path. Picking an entry
/// raises <see cref="SpritePicked"/> (consumed by the later S5-5 reassign / S5-6 spawn
/// steps) and enlarges it in the preview strip. With no assets (TP2, a repo sample, or an
/// incomplete install) it shows a single explanatory line and never blocks a level open.
/// Thumbnails are built lazily the first time the panel is shown, and capped, so opening
/// it can't stall the UI on a very large atlas set.</summary>
public sealed class AssetBrowserPanel : Grid
{
    private const int ThumbBox = 56;      // uniform-fit box (px) per thumbnail
    private const int Cap = 500;          // max thumbnails realized per rebuild (perf guard)

    private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(48, 51, 58));
    private static readonly Brush PageBg = new SolidColorBrush(Color.FromRgb(32, 34, 40));
    private static readonly Brush PlaceholderBg = new SolidColorBrush(Color.FromRgb(70, 40, 40));

    private readonly TextBox _filter = new() { Margin = new Thickness(0, 0, 4, 0) };
    private readonly ComboBox _kind = new() { Width = 84, Margin = new Thickness(0) };
    private readonly WrapPanel _grid = new() { ItemWidth = ThumbBox + 12, ItemHeight = ThumbBox + 26 };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _count = new() { FontSize = 10, Opacity = 0.75, Margin = new Thickness(2, 2, 2, 0) };
    private readonly Image _previewImg = new() { Width = ThumbBox + 8, Height = ThumbBox + 8, Stretch = Stretch.Uniform };
    private readonly TextBlock _previewLabel = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };

    private readonly DispatcherTimer _debounce;
    private ISpriteSource? _ss;
    private bool _populated;

    /// <summary>Raised when the user picks an asset, carrying its name (cell or anim).</summary>
    public event Action<string>? SpritePicked;

    public AssetBrowserPanel()
    {
        Background = PageBg;
        Margin = new Thickness(4);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // controls
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // preview
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // count

        foreach (var o in new object[] { "Cells", "Anims" })
            _kind.Items.Add(o);
        _kind.SelectedIndex = 0;

        var controls = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_kind, Dock.Right);
        controls.Children.Add(_kind);
        controls.Children.Add(_filter);

        var preview = new Border
        {
            Background = CardBg,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 0, 4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Border { Background = PageBg, Child = _previewImg, Width = ThumbBox + 8, Height = ThumbBox + 8 },
                    _previewLabel,
                },
            },
        };
        _previewLabel.Text = "Pick an asset to preview it.";
        _previewLabel.VerticalAlignment = VerticalAlignment.Center;
        _previewLabel.Margin = new Thickness(6, 0, 0, 0);

        _scroller = new ScrollViewer
        {
            Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        SetRow(controls, 0); SetRow(preview, 1); SetRow(_scroller, 2); SetRow(_count, 3);
        Children.Add(controls);
        Children.Add(preview);
        Children.Add(_scroller);
        Children.Add(_count);

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rebuild(); };

        _filter.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _kind.SelectionChanged += (_, _) => Rebuild();
    }

    /// <summary>Point the browser at a freshly loaded level's assets (null = none).
    /// Marks the grid stale; it is filled the next time <see cref="RefreshIfVisible"/> runs.</summary>
    public void SetSource(ISpriteSource? ss)
    {
        _ss = ss;
        _populated = false;
        _grid.Children.Clear();
        _count.Text = "";
    }

    /// <summary>Called when the host tab becomes visible; builds thumbnails on first show
    /// (or after a new level) so the work never happens during a level open.</summary>
    public void RefreshIfVisible()
    {
        if (_populated) return;
        _populated = true;
        Rebuild();
    }

    private bool AnimsView => _kind.SelectedIndex == 1;

    private void Rebuild()
    {
        _grid.Children.Clear();
        if (_ss is null)
        {
            _count.Text = "No sprite assets for this level (TP2 or assets folder not found).";
            return;
        }

        IEnumerable<string> names = AnimsView ? _ss.AnimNames : _ss.CellNames;
        string f = _filter.Text.Trim();
        var list = new List<string>();
        foreach (var n in names)
            if (f.Length == 0 || n.Contains(f, StringComparison.OrdinalIgnoreCase))
                list.Add(n);
        list.Sort(StringComparer.OrdinalIgnoreCase);

        int shown = Math.Min(list.Count, Cap);
        for (int i = 0; i < shown; i++)
            _grid.Children.Add(MakeThumb(list[i]));

        _count.Text = list.Count == 0 ? "no matches"
            : $"showing {shown} of {list.Count} {(AnimsView ? "animations" : "cells")}"
              + (list.Count > shown ? " (refine the filter)" : "");
    }

    private UIElement MakeThumb(string name)
    {
        BitmapSource? art = null;
        if (AnimsView)
        {
            if (_ss!.TryGet(name, 0, null, out var im, out _, out _, out _, out _, out _)) art = im;
        }
        else if (_ss!.TryGetCellThumb(name, out var im2)) art = im2;

        var box = new Border
        {
            Width = ThumbBox,
            Height = ThumbBox,
            Background = art is null ? PlaceholderBg : CardBg,
            Child = new Image { Source = art, Stretch = Stretch.Uniform },
        };

        var btn = new Button
        {
            Content = new StackPanel
            {
                Children =
                {
                    box,
                    new TextBlock
                    {
                        Text = name,
                        FontSize = 9,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = ThumbBox + 8,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
            ToolTip = name,
            Margin = new Thickness(2),
            Padding = new Thickness(1),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Focusable = false,
        };
        btn.Click += (_, _) => Pick(name, art);
        return btn;
    }

    private void Pick(string name, BitmapSource? image)
    {
        _previewImg.Source = image;
        _previewLabel.Text = image is null
            ? $"{name}\n(no preview)"
            : $"{name}\n{image.PixelWidth}\u00d7{image.PixelHeight}px";
        SpritePicked?.Invoke(name);
    }
}
