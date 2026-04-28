namespace OpenApparatus.Studio.ViewModels;

/// <summary>How the placement-constraint overlay paints valid sub-cells.
/// <see cref="Area"/> tints only the cells whose centre satisfies every active
/// constraint — a fast approximation of the continuous valid region.
/// <see cref="PlacementGrid"/> additionally tints partially-valid cells in
/// yellow (any corner inside the valid region but the centre out), so the
/// user can see the fuzzy boundary at the resolution of the placement grid.</summary>
public enum ConstraintHighlightMode
{
    Area = 0,
    PlacementGrid = 1,
}

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

    /// <summary>When true, the valid-placement overlay is drawn for every
    /// room (each tinted with that room's wall colour so they read as
    /// distinct). When false, the overlay is scoped to the room currently in
    /// focus — the room containing the selected sub-cell or selected object —
    /// and is hidden entirely otherwise. Default true so first-time users see
    /// the full picture.</summary>
    public bool ShowAllConstraints { get; set; } = true;

    /// <summary>How the valid-region overlay paints sub-cells. See
    /// <see cref="ConstraintHighlightMode"/>.</summary>
    public ConstraintHighlightMode HighlightMode { get; set; } = ConstraintHighlightMode.Area;

    /// <summary>Field-by-field copy from another instance. Used by
    /// project-file load to refresh the existing VM-owned Constraints
    /// instance without breaking bindings to it.</summary>
    public void CopyFrom(PlacementConstraints o)
    {
        ObjectToObjectEnabled = o.ObjectToObjectEnabled;
        ObjectToObjectMin = o.ObjectToObjectMin;
        ObjectToObjectMax = o.ObjectToObjectMax;
        ObjectToObjectAcrossConnectedRooms = o.ObjectToObjectAcrossConnectedRooms;
        DoorToObjectEnabled = o.DoorToObjectEnabled;
        DoorToObjectMin = o.DoorToObjectMin;
        DoorToObjectMax = o.DoorToObjectMax;
        DoorAppliesToEveryDoor = o.DoorAppliesToEveryDoor;
        DoorAngleBandEnabled = o.DoorAngleBandEnabled;
        DoorAngleMinDeg = o.DoorAngleMinDeg;
        DoorAngleMaxDeg = o.DoorAngleMaxDeg;
        ObjectToWallEnabled = o.ObjectToWallEnabled;
        ObjectToWallMin = o.ObjectToWallMin;
        PerRoomCountsEnabled = o.PerRoomCountsEnabled;
        PerRoomCountMin = o.PerRoomCountMin;
        PerRoomCountMax = o.PerRoomCountMax;
        HighlightViolations = o.HighlightViolations;
        ShowAllConstraints = o.ShowAllConstraints;
        HighlightMode = o.HighlightMode;
    }
}

/// <summary>One reason an object (or a room, for count constraints) violates
/// the active set of placement constraints.</summary>
public sealed class ConstraintViolation
{
    public int? ObjectIndex { get; set; }
    public int? RoomId { get; set; }
    public string Message { get; set; } = "";
}
