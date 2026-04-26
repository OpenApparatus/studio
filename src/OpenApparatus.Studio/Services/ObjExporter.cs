using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using OpenApparatus.Geometry;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Writes a generated MultiRoomEnvironment as Wavefront .obj geometry. Two
/// outputs are supported:
///
/// • <see cref="ExportCombined"/> — one .obj with every room as a separate
///   group/object (`g`/`o`). Unity's built-in OBJ importer collapses the whole
///   file into a single Mesh asset (groups become sub-meshes), so this is best
///   for tools like Blender that respect `o`.
///
/// • <see cref="ExportPerRoom"/> — one .obj per room, all sharing one .mtl.
///   This is the Unity-friendly path: each file imports as its own model
///   prefab which the user parents under a single GameObject to recover the
///   per-room hierarchy.
///
/// In both modes each room emits one floor, one ceiling, and one wall_&lt;i&gt;
/// per adjacency it touches. Internal walls split on face normal — RoomA owns
/// the +N body face, RoomB owns the -N body face, frame pieces (top, bottom,
/// caps, tunnel jambs/lintels/sills/thresholds) belong to the lower-id room.
/// Each piece carries a unique material name so re-skinning one room never
/// alters the other's view of the shared wall.
/// </summary>
public static class ObjExporter
{
    public const string FloorMaterialPrefix = "OpenApparatus_Floor";
    public const string WallsMaterialPrefix = "OpenApparatus_Walls";
    public const string CeilingMaterialPrefix = "OpenApparatus_Ceiling";

    /// <summary>
    /// Writes one .obj containing every room. Returns the materials referenced
    /// across all rooms so the caller can drop them into a sidecar .mtl.
    /// </summary>
    public static IReadOnlyList<MaterialSlot> ExportCombined(
        TextWriter w,
        MultiRoomEnvironment plan,
        float wallThickness,
        float wallHeight,
        string? mtlLibFileName = null)
    {
        var slots = new List<MaterialSlot>();
        var rooms = BuildRoomGroups(plan, wallThickness, wallHeight, slots);

        var pool = new GeometryPool();
        var groups = new List<GroupRecord>();
        foreach (var room in rooms)
            foreach (var g in room.Groups)
                groups.Add(g.RebaseInto(pool));

        WriteFile(w, mtlLibFileName,
            header: $"{plan.Rooms.Count} rooms, {plan.Adjacencies.Count} walls, {groups.Count} groups",
            pool, groups);

        return slots;
    }

    /// <summary>
    /// Writes one .obj per room into <paramref name="folder"/> using
    /// <paramref name="baseName"/> as the prefix (files become
    /// <c>{baseName}_room_{id}.obj</c>). Each file is fully self-contained and
    /// references the shared <paramref name="mtlLibFileName"/>.
    /// Returns the materials referenced across all rooms.
    /// </summary>
    public static IReadOnlyList<MaterialSlot> ExportPerRoom(
        string folder,
        string baseName,
        MultiRoomEnvironment plan,
        float wallThickness,
        float wallHeight,
        string? mtlLibFileName = null)
    {
        var slots = new List<MaterialSlot>();
        var rooms = BuildRoomGroups(plan, wallThickness, wallHeight, slots);

        Directory.CreateDirectory(folder);
        foreach (var room in rooms)
        {
            var pool = new GeometryPool();
            var groups = new List<GroupRecord>();
            foreach (var g in room.Groups)
                groups.Add(g.RebaseInto(pool));

            string path = Path.Combine(folder, $"{baseName}_room_{room.RoomId}.obj");
            using var w = new StreamWriter(path);
            WriteFile(w, mtlLibFileName,
                header: $"room #{room.RoomId} — {groups.Count} groups",
                pool, groups);
        }

        return slots;
    }

