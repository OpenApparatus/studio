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

    // ---- Visual / generation parameters ----

    [ObservableProperty] float _wallThickness = 0.2f;
    [ObservableProperty] float _wallHeight = 3f;
    [ObservableProperty] float _doorWidth = 1.2f;
    [ObservableProperty] float _doorHeight = 2.2f;

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
            StatusMessage = "Nothing selected. Click+drag tiles first.";
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
        EditVersion++;
        StatusMessage = "Selection cleared.";
    }

    [RelayCommand]
    void ResetAll()
    {
        ResetGrid();
        Rebuild();
        StatusMessage = "Grid reset.";
    }

    /// <summary>
    /// Cycles the passage type of the wall closest to the given world XZ point,
    /// if within <paramref name="toleranceWorld"/>. Cycle order: Closed → Doorway → Open → Closed.
    /// Returns true if a wall was clicked.
    /// </summary>
    public bool TryCyclePassageAtWorld(System.Numerics.Vector2 worldPos, float toleranceWorld)
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

        hit.Passage = hit.Passage switch
        {
            Passage.Closed _ => new Passage.Doorway(
                (hit.SharedSegment.Length - System.Math.Min(DoorWidth, hit.SharedSegment.Length)) * 0.5f,
                System.Math.Min(DoorWidth, hit.SharedSegment.Length),
                DoorHeight),
            Passage.Doorway _ => Passage.Open.Instance,
            _ => Passage.Closed.Instance,
        };
        EditVersion++;
        StatusMessage = $"Wall {hit.RoomA.Id}↔" +
                        (hit.RoomB is null ? "outside" : $"{hit.RoomB.Id}") +
                        $" → {hit.Passage.GetType().Name}";
        return true;
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
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            ObjExporter.Export(writer, CurrentEnvironment, WallThickness, WallHeight);
            StatusMessage = $"Exported OBJ → {file.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    void Rebuild()
    {
        try
        {
            var env = MultiRoomEnvironmentBuilder.FromGrid(RoomGrid, TileSize);
            // For now, all adjacencies stay Closed; D2 will add per-wall passage editing.
            CurrentEnvironment = env;
        }
        catch (Exception ex)
        {
            CurrentEnvironment = null;
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
