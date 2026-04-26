using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OpenApparatus.Studio.ViewModels;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Right-side appearance panel for the currently selected room. Rebuilds itself
/// from scratch whenever the selected room or the edit version changes — the
/// per-wall list is dynamic in length, so a fully data-bound implementation
/// would be heavier than just walking the VM and emitting controls in code.
/// </summary>
public partial class RoomEditorPanel : UserControl
{
    public RoomEditorPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    StackPanel BodyPanel => this.FindControl<StackPanel>("Body")!;

    MainWindowViewModel? _vm;

    void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        Rebuild();
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedRoomId)
                          or nameof(MainWindowViewModel.EditVersion)
                          or nameof(MainWindowViewModel.CurrentEnvironment))
        {
            Rebuild();
        }
    }

    void Rebuild()
    {
        BodyPanel.Children.Clear();
        if (_vm is null || _vm.SelectedRoomId < 0) return;
        int roomId = _vm.SelectedRoomId;

        BodyPanel.Children.Add(new TextBlock
        {
            Text = $"Room {roomId}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // Floor section.
        BodyPanel.Children.Add(SectionHeader("Floor"));
        BodyPanel.Children.Add(ColorRow(
            label: "Floor color",
            current: _vm.RoomFloorColors.TryGetValue(roomId, out var fc) ? (System.Numerics.Vector3?)fc : null,
            setter: c => _vm.SetRoomFloorColor(roomId, c),
            clearer: () => _vm.ClearRoomFloorColor(roomId)));

        // Ceiling section.
        BodyPanel.Children.Add(SectionHeader("Ceiling", topMargin: 14));
        BodyPanel.Children.Add(ColorRow(
            label: "Ceiling color",
            current: _vm.RoomCeilingColors.TryGetValue(roomId, out var cc) ? (System.Numerics.Vector3?)cc : null,
            setter: c => _vm.SetRoomCeilingColor(roomId, c),
            clearer: () => _vm.ClearRoomCeilingColor(roomId)));

        // Walls section.
        BodyPanel.Children.Add(SectionHeader("Walls", topMargin: 14));

        bool multi = _vm.IsRoomMultiColor(roomId);
        var multiToggle = new CheckBox
        {
            Content = "Multi-color walls",
            IsChecked = multi,
            Margin = new Thickness(0, 0, 0, 6),
        };
        multiToggle.IsCheckedChanged += (_, _) =>
        {
            _vm.SetRoomMultiColor(roomId, multiToggle.IsChecked == true);
            Rebuild();
        };
        BodyPanel.Children.Add(multiToggle);

        if (!multi)
        {
            BodyPanel.Children.Add(ColorRow(
                label: "Wall color",
                current: _vm.RoomSingleWallColors.TryGetValue(roomId, out var wc) ? (System.Numerics.Vector3?)wc : null,
                setter: c => _vm.SetRoomSingleWallColor(roomId, c),
                clearer: () => _vm.ClearRoomSingleWallColor(roomId)));
        }
        else
        {
            // One row per wall the room participates in.
            int idx = 1;
            foreach (var adj in _vm.SelectedRoomAdjacencies)
            {
                int wallNum = idx++;
                var key = OpenApparatus.Studio.Services.GltfExporter.WallColorKey(roomId, adj);
                System.Numerics.Vector3? cur = _vm.WallColors.TryGetValue(key, out var v) ? v : null;
                var capturedAdj = adj;
                BodyPanel.Children.Add(ColorRow(
                    label: $"Wall {wallNum}",
                    current: cur,
                    setter: c => _vm.SetWallColor(roomId, capturedAdj, c),
                    clearer: () => _vm.ClearWallColor(roomId, capturedAdj)));
            }
        }
    }

    static TextBlock SectionHeader(string text, double topMargin = 0)
    {
        return new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, topMargin, 0, 4),
        };
    }

    /// <summary>
    /// Single row: a colored swatch + label. Click opens the color dialog,
    /// right-click clears the override back to default.
    /// </summary>
    Control ColorRow(
        string label,
        System.Numerics.Vector3? current,
        System.Action<System.Numerics.Vector3> setter,
        System.Action clearer)
    {
        var swatch = new Border
        {
            Width = 28,
            Height = 22,
            BorderBrush = new SolidColorBrush(Color.FromRgb(120, 120, 130)),
            BorderThickness = new Thickness(1),
            Background = current.HasValue
                ? new SolidColorBrush(Color.FromRgb(
                    (byte)(current.Value.X * 255),
                    (byte)(current.Value.Y * 255),
                    (byte)(current.Value.Z * 255)))
                : (IBrush)new SolidColorBrush(Color.FromRgb(199, 199, 204)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var labelBlock = new TextBlock
        {
            Text = label + (current.HasValue ? "" : "  (default)"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var resetBtn = new Button
        {
            Content = "Reset",
            FontSize = 10,
            Padding = new Thickness(4, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = current.HasValue,
        };
        resetBtn.Click += (_, _) => { clearer(); Rebuild(); };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
        };
        stack.Children.Add(swatch);
        stack.Children.Add(labelBlock);
        stack.Children.Add(resetBtn);

        var clickRow = new Button
        {
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 4),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(1),
        };
        clickRow.Click += async (_, _) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner is null) return;
            var dlg = new WallColorDialog();
            dlg.Configure(label, current);
            await dlg.ShowDialog(owner);
            switch (dlg.ChosenOutcome)
            {
                case WallColorDialog.Outcome.Set:
                    setter(dlg.ChosenColor);
                    Rebuild();
                    break;
                case WallColorDialog.Outcome.Reset:
                    clearer();
                    Rebuild();
                    break;
            }
        };
        return clickRow;
    }
}
