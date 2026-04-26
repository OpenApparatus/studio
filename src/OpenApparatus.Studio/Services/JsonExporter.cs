using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Writes the editor's authored state to a self-describing JSON document.
/// The layout, passage overrides, and the parameters needed to rebuild the
/// environment are all captured so a future load step can fully restore it.
/// </summary>
public static class JsonExporter
{
    public const int SchemaVersion = 1;

    public static void Export(TextWriter w, EnvironmentDocument doc)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
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
        int w = roomGrid.GetLength(0);
        int l = roomGrid.GetLength(1);
        var tiles = new int[w][];
        for (int x = 0; x < w; x++)
        {
            tiles[x] = new int[l];
            for (int z = 0; z < l; z++)
                tiles[x][z] = roomGrid[x, z];
        }

        var passages = new List<PassageEntry>();
        if (environment is not null)
        {
            foreach (var adj in environment.Adjacencies)
            {
                var mid = adj.SharedSegment.Midpoint;
                var entry = new PassageEntry
                {
                    WallMidpoint = new[] { mid.X, mid.Y },
                    Type = adj.Passage switch
                    {
                        Passage.Open => "open",
                        Passage.Closed => "closed",
                        Passage.Doorway => "doorway",
                        _ => "closed",
                    },
                };
                if (adj.Passage is Passage.Doorway dw)
                {
                    var list = new List<OpeningEntry>();
                    foreach (var op in dw.Openings)
                    {
                        list.Add(new OpeningEntry
                        {
                            OffsetAlongEdge = op.OffsetAlongEdge,
                            Width = op.Width,
                            Height = op.Height,
                            SillHeight = op.SillHeight,
                        });
                    }
                    entry.Openings = list;
                }
                passages.Add(entry);
            }
        }

        return new EnvironmentDocument
        {
            Version = SchemaVersion,
            TileSize = tileSize,
            WallThickness = wallThickness,
            WallHeight = wallHeight,
            DoorWidth = doorWidth,
            DoorHeight = doorHeight,
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
            WindowSillHeight = windowSillHeight,
            Grid = new GridSection
            {
                Width = w,
                Length = l,
                Tiles = tiles,
            },
            Passages = passages,
        };
    }

    public sealed class EnvironmentDocument
    {
        public int Version { get; set; }
        public float TileSize { get; set; }
        public float WallThickness { get; set; }
        public float WallHeight { get; set; }
        public float DoorWidth { get; set; }
        public float DoorHeight { get; set; }
        public float WindowWidth { get; set; }
        public float WindowHeight { get; set; }
        public float WindowSillHeight { get; set; }
        public GridSection Grid { get; set; } = new();
        public List<PassageEntry> Passages { get; set; } = new();
    }

    public sealed class GridSection
    {
        public int Width { get; set; }
        public int Length { get; set; }
        public int[][] Tiles { get; set; } = System.Array.Empty<int[]>();
    }

    public sealed class PassageEntry
    {
        public float[] WallMidpoint { get; set; } = System.Array.Empty<float>();
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
