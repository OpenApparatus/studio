using System;
using System.Collections.Generic;
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

    // 3D-view interaction state. Captured at PointerPressed and consumed
    // by PointerMoved while the corresponding mouse button is held.
    bool _isoOrbiting;     // left drag → orbit
    bool _isoPanning;      // middle/right drag → pan pivot
    Point _isoDragStart;
    float _isoStartYaw, _isoStartPitch, _isoStartPivotX, _isoStartPivotZ;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var vm = Vm;
        if (vm is null) return;
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        // 3D view: read-only camera controls.
        // Left drag  → orbit (yaw + pitch)
        // Middle/right drag → pan the pivot in world XZ
        if (vm.IsIsoView)
        {
            _isoDragStart = pos;
            _isoStartYaw     = vm.IsoYaw;
            _isoStartPitch   = vm.IsoPitch;
            _isoStartPivotX  = vm.IsoPivotX;
            _isoStartPivotZ  = vm.IsoPivotZ;
            if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
                _isoPanning = true;
            else if (props.IsLeftButtonPressed)
                _isoOrbiting = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

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
            HandleObjectsModeClick(vm, pos, origin, tilePxSize, e.KeyModifiers);
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

        // 3D view: dolly the camera in / out by adjusting orbit distance.
        if (vm.IsIsoView)
        {
            float dollyStep = e.Delta.Y > 0 ? 1f / 1.12f : 1.12f;
            vm.IsoDistance = System.Math.Clamp(vm.IsoDistance * dollyStep, 2f, 500f);
            vm.RaiseEditVersion();
            e.Handled = true;
            return;
        }

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

    /// <summary>One leg of the connectivity graph — used by both the static
    /// and animated path renderers.</summary>
    readonly struct PathEdge
    {
        public readonly System.Numerics.Vector2 AWorld;
        public readonly System.Numerics.Vector2 DoorWorld;
        public readonly System.Numerics.Vector2 BWorld;
        public readonly int ARoom;
        public readonly int BRoom;
        public PathEdge(int aRoom, System.Numerics.Vector2 a, System.Numerics.Vector2 d, int bRoom, System.Numerics.Vector2 b)
        { ARoom = aRoom; AWorld = a; DoorWorld = d; BRoom = bRoom; BWorld = b; }
    }

    /// <summary>Returns every connectivity edge in the environment — one per
    /// traversable adjacency / opening combination. Order is unspecified.</summary>
    static System.Collections.Generic.List<PathEdge> CollectAllPathEdges(MainWindowViewModel vm, OpenApparatus.Topology.MultiRoomEnvironment env)
    {
        var list = new System.Collections.Generic.List<PathEdge>();
        foreach (var adj in env.Adjacencies)
        {
            if (!adj.IsInternal) continue;
            if (adj.Passage is OpenApparatus.Topology.Passage.Closed) continue;
            var aWorld = RoomCenterWorld(adj.RoomA);
            var bWorld = RoomCenterWorld(adj.RoomB!);
            var seg = adj.SharedSegment;
            if (adj.Passage is OpenApparatus.Topology.Passage.Doorway dw)
            {
                foreach (var op in dw.Openings)
                {
                    var door = seg.Start + seg.Direction * (op.OffsetAlongEdge + op.Width * 0.5f);
                    list.Add(new PathEdge(adj.RoomA.Id, aWorld, door, adj.RoomB!.Id, bWorld));
                }
            }
            else
            {
                list.Add(new PathEdge(adj.RoomA.Id, aWorld, seg.Midpoint, adj.RoomB!.Id, bWorld));
            }
        }
        return list;
    }

    /// <summary>Orders the path edges for the animation: BFS outward from the
    /// lowest-id room (the start), so reveal proceeds room by room.</summary>
    static System.Collections.Generic.List<PathEdge> OrderEdgesBfs(
        OpenApparatus.Topology.MultiRoomEnvironment env,
        System.Collections.Generic.List<PathEdge> all)
    {
        var ordered = new System.Collections.Generic.List<PathEdge>();
        if (env.Rooms.Count == 0) return ordered;
        // Starting room: lowest id present.
        int startId = int.MaxValue;
        foreach (var r in env.Rooms) if (r.Id < startId) startId = r.Id;
        var visited = new System.Collections.Generic.HashSet<int> { startId };
        var queue = new System.Collections.Generic.Queue<int>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            // Edges leaving cur to unvisited rooms first; the rest fall to the tail.
            foreach (var e in all)
            {
                int? other = e.ARoom == cur ? e.BRoom :
                             e.BRoom == cur ? e.ARoom : (int?)null;
                if (other is int o && !visited.Contains(o))
                {
                    // Re-orient the edge so it always goes from `cur` outward.
                    var orient = e.ARoom == cur
                        ? e
                        : new PathEdge(e.BRoom, e.BWorld, e.DoorWorld, e.ARoom, e.AWorld);
                    ordered.Add(orient);
                    visited.Add(o);
                    queue.Enqueue(o);
                }
            }
        }
        // Append edges that connect already-visited rooms (cycles) so cyclical
        // graphs still draw every connection. Skip duplicates.
        foreach (var e in all)
        {
            bool already = false;
            foreach (var oe in ordered)
                if ((oe.ARoom == e.ARoom && oe.BRoom == e.BRoom)
                 || (oe.ARoom == e.BRoom && oe.BRoom == e.ARoom))
                {
                    if (oe.DoorWorld == e.DoorWorld) { already = true; break; }
                }
            if (!already) ordered.Add(e);
        }
        return ordered;
    }

    static void DrawStaticPaths(
        DrawingContext ctx, MainWindowViewModel vm, OpenApparatus.Topology.MultiRoomEnvironment env,
        System.Func<System.Numerics.Vector2, Point> ToScreen,
        IBrush pathBrush, Pen pathPen, Pen nodePen, IBrush nodeFill)
    {
        var roomCentres = new System.Collections.Generic.Dictionary<int, Point>();
        foreach (var e in CollectAllPathEdges(vm, env))
        {
            var aScr = ToScreen(e.AWorld);
            var bScr = ToScreen(e.BWorld);
            var dScr = ToScreen(e.DoorWorld);
            roomCentres[e.ARoom] = aScr;
            roomCentres[e.BRoom] = bScr;
            ctx.DrawLine(pathPen, aScr, dScr);
            ctx.DrawLine(pathPen, dScr, bScr);
            ctx.DrawEllipse(pathBrush, null, dScr, 3, 3);
        }
        const double NodeRadius = 13.0;
        foreach (var centre in roomCentres.Values)
            ctx.DrawEllipse(nodeFill, nodePen, centre, NodeRadius, NodeRadius);
    }

    /// <summary>
    /// Path overlay with the leading edge clipped to the current animation
    /// progress. Edges are revealed in BFS order from the lowest-id room.
    /// Room nodes appear as their inbound edge becomes fully drawn so the
    /// reveal feels like a wavefront propagating outward.
    /// </summary>
    static void DrawAnimatedPaths(
        DrawingContext ctx, MainWindowViewModel vm, OpenApparatus.Topology.MultiRoomEnvironment env,
        System.Func<System.Numerics.Vector2, Point> ToScreen,
        IBrush pathBrush, Pen pathPen, Pen nodePen, IBrush nodeFill)
    {
        var ordered = OrderEdgesBfs(env, CollectAllPathEdges(vm, env));
        if (ordered.Count == 0) return;

        // 0..1 → which edge index is "in progress", and how far through it.
        double total = ordered.Count;
        double pos = vm.PathAnimationProgress * total;
        int leadIdx = (int)System.Math.Floor(pos);
        double leadFrac = pos - leadIdx;

        var visibleRooms = new System.Collections.Generic.HashSet<int>();
        // Always draw the start node so it's visible from t=0.
        if (ordered.Count > 0) visibleRooms.Add(ordered[0].ARoom);

        const double NodeRadius = 13.0;

        for (int i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            var aScr = ToScreen(e.AWorld);
            var bScr = ToScreen(e.BWorld);
            var dScr = ToScreen(e.DoorWorld);

            if (i < leadIdx)
            {
                // Fully drawn.
                ctx.DrawLine(pathPen, aScr, dScr);
                ctx.DrawLine(pathPen, dScr, bScr);
                ctx.DrawEllipse(pathBrush, null, dScr, 3, 3);
                visibleRooms.Add(e.ARoom);
                visibleRooms.Add(e.BRoom);
            }
            else if (i == leadIdx && leadFrac > 0)
            {
                // Partially drawn — reveal A → door → B in path-length order.
                double aToDoor = System.Math.Sqrt((dScr.X - aScr.X) * (dScr.X - aScr.X) + (dScr.Y - aScr.Y) * (dScr.Y - aScr.Y));
                double doorToB = System.Math.Sqrt((bScr.X - dScr.X) * (bScr.X - dScr.X) + (bScr.Y - dScr.Y) * (bScr.Y - dScr.Y));
                double total2 = aToDoor + doorToB;
                if (total2 > 1e-6)
                {
                    double sweep = total2 * leadFrac;
                    if (sweep <= aToDoor)
                    {
                        double t = sweep / aToDoor;
                        var tip = new Point(aScr.X + (dScr.X - aScr.X) * t, aScr.Y + (dScr.Y - aScr.Y) * t);
                        ctx.DrawLine(pathPen, aScr, tip);
                    }
                    else
                    {
                        ctx.DrawLine(pathPen, aScr, dScr);
                        ctx.DrawEllipse(pathBrush, null, dScr, 3, 3);
                        double t = (sweep - aToDoor) / doorToB;
                        var tip = new Point(dScr.X + (bScr.X - dScr.X) * t, dScr.Y + (bScr.Y - dScr.Y) * t);
                        ctx.DrawLine(pathPen, dScr, tip);
                    }
                }
                visibleRooms.Add(e.ARoom);
            }
            // else: not yet revealed.
        }

        // Room nodes — drawn after the lines so they paint over leading-edge
        // tips for a clean look.
        foreach (var rid in visibleRooms)
        {
            // Pick any centre for this room from the ordered edges.
            foreach (var e in ordered)
            {
                if (e.ARoom == rid) { ctx.DrawEllipse(nodeFill, nodePen, ToScreen(e.AWorld), NodeRadius, NodeRadius); break; }
                if (e.BRoom == rid) { ctx.DrawEllipse(nodeFill, nodePen, ToScreen(e.BWorld), NodeRadius, NodeRadius); break; }
            }
        }
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
    void HandleObjectsModeClick(MainWindowViewModel vm, Point pos, Point origin, double tilePxSize, KeyModifiers modifiers)
    {
        // 1. Object hit-test. Use a small screen-space radius around the
        //    object's projected centre.
        const double ObjHitRadiusPx = 10.0;
        bool additive = (modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
        for (int i = 0; i < vm.Objects.Count; i++)
        {
            var o = vm.Objects[i];
            var screen = WorldXzToScreen(
                new System.Numerics.Vector2(o.Position.X, o.Position.Z),
                origin, tilePxSize, vm);
            double dx = screen.X - pos.X, dy = screen.Y - pos.Y;
            if (dx * dx + dy * dy <= ObjHitRadiusPx * ObjHitRadiusPx)
            {
                if (additive) vm.ToggleObjectInSelection(i);
                else vm.SetObjectSelection(i);
                vm.SelectedSubCell = null;
                return;
            }
        }

        // 2. Sub-cell hit-test.
        if (TryHitSubCell(vm, pos, origin, tilePxSize, out var sc))
        {
            vm.SelectedSubCell = sc;
            vm.SetObjectSelection(-1);
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

    /// <summary>Lerp the colour toward its luminance-preserving grey by
    /// (1 - sat). sat == 1 returns the original colour; sat == 0 returns the
    /// greyscale version. Matches the editor's TileSaturation slider.</summary>
    public static System.Numerics.Vector3 DesaturateRgbPublic(System.Numerics.Vector3 rgb, float sat) => DesaturateRgb(rgb, sat);
    static System.Numerics.Vector3 DesaturateRgb(System.Numerics.Vector3 rgb, float sat)
    {
        float lum = 0.299f * rgb.X + 0.587f * rgb.Y + 0.114f * rgb.Z;
        return new System.Numerics.Vector3(
            lum + (rgb.X - lum) * sat,
            lum + (rgb.Y - lum) * sat,
            lum + (rgb.Z - lum) * sat);
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

        // Cursor world-position readout (top-down only — 3D coords would
        // need ray-cast against the floor plane, future work).
        if (!vm.IsIsoView)
        {
            var (originLayout, tileLayout) = ComputeLayout(vm);
            if (tileLayout > 0)
            {
                var w = ScreenToWorld(pos, originLayout, tileLayout, vm);
                vm.SetCursorWorldPos(w.X, w.Y);
            }
        }

        // ── 3D camera dragging ──
        if (_isoOrbiting)
        {
            double dx = pos.X - _isoDragStart.X;
            double dy = pos.Y - _isoDragStart.Y;
            // 0.005 rad/px feels close to standard 3D viewers.
            vm.IsoYaw   = _isoStartYaw   - (float)(dx * 0.005);
            vm.IsoPitch = (float)System.Math.Clamp(
                _isoStartPitch + (float)(dy * 0.005), 0.05f, (float)(System.Math.PI / 2 - 0.05));
            vm.RaiseEditVersion();
            return;
        }
        if (_isoPanning)
        {
            // Convert pixel delta to world-space pan, oriented to camera yaw.
            // Right is +X in screen; up is +Z. Apply yaw to map screen axes
            // to world XZ. Sensitivity scales with distance so closer views
            // pan in finer increments.
            double dx = pos.X - _isoDragStart.X;
            double dy = pos.Y - _isoDragStart.Y;
            float worldPerPx = vm.IsoDistance * 0.0015f;
            float cosY = System.MathF.Cos(vm.IsoYaw);
            float sinY = System.MathF.Sin(vm.IsoYaw);
            // Screen right vector in world: (cosY, 0, -sinY)
            // Screen up vector projected into XZ: (sinY, 0, cosY)
            vm.IsoPivotX = _isoStartPivotX
                - (float)dx * worldPerPx * cosY
                + (float)dy * worldPerPx * sinY;
            vm.IsoPivotZ = _isoStartPivotZ
                + (float)dx * worldPerPx * sinY
                + (float)dy * worldPerPx * cosY;
            vm.RaiseEditVersion();
            return;
        }

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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Vm?.ClearCursorWorldPos();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        if (_panning || _isoPanning || _isoOrbiting)
        {
            _panning = false;
            _isoPanning = false;
            _isoOrbiting = false;
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

        // 3D preview mode is rendered by a separate axonometric path so the
        // top-down editor doesn't pollute its hot draw loop with an extra
        // branch on every primitive.
        if (vm.IsIsoView)
        {
            RenderIso(ctx, vm, size);
            return;
        }

        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(232, 232, 236)), new Rect(size));

        var (origin, tileSize) = ComputeLayout(vm);
        if (tileSize <= 0) return;

        var emptyFill = new SolidColorBrush(Color.FromRgb(248, 248, 250));
        // Brand-blue tinted fill — matches the selection treatment used on
        // walls + objects elsewhere in the editor.
        var selectedFill = new SolidColorBrush(Color.FromArgb(110, 31, 111, 235));
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
                    // Apply the global tile-saturation slider. 1 = full
                    // colour, 0 = greyscale (luminance-preserving).
                    if (vm.TileSaturation < 0.999)
                        rgb = DesaturateRgb(rgb, (float)vm.TileSaturation);
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
            var subStroke = new Pen(new SolidColorBrush(Color.FromArgb(190, 70, 70, 90)), 0.8);
            int n = vm.GridSubdivision;
            double subPx = tileSize / n;
            for (int x = 0; x < vm.GridWidth; x++)
                for (int z = 0; z < vm.GridLength; z++)
                {
                    // Draw the sub-grid on every tile (including empty ones) —
                    // objects can live outside any room, and the user needs to
                    // see the placement targets there too.
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
            // Selected-wall highlight: brand-blue glow drawn first so the
            // wall pen renders on top of it. Matches selection elsewhere.
            var selectedHighlightPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 31, 111, 235)), 9.0)
            {
                LineCap = PenLineCap.Round,
            };

            // Interior wall borders — drawn FIRST so the wall outlines (and any
            // door / window indicators on them) layer on top and stay visible.
            // Square caps so wall ends look architectural rather than rounded;
            // inset both ends by the offset distance so two perpendicular
            // borders don't overlap at the inside corner of a room.
            if (vm.ShowWallBorders)
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

                if (vm.IsPathAnimating)
                    DrawAnimatedPaths(ctx, vm, env, ToScreen, pathBrush, pathPen, nodePen, nodeFill);
                else
                    DrawStaticPaths(ctx, vm, env, ToScreen, pathBrush, pathPen, nodePen, nodeFill);
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

        // Room id labels — gated by ShowRoomLabels. Even when off we still
        // need typeface for layout-measurement labels later, so it's
        // declared at this scope.
        var labelBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        var typeface = new Typeface("Inter");
        if (vm.ShowRoomLabels)
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

        // Constraint zones (door annular wedges, object exclusion discs) and
        // violator rings — drawn before the measurement labels so the labels
        // remain on top, but after objects so the zones land on the floor.
        DrawConstraintOverlays(ctx, vm, origin, tileSize);

        // Measurement overlay — door→object lines with distance + angle, plus
        // inter-object distance lines. Drawn last so labels stay legible.
        DrawMeasurements(ctx, vm, origin, tileSize);

        // World origin marker — small "+" + "0,0" label at world (0,0).
        // World z=0 maps to the bottom edge of the grid, so the marker
        // sits at the bottom-left corner of the rendered area.
        var originScreen = new Point(origin.X, origin.Y + vm.GridLength * tileSize);
        DrawOriginMarker(ctx, originScreen, typeface);

        // Layout-measurement labels (room dimensions / floor area / opening
        // sizes / wall lengths) on top of everything else.
        DrawLayoutMeasurements(ctx, vm, origin, tileSize, typeface);

        // Empty-state overlay — only shown when the environment has no
        // rooms yet, so a fresh project doesn't read as an inert grid.
        if (vm.CurrentEnvironment is { } envForEmpty && envForEmpty.Rooms.Count == 0)
            DrawEmptyState(ctx, size, typeface, vm.IsObjectsMode);
    }

    /// <summary>Faded centred prompt drawn over the empty grid so a
    /// freshly-created project has a clear "what do I do next" cue.
    /// Wording differs slightly per mode.</summary>
    public static void DrawEmptyStatePublic(DrawingContext ctx, Size size, Typeface typeface, bool isObjectsMode)
        => DrawEmptyState(ctx, size, typeface, isObjectsMode);
    /// <summary>Renders a small "+ 0,0" mark at the world origin so users
    /// can see where (0, 0) lives on the canvas. Useful when aligning the
    /// design with externally-authored content that expects a specific
    /// origin.</summary>
    static void DrawOriginMarker(DrawingContext ctx, Point origin, Typeface typeface)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, 31, 111, 235)), 1.0);
        const double r = 5;
        ctx.DrawLine(pen, new Point(origin.X - r, origin.Y), new Point(origin.X + r, origin.Y));
        ctx.DrawLine(pen, new Point(origin.X, origin.Y - r), new Point(origin.X, origin.Y + r));
        var fmt = new FormattedText("0, 0",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 9.5,
            new SolidColorBrush(Color.FromArgb(180, 31, 111, 235)));
        ctx.DrawText(fmt, new Point(origin.X + r + 2, origin.Y + 2));
    }

    static void DrawEmptyState(DrawingContext ctx, Size size, Typeface typeface, bool isObjectsMode)
    {
        var titleBrush = new SolidColorBrush(Color.FromArgb(180, 35, 38, 46));
        var bodyBrush  = new SolidColorBrush(Color.FromArgb(160, 90, 98, 112));

        // Stylised "empty floor plan" hero — three concentric squares
        // matching the welcome panel's hero. Centre x is shared with
        // the title text so the composition reads as one stack.
        double cx = size.Width * 0.5;
        double topY = size.Height * 0.5 - 110;
        var heroPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 31, 111, 235)), 1.6);
        for (int i = 0; i < 3; i++)
        {
            double s = 80 - i * 20;
            ctx.DrawRectangle(
                i == 0 ? new SolidColorBrush(Color.FromArgb(20, 31, 111, 235)) : null,
                heroPen,
                new Rect(cx - s * 0.5, topY - s * 0.5, s, s),
                6, 6);
        }

        string title = isObjectsMode
            ? "Add some rooms first"
            : "Sketch your first room";
        string body = isObjectsMode
            ? "Switch to Layout, drag a few tiles to select them, then press R."
            : "Click and drag empty tiles to mark a footprint, then press R to make it a room.";

        var titleFmt = new FormattedText(title,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold),
            18, titleBrush);
        var bodyFmt = new FormattedText(body,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 12.5, bodyBrush)
        { MaxTextWidth = System.Math.Min(size.Width - 80, 420) };

        double cy = size.Height * 0.5;
        double totalH = titleFmt.Height + 6 + bodyFmt.Height;
        double y = cy - totalH * 0.5;
        ctx.DrawText(titleFmt, new Point(cx - titleFmt.Width * 0.5, y));
        ctx.DrawText(bodyFmt, new Point(cx - bodyFmt.Width * 0.5,
                                        y + titleFmt.Height + 6));
    }

    /// <summary>Real 3D viewer (orbit / pan / zoom) — full
    /// software rasterizer with perspective projection, painter's-order
    /// depth sort, and Lambert shading. Implementation lives in
    /// <see cref="Iso3DRenderer"/> to keep this file focused on the
    /// top-down editor.</summary>
    static void RenderIso(DrawingContext ctx, MainWindowViewModel vm, Size size)
        => Iso3DRenderer.Render(ctx, vm, size);

    static void DrawLayoutMeasurements(
        DrawingContext ctx, MainWindowViewModel vm,
        Point origin, double tileSize, Typeface typeface)
    {
        if (!vm.ShowRoomDimensions
            && !vm.ShowFloorAreaLabels
            && !vm.ShowOpeningSizeLabels) return;
        if (vm.CurrentEnvironment is not { } env) return;

        Point ToScreen(System.Numerics.Vector2 worldXz)
        {
            double xTile = worldXz.X / vm.TileSize;
            double zTile = worldXz.Y / vm.TileSize;
            return new Point(
                origin.X + xTile * tileSize,
                origin.Y + (vm.GridLength - zTile) * tileSize);
        }

        var labelBrush = new SolidColorBrush(Color.FromRgb(45, 45, 60));
        var labelBg = new SolidColorBrush(Color.FromArgb(220, 250, 250, 252));
        var labelBorder = new Pen(new SolidColorBrush(Color.FromRgb(180, 180, 200)), 0.6);

        // Per-room: dimensions and / or floor area at the room's centre.
        if (vm.ShowRoomDimensions || vm.ShowFloorAreaLabels)
        {
            foreach (var room in env.Rooms)
            {
                if (room.Shape is not OpenApparatus.Topology.RectangleShape rect) continue;
                var b = room.GetWorldBounds();
                var centre = new System.Numerics.Vector2(
                    (b.Min.X + b.Max.X) * 0.5f,
                    (b.Min.Y + b.Max.Y) * 0.5f);
                var p = ToScreen(centre);
                double y = p.Y + 28; // below the room id label so they don't collide
                if (vm.ShowRoomDimensions)
                {
                    DrawLabel(ctx, typeface, labelBrush, labelBg, labelBorder,
                        $"{rect.Width:0.00} × {rect.Depth:0.00} m",
                        new Point(p.X, y));
                    y += 18;
                }
                if (vm.ShowFloorAreaLabels)
                {
                    DrawLabel(ctx, typeface, labelBrush, labelBg, labelBorder,
                        $"{rect.Width * rect.Depth:0.00} m²",
                        new Point(p.X, y));
                }
            }
        }

        // Per-opening size labels at each opening's centre point.
        if (vm.ShowOpeningSizeLabels)
        {
            foreach (var adj in env.Adjacencies)
            {
                if (adj.Passage is not OpenApparatus.Topology.Passage.Doorway dw) continue;
                var seg = adj.SharedSegment;
                foreach (var op in dw.Openings)
                {
                    var c = seg.Start + seg.Direction * (op.OffsetAlongEdge + op.Width * 0.5f);
                    var p = ToScreen(c);
                    string text = op.IsWindow
                        ? $"{op.Width:0.00}×{(op.Height - op.SillHeight):0.00} m"
                        : $"{op.Width:0.00}×{op.Height:0.00} m";
                    DrawLabel(ctx, typeface, labelBrush, labelBg, labelBorder, text, p);
                }
            }
        }
    }

    /// <summary>
    /// Translucent compliance hints. Door annular wedges show where an object
    /// satisfies the door-to-object distance + angle constraints; object
    /// exclusion discs show the min-spacing radius around the selected
    /// object; objects that violate any constraint get a red dashed ring.
    /// </summary>
    static void DrawConstraintOverlays(DrawingContext ctx, MainWindowViewModel vm, Point origin, double tileSize)
    {
        if (vm.CurrentEnvironment is not { } env) return;
        var c = vm.Constraints;
        Point ToScreen(System.Numerics.Vector2 worldXz)
        {
            double xTile = worldXz.X / vm.TileSize;
            double zTile = worldXz.Y / vm.TileSize;
            return new Point(
                origin.X + xTile * tileSize,
                origin.Y + (vm.GridLength - zTile) * tileSize);
        }
        double scale = tileSize / vm.TileSize;

        // Door zones — green-ish fill per door, only for the rooms in scope
        // (the same scope as measurements: every room or just the selected).
        if (c.DoorToObjectEnabled && (c.DoorToObjectMin > 0 || c.DoorToObjectMax > 0 || c.DoorAngleBandEnabled))
        {
            var rooms = new List<OpenApparatus.Topology.Room>(env.Rooms);

            var zoneFill = new SolidColorBrush(Color.FromArgb(60, 90, 200, 130));
            var zoneEdge = new Pen(new SolidColorBrush(Color.FromArgb(180, 60, 160, 100)), 0.8);

            foreach (var room in rooms)
            {
                foreach (var adj in env.Adjacencies)
                {
                    if (adj.RoomA != room && adj.RoomB != room) continue;
                    if (adj.Passage is not OpenApparatus.Topology.Passage.Doorway dw) continue;
                    bool isRoomA = adj.RoomA == room;
                    var seg = adj.SharedSegment;
                    var inward = isRoomA ? seg.Normal : -seg.Normal;
                    foreach (var op in dw.Openings)
                    {
                        if (op.IsWindow) continue;
                        var doorWorld = seg.Start + seg.Direction * (op.OffsetAlongEdge + op.Width * 0.5f);
                        var doorScr = ToScreen(doorWorld);
                        DrawDoorZone(ctx, zoneFill, zoneEdge, doorScr, inward, c, scale);
                    }
                }
            }
        }

        // Object exclusion radius around the currently-selected object.
        if (c.ObjectToObjectEnabled && c.ObjectToObjectMin > 0
            && vm.SelectedObjectIndex >= 0 && vm.SelectedObjectIndex < vm.Objects.Count)
        {
            var sel = vm.Objects[vm.SelectedObjectIndex];
            var p = ToScreen(new System.Numerics.Vector2(sel.Position.X, sel.Position.Z));
            double r = c.ObjectToObjectMin * scale;
            var fill = new SolidColorBrush(Color.FromArgb(45, 220, 90, 90));
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(190, 200, 70, 70)), 1.0)
            {
                DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0),
            };
            ctx.DrawEllipse(fill, edge, p, r, r);
        }

        // Violator rings — red dashed circle around any object that fails any
        // constraint, when HighlightViolations is on.
        if (c.HighlightViolations)
        {
            var bad = vm.GetViolatingObjectIndices();
            if (bad.Count > 0)
            {
                var ringPen = new Pen(new SolidColorBrush(Color.FromRgb(220, 40, 40)), 2.0)
                {
                    DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0),
                };
                double iconR = System.Math.Min(16.0, System.Math.Max(6.0, tileSize * 0.25));
                foreach (var idx in bad)
                {
                    var o = vm.Objects[idx];
                    var p = ToScreen(new System.Numerics.Vector2(o.Position.X, o.Position.Z));
                    ctx.DrawEllipse(null, ringPen, p, iconR + 4, iconR + 4);
                }
            }
        }
    }

    /// <summary>
    /// Draws the green annular wedge for one door's compliance zone — the
    /// region where an object satisfies the active door-to-object min/max
    /// distance and (optionally) the angle band. Sampled as a triangle fan
    /// in screen space.
    /// </summary>
    static void DrawDoorZone(
        DrawingContext ctx, IBrush fill, Pen edge,
        Point doorScr, System.Numerics.Vector2 inwardWorld,
        PlacementConstraints c, double scale)
    {
        var inwardScr = new Point(inwardWorld.X * scale, -inwardWorld.Y * scale);
        double iLen = System.Math.Sqrt(inwardScr.X * inwardScr.X + inwardScr.Y * inwardScr.Y);
        if (iLen < 1e-3) return;
        var inU = new Point(inwardScr.X / iLen, inwardScr.Y / iLen);
        var perpU = new Point(inU.Y, -inU.X);

        // Distance bounds in screen pixels. 0 means unset.
        double minR = c.DoorToObjectMin > 0 ? c.DoorToObjectMin * scale : 0;
        // If max is unset, fall back to a generous swathe (10 m or one
        // viewport's worth — whichever is smaller).
        double maxR = c.DoorToObjectMax > 0 ? c.DoorToObjectMax * scale : (10.0 * scale);

        // Angle band: half-spread on each side of the 0° axis. When the band
        // is disabled we sweep the full 180° forward arc (everything in
        // front of the door).
        double minDeg = c.DoorAngleBandEnabled ? System.Math.Max(0, c.DoorAngleMinDeg) : 0;
        double maxDeg = c.DoorAngleBandEnabled ? System.Math.Min(180, c.DoorAngleMaxDeg) : 180;
        if (maxDeg <= minDeg) return;
        double minRad = minDeg * System.Math.PI / 180.0;
        double maxRad = maxDeg * System.Math.PI / 180.0;

        // Build the polygon path: outer arc on the +side then -side, then
        // inner arc back. Use StreamGeometry to support fill + stroke.
        const int Steps = 24;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            // Start at outer arc, +side, near edge of band.
            var p0 = ZonePoint(doorScr, inU, perpU, +1, minRad, maxR);
            g.BeginFigure(p0, isFilled: true);
            for (int i = 1; i <= Steps; i++)
            {
                double t = i / (double)Steps;
                double a = minRad + (maxRad - minRad) * t;
                g.LineTo(ZonePoint(doorScr, inU, perpU, +1, a, maxR));
            }
            // Drop down to inner arc (still on +side).
            for (int i = Steps; i >= 0; i--)
            {
                double t = i / (double)Steps;
                double a = minRad + (maxRad - minRad) * t;
                g.LineTo(ZonePoint(doorScr, inU, perpU, +1, a, minR));
            }
            // Mirror onto -side (skipped if minRad == 0 to avoid a stray
            // line through the centre).
            if (minRad < 1e-3)
            {
                // Continue from the -side outer at maxRad and sweep back.
                for (int i = 0; i <= Steps; i++)
                {
                    double t = i / (double)Steps;
                    double a = minRad + (maxRad - minRad) * (1 - t);
                    g.LineTo(ZonePoint(doorScr, inU, perpU, -1, a, minR));
                }
                for (int i = 0; i <= Steps; i++)
                {
                    double t = i / (double)Steps;
                    double a = minRad + (maxRad - minRad) * t;
                    g.LineTo(ZonePoint(doorScr, inU, perpU, -1, a, maxR));
                }
            }
            else
            {
                // The +side ribbon is closed off; draw the -side as a
                // separate ribbon by closing this figure first.
                g.EndFigure(true);
                var s0 = ZonePoint(doorScr, inU, perpU, -1, minRad, maxR);
                g.BeginFigure(s0, isFilled: true);
                for (int i = 1; i <= Steps; i++)
                {
                    double t = i / (double)Steps;
                    double a = minRad + (maxRad - minRad) * t;
                    g.LineTo(ZonePoint(doorScr, inU, perpU, -1, a, maxR));
                }
                for (int i = Steps; i >= 0; i--)
                {
                    double t = i / (double)Steps;
                    double a = minRad + (maxRad - minRad) * t;
                    g.LineTo(ZonePoint(doorScr, inU, perpU, -1, a, minR));
                }
            }
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, edge, geo);
    }

    static Point ZonePoint(Point centre, Point inU, Point perpU, int sign, double angleRad, double radius)
    {
        double cs = System.Math.Cos(angleRad);
        double sn = System.Math.Sin(angleRad) * sign;
        return new Point(
            centre.X + (inU.X * cs + perpU.X * sn) * radius,
            centre.Y + (inU.Y * cs + perpU.Y * sn) * radius);
    }

    /// <summary>
    /// Draws the object-measurement overlay. Three independent sub-layers,
    /// each gated by its own VM toggle:
    ///   ShowDoorDistances  - door→object line + midpoint distance label.
    ///   ShowDoorAngles     - door→object angle arc + |angle| label + 0° tick.
    ///   ShowObjectDistances- object↔object line + midpoint distance label.
    /// </summary>
    static void DrawMeasurements(DrawingContext ctx, MainWindowViewModel vm, Point origin, double tileSize)
    {
        if (!vm.AnyObjectMeasurementsVisible) return;
        if (vm.CurrentEnvironment is not { } env) return;
        if (vm.Objects.Count == 0) return;

        // Always run across every room — fine-grained toggles drive what
        // actually renders. (The 'Selected room only' filter is gone.)
        var rooms = new List<OpenApparatus.Topology.Room>(env.Rooms);
        if (rooms.Count == 0) return;

        Point ToScreen(System.Numerics.Vector2 worldXz)
        {
            double xTile = worldXz.X / vm.TileSize;
            double zTile = worldXz.Y / vm.TileSize;
            return new Point(
                origin.X + xTile * tileSize,
                origin.Y + (vm.GridLength - zTile) * tileSize);
        }

        var typeface = new Typeface("Inter");
        var labelBrush = new SolidColorBrush(Color.FromRgb(45, 45, 60));
        var labelBg = new SolidColorBrush(Color.FromArgb(220, 250, 250, 252));
        var labelBorder = new Pen(new SolidColorBrush(Color.FromRgb(180, 180, 200)), 0.6);

        // Three colour states per line / arc:
        //   neutral   — no constraint of this kind is active, so we don't
        //               know what valid means. Use the original purple/teal.
        //   valid     — green; the constraint is active and this measurement
        //               falls inside it.
        //   invalid   — red and thicker, so violators stand out.
        var dashLong = new DashStyle(new[] { 4.0, 3.0 }, 0);
        var dashShort = new DashStyle(new[] { 2.5, 2.5 }, 0);
        var doorObjNeutral = new Pen(new SolidColorBrush(Color.FromArgb(210, 130, 80, 200)), 1.5) { DashStyle = dashLong };
        var doorObjValid   = new Pen(new SolidColorBrush(Color.FromArgb(220,  40, 160,  80)), 1.5) { DashStyle = dashLong };
        var doorObjInvalid = new Pen(new SolidColorBrush(Color.FromArgb(230, 200,  40,  40)), 2.8) { DashStyle = dashLong };
        var objObjNeutral  = new Pen(new SolidColorBrush(Color.FromArgb(180,  90, 130, 180)), 1.2) { DashStyle = dashShort };
        var objObjValid    = new Pen(new SolidColorBrush(Color.FromArgb(220,  40, 160,  80)), 1.5) { DashStyle = dashShort };
        var objObjInvalid  = new Pen(new SolidColorBrush(Color.FromArgb(230, 200,  40,  40)), 2.6) { DashStyle = dashShort };
        var arcNeutral = new Pen(new SolidColorBrush(Color.FromArgb(220, 130,  80, 200)), 1.3);
        var arcValid   = new Pen(new SolidColorBrush(Color.FromArgb(220,  40, 160,  80)), 1.5);
        var arcInvalid = new Pen(new SolidColorBrush(Color.FromArgb(230, 200,  40,  40)), 2.4);

        var c = vm.Constraints;

        foreach (var room in rooms)
        {
            // Doors of this room (door openings only — windows skipped).
            // Each door yields an entry-point world position and the inward
            // normal pointing into THIS room (flipped for RoomB).
            var doors = new List<(System.Numerics.Vector2 Pos, System.Numerics.Vector2 Normal)>();
            foreach (var adj in env.Adjacencies)
            {
                if (adj.RoomA != room && adj.RoomB != room) continue;
                if (adj.Passage is not OpenApparatus.Topology.Passage.Doorway dw) continue;
                bool isRoomA = adj.RoomA == room;
                var seg = adj.SharedSegment;
                var inward = isRoomA ? seg.Normal : -seg.Normal;
                foreach (var op in dw.Openings)
                {
                    if (op.IsWindow) continue;
                    var p = seg.Start + seg.Direction * (op.OffsetAlongEdge + op.Width * 0.5f);
                    doors.Add((p, inward));
                }
            }

            // Objects belonging to this room.
            var objs = new List<OpenApparatus.Studio.ViewModels.RoomObject>();
            foreach (var o in vm.Objects)
                if (o.OwningRoomId == room.Id) objs.Add(o);

            // 0° reference tick at every door — short line pointing inward
            // along the door's normal, with a small "0°" label so the angle
            // zero is explicit. Gated by the door-angles toggle since it's
            // an angle aid.
            var zeroPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 70, 70, 90)), 1.0)
            {
                DashStyle = new DashStyle(new[] { 2.0, 2.0 }, 0),
            };
            if (vm.ShowDoorAngles)
            foreach (var (doorPos, inward) in doors)
            {
                var doorScr = ToScreen(doorPos);
                double scale = tileSize / vm.TileSize;
                var inwardScr = new Point(inward.X * scale, -inward.Y * scale);
                double inLen = System.Math.Sqrt(inwardScr.X * inwardScr.X + inwardScr.Y * inwardScr.Y);
                if (inLen < 1e-3) continue;
                var inU = new Point(inwardScr.X / inLen, inwardScr.Y / inLen);
                double tickPx = System.Math.Min(28.0, System.Math.Max(14.0, tileSize * 0.20));
                var tickEnd = new Point(doorScr.X + inU.X * tickPx, doorScr.Y + inU.Y * tickPx);
                ctx.DrawLine(zeroPen, doorScr, tickEnd);
                var zeroFmt = new FormattedText("0°",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 10,
                    new SolidColorBrush(Color.FromRgb(80, 80, 100)));
                ctx.DrawText(zeroFmt,
                    new Point(tickEnd.X + inU.X * 2 - zeroFmt.Width * 0.5,
                              tickEnd.Y + inU.Y * 2 - zeroFmt.Height * 0.5));
            }

            // Door → object: per door, per object. Distance line + midpoint
            // label is the ShowDoorDistances layer; angle arc + |angle| label
            // is the ShowDoorAngles layer. Each is rendered independently
            // and is colour-graded by whether it satisfies the relevant
            // constraint (when that constraint is active).
            if (vm.ShowDoorDistances || vm.ShowDoorAngles)
            foreach (var (doorPos, inward) in doors)
            {
                var doorScr = ToScreen(doorPos);
                foreach (var obj in objs)
                {
                    var objPos = new System.Numerics.Vector2(obj.Position.X, obj.Position.Z);
                    var objScr = ToScreen(objPos);
                    var v = objPos - doorPos;
                    double dist = v.Length();

                    // Compute angle once — needed for either layer.
                    double forward = v.X * inward.X + v.Y * inward.Y;
                    double perp = v.X * inward.Y - v.Y * inward.X;
                    double angleRad = System.Math.Atan2(perp, forward);
                    double angleDeg = angleRad * 180.0 / System.Math.PI;
                    double absAngle = System.Math.Abs(angleDeg);

                    if (vm.ShowDoorDistances)
                    {
                        var pen = doorObjNeutral;
                        if (c.DoorToObjectEnabled && (c.DoorToObjectMin > 0 || c.DoorToObjectMax > 0))
                        {
                            bool ok = true;
                            if (c.DoorToObjectMin > 0 && dist < c.DoorToObjectMin) ok = false;
                            if (c.DoorToObjectMax > 0 && dist > c.DoorToObjectMax) ok = false;
                            pen = ok ? doorObjValid : doorObjInvalid;
                        }
                        ctx.DrawLine(pen, doorScr, objScr);
                    }

                    if (vm.ShowDoorAngles)
                    {
                        var pen = arcNeutral;
                        if (c.DoorAngleBandEnabled)
                        {
                            bool ok = absAngle >= c.DoorAngleMinDeg && absAngle <= c.DoorAngleMaxDeg;
                            pen = ok ? arcValid : arcInvalid;
                        }
                        DrawAngleArc(ctx, pen, doorScr, inward, angleRad, vm.TileSize, tileSize);
                    }

                    // Stack the distance and angle labels along the door→object
                    // line so multiple objects fanning off a single door don't
                    // pile their labels on top of each other near the arc.
                    if (vm.ShowDoorDistances || vm.ShowDoorAngles)
                    {
                        double mx = (doorScr.X + objScr.X) * 0.5;
                        double my = (doorScr.Y + objScr.Y) * 0.5;
                        double lx = objScr.X - doorScr.X;
                        double ly = objScr.Y - doorScr.Y;
                        double llen = System.Math.Sqrt(lx * lx + ly * ly);
                        // Screen-space perpendicular unit, used as the "stack
                        // axis" for the two labels. Falls back to vertical when
                        // the segment is degenerate.
                        double px = llen > 0.001 ? -ly / llen : 0.0;
                        double py = llen > 0.001 ?  lx / llen : 1.0;
                        const double off = 9.0;
                        if (vm.ShowDoorDistances && vm.ShowDoorAngles)
                        {
                            DrawDistanceLabel(ctx, typeface, $"{dist:0.00} m",
                                new Point(mx - px * off, my - py * off));
                            DrawAngleLabel(ctx, typeface, $"{absAngle:0}°",
                                new Point(mx + px * off, my + py * off));
                        }
                        else if (vm.ShowDoorDistances)
                        {
                            DrawDistanceLabel(ctx, typeface, $"{dist:0.00} m",
                                new Point(mx, my));
                        }
                        else
                        {
                            DrawAngleLabel(ctx, typeface, $"{absAngle:0}°",
                                new Point(mx, my));
                        }
                    }
                }
            }

            // Object ↔ object distances. Also colour-graded by the active
            // object-to-object min/max constraint.
            if (vm.ShowObjectDistances)
            for (int i = 0; i < objs.Count; i++)
                for (int j = i + 1; j < objs.Count; j++)
                {
                    var a = objs[i]; var b = objs[j];
                    var ap = new System.Numerics.Vector2(a.Position.X, a.Position.Z);
                    var bp = new System.Numerics.Vector2(b.Position.X, b.Position.Z);
                    var aScr = ToScreen(ap);
                    var bScr = ToScreen(bp);
                    double dist = (bp - ap).Length();

                    var pen = objObjNeutral;
                    if (c.ObjectToObjectEnabled && (c.ObjectToObjectMin > 0 || c.ObjectToObjectMax > 0))
                    {
                        bool ok = true;
                        if (c.ObjectToObjectMin > 0 && dist < c.ObjectToObjectMin) ok = false;
                        if (c.ObjectToObjectMax > 0 && dist > c.ObjectToObjectMax) ok = false;
                        pen = ok ? objObjValid : objObjInvalid;
                    }
                    ctx.DrawLine(pen, aScr, bScr);
                    DrawDistanceLabel(ctx, typeface, $"{dist:0.00} m",
                        new Point((aScr.X + bScr.X) * 0.5, (aScr.Y + bScr.Y) * 0.5));
                }
        }
    }

    /// <summary>Quarter-radius arc at the door from 0° (along the inward
    /// normal) sweeping to <paramref name="angleRad"/>. Sampled as line
    /// segments — simpler than getting StreamGeometry sweep direction right
    /// for arbitrary normals.</summary>
    static void DrawAngleArc(
        DrawingContext ctx, Pen pen, Point doorScr,
        System.Numerics.Vector2 inwardWorld, double angleRad,
        float tileSizeMetres, double tilePxSize)
    {
        // Radius: ~0.5 m in world space, but capped so we don't blow past the
        // line's reach when the object is very close to the door.
        double radiusPx = System.Math.Min(28.0, System.Math.Max(10.0, tilePxSize * 0.18));
        // Convert inward normal to a unit screen-space vector. World +Z maps
        // to screen -Y, so the y component flips sign.
        double scale = tilePxSize / tileSizeMetres;
        var inwardScr = new Point(inwardWorld.X * scale, -inwardWorld.Y * scale);
        double len = System.Math.Sqrt(inwardScr.X * inwardScr.X + inwardScr.Y * inwardScr.Y);
        if (len < 1e-3) return;
        var inwardU = new Point(inwardScr.X / len, inwardScr.Y / len);
        // Screen-space "left" perpendicular: rotate inward 90° CCW in screen
        // coords. The world cross-z used to compute the angle defines positive
        // = world-left, which on screen (Y-flipped) is also a 90° rotation
        // but in the opposite sense. Adjust so positive angles arc toward the
        // same side the cross-product chose.
        var perpU = new Point(inwardU.Y, -inwardU.X);

        const int Steps = 18;
        var prev = new Point(doorScr.X + inwardU.X * radiusPx, doorScr.Y + inwardU.Y * radiusPx);
        for (int i = 1; i <= Steps; i++)
        {
            double t = i / (double)Steps;
            double a = angleRad * t;
            double cs = System.Math.Cos(a);
            double sn = System.Math.Sin(a);
            var pt = new Point(
                doorScr.X + (inwardU.X * cs + perpU.X * sn) * radiusPx,
                doorScr.Y + (inwardU.Y * cs + perpU.Y * sn) * radiusPx);
            ctx.DrawLine(pen, prev, pt);
            prev = pt;
        }
    }

    /// <summary>Draws a small text label with a rounded translucent
    /// background centred at <paramref name="centre"/>.</summary>
    static void DrawLabel(
        DrawingContext ctx, Typeface typeface, IBrush textBrush, IBrush bg, Pen border,
        string text, Point centre)
    {
        var fmt = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 11, textBrush);
        const double padX = 4.0, padY = 1.5;
        var rect = new Rect(
            centre.X - fmt.Width * 0.5 - padX,
            centre.Y - fmt.Height * 0.5 - padY,
            fmt.Width + padX * 2,
            fmt.Height + padY * 2);
        ctx.DrawRectangle(bg, border, rect, 3, 3);
        ctx.DrawText(fmt, new Point(rect.X + padX, rect.Y + padY));
    }

    /// <summary>Distance-style label: rectangular pill, blue-tinted background
    /// and dark-blue text. Used for door→object and object↔object distances —
    /// distinct from angle labels at a glance. Public so the legend bar can
    /// render a matching example.</summary>
    public static void DrawDistanceLabel(
        DrawingContext ctx, Typeface typeface, string text, Point centre)
    {
        var textBrush = new SolidColorBrush(Color.FromRgb(20, 50, 105));
        var bg = new SolidColorBrush(Color.FromArgb(235, 224, 236, 252));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(110, 150, 210)), 0.8);
        var fmt = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 11, textBrush);
        const double padX = 5.0, padY = 1.5;
        var rect = new Rect(
            centre.X - fmt.Width * 0.5 - padX,
            centre.Y - fmt.Height * 0.5 - padY,
            fmt.Width + padX * 2,
            fmt.Height + padY * 2);
        ctx.DrawRectangle(bg, border, rect, 3, 3);
        ctx.DrawText(fmt, new Point(rect.X + padX, rect.Y + padY));
    }

    /// <summary>Angle-style label: fully rounded pill, amber-tinted background
    /// and dark-amber text. Used for door→object angles. The pill ends and
    /// warm palette make it visually distinct from rectangular distance
    /// labels even when both are stacked along the same line.</summary>
    public static void DrawAngleLabel(
        DrawingContext ctx, Typeface typeface, string text, Point centre)
    {
        var textBrush = new SolidColorBrush(Color.FromRgb(80, 40, 5));
        var bg = new SolidColorBrush(Color.FromArgb(235, 255, 238, 210));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(200, 150, 75)), 0.8);
        var fmt = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, 11, textBrush);
        const double padX = 7.0, padY = 2.0;
        var rect = new Rect(
            centre.X - fmt.Width * 0.5 - padX,
            centre.Y - fmt.Height * 0.5 - padY,
            fmt.Width + padX * 2,
            fmt.Height + padY * 2);
        double r = rect.Height * 0.5; // fully-rounded ends
        ctx.DrawRectangle(bg, border, rect, r, r);
        ctx.DrawText(fmt, new Point(rect.X + padX, rect.Y + padY));
    }

    static void DrawObjects(DrawingContext ctx, MainWindowViewModel vm, Point origin, double tileSize)
    {
        if (vm.Objects.Count == 0) return;
        bool dim = !vm.IsObjectsMode;
        for (int i = 0; i < vm.Objects.Count; i++)
        {
            var o = vm.Objects[i];
            var type = vm.GetObjectType(o.Slot);
            if (type is null) continue;
            var screen = WorldXzToScreen(
                new System.Numerics.Vector2(o.Position.X, o.Position.Z),
                origin, tileSize, vm);
            byte alpha = (byte)(dim ? 110 : 255);
            var fill = new SolidColorBrush(Color.FromArgb(alpha,
                (byte)(type.Color.X * 255), (byte)(type.Color.Y * 255), (byte)(type.Color.Z * 255)));
            var border = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 30, 30, 40)), 1.2);
            // Multi-select: any object in vm.SelectedObjectIndices gets a
            // ring; the primary (single) selection still drives the
            // inspector binding.
            bool selected = vm.IsObjectsMode &&
                (vm.SelectedObjectIndices.Contains(i) || i == vm.SelectedObjectIndex);
            // 2D icon footprint: roughly 1/3 of a tile, capped at 16 px.
            double iconR = System.Math.Min(16.0, System.Math.Max(6.0, tileSize * 0.25));
            DrawObjectIcon(ctx, type.Shape, screen, iconR, fill, border);
            if (selected)
            {
                // Brand-blue 2-px ring; same treatment as wall + tile selection.
                ctx.DrawEllipse(null,
                    new Pen(new SolidColorBrush(Color.FromRgb(31, 111, 235)), 2.0)
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
        // Cache for the VM's FrameSelection / fit commands.
        vm.ReportViewportSize(size.Width - EditorPadding * 2, size.Height - EditorPadding * 2);
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
