using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OpenApparatus.Studio.Themes;
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
                          or nameof(MainWindowViewModel.SelectedObjectIndex)
                          or nameof(MainWindowViewModel.SelectedObjectsCount)
                          or nameof(MainWindowViewModel.HasMultipleObjectsSelected)
                          or nameof(MainWindowViewModel.ThemeVariant))
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
            // Multi-select takes priority — the user explicitly assembled
            // a group, so the inspector should reflect that.
            if (_vm.SelectedObjectIndices.Count > 1)
                BuildMultiSelectEditor();
            else if (_vm.SelectedObjectIndex >= 0 && _vm.SelectedObjectIndex < _vm.Objects.Count)
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

    // Inspector design tokens — short aliases for the shared Tokens
    // accessor so the inline call sites stay readable. Tokens itself
    // resolves against the active theme variant at access time, so the
    // inspector tracks Light/Dark when rebuilt.
    static IBrush TextPrimary   => Tokens.TextPrimary;
    static IBrush TextSecondary => Tokens.TextSecondary;
    static IBrush TextMuted     => Tokens.TextMuted;
    static IBrush BorderHair    => Tokens.BorderHairline;
    static IBrush SurfaceRaised => Tokens.SurfaceRaised;

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
        AddPlaceholderBody("Click an object to edit. Ctrl-click to multi-select. Click a room tile to see every object in that room.");
    }

    /// <summary>Inspector body when more than one object is selected via
    /// Ctrl/Shift-click. Shows a summary + a single bulk Delete action.
    /// Per-object editing is deliberately not exposed at this level — it
    /// would need true multi-edit support (mixed-value indicators etc).</summary>
    void BuildMultiSelectEditor()
    {
        if (_vm is null) return;
        int n = _vm.SelectedObjectIndices.Count;
        AddInspectorHeader($"{n} objects selected", "Ctrl-click to add / remove · Esc to clear");

        // Summary list — show up to ~12 distinct types so the user can
        // sanity-check what's in the set without scrolling forever.
        var byType = new Dictionary<string, int>();
        foreach (var idx in _vm.SelectedObjectIndices)
        {
            if (idx < 0 || idx >= _vm.Objects.Count) continue;
            var t = _vm.GetObjectType(_vm.Objects[idx].Slot);
            string name = t?.Name ?? $"Type {_vm.Objects[idx].Slot}";
            byType[name] = byType.TryGetValue(name, out var c) ? c + 1 : 1;
        }
        var summary = new StackPanel { Spacing = 4 };
        foreach (var kv in byType.OrderByDescending(kv => kv.Value).Take(12))
        {
            summary.Children.Add(new TextBlock
            {
                Text = $"{kv.Value}× {kv.Key}",
                FontSize = 12,
                Foreground = TextSecondary,
            });
        }
        BodyPanel.Children.Add(new Border
        {
            Background = SurfaceRaised,
            BorderBrush = BorderHair,
            BorderThickness = new Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = summary,
        });

        var bulkDelete = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xB6, 0xB6)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x28, 0x28)),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 6),
            Content = $"Delete {n} objects",
        };
        bulkDelete.Click += (_, _) => _vm.DeleteSelectedObjectsCommand.Execute(null);
        BodyPanel.Children.Add(bulkDelete);
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
            sel.OwningRoomId < 0 ? "outside" : $"Room {sel.OwningRoomId}",
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

    /// <summary>Builds a horizontal Vector3 row (label on top, three
    /// axis-coloured NumericUpDowns side-by-side). Each axis carries
    /// its standard colour: X = red, Y = green, Z = blue. Used by the
    /// object inspector for Position; future use for Scale / etc.
    /// would be a one-line drop-in.</summary>
    Control BuildVector3Row(
        string label, string unit,
        float x, float y, float z,
        (double Min, double Max, double Step) xRange,
        (double Min, double Max, double Step) yRange,
        (double Min, double Max, double Step) zRange,
        System.Action<float, float, float> onChanged)
    {
        var holder = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        headerRow.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        headerRow.Children.Add(new TextBlock
        {
            Text = $"({unit})",
            FontSize = 11,
            Foreground = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        });
        holder.Children.Add(headerRow);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 6,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // Track values so each handler can recompose the full Vector3.
        float curX = x, curY = y, curZ = z;
        bool suppress = false;

        Control AxisCell(int col, string letter, Color axisColor,
                         double value, (double Min, double Max, double Step) range,
                         System.Action<float> setLocal)
        {
            var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            // Coloured letter chip on the left.
            var chip = new Border
            {
                Background = new SolidColorBrush(axisColor),
                CornerRadius = new Avalonia.CornerRadius(3, 0, 0, 3),
                Padding = new Thickness(6, 0),
                Width = 18,
                Child = new TextBlock
                {
                    Text = letter,
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(chip, 0);
            cell.Children.Add(chip);

            var box = new NumericUpDown
            {
                Value = (decimal)value,
                Minimum = (decimal)range.Min,
                Maximum = (decimal)range.Max,
                Increment = (decimal)range.Step,
                FormatString = "0.00",
                ShowButtonSpinner = false,
                MinHeight = 0,
                Height = 28,
                Padding = new Thickness(6, 4),
                FontSize = 12,
                Background = Tokens.SurfacePrimary,
                BorderBrush = Tokens.SurfacePressed,
                BorderThickness = new Thickness(0, 1, 1, 1),
                CornerRadius = new Avalonia.CornerRadius(0, 3, 3, 0),
            };
            box.ValueChanged += (_, e) =>
            {
                if (suppress) return;
                if (!e.NewValue.HasValue) return;
                setLocal((float)e.NewValue.Value);
                onChanged(curX, curY, curZ);
            };
            Grid.SetColumn(box, 1);
            cell.Children.Add(box);
            Grid.SetColumn(cell, col);
            return cell;
        }

        // Standard axis colour conventions — red/green/blue at slightly
        // muted saturation so they don't shout next to the AR HUD emerald.
        grid.Children.Add(AxisCell(0, "X", Color.FromRgb(0xD0, 0x46, 0x46),
            x, xRange, v => { curX = v; }));
        grid.Children.Add(AxisCell(1, "Y", Color.FromRgb(0x4D, 0xA8, 0x55),
            y, yRange, v => { curY = v; }));
        grid.Children.Add(AxisCell(2, "Z", Color.FromRgb(0x3A, 0x6E, 0xC4),
            z, zRange, v => { curZ = v; }));

        holder.Children.Add(grid);
        return holder;
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
            $"Room {roomId} · objects",
            indices.Count == 1 ? "1 object" : $"{indices.Count} objects");

        if (indices.Count == 0)
        {
            AddPlaceholderBody("Click a sub-cell, then 1–9.");
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
            Text = t is null ? $"Type {_vm.Objects[idx].Slot}" : t.Name,
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
            Text = $"Room: {(sel.OwningRoomId < 0 ? "outside" : sel.OwningRoomId.ToString())}",
            Foreground = TextMuted,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        });

        // Position — single horizontal row with X/Y/Z side-by-side, each
        // labelled in its axis colour (Unity / Blender / Houdini convention:
        // red X, green Y, blue Z). Reads as one transform unit instead of
        // four floating numeric inputs.
        host.Children.Add(BuildVector3Row(
            "Position", "m",
            sel.Position.X, sel.Position.Y, sel.Position.Z,
            (-100, 100, 0.05),
            (0, 5, 0.05),
            (-100, 100, 0.05),
            (x, y, z) =>
            {
                sel.Position = new System.Numerics.Vector3(x, y, z);
                _vm.OnEditedSelectedObject();
            }));
        host.Children.Add(NumericRow("Rotation (°)", sel.Rotation * (180.0 / System.Math.PI), -360, 360, 5,
            v => { sel.Rotation = (float)(v * System.Math.PI / 180.0); _vm.OnEditedSelectedObject(); }));

        // Destructive button styled with warning red so the user
        // recognises this isn't a routine action.
        var deleteBtn = new Button
        {
            Content = "Delete",
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
                Content = "Set as start",
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
            label: "Floor colour",
            current: _vm.RoomFloorColors.TryGetValue(roomId, out var fc) ? (System.Numerics.Vector3?)fc : null,
            setter: c => _vm.SetRoomFloorColor(roomId, c),
            clearer: () => _vm.ClearRoomFloorColor(roomId)));

        // Ceiling section.
        BodyPanel.Children.Add(SectionHeader("Ceiling", topMargin: 14));
        BodyPanel.Children.Add(ColorRow(
            label: "Ceiling colour",
            current: _vm.RoomCeilingColors.TryGetValue(roomId, out var cc) ? (System.Numerics.Vector3?)cc : null,
            setter: c => _vm.SetRoomCeilingColor(roomId, c),
            clearer: () => _vm.ClearRoomCeilingColor(roomId)));

        // Walls section.
        BodyPanel.Children.Add(SectionHeader("Walls", topMargin: 14));

        bool multi = _vm.IsRoomMultiColor(roomId);
        var multiToggle = new CheckBox
        {
            Content = "Per-wall colours",
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
                label: "Wall colour",
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

        AddInspectorHeader(label);

        // Width
        BodyPanel.Children.Add(NumericRow(
            "Width (m)", op.Width, 0.3, 6.0, 0.1,
            v => _vm.UpdateSelectedOpening(o => o.With(width: (float)v))));

        // Top of opening (head height).
        BodyPanel.Children.Add(NumericRow(
            "Top (m)", op.Height, isWindow ? 0.5 : 1.5, 4.0, 0.1,
            v => _vm.UpdateSelectedOpening(o => o.With(height: (float)v))));

        if (isWindow)
        {
            // Bottom of opening (sill height).
            BodyPanel.Children.Add(NumericRow(
                "Bottom (m)", op.SillHeight, 0.05, 2.5, 0.05,
                v => _vm.UpdateSelectedOpening(o => o.With(sillHeight: (float)v))));
        }
        else
        {
            // Door swing controls.
            BodyPanel.Children.Add(SectionHeader("Hinge", topMargin: 12));
            BodyPanel.Children.Add(ToggleRow(
                "Hinge right",
                op.HingeAtEnd,
                v => _vm.UpdateSelectedOpening(o => o.With(hingeAtEnd: v))));

            BodyPanel.Children.Add(SectionHeader("Swing", topMargin: 12));
            BodyPanel.Children.Add(ToggleRow(
                _vm.SelectedAdjacency?.IsInternal == true
                    ? $"Swing into Room {_vm.SelectedAdjacency.RoomA.Id}"
                    : "Swing outward",
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
        // Classes="compact" pulls the project-wide stepper chrome (rounded
        // outer border with hairline-divided − / + cells) so this panel's
        // numeric inputs match the document settings + placement-constraints
        // panels rather than falling back to Avalonia's bulky default.
        var box = new NumericUpDown
        {
            Classes = { "compact" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
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
