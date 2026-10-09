using Cube.Core.Mesh;

namespace Cube.Core.Selection;

/// <summary>Select → Non-Manifold(v0.0.58) 옵션: 찾을 비매니폴드·열린 요소 종류.</summary>
public sealed class NonManifoldOptions
{
    /// <summary>경계(면이 한쪽에만 있는) 엣지와 그 정점 — 닫히지 않은 구멍(Blender Boundaries).</summary>
    public bool Boundaries = true;
    /// <summary>나비넥타이(꼬집힌) 정점: 주변 면이 정점 하나로만 이어진 부채꼴 둘 이상(Blender Vertices).</summary>
    public bool Bowtie = true;
    /// <summary>면에 속하지 않은 고립 정점(Blender Wire/Loose에 해당; Cube 메시에는 와이어 엣지가 없다).</summary>
    public bool Isolated = true;
}

/// <summary>
/// 비매니폴드 요소 찾기. Cube 메시는 하프에지 구조라 엣지 하나에 면이 셋 이상 붙거나 이웃 면 방향이 어긋나는 경우는 애초에 만들 수 없다
/// (AddFace가 거부). 그래서 남는 경우 — 경계 엣지(닫히지 않음), 정점에서만 맞닿은 부채꼴(나비넥타이), 고립 정점 — 를 찾는다.
/// </summary>
public static class NonManifold
{
    /// <summary>찾은 정점·엣지 ID. 나비넥타이 정점의 엣지는 그 정점에 닿은 모든 엣지, 고립 정점은 엣지가 없다.</summary>
    public static (HashSet<int> verts, HashSet<int> edges) Find(PolyMesh m, NonManifoldOptions o)
    {
        var verts = new HashSet<int>(); var edges = new HashSet<int>();
        if (o.Boundaries)
            for (int e = 0; e < m.EdgeCount; e++)
            {
                var ed = m.Edges[e];
                if (!ed.Alive || ed.He1 >= 0) continue;
                edges.Add(e);
                var (a, b) = m.EdgeVertices(e); verts.Add(a); verts.Add(b);
            }
        var tmp = new List<int>();
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            var outgoing = m.VertexOutgoing(v);
            if (outgoing.Length == 0) { if (o.Isolated) verts.Add(v); continue; }
            if (o.Bowtie && IsBowtie(m, v, outgoing))
            {
                verts.Add(v);
                m.GetVertexEdges(v, tmp); edges.UnionWith(tmp);
            }
        }
        return (verts, edges);
    }

    /// <summary>
    /// 정점 v 주변 부채꼴이 둘 이상인지: 출발 하프에지 하나에서 이웃 면을 따라(prev.twin 방향, 경계를 만나면 반대 방향 twin.next로도) 돌며
    /// 닿는 출발 하프에지 수가 전체보다 적으면 정점 하나로만 이어진 덩어리가 따로 있다.
    /// </summary>
    public static bool IsBowtie(PolyMesh m, int v, ReadOnlySpan<int> outgoing)
    {
        if (outgoing.Length <= 1) return false;
        var seen = new HashSet<int> { outgoing[0] };
        // 한 방향: 이 면에서 v로 들어오는 하프에지(prev)의 트윈 = 다음 면에서 v를 떠나는 하프에지
        int cur = outgoing[0];
        bool closed = false;
        for (int guard = 0; guard < outgoing.Length + 2; guard++)
        {
            int nxt = m.Hes[m.Hes[cur].Prev].Twin;
            if (nxt < 0) break;
            if (nxt == outgoing[0]) { closed = true; break; }
            if (!seen.Add(nxt)) break;
            cur = nxt;
        }
        // 경계에서 멈췄으면 반대 방향: 트윈(v로 들어옴)의 next = 이웃 면에서 v를 떠나는 하프에지
        if (!closed)
        {
            cur = outgoing[0];
            for (int guard = 0; guard < outgoing.Length + 2; guard++)
            {
                int tw = m.Hes[cur].Twin;
                if (tw < 0) break;
                int nxt = m.Hes[tw].Next;
                if (!seen.Add(nxt)) break;
                cur = nxt;
            }
        }
        return seen.Count < outgoing.Length;
    }
}
