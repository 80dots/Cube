using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>
/// 자동 심 선택. (1) 날카로운 엣지(이면각 ≥ angle, 하드 엣지)를 심으로 잡고, (2) 심으로 나뉜 각 영역이 원반(disk)이 되도록
/// 닫힌 영역은 가장 먼 두 정점을 잇는 두 경로(반구 둘로 나눔), 구멍이 있는 영역은 경계 사이 최단 경로를 추가로 자른다.
/// </summary>
public static class AutoSeams
{
    public static HashSet<int> Select(PolyMesh m, float angleDeg = 55f, int maxPasses = 4)
    {
        var seams = new HashSet<int>();
        float cosLimit = MathF.Cos(angleDeg * MathF.PI / 180f);
        // 1) 날카로운/하드 엣지
        for (int e = 0; e < m.EdgeCount; e++)
        {
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
            if (!changed) break;
        }
        return seams;
    }

    /// <summary>심/경계로 나뉜 면 연결 영역.</summary>
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

    private static int BoundaryLoops(PolyMesh m, HashSet<int> boundaryEdges) => BoundaryLoopVertices(m, boundaryEdges).Count;

    private static List<HashSet<int>> BoundaryLoopVertices(PolyMesh m, HashSet<int> boundaryEdges)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (int e in boundaryEdges)
        {
            var (a, b) = m.EdgeVertices(e);
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<int>(); la.Add(b);
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<int>(); lb.Add(a);
        }
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

    private static Dictionary<int, float> Dijkstra(PolyMesh m, HashSet<int> edges, IEnumerable<int> sources, HashSet<int>? avoidVerts, HashSet<int>? penalizeEdges, Dictionary<int, int>? prevEdge)
    {
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

    private static int FarthestFrom(PolyMesh m, HashSet<int> verts, HashSet<int> edges, HashSet<int> sources)
    {
        var d = Dijkstra(m, edges, sources, null, null, null);
        int best = -1; float bd = -1;
        foreach (var (v, dv) in d) if (!sources.Contains(v) && dv > bd) { bd = dv; best = v; }
        return best;
    }

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
