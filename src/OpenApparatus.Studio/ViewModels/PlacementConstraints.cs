namespace OpenApparatus.Studio.ViewModels;

/// <summary>
/// Spatial constraints applied to object placements so behavioural studies
/// aren't confounded by accidental layout effects (objects clustered near a
/// door, isolated in a corner, etc.).
///
/// Constraints are <em>validation only</em> — they never block placement or
/// export. The editor draws compliance overlays (zones, exclusion discs,
/// violator rings) so the user can see immediately whether a placement
/// satisfies them.
///
/// Bound floats use 0 to mean "no bound on this side". A min of 0 imposes no
/// lower bound (everything passes); a max of 0 imposes no upper bound. When
/// both are 0 for a constraint group the group is effectively no-op even with
/// its enabled flag set.
/// </summary>
public sealed class PlacementConstraints
{
    // ---- Object ↔ Object ----
    public bool ObjectToObjectEnabled { get; set; }
    public float ObjectToObjectMin { get; set; }
    public float ObjectToObjectMax { get; set; }

    /// <summary>When true, the object-to-object constraint applies to every
    /// pair whose rooms are connected by a traversable passage (open or
    /// doorway), not just within a single room. Closed walls are ignored.</summary>
    public bool ObjectToObjectAcrossConnectedRooms { get; set; }

    // ---- Door → Object ----
    public bool DoorToObjectEnabled { get; set; }
    public float DoorToObjectMin { get; set; }
    public float DoorToObjectMax { get; set; }

    /// <summary>When true (default), an object must satisfy the door
    /// constraint relative to <em>every</em> door of its room. When false,
    /// satisfying any one door is enough.</summary>
    public bool DoorAppliesToEveryDoor { get; set; } = true;

    public bool DoorAngleBandEnabled { get; set; }
    public float DoorAngleMinDeg { get; set; }
    public float DoorAngleMaxDeg { get; set; }

    // ---- Object → Wall ----
    public bool ObjectToWallEnabled { get; set; }
    public float ObjectToWallMin { get; set; }

    // ---- Per-room counts ----
    public bool PerRoomCountsEnabled { get; set; }
    public int PerRoomCountMin { get; set; }
    public int PerRoomCountMax { get; set; }

    // ---- Visualisation ----
    public bool HighlightViolations { get; set; } = true;
}

/// <summary>One reason an object (or a room, for count constraints) violates
/// the active set of placement constraints.</summary>
public sealed class ConstraintViolation
{
    public int? ObjectIndex { get; set; }
    public int? RoomId { get; set; }
    public string Message { get; set; } = "";
}