    static List<RoomGroups> BuildRoomGroups(
        MultiRoomEnvironment plan, float wallThickness, float wallHeight,
        List<MaterialSlot> slotsOut)
    {
        var interiorBuilder = new RectangleInteriorBuilder();
        var wallBuilder = new BoundaryWallBuilder();

        // Pre-build walls once so the same MeshData backs both rooms' splits.
        var wallMeshes = new Dictionary<Adjacency, MeshData>();
        foreach (var adj in plan.Adjacencies)
            wallMeshes[adj] = wallBuilder.Build(adj, wallThickness, wallHeight);

        var result = new List<RoomGroups>();
        foreach (var room in plan.Rooms)
        {
            var interior = interiorBuilder.Build(room, wallThickness, wallHeight);
            var roomAdjacencies = new List<Adjacency>();
            foreach (var adj in plan.Adjacencies)
                if (adj.RoomA == room || adj.RoomB == room)
                    roomAdjacencies.Add(adj);

            var rg = new RoomGroups(room.Id);

            // Floor — interior + threshold-floor strips from owned walls.
            var floor = new SourceTriBucket();
            floor.AddSubmesh(interior, SubmeshIndex.Floor);
            foreach (var adj in roomAdjacencies)
            {
                if (LowerIdOwner(adj).Id != room.Id) continue;
                floor.AddSubmesh(wallMeshes[adj], SubmeshIndex.Floor);
            }
            rg.TryAdd(floor,
                $"room_{room.Id}_floor",
                $"{FloorMaterialPrefix}_Room{room.Id}",
                FloorColor, slotsOut);

            // Ceiling.
            var ceiling = new SourceTriBucket();
            ceiling.AddSubmesh(interior, SubmeshIndex.Ceiling);
            rg.TryAdd(ceiling,
                $"room_{room.Id}_ceiling",
                $"{CeilingMaterialPrefix}_Room{room.Id}",
                CeilingColor, slotsOut);

            // Walls — one per adjacency, split by face-normal alignment.
            for (int i = 0; i < roomAdjacencies.Count; i++)
            {
                var adj = roomAdjacencies[i];
                var wall = wallMeshes[adj];
                var seg = adj.SharedSegment;
                var nrm3 = new Vector3(seg.Normal.X, 0f, seg.Normal.Y);
                bool roomIsA = adj.RoomA == room;

                var wallBucket = new SourceTriBucket();
                AddSplitWallFaces(
                    wallBucket, wall, SubmeshIndex.Walls, nrm3,
                    includeBodyA: roomIsA,
                    includeBodyB: !roomIsA,
                    includeFrame: LowerIdOwner(adj).Id == room.Id);

                int wallNum = i + 1;
                rg.TryAdd(wallBucket,
                    $"room_{room.Id}_wall_{wallNum}",
                    $"{WallsMaterialPrefix}{wallNum}_Room{room.Id}",
                    WallsColor, slotsOut);
            }

            result.Add(rg);
        }
        return result;
    }

