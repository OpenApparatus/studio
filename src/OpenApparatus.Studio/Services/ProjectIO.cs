using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenApparatus.Studio.ViewModels;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Studio project persistence — round-trips the editor's authored state
/// to a single JSON file. Distinct from <see cref="JsonExporter"/>,
/// which produces a downstream-consumer schema; this format is
/// editor-internal and is preserved exactly across save / open so
/// reopening reconstructs the canvas as the user left it.
///
/// Versioned so future schema changes can migrate older saves. v1 covers
/// grid dimensions, defaults, room ownership, passages, all colour
/// palettes (per-room + per-wall), object types + instances, project
/// title, placement constraints, and the camera state for both 2D and
/// 3D views.
/// </summary>
public static class ProjectIO
{
    public const string CurrentVersion = "1.0";
    public const string FileExtension = ".oapp";

    static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Save(string path, MainWindowViewModel vm)
    {
        var doc = ProjectFile.From(vm);
        var json = JsonSerializer.Serialize(doc, s_options);
        File.WriteAllText(path, json);
    }

    public static void Load(string path, MainWindowViewModel vm)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<ProjectFile>(json, s_options)
            ?? throw new InvalidDataException("Project file is empty.");
        if (doc.Version is null || !doc.Version.StartsWith("1."))
            throw new InvalidDataException(
                $"Unsupported project version '{doc.Version}'. This studio reads v1.x.");
        doc.Apply(vm);
    }
}

/// <summary>Serializable mirror of every authored field on the VM. Each
/// property is written in camelCase; null / default values are omitted on
/// write to keep saves small.</summary>
public sealed class ProjectFile
{
    public string? Version { get; set; } = ProjectIO.CurrentVersion;
    public string? Title { get; set; }

    // Grid + measurements
    public int GridWidth { get; set; }
    public int GridLength { get; set; }
    public float TileSize { get; set; }
    public float WallThickness { get; set; }
    public float WallHeight { get; set; }
    public float DoorWidth { get; set; }
    public float DoorHeight { get; set; }
    public float WindowWidth { get; set; }
    public float WindowHeight { get; set; }
    public float WindowSillHeight { get; set; }
    public int GridSubdivision { get; set; }
    public float DefaultObjectY { get; set; }

    // Defaults
    public float[]? DefaultFloorColor { get; set; }
    public float[]? DefaultCeilingColor { get; set; }

    // Tile → room ownership grid (flattened row-major).
    public int[]? RoomGrid { get; set; }

    // Per-room palettes / state.
    public Dictionary<int, float[]>? RoomFloorColors { get; set; }
    public Dictionary<int, float[]>? RoomCeilingColors { get; set; }
    public Dictionary<int, float[]>? RoomSingleWallColors { get; set; }
    public Dictionary<int, string>? RoomNames { get; set; }
    public List<int>? MultiColorRoomIds { get; set; }

    // Per-wall colour overrides keyed by "{roomId}_{midXmm}_{midZmm}".
    public Dictionary<string, float[]>? WallColors { get; set; }

    // Passage overrides — adjacency identity is reconstructed from
    // start/end mm-coordinates, since the in-memory Adjacency object
    // doesn't survive serialization.
    public List<PassageOverrideEntry>? PassageOverrides { get; set; }

    // Object types + instances.
    public List<ObjectTypeEntry>? ObjectTypes { get; set; }
    public List<ObjectInstanceEntry>? Objects { get; set; }

    // Camera state.
    public string? CameraView { get; set; }
    public double ZoomFactor { get; set; }
    public double PanOffsetX { get; set; }
    public double PanOffsetY { get; set; }
    public float IsoYaw { get; set; }
    public float IsoPitch { get; set; }
    public float IsoDistance { get; set; }
    public float IsoPivotX { get; set; }
    public float IsoPivotZ { get; set; }

    // Placement constraints — straight POCO copy.
    public PlacementConstraints? Constraints { get; set; }

