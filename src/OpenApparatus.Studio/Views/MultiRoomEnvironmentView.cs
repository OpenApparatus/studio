using System;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenApparatus;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// A 2D top-down renderer for a <see cref="MultiRoomEnvironment"/>. Rooms are drawn as
/// filled polygons; walls (closed boundaries) as solid lines; doorways as
/// gaps in the wall lines; open boundaries are not drawn.
///
/// The view auto-fits the floor plan to the available size with padding,
/// preserving aspect ratio. Y in world space maps to UP on screen (despite
/// Avalonia's screen-space Y growing downward, we flip it for cartographic
/// readability).
/// </summary>
public class MultiRoomEnvironmentView : Control
{
    public static readonly StyledProperty<MultiRoomEnvironment?> PlanProperty =
        AvaloniaProperty.Register<MultiRoomEnvironmentView, MultiRoomEnvironment?>(nameof(Plan));

    public static readonly StyledProperty<float> WallThicknessProperty =
        AvaloniaProperty.Register<MultiRoomEnvironmentView, float>(nameof(WallThickness), defaultValue: 0.2f);

    public MultiRoomEnvironment? Plan
    {
        get => GetValue(PlanProperty);
        set => SetValue(PlanProperty, value);
    }

    public float WallThickness
    {
        get => GetValue(WallThicknessProperty);
        set => SetValue(WallThicknessProperty, value);
    }

    static MultiRoomEnvironmentView()
    {
        AffectsRender<MultiRoomEnvironmentView>(PlanProperty, WallThicknessProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var size = Bounds.Size;
        if (Plan is null || size.Width <= 0 || size.Height <= 0) return;

        // Background.
        ctx.FillRectangle(Brushes.Gainsboro, new Rect(size));

        var bounds = Plan.GetWorldBounds();
        const double padding = 24.0;
        double availW = size.Width - padding * 2;
        double availH = size.Height - padding * 2;
        if (availW <= 0 || availH <= 0) return;

        double sx = availW / bounds.Width;
        double sy = availH / bounds.Depth;
        double scale = System.Math.Min(sx, sy);

        // Center within the available area, with Y flipped (world +Z = screen up).
        double offsetX = padding + (availW - bounds.Width * scale) * 0.5;
        double offsetY = padding + (availH - bounds.Depth * scale) * 0.5;

        Point WorldToScreen(Vector2 p) => new(
            offsetX + (p.X - bounds.Min.X) * scale,
            size.Height - (offsetY + (p.Y - bounds.Min.Y) * scale));

        // Draw each room as a filled polygon.
        var cellFill = new SolidColorBrush(Color.FromRgb(245, 245, 248));
        var rectangleFill = new SolidColorBrush(Color.FromRgb(225, 232, 245));
        foreach (var room in Plan.Rooms)
        {
            var fill = room.RoomType == RoomType.Rectangle ? rectangleFill : cellFill;
            DrawCellInterior(ctx, room, WorldToScreen, fill);
        }

        // Draw walls / doors per adjacency. Doors use a vivid orange that
        // contrasts both with the room fills (gray / pale blue) and the black
        // wall lines, plus a thicker stroke so they read as openings clearly.
        var wallPen = new Pen(Brushes.Black, 2.0);
        var doorPen = new Pen(new SolidColorBrush(Color.FromRgb(224, 96, 16)), 5.0)
        {
            LineCap = PenLineCap.Round,
        };
        foreach (var adj in Plan.Adjacencies)
        {
            switch (adj.Passage)
            {
                case Passage.Closed _:
                    DrawSegment(ctx, adj.SharedSegment, WorldToScreen, wallPen);
                    break;
                case Passage.Doorway d:
                    DrawWallWithDoor(ctx, adj.SharedSegment, d, WorldToScreen, wallPen, doorPen);
                    break;
                case Passage.Open _:
                    // No drawing — rooms visually merge along this segment.
                    break;
            }
        }

        // Room id labels.
        var labelBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80));
        var typeface = new Typeface("Inter");
        foreach (var room in Plan.Rooms)
        {
            var c = WorldToScreen(room.GetWorldBounds().Center);
            var text = new FormattedText(room.Id.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 12, labelBrush);
            ctx.DrawText(text, new Point(c.X - text.Width * 0.5, c.Y - text.Height * 0.5));
        }
    }

    static void DrawCellInterior(DrawingContext ctx, Room room, Func<Vector2, Point> toScreen, IBrush fill)
    {
        var outline = room.GetWorldOutline();
        if (outline.Count == 0) return;
        var geo = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(toScreen(outline[0].Start), isFilled: true);
            for (int i = 0; i < outline.Count; i++)
                sgc.LineTo(toScreen(outline[i].End));
            sgc.EndFigure(isClosed: true);
        }
        ctx.DrawGeometry(fill, null, geo);
    }

    static void DrawSegment(DrawingContext ctx, EdgeSegment seg, Func<Vector2, Point> toScreen, IPen pen) =>
        ctx.DrawLine(pen, toScreen(seg.Start), toScreen(seg.End));

    static void DrawWallWithDoor(DrawingContext ctx, EdgeSegment seg, Passage.Doorway door,
        Func<Vector2, Point> toScreen, IPen wallPen, IPen doorPen)
    {
        var d = seg.Direction;
        var doorStart = seg.Start + d * door.OffsetAlongEdge;
        var doorEnd = seg.Start + d * (door.OffsetAlongEdge + door.Width);
        // Wall segment: start → doorStart, doorEnd → end.
        ctx.DrawLine(wallPen, toScreen(seg.Start), toScreen(doorStart));
        ctx.DrawLine(wallPen, toScreen(doorEnd), toScreen(seg.End));
        // Door indicator: thicker green line across the opening.
        ctx.DrawLine(doorPen, toScreen(doorStart), toScreen(doorEnd));
    }
}
