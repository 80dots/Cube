using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>엣지 루프/링 탐색과 Insert Edge Loop.</summary>
public static partial class MeshOps
{
    /// <summary>
    /// Maya 식 엣지 루프: 양 끝에서 4가 정점을 만나는 동안 "반대쪽" 엣지(인접 면을 공유하지 않는 엣지)로 이어 간다.
    /// 경계 엣지에서 시작하면 경계(보더) 루프를 돈다. 결과는 시작 엣지를 포함한 순서 있는 목록.
    /// </summary>
    /// <remarks>
    /// 시작 엣지에서 b 쪽(정방향)과 a 쪽(역방향)으로 각각 <see cref="OppositeEdgeAtVertex"/>를 반복 적용한다.
    /// 4가가 아닌 정점(극점/경계)이나 이미 방문한 엣지(닫힌 루프)를 만나면 멈춘다. a 쪽 체인은 뒤집어 앞에 붙여 결과가 루프 순서가 된다.
    /// </remarks>
    public static List<int> EdgeLoop(PolyMesh m, int edge)
    {
        var result = new List<int>();
        if (edge < 0 || edge >= m.EdgeCount || !m.Edges[edge].Alive) return result;
        if (m.IsBoundaryEdge(edge)) return BoundaryLoop(m, edge);
        // visited: 닫힌 루프에서 한 바퀴 돌아오면 멈추기 위한 방문 집합
        var visited = new HashSet<int> { edge };
        result.Add(edge);
        var (a, b) = m.EdgeVertices(edge);
        foreach (int startV in new[] { b, a })
        {
            int v = startV, e = edge;
            var chain = new List<int>();
            while (true)
            {
                // 현재 정점 v에서 e의 "맞은편" 엣지로 넘어가고, 그 엣지의 다른 끝으로 정점을 옮긴다
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
    /// <remarks>
    /// 4가 정점의 네 엣지 중 e 자신을 빼면 셋이 남는다. 그중 둘은 e의 인접 면(f0/f1)에 속하고, 면을 공유하지 않는 엣지가 정확히 하나면
    /// 그것이 루프 진행 방향의 맞은편 엣지다.
    /// </remarks>
    public static int OppositeEdgeAtVertex(PolyMesh m, int v, int e)
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

    /// <summary>
    /// 경계(보더) 루프: 경계 엣지에서 시작해 각 정점에서 다른 경계 엣지가 정확히 하나일 때 그쪽으로 이어 간다.
    /// 경계 엣지가 여러 개 모이는 정점(비매니폴드 정점)에서는 멈춘다. 결과는 루프 순서의 엣지 목록.
    /// </summary>
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
    /// <remarks>
    /// He0 쪽 면으로 먼저 걷고(<see cref="WalkRing"/>) 시작 엣지로 돌아오면 닫힌 링. 아니면 He1 쪽으로도 걸어 뒤집어 앞에 붙인다.
    /// </remarks>
    public static (List<(int edge, int a, int b)> entries, List<int> faces, bool closed) EdgeRing(PolyMesh m, int edge)
    {
        var entries = new List<(int, int, int)>();
        var faces = new List<int>();
        if (edge < 0 || edge >= m.EdgeCount || !m.Edges[edge].Alive) return (entries, faces, false);
        // 시작 엣지의 a/b 방향(EdgeVertices 순서)을 링 전체의 기준으로 쓴다
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
    /// <remarks>
    /// 쿼드 [v0, v1, v2, v3]에서 진입 하프에지 he = v0→v1이면 반대편 하프에지 opp = v2→v3이다. 진입 엣지의 a가 v0이면
    /// a 쪽에 이어지는 정점은 v3(a'), b 쪽은 v2(b')가 되므로 그렇게 a/b를 갱신하고, opp의 트윈으로 다음 면에 들어간다.
    /// 쿼드가 아니거나 이미 본 면이면 멈춘다. 시작 엣지에 돌아오면 closed = true.
    /// </remarks>
    /// <param name="he">출발 하프에지(시작 엣지의 He0 또는 He1).</param>
    /// <param name="faces">지나간 쿼드 면들(출력).</param>
    /// <param name="closed">한 바퀴 돌아 시작 엣지로 돌아왔는지(출력).</param>
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
    /// <remarks>
    /// 처리 순서: ① 링 항목마다 a→b 비율 t 위치에 분할 정점 생성 ② 영향 면(링 쿼드 + 열린 링 양끝의 비쿼드 면)의 코너·하드 플래그를 캡처 후 제거
    /// ③ 링 쿼드는 링 엣지 두 개가 마주 보는 코너 k를 찾아 두 쿼드로 쪼개고, 끝 면은 링 엣지 사이에 분할 정점만 끼운다
    /// ④ 원래 엣지의 하드 플래그를 쪼개진 엣지에 복원 ⑤ 새로 생긴 분할 정점 쌍 사이 엣지를 결과로 반환.
    /// t는 0.01..0.99로 클램프한다.
    /// </remarks>
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
        // 링 엣지 ID → entries 인덱스
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
        // 링 엣지 자체의 하드 플래그(현재 직접 쓰이지는 않지만 면 캡처 전 상태 기록)
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
        // 면을 지운 뒤라 엣지 레코드가 죽었을 수 있으므로 FindEdgeIncludingDead로 entries의 정점 쌍에서 찾는다.
        // 보간 비율 s는 코너 방향이 a→b면 t, b→a면 1−t.
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
                // [c0, pk, pk2, c3]와 [pk, c1, c2, pk2] 두 쿼드로 나누고 원래 네 엣지의 하드를 쪼개진 조각에 복원
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
        // 새 루프 엣지(분할 정점 쌍) ID 수집
        foreach (var (a, b) in newEdgePairs.Distinct())
        {
            int e = m.FindEdge(a, b);
            if (e >= 0) result.Add(e);
        }
        m.BumpTopology();
        return result;
    }

    /// <summary>면 제거 후에도 링 엣지(정점 쌍)를 찾기 위한 보조: entries에서 정점 쌍으로 검색.</summary>
    /// <remarks>
    /// 확장 메서드 형태지만 메시를 보지 않고 entries만 선형 검색한다. 면이 제거되어 FindEdge로 찾을 수 없을 때도 원래 링 엣지 ID를 돌려준다.
    /// </remarks>
    private static int FindEdgeIncludingDead(this PolyMesh m, int x, int y, List<(int edge, int a, int b)> entries)
    {
        foreach (var (e, a, b) in entries) if ((a == x && b == y) || (a == y && b == x)) return e;
        return -1;
    }

    /// <summary>
    /// Maya 면 루프: 서로 엣지를 공유하는 두 면 A, B에서 시작해 B 쪽으로(공유 엣지의 반대편 엣지를 건너) 쿼드를 따라 계속 가고,
    /// A 쪽으로도 반대 방향으로 간다. 쿼드가 아닌 면(그 면까지 포함)이나 경계에서 멈추고, 한 바퀴 돌아오면 닫힌다.
    /// 두 면이 이웃이 아니면 빈 목록.
    /// </summary>
    public static List<int> FaceLoop(PolyMesh m, int faceA, int faceB)
    {
        var result = new List<int>();
        if (faceA < 0 || faceB < 0 || faceA >= m.FaceCount || faceB >= m.FaceCount || !m.Faces[faceA].Alive || !m.Faces[faceB].Alive || faceA == faceB) return result;
        // faceA의 하프에지 중 트윈이 faceB에 속한 것의 엣지 = 공유 엣지
        int shared = -1;
        int start = m.Faces[faceA].HalfEdge, he = start;
        do { int tw = m.Hes[he].Twin; if (tw >= 0 && m.Hes[tw].Face == faceB) { shared = m.Hes[he].Edge; break; } he = m.Hes[he].Next; } while (he != start);
        if (shared < 0) return result;
        // 정방향: faceB에서 공유 엣지로 들어가 계속. 닫힌 루프면 역방향은 필요 없다.
        var visited = new HashSet<int> { faceA, faceB };
        var forward = new List<int> { faceB };
        bool closed = Walk(faceB, shared, forward);
        var backward = new List<int>();
        if (!closed) Walk(faceA, shared, backward);
        backward.Reverse();
        result.AddRange(backward);
        result.Add(faceA);
        result.AddRange(forward);
        return result;

        // f에 entry 엣지로 들어왔을 때 반대편 엣지를 건너 계속 간다. 시작 면으로 돌아오면 true(닫힌 루프).
        bool Walk(int f, int entry, List<int> list)
        {
            // guard: 면 수 + 2번 이상 돌면 비정상 위상으로 보고 멈춤
            for (int guard = 0; guard < m.FaceCount + 2; guard++)
            {
                if (m.FaceDegree(f) != 4) return false;
                int h = m.Faces[f].HalfEdge, s0 = h, entryHe = -1;
                do { if (m.Hes[h].Edge == entry) { entryHe = h; break; } h = m.Hes[h].Next; } while (h != s0);
                if (entryHe < 0) return false;
                // 진입 엣지의 반대편(쿼드 기준 두 칸 다음) 하프에지 너머 면으로 이동
                int opp = m.Hes[m.Hes[entryHe].Next].Next;
                int tw = m.Hes[opp].Twin;
                if (tw < 0) return false;
                int nf = m.Hes[tw].Face;
                if (nf < 0 || !m.Faces[nf].Alive) return false;
                if (visited.Contains(nf)) return nf == faceA || nf == faceB;
                visited.Add(nf);
                list.Add(nf);
                f = nf; entry = m.Hes[opp].Edge;
            }
            return false;
        }
    }
}
