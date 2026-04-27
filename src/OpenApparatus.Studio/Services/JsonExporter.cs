using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenApparatus.Studio.ViewModels;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Writes the editor's authored state to a self-describing JSON document. Each
/// room is a top-level container that carries its own walls — each wall is
/// described from that room's perspective (start/end follow the room's interior
/// being on the +N side; <see cref="WallEntry.NeighborRoomId"/> is null for
/// outer walls). Internal walls therefore appear once in each adjoining room.
/// </summary>
public static class JsonExporter
{
    public const int SchemaVersion = 3;

    public static void Export(TextWriter w, EnvironmentDocument doc)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        w.Write(JsonSerializer.Serialize(doc, options));
    }

    public static EnvironmentDocument BuildDocument(
        int[,] roomGrid,
        float tileSize,
        float wallThickness,
        float wallHeight,
        float doorWidth,
        float doorHeight,
        float windowWidth,
        float windowHeight,
        float windowSillHeight,
        MultiRoomEnvironment? environment,
        int gridSubdivision = 1,
        float defaultObjectY = 0f,
        IReadOnlyList<RoomObject>? objects = null,
        IReadOnlyList<ObjectType>? objectTypes = null,
        PlacementConstraints? constraints = null)
    {
        int gridW = roomGrid.GetLength(0);
        int gridL = roomGrid.GetLength(1);
        var tiles = new int[gridW][];
        for (int x = 0; x < gridW; x++)
        {
            tiles[x] = new int[gridL];
            for (int z = 0; z < gridL; z++)
                tiles[x][z] = roomGrid[x, z];
        }

        var doc = new EnvironmentDocument
        {
            Version = SchemaVersion,
            Parameters = new ParametersSection
            {
                TileSize = tileSize,
                WallThickness = wallThickness,
                WallHeight = wallHeight,
                DoorWidth = doorWidth,
                DoorHeight = doorHeight,
                WindowWidth = windowWidth,
                WindowHeight = windowHeight,
                WindowSillHeight = windowSillHeight,
                GridSubdivision = gridSubdivision,
                DefaultObjectY = defaultObjectY,
            },
            Grid = new GridSection
            {
                Width = gridW,
                Length = gridL,
                Tiles = tiles,
            },
            ObjectSlots = BuildSlotDefinitions(objectTypes),
            PlacementConstraints = constraints is null ? null : new PlacementConstraintsSection
            {
                ObjectToObjectEnabled = constraints.ObjectToObjectEnabled,
                ObjectToObjectMin = constraints.ObjectToObjectMin,
                ObjectToObjectMax = constraints.ObjectToObjectMax,
                ObjectToObjectAcrossConnectedRooms = constraints.ObjectToObjectAcrossConnectedRooms,
                DoorToObjectEnabled = constraints.DoorToObjectEnabled,
                DoorToObjectMin = constraints.DoorToObjectMin,
                DoorToObjectMax = constraints.DoorToObjectMax,
                DoorAppliesToEveryDoor = constraints.DoorAppliesToEveryDoor,
                DoorAngleBandEnabled = constraints.DoorAngleBandEnabled,
                DoorAngleMinDeg = constraints.DoorAngleMinDeg,
                DoorAngleMaxDeg = constraints.DoorAngleMaxDeg,
                ObjectToWallEnabled = constraints.ObjectToWallEnabled,
                ObjectToWallMin = constraints.ObjectToWallMin,
                PerRoomCountsEnabled = constraints.PerRoomCountsEnabled,
                PerRoomCountMin = constraints.PerRoomCountMin,
                PerRoomCountMax = constraints.PerRoomCountMax,
            },
        };

        if (environment is null) return doc;

        // Collect tiles per room from the grid (cheap second pass — keeps the
        // JSON readable without forcing callers to compute it themselves).
        var tilesByRoom = new Dictionary<int, List<int[]>>();
        for (int x = 0; x < gridW; x++)
            for (int z = 0; z < gridL; z++)
            {
                int id = roomGrid[x, z];
                if (id < 0) continue;
                if (!tilesByRoom.TryGetValue(id, out var list))
                    tilesByRoom[id] = list = new List<int[]>();
                list.Add(new[] { x, z });
            }

        foreach (var room in environment.Rooms)
        {
            var entry = new RoomEntry
            {
                Id = room.Id,
                Position = new[] { room.Position.X, room.Position.Y },
                Tiles = tilesByRoom.TryGetValue(room.Id, out var t) ? t : new List<int[]>(),
                Shape = ShapeFor(room),
            };

            int wallNumber = 1;
            foreach (var adj in environment.Adjacencies)
            {
                if (adj.RoomA != room && adj.RoomB != room) continue;

                bool roomIsA = adj.RoomA == room;
                var seg = adj.SharedSegment;
                // Orient the wall segment so the room being described is on the
                // +N (left of Start→End) side. RoomA is already on +N; flip for
                // RoomB.
                var start = roomIsA ? seg.Start : seg.End;
                var end   = roomIsA ? seg.End   : seg.Start;

                var wall = new WallEntry
                {
                    Number = wallNumber++,
                    Side = SideLabel(room, seg),
                    Start = new[] { start.X, start.Y },
                    End = new[] { end.X, end.Y },
                    NeighborRoomId = roomIsA ? adj.RoomB?.Id : adj.RoomA.Id,
                    Passage = PassageFor(adj.Passage),
                };
                entry.Walls.Add(wall);
            }

            // Per-room object instances. Rooms without objects emit an empty
            // list (omitted by JsonIgnoreCondition.WhenWritingNull when the
            // collection itself is null — we leave it null to keep small docs
            // tidy).
            if (objects != null)
            {
                List<ObjectInstanceEntry>? objList = null;
                foreach (var o in objects)
                {
                    if (o.OwningRoomId != room.Id) continue;
                    objList ??= new List<ObjectInstanceEntry>();
                    objList.Add(new ObjectInstanceEntry
                    {
                        Slot = o.Slot,
                        Position = new[] { o.Position.X, o.Position.Y, o.Position.Z },
                        Rotation = o.Rotation,
                    });
                }
                entry.Objects = objList;
            }

            doc.Rooms.Add(entry);
        }

        // Outside section: anything with OwningRoomId == -1 (or pointing at a
        // room that doesn't exist in the env) gets bucketed here so importers
        // can still find every object.
        if (objects != null)
        {
            var validRoomIds = new HashSet<int>();
            foreach (var r in environment.Rooms) validRoomIds.Add(r.Id);
            List<ObjectInstanceEntry>? outsideList = null;
            foreach (var o in objects)
            {
                if (validRoomIds.Contains(o.OwningRoomId)) continue;
                outsideList ??= new List<ObjectInstanceEntry>();
                outsideList.Add(new ObjectInstanceEntry
                {
                    Slot = o.Slot,
                    Position = new[] { o.Position.X, o.Position.Y, o.Position.Z },
                    Rotation = o.Rotation,
                });
            }
            if (outsideList != null)
                doc.Outside = new OutsideSection { Objects = outsideList };
        }

        return doc;
    }

    static List<ObjectSlotEntry> BuildSlotDefinitions(IReadOnlyList<ObjectType>? types)
    {
        var list = new List<ObjectSlotEntry>(types?.Count ?? 0);
        if (types is null) return list;
        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i];
            list.Add(new ObjectSlotEntry
            {
                Id = i + 1,
                Shape = t.Shape.ToString().ToLowerInvariant(),
                Color = new[] { t.Color.X, t.Color.Y, t.Color.Z },
                Size = t.Size,
                DisplayName = t.Name,
            });
        }
        return list;
    }

    static ShapeSection ShapeFor(Room room) => room.Shape switch
    {
        RectangleShape r => new ShapeSection { Type = "rectangle", Width = r.Width, Depth = r.Depth },
        _ => new ShapeSection { Type = room.Shape.GetType().Name },
    };

    static PassageSection PassageFor(Passage p)
    {
        switch (p)
        {
            case Passage.Open:
                return new PassageSection { Type = "open" };
            case Passage.Closed:
                return new PassageSection { Type = "closed" };
            case Passage.Doorway dw:
                var ops = new List<OpeningEntry>();
                foreach (var op in dw.Openings)
                    ops.Add(new OpeningEntry
                    {
                        OffsetAlongEdge = op.OffsetAlongEdge,
                        Width = op.Width,
                        Height = op.Height,
                        SillHeight = op.SillHeight,
                    });
                return new PassageSection { Type = "doorway", Openings = ops };
            default:
                return new PassageSection { Type = "closed" };
        }
    }

    /// <summary>
    /// Best-effort cardinal label for a wall on a rectangular room (north / south /
    /// east / west). Useful as a hint when reading the JSON; not authoritative.
    /// </summary>
    static string? SideLabel(Room room, EdgeSegment seg)
    {
        if (room.Shape is not RectangleShape) return null;
        var mid = seg.Midpoint;
        var center = room.Position + new Vector2(
            ((RectangleShape)room.Shape).Width * 0.5f,
            ((RectangleShape)room.Shape).Depth * 0.5f);
        float dx = mid.X - center.X;
        float dz = mid.Y - center.Y;
        if (System.Math.Abs(dx) > System.Math.Abs(dz))
            return dx > 0 ? "east" : "west";
        return dz > 0 ? "north" : "south";
    }

    public sealed class EnvironmentDocument
    {
        public int Version { get; set; }
        public ParametersSection Parameters { get; set; } = new();
        public GridSection Grid { get; set; } = new();
        public List<ObjectSlotEntry> ObjectSlots { get; set; } = new();
        public List<RoomEntry> Rooms { get; set; } = new();
        /// <summary>Objects placed outside any room (OwningRoomId == -1).
        /// Null when there are none, so small documents stay tidy.</summary>
        public OutsideSection? Outside { get; set; }
        /// <summary>Active placement constraints. Null = constraints feature
        /// not used; otherwise a snapshot of every threshold + toggle.</summary>
        public PlacementConstraintsSection? PlacementConstraints { get; set; }
    }

    public sealed class PlacementConstraintsSection
    {
        public bool ObjectToObjectEnabled { get; set; }
        public float ObjectToObjectMin { get; set; }
        public float ObjectToObjectMax { get; set; }
        public bool ObjectToObjectAcrossConnectedRooms { get; set; }
        public bool DoorToObjectEnabled { get; set; }
        public float DoorToObjectMin { get; set; }
        public float DoorToObjectMax { get; set; }
        public bool DoorAppliesToEveryDoor { get; set; }
        public bool DoorAngleBandEnabled { get; set; }
        public float DoorAngleMinDeg { get; set; }
        public float DoorAngleMaxDeg { get; set; }
        public bool ObjectToWallEnabled { get; set; }
        public float ObjectToWallMin { get; set; }
        public bool PerRoomCountsEnabled { get; set; }
        public int PerRoomCountMin { get; set; }
        public int PerRoomCountMax { get; set; }
    }

    public sealed class OutsideSection
    {
        public List<ObjectInstanceEntry> Objects { get; set; } = new();
    }

    public sealed class ParametersSection
    {
        public float TileSize { get; set; }
        public float WallThickness { get; set; }
        public float WallHeight { get; set; }
        public float DoorWidth { get; set; }
        public float DoorHeight { get; set; }
        public float WindowWidth { get; set; }
        public float WindowHeight { get; set; }
        public float WindowSillHeight { get; set; }
        public int GridSubdivision { get; set; } = 1;
        public float DefaultObjectY { get; set; }
    }

    public sealed class GridSection
    {
        public int Width { get; set; }
        public int Length { get; set; }
        public int[][] Tiles { get; set; } = System.Array.Empty<int[]>();
    }

    public sealed class RoomEntry
    {
        public int Id { get; set; }
        public ShapeSection Shape { get; set; } = new();
        public float[] Position { get; set; } = System.Array.Empty<float>();
        public List<int[]> Tiles { get; set; } = new();
        public List<WallEntry> Walls { get; set; } = new();
        public List<ObjectInstanceEntry>? Objects { get; set; }
    }

    public sealed class ObjectSlotEntry
    {
        public int Id { get; set; }
        public string Shape { get; set; } = "";
        public float[] Color { get; set; } = System.Array.Empty<float>();
        public float Size { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public sealed class ObjectInstanceEntry
    {
        public int Slot { get; set; }
        public float[] Position { get; set; } = System.Array.Empty<float>();
        public float Rotation { get; set; }
    }

    public sealed class ShapeSection
    {
        public string Type { get; set; } = "";
        public float? Width { get; set; }
        public float? Depth { get; set; }
    }

    public sealed class WallEntry
    {
        public int Number { get; set; }
        public string? Side { get; set; }
        public float[] Start { get; set; } = System.Array.Empty<float>();
        public float[] End { get; set; } = System.Array.Empty<float>();
        public int? NeighborRoomId { get; set; }
        public PassageSection Passage { get; set; } = new();
    }

    public sealed class PassageSection
    {
        public string Type { get; set; } = "closed";
        public List<OpeningEntry>? Openings { get; set; }
    }

    public sealed class OpeningEntry
    {
        public float OffsetAlongEdge { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float SillHeight { get; set; }
    }
}
