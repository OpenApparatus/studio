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
                          or nameof(MainWindowViewModel.CurrentEnvironment)
                          or nameof(MainWindowViewModel.ViewMode)
                          or nameof(MainWindowViewModel.IsObjectsMode)
                          or nameof(MainWindowViewModel.SelectedObjectIndex))
        {
            Rebuild();
        }
    }

    void Rebuild()
    {
        BodyPanel.Children.Clear();
        if (_vm is null) return;

        // ── Object mode ──
        // The right panel is the per-selection editor only:
        //   - object selected → its type + transform + delete
        //   - room selected (via clicking a tile) → stacked editors for every
        //     object in that room
        //   - nothing → placeholder
        if (_vm.IsObjectsMode)
        {
            if (_vm.SelectedObjectIndex >= 0 && _vm.SelectedObjectIndex < _vm.Objects.Count)
                BuildSingleObjectEditor(_vm.SelectedObjectIndex);
            else if (_vm.SelectedRoomId >= 0)
                BuildStackedObjectsForRoom(_vm.SelectedRoomId);
            else
                BuildObjectModePlaceholder();
            return;
        }

        // ── Layout mode ──
        // Opening edit takes precedence (tweak this door / window), then room
        // appearance, then placeholder.
        if (_vm.HasSelectedOpening)
        {
            BuildOpeningEditor();
            return;
        }
        if (_vm.SelectedRoomId < 0)
        {
            BuildPlaceholder();
            return;
        }
        BuildRoomEditor();
    }

    // Inspector design tokens — duplicated from Themes/Tokens.axaml so the
    // code-behind builders can match the XAML palette without round-tripping
    // through Application.Resources.
    static readonly IBrush TextPrimary   = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E));
    static readonly IBrush TextSecondary = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70));
    static readonly IBrush TextMuted     = new SolidColorBrush(Color.FromRgb(0x7A, 0x80, 0x8C));
    static readonly IBrush BorderHair    = new SolidColorBrush(Color.FromRgb(0xD3, 0xD7, 0xDF));
    static readonly IBrush SurfaceRaised = new SolidColorBrush(Colors.White);

    /// <summary>Inspector header — title + optional subtitle, separated from
    /// the body by a hairline rule. Used by every Build* state so the
    /// inspector reads as one consistent surface across selections.</summary>
    void AddInspectorHeader(string title, string? subtitle = null)
    {
        BodyPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = TextPrimary,
            Margin = new Thickness(0, 0, 0, subtitle is null ? 8 : 2),
        });
        if (subtitle is not null)
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = TextMuted,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }
        BodyPanel.Children.Add(new Border
        {
            Height = 1,
            Background = BorderHair,
            Margin = new Thickness(0, 0, 0, 12),
        });
    }

    /// <summary>Empty-state body copy. Wraps long text to the panel width and
    /// uses muted colour so the placeholder reads as not-interactive.</summary>
    void AddPlaceholderBody(string text)
    {
        BodyPanel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextMuted,
            FontSize = 12,
        });
    }

    void BuildObjectModePlaceholder()
    {
        AddInspectorHeader("No selection");
        AddPlaceholderBody("Click an object to edit its type, position, or rotation. Click a tile in a room to view all of that room's objects together.");
    }

    void BuildSingleObjectEditor(int idx)
    {
        // Header carries a small shape+colour preview so the user
        // recognises which object they're editing without having to
        // glance back at the canvas.
        var sel = _vm!.Objects[idx];
        var t = _vm.GetObjectType(sel.Slot);
        AddInspectorHeaderWithObjectPreview(
            t?.Name ?? "Selected object",
            $"slot {sel.Slot}  ·  room {(sel.OwningRoomId < 0 ? "outside" : sel.OwningRoomId.ToString())}",
            t);
        BuildObjectEditorRows(idx);
    }

    /// <summary>Same chrome as AddInspectorHeader, but with a 26-px swatch
    /// preview on the left showing the object type's colour + a glyph
    /// for its shape. Only used for object selections.</summary>
    void AddInspectorHeaderWithObjectPreview(string title, string? subtitle, OpenApparatus.Studio.ViewModels.ObjectType? type)
    {
        var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, subtitle is null ? 8 : 2) };
        if (type is not null)
        {
            float lum = type.Color.X * 0.299f + type.Color.Y * 0.587f + type.Color.Z * 0.114f;
            var glyphBrush = new SolidColorBrush(lum > 0.6f ? Color.FromRgb(20, 20, 26) : Colors.White);
            headerStack.Children.Add(new Border
            {
                Width = 26, Height = 26,
                Background = new SolidColorBrush(Color.FromRgb(
                    (byte)(type.Color.X * 255), (byte)(type.Color.Y * 255), (byte)(type.Color.Z * 255))),
                BorderBrush = BorderHair,
                BorderThickness = new Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ShapeGlyph(type.Shape),
                    FontSize = 14,
                    Foreground = glyphBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        headerStack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        BodyPanel.Children.Add(headerStack);
        if (subtitle is not null)
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = TextMuted,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }
        BodyPanel.Children.Add(new Border
        {
            Height = 1,
            Background = BorderHair,
            Margin = new Thickness(0, 0, 0, 12),
        });
    }

    /// <summary>Compass-direction caption for a wall, derived from the
    /// adjacency segment's outward normal in world XZ. World X is east,
    /// world Z is south. Returns one of "north / east / south / west".</summary>
    static string WallCompassDirection(OpenApparatus.Topology.Adjacency adj)
    {
        var n = adj.SharedSegment.Normal;
        // n.x > 0 → wall faces +X → east; etc.
        // Larger magnitude axis wins.
        if (System.Math.Abs(n.X) >= System.Math.Abs(n.Y))
            return n.X > 0 ? "east" : "west";
        return n.Y > 0 ? "south" : "north";
    }

    static string ShapeGlyph(OpenApparatus.Studio.ViewModels.ObjectShape shape) => shape switch
    {
        OpenApparatus.Studio.ViewModels.ObjectShape.Cube          => "■",
        OpenApparatus.Studio.ViewModels.ObjectShape.Sphere        => "●",
        OpenApparatus.Studio.ViewModels.ObjectShape.Cylinder      => "▮",
        OpenApparatus.Studio.ViewModels.ObjectShape.SquatCylinder => "▬",
        OpenApparatus.Studio.ViewModels.ObjectShape.Cone          => "▲",
        OpenApparatus.Studio.ViewModels.ObjectShape.Capsule       => "⬭",
        OpenApparatus.Studio.ViewModels.ObjectShape.Pyramid       => "◆",
        _ => "?",
    };

    void BuildStackedObjectsForRoom(int roomId)
    {
        var indices = new System.Collections.Generic.List<int>();
        for (int i = 0; i < _vm!.Objects.Count; i++)
            if (_vm.Objects[i].OwningRoomId == roomId) indices.Add(i);

        AddInspectorHeader(
            $"Room {roomId} objects",
            indices.Count == 1 ? "1 object" : $"{indices.Count} objects");

        if (indices.Count == 0)
        {
            AddPlaceholderBody("No objects in this room yet. Switch to a sub-cell and press 1–9 to place one.");
            return;
        }

        foreach (var i in indices)
        {
            BodyPanel.Children.Add(new Border
            {
                Background = SurfaceRaised,
                BorderBrush = BorderHair,
                BorderThickness = new Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = BuildObjectCard(i),
            });
        }
    }

    Control BuildObjectCard(int idx)
    {
        var sp = new StackPanel { Spacing = 0 };
        var t = _vm!.GetObjectType(_vm.Objects[idx].Slot);
        sp.Children.Add(new TextBlock
        {
            Text = t is null ? $"Slot {_vm.Objects[idx].Slot}" : $"{t.Name} (slot {_vm.Objects[idx].Slot})",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        AddObjectEditorControls(sp, idx);
        return sp;
    }

    void BuildObjectEditorRows(int idx)
    {
        AddObjectEditorControls(BodyPanel, idx);
    }

    void AddObjectEditorControls(StackPanel host, int idx)
    {
        if (_vm is null) return;
        if (idx < 0 || idx >= _vm.Objects.Count) return;
        var sel = _vm.Objects[idx];

        // Type chooser — combo over all object types so the user can swap
        // which type this instance is.
        var typeRow = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        typeRow.Children.Add(new TextBlock { Text = "Type", FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int ti = 0; ti < _vm.ObjectTypes.Count; ti++)
            combo.Items.Add(_vm.ObjectTypes[ti].Name);
        combo.SelectedIndex = sel.Slot - 1;
        combo.SelectionChanged += (_, _) =>
        {
            int newIdx = combo.SelectedIndex;
            if (newIdx < 0) return;
            sel.Slot = newIdx + 1;
            _vm.OnEditedSelectedObject();
        };
        typeRow.Children.Add(combo);
        host.Children.Add(typeRow);

        host.Children.Add(new TextBlock
        {
            Text = $"Owning room: {(sel.OwningRoomId < 0 ? "outside" : sel.OwningRoomId.ToString())}",
            Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 120)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        });

        host.Children.Add(NumericRow("X (m)", sel.Position.X, -100, 100, 0.05,
            v => { sel.Position = new System.Numerics.Vector3((float)v, sel.Position.Y, sel.Position.Z); _vm.OnEditedSelectedObject(); }));
        host.Children.Add(NumericRow("Y (m)", sel.Position.Y, 0, 5, 0.05,
            v => { sel.Position = new System.Numerics.Vector3(sel.Position.X, (float)v, sel.Position.Z); _vm.OnEditedSelectedObject(); }));
        host.Children.Add(NumericRow("Z (m)", sel.Position.Z, -100, 100, 0.05,
            v => { sel.Position = new System.Numerics.Vector3(sel.Position.X, sel.Position.Y, (float)v); _vm.OnEditedSelectedObject(); }));
        host.Children.Add(NumericRow("Rotation (°)", sel.Rotation * (180.0 / System.Math.PI), -360, 360, 5,
            v => { sel.Rotation = (float)(v * System.Math.PI / 180.0); _vm.OnEditedSelectedObject(); }));

        // Destructive button styled with warning red so the user
        // recognises this isn't a routine action.
        var deleteBtn = new Button
        {
            Content = "Delete object",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xB6, 0xB6)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x28, 0x28)),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 6),
        };
        int capturedIdx = idx;
        deleteBtn.Click += (_, _) =>
        {
            _vm.SelectedObjectIndex = capturedIdx;
            _vm.DeleteSelectedObjectCommand.Execute(null);
        };
        host.Children.Add(deleteBtn);
    }

    // BuildObjectsEditor was replaced by BuildSingleObjectEditor /
    // BuildStackedObjectsForRoom (object-mode right panel) plus the
    // ObjectTypesPanel control on the left for the type list. The
    // sub-grid + Snap + Constraints all live on the left panel now.

    /// <summary>Apply a side-effecting change to the selected object's mutable
    /// fields, then bump EditVersion so the editor view repaints.</summary>
    void MutateSelectedObject(System.Action<OpenApparatus.Studio.ViewModels.RoomObject> change)
    {
        var sel = _vm?.SelectedObject;
        if (sel is null) return;
        change(sel);
        _vm!.OnEditedSelectedObject();
    }

    Control TypeRow(int index, OpenApparatus.Studio.ViewModels.ObjectType type)
    {
        // 30×30 swatch button — clicking opens the shape/color picker.
        var swatchBtn = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(
                (byte)(type.Color.X * 255), (byte)(type.Color.Y * 255), (byte)(type.Color.Z * 255))),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 70)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(swatchBtn, $"{type.Shape} — click to edit shape and colour.");
        swatchBtn.Click += async (_, _) => await OnEditType(index);

        var slotLabel = new TextBlock
        {
            Text = $"{index + 1}.",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            MinWidth = 18,
        };

        var nameBox = new TextBox
        {
            Text = type.Name,
            Watermark = $"Object {index + 1}",
            VerticalAlignment = VerticalAlignment.Center,
            Width = 110,
        };
        // Mirror the room-name pattern: only commit changes that actually
        // differ from the stored name, so Avalonia's tree-attach TextChanged
        // doesn't loop through OnEditedObjectType.
        string lastCommitted = type.Name;
        nameBox.TextChanged += (_, _) =>
        {
            var t = (nameBox.Text ?? string.Empty).Trim();
            if (t == lastCommitted) return;
            lastCommitted = t;
            type.Name = t.Length > 0 ? t : $"Object {index + 1}";
            _vm!.OnEditedObjectType();
        };

        var delBtn = new Button
        {
            Content = "×",
            FontSize = 13,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            IsEnabled = _vm!.ObjectTypes.Count > 1,
        };
        ToolTip.SetTip(delBtn, _vm.ObjectTypes.Count > 1
            ? "Remove this object type."
            : "At least one object type is required.");
        delBtn.Click += (_, _) =>
        {
            _vm.RemoveObjectType(index);
            Rebuild();
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
        };
        stack.Children.Add(swatchBtn);
        stack.Children.Add(slotLabel);
        stack.Children.Add(nameBox);
        stack.Children.Add(delBtn);
        return stack;
    }

    async System.Threading.Tasks.Task OnEditType(int index)
    {
        if (_vm is null || index < 0 || index >= _vm.ObjectTypes.Count) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;
        var type = _vm.ObjectTypes[index];
        var dlg = new ObjectTypePickerDialog();
        dlg.Configure(type.Shape, type.Color);
        await dlg.ShowDialog(owner);
        if (dlg.Confirmed)
        {
            type.Shape = dlg.ChosenShape;
            type.Color = dlg.ChosenColor;
            _vm.OnEditedObjectType();
            Rebuild();
        }
    }

    void BuildPlaceholder()
    {
        AddInspectorHeader("No selection");
        AddPlaceholderBody("Click a tile in a room to edit its appearance, or click a wall and add a door / window to edit it.");
    }

    void BuildRoomEditor()
    {
        int roomId = _vm!.SelectedRoomId;

        AddInspectorHeader(
            $"Room {roomId}",
            roomId == 0 ? "start room" : null);

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
            // One row per wall the room participates in. Label includes
            // a compass-direction hint + which other room it borders so
            // the user can identify it without staring at the canvas.
            int idx = 1;
            foreach (var adj in _vm.SelectedRoomAdjacencies)
            {
                int wallNum = idx++;
                var key = OpenApparatus.Studio.Services.GltfExporter.WallColorKey(roomId, adj);
                System.Numerics.Vector3? cur = _vm.WallColors.TryGetValue(key, out var v) ? v : null;
                var capturedAdj = adj;
                string dir = WallCompassDirection(adj);
                string neighbour = adj.IsOuter
                    ? "outside"
                    : (adj.RoomA.Id == roomId
                        ? $"Room {adj.RoomB!.Id}"
                        : $"Room {adj.RoomA.Id}");
                BodyPanel.Children.Add(ColorRow(
                    label: $"Wall {wallNum} — {dir}, {neighbour}",
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

        AddInspectorHeader(label,
            isWindow ? "in selected wall" : "in selected wall");

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
