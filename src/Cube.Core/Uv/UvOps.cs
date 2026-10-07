using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>UV 점: 같은 정점을 공유하고 심으로 끊기지 않으며 같은 UV를 가진 코너(하프에지)들의 묶음.</summary>
public sealed class UvPoint
{
    public int Vertex;
    public readonly List<int> HalfEdges = new();
    public Vector2 Uv;
    public int Shell = -1;
    /// <summary>코너 중 하나라도 PinUv면 고정.</summary>
    public bool Pinned;
}

/// <summary>메시의 UV 점/셸 구조. 위상이나 UV가 바뀌면 다시 만든다.</summary>
public sealed class UvTopology
{
    public readonly List<UvPoint> Points = new();
    /// <summary>halfEdge → UV 점 인덱스.</summary>
    public int[] HeToPoint = Array.Empty<int>();
    public int ShellCount;

    public static UvTopology Build(PolyMesh m, float eps = 1e-5f)
    {
        var t = new UvTopology { HeToPoint = new int[m.HalfEdgeCount] };
        Array.Fill(t.HeToPoint, -1);
        float eps2 = eps * eps;
        // 정점별로 코너를 모으고, 심이 아닌 엣지를 건너 같은 UV인 코너끼리 묶는다(union-find)
        var parent = new int[m.HalfEdgeCount];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive || he.Twin < 0) continue;
            var ed = m.Edges[he.Edge];
            if (ed.Seam) continue;
            // he: a→b, twin: b→a. 정점 a의 코너는 he와 twin.Next, 정점 b의 코너는 he.Next와 twin
            var tw = m.Hes[he.Twin];
            int cornerA1 = h, cornerA2 = tw.Next;
            int cornerB1 = he.Next, cornerB2 = he.Twin;
            if (Vector2.DistanceSquared(m.Hes[cornerA1].Uv0, m.Hes[cornerA2].Uv0) <= eps2) Union(cornerA1, cornerA2);
            if (Vector2.DistanceSquared(m.Hes[cornerB1].Uv0, m.Hes[cornerB2].Uv0) <= eps2) Union(cornerB1, cornerB2);
        }
        var map = new Dictionary<int, int>();
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            if (!m.Hes[h].Alive) continue;
            int r = Find(h);
            if (!map.TryGetValue(r, out int idx))
            {
                idx = t.Points.Count;
                map[r] = idx;
                t.Points.Add(new UvPoint { Vertex = m.Hes[h].Vertex, Uv = m.Hes[h].Uv0 });
            }
            t.Points[idx].HalfEdges.Add(h);
            if (m.Hes[h].PinUv) t.Points[idx].Pinned = true;
            t.HeToPoint[h] = idx;
        }
        // 셸: UV 점이 연결된(같은 면에 속한) 성분
        var pp = new int[t.Points.Count];
        for (int i = 0; i < pp.Length; i++) pp[i] = i;
        int FindP(int x) { while (pp[x] != x) { pp[x] = pp[pp[x]]; x = pp[x]; } return x; }
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive) continue;
            int a = FindP(t.HeToPoint[h]), b = FindP(t.HeToPoint[he.Next]);
            if (a != b) pp[a] = b;
        }
        var shellMap = new Dictionary<int, int>();
        for (int i = 0; i < t.Points.Count; i++)
        {
            int r = FindP(i);
            if (!shellMap.TryGetValue(r, out int s)) { s = shellMap.Count; shellMap[r] = s; }
            t.Points[i].Shell = s;
        }
        t.ShellCount = shellMap.Count;
        return t;
    }

    public IEnumerable<int> PointsInShell(int shell)
    {
        for (int i = 0; i < Points.Count; i++) if (Points[i].Shell == shell) yield return i;
    }

    /// <summary>UV 점 집합에 속한 모든 하프에지.</summary>
    public IEnumerable<int> HalfEdgesOf(IEnumerable<int> pointIds)
    {
        foreach (int p in pointIds) foreach (int h in Points[p].HalfEdges) yield return h;
    }
}

/// <summary>UV 편집 연산. 모두 코너 UV(HalfEdge.Uv0)와 엣지 심 플래그만 바꾼다(위상 불변).</summary>
public static partial class UvOps
{
    internal static void SetUv(PolyMesh m, int he, Vector2 uv) { var h = m.Hes[he]; h.Uv0 = uv; m.Hes[he] = h; }

