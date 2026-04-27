using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace OpenApparatus.Studio.Behaviors;

/// <summary>
/// Attached behavior that turns a label (TextBlock) into a horizontal
/// "drag-to-scrub" affordance for a sibling NumericUpDown — the standard
/// CAD / DCC interaction. Hover the label, the cursor turns into a
/// horizontal-resize arrow; drag left/right to scrub the bound value.
///
/// Usage:
///     <TextBlock behaviors:DragScrub.Target="{Binding ElementName=widthInput}"
///                behaviors:DragScrub.Step="0.05"
///                Text="Width"/>
///     <NumericUpDown x:Name="widthInput" .../>
///
/// Step and an optional Multiplier (held Shift = ×0.1, Ctrl = ×10) follow
/// the convention used by Blender / Figma. The behavior reads Min/Max from
/// the target NumericUpDown so callers don't need to duplicate the range.
/// </summary>
public static class DragScrub
{
    public static readonly AttachedProperty<NumericUpDown?> TargetProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, NumericUpDown?>(
            "Target", typeof(DragScrub));

    public static readonly AttachedProperty<double> StepProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, double>(
            "Step", typeof(DragScrub), defaultValue: 0.05);

    public static void SetTarget(TextBlock t, NumericUpDown? v) => t.SetValue(TargetProperty, v);
    public static NumericUpDown? GetTarget(TextBlock t) => t.GetValue(TargetProperty);
    public static void SetStep(TextBlock t, double v) => t.SetValue(StepProperty, v);
    public static double GetStep(TextBlock t) => t.GetValue(StepProperty);

    static DragScrub()
    {
        TargetProperty.Changed.AddClassHandler<TextBlock>(OnTargetChanged);
    }

    static void OnTargetChanged(TextBlock label, AvaloniaPropertyChangedEventArgs e)
    {
        // Detach old; attach to new. Cursor + handlers go together.
        label.PointerPressed  -= OnPressed;
        label.PointerMoved    -= OnMoved;
        label.PointerReleased -= OnReleased;
        label.PointerEntered  -= OnEntered;
        label.PointerExited   -= OnExited;
        if (e.NewValue is NumericUpDown)
        {
            label.PointerPressed  += OnPressed;
            label.PointerMoved    += OnMoved;
            label.PointerReleased += OnReleased;
            label.PointerEntered  += OnEntered;
            label.PointerExited   += OnExited;
        }
    }

    // Per-label state. Static dictionary keyed by the TextBlock so we don't
    // need a wrapper control.
    sealed class State
    {
        public Point StartPos;
        public decimal StartValue;
        public bool Active;
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, State> _state = new();
    static State GetState(TextBlock t)
    {
        if (!_state.TryGetValue(t, out var s)) { s = new State(); _state.Add(t, s); }
        return s;
    }

    static void OnEntered(object? sender, PointerEventArgs e)
    {
        if (sender is TextBlock t) t.Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    static void OnExited(object? sender, PointerEventArgs e)
    {
        if (sender is TextBlock t) t.Cursor = Cursor.Default;
    }

    static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock label) return;
        var target = GetTarget(label);
        if (target is null) return;
        if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;

        var s = GetState(label);
        s.StartPos = e.GetPosition(label);
        s.StartValue = target.Value ?? 0m;
        s.Active = true;
        e.Pointer.Capture(label);
        e.Handled = true;
    }

    static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not TextBlock label) return;
        var s = GetState(label);
        if (!s.Active) return;
        var target = GetTarget(label);
        if (target is null) return;

        double dx = e.GetPosition(label).X - s.StartPos.X;
        double step = GetStep(label);
        // Held modifiers: Shift = fine (×0.1), Ctrl = coarse (×10).
        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Shift)) step *= 0.1;
        else if (mods.HasFlag(KeyModifiers.Control)) step *= 10.0;

        decimal delta = (decimal)(dx * step);
        decimal candidate = s.StartValue + delta;

        // Clamp to the target's own Min/Max if those are set.
        decimal min = target.Minimum;
        decimal max = target.Maximum;
        if (max > min) candidate = System.Math.Clamp(candidate, min, max);

        target.Value = candidate;
    }

    static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not TextBlock label) return;
        var s = GetState(label);
        if (s.Active)
        {
            s.Active = false;
            e.Pointer.Capture(null);
        }
    }
}
