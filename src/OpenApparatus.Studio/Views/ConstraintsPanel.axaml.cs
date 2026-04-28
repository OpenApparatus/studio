using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Left-panel host for the Placement-constraints section. Constraints are
/// global so they live in the left panel (the right panel is contextual,
/// reserved for the selected room / opening / object). Hidden when ViewMode
/// is not Objects so the left panel doesn't grow unnecessarily.
/// </summary>
public partial class ConstraintsPanel : UserControl
{
    MainWindowViewModel? _vm;

    public ConstraintsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    StackPanel Body => this.FindControl<StackPanel>("ConstraintsBody")!;

    void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        Rebuild();
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ViewMode)
                          or nameof(MainWindowViewModel.IsObjectsMode)
                          or nameof(MainWindowViewModel.Constraints)
                          or nameof(MainWindowViewModel.ConstraintViolations)
                          or nameof(MainWindowViewModel.ConstraintComplianceSummary)
                          or nameof(MainWindowViewModel.CurrentEnvironment)
                          // EditVersion catches in-canvas edits that affect compliance.
                          or nameof(MainWindowViewModel.EditVersion))
        {
            Rebuild();
        }
    }

    void Rebuild()
    {
        Body.Children.Clear();
        if (_vm is null) return;
        // Constraints panel is only meaningful in Objects mode.
        IsVisible = _vm.IsObjectsMode;
        if (!_vm.IsObjectsMode) return;

        var c = _vm.Constraints;

        Body.Children.Add(new TextBlock
        {
            Text = "Placement constraints",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 4),
        });

        Body.Children.Add(new TextBlock
        {
            Text = _vm.ConstraintComplianceSummary,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        });

        Body.Children.Add(new TextBlock
        {
            Text = "Validation only — placements are never blocked. Sub-cells where every active constraint is satisfied are tinted; violators get a red dashed ring.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 130)),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 8),
        });

        // Scope toggle — when on, the valid-region overlay is drawn for every
        // room (each tinted with its wall colour). When off, the overlay is
        // scoped to the room of the currently-selected sub-cell or object so
        // it stays out of the way until the user is actively placing.
        Body.Children.Add(BoolRow("Show all constraints", c.ShowAllConstraints,
            v => { c.ShowAllConstraints = v; _vm.OnConstraintsChanged(); },
            "On: valid-placement overlays render for every room, tinted with each room's wall colour. Off: only the room of the selected sub-cell / object shows its overlay."));

        // Highlight-mode selector — Area (centre-only green tint) vs.
        // Placement Grid (also yellow for sub-cells whose corners straddle the
        // valid region's boundary).
        Body.Children.Add(EnumRow(
            "Highlight mode",
            new[] { ("Area", ConstraintHighlightMode.Area), ("Placement Grid", ConstraintHighlightMode.PlacementGrid) },
            c.HighlightMode,
            v => { c.HighlightMode = v; _vm.OnConstraintsChanged(); },
            "Area: tint sub-cells whose centre is in the valid region (green). Placement Grid: also tint partial sub-cells in yellow when any corner falls in the valid region but the centre doesn't."));

        // ─── Object ↔ Object ───
        var oo = ConstraintGroup("Object ↔ Object", c.ObjectToObjectEnabled,
            v => { c.ObjectToObjectEnabled = v; _vm.OnConstraintsChanged(); });
        oo.Children.Add(MetreField("Min distance (m)", c.ObjectToObjectMin,
            v => { c.ObjectToObjectMin = (float)v; _vm.OnConstraintsChanged(); }));
        oo.Children.Add(MetreField("Max distance (m)", c.ObjectToObjectMax,
            v => { c.ObjectToObjectMax = (float)v; _vm.OnConstraintsChanged(); }));
        oo.Children.Add(BoolRow("Across connected rooms", c.ObjectToObjectAcrossConnectedRooms,
            v => { c.ObjectToObjectAcrossConnectedRooms = v; _vm.OnConstraintsChanged(); },
            "Apply to pairs in rooms connected by a door or open passage. Closed walls don't count."));
        Body.Children.Add((Control)oo.Tag!);

        // ─── Door → Object ───
        var dop = ConstraintGroup("Door → Object", c.DoorToObjectEnabled,
            v => { c.DoorToObjectEnabled = v; _vm.OnConstraintsChanged(); });
        dop.Children.Add(MetreField("Min distance (m)", c.DoorToObjectMin,
            v => { c.DoorToObjectMin = (float)v; _vm.OnConstraintsChanged(); }));
        dop.Children.Add(MetreField("Max distance (m)", c.DoorToObjectMax,
            v => { c.DoorToObjectMax = (float)v; _vm.OnConstraintsChanged(); }));
        dop.Children.Add(BoolRow("Apply to every door", c.DoorAppliesToEveryDoor,
            v => { c.DoorAppliesToEveryDoor = v; _vm.OnConstraintsChanged(); },
            "On (default): every door of the room must satisfy the band. Off: any door is enough."));
        dop.Children.Add(BoolRow("Within angle band", c.DoorAngleBandEnabled,
            v => { c.DoorAngleBandEnabled = v; _vm.OnConstraintsChanged(); },
            "Constrain |angle| from the door's straight-into-room axis."));
        dop.Children.Add(NumericRow("Min |angle| (°)", c.DoorAngleMinDeg, 0, 180, 1,
            v => { c.DoorAngleMinDeg = (float)v; _vm.OnConstraintsChanged(); }));
        dop.Children.Add(NumericRow("Max |angle| (°)", c.DoorAngleMaxDeg, 0, 180, 1,
            v => { c.DoorAngleMaxDeg = (float)v; _vm.OnConstraintsChanged(); }));
        Body.Children.Add((Control)dop.Tag!);

        // ─── Object → Wall ───
        var ow = ConstraintGroup("Object → Wall", c.ObjectToWallEnabled,
            v => { c.ObjectToWallEnabled = v; _vm.OnConstraintsChanged(); });
        ow.Children.Add(MetreField("Min distance (m)", c.ObjectToWallMin,
            v => { c.ObjectToWallMin = (float)v; _vm.OnConstraintsChanged(); }));
        Body.Children.Add((Control)ow.Tag!);

        // ─── Per-room counts ───
        var pc = ConstraintGroup("Per-room counts", c.PerRoomCountsEnabled,
            v => { c.PerRoomCountsEnabled = v; _vm.OnConstraintsChanged(); });
        pc.Children.Add(NumericRow("Min per room", c.PerRoomCountMin, 0, 99, 1,
            v => { c.PerRoomCountMin = (int)v; _vm.OnConstraintsChanged(); }));
        pc.Children.Add(NumericRow("Max per room", c.PerRoomCountMax, 0, 99, 1,
            v => { c.PerRoomCountMax = (int)v; _vm.OnConstraintsChanged(); }));
        Body.Children.Add((Control)pc.Tag!);

        // Highlight violations toggle.
        Body.Children.Add(BoolRow("Highlight violations", c.HighlightViolations,
            v => { c.HighlightViolations = v; _vm.OnConstraintsChanged(); },
            "Draw a red dashed ring around objects that fail any active constraint."));

        // Live violation list — rendered as its own card with a red
        // header so it clearly separates from the constraint config
        // cards above it.
        var vList = _vm.ConstraintViolations;
        if (vList.Count > 0)
        {
            var violInner = new StackPanel { Spacing = 4 };
            violInner.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 0, 4),
                Children =
                {
                    new Ellipse {
                        Width = 8, Height = 8,
                        Fill = new SolidColorBrush(Color.FromRgb(0xC8, 0x28, 0x28)),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock {
                        Text = $"{vList.Count} violation{(vList.Count == 1 ? "" : "s")}",
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x28, 0x28)),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            });
            foreach (var v in vList)
            {
                violInner.Children.Add(new TextBlock
                {
                    Text = "• " + v.Message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)),
                });
            }
            Body.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xF6)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xC0, 0xC0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 0),
                Child = violInner,
            });
        }
    }

    StackPanel ConstraintGroup(string title, bool enabled, Action<bool> onToggle)
    {
        // Each constraint group is rendered as an elevated card matching
        // Border.section in MainWindow.axaml. The header checkbox toggles
        // the whole group; the body is dimmed (lower opacity) when off so
        // disabled fields read as intentionally inactive.
        var inner = new StackPanel { Spacing = 4 };
        var hdr = new CheckBox
        {
            Content = title,
            FontWeight = FontWeight.SemiBold,
            IsChecked = enabled,
            Margin = new Thickness(0, 0, 0, 4),
        };
        hdr.IsCheckedChanged += (_, _) =>
        {
            onToggle(hdr.IsChecked == true);
            Rebuild();
        };
        inner.Children.Add(hdr);
        // Wrap in a Border carrying section chrome — same Background/border/
        // CornerRadius/BoxShadow as Border.section in MainWindow.axaml.
        var card = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0xD7, 0xDF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 6),
            BoxShadow = BoxShadows.Parse("0 1 2 0 #14000000"),
            Opacity = enabled ? 1.0 : 0.65,
            Child = inner,
        };
        // Returning the inner StackPanel so callers can keep adding rows
        // to the body; the outer card is added to the panel via a one-off
        // tag attribute.
        inner.Tag = card;
        return inner;
    }

    Control MetreField(string label, double value, Action<double> onChanged)
        => NumericRow(label, value, 0, 99, 0.05, onChanged);

    Control NumericRow(string label, double value, double min, double max, double step,
        Action<double> onChanged)
    {
        // Classes="compact" + HorizontalAlignment=Stretch matches the
        // numeric inputs used in "Defaults for new objects" so the two
        // panels share the same chrome (28-px rounded box, slim 16-px
        // chevron column on the right).
        var box = new NumericUpDown
        {
            Classes = { "compact" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Value = (decimal)value,
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)step,
            FormatString = step >= 1 ? "0" : "0.00",
        };
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue.HasValue) onChanged((double)e.NewValue.Value);
        };
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
        stack.Children.Add(box);
        return stack;
    }

    Control EnumRow<T>(string label, (string Display, T Value)[] options, T current,
        Action<T> onChanged, string? tip = null) where T : struct, Enum
    {
        var combo = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        int selected = 0;
        for (int i = 0; i < options.Length; i++)
        {
            combo.Items.Add(options[i].Display);
            if (EqualityComparer<T>.Default.Equals(options[i].Value, current)) selected = i;
        }
        combo.SelectedIndex = selected;
        combo.SelectionChanged += (_, _) =>
        {
            int idx = combo.SelectedIndex;
            if (idx >= 0 && idx < options.Length) onChanged(options[idx].Value);
        };
        if (tip != null) ToolTip.SetTip(combo, tip);
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
        stack.Children.Add(combo);
        return stack;
    }

    Control BoolRow(string label, bool value, Action<bool> onChanged, string? tip = null)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Margin = new Thickness(0, 2, 0, 2),
            FontSize = 11,
        };
        if (tip != null) ToolTip.SetTip(cb, tip);
        cb.IsCheckedChanged += (_, _) =>
        {
            onChanged(cb.IsChecked == true);
            Rebuild();
        };
        return cb;
    }
}
