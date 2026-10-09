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
        var dirs = AutomaticDirections(planes).ToList();
        bool unsigned = planes == 3;
        // 면 → 투영 방향 인덱스. 법선과 내적이 가장 큰 방향
        var assign = new Dictionary<int, int>();
        foreach (int f in list)
        {
            var n = MeshNormals.FaceNormalUnnormalized(m, f);
            if (n.LengthSquared() > 1e-18f) n = Vector3.Normalize(n);
            int best = 0; float bestDot = float.MinValue;
            for (int i = 0; i < dirs.Count; i++) { float d = Vector3.Dot(n, dirs[i]); if (unsigned) d = MathF.Abs(d); if (d > bestDot) { bestDot = d; best = i; } }
            // 어느 평면도 이 면을 마주 보지 않으면(5평면의 바닥 등) 투영이 납작해지거나 뒤집히므로,
            // 절댓값이 가장 큰 평면의 반대 방향을 방향 목록에 더해 그 방향으로 투영한다
            if (!unsigned && bestDot < 0.3f)
            {
                int flip = 0; float flipDot = float.MinValue;
                for (int i = 0; i < dirs.Count; i++) { float d = -Vector3.Dot(n, dirs[i]); if (d > flipDot) { flipDot = d; flip = i; } }
                if (flipDot > bestDot)
                {
                    var opp = -dirs[flip];
                    best = dirs.FindIndex(x => Vector3.DistanceSquared(x, opp) < 1e-8f);
                    if (best < 0) { dirs.Add(opp); best = dirs.Count - 1; }
                }
            }
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
        // 경계가 없는 닫힌 영역(큐브 전체 등)은 정사각형에 붙일 테두리가 없으므로 자동 심으로 원반이 되게 먼저 자른다
        var set = new HashSet<int>(list);
        var shells = list.Select(f => topo.Points[topo.HeToPoint[m.Faces[f].HalfEdge]].Shell).Distinct().ToList();
        if (shells.Any(s => ShellBorderLoops(m, topo, s).Count == 0))
        {
            var cut = AutoSeams.Select(m).Where(e => { var (f0, f1) = m.EdgeFaces(e); return f0 >= 0 && f1 >= 0 && set.Contains(f0) && set.Contains(f1); });
            CutEdges(m, cut);
            // 잘린 영역마다 연속인 초기 UV를 다시 만든다
            foreach (var region in AutoSeams.Regions(m, new HashSet<int>(Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive && m.Edges[e].Seam))).Where(r => set.Contains(r[0])))
            {
                PlanarProjectBestFit(m, region);
            }
            CutEdges(m, cut);
            topo = UvTopology.Build(m);
            shells = list.Select(f => topo.Points[topo.HeToPoint[m.Faces[f].HalfEdge]].Shell).Distinct().ToList();
        }
        foreach (int shell in shells)
        {
            // 시작 점: 경계에서 UV가 가장 왼쪽 아래인 점
            var loops = ShellBorderLoops(m, topo, shell);
            int start = loops.Count > 0 ? loops.OrderByDescending(l => l.Count).First().OrderBy(p => topo.Points[p].Uv.X + topo.Points[p].Uv.Y).First() : -1;
            // 경계를 정사각형에 붙이고 내부를 이완
            MapBorder(m, topo, shell, square: true, startPoint: start);
            // 안쪽은 먼저 Tutte(볼록한 정사각형 경계 → 겹침 없음)로 채우고 Optimize로 이완한다. 이완이 접으면 Tutte 결과로 되돌린다.
            bool tutte = TutteDiskInit(m, topo, shell, keepBorder: true);
            var before = new Dictionary<int, Vector2>();
            foreach (int p in topo.PointsInShell(shell)) before[p] = topo.Points[p].Uv;
            Optimize(m, topo, new[] { shell }, iterations);
            if (tutte && FoldedShells(m, topo).Contains(shell))
                foreach (var (p, uv) in before) SetPointUv(m, topo, p, uv);
        }
        if (shells.Count > 1) Layout(m, topo, shells, 0.01f);
    }

    /// <summary>
    /// Create UV Shell (Grid): 선택 면(쿼드)을 하나의 셸로 떼어 내고 쿼드마다 같은 크기의 정사각형 칸이 되도록 격자로 펼친 뒤
    /// 종횡비를 유지해 0..1에 맞춘다(Maya Create UV Shell (Grid)). 연결 영역마다 따로 펼친다.
    /// </summary>
    /// <remarks>
    /// 영역의 첫 쿼드를 (0,0)-(1,0)-(1,1)-(0,1)에 놓고 공유 엣지를 건너(BFS) 이웃 쿼드를 그 엣지의 바깥쪽 한 칸에 놓는다
    /// (면 안쪽이 반시계 왼쪽이므로 이웃은 엣지 방향의 왼쪽 법선 쪽). 격자로 맞지 않는 곳(극점·나선)은 먼저 놓인 칸이 우선하고
    /// 양쪽 코너 UV가 달라지는 엣지는 심이 된다. 쿼드가 아닌 면이 섞인 영역은 <see cref="ContourStretch"/>로 대신한다.
    /// 영역들은 끝에 <see cref="Layout(PolyMesh, UvTopology, IEnumerable{int}, float)"/>으로 0..1에 나란히 놓는다.
    /// </remarks>
    /// <returns>격자로 펼친 영역 수.</returns>
    public static int CreateShellGrid(PolyMesh m, IEnumerable<int> faces)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).Distinct().ToList();
        if (list.Count == 0) return 0;
        var set = new HashSet<int>(list);
        var visited = new HashSet<int>();
        int grids = 0;
        var regions = new List<List<int>>();
        foreach (int seed in list)
        {
            if (visited.Contains(seed)) continue;
            // 선택 안에서 엣지로 이어진 영역
            var region = new List<int>(); var stack = new Stack<int>(); stack.Push(seed); visited.Add(seed);
            while (stack.Count > 0)
            {
                int f = stack.Pop(); region.Add(f);
                foreach (int h in FaceHalfEdges(m, f))
                {
                    int tw = m.Hes[h].Twin; if (tw < 0) continue; int g = m.Hes[tw].Face;
                    if (set.Contains(g) && visited.Add(g)) stack.Push(g);
                }
            }
            regions.Add(region);
        }
        foreach (var region in regions)
        {
            if (region.Any(f => FaceHalfEdges(m, f).Count() != 4)) { ContourStretch(m, region); continue; }
            var regionSet = new HashSet<int>(region);
            var placed = new HashSet<int>();
            var queue = new Queue<int>();
            var first = FaceHalfEdges(m, region[0]).ToList();
            var unit = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            for (int i = 0; i < 4; i++) SetUv(m, first[i], unit[i]);
            placed.Add(region[0]); queue.Enqueue(region[0]);
            while (queue.Count > 0)
            {
                int f = queue.Dequeue();
                foreach (int h in FaceHalfEdges(m, f).ToList())
                {
                    int tw = m.Hes[h].Twin; if (tw < 0) continue;
                    int g = m.Hes[tw].Face;
                    if (!regionSet.Contains(g) || placed.Contains(g)) continue;
                    // f의 h: a→b(UV pa, pb). g의 twin: b→a, 그다음 두 코너는 b→a 방향 왼쪽 법선만큼 바깥 칸
                    var pa = m.Hes[h].Uv0; var pb = m.Hes[m.Hes[h].Next].Uv0;
                    var d = pa - pb; var left = new Vector2(-d.Y, d.X);
                    int c0 = tw, c1 = m.Hes[c0].Next, c2 = m.Hes[c1].Next, c3 = m.Hes[c2].Next;
                    SetUv(m, c0, pb); SetUv(m, c1, pa); SetUv(m, c2, pa + left); SetUv(m, c3, pb + left);
                    placed.Add(g); queue.Enqueue(g);
                }
            }
            grids++;
        }
        // 선택 둘레와 격자가 어긋난 엣지를 심으로(내부 일치 엣지는 심 해제)
        MarkSeamsAroundSelection(m, list);
        var topo = UvTopology.Build(m);
        var shellIds = new HashSet<int>(); foreach (int f in list) shellIds.Add(topo.Points[topo.HeToPoint[m.Faces[f].HalfEdge]].Shell);
        Layout(m, topo, shellIds, 0.01f);
        return grids;
    }
}
