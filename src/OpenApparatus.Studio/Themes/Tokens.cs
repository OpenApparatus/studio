using Avalonia;
using Avalonia.Media;

namespace OpenApparatus.Studio.Themes;

/// <summary>
/// Strongly-typed C# accessor for the brush + color resources defined in
/// Themes/Tokens.axaml. Code-behind that draws chrome should pull from
/// here instead of constructing SolidColorBrushes from inline hex literals
/// — that keeps the AR HUD palette adjustable from a single XAML file.
///
/// Lookups resolve against the active theme variant at call time. They
/// don't live-update on theme switch (matching the legacy LookupBrush
/// pattern); panels that need to flip with the theme should be rebuilt.
///
/// Fallback hexes mirror the Light variant of the matching XAML brush —
/// they only fire if the App resources haven't been wired up yet (e.g.
/// during very early DataContext initialisation).
/// </summary>
internal static class Tokens
{
    static IBrush Brush(string key, Color fallback)
    {
        var app = Application.Current;
        if (app is not null
            && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v)
            && v is IBrush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    static Color ColorOf(string key, Color fallback)
    {
        var app = Application.Current;
        if (app is not null
            && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var v)
            && v is Color c)
            return c;
        return fallback;
    }

    public static IBrush SurfacePrimary    => Brush("SurfacePrimaryBrush",   Color.FromRgb(0xEC, 0xFD, 0xF5));
    public static IBrush SurfaceSecondary  => Brush("SurfaceSecondaryBrush", Color.FromRgb(0xD1, 0xFA, 0xE5));
    public static IBrush SurfaceRaised     => Brush("SurfaceRaisedBrush",    Colors.White);
    public static IBrush SurfaceHover      => Brush("SurfaceHoverBrush",     Color.FromRgb(0xD1, 0xFA, 0xE5));
    public static IBrush SurfacePressed    => Brush("SurfacePressedBrush",   Color.FromRgb(0xA7, 0xF3, 0xD0));
    public static IBrush SurfaceInk        => Brush("SurfaceInkBrush",       Color.FromRgb(0x02, 0x2C, 0x22));
    public static IBrush EditorBg          => Brush("EditorBgBrush",         Color.FromRgb(0x02, 0x2C, 0x22));
    public static IBrush StatusBg          => Brush("StatusBgBrush",         Color.FromRgb(0x06, 0x4E, 0x3B));
    public static IBrush BorderHairline    => Brush("BorderHairlineBrush",   Color.FromArgb(0x66, 0x34, 0xD3, 0x99));
    public static IBrush BorderStrong      => Brush("BorderStrongBrush",     Color.FromRgb(0x10, 0xB9, 0x81));
    public static IBrush Accent            => Brush("AccentBrush",           Color.FromRgb(0x05, 0x96, 0x69));
    public static IBrush AccentHover       => Brush("AccentHoverBrush",      Color.FromRgb(0x10, 0xB9, 0x81));
    public static IBrush AccentPress       => Brush("AccentPressBrush",      Color.FromRgb(0x04, 0x78, 0x57));
    public static IBrush AccentEmphasis    => Brush("AccentEmphasisBrush",   Color.FromRgb(0x10, 0xB9, 0x81));
    public static IBrush TextPrimary       => Brush("TextPrimaryBrush",      Color.FromRgb(0x02, 0x2C, 0x22));
    public static IBrush TextSecondary     => Brush("TextSecondaryBrush",    Color.FromArgb(0xCC, 0x06, 0x5F, 0x46));
    public static IBrush TextMuted         => Brush("TextMutedBrush",        Color.FromRgb(0x04, 0x78, 0x57));
    public static IBrush TextOnDark        => Brush("TextOnDarkBrush",       Colors.White);
    public static IBrush Valid             => Brush("ValidBrush",            Color.FromRgb(0x65, 0xA3, 0x0D));
    public static IBrush Invalid           => Brush("InvalidBrush",          Color.FromRgb(0xB9, 0x1C, 0x1C));

    /// <summary>Brand emerald as a raw Color, for callers that need to
    /// build alpha variants (e.g. selection overlays drawn over the 2D
    /// editor canvas at varying opacities).</summary>
    public static Color AccentColor        => ColorOf("AccentColor",         Color.FromRgb(0x10, 0xB9, 0x81));

    /// <summary>Brand emerald with a custom alpha, for selection rings,
    /// hover glows, and other scene overlays that need a translucent
    /// accent. One call site per overlay keeps the AR HUD accent the
    /// single source of truth.</summary>
    public static Color AccentArgb(byte alpha)
    {
        var c = AccentColor;
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }
}
