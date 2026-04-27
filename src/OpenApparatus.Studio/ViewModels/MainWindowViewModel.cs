using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenApparatus;
using OpenApparatus.Studio.Services;
using OpenApparatus.Topology;
using OpenApparatus.Topology.Assigners;
using OpenApparatus.Topology.Generators;

namespace OpenApparatus.Studio.ViewModels;

/// <summary>
/// View-model for the multi-room environment editor. Owns the authored tile grid
/// (which tile belongs to which room, or -1 for empty) plus visual / generation
/// parameters. The materialized <see cref="MultiRoomEnvironment"/> is rebuilt on
/// every grid change.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    // ---- Grid + authoring state ----

    [ObservableProperty] int _gridWidth = 8;
    [ObservableProperty] int _gridLength = 6;
    [ObservableProperty] float _tileSize = 3.5f;

    /// <summary>
    /// roomId per tile; -1 = empty. Indexing: <c>RoomGrid[x, z]</c>. Always
    /// <c>GridWidth × GridLength</c> in size.
    /// </summary>
    public int[,] RoomGrid { get; private set; }

    /// <summary>Tiles the user has selected but not yet committed to a room.</summary>
    public HashSet<(int x, int z)> SelectedTiles { get; } = new();

    int _nextRoomId = 0;

    /// <summary>
    /// Per-wall passage overrides remembered across grid rebuilds. Without this,
    /// every Rebuild() resets all walls to Closed (since FromGrid produces a fresh
    /// MultiRoomEnvironment with default passages), losing user-placed doorways.
    ///
    /// Keys identify a wall by its segment midpoint, quantized to millimeters.
    /// This means a door survives outer→internal transitions: when a new room is
    /// placed on the far side of an existing outer wall, the wall's physical
    /// location stays the same and the override applies to the new internal
    /// adjacency. Room-pair-based keys would lose the door in that case.
    /// </summary>
    /// <summary>
    /// Per-wall passage overrides remembered across grid rebuilds. The Start
    /// position is captured alongside the passage so that when the wall's
    /// segment direction flips on a rebuild (which happens whenever the lower-
    /// id RoomA changes — e.g. after a renumber), each opening's offset can
    /// be flipped to keep its world position fixed.
    /// </summary>
    readonly Dictionary<(int, int), (Passage Passage, System.Numerics.Vector2 Start)> _passageOverrides = new();

    /// <summary>
    /// Per-wall color overrides keyed by (roomId, segment-midpoint-mm). A wall
    /// shared between two rooms can carry two distinct colors — one per side.
    /// Stored as RGB in 0..1; default gray is used when no override is set.
    /// </summary>
    readonly Dictionary<(int RoomId, int MidX, int MidZ), System.Numerics.Vector3> _wallColors = new();

    static (int, int) PassageKey(Adjacency adj)
    {
        var mid = adj.SharedSegment.Midpoint;
        return (
            (int)System.Math.Round(mid.X * 1000),
            (int)System.Math.Round(mid.Y * 1000));
    }

    void RememberPassage(Adjacency adj)
        => _passageOverrides[PassageKey(adj)] = (adj.Passage, adj.SharedSegment.Start);

    /// <summary>Read-only access to per-wall color overrides (for view rendering).</summary>
    public IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), System.Numerics.Vector3> WallColors => _wallColors;

    /// <summary>
    /// Adjacencies the currently selected room participates in, in build order.
    /// Empty when no room is selected (or when CurrentEnvironment hasn't been
    /// rebuilt for the selected id).
    /// </summary>
    public IReadOnlyList<Adjacency> SelectedRoomAdjacencies
    {
        get
        {
            var list = new List<Adjacency>();
            if (CurrentEnvironment is null || SelectedRoomId < 0) return list;
            foreach (var adj in CurrentEnvironment.Adjacencies)
                if (adj.RoomA.Id == SelectedRoomId || (adj.RoomB?.Id == SelectedRoomId))
                    list.Add(adj);
            return list;
        }
    }

    /// <summary>Returns the index of the opening on <paramref name="adj"/> whose span
    /// contains <paramref name="alongMeters"/>, or -1 if none.</summary>
    static int FindOpeningAt(Adjacency adj, float alongMeters)
    {
        if (adj.Passage is not Passage.Doorway dw) return -1;
        const float EPS = 1e-3f;
        for (int i = 0; i < dw.Openings.Count; i++)
        {
            var op = dw.Openings[i];
            if (alongMeters >= op.OffsetAlongEdge - EPS &&
                alongMeters <= op.OffsetAlongEdge + op.Width + EPS)
                return i;
        }
        return -1;
    }

    /// <summary>Replace the opening at <see cref="SelectedOpeningIndex"/> with the
    /// result of <paramref name="transform"/>. No-op when no opening is selected.</summary>
    public void UpdateSelectedOpening(System.Func<Opening, Opening> transform)
    {
        if (!HasSelectedOpening || SelectedAdjacency is null) return;
        var dw = (Passage.Doorway)SelectedAdjacency.Passage;
        var openings = new List<Opening>(dw.Openings);
        var newOp = transform(openings[SelectedOpeningIndex]);
        // Keep within wall length.
        var seg = SelectedAdjacency.SharedSegment;
        if (newOp.OffsetAlongEdge + newOp.Width > seg.Length + 1e-3f)
        {
            StatusMessage = $"Can't fit a {newOp.Width:F2}m opening — wall is only {seg.Length:F2}m.";
            return;
        }
        PushUndo();
        openings[SelectedOpeningIndex] = newOp;
        SelectedAdjacency.Passage = new Passage.Doorway(openings);
        RememberPassage(SelectedAdjacency);
        EditVersion++;
        OnPropertyChanged(nameof(SelectedOpening));
    }

    /// <summary>Effective wall color for a room's side of an adjacency, accounting for
    /// the room's multi-color toggle. Falls back to the default neutral when nothing
    /// has been set.</summary>
    public System.Numerics.Vector3 EffectiveWallColor(int roomId, Adjacency adj)
    {
        var key = GltfExporter.WallColorKey(roomId, adj);
        if (IsRoomMultiColor(roomId) && _wallColors.TryGetValue(key, out var perWall))
            return perWall;
        if (_roomSingleWallColors.TryGetValue(roomId, out var single))
            return single;
        return new System.Numerics.Vector3(0.78f, 0.78f, 0.80f);
    }

    /// <summary>Sets the color for one room's side of the given wall. RGB in 0..1.</summary>
    public void SetWallColor(int roomId, Adjacency adj, System.Numerics.Vector3 rgb)
    {
        PushUndo();
        _wallColors[GltfExporter.WallColorKey(roomId, adj)] = rgb;
        EditVersion++;
    }

    /// <summary>Removes any color override for one room's side of the given wall.</summary>
    public void ClearWallColor(int roomId, Adjacency adj)
    {
        PushUndo();
        _wallColors.Remove(GltfExporter.WallColorKey(roomId, adj));
        EditVersion++;
    }

    // ---- Visual / generation parameters ----

    [ObservableProperty] float _wallThickness = 0.2f;
    [ObservableProperty] float _wallHeight = 3f;
    [ObservableProperty] float _doorWidth = 1.2f;
    [ObservableProperty] float _doorHeight = 2.2f;
    [ObservableProperty] float _windowWidth = 1.2f;
    [ObservableProperty] float _windowHeight = 2.2f;
    [ObservableProperty] float _windowSillHeight = 1.0f;

    /// <summary>
    /// id of the currently selected room (-1 = none). Set when the user clicks a
    /// tile that already belongs to a room, or right after a fresh CreateRoom.
    /// Drives the right-side room editor panel.
    /// </summary>
    [ObservableProperty] int _selectedRoomId = -1;

    /// <summary>
    /// Index of the currently selected opening within <see cref="SelectedAdjacency"/>'s
    /// passage (-1 = none). Set when the user clicks a wall anchor that lands inside
    /// an existing opening, or right after toggling a new door / window in. Drives
    /// the right-side opening editor panel — when this is &gt;=0 the panel shows
    /// per-opening size + hinge / swing controls instead of room appearance.
    /// </summary>
    [ObservableProperty] int _selectedOpeningIndex = -1;

    public bool HasSelectedOpening =>
        SelectedOpeningIndex >= 0 && SelectedAdjacency?.Passage is Passage.Doorway dw
        && SelectedOpeningIndex < dw.Openings.Count;

    public Opening? SelectedOpening
        => HasSelectedOpening
            ? ((Passage.Doorway)SelectedAdjacency!.Passage).Openings[SelectedOpeningIndex]
            : null;

    partial void OnSelectedOpeningIndexChanged(int value)
        => OnPropertyChanged(nameof(HasSelectedOpening));

    /// <summary>Defaults applied to every new room's floor / ceiling. Editing them
    /// does NOT retroactively change existing rooms — those keep whatever colors
    /// they had at creation (or after manual override).</summary>
    [ObservableProperty] System.Numerics.Vector3 _defaultFloorColor   = new(0.92f, 0.92f, 0.93f);
    [ObservableProperty] System.Numerics.Vector3 _defaultCeilingColor = new(0.92f, 0.92f, 0.90f);

    /// <summary>Which surface the editor view recolors tiles by — Floor shows
    /// each room's floor colour, Ceiling shows each room's ceiling colour, and
    /// Objects switches the editor into the object-placement mode.</summary>
    public enum ViewSurface { Floor, Ceiling, Objects }
    [ObservableProperty] ViewSurface _viewMode = ViewSurface.Floor;

    public bool IsObjectsMode => ViewMode == ViewSurface.Objects;

    /// <summary>Global subdivision: every tile is divided into N×N sub-cells
    /// when placing or snapping objects. 1 = no subdivision (objects sit at
    /// tile centres), 2 = 2×2 per tile, …, up to 8.</summary>
    [ObservableProperty] int _gridSubdivision = 1;
    partial void OnGridSubdivisionChanged(int value) => EditVersion++;

    /// <summary>Default Y coordinate (metres above the floor) used when an
    /// object is first placed. The 3D viewer will let users edit Y per-object;
    /// for now, every new object starts here.</summary>
    [ObservableProperty] float _defaultObjectY = 0.5f;

    /// <summary>The selected sub-cell in Objects mode, or null. fineX / fineZ
    /// run 0..GridSubdivision-1 within the parent tile.</summary>
    [ObservableProperty] (int TileX, int TileZ, int FineX, int FineZ)? _selectedSubCell;
    partial void OnSelectedSubCellChanged((int TileX, int TileZ, int FineX, int FineZ)? value) => EditVersion++;

    /// <summary>Index of the currently selected RoomObject in <see cref="Objects"/>,
    /// or -1 when nothing is selected.</summary>
    [ObservableProperty] int _selectedObjectIndex = -1;
    partial void OnSelectedObjectIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedObject));
        EditVersion++;
    }

    public RoomObject? SelectedObject =>
        SelectedObjectIndex >= 0 && SelectedObjectIndex < _objects.Count
            ? _objects[SelectedObjectIndex] : null;

    readonly List<RoomObject> _objects = new();
    public IReadOnlyList<RoomObject> Objects => _objects;

    /// <summary>
    /// Editable object types. The user starts with one default and uses
    /// <see cref="AddObjectTypeCommand"/> to grow the list. Hotkey 1..N
    /// places an instance of the matching type.
    /// </summary>
    readonly System.Collections.ObjectModel.ObservableCollection<ObjectType> _objectTypes = new();
    public System.Collections.ObjectModel.ObservableCollection<ObjectType> ObjectTypes => _objectTypes;

    /// <summary>1-based lookup matching <see cref="RoomObject.Slot"/>. Returns
    /// null when the slot is out of range (e.g. hotkey 7 was pressed but only
    /// 4 types exist).</summary>
    public ObjectType? GetObjectType(int slot)
    {
        int idx = slot - 1;
        if (idx < 0 || idx >= _objectTypes.Count) return null;
        return _objectTypes[idx];
    }

    [RelayCommand]
    void AddObjectType()
    {
        PushUndo();
        int id = _objectTypes.Count + 1;
        var shape = (ObjectShape)((id - 1) % System.Enum.GetValues<ObjectShape>().Length);
        // Pick a perceptually-distinct hue so consecutive Add presses don't
        // produce visually similar swatches. Same HSV walk used for room hues.
        var rgb = HsvToRgb((id * 137.5) % 360, 0.55f, 0.92f);
        _objectTypes.Add(new ObjectType
        {
            Name = $"Object {id}",
            Shape = shape,
            Color = rgb,
            Size = 0.30f,
        });
        StatusMessage = $"Added object type {id}.";
        EditVersion++;
    }

    /// <summary>Remove the object type at index <paramref name="index"/> (0-based).
    /// Refuses to remove the last remaining type. Any RoomObject pointing at the
    /// removed type is dropped; later types shift down by one slot.</summary>
    public void RemoveObjectType(int index)
    {
        if (index < 0 || index >= _objectTypes.Count) return;
        if (_objectTypes.Count <= 1)
        {
            StatusMessage = "Need at least one object type.";
            return;
        }
        PushUndo();
        int removedSlot = index + 1;
        _objectTypes.RemoveAt(index);
        // Drop instances using the removed type, decrement instances above it.
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (_objects[i].Slot == removedSlot) _objects.RemoveAt(i);
            else if (_objects[i].Slot > removedSlot) _objects[i].Slot--;
        }
        if (SelectedObjectIndex >= _objects.Count) SelectedObjectIndex = -1;
        StatusMessage = $"Removed object type {removedSlot}.";
        EditVersion++;
    }

    /// <summary>Called from the inspector after editing a type's name / shape /
    /// color in place. Repaints the editor view to reflect the change.</summary>
    public void OnEditedObjectType()
    {
        EditVersion++;
    }

    /// <summary>Sub-cell side length in metres at the current subdivision.</summary>
    public float SubCellSize => TileSize / System.Math.Max(1, GridSubdivision);

    /// <summary>World position of the centre of a sub-cell.</summary>
    public System.Numerics.Vector2 SubCellCenter(int tileX, int tileZ, int fineX, int fineZ)
    {
        float s = SubCellSize;
        return new System.Numerics.Vector2(
            tileX * TileSize + (fineX + 0.5f) * s,
            tileZ * TileSize + (fineZ + 0.5f) * s);
    }

    /// <summary>The room id at <paramref name="worldPos"/>, or -1 if no tile
    /// owns that point. Used to assign owning rooms when objects are placed
    /// or snapped.</summary>
    public int RoomIdAtWorld(System.Numerics.Vector2 worldPos)
    {
        int tx = (int)System.Math.Floor(worldPos.X / TileSize);
        int tz = (int)System.Math.Floor(worldPos.Y / TileSize);
        if (tx < 0 || tx >= GridWidth || tz < 0 || tz >= GridLength) return -1;
        return RoomGrid[tx, tz];
    }

    /// <summary>
    /// Place an object of <paramref name="slot"/> at the centre of
    /// <see cref="SelectedSubCell"/>, owned by the room that contains that
    /// sub-cell. No-ops when no sub-cell is selected, the slot is out of
    /// range, the cell sits in empty space, or an object of the same slot is
    /// already at this sub-cell centre.
    /// </summary>
    public void PlaceObjectAtSelectedSubCell(int slot)
    {
        var typeForPlacement = GetObjectType(slot);
        if (typeForPlacement is null) return;
        if (SelectedSubCell is not { } sc) return;
        var center2 = SubCellCenter(sc.TileX, sc.TileZ, sc.FineX, sc.FineZ);
        // RoomIdAtWorld returns -1 when the sub-cell sits on empty space; we
        // treat that as "outside any room" rather than refusing the placement
        // — empty tiles are still valid object territory and the JSON / glTF
        // exports group those objects under an Outside container.
        int roomId = RoomIdAtWorld(center2);
        // No-op if the same slot is already at the same sub-cell centre.
        const float EPS = 1e-3f;
        foreach (var o in _objects)
        {
            if (o.Slot != slot) continue;
            if (System.Math.Abs(o.Position.X - center2.X) < EPS &&
                System.Math.Abs(o.Position.Z - center2.Y) < EPS)
            {
                StatusMessage = $"Slot {slot} already placed at this sub-cell.";
                return;
            }
        }
        PushUndo();
        var obj = new RoomObject
        {
            OwningRoomId = roomId,
            Slot = slot,
            Position = new System.Numerics.Vector3(center2.X, DefaultObjectY, center2.Y),
            Rotation = 0f,
        };
        _objects.Add(obj);
        SelectedObjectIndex = _objects.Count - 1;
        StatusMessage = roomId >= 0
            ? $"Placed {typeForPlacement.Name} in Room {roomId}."
            : $"Placed {typeForPlacement.Name} (outside any room).";
        EditVersion++;
    }

    /// <summary>Called from the inspector after an in-place mutation of the
    /// selected RoomObject (X/Y/Z/rotation spinners) so the editor view
    /// repaints. We can't use ObservableProperty on RoomObject because it's a
    /// reference type held by index in a list — bumping EditVersion is the
    /// signal everything else listens to.</summary>
    public void OnEditedSelectedObject()
    {
        EditVersion++;
        OnPropertyChanged(nameof(SelectedObject));
    }

    /// <summary>Delete the currently selected object.</summary>
    [RelayCommand]
    void DeleteSelectedObject()
    {
        if (SelectedObjectIndex < 0 || SelectedObjectIndex >= _objects.Count) return;
        PushUndo();
        _objects.RemoveAt(SelectedObjectIndex);
        SelectedObjectIndex = -1;
        StatusMessage = "Object deleted.";
        EditVersion++;
    }

    /// <summary>
    /// Snap every object's X/Z position to the centre of its containing
    /// sub-cell at the current <see cref="GridSubdivision"/>. Y is preserved
    /// — height is independent of the grid. Useful after changing the
    /// subdivision so older objects align with the new grain.
    /// </summary>
    [RelayCommand]
    void SnapObjectsToGrid()
    {
        if (_objects.Count == 0)
        {
            StatusMessage = "No objects to snap.";
            return;
        }
        PushUndo();
        float s = SubCellSize;
        int touched = 0;
        foreach (var o in _objects)
        {
            float fx = o.Position.X / s;
            float fz = o.Position.Z / s;
            float snappedX = (System.MathF.Floor(fx) + 0.5f) * s;
            float snappedZ = (System.MathF.Floor(fz) + 0.5f) * s;
            if (System.MathF.Abs(snappedX - o.Position.X) > 1e-4f ||
                System.MathF.Abs(snappedZ - o.Position.Z) > 1e-4f)
            {
                o.Position = new System.Numerics.Vector3(snappedX, o.Position.Y, snappedZ);
                touched++;
            }
            // Reassign owning room based on the new position. -1 = outside any
            // room, which is a valid state — the export groups these under an
            // Outside container.
            o.OwningRoomId = RoomIdAtWorld(new System.Numerics.Vector2(snappedX, snappedZ));
        }
        StatusMessage = touched > 0
            ? $"Snapped {touched} object(s) to the {GridSubdivision}× sub-grid."
            : "Objects already aligned to the current sub-grid.";
        EditVersion++;
    }

    /// <summary>When on, the editor draws a path overlay through every
    /// traversable passage (open or doorway), anchoring each leg to the
    /// centres of the tile cells on either side of the opening.</summary>
    [ObservableProperty] bool _showPaths;
    partial void OnShowPathsChanged(bool value) => EditVersion++;

    /// <summary>Colour used by the path overlay. Default is a green that reads
    /// against most floor / wall tones.</summary>
    [ObservableProperty] System.Numerics.Vector3 _pathColor = new(0.125f, 0.66f, 0.376f);
    partial void OnPathColorChanged(System.Numerics.Vector3 value) => EditVersion++;

    /// <summary>Editor-only opacity multiplier for the interior wall borders.
    /// 1.0 = fully opaque (default), 0 = invisible. Does NOT alter the colours
    /// used by the glTF export — purely a viewport aid for spotting doors and
    /// windows behind the coloured ribbons.</summary>
    [ObservableProperty] double _wallBorderOpacity = 1.0;

    partial void OnWallBorderOpacityChanged(double value) => EditVersion++;

    /// <summary>Editor zoom factor. 1.0 = fit-to-view; >1 zooms in, &lt;1 zooms out.
    /// The viewport renders ZoomFactor× the auto-fit tile size.</summary>
    [ObservableProperty] double _zoomFactor = 1.0;
    /// <summary>Pan offset added to the viewport origin in screen pixels.</summary>
    [ObservableProperty] double _panOffsetX;
    [ObservableProperty] double _panOffsetY;

    public const double MinZoom = 0.5;
    public const double MaxZoom = 8.0;

    partial void OnZoomFactorChanged(double value) => EditVersion++;
    partial void OnPanOffsetXChanged(double value) => EditVersion++;
    partial void OnPanOffsetYChanged(double value) => EditVersion++;

    /// <summary>Reset zoom + pan to the default fit-to-view layout.</summary>
    [RelayCommand]
    void ResetView()
    {
        ZoomFactor = 1.0;
        PanOffsetX = 0;
        PanOffsetY = 0;
    }

    /// <summary>
    /// Compute zoom + pan so the given room fits centered in the viewport with a
    /// small empty buffer of tiles on each side. Width / height are the editor's
    /// available pixel area minus its padding (16 px on each side).
    /// </summary>
    public void FocusOnRoom(int roomId, double availW, double availH)
    {
        if (availW <= 0 || availH <= 0 || GridWidth <= 0 || GridLength <= 0) return;

        int xMin = int.MaxValue, xMax = int.MinValue, zMin = int.MaxValue, zMax = int.MinValue;
        bool any = false;
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
                if (RoomGrid[x, z] == roomId)
                {
                    any = true;
                    if (x < xMin) xMin = x;
                    if (x > xMax) xMax = x;
                    if (z < zMin) zMin = z;
                    if (z > zMax) zMax = z;
                }
        if (!any) return;

        const double Buffer = 1.5; // tiles of empty space around the room
        double bufferedW = (xMax - xMin + 1) + Buffer * 2;
        double bufferedD = (zMax - zMin + 1) + Buffer * 2;

        // Fit-to-view tile size for the WHOLE grid (matches ComputeLayout).
        double autoFit = System.Math.Min(availW / GridWidth, availH / GridLength);
        if (autoFit <= 0) return;

        // Tile size that makes the buffered room fit exactly.
        double targetTile = System.Math.Min(availW / bufferedW, availH / bufferedD);
        double zoom = System.Math.Clamp(targetTile / autoFit, MinZoom, MaxZoom);

        // Pan so the room's center sits in the viewport center. With zoom in
        // place, the actual tile size is autoFit * zoom; the math mirrors what
        // ComputeLayout does so the result is exactly the room-centered layout.
        double tile = autoFit * zoom;
        double cx = (xMin + xMax + 1) * 0.5;
        double cz = (zMin + zMax + 1) * 0.5;

        ZoomFactor = zoom;
        PanOffsetX = tile * (GridWidth * 0.5 - cx);
        PanOffsetY = tile * (cz - GridLength * 0.5);
    }

    partial void OnViewModeChanged(ViewSurface value)
    {
        // Leaving Objects mode clears any active sub-cell / object selection
        // so the right panel collapses cleanly back to the room editor.
        if (value != ViewSurface.Objects)
        {
            SelectedSubCell = null;
            SelectedObjectIndex = -1;
        }
        EditVersion++;
        OnPropertyChanged(nameof(IsObjectsMode));
    }

    /// <summary>Auto-generated room color used as both the visible tile fill and
    /// the seed for the room's wall color when it is first created. The same
    /// HSV walk used by the editor view, exposed here so the VM can decide on
    /// a wall color before the view ever runs.</summary>
    public static System.Numerics.Vector3 RoomColorRgb(int roomId)
    {
        double h = (roomId * 137.5) % 360.0;
        return HsvToRgb(h, 0.45f, 0.92f);
    }

    static System.Numerics.Vector3 HsvToRgb(double h, float s, float v)
    {
        double c = v * s;
        double hp = h / 60.0;
        double x = c * (1 - System.Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;
        if (hp < 1)      { r = c; g = x; }
        else if (hp < 2) { r = x; g = c; }
        else if (hp < 3) { g = c; b = x; }
        else if (hp < 4) { g = x; b = c; }
        else if (hp < 5) { r = x; b = c; }
        else             { r = c; b = x; }
        double m = v - c;
        return new System.Numerics.Vector3((float)(r + m), (float)(g + m), (float)(b + m));
    }

    public bool HasSelectedRoom => SelectedRoomId >= 0;
    public string SelectedRoomTitle => SelectedRoomId >= 0 ? $"Room {SelectedRoomId}" : "";

    partial void OnSelectedRoomIdChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelectedRoom));
        OnPropertyChanged(nameof(SelectedRoomTitle));
    }

    // ---- Per-room appearance state ----
    // Storage is keyed by roomId, never by Room object — rebuilds throw away the
    // Room instances but keep the same ids, so user selections survive grid edits.

    readonly Dictionary<int, string> _roomNames = new();
    public IReadOnlyDictionary<int, string> RoomNames => _roomNames;
    public string GetRoomName(int roomId)
        => _roomNames.TryGetValue(roomId, out var n) ? n : string.Empty;
    public void SetRoomName(int roomId, string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) _roomNames.Remove(roomId);
        else _roomNames[roomId] = trimmed;
        EditVersion++;
    }

    readonly Dictionary<int, System.Numerics.Vector3> _roomFloorColors = new();
    readonly Dictionary<int, System.Numerics.Vector3> _roomCeilingColors = new();
    readonly Dictionary<int, System.Numerics.Vector3> _roomSingleWallColors = new();
    readonly HashSet<int> _multiColorRoomIds = new();

    public IReadOnlyDictionary<int, System.Numerics.Vector3> RoomFloorColors => _roomFloorColors;
    public IReadOnlyDictionary<int, System.Numerics.Vector3> RoomCeilingColors => _roomCeilingColors;
    public IReadOnlyDictionary<int, System.Numerics.Vector3> RoomSingleWallColors => _roomSingleWallColors;
    public IReadOnlyCollection<int> MultiColorRoomIds => _multiColorRoomIds;

    public bool IsRoomMultiColor(int roomId) => _multiColorRoomIds.Contains(roomId);

    public void SetRoomMultiColor(int roomId, bool on)
    {
        PushUndo();
        if (on) _multiColorRoomIds.Add(roomId);
        else _multiColorRoomIds.Remove(roomId);
        EditVersion++;
    }

    public void SetRoomFloorColor(int roomId, System.Numerics.Vector3 rgb)
    {
        PushUndo();
        _roomFloorColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomFloorColor(int roomId)
    {
        PushUndo();
        _roomFloorColors.Remove(roomId); EditVersion++;
    }

    public void SetRoomCeilingColor(int roomId, System.Numerics.Vector3 rgb)
    {
        PushUndo();
        _roomCeilingColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomCeilingColor(int roomId)
    {
        PushUndo();
        _roomCeilingColors.Remove(roomId); EditVersion++;
    }

    public void SetRoomSingleWallColor(int roomId, System.Numerics.Vector3 rgb)
    {
        PushUndo();
        _roomSingleWallColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomSingleWallColor(int roomId)
    {
        PushUndo();
        _roomSingleWallColors.Remove(roomId); EditVersion++;
    }

    [ObservableProperty] MultiRoomEnvironment? _currentEnvironment;
    [ObservableProperty] string _statusMessage = "Click and drag to select tiles, then click 'Create Room'.";

    /// <summary>Bumped after every grid mutation so views can re-render.</summary>
    [ObservableProperty] int _editVersion;

    // ---- Undo / redo state. Snapshots are deep copies of authored state; we
    // push one before every user-initiated mutation. Selection / view state is
    // excluded so undo doesn't move the camera.
    readonly Stack<Snapshot> _undoStack = new();
    readonly Stack<Snapshot> _redoStack = new();
    const int UndoLimit = 100;
    bool _suppressUndoCapture;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Capture the current state into the undo stack and clear redo.
    /// Call this at the start of any user-initiated mutating action.</summary>
    public void PushUndo()
    {
        if (_suppressUndoCapture) return;
        _undoStack.Push(Snapshot.Capture(this));
        if (_undoStack.Count > UndoLimit)
        {
            // Drop oldest by reversing — Stack is LIFO so we pop+rebuild.
            var keep = new Snapshot[UndoLimit];
            for (int i = UndoLimit - 1; i >= 0; i--) keep[i] = _undoStack.Pop();
            _undoStack.Clear();
            for (int i = 0; i < UndoLimit; i++) _undoStack.Push(keep[i]);
        }
        _redoStack.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    [RelayCommand]
    void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(Snapshot.Capture(this));
        var snap = _undoStack.Pop();
        _suppressUndoCapture = true;
        try { snap.Restore(this); }
        finally { _suppressUndoCapture = false; }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        StatusMessage = "Undo.";
    }

    [RelayCommand]
    void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(Snapshot.Capture(this));
        var snap = _redoStack.Pop();
        _suppressUndoCapture = true;
        try { snap.Restore(this); }
        finally { _suppressUndoCapture = false; }
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        StatusMessage = "Redo.";
    }

    // Internal raw accessors used by Snapshot. Keep these here rather than
    // exposing the underlying dictionaries so the public API stays clean.
    internal int NextRoomIdRaw => _nextRoomId;
    internal Dictionary<(int, int), (Passage, System.Numerics.Vector2)> PassageOverridesRaw => _passageOverrides;

    /// <summary>Internal hook used by <see cref="Snapshot.Restore"/>. Do not
    /// call directly — go through Undo / Redo / push-and-mutate instead.</summary>
    internal void RestoreFromSnapshot(Snapshot s)
    {
        // Selection and transient state cleared so we don't dangle pointers
        // into a stale environment.
        SelectedAdjacency = null;
        SelectedOpeningIndex = -1;
        SelectedRoomId = -1;
        SelectedTiles.Clear();
        SelectedSubCell = null;
        SelectedObjectIndex = -1;

        // Resize the grid + replay tile assignments.
        RoomGrid = (int[,])s.RoomGrid.Clone();
        GridWidth = s.GridWidth;
        GridLength = s.GridLength;

        // Parameter state.
        TileSize = s.TileSize;
        WallThickness = s.WallThickness;
        WallHeight = s.WallHeight;
        DoorWidth = s.DoorWidth;
        DoorHeight = s.DoorHeight;
        WindowWidth = s.WindowWidth;
        WindowHeight = s.WindowHeight;
        WindowSillHeight = s.WindowSillHeight;
        GridSubdivision = s.GridSubdivision;
        DefaultObjectY = s.DefaultObjectY;
        DefaultFloorColor = s.DefaultFloorColor;
        DefaultCeilingColor = s.DefaultCeilingColor;
        _nextRoomId = s.NextRoomId;

        // Authored dictionaries.
        _passageOverrides.Clear();
        foreach (var kv in s.PassageOverrides) _passageOverrides[kv.Key] = kv.Value;
        _wallColors.Clear();
        foreach (var kv in s.WallColors) _wallColors[kv.Key] = kv.Value;
        _roomFloorColors.Clear();
        foreach (var kv in s.RoomFloorColors) _roomFloorColors[kv.Key] = kv.Value;
        _roomCeilingColors.Clear();
        foreach (var kv in s.RoomCeilingColors) _roomCeilingColors[kv.Key] = kv.Value;
        _roomSingleWallColors.Clear();
        foreach (var kv in s.RoomSingleWallColors) _roomSingleWallColors[kv.Key] = kv.Value;
        _multiColorRoomIds.Clear();
        foreach (var id in s.MultiColorRoomIds) _multiColorRoomIds.Add(id);
        _roomNames.Clear();
        foreach (var kv in s.RoomNames) _roomNames[kv.Key] = kv.Value;

        _objects.Clear();
        foreach (var o in s.Objects)
            _objects.Add(new RoomObject
            {
                OwningRoomId = o.OwningRoomId,
                Slot = o.Slot,
                Position = o.Position,
                Rotation = o.Rotation,
            });
        _objectTypes.Clear();
        foreach (var t in s.ObjectTypes)
            _objectTypes.Add(new ObjectType
            {
                Name = t.Name,
                Shape = t.Shape,
                Color = t.Color,
                Size = t.Size,
            });

        Rebuild();
        EditVersion++;
    }

    public MainWindowViewModel()
    {
        RoomGrid = new int[GridWidth, GridLength];
        ResetGrid();
        // Seed one default object type so Objects mode has something to place
        // out of the box. The user grows the list via 'Add object type'.
        _objectTypes.Add(new ObjectType
        {
            Name = "Object 1",
            Shape = ObjectShape.Cube,
            Color = new System.Numerics.Vector3(0.85f, 0.30f, 0.30f),
            Size = 0.30f,
        });
        Rebuild();
    }

    // ---- Grid management ----

    void ResetGrid()
    {
        RoomGrid = new int[GridWidth, GridLength];
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
                RoomGrid[x, z] = -1;
        SelectedTiles.Clear();
        _nextRoomId = 0;
    }

    /// <summary>Mutates the selection set; called by the editor view on user input.</summary>
    public void SetTileSelected(int x, int z, bool selected)
    {
        if (x < 0 || x >= GridWidth || z < 0 || z >= GridLength) return;
        // Can't select tiles already owned by a room.
        if (RoomGrid[x, z] >= 0) return;

        if (selected) SelectedTiles.Add((x, z));
        else SelectedTiles.Remove((x, z));
        EditVersion++;
    }

    [RelayCommand]
    void CreateRoomFromSelection()
    {
        if (SelectedTiles.Count == 0)
        {
            StatusMessage = "Nothing selected. Click+drag empty tiles first.";
            return;
        }

        // Validate: selection must be a contiguous rectangle (v1 limitation).
        int xMin = int.MaxValue, xMax = int.MinValue, zMin = int.MaxValue, zMax = int.MinValue;
        foreach (var (x, z) in SelectedTiles)
        {
            if (x < xMin) xMin = x;
            if (x > xMax) xMax = x;
            if (z < zMin) zMin = z;
            if (z > zMax) zMax = z;
        }
        int bbox = (xMax - xMin + 1) * (zMax - zMin + 1);
        if (SelectedTiles.Count != bbox)
        {
            StatusMessage = "Selection must be a filled rectangle (v1 limitation). Adjust and try again.";
            return;
        }

        PushUndo();
        int id = _nextRoomId++;
        foreach (var (x, z) in SelectedTiles)
            RoomGrid[x, z] = id;

        // Seed appearance from the auto-generated room hue (used as the wall
        // color so the room reads as a single colored block by default) and
        // the user-configured floor / ceiling defaults.
        _roomSingleWallColors[id] = RoomColorRgb(id);
        _roomFloorColors[id]      = DefaultFloorColor;
        _roomCeilingColors[id]    = DefaultCeilingColor;

        SelectedTiles.Clear();
        SelectedRoomId = id; // auto-select the new room so its editor panel appears
        EditVersion++;
        Rebuild();
        StatusMessage = $"Created room #{id} ({xMax - xMin + 1}×{zMax - zMin + 1} tiles).";
    }

    /// <summary>
    /// Renumbers all rooms so the room with id <paramref name="newStartId"/>
    /// becomes id 0, with the rest filled in BFS order from there. Walking the
    /// adjacency graph this way keeps spatially-related rooms numerically
    /// adjacent, which is the most useful default for downstream tooling that
    /// reads room numbers as a navigation order. Disconnected rooms get
    /// trailing ids in their previous order. All per-room state (colours,
    /// multi-colour flag, per-wall overrides, current selection) is remapped
    /// so the user sees no apparent change beyond the new ids.
    /// </summary>
    [RelayCommand]
    void MarkSelectedRoomAsStart()
    {
        if (SelectedRoomId < 0)
        {
            StatusMessage = "Select a room first.";
            return;
        }
        if (CurrentEnvironment is null) return;
        if (SelectedRoomId == 0)
        {
            StatusMessage = "Room 0 is already the start room.";
            return;
        }
        PushUndo();
        int oldStart = SelectedRoomId;
        RenumberFromStart(oldStart);
        SelectedRoomId = 0;
        StatusMessage = $"Room {oldStart} is now Room 0 (start). Other rooms renumbered by BFS distance.";
    }

    void RenumberFromStart(int newStartOldId)
    {
        var env = CurrentEnvironment;
        if (env is null) return;

        // BFS through the adjacency graph; each room becomes the next id.
        // Multi-exit branches break ties by the neighbour's current id, which
        // is deterministic but otherwise arbitrary — the "best guess" the user
        // asked for.
        var oldToNew = new Dictionary<int, int>();
        int next = 0;
        oldToNew[newStartOldId] = next++;
        var queue = new Queue<int>();
        queue.Enqueue(newStartOldId);

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            var room = env.Rooms.FirstOrDefault(r => r.Id == cur);
            if (room is null) continue;

            var neighbours = new List<int>();
            foreach (var adj in env.Adjacencies)
            {
                if (!adj.IsInternal) continue;
                // Only follow traversable passages — closed walls don't form a
                // path between rooms and shouldn't drive the numbering order.
                if (adj.Passage is Passage.Closed) continue;
                int? other =
                    adj.RoomA.Id == cur ? adj.RoomB!.Id :
                    adj.RoomB!.Id == cur ? adj.RoomA.Id :
                    (int?)null;
                if (other is int nid && !oldToNew.ContainsKey(nid))
                    neighbours.Add(nid);
            }
            neighbours.Sort();
            foreach (var nid in neighbours)
            {
                if (oldToNew.ContainsKey(nid)) continue; // could've been picked up by a previous neighbour
                oldToNew[nid] = next++;
                queue.Enqueue(nid);
            }
        }

        // Disconnected rooms (graph-component-isolated) trail in id order.
        foreach (var room in env.Rooms.OrderBy(r => r.Id))
            if (!oldToNew.ContainsKey(room.Id))
                oldToNew[room.Id] = next++;

        // Identity mapping → no-op renumber.
        bool anyChange = false;
        foreach (var kvp in oldToNew)
            if (kvp.Key != kvp.Value) { anyChange = true; break; }
        if (!anyChange) return;

        // Apply mapping to every place a room id is stored.
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
            {
                int old = RoomGrid[x, z];
                if (old >= 0 && oldToNew.TryGetValue(old, out var nu))
                    RoomGrid[x, z] = nu;
            }
        RemapKeys(_roomFloorColors, oldToNew);
        RemapKeys(_roomCeilingColors, oldToNew);
        RemapKeys(_roomSingleWallColors, oldToNew);
        RemapKeys(_roomNames, oldToNew);

        var newMulti = new HashSet<int>();
        foreach (var id in _multiColorRoomIds)
            newMulti.Add(oldToNew.TryGetValue(id, out var nu) ? nu : id);
        _multiColorRoomIds.Clear();
        foreach (var id in newMulti) _multiColorRoomIds.Add(id);

        var newWallColors = new Dictionary<(int RoomId, int MidX, int MidZ), System.Numerics.Vector3>();
        foreach (var kvp in _wallColors)
        {
            int oldId = kvp.Key.RoomId;
            int mappedId = oldToNew.TryGetValue(oldId, out var nu) ? nu : oldId;
            newWallColors[(mappedId, kvp.Key.MidX, kvp.Key.MidZ)] = kvp.Value;
        }
        _wallColors.Clear();
        foreach (var kvp in newWallColors) _wallColors[kvp.Key] = kvp.Value;

        _nextRoomId = next;

        Rebuild();
        EditVersion++;
    }

    /// <summary>
    /// Walks every internal adjacency and rewrites each door opening so it
    /// swings into the room with the higher id (RoomB by Adjacency convention,
    /// hence SwingNegative=true). Outer adjacencies and window openings are
    /// left untouched.
    /// </summary>
    public void UpdateDoorSwingsToHigherRoom()
    {
        if (CurrentEnvironment is null) return;
        PushUndo();
        int touched = 0;
        foreach (var adj in CurrentEnvironment.Adjacencies)
        {
            if (!adj.IsInternal) continue;
            if (adj.Passage is not Passage.Doorway dw) continue;

            // Higher room is whichever of RoomA / RoomB has the larger id;
            // in this codebase RoomA is always lower, so target = -N (true).
            bool targetSwingNegative = adj.RoomB!.Id > adj.RoomA.Id;

            var openings = new List<Opening>(dw.Openings.Count);
            bool changed = false;
            foreach (var op in dw.Openings)
            {
                if (op.IsWindow) { openings.Add(op); continue; }
                if (op.SwingNegative != targetSwingNegative)
                {
                    openings.Add(op.With(swingNegative: targetSwingNegative));
                    changed = true;
                }
                else
                {
                    openings.Add(op);
                }
            }
            if (changed)
            {
                adj.Passage = new Passage.Doorway(openings);
                RememberPassage(adj);
                touched += openings.Count;
            }
        }
        if (touched > 0)
        {
            EditVersion++;
            StatusMessage = $"Updated swing on {touched} door opening(s).";
        }
        else
        {
            StatusMessage = "All doors already swing toward the higher-numbered room.";
        }
    }

    static void RemapKeys<T>(Dictionary<int, T> dict, Dictionary<int, int> oldToNew)
    {
        var copy = new Dictionary<int, T>(dict);
        dict.Clear();
        foreach (var kvp in copy)
        {
            int newKey = oldToNew.TryGetValue(kvp.Key, out var nu) ? nu : kvp.Key;
            dict[newKey] = kvp.Value;
        }
    }

    /// <summary>
    /// Click handler for the editor view — if the tile belongs to a room, select
    /// that room (opens the right-side editor panel). Returns true if the click
    /// hit a room and should NOT fall through to drag-select.
    /// </summary>
    public bool TrySelectRoomAtTile(int x, int z)
    {
        if (x < 0 || x >= GridWidth || z < 0 || z >= GridLength) return false;
        int id = RoomGrid[x, z];
        if (id < 0) return false;
        SelectedRoomId = id;
        SelectedAdjacency = null;
        StatusMessage = $"Selected Room {id}.";
        return true;
    }

    [RelayCommand]
    void ClearSelection()
    {
        SelectedTiles.Clear();
        SelectedAdjacency = null;
        EditVersion++;
        StatusMessage = "Selection cleared.";
    }

    [RelayCommand]
    void ResetAll()
    {
        PushUndo();
        ResetGrid();
        // Clear authored state too — Reset means a clean slate.
        _passageOverrides.Clear();
        _wallColors.Clear();
        _roomFloorColors.Clear();
        _roomCeilingColors.Clear();
        _roomSingleWallColors.Clear();
        _multiColorRoomIds.Clear();
        _roomNames.Clear();
        _objects.Clear();
        SelectedRoomId = -1;
        SelectedSubCell = null;
        SelectedObjectIndex = -1;
        Rebuild();
        EditVersion++;
        StatusMessage = "Grid reset.";
    }

    /// <summary>
    /// The wall the user has clicked on. Set via <see cref="TrySelectAdjacencyAtWorld"/>;
    /// hotkeys then mutate its passage. Cleared on grid changes (the materialized
    /// adjacency would point at a stale Adjacency object).
    /// </summary>
    [ObservableProperty] Adjacency? _selectedAdjacency;

    /// <summary>
    /// World-space distance along the selected wall (from segment Start) where the
    /// user clicked, snapped to the nearest valid door anchor (whole-wall midpoint
    /// or a tile-pair-seam center). Pressing D places the doorway centered here.
    /// Exposed so the view can highlight the active anchor.
    /// </summary>
    public float SelectedClickAlong => _selectedClickAlong;

    float _selectedClickAlong;

    /// <summary>
    /// Selects the wall closest to the given world XZ point, if within tolerance.
    /// Returns true if a wall was selected. Also records where along the wall the
    /// click landed so a subsequent door placement can snap to that spot.
    /// </summary>
    public bool TrySelectAdjacencyAtWorld(System.Numerics.Vector2 worldPos, float toleranceWorld)
    {
        if (CurrentEnvironment is null) return false;

        Adjacency? hit = null;
        float minDist = toleranceWorld;
        foreach (var adj in CurrentEnvironment.Adjacencies)
        {
            float d = DistanceFromPointToSegment(worldPos, adj.SharedSegment);
            if (d < minDist)
            {
                minDist = d;
                hit = adj;
            }
        }
        if (hit is null) return false;

        SelectedAdjacency = hit;
        // Project click onto wall direction → "along" coordinate in world units.
        var seg = hit.SharedSegment;
        var ap = worldPos - seg.Start;
        var ab = seg.End - seg.Start;
        float lenSq = ab.LengthSquared();
        float along = lenSq > 1e-6f
            ? System.Math.Clamp(System.Numerics.Vector2.Dot(ap, ab) / lenSq, 0f, 1f) * seg.Length
            : 0f;
        _selectedClickAlong = SnapAlongToCandidate(along, seg.Length, TileSize);

        // If the snapped anchor lands inside an existing opening, select it so
        // the right-side panel pops up with that opening's settings.
        SelectedOpeningIndex = FindOpeningAt(hit, _selectedClickAlong);
        OnPropertyChanged(nameof(HasSelectedOpening));
        OnPropertyChanged(nameof(SelectedOpening));

        EditVersion++;
        string b = hit.RoomB is null ? "outside" : $"#{hit.RoomB.Id}";
        StatusMessage = $"Selected wall #{hit.RoomA.Id} ↔ {b} ({hit.Passage.GetType().Name}). " +
                        "Press D for doorway, O for open, C for closed.";
        return true;
    }

    /// <summary>
    /// Toggle a doorway at the active anchor on the selected wall (D hotkey).
    /// If a door already exists at the active anchor, remove it. Otherwise, add
    /// one — preserving any other openings on the wall.
    /// </summary>
    [RelayCommand]
    void ToggleDoorOnSelectedWall() => ToggleOpeningOnSelectedWall(isWindow: false);

    /// <summary>
    /// Toggle a window at the active anchor on the selected wall (W hotkey).
    /// If a window already exists at the active anchor, remove it. Otherwise,
    /// add one and merge with any window directly adjacent on either side
    /// (matching sill / head heights) so consecutive windows form a single
    /// wider opening.
    /// </summary>
    [RelayCommand]
    void ToggleWindowOnSelectedWall() => ToggleOpeningOnSelectedWall(isWindow: true);

    void ToggleOpeningOnSelectedWall(bool isWindow)
    {
        if (SelectedAdjacency is null)
        {
            StatusMessage = "No wall selected. Click a wall first.";
            return;
        }
        PushUndo();

        var adj = SelectedAdjacency;
        float segLen = adj.SharedSegment.Length;
        float reqWidth = isWindow ? WindowWidth : DoorWidth;
        float reqHeight = isWindow ? WindowHeight : DoorHeight;
        float reqSill = isWindow ? WindowSillHeight : 0f;
        // Refuse to place an opening that won't fit at the selected anchor.
        // The previous behaviour clamped w = min(reqWidth, segLen), which on
        // walls shorter than DoorWidth produced a door spanning the entire
        // wall instead of one centered on the click — confusing.
        if (reqWidth > segLen + 1e-3f)
        {
            string label1 = isWindow ? "window" : "door";
            StatusMessage = $"Can't fit a {reqWidth:F2}m {label1} on this {segLen:F2}m wall. " +
                            $"Reduce the {label1} width or pick a longer wall.";
            return;
        }
        float w = reqWidth;
        float anchor = _selectedClickAlong > 0f ? _selectedClickAlong : segLen * 0.5f;
        string label = isWindow ? "window" : "door";

        var openings = adj.Passage is Passage.Doorway dw
            ? new List<Opening>(dw.Openings)
            : new List<Opening>();

        // Does an opening already cover the active anchor (i.e., the anchor sits
        // inside the opening's span)? Use containment rather than center-distance
        // so merged wide openings still toggle off correctly.
        const float ANCHOR_EPS = 1e-3f;
        int sameKindIdx = -1;
        int otherKindIdx = -1;
        for (int i = 0; i < openings.Count; i++)
        {
            var op = openings[i];
            if (anchor < op.OffsetAlongEdge - ANCHOR_EPS) continue;
            if (anchor > op.OffsetAlongEdge + op.Width + ANCHOR_EPS) continue;
            if (op.IsWindow == isWindow) sameKindIdx = i;
            else otherKindIdx = i;
        }

        if (sameKindIdx >= 0)
        {
            openings.RemoveAt(sameKindIdx);
            StatusMessage = openings.Count > 0
                ? $"Removed {label} — {openings.Count} opening(s) remaining."
                : $"Removed {label} — wall is now closed.";
        }
        else
        {
            float offset = System.Math.Clamp(anchor - w * 0.5f, 0f, segLen - w);
            float endOff = offset + w;

            // Reject if the new opening would clash with a *different-kind* opening
            // (a door cannot share space with a window). Same-kind clashes fall
            // through to the merge step below.
            bool blocked = false;
            foreach (var op in openings)
            {
                if (op.IsWindow == isWindow) continue;
                if (endOff > op.OffsetAlongEdge && offset < op.OffsetAlongEdge + op.Width)
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked)
            {
                StatusMessage = $"Can't add {label} here — would overlap a {(isWindow ? "door" : "window")}.";
                return;
            }

            // Default door swing: into the room with the higher id. RoomA is
            // always the lower id by convention, so internal walls swing -N
            // (into RoomB). Outer walls have RoomB = null → swing +N into the
            // only room. Windows ignore hinge / swing.
            bool defaultSwingNegative = !isWindow && adj.IsInternal;
            openings.Add(new Opening(offset, w, reqHeight, reqSill,
                hingeAtEnd: false,
                swingNegative: defaultSwingNegative));
            MergeOverlappingSameKind(openings);
            StatusMessage = $"Added {label} at offset {offset:F2}m. {openings.Count} opening(s) on this wall.";
        }

        adj.Passage = openings.Count > 0
            ? new Passage.Doorway(openings)
            : Passage.Closed.Instance;
        RememberPassage(adj);

        // After mutating, surface the opening at the active anchor (if any) to
        // the right-side panel so the user can immediately tweak its size /
        // hinge / swing.
        SelectedOpeningIndex = FindOpeningAt(adj, anchor);
        OnPropertyChanged(nameof(HasSelectedOpening));
        OnPropertyChanged(nameof(SelectedOpening));
        // Clear room selection so the panel switches to opening mode.
        if (HasSelectedOpening) SelectedRoomId = -1;

        EditVersion++;
    }

    /// <summary>
    /// Merge same-kind openings (door+door or window+window) that touch or
    /// overlap into a single wider opening. Heights / sill must match.
    /// Mutates <paramref name="openings"/> in place.
    /// </summary>
    static void MergeOverlappingSameKind(List<Opening> openings)
    {
        const float EPS = 1e-3f;
        bool merged;
        do
        {
            openings.Sort((a, b) => a.OffsetAlongEdge.CompareTo(b.OffsetAlongEdge));
            merged = false;
            for (int i = 0; i < openings.Count - 1; i++)
            {
                var a = openings[i];
                var b = openings[i + 1];
                if (a.IsWindow != b.IsWindow) continue;
                if (System.Math.Abs(a.Height - b.Height) > EPS) continue;
                if (System.Math.Abs(a.SillHeight - b.SillHeight) > EPS) continue;
                // Touching or overlapping: b starts at or before a's right edge.
                if (b.OffsetAlongEdge > a.OffsetAlongEdge + a.Width + EPS) continue;

                float newStart = a.OffsetAlongEdge;
                float newEnd = System.Math.Max(
                    a.OffsetAlongEdge + a.Width,
                    b.OffsetAlongEdge + b.Width);
                openings[i] = new Opening(
                    newStart,
                    newEnd - newStart,
                    a.Height,
                    a.SillHeight);
                openings.RemoveAt(i + 1);
                merged = true;
                break;
            }
        } while (merged);
    }

    /// <summary>Set the selected wall's passage to Open (no wall) — O hotkey.</summary>
    [RelayCommand]
    void OpenSelectedWall()
    {
        if (SelectedAdjacency is null)
        {
            StatusMessage = "No wall selected. Click a wall first.";
            return;
        }
        PushUndo();
        SelectedAdjacency.Passage = Passage.Open.Instance;
        RememberPassage(SelectedAdjacency);
        EditVersion++;
        StatusMessage = "Wall → Open (no wall).";
    }

    /// <summary>Set the selected wall's passage to Closed (solid wall, no doors) — C hotkey.</summary>
    [RelayCommand]
    void CloseSelectedWall()
    {
        if (SelectedAdjacency is null)
        {
            StatusMessage = "No wall selected. Click a wall first.";
            return;
        }
        PushUndo();
        SelectedAdjacency.Passage = Passage.Closed.Instance;
        RememberPassage(SelectedAdjacency);
        EditVersion++;
        StatusMessage = "Wall → Closed.";
    }

    /// <summary>
    /// Snap a click position along a wall to the nearest valid door anchor. Anchors
    /// are at every tile center along the wall AND at every internal tile-seam
    /// (the boundary between two consecutive tiles, excluding the two corners).
    /// </summary>
    static float SnapAlongToCandidate(float clickAlong, float segLen, float tileSize)
    {
        float best = float.MaxValue;
        float bestDist = float.MaxValue;
        foreach (var anchor in DoorAnchorPositions(segLen, tileSize))
        {
            float d = System.Math.Abs(clickAlong - anchor);
            if (d < bestDist)
            {
                bestDist = d;
                best = anchor;
            }
        }
        return best;
    }

    /// <summary>
    /// All valid door anchors along the given wall, in world units along the segment.
    /// For an N-tile-long wall there are 2N-1 anchors:
    ///   • N tile centers — at (i + 0.5) × tileSize for i = 0..N-1
    ///   • N-1 internal tile-seam centers — at i × tileSize for i = 1..N-1
    ///     (corners at 0 and N×tileSize are excluded — those are wall ends)
    /// </summary>
    public static IEnumerable<float> DoorAnchorsAlongWall(EdgeSegment segment, float tileSize) =>
        DoorAnchorPositions(segment.Length, tileSize);

    static IEnumerable<float> DoorAnchorPositions(float segLen, float tileSize)
    {
        int n = (int)System.Math.Max(1, System.Math.Round(segLen / tileSize));
        for (int i = 0; i < n; i++)
            yield return (i + 0.5f) * tileSize;       // tile centers
        for (int i = 1; i < n; i++)
            yield return i * tileSize;                 // internal tile-seams
    }

    static float DistanceFromPointToSegment(System.Numerics.Vector2 p, EdgeSegment seg)
    {
        var ab = seg.End - seg.Start;
        var ap = p - seg.Start;
        float abLenSq = ab.LengthSquared();
        if (abLenSq < 1e-6f) return (p - seg.Start).Length();
        float t = System.Math.Clamp(System.Numerics.Vector2.Dot(ap, ab) / abLenSq, 0f, 1f);
        var closest = seg.Start + ab * t;
        return (p - closest).Length();
    }

    /// <summary>
    /// Generates a random GridDomino layout into the authored grid as a starting point
    /// (D4: keeps the parametric generator available without coupling the UI to it).
    /// </summary>
    [RelayCommand]
    void RandomFill()
    {
        PushUndo();
        try
        {
            int seed = new Random().Next(int.MaxValue);
            var gen = new GridDominoGenerator
            {
                FloorWidthCells = GridWidth,
                FloorLengthCells = GridLength,
                RectangleRoomCount = (GridWidth * GridLength) / 6, // ~1 rectangle per 6 tiles
                TileSize = TileSize,
            };
            var env = gen.Generate(new SeededRandom(seed));

            // Reverse-engineer the grid from the generated env: each room has a position
            // (in world coords) and a RectangleShape; convert back to tile coordinates.
            ResetGrid();
            int maxId = 0;
            foreach (var room in env.Rooms)
            {
                int xMin = (int)MathF.Round(room.Position.X / TileSize);
                int zMin = (int)MathF.Round(room.Position.Y / TileSize);
                var rect = (RectangleShape)room.Shape;
                int w = (int)MathF.Round(rect.Width / TileSize);
                int d = (int)MathF.Round(rect.Depth / TileSize);
                for (int x = xMin; x < xMin + w; x++)
                    for (int z = zMin; z < zMin + d; z++)
                        if (x >= 0 && x < GridWidth && z >= 0 && z < GridLength)
                            RoomGrid[x, z] = room.Id;
                if (room.Id >= maxId) maxId = room.Id + 1;
                // Seed appearance for every random room so the editor view and the
                // glTF export both read consistently without forcing the user to
                // visit each room's panel.
                if (!_roomSingleWallColors.ContainsKey(room.Id))
                    _roomSingleWallColors[room.Id] = RoomColorRgb(room.Id);
                if (!_roomFloorColors.ContainsKey(room.Id))
                    _roomFloorColors[room.Id] = DefaultFloorColor;
                if (!_roomCeilingColors.ContainsKey(room.Id))
                    _roomCeilingColors[room.Id] = DefaultCeilingColor;
            }
            _nextRoomId = maxId;

            Rebuild();
            EditVersion++;
            StatusMessage = $"Random fill: {env.Rooms.Count} rooms (seed {seed}). Edit as needed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Random fill failed: {ex.Message}";
        }
    }

    [RelayCommand]
    async Task ExportGltfAsync(Window? owner)
    {
        if (owner is null || CurrentEnvironment is null) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export geometry as glTF",
            SuggestedFileName = "environment.glb",
            DefaultExtension = "glb",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("glTF Binary (*.glb)") { Patterns = new[] { "*.glb" } },
                new FilePickerFileType("glTF JSON (*.gltf)")  { Patterns = new[] { "*.gltf" } },
            },
        });
        if (file is null) return;

        try
        {
            // SharpGLTF picks GLB vs glTF+bin from the extension on Save —
            // .glb is single-file binary; .gltf writes JSON + sidecar .bin.
            // glTF natively supports scene hierarchy, so the per-room nesting
            // (Root → Room_<id> → floor/ceiling/wall_<i>) survives import in
            // Unity, Blender, Three.js, or any glTF-aware tool.
            GltfExporter.Export(
                file.Path.LocalPath, CurrentEnvironment, WallThickness, WallHeight,
                _multiColorRoomIds,
                _roomFloorColors, _roomCeilingColors,
                _roomSingleWallColors, _wallColors,
                _objects, _objectTypes);
            StatusMessage = $"Exported glTF → {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    async Task ExportJsonAsync(Window? owner)
    {
        if (owner is null) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export environment as JSON",
            SuggestedFileName = "environment.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;

        try
        {
            var doc = JsonExporter.BuildDocument(
                RoomGrid,
                TileSize,
                WallThickness, WallHeight,
                DoorWidth, DoorHeight,
                WindowWidth, WindowHeight, WindowSillHeight,
                CurrentEnvironment,
                GridSubdivision,
                DefaultObjectY,
                _objects,
                _objectTypes);

            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            JsonExporter.Export(writer, doc);
            StatusMessage = $"Exported JSON → {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"JSON export failed: {ex.Message}";
        }
    }

    void Rebuild()
    {
        try
        {
            var env = MultiRoomEnvironmentBuilder.FromGrid(RoomGrid, TileSize);

            // Re-apply any persisted passage choices. Doors / open passages set by
            // the user need to survive grid edits — including outer↔internal
            // transitions when new rooms are placed against existing walls.
            foreach (var adj in env.Adjacencies)
            {
                if (!_passageOverrides.TryGetValue(PassageKey(adj), out var stored))
                    continue;

                // If the new segment's Start is closer to the stored End than to
                // the stored Start, the wall direction flipped on rebuild
                // (typical after renumbering: RoomA is always the lower-id
                // room, so swapping ids can flip the segment). Re-orient the
                // openings so they keep their world positions.
                var newSeg = adj.SharedSegment;
                bool flipped = (newSeg.Start - stored.Start).LengthSquared()
                             > (newSeg.End - stored.Start).LengthSquared();

                if (stored.Passage is Passage.Doorway dw)
                {
                    var rebased = new List<Opening>(dw.Openings.Count);
                    foreach (var op in dw.Openings)
                    {
                        var op2 = flipped
                            ? op.With(
                                offsetAlongEdge: newSeg.Length - op.OffsetAlongEdge - op.Width,
                                hingeAtEnd: !op.HingeAtEnd,
                                swingNegative: !op.SwingNegative)
                            : op;
                        if (op2.OffsetAlongEdge >= -1e-3f &&
                            op2.OffsetAlongEdge + op2.Width <= newSeg.Length + 1e-3f)
                            rebased.Add(op2);
                    }
                    if (rebased.Count > 0)
                        adj.Passage = new Passage.Doorway(rebased);
                    // else: leave as default Closed — no openings fit the new segment.
                }
                else
                {
                    adj.Passage = stored.Passage;
                }
            }

            CurrentEnvironment = env;
            // The previous SelectedAdjacency points at a stale Adjacency from the previous
            // build; clear it so the editor doesn't try to mutate orphaned objects.
            SelectedAdjacency = null;
        }
        catch (Exception ex)
        {
            CurrentEnvironment = null;
            SelectedAdjacency = null;
            StatusMessage = $"Build failed: {ex.Message}";
        }
    }

    // Reactivity: when grid dimensions change, resize the underlying array.
    partial void OnGridWidthChanged(int value) => OnGridDimensionChanged();
    partial void OnGridLengthChanged(int value) => OnGridDimensionChanged();
    partial void OnTileSizeChanged(float value) => Rebuild();
    partial void OnWallThicknessChanged(float value) => Rebuild();
    partial void OnWallHeightChanged(float value) => Rebuild();
    partial void OnDoorWidthChanged(float value) => Rebuild();
    partial void OnDoorHeightChanged(float value) => Rebuild();
    partial void OnWindowWidthChanged(float value) => Rebuild();
    partial void OnWindowHeightChanged(float value) => Rebuild();
    partial void OnWindowSillHeightChanged(float value) => Rebuild();

    void OnGridDimensionChanged()
    {
        if (GridWidth < 1 || GridLength < 1) return;
        // Resize grid, preserving rooms that fit in the new bounds.
        var oldGrid = RoomGrid;
        int oldW = oldGrid.GetLength(0);
        int oldL = oldGrid.GetLength(1);
        var newGrid = new int[GridWidth, GridLength];
        for (int x = 0; x < GridWidth; x++)
            for (int z = 0; z < GridLength; z++)
                newGrid[x, z] = (x < oldW && z < oldL) ? oldGrid[x, z] : -1;
        RoomGrid = newGrid;
        // Drop any selection now off-grid.
        SelectedTiles.RemoveWhere(t => t.x >= GridWidth || t.z >= GridLength);
        EditVersion++;
        Rebuild();
    }
}
