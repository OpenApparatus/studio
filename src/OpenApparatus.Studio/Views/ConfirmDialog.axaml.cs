using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenApparatus.Studio.Views;

public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; }

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public void Configure(string title, string message)
    {
        Title = title;
        var msg = this.FindControl<TextBlock>("Message");
        if (msg != null) msg.Text = message;
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void OnYes(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    void OnNo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
