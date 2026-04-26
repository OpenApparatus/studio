using System.Numerics;

namespace OpenApparatus.Studio.ViewModels;

/// <summary>
/// One placed object in a room. Position is world XYZ in metres; Rotation is
/// radians around Y (yaw). OwningRoomId is the room the object belongs to —
/// re-evaluated on Rebuild() so an object whose room shrinks or moves is
/// reassigned to whatever room now contains its world position.
/// </summary>
public sealed class RoomObject
{
    public int OwningRoomId { get; set; }
    public int Slot { get; set; }
    public Vector3 Position { get; set; }
    public float Rotation { get; set; }
}

/// <summary>Shape primitive for object slots. Procedural geometry is generated
/// in <c>GltfExporter</c> at export time; the editor view renders a 2D icon
/// that matches the same shape category.</summary>
public enum ObjectShape
{
    Cube,
    Sphere,
    Cylinder,
    Cone,
    Capsule,
    Pyramid,
    SquatCylinder,
}

public sealed class ObjectSlot
{
    public int Id { get; init; }
    public ObjectShape Shape { get; init; }
    public Vector3 Color { get; init; }
    public float Size { get; init; }
    public string DisplayName { get; init; } = "";
}

/// <summary>The nine fixed object presets. Index 0 → slot 1.</summary>
public static class ObjectSlots
{
    public static readonly ObjectSlot[] All = new[]
    {
        new ObjectSlot { Id = 1, Shape = ObjectShape.Cube,          Color = new(0.85f, 0.30f, 0.30f), Size = 0.30f, DisplayName = "Red cube" },
        new ObjectSlot { Id = 2, Shape = ObjectShape.Sphere,        Color = new(0.27f, 0.50f, 0.85f), Size = 0.30f, DisplayName = "Blue sphere" },
        new ObjectSlot { Id = 3, Shape = ObjectShape.Cylinder,      Color = new(0.32f, 0.70f, 0.40f), Size = 0.30f, DisplayName = "Green cylinder" },
        new ObjectSlot { Id = 4, Shape = ObjectShape.Cone,          Color = new(0.95f, 0.78f, 0.25f), Size = 0.30f, DisplayName = "Yellow cone" },
        new ObjectSlot { Id = 5, Shape = ObjectShape.Capsule,       Color = new(0.85f, 0.32f, 0.78f), Size = 0.30f, DisplayName = "Magenta capsule" },
        new ObjectSlot { Id = 6, Shape = ObjectShape.Pyramid,       Color = new(0.30f, 0.78f, 0.85f), Size = 0.30f, DisplayName = "Cyan pyramid" },
        new ObjectSlot { Id = 7, Shape = ObjectShape.SquatCylinder, Color = new(0.95f, 0.55f, 0.20f), Size = 0.40f, DisplayName = "Orange disc" },
        new ObjectSlot { Id = 8, Shape = ObjectShape.Cube,          Color = new(0.55f, 0.30f, 0.78f), Size = 0.30f, DisplayName = "Purple cube" },
        new ObjectSlot { Id = 9, Shape = ObjectShape.Sphere,        Color = new(0.95f, 0.55f, 0.78f), Size = 0.35f, DisplayName = "Pink sphere" },
    };

    /// <summary>Looks up a slot by 1-based id (1..9). Returns null if out of range.</summary>
    public static ObjectSlot? Get(int slotId)
        => slotId >= 1 && slotId <= All.Length ? All[slotId - 1] : null;
}
