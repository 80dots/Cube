using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>엣지 루프/링 탐색과 Insert Edge Loop.</summary>
public static partial class MeshOps
{
    /// <summary>
    /// Maya 식 엣지 루프: 양 끝에서 4가 정점을 만나는 동안 "반대쪽" 엣지(인접 면을 공유하지 않는 엣지)로 이어 간다.
    /// 경계 엣지에서 시작하면 경계(보더) 루프를 돈다. 결과는 시작 엣지를 포함한 순서 있는 목록.
    /// </summary>
    public static List<int> EdgeLoop(PolyMesh m, int edge)
    {
        var result = new List<int>();
        if (edge < 0 || edge >= m.EdgeCount || !m.Edges[edge].Alive) return result;
        if (m.IsBoundaryEdge(edge)) return BoundaryLoop(m, edge);
        var visited = new HashSet<int> { edge };
        result.Add(edge);
        var (a, b) = m.EdgeVertices(edge);
        foreach (int startV in new[] { b, a })
        {
            int v = startV, e = edge;
            var chain = new List<int>();
            while (true)
            {
                int next = OppositeEdgeAtVertex(m, v, e);
                if (next < 0 || !visited.Add(next)) break;
                chain.Add(next);
                var (x, y) = m.EdgeVertices(next);
                v = x == v ? y : x;
                e = next;
            }
            if (startV == b) result.AddRange(chain);
            else { chain.Reverse(); result.InsertRange(0, chain); }
        }
        return result;
    }

    /// <summary>4가 내부 정점 v에서 e와 면을 공유하지 않는 유일한 엣지. 없거나 모호하면 -1.</summary>
    private static int OppositeEdgeAtVertex(PolyMesh m, int v, int e)
    {
        var edges = new List<int>(); m.GetVertexEdges(v, edges);
        if (edges.Count != 4) return -1;
        var (f0, f1) = m.EdgeFaces(e);
        int found = -1, count = 0;
        foreach (int c in edges)
        {
            if (c == e) continue;
            var (g0, g1) = m.EdgeFaces(c);
            bool shares = g0 == f0 || g0 == f1 || (g1 >= 0 && (g1 == f0 || g1 == f1));
            if (!shares) { found = c; count++; }
        }
        return count == 1 ? found : -1;
    }

    private static List<int> BoundaryLoop(PolyMesh m, int edge)
    {
        var result = new List<int> { edge };
        var visited = new HashSet<int> { edge };
        var (a, b) = m.EdgeVertices(edge);
        foreach (int startV in new[] { b, a })
        {
            int v = startV, e = edge;
            var chain = new List<int>();
            while (true)
            {
                var edges = new List<int>(); m.GetVertexEdges(v, edges);
                var borders = edges.Where(x => x != e && m.IsBoundaryEdge(x)).ToList();
                if (borders.Count != 1 || !visited.Add(borders[0])) break;
                int next = borders[0];
                chain.Add(next);
                var (x, y) = m.EdgeVertices(next);
                v = x == v ? y : x;
                e = next;
            }
            if (startV == b) result.AddRange(chain);
            else { chain.Reverse(); result.InsertRange(0, chain); }
        }
        return result;
    }

    /// <summary>
    /// 엣지 링: 쿼드 면을 가로질러 반대편 엣지로 이어 간다. 각 항목은 (엣지, a쪽 정점, b쪽 정점)이며 a/b 쪽은 링 전체에서 일관된다.
    /// faces[i]는 entries[i]와 entries[i+1] 사이의 쿼드(닫힌 링이면 마지막 면이 끝과 처음을 잇는다).
    /// </summary>
    public static (List<(int edge, int a, int b)> entries, List<int> faces, bool closed) EdgeRing(PolyMesh m, int edge)
    {
        var entries = new List<(int, int, int)>();
        var faces = new List<int>();
        if (edge < 0 || edge >= m.EdgeCount || !m.Edges[edge].Alive) return (entries, faces, false);
        var (a0, b0) = m.EdgeVertices(edge);
        var ed = m.Edges[edge];
        var fwd = WalkRing(m, ed.He0, a0, b0, edge, out var fwdFaces, out bool closed);
        if (closed)
        {
            entries.Add((edge, a0, b0)); entries.AddRange(fwd);
            faces.AddRange(fwdFaces);
            return (entries, faces, true);
        }
        var back = new List<(int, int, int)>(); var backFacesList = new List<int>();
        if (ed.He1 >= 0) back = WalkRing(m, ed.He1, a0, b0, edge, out backFacesList, out _);
        back.Reverse(); backFacesList.Reverse();
        entries.AddRange(back); entries.Add((edge, a0, b0)); entries.AddRange(fwd);
        faces.AddRange(backFacesList); faces.AddRange(fwdFaces);
        return (entries, faces, false);
    }

