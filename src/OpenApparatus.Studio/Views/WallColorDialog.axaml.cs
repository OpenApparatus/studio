using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenApparatus.Studio.Views;

public partial class WallColorDialog : Window
{
    public enum Outcome { Cancelled, Set, Reset }

    public Outcome ChosenOutcome { get; private set; } = Outcome.Cancelled;
    public System.Numerics.Vector3 ChosenColor { get; private set; }

    public WallColorDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Configure the dialog after construction. Pulled into a separate method so
    /// any null look-ups against named XAML elements raise a clear error from
    /// here rather than silently from a constructor's body.
    /// </summary>
    public void Configure(string roomLabel, System.Numerics.Vector3? current)
    {
        var header = this.FindControl<TextBlock>("HeaderText")
            ?? throw new System.InvalidOperationException("HeaderText control missing from WallColorDialog XAML.");
        var picker = this.FindControl<ColorPicker>("Picker")
            ?? throw new System.InvalidOperationException("Picker control missing from WallColorDialog XAML.");

        header.Text = $"Color for {roomLabel}";
        picker.Color = current.HasValue
            ? Color.FromRgb(
                (byte)(current.Value.X * 255),
                (byte)(current.Value.Y * 255),
                (byte)(current.Value.Z * 255))
            : Color.FromRgb(199, 199, 204); // default-ish gray
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picker = this.FindControl<ColorPicker>("Picker");
        if (picker is null) { Close(); return; }
        var c = picker.Color;
        ChosenColor = new System.Numerics.Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
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
