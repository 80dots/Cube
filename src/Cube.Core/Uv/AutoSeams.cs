using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>
/// 자동 심 선택. (1) 날카로운 엣지(이면각 ≥ angle, 하드 엣지)를 심으로 잡고, (2) 심으로 나뉜 각 영역이 원반(disk)이 되도록
/// 닫힌 영역은 가장 먼 두 정점을 잇는 두 경로(반구 둘로 나눔), 구멍이 있는 영역은 경계 사이 최단 경로를 추가로 자른다.
/// </summary>
/// <remarks>
/// 영역(심으로 나뉜 면 연결 성분)의 위상은 오일러 특성 χ = V − E + F 와 경계 루프 수로 판단한다.
/// 원반 = χ 1 + 경계 1개, 닫힌 구 = 경계 0개, 튜브 = 경계 2개 이상, 손잡이(토러스 등) = χ &lt; 1.
/// 원반이 아닌 영역에 경로를 잘라 넣고, 새 심으로 영역이 바뀌었으므로 다시 검사한다(최대 maxPasses회).
/// 결과는 엣지 ID 집합만 돌려주고 메시는 바꾸지 않는다(uv.autoSeams는 선택만, AutoWrap은 심으로 적용).
/// </remarks>
public static class AutoSeams
{
    /// <summary>자동 심으로 쓸 엣지 ID 집합을 계산한다(메시 불변).</summary>
    /// <param name="angleDeg">이 각도 이상 꺾인 엣지(두 면 법선 사이 각)를 날카로운 엣지로 본다.</param>
    /// <param name="maxPasses">위상 보정 반복 횟수 상한.</param>
    /// <returns>심 엣지 ID 집합.</returns>
    public static HashSet<int> Select(PolyMesh m, float angleDeg = 55f, int maxPasses = 4)
    {
        var seams = new HashSet<int>();
        // 법선 내적이 cos(angle)보다 작으면 각도가 angle보다 크다
        float cosLimit = MathF.Cos(angleDeg * MathF.PI / 180f);
        // 1) 날카로운/하드 엣지
        for (int e = 0; e < m.EdgeCount; e++)
        {
            // 메시 경계 엣지는 이미 열려 있으므로 심 대상이 아니다
            if (!m.Edges[e].Alive || m.IsBoundaryEdge(e)) continue;
            if (m.Edges[e].Hard) { seams.Add(e); continue; }
            var (f0, f1) = m.EdgeFaces(e);
            var n0 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f0)); var n1 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f1));
            if (Vector3.Dot(n0, n1) < cosLimit) seams.Add(e);
        }
        // 2) 영역별 위상 검사
        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool changed = false;
            foreach (var region in Regions(m, seams))
            {
                // χ = V − E + F, 경계 루프 수
                var (verts, edges, boundaryEdges) = RegionStats(m, region, seams);
                int chi = verts.Count - edges.Count + region.Count;
                int loops = BoundaryLoops(m, boundaryEdges);
                if (chi == 1 && loops == 1) continue;        // 원반
                if (loops == 0)
                {
                    // 닫힌 곡면: 가장 먼 두 정점 A,B 사이 경로 + 그 경로에서 가장 먼 정점 C를 지나는 두 번째 경로 → 두 반쪽
                    if (!FarthestPair(m, verts, edges, out int a, out int b)) continue;
                    var p1 = ShortestPath(m, edges, a, b, null);
                    foreach (int e in p1) seams.Add(e);
                    var onPath = new HashSet<int>(); foreach (int e in p1) { var (x, y) = m.EdgeVertices(e); onPath.Add(x); onPath.Add(y); }
                    int c = FarthestFrom(m, verts, edges, onPath);
                    if (c >= 0)
                    {
                        var avoid = new HashSet<int>(onPath); avoid.Remove(a); avoid.Remove(b);
                        var p2 = ShortestPath(m, edges, a, c, avoid).Concat(ShortestPath(m, edges, c, b, avoid)).ToList();
                        foreach (int e in p2) seams.Add(e);
                    }
                    changed = true;
                }
                // 경계가 2개 이상(튜브)이거나 χ &lt; 1(손잡이가 있음)
                else if (loops >= 2 || chi < 1)
                {
                    // 튜브/구멍: 서로 다른 경계의 정점 사이 최단 경로(경계 자체를 따라가지 않도록 경계 엣지 가중치 ↑)
                    var loopsVerts = BoundaryLoopVertices(m, boundaryEdges);
                    if (loopsVerts.Count >= 2)
                    {
                        var path = ShortestPathBetweenSets(m, edges, loopsVerts[0], loopsVerts[1], boundaryEdges);
                        if (path.Count > 0) { foreach (int e in path) seams.Add(e); changed = true; }
                    }
                    else if (chi < 1)
                    {
                        // 손잡이(토러스 등): 경계 정점에서 가장 먼 정점까지 경로
                        var bv = loopsVerts.Count > 0 ? loopsVerts[0] : new HashSet<int> { verts.First() };
                        int far = FarthestFrom(m, verts, edges, bv);
                        if (far >= 0)
                        {
                            var path = ShortestPathBetweenSets(m, edges, bv, new HashSet<int> { far }, boundaryEdges);
                            if (path.Count > 0) { foreach (int e in path) seams.Add(e); changed = true; }
                        }
                    }
                }
            }
            // 이번 패스에서 아무것도 자르지 않았으면 모든 영역이 원반이거나 더 할 수 있는 것이 없다
            if (!changed) break;
        }
        // 3) 아직 손잡이가 남은 영역(토러스 등, χ < 1)은 경로 자르기로는 원반이 되지 않으므로 트리-코트리 절단 그래프를 더한다
        foreach (var region in Regions(m, seams))
        {
            var (verts, edges, boundaryEdges) = RegionStats(m, region, seams);
            if (verts.Count - edges.Count + region.Count < 1) seams.UnionWith(CutGraph(m, region, edges, boundaryEdges));
        }
        return seams;
    }

    /// <summary>
    /// 트리-코트리 절단 그래프: 영역을 원반으로 만드는 내부 엣지 집합.
    /// 면 쌍대 그래프의 최대 신장 트리(긴 엣지를 트리에 남겨 짧은 엣지를 자름)에 들지 않은 내부 엣지와 영역 경계로 그래프를 만들고,
    /// 차수 1인 정점에 매달린 내부 엣지를 반복해서 떼어 내면(가지치기) 손잡이를 끊는 고리와 경계로 가는 경로만 남는다.
    /// </summary>
    private static List<int> CutGraph(PolyMesh m, List<int> region, HashSet<int> edges, HashSet<int> boundaryEdges)
    {
        var set = new HashSet<int>(region);
        // 영역 안 내부 엣지(양쪽 면이 영역 안, 경계 아님)를 길이 내림차순으로 크루스칼
        var interior = edges.Where(e => !boundaryEdges.Contains(e)).ToList();
        float Len(int e) { var (a, b) = m.EdgeVertices(e); return Vector3.Distance(m.Verts[a].Position, m.Verts[b].Position); }
        interior.Sort((x, y) => Len(y).CompareTo(Len(x)));
        var parent = new Dictionary<int, int>(); foreach (int f in region) parent[f] = f;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var cut = new HashSet<int>();
        foreach (int e in interior)
        {
            var (f0, f1) = m.EdgeFaces(e);
            if (f0 < 0 || f1 < 0 || !set.Contains(f0) || !set.Contains(f1)) continue;
            int a = Find(f0), b = Find(f1);
            if (a != b) parent[a] = b; else cut.Add(e);
        }
        // 가지치기: (절단 후보 ∪ 경계) 그래프에서 차수 1 정점에 닿은 절단 후보를 반복 제거
        var deg = new Dictionary<int, int>();
        void Inc(int v, int d) => deg[v] = deg.GetValueOrDefault(v) + d;
        foreach (int e in cut.Concat(boundaryEdges)) { var (a, b) = m.EdgeVertices(e); Inc(a, 1); Inc(b, 1); }
        bool removed = true;
        while (removed)
        {
            removed = false;
            foreach (int e in cut.ToList())
            {
                var (a, b) = m.EdgeVertices(e);
                if (deg[a] > 1 && deg[b] > 1) continue;
                cut.Remove(e); Inc(a, -1); Inc(b, -1); removed = true;
            }
        }
        return cut.ToList();
    }

    /// <summary>심/경계로 나뉜 면 연결 영역.</summary>
    /// <remarks>
    /// 아직 방문하지 않은 면마다 DFS로 심도 경계도 아닌 엣지를 건너 이웃 면을 모은다. 결과는 면 ID 목록의 목록.
    /// AutoWrap이 영역별 투영에 쓴다.
    /// </remarks>
    public static List<List<int>> Regions(PolyMesh m, HashSet<int> seams)
    {
        var result = new List<List<int>>();
        var seen = new HashSet<int>();
        var hes = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive || seen.Contains(f)) continue;
            var region = new List<int>(); var stack = new Stack<int>(); stack.Push(f); seen.Add(f);
            while (stack.Count > 0)
            {
                int cur = stack.Pop(); region.Add(cur);
                m.GetFaceHalfEdges(cur, hes);
                foreach (int he in hes)
                {
                    var h = m.Hes[he];
                    if (h.Twin < 0 || seams.Contains(h.Edge)) continue;
                    int other = m.Hes[h.Twin].Face;
                    if (seen.Add(other)) stack.Push(other);
                }
            }
            result.Add(region);
        }
        return result;
    }

    /// <summary>
    /// 영역의 정점·엣지 집합과 영역 경계 엣지(메시 경계, 심, 또는 상대 면이 영역 밖인 엣지)를 모은다.
    /// 오일러 특성과 경계 루프 계산에 쓴다.
    /// </summary>
    private static (HashSet<int> verts, HashSet<int> edges, HashSet<int> boundary) RegionStats(PolyMesh m, List<int> region, HashSet<int> seams)
    {
        var set = new HashSet<int>(region);
        var verts = new HashSet<int>(); var edges = new HashSet<int>(); var boundary = new HashSet<int>();
        var hes = new List<int>();
        foreach (int f in region)
        {
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes)
            {
                var h = m.Hes[he];
                verts.Add(h.Vertex); edges.Add(h.Edge);
                if (h.Twin < 0 || seams.Contains(h.Edge) || !set.Contains(m.Hes[h.Twin].Face)) boundary.Add(h.Edge);
            }
        }
        return (verts, edges, boundary);
    }

    /// <summary>경계 엣지가 이루는 연결 성분(루프) 수.</summary>
    private static int BoundaryLoops(PolyMesh m, HashSet<int> boundaryEdges) => BoundaryLoopVertices(m, boundaryEdges).Count;

    /// <summary>
    /// 경계 엣지들을 정점 인접 그래프로 만들고 연결 성분(루프)마다 정점 집합을 돌려준다.
    /// 엄밀한 루프 추적이 아니라 연결 성분이므로 8자 모양으로 맞닿은 경계는 하나로 센다.
    /// </summary>
    private static List<HashSet<int>> BoundaryLoopVertices(PolyMesh m, HashSet<int> boundaryEdges)
    {
        // 경계 엣지의 정점 인접 리스트
        var adj = new Dictionary<int, List<int>>();
        foreach (int e in boundaryEdges)
        {
            var (a, b) = m.EdgeVertices(e);
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<int>(); la.Add(b);
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<int>(); lb.Add(a);
        }
        // DFS로 연결 성분 수집
        var loops = new List<HashSet<int>>(); var seen = new HashSet<int>();
        foreach (int v in adj.Keys)
        {
            if (!seen.Add(v)) continue;
            var comp = new HashSet<int> { v }; var stack = new Stack<int>(); stack.Push(v);
            while (stack.Count > 0) { int cur = stack.Pop(); foreach (int n in adj[cur]) if (seen.Add(n)) { comp.Add(n); stack.Push(n); } }
            loops.Add(comp);
        }
        return loops;
    }

    /// <summary>
    /// 지정 엣지 집합 위에서 다중 출발점 Dijkstra를 돌린다. 가중치 = 엣지 3D 길이(penalizeEdges에 있으면 ×20).
    /// </summary>
    /// <param name="sources">거리 0으로 시작하는 정점들.</param>
    /// <param name="avoidVerts">들어가지 않을 정점(두 번째 경로가 첫 경로와 겹치지 않게).</param>
    /// <param name="penalizeEdges">가중치를 크게 할 엣지(경계를 따라 걷는 경로를 피하게).</param>
    /// <param name="prevEdge">null이 아니면 정점 → 거기로 온 엣지를 기록(경로 복원용).</param>
    /// <returns>도달한 정점 → 최단 거리.</returns>
    private static Dictionary<int, float> Dijkstra(PolyMesh m, HashSet<int> edges, IEnumerable<int> sources, HashSet<int>? avoidVerts, HashSet<int>? penalizeEdges, Dictionary<int, int>? prevEdge)
    {
        // 엣지 집합으로 가중 인접 리스트 구성
        var adj = new Dictionary<int, List<(int v, int e, float w)>>();
        foreach (int e in edges)
        {
            var (a, b) = m.EdgeVertices(e);
            float w = Vector3.Distance(m.Verts[a].Position, m.Verts[b].Position);
            if (penalizeEdges != null && penalizeEdges.Contains(e)) w *= 20f;
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = new(); la.Add((b, e, w));
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new(); lb.Add((a, e, w));
        }
        var dist = new Dictionary<int, float>();
        var pq = new PriorityQueue<int, float>();
        foreach (int s in sources) { dist[s] = 0; pq.Enqueue(s, 0); }
        // 우선순위 큐에서 꺼낸 거리가 이미 갱신된 값보다 크면 오래된 항목이므로 건너뛴다
        while (pq.TryDequeue(out int u, out float d))
        {
            if (d > dist[u]) continue;
            if (!adj.TryGetValue(u, out var list)) continue;
            foreach (var (v, e, w) in list)
            {
                if (avoidVerts != null && avoidVerts.Contains(v)) continue;
                float nd = d + w;
                if (!dist.TryGetValue(v, out float old) || nd < old) { dist[v] = nd; if (prevEdge != null) prevEdge[v] = e; pq.Enqueue(v, nd); }
            }
        }
        return dist;
    }

    /// <summary>
    /// 영역에서 측지 거리가 가장 먼 두 정점을 근사한다(이중 스윕: 임의 시작 → 가장 먼 a → a에서 가장 먼 b).
    /// </summary>
    private static bool FarthestPair(PolyMesh m, HashSet<int> verts, HashSet<int> edges, out int a, out int b)
    {
        a = b = -1;
        if (verts.Count < 2) return false;
        int start = verts.First();
        var d0 = Dijkstra(m, edges, new[] { start }, null, null, null);
        a = d0.OrderByDescending(kv => kv.Value).First().Key;
        var d1 = Dijkstra(m, edges, new[] { a }, null, null, null);
        b = d1.OrderByDescending(kv => kv.Value).First().Key;
        return a != b;
    }

    /// <summary>sources 집합에서 측지 거리가 가장 먼 정점(sources 제외). 없으면 −1.</summary>
    private static int FarthestFrom(PolyMesh m, HashSet<int> verts, HashSet<int> edges, HashSet<int> sources)
    {
        var d = Dijkstra(m, edges, sources, null, null, null);
        int best = -1; float bd = -1;
        foreach (var (v, dv) in d) if (!sources.Contains(v) && dv > bd) { bd = dv; best = v; }
        return best;
    }

    /// <summary>from → to 최단 경로의 엣지 목록(to에서 거꾸로 따라감). 도달 불가면 빈 목록.</summary>
    private static List<int> ShortestPath(PolyMesh m, HashSet<int> edges, int from, int to, HashSet<int>? avoid)
    {
        var prev = new Dictionary<int, int>();
        var dist = Dijkstra(m, edges, new[] { from }, avoid, null, prev);
        var path = new List<int>();
        if (!dist.ContainsKey(to)) return path;
        int cur = to;
        while (cur != from && prev.TryGetValue(cur, out int e))
        {
            path.Add(e);
            var (x, y) = m.EdgeVertices(e);
            cur = x == cur ? y : x;
        }
        return path;
    }

    /// <summary>
    /// 정점 집합 from 중 하나에서 to 중 가장 가까운 정점까지의 최단 경로 엣지 목록. penalize 엣지는 가중치 ×20.
    /// </summary>
    private static List<int> ShortestPathBetweenSets(PolyMesh m, HashSet<int> edges, HashSet<int> from, HashSet<int> to, HashSet<int> penalize)
    {
        var prev = new Dictionary<int, int>();
        var dist = Dijkstra(m, edges, from, null, penalize, prev);
        int target = -1; float bd = float.MaxValue;
        foreach (int t in to) if (dist.TryGetValue(t, out float d) && d < bd) { bd = d; target = t; }
        var path = new List<int>();
        if (target < 0) return path;
        int cur = target;
        while (!from.Contains(cur) && prev.TryGetValue(cur, out int e))
        {
            path.Add(e);
            var (x, y) = m.EdgeVertices(e);
            cur = x == cur ? y : x;
        }
        return path;
    }
}
