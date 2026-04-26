using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Horizontal strip rendered below the grid editor that lists the wall-symbol
/// vocabulary (Wall / Door / Window / Open) on a rounded card with a soft drop
/// shadow so the legend reads as a callout panel rather than part of the
/// underlying chrome.
/// </summary>
public sealed class LegendBar : Control
{
    public LegendBar()
    {
        // Tall enough to fit the card + shadow + the door swing arc that
        // extends ~30 px above its hinge.
        Height = 78;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        // Bar background — matches the studio panels so the card pops.
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(232, 232, 236)), new Rect(size));

        var typeface = new Typeface("Inter");
        var labelBrush = new SolidColorBrush(Color.FromRgb(40, 40, 50));

        const double symbolW = 44;
        const double labelGap = 8;
        const double cellSpacing = 28;
        const double cardPadX = 22;
        const double cornerRadius = 8;

        var entries = new (string Label, Action<DrawingContext, Point, double> Draw)[]
        {
            ("Wall",   GridEditorView.DrawWallSymbol),
            ("Door",   GridEditorView.DrawDoorSymbol),
            ("Window", GridEditorView.DrawWindowSymbol),
            ("Open",   GridEditorView.DrawOpenSymbol),
        };

        // Pre-measure each label so we can centre the row + size the card.
        var formats = new FormattedText[entries.Length];
        double rowWidth = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            formats[i] = new FormattedText(entries[i].Label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 12, labelBrush);
            rowWidth += symbolW + labelGap + formats[i].Width;
            if (i < entries.Length - 1) rowWidth += cellSpacing;
        }

        // Card geometry. Height is fixed so the door swing arc has room.
        double cardW = rowWidth + cardPadX * 2;
        double cardH = 56;
        double cardX = System.Math.Max(0, (size.Width - cardW) * 0.5);
        double cardY = (size.Height - cardH) * 0.5;
        var cardRect = new Rect(cardX, cardY, cardW, cardH);

        // Drop shadow — three offset semi-transparent rectangles fake a soft
        // blur. DrawingContext can't render Avalonia's BoxShadow directly, so
        // approximate it with stacked tints.
        for (int s = 4; s >= 1; s--)
        {
            byte alpha = (byte)(28 - s * 4); // 24, 20, 16, 12
            var shadowRect = new Rect(cardX - s, cardY + s + 2, cardW + s * 2, cardH + s);
            ctx.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
                null, shadowRect, cornerRadius + s, cornerRadius + s);
        }

        // Card.
        ctx.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(252, 252, 253)),
            new Pen(new SolidColorBrush(Color.FromRgb(210, 210, 218)), 1),
            cardRect, cornerRadius, cornerRadius);

        // Legend content centred on the card; baseline nudged down a few px so
        // the door's tall swing arc doesn't push the row visually too high.
        double y = cardY + cardH * 0.5 + 8;
        double x = cardX + cardPadX;
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i].Draw(ctx, new Point(x, y), symbolW);
            x += symbolW + labelGap;
            ctx.DrawText(formats[i], new Point(x, y - formats[i].Height * 0.5));
            x += formats[i].Width + (i < entries.Length - 1 ? cellSpacing : 0);
        }
    }
}
