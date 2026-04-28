using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using OpenApparatus.Studio.Themes;

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
        PaintHero();
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Tints the three concentric hero rectangles from the
    /// AccentColor token at fixed alpha steps. Defined here rather than
    /// inline in XAML so the AR HUD accent stays adjustable from
    /// Tokens.axaml alone.</summary>
    void PaintHero()
    {
        var c = Tokens.AccentColor;
        var outer = this.FindControl<Rectangle>("HeroOuter");
        var mid   = this.FindControl<Rectangle>("HeroMid");
        var inner = this.FindControl<Rectangle>("HeroInner");
        if (outer is not null)
        {
            outer.Stroke = new SolidColorBrush(Color.FromArgb(0x8C, c.R, c.G, c.B));
            outer.Fill   = new SolidColorBrush(Color.FromArgb(0x14, c.R, c.G, c.B));
        }
        if (mid is not null)
            mid.Stroke = new SolidColorBrush(Color.FromArgb(0xA8, c.R, c.G, c.B));
        if (inner is not null)
            inner.Stroke = new SolidColorBrush(Color.FromArgb(0xC8, c.R, c.G, c.B));
    }

    void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
