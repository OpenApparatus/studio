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
            MinWidth = 100,
        };
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
}
