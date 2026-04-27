using System;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Modal dialog for editing one object type's shape + colour. Used by the
/// object panel — clicking a type's swatch opens this; OK commits the chosen
/// shape and colour back to the VM.
/// </summary>
public partial class ObjectTypePickerDialog : Window
{
    public bool Confirmed { get; private set; }
    public ObjectShape ChosenShape { get; private set; }
    public Vector3 ChosenColor { get; private set; }

    Color _current = Color.FromRgb(199, 199, 204);

    public ObjectTypePickerDialog()
    {
        InitializeComponent();

        var combo = this.FindControl<ComboBox>("ShapeCombo")!;
        foreach (ObjectShape s in Enum.GetValues<ObjectShape>())
            combo.Items.Add(s);
        combo.SelectedIndex = 0;

        BuildSwatches();
        var hex = this.FindControl<TextBox>("HexInput")!;
        hex.LostFocus += (_, _) => ApplyHex(hex.Text);
        hex.KeyUp += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyHex(hex.Text);
        };
        UpdatePreview();
    }

    public void Configure(ObjectShape shape, Vector3 color)
    {
        var combo = this.FindControl<ComboBox>("ShapeCombo");
        if (combo != null) combo.SelectedItem = shape;

        _current = Color.FromRgb(
            (byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255));
        UpdatePreview();
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void BuildSwatches()
    {
        var palette = new[]
        {
            "#FFFFFF", "#D9D9D9", "#999999", "#555555", "#222222", "#000000",
            "#F5DEB3", "#C19A6B", "#8B5A2B", "#5C3A1E", "#A0522D", "#704214",
            "#FAD0C4", "#FF6B6B", "#E07B39", "#F2C744", "#80B918", "#52796F",
        };
        var grid = this.FindControl<UniformGrid>("Swatches")!;
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
            // Invalid hex — ignore so the preview holds its previous value.
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

    void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var combo = this.FindControl<ComboBox>("ShapeCombo");
        ChosenShape = combo?.SelectedItem is ObjectShape s ? s : ObjectShape.Cube;
        ChosenColor = new Vector3(_current.R / 255f, _current.G / 255f, _current.B / 255f);
        Confirmed = true;
        Close();
    }

    void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
