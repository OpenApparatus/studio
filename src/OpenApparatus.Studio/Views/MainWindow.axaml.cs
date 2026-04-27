using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Tunnel handler so we see the key before TextBox does, but we then
        // bail out when a TextBox or other text-input has focus — that way
        // typing a letter (e.g. 'O') in the room name field stays in the
        // field instead of firing the OpenSelectedWall command.
        AddHandler(KeyDownEvent, OnGlobalKey, RoutingStrategies.Tunnel);
    }

    void OnGlobalKey(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        // Esc dismisses the shortcut overlay before any other Esc handler
        // (Clear Selection in the editor) gets a chance — but only when
        // the overlay is open. Otherwise Esc passes through normally.
        if (vm.IsShortcutOverlayVisible && e.Key == Key.Escape)
        {
            vm.DismissShortcutOverlayCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Undo / Redo are global — they fire even when a TextBox has focus so
        // the user can roll back unexpected typing. Other shortcuts still
        // defer to the focused field.
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (ctrl && e.Key == Key.Z)
        {
            vm.UndoCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && (e.KeyModifiers & KeyModifiers.Shift) != 0)))
        {
            vm.RedoCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (FocusedIsTextEntry()) return;

        // Objects-mode shortcuts: 1..9 place an object at the selected sub-cell;
        // Delete removes the selected object. We test these first so a digit
        // key in Objects mode never collides with a future room/door letter
        // mapping.
        if (vm.IsObjectsMode)
        {
            int? slot = e.Key switch
            {
                Key.D1 or Key.NumPad1 => 1,
                Key.D2 or Key.NumPad2 => 2,
                Key.D3 or Key.NumPad3 => 3,
                Key.D4 or Key.NumPad4 => 4,
                Key.D5 or Key.NumPad5 => 5,
                Key.D6 or Key.NumPad6 => 6,
                Key.D7 or Key.NumPad7 => 7,
                Key.D8 or Key.NumPad8 => 8,
                Key.D9 or Key.NumPad9 => 9,
                _ => null,
            };
            if (slot is int s)
            {
                vm.PlaceObjectAtSelectedSubCell(s);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Delete or Key.Back)
            {
                vm.DeleteSelectedObjectCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.R: vm.CreateRoomFromSelectionCommand.Execute(null); e.Handled = true; break;
            case Key.D: vm.ToggleDoorOnSelectedWallCommand.Execute(null); e.Handled = true; break;
            case Key.W: vm.ToggleWindowOnSelectedWallCommand.Execute(null); e.Handled = true; break;
            case Key.O: vm.OpenSelectedWallCommand.Execute(null); e.Handled = true; break;
            case Key.C: vm.CloseSelectedWallCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: vm.ClearSelectionCommand.Execute(null); e.Handled = true; break;
        }
    }

    bool FocusedIsTextEntry()
    {
        var focused = FocusManager?.GetFocusedElement();
        return focused is TextBox or AutoCompleteBox;
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
