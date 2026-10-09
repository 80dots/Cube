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
    /// <remarks>
    /// 알고리즘: ① 각 경계 엣지를 He0(면 쪽) 방향 x→y의 유향 엣지로 보고 outgoing[x] = (엣지, y)로 저장(한 정점에서 둘 이상 나가면 분기 → 실패)
    /// ② 들어오는 엣지가 없는 정점(열린 체인 시작)부터, 그다음 남은 정점(닫힌 루프)에서 체인을 추출
    /// ③ 체인이 정확히 2개이고 열림/닫힘과 엣지 수가 같아야 진행 ④ 체인 A의 i번째 엣지와 체인 B의 j번째 엣지(반대 방향)를 쿼드로 잇는다.
    /// 두 경계 체인은 서로 반대 방향으로 돌기 때문에 B를 역순으로 짝지어야 새 면이 기존 면과 같은 감김(CCW)이 된다.
    /// </remarks>
    public static List<int> BridgeEdges(PolyMesh m, IEnumerable<int> edgeIds)
    {
        var result = new List<int>();
        // 경계 엣지만 대상(내부 엣지에 면을 더 붙이면 비매니폴드)
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
        // used: 이미 체인에 들어간 시작 정점. 열린 체인 시작점을 먼저 처리해 체인이 중간부터 잘리지 않게 한다.
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
        // N = 체인당 엣지 수(닫힌 루프는 정점 수와 같고, 열린 체인은 정점 수 − 1)
        var (A, closedA) = chains[0]; var (B, closedB) = chains[1];
        if (closedA != closedB) return result;
        int nA = closedA ? A.Count : A.Count - 1, nB = closedB ? B.Count : B.Count - 1;
        if (nA != nB || nA == 0) return result;
        int N = nA;

        // 쿼드 [a_{i+1}, a_i, b_{j+1}, b_j], j = offset - i (닫힘) / N-1-i (열림)
        // AV/BV: 순환 인덱스로 체인 정점 접근(음수·초과 인덱스 허용)
        int AV(int i) => A[((i % A.Count) + A.Count) % A.Count];
        int BV(int j) => B[((j % B.Count) + B.Count) % B.Count];
        int bestOffset = 0;
        // 닫힌 루프: 시작 위치 짝(offset)을 모두 시험해 짝지은 정점 사이 거리 합이 최소인 것을 고른다(비틀림 최소화)
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
            // 양 끝이 같은 정점으로 모이면 삼각형/퇴화이므로 정점이 3개 미만이면 건너뜀
            if (quad.Distinct().Count() < 3) continue;
            int f = m.AddFace(quad);
            if (f >= 0) result.Add(f);
        }
        if (result.Count > 0) m.BumpTopology();
        return result;
    }

    /// <summary>
    /// Bridge(면 선택, Maya와 같음): 선택 면들을 지우고 그 영역들의 경계 루프를 <see cref="BridgeEdges"/>로 잇는다(예: 큐브 윗면·아랫면 → 관통 구멍).
    /// 영역이 정확히 둘이 아니거나 경계 엣지 수가 다르면 메시를 바꾸지 않고 빈 목록을 돌려준다.
    /// </summary>
    public static List<int> BridgeFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        var faces = AliveFaces(m, faceIds).ToList();
        if (faces.Count < 2) return new List<int>();
        // 선택 영역 경계(한쪽만 선택 면인 엣지)를 정점 쌍으로 기억한다(면을 지우면 엣지 ID가 바뀔 수 있음)
        var set = new HashSet<int>(faces);
        var pairs = new List<(int a, int b)>();
        var hes = new List<int>();
        foreach (int f in faces)
        {
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes)
            {
                int tw = m.Hes[he].Twin;
                if (tw >= 0 && set.Contains(m.Hes[tw].Face)) continue;
                pairs.Add((m.Hes[he].Vertex, m.Hes[m.Hes[he].Next].Vertex));
            }
        }
        var work = m.Clone();
        DeleteFaces(work, faces);
        var edges = pairs.Select(p => work.FindEdge(p.a, p.b)).Where(e => e >= 0).Distinct().ToList();
        var made = BridgeEdges(work, edges);
        if (made.Count == 0) return made;
        m.CopyFrom(work);
        return made;
    }
}
