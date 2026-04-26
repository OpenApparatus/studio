using OpenApparatus.Studio.ViewModels;
using OpenApparatus.Topology.Generators;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Plain-data record of all generator/assigner parameters needed to reproduce a
/// floor plan. This is the on-disk format for .floorplan.json files — it does not
/// include the materialized FloorPlan, since that's regenerated deterministically
/// from these inputs.
/// </summary>
public sealed class FloorPlanSpec
{
    public int SchemaVersion { get; set; } = 1;
    public int FloorWidthCells { get; set; }
    public int FloorLengthCells { get; set; }
    public int RectangleRoomCount { get; set; }
    public RectangleOrientation RectangleOrientation { get; set; } = RectangleOrientation.Random;
    public float TileSize { get; set; }
    public float WallThickness { get; set; }
    public float WallHeight { get; set; }
    public int Seed { get; set; }
    public bool IncludeOuterEntrance { get; set; }
    public StartingRoomTypeChoice StartingRoomType { get; set; } = StartingRoomTypeChoice.NoPreference;
    public float DoorWidth { get; set; }
    public float DoorHeight { get; set; }

    public static FloorPlanSpec From(MainWindowViewModel vm) => new()
    {
        SchemaVersion = 1,
        FloorWidthCells = vm.FloorWidthCells,
        FloorLengthCells = vm.FloorLengthCells,
        RectangleRoomCount = vm.RectangleRoomCount,
        RectangleOrientation = vm.RectangleOrientation,
        TileSize = vm.TileSize,
        WallThickness = vm.WallThickness,
        WallHeight = vm.WallHeight,
        Seed = vm.Seed,
        IncludeOuterEntrance = vm.IncludeOuterEntrance,
        StartingRoomType = vm.StartingRoomType,
        DoorWidth = vm.DoorWidth,
        DoorHeight = vm.DoorHeight,
    };

    public void ApplyTo(MainWindowViewModel vm)
    {
        vm.FloorWidthCells = FloorWidthCells;
        vm.FloorLengthCells = FloorLengthCells;
        vm.RectangleRoomCount = RectangleRoomCount;
        vm.RectangleOrientation = RectangleOrientation;
        vm.TileSize = TileSize;
        vm.WallThickness = WallThickness;
        vm.WallHeight = WallHeight;
        vm.Seed = Seed;
        vm.IncludeOuterEntrance = IncludeOuterEntrance;
        vm.StartingRoomType = StartingRoomType;
        vm.DoorWidth = DoorWidth;
        vm.DoorHeight = DoorHeight;
    }
}
