using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// 위상 편집 연산. 모두 메시를 제자리에서 수정하며 끝에 <see cref="PolyMesh.BumpTopology"/>를 호출한다.
/// 노멀 재계산은 호출자(MeshEditCommand)가 한다.
/// </summary>
public static partial class MeshOps
{
    internal readonly record struct Corner(int Vertex, Vector2 Uv, Vector3 Normal);

    internal static List<Corner> CaptureCorners(PolyMesh m, int f)
    {
        var list = new List<Corner>();
        int start = m.Faces[f].HalfEdge, he = start;
        do { var h = m.Hes[he]; list.Add(new Corner(h.Vertex, h.Uv0, h.Normal)); he = h.Next; } while (he != start);
        return list;
    }

    internal static int AddFaceWithCorners(PolyMesh m, IReadOnlyList<Corner> corners, int material = 0)
    {
        var ids = new int[corners.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = corners[i].Vertex;
        int f = m.AddFace(ids, material);
        if (f < 0) return -1;
        int start = m.Faces[f].HalfEdge, he = start, i2 = 0;
        do { var h = m.Hes[he]; h.Uv0 = corners[i2].Uv; h.Normal = corners[i2].Normal; m.Hes[he] = h; he = h.Next; i2++; } while (he != start);
        return f;
    }

    internal static void SetHard(PolyMesh m, int a, int b, bool hard)
    {
        int e = m.FindEdge(a, b);
        if (e < 0) return;
        var ed = m.Edges[e]; ed.Hard = hard; m.Edges[e] = ed;
    }

    internal static bool IsHard(PolyMesh m, int a, int b)
    {
        int e = m.FindEdge(a, b);
        return e >= 0 && m.Edges[e].Hard;
    }

    // ------------------------------------------------------------ Extrude

    /// <summary>
    /// 면 집합을 압출(Maya "Keep Faces Together" on). 이동 거리 0으로 위상만 만든다.
    /// 영역 경계 엣지마다 측면 쿼드가 생기고, 경계 정점은 복제된다. 반환값은 새 캡 면 ID들.
    /// </summary>
    public static List<int> ExtrudeFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        var region = new HashSet<int>(faceIds.Where(f => f >= 0 && f < m.Faces.Count && m.Faces[f].Alive));
        var result = new List<int>();
        if (region.Count == 0) return result;

        // 경계 하프에지(영역 안, 트윈이 영역 밖)
        var boundaryHes = new List<(int a, int b, bool hard)>();
        var boundaryVerts = new HashSet<int>();
        foreach (int f in region)
        {
            int start = m.Faces[f].HalfEdge, he = start;
            do
            {
                var h = m.Hes[he];
                bool boundary = h.Twin < 0 || !region.Contains(m.Hes[h.Twin].Face);
                if (boundary)
                {
                    int a = h.Vertex, b = m.Hes[h.Next].Vertex;
                    boundaryHes.Add((a, b, m.Edges[h.Edge].Hard));
                    boundaryVerts.Add(a); boundaryVerts.Add(b);
                }
                he = h.Next;
            } while (he != start);
        }

        // 원본 면 코너 캡처 + 내부 엣지 하드 플래그
        var faceCorners = new Dictionary<int, (List<Corner> corners, int material, List<bool> hard)>();
        foreach (int f in region)
        {
            var corners = CaptureCorners(m, f);
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
            faceCorners[f] = (corners, m.Faces[f].Material, hard);
        }

        // 경계 정점 복제
        var dup = new Dictionary<int, int>();
        foreach (int v in boundaryVerts) dup[v] = m.AddVertex(m.Verts[v].Position);

        // 영역 면 제거(정점은 유지)
        foreach (int f in region) m.RemoveFace(f, removeIsolated: false);

        // 캡 면 재생성(경계 정점 → 복제본)
        foreach (var (f, (corners, material, hard)) in faceCorners)
        {
            var mapped = corners.Select(c => c with { Vertex = dup.TryGetValue(c.Vertex, out int d) ? d : c.Vertex }).ToList();
            int nf = AddFaceWithCorners(m, mapped, material);
            if (nf >= 0)
            {
                result.Add(nf);
                for (int i = 0; i < mapped.Count; i++) SetHard(m, mapped[i].Vertex, mapped[(i + 1) % mapped.Count].Vertex, hard[i]);
            }
        }

        // 측면 쿼드 (a, b, b', a')
        foreach (var (a, b, hard) in boundaryHes)
        {
            int a2 = dup[a], b2 = dup[b];
            var quad = new List<Corner>
            {
                new(a, new Vector2(0, 0), Vector3.Zero), new(b, new Vector2(1, 0), Vector3.Zero),
                new(b2, new Vector2(1, 1), Vector3.Zero), new(a2, new Vector2(0, 1), Vector3.Zero),
            };
            int q = AddFaceWithCorners(m, quad);
            if (q >= 0)
            {
                SetHard(m, a, b, hard);
                SetHard(m, a2, b2, hard);
                SetHard(m, a, a2, hard);
                SetHard(m, b, b2, hard);
            }
        }
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Delete

    public static void DeleteFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        foreach (int f in faceIds.ToArray()) m.RemoveFace(f);
        m.BumpTopology();
    }