    // ── Build from VM ──
    public static ProjectFile From(MainWindowViewModel vm)
    {
        var f = new ProjectFile
        {
            Title = vm.ProjectTitle,
            GridWidth = vm.GridWidth,
            GridLength = vm.GridLength,
            TileSize = vm.TileSize,
            WallThickness = vm.WallThickness,
            WallHeight = vm.WallHeight,
            DoorWidth = vm.DoorWidth,
            DoorHeight = vm.DoorHeight,
            WindowWidth = vm.WindowWidth,
            WindowHeight = vm.WindowHeight,
            WindowSillHeight = vm.WindowSillHeight,
            GridSubdivision = vm.GridSubdivision,
            DefaultObjectY = vm.DefaultObjectY,
            DefaultFloorColor = ColorToArr(vm.DefaultFloorColor),
            DefaultCeilingColor = ColorToArr(vm.DefaultCeilingColor),
            CameraView = vm.CameraView.ToString(),
            ZoomFactor = vm.ZoomFactor,
            PanOffsetX = vm.PanOffsetX,
            PanOffsetY = vm.PanOffsetY,
            IsoYaw = vm.IsoYaw,
            IsoPitch = vm.IsoPitch,
            IsoDistance = vm.IsoDistance,
            IsoPivotX = vm.IsoPivotX,
            IsoPivotZ = vm.IsoPivotZ,
            Constraints = vm.Constraints,
        };

        // Flatten RoomGrid.
        var grid = new int[vm.GridWidth * vm.GridLength];
        for (int x = 0; x < vm.GridWidth; x++)
            for (int z = 0; z < vm.GridLength; z++)
                grid[x * vm.GridLength + z] = vm.RoomGrid[x, z];
        f.RoomGrid = grid;

        f.RoomFloorColors   = vm.RoomFloorColors.ToDictionary(kv => kv.Key,   kv => ColorToArr(kv.Value));
        f.RoomCeilingColors = vm.RoomCeilingColors.ToDictionary(kv => kv.Key, kv => ColorToArr(kv.Value));
        f.RoomSingleWallColors = vm.RoomSingleWallColors.ToDictionary(kv => kv.Key, kv => ColorToArr(kv.Value));
        f.RoomNames = vm.RoomNames.ToDictionary(kv => kv.Key, kv => kv.Value);
        f.MultiColorRoomIds = vm.MultiColorRoomIds.ToList();

        f.WallColors = vm.WallColors.ToDictionary(
            kv => $"{kv.Key.RoomId}_{kv.Key.MidX}_{kv.Key.MidZ}",
            kv => ColorToArr(kv.Value));

        f.PassageOverrides = vm.SerializePassageOverrides();

        f.ObjectTypes = vm.ObjectTypes
            .Select(t => new ObjectTypeEntry
            {
                Name = t.Name,
                Shape = t.Shape.ToString(),
                Color = ColorToArr(t.Color),
                Size = t.Size,
            }).ToList();
        f.Objects = vm.Objects
            .Select(o => new ObjectInstanceEntry
            {
                Slot = o.Slot,
                OwningRoomId = o.OwningRoomId,
                X = o.Position.X, Y = o.Position.Y, Z = o.Position.Z,
                Rotation = o.Rotation,
            }).ToList();
        return f;
    }

    public void Apply(MainWindowViewModel vm)
        => vm.RestoreFromProjectFile(this);

    static float[] ColorToArr(Vector3 v) => new[] { v.X, v.Y, v.Z };
}

public sealed class PassageOverrideEntry
{
    public float StartX { get; set; }
    public float StartZ { get; set; }
    public float EndX { get; set; }
    public float EndZ { get; set; }
    public string Kind { get; set; } = "Closed"; // Closed / Open / Doorway
    public List<OpeningEntry>? Openings { get; set; }
}

public sealed class OpeningEntry
{
    public float Offset { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float SillHeight { get; set; }
    public bool HingeAtEnd { get; set; }
    public bool SwingNegative { get; set; }
}

public sealed class ObjectTypeEntry
{
    public string Name { get; set; } = "";
    public string Shape { get; set; } = "Cube";
    public float[]? Color { get; set; }
    public float Size { get; set; } = 0.3f;
}

public sealed class ObjectInstanceEntry
{
    public int Slot { get; set; }
    public int OwningRoomId { get; set; } = -1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rotation { get; set; }
}
