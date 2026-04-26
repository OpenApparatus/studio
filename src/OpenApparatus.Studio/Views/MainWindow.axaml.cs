using Avalonia.Controls;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    async void OnPickDefaultFloor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm; if (vm is null) return;
        var picked = await PickColor("Default floor color", vm.DefaultFloorColor);
        if (picked.HasValue) vm.DefaultFloorColor = picked.Value;
    }

    async void OnPickDefaultCeiling(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm; if (vm is null) return;
        var picked = await PickColor("Default ceiling color", vm.DefaultCeilingColor);
        if (picked.HasValue) vm.DefaultCeilingColor = picked.Value;
    }

    async void OnPickPathColor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm; if (vm is null) return;
        var picked = await PickColor("Path color", vm.PathColor);
        if (picked.HasValue) vm.PathColor = picked.Value;
    }

    /// <summary>
    /// Opens the wall color dialog repurposed as a generic color picker. Returns
    /// the selected color, or null if cancelled / reset (caller decides whether
    /// to treat reset as "no change" — for global defaults that's the safer
    /// option than overwriting with a hard-coded fallback).
    /// </summary>
    async System.Threading.Tasks.Task<System.Numerics.Vector3?> PickColor(
        string label, System.Numerics.Vector3 current)
    {
        var dlg = new WallColorDialog();
        dlg.Configure(label, current);
        await dlg.ShowDialog(this);
        return dlg.ChosenOutcome == WallColorDialog.Outcome.Set ? dlg.ChosenColor : (System.Numerics.Vector3?)null;
    }
}
