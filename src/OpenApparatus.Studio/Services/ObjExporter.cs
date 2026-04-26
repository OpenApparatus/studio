using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using OpenApparatus.Geometry;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Writes a generated MultiRoomEnvironment as a Wavefront .obj where each room is
/// a self-contained set of OBJ objects: one floor, one ceiling, and one wall
/// object per adjacency the room participates in. Internal walls are split
/// across the two rooms — RoomA gets the +N body face (visible from inside
/// RoomA) plus all "frame" geometry (top, bottom, caps, tunnel pieces, sills,
/// thresholds), RoomB gets only the -N body face. This gives every room its own
/// material slot for its side of the wall, so re-skinning one room does not
/// alter what the other room sees.
/// </summary>
public static class ObjExporter
{
    public const string FloorMaterialPrefix = "OpenApparatus_Floor";
    public const string WallsMaterialPrefix = "OpenApparatus_Walls";
    public const string CeilingMaterialPrefix = "OpenApparatus_Ceiling";

    public static IReadOnlyList<MaterialSlot> Export(
        TextWriter w,
        MultiRoomEnvironment plan,
        float wallThickness,
        float wallHeight,
        string? mtlLibFileName = null)
    {
        var slots = new List<MaterialSlot>();
        var interiorBuilder = new RectangleInteriorBuilder();
        var wallBuilder = new BoundaryWallBuilder();

        // Pre-build wall meshes once so the same MeshData backs both rooms' splits.
        var wallMeshes = new Dictionary<Adjacency, MeshData>();
        foreach (var adj in plan.Adjacencies)
            wallMeshes[adj] = wallBuilder.Build(adj, wallThickness, wallHeight);

        w.WriteLine("# OpenApparatus floor-plan export");
        w.WriteLine($"# {plan.Rooms.Count} rooms, {plan.Adjacencies.Count} walls");
        if (!string.IsNullOrEmpty(mtlLibFileName))
            w.WriteLine($"mtllib {mtlLibFileName}");
        w.WriteLine();

        int vertexBase = 1;

        foreach (var room in plan.Rooms)
        {
            var interior = interiorBuilder.Build(room, wallThickness, wallHeight);
            var roomAdjacencies = new List<Adjacency>();
            foreach (var adj in plan.Adjacencies)
                if (adj.RoomA == room || adj.RoomB == room)
                    roomAdjacencies.Add(adj);

            // -------- Floor --------
            // Interior floor + threshold-floor strips contributed by walls owned
            // by this room (lower-id room owns internal walls; RoomA owns outer).
            var floor = new FaceBucket();
            floor.AddSubmesh(interior, SubmeshIndex.Floor);
            foreach (var adj in roomAdjacencies)
            {
                if (LowerIdOwner(adj).Id != room.Id) continue;
                floor.AddSubmesh(wallMeshes[adj], SubmeshIndex.Floor);
            }
            EmitBucket(w, floor,
                $"room_{room.Id}_floor",
                $"{FloorMaterialPrefix}_Room{room.Id}",
                FloorColor, slots, ref vertexBase);

            // -------- Ceiling --------
            var ceiling = new FaceBucket();
            ceiling.AddSubmesh(interior, SubmeshIndex.Ceiling);
            EmitBucket(w, ceiling,
                $"room_{room.Id}_ceiling",
                $"{CeilingMaterialPrefix}_Room{room.Id}",
                CeilingColor, slots, ref vertexBase);

            // -------- Walls --------
            // One object per adjacency the room touches. Classify each wall face
            // by normal alignment with the segment's +N axis: faces facing into
            // RoomA → RoomA-side, faces facing into RoomB → RoomB-side. The
            // remaining "frame" faces (top, bottom, caps, tunnel pieces) go to
            // the lower-id-room side so they are not duplicated.
            for (int i = 0; i < roomAdjacencies.Count; i++)
            {
                var adj = roomAdjacencies[i];
                var wall = wallMeshes[adj];
                var seg = adj.SharedSegment;
                var nrm3 = new Vector3(seg.Normal.X, 0f, seg.Normal.Y);
                bool roomIsA = adj.RoomA == room;

                var wallBucket = new FaceBucket();
                AddSplitWallFaces(
                    wallBucket, wall, SubmeshIndex.Walls, nrm3,
                    includeBodyA: roomIsA,
                    includeBodyB: !roomIsA,
                    includeFrame: LowerIdOwner(adj).Id == room.Id);

                int wallNum = i + 1;
                EmitBucket(w, wallBucket,
                    $"room_{room.Id}_wall_{wallNum}",
                    $"{WallsMaterialPrefix}{wallNum}_Room{room.Id}",
                    WallsColor, slots, ref vertexBase);
            }
        }

        return slots;
    }

