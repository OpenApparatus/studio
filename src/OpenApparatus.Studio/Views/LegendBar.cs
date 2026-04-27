using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Horizontal strip rendered below the grid editor that shows two legend
/// cards side-by-side:
///   1. Wall-symbol vocabulary (Wall / Door / Window / Open).
///   2. Object-measurement label styles (Distance / Angle) — example pills
///      that match what the grid editor actually draws so users can tell
///      the two value kinds apart.
/// Both cards are rounded callouts with a soft drop shadow, centred on
/// the bar.
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

        // Bar background — matches the studio panels so the cards pop.
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(232, 232, 236)), new Rect(size));

        var typeface = new Typeface("Inter");
        var labelBrush = new SolidColorBrush(Color.FromRgb(40, 40, 50));

        const double symbolW = 44;
        const double labelGap = 8;
        const double cellSpacing = 24;
        const double cardPadX = 20;
        const double cornerRadius = 8;
        const double cardH = 56;
        const double cardGap = 14;

        // ── Card 1: wall-symbol vocabulary ─────────────────────────
        var symbolEntries = new (string Label, Action<DrawingContext, Point, double> Draw)[]
        {
            ("Wall",   GridEditorView.DrawWallSymbol),
            ("Door",   GridEditorView.DrawDoorSymbol),
            ("Window", GridEditorView.DrawWindowSymbol),
            ("Open",   GridEditorView.DrawOpenSymbol),
        };
        var symFormats = new FormattedText[symbolEntries.Length];
        double row1 = 0;
        for (int i = 0; i < symbolEntries.Length; i++)
        {
            symFormats[i] = new FormattedText(symbolEntries[i].Label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 12, labelBrush);
            row1 += symbolW + labelGap + symFormats[i].Width;
            if (i < symbolEntries.Length - 1) row1 += cellSpacing;
        }
        double card1W = row1 + cardPadX * 2;

        // ── Card 2: measurement-label examples ─────────────────────
        // Caption + styled pill for each of Distance and Angle.
        var distCap  = new FormattedText("Distance",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 12, labelBrush);
        var angleCap = new FormattedText("Angle",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 12, labelBrush);
        // Pre-measure pill widths by formatting the same text the helpers do
        // and adding their internal horizontal padding.
        var distPillFmt  = new FormattedText("1.20 m",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 11, Brushes.Black);
        var anglePillFmt = new FormattedText("45°",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 11, Brushes.Black);
        double distPillW  = distPillFmt.Width  + 5 * 2;   // matches DrawDistanceLabel padX
        double anglePillW = anglePillFmt.Width + 7 * 2;   // matches DrawAngleLabel padX
        double row2 = distCap.Width + labelGap + distPillW
                    + cellSpacing
                    + angleCap.Width + labelGap + anglePillW;
        double card2W = row2 + cardPadX * 2;

        // ── Position both cards centred on the bar. ────────────────
        double totalW = card1W + cardGap + card2W;
        double startX = System.Math.Max(0, (size.Width - totalW) * 0.5);
        double cardY  = (size.Height - cardH) * 0.5;

        // Card chrome: drop shadow + filled rounded rectangle + thin border.
        void DrawCard(Rect r)
        {
            ctx.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(18, 0, 0, 0)),
                null,
                new Rect(r.X, r.Y + 2, r.Width, r.Height),
                cornerRadius, cornerRadius);
            ctx.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(252, 252, 253)),
                new Pen(new SolidColorBrush(Color.FromRgb(210, 210, 218)), 1),
                r, cornerRadius, cornerRadius);
        }

        // ── Card 1 paint. ──────────────────────────────────────────
        var c1 = new Rect(startX, cardY, card1W, cardH);
        DrawCard(c1);
        {
            // Baseline nudged down so the door's tall swing arc doesn't
            // visually push the row up.
            double y = c1.Y + cardH * 0.5 + 8;
            double x = c1.X + cardPadX;
            for (int i = 0; i < symbolEntries.Length; i++)
            {
                symbolEntries[i].Draw(ctx, new Point(x, y), symbolW);
                x += symbolW + labelGap;
                ctx.DrawText(symFormats[i],
                    new Point(x, y - symFormats[i].Height * 0.5));
                x += symFormats[i].Width
                   + (i < symbolEntries.Length - 1 ? cellSpacing : 0);
            }
        }

        // ── Card 2 paint. ──────────────────────────────────────────
        var c2 = new Rect(startX + card1W + cardGap, cardY, card2W, cardH);
        DrawCard(c2);
        {
            double yMid = c2.Y + cardH * 0.5;
            double x = c2.X + cardPadX;

            // Distance: caption + blue rectangular pill.
            ctx.DrawText(distCap, new Point(x, yMid - distCap.Height * 0.5));
            x += distCap.Width + labelGap;
            GridEditorView.DrawDistanceLabel(ctx, typeface, "1.20 m",
                new Point(x + distPillW * 0.5, yMid));
            x += distPillW + cellSpacing;

            // Angle: caption + amber rounded pill.
            ctx.DrawText(angleCap, new Point(x, yMid - angleCap.Height * 0.5));
            x += angleCap.Width + labelGap;
            GridEditorView.DrawAngleLabel(ctx, typeface, "45°",
                new Point(x + anglePillW * 0.5, yMid));
        }
    }
}
