using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using OpenApparatus.Geometry;
using OpenApparatus.Studio.ViewModels;
using OpenApparatus.Topology;

namespace OpenApparatus.Studio.Views;

/// <summary>
/// Software 3D rasterizer for the Studio's "3D view" mode.
///
/// Builds a list of triangle faces from the authored environment (floors,
/// walls, ceilings, objects), back-face culls them against the orbit
/// camera, lights each surviving face with a single directional light,
/// painter's-sorts by camera-space depth, and draws each face as a
/// filled <see cref="StreamGeometry"/> via Avalonia's DrawingContext.
///
/// This isn't real-time GPU rendering — it's a software rasterizer using
/// 2D primitives — but the math is genuinely 3D (perspective projection,
/// view matrix, world-space normals, Lambert shading), so the camera
/// behaviour and parallax look correct.
/// </summary>
internal static class Iso3DRenderer
{
    /// <summary>One triangle in world space + the shading inputs we'll
    /// need at draw time. The Layer field is a tiebreaker for the
    /// painter's depth sort: lower layers always draw first regardless
    /// of average depth. Without it, a large floor triangle whose
    /// average camera-Z lands behind a smaller wall in front of it
    /// would still draw on top — a classic average-depth artifact.</summary>
    readonly struct Tri
    {
        public readonly Vector3 V0, V1, V2;
        public readonly Vector3 Normal;     // world-space face normal
        public readonly Vector3 Color;      // 0..1 RGB
        public readonly bool DoubleSided;   // true for walls (visible from both rooms)
        public readonly int Layer;          // 0 = floor, 1 = wall, 2 = object (drawn last)
        public Tri(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 normal, Vector3 color, bool doubleSided = false, int layer = 1)
        { V0 = v0; V1 = v1; V2 = v2; Normal = normal; Color = color; DoubleSided = doubleSided; Layer = layer; }
    }

    const int LayerFloor  = 0;
    const int LayerWall   = 1;
    const int LayerObject = 2;

    /// <summary>Public entry point — renders the 3D scene to the
    /// supplied DrawingContext at the requested size. We render 2× the
    /// requested size into a RenderTargetBitmap, then DrawImage that
    /// bitmap back at 1× — this gives free supersampling AA for the
    /// triangle edges (cheaper than analytic edge AA, and ~independent
    /// of the geometry detail). Falls back to a direct render if
    /// bitmap creation fails for any reason (memory pressure, etc).</summary>
    public static void Render(DrawingContext ctx, MainWindowViewModel vm, Size size)
    {
        // Below ~64 px there's nothing to AA — render directly.
        if (size.Width < 64 || size.Height < 64)
        {
            RenderInner(ctx, vm, size);
            return;
        }
        const double scale = 2.0;
        var px = new PixelSize(
            (int)System.Math.Ceiling(size.Width  * scale),
            (int)System.Math.Ceiling(size.Height * scale));
        Avalonia.Media.Imaging.RenderTargetBitmap? bmp = null;
        try
        {
            bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(px, new Avalonia.Vector(96 * scale, 96 * scale));
            using (var inner = bmp.CreateDrawingContext())
            {
                // Inner render uses logical (scale-independent) size
                // coords because the bitmap's DPI carries the scaling.
                RenderInner(inner, vm, size);
            }
            ctx.DrawImage(bmp, new Rect(0, 0, size.Width, size.Height));
        }
        catch
        {
            RenderInner(ctx, vm, size);
        }
        finally
        {
            bmp?.Dispose();
        }
    }

