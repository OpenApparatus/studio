using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenApparatus.Studio.Converters;

/// <summary>
/// Two-way converter that maps an enum value to a bool — true when the value
/// equals the converter parameter (matched by name, case-insensitive). Used to
/// drive a group of RadioButtons from a single enum-typed view-model property.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // RadioButton sets IsChecked=true when picked; we only want to round-trip
        // a true into the matching enum value. Avalonia ignores the false case.
        if (value is true && parameter is not null && targetType.IsEnum)
            return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
