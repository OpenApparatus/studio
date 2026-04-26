using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Modal dialog used to pick a color for a wall (or reset to the default).
/// Result returned via <see cref="Result"/>: a <see cref="System.Numerics.Vector3"/>
/// RGB triple if the user picked a color, or null if they chose "reset to default"
/// (clear the override) or cancelled.
/// </summary>
public partial class WallColorDialog : Window
{
    public enum Outcome { Cancelled, Set, Reset }

    public Outcome ChosenOutcome { get; private set; } = Outcome.Cancelled;
    public System.Numerics.Vector3 ChosenColor { get; private set; }

    public WallColorDialog()
    {
        InitializeComponent();
    }

    public WallColorDialog(string roomLabel, System.Numerics.Vector3? current) : this()
    {
        HeaderText.Text = $"Color for {roomLabel}";

        if (current.HasValue)
            Picker.Color = Color.FromRgb(
                (byte)(current.Value.X * 255),
                (byte)(current.Value.Y * 255),
                (byte)(current.Value.Z * 255));
        else
            Picker.Color = Color.FromRgb(199, 199, 204); // default-ish gray
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var c = Picker.Color;
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
