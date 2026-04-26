using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OpenApparatus.Geometry;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Writes a generated MultiRoomEnvironment's geometry to a Wavefront .obj file. To
/// guarantee Unity (and other importers that fold by material) creates distinct
/// floor/walls/ceiling meshes for every room, each (room, submesh) combination is
/// written as its own OBJ object with a unique vertex block and a unique material
/// name. Sidecar .mtl entries reference the same default colors per type, so the
/// rooms look consistent until the user overrides individual materials.
/// </summary>
public static class ObjExporter
{
    public const string FloorMaterialPrefix = "OpenApparatus_Floor";
    public const string WallsMaterialPrefix = "OpenApparatus_Walls";
    public const string CeilingMaterialPrefix = "OpenApparatus_Ceiling";

    /// <summary>
    /// Writes OBJ geometry. If <paramref name="mtlLibFileName"/> is non-null the file
    /// emits an `mtllib` reference at the top so importers pick up the sidecar MTL,
    /// and the returned list is the ordered set of material names that need entries
    /// in the .mtl file. Pass it to <see cref="WriteMtl"/>.
    /// </summary>
    public static IReadOnlyList<MaterialSlot> Export(
        TextWriter w,
        MultiRoomEnvironment plan,
        float wallThickness,
        float wallHeight,
        string? mtlLibFileName = null)
    {
        var roomMeshes = new MultiRoomEnvironmentMeshAssembler().Assemble(plan, wallThickness, wallHeight);

        w.WriteLine("# OpenApparatus floor-plan export");
        w.WriteLine($"# {roomMeshes.Count} rooms");
        if (!string.IsNullOrEmpty(mtlLibFileName))
            w.WriteLine($"mtllib {mtlLibFileName}");
        w.WriteLine();

        var slots = new List<MaterialSlot>();

        // OBJ vertex indices are 1-based and global. We re-emit only the verts each
        // sub-object needs so importers that key by `o` get clean per-object meshes
        // even when they don't honor `usemtl`-based subset splitting.
        int vertexBase = 1;

        foreach (var assembled in roomMeshes)
        {
            var mesh = assembled.Mesh;
            int roomId = assembled.Room.Id;

            for (int s = 0; s < mesh.SubmeshCount; s++)
            {
                var tris = mesh.SubmeshIndices[s];
                if (tris.Length == 0) continue;

                string objectName = $"room_{roomId}_{SubmeshShortName(s)}";
                string materialName = $"{SubmeshMaterialPrefix(s)}_Room{roomId}";
                slots.Add(new MaterialSlot(materialName, SubmeshDefaultColor(s)));

                w.WriteLine($"o {objectName}");
                w.WriteLine($"usemtl {materialName}");

                // Compact the verts: only those referenced by this submesh's faces.
                // Track insertion order separately — Dictionary key iteration order is
                // not guaranteed and we need normals/UVs to line up with vertices.
                var localIndex = new Dictionary<int, int>(tris.Length);
                var orderedGlobals = new List<int>();
                foreach (int globalIdx in tris)
                {
                    if (localIndex.ContainsKey(globalIdx)) continue;
                    localIndex[globalIdx] = orderedGlobals.Count;
                    orderedGlobals.Add(globalIdx);
                }
                foreach (int globalIdx in orderedGlobals)
                {
                    var v = mesh.Vertices[globalIdx];
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "v {0:F6} {1:F6} {2:F6}", v.X, v.Y, v.Z));
                }
                foreach (int globalIdx in orderedGlobals)
                {
                    var n = mesh.Normals[globalIdx];
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "vn {0:F6} {1:F6} {2:F6}", n.X, n.Y, n.Z));
                }
                foreach (int globalIdx in orderedGlobals)
                {
                    var u = mesh.Uv0[globalIdx];
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "vt {0:F6} {1:F6}", u.X, u.Y));
                }

                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = localIndex[tris[t + 0]] + vertexBase;
                    int b = localIndex[tris[t + 1]] + vertexBase;
                    int c = localIndex[tris[t + 2]] + vertexBase;
                    w.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                }

                vertexBase += orderedGlobals.Count;
                w.WriteLine();
            }
        }

        return slots;
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

    static string SubmeshShortName(int submeshIndex) => submeshIndex switch
    {
        SubmeshIndex.Floor => "floor",
        SubmeshIndex.Walls => "walls",
        SubmeshIndex.Ceiling => "ceiling",
        _ => $"submesh{submeshIndex}",
    };

    static string SubmeshMaterialPrefix(int submeshIndex) => submeshIndex switch
    {
        SubmeshIndex.Floor => FloorMaterialPrefix,
        SubmeshIndex.Walls => WallsMaterialPrefix,
        SubmeshIndex.Ceiling => CeilingMaterialPrefix,
        _ => $"OpenApparatus_Submesh{submeshIndex}",
    };

    static (float r, float g, float b) SubmeshDefaultColor(int submeshIndex) => submeshIndex switch
    {
        SubmeshIndex.Floor   => (0.55f, 0.42f, 0.30f), // warm wood
        SubmeshIndex.Walls   => (0.78f, 0.78f, 0.80f), // light gray
        SubmeshIndex.Ceiling => (0.92f, 0.92f, 0.90f), // off-white
        _                    => (0.7f,  0.7f,  0.7f),
    };

    public readonly record struct MaterialSlot(string Name, (float r, float g, float b) Kd);
}
