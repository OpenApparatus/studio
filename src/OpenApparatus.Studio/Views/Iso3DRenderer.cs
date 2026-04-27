using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
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
    /// need at draw time.</summary>
    readonly struct Tri
    {
        public readonly Vector3 V0, V1, V2;
        public readonly Vector3 Normal;     // world-space face normal
        public readonly Vector3 Color;      // 0..1 RGB
        public readonly bool DoubleSided;   // true for walls (visible from both rooms)
        public Tri(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 normal, Vector3 color, bool doubleSided = false)
        { V0 = v0; V1 = v1; V2 = v2; Normal = normal; Color = color; DoubleSided = doubleSided; }
    }

    public static void Render(DrawingContext ctx, MainWindowViewModel vm, Size size)
    {
        // Background. Slightly cooler than the top-down's grey so the 3D
        // view reads as a different mode without being jarring.
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(228, 230, 236)), new Rect(size));

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

        // ── Mesh collection ──────────────────────────────────────
        var tris = new List<Tri>(2048);
        AddFloors(tris, vm);
        AddCeilings(tris, vm);
        AddWalls(tris, vm, env);
        AddObjects(tris, vm);

        // ── Project + cull + shade ─────────────────────────────
        // For each triangle: discard if any vertex is behind the camera,
        // discard if backfacing (single-sided only), record screen verts
        // + average camera-space depth + lit colour.
        var drawList = new List<(Point[] Pts, float Depth, Color Col)>(tris.Count);
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
            drawList.Add((new[] { p0.Value, p1.Value, p2.Value }, depth, col));
        }

        // Painter's: most-negative depth = farthest from camera, drawn first.
        drawList.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        foreach (var item in drawList)
            DrawTri(ctx, item.Pts, item.Col);

        // ── Room labels (drawn last so they sit on top of geometry). ──
        DrawRoomLabels(ctx, vm, env, vp, size, camPos);

        // ── Persistent mode hint. ─────────────────────────────────
        DrawHint(ctx, size, "3D preview — drag to orbit, right-drag to pan, wheel to zoom");
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

    static void AddFloors(List<Tri> tris, MainWindowViewModel vm)
    {
        Vector3 nUp = Vector3.UnitY;
        for (int xi = 0; xi < vm.GridWidth; xi++)
            for (int zi = 0; zi < vm.GridLength; zi++)
            {
                int id = vm.RoomGrid[xi, zi];
                if (id < 0) continue;
                Vector3 col = vm.RoomFloorColors.TryGetValue(id, out var c)
                    ? c : MainWindowViewModel.RoomColorRgb(id);
                if (vm.TileSaturation < 0.999)
                    col = GridEditorView.DesaturateRgbPublic(col, (float)vm.TileSaturation);
                float x0 = xi * vm.TileSize, x1 = x0 + vm.TileSize;
                float z0 = zi * vm.TileSize, z1 = z0 + vm.TileSize;
                Vector3 a = new(x0, 0, z0), b = new(x1, 0, z0);
                Vector3 cv = new(x1, 0, z1), d = new(x0, 0, z1);
                tris.Add(new Tri(a, b, cv, nUp, col));
                tris.Add(new Tri(a, cv, d, nUp, col));
            }
    }

    static void AddCeilings(List<Tri> tris, MainWindowViewModel vm)
    {
        // Skip ceilings — they'd block the orbit view from above. Common
        // pattern in floor-plan 3D viewers (SketchUp / Revit "doll-house"
        // mode does the same). If we ever want them they'd go here.
        _ = tris; _ = vm;
    }

    static void AddWalls(List<Tri> tris, MainWindowViewModel vm, MultiRoomEnvironment env)
    {
        float H = vm.WallHeight;
        foreach (var adj in env.Adjacencies)
        {
            if (adj.Passage is Passage.Open) continue;

            // Pick a wall colour. Use room A's effective wall colour for
            // the +n side and room B's for the -n side.
            Vector3 colA = vm.EffectiveWallColor(adj.RoomA.Id, adj);
            Vector3 colB = adj.RoomB is { } rB
                ? vm.EffectiveWallColor(rB.Id, adj)
                : colA * 0.7f;

            // Mix the two for a single double-sided slab — keeps the
            // mesh count down. Per-face two-sided colouring would need
            // separate triangulations per side.
            Vector3 col = (colA + colB) * 0.5f;

            var s = adj.SharedSegment;
            Vector3 p0 = new(s.Start.X, 0, s.Start.Y);
            Vector3 p1 = new(s.End.X,   0, s.End.Y);

            // Wall normal — perpendicular to (p1-p0) in the XZ plane.
            Vector3 along = p1 - p0;
            float lenAlong = along.Length();
            if (lenAlong < 1e-3f) continue;
            Vector3 dir = along / lenAlong;
            Vector3 normal = new(dir.Z, 0, -dir.X);

            // Decompose the wall into rectangular sub-rects when there
            // are doors / windows: solid sections + sill bars + lintels +
            // window glass panes.
            var openings = new List<(float t0, float t1, Opening op)>();
            if (adj.Passage is Passage.Doorway dw)
            {
                foreach (var op in dw.Openings)
                {
                    float center = op.OffsetAlongEdge / lenAlong;
                    float half = (op.Width * 0.5f) / lenAlong;
                    openings.Add((MathF.Max(0f, center - half),
                                  MathF.Min(1f, center + half), op));
                }
                openings.Sort((a, b) => a.t0.CompareTo(b.t0));
            }

            void Slab(float t0, float t1, float yBot, float yTop, Vector3 colSlab)
            {
                if (t1 - t0 < 1e-4f || yTop - yBot < 1e-4f) return;
                Vector3 a = new(p0.X + (p1.X - p0.X) * t0, yBot, p0.Z + (p1.Z - p0.Z) * t0);
                Vector3 b = new(p0.X + (p1.X - p0.X) * t1, yBot, p0.Z + (p1.Z - p0.Z) * t1);
                Vector3 c = new(p0.X + (p1.X - p0.X) * t1, yTop, p0.Z + (p1.Z - p0.Z) * t1);
                Vector3 d = new(p0.X + (p1.X - p0.X) * t0, yTop, p0.Z + (p1.Z - p0.Z) * t0);
                tris.Add(new Tri(a, b, c, normal, colSlab, doubleSided: true));
                tris.Add(new Tri(a, c, d, normal, colSlab, doubleSided: true));
            }

            float cursor = 0f;
            Vector3 glassCol = new(0.78f, 0.88f, 0.95f);  // light-blue tinted glass
            foreach (var (t0, t1, op) in openings)
            {
                Slab(cursor, t0, 0, H, col);                 // wall to the left of opening
                if (op.SillHeight > 1e-3f)
                    Slab(t0, t1, 0, op.SillHeight, col);     // sill (windows)
                if (op.Height < H - 1e-3f)
                    Slab(t0, t1, op.Height, H, col);         // lintel above opening
                if (op.IsWindow)
                    Slab(t0, t1, op.SillHeight, op.Height, glassCol);  // window glass pane
                // doors: leave the doorway empty
                cursor = t1;
            }
            Slab(cursor, 1f, 0, H, col);                     // wall to the right of last opening
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
        tris.Add(new Tri(a, b, c, n, col));
        tris.Add(new Tri(a, c, d, n, col));
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
                tris.Add(new Tri(p00, p01, p11, n0, col));
                tris.Add(new Tri(p00, p11, p10, n1, col));
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
            tris.Add(new Tri(b0, b1, t1, nSide, col));
            tris.Add(new Tri(b0, t1, t0, nSide, col));
            // Caps.
            tris.Add(new Tri(top, t0, t1, Vector3.UnitY, col));
            tris.Add(new Tri(bot, b1, b0, -Vector3.UnitY, col));
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
            tris.Add(new Tri(b0, b1, apex, nSide, col));
            tris.Add(new Tri(baseC, b1, b0, -Vector3.UnitY, col));
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
            tris.Add(new Tri(b0, b1, t1, nSide, col));
            tris.Add(new Tri(b0, t1, t0, nSide, col));
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
                    tris.Add(new Tri(p00, p01, p11, n, col));
                    tris.Add(new Tri(p00, p11, p10, n, col));
                }
                else
                {
                    tris.Add(new Tri(p00, p11, p01, n, col));
                    tris.Add(new Tri(p00, p10, p11, n, col));
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
        tris.Add(new Tri(a, c, b, -Vector3.UnitY, col));
        tris.Add(new Tri(a, d, c, -Vector3.UnitY, col));
        // Sides
        Vector3 N(Vector3 p, Vector3 q) => Vector3.Normalize(Vector3.Cross(q - p, apex - p));
        tris.Add(new Tri(a, b, apex, N(a, b), col));
        tris.Add(new Tri(b, c, apex, N(b, c), col));
        tris.Add(new Tri(c, d, apex, N(c, d), col));
        tris.Add(new Tri(d, a, apex, N(d, a), col));
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
