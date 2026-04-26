using System.Globalization;
using System.IO;
using OpenApparatus.Geometry;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Services;

/// <summary>
/// Writes a generated FloorPlan's geometry to a Wavefront .obj file. Each cell
/// becomes one OBJ object ('o cell_<id>'), with one named group per submesh
/// (g floor / g walls / g ceiling) so other tools can target them by name.
/// </summary>
public static class ObjExporter
{
    public static void Export(TextWriter w, FloorPlan plan, float wallThickness, float wallHeight)
    {
        var cellMeshes = new FloorPlanMeshAssembler().Assemble(plan, wallThickness, wallHeight);

        w.WriteLine("# OpenApparatus floor-plan export");
        w.WriteLine($"# {cellMeshes.Count} cells");
        w.WriteLine();

        // OBJ vertex indices are 1-based and global to the file.
        int vertexBase = 1;

        foreach (var assembled in cellMeshes)
        {
            var mesh = assembled.Mesh;
            w.WriteLine($"o cell_{assembled.Cell.Id}");

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

    static string SubmeshGroupName(int submeshIndex) => submeshIndex switch
    {
        SubmeshIndex.Floor => "floor",
        SubmeshIndex.Walls => "walls",
        SubmeshIndex.Ceiling => "ceiling",
        _ => $"submesh_{submeshIndex}",
    };
}
