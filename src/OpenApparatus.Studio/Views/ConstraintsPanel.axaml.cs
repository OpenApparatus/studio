using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
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
            Text = "Validation only — placements are never blocked. Violators get a red dashed ring; door zones (green) and exclusion radius (red) show the compliant region in the editor.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 130)),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 8),
        });

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
        Body.Children.Add(oo);

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
        Body.Children.Add(dop);

        // ─── Object → Wall ───
        var ow = ConstraintGroup("Object → Wall", c.ObjectToWallEnabled,
            v => { c.ObjectToWallEnabled = v; _vm.OnConstraintsChanged(); });
        ow.Children.Add(MetreField("Min distance (m)", c.ObjectToWallMin,
            v => { c.ObjectToWallMin = (float)v; _vm.OnConstraintsChanged(); }));
        Body.Children.Add(ow);

        // ─── Per-room counts ───
        var pc = ConstraintGroup("Per-room counts", c.PerRoomCountsEnabled,
            v => { c.PerRoomCountsEnabled = v; _vm.OnConstraintsChanged(); });
        pc.Children.Add(NumericRow("Min per room", c.PerRoomCountMin, 0, 99, 1,
            v => { c.PerRoomCountMin = (int)v; _vm.OnConstraintsChanged(); }));
        pc.Children.Add(NumericRow("Max per room", c.PerRoomCountMax, 0, 99, 1,
            v => { c.PerRoomCountMax = (int)v; _vm.OnConstraintsChanged(); }));
        Body.Children.Add(pc);

        // Highlight violations toggle.
        Body.Children.Add(BoolRow("Highlight violations", c.HighlightViolations,
            v => { c.HighlightViolations = v; _vm.OnConstraintsChanged(); },
            "Draw a red dashed ring around objects that fail any active constraint."));

        // Live violation list.
        var vList = _vm.ConstraintViolations;
        if (vList.Count > 0)
        {
            Body.Children.Add(new TextBlock
            {
                Text = "Violations:",
                FontWeight = FontWeight.SemiBold,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 2),
            });
            foreach (var v in vList)
            {
                Body.Children.Add(new TextBlock
                {
                    Text = "• " + v.Message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 60, 60)),
                });
            }
        }
    }

    StackPanel ConstraintGroup(string title, bool enabled, Action<bool> onToggle)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
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
        outer.Children.Add(hdr);
        return outer;
    }

    Control MetreField(string label, double value, Action<double> onChanged)
        => NumericRow(label, value, 0, 99, 0.05, onChanged);

    Control NumericRow(string label, double value, double min, double max, double step,
        Action<double> onChanged)
    {
        var box = new NumericUpDown
        {
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
