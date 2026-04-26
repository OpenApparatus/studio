using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    readonly Dictionary<(int, int), Passage> _passageOverrides = new();

    static (int, int) PassageKey(Adjacency adj)
    {
        var mid = adj.SharedSegment.Midpoint;
        return (
            (int)System.Math.Round(mid.X * 1000),
            (int)System.Math.Round(mid.Y * 1000));
    }

    void RememberPassage(Adjacency adj) => _passageOverrides[PassageKey(adj)] = adj.Passage;

    // ---- Visual / generation parameters ----

    [ObservableProperty] float _wallThickness = 0.2f;
    [ObservableProperty] float _wallHeight = 3f;
    [ObservableProperty] float _doorWidth = 1.2f;
    [ObservableProperty] float _doorHeight = 2.2f;
    [ObservableProperty] float _windowWidth = 1.2f;
    [ObservableProperty] float _windowHeight = 2.2f;
    [ObservableProperty] float _windowSillHeight = 1.0f;

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

        SelectedTiles.Clear();
        EditVersion++;
        Rebuild();
        StatusMessage = $"Created room #{id} ({xMax - xMin + 1}×{zMax - zMin + 1} tiles).";
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
        float w = System.Math.Min(reqWidth, segLen);
        float anchor = _selectedClickAlong > 0f ? _selectedClickAlong : segLen * 0.5f;
        string label = isWindow ? "window" : "door";

        var openings = adj.Passage is Passage.Doorway dw
            ? new List<Opening>(dw.Openings)
            : new List<Opening>();

        // Does an opening of the SAME kind already cover the active anchor?
        // Tolerance = half the requested width so adjacent anchors don't false-match.
        int sameKindIdx = -1;
        int otherKindIdx = -1;
        for (int i = 0; i < openings.Count; i++)
        {
            float center = openings[i].OffsetAlongEdge + openings[i].Width * 0.5f;
            if (System.Math.Abs(center - anchor) >= w * 0.5f) continue;
            if (openings[i].IsWindow == isWindow) sameKindIdx = i;
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
            if (otherKindIdx >= 0)
            {
                StatusMessage = $"Can't add {label} here — a {(isWindow ? "door" : "window")} already occupies this anchor.";
                return;
            }
            float offset = System.Math.Clamp(anchor - w * 0.5f, 0f, segLen - w);
            bool overlaps = false;
            foreach (var op in openings)
            {
                if (offset + w > op.OffsetAlongEdge && offset < op.OffsetAlongEdge + op.Width)
                {
                    overlaps = true;
                    break;
                }
            }
            if (overlaps)
            {
                StatusMessage = $"Can't add {label} here — would overlap an existing opening.";
                return;
            }
            openings.Add(new Opening(offset, w, reqHeight, reqSill));
            if (isWindow) MergeAdjacentWindows(openings);
            StatusMessage = $"Added {label} at offset {offset:F2}m. {openings.Count} opening(s) on this wall.";
        }

        adj.Passage = openings.Count > 0
            ? new Passage.Doorway(openings)
            : Passage.Closed.Instance;
        RememberPassage(adj);
        EditVersion++;
    }

    /// <summary>
    /// Merge windows that touch end-to-end (and share sill / head heights) into
    /// single wider openings. Mutates <paramref name="openings"/> in place.
    /// Doors are left alone — they always remain individual openings.
    /// </summary>
    static void MergeAdjacentWindows(List<Opening> openings)
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
                if (!a.IsWindow || !b.IsWindow) continue;
                if (System.Math.Abs(a.Height - b.Height) > EPS) continue;
                if (System.Math.Abs(a.SillHeight - b.SillHeight) > EPS) continue;
                if (System.Math.Abs(a.OffsetAlongEdge + a.Width - b.OffsetAlongEdge) > EPS) continue;

                openings[i] = new Opening(
                    a.OffsetAlongEdge,
                    a.Width + b.Width,
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
    async Task ExportObjAsync(Window? owner)
    {
        if (owner is null || CurrentEnvironment is null) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export geometry as OBJ",
            SuggestedFileName = "environment.obj",
            DefaultExtension = "obj",
            FileTypeChoices = new[] { new FilePickerFileType("Wavefront OBJ") { Patterns = new[] { "*.obj" } } },
        });
        if (file is null) return;

        try
        {
            // Sidecar MTL lives next to the OBJ. Without `mtllib`+`usemtl`, Unity
            // collapses each room into a single material slot per object.
            var objPath = file.Path.LocalPath;
            var mtlPath = System.IO.Path.ChangeExtension(objPath, ".mtl");
            var mtlFileName = System.IO.Path.GetFileName(mtlPath);

            IReadOnlyList<ObjExporter.MaterialSlot> slots;
            await using (var stream = await file.OpenWriteAsync())
            using (var writer = new StreamWriter(stream))
            {
                slots = ObjExporter.Export(
                    writer, CurrentEnvironment, WallThickness, WallHeight, mtlFileName);
            }

            using (var mtlWriter = new StreamWriter(mtlPath))
            {
                ObjExporter.WriteMtl(mtlWriter, slots);
            }

            StatusMessage = $"Exported OBJ + MTL → {file.Name}";
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
                if (!_passageOverrides.TryGetValue(PassageKey(adj), out var passage))
                    continue;

                // Validate any doorway openings still fit the (possibly different-length)
                // segment in the new build. Skip openings that no longer fit.
                if (passage is Passage.Doorway dw)
                {
                    var fitting = new List<Opening>();
                    foreach (var op in dw.Openings)
                    {
                        if (op.OffsetAlongEdge >= 0f &&
                            op.OffsetAlongEdge + op.Width <= adj.SharedSegment.Length + 1e-3f)
                            fitting.Add(op);
                    }
                    if (fitting.Count == dw.Openings.Count)
                        adj.Passage = passage;
                    else if (fitting.Count > 0)
                        adj.Passage = new Passage.Doorway(fitting);
                    // else: leave as default Closed — no openings fit the new segment.
                }
                else
                {
                    adj.Passage = passage;
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
