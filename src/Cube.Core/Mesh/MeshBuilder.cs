using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// 폴리곤 프리미티브 생성. Maya polyCube/polyPlane/... 의 기본값과 비슷한 형태로 만든다.
/// 모든 프리미티브는 원점 중심, Y-up, 미터 단위이며 면은 바깥에서 볼 때 반시계(코어 앞면 규약)로 만든다.
/// UV는 하단 원점(Maya) 0..1, 필요한 곳(면 경계·u 0/1 이음매)에는 심을 표시하고 마지막에 노멀을 계산해 돌려준다.
/// </summary>
public static class MeshBuilder
{
    /// <summary>원점 중심 큐브. 모든 엣지는 하드(Maya 큐브 기본 표시와 동일). 각 면 UV는 0..1.</summary>
    /// <param name="width">X 방향 크기.</param>
    /// <param name="height">Y 방향 크기.</param>
    /// <param name="depth">Z 방향 크기.</param>
    public static PolyMesh Cube(float width = 1f, float height = 1f, float depth = 1f)
    {
        var m = new PolyMesh();
        // 반 크기
        float x = width / 2, y = height / 2, z = depth / 2;
        // 정점 8개: 0~3 = 앞면(+Z) 좌하·우하·우상·좌상, 4~7 = 뒷면(-Z) 같은 순서
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
    /// <param name="points">다각형 꼭짓점(월드/로컬 동일, 순서대로 루프). 3개 미만이면 빈 메시.</param>
    /// <param name="normalHint">원하는 앞면 방향(보통 카메라 쪽). 뉴웰 법선이 반대면 정점 순서를 뒤집는다.</param>
    /// <returns>면 하나짜리 메시. 자기 교차 등으로 AddFace가 실패하면 정점만 있는 메시.</returns>
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
        // 투영 기저(u, v)는 법선(퇴화 시 힌트)에 수직인 평면 축
        var ext = max - min; var (u, v) = Uv.UvOps.ProjectionBasis(Vector3.Normalize(n.LengthSquared() > 1e-12f ? n : normalHint));
        int f = m.AddFace(ids);
        if (f >= 0)
        {
            // 가장 큰 AABB 변 길이로 나눠 비율을 유지한 채 대략 0..1로 맞춘다(min 모서리가 원점)
            float size = MathF.Max(MathF.Max(ext.X, ext.Y), MathF.Max(ext.Z, 1e-6f));
            int start = m.Faces[f].HalfEdge, he = start;
            do { var h = m.Hes[he]; var p = m.Verts[h.Vertex].Position - min; h.Uv0 = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)) / size; m.Hes[he] = h; he = h.Next; } while (he != start);
        }
        MeshNormals.Recompute(m);
        m.BumpTopology();
        return m;
    }

    /// <summary>XZ 평면(법선 +Y), 원점 중심, 분할 가능.</summary>
    /// <param name="width">X 방향 크기.</param>
    /// <param name="depth">Z 방향 크기.</param>
    /// <param name="subdivX">X 방향 분할 수(최소 1).</param>
    /// <param name="subdivZ">Z 방향 분할 수(최소 1).</param>
    public static PolyMesh Plane(float width = 1f, float depth = 1f, int subdivX = 1, int subdivZ = 1)
    {
        subdivX = Math.Max(1, subdivX); subdivZ = Math.Max(1, subdivZ);
        var m = new PolyMesh();
        // (subdivX+1)×(subdivZ+1) 격자 정점. j 증가 = -Z(뒤쪽) 방향, i 증가 = +X 방향
        var ids = new int[(subdivX + 1) * (subdivZ + 1)];
        for (int j = 0; j <= subdivZ; j++)
            for (int i = 0; i <= subdivX; i++)
            {
                float u = (float)i / subdivX, w = (float)j / subdivZ;
                ids[j * (subdivX + 1) + i] = m.AddVertex(new(-width / 2 + u * width, 0, depth / 2 - w * depth));
            }
        // 격자 칸마다 쿼드 하나, UV는 격자 비율 그대로(i/subdivX, j/subdivZ)
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
    /// <param name="radius">반지름.</param>
    /// <param name="height">높이(Y, 원점 중심이라 -h/2..h/2).</param>
    /// <param name="segments">둘레 분할 수(최소 3).</param>
    /// <param name="caps">위·아래 캡 면을 만들지.</param>
    public static PolyMesh Cylinder(float radius = 0.5f, float height = 1f, int segments = 20, bool caps = true)
    {
        segments = Math.Max(3, segments);
        var m = new PolyMesh();
        // 아래·위 링 정점
        var bottom = new int[segments]; var top = new int[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.Tau * i / segments;
            float cx = MathF.Cos(a) * radius, cz = -MathF.Sin(a) * radius; // 위에서 볼 때 반시계
            bottom[i] = m.AddVertex(new(cx, -height / 2, cz));
            top[i] = m.AddVertex(new(cx, height / 2, cz));
        }
        // 옆면 쿼드: u = 둘레 비율, v = 0(아래)..1(위)
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
            // 위 캡은 링 순서 그대로(위에서 반시계), 아래 캡은 역순(아래에서 볼 때 반시계)
            var topLoop = new int[segments]; var botLoop = new int[segments];
            for (int i = 0; i < segments; i++) { topLoop[i] = top[i]; botLoop[i] = bottom[segments - 1 - i]; }
            int ft = m.AddFace(topLoop); int fb = m.AddFace(botLoop);
            SetCapUvs(m, ft, radius); SetCapUvs(m, fb, radius);
            // 캡 테두리는 하드 + 심(캡은 별도 UV 섬)
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
    /// <param name="radius">밑면 반지름.</param>
    /// <param name="height">높이(밑면 y = -h/2, 꼭짓점 y = +h/2).</param>
    /// <param name="segments">둘레 분할 수(최소 3).</param>
    /// <param name="cap">밑면 캡 면을 만들지.</param>
    public static PolyMesh Cone(float radius = 0.5f, float height = 1f, int segments = 20, bool cap = true)
    {
        segments = Math.Max(3, segments);
        var m = new PolyMesh();
        // 밑면 링 정점(위에서 볼 때 반시계)
        var ring = new int[segments];
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.Tau * i / segments;
            ring[i] = m.AddVertex(new(MathF.Cos(a) * radius, -height / 2, -MathF.Sin(a) * radius));
        }
        int apex = m.AddVertex(new(0, height / 2, 0));
        // 옆면 삼각형: 링 두 점 + 꼭짓점. 꼭짓점 UV는 두 링 점 u의 가운데, v = 1
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
            // 밑면 캡은 링 역순(아래에서 볼 때 반시계). 테두리는 하드 + 심
            var loop = new int[segments];
            for (int i = 0; i < segments; i++) loop[i] = ring[segments - 1 - i];
            int fb = m.AddFace(loop);
            SetCapUvs(m, fb, radius);
            for (int i = 0; i < segments; i++) { SetEdgeHard(m, ring[i], ring[(i + 1) % segments], true); SetEdgeSeam(m, ring[i], ring[(i + 1) % segments], true); }
        }
        // u가 0/1로 갈리는 세로 심
        SetEdgeSeam(m, ring[0], apex, true);
        MeshNormals.Recompute(m);
        return m;
    }

    /// <summary>UV 구. 극은 삼각형 팬, 나머지는 쿼드. 모두 소프트.</summary>
    /// <param name="radius">반지름.</param>
    /// <param name="segments">경도(둘레) 분할 수(최소 3).</param>
    /// <param name="rings">위도 분할 수(최소 2). 극 사이에 rings-1개의 정점 링이 생긴다.</param>
    public static PolyMesh Sphere(float radius = 0.5f, int segments = 20, int rings = 10)
    {
        segments = Math.Max(3, segments); rings = Math.Max(2, rings);
        var m = new PolyMesh();
        // 북극 정점
        int top = m.AddVertex(new(0, radius, 0));
        // 극을 뺀 위도 링 정점 격자 [링, 둘레]. phi = 북극에서의 각도
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
        // 남극 정점
        int bottom = m.AddVertex(new(0, -radius, 0));
        Span<int> t = stackalloc int[3]; Span<int> q = stackalloc int[4];
        // 북극 팬: 첫 링과 북극을 잇는 삼각형(극 UV는 u 가운데, v = 1)
        for (int s = 0; s < segments; s++)
        {
            int n = (s + 1) % segments;
            t[0] = grid[0, s]; t[1] = grid[0, n]; t[2] = top;
            int f = m.AddFace(t);
            SetFaceUvs(m, f, new((float)s / segments, 1f - 1f / rings), new((float)(s + 1) / segments, 1f - 1f / rings), new((s + 0.5f) / segments, 1));
        }
        // 가운데 띠: 이웃한 두 링 사이 쿼드. v는 위(북극) = 1에서 아래로 감소
        for (int r = 0; r < rings - 2; r++)
            for (int s = 0; s < segments; s++)
            {
                int n = (s + 1) % segments;
                q[0] = grid[r + 1, s]; q[1] = grid[r + 1, n]; q[2] = grid[r, n]; q[3] = grid[r, s];
                int f = m.AddFace(q);
                float v0 = 1f - (float)(r + 2) / rings, v1 = 1f - (float)(r + 1) / rings;
                SetFaceUvs(m, f, new((float)s / segments, v0), new((float)(s + 1) / segments, v0), new((float)(s + 1) / segments, v1), new((float)s / segments, v1));
            }
        // 남극 팬(v = 0)
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
    /// <param name="radius">링 중심(튜브 중심선)까지의 반지름.</param>
    /// <param name="sectionRadius">튜브 단면 반지름.</param>
    /// <param name="segments">링 둘레 방향 분할 수(최소 3).</param>
    /// <param name="sections">단면 둘레 분할 수(최소 3).</param>
    public static PolyMesh Torus(float radius = 0.5f, float sectionRadius = 0.2f, int segments = 20, int sections = 12)
    {
        segments = Math.Max(3, segments); sections = Math.Max(3, sections);
        var m = new PolyMesh();
        // 격자 [i = 링 둘레, j = 단면 둘레]. 각 정점 = 링 중심점 + 반경 방향·Y 방향으로 단면 원
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
        // 이웃 격자점 네 개로 쿼드(양방향 모두 감싸므로 경계 없음). UV = (i/segments, j/sections)
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
        // u, v가 0/1로 갈리는 두 이음매 루프를 심으로 표시
        for (int j = 0; j < sections; j++) SetEdgeSeam(m, grid[0, j], grid[0, (j + 1) % sections], true);
        for (int i = 0; i < segments; i++) SetEdgeSeam(m, grid[i, 0], grid[(i + 1) % segments, 0], true);
        MeshNormals.Recompute(m);
        return m;
    }

    // ---------------------------------------------------------------- 헬퍼

    /// <summary>쿼드 하나를 추가하고 UV를 단위 정사각형(0,0)-(1,0)-(1,1)-(0,1)로 준다(큐브 면용). 반환값은 면 ID(실패 시 -1).</summary>
    private static int AddQuad(PolyMesh m, int a, int b, int c, int d)
    {
        Span<int> q = stackalloc int[4] { a, b, c, d };
        int f = m.AddFace(q);
        SetFaceUvs(m, f, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
        return f;
    }

    /// <summary>
    /// 면 <paramref name="f"/>의 코너 UV를 루프 순서(Faces[f].HalfEdge부터 Next 방향)대로 <paramref name="uvs"/>로 채운다.
    /// UV가 모자라면 남은 코너는 0. f &lt; 0(AddFace 실패)이면 아무것도 하지 않는다.
    /// 주의: AddFace는 첫 정점의 하프에지를 Faces[f].HalfEdge로 두므로 uvs 순서 = AddFace에 넘긴 정점 순서다.
    /// </summary>
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

    /// <summary>
    /// 원형 캡 면의 평면 UV: XZ 위치를 지름(2r)으로 나눠 중심 (0.5, 0.5)의 원에 매핑한다(-Z가 v 위쪽).
    /// </summary>
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

    /// <summary>살아 있는 모든 엣지의 하드 플래그를 설정한다(노멀 재계산은 호출자가).</summary>
    public static void SetAllEdgesHard(PolyMesh m, bool hard)
    {
        for (int e = 0; e < m.Edges.Count; e++)
        {
            var ed = m.Edges[e];
            if (!ed.Alive) continue;
            ed.Hard = hard; m.Edges[e] = ed;
        }
    }

    /// <summary>두 정점을 잇는 엣지의 하드 플래그를 설정한다. 엣지가 없으면 무시.</summary>
    public static void SetEdgeHard(PolyMesh m, int va, int vb, bool hard)
    {
        int e = m.FindEdge(va, vb);
        if (e < 0) return;
        var ed = m.Edges[e]; ed.Hard = hard; m.Edges[e] = ed;
    }

    /// <summary>두 정점을 잇는 엣지의 UV 심 플래그를 설정한다. 엣지가 없으면 무시.</summary>
    public static void SetEdgeSeam(PolyMesh m, int va, int vb, bool seam)
    {
        int e = m.FindEdge(va, vb);
        if (e < 0) return;
        var ed = m.Edges[e]; ed.Seam = seam; m.Edges[e] = ed;
    }

    /// <summary>살아 있는 모든 엣지의 UV 심 플래그를 설정한다(큐브처럼 면마다 독립 UV 섬을 만들 때).</summary>
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
