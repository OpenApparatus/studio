using OpenApparatus.Studio.ViewModels;

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
    public int FloorHeightCells { get; set; }
    public int RectangleRoomCount { get; set; }
    public float TileSize { get; set; }
    public float WallThickness { get; set; }
    public float WallHeight { get; set; }
    public int Seed { get; set; }
    public bool IncludeOuterEntrance { get; set; }
    public float DoorWidth { get; set; }
    public float DoorHeight { get; set; }

    public static FloorPlanSpec From(MainWindowViewModel vm) => new()
    {
        SchemaVersion = 1,
        FloorWidthCells = vm.FloorWidthCells,
        FloorHeightCells = vm.FloorHeightCells,
        RectangleRoomCount = vm.RectangleRoomCount,
        TileSize = vm.TileSize,
        WallThickness = vm.WallThickness,
        WallHeight = vm.WallHeight,
        Seed = vm.Seed,
        IncludeOuterEntrance = vm.IncludeOuterEntrance,
        DoorWidth = vm.DoorWidth,
        DoorHeight = vm.DoorHeight,
    };

    public void ApplyTo(MainWindowViewModel vm)
    {
        // Note: setting these triggers OnXChanged → Regenerate(), but we do a final
        // explicit Regenerate() in the caller to guarantee one consistent build.
        vm.FloorWidthCells = FloorWidthCells;
        vm.FloorHeightCells = FloorHeightCells;
        vm.RectangleRoomCount = RectangleRoomCount;
        vm.TileSize = TileSize;
        vm.WallThickness = WallThickness;
        vm.WallHeight = WallHeight;
        vm.Seed = Seed;
        vm.IncludeOuterEntrance = IncludeOuterEntrance;
        vm.DoorWidth = DoorWidth;
        vm.DoorHeight = DoorHeight;
    }
}
