using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenApparatus.Geometry;
using OpenApparatus.Studio.ViewModels;
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
///
/// Coordinate handedness: glTF is right-handed, Unity is left-handed, and
/// Unity's glTF importers (UnityGLTF, glTFast) compensate by negating X on
/// import. Without intervention that turns the studio's "+X = east, right on
/// the 2D map" into "-X = left in Unity," so the imported scene reads as a
/// left/right mirror of the editor. We pre-mirror X in the writer (positions,
/// normals, object translations / Y-rotations, and triangle winding) so the
/// importer's flip cancels out and Unity sees the same orientation as the 2D
/// view. Right-handed viewers (Blender, Three.js) will see this as the actual
/// mirror — that's the trade-off; Unity is the primary target.
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
        IReadOnlyCollection<int>? multiColorRoomIds = null,
        IReadOnlyDictionary<int, Vector3>? roomFloorColors = null,
        IReadOnlyDictionary<int, Vector3>? roomCeilingColors = null,
        IReadOnlyDictionary<int, Vector3>? roomSingleWallColors = null,
        IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), Vector3>? perWallColors = null,
        IReadOnlyList<RoomObject>? objects = null,
        IReadOnlyList<ObjectType>? objectTypes = null)
    {
        var model = BuildModel(plan, wallThickness, wallHeight,
            multiColorRoomIds, roomFloorColors, roomCeilingColors,
            roomSingleWallColors, perWallColors, objects, objectTypes);
        // SharpGLTF picks GLB vs glTF+bin from the extension on Save.
        model.Save(path);
    }

    public static ModelRoot BuildModel(
        MultiRoomEnvironment plan,
        float wallThickness,
        float wallHeight,
        IReadOnlyCollection<int>? multiColorRoomIds = null,
        IReadOnlyDictionary<int, Vector3>? roomFloorColors = null,
        IReadOnlyDictionary<int, Vector3>? roomCeilingColors = null,
        IReadOnlyDictionary<int, Vector3>? roomSingleWallColors = null,
        IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), Vector3>? perWallColors = null,
        IReadOnlyList<RoomObject>? objects = null,
        IReadOnlyList<ObjectType>? objectTypes = null)
    {
        ObjectType? TypeAt(int slot1Based)
        {
            int idx = slot1Based - 1;
            if (objectTypes is null || idx < 0 || idx >= objectTypes.Count) return null;
            return objectTypes[idx];
        }
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
            var floorColor = roomFloorColors != null && roomFloorColors.TryGetValue(room.Id, out var fc)
                ? (fc.X, fc.Y, fc.Z) : FloorColor;
            var floorMb = NewMesh($"room_{room.Id}_floor");
            var floorPrim = floorMb.UsePrimitive(MakeMaterial(
                $"{ObjExporter.FloorMaterialPrefix}_Room{room.Id}", floorColor));
            AddSubmeshTriangles(floorPrim, interior, SubmeshIndex.Floor);
            foreach (var adj in roomAdjacencies)
            {
                if (LowerIdOwner(adj).Id != room.Id) continue;
                AddSubmeshTriangles(floorPrim, wallMeshes[adj], SubmeshIndex.Floor);
            }
            AddMeshIfNonEmpty(scene, roomNode, floorMb, $"room_{room.Id}_floor");

            // -------- Ceiling --------
            var ceilingColor = roomCeilingColors != null && roomCeilingColors.TryGetValue(room.Id, out var cc)
                ? (cc.X, cc.Y, cc.Z) : CeilingColor;
            var ceilingMb = NewMesh($"room_{room.Id}_ceiling");
            var ceilingPrim = ceilingMb.UsePrimitive(MakeMaterial(
                $"{ObjExporter.CeilingMaterialPrefix}_Room{room.Id}", ceilingColor));
            AddSubmeshTriangles(ceilingPrim, interior, SubmeshIndex.Ceiling);
            AddMeshIfNonEmpty(scene, roomNode, ceilingMb, $"room_{room.Id}_ceiling");

            // -------- Walls --------
            bool roomMulti = multiColorRoomIds != null && multiColorRoomIds.Contains(room.Id);
            if (roomMulti)
            {
                // Per-wall meshes so each adjacency carries its own material.
                for (int i = 0; i < roomAdjacencies.Count; i++)
                {
                    var adj = roomAdjacencies[i];
                    var wall = wallMeshes[adj];
                    var seg = adj.SharedSegment;
                    var nrm3 = new Vector3(seg.Normal.X, 0f, seg.Normal.Y);
                    bool roomIsA = adj.RoomA == room;
                    bool isLowerOwner = LowerIdOwner(adj).Id == room.Id;

                    int wallNum = i + 1;
                    var color = ResolveWallColor(perWallColors, roomSingleWallColors, room.Id, adj);
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
                // Single combined walls mesh for the room. Use the room's chosen
                // single-wall color, falling back to the neutral default.
                var singleColor = roomSingleWallColors != null && roomSingleWallColors.TryGetValue(room.Id, out var sw)
                    ? (sw.X, sw.Y, sw.Z) : WallsColor;
                var wallsMb = NewMesh($"room_{room.Id}_walls");
                var wallsPrim = wallsMb.UsePrimitive(MakeMaterial(
                    $"{ObjExporter.WallsMaterialPrefix}_Room{room.Id}", singleColor));

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

            // Per-room object instances. Each one becomes a child node of the
            // room called "slot_<n>_<index>"; a Unity post-import script can
            // swap these for prefabs by parsing that name.
            if (objects != null)
            {
                int objCount = 0;
                NodeBuilder? objectsParent = null;
                for (int oi = 0; oi < objects.Count; oi++)
                {
                    var obj = objects[oi];
                    if (obj.OwningRoomId != room.Id) continue;
                    var slot = TypeAt(obj.Slot);
                    if (slot is null) continue;

                    objectsParent ??= roomNode.CreateNode($"room_{room.Id}_objects");
                    var inst = objectsParent.CreateNode($"slot_{obj.Slot}_{objCount}");
                    // Mirror X on translation; rotation around Y becomes its
                    // negative under the same mirror (clockwise becomes CCW).
                    inst = inst.WithLocalTranslation(MirrorX(obj.Position));
                    if (obj.Rotation != 0f)
                        inst = inst.WithLocalRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -obj.Rotation));

                    var mb = NewMesh($"slot_{obj.Slot}_{objCount}_mesh");
                    var prim = mb.UsePrimitive(MakeMaterial(
                        $"OpenApparatus_Slot{obj.Slot}", (slot.Color.X, slot.Color.Y, slot.Color.Z)));
                    AddPrimitiveShape(prim, slot.Shape, slot.Size);
                    scene.AddRigidMesh(mb, inst);
                    objCount++;
                }
            }
        }

        // Outside objects — anything whose owning room id doesn't match an
        // existing room (typically -1) lands in a sibling 'Outside' node so
        // importers don't drop it on the floor.
        if (objects != null)
        {
            var validRoomIds = new HashSet<int>();
            foreach (var r in plan.Rooms) validRoomIds.Add(r.Id);
            int outsideCount = 0;
            NodeBuilder? outsideParent = null;
            for (int oi = 0; oi < objects.Count; oi++)
            {
                var obj = objects[oi];
                if (validRoomIds.Contains(obj.OwningRoomId)) continue;
                var slot = TypeAt(obj.Slot);
                if (slot is null) continue;

                outsideParent ??= rootNode.CreateNode("Outside");
                var inst = outsideParent.CreateNode($"slot_{obj.Slot}_{outsideCount}");
                inst = inst.WithLocalTranslation(MirrorX(obj.Position));
                if (obj.Rotation != 0f)
                    inst = inst.WithLocalRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -obj.Rotation));

                var mb = NewMesh($"outside_slot_{obj.Slot}_{outsideCount}_mesh");
                var prim = mb.UsePrimitive(MakeMaterial(
                    $"OpenApparatus_Slot{obj.Slot}", (slot.Color.X, slot.Color.Y, slot.Color.Z)));
                AddPrimitiveShape(prim, slot.Shape, slot.Size);
                scene.AddRigidMesh(mb, inst);
                outsideCount++;
            }
        }

        scene.AddNode(rootNode);
        return scene.ToGltf2();
    }

    /// <summary>Generates the geometry for one object slot's primitive at the
    /// origin (rotation/translation are applied at the node level). Sized so
    /// the largest extent is roughly <paramref name="size"/> metres.</summary>
    static void AddPrimitiveShape(IPrimitiveBuilder prim, ObjectShape shape, float size)
    {
        switch (shape)
        {
            case ObjectShape.Cube: BuildBox(prim, size, size, size); break;
            case ObjectShape.Sphere: BuildSphere(prim, size * 0.5f, 10, 16); break;
            case ObjectShape.Cylinder: BuildCylinder(prim, size * 0.5f, size, 16); break;
            case ObjectShape.SquatCylinder: BuildCylinder(prim, size * 0.5f, size * 0.4f, 16); break;
            case ObjectShape.Cone: BuildCone(prim, size * 0.5f, size, 16); break;
            case ObjectShape.Capsule: BuildCapsule(prim, size * 0.3f, size, 10, 16); break;
            case ObjectShape.Pyramid: BuildPyramid(prim, size); break;
        }
    }

    static void BuildBox(IPrimitiveBuilder prim, float w, float h, float d)
    {
        float hx = w * 0.5f, hy = h * 0.5f, hz = d * 0.5f;
        // Six faces. Vertices wound CCW from outside.
        // -y (bottom)
        AddQuad(prim, new(-hx, 0, -hz), new(hx, 0, -hz), new(hx, 0, hz), new(-hx, 0, hz), new(0, -1, 0));
        // +y (top)
        AddQuad(prim, new(-hx, h, hz), new(hx, h, hz), new(hx, h, -hz), new(-hx, h, -hz), new(0, 1, 0));
        // -z
        AddQuad(prim, new(-hx, 0, -hz), new(-hx, h, -hz), new(hx, h, -hz), new(hx, 0, -hz), new(0, 0, -1));
        // +z
        AddQuad(prim, new(hx, 0, hz), new(hx, h, hz), new(-hx, h, hz), new(-hx, 0, hz), new(0, 0, 1));
        // -x
        AddQuad(prim, new(-hx, 0, hz), new(-hx, h, hz), new(-hx, h, -hz), new(-hx, 0, -hz), new(-1, 0, 0));
        // +x
        AddQuad(prim, new(hx, 0, -hz), new(hx, h, -hz), new(hx, h, hz), new(hx, 0, hz), new(1, 0, 0));
    }

    static void BuildSphere(IPrimitiveBuilder prim, float r, int stacks, int slices)
    {
        // Origin at the bottom of the sphere — the editor's Y is "metres above
        // the floor" so a Y of 0 should place the sphere's lowest point on the
        // floor. Stacks = latitude rings, slices = longitude segments.
        for (int i = 0; i < stacks; i++)
        {
            double phi1 = System.Math.PI * (i / (double)stacks);
            double phi2 = System.Math.PI * ((i + 1) / (double)stacks);
            for (int j = 0; j < slices; j++)
            {
                double theta1 = 2 * System.Math.PI * (j / (double)slices);
                double theta2 = 2 * System.Math.PI * ((j + 1) / (double)slices);
                Vector3 P(double phi, double theta) => new(
                    r + (float)(r * System.Math.Sin(phi) * System.Math.Cos(theta)) - r,
                    r + (float)(r * System.Math.Cos(phi)),
                    (float)(r * System.Math.Sin(phi) * System.Math.Sin(theta)));
                var v00 = P(phi1, theta1);
                var v01 = P(phi1, theta2);
                var v10 = P(phi2, theta1);
                var v11 = P(phi2, theta2);
                AddTri(prim, v00, v01, v11, ApproxNormal(v00, v01, v11));
                AddTri(prim, v00, v11, v10, ApproxNormal(v00, v11, v10));
            }
        }
    }

    static void BuildCylinder(IPrimitiveBuilder prim, float r, float h, int slices)
    {
        for (int i = 0; i < slices; i++)
        {
            double t1 = 2 * System.Math.PI * (i / (double)slices);
            double t2 = 2 * System.Math.PI * ((i + 1) / (double)slices);
            float c1 = (float)System.Math.Cos(t1), s1 = (float)System.Math.Sin(t1);
            float c2 = (float)System.Math.Cos(t2), s2 = (float)System.Math.Sin(t2);
            // Side
            var n1 = new Vector3(c1, 0, s1);
            var n2 = new Vector3(c2, 0, s2);
            AddTri(prim, new(r * c1, 0, r * s1), new(r * c2, 0, r * s2), new(r * c2, h, r * s2), n1);
            AddTri(prim, new(r * c1, 0, r * s1), new(r * c2, h, r * s2), new(r * c1, h, r * s1), n1);
            // Top cap
            AddTri(prim, new(0, h, 0), new(r * c1, h, r * s1), new(r * c2, h, r * s2), new(0, 1, 0));
            // Bottom cap
            AddTri(prim, new(0, 0, 0), new(r * c2, 0, r * s2), new(r * c1, 0, r * s1), new(0, -1, 0));
        }
    }

    static void BuildCone(IPrimitiveBuilder prim, float r, float h, int slices)
    {
        for (int i = 0; i < slices; i++)
        {
            double t1 = 2 * System.Math.PI * (i / (double)slices);
            double t2 = 2 * System.Math.PI * ((i + 1) / (double)slices);
            float c1 = (float)System.Math.Cos(t1), s1 = (float)System.Math.Sin(t1);
            float c2 = (float)System.Math.Cos(t2), s2 = (float)System.Math.Sin(t2);
            var p1 = new Vector3(r * c1, 0, r * s1);
            var p2 = new Vector3(r * c2, 0, r * s2);
            var apex = new Vector3(0, h, 0);
            AddTri(prim, p1, p2, apex, ApproxNormal(p1, p2, apex));
            AddTri(prim, new(0, 0, 0), p2, p1, new(0, -1, 0));
        }
    }

    static void BuildCapsule(IPrimitiveBuilder prim, float r, float h, int stacks, int slices)
    {
        // Cylindrical body + half-spheres on each end. Simplification: render
        // as a stretched sphere at the centre. Acceptable for stand-in geometry.
        BuildCylinder(prim, r, h, slices);
        // Top hemisphere
        for (int i = 0; i < stacks; i++)
        {
            double phi1 = (System.Math.PI * 0.5) * (i / (double)stacks);
            double phi2 = (System.Math.PI * 0.5) * ((i + 1) / (double)stacks);
            for (int j = 0; j < slices; j++)
            {
                double theta1 = 2 * System.Math.PI * (j / (double)slices);
                double theta2 = 2 * System.Math.PI * ((j + 1) / (double)slices);
                Vector3 P(double phi, double theta) => new(
                    (float)(r * System.Math.Sin(phi) * System.Math.Cos(theta)),
                    h + (float)(r * System.Math.Cos(phi)),
                    (float)(r * System.Math.Sin(phi) * System.Math.Sin(theta)));
                var v00 = P(phi2, theta1);
                var v01 = P(phi2, theta2);
                var v10 = P(phi1, theta1);
                var v11 = P(phi1, theta2);
                AddTri(prim, v00, v01, v11, ApproxNormal(v00, v01, v11));
                AddTri(prim, v00, v11, v10, ApproxNormal(v00, v11, v10));
            }
        }
    }

    static void BuildPyramid(IPrimitiveBuilder prim, float size)
    {
        float h = size, hx = size * 0.5f;
        var p1 = new Vector3(-hx, 0, -hx);
        var p2 = new Vector3( hx, 0, -hx);
        var p3 = new Vector3( hx, 0,  hx);
        var p4 = new Vector3(-hx, 0,  hx);
        var apex = new Vector3(0, h, 0);
        AddQuad(prim, p1, p4, p3, p2, new(0, -1, 0));
        AddTri(prim, p1, p2, apex, ApproxNormal(p1, p2, apex));
        AddTri(prim, p2, p3, apex, ApproxNormal(p2, p3, apex));
        AddTri(prim, p3, p4, apex, ApproxNormal(p3, p4, apex));
        AddTri(prim, p4, p1, apex, ApproxNormal(p4, p1, apex));
    }

    static Vector3 ApproxNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        var len = n.Length();
        return len > 1e-6f ? n / len : new Vector3(0, 1, 0);
    }

    /// <summary>Mirror across the X=0 plane. Applied to every position and
    /// normal we emit so Unity's importer-side X flip cancels back to the
    /// authored orientation.</summary>
    static Vector3 MirrorX(Vector3 v) => new(-v.X, v.Y, v.Z);

    static void AddQuad(IPrimitiveBuilder prim, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        var n = MirrorX(normal);
        var va = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(MirrorX(a), n), new VertexTexture1(Vector2.Zero));
        var vb = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(MirrorX(b), n), new VertexTexture1(Vector2.Zero));
        var vc = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(MirrorX(c), n), new VertexTexture1(Vector2.Zero));
        var vd = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(MirrorX(d), n), new VertexTexture1(Vector2.Zero));
        // Mirroring across X reverses triangle orientation; swap the last two
        // verts so winding (and therefore front-facing) stays correct.
        prim.AddTriangle(va, vc, vb);
        prim.AddTriangle(va, vd, vc);
    }

    static void AddTri(IPrimitiveBuilder prim, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        var n = MirrorX(normal);
        var va = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(MirrorX(a), n), new VertexTexture1(Vector2.Zero));
        var vb = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(MirrorX(b), n), new VertexTexture1(Vector2.Zero));
        var vc = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
            new VertexPositionNormal(MirrorX(c), n), new VertexTexture1(Vector2.Zero));
        prim.AddTriangle(va, vc, vb);
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
        // Winding reversed because MakeVertex mirrors X (see class summary).
        prim.AddTriangle(va, vc, vb);
    }

    static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> MakeVertex(
        MeshData src, int idx)
    {
        var pos = new VertexPositionNormal(MirrorX(src.Vertices[idx]), MirrorX(src.Normals[idx]));
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

    /// <summary>Per-wall color resolution order: per-wall override → room's single
    /// wall color → neutral default.</summary>
    static (float r, float g, float b) ResolveWallColor(
        IReadOnlyDictionary<(int RoomId, int MidX, int MidZ), Vector3>? perWall,
        IReadOnlyDictionary<int, Vector3>? roomSingle,
        int roomId, Adjacency adj)
    {
        if (perWall != null && perWall.TryGetValue(WallColorKey(roomId, adj), out var v))
            return (v.X, v.Y, v.Z);
        if (roomSingle != null && roomSingle.TryGetValue(roomId, out var s))
            return (s.X, s.Y, s.Z);
        return WallsColor;
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
