using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public const int SchemaVersion = 2;

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
        MultiRoomEnvironment? environment)
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
            },
            Grid = new GridSection
            {
                Width = gridW,
                Length = gridL,
                Tiles = tiles,
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

            doc.Rooms.Add(entry);
        }

        return doc;
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
        public List<RoomEntry> Rooms { get; set; } = new();
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
