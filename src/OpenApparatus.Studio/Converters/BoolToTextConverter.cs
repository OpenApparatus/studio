using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenApparatus.Studio.Converters;

/// <summary>
/// Converts a bool to one of two strings, encoded in the converter parameter
/// as <c>"trueText|falseText"</c>. Useful for binding a button caption to a
/// toggle (e.g. play / pause).
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public static readonly BoolToTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string s) return string.Empty;
        var parts = s.Split('|', 2);
        if (parts.Length != 2) return s;
        return value is true ? parts[0] : parts[1];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
