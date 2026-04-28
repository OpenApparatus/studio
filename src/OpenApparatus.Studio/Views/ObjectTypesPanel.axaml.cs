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
/// Left-panel host for the editable object-type list. Each row is a
/// shape/colour swatch button + name TextBox + delete button. An "Add
/// object type" button below the list grows the collection. Hidden when
/// EditMode is not Object.
/// </summary>
public partial class ObjectTypesPanel : UserControl
{
    MainWindowViewModel? _vm;

    public ObjectTypesPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    StackPanel Body => this.FindControl<StackPanel>("ObjectTypesBody")!;

    void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        Rebuild();
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.EditMode)
                          or nameof(MainWindowViewModel.IsObjectsMode)
                          or nameof(MainWindowViewModel.EditVersion))
        {
            Rebuild();
        }
    }

    void Rebuild()
    {
        Body.Children.Clear();
        if (_vm is null) return;
        IsVisible = _vm.IsObjectsMode;
        if (!_vm.IsObjectsMode) return;

        Body.Children.Add(new TextBlock
        {
            Text = "Object types",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        Body.Children.Add(new TextBlock
        {
            Text = "Click a swatch to edit its shape and colour. Press 1–" +
                System.Math.Min(_vm.ObjectTypes.Count, 9) +
                " in the editor with a sub-cell selected to place that type.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 130)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        });

        for (int i = 0; i < _vm.ObjectTypes.Count; i++)
            Body.Children.Add(TypeRow(i, _vm.ObjectTypes[i]));

        var addBtn = new Button
        {
            Content = "+ Add object type",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        addBtn.Click += (_, _) => _vm.AddObjectTypeCommand.Execute(null);
        Body.Children.Add(addBtn);
    }

    Control TypeRow(int index, ObjectType type)
    {
        // Swatch button now shows both the colour fill AND a silhouette
        // glyph hinting at the type's 3D shape. Glyph contrast picks
        // black or white depending on swatch luminance so it stays
        // readable.
        float lum = type.Color.X * 0.299f + type.Color.Y * 0.587f + type.Color.Z * 0.114f;
        var glyphBrush = new SolidColorBrush(lum > 0.6f ? Color.FromRgb(20, 20, 26) : Colors.White);
        var swatchBtn = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(
                (byte)(type.Color.X * 255), (byte)(type.Color.Y * 255), (byte)(type.Color.Z * 255))),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 70)),
            BorderThickness = new Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = ShapeGlyph(type.Shape),
                FontSize = 16,
                Foreground = glyphBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(swatchBtn, $"{type.Shape} — click to edit shape and colour.");
        swatchBtn.Click += async (_, _) => await OnEditType(index);

        // Slot indicator styled as a keyboard chip — communicates that
        // this row is bound to a hotkey.
        var slotChip = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0xD7, 0xDF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(3),
            Padding = new Thickness(5, 1),
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
            Child = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontSize = 10,
                FontFamily = new FontFamily("Consolas, 'Courier New', monospace"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E)),
            },
        };
        ToolTip.SetTip(slotChip, $"Press {index + 1} on the keyboard to place this object type.");

        var nameBox = new TextBox
        {
            Text = type.Name,
            Watermark = $"Object {index + 1}",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 100,
        };
        // Commit on LostFocus / Enter only. Committing on every TextChanged
        // bumps EditVersion, which rebuilds the panel and steals focus from
        // the TextBox after every keystroke.
        string lastCommitted = type.Name;
        void CommitName()
        {
            var t = (nameBox.Text ?? string.Empty).Trim();
            if (t == lastCommitted) return;
            lastCommitted = t;
            type.Name = t.Length > 0 ? t : $"Object {index + 1}";
            _vm!.OnEditedObjectType();
        }
        nameBox.LostFocus += (_, _) => CommitName();
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                CommitName();
                e.Handled = true;
            }
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
        stack.Children.Add(slotChip);
        stack.Children.Add(nameBox);
        stack.Children.Add(delBtn);
        return stack;
    }

    /// <summary>Maps the 7 ObjectShape primitives to a Unicode glyph that
    /// approximates their silhouette. Used as a hint inside the swatch
    /// button so the type list shows shape, not just colour.</summary>
    static string ShapeGlyph(ObjectShape shape) => shape switch
    {
        ObjectShape.Cube          => "■",
        ObjectShape.Sphere        => "●",
        ObjectShape.Cylinder      => "▮",
        ObjectShape.SquatCylinder => "▬",
        ObjectShape.Cone          => "▲",
        ObjectShape.Capsule       => "⬭",
        ObjectShape.Pyramid       => "◆",
        _ => "?",
    };

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
}
