using System.Collections.Generic;
using System.Numerics;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.ViewModels;

/// <summary>
/// A frozen copy of the editor's authored state. Used by the undo / redo
/// stacks: every mutating action calls <see cref="Capture"/> before it runs,
/// the snapshot is pushed on the undo stack, and <see cref="Restore"/> rolls
/// the VM back when the user invokes Undo.
///
/// Captures only the state the user can directly mutate — selection /
/// transient view state (zoom, pan, ShowPaths, opacity) is intentionally
/// excluded so undo doesn't cause the camera to jump around.
/// </summary>
public sealed class Snapshot
{
    public int[,] RoomGrid = new int[0, 0];

    public Dictionary<(int, int), (Passage Passage, Vector2 Start)> PassageOverrides = new();
    public Dictionary<(int RoomId, int MidX, int MidZ), Vector3> WallColors = new();
    public Dictionary<int, Vector3> RoomFloorColors = new();
    public Dictionary<int, Vector3> RoomCeilingColors = new();
    public Dictionary<int, Vector3> RoomSingleWallColors = new();
    public HashSet<int> MultiColorRoomIds = new();
    public Dictionary<int, string> RoomNames = new();

    public List<RoomObject> Objects = new();
    public List<ObjectType> ObjectTypes = new();

    public int NextRoomId;
    public int GridWidth;
    public int GridLength;
    public float TileSize;
    public float WallThickness;
    public float WallHeight;
    public float DoorWidth;
    public float DoorHeight;
    public float WindowWidth;
    public float WindowHeight;
    public float WindowSillHeight;
    public int GridSubdivision;
    public float DefaultObjectY;
    public Vector3 DefaultFloorColor;
    public Vector3 DefaultCeilingColor;

    public static Snapshot Capture(MainWindowViewModel vm)
    {
        var s = new Snapshot
        {
            RoomGrid = (int[,])vm.RoomGrid.Clone(),
            NextRoomId = vm.NextRoomIdRaw,
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
            DefaultFloorColor = vm.DefaultFloorColor,
            DefaultCeilingColor = vm.DefaultCeilingColor,
        };

        foreach (var kv in vm.PassageOverridesRaw) s.PassageOverrides[kv.Key] = kv.Value;
        foreach (var kv in vm.WallColors) s.WallColors[kv.Key] = kv.Value;
        foreach (var kv in vm.RoomFloorColors) s.RoomFloorColors[kv.Key] = kv.Value;
        foreach (var kv in vm.RoomCeilingColors) s.RoomCeilingColors[kv.Key] = kv.Value;
        foreach (var kv in vm.RoomSingleWallColors) s.RoomSingleWallColors[kv.Key] = kv.Value;
        foreach (var id in vm.MultiColorRoomIds) s.MultiColorRoomIds.Add(id);
        foreach (var kv in vm.RoomNames) s.RoomNames[kv.Key] = kv.Value;

        foreach (var o in vm.Objects)
            s.Objects.Add(new RoomObject
            {
                OwningRoomId = o.OwningRoomId,
                Slot = o.Slot,
                Position = o.Position,
                Rotation = o.Rotation,
            });
        foreach (var t in vm.ObjectTypes)
            s.ObjectTypes.Add(new ObjectType
            {
                Name = t.Name,
                Shape = t.Shape,
                Color = t.Color,
                Size = t.Size,
            });

        return s;
    }

    public void Restore(MainWindowViewModel vm)
        => vm.RestoreFromSnapshot(this);
}
