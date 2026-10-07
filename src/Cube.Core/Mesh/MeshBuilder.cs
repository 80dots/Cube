using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>폴리곤 프리미티브 생성. Maya polyCube/polyPlane/... 의 기본값과 비슷한 형태로 만든다.</summary>
public static class MeshBuilder
{
    /// <summary>원점 중심 큐브. 모든 엣지는 하드(Maya 큐브 기본 표시와 동일). 각 면 UV는 0..1.</summary>
    public static PolyMesh Cube(float width = 1f, float height = 1f, float depth = 1f)
    {
        var m = new PolyMesh();
        float x = width / 2, y = height / 2, z = depth / 2;
        int[] v =
        {
            m.AddVertex(new(-x, -y,  z)), m.AddVertex(new( x, -y,  z)), m.AddVertex(new( x,  y,  z)), m.AddVertex(new(-x,  y,  z)), // 앞(+Z)
            m.AddVertex(new(-x, -y, -z)), m.AddVertex(new( x, -y, -z)), m.AddVertex(new( x,  y, -z)), m.AddVertex(new(-x,  y, -z)), // 뒤(-Z)
        };
        // 반시계(바깥에서 볼 때)
        AddQuad(m, v[0], v[1], v[2], v[3]); // +Z
        AddQuad(m, v[5], v[4], v[7], v[6]); // -Z
        AddQuad(m, v[1], v[5], v[6], v[2]); // +X
        AddQuad(m, v[4], v[0], v[3], v[7]); // -X
        AddQuad(m, v[3], v[2], v[6], v[7]); // +Y
        AddQuad(m, v[4], v[5], v[1], v[0]); // -Y
        SetAllEdgesHard(m, true);
        SetAllEdgesSeam(m, true); // 면마다 독립된 UV 섬
        MeshNormals.Recompute(m);
        return m;
    }

