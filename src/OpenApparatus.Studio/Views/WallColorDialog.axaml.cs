using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenApparatus.Studio.Views;

public partial class WallColorDialog : Window
{
    public enum Outcome { Cancelled, Set, Reset }

    public Outcome ChosenOutcome { get; private set; } = Outcome.Cancelled;
    public System.Numerics.Vector3 ChosenColor { get; private set; }

    Color _current = Color.FromRgb(199, 199, 204);
    bool _internalUpdate;

    public WallColorDialog()
    {
        InitializeComponent();

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
        Title = $"Pick colour — {roomLabel}";
        var header = this.FindControl<TextBlock>("HeaderText")
            ?? throw new System.InvalidOperationException("HeaderText control missing.");
        header.Text = $"Colour for {roomLabel}";

        if (current.HasValue)
        {
            _current = Color.FromRgb(
                (byte)(current.Value.X * 255),
                (byte)(current.Value.Y * 255),
                (byte)(current.Value.Z * 255));
        }
        SyncPickerToCurrent();
        UpdatePreview();
    }

    /// <summary>Called when the user picks a colour via the ColorView.
    /// Pushes the new value into _current + the hex field. The
    /// _internalUpdate flag prevents feedback when SyncPickerToCurrent
    /// programmatically writes to the picker.</summary>
    void OnPickerColorChanged(object? sender, Avalonia.Controls.ColorChangedEventArgs e)
    {
        if (_internalUpdate) return;
        _current = Color.FromRgb(e.NewColor.R, e.NewColor.G, e.NewColor.B);
        UpdatePreview();
    }

    void SyncPickerToCurrent()
    {
        var picker = this.FindControl<ColorView>("Picker");
        if (picker is null) return;
        _internalUpdate = true;
        try { picker.Color = _current; }
        finally { _internalUpdate = false; }
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
            SyncPickerToCurrent();
            UpdatePreview();
        }
        catch
        {
            // invalid hex — leave preview alone
        }
    }

    void UpdatePreview()
    {
        if (this.FindControl<Border>("Preview") is { } preview)
            preview.Background = new SolidColorBrush(_current);
        if (this.FindControl<TextBox>("HexInput") is { } hex
            && (hex.Text ?? "") != HexOf(_current))
            hex.Text = HexOf(_current);
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