    internal static IEnumerable<int> FaceHalfEdges(PolyMesh m, int f)
    {
        int start = m.Faces[f].HalfEdge, he = start;
        do { yield return he; he = m.Hes[he].Next; } while (he != start);
    }

    public static (Vector2 min, Vector2 max) Bounds(IEnumerable<Vector2> pts)
    {
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var p in pts) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        return (min, max);
    }

    /// <summary>선택 면의 평면 투영. 법선 방향으로 투영 평면을 잡고 결과를 0..1 범위로 정규화한다.</summary>
    public static void PlanarProject(PolyMesh m, IEnumerable<int> faces, Vector3 normal)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return;
        var (u, v) = ProjectionBasis(normal);
        var raw = new Dictionary<int, Vector2>();
        foreach (int f in list)
            foreach (int he in FaceHalfEdges(m, f))
            {
                var p = m.Verts[m.Hes[he].Vertex].Position;
                raw[he] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
            }
        Normalize(m, raw);
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>
    /// 투영 평면의 u/v 축: 그 방향에서 바라본 화면의 오른쪽/위와 일치시킨다
    /// (+Z: u=X, v=Y / +X: u=-Z, v=Y / +Y(위에서): u=X, v=-Z / -Y: u=X, v=+Z).
    /// </summary>
    public static (Vector3 u, Vector3 v) ProjectionBasis(Vector3 normal)
    {
        var n = Vector3.Normalize(normal);
        if (MathF.Abs(n.Y) > 0.99f) return (Vector3.UnitX, n.Y > 0 ? -Vector3.UnitZ : Vector3.UnitZ);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n));
        var up = Vector3.Cross(n, right);
        return (right, up);
    }

    /// <summary>선택 면의 평균 법선으로 평면 투영(Maya "Best Plane").</summary>
    public static void PlanarProjectBestFit(PolyMesh m, IEnumerable<int> faces)
    {
        var list = faces.ToList();
        var n = Vector3.Zero;
        foreach (int f in list) if (f >= 0 && f < m.FaceCount && m.Faces[f].Alive) n += MeshNormals.FaceNormalUnnormalized(m, f);
        if (n.LengthSquared() < 1e-12f) n = Vector3.UnitY;
        PlanarProject(m, list, n);
    }

    /// <summary>Y축 원통 투영. u = 각도/2π, v = 높이 정규화. 각도 경계(심)에서 u가 0과 1로 갈리는 면은 짧은 쪽으로 맞춘다.</summary>
    public static void CylindricalProject(PolyMesh m, IEnumerable<int> faces) => CylindricalProject(m, faces, Vector3.UnitY, null);

    /// <summary>임의 축 원통 투영. originPoint가 있으면 그 점의 각도가 u=0(랩 경계)이 된다 — 자동 심 경로에 맞출 때 쓴다.</summary>
    public static void CylindricalProject(PolyMesh m, IEnumerable<int> faces, Vector3 axis, Vector3? originPoint)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return;
        axis = axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitY;
        var (bu, bv) = ProjectionBasis(axis); // axis에 수직인 두 축
        var center = Vector3.Zero; int cnt = 0;
        foreach (int f in list) foreach (int he in FaceHalfEdges(m, f)) { center += m.Verts[m.Hes[he].Vertex].Position; cnt++; }
        center /= Math.Max(cnt, 1);
        float hmin = float.MaxValue, hmax = float.MinValue;
        foreach (int f in list) foreach (int he in FaceHalfEdges(m, f)) { float hh = Vector3.Dot(m.Verts[m.Hes[he].Vertex].Position - center, axis); hmin = MathF.Min(hmin, hh); hmax = MathF.Max(hmax, hh); }
        float h = MathF.Max(hmax - hmin, 1e-6f);
        float Angle(Vector3 p) => MathF.Atan2(Vector3.Dot(p, bv), Vector3.Dot(p, bu));
        float a0 = originPoint is { } op ? Angle(op - center) : 0f;
        foreach (int f in list)
        {
            var corners = FaceHalfEdges(m, f).ToList();
            var us = new float[corners.Count];
            for (int i = 0; i < corners.Count; i++)
            {
                var p = m.Verts[m.Hes[corners[i]].Vertex].Position - center;
                us[i] = ((Angle(p) - a0) / MathF.Tau + 2f) % 1f;
            }
            // 면 내부에서 u 불연속(0.5 이상 차이)이면 작은 쪽을 +1
            float uref = us[0];
            for (int i = 0; i < corners.Count; i++)
            {
                if (us[i] - uref > 0.5f) us[i] -= 1f; else if (uref - us[i] > 0.5f) us[i] += 1f;
                float v = (Vector3.Dot(m.Verts[m.Hes[corners[i]].Vertex].Position - center, axis) - hmin) / h;
                SetUv(m, corners[i], new Vector2(us[i], v));
            }
        }
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>구면 투영(경도/위도).</summary>
    public static void SphericalProject(PolyMesh m, IEnumerable<int> faces)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return;
        var center = Vector3.Zero; int cnt = 0;
        foreach (int f in list) foreach (int he in FaceHalfEdges(m, f)) { center += m.Verts[m.Hes[he].Vertex].Position; cnt++; }
        center /= Math.Max(cnt, 1);
        foreach (int f in list)
        {
            var corners = FaceHalfEdges(m, f).ToList();
            var us = new float[corners.Count];
            for (int i = 0; i < corners.Count; i++)
            {
                var p = m.Verts[m.Hes[corners[i]].Vertex].Position - center;
                us[i] = (MathF.Atan2(-p.Z, p.X) / MathF.Tau + 1f) % 1f;
            }
            float uref = us[0];
            for (int i = 0; i < corners.Count; i++)
            {
                if (us[i] - uref > 0.5f) us[i] -= 1f; else if (uref - us[i] > 0.5f) us[i] += 1f;
                var p = m.Verts[m.Hes[corners[i]].Vertex].Position - center;
                float len = p.Length();
                float v = len > 1e-9f ? MathF.Acos(Math.Clamp(-p.Y / len, -1f, 1f)) / MathF.PI : 0.5f;
                SetUv(m, corners[i], new Vector2(us[i], v));
            }
        }
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>원시 2D 좌표를 0..1 사각형 안에 맞춘다(종횡비 유지).</summary>
    private static void Normalize(PolyMesh m, Dictionary<int, Vector2> raw)
    {
        var (min, max) = Bounds(raw.Values);
        float size = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), 1e-9f);
        foreach (var (he, p) in raw) SetUv(m, he, (p - min) / size);
    }

    /// <summary>투영된 면 집합의 경계 엣지를 심으로 표시(기존 UV와 분리). 내부 엣지의 심은 해제.</summary>
    internal static void MarkSeamsAroundSelection(PolyMesh m, List<int> faces)
    {
        var set = new HashSet<int>(faces);
        foreach (int f in faces)
            foreach (int he in FaceHalfEdges(m, f))
            {
                var h = m.Hes[he];
                var ed = m.Edges[h.Edge];
                bool boundary = h.Twin < 0 || !set.Contains(m.Hes[h.Twin].Face);
                if (h.Twin < 0) { ed.Seam = false; m.Edges[h.Edge] = ed; continue; }
                // 내부 엣지라도 투영 결과 양쪽 코너 UV가 다르면(원통 랩 경계 등) 심으로 남긴다
                var tw = m.Hes[h.Twin];
                bool mismatch = (h.Uv0 - m.Hes[tw.Next].Uv0).LengthSquared() > 1e-8f || (m.Hes[h.Next].Uv0 - tw.Uv0).LengthSquared() > 1e-8f;
                ed.Seam = boundary || mismatch;
                m.Edges[h.Edge] = ed;
            }
    }

    /// <summary>
    /// Auto Wrap: 자동 심 선택 → 심 적용 → 섬마다 최적 평면 투영으로 초기화 → 이완(Unfold) → Layout.
    /// 반환값은 심 엣지 수.
    /// </summary>
    public static int AutoWrap(PolyMesh m, float angleDeg = 55f, int unfoldIterations = 120)
    {
        var seams = AutoSeams.Select(m, angleDeg);
        for (int e = 0; e < m.EdgeCount; e++) { if (!m.Edges[e].Alive) continue; var ed = m.Edges[e]; ed.Seam = seams.Contains(e); m.Edges[e] = ed; }
        float cosLimit = MathF.Cos(angleDeg * MathF.PI / 180f);
        foreach (var region in AutoSeams.Regions(m, seams))
        {
            // 법선이 서로 상쇄되는(둘러싸는) 영역은 원통 투영, 아니면 최적 평면 투영
            var sum = Vector3.Zero; float total = 0;
            foreach (int f in region) { var n = MeshNormals.FaceNormalUnnormalized(m, f); sum += n; total += n.Length(); }
            bool wraps = total > 1e-9f && sum.Length() / total < 0.5f && region.Count >= 6;
            if (!wraps) { PlanarProjectBestFit(m, region); continue; }
            // 축 = 면 법선과 가장 직교하는 월드 축
            Vector3 best = Vector3.UnitY; float bestScore = float.MaxValue;
            foreach (var ax in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                float score = 0; foreach (int f in region) score += MathF.Abs(Vector3.Dot(MeshNormals.FaceNormalUnnormalized(m, f), ax));
                if (score < bestScore) { bestScore = score; best = ax; }
            }
            // 랩 경계를 잘린(날카롭지 않은) 심 경로에 맞춘다
            Vector3? origin = null;
            var set = new HashSet<int>(region); var hes = new List<int>();
            foreach (int f in region)
            {
                m.GetFaceHalfEdges(f, hes);
                foreach (int he in hes)
                {
                    var hh = m.Hes[he];
                    if (hh.Twin < 0 || !seams.Contains(hh.Edge) || !set.Contains(m.Hes[hh.Twin].Face)) continue;
                    var n0 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f)); var n1 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, m.Hes[hh.Twin].Face));
                    if (Vector3.Dot(n0, n1) >= cosLimit && !m.Edges[hh.Edge].Hard) { origin = (m.Verts[hh.Vertex].Position + m.Verts[m.Hes[hh.Next].Vertex].Position) * 0.5f; break; }
                }
                if (origin != null) break;
            }
            CylindricalProject(m, region, best, origin);
        }
        // 투영이 심을 다시 쓰므로 자동 심을 복원
        for (int e = 0; e < m.EdgeCount; e++) { if (!m.Edges[e].Alive) continue; var ed = m.Edges[e]; ed.Seam = seams.Contains(e); m.Edges[e] = ed; }
        var topo = UvTopology.Build(m);
        UnfoldRelax(m, topo, Enumerable.Range(0, topo.ShellCount), unfoldIterations);
        topo = UvTopology.Build(m);
        Layout(m, topo, Enumerable.Range(0, topo.ShellCount));
        return seams.Count;
    }

    /// <summary>Cut UV Edges: 선택 엣지를 심으로 만든다.</summary>
    public static void CutEdges(PolyMesh m, IEnumerable<int> edges)
    {
        foreach (int e in edges)
        {
            if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive || m.Edges[e].He1 < 0) continue;
            var ed = m.Edges[e]; ed.Seam = true; m.Edges[e] = ed;
        }
    }

    /// <summary>Sew UV Edges: 심을 해제하고 양쪽 코너 UV를 평균으로 맞춘다.</summary>
    public static void SewEdges(PolyMesh m, IEnumerable<int> edges)
    {
        foreach (int e in edges)
        {
            if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive || m.Edges[e].He1 < 0) continue;
            var ed = m.Edges[e]; ed.Seam = false; m.Edges[e] = ed;
            var he = m.Hes[ed.He0]; var tw = m.Hes[ed.He1];
            // 정점 a: he, tw.Next / 정점 b: he.Next, tw
            int a1 = ed.He0, a2 = tw.Next, b1 = he.Next, b2 = ed.He1;
            var ua = (m.Hes[a1].Uv0 + m.Hes[a2].Uv0) * 0.5f; var ub = (m.Hes[b1].Uv0 + m.Hes[b2].Uv0) * 0.5f;
            SetUv(m, a1, ua); SetUv(m, a2, ua); SetUv(m, b1, ub); SetUv(m, b2, ub);
        }
    }

    /// <summary>UV 점들을 2D 변환(행벡터 3x2 아핀: uv' = uv·A + t).</summary>
    public static void TransformPoints(PolyMesh m, UvTopology topo, IEnumerable<int> points, Matrix3x2 xf)
    {
        foreach (int p in points)
        {
            var pt = topo.Points[p];
            var uv = Vector2.Transform(pt.Uv, xf);
            pt.Uv = uv;
            foreach (int he in pt.HalfEdges) SetUv(m, he, uv);
        }
    }

    public static void SetPointUv(PolyMesh m, UvTopology topo, int point, Vector2 uv)
    {
        var pt = topo.Points[point];
        pt.Uv = uv;
        foreach (int he in pt.HalfEdges) SetUv(m, he, uv);
    }

    /// <summary>Flip U 또는 V (선택 UV 점의 경계 상자 기준).</summary>
    public static void Flip(PolyMesh m, UvTopology topo, IEnumerable<int> points, bool flipU)
    {
        var list = points.ToList();
        if (list.Count == 0) return;
        var (min, max) = Bounds(list.Select(p => topo.Points[p].Uv));
        foreach (int p in list)
        {
            var uv = topo.Points[p].Uv;
            uv = flipU ? new Vector2(min.X + max.X - uv.X, uv.Y) : new Vector2(uv.X, min.Y + max.Y - uv.Y);
            SetPointUv(m, topo, p, uv);
        }
    }

    /// <summary>
    /// Unfold(이완): 셸마다 각 삼각형이 3D 모양(프로크루스테스 맞춤)을 따르도록 반복해서 UV 점을 당긴다.
    /// 초기값은 현재 UV. 저폴리용 단순 ARAP 근사.
    /// </summary>
    public static void UnfoldRelax(PolyMesh m, UvTopology topo, IEnumerable<int> shells, int iterations = 60) => UnfoldRelax(m, topo, shells, iterations, null);

    /// <summary>pinned(UV 점 ID)와 Pin된 점은 움직이지 않는다.</summary>
    public static void UnfoldRelax(PolyMesh m, UvTopology topo, IEnumerable<int> shells, int iterations, HashSet<int>? pinned)
    {
        var shellSet = new HashSet<int>(shells);
        bool IsPinned(int p) => topo.Points[p].Pinned || (pinned != null && pinned.Contains(p));
        var render = MeshTessellator.Build(m);
        // 셸별 삼각형 목록: (uv점 3개, 3D 모양을 2D로 펼친 로컬 좌표 3개)
        var tris = new List<(int[] pts, Vector2[] local)>();
        for (int t = 0; t < render.TriangleCount; t++)
        {
            int h0 = render.CornerToHalfEdge[render.Indices[t * 3]], h1 = render.CornerToHalfEdge[render.Indices[t * 3 + 1]], h2 = render.CornerToHalfEdge[render.Indices[t * 3 + 2]];
            int p0 = topo.HeToPoint[h0], p1 = topo.HeToPoint[h1], p2 = topo.HeToPoint[h2];
            if (!shellSet.Contains(topo.Points[p0].Shell)) continue;
            var a = render.Positions[render.Indices[t * 3]]; var b = render.Positions[render.Indices[t * 3 + 1]]; var c = render.Positions[render.Indices[t * 3 + 2]];
            // 삼각형 로컬 2D: a=(0,0), b=(|ab|,0), c=투영
            var ab = b - a; var ac = c - a;
            float lab = ab.Length(); if (lab < 1e-9f) continue;
            var ex = ab / lab;
            float cx = Vector3.Dot(ac, ex);
            float cy = (ac - ex * cx).Length();
            tris.Add((new[] { p0, p1, p2 }, new[] { Vector2.Zero, new Vector2(lab, 0), new Vector2(cx, cy) }));
        }
        if (tris.Count == 0) return;
        var pos = topo.Points.Select(p => p.Uv).ToArray();
        // 스케일 정규화: 현재 UV 면적과 3D 면적 비율
        float uvArea = 0, area3 = 0;
        foreach (var (pts, local) in tris)
        {
            uvArea += MathF.Abs(Cross(pos[pts[1]] - pos[pts[0]], pos[pts[2]] - pos[pts[0]]));
            area3 += MathF.Abs(Cross(local[1] - local[0], local[2] - local[0]));
        }
        float scale = uvArea > 1e-12f && area3 > 1e-12f ? MathF.Sqrt(uvArea / area3) : 1f;
        var acc = new Vector2[pos.Length]; var cnt = new int[pos.Length];
        for (int it = 0; it < iterations; it++)
        {
            Array.Clear(acc); Array.Clear(cnt);
            foreach (var (pts, local) in tris)
            {
                // 현재 UV 삼각형에 로컬 삼각형을 강체(회전+이동, 스케일 고정)로 맞춘다
                var cu = (pos[pts[0]] + pos[pts[1]] + pos[pts[2]]) / 3f;
                var cl = (local[0] + local[1] + local[2]) / 3f;
                float sxx = 0, sxy = 0;
                for (int i = 0; i < 3; i++)
                {
                    var q = pos[pts[i]] - cu; var l = (local[i] - cl) * scale;
                    sxx += l.X * q.X + l.Y * q.Y; sxy += l.X * q.Y - l.Y * q.X;
                }
                float ang = MathF.Atan2(sxy, sxx);
                float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
                for (int i = 0; i < 3; i++)
                {
                    var l = (local[i] - cl) * scale;
                    var target = cu + new Vector2(l.X * cs - l.Y * sn, l.X * sn + l.Y * cs);
                    acc[pts[i]] += target; cnt[pts[i]]++;
                }
            }
            for (int i = 0; i < pos.Length; i++) if (cnt[i] > 0 && !IsPinned(i)) pos[i] = acc[i] / cnt[i];
        }
        for (int i = 0; i < pos.Length; i++) if (shellSet.Contains(topo.Points[i].Shell) && !IsPinned(i)) SetPointUv(m, topo, i, pos[i]);
    }

    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Layout: 셸들을 0..1 사각형에 선반(shelf) 방식으로 패킹한다. 종횡비 유지, 간격 spacing.</summary>
    public static void Layout(PolyMesh m, UvTopology topo, IEnumerable<int> shells, float spacing = 0.01f) => Layout(m, topo, shells, spacing, false, Vector2.Zero, 1f);

    /// <summary>Layout 옵션: rotateToFit = 셸을 세워(높이 ≤ 폭) 넣기, tileOrigin/tileSize = 대상 타일(UDIM).</summary>
    public static void Layout(PolyMesh m, UvTopology topo, IEnumerable<int> shells, float spacing, bool rotateToFit, Vector2 tileOrigin, float tileSize)
    {
        var shellList = shells.Distinct().ToList();
        if (shellList.Count == 0) return;
        if (rotateToFit)
            foreach (int s in shellList)
            {
                var pts = topo.PointsInShell(s).ToList();
                if (pts.Count == 0) continue;
                var (mn, mx) = Bounds(pts.Select(p => topo.Points[p].Uv));
                if (mx.Y - mn.Y > mx.X - mn.X) TransformPoints(m, topo, pts, Matrix3x2.CreateRotation(-MathF.PI / 2f, (mn + mx) * 0.5f));
            }
        var boxes = new List<(int shell, Vector2 min, Vector2 size)>();
        foreach (int s in shellList)
        {
            var pts = topo.PointsInShell(s).Select(p => topo.Points[p].Uv).ToList();
            if (pts.Count == 0) continue;
            var (min, max) = Bounds(pts);
            boxes.Add((s, min, Vector2.Max(max - min, new Vector2(1e-6f))));
        }
        // 전체 면적으로 스케일 추정 후 높이 내림차순 선반 패킹; 안 들어가면 스케일을 줄여 재시도
        float total = boxes.Sum(b => (b.size.X + spacing) * (b.size.Y + spacing));
        float scale = total > 0 ? MathF.Sqrt(0.85f / total) : 1f;
        boxes.Sort((a, b) => b.size.Y.CompareTo(a.size.Y));
        var placed = new Dictionary<int, Vector2>();
        for (int attempt = 0; attempt < 12; attempt++)
        {
            placed.Clear();
            float x = spacing, y = spacing, rowH = 0; bool ok = true;
            foreach (var (shell, _, size) in boxes)
            {
                float w = size.X * scale, h = size.Y * scale;
                if (x + w + spacing > 1f) { x = spacing; y += rowH + spacing; rowH = 0; }
                if (y + h + spacing > 1f || x + w + spacing > 1f) { ok = false; break; }
                placed[shell] = new Vector2(x, y);
                x += w + spacing; rowH = MathF.Max(rowH, h);
            }
            if (ok) break;
            scale *= 0.9f;
        }
        foreach (var (shell, min, _) in boxes)
        {
            if (!placed.TryGetValue(shell, out var origin)) continue;
            foreach (int p in topo.PointsInShell(shell))
                SetPointUv(m, topo, p, tileOrigin + (origin + (topo.Points[p].Uv - min) * scale) * tileSize);
        }
    }

    /// <summary>UV 점 집합을 포함하는 셸 ID들.</summary>
    public static IEnumerable<int> ShellsOf(UvTopology topo, IEnumerable<int> points) => points.Select(p => topo.Points[p].Shell).Distinct();
}