    static void WriteFile(
        TextWriter w, string? mtlLibFileName,
        string header, GeometryPool pool, List<GroupRecord> groups)
    {
        w.WriteLine("# OpenApparatus floor-plan export");
        w.WriteLine($"# {header}");
        if (!string.IsNullOrEmpty(mtlLibFileName))
            w.WriteLine($"mtllib {mtlLibFileName}");
        w.WriteLine();

        for (int i = 0; i < pool.Vertices.Count; i++)
        {
            var v = pool.Vertices[i];
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "v {0:F6} {1:F6} {2:F6}", v.X, v.Y, v.Z));
        }
        for (int i = 0; i < pool.Normals.Count; i++)
        {
            var n = pool.Normals[i];
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "vn {0:F6} {1:F6} {2:F6}", n.X, n.Y, n.Z));
        }
        for (int i = 0; i < pool.Uvs.Count; i++)
        {
            var u = pool.Uvs[i];
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "vt {0:F6} {1:F6}", u.X, u.Y));
        }
        w.WriteLine();

        foreach (var g in groups)
        {
            // Both `o` and `g` for maximum importer compatibility — Unity keys
            // sub-meshes off `g`; Blender / Maya prefer `o`. `s off` disables
            // smoothing groups so face normals stay sharp at edges.
            w.WriteLine($"o {g.Name}");
            w.WriteLine($"g {g.Name}");
            w.WriteLine($"usemtl {g.Material}");
            w.WriteLine("s off");
            for (int i = 0; i < g.Triangles.Count; i += 3)
            {
                int a = g.Triangles[i + 0] + 1;
                int b = g.Triangles[i + 1] + 1;
                int c = g.Triangles[i + 2] + 1;
                w.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
            w.WriteLine();
        }
    }

    static void AddSplitWallFaces(
        SourceTriBucket bucket, MeshData wall, int submesh, Vector3 nrm3,
        bool includeBodyA, bool includeBodyB, bool includeFrame)
    {
        const float ALIGN = 0.9f;
        var tris = wall.SubmeshIndices[submesh];
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t + 0];
            int b = tris[t + 1];
            int c = tris[t + 2];
            // Quads share a normal across all 4 verts, so any vertex's normal
            // characterizes the face.
            var n = wall.Normals[a];
            float dot = Vector3.Dot(n, nrm3);

            bool keep = dot > ALIGN ? includeBodyA
                      : dot < -ALIGN ? includeBodyB
                      : includeFrame;
            if (!keep) continue;
            bucket.AddTriangle(wall, a, b, c);
        }
    }

    static Room LowerIdOwner(Adjacency adj)
    {
        if (adj.IsOuter) return adj.RoomA;
        return adj.RoomA.Id < adj.RoomB!.Id ? adj.RoomA : adj.RoomB;
    }

    /// <summary>
    /// Writes the sidecar .mtl. Pass the slot list returned by either export
    /// path so every material referenced by the .obj files is defined.
    /// </summary>
    public static void WriteMtl(TextWriter w, IReadOnlyList<MaterialSlot> slots)
    {
        w.WriteLine("# OpenApparatus material library");
        w.WriteLine();
        // Dedupe — combined and per-room exports build the same slot set;
        // callers may pass either or merge them.
        var seen = new HashSet<string>();
        foreach (var slot in slots)
        {
            if (!seen.Add(slot.Name)) continue;
            WriteMaterial(w, slot.Name, slot.Kd);
        }
    }

    static void WriteMaterial(TextWriter w, string name, (float r, float g, float b) kd)
    {
        w.WriteLine($"newmtl {name}");
        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Kd {0:F4} {1:F4} {2:F4}", kd.r, kd.g, kd.b));
        w.WriteLine("Ka 0.0000 0.0000 0.0000");
        w.WriteLine("Ks 0.0000 0.0000 0.0000");
        w.WriteLine("Ns 10.0000");
        w.WriteLine("d 1.0000");
        w.WriteLine("illum 1");
        w.WriteLine();
    }

    static readonly (float r, float g, float b) FloorColor   = (0.55f, 0.42f, 0.30f);
    static readonly (float r, float g, float b) WallsColor   = (0.78f, 0.78f, 0.80f);
    static readonly (float r, float g, float b) CeilingColor = (0.92f, 0.92f, 0.90f);

    public readonly record struct MaterialSlot(string Name, (float r, float g, float b) Kd);

    // ---- internal helpers ------------------------------------------------------

    /// <summary>Buffers triangles by source MeshData reference for later rebasing.</summary>
    sealed class SourceTriBucket
    {
        public readonly List<(MeshData Src, int A, int B, int C)> Tris = new();

        public void AddSubmesh(MeshData src, int submeshIndex)
        {
            var tris = src.SubmeshIndices[submeshIndex];
            for (int i = 0; i < tris.Length; i += 3)
                Tris.Add((src, tris[i], tris[i + 1], tris[i + 2]));
        }

        public void AddTriangle(MeshData src, int ia, int ib, int ic)
            => Tris.Add((src, ia, ib, ic));
    }

    sealed class GeometryPool
    {
        public List<Vector3> Vertices { get; } = new();
        public List<Vector3> Normals { get; } = new();
        public List<Vector2> Uvs { get; } = new();
        readonly Dictionary<(MeshData, int), int> _index = new();

        public int Add(MeshData src, int srcIdx)
        {
            var key = (src, srcIdx);
            if (_index.TryGetValue(key, out var existing)) return existing;
            int idx = Vertices.Count;
            _index[key] = idx;
            Vertices.Add(src.Vertices[srcIdx]);
            Normals.Add(src.Normals[srcIdx]);
            Uvs.Add(src.Uv0[srcIdx]);
            return idx;
        }
    }

    sealed record GroupRecord(string Name, string Material, List<int> Triangles);

    sealed class PendingGroup
    {
        public string Name = "";
        public string Material = "";
        public SourceTriBucket Bucket = new();

        public GroupRecord RebaseInto(GeometryPool pool)
        {
            var rebased = new List<int>(Bucket.Tris.Count * 3);
            foreach (var t in Bucket.Tris)
            {
                rebased.Add(pool.Add(t.Src, t.A));
                rebased.Add(pool.Add(t.Src, t.B));
                rebased.Add(pool.Add(t.Src, t.C));
            }
            return new GroupRecord(Name, Material, rebased);
        }
    }

    sealed class RoomGroups
    {
        public int RoomId { get; }
        public List<PendingGroup> Groups { get; } = new();
        public RoomGroups(int roomId) { RoomId = roomId; }

        public void TryAdd(
            SourceTriBucket bucket, string name, string material,
            (float r, float g, float b) color, List<MaterialSlot> slotsOut)
        {
            if (bucket.Tris.Count == 0) return;
            slotsOut.Add(new MaterialSlot(material, color));
            Groups.Add(new PendingGroup { Name = name, Material = material, Bucket = bucket });
        }
    }
}
