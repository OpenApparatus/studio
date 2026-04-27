using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenApparatus.Studio.Views;

public partial class AboutDialog : Window
{
    /// <summary>Static text shown in the About dialog. Pulled from the
    /// running assembly version so it stays in sync with the build.</summary>
    public string Version
    {
        get
        {
            var asm = typeof(AboutDialog).Assembly;
            var v = asm.GetName().Version;
            return v is null ? "Version unknown"
                : $"Version {v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public AboutDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
