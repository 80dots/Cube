using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Maya Edit Mesh 메뉴의 컴포넌트 연산들: Poke, Collapse, Merge to Center, Average, Detach, Duplicate, Chamfer, Connect, Flip/Spin Edge, Extrude Edges, Wedge, Circularize, Offset Loop, Slide, Flip/Symmetrize.</summary>
public static partial class MeshOps
{
    private static IEnumerable<int> AliveFaces(PolyMesh m, IEnumerable<int> ids) => ids.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).Distinct();
    private static IEnumerable<int> AliveEdges(PolyMesh m, IEnumerable<int> ids) => ids.Where(e => e >= 0 && e < m.EdgeCount && m.Edges[e].Alive).Distinct();
    private static IEnumerable<int> AliveVerts(PolyMesh m, IEnumerable<int> ids) => ids.Where(v => v >= 0 && v < m.VertexCount && m.Verts[v].Alive).Distinct();

    // ------------------------------------------------------------ Poke

    /// <summary>Poke: 면 중심에 정점을 추가하고 삼각형 부채꼴로 나눈다. offset은 면 법선 방향 이동. 반환값은 새 중심 정점들.</summary>
    public static List<int> Poke(PolyMesh m, IEnumerable<int> faceIds, float offset)
    {
        var result = new List<int>();
        var faces = AliveFaces(m, faceIds).ToList();
        if (faces.Count == 0) return result;
        var rb = new FaceRebuilder(m);
        foreach (int f in faces) rb.Capture(f);
        var centers = new Dictionary<int, int>();
        foreach (int f in faces)
        {
            var n = MeshNormals.FaceNormalUnnormalized(m, f);
            if (n.LengthSquared() > 1e-18f) n = Vector3.Normalize(n);
            centers[f] = m.AddVertex(m.FaceCentroid(f) + n * offset);
        }
        rb.RemoveCaptured();
        foreach (var (f, corners, material) in rb.Captured)
        {
            int cv = centers[f];
            var uvC = Vector2.Zero; foreach (var c in corners) uvC += c.Uv; uvC /= corners.Count;
            for (int i = 0; i < corners.Count; i++)
                rb.AddFace(new[] { corners[i], corners[(i + 1) % corners.Count], new Corner(cv, uvC, corners[i].Normal) }, material);
            result.Add(cv);
        }
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Collapse / Merge to Center / Average

    /// <summary>정점 묶음들을 각각 하나로 합친다(위치 = 묶음 평균 또는 지정). 반환값은 대표 정점들.</summary>
    private static List<int> MergeGroups(PolyMesh m, List<List<int>> groups, Func<List<int>, Vector3>? position)
    {
        var map = new Dictionary<int, int>();
        var reps = new List<int>();
        foreach (var g in groups)
        {
            var verts = AliveVerts(m, g).Where(v => !map.ContainsKey(v)).ToList();
            if (verts.Count == 0) continue;
            int rep = verts[0];
            var pos = position?.Invoke(verts) ?? verts.Aggregate(Vector3.Zero, (s, v) => s + m.Verts[v].Position) / verts.Count;
            var vv = m.Verts[rep]; vv.Position = pos; m.Verts[rep] = vv;
            for (int i = 1; i < verts.Count; i++) map[verts[i]] = rep;
            reps.Add(rep);
        }
        if (map.Count > 0) RebuildFacesWithVertexMap(m, map);
        m.BumpTopology();
        return reps;
    }

    /// <summary>Collapse(엣지): 각 엣지를 중점 정점 하나로 접는다. 이어진 엣지들은 한 묶음이 된다. 반환값은 남은 정점들.</summary>
    public static List<int> CollapseEdges(PolyMesh m, IEnumerable<int> edgeIds)
    {
        // union-find로 연결된 선택 엣지를 묶는다
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent.TryGetValue(x, out int p) && p != x) { x = p; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        foreach (int e in AliveEdges(m, edgeIds)) { var (a, b) = m.EdgeVertices(e); parent.TryAdd(a, a); parent.TryAdd(b, b); Union(a, b); }
        var groups = parent.Keys.GroupBy(Find).Select(g => g.ToList()).ToList();
        return MergeGroups(m, groups, null);
    }

    /// <summary>Collapse(면): 각 면(연결된 면 묶음)을 중심 정점 하나로 접는다.</summary>
    public static List<int> CollapseFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        var edges = new HashSet<int>();
        var hes = new List<int>();
        foreach (int f in AliveFaces(m, faceIds)) { m.GetFaceHalfEdges(f, hes); foreach (int he in hes) edges.Add(m.Hes[he].Edge); }
        return CollapseEdges(m, edges);
    }

    /// <summary>Merge to Center: 선택 정점을 모두 중심 한 점으로 합친다.</summary>
    public static int MergeToCenter(PolyMesh m, IEnumerable<int> vertIds)
    {
        var verts = AliveVerts(m, vertIds).ToList();
        if (verts.Count < 2) return verts.Count == 1 ? verts[0] : -1;
        var reps = MergeGroups(m, new List<List<int>> { verts }, null);
        return reps.Count > 0 ? reps[0] : -1;
    }

    /// <summary>Average Vertices: 선택 정점을 이웃 평균 쪽으로 옮긴다(라플라시안 평활, 위상 불변).</summary>
    public static void AverageVertices(PolyMesh m, IEnumerable<int> vertIds, int iterations, float strength = 0.5f)
    {
        var verts = AliveVerts(m, vertIds).ToList();
        iterations = Math.Clamp(iterations, 1, 100);
        var edges = new List<int>();
        for (int it = 0; it < iterations; it++)
        {
            var target = new Dictionary<int, Vector3>();
            foreach (int v in verts)
            {
                m.GetVertexEdges(v, edges);
                if (edges.Count == 0) continue;
                var sum = Vector3.Zero;
                foreach (int e in edges) { var (a, b) = m.EdgeVertices(e); sum += m.Verts[a == v ? b : a].Position; }
                target[v] = Vector3.Lerp(m.Verts[v].Position, sum / edges.Count, strength);
            }
            foreach (var (v, p) in target) { var vv = m.Verts[v]; vv.Position = p; m.Verts[v] = vv; }
        }
        m.BumpGeometry();
    }

    // ------------------------------------------------------------ Detach / Duplicate

    /// <summary>Detach(정점): 선택 정점을 면마다 별개의 정점으로 분리한다(첫 면은 원래 정점 유지). 반환값은 새 정점들.</summary>
    public static List<int> DetachVertices(PolyMesh m, IEnumerable<int> vertIds)
    {
        var result = new List<int>();
        var verts = AliveVerts(m, vertIds).ToList();
        var rb = new FaceRebuilder(m);
        var faces = new List<int>();
        var faceSet = new List<int>();
        foreach (int v in verts) { m.GetVertexFaces(v, faces); foreach (int f in faces) if (!faceSet.Contains(f)) faceSet.Add(f); }
        foreach (int f in faceSet) rb.Capture(f);
        rb.RemoveCaptured();
        var used = new HashSet<int>();
        foreach (var (f, corners, material) in rb.Captured)
        {
            var loop = new List<Corner>();
            foreach (var c in corners)
            {
                if (verts.Contains(c.Vertex) && !used.Add(c.Vertex))
                {
                    int nv = m.AddVertex(m.Verts[c.Vertex].Position);
                    result.Add(nv);
                    loop.Add(c with { Vertex = nv });
                }
                else loop.Add(c);
            }
            rb.AddFace(loop, material);
        }
        m.BumpTopology();
        return result;
    }

    /// <summary>Detach(면): 선택 면 영역을 둘레 엣지를 따라 떼어 낸다(경계 정점 복제, 위치는 그대로). 반환값은 다시 만든 면들.</summary>
    public static List<int> DetachFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        var result = new List<int>();
        var region = new HashSet<int>(AliveFaces(m, faceIds));
        if (region.Count == 0) return result;
        var boundaryVerts = new HashSet<int>();
        var hes = new List<int>();
        foreach (int f in region)
        {
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes)
            {
                var h = m.Hes[he];
                bool boundary = h.Twin < 0 || !region.Contains(m.Hes[h.Twin].Face);
                if (boundary && h.Twin >= 0) { boundaryVerts.Add(h.Vertex); boundaryVerts.Add(m.Hes[h.Next].Vertex); }
            }
        }
        // 영역 밖 면과 닿는 정점만 복제(완전 경계 정점은 그대로)
        var rb = new FaceRebuilder(m);
        foreach (int f in region) rb.Capture(f);
        var dup = new Dictionary<int, int>();
        foreach (int v in boundaryVerts) dup[v] = m.AddVertex(m.Verts[v].Position);
        rb.RemoveCaptured();
        foreach (var (_, corners, material) in rb.Captured)
        {
            int nf = rb.AddFace(corners.Select(c => dup.TryGetValue(c.Vertex, out int d) ? c with { Vertex = d } : c).ToList(), material);
            if (nf >= 0) result.Add(nf);
        }
        foreach (int v in boundaryVerts) m.RemoveVertexIfIsolated(v);
        m.BumpTopology();
        return result;
    }

    /// <summary>Duplicate(면): 선택 면을 같은 메시 안에 복사한다(새 정점, 분리된 셸). 반환값은 새 면들.</summary>
    public static List<int> DuplicateFaces(PolyMesh m, IEnumerable<int> faceIds)
    {
        var result = new List<int>();
        var faces = AliveFaces(m, faceIds).ToList();
        var vmap = new Dictionary<int, int>();
        var flags = new List<(int a, int b, EdgeFlags f)>();
        var captured = new List<(List<Corner> corners, int material)>();
        foreach (int f in faces)
        {
            var corners = CaptureCorners(m, f);
            for (int i = 0; i < corners.Count; i++) flags.Add((corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex, GetFlags(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex)));
            captured.Add((corners, m.Faces[f].Material));
        }
        foreach (var (corners, material) in captured)
        {
            var mapped = corners.Select(c => { if (!vmap.TryGetValue(c.Vertex, out int nv)) { nv = m.AddVertex(m.Verts[c.Vertex].Position); vmap[c.Vertex] = nv; } return c with { Vertex = nv }; }).ToList();
            int nf = AddFaceWithCorners(m, mapped, material);
            if (nf >= 0) result.Add(nf);
        }
        foreach (var (a, b, f) in flags) if (vmap.TryGetValue(a, out int na) && vmap.TryGetValue(b, out int nb)) SetFlags(m, na, nb, f);
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Chamfer Vertices

    /// <summary>Chamfer Vertices: 정점을 둘레 엣지 위 width 지점의 정점들로 바꾸고(removeFace가 아니면) 그 자리에 면을 채운다. 반환값은 새 캡 면들.</summary>
    public static List<int> ChamferVertices(PolyMesh m, IEnumerable<int> vertIds, float width, bool removeFace)
    {
        var result = new List<int>();
        var verts = new HashSet<int>(AliveVerts(m, vertIds));
        if (verts.Count == 0) return result;
        width = MathF.Max(width, 1e-5f);
        var rb = new FaceRebuilder(m);
        var faces = new List<int>();
        foreach (int v in verts) { m.GetVertexFaces(v, faces); foreach (int f in faces) rb.Capture(f); }
        var onEdge = new Dictionary<(int v, int other), int>();
        var uvOf = new Dictionary<int, Vector2>();
        var capEdges = new Dictionary<int, List<(int from, int to)>>();
        foreach (int v in verts) capEdges[v] = new List<(int, int)>();
        int P(int v, int other)
        {
            if (onEdge.TryGetValue((v, other), out int id)) return id;
            var pv = m.Verts[v].Position; var po = m.Verts[other].Position;
            float len = Vector3.Distance(pv, po);
            float d = MathF.Min(width, len * 0.45f);
            id = m.AddVertex(len > 1e-9f ? pv + (po - pv) * (d / len) : pv);
            onEdge[(v, other)] = id; rb.SetParent(id, v, other);
            return id;
        }
        float Frac(int v, int other) { float len = Vector3.Distance(m.Verts[v].Position, m.Verts[other].Position); return len > 1e-9f ? MathF.Min(width, len * 0.45f) / len : 0f; }
        var loops = new List<(List<Corner> loop, int material)>();
        foreach (var (_, corners, material) in rb.Captured)
        {
            int n = corners.Count;
            var loop = new List<Corner>();
            for (int i = 0; i < n; i++)
            {
                var c = corners[i]; var prev = corners[(i + n - 1) % n]; var next = corners[(i + 1) % n];
                if (!verts.Contains(c.Vertex)) { loop.Add(c); continue; }
                int p1 = P(c.Vertex, prev.Vertex), p2 = P(c.Vertex, next.Vertex);
                var uv1 = Vector2.Lerp(c.Uv, prev.Uv, Frac(c.Vertex, prev.Vertex)); var uv2 = Vector2.Lerp(c.Uv, next.Uv, Frac(c.Vertex, next.Vertex));
                loop.Add(new Corner(p1, uv1, c.Normal)); loop.Add(new Corner(p2, uv2, c.Normal));
                uvOf.TryAdd(p1, uv1); uvOf.TryAdd(p2, uv2);
                capEdges[c.Vertex].Add((p2, p1));
            }
            loops.Add((loop, material));
        }
        rb.RemoveCaptured();
        foreach (var (loop, material) in loops) rb.AddFace(loop, material);
        if (!removeFace)
        {
            foreach (int v in verts)
            {
                var edges = capEdges[v];
                if (edges.Count < 3) continue;
                var next = new Dictionary<int, int>(); bool bad = false;
                foreach (var (from, to) in edges) if (from == to || !next.TryAdd(from, to)) { bad = true; break; }
                if (bad) continue;
                var loop = new List<int>(); int start = edges[0].from, cur = start;
                while (loop.Count <= edges.Count) { loop.Add(cur); if (!next.TryGetValue(cur, out cur)) { bad = true; break; } if (cur == start) break; }
                if (bad || cur != start || loop.Count != edges.Count) continue;
                int cf = AddFaceWithCorners(m, loop.Select(id => new Corner(id, uvOf.GetValueOrDefault(id), Vector3.Zero)).ToList());
                if (cf >= 0) result.Add(cf);
            }
        }
        foreach (int v in verts) m.RemoveVertexIfIsolated(v);
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Connect

    /// <summary>Connect(정점): 같은 면에 속한 선택 정점들을 면 루프 순서대로 엣지로 잇는다. 반환값은 새 엣지들.</summary>
    public static List<int> ConnectVertices(PolyMesh m, IEnumerable<int> vertIds)
    {
        var result = new List<int>();
        var verts = new HashSet<int>(AliveVerts(m, vertIds));
        if (verts.Count < 2) return result;
        // 면마다 선택 코너 순서 수집(면이 분할되면 ID가 바뀌므로 정점 쌍으로 기록)
        var pairs = new List<(int a, int b)>();
        var seenFaces = new HashSet<int>();
        var faces = new List<int>(); var loop = new List<int>();
        foreach (int v in verts)
        {
            m.GetVertexFaces(v, faces);
            foreach (int f in faces)
            {
                if (!seenFaces.Add(f)) continue;
                m.GetFaceVertices(f, loop);
                var sel = loop.Where(verts.Contains).ToList();
                if (sel.Count < 2) continue;
                for (int i = 0; i < sel.Count; i++)
                {
                    int a = sel[i], b = sel[(i + 1) % sel.Count];
                    if (sel.Count == 2 && i == 1) break;
                    if (m.FindEdge(a, b) < 0) pairs.Add((a, b));
                }
            }
        }
        foreach (var (a, b) in pairs)
        {
            int e = SplitFaceBetween(m, a, b);
            if (e >= 0) result.Add(e);
        }
        return result;
    }

    /// <summary>Connect(엣지): 선택 엣지들을 중점에서 나누고, 같은 면의 중점끼리 엣지로 잇는다. 반환값은 새 엣지들.</summary>
    public static List<int> ConnectEdges(PolyMesh m, IEnumerable<int> edgeIds)
    {
        var mids = new List<int>();
        foreach (int e in AliveEdges(m, edgeIds).ToArray())
        {
            if (e >= m.EdgeCount || !m.Edges[e].Alive) continue;
            int v = SplitEdge(m, e, 0.5f);
            if (v >= 0) mids.Add(v);
        }
        return ConnectVertices(m, mids);
    }

    // ------------------------------------------------------------ Flip / Spin edge

    /// <summary>Flip Triangle Edge: 두 삼각형 사이의 엣지를 반대 대각선으로 바꾼다. 반환값은 새 엣지들.</summary>
    public static List<int> FlipTriangleEdges(PolyMesh m, IEnumerable<int> edgeIds)
    {
        var result = new List<int>();
        foreach (int e in AliveEdges(m, edgeIds).ToArray())
        {
            if (e >= m.EdgeCount || !m.Edges[e].Alive || m.IsBoundaryEdge(e)) continue;
            var (f0, f1) = m.EdgeFaces(e);
            if (m.FaceDegree(f0) != 3 || m.FaceDegree(f1) != 3) continue;
            int ne = SpinEdge(m, e, forward: true);
            if (ne >= 0) result.Add(ne);
        }
        return result;
    }

    /// <summary>Spin Edge: 엣지를 양쪽 면을 합친 루프에서 한 칸 돌린 대각선으로 바꾼다. 반환값은 새 엣지(-1 = 실패).</summary>
    public static int SpinEdge(PolyMesh m, int e, bool forward)
    {
        if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive || m.IsBoundaryEdge(e)) return -1;
        var ed = m.Edges[e];
        int he0 = ed.He0, he1 = ed.He1;
        int a = m.Hes[he0].Vertex, b = m.Hes[he1].Vertex;
        // 합친 루프: [b, x1..xk, a, y1..yl]
        var loop = new List<int>();
        int cur = m.Hes[he0].Next; while (cur != he0) { loop.Add(m.Hes[cur].Vertex); cur = m.Hes[cur].Next; }
        int ia = loop.Count;
        cur = m.Hes[he1].Next; while (cur != he1) { loop.Add(m.Hes[cur].Vertex); cur = m.Hes[cur].Next; }
        int n = loop.Count;
        if (n < 4) return -1;
        int ib = 0;
        int na, nb;
        if (forward) { na = loop[(ia + 1) % n]; nb = loop[(ib + 1) % n]; }
        else { na = loop[(ia + n - 1) % n]; nb = loop[(ib + n - 1) % n]; }
        if (na == nb || m.FindEdge(na, nb) >= 0) return -1;
        var (merged, mergedFace) = MergeFacesAcrossEdgeReturning(m, e);
        if (!merged || mergedFace < 0) return -1;
        int ne = SplitFace(m, mergedFace, na, nb);
        m.BumpTopology();
        return ne;
    }

    public static List<int> SpinEdges(PolyMesh m, IEnumerable<int> edgeIds, bool forward)
    {
        var result = new List<int>();
        foreach (int e in AliveEdges(m, edgeIds).ToArray()) { int ne = SpinEdge(m, e, forward); if (ne >= 0) result.Add(ne); }
        return result;
    }

    private static (bool ok, int face) MergeFacesAcrossEdgeReturning(PolyMesh m, int e)
    {
        var ed = m.Edges[e];
        int he0 = ed.He0, he1 = ed.He1;
        int f0 = m.Hes[he0].Face, f1 = m.Hes[he1].Face;
        if (f0 == f1) return (false, -1);
        var rb = new FaceRebuilder(m);
        rb.Capture(f0); rb.Capture(f1);
        var loop = new List<Corner>();
        int cur = m.Hes[he0].Next;
        while (cur != he0) { var h = m.Hes[cur]; loop.Add(new Corner(h.Vertex, h.Uv0, h.Normal)); cur = h.Next; }
        cur = m.Hes[he1].Next;
        while (cur != he1) { var h = m.Hes[cur]; loop.Add(new Corner(h.Vertex, h.Uv0, h.Normal)); cur = h.Next; }
        int material = m.Faces[f0].Material;
        rb.RemoveCaptured();
        int nf = rb.AddFace(loop, material);
        return (nf >= 0, nf);
    }

    // ------------------------------------------------------------ Extrude edges

    /// <summary>Extrude(엣지): 경계 엣지마다 새 정점 쌍을 만들어 쿼드를 붙인다(이동 0, 이어진 엣지는 정점 공유). 반환값은 새 면들.</summary>
    public static List<int> ExtrudeEdges(PolyMesh m, IEnumerable<int> edgeIds)
    {
        var result = new List<int>();
        var edges = AliveEdges(m, edgeIds).Where(e => m.IsBoundaryEdge(e)).ToList();
        if (edges.Count == 0) return result;
        var dup = new Dictionary<int, int>();
        int D(int v) { if (!dup.TryGetValue(v, out int d)) { d = m.AddVertex(m.Verts[v].Position); dup[v] = d; } return d; }
        foreach (int e in edges)
        {
            int he = m.Edges[e].He0;
            var h = m.Hes[he];
            int a = h.Vertex, b = m.Hes[h.Next].Vertex;
            var uvA = h.Uv0; var uvB = m.Hes[h.Next].Uv0;
            int a2 = D(a), b2 = D(b);
            var flags = GetFlags(m, a, b);
            int q = AddFaceWithCorners(m, new[] { new Corner(b, uvB, h.Normal), new Corner(a, uvA, h.Normal), new Corner(a2, uvA, h.Normal), new Corner(b2, uvB, h.Normal) }, m.Faces[h.Face].Material);
            if (q >= 0) { result.Add(q); SetFlags(m, a2, b2, flags); }
        }
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Wedge

    /// <summary>Wedge: 선택 면을 pivot 엣지를 축으로 arcAngle(도)만큼 divisions 단계로 돌려 가며 압출한다. 반환값은 마지막 캡 면들.</summary>
    public static List<int> Wedge(PolyMesh m, IEnumerable<int> faceIds, int pivotEdge, float arcAngle, int divisions)
    {
        var faces = AliveFaces(m, faceIds).ToList();
        if (faces.Count == 0 || pivotEdge < 0 || pivotEdge >= m.EdgeCount || !m.Edges[pivotEdge].Alive) return new List<int>();
        divisions = Math.Clamp(divisions, 1, 64);
        var (pa, pb) = m.EdgeVertices(pivotEdge);
        var axisA = m.Verts[pa].Position; var axisB = m.Verts[pb].Position;
        var axis = axisB - axisA;
        if (axis.LengthSquared() < 1e-12f) return new List<int>();
        axis = Vector3.Normalize(axis);
        // 회전 방향: 면 법선이 축에서 멀어지는 쪽으로 돈다(양의 각 = 면 법선 방향으로 휨)
        var regionNormal = Vector3.Zero; foreach (int f in faces) regionNormal += MeshNormals.FaceNormalUnnormalized(m, f);
        var regionCenter = faces.Aggregate(Vector3.Zero, (s, f) => s + m.FaceCentroid(f)) / faces.Count;
        var radial = regionCenter - axisA; radial -= axis * Vector3.Dot(radial, axis);
        float sign = Vector3.Dot(Vector3.Cross(axis, radial), regionNormal) >= 0 ? 1f : -1f;
        float step = arcAngle * MathF.PI / 180f / divisions * sign;
        var current = faces;
        for (int d = 1; d <= divisions; d++)
        {
            var caps = ExtrudeFaces(m, current);
            if (caps.Count == 0) break;
            var capVerts = new HashSet<int>(); var tmp = new List<int>();
            foreach (int f in caps) { m.GetFaceVertices(f, tmp); capVerts.UnionWith(tmp); }
            var rot = Matrix4x4.CreateFromAxisAngle(axis, step);
            var merge = new List<int> { pa, pb };
            foreach (int v in capVerts)
            {
                var p = m.Verts[v].Position;
                if (Vector3.Distance(p, axisA) < 1e-6f || Vector3.Distance(p, axisB) < 1e-6f) { merge.Add(v); continue; }
                var vv = m.Verts[v]; vv.Position = axisA + Vector3.Transform(p - axisA, rot); m.Verts[v] = vv;
            }
            // 힌지 정점 복제본은 원래 정점과 합친다(측면 쿼드는 퇴화되어 사라진다). 합치면 면 ID가 바뀌므로 정점 집합으로 캡을 다시 찾는다
            var rotated = new HashSet<int>(capVerts.Where(v => !merge.Contains(v)));
            MergeVertices(m, merge, 1e-5f);
            var allowed = new HashSet<int>(rotated) { pa, pb };
            current = new List<int>();
            var faceSet = new HashSet<int>();
            foreach (int v in rotated) { m.GetVertexFaces(v, tmp); faceSet.UnionWith(tmp); }
            foreach (int f in faceSet)
            {
                m.GetFaceVertices(f, tmp);
                if (tmp.All(allowed.Contains) && tmp.Any(rotated.Contains)) current.Add(f);
            }
            if (current.Count == 0) break;
            // 각 단계마다 캡 정점 위치를 누적 회전해야 하므로 축 위치는 그대로, 다음 단계는 현재 캡을 다시 압출
            // (ExtrudeFaces는 이동 0이라 새 캡은 이전 캡 위치에서 출발)
        }
        m.BumpTopology();
        return current;
    }

    // ------------------------------------------------------------ Circularize

    /// <summary>
    /// Circularize: 선택 정점(또는 면 영역의 둘레 정점)을 평균 평면 위의 원에 배치한다. radialOffset은 반지름 배율 보정(0 = 평균 거리), evenly면 각도를 균등 분배.
    /// 면 영역이면 안쪽 정점은 둘레의 평균 이동만큼 따라간다.
    /// </summary>
    public static void Circularize(PolyMesh m, IEnumerable<int> vertIds, float radialOffset, bool evenly)
    {
        var verts = AliveVerts(m, vertIds).ToList();
        if (verts.Count < 3) return;
        var center = verts.Aggregate(Vector3.Zero, (s, v) => s + m.Verts[v].Position) / verts.Count;
        // 평면 법선: 인접 면 법선 평균, 없으면 뉴웰
        var normal = Vector3.Zero; var faces = new List<int>();
        foreach (int v in verts) { m.GetVertexFaces(v, faces); foreach (int f in faces.Distinct()) normal += MeshNormals.FaceNormalUnnormalized(m, f); }
        if (normal.LengthSquared() < 1e-12f)
        {
            for (int i = 0; i < verts.Count; i++) { var a = m.Verts[verts[i]].Position; var b = m.Verts[verts[(i + 1) % verts.Count]].Position; normal += Vector3.Cross(a - center, b - center); }
        }
        if (normal.LengthSquared() < 1e-12f) normal = Vector3.UnitY;
        normal = Vector3.Normalize(normal);
        EarClipping.PlaneBasis(normal, out var u, out var w);
        var items = verts.Select(v =>
        {
            var d = m.Verts[v].Position - center; d -= normal * Vector3.Dot(d, normal);
            return (v, angle: MathF.Atan2(Vector3.Dot(d, w), Vector3.Dot(d, u)), r: d.Length());
        }).OrderBy(x => x.angle).ToList();
        float radius = items.Average(x => x.r) * (1f + radialOffset);
        for (int i = 0; i < items.Count; i++)
        {
            float ang = evenly ? items[0].angle + MathF.Tau * i / items.Count : items[i].angle;
            var p = center + (u * MathF.Cos(ang) + w * MathF.Sin(ang)) * radius;
            var vv = m.Verts[items[i].v]; vv.Position = p; m.Verts[items[i].v] = vv;
        }
        m.BumpGeometry();
    }

    // ------------------------------------------------------------ Offset Edge Loop / Slide Edge

    /// <summary>Offset Edge Loop: 선택 엣지(루프) 양옆 offset 거리에 엣지 루프를 하나씩 끼운다. 반환값은 새 루프 엣지들.</summary>
    public static List<int> OffsetEdgeLoop(PolyMesh m, IEnumerable<int> edgeIds, float offset)
    {
        var result = new List<int>();
        var selected = new HashSet<int>(AliveEdges(m, edgeIds));
        offset = MathF.Max(offset, 1e-4f);
        var done = new HashSet<int>();
        foreach (int e in selected.ToArray())
        {
            if (e >= m.EdgeCount || !m.Edges[e].Alive) continue;
            var ed = m.Edges[e];
            foreach (int he in new[] { ed.He0, ed.He1 })
            {
                if (he < 0) continue;
                int f = m.Hes[he].Face;
                if (m.FaceDegree(f) != 4) continue;
                int next = m.Hes[he].Next;         // 공유 정점 b에서 나가는 옆 엣지
                int shared = m.Hes[next].Vertex;
                int e2 = m.Hes[next].Edge;
                if (e2 >= m.EdgeCount || !m.Edges[e2].Alive || selected.Contains(e2) || done.Contains(e2)) continue;
                var (x, y) = m.EdgeVertices(e2);
                float len = Vector3.Distance(m.Verts[x].Position, m.Verts[y].Position);
                float t = len > 1e-9f ? Math.Clamp(offset / len, 0.02f, 0.98f) : 0.5f;
                if (x != shared) t = 1f - t;
                var loop = InsertEdgeLoop(m, e2, t);
                foreach (int ne in loop) done.Add(ne);
                done.Add(e2);
                result.AddRange(loop);
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>Slide Edge: 선택 엣지 루프의 정점을 옆 엣지를 따라 t(-1..1)만큼 민다(양수 = 루프 진행 방향 기준 왼쪽 면 쪽).</summary>
    public static void SlideEdges(PolyMesh m, IEnumerable<int> edgeIds, float t)
    {
        var selected = new HashSet<int>(AliveEdges(m, edgeIds));
        if (selected.Count == 0) return;
        t = Math.Clamp(t, -0.98f, 0.98f);
        var target = new Dictionary<int, (Vector3 left, Vector3 right)>();
        foreach (int e in selected)
        {
            var ed = m.Edges[e];
            int heL = ed.He0, heR = ed.He1;
            int a = m.Hes[heL].Vertex, b = m.Hes[m.Hes[heL].Next].Vertex;
            // 왼쪽 면(heL): a의 이웃 = Prev(heL).Vertex, b의 이웃 = Next(Next(heL)).Vertex
            int aL = m.Hes[m.Hes[heL].Prev].Vertex, bL = m.Hes[m.Hes[m.Hes[heL].Next].Next].Vertex;
            int aR = -1, bR = -1;
            if (heR >= 0) { bR = m.Hes[m.Hes[heR].Prev].Vertex; aR = m.Hes[m.Hes[m.Hes[heR].Next].Next].Vertex; }
            void Put(int v, int l, int r)
            {
                if (selected.Contains(m.FindEdge(v, l)) ) l = -1;
                if (r >= 0 && selected.Contains(m.FindEdge(v, r))) r = -1;
                if (target.ContainsKey(v)) return;
                target[v] = (l >= 0 ? m.Verts[l].Position : m.Verts[v].Position, r >= 0 ? m.Verts[r].Position : m.Verts[v].Position);
            }
            Put(a, aL, aR); Put(b, bL, bR);
        }
        foreach (var (v, (left, right)) in target)
        {
            var p = m.Verts[v].Position;
            var np = t >= 0 ? Vector3.Lerp(p, left, t) : Vector3.Lerp(p, right, -t);
            var vv = m.Verts[v]; vv.Position = np; m.Verts[v] = vv;
        }
        m.BumpGeometry();
    }

    // ------------------------------------------------------------ Flip / Symmetrize components

    /// <summary>대칭 축(0=X,1=Y,2=Z, 오브젝트 공간 planeOffset 위치)의 반대편에서 가장 가까운 정점을 찾는다.</summary>
    private static int MirrorPartner(PolyMesh m, int v, int axis, float planeOffset, float tolerance)
    {
        var p = m.Verts[v].Position;
        var mp = p; SetAxis(ref mp, axis, 2f * planeOffset - GetAxis(p, axis));
        int best = -1; float bestD = tolerance * tolerance;
        for (int i = 0; i < m.VertexCount; i++)
        {
            if (!m.Verts[i].Alive || i == v) continue;
            float d = Vector3.DistanceSquared(m.Verts[i].Position, mp);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static float GetAxis(Vector3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
    private static void SetAxis(ref Vector3 p, int axis, float v) { if (axis == 0) p.X = v; else if (axis == 1) p.Y = v; else p.Z = v; }

    /// <summary>Symmetrize(컴포넌트): 선택 정점의 거울 짝 정점을 선택 정점의 거울 위치로 옮긴다.</summary>
    public static int SymmetrizeVertices(PolyMesh m, IEnumerable<int> vertIds, int axis, float planeOffset, float tolerance)
    {
        int n = 0;
        foreach (int v in AliveVerts(m, vertIds))
        {
            int p = MirrorPartner(m, v, axis, planeOffset, tolerance);
            if (p < 0) continue;
            var mp = m.Verts[v].Position; SetAxis(ref mp, axis, 2f * planeOffset - GetAxis(mp, axis));
            var vv = m.Verts[p]; vv.Position = mp; m.Verts[p] = vv; n++;
        }
        m.BumpGeometry();
        return n;
    }

    /// <summary>Flip(컴포넌트): 선택 정점과 거울 짝 정점의 위치를 서로 바꾼다(거울 반사해서).</summary>
    public static int FlipVertices(PolyMesh m, IEnumerable<int> vertIds, int axis, float planeOffset, float tolerance)
    {
        int n = 0; var done = new HashSet<int>();
        foreach (int v in AliveVerts(m, vertIds))
        {
            if (done.Contains(v)) continue;
            int p = MirrorPartner(m, v, axis, planeOffset, tolerance);
            if (p < 0 || done.Contains(p)) continue;
            var pv = m.Verts[v].Position; var pp = m.Verts[p].Position;
            SetAxis(ref pv, axis, 2f * planeOffset - GetAxis(pv, axis)); SetAxis(ref pp, axis, 2f * planeOffset - GetAxis(pp, axis));
            var a = m.Verts[v]; a.Position = pp; m.Verts[v] = a;
            var b = m.Verts[p]; b.Position = pv; m.Verts[p] = b;
            done.Add(v); done.Add(p); n++;
        }
        m.BumpGeometry();
        return n;
    }
}
