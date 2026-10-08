using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>UV Create 메뉴 추가 투영: Automatic(N 평면), Camera-Based, Best Plane(정점 기준), Contour Stretch, Create Shell(Grid).</summary>
/// <remarks>
/// UvOps partial의 일부. 모든 투영은 메시 로컬 좌표 기준이며 코너 UV와 심만 바꾼다(위상 불변).
/// </remarks>
public static partial class UvOps
{
    /// <summary>Automatic 투영 방향 집합(3/4/5/6/8/12 평면).</summary>
    /// <remarks>
    /// 3 = 세 축(부호 무시), 4 = 정사면체 꼭짓점 방향, 5 = ±X, ±Z, +Y(바닥 없음), 8 = 정팔면체 대각(정육면체 꼭짓점),
    /// 12 = 황금비 g로 만든 정이십면체 꼭짓점 방향, 그 외(6) = ±X, ±Y, ±Z.
    /// </remarks>
    public static Vector3[] AutomaticDirections(int planes)
    {
        switch (planes)
        {
            case 3: return new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
            case 4: return new[] { Vector3.Normalize(new Vector3(1, 1, 1)), Vector3.Normalize(new Vector3(-1, 1, -1)), Vector3.Normalize(new Vector3(1, -1, -1)), Vector3.Normalize(new Vector3(-1, -1, 1)) };
            case 5: return new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitY };
            case 8:
                {
                    var l = new List<Vector3>();
                    foreach (int sx in new[] { -1, 1 }) foreach (int sy in new[] { -1, 1 }) foreach (int sz in new[] { -1, 1 }) l.Add(Vector3.Normalize(new Vector3(sx, sy, sz)));
                    return l.ToArray();
                }
            case 12:
                {
                    float g = (1 + MathF.Sqrt(5)) / 2;
                    var l = new List<Vector3>();
                    foreach (int a in new[] { -1, 1 }) foreach (int b in new[] { -1, 1 }) { l.Add(Vector3.Normalize(new Vector3(0, a, b * g))); l.Add(Vector3.Normalize(new Vector3(a, b * g, 0))); l.Add(Vector3.Normalize(new Vector3(b * g, 0, a))); }
                    return l.ToArray();
                }
            default: return new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        }
    }

    /// <summary>
    /// Automatic Mapping: 면마다 법선과 가장 잘 맞는 투영 방향을 고르고(3평면은 부호 무시), 같은 방향의 연결 면끼리 셸을 만들어 평면 투영한 뒤 Layout한다.
    /// fewerPieces면 인접 면 각도가 작을 때 이웃 방향을 따라가 셸 수를 줄인다. 반환값은 셸 수.
    /// </summary>
    // 유효 면 필터
    public static int AutomaticProject(PolyMesh m, IEnumerable<int> faces, int planes = 6, bool fewerPieces = true, float spacing = 0.01f)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return 0;
        // 3평면은 축 부호를 무시(앞뒤 면이 같은 투영 방향을 공유)
        var dirs = AutomaticDirections(planes);
        bool unsigned = planes == 3;
        // 면 → 투영 방향 인덱스. 법선과 내적이 가장 큰 방향
        var assign = new Dictionary<int, int>();
        foreach (int f in list)
        {
            var n = MeshNormals.FaceNormalUnnormalized(m, f);
            if (n.LengthSquared() > 1e-18f) n = Vector3.Normalize(n);
            int best = 0; float bestDot = float.MinValue;
            for (int i = 0; i < dirs.Length; i++) { float d = Vector3.Dot(n, dirs[i]); if (unsigned) d = MathF.Abs(d); if (d > bestDot) { bestDot = d; best = i; } }
            assign[f] = best;
        }
        // 조건: 같은 방향을 쓰는 이웃이 2개 이상이고 그 방향이 이 면 법선과 0.3 이상 맞을 때만 바꾼다
        if (fewerPieces)
        {
            // 이웃 면과 법선 각도가 45° 이내면 더 큰 이웃 무리의 방향을 따른다(간단한 2회 전파)
            var set = new HashSet<int>(list);
            for (int pass = 0; pass < 2; pass++)
                foreach (int f in list)
                {
                    var votes = new Dictionary<int, int>();
                    var n = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f) + new Vector3(1e-12f));
                    foreach (int h in FaceHalfEdges(m, f))
                    {
                        int tw = m.Hes[h].Twin; if (tw < 0) continue; int g = m.Hes[tw].Face; if (!set.Contains(g)) continue;
                        var ng = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, g) + new Vector3(1e-12f));
                        if (Vector3.Dot(n, ng) < 0.7071f) continue;
                        votes[assign[g]] = votes.GetValueOrDefault(assign[g]) + 1;
                    }
                    if (votes.Count == 0) continue;
                    var top = votes.OrderByDescending(kv => kv.Value).First();
                    float dOwn = Vector3.Dot(n, dirs[assign[f]]), dTop = Vector3.Dot(n, dirs[top.Key]);
                    if (unsigned) { dOwn = MathF.Abs(dOwn); dTop = MathF.Abs(dTop); }
                    if (top.Value >= 2 && dTop > 0.3f) assign[f] = top.Key;
                }
        }
        // 방향별 연결 성분 → 평면 투영
        // 같은 방향 인덱스를 가진 인접 면끼리 DFS로 영역을 만들고 영역마다 정규화 없는 평면 투영
        var visited = new HashSet<int>();
        int shells = 0;
        foreach (int seed in list)
        {
            if (visited.Contains(seed)) continue;
            var region = new List<int>(); var stack = new Stack<int>(); stack.Push(seed); visited.Add(seed);
            int dir = assign[seed];
            while (stack.Count > 0)
            {
                int f = stack.Pop(); region.Add(f);
                foreach (int h in FaceHalfEdges(m, f))
                {
                    int tw = m.Hes[h].Twin; if (tw < 0) continue; int g = m.Hes[tw].Face;
                    if (!assign.ContainsKey(g) || visited.Contains(g) || assign[g] != dir) continue;
                    visited.Add(g); stack.Push(g);
                }
            }
            // 3평면이면 영역 평균 법선 쪽을 향하도록 투영 방향 부호를 맞춘다(뒤집힌 UV 방지)
            var normal = dirs[dir];
            if (unsigned) { var avg = Vector3.Zero; foreach (int f in region) avg += MeshNormals.FaceNormalUnnormalized(m, f); if (Vector3.Dot(avg, normal) < 0) normal = -normal; }
            PlanarProjectRaw(m, region, normal);
            shells++;
        }
        // 셸 경계를 심으로, 그리고 Layout
        MarkSeamsAroundSelection(m, list);
        foreach (int f in list)
            foreach (int h in FaceHalfEdges(m, f))
            {
                int tw = m.Hes[h].Twin; if (tw < 0) continue; int g = m.Hes[tw].Face;
                if (assign.ContainsKey(g) && assign[g] != assign[f]) { var ed = m.Edges[m.Hes[h].Edge]; ed.Seam = true; m.Edges[m.Hes[h].Edge] = ed; }
            }
        // 새 셸 구조에서 선택 면이 속한 셸만 0..1에 패킹
        var topo = UvTopology.Build(m);
        var shellIds = new HashSet<int>(); foreach (int f in list) shellIds.Add(topo.Points[topo.HeToPoint[m.Faces[f].HalfEdge]].Shell);
        Layout(m, topo, shellIds, spacing);
        return shells;
    }

    /// <summary>평면 투영(정규화 없이 월드 단위 그대로). Automatic의 셸 간 스케일을 일정하게 하려고 쓴다.</summary>
    /// <remarks>u·v = 위치와 투영 기저 내적. 이후 Layout이 크기를 맞춘다.</remarks>
    private static void PlanarProjectRaw(PolyMesh m, List<int> faces, Vector3 normal)
    {
        var (u, v) = ProjectionBasis(normal);
        foreach (int f in faces) foreach (int he in FaceHalfEdges(m, f)) { var p = m.Verts[m.Hes[he].Vertex].Position; SetUv(m, he, new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v))); }
    }

    /// <summary>Camera-Based: 카메라 오른쪽/위 축으로 평면 투영(0..1 정규화).</summary>
    /// <remarks>
    /// 화면에서 보이는 그대로 UV를 만든다. right/up은 App이 활성 뷰포트 카메라에서 메시 로컬 공간으로 바꿔 넘긴다.
    /// 종횡비를 유지하며 최소점을 원점으로, 긴 쪽을 1로 맞춘다.
    /// </remarks>
    public static void CameraProject(PolyMesh m, IEnumerable<int> faces, Vector3 right, Vector3 up)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return;
        var raw = new Dictionary<int, Vector2>();
        foreach (int f in list) foreach (int he in FaceHalfEdges(m, f)) { var p = m.Verts[m.Hes[he].Vertex].Position; raw[he] = new Vector2(Vector3.Dot(p, right), Vector3.Dot(p, up)); }
        var (min, max) = Bounds(raw.Values);
        float size = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), 1e-9f);
        foreach (var (he, p) in raw) SetUv(m, he, (p - min) / size);
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>Best Plane(정점 기준): 주어진 정점들의 최적 평면(PCA 법선)으로 면들을 평면 투영.</summary>
    /// <remarks>
    /// 정점이 3개 미만이거나 법선이 0이면 선택 면 평균 법선(<see cref="PlanarProjectBestFit"/>)으로 대체한다.
    /// 법선은 정점을 주어진 순서대로 이은 다각형의 뉴웰 법선(중심 기준 외적 합)으로 구한다.
    /// </remarks>
    public static void BestPlaneProject(PolyMesh m, IEnumerable<int> faces, IEnumerable<int> planeVertices)
    {
        var verts = planeVertices.Where(v => v >= 0 && v < m.VertexCount && m.Verts[v].Alive).ToList();
        Vector3 normal;
        if (verts.Count < 3) { PlanarProjectBestFit(m, faces); return; }
        // 정점 중심
        var c = verts.Aggregate(Vector3.Zero, (s, v) => s + m.Verts[v].Position) / verts.Count;
        // 공분산의 최소 고유벡터 ≈ 뉴웰 법선(평면상 다각형이면 정확)
        normal = Vector3.Zero;
        for (int i = 0; i < verts.Count; i++) { var a = m.Verts[verts[i]].Position - c; var b = m.Verts[verts[(i + 1) % verts.Count]].Position - c; normal += Vector3.Cross(a, b); }
        if (normal.LengthSquared() < 1e-12f) { PlanarProjectBestFit(m, faces); return; }
        PlanarProject(m, faces, normal);
    }

    /// <summary>Contour Stretch(근사): 선택 면을 셸로 만들고 경계를 0..1 정사각형에 호 길이 비례로 붙인 뒤 안쪽을 이완한다.</summary>
    /// <remarks>
    /// 순서: 최적 평면 투영(초기값) → 선택 영역을 독립 셸로 분리 → 가장 큰 경계 루프에서 UV 좌하단 점을 시작점으로
    /// 경계를 정사각형 둘레에 배치(MapBorder) → 경계를 고정한 채 내부 이완(Optimize).
    /// </remarks>
    public static void ContourStretch(PolyMesh m, IEnumerable<int> faces, int iterations = 120)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return;
        // 초기 UV를 만들고 선택 면을 별도 셸로 자른다
        PlanarProjectBestFit(m, list);
        CreateUvShell(m, list);
        var topo = UvTopology.Build(m);
        int shell = topo.Points[topo.HeToPoint[m.Faces[list[0]].HalfEdge]].Shell;
        // 시작 점: 경계에서 UV가 가장 왼쪽 아래인 점
        var loops = ShellBorderLoops(m, topo, shell);
        int start = loops.Count > 0 ? loops.OrderByDescending(l => l.Count).First().OrderBy(p => topo.Points[p].Uv.X + topo.Points[p].Uv.Y).First() : -1;
        // 경계를 정사각형에 붙이고 내부를 이완
        MapBorder(m, topo, shell, square: true, startPoint: start);
        Optimize(m, topo, new[] { shell }, iterations);
    }

    // 참고: 아래 summary는 대응하는 메서드 없이 남아 있는 주석이다(Core에 CreateShellGrid 메서드는 없다).
    /// <summary>Create Shell (Grid): 선택 면을 셸로 만들고 Contour Stretch처럼 0..1 격자에 펼친다.</summary>
}
