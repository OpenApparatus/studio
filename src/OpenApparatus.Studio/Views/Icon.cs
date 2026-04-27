using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Vector icon control. Renders a path from <c>Themes/Icons.axaml</c>
/// (keyed <c>Icon{Symbol}</c>) into a <see cref="Path"/> with a
/// stroke-only style — the icon library uses 24×24 outline glyphs.
///
/// Usage:
///     &lt;views:Icon Symbol="Undo" Size="16" Foreground="..."/&gt;
/// </summary>
public sealed class Icon : ContentControl
{
    public static readonly StyledProperty<string> SymbolProperty =
        AvaloniaProperty.Register<Icon, string>(nameof(Symbol), defaultValue: "");

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), defaultValue: 16.0);

    public static readonly StyledProperty<double> StrokeWeightProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeWeight), defaultValue: 1.6);

    public string Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }
    public double StrokeWeight
    {
        get => GetValue(StrokeWeightProperty);
        set => SetValue(StrokeWeightProperty, value);
    }

    Path? _path;

    public Icon()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment   = VerticalAlignment.Center;
        Build();
    }

    static Icon()
    {
        SymbolProperty.Changed.AddClassHandler<Icon>((x, _) => x.Build());
        SizeProperty.Changed.AddClassHandler<Icon>((x, _) => x.Build());
        StrokeWeightProperty.Changed.AddClassHandler<Icon>((x, _) => x.Build());
    }

    void Build()
    {
        _path ??= new Path { StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        _path.Width  = Size;
        _path.Height = Size;
        _path.Stretch = Stretch.Uniform;
        _path.StrokeThickness = StrokeWeight;
        // Bind stroke to the inherited Foreground so styling cascades
        // naturally from parents (Button, ToggleButton, etc).
        _path.Bind(Path.StrokeProperty, this.GetObservable(ForegroundProperty));
        _path.Fill = Brushes.Transparent;

        // Look up the icon geometry by key. Empty / missing → empty path.
        if (!string.IsNullOrEmpty(Symbol))
        {
            string key = "Icon" + Symbol;
            if (Application.Current?.Resources.TryGetResource(key, null, out var res) == true
                && res is Avalonia.Media.Geometry geom)
            {
                _path.Data = geom;
            }
            else
            {
                _path.Data = null;
            }
        }
        Content = _path;
    }
}