    /// <summary>엣지 삭제(Maya Delete Edge): 양쪽 면을 하나로 합친다. 경계 엣지는 인접 면을 지운다. 결과로 생긴 2가 정점은 녹인다.</summary>
    public static void DeleteEdges(PolyMesh m, IEnumerable<int> edgeIds)
    {
        var touched = new HashSet<int>();
        foreach (int e in edgeIds.ToArray())
        {
            if (e < 0 || e >= m.Edges.Count || !m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            touched.Add(a); touched.Add(b);
            var (f0, f1) = m.EdgeFaces(e);
            if (f1 < 0) { m.RemoveFace(f0, removeIsolated: false); continue; }
            MergeFacesAcrossEdge(m, e);
        }
        foreach (int v in touched) DissolveIfValence2(m, v);
        foreach (int v in touched) m.RemoveVertexIfIsolated(v);
        m.BumpTopology();
    }

    private static void MergeFacesAcrossEdge(PolyMesh m, int e)
    {
        var ed = m.Edges[e];
        int he0 = ed.He0, he1 = ed.He1;
        int f0 = m.Hes[he0].Face, f1 = m.Hes[he1].Face;
        if (f0 == f1) return; // 같은 면의 두 변(비정상) → 무시
        // f0 루프를 he0부터 시작해 he0를 제외하고 수집, 그 자리에 f1 루프(he1 제외)를 끼운다
        var loop = new List<Corner>();
        int cur = m.Hes[he0].Next;
        while (cur != he0) { var h = m.Hes[cur]; loop.Add(new Corner(h.Vertex, h.Uv0, h.Normal)); cur = h.Next; }
        cur = m.Hes[he1].Next;
        while (cur != he1) { var h = m.Hes[cur]; loop.Add(new Corner(h.Vertex, h.Uv0, h.Normal)); cur = h.Next; }
        // 하드 플래그 보존
        var hard = new List<bool>();
        for (int i = 0; i < loop.Count; i++) hard.Add(IsHard(m, loop[i].Vertex, loop[(i + 1) % loop.Count].Vertex));
        int material = m.Faces[f0].Material;
        m.RemoveFace(f0, removeIsolated: false);
        m.RemoveFace(f1, removeIsolated: false);
        // 중복 정점이 생기면(같은 정점을 두 번 지나는 비단순 루프) 거부될 수 있다 → 그대로 둔다
        int nf = AddFaceWithCorners(m, loop, material);
        if (nf >= 0) for (int i = 0; i < loop.Count; i++) SetHard(m, loop[i].Vertex, loop[(i + 1) % loop.Count].Vertex, hard[i]);
    }

    /// <summary>엣지가 정확히 2개인 정점을 인접 면 루프에서 제거한다(직선 위 불필요 정점 정리).</summary>
    public static bool DissolveIfValence2(PolyMesh m, int v)
    {
        if (v < 0 || v >= m.Verts.Count || !m.Verts[v].Alive) return false;
        var edges = new List<int>(); m.GetVertexEdges(v, edges);
        if (edges.Count != 2) return false;
        var faces = new List<int>(); m.GetVertexFaces(v, faces);
        foreach (int f in faces.Distinct().ToArray())
        {
            var corners = CaptureCorners(m, f).Where(c => c.Vertex != v).ToList();
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex) || IsHard(m, corners[i].Vertex, v) || IsHard(m, v, corners[(i + 1) % corners.Count].Vertex));
            int material = m.Faces[f].Material;
            m.RemoveFace(f, removeIsolated: false);
            if (corners.Count >= 3)
            {
                int nf = AddFaceWithCorners(m, corners, material);
                if (nf >= 0) for (int i = 0; i < corners.Count; i++) SetHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex, hard[i]);
            }
        }
        m.RemoveVertexIfIsolated(v);
        return true;
    }

    /// <summary>정점 삭제(Maya Delete Vertex): 내부 정점은 주변 면을 하나로 합치고, 경계 정점은 주변 면을 지운다.</summary>
    public static void DeleteVertices(PolyMesh m, IEnumerable<int> vertIds)
    {
        foreach (int v in vertIds.ToArray())
        {
            if (v < 0 || v >= m.Verts.Count || !m.Verts[v].Alive) continue;
            if (DissolveIfValence2(m, v)) continue;
            var outgoing = m.VertexOutgoing(v).ToArray();
            bool boundary = outgoing.Any(he => m.Hes[he].Twin < 0 || m.Hes[m.Hes[he].Prev].Twin < 0);
            if (boundary || outgoing.Length == 0)
            {
                var faces = new List<int>(); m.GetVertexFaces(v, faces);
                foreach (int f in faces.Distinct()) m.RemoveFace(f, removeIsolated: false);
                m.RemoveVertexIfIsolated(v);
                continue;
            }
            // 부채꼴을 돌며 외곽 루프 수집: 각 면에서 v 다음 정점부터 v 이전 정점까지
            var loop = new List<Corner>();
            int startHe = outgoing[0], cur = startHe;
            var visitedFaces = new List<int>();
            int guard = 0;
            do
            {
                var h = m.Hes[cur];
                visitedFaces.Add(h.Face);
                int walk = h.Next;
                while (m.Hes[walk].Next != cur) { var w = m.Hes[walk]; loop.Add(new Corner(w.Vertex, w.Uv0, w.Normal)); walk = w.Next; }
                // 마지막 정점(v 직전)은 다음 면의 첫 정점과 같으므로 건너뜀
                cur = m.Hes[m.Hes[cur].Prev].Twin; // 다음 면에서 v에서 나가는 하프에지
                if (cur < 0 || guard++ > 10000) break;
            } while (cur != startHe);
            var hard = new List<bool>();
            for (int i = 0; i < loop.Count; i++) hard.Add(IsHard(m, loop[i].Vertex, loop[(i + 1) % loop.Count].Vertex));
            int material = m.Faces[visitedFaces[0]].Material;
            foreach (int f in visitedFaces.Distinct()) m.RemoveFace(f, removeIsolated: false);
            m.RemoveVertexIfIsolated(v);
            if (loop.Count >= 3)
            {
                int nf = AddFaceWithCorners(m, loop, material);
                if (nf >= 0) for (int i = 0; i < loop.Count; i++) SetHard(m, loop[i].Vertex, loop[(i + 1) % loop.Count].Vertex, hard[i]);
            }
        }
        m.BumpTopology();
    }

    // ------------------------------------------------------------ Merge

    /// <summary>임계 거리 안의 선택 정점들을 하나로 합친다. 영향을 받는 면은 다시 만들고 퇴화 면은 버린다. 반환값은 합쳐진 쌍 수.</summary>
    public static int MergeVertices(PolyMesh m, IEnumerable<int> vertIds, float threshold)
    {
        var verts = vertIds.Where(v => v >= 0 && v < m.Verts.Count && m.Verts[v].Alive).Distinct().ToList();
        var rep = new Dictionary<int, int>();
        float t2 = threshold * threshold;
        int merged = 0;
        for (int i = 0; i < verts.Count; i++)
        {
            if (rep.ContainsKey(verts[i])) continue;
            for (int j = i + 1; j < verts.Count; j++)
            {
                if (rep.ContainsKey(verts[j])) continue;
                if (Vector3.DistanceSquared(m.Verts[verts[i]].Position, m.Verts[verts[j]].Position) <= t2) { rep[verts[j]] = verts[i]; merged++; }
            }
        }
        if (merged == 0) return 0;
        // 대표 정점 위치 = 클러스터 평균
        foreach (var group in rep.GroupBy(kv => kv.Value))
        {
            var sum = m.Verts[group.Key].Position; int n = 1;
            foreach (var kv in group) { sum += m.Verts[kv.Key].Position; n++; }
            var vv = m.Verts[group.Key]; vv.Position = sum / n; m.Verts[group.Key] = vv;
        }
        RebuildFacesWithVertexMap(m, rep);
        m.BumpTopology();
        return merged;
    }

    private static void RebuildFacesWithVertexMap(PolyMesh m, Dictionary<int, int> map)
    {
        var affected = new HashSet<int>();
        var tmp = new List<int>();
        foreach (int v in map.Keys) { m.GetVertexFaces(v, tmp); affected.UnionWith(tmp); }
        var rebuilt = new List<(List<Corner> corners, int material, List<bool> hard)>();
        foreach (int f in affected)
        {
            var corners = CaptureCorners(m, f);
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
            rebuilt.Add((corners, m.Faces[f].Material, hard));
        }
        foreach (int f in affected) m.RemoveFace(f, removeIsolated: false);
        foreach (var (corners, material, hard) in rebuilt)
        {
            var mapped = new List<Corner>(); var mappedHard = new List<bool>();
            for (int i = 0; i < corners.Count; i++)
            {
                var c = corners[i] with { Vertex = map.TryGetValue(corners[i].Vertex, out int r) ? r : corners[i].Vertex };
                if (mapped.Count > 0 && mapped[^1].Vertex == c.Vertex) { mappedHard[^1] |= hard[i]; continue; }
                mapped.Add(c); mappedHard.Add(hard[i]);
            }
            while (mapped.Count > 1 && mapped[0].Vertex == mapped[^1].Vertex) { mapped.RemoveAt(mapped.Count - 1); mappedHard.RemoveAt(mappedHard.Count - 1); }
            if (mapped.Count < 3) continue;
            int nf = AddFaceWithCorners(m, mapped, material);
            if (nf >= 0) for (int i = 0; i < mapped.Count; i++) SetHard(m, mapped[i].Vertex, mapped[(i + 1) % mapped.Count].Vertex, mappedHard[i]);
        }
        foreach (int v in map.Keys) m.RemoveVertexIfIsolated(v);
        foreach (int v in map.Values.Distinct()) m.RemoveVertexIfIsolated(v);
    }

    // ------------------------------------------------------------ 기타

    /// <summary>
    /// 면의 방향(노멀)을 뒤집는다. 하프에지 구조의 일관성을 위해 선택 면이 속한 연결 요소 전체를 뒤집는다
    /// (Maya의 Reverse + Conform에 해당; 안팎이 뒤집힌 메시를 고치는 용도).
    /// </summary>
    public static void ReverseFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        var selected = new HashSet<int>(faceIds);
        var faces = new HashSet<int>();
        foreach (var comp in ConnectedComponents(m)) if (comp.Any(selected.Contains)) faces.UnionWith(comp);
        // 1) 모든 면을 캡처하고 제거한 뒤 2) 뒤집어 재생성 (동시에 해야 같은 방향 충돌이 없다)
        var captured = new List<(List<Corner> corners, int material, List<bool> hard)>();
        foreach (int f in faces)
        {
            if (f < 0 || f >= m.Faces.Count || !m.Faces[f].Alive) continue;
            var corners = CaptureCorners(m, f);
            var hard = new List<bool>();
            for (int i = 0; i < corners.Count; i++) hard.Add(IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
            captured.Add((corners, m.Faces[f].Material, hard));
        }
        foreach (int f in faces) if (f >= 0 && f < m.Faces.Count && m.Faces[f].Alive) m.RemoveFace(f, removeIsolated: false);
        foreach (var (corners, material, hard) in captured)
        {
            var rev = corners.AsEnumerable().Reverse().ToList();
            int nf = AddFaceWithCorners(m, rev, material);
            if (nf < 0) continue;
            int n = corners.Count;
            // 원래 엣지 i는 (c[i], c[i+1]); 뒤집힌 루프에서 (rev[j], rev[j+1]) = (c[n-1-j], c[n-2-j]) → 원래 엣지 n-2-j
            for (int j = 0; j < n; j++) SetHard(m, rev[j].Vertex, rev[(j + 1) % n].Vertex, hard[((n - 2 - j) % n + n) % n]);
        }
        m.BumpTopology();
    }

    public static void SetEdgesHard(PolyMesh m, IEnumerable<int> edgeIds, bool hard)
    {
        foreach (int e in edgeIds)
        {
            if (e < 0 || e >= m.Edges.Count || !m.Edges[e].Alive) continue;
            var ed = m.Edges[e]; ed.Hard = hard; m.Edges[e] = ed;
        }
    }

    /// <summary>다른 메시를 변환 행렬을 적용해 이 메시에 덧붙인다. 반환값은 정점 ID 오프셋 매핑.</summary>
    public static void Append(PolyMesh target, PolyMesh source, Matrix4x4 transform)
    {
        var vmap = new int[source.Verts.Count];
        for (int v = 0; v < source.Verts.Count; v++)
            vmap[v] = source.Verts[v].Alive ? target.AddVertex(Vector3.Transform(source.Verts[v].Position, transform)) : -1;
        bool flip = Matrix4x4.Invert(transform, out _) && Det3(transform) < 0;
        for (int f = 0; f < source.Faces.Count; f++)
        {
            if (!source.Faces[f].Alive) continue;
            var corners = CaptureCorners(source, f).Select(c => c with { Vertex = vmap[c.Vertex], Normal = Vector3.Normalize(Vector3.TransformNormal(c.Normal, transform)) }).ToList();
            if (flip) corners.Reverse();
            var srcCorners = CaptureCorners(source, f);
            int nf = AddFaceWithCorners(target, corners, source.Faces[f].Material);
            if (nf < 0) continue;
            for (int i = 0; i < srcCorners.Count; i++)
                SetHard(target, vmap[srcCorners[i].Vertex], vmap[srcCorners[(i + 1) % srcCorners.Count].Vertex], IsHard(source, srcCorners[i].Vertex, srcCorners[(i + 1) % srcCorners.Count].Vertex));
        }
        target.BumpTopology();
    }

    private static float Det3(Matrix4x4 m)
        => m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    /// <summary>연결 요소(면 인접 기준)별 면 ID 목록.</summary>
    public static List<List<int>> ConnectedComponents(PolyMesh m)
    {
        var comp = new int[m.Faces.Count];
        Array.Fill(comp, -1);
        var result = new List<List<int>>();
        var stack = new Stack<int>();
        var tmp = new List<int>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive || comp[f] >= 0) continue;
            int id = result.Count; var list = new List<int>();
            stack.Push(f); comp[f] = id;
            while (stack.Count > 0)
            {
                int cur = stack.Pop(); list.Add(cur);
                m.GetFaceVertices(cur, tmp);
                foreach (int v in tmp.ToArray())
                {
                    var faces = new List<int>(); m.GetVertexFaces(v, faces);
                    foreach (int nf in faces) if (comp[nf] < 0) { comp[nf] = id; stack.Push(nf); }
                }
            }
            result.Add(list);
        }
        return result;
    }

    /// <summary>면 ID 집합만으로 새 메시를 만든다(정점은 필요한 것만 복사).</summary>
    public static PolyMesh ExtractFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        var target = new PolyMesh();
        var vmap = new Dictionary<int, int>();
        foreach (int f in faceIds)
        {
            var corners = CaptureCorners(m, f);
            var mapped = corners.Select(c =>
            {
                if (!vmap.TryGetValue(c.Vertex, out int nv)) { nv = target.AddVertex(m.Verts[c.Vertex].Position); vmap[c.Vertex] = nv; }
                return c with { Vertex = nv };
            }).ToList();
            int nf = AddFaceWithCorners(target, mapped, m.Faces[f].Material);
            if (nf < 0) continue;
            for (int i = 0; i < corners.Count; i++)
                SetHard(target, mapped[i].Vertex, mapped[(i + 1) % mapped.Count].Vertex, IsHard(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
        }
        target.BumpTopology();
        return target;
    }
}
