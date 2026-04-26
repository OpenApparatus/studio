using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenApparatus.Studio.Converters;

/// <summary>OR-fold converter: returns true if any provided value is true.</summary>
public sealed class AnyTrueConverter : IMultiValueConverter
{
    public static readonly AnyTrueConverter Instance = new();

    public object? Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        foreach (var v in values)
            if (v is true) return true;
        return false;
    }
}