    /// <summary>점 목록으로 n각형 하나(Create Polygon Tool). 점은 순서대로 루프를 이루며, 법선이 normalHint 쪽을 향하도록 뒤집는다.</summary>
    public static PolyMesh Polygon(IReadOnlyList<Vector3> points, Vector3 normalHint)
    {
        var m = new PolyMesh();
        if (points.Count < 3) return m;
        var ids = points.Select(p => m.AddVertex(p)).ToArray();
        // 뉴웰 법선
        var n = Vector3.Zero;
        for (int i = 0; i < points.Count; i++) { var a = points[i]; var b = points[(i + 1) % points.Count]; n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)); }
        if (Vector3.Dot(n, normalHint) < 0) Array.Reverse(ids);
        // 평면 UV: 가장 큰 범위 두 축으로 0..1
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var p in points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var ext = max - min; var (u, v) = Uv.UvOps.ProjectionBasis(Vector3.Normalize(n.LengthSquared() > 1e-12f ? n : normalHint));
        int f = m.AddFace(ids);
        if (f >= 0)
        {
            float size = MathF.Max(MathF.Max(ext.X, ext.Y), MathF.Max(ext.Z, 1e-6f));
            int start = m.Faces[f].HalfEdge, he = start;
            do { var h = m.Hes[he]; var p = m.Verts[h.Vertex].Position - min; h.Uv0 = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)) / size; m.Hes[he] = h; he = h.Next; } while (he != start);
        }
        MeshNormals.Recompute(m);
        m.BumpTopology();
        return m;
    }

    /// <summary>XZ 평면(법선 +Y), 원점 중심, 분할 가능.</summary>
    public static PolyMesh Plane(float width = 1f, float depth = 1f, int subdivX = 1, int subdivZ = 1)
    {
        subdivX = Math.Max(1, subdivX); subdivZ = Math.Max(1, subdivZ);
        var m = new PolyMesh();
        var ids = new int[(subdivX + 1) * (subdivZ + 1)];
        for (int j = 0; j <= subdivZ; j++)
            for (int i = 0; i <= subdivX; i++)
            {
                float u = (float)i / subdivX, w = (float)j / subdivZ;
                ids[j * (subdivX + 1) + i] = m.AddVertex(new(-width / 2 + u * width, 0, depth / 2 - w * depth));
            }
        Span<int> q = stackalloc int[4];
        for (int j = 0; j < subdivZ; j++)
            for (int i = 0; i < subdivX; i++)
            {
                int a = j * (subdivX + 1) + i, b = a + 1, c = a + subdivX + 1, d = c + 1;
                // 위(+Y)에서 볼 때 반시계: a(앞좌) b(앞우) d(뒤우) c(뒤좌)
                q[0] = ids[a]; q[1] = ids[b]; q[2] = ids[d]; q[3] = ids[c];
                int f = m.AddFace(q);
                SetFaceUvs(m, f,
                    new Vector2((float)i / subdivX, (float)j / subdivZ), new Vector2((float)(i + 1) / subdivX, (float)j / subdivZ),
                    new Vector2((float)(i + 1) / subdivX, (float)(j + 1) / subdivZ), new Vector2((float)i / subdivX, (float)(j + 1) / subdivZ));
            }
        MeshNormals.Recompute(m);
        return m;
    }

    /// <summary>Y축 원기둥. 옆면은 소프트, 캡 테두리는 하드. 캡은 n-gon 하나.</summary>
    public static PolyMesh Cylinder(float radius = 0.5f, float height = 1f, int segments = 20, bool caps = true)
    {
        segments = Math.Max(3, segments);
        var m = new PolyMesh();
        var bottom = new int[segments]; var top = new int[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.Tau * i / segments;
            float cx = MathF.Cos(a) * radius, cz = -MathF.Sin(a) * radius; // 위에서 볼 때 반시계
            bottom[i] = m.AddVertex(new(cx, -height / 2, cz));
            top[i] = m.AddVertex(new(cx, height / 2, cz));
        }
        Span<int> q = stackalloc int[4];
        for (int i = 0; i < segments; i++)
        {
            int n = (i + 1) % segments;
            q[0] = bottom[i]; q[1] = bottom[n]; q[2] = top[n]; q[3] = top[i];
            int f = m.AddFace(q);
            float u0 = (float)i / segments, u1 = (float)(i + 1) / segments;
            SetFaceUvs(m, f, new(u0, 0), new(u1, 0), new(u1, 1), new(u0, 1));
        }
        if (caps)
        {
            var topLoop = new int[segments]; var botLoop = new int[segments];
            for (int i = 0; i < segments; i++) { topLoop[i] = top[i]; botLoop[i] = bottom[segments - 1 - i]; }
            int ft = m.AddFace(topLoop); int fb = m.AddFace(botLoop);
            SetCapUvs(m, ft, radius); SetCapUvs(m, fb, radius);
            for (int i = 0; i < segments; i++)
            {
                SetEdgeHard(m, top[i], top[(i + 1) % segments], true);
                SetEdgeHard(m, bottom[i], bottom[(i + 1) % segments], true);
                SetEdgeSeam(m, top[i], top[(i + 1) % segments], true);
                SetEdgeSeam(m, bottom[i], bottom[(i + 1) % segments], true);
            }
        }
        SetEdgeSeam(m, bottom[0], top[0], true); // u가 0/1로 갈리는 세로 심
        MeshNormals.Recompute(m);
        return m;
    }

    /// <summary>Y축 원뿔(꼭짓점 위). 캡은 n-gon.</summary>
    public static PolyMesh Cone(float radius = 0.5f, float height = 1f, int segments = 20, bool cap = true)
    {
        segments = Math.Max(3, segments);
        var m = new PolyMesh();
        var ring = new int[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.Tau * i / segments;
            ring[i] = m.AddVertex(new(MathF.Cos(a) * radius, -height / 2, -MathF.Sin(a) * radius));
        }
        int apex = m.AddVertex(new(0, height / 2, 0));
        Span<int> t = stackalloc int[3];
        for (int i = 0; i < segments; i++)
        {
            int n = (i + 1) % segments;
            t[0] = ring[i]; t[1] = ring[n]; t[2] = apex;
            int f = m.AddFace(t);
            SetFaceUvs(m, f, new((float)i / segments, 0), new((float)(i + 1) / segments, 0), new((i + 0.5f) / segments, 1));
        }
        if (cap)
        {
            var loop = new int[segments];
            for (int i = 0; i < segments; i++) loop[i] = ring[segments - 1 - i];
            int fb = m.AddFace(loop);
            SetCapUvs(m, fb, radius);
            for (int i = 0; i < segments; i++) { SetEdgeHard(m, ring[i], ring[(i + 1) % segments], true); SetEdgeSeam(m, ring[i], ring[(i + 1) % segments], true); }
        }
        SetEdgeSeam(m, ring[0], apex, true);
        MeshNormals.Recompute(m);
        return m;
    }

    /// <summary>UV 구. 극은 삼각형 팬, 나머지는 쿼드. 모두 소프트.</summary>
    public static PolyMesh Sphere(float radius = 0.5f, int segments = 20, int rings = 10)
    {
        segments = Math.Max(3, segments); rings = Math.Max(2, rings);
        var m = new PolyMesh();
        int top = m.AddVertex(new(0, radius, 0));
        var grid = new int[rings - 1, segments];
        for (int r = 1; r < rings; r++)
        {
            float phi = MathF.PI * r / rings;
            float y = MathF.Cos(phi) * radius, rr = MathF.Sin(phi) * radius;
            for (int s = 0; s < segments; s++)
            {
                float a = MathF.Tau * s / segments;
                grid[r - 1, s] = m.AddVertex(new(MathF.Cos(a) * rr, y, -MathF.Sin(a) * rr));
            }
        }
        int bottom = m.AddVertex(new(0, -radius, 0));
        Span<int> t = stackalloc int[3]; Span<int> q = stackalloc int[4];
        for (int s = 0; s < segments; s++)
        {
            int n = (s + 1) % segments;
            t[0] = grid[0, s]; t[1] = grid[0, n]; t[2] = top;
            int f = m.AddFace(t);
            SetFaceUvs(m, f, new((float)s / segments, 1f - 1f / rings), new((float)(s + 1) / segments, 1f - 1f / rings), new((s + 0.5f) / segments, 1));
        }
        for (int r = 0; r < rings - 2; r++)
            for (int s = 0; s < segments; s++)
            {
                int n = (s + 1) % segments;
                q[0] = grid[r + 1, s]; q[1] = grid[r + 1, n]; q[2] = grid[r, n]; q[3] = grid[r, s];
                int f = m.AddFace(q);
                float v0 = 1f - (float)(r + 2) / rings, v1 = 1f - (float)(r + 1) / rings;
                SetFaceUvs(m, f, new((float)s / segments, v0), new((float)(s + 1) / segments, v0), new((float)(s + 1) / segments, v1), new((float)s / segments, v1));
            }
        for (int s = 0; s < segments; s++)
        {
            int n = (s + 1) % segments;
            t[0] = bottom; t[1] = grid[rings - 2, n]; t[2] = grid[rings - 2, s];
            int f = m.AddFace(t);
            SetFaceUvs(m, f, new((s + 0.5f) / segments, 0), new((float)(s + 1) / segments, 1f / rings), new((float)s / segments, 1f / rings));
        }
        // u가 0/1로 갈리는 자오선 심
        SetEdgeSeam(m, top, grid[0, 0], true);
        for (int r = 0; r < rings - 2; r++) SetEdgeSeam(m, grid[r, 0], grid[r + 1, 0], true);
        SetEdgeSeam(m, grid[rings - 2, 0], bottom, true);
        MeshNormals.Recompute(m);
        return m;
    }

    /// <summary>Y축 토러스. 모두 쿼드, 소프트.</summary>
    public static PolyMesh Torus(float radius = 0.5f, float sectionRadius = 0.2f, int segments = 20, int sections = 12)
    {
        segments = Math.Max(3, segments); sections = Math.Max(3, sections);
        var m = new PolyMesh();
        var grid = new int[segments, sections];
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.Tau * i / segments;
            var center = new Vector3(MathF.Cos(a) * radius, 0, -MathF.Sin(a) * radius);
            var radial = new Vector3(MathF.Cos(a), 0, -MathF.Sin(a));
            for (int j = 0; j < sections; j++)
            {
                float b = MathF.Tau * j / sections;
                grid[i, j] = m.AddVertex(center + radial * (MathF.Cos(b) * sectionRadius) + Vector3.UnitY * (MathF.Sin(b) * sectionRadius));
            }
        }
        Span<int> q = stackalloc int[4];
        for (int i = 0; i < segments; i++)
        {
            int ni = (i + 1) % segments;
            for (int j = 0; j < sections; j++)
            {
                int nj = (j + 1) % sections;
                q[0] = grid[i, j]; q[1] = grid[ni, j]; q[2] = grid[ni, nj]; q[3] = grid[i, nj];
                int f = m.AddFace(q);
                SetFaceUvs(m, f, new((float)i / segments, (float)j / sections), new((float)(i + 1) / segments, (float)j / sections),
                    new((float)(i + 1) / segments, (float)(j + 1) / sections), new((float)i / segments, (float)(j + 1) / sections));
            }
        }
        for (int j = 0; j < sections; j++) SetEdgeSeam(m, grid[0, j], grid[0, (j + 1) % sections], true);
        for (int i = 0; i < segments; i++) SetEdgeSeam(m, grid[i, 0], grid[(i + 1) % segments, 0], true);
        MeshNormals.Recompute(m);
        return m;
    }

    // ---------------------------------------------------------------- 헬퍼

    private static int AddQuad(PolyMesh m, int a, int b, int c, int d)
    {
        Span<int> q = stackalloc int[4] { a, b, c, d };
        int f = m.AddFace(q);
        SetFaceUvs(m, f, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
        return f;
    }

    public static void SetFaceUvs(PolyMesh m, int f, params Vector2[] uvs)
    {
        if (f < 0) return;
        int start = m.Faces[f].HalfEdge, he = start, i = 0;
        do
        {
            var h = m.Hes[he];
            h.Uv0 = i < uvs.Length ? uvs[i] : Vector2.Zero;
            m.Hes[he] = h;
            he = h.Next; i++;
        } while (he != start);
    }

    private static void SetCapUvs(PolyMesh m, int f, float radius)
    {
        if (f < 0) return;
        int start = m.Faces[f].HalfEdge, he = start;
        do
        {
            var h = m.Hes[he];
            var p = m.Verts[h.Vertex].Position;
            h.Uv0 = new Vector2(0.5f + p.X / (2 * radius), 0.5f - p.Z / (2 * radius));
            m.Hes[he] = h;
            he = h.Next;
        } while (he != start);
    }

    public static void SetAllEdgesHard(PolyMesh m, bool hard)
    {
        for (int e = 0; e < m.Edges.Count; e++)
        {
            var ed = m.Edges[e];
            if (!ed.Alive) continue;
            ed.Hard = hard; m.Edges[e] = ed;
        }
    }

    public static void SetEdgeHard(PolyMesh m, int va, int vb, bool hard)
    {
        int e = m.FindEdge(va, vb);
        if (e < 0) return;
        var ed = m.Edges[e]; ed.Hard = hard; m.Edges[e] = ed;
    }

    public static void SetEdgeSeam(PolyMesh m, int va, int vb, bool seam)
    {
        int e = m.FindEdge(va, vb);
        if (e < 0) return;
        var ed = m.Edges[e]; ed.Seam = seam; m.Edges[e] = ed;
    }

    public static void SetAllEdgesSeam(PolyMesh m, bool seam)
    {
        for (int e = 0; e < m.Edges.Count; e++)
        {
            var ed = m.Edges[e];
            if (!ed.Alive) continue;
            ed.Seam = seam; m.Edges[e] = ed;
        }
    }
}
