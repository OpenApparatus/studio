using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenApparatus.Studio.Views;

public partial class WallColorDialog : Window
{
    public enum Outcome { Cancelled, Set, Reset }

    public Outcome ChosenOutcome { get; private set; } = Outcome.Cancelled;
    public System.Numerics.Vector3 ChosenColor { get; private set; }

    Color _current = Color.FromRgb(199, 199, 204);

    public WallColorDialog()
    {
        InitializeComponent();
        BuildSwatches();
        var hex = this.FindControl<TextBox>("HexInput")!;
        hex.LostFocus += (_, _) => ApplyHex(hex.Text);
        hex.KeyUp += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyHex(hex.Text);
        };
        UpdatePreview();
    }

    public void Configure(string roomLabel, System.Numerics.Vector3? current)
    {
        var header = this.FindControl<TextBlock>("HeaderText")
            ?? throw new System.InvalidOperationException("HeaderText control missing.");
        header.Text = $"Color for {roomLabel}";

        if (current.HasValue)
        {
            _current = Color.FromRgb(
                (byte)(current.Value.X * 255),
                (byte)(current.Value.Y * 255),
                (byte)(current.Value.Z * 255));
        }
        UpdatePreview();
    }

    void BuildSwatches()
    {
        // 18 swatches across 3 rows × 6 columns. Picked to cover warm/cool/neutral
        // architectural palette without a full picker UI.
        var palette = new[]
        {
            "#FFFFFF", "#D9D9D9", "#999999", "#555555", "#222222", "#000000",
            "#F5DEB3", "#C19A6B", "#8B5A2B", "#5C3A1E", "#A0522D", "#704214",
            "#FAD0C4", "#FF6B6B", "#E07B39", "#F2C744", "#80B918", "#52796F",
        };
        var grid = this.FindControl<UniformGrid>("Swatches")
            ?? throw new System.InvalidOperationException("Swatches grid missing.");
        foreach (var hex in palette)
        {
            var c = Color.Parse(hex);
            var btn = new Button
            {
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Avalonia.Thickness(1),
                Margin = new Avalonia.Thickness(2),
                Padding = new Avalonia.Thickness(0),
                MinHeight = 36,
                MinWidth = 36,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            ToolTip.SetTip(btn, hex);
            var captured = c;
            btn.Click += (_, _) =>
            {
                _current = captured;
                UpdatePreview();
                var hexInput = this.FindControl<TextBox>("HexInput");
                if (hexInput != null) hexInput.Text = HexOf(captured);
            };
            grid.Children.Add(btn);
        }
    }

    void ApplyHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = text.Trim();
        if (!t.StartsWith("#")) t = "#" + t;
        try
        {
            var c = Color.Parse(t);
            _current = Color.FromRgb(c.R, c.G, c.B);
            UpdatePreview();
        }
        catch
        {
            // ignore — invalid hex; preview keeps its current value.
        }
    }

    void UpdatePreview()
    {
        var preview = this.FindControl<Border>("Preview");
        if (preview != null) preview.Background = new SolidColorBrush(_current);
        var hex = this.FindControl<TextBox>("HexInput");
        if (hex != null && (hex.Text ?? "") != HexOf(_current)) hex.Text = HexOf(_current);
    }

    static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ChosenColor = new System.Numerics.Vector3(_current.R / 255f, _current.G / 255f, _current.B / 255f);
        ChosenOutcome = Outcome.Set;
        Close();
    }

    void OnReset(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ChosenOutcome = Outcome.Reset;
        Close();
    }

    void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ChosenOutcome = Outcome.Cancelled;
        Close();
    }
}