    /// <summary>he가 속한 면부터 링을 따라간다. 반환은 he의 엣지 다음부터의 항목들.</summary>
    private static List<(int edge, int a, int b)> WalkRing(PolyMesh m, int he, int a, int b, int startEdge, out List<int> faces, out bool closed)
    {
        var list = new List<(int, int, int)>();
        faces = new List<int>();
        closed = false;
        var seen = new HashSet<int>();
        int guard = 0;
        while (he >= 0 && guard++ < 100000)
        {
            int f = m.Hes[he].Face;
            if (m.FaceDegree(f) != 4 || !seen.Add(f)) break;
            var h = m.Hes[he];
            int opp = m.Hes[h.Next].Next;          // 반대편 하프에지: loop[2] → loop[3]
            int v2 = m.Hes[opp].Vertex, v3 = m.Hes[m.Hes[opp].Next].Vertex;
            int na, nb;
            if (h.Vertex == a) { nb = v2; na = v3; }  // 루프 [a, b, b', a']
            else { na = v2; nb = v3; }                // 루프 [b, a, a', b']
            int e2 = m.Hes[opp].Edge;
            faces.Add(f);
            if (e2 == startEdge) { closed = true; break; }
            list.Add((e2, na, nb));
            a = na; b = nb;
            he = m.Hes[opp].Twin;
        }
        if (!closed && faces.Count > list.Count) { /* 마지막 면이 추가되었지만 다음 엣지가 시작 엣지가 아니고 트윈이 없음: 정상(열린 링) */ }
        return list;
    }

    /// <summary>
    /// Insert Edge Loop: edge를 지나는 엣지 링의 모든 엣지를 t(0..1, 시작 엣지의 a→b 기준)에서 나누고 쿼드들을 둘로 쪼갠다.
    /// 링 끝의 비쿼드 면에는 정점만 끼운다. 반환값은 새 루프 엣지 ID들.
    /// </summary>
    public static List<int> InsertEdgeLoop(PolyMesh m, int edge, float t)
    {
        var result = new List<int>();
        var (entries, ringFaces, closed) = EdgeRing(m, edge);
        if (entries.Count == 0 || ringFaces.Count == 0) return result;
        t = Math.Clamp(t, 0.01f, 0.99f);

        // 분할 정점
        var split = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var (_, a, b) = entries[i];
            split[i] = m.AddVertex(Vector3.Lerp(m.Verts[a].Position, m.Verts[b].Position, t));
        }
        var entryIndex = new Dictionary<int, int>();
        for (int i = 0; i < entries.Count; i++) entryIndex[entries[i].edge] = i;

        // 영향을 받는 면: 링 면 + 열린 링 양끝의 바깥 면
        var rebuild = new List<(int face, List<Corner> corners, int material, List<bool> hard)>();
        var ringSet = new HashSet<int>(ringFaces);
        var endFaces = new List<int>();
        if (!closed)
        {
            foreach (int idx in new[] { 0, entries.Count - 1 })
            {
                var (f0, f1) = m.EdgeFaces(entries[idx].edge);
                foreach (int f in new[] { f0, f1 }) if (f >= 0 && !ringSet.Contains(f) && !endFaces.Contains(f)) endFaces.Add(f);
            }
        }
        var hardOf = new Dictionary<int, bool>();
        foreach (var (e, _, _) in entries) hardOf[e] = m.Edges[e].Hard;

        foreach (int f in ringFaces.Concat(endFaces))
        {
            var corners = CaptureCorners(m, f);
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
            rebuild.Add((f, corners, m.Faces[f].Material, hard));
        }
        foreach (var (f, _, _, _) in rebuild) m.RemoveFace(f, removeIsolated: false);

