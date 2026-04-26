using System.Collections.Generic;
using System.Numerics;
using OpenApparatus.Geometry;
using OpenApparatus.Topology;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Writes a generated MultiRoomEnvironment as a glTF 2.0 file (.glb binary or
/// .gltf JSON+bin). Unlike OBJ, glTF natively supports scene hierarchy: the
/// output puts every room under its own named parent node, and each per-room
/// mesh (floor, ceiling, wall_&lt;i&gt;) is a child node under that parent.
/// Importers (Unity, Blender, Three.js, glTF web viewers) all reconstruct the
/// hierarchy automatically — no post-processing required.
///
/// Internal walls are split by face normal so each room owns the body face it
/// can see — RoomA gets +N, RoomB gets -N. Frame pieces (top, bottom, caps,
/// tunnel jambs / lintels / sills / thresholds) belong to the lower-id room
/// and carry a unique material per (room, part) so re-skinning one room never
/// alters the other room's view of a shared wall.
/// </summary>
public static class GltfExporter
{
    /// <summary>
    /// Builds the scene and writes it to <paramref name="path"/>. The format is
    /// chosen from the file extension: .glb writes a single binary file, .gltf
    /// writes the JSON descriptor with a sidecar .bin for vertex data.
    /// </summary>
    public static void Export(
        string path,
        MultiRoomEnvironment plan,
        float wallThickness,
        float wallHeight,
        bool multiMeshWalls = false,
        IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), Vector3>? wallColors = null)
    {
        var model = BuildModel(plan, wallThickness, wallHeight, multiMeshWalls, wallColors);
        // SharpGLTF picks GLB vs glTF+bin from the extension on Save.
        model.Save(path);
    }

    public static ModelRoot BuildModel(
        MultiRoomEnvironment plan,
        float wallThickness,
        float wallHeight,
        bool multiMeshWalls = false,
        IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), Vector3>? wallColors = null)
    {
        var interiorBuilder = new RectangleInteriorBuilder();
        var wallBuilder = new BoundaryWallBuilder();

        // Pre-build walls so the same MeshData backs both rooms' splits.
        var wallMeshes = new Dictionary<Adjacency, MeshData>();
        foreach (var adj in plan.Adjacencies)
            wallMeshes[adj] = wallBuilder.Build(adj, wallThickness, wallHeight);

        var scene = new SceneBuilder("OpenApparatus");
        var rootNode = new NodeBuilder("OpenApparatus");

        foreach (var room in plan.Rooms)
        {
            var interior = interiorBuilder.Build(room, wallThickness, wallHeight);
            var roomAdjacencies = new List<Adjacency>();
            foreach (var adj in plan.Adjacencies)
                if (adj.RoomA == room || adj.RoomB == room)
                    roomAdjacencies.Add(adj);

            var roomNode = rootNode.CreateNode($"Room_{room.Id}");

            // -------- Floor --------
            var floorMb = NewMesh($"room_{room.Id}_floor");
            var floorPrim = floorMb.UsePrimitive(MakeMaterial(
                $"{ObjExporter.FloorMaterialPrefix}_Room{room.Id}", FloorColor));
            AddSubmeshTriangles(floorPrim, interior, SubmeshIndex.Floor);
            foreach (var adj in roomAdjacencies)
            {
                if (LowerIdOwner(adj).Id != room.Id) continue;
                AddSubmeshTriangles(floorPrim, wallMeshes[adj], SubmeshIndex.Floor);
            }
            AddMeshIfNonEmpty(scene, roomNode, floorMb, $"room_{room.Id}_floor");

            // -------- Ceiling --------
            var ceilingMb = NewMesh($"room_{room.Id}_ceiling");
            var ceilingPrim = ceilingMb.UsePrimitive(MakeMaterial(
                $"{ObjExporter.CeilingMaterialPrefix}_Room{room.Id}", CeilingColor));
            AddSubmeshTriangles(ceilingPrim, interior, SubmeshIndex.Ceiling);
            AddMeshIfNonEmpty(scene, roomNode, ceilingMb, $"room_{room.Id}_ceiling");

            // -------- Walls --------
            if (multiMeshWalls)
            {
                // One mesh per adjacency the room touches — each wall carries
                // its own material, so per-wall colors apply independently.
                for (int i = 0; i < roomAdjacencies.Count; i++)
                {
                    var adj = roomAdjacencies[i];
                    var wall = wallMeshes[adj];
                    var seg = adj.SharedSegment;
                    var nrm3 = new Vector3(seg.Normal.X, 0f, seg.Normal.Y);
                    bool roomIsA = adj.RoomA == room;
                    bool isLowerOwner = LowerIdOwner(adj).Id == room.Id;

                    int wallNum = i + 1;
                    var color = ResolveWallColor(wallColors, room.Id, adj);
                    var wallMb = NewMesh($"room_{room.Id}_wall_{wallNum}");
                    var wallPrim = wallMb.UsePrimitive(MakeMaterial(
                        $"{ObjExporter.WallsMaterialPrefix}{wallNum}_Room{room.Id}", color));
                    AddSplitWallTriangles(
                        wallPrim, wall, SubmeshIndex.Walls, nrm3,
                        includeBodyA: roomIsA,
                        includeBodyB: !roomIsA,
                        includeFrame: isLowerOwner);
                    AddMeshIfNonEmpty(scene, roomNode, wallMb, $"room_{room.Id}_wall_{wallNum}");
                }
            }
            else
            {
                // One wall mesh for the whole room — fewer draw calls. Per-wall
                // colors collapse to a single material; we pick the colors of
                // any wall that has been overridden, or the default if none.
                var wallsMb = NewMesh($"room_{room.Id}_walls");
                var firstOverride = FirstWallColor(wallColors, room.Id, roomAdjacencies);
                var wallsPrim = wallsMb.UsePrimitive(MakeMaterial(
                    $"{ObjExporter.WallsMaterialPrefix}_Room{room.Id}",
                    firstOverride ?? WallsColor));

                foreach (var adj in roomAdjacencies)
                {
                    var wall = wallMeshes[adj];
                    var seg = adj.SharedSegment;
                    var nrm3 = new Vector3(seg.Normal.X, 0f, seg.Normal.Y);
                    bool roomIsA = adj.RoomA == room;
                    bool isLowerOwner = LowerIdOwner(adj).Id == room.Id;
                    AddSplitWallTriangles(
                        wallsPrim, wall, SubmeshIndex.Walls, nrm3,
                        includeBodyA: roomIsA,
                        includeBodyB: !roomIsA,
                        includeFrame: isLowerOwner);
                }
                AddMeshIfNonEmpty(scene, roomNode, wallsMb, $"room_{room.Id}_walls");
            }
        }

        scene.AddNode(rootNode);
        return scene.ToGltf2();
    }

    static MeshBuilder<VertexPositionNormal, VertexTexture1> NewMesh(string name)
        => new(name);

    static void AddSubmeshTriangles(
        IPrimitiveBuilder prim, MeshData src, int submeshIndex)
    {
        var tris = src.SubmeshIndices[submeshIndex];
        for (int i = 0; i < tris.Length; i += 3)
            AddTriangle(prim, src, tris[i], tris[i + 1], tris[i + 2]);
    }

    static void AddSplitWallTriangles(
        IPrimitiveBuilder prim, MeshData wall, int submesh, Vector3 nrm3,
        bool includeBodyA, bool includeBodyB, bool includeFrame)
    {
        const float ALIGN = 0.9f;
        var tris = wall.SubmeshIndices[submesh];
        for (int t = 0; t < tris.Length; t += 3)
        {
            int a = tris[t + 0];
            int b = tris[t + 1];
            int c = tris[t + 2];
            // Quads share a normal across all 4 verts, so any vertex characterizes the face.
            var n = wall.Normals[a];
            float dot = Vector3.Dot(n, nrm3);

            bool keep = dot > ALIGN ? includeBodyA
                      : dot < -ALIGN ? includeBodyB
                      : includeFrame;
            if (!keep) continue;
            AddTriangle(prim, wall, a, b, c);
        }
    }

    static void AddTriangle(IPrimitiveBuilder prim, MeshData src, int ia, int ib, int ic)
    {
        var va = MakeVertex(src, ia);
        var vb = MakeVertex(src, ib);
        var vc = MakeVertex(src, ic);
        prim.AddTriangle(va, vb, vc);
    }

    static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> MakeVertex(
        MeshData src, int idx)
    {
        var pos = new VertexPositionNormal(src.Vertices[idx], src.Normals[idx]);
        var uv = new VertexTexture1(src.Uv0[idx]);
        return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(pos, uv);
    }

    static void AddMeshIfNonEmpty(
        SceneBuilder scene,
        NodeBuilder parent,
        MeshBuilder<VertexPositionNormal, VertexTexture1> mesh,
        string nodeName)
    {
        bool hasGeometry = false;
        foreach (var prim in mesh.Primitives)
        {
            if (prim.Triangles.Count > 0) { hasGeometry = true; break; }
        }
        if (!hasGeometry) return;

        var leaf = parent.CreateNode(nodeName);
        scene.AddRigidMesh(mesh, leaf);
    }

    static MaterialBuilder MakeMaterial(string name, (float r, float g, float b) color)
    {
        return new MaterialBuilder(name)
            .WithMetallicRoughnessShader()
            .WithChannelParam(KnownChannel.BaseColor,
                KnownProperty.RGBA,
                new Vector4(color.r, color.g, color.b, 1f))
            .WithChannelParam(KnownChannel.MetallicRoughness,
                KnownProperty.MetallicFactor, 0f)
            .WithChannelParam(KnownChannel.MetallicRoughness,
                KnownProperty.RoughnessFactor, 0.85f);
    }

    static Room LowerIdOwner(Adjacency adj)
    {
        if (adj.IsOuter) return adj.RoomA;
        return adj.RoomA.Id < adj.RoomB!.Id ? adj.RoomA : adj.RoomB;
    }

    /// <summary>Looks up a per-wall color override by (room, segment-midpoint).</summary>
    static (float r, float g, float b) ResolveWallColor(
        IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), Vector3>? overrides,
        int roomId, Adjacency adj)
    {
        if (overrides is null) return WallsColor;
        var key = WallColorKey(roomId, adj);
        if (!overrides.TryGetValue(key, out var v)) return WallsColor;
        return (v.X, v.Y, v.Z);
    }

    static (float r, float g, float b)? FirstWallColor(
        IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), Vector3>? overrides,
        int roomId, IEnumerable<Adjacency> adjacencies)
    {
        if (overrides is null) return null;
        foreach (var adj in adjacencies)
        {
            if (overrides.TryGetValue(WallColorKey(roomId, adj), out var v))
                return (v.X, v.Y, v.Z);
        }
        return null;
    }

    public static (int RoomId, int MidX, int MidZ) WallColorKey(int roomId, Adjacency adj)
    {
        var mid = adj.SharedSegment.Midpoint;
        return (roomId,
            (int)System.Math.Round(mid.X * 1000),
            (int)System.Math.Round(mid.Y * 1000));
    }

    static readonly (float r, float g, float b) FloorColor   = (0.55f, 0.42f, 0.30f);
    static readonly (float r, float g, float b) WallsColor   = (0.78f, 0.78f, 0.80f);
    static readonly (float r, float g, float b) CeilingColor = (0.92f, 0.92f, 0.90f);
}
