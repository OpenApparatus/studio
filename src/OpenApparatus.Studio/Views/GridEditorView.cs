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
        // Clip drawing to the control's bounds so a zoomed grid never spills
        // over the surrounding chrome (toolbar, side panel, legend bar).
        ClipToBounds = true;
    }

    bool _dragging;
    bool _panning;
    Point _panStartPos;
    double _panStartOffsetX;
    double _panStartOffsetY;

    MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    /// <summary>Width of the interior wall border in screen pixels.</summary>
    const double InteriorBorderThicknessPx = 14.0;
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
        var props = e.GetCurrentPoint(this).Properties;

        // Middle / right mouse button drag = pan.
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
        {
            _panning = true;
            _panStartPos = pos;
            _panStartOffsetX = vm.PanOffsetX;
            _panStartOffsetY = vm.PanOffsetY;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var (origin, tilePxSize) = ComputeLayout(vm);
        if (tilePxSize <= 0) return;

        // Double-click on a room tile → focus that room.
        if (e.ClickCount == 2 && TryHitTest(pos, vm, out int dcX, out int dcZ))
        {
            int dcId = vm.RoomGrid[dcX, dcZ];
            if (dcId >= 0)
            {
                var (availW, availH) = GetAvailableArea();
                vm.FocusOnRoom(dcId, availW, availH);
                if (!vm.IsObjectsMode) vm.TrySelectRoomAtTile(dcX, dcZ);
                e.Handled = true;
                return;
            }
        }

        if (vm.IsObjectsMode)
        {
            HandleObjectsModeClick(vm, pos, origin, tilePxSize);
            e.Handled = true;
            return;
        }

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
            // A click on the grid means we've left the wall context — clear any
            // wall selection so the door-anchor markers disappear.
            vm.SelectedAdjacency = null;

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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var vm = Vm;
        if (vm is null) return;

        // Zoom centered on the cursor: figure out which world point sits under
        // the cursor before changing zoom, then adjust pan so that point still
        // sits under the cursor afterwards.
        var pos = e.GetPosition(this);
        var (origin, tilePxSize) = ComputeLayout(vm);
        if (tilePxSize <= 0) return;
        var worldBefore = ScreenToWorld(pos, origin, tilePxSize, vm);

        double step = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        double newZoom = System.Math.Clamp(
            vm.ZoomFactor * step,
            MainWindowViewModel.MinZoom, MainWindowViewModel.MaxZoom);
        if (System.Math.Abs(newZoom - vm.ZoomFactor) < 1e-4) { e.Handled = true; return; }
        vm.ZoomFactor = newZoom;

        var (origin2, tile2) = ComputeLayout(vm);
        if (tile2 <= 0) { e.Handled = true; return; }
        // Where would worldBefore land on screen now?
        double xTileNew = worldBefore.X / vm.TileSize;
        double zTileNew = worldBefore.Y / vm.TileSize;
        var screenAfter = new Point(
            origin2.X + xTileNew * tile2,
            origin2.Y + (vm.GridLength - zTileNew) * tile2);
        // Shift pan so it lands on the original cursor again.
        vm.PanOffsetX += pos.X - screenAfter.X;
        vm.PanOffsetY += pos.Y - screenAfter.Y;
        e.Handled = true;
    }

    static System.Numerics.Vector2 RoomCenterWorld(OpenApparatus.Topology.Room room)
    {
        var b = room.GetWorldBounds();
        return new System.Numerics.Vector2(
            (b.Min.X + b.Max.X) * 0.5f,
            (b.Min.Y + b.Max.Y) * 0.5f);
    }

    /// <summary>
    /// Click handling while ViewMode == Objects: an object hit takes priority
    /// over a sub-cell hit (so the user can click an object to select it even
    /// if the sub-cell underneath is also a valid placement target). Falling
    /// off both clears the selection.
    /// </summary>
    void HandleObjectsModeClick(MainWindowViewModel vm, Point pos, Point origin, double tilePxSize)
    {
        // 1. Object hit-test. Use a small screen-space radius around the
        //    object's projected centre.
        const double ObjHitRadiusPx = 10.0;
        for (int i = 0; i < vm.Objects.Count; i++)
        {
            var o = vm.Objects[i];
            var screen = WorldXzToScreen(
                new System.Numerics.Vector2(o.Position.X, o.Position.Z),
                origin, tilePxSize, vm);
            double dx = screen.X - pos.X, dy = screen.Y - pos.Y;
            if (dx * dx + dy * dy <= ObjHitRadiusPx * ObjHitRadiusPx)
            {
                vm.SelectedObjectIndex = i;
                vm.SelectedSubCell = null;
                return;
            }
        }

        // 2. Sub-cell hit-test.
        if (TryHitSubCell(vm, pos, origin, tilePxSize, out var sc))
        {
            vm.SelectedSubCell = sc;
            vm.SelectedObjectIndex = -1;
            return;
        }

        // 3. Empty space → clear selection.
        vm.SelectedSubCell = null;
        vm.SelectedObjectIndex = -1;
    }

    static bool TryHitSubCell(
        MainWindowViewModel vm, Point pos, Point origin, double tilePxSize,
        out (int TileX, int TileZ, int FineX, int FineZ) sc)
    {
        sc = default;
        int n = System.Math.Max(1, vm.GridSubdivision);
        // Convert screen → tile-space; floor to int gives tile + sub-cell.
        double localX = pos.X - origin.X;
        double localY = origin.Y + vm.GridLength * tilePxSize - pos.Y;
        if (localX < 0 || localY < 0) return false;
        double subPx = tilePxSize / n;
        int subX = (int)(localX / subPx);
        int subZ = (int)(localY / subPx);
        int tileX = subX / n;
        int tileZ = subZ / n;
        if (tileX < 0 || tileX >= vm.GridWidth || tileZ < 0 || tileZ >= vm.GridLength) return false;
        sc = (tileX, tileZ, subX % n, subZ % n);
        return true;
    }

    static Point WorldXzToScreen(System.Numerics.Vector2 worldXz, Point origin, double tilePxSize, MainWindowViewModel vm)
    {
        double xTile = worldXz.X / vm.TileSize;
        double zTile = worldXz.Y / vm.TileSize;
        return new Point(
            origin.X + xTile * tilePxSize,
            origin.Y + (vm.GridLength - zTile) * tilePxSize);
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
        var vm = Vm;
        if (vm is null) return;
        var pos = e.GetPosition(this);

        if (_panning)
        {
            vm.PanOffsetX = _panStartOffsetX + (pos.X - _panStartPos.X);
            vm.PanOffsetY = _panStartOffsetY + (pos.Y - _panStartPos.Y);
            return;
        }

        if (!_dragging) return;
        if (TryHitTest(pos, vm, out int x, out int z))
            ApplyDrag(vm, x, z);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
        }
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
        var gridStroke = new Pen(new SolidColorBrush(Color.FromRgb(190, 190, 198)), 1.5);

        // Tiles. Floor view uses each room's floor color, Ceiling view uses its
        // ceiling color. Either falls back to the auto-generated room hue if no
        // color has been seeded yet (e.g. very old saves).
        bool ceilingMode = vm.ViewMode == MainWindowViewModel.ViewSurface.Ceiling;
        var perRoom = ceilingMode ? vm.RoomCeilingColors : vm.RoomFloorColors;
        for (int x = 0; x < vm.GridWidth; x++)
            for (int z = 0; z < vm.GridLength; z++)
            {
                var rect = TileRect(origin, tileSize, x, z, vm.GridLength);
                int id = vm.RoomGrid[x, z];
                IBrush fill;
                if (id >= 0)
                {
                    var rgb = perRoom.TryGetValue(id, out var c)
                        ? c
                        : MainWindowViewModel.RoomColorRgb(id);
                    fill = new SolidColorBrush(Color.FromRgb(
                        (byte)(rgb.X * 255), (byte)(rgb.Y * 255), (byte)(rgb.Z * 255)));
                }
                else if (vm.SelectedTiles.Contains((x, z))) fill = selectedFill;
                else fill = emptyFill;
                ctx.FillRectangle(fill, rect);
                ctx.DrawRectangle(null, gridStroke, rect);
            }

        // Objects-mode overlays: subdivision grid + selected-subcell highlight.
        // Drawn here (after tiles, before walls) so walls always render on top
        // of the sub-grid lines.
        if (vm.IsObjectsMode && vm.GridSubdivision > 1)
        {
            var subStroke = new Pen(new SolidColorBrush(Color.FromArgb(95, 110, 110, 130)), 0.35);
            int n = vm.GridSubdivision;
            double subPx = tileSize / n;
            for (int x = 0; x < vm.GridWidth; x++)
                for (int z = 0; z < vm.GridLength; z++)
                {
                    if (vm.RoomGrid[x, z] < 0) continue;
                    var tileRect = TileRect(origin, tileSize, x, z, vm.GridLength);
                    for (int k = 1; k < n; k++)
                    {
                        double offset = k * subPx;
                        ctx.DrawLine(subStroke,
                            new Point(tileRect.X + offset, tileRect.Y),
                            new Point(tileRect.X + offset, tileRect.Y + tileSize));
                        ctx.DrawLine(subStroke,
                            new Point(tileRect.X, tileRect.Y + offset),
                            new Point(tileRect.X + tileSize, tileRect.Y + offset));
                    }
                }
        }
        if (vm.IsObjectsMode && vm.SelectedSubCell is { } sub)
        {
            int n = System.Math.Max(1, vm.GridSubdivision);
            double subPx = tileSize / n;
            var tileRect = TileRect(origin, tileSize, sub.TileX, sub.TileZ, vm.GridLength);
            // Y in screen grows down but tile Z grows up — subZ=0 sits at the
            // BOTTOM of the tile in world space, which is the BOTTOM of the
            // tile rect on screen too. So the highlight Y is measured from
            // tileRect.Bottom upward.
            double left = tileRect.X + sub.FineX * subPx;
            double bottom = tileRect.Y + tileSize - sub.FineZ * subPx;
            double top = bottom - subPx;
            var hiRect = new Rect(left, top, subPx, subPx);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(140, 255, 220, 80)), hiRect);
            ctx.DrawRectangle(null,
                new Pen(new SolidColorBrush(Color.FromRgb(220, 160, 30)), 1.5), hiRect);
        }

        // Wall segments from the materialized environment, drawn over the grid. World
        // coords are in meters; convert to tile units (÷ vm.TileSize) then to pixels.
        if (vm.CurrentEnvironment is { } env)
        {
            // Bumped from 2.5 to 4 px so the wall outline still reads clearly
            // against the 14 px coloured interior borders that flank it.
            var closedPen = new Pen(Brushes.Black, 4.0);
            var doorPen = new Pen(new SolidColorBrush(Color.FromRgb(224, 96, 16)), 5.0)
            {
                LineCap = PenLineCap.Round,
            };
            // Windows: standard architectural symbol — two thin parallel solid
            // lines (inside + outside glass faces) straddling the wall, in light
            // blue so they're distinguishable from black walls.
            var windowPen = new Pen(new SolidColorBrush(Color.FromRgb(60, 140, 220)), 2.0)
            {
                LineCap = PenLineCap.Square,
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

            // Interior wall borders — drawn FIRST so the wall outlines (and any
            // door / window indicators on them) layer on top and stay visible.
            // Square caps so wall ends look architectural rather than rounded;
            // inset both ends by the offset distance so two perpendicular
            // borders don't overlap at the inside corner of a room.
            foreach (var adj in env.Adjacencies)
            {
                var seg = adj.SharedSegment;
                var p0b = ToScreen(seg.Start);
                var p1b = ToScreen(seg.End);
                var nrmW = seg.Normal;
                var nrmScreen = new Point(
                    nrmW.X / vm.TileSize * tileSize,
                    -nrmW.Y / vm.TileSize * tileSize);
                double nrmLen = Math.Sqrt(nrmScreen.X * nrmScreen.X + nrmScreen.Y * nrmScreen.Y);
                if (nrmLen < 1e-3) continue;
                var nrmUnit = new Point(nrmScreen.X / nrmLen, nrmScreen.Y / nrmLen);

                DrawSide(adj, isRoomA: true, p0b, p1b, nrmUnit, +1);
                if (adj.IsInternal) DrawSide(adj, isRoomA: false, p0b, p1b, nrmUnit, -1);

                void DrawSide(OpenApparatus.Topology.Adjacency a, bool isRoomA,
                    Point pa, Point pb, Point nrm, int sign)
                {
                    int roomId = isRoomA ? a.RoomA.Id : a.RoomB!.Id;
                    var off = new Point(nrm.X * sign * InteriorBorderOffsetPx, nrm.Y * sign * InteriorBorderOffsetPx);
                    var ax = new Point(pa.X + off.X, pa.Y + off.Y);
                    var bx = new Point(pb.X + off.X, pb.Y + off.Y);
                    var inset = ShrinkSegment(ax, bx, InteriorBorderOffsetPx);

                    var rgb = vm.EffectiveWallColor(roomId, a);
                    byte alpha = (byte)Math.Clamp(Math.Round(vm.WallBorderOpacity * 255), 0, 255);
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha,
                        (byte)(rgb.X * 255), (byte)(rgb.Y * 255), (byte)(rgb.Z * 255))),
                        InteriorBorderThicknessPx)
                    {
                        LineCap = PenLineCap.Square,
                    };
                    ctx.DrawLine(pen, inset.A, inset.B);
                }
            }

            // Wall outlines (closed sections, doorways, open passages) — drawn
            // after the interior borders so they remain visible on top.
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
                            // For each opening: gap in the wall + colored indicator
                            // across the gap. Doors get perpendicular jamb tick marks
                            // at each end (standard architectural notation). Windows
                            // get parallel sill lines spanning the opening.
                            var dir = s.Direction;
                            var nrm2 = s.Normal;
                            var nrmScr = new Point(
                                nrm2.X / vm.TileSize * tileSize,
                                -nrm2.Y / vm.TileSize * tileSize);
                            double nrmLen2 = Math.Sqrt(nrmScr.X * nrmScr.X + nrmScr.Y * nrmScr.Y);
                            var nrmU = nrmLen2 < 1e-3 ? new Point(0, 0)
                                : new Point(nrmScr.X / nrmLen2, nrmScr.Y / nrmLen2);

                            var jambPen = new Pen(Brushes.Black, 2.5)
                            {
                                LineCap = PenLineCap.Square,
                            };
                            const double JambHalfPx = 7.0;
                            const double WindowSillOffsetPx = 3.0;

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
                                var startScr = ToScreen(doorStartW);
                                var endScr = ToScreen(doorEndW);

                                // Compute screen-space wall direction once per opening; used by
                                // both the door swing arc and the window symbol below.
                                var dirVec = new Point(endScr.X - startScr.X, endScr.Y - startScr.Y);
                                double dirLen = Math.Sqrt(dirVec.X * dirVec.X + dirVec.Y * dirVec.Y);
                                var dirU = dirLen < 1e-3 ? new Point(1, 0)
                                    : new Point(dirVec.X / dirLen, dirVec.Y / dirLen);

                                if (op.IsWindow)
                                {
                                    // Architectural window symbol: two parallel
                                    // thin lines (inside + outside glass faces)
                                    // straddling the wall, plus perpendicular jamb
                                    // tick marks at each end where the wall stops.
                                    if (nrmLen2 >= 1e-3)
                                    {
                                        var pInner = new Point(nrmU.X * WindowSillOffsetPx, nrmU.Y * WindowSillOffsetPx);
                                        ctx.DrawLine(windowPen,
                                            new Point(startScr.X + pInner.X, startScr.Y + pInner.Y),
                                            new Point(endScr.X + pInner.X, endScr.Y + pInner.Y));
                                        ctx.DrawLine(windowPen,
                                            new Point(startScr.X - pInner.X, startScr.Y - pInner.Y),
                                            new Point(endScr.X - pInner.X, endScr.Y - pInner.Y));

                                        var off = new Point(nrmU.X * JambHalfPx, nrmU.Y * JambHalfPx);
                                        ctx.DrawLine(jambPen,
                                            new Point(startScr.X - off.X, startScr.Y - off.Y),
                                            new Point(startScr.X + off.X, startScr.Y + off.Y));
                                        ctx.DrawLine(jambPen,
                                            new Point(endScr.X - off.X, endScr.Y - off.Y),
                                            new Point(endScr.X + off.X, endScr.Y + off.Y));
                                    }
                                }
                                else
                                {
                                    // Architectural door symbol: door panel +
                                    // 90° swing arc + jamb tick marks.
                                    // Hinge end and swing direction are read
                                    // from the opening so the symbol matches
                                    // whatever the user picked in the panel.
                                    if (nrmLen2 >= 1e-3 && dirLen > 1e-3)
                                    {
                                        // Hinge: start of opening by default,
                                        // end of opening when HingeAtEnd. Pivot
                                        // direction (closed → open) flips
                                        // accordingly.
                                        var hinge = op.HingeAtEnd ? endScr : startScr;
                                        var closedTip = op.HingeAtEnd ? startScr : endScr;
                                        // Sign for swing direction: +1 = +N
                                        // (RoomA side), -1 = -N (RoomB side or
                                        // opposite for outer walls).
                                        double swingSign = op.SwingNegative ? -1.0 : 1.0;
                                        // Sign for "from hinge toward closed
                                        // tip": the wall-direction unit vector
                                        // pointing along the opening from the
                                        // hinge to the other jamb.
                                        double dirSign = op.HingeAtEnd ? -1.0 : 1.0;
                                        var openTip = new Point(
                                            hinge.X + nrmU.X * dirLen * swingSign,
                                            hinge.Y + nrmU.Y * dirLen * swingSign);
                                        var doorPanelPen = new Pen(
                                            new SolidColorBrush(Color.FromRgb(224, 96, 16)), 2.5)
                                        {
                                            LineCap = PenLineCap.Square,
                                        };
                                        var swingPen = new Pen(
                                            new SolidColorBrush(Color.FromRgb(224, 96, 16)), 1.5);

                                        ctx.DrawLine(doorPanelPen, hinge, openTip);

                                        // 90° arc from openTip to endScr (closed
                                        // position) sampled as line segments —
                                        // simpler than wrangling StreamGeometry
                                        // sweep direction for either orientation.
                                        // Arc from openTip (perpendicular to
                                        // wall, on the chosen swing side) to
                                        // closedTip (along wall from hinge).
                                        const int Steps = 16;
                                        var prevArc = openTip;
                                        for (int si = 1; si <= Steps; si++)
                                        {
                                            double t = si / (double)Steps;
                                            double ang = t * Math.PI / 2;
                                            double dx = nrmU.X * swingSign * Math.Cos(ang)
                                                      + dirU.X * dirSign * Math.Sin(ang);
                                            double dy = nrmU.Y * swingSign * Math.Cos(ang)
                                                      + dirU.Y * dirSign * Math.Sin(ang);
                                            var pt = new Point(
                                                hinge.X + dx * dirLen,
                                                hinge.Y + dy * dirLen);
                                            ctx.DrawLine(swingPen, prevArc, pt);
                                            prevArc = pt;
                                        }

                                        // Jamb tick marks at both ends.
                                        var off = new Point(nrmU.X * JambHalfPx, nrmU.Y * JambHalfPx);
                                        ctx.DrawLine(jambPen,
                                            new Point(startScr.X - off.X, startScr.Y - off.Y),
                                            new Point(startScr.X + off.X, startScr.Y + off.Y));
                                        ctx.DrawLine(jambPen,
                                            new Point(endScr.X - off.X, endScr.Y - off.Y),
                                            new Point(endScr.X + off.X, endScr.Y + off.Y));
                                    }
                                }
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

            // Path overlay: when ShowPaths is on, draw a polyline from each
            // room's centre through every traversable opening into its
            // neighbour's centre. Closed walls don't form paths and are
            // skipped. Anchored at door / opening midpoints since cell-centre
            // anchoring isn't well-defined for multi-tile rooms.
            if (vm.ShowPaths)
            {
                var pc = vm.PathColor;
                var pathColor = Color.FromRgb((byte)(pc.X * 255), (byte)(pc.Y * 255), (byte)(pc.Z * 255));
                var pathBrush = new SolidColorBrush(pathColor);
                var pathPen = new Pen(pathBrush, 2.5) { LineCap = PenLineCap.Round };
                var nodePen = new Pen(pathBrush, 2.0);
                var nodeFill = new SolidColorBrush(Color.FromRgb(255, 255, 255));

                // Pass 1: lines + door midpoint dots. Lines drawn first so the
                // room-centre circles can paint over their endpoints — that way
                // the line never bleeds into the room number.
                var roomCentres = new System.Collections.Generic.Dictionary<int, Point>();
                foreach (var adj in env.Adjacencies)
                {
                    if (!adj.IsInternal) continue;
                    if (adj.Passage is OpenApparatus.Topology.Passage.Closed) continue;

                    var aWorld = RoomCenterWorld(adj.RoomA);
                    var bWorld = RoomCenterWorld(adj.RoomB!);
                    var aScr = ToScreen(aWorld);
                    var bScr = ToScreen(bWorld);
                    roomCentres[adj.RoomA.Id] = aScr;
                    roomCentres[adj.RoomB!.Id] = bScr;

                    var seg = adj.SharedSegment;
                    var dir2 = seg.Direction;
                    var doorPoints = new System.Collections.Generic.List<System.Numerics.Vector2>();
                    if (adj.Passage is OpenApparatus.Topology.Passage.Doorway dw)
                    {
                        foreach (var op in dw.Openings)
                            doorPoints.Add(seg.Start + dir2 * (op.OffsetAlongEdge + op.Width * 0.5f));
                    }
                    else
                    {
                        doorPoints.Add(seg.Midpoint);
                    }

                    foreach (var dp in doorPoints)
                    {
                        var midScr = ToScreen(dp);
                        ctx.DrawLine(pathPen, aScr, midScr);
                        ctx.DrawLine(pathPen, midScr, bScr);
                        ctx.DrawEllipse(pathBrush, null, midScr, 3, 3);
                    }
                }

                // Pass 2: room-centre nodes. White fill + path-colour outline so
                // the room-id label (drawn later in Render()) stays legible.
                const double NodeRadius = 13.0;
                foreach (var centre in roomCentres.Values)
                    ctx.DrawEllipse(nodeFill, nodePen, centre, NodeRadius, NodeRadius);
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

            // Optional room name beneath the id. Positioned far enough below
            // the centre that the path overlay's 13-px node circle (when
            // visible) doesn't overlap it; padded a few extra pixels so the
            // gap reads cleanly even without the circle.
            string roomName = vm.GetRoomName(rId);
            if (!string.IsNullOrEmpty(roomName))
            {
                var nameFmt = new FormattedText(roomName,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 11,
                    new SolidColorBrush(Color.FromRgb(50, 50, 60)));
                const double nodeRadius = 13.0;
                const double nameGap = 5.0;
                ctx.DrawText(nameFmt,
                    new Point(cx - nameFmt.Width * 0.5, cy + nodeRadius + nameGap));
            }
        }

        // Object icons — drawn last so they sit on top of room labels in
        // Objects mode. In Floor / Ceiling mode they still render, dimmed,
        // so the user always sees what's been placed.
        DrawObjects(ctx, vm, origin, tileSize);
    }

    static void DrawObjects(DrawingContext ctx, MainWindowViewModel vm, Point origin, double tileSize)
    {
        if (vm.Objects.Count == 0) return;
        bool dim = !vm.IsObjectsMode;
        for (int i = 0; i < vm.Objects.Count; i++)
        {
            var o = vm.Objects[i];
            var slot = ObjectSlots.Get(o.Slot);
            if (slot is null) continue;
            var screen = WorldXzToScreen(
                new System.Numerics.Vector2(o.Position.X, o.Position.Z),
                origin, tileSize, vm);
            byte alpha = (byte)(dim ? 110 : 255);
            var fill = new SolidColorBrush(Color.FromArgb(alpha,
                (byte)(slot.Color.X * 255), (byte)(slot.Color.Y * 255), (byte)(slot.Color.Z * 255)));
            var border = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 30, 30, 40)), 1.2);
            bool selected = vm.IsObjectsMode && i == vm.SelectedObjectIndex;
            // 2D icon footprint: roughly 1/3 of a tile, capped at 16 px.
            double iconR = System.Math.Min(16.0, System.Math.Max(6.0, tileSize * 0.25));
            DrawObjectIcon(ctx, slot.Shape, screen, iconR, fill, border);
            if (selected)
            {
                ctx.DrawEllipse(null,
                    new Pen(new SolidColorBrush(Color.FromRgb(255, 200, 30)), 2.0)
                    {
                        DashStyle = new DashStyle(new[] { 2.5, 1.5 }, 0),
                    },
                    screen, iconR + 5, iconR + 5);
            }
        }
    }

    static void DrawObjectIcon(DrawingContext ctx, ObjectShape shape, Point centre, double r, IBrush fill, Pen border)
    {
        switch (shape)
        {
            case ObjectShape.Sphere:
                ctx.DrawEllipse(fill, border, centre, r, r);
                break;
            case ObjectShape.Cylinder:
                ctx.DrawEllipse(fill, border, centre, r, r * 0.7);
                break;
            case ObjectShape.Cone:
                ctx.DrawGeometry(fill, border, BuildTriangle(centre, r));
                break;
            case ObjectShape.Pyramid:
                ctx.DrawGeometry(fill, border, BuildDiamond(centre, r));
                break;
            case ObjectShape.SquatCylinder:
                ctx.DrawEllipse(fill, border, centre, r * 1.1, r * 0.55);
                break;
            case ObjectShape.Capsule:
                {
                    var rect = new Rect(centre.X - r * 0.55, centre.Y - r, r * 1.1, r * 2);
                    ctx.DrawRectangle(fill, border, rect, r * 0.55, r * 0.55);
                    break;
                }
            case ObjectShape.Cube:
            default:
                ctx.DrawRectangle(fill, border,
                    new Rect(centre.X - r, centre.Y - r, r * 2, r * 2));
                break;
        }
    }

    static Avalonia.Media.Geometry BuildTriangle(Point centre, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(centre.X, centre.Y - r), isFilled: true);
            c.LineTo(new Point(centre.X + r, centre.Y + r));
            c.LineTo(new Point(centre.X - r, centre.Y + r));
            c.EndFigure(true);
        }
        return g;
    }

    static Avalonia.Media.Geometry BuildDiamond(Point centre, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(centre.X, centre.Y - r), isFilled: true);
            c.LineTo(new Point(centre.X + r, centre.Y));
            c.LineTo(new Point(centre.X, centre.Y + r));
            c.LineTo(new Point(centre.X - r, centre.Y));
            c.EndFigure(true);
        }
        return g;
    }

    /// <summary>Draws a single legend symbol; exposed so a separate legend control
    /// can render the same vocabulary the editor uses inline.</summary>
    public static void DrawWallSymbol(DrawingContext ctx, Point start, double width)
    {
        var pen = new Pen(Brushes.Black, 4) { LineCap = PenLineCap.Square };
        ctx.DrawLine(pen, start, new Point(start.X + width, start.Y));
    }

    public static void DrawDoorSymbol(DrawingContext ctx, Point start, double width)
    {
        // Hinge at left, opening to the right; arc swings up. Same vocabulary
        // (panel + arc + jambs) used in the editor proper.
        var orange = new SolidColorBrush(Color.FromRgb(224, 96, 16));
        var jambPen = new Pen(Brushes.Black, 2) { LineCap = PenLineCap.Square };
        var panelPen = new Pen(orange, 2) { LineCap = PenLineCap.Square };
        var arcPen = new Pen(orange, 1.2);

        var hinge = new Point(start.X, start.Y + 4);
        var closedTip = new Point(hinge.X + width, hinge.Y);
        var openTip = new Point(hinge.X, hinge.Y - width * 0.7);

        // Panel (hinge → openTip)
        ctx.DrawLine(panelPen, hinge, openTip);
        // Approximated 90° arc from openTip to closedTip
        const int Steps = 12;
        var prev = openTip;
        double r = width * 0.7;
        for (int s = 1; s <= Steps; s++)
        {
            double t = s / (double)Steps;
            double a = t * Math.PI / 2;
            // Start angle: -Y (up), end angle: +X (right)
            double dx = -Math.Sin(-a);
            double dy = -Math.Cos(a);
            var pt = new Point(hinge.X + dx * r, hinge.Y + dy * r);
            // Map x linearly so closed-tip lands at width
            pt = new Point(hinge.X + Math.Sin(a) * width, hinge.Y - Math.Cos(a) * r);
            ctx.DrawLine(arcPen, prev, pt);
            prev = pt;
        }
        // Jambs
        ctx.DrawLine(jambPen,
            new Point(hinge.X, hinge.Y - 4), new Point(hinge.X, hinge.Y + 4));
        ctx.DrawLine(jambPen,
            new Point(closedTip.X, closedTip.Y - 4), new Point(closedTip.X, closedTip.Y + 4));
    }

    public static void DrawWindowSymbol(DrawingContext ctx, Point start, double width)
    {
        var blue = new SolidColorBrush(Color.FromRgb(60, 140, 220));
        var winPen = new Pen(blue, 2) { LineCap = PenLineCap.Square };
        var jambPen = new Pen(Brushes.Black, 2) { LineCap = PenLineCap.Square };

        // Two parallel sill lines
        ctx.DrawLine(winPen,
            new Point(start.X, start.Y - 3), new Point(start.X + width, start.Y - 3));
        ctx.DrawLine(winPen,
            new Point(start.X, start.Y + 3), new Point(start.X + width, start.Y + 3));
        // Jambs
        ctx.DrawLine(jambPen,
            new Point(start.X, start.Y - 6), new Point(start.X, start.Y + 6));
        ctx.DrawLine(jambPen,
            new Point(start.X + width, start.Y - 6), new Point(start.X + width, start.Y + 6));
    }

    public static void DrawOpenSymbol(DrawingContext ctx, Point start, double width)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(120, 180, 120)), 1.5)
        {
            DashStyle = DashStyle.Dash,
        };
        ctx.DrawLine(pen, start, new Point(start.X + width, start.Y));
    }

    static Rect TileRect(Point origin, double tileSize, int x, int z, int gridLength)
    {
        double left = origin.X + x * tileSize;
        double top = origin.Y + (gridLength - z - 1) * tileSize;
        return new Rect(left, top, tileSize, tileSize);
    }

    public const double EditorPadding = 16.0;

    /// <summary>
    /// Returns the viewport's available content area (the box the grid is laid
    /// out into, after subtracting padding). Used by FocusOnRoom on the VM to
    /// compute the right zoom + pan target.
    /// </summary>
    public (double availW, double availH) GetAvailableArea()
    {
        var size = Bounds.Size;
        return (size.Width - EditorPadding * 2, size.Height - EditorPadding * 2);
    }

    (Point origin, double tileSize) ComputeLayout(MainWindowViewModel vm)
    {
        var size = Bounds.Size;
        double availW = size.Width - EditorPadding * 2;
        double availH = size.Height - EditorPadding * 2;
        if (availW <= 0 || availH <= 0 || vm.GridWidth <= 0 || vm.GridLength <= 0)
            return (new Point(EditorPadding, EditorPadding), 0);

        double sx = availW / vm.GridWidth;
        double sy = availH / vm.GridLength;
        double tile = System.Math.Min(sx, sy) * vm.ZoomFactor;
        double offsetX = EditorPadding + (availW - tile * vm.GridWidth) * 0.5 + vm.PanOffsetX;
        double offsetY = EditorPadding + (availH - tile * vm.GridLength) * 0.5 + vm.PanOffsetY;
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