    /// <summary>
    /// Adds wall-submesh faces to <paramref name="bucket"/>, partitioned by how
    /// each face's normal aligns with the segment normal <paramref name="nrm3"/>.
    /// </summary>
    static void AddSplitWallFaces(
        FaceBucket bucket, MeshData wall, int submesh, Vector3 nrm3,
        bool includeBodyA, bool includeBodyB, bool includeFrame)
    {
        const float ALIGN = 0.9f;
        var tris = wall.SubmeshIndices[submesh];
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t + 0];
            int b = tris[t + 1];
            int c = tris[t + 2];
            // All four verts of a quad share a normal (AddQuad does this), and the
            // two tris of a quad share that normal too — sample any vertex.
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

    static void EmitBucket(
        TextWriter w, FaceBucket bucket,
        string objectName, string materialName,
        (float r, float g, float b) color,
        List<MaterialSlot> slots, ref int vertexBase)
    {
        if (bucket.IsEmpty) return;

        slots.Add(new MaterialSlot(materialName, color));
        w.WriteLine($"o {objectName}");
        w.WriteLine($"usemtl {materialName}");

        foreach (var v in bucket.Vertices)
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "v {0:F6} {1:F6} {2:F6}", v.X, v.Y, v.Z));
        foreach (var n in bucket.Normals)
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "vn {0:F6} {1:F6} {2:F6}", n.X, n.Y, n.Z));
        foreach (var u in bucket.Uvs)
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "vt {0:F6} {1:F6}", u.X, u.Y));

        for (int i = 0; i < bucket.Triangles.Count; i += 3)
        {
            int a = bucket.Triangles[i + 0] + vertexBase;
            int b = bucket.Triangles[i + 1] + vertexBase;
            int c = bucket.Triangles[i + 2] + vertexBase;
            w.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
        }

        vertexBase += bucket.Vertices.Count;
        w.WriteLine();
    }

    /// <summary>
    /// Accumulates verts/normals/uvs/tris from arbitrary MeshData sources for a
    /// single OBJ object. Tracks (source, sourceIdx) → local-index dedupe so
    /// shared verts collapse cleanly.
    /// </summary>
    sealed class FaceBucket
    {
        public List<Vector3> Vertices { get; } = new();
        public List<Vector3> Normals { get; } = new();
        public List<Vector2> Uvs { get; } = new();
        public List<int> Triangles { get; } = new();

        readonly Dictionary<(MeshData, int), int> _index = new();
        public bool IsEmpty => Triangles.Count == 0;

        public void AddSubmesh(MeshData src, int submeshIndex)
        {
            var tris = src.SubmeshIndices[submeshIndex];
            for (int i = 0; i < tris.Length; i += 3)
                AddTriangle(src, tris[i], tris[i + 1], tris[i + 2]);
        }

        public void AddTriangle(MeshData src, int ia, int ib, int ic)
        {
            Triangles.Add(LocalIndex(src, ia));
            Triangles.Add(LocalIndex(src, ib));
            Triangles.Add(LocalIndex(src, ic));
        }

        int LocalIndex(MeshData src, int srcIdx)
        {
            var key = (src, srcIdx);
            if (_index.TryGetValue(key, out var existing)) return existing;
            int local = Vertices.Count;
            _index[key] = local;
            Vertices.Add(src.Vertices[srcIdx]);
            Normals.Add(src.Normals[srcIdx]);
            Uvs.Add(src.Uv0[srcIdx]);
            return local;
        }
    }

    /// <summary>
    /// Writes the sidecar .mtl file. Pass the slot list returned by <see cref="Export"/>
    /// so every material referenced by the OBJ is defined.
    /// </summary>
    public static void WriteMtl(TextWriter w, IReadOnlyList<MaterialSlot> slots)
    {
        w.WriteLine("# OpenApparatus material library");
        w.WriteLine();
        foreach (var slot in slots)
            WriteMaterial(w, slot.Name, slot.Kd);
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

    static readonly (float r, float g, float b) FloorColor   = (0.55f, 0.42f, 0.30f); // warm wood
    static readonly (float r, float g, float b) WallsColor   = (0.78f, 0.78f, 0.80f); // light gray
    static readonly (float r, float g, float b) CeilingColor = (0.92f, 0.92f, 0.90f); // off-white

    public readonly record struct MaterialSlot(string Name, (float r, float g, float b) Kd);
}
