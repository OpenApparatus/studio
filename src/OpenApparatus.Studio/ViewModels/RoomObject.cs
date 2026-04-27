using System.Numerics;

namespace OpenApparatus.Studio.ViewModels;

/// <summary>
/// One placed object in a room. Position is world XYZ in metres; Rotation is
/// radians around Y (yaw). OwningRoomId is the room the object belongs to —
/// re-evaluated on Rebuild() so an object whose room shrinks or moves is
/// reassigned to whatever room now contains its world position.
/// <see cref="Slot"/> is a 1-based index into the VM's ObjectTypes list.
/// </summary>
public sealed class RoomObject
{
    public int OwningRoomId { get; set; }
    public int Slot { get; set; }
    public Vector3 Position { get; set; }
    public float Rotation { get; set; }
}

/// <summary>Shape primitive for object types. Procedural geometry is generated
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

/// <summary>
/// One user-editable object type. The user starts with one of these in the
/// inspector and can add more via the 'Add object type' button. Hotkey
/// 1..ObjectTypes.Count places an instance of the matching type.
/// </summary>
public sealed class ObjectType
{
    public string Name { get; set; } = "";
    public ObjectShape Shape { get; set; }
    public Vector3 Color { get; set; }
    public float Size { get; set; } = 0.30f;
}
