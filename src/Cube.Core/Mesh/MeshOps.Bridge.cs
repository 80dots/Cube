using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Bridge: 두 경계 엣지 체인 사이를 쿼드로 잇는다.</summary>
public static partial class MeshOps
{
    /// <summary>
    /// 선택 경계 엣지들을 연결 성분으로 나눠 정확히 두 체인(열린 체인 또는 닫힌 루프)이고 엣지 수가 같을 때 쿼드로 잇는다.
    /// 면 방향이 일관되도록 체인을 반대 방향으로 짝짓고, 닫힌 루프는 정점 거리 합이 최소가 되는 오프셋을 고른다.
    /// 반환값은 새 면 ID들(실패하면 빈 목록).
    /// </summary>
    public static List<int> BridgeEdges(PolyMesh m, IEnumerable<int> edgeIds)
    {
        var result = new List<int>();
        var edges = edgeIds.Where(e => e >= 0 && e < m.EdgeCount && m.Edges[e].Alive && m.IsBoundaryEdge(e)).Distinct().ToList();
        if (edges.Count < 2) return result;

        // 면 방향(he0: x→y) 기준 directed edge
        var outgoing = new Dictionary<int, (int edge, int to)>();
        var incoming = new HashSet<int>();
        foreach (int e in edges)
        {
            int he = m.Edges[e].He0;
            int x = m.Hes[he].Vertex, y = m.Hes[m.Hes[he].Next].Vertex;
            if (!outgoing.TryAdd(x, (e, y))) return result; // 분기: 체인이 아님
            incoming.Add(y);
        }

        // 체인 추출
        var chains = new List<(List<int> verts, bool closed)>();
        var used = new HashSet<int>();
        foreach (int start in outgoing.Keys.Where(v => !incoming.Contains(v)).Concat(outgoing.Keys))
        {
            if (used.Contains(start)) continue;
            var verts = new List<int> { start };
            int cur = start; bool closed = false;
            while (outgoing.TryGetValue(cur, out var nx) && !used.Contains(cur))
            {
                used.Add(cur);
                cur = nx.to;
                if (cur == start) { closed = true; break; }
                verts.Add(cur);
            }
            if (verts.Count >= 2) chains.Add((verts, closed));
        }
        if (chains.Count != 2) return result;
        var (A, closedA) = chains[0]; var (B, closedB) = chains[1];
        if (closedA != closedB) return result;
        int nA = closedA ? A.Count : A.Count - 1, nB = closedB ? B.Count : B.Count - 1;
        if (nA != nB || nA == 0) return result;
        int N = nA;

        // 쿼드 [a_{i+1}, a_i, b_{j+1}, b_j], j = offset - i (닫힘) / N-1-i (열림)
        int AV(int i) => A[((i % A.Count) + A.Count) % A.Count];
        int BV(int j) => B[((j % B.Count) + B.Count) % B.Count];
        int bestOffset = 0;
        if (closedA)
        {
            float best = float.MaxValue;
            for (int off = 0; off < N; off++)
            {
                float sum = 0;
                for (int i = 0; i < N; i++) sum += Vector3.Distance(m.Verts[AV(i)].Position, m.Verts[BV(off - i + 1)].Position);
                if (sum < best) { best = sum; bestOffset = off; }
            }
        }
        for (int i = 0; i < N; i++)
        {
            int j = closedA ? bestOffset - i : N - 1 - i;
            int a0 = AV(i), a1 = AV(i + 1), b0 = BV(j), b1 = BV(j + 1);
            var quad = new[] { a1, a0, b1, b0 };
            if (quad.Distinct().Count() < 3) continue;
            int f = m.AddFace(quad);
            if (f >= 0) result.Add(f);
        }
        if (result.Count > 0) m.BumpTopology();
        return result;
    }
}