    static void RenderInner(DrawingContext ctx, MainWindowViewModel vm, Size size)
    {
        // Background — soft horizon gradient. The earlier near-grey on
        // grey was effectively invisible against the building. Now uses
        // a clear sky-blue at the top transitioning to a warmer light
        // ground tone at the bottom so the user sees an actual sky and
        // ground separation behind the model.
        var sky = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        };
        sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xBF, 0xD7, 0xEE), 0));     // sky blue
        sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xDF, 0xE6, 0xEE), 0.55));  // horizon
        sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xC7, 0xC2, 0xB6), 1));     // warm ground
        ctx.FillRectangle(sky, new Rect(size));

        if (vm.CurrentEnvironment is not { } env) return;

        // Auto-fit camera the first time we render this scene, or after
        // the user hits Reset View. Pivot lands at grid centre, distance
        // is sized to the scene's bounding box.
        InitCameraIfNeeded(vm);

        if (env.Rooms.Count == 0)
        {
            // Re-use the top-down empty-state instead of an empty scene.
            GridEditorView.DrawEmptyStatePublic(ctx, size, new Typeface("Inter"), vm.IsObjectsMode);
            DrawHint(ctx, size, "3D preview — switch to Top to edit");
            return;
        }

        // ── Camera matrices ────────────────────────────────────────
        Vector3 pivot = new(vm.IsoPivotX, vm.WallHeight * 0.5f, vm.IsoPivotZ);
        Vector3 camPos = ComputeCamPos(vm, pivot);
        Matrix4x4 view = Matrix4x4.CreateLookAt(camPos, pivot, Vector3.UnitY);
        float aspect = (float)System.Math.Max(0.0001, size.Width / System.Math.Max(0.0001, size.Height));
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, aspect, 0.1f, 1000f);
        Matrix4x4 vp = view * proj;

        // ── Light ─────────────────────────────────────────────────
        Vector3 lightDir = Vector3.Normalize(new Vector3(0.45f, -0.95f, 0.30f));

        // Ground shadow — soft dark ellipse on the floor plane, sized to
        // the building's footprint. Cheap weight cue; sells the
        // "building sits on the ground" feel.
        DrawGroundShadow(ctx, vm, vp, size);

        // ── Mesh collection ──────────────────────────────────────
        // Use the same mesh builders the glTF exporter does, so the 3D
        // view shows exactly what would be exported (real wall thickness,
        // doorway tunnels, frame geometry around openings, etc).
        var tris = new List<Tri>(4096);
        AddRealEnvironmentMeshes(tris, vm, env);
        AddObjects(tris, vm);

        // ── Project + cull + shade ─────────────────────────────
        // For each triangle: discard if any vertex is behind the camera,
        // discard if backfacing (single-sided only), record screen verts
        // + average camera-space depth + layer index + lit colour.
        var drawList = new List<(Point[] Pts, float Depth, int Layer, Color Col)>(tris.Count);
        foreach (var t in tris)
        {
            var center = (t.V0 + t.V1 + t.V2) / 3f;
            var dirToTri = center - camPos;
            float facing = Vector3.Dot(t.Normal, dirToTri);
            if (!t.DoubleSided && facing > 0) continue;  // backface

            // Effective normal — flip for double-sided faces lit from behind.
            Vector3 nEff = (t.DoubleSided && facing > 0) ? -t.Normal : t.Normal;

            var p0 = Project(t.V0, vp, size); if (!p0.HasValue) continue;
            var p1 = Project(t.V1, vp, size); if (!p1.HasValue) continue;
            var p2 = Project(t.V2, vp, size); if (!p2.HasValue) continue;

            // Camera-space Z: more negative = farther (we use abs distance).
            float d0 = Vector4.Transform(new Vector4(t.V0, 1f), view).Z;
            float d1 = Vector4.Transform(new Vector4(t.V1, 1f), view).Z;
            float d2 = Vector4.Transform(new Vector4(t.V2, 1f), view).Z;
            float depth = (d0 + d1 + d2) / 3f;

            // Lambert shading + ambient floor of 0.45 so unlit faces don't
            // disappear into shadow.
            float lambert = MathF.Max(0f, -Vector3.Dot(nEff, lightDir));
            float intensity = 0.45f + 0.55f * lambert;

            var col = ToColor(t.Color * intensity);
            drawList.Add((new[] { p0.Value, p1.Value, p2.Value }, depth, t.Layer, col));
        }

        // Painter's, with layer as the primary key: floor → walls → objects,
        // and within each layer most-negative depth (farthest) drawn first.
        // The layer dimension fixes the classic large-floor-tri-over-small-
        // wall-tri artifact average depth alone can't avoid.
        drawList.Sort((a, b) =>
        {
            int byLayer = a.Layer.CompareTo(b.Layer);
            return byLayer != 0 ? byLayer : a.Depth.CompareTo(b.Depth);
        });

        foreach (var item in drawList)
            DrawTri(ctx, item.Pts, item.Col);

        // ── Room labels (drawn last so they sit on top of geometry). ──
        DrawRoomLabels(ctx, vm, env, vp, size, camPos);

        // ── 1-metre scale gizmo, bottom-right. ────────────────────
        DrawScaleBar(ctx, vm, vp, size);

        // ── Persistent mode hint. ─────────────────────────────────
        DrawHint(ctx, size, "3D preview — drag to orbit, right-drag to pan, wheel to zoom");
    }

    /// <summary>Soft drop-shadow projected onto the floor plane. The
    /// shadow is the actual projected footprint of the building, slightly
    /// inflated outward in world space — so when the camera orbits, the
    /// shadow rotates with the building (a screen-space ellipse like the
    /// previous version stayed axis-aligned to screen X/Y, which broke
    /// at every camera angle).
    ///
    /// Drawn as 3 stacked filled polygons with shrinking inflation +
    /// shrinking alpha, simulating a soft falloff cheaply.</summary>
    static void DrawGroundShadow(DrawingContext ctx, MainWindowViewModel vm, Matrix4x4 vp, Size size)
    {
        float w = vm.GridWidth  * vm.TileSize;
        float l = vm.GridLength * vm.TileSize;
        // Centre of the footprint in world space — used as the inflation
        // pivot. Each shadow layer pushes corners away from this centre.
        Vector3 centre = new(w * 0.5f, 0f, l * 0.5f);
        Vector3[] corners =
        {
            new(0, 0, 0), new(w, 0, 0), new(w, 0, l), new(0, 0, l),
        };
        // Three stacked layers, fading outward.
        var insets = new[] { 0.6f, 0.3f, 0.0f };  // metres of outward inflation
        var alphas = new byte[] { 56, 38, 22 };
        for (int s = 0; s < 3; s++)
        {
            var inflate = insets[s];
            var poly = new Point[4];
            bool ok = true;
            for (int i = 0; i < 4; i++)
            {
                Vector3 dir = corners[i] - centre;
                Vector3 worldP = corners[i] + Vector3.Normalize(dir) * inflate;
                worldP.Y = 0f;
                var p = Project(worldP, vp, size);
                if (!p.HasValue) { ok = false; break; }
                poly[i] = p.Value;
            }
            if (!ok) continue;

            var geom = new StreamGeometry();
            using (var gctx = geom.Open())
            {
                gctx.BeginFigure(poly[0], true);
                for (int i = 1; i < 4; i++) gctx.LineTo(poly[i]);
                gctx.EndFigure(true);
            }
            ctx.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(alphas[s], 0, 0, 0)),
                null, geom);
        }
    }

    /// <summary>1-metre scale bar bottom-right. Projects a 1-m segment
    /// along the world X axis at the camera pivot's height onto screen
    /// and draws the resulting span as a labelled rule.</summary>
    static void DrawScaleBar(DrawingContext ctx, MainWindowViewModel vm, Matrix4x4 vp, Size size)
    {
        var p0 = Project(new Vector3(vm.IsoPivotX, 0, vm.IsoPivotZ), vp, size);
        var p1 = Project(new Vector3(vm.IsoPivotX + 1f, 0, vm.IsoPivotZ), vp, size);
        if (!p0.HasValue || !p1.HasValue) return;
        double meterPx = System.Math.Abs(p1.Value.X - p0.Value.X);
        if (meterPx < 4) return;
        double barLeft  = size.Width - meterPx - 18;
        double barRight = size.Width - 18;
        double barY     = size.Height - 22;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 35, 38, 46)), 1.4);
        ctx.DrawLine(pen, new Point(barLeft, barY), new Point(barRight, barY));
        ctx.DrawLine(pen, new Point(barLeft,  barY - 4), new Point(barLeft,  barY + 4));
        ctx.DrawLine(pen, new Point(barRight, barY - 4), new Point(barRight, barY + 4));
        var fmt = new FormattedText("1 m",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 10.5,
            new SolidColorBrush(Color.FromArgb(220, 35, 38, 46)));
        ctx.DrawText(fmt, new Point((barLeft + barRight) * 0.5 - fmt.Width * 0.5, barY + 4));
    }

    // ── Camera helpers ────────────────────────────────────────────

    static void InitCameraIfNeeded(MainWindowViewModel vm)
    {
        if (vm.IsoCameraInitialised) return;
        // Pivot at world centre of the grid.
        float cx = vm.GridWidth  * vm.TileSize * 0.5f;
        float cz = vm.GridLength * vm.TileSize * 0.5f;
        vm.IsoPivotX = cx;
        vm.IsoPivotZ = cz;
        // Distance: pick something that fits the longest grid extent at
        // 45° fov with a bit of headroom.
        float diag = MathF.Sqrt(
            (vm.GridWidth * vm.TileSize) * (vm.GridWidth * vm.TileSize) +
            (vm.GridLength * vm.TileSize) * (vm.GridLength * vm.TileSize));
        vm.IsoDistance = MathF.Max(8f, diag * 1.1f);
        vm.IsoCameraInitialised = true;
    }

    static Vector3 ComputeCamPos(MainWindowViewModel vm, Vector3 pivot)
    {
        // Spherical orbit. Yaw rotates about Y, pitch tilts up from XZ.
        float cosP = MathF.Cos(vm.IsoPitch);
        float sinP = MathF.Sin(vm.IsoPitch);
        var dir = new Vector3(MathF.Sin(vm.IsoYaw) * cosP, sinP, MathF.Cos(vm.IsoYaw) * cosP);
        return pivot + dir * vm.IsoDistance;
    }

    static Point? Project(Vector3 worldPos, Matrix4x4 vp, Size size)
    {
        var clip = Vector4.Transform(new Vector4(worldPos, 1f), vp);
        if (clip.W <= 0.0001f) return null;
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        return new Point(
            (ndcX + 1f) * 0.5 * size.Width,
            (1f - ndcY) * 0.5 * size.Height);
    }

    // ── Mesh builders ──────────────────────────────────────────────

    /// <summary>Builds the same geometry the glTF exporter would and
    /// streams its triangles into the rasterizer's Tri list. Floor +
    /// wall meshes come from the core's RectangleInteriorBuilder /
    /// BoundaryWallBuilder (so wall thickness, doorway tunnels, and
    /// frame geometry around openings are real, not hand-rolled).
    /// Ceilings are dropped on purpose so the orbit camera can see
    /// inside ("dollhouse" view, like SketchUp / Revit).</summary>
    static void AddRealEnvironmentMeshes(List<Tri> tris, MainWindowViewModel vm, MultiRoomEnvironment env)
    {
        var interiorBuilder = new RectangleInteriorBuilder();
        var wallBuilder = new BoundaryWallBuilder();

        // Pre-build wall meshes once per adjacency (matches GltfExporter).
        var wallMeshes = new Dictionary<Adjacency, MeshData>(env.Adjacencies.Count);
        foreach (var adj in env.Adjacencies)
            wallMeshes[adj] = wallBuilder.Build(adj, vm.WallThickness, vm.WallHeight);

        // Per-room interior + per-adjacency walls, just like the exporter.
        foreach (var room in env.Rooms)
        {
            // Floor / ceiling colours match the 2D editor.
            Vector3 floorCol = vm.RoomFloorColors.TryGetValue(room.Id, out var fc)
                ? fc : MainWindowViewModel.RoomColorRgb(room.Id);
            if (vm.TileSaturation < 0.999)
                floorCol = GridEditorView.DesaturateRgbPublic(floorCol, (float)vm.TileSaturation);

            // Interior carries floor + ceiling submeshes; walls submesh is
            // empty there. Emit floor only — skip ceiling so we can see in.
            var interior = interiorBuilder.Build(room, vm.WallThickness, vm.WallHeight);
            EmitSubmesh(tris, interior, SubmeshIndex.Floor, floorCol, doubleSided: false, layer: LayerFloor);

            // Each adjacency: emit its real wall geometry once, owned by
            // the lower-id room (matches GltfExporter ownership rules so
            // we don't double-render shared walls).
            foreach (var adj in env.Adjacencies)
            {
                if (adj.RoomA != room && adj.RoomB != room) continue;
                int ownerId = adj.IsOuter ? adj.RoomA.Id
                            : (adj.RoomA.Id < adj.RoomB!.Id ? adj.RoomA.Id : adj.RoomB.Id);
                if (ownerId != room.Id) continue;

                // Per-face wall colour: the face that points toward Room A
                // (positive dot with SharedSegment.Normal) is what Room A
                // sees from the inside, so it gets Room A's wall colour.
                // The opposite face is what Room B sees → Room B's colour.
                // Top / bottom / end faces (normal nearly perpendicular to
                // the segment normal) use the mix of the two.
                Vector3 colA = vm.EffectiveWallColor(adj.RoomA.Id, adj);
                Vector3 colB = adj.RoomB is { } rB
                    ? vm.EffectiveWallColor(rB.Id, adj)
                    : colA * 0.7f;
                var n2 = adj.SharedSegment.Normal;
                Vector3 nAdj = new(n2.X, 0f, n2.Y);
                EmitWallSubmesh(tris, wallMeshes[adj], nAdj, colA, colB);

                // The wall's own floor frame (around the doorway) belongs
                // to the lower-id owner's floor mesh in the exporter; emit
                // it here too so doorway thresholds aren't holes.
                EmitSubmesh(tris, wallMeshes[adj], SubmeshIndex.Floor, floorCol, doubleSided: false, layer: LayerFloor);
            }
        }
    }

    /// <summary>Like EmitSubmesh, but every wall triangle gets its colour
    /// chosen by whichever room sees that face. Room A's wall colour for
    /// faces pointing along the adjacency normal, Room B's for the
    /// opposite side, the mix for top / bottom / wall-end caps.</summary>
    static void EmitWallSubmesh(
        List<Tri> tris, MeshData mesh, Vector3 nAdj,
        Vector3 colA, Vector3 colB)
    {
        if (SubmeshIndex.Walls >= mesh.SubmeshCount) return;
        var idx = mesh.SubmeshIndices[SubmeshIndex.Walls];
        var v = mesh.Vertices;
        var n = mesh.Normals;
        Vector3 colMix = (colA + colB) * 0.5f;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int ia = idx[i], ib = idx[i + 1], ic = idx[i + 2];
            Vector3 normal = (n[ia] + n[ib] + n[ic]) / 3f;
            float nLen = normal.Length();
            if (nLen < 1e-4f)
            {
                normal = Vector3.Cross(v[ib] - v[ia], v[ic] - v[ia]);
                nLen = normal.Length();
                if (nLen < 1e-4f) continue;
            }
            normal /= nLen;
            // Side selection. Threshold of 0.5 so cap faces (top/bottom/
            // ends, where the dot is ~0) take the mix; clearly side-facing
            // faces (dot near ±1) take the corresponding room's colour.
            float side = Vector3.Dot(normal, nAdj);
            Vector3 col = side >  0.5f ? colA
                        : side < -0.5f ? colB
                        : colMix;
            tris.Add(new Tri(v[ia], v[ib], v[ic], normal, col, doubleSided: false, layer: LayerWall));
        }
    }

    /// <summary>Streams one MeshData submesh into the rasterizer's Tri
    /// buffer. Vertex normals from the source mesh are averaged per
    /// triangle so the rasterizer can do a single shading lookup per
    /// face (Lambert), rather than per-vertex Phong.</summary>
    static void EmitSubmesh(List<Tri> tris, MeshData mesh, int submeshIndex, Vector3 color, bool doubleSided, int layer = LayerWall)
    {
        if (submeshIndex < 0 || submeshIndex >= mesh.SubmeshCount) return;
        var idx = mesh.SubmeshIndices[submeshIndex];
        var v = mesh.Vertices;
        var n = mesh.Normals;
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int ia = idx[i], ib = idx[i + 1], ic = idx[i + 2];
            // Face normal: average the per-vertex normals (close enough
            // for flat-shaded preview), fall back to cross product if the
            // average is degenerate.
            Vector3 normal = (n[ia] + n[ib] + n[ic]) / 3f;
            float nLen = normal.Length();
            if (nLen < 1e-4f)
            {
                normal = Vector3.Cross(v[ib] - v[ia], v[ic] - v[ia]);
                nLen = normal.Length();
                if (nLen < 1e-4f) continue;
            }
            normal /= nLen;
            tris.Add(new Tri(v[ia], v[ib], v[ic], normal, color, doubleSided, layer));
        }
    }

    static void AddObjects(List<Tri> tris, MainWindowViewModel vm)
    {
        for (int i = 0; i < vm.Objects.Count; i++)
        {
            var o = vm.Objects[i];
            var t = vm.GetObjectType(o.Slot);
            if (t is null) continue;
            var center = new Vector3(o.Position.X, o.Position.Y + t.Size, o.Position.Z);
            float r = t.Size; // radius / half-extent
            switch (t.Shape)
            {
                case ObjectShape.Cube:           AddBox(tris, center, new Vector3(r, r, r), t.Color, o.Rotation); break;
                case ObjectShape.Sphere:         AddSphere(tris, center, r, t.Color, 14, 10); break;
                case ObjectShape.Cylinder:       AddCylinder(tris, center, r, r * 1.5f, t.Color, 16); break;
                case ObjectShape.SquatCylinder:  AddCylinder(tris, center, r, r * 0.6f, t.Color, 16); break;
                case ObjectShape.Cone:           AddCone(tris, center, r, r * 1.6f, t.Color, 16); break;
                case ObjectShape.Capsule:        AddCapsule(tris, center, r * 0.6f, r, t.Color, 14, 6); break;
                case ObjectShape.Pyramid:        AddPyramid(tris, center, r, r * 1.3f, t.Color); break;
            }
        }
    }

    // ── Primitive mesh emitters ───────────────────────────────────

    static void AddBox(List<Tri> tris, Vector3 c, Vector3 half, Vector3 col, float rotY)
    {
        // Eight corners of the box in local space, then rotate around Y.
        float cosR = MathF.Cos(rotY), sinR = MathF.Sin(rotY);
        Vector3 R(Vector3 p)
        {
            var d = p - c;
            return new Vector3(c.X + d.X * cosR - d.Z * sinR, p.Y, c.Z + d.X * sinR + d.Z * cosR);
        }
        Vector3 m = c - half, M = c + half;
        // Eight corners.
        Vector3 v0 = R(new(m.X, m.Y, m.Z));
        Vector3 v1 = R(new(M.X, m.Y, m.Z));
        Vector3 v2 = R(new(M.X, m.Y, M.Z));
        Vector3 v3 = R(new(m.X, m.Y, M.Z));
        Vector3 v4 = R(new(m.X, M.Y, m.Z));
        Vector3 v5 = R(new(M.X, M.Y, m.Z));
        Vector3 v6 = R(new(M.X, M.Y, M.Z));
        Vector3 v7 = R(new(m.X, M.Y, M.Z));
        AddQuad(tris, v3, v2, v1, v0, -Vector3.UnitY, col); // bottom
        AddQuad(tris, v4, v5, v6, v7, Vector3.UnitY, col); // top
        AddQuad(tris, v0, v1, v5, v4, RotY(-Vector3.UnitZ, rotY), col); // -Z
        AddQuad(tris, v2, v3, v7, v6, RotY(Vector3.UnitZ, rotY), col); // +Z
        AddQuad(tris, v3, v0, v4, v7, RotY(-Vector3.UnitX, rotY), col); // -X
        AddQuad(tris, v1, v2, v6, v5, RotY(Vector3.UnitX, rotY), col); // +X
    }

    static Vector3 RotY(Vector3 v, float r)
    {
        float c = MathF.Cos(r), s = MathF.Sin(r);
        return new(v.X * c - v.Z * s, v.Y, v.X * s + v.Z * c);
    }

    static void AddQuad(List<Tri> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Vector3 col)
    {
        tris.Add(new Tri(a, b, c, n, col, layer: LayerObject));
        tris.Add(new Tri(a, c, d, n, col, layer: LayerObject));
    }

    static void AddSphere(List<Tri> tris, Vector3 center, float r, Vector3 col, int lon, int lat)
    {
        for (int j = 0; j < lat; j++)
        {
            float phi0 = MathF.PI * j / lat - MathF.PI / 2;
            float phi1 = MathF.PI * (j + 1) / lat - MathF.PI / 2;
            for (int i = 0; i < lon; i++)
            {
                float th0 = 2 * MathF.PI * i / lon;
                float th1 = 2 * MathF.PI * (i + 1) / lon;
                Vector3 p00 = SpherePt(center, r, phi0, th0);
                Vector3 p01 = SpherePt(center, r, phi0, th1);
                Vector3 p10 = SpherePt(center, r, phi1, th0);
                Vector3 p11 = SpherePt(center, r, phi1, th1);
                Vector3 n0 = Vector3.Normalize(((p00 + p01 + p10) / 3f) - center);
                Vector3 n1 = Vector3.Normalize(((p01 + p10 + p11) / 3f) - center);
                tris.Add(new Tri(p00, p01, p11, n0, col, layer: LayerObject));
                tris.Add(new Tri(p00, p11, p10, n1, col, layer: LayerObject));
            }
        }
    }

    static Vector3 SpherePt(Vector3 c, float r, float phi, float th)
        => c + new Vector3(r * MathF.Cos(phi) * MathF.Cos(th), r * MathF.Sin(phi), r * MathF.Cos(phi) * MathF.Sin(th));

    static void AddCylinder(List<Tri> tris, Vector3 center, float r, float halfH, Vector3 col, int sides)
    {
        Vector3 top = center + new Vector3(0, halfH, 0);
        Vector3 bot = center - new Vector3(0, halfH, 0);
        for (int i = 0; i < sides; i++)
        {
            float a0 = 2 * MathF.PI * i / sides;
            float a1 = 2 * MathF.PI * (i + 1) / sides;
            Vector3 t0 = top + new Vector3(r * MathF.Cos(a0), 0, r * MathF.Sin(a0));
            Vector3 t1 = top + new Vector3(r * MathF.Cos(a1), 0, r * MathF.Sin(a1));
            Vector3 b0 = bot + new Vector3(r * MathF.Cos(a0), 0, r * MathF.Sin(a0));
            Vector3 b1 = bot + new Vector3(r * MathF.Cos(a1), 0, r * MathF.Sin(a1));
            Vector3 nSide = Vector3.Normalize(new Vector3(MathF.Cos((a0 + a1) * 0.5f), 0, MathF.Sin((a0 + a1) * 0.5f)));
            tris.Add(new Tri(b0, b1, t1, nSide, col, layer: LayerObject));
            tris.Add(new Tri(b0, t1, t0, nSide, col, layer: LayerObject));
            // Caps.
            tris.Add(new Tri(top, t0, t1, Vector3.UnitY, col, layer: LayerObject));
            tris.Add(new Tri(bot, b1, b0, -Vector3.UnitY, col, layer: LayerObject));
        }
    }

    static void AddCone(List<Tri> tris, Vector3 center, float r, float h, Vector3 col, int sides)
    {
        Vector3 apex = center + new Vector3(0, h * 0.5f, 0);
        Vector3 baseC = center - new Vector3(0, h * 0.5f, 0);
        for (int i = 0; i < sides; i++)
        {
            float a0 = 2 * MathF.PI * i / sides;
            float a1 = 2 * MathF.PI * (i + 1) / sides;
            Vector3 b0 = baseC + new Vector3(r * MathF.Cos(a0), 0, r * MathF.Sin(a0));
            Vector3 b1 = baseC + new Vector3(r * MathF.Cos(a1), 0, r * MathF.Sin(a1));
            Vector3 nSide = Vector3.Normalize(Vector3.Cross(b1 - b0, apex - b0));
            tris.Add(new Tri(b0, b1, apex, nSide, col, layer: LayerObject));
            tris.Add(new Tri(baseC, b1, b0, -Vector3.UnitY, col, layer: LayerObject));
        }
    }

    static void AddCapsule(List<Tri> tris, Vector3 center, float r, float halfTotal, Vector3 col, int lon, int lat)
    {
        // Cylinder body of height (halfTotal*2 - 2r), capped with hemispheres.
        float cylHalf = MathF.Max(0, halfTotal - r);
        Vector3 top = center + new Vector3(0, cylHalf, 0);
        Vector3 bot = center - new Vector3(0, cylHalf, 0);
        // Cylinder side
        for (int i = 0; i < lon; i++)
        {
            float a0 = 2 * MathF.PI * i / lon;
            float a1 = 2 * MathF.PI * (i + 1) / lon;
            Vector3 t0 = top + new Vector3(r * MathF.Cos(a0), 0, r * MathF.Sin(a0));
            Vector3 t1 = top + new Vector3(r * MathF.Cos(a1), 0, r * MathF.Sin(a1));
            Vector3 b0 = bot + new Vector3(r * MathF.Cos(a0), 0, r * MathF.Sin(a0));
            Vector3 b1 = bot + new Vector3(r * MathF.Cos(a1), 0, r * MathF.Sin(a1));
            Vector3 nSide = Vector3.Normalize(new Vector3(MathF.Cos((a0 + a1) * 0.5f), 0, MathF.Sin((a0 + a1) * 0.5f)));
            tris.Add(new Tri(b0, b1, t1, nSide, col, layer: LayerObject));
            tris.Add(new Tri(b0, t1, t0, nSide, col, layer: LayerObject));
        }
        // Hemispheres
        AddHemisphere(tris, top, r, col, lon, lat / 2, +1);
        AddHemisphere(tris, bot, r, col, lon, lat / 2, -1);
    }

    static void AddHemisphere(List<Tri> tris, Vector3 center, float r, Vector3 col, int lon, int lat, int sign)
    {
        for (int j = 0; j < lat; j++)
        {
            float phi0 = (MathF.PI / 2) * j / lat * sign;
            float phi1 = (MathF.PI / 2) * (j + 1) / lat * sign;
            for (int i = 0; i < lon; i++)
            {
                float th0 = 2 * MathF.PI * i / lon;
                float th1 = 2 * MathF.PI * (i + 1) / lon;
                Vector3 p00 = SpherePt(center, r, phi0, th0);
                Vector3 p01 = SpherePt(center, r, phi0, th1);
                Vector3 p10 = SpherePt(center, r, phi1, th0);
                Vector3 p11 = SpherePt(center, r, phi1, th1);
                Vector3 n = Vector3.Normalize(((p00 + p11) / 2f) - center);
                if (sign > 0)
                {
                    tris.Add(new Tri(p00, p01, p11, n, col, layer: LayerObject));
                    tris.Add(new Tri(p00, p11, p10, n, col, layer: LayerObject));
                }
                else
                {
                    tris.Add(new Tri(p00, p11, p01, n, col, layer: LayerObject));
                    tris.Add(new Tri(p00, p10, p11, n, col, layer: LayerObject));
                }
            }
        }
    }

    static void AddPyramid(List<Tri> tris, Vector3 center, float halfBase, float h, Vector3 col)
    {
        Vector3 apex = center + new Vector3(0, h * 0.5f, 0);
        Vector3 a = center + new Vector3(-halfBase, -h * 0.5f, -halfBase);
        Vector3 b = center + new Vector3( halfBase, -h * 0.5f, -halfBase);
        Vector3 c = center + new Vector3( halfBase, -h * 0.5f,  halfBase);
        Vector3 d = center + new Vector3(-halfBase, -h * 0.5f,  halfBase);
        // Base
        tris.Add(new Tri(a, c, b, -Vector3.UnitY, col, layer: LayerObject));
        tris.Add(new Tri(a, d, c, -Vector3.UnitY, col, layer: LayerObject));
        // Sides
        Vector3 N(Vector3 p, Vector3 q) => Vector3.Normalize(Vector3.Cross(q - p, apex - p));
        tris.Add(new Tri(a, b, apex, N(a, b), col, layer: LayerObject));
        tris.Add(new Tri(b, c, apex, N(b, c), col, layer: LayerObject));
        tris.Add(new Tri(c, d, apex, N(c, d), col, layer: LayerObject));
        tris.Add(new Tri(d, a, apex, N(d, a), col, layer: LayerObject));
    }

    // ── Drawing helpers ────────────────────────────────────────────

    static void DrawTri(DrawingContext ctx, Point[] pts, Color col)
    {
        var geom = new StreamGeometry();
        using (var gctx = geom.Open())
        {
            gctx.BeginFigure(pts[0], true);
            gctx.LineTo(pts[1]);
            gctx.LineTo(pts[2]);
            gctx.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(col), null, geom);
    }

    static void DrawRoomLabels(
        DrawingContext ctx, MainWindowViewModel vm, MultiRoomEnvironment env,
        Matrix4x4 vp, Size size, Vector3 camPos)
    {
        var typeface = new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold);
        var bg = new SolidColorBrush(Color.FromArgb(220, 35, 38, 46));
        var fg = new SolidColorBrush(Colors.White);
        for (int rid = 0; rid < env.Rooms.Count; rid++)
        {
            // Room centre = mean of its tile centres.
            int n = 0; float sumX = 0, sumZ = 0;
            for (int x = 0; x < vm.GridWidth; x++)
                for (int z = 0; z < vm.GridLength; z++)
                    if (vm.RoomGrid[x, z] == rid)
                    {
                        sumX += (x + 0.5f) * vm.TileSize;
                        sumZ += (z + 0.5f) * vm.TileSize;
                        n++;
                    }
            if (n == 0) continue;
            var labelWorld = new Vector3(sumX / n, vm.WallHeight + 0.4f, sumZ / n);
            var screen = Project(labelWorld, vp, size);
            if (!screen.HasValue) continue;
            var roomName = vm.GetRoomName(rid);
            string text = string.IsNullOrWhiteSpace(roomName) ? $"Room {rid}" : roomName;
            var fmt = new FormattedText(text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 11, fg);
            const double padX = 6, padY = 2;
            var rect = new Rect(
                screen.Value.X - fmt.Width * 0.5 - padX,
                screen.Value.Y - fmt.Height * 0.5 - padY,
                fmt.Width + padX * 2,
                fmt.Height + padY * 2);
            ctx.DrawRectangle(bg, null, rect, 4, 4);
            ctx.DrawText(fmt, new Point(rect.X + padX, rect.Y + padY));
        }
        _ = camPos;
    }

    static void DrawHint(DrawingContext ctx, Size size, string text)
    {
        var hintBrush = new SolidColorBrush(Color.FromArgb(180, 90, 98, 112));
        var hint = new FormattedText(text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 11.5, hintBrush);
        ctx.DrawText(hint, new Point(12, size.Height - hint.Height - 8));
    }

    static Color ToColor(Vector3 rgb)
    {
        byte r = (byte)System.Math.Clamp(rgb.X * 255f, 0f, 255f);
        byte g = (byte)System.Math.Clamp(rgb.Y * 255f, 0f, 255f);
        byte b = (byte)System.Math.Clamp(rgb.Z * 255f, 0f, 255f);
        return Color.FromRgb(r, g, b);
    }
}
