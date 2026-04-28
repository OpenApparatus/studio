using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using OpenApparatus.Studio.Services;
using OpenApparatus.Studio.Themes;
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
            var accent = Tokens.AccentColor;
            for (int i = 0; i < 3; i++)
            {
                double s = 56 - i * 16;
                double x = (Size - s) * 0.5;
                var pen = new Avalonia.Media.Pen(
                    new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.FromArgb(
                            (byte)(220 - i * 50), accent.R, accent.G, accent.B)),
                    2.4);
                Avalonia.Media.IBrush? fill = i == 0
                    ? new Avalonia.Media.SolidColorBrush(
                        Avalonia.Media.Color.FromArgb(40, accent.R, accent.G, accent.B))
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

    async void OnExportPng2D(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = Vm; if (vm is null) return;
        if (!vm.IsTopDownView)
        {
            vm.StatusMessage = "Switch to 2D view before exporting a PNG.";
            return;
        }
        var editor = this.FindControl<GridEditorView>("GridEditor");
        if (editor is null || editor.Bounds.Width <= 0 || editor.Bounds.Height <= 0)
        {
            vm.StatusMessage = "2D view is not available.";
            return;
        }
        var legend = this.FindControl<LegendBar>("LegendBar");

        var dlg = new Png2DExportDialog();
        dlg.RememberInitial(
            vm.ShowWallBorders, vm.ShowRoomLabels,
            vm.ShowAxisMarkers, vm.ShowConstraintOverlays, legend?.IsVisible == true,
            vm.ShowRoomDimensions, vm.ShowFloorAreaLabels, vm.ShowOpeningSizeLabels,
            vm.ShowObjectDistances, vm.ShowDoorAngles, vm.ShowDoorDistances);
        dlg.Configure(
            vm.ShowWallBorders, vm.ShowRoomLabels,
            vm.ShowAxisMarkers, vm.ShowConstraintOverlays, legend?.IsVisible == true,
            vm.ShowRoomDimensions, vm.ShowFloorAreaLabels, vm.ShowOpeningSizeLabels,
            vm.ShowObjectDistances, vm.ShowDoorAngles, vm.ShowDoorDistances);
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export 2D view as PNG",
            SuggestedFileName = "floorplan.png",
            DefaultExtension = "png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG image (*.png)") { Patterns = new[] { "*.png" } },
            },
        });
        if (file is null) return;

        // Snapshot toggle state, apply the user's selections, render to a 2× DPI
        // bitmap, then restore. Toggles bump EditVersion which AffectsRender on
        // the editor — but RenderTargetBitmap.Render reads the visual's render
        // logic synchronously, so we don't need to wait for layout / invalidate.
        var prevWallBorders        = vm.ShowWallBorders;
        var prevRoomLabels         = vm.ShowRoomLabels;
        var prevAxisMarkers        = vm.ShowAxisMarkers;
        var prevConstraintOverlays = vm.ShowConstraintOverlays;
        var prevRoomDims           = vm.ShowRoomDimensions;
        var prevFloorArea          = vm.ShowFloorAreaLabels;
        var prevOpeningSizes       = vm.ShowOpeningSizeLabels;
        var prevObjectDist         = vm.ShowObjectDistances;
        var prevDoorAngles         = vm.ShowDoorAngles;
        var prevDoorDist           = vm.ShowDoorDistances;

        try
        {
            vm.ShowWallBorders        = dlg.Ribbons;
            vm.ShowRoomLabels         = dlg.RoomLabels;
            vm.ShowAxisMarkers        = dlg.AxisMarkers;
            vm.ShowConstraintOverlays = dlg.Placement;
            vm.ShowRoomDimensions     = dlg.RoomDims;
            vm.ShowFloorAreaLabels    = dlg.FloorArea;
            vm.ShowOpeningSizeLabels  = dlg.OpeningSizes;
            vm.ShowObjectDistances    = dlg.ObjectDist;
            vm.ShowDoorAngles         = dlg.DoorAngles;
            vm.ShowDoorDistances      = dlg.DoorDist;

            const double scale = 2.0;
            var eb = editor.Bounds;
            var editorPx = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(eb.Width  * scale)),
                Math.Max(1, (int)Math.Ceiling(eb.Height * scale)));
            using var editorBmp = new RenderTargetBitmap(editorPx, new Vector(96 * scale, 96 * scale));
            editorBmp.Render(editor);

            bool includeLegend = dlg.Legend && legend != null
                && legend.Bounds.Width > 0 && legend.Bounds.Height > 0;

            if (!includeLegend)
            {
                await using var stream = await file.OpenWriteAsync();
                editorBmp.Save(stream);
            }
            else
            {
                var lb = legend!.Bounds;
                var legendPx = new PixelSize(
                    Math.Max(1, (int)Math.Ceiling(lb.Width  * scale)),
                    Math.Max(1, (int)Math.Ceiling(lb.Height * scale)));
                using var legendBmp = new RenderTargetBitmap(legendPx, new Vector(96 * scale, 96 * scale));
                legendBmp.Render(legend);

                // Composite editor on top, legend below. Width is the wider of
                // the two so neither gets clipped; the editor background fills
                // any gap to the right of the editor when the legend is wider.
                double finalW = Math.Max(eb.Width, lb.Width);
                double finalH = eb.Height + lb.Height;
                var finalPx = new PixelSize(
                    Math.Max(1, (int)Math.Ceiling(finalW * scale)),
                    Math.Max(1, (int)Math.Ceiling(finalH * scale)));
                using var finalBmp = new RenderTargetBitmap(finalPx, new Vector(96 * scale, 96 * scale));
                using (var fctx = finalBmp.CreateDrawingContext())
                {
                    // Match the editor's solid background so the gap (if any)
                    // and any sub-pixel seams between the two regions stay
                    // visually continuous with the rest of the floorplan.
                    fctx.FillRectangle(
                        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(232, 232, 236)),
                        new Rect(0, 0, finalW, finalH));
                    fctx.DrawImage(editorBmp,
                        new Rect(0, 0, editorBmp.Size.Width, editorBmp.Size.Height),
                        new Rect(0, 0, eb.Width, eb.Height));
                    fctx.DrawImage(legendBmp,
                        new Rect(0, 0, legendBmp.Size.Width, legendBmp.Size.Height),
                        new Rect(0, eb.Height, lb.Width, lb.Height));
                }

                await using var stream = await file.OpenWriteAsync();
                finalBmp.Save(stream);
            }
            vm.StatusMessage = $"Exported PNG → {file.Name}";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"PNG export failed: {ex.Message}";
        }
        finally
        {
            vm.ShowWallBorders        = prevWallBorders;
            vm.ShowRoomLabels         = prevRoomLabels;
            vm.ShowAxisMarkers        = prevAxisMarkers;
            vm.ShowConstraintOverlays = prevConstraintOverlays;
            vm.ShowRoomDimensions     = prevRoomDims;
            vm.ShowFloorAreaLabels    = prevFloorArea;
            vm.ShowOpeningSizeLabels  = prevOpeningSizes;
            vm.ShowObjectDistances    = prevObjectDist;
            vm.ShowDoorAngles         = prevDoorAngles;
            vm.ShowDoorDistances      = prevDoorDist;
        }
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