        // 코너 쌍 (cx → cy)가 링 엣지면 그 분할 정점과 보간 UV를 돌려준다
        bool TrySplitCorner(Corner cx, Corner cy, out int idx, out Corner p)
        {
            idx = -1; p = default;
            int e = m.FindEdgeIncludingDead(cx.Vertex, cy.Vertex, entries);
            if (e < 0 || !entryIndex.TryGetValue(e, out idx)) return false;
            var (_, a, _) = entries[idx];
            float s = cx.Vertex == a ? t : 1f - t;
            p = new Corner(split[idx], Vector2.Lerp(cx.Uv, cy.Uv, s), Vector3.Lerp(cx.Normal, cy.Normal, s));
            return true;
        }

        var newEdgePairs = new List<(int a, int b)>();
        foreach (var (f, corners, material, hard) in rebuild)
        {
            int n = corners.Count;
            if (ringSet.Contains(f) && n == 4)
            {
                // k: 코너 k→k+1 이 링 엣지, k+2→k+3 도 링 엣지
                int k = -1; Corner pk = default, pk2 = default; int ik = -1, ik2 = -1;
                for (int i = 0; i < 4; i++)
                {
                    if (TrySplitCorner(corners[i], corners[(i + 1) % 4], out ik, out pk) && TrySplitCorner(corners[(i + 2) % 4], corners[(i + 3) % 4], out ik2, out pk2)) { k = i; break; }
                }
                if (k < 0) { AddFaceWithCorners(m, corners, material); continue; }
                var c0 = corners[k]; var c1 = corners[(k + 1) % 4]; var c2 = corners[(k + 2) % 4]; var c3 = corners[(k + 3) % 4];
                int fa = AddFaceWithCorners(m, new[] { c0, pk, pk2, c3 }, material);
                int fb = AddFaceWithCorners(m, new[] { pk, c1, c2, pk2 }, material);
                bool h01 = hard[k], h23 = hard[(k + 2) % 4], h12 = hard[(k + 1) % 4], h30 = hard[(k + 3) % 4];
                SetHard(m, c0.Vertex, pk.Vertex, h01); SetHard(m, pk.Vertex, c1.Vertex, h01);
                SetHard(m, c2.Vertex, pk2.Vertex, h23); SetHard(m, pk2.Vertex, c3.Vertex, h23);
                SetHard(m, c1.Vertex, c2.Vertex, h12); SetHard(m, c3.Vertex, c0.Vertex, h30);
                if (fa >= 0 || fb >= 0) newEdgePairs.Add((pk.Vertex, pk2.Vertex));
            }
            else
            {
                // 끝 면: 링 엣지에 해당하는 코너 쌍 사이에 분할 정점을 끼운다
                var loop = new List<Corner>(); var loopHard = new List<bool>();
                for (int i = 0; i < n; i++)
                {
                    loop.Add(corners[i]);
                    if (TrySplitCorner(corners[i], corners[(i + 1) % n], out _, out var p))
                    { loopHard.Add(hard[i]); loop.Add(p); loopHard.Add(hard[i]); }
                    else loopHard.Add(hard[i]);
                }
                int nf = AddFaceWithCorners(m, loop, material);
                if (nf >= 0) for (int i = 0; i < loop.Count; i++) SetHard(m, loop[i].Vertex, loop[(i + 1) % loop.Count].Vertex, loopHard[i]);
            }
        }
        foreach (var (a, b) in newEdgePairs.Distinct())
        {
            int e = m.FindEdge(a, b);
            if (e >= 0) result.Add(e);
        }
        m.BumpTopology();
        return result;
    }

    /// <summary>면 제거 후에도 링 엣지(정점 쌍)를 찾기 위한 보조: entries에서 정점 쌍으로 검색.</summary>
    private static int FindEdgeIncludingDead(this PolyMesh m, int x, int y, List<(int edge, int a, int b)> entries)
    {
        foreach (var (e, a, b) in entries) if ((a == x && b == y) || (a == y && b == x)) return e;
        return -1;
    }
}
