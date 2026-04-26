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
        // Deliberately NOT listening to EditVersion: any text input or
        // NumericUpDown edit in this panel mutates VM state that bumps
        // EditVersion, which would otherwise rebuild the panel mid-edit and
        // either lose focus or — when the input fires a re-entrant TextChanged
        // on tree-attach — spin into an infinite rebuild loop. Visual-only
        // changes (path colour, ShowPaths, opacity, etc.) live in the editor
        // view, not in this panel, so skipping EditVersion costs nothing.
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedRoomId)
                          or nameof(MainWindowViewModel.SelectedOpeningIndex)
                          or nameof(MainWindowViewModel.HasSelectedOpening)
                          or nameof(MainWindowViewModel.CurrentEnvironment))
        {
            Rebuild();
        }
    }

    void Rebuild()
    {
        BodyPanel.Children.Clear();
        if (_vm is null) return;

        // Opening edits take precedence — when an opening is selected the user
        // is in 'tweak this door / window' context; the room panel can wait.
        if (_vm.HasSelectedOpening)
        {
            BuildOpeningEditor();
            return;
        }
        if (_vm.SelectedRoomId < 0) return;
        BuildRoomEditor();
    }

    void BuildRoomEditor()
    {
        int roomId = _vm!.SelectedRoomId;

        BodyPanel.Children.Add(new TextBlock
        {
            Text = roomId == 0 ? "Room 0 (start)" : $"Room {roomId}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 8),
        });

        // Start-room marker. The button is hidden when this room is already
        // the start (id 0) so the panel doesn't show a dead-action button.
        if (roomId != 0)
        {
            var markBtn = new Button
            {
                Content = "Mark as start room",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 6),
                Margin = new Thickness(0, 0, 0, 12),
            };
            ToolTip.SetTip(markBtn,
                "Renumber rooms so this one becomes Room 0. " +
                "Other rooms are reassigned by BFS distance, with ties broken by their previous id.");
            markBtn.Click += async (_, _) => await OnMarkAsStart(roomId);
            BodyPanel.Children.Add(markBtn);
        }

        // Name input.
        BodyPanel.Children.Add(SectionHeader("Name"));
        var nameBox = new TextBox
        {
            Watermark = "Optional",
            Text = _vm.GetRoomName(roomId),
            Margin = new Thickness(0, 0, 0, 6),
        };
        nameBox.TextChanged += (_, _) =>
        {
            // Update on every keystroke; cheap because the dict + EditVersion bump
            // are O(1) and the editor view is the only consumer that re-renders.
            _vm.SetRoomName(roomId, nameBox.Text);
        };
        BodyPanel.Children.Add(nameBox);

        // Floor section.
        BodyPanel.Children.Add(SectionHeader("Floor", topMargin: 14));
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

    void BuildOpeningEditor()
    {
        var op = _vm!.SelectedOpening!.Value;
        bool isWindow = op.IsWindow;
        string label = isWindow ? "Window" : "Door";

        BodyPanel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // Width
        BodyPanel.Children.Add(NumericRow(
            "Width (m)", op.Width, 0.3, 6.0, 0.1,
            v => _vm.UpdateSelectedOpening(o => o.With(width: (float)v))));

        // Head height
        BodyPanel.Children.Add(NumericRow(
            "Head height (m)", op.Height, isWindow ? 0.5 : 1.5, 4.0, 0.1,
            v => _vm.UpdateSelectedOpening(o => o.With(height: (float)v))));

        if (isWindow)
        {
            BodyPanel.Children.Add(NumericRow(
                "Sill height (m)", op.SillHeight, 0.05, 2.5, 0.05,
                v => _vm.UpdateSelectedOpening(o => o.With(sillHeight: (float)v))));
        }
        else
        {
            // Door swing controls.
            BodyPanel.Children.Add(SectionHeader("Hinge", topMargin: 12));
            BodyPanel.Children.Add(ToggleRow(
                "At end of opening",
                op.HingeAtEnd,
                v => _vm.UpdateSelectedOpening(o => o.With(hingeAtEnd: v))));

            BodyPanel.Children.Add(SectionHeader("Swing", topMargin: 12));
            BodyPanel.Children.Add(ToggleRow(
                _vm.SelectedAdjacency?.IsInternal == true
                    ? $"Into Room {_vm.SelectedAdjacency.RoomA.Id} (flip swing)"
                    : "Reverse swing",
                op.SwingNegative != (_vm.SelectedAdjacency?.IsInternal == true),
                v =>
                {
                    bool isInternal = _vm.SelectedAdjacency?.IsInternal == true;
                    bool wantNegative = isInternal ? !v : v;
                    _vm.UpdateSelectedOpening(o => o.With(swingNegative: wantNegative));
                }));
        }
    }

    Control NumericRow(string label, double value, double min, double max, double step,
        System.Action<double> onChanged)
    {
        var box = new NumericUpDown
        {
            Value = (decimal)value,
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)step,
            FormatString = "0.00",
        };
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue.HasValue) onChanged((double)e.NewValue.Value);
        };

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) });
        stack.Children.Add(box);
        return stack;
    }

    async System.Threading.Tasks.Task OnMarkAsStart(int oldId)
    {
        if (_vm is null) return;
        // Renumber first so the IDs the user sees afterwards line up. Then
        // ask whether to flip door swings to point into the new higher rooms.
        _vm.MarkSelectedRoomAsStartCommand.Execute(null);
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var dlg = new ConfirmDialog();
        dlg.Configure("Update door swings?",
            $"Room {oldId} is now Room 0 (start).\n\n" +
            "Would you like every door's swing to be updated so it opens " +
            "into the higher-numbered room? (Windows are unaffected.)");
        await dlg.ShowDialog(owner);
        if (dlg.Result) _vm.UpdateDoorSwingsToHigherRoom();
    }

    Control ToggleRow(string label, bool value, System.Action<bool> onChanged)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Margin = new Thickness(0, 0, 0, 4),
        };
        cb.IsCheckedChanged += (_, _) => onChanged(cb.IsChecked == true);
        return cb;
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
