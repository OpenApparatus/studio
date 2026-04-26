using System;
using System.Globalization;
using System.Numerics;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OpenApparatus.Studio.Converters;

/// <summary>Converts an RGB Vector3 (0..1 components) into a SolidColorBrush.
/// One-way: useful for binding a colour-typed VM property to a Background or
/// Fill on a swatch control.</summary>
public sealed class Vector3ToBrushConverter : IValueConverter
{
    public static readonly Vector3ToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Vector3 v)
            return new SolidColorBrush(Color.FromRgb(
                (byte)Math.Clamp(Math.Round(v.X * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(v.Y * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(v.Z * 255), 0, 255)));
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
