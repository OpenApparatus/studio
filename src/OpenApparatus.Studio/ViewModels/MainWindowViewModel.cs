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
        _wallColors[GltfExporter.WallColorKey(roomId, adj)] = rgb;
        EditVersion++;
    }

    /// <summary>Removes any color override for one room's side of the given wall.</summary>
    public void ClearWallColor(int roomId, Adjacency adj)
    {
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

    /// <summary>Which surface the editor view recolors tiles by — "Floor" shows
    /// each room's floor color, "Ceiling" shows each room's ceiling color.</summary>
    public enum ViewSurface { Floor, Ceiling }
    [ObservableProperty] ViewSurface _viewMode = ViewSurface.Floor;

    /// <summary>When on, the editor draws a path overlay through every
    /// traversable passage (open or doorway), anchoring each leg to the
    /// centres of the tile cells on either side of the opening.</summary>
    [ObservableProperty] bool _showPaths;
    partial void OnShowPathsChanged(bool value) => EditVersion++;

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

    partial void OnViewModeChanged(ViewSurface value) => EditVersion++;

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
        if (on) _multiColorRoomIds.Add(roomId);
        else _multiColorRoomIds.Remove(roomId);
        EditVersion++;
    }

    public void SetRoomFloorColor(int roomId, System.Numerics.Vector3 rgb)
    {
        _roomFloorColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomFloorColor(int roomId)
    {
        _roomFloorColors.Remove(roomId); EditVersion++;
    }

    public void SetRoomCeilingColor(int roomId, System.Numerics.Vector3 rgb)
    {
        _roomCeilingColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomCeilingColor(int roomId)
    {
        _roomCeilingColors.Remove(roomId); EditVersion++;
    }

    public void SetRoomSingleWallColor(int roomId, System.Numerics.Vector3 rgb)
    {
        _roomSingleWallColors[roomId] = rgb; EditVersion++;
    }

    public void ClearRoomSingleWallColor(int roomId)
    {
        _roomSingleWallColors.Remove(roomId); EditVersion++;
    }

    [ObservableProperty] MultiRoomEnvironment? _currentEnvironment;
    [ObservableProperty] string _statusMessage = "Click and drag to select tiles, then click 'Create Room'.";

    /// <summary>Bumped after every grid mutation so views can re-render.</summary>
    [ObservableProperty] int _editVersion;

    public MainWindowViewModel()
    {
        RoomGrid = new int[GridWidth, GridLength];
        ResetGrid();
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
        ResetGrid();
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
                _roomSingleWallColors, _wallColors);
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
                CurrentEnvironment);

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
