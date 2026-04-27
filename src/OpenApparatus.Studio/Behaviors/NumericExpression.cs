using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace OpenApparatus.Studio.Behaviors;

/// <summary>
/// Attached behaviour that gives a <see cref="NumericUpDown"/> support
/// for arithmetic expressions: typing <c>1.2+0.05</c> or
/// <c>(2+3)/4</c> and pressing Enter evaluates and applies the result
/// instead of rejecting the input as non-numeric.
///
/// Implementation: subscribe to the inner TextBox's KeyDown for Enter
/// and to its LostFocus. Both events run the <see cref="ExpressionEvaluator"/>
/// against the current text; on a successful evaluation we set
/// <see cref="NumericUpDown.Value"/> directly, which takes care of
/// clamping + raising the ValueChanged event.
/// </summary>
public static class NumericExpression
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<NumericUpDown, bool>(
            "Enabled", typeof(NumericExpression));

    public static void SetEnabled(NumericUpDown n, bool v) => n.SetValue(EnabledProperty, v);
    public static bool GetEnabled(NumericUpDown n) => n.GetValue(EnabledProperty);

    static NumericExpression()
    {
        EnabledProperty.Changed.AddClassHandler<NumericUpDown>(OnEnabledChanged);
    }

    static void OnEnabledChanged(NumericUpDown n, AvaloniaPropertyChangedEventArgs e)
    {
        n.AttachedToLogicalTree -= OnAttached;
        n.AttachedToLogicalTree += OnAttached;
    }

    static void OnAttached(object? sender, LogicalTreeAttachmentEventArgs e)
    {
        if (sender is not NumericUpDown n) return;
        if (!GetEnabled(n)) return;
        // Walk down to the inner TextBox once the template applies.
        n.TemplateApplied += (_, _) => Wire(n);
        Wire(n);
    }

    static void Wire(NumericUpDown n)
    {
        var tb = n.FindDescendantOfType<TextBox>();
        if (tb is null) return;
        tb.RemoveHandler(InputElement.KeyDownEvent, OnTextBoxKey);
        tb.AddHandler(InputElement.KeyDownEvent, OnTextBoxKey, handledEventsToo: false);
        tb.LostFocus -= OnTextBoxLostFocus;
        tb.LostFocus += OnTextBoxLostFocus;
    }

    static void OnTextBoxKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        ApplyExpression(tb);
    }

    static void OnTextBoxLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox tb) ApplyExpression(tb);
    }

    static void ApplyExpression(TextBox tb)
    {
        // Walk up to the owning NumericUpDown.
        Avalonia.Controls.NumericUpDown? owner = null;
        var p = tb.Parent;
        while (p is not null && owner is null)
        {
            if (p is NumericUpDown nud) owner = nud;
            p = (p as Control)?.Parent;
        }
        if (owner is null) return;
        var text = tb.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;
        // Skip plain numbers — NumericUpDown handles those itself.
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out _)) return;
        var v = ExpressionEvaluator.TryEval(text);
        if (v is null) return;
        // Round to a sensible precision matching the input's format.
        owner.Value = (decimal)v.Value;
    }
}

internal static class TreeExt
{
    public static T? FindDescendantOfType<T>(this Avalonia.Controls.Control root) where T : Control
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)
            .OfType<T>().FirstOrDefault();
}
