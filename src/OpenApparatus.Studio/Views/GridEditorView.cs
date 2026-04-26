using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Interactive grid editor: shows tiles as squares, supports click+drag to add
/// tiles to the current selection, distinguishes empty / selected / room-owned tiles
/// by color, and re-renders on demand.
///
/// Wired to the <see cref="MainWindowViewModel"/> via DataContext: reads
/// <see cref="MainWindowViewModel.RoomGrid"/> and <see cref="MainWindowViewModel.SelectedTiles"/>,
/// calls <see cref="MainWindowViewModel.SetTileSelected"/> on user input. Re-renders
/// whenever the VM bumps its <see cref="MainWindowViewModel.EditVersion"/>.
/// </summary>
public class GridEditorView : Control
{
    public static readonly StyledProperty<int> EditVersionProperty =
        AvaloniaProperty.Register<GridEditorView, int>(nameof(EditVersion));

    public int EditVersion
    {
        get => GetValue(EditVersionProperty);
        set => SetValue(EditVersionProperty, value);
    }

    static GridEditorView()
    {
        // Re-render whenever the VM bumps its EditVersion (set on every grid mutation).
        AffectsRender<GridEditorView>(EditVersionProperty);
    }

    public GridEditorView()
    {
        Focusable = true;
    }

    bool _dragging;

    MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    /// <summary>Width of the interior wall border in screen pixels.</summary>
    const double InteriorBorderThicknessPx = 7.0;
    /// <summary>Distance from the wall outline to the border's centerline. Set to
    /// half the thickness so the border's outer edge sits flush with the wall —
    /// the borders are no longer clickable, so the gap that used to keep them
    /// distinct from the wall hit zone is unnecessary.</summary>
    const double InteriorBorderOffsetPx = InteriorBorderThicknessPx * 0.5;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var vm = Vm;
        if (vm is null) return;
        var pos = e.GetPosition(this);

        var (origin, tilePxSize) = ComputeLayout(vm);
        if (tilePxSize <= 0) return;

        // 1. Wall hit — clicking a wall outline selects it for door / window edits.
        var worldPos = ScreenToWorld(pos, origin, tilePxSize, vm);
        float toleranceWorld = (float)(10.0 * vm.TileSize / tilePxSize);
        if (vm.TrySelectAdjacencyAtWorld(worldPos, toleranceWorld))
        {
            e.Handled = true;
            return;
        }

