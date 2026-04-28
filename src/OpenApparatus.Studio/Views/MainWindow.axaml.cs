using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenApparatus.Studio.Services;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Views;

public partial class MainWindow : Window
{
    AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        // Tunnel handler so we see the key before TextBox does, but we then
        // bail out when a TextBox or other text-input has focus — that way
        // typing a letter (e.g. 'O') in the room name field stays in the
        // field instead of firing the OpenSelectedWall command.
        AddHandler(KeyDownEvent, OnGlobalKey, RoutingStrategies.Tunnel);

        // Restore window geometry from per-user settings, then watch for
        // geometry changes and persist on every move/resize. Settings load
        // is best-effort; a missing file just keeps the design defaults.
        Opened += OnOpened;
        Closing += OnClosing;

        // Programmatic app icon — the same three-square brand mark used
        // in the welcome screen + about dialog, rendered to a 64-px
        // bitmap so the OS taskbar / Alt-Tab gets a real icon instead
        // of falling back to a generic Avalonia placeholder.
        Icon = BuildBrandIcon();
    }

    static Avalonia.Controls.WindowIcon BuildBrandIcon()
    {
        const int Size = 64;
        var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize(Size, Size),
            new Avalonia.Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            ctx.FillRectangle(
                new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Transparent),
                new Avalonia.Rect(0, 0, Size, Size));
            for (int i = 0; i < 3; i++)
            {
                double s = 56 - i * 16;
                double x = (Size - s) * 0.5;
                var pen = new Avalonia.Media.Pen(
                    new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.FromArgb(
                            (byte)(220 - i * 50), 0x1F, 0x6F, 0xEB)),
                    2.4);
                Avalonia.Media.IBrush? fill = i == 0
                    ? new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.FromArgb(40, 0x1F, 0x6F, 0xEB))
                    : null;
                ctx.DrawRectangle(fill, pen,
                    new Avalonia.Rect(x, x, s, s),
                    5, 5);
            }
        }
        return new Avalonia.Controls.WindowIcon(bmp);
    }

    void OnOpened(object? sender, EventArgs e)
    {
        _settings = AppSettings.LoadOrDefault();
        if (_settings.WindowWidth  > 200) Width  = _settings.WindowWidth;
        if (_settings.WindowHeight > 200) Height = _settings.WindowHeight;
        if (!double.IsNaN(_settings.WindowX) && !double.IsNaN(_settings.WindowY))
            Position = new PixelPoint((int)_settings.WindowX, (int)_settings.WindowY);
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        // Restore the persisted theme variant. The VM apply-method
        // talks to Application.RequestedThemeVariant which cascades to
        // every {DynamicResource} binding in the chrome.
        if (Vm is { } vm)
        {
            if (System.Enum.TryParse<MainWindowViewModel.ThemeVariantKind>(
                _settings.ThemeVariant, out var th))
            {
                vm.ThemeVariant = th;
            }
            vm.ApplyThemeVariant();
        }
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (_settings.WindowMaximized)
        {
            // Don't overwrite the restore-size when closing maximized.
        }
        else
        {
            _settings.WindowWidth  = Width;
            _settings.WindowHeight = Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
        _settings.Save();
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
                // Multi-select aware: if more than one object is in the
                // selection set, do a bulk delete with an Undo toast.
                if (vm.SelectedObjectIndices.Count > 1)
                    vm.DeleteSelectedObjectsCommand.Execute(null);
                else
                    vm.DeleteSelectedObjectCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.A)
            {
                vm.SelectAllObjectsCommand.Execute(null);
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
            case Key.F: vm.FrameSelectionCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: vm.ClearSelectionCommand.Execute(null); e.Handled = true; break;
        }
    }

    bool FocusedIsTextEntry()
    {
        var focused = FocusManager?.GetFocusedElement();
        return focused is TextBox or AutoCompleteBox;
    }

    MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    void OnExitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    async void OnShowAbout(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dlg = new AboutDialog();
        await dlg.ShowDialog(this);
    }

    /// <summary>Translate vertical mouse-wheel motion into horizontal
    /// scrolling on the toolbar — most users don't think to hold Shift
    /// to scroll horizontally and would otherwise see no response when
    /// the strip overflows on a narrow window.</summary>
    void OnToolbarWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if (sender is ScrollViewer sv && e.Delta.Y != 0)
        {
            sv.Offset = new Avalonia.Vector(
                System.Math.Max(0, sv.Offset.X - e.Delta.Y * 60),
                sv.Offset.Y);
            e.Handled = true;
        }
    }

    void OnOpenRecent(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string path && Vm is { } vm)
            vm.OpenProjectFromPath(path);
    }

    async void OnPickDefaultFloor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm; if (vm is null) return;
        var picked = await PickColor("Default floor colour", vm.DefaultFloorColor);
        if (picked.HasValue) vm.DefaultFloorColor = picked.Value;
    }

    async void OnPickDefaultCeiling(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm; if (vm is null) return;
        var picked = await PickColor("Default ceiling colour", vm.DefaultCeilingColor);
        if (picked.HasValue) vm.DefaultCeilingColor = picked.Value;
    }

    async void OnPickDefaultWall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm; if (vm is null) return;
        var picked = await PickColor("Default wall colour", vm.DefaultWallColor);
        if (picked.HasValue) vm.DefaultWallColor = picked.Value;
    }

    async void OnPickPathColor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm; if (vm is null) return;
        var picked = await PickColor("Path colour", vm.PathColor);
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
