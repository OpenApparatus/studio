using System.Globalization;
using System.IO;
using OpenApparatus.Geometry;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Writes a generated MultiRoomEnvironment's geometry to a Wavefront .obj file. Each room
/// becomes one OBJ object ('o room_&lt;id&gt;'), with one named group per submesh
/// (g floor / g walls / g ceiling) tagged with a `usemtl` directive so importers like
/// Unity create one material slot per submesh. A sidecar .mtl file with placeholder
/// materials can be written alongside.
/// </summary>
public static class ObjExporter
{
    public const string FloorMaterialName = "OpenApparatus_Floor";
    public const string WallsMaterialName = "OpenApparatus_Walls";
    public const string CeilingMaterialName = "OpenApparatus_Ceiling";

    /// <summary>
    /// Writes OBJ geometry. If <paramref name="mtlLibFileName"/> is non-null the file
    /// emits an `mtllib` reference at the top so importers pick up the sidecar MTL.
    /// </summary>
    public static void Export(
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

        // OBJ vertex indices are 1-based and global to the file.
        int vertexBase = 1;

        foreach (var assembled in roomMeshes)
        {
            var mesh = assembled.Mesh;
            w.WriteLine($"o room_{assembled.Room.Id}");

            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var v = mesh.Vertices[i];
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "v {0:F6} {1:F6} {2:F6}", v.X, v.Y, v.Z));
            }
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var n = mesh.Normals[i];
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "vn {0:F6} {1:F6} {2:F6}", n.X, n.Y, n.Z));
            }
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var u = mesh.Uv0[i];
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "vt {0:F6} {1:F6}", u.X, u.Y));
            }

            for (int s = 0; s < mesh.SubmeshCount; s++)
            {
                var tris = mesh.SubmeshIndices[s];
                if (tris.Length == 0) continue;
                w.WriteLine($"g {SubmeshGroupName(s)}");
                w.WriteLine($"usemtl {SubmeshMaterialName(s)}");
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = tris[t + 0] + vertexBase;
                    int b = tris[t + 1] + vertexBase;
                    int c = tris[t + 2] + vertexBase;
                    w.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                }
            }

            vertexBase += mesh.VertexCount;
            w.WriteLine();
        }
    }

    /// <summary>
    /// Writes the sidecar .mtl file with three placeholder materials. Users can
    /// freely override these in Unity by re-pointing the material slots — the
    /// distinct names ensure Unity creates separate slots on import.
    /// </summary>
    public static void WriteMtl(TextWriter w)
    {
        w.WriteLine("# OpenApparatus material library");
        w.WriteLine();
        WriteMaterial(w, FloorMaterialName,   kd: (0.55f, 0.42f, 0.30f)); // warm wood
        WriteMaterial(w, WallsMaterialName,   kd: (0.78f, 0.78f, 0.80f)); // light gray
        WriteMaterial(w, CeilingMaterialName, kd: (0.92f, 0.92f, 0.90f)); // off-white
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

    static string SubmeshGroupName(int submeshIndex) => submeshIndex switch
    {
        SubmeshIndex.Floor => "floor",
        SubmeshIndex.Walls => "walls",
        SubmeshIndex.Ceiling => "ceiling",
        _ => $"submesh_{submeshIndex}",
    };

    static string SubmeshMaterialName(int submeshIndex) => submeshIndex switch
    {
        SubmeshIndex.Floor => FloorMaterialName,
        SubmeshIndex.Walls => WallsMaterialName,
        SubmeshIndex.Ceiling => CeilingMaterialName,
        _ => $"OpenApparatus_Submesh{submeshIndex}",
    };
}
