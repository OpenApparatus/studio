using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Horizontal strip rendered below the grid editor that lists the wall-symbol
/// vocabulary (Wall / Door / Window / Open) using the same drawing primitives
/// the editor uses inline. Sized to a fixed height so the grid never has to
/// share space with the legend.
/// </summary>
public sealed class LegendBar : Control
{
    public LegendBar()
    {
        // Tall enough that the door's swing arc (which extends ~0.7 × symbolW
        // above the hinge) doesn't get clipped at the top edge.
        Height = 64;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(245, 245, 248)), new Rect(size));
        ctx.DrawLine(
            new Pen(new SolidColorBrush(Color.FromRgb(200, 200, 205)), 1),
            new Point(0, 0), new Point(size.Width, 0));

        var typeface = new Typeface("Inter");
        var labelBrush = new SolidColorBrush(Color.FromRgb(40, 40, 50));

        const double symbolW = 44;
        const double labelGap = 8;
        const double cellSpacing = 28;

        var entries = new (string Label, Action<DrawingContext, Point, double> Draw)[]
        {
            ("Wall",   GridEditorView.DrawWallSymbol),
            ("Door",   GridEditorView.DrawDoorSymbol),
            ("Window", GridEditorView.DrawWindowSymbol),
            ("Open",   GridEditorView.DrawOpenSymbol),
        };

        double y = size.Height * 0.5;
        double x = 16;
        foreach (var (label, draw) in entries)
        {
            draw(ctx, new Point(x, y), symbolW);
            x += symbolW + labelGap;
            var fmt = new FormattedText(label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 12, labelBrush);
            ctx.DrawText(fmt, new Point(x, y - fmt.Height * 0.5));
            x += fmt.Width + cellSpacing;
        }
    }
}