        // 2. Tile click — if the tile belongs to a room, select that room and
        // open the appearance panel; otherwise begin a drag-select for room
        // creation.
        if (TryHitTest(pos, vm, out int x, out int z))
        {
            if (vm.TrySelectRoomAtTile(x, z))
            {
                e.Handled = true;
                return;
            }
            bool wasSelected = vm.SelectedTiles.Contains((x, z));
            _dragging = true;
            _dragMode = wasSelected ? DragMode.Deselect : DragMode.Select;
            ApplyDrag(vm, x, z);
            e.Handled = true;
        }
    }

    static (Point A, Point B) ShrinkSegment(Point a, Point b, double pixels)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = System.Math.Sqrt(dx * dx + dy * dy);
        if (len <= pixels * 2) return (a, b);
        double t = pixels / len;
        return (
            new Point(a.X + dx * t, a.Y + dy * t),
            new Point(b.X - dx * t, b.Y - dy * t));
    }

    static System.Numerics.Vector2 ScreenToWorld(Point pos, Point origin, double tilePxSize, MainWindowViewModel vm)
    {
        double xTile = (pos.X - origin.X) / tilePxSize;
        double zTile = (origin.Y + vm.GridLength * tilePxSize - pos.Y) / tilePxSize;
        return new System.Numerics.Vector2((float)(xTile * vm.TileSize), (float)(zTile * vm.TileSize));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var vm = Vm;
        if (vm is null) return;
        var pos = e.GetPosition(this);
        if (TryHitTest(pos, vm, out int x, out int z))
            ApplyDrag(vm, x, z);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
    }

    void ApplyDrag(MainWindowViewModel vm, int x, int z)
    {
        bool select = _dragMode == DragMode.Select;
        vm.SetTileSelected(x, z, select);
    }

    enum DragMode { Select, Deselect }
    DragMode _dragMode;

    bool TryHitTest(Point pos, MainWindowViewModel vm, out int tileX, out int tileZ)
    {
        tileX = tileZ = -1;
        var (origin, tileSize) = ComputeLayout(vm);
        if (tileSize <= 0) return false;
        // Y in screen space grows downward; grid Z grows upward → flip.
        double localX = pos.X - origin.X;
        double localY = origin.Y + vm.GridLength * tileSize - pos.Y;
        if (localX < 0 || localY < 0) return false;
        int x = (int)(localX / tileSize);
        int z = (int)(localY / tileSize);
        if (x < 0 || x >= vm.GridWidth || z < 0 || z >= vm.GridLength) return false;
        tileX = x;
        tileZ = z;
        return true;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var vm = Vm;
        if (vm is null) return;
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(232, 232, 236)), new Rect(size));

        var (origin, tileSize) = ComputeLayout(vm);
        if (tileSize <= 0) return;

        var emptyFill = new SolidColorBrush(Color.FromRgb(248, 248, 250));
        var selectedFill = new SolidColorBrush(Color.FromRgb(255, 220, 130));
        var gridStroke = new Pen(new SolidColorBrush(Color.FromRgb(200, 200, 205)), 1.0);

        // Tiles.
        for (int x = 0; x < vm.GridWidth; x++)
            for (int z = 0; z < vm.GridLength; z++)
            {
                var rect = TileRect(origin, tileSize, x, z, vm.GridLength);
                int id = vm.RoomGrid[x, z];
                IBrush fill;
                if (id >= 0) fill = RoomColor(id);
                else if (vm.SelectedTiles.Contains((x, z))) fill = selectedFill;
                else fill = emptyFill;
                ctx.FillRectangle(fill, rect);
                ctx.DrawRectangle(null, gridStroke, rect);
            }

        // Wall segments from the materialized environment, drawn over the grid. World
        // coords are in meters; convert to tile units (÷ vm.TileSize) then to pixels.
        if (vm.CurrentEnvironment is { } env)
        {
            var closedPen = new Pen(Brushes.Black, 2.5);
            var doorPen = new Pen(new SolidColorBrush(Color.FromRgb(224, 96, 16)), 5.0)
            {
                LineCap = PenLineCap.Round,
            };
            // Windows: light blue, dashed-thin so they read as a different opening type
            // than doors (which are a solid orange line) when both share a wall.
            var windowPen = new Pen(new SolidColorBrush(Color.FromRgb(60, 140, 220)), 5.0)
            {
                LineCap = PenLineCap.Round,
                DashStyle = new DashStyle(new[] { 1.6, 1.0 }, 0),
            };
            var openPen = new Pen(new SolidColorBrush(Color.FromRgb(120, 180, 120)), 1.5)
            {
                DashStyle = DashStyle.Dash,
            };
            Point ToScreen(System.Numerics.Vector2 worldXz)
            {
                double xTile = worldXz.X / vm.TileSize;
                double zTile = worldXz.Y / vm.TileSize;
                return new Point(
                    origin.X + xTile * tileSize,
                    origin.Y + (vm.GridLength - zTile) * tileSize);
            }
            // Selected-wall highlight: drawn first so the wall pen renders on top of it.
            var selectedHighlightPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 215, 80)), 9.0)
            {
                LineCap = PenLineCap.Round,
            };

            foreach (var adj in env.Adjacencies)
            {
                var s = adj.SharedSegment;
                var p0 = ToScreen(s.Start);
                var p1 = ToScreen(s.End);

                if (ReferenceEquals(adj, vm.SelectedAdjacency))
                    ctx.DrawLine(selectedHighlightPen, p0, p1);

                switch (adj.Passage)
                {
                    case OpenApparatus.Topology.Passage.Closed _:
                        ctx.DrawLine(closedPen, p0, p1);
                        break;
                    case OpenApparatus.Topology.Passage.Doorway d:
                        {
                            // For each opening: gap in the wall + orange door indicator
                            // across the gap. Closed sections fill the spaces between/around.
                            var dir = s.Direction;
                            var ordered = d.Openings
                                .OrderBy(o => o.OffsetAlongEdge)
                                .ToList();
                            float prev = 0f;
                            foreach (var op in ordered)
                            {
                                if (op.OffsetAlongEdge > prev + 1e-4f)
                                    ctx.DrawLine(closedPen,
                                        ToScreen(s.Start + dir * prev),
                                        ToScreen(s.Start + dir * op.OffsetAlongEdge));
                                var doorStartW = s.Start + dir * op.OffsetAlongEdge;
                                var doorEndW = s.Start + dir * (op.OffsetAlongEdge + op.Width);
                                ctx.DrawLine(op.IsWindow ? windowPen : doorPen,
                                    ToScreen(doorStartW), ToScreen(doorEndW));
                                prev = op.OffsetAlongEdge + op.Width;
                            }
                            if (s.Length > prev + 1e-4f)
                                ctx.DrawLine(closedPen,
                                    ToScreen(s.Start + dir * prev),
                                    ToScreen(s.Start + dir * s.Length));
                        }
                        break;
                    case OpenApparatus.Topology.Passage.Open _:
                        ctx.DrawLine(openPen, p0, p1);
                        break;
                }
            }

            // Interior wall borders — one short line on each room's side of every
            // wall, offset inward so it sits clearly inside the room and doesn't
            // overlap the wall outline. Coloured with any per-(room, wall) override
            // the user has set, otherwise a subtle neutral so the border still
            // reads as clickable.
            // Square caps so wall ends look architectural rather than rounded;
            // inset both ends by the offset distance so two perpendicular
            // borders don't overlap at the inside corner of a room.
            foreach (var adj in env.Adjacencies)
            {
                var seg = adj.SharedSegment;
                var p0 = ToScreen(seg.Start);
                var p1 = ToScreen(seg.End);
                var nrmW = seg.Normal;
                var nrmScreen = new Point(
                    nrmW.X / vm.TileSize * tileSize,
                    -nrmW.Y / vm.TileSize * tileSize);
                double nrmLen = Math.Sqrt(nrmScreen.X * nrmScreen.X + nrmScreen.Y * nrmScreen.Y);
                if (nrmLen < 1e-3) continue;
                var nrmUnit = new Point(nrmScreen.X / nrmLen, nrmScreen.Y / nrmLen);

                DrawSide(adj, isRoomA: true, p0, p1, nrmUnit, +1);
                if (adj.IsInternal) DrawSide(adj, isRoomA: false, p0, p1, nrmUnit, -1);

                void DrawSide(OpenApparatus.Topology.Adjacency a, bool isRoomA,
                    Point pa, Point pb, Point nrm, int sign)
                {
                    int roomId = isRoomA ? a.RoomA.Id : a.RoomB!.Id;
                    var off = new Point(nrm.X * sign * InteriorBorderOffsetPx, nrm.Y * sign * InteriorBorderOffsetPx);
                    var ax = new Point(pa.X + off.X, pa.Y + off.Y);
                    var bx = new Point(pb.X + off.X, pb.Y + off.Y);
                    // Inset by the offset distance so a perpendicular neighbour's
                    // border ends right at the same point — no crossing.
                    var inset = ShrinkSegment(ax, bx, InteriorBorderOffsetPx);

                    var rgb = vm.EffectiveWallColor(roomId, a);
                    var pen = new Pen(new SolidColorBrush(Color.FromRgb(
                        (byte)(rgb.X * 255), (byte)(rgb.Y * 255), (byte)(rgb.Z * 255))),
                        InteriorBorderThicknessPx)
                    {
                        LineCap = PenLineCap.Square,
                    };
                    ctx.DrawLine(pen, inset.A, inset.B);
                }
            }

            // Selected wall: draw markers at every door-anchor candidate, with the
            // currently-active anchor (where pressing D will place the door) drawn
            // larger and in orange. Clicking another anchor moves the selection.
            if (vm.SelectedAdjacency is { } sel)
            {
                var seg = sel.SharedSegment;
                var dirN = seg.Direction;
                var anchorBrush = new SolidColorBrush(Color.FromArgb(220, 30, 30, 30));
                var anchorBorder = new Pen(Brushes.White, 1.5);
                var activeBrush = new SolidColorBrush(Color.FromRgb(224, 96, 16));
                var activePen = new Pen(Brushes.White, 2.0);
                const float epsilon = 1e-3f;

                foreach (var alongMeters in MainWindowViewModel.DoorAnchorsAlongWall(seg, vm.TileSize))
                {
                    var worldPos = seg.Start + dirN * alongMeters;
                    var screenPos = ToScreen(worldPos);
                    bool isActive = MathF.Abs(alongMeters - vm.SelectedClickAlong) < epsilon;
                    if (isActive)
                        ctx.DrawEllipse(activeBrush, activePen, screenPos, 7, 7);
                    else
                        ctx.DrawEllipse(anchorBrush, anchorBorder, screenPos, 4, 4);
                }
            }
        }

        // Room id labels.
        var labelBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        var typeface = new Typeface("Inter");
        for (int rId = 0; rId < 1000; rId++)
        {
            // Only labels rooms that exist in the grid.
            bool found = false;
            int xSum = 0, zSum = 0, cnt = 0;
            for (int x = 0; x < vm.GridWidth; x++)
                for (int z = 0; z < vm.GridLength; z++)
                    if (vm.RoomGrid[x, z] == rId)
                    {
                        xSum += x;
                        zSum += z;
                        cnt++;
                        found = true;
                    }
            if (!found) continue;
            double cx = origin.X + (xSum / (double)cnt + 0.5) * tileSize;
            double cy = origin.Y + (vm.GridLength - (zSum / (double)cnt + 0.5)) * tileSize;
            var fmt = new FormattedText(rId.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 13, labelBrush);
            ctx.DrawText(fmt, new Point(cx - fmt.Width * 0.5, cy - fmt.Height * 0.5));
        }
    }

    static Rect TileRect(Point origin, double tileSize, int x, int z, int gridLength)
    {
        double left = origin.X + x * tileSize;
        double top = origin.Y + (gridLength - z - 1) * tileSize;
        return new Rect(left, top, tileSize, tileSize);
    }

    (Point origin, double tileSize) ComputeLayout(MainWindowViewModel vm)
    {
        var size = Bounds.Size;
        const double padding = 16.0;
        double availW = size.Width - padding * 2;
        double availH = size.Height - padding * 2;
        if (availW <= 0 || availH <= 0 || vm.GridWidth <= 0 || vm.GridLength <= 0)
            return (new Point(padding, padding), 0);

        double sx = availW / vm.GridWidth;
        double sy = availH / vm.GridLength;
        double tile = System.Math.Min(sx, sy);
        double offsetX = padding + (availW - tile * vm.GridWidth) * 0.5;
        double offsetY = padding + (availH - tile * vm.GridLength) * 0.5;
        return (new Point(offsetX, offsetY), tile);
    }

    static IBrush RoomColor(int roomId)
    {
        // HSV cycling — distinct, predictable colors per room id.
        double h = (roomId * 47) % 360;
        return HsvToBrush(h, 0.45, 0.92);
    }

    static IBrush HsvToBrush(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - System.Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return new SolidColorBrush(Color.FromRgb(
            (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255)));
    }
}
